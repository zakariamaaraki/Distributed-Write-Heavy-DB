using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using LsmWriteDb.ChangeLogs;

namespace LsmWriteDb.Storage;

public sealed record LsmStoreOptions(
    string DataPath,
    int FlushThreshold,
    string TableName = TableNames.Default,
    int BlockSizeBytes = LsmStoreOptions.DefaultBlockSizeBytes)
{
    public const int DefaultBlockSizeBytes = SstableStore.DefaultBlockSizeBytes;
}

public sealed record KeyValueRow(string Key, string Value);

public sealed record StoreWriteOperation(string Key, string? Value, bool IsDeleted)
{
    public static StoreWriteOperation Put(string key, string value)
    {
        return new StoreWriteOperation(key, value, IsDeleted: false);
    }

    public static StoreWriteOperation Put(string table, string key, string value)
    {
        return new StoreWriteOperation(key, value, IsDeleted: false)
        {
            Table = TableNames.Normalize(table)
        };
    }

    public static StoreWriteOperation Delete(string key)
    {
        return new StoreWriteOperation(key, null, IsDeleted: true);
    }

    public static StoreWriteOperation Delete(string table, string key)
    {
        return new StoreWriteOperation(key, null, IsDeleted: true)
        {
            Table = TableNames.Normalize(table)
        };
    }

    public string Table { get; init; } = TableNames.Default;
}

public sealed record StoreStats(
    int MemTableEntries,
    int SstableCount,
    long LastSequence,
    int FlushThreshold,
    int BlockSizeBytes);

public sealed class LsmStore
{
    private const string CommittedBatchWalEntryType = "committedBatch";
    private static readonly JsonSerializerOptions WalJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LsmStoreOptions _options;
    private readonly OrderedMemTable _memTable = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly SstableStore _sstables;
    private readonly ChangeLogService _changeLog;
    private readonly IStoreSequenceGenerator? _sequenceGenerator;
    private readonly string _walPath;

    private bool _initialized;
    private long _lastSequence;

    public LsmStore(LsmStoreOptions options)
        : this(options, new ChangeLogService(options))
    {
    }

    public LsmStore(
        LsmStoreOptions options,
        ChangeLogService changeLog,
        IStoreSequenceGenerator? sequenceGenerator = null)
    {
        if (options.FlushThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Flush threshold must be greater than zero.");
        }

        if (options.BlockSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Block size must be greater than zero.");
        }

        _options = options with { TableName = TableNames.Normalize(options.TableName) };
        _changeLog = changeLog;
        _sequenceGenerator = sequenceGenerator;
        _sstables = new SstableStore(options.DataPath, options.BlockSizeBytes);
        _walPath = Path.Combine(options.DataPath, "wal.log");
    }

    public async Task InitializeAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_options.DataPath);

            _lastSequence = await FindMaxSequenceAsync();
            _sequenceGenerator?.Observe(_lastSequence);

            var walRecords = await ReadWalAsync();
            foreach (var record in walRecords)
            {
                _memTable.Apply(record);
                _lastSequence = Math.Max(_lastSequence, record.Sequence);
            }

            await _changeLog.PublishAsync(ToChangeLogEntries(walRecords));

            _initialized = true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task PutAsync(string key, string value)
    {
        ValidateKey(key);

        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            var record = NextRecord(key, value, isDeleted: false);
            await AppendWalAsync(record);
            _memTable.Apply(record);
            await _changeLog.PublishAsync(ToChangeLogEntries([record]));

            await FlushIfNeededAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteAsync(string key)
    {
        ValidateKey(key);

        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            var record = NextRecord(key, value: null, isDeleted: true);
            await AppendWalAsync(record);
            _memTable.Apply(record);
            await _changeLog.PublishAsync(ToChangeLogEntries([record]));

            await FlushIfNeededAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ApplyBatchAsync(IReadOnlyList<StoreWriteOperation> operations)
    {
        if (operations.Count == 0)
        {
            return;
        }

        foreach (var operation in operations)
        {
            ValidateWriteOperation(operation);
            ValidateTable(operation.Table);
        }

        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            var records = operations
                .Select(operation => NextRecord(operation.Key, operation.Value, operation.IsDeleted))
                .ToList();

            await AppendCommittedBatchWalAsync(records);

            foreach (var record in records)
            {
                _memTable.Apply(record);
            }

            await _changeLog.PublishAsync(ToChangeLogEntries(records));
            await FlushIfNeededAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ApplyReplicatedChangeAsync(ChangeLogEntry entry)
    {
        ValidateTable(entry.Table);
        ValidateKey(entry.Key);
        if (!entry.IsDeleted && entry.Value is null)
        {
            throw new ArgumentException("Replicated put changes require a value.", nameof(entry));
        }

        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            if (entry.Sequence <= _lastSequence)
            {
                return;
            }

            var record = new StoredRecord(entry.Sequence, entry.Key, entry.Value, entry.IsDeleted);
            await AppendWalAsync(record);
            _memTable.Apply(record);
            _lastSequence = Math.Max(_lastSequence, record.Sequence);
            _sequenceGenerator?.Observe(record.Sequence);

            await _changeLog.PublishAsync([entry]);
            await FlushIfNeededAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<KeyValueRow?> GetAsync(string key)
    {
        ValidateKey(key);

        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            if (_memTable.TryGet(key, out var memoryRecord))
            {
                return ToRowOrNull(memoryRecord);
            }

            StoredRecord? newest = null;
            foreach (var file in _sstables.GetDataFilesNewestFirst())
            {
                var record = await _sstables.TryGetAsync(file, key);
                if (record is null)
                {
                    continue;
                }

                if (newest is null || record.Sequence > newest.Sequence)
                {
                    newest = record;
                }
            }

            return newest is null ? null : ToRowOrNull(newest);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<KeyValueRow>> RangeAsync(string? start, string? end, int limit)
    {
        var boundedLimit = Math.Clamp(limit, 1, 1_000);

        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            var latestByKey = new SortedDictionary<string, StoredRecord>(StringComparer.Ordinal);

            foreach (var record in _memTable.Range(start, end))
            {
                KeepNewest(latestByKey, record);
            }

            foreach (var file in _sstables.GetDataFilesNewestFirst())
            {
                foreach (var record in await _sstables.ReadTableAsync(file))
                {
                    if (IsInsideRange(record.Key, start, end))
                    {
                        KeepNewest(latestByKey, record);
                    }
                }
            }

            return latestByKey.Values
                .Where(record => !record.IsDeleted)
                .Take(boundedLimit)
                .Select(record => new KeyValueRow(record.Key, record.Value ?? string.Empty))
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<KeyValueRow>> ScanAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            var latestByKey = new SortedDictionary<string, StoredRecord>(StringComparer.Ordinal);

            foreach (var record in _memTable.Range(start: null, end: null))
            {
                KeepNewest(latestByKey, record);
            }

            foreach (var file in _sstables.GetDataFilesNewestFirst())
            {
                foreach (var record in await _sstables.ReadTableAsync(file))
                {
                    KeepNewest(latestByKey, record);
                }
            }

            return latestByKey.Values
                .Where(record => !record.IsDeleted)
                .Select(record => new KeyValueRow(record.Key, record.Value ?? string.Empty))
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<StoreStats> GetStatsAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            return new StoreStats(
                _memTable.Count,
                await _sstables.CountAsync(),
                _lastSequence,
                _options.FlushThreshold,
                _options.BlockSizeBytes);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private StoredRecord NextRecord(string key, string? value, bool isDeleted)
    {
        var sequence = _sequenceGenerator?.NextSequence() ?? ++_lastSequence;
        _lastSequence = Math.Max(_lastSequence, sequence);
        return new StoredRecord(sequence, key, value, isDeleted);
    }

    private IReadOnlyList<ChangeLogEntry> ToChangeLogEntries(IReadOnlyList<StoredRecord> records)
    {
        var committedAt = DateTimeOffset.UtcNow;
        return records
            .Select(record => new ChangeLogEntry(
                record.Sequence,
                record.IsDeleted ? "delete" : "put",
                record.Key,
                record.Value,
                record.IsDeleted,
                committedAt)
            {
                Table = _options.TableName
            })
            .ToList();
    }

    private async Task FlushIfNeededAsync()
    {
        if (_memTable.Count < _options.FlushThreshold)
        {
            return;
        }

        var snapshot = _memTable.Snapshot();
        if (snapshot.Count == 0)
        {
            return;
        }

        await _sstables.WriteTableAsync(snapshot);
        _memTable.Clear();
        await ClearWalAsync();
        await CompactAsync();
    }

    private async Task CompactAsync()
    {
        var files = _sstables.GetDataFilesNewestFirst();
        if (files.Count == 0)
        {
            return;
        }

        var newestByKey = new SortedDictionary<string, StoredRecord>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var record in await _sstables.ReadTableAsync(file))
            {
                KeepNewest(newestByKey, record);
            }
        }

        var liveRecords = newestByKey.Values
            .Where(record => !record.IsDeleted)
            .ToList();

        if (liveRecords.Count > 0)
        {
            await _sstables.WriteTableAsync(liveRecords);
        }

        await _sstables.DeleteTablesAsync(files);
    }

    private async Task<long> FindMaxSequenceAsync()
    {
        var maxSequence = 0L;

        foreach (var file in _sstables.GetDataFilesNewestFirst())
        {
            foreach (var record in await _sstables.ReadTableAsync(file))
            {
                maxSequence = Math.Max(maxSequence, record.Sequence);
            }
        }

        foreach (var record in await ReadWalAsync())
        {
            maxSequence = Math.Max(maxSequence, record.Sequence);
        }

        return maxSequence;
    }

    private async Task AppendWalAsync(StoredRecord record)
    {
        await AppendWalLineAsync(record);
    }

    private async Task AppendCommittedBatchWalAsync(IReadOnlyList<StoredRecord> records)
    {
        await AppendWalLineAsync(new WalCommittedBatch(CommittedBatchWalEntryType, records));
    }

    private async Task AppendWalLineAsync<T>(T value)
    {
        var payload = JsonSerializer.Serialize(value, WalJsonOptions);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var line = JsonSerializer.Serialize(new WalEnvelope(payload, checksum), WalJsonOptions);

        await using var stream = new FileStream(
            _walPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync(line);
        await writer.FlushAsync();
    }

    private async Task ClearWalAsync()
    {
        await File.WriteAllTextAsync(_walPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private async Task<IReadOnlyList<StoredRecord>> ReadWalAsync()
    {
        if (!File.Exists(_walPath))
        {
            return [];
        }

        var bytes = await File.ReadAllBytesAsync(_walPath);
        return ParseWalRecords(bytes);
    }

    private static IReadOnlyList<StoredRecord> ParseWalRecords(byte[] bytes)
    {
        var records = new List<StoredRecord>();
        if (bytes.Length == 0)
        {
            return records;
        }

        var json = bytes.AsSpan();
        var preamble = Encoding.UTF8.GetPreamble();
        if (json.StartsWith(preamble))
        {
            json = json[preamble.Length..];
        }

        var text = Encoding.UTF8.GetString(json);
        if (string.IsNullOrWhiteSpace(text))
        {
            return records;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TryAppendWalLine(line, records);
        }

        return records;
    }

    private static void TryAppendWalLine(string line, List<StoredRecord> records)
    {
        try
        {
            using var document = JsonDocument.Parse(line);

            if (document.RootElement.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), CommittedBatchWalEntryType, StringComparison.Ordinal))
            {
                var batch = JsonSerializer.Deserialize<WalCommittedBatch>(line, WalJsonOptions);
                if (batch?.Records is not null)
                {
                    records.AddRange(batch.Records);
                }

                return;
            }

            var record = JsonSerializer.Deserialize<StoredRecord>(line, WalJsonOptions);
            if (record is null)
            {
                return;
            }

            records.Add(record);
        }
        catch (JsonException)
        {
            // A server crash can leave a partial trailing WAL line. Ignore it
            // so an incomplete transaction batch is not replayed as committed.
        }
    }

    private static void KeepNewest(SortedDictionary<string, StoredRecord> records, StoredRecord candidate)
    {
        if (!records.TryGetValue(candidate.Key, out var current) || candidate.Sequence > current.Sequence)
        {
            records[candidate.Key] = candidate;
        }
    }

    private static KeyValueRow? ToRowOrNull(StoredRecord record)
    {
        return record.IsDeleted ? null : new KeyValueRow(record.Key, record.Value ?? string.Empty);
    }

    private static bool IsInsideRange(string key, string? start, string? end)
    {
        if (start is not null && string.CompareOrdinal(key, start) < 0)
        {
            return false;
        }

        if (end is not null && string.CompareOrdinal(key, end) > 0)
        {
            return false;
        }

        return true;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }
    }

    private static void ValidateWriteOperation(StoreWriteOperation operation)
    {
        TableNames.Normalize(operation.Table);
        ValidateKey(operation.Key);

        if (!operation.IsDeleted && operation.Value is null)
        {
            throw new ArgumentException("Value is required.", nameof(operation));
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Store has not been initialized.");
        }
    }

    private void ValidateTable(string table)
    {
        var normalized = TableNames.Normalize(table);
        if (!string.Equals(normalized, _options.TableName, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Change belongs to table '{normalized}', but this store handles table '{_options.TableName}'.", nameof(table));
        }
    }
}

internal sealed class OrderedMemTable
{
    private readonly SortedDictionary<string, StoredRecord> _records = new(StringComparer.Ordinal);

    public int Count => _records.Count;

    public void Apply(StoredRecord record)
    {
        _records[record.Key] = record;
    }

    public bool TryGet(string key, out StoredRecord record)
    {
        return _records.TryGetValue(key, out record!);
    }

    public IReadOnlyList<StoredRecord> Snapshot()
    {
        return _records.Values.ToList();
    }

    public IEnumerable<StoredRecord> Range(string? start, string? end)
    {
        foreach (var record in _records.Values)
        {
            if (!IsInsideRange(record.Key, start, end))
            {
                if (end is not null && string.CompareOrdinal(record.Key, end) > 0)
                {
                    yield break;
                }

                continue;
            }

            yield return record;
        }
    }

    public void Clear()
    {
        _records.Clear();
    }

    private static bool IsInsideRange(string key, string? start, string? end)
    {
        if (start is not null && string.CompareOrdinal(key, start) < 0)
        {
            return false;
        }

        if (end is not null && string.CompareOrdinal(key, end) > 0)
        {
            return false;
        }

        return true;
    }
}

internal sealed record StoredRecord(long Sequence, string Key, string? Value, bool IsDeleted);

internal sealed record WalCommittedBatch(string Type, IReadOnlyList<StoredRecord> Records);
internal sealed record WalEnvelope(string Payload, string Checksum);
