using System.Text;
using System.Text.Json;

namespace LsmWriteDb.Storage;

public sealed record LsmStoreOptions(string DataPath, int FlushThreshold);

public sealed record KeyValueRow(string Key, string Value);

public sealed record StoreStats(
    int MemTableEntries,
    int SstableCount,
    long LastSequence,
    int FlushThreshold);

public sealed class LsmStore
{
    private static readonly JsonSerializerOptions WalJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions TableJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly LsmStoreOptions _options;
    private readonly OrderedMemTable _memTable = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _walPath;
    private readonly string _sstableDirectory;

    private bool _initialized;
    private long _lastSequence;

    public LsmStore(LsmStoreOptions options)
    {
        if (options.FlushThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Flush threshold must be greater than zero.");
        }

        _options = options;
        _walPath = Path.Combine(options.DataPath, "wal.log");
        _sstableDirectory = Path.Combine(options.DataPath, "sstables");
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
            Directory.CreateDirectory(_sstableDirectory);

            _lastSequence = await FindMaxSequenceAsync();

            foreach (var record in await ReadWalAsync())
            {
                _memTable.Apply(record);
                _lastSequence = Math.Max(_lastSequence, record.Sequence);
            }

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
            foreach (var file in GetSstableFilesNewestFirst())
            {
                foreach (var record in await ReadSstableAsync(file))
                {
                    if (record.Key != key)
                    {
                        continue;
                    }

                    if (newest is null || record.Sequence > newest.Sequence)
                    {
                        newest = record;
                    }
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

            foreach (var file in GetSstableFilesNewestFirst())
            {
                foreach (var record in await ReadSstableAsync(file))
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

    public async Task<StoreStats> GetStatsAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            EnsureInitialized();

            return new StoreStats(
                _memTable.Count,
                GetSstableFilesNewestFirst().Count,
                _lastSequence,
                _options.FlushThreshold);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private StoredRecord NextRecord(string key, string? value, bool isDeleted)
    {
        var sequence = ++_lastSequence;
        return new StoredRecord(sequence, key, value, isDeleted);
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

        await WriteSstableAsync(snapshot);
        _memTable.Clear();
        await ClearWalAsync();
        await CompactAsync();
    }

    private async Task CompactAsync()
    {
        var files = GetSstableFilesNewestFirst();
        if (files.Count == 0)
        {
            return;
        }

        var newestByKey = new SortedDictionary<string, StoredRecord>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var record in await ReadSstableAsync(file))
            {
                KeepNewest(newestByKey, record);
            }
        }

        var liveRecords = newestByKey.Values
            .Where(record => !record.IsDeleted)
            .ToList();

        if (liveRecords.Count > 0)
        {
            await WriteSstableAsync(liveRecords);
        }

        foreach (var file in files)
        {
            File.Delete(file);
        }
    }

    private async Task<long> FindMaxSequenceAsync()
    {
        var maxSequence = 0L;

        foreach (var file in GetSstableFilesNewestFirst())
        {
            foreach (var record in await ReadSstableAsync(file))
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
        var line = JsonSerializer.Serialize(record, WalJsonOptions);

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

        if (IsOnlyWhiteSpace(json))
        {
            return records;
        }

        var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowMultipleValues = true });

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<StoredRecord>(ref reader, WalJsonOptions);
            if (record is null)
            {
                continue;
            }

            records.Add(record);
        }

        return records;
    }

    private static bool IsOnlyWhiteSpace(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task WriteSstableAsync(IReadOnlyList<StoredRecord> records)
    {
        var tableName = $"sstable-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json";
        var finalPath = Path.Combine(_sstableDirectory, tableName);
        var tempPath = finalPath + ".tmp";
        var json = JsonSerializer.Serialize(records, TableJsonOptions);

        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    private async Task<IReadOnlyList<StoredRecord>> ReadSstableAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var records = await JsonSerializer.DeserializeAsync<List<StoredRecord>>(stream, TableJsonOptions);
        return records ?? [];
    }

    private List<string> GetSstableFilesNewestFirst()
    {
        if (!Directory.Exists(_sstableDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_sstableDirectory, "sstable-*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
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

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Store has not been initialized.");
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
