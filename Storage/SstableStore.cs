using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace LsmWriteDb.Storage;

internal sealed class SstableStore
{
    public const int DefaultBlockSizeBytes = 16 * 1024;
    public const int DefaultMaxFileSizeBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _sstableDirectory;
    private readonly int _blockSizeBytes;
    private readonly int _maxFileSizeBytes;

    public SstableStore(
        string dataPath,
        int blockSizeBytes = DefaultBlockSizeBytes,
        int maxFileSizeBytes = DefaultMaxFileSizeBytes)
    {
        if (blockSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSizeBytes), "Block size must be greater than zero.");
        }

        if (maxFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes), "Maximum SSTable file size must be greater than zero.");
        }

        _sstableDirectory = Path.Combine(dataPath, "sstables");
        _blockSizeBytes = blockSizeBytes;
        _maxFileSizeBytes = maxFileSizeBytes;
    }

    public Task<int> CountAsync()
    {
        return Task.FromResult(GetDataFilesNewestFirst().Count);
    }

    public List<string> GetDataFilesNewestFirst()
    {
        if (!Directory.Exists(_sstableDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_sstableDirectory, "sstable-*.json")
            .Where(IsDataFile)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetCandidateDataFilesAsync(string key)
    {
        var candidates = new List<string>();
        foreach (var dataPath in GetDataFilesNewestFirst())
        {
            var index = await ReadSparseIndexAsync(dataPath);
            if (index is null || index.Any(entry =>
                    string.CompareOrdinal(key, entry.FirstKey) >= 0
                    && string.CompareOrdinal(key, entry.LastKey) <= 0))
            {
                candidates.Add(dataPath);
            }
        }

        return candidates;
    }

    public async Task<IReadOnlyList<StoredRecord>> ReadTableAsync(string dataPath)
    {
        var index = await ReadSparseIndexAsync(dataPath);
        if (index is not null)
        {
            var indexedRecords = new List<StoredRecord>();
            foreach (var entry in index)
            {
                indexedRecords.AddRange(await ReadBlockAsync(dataPath, entry));
            }

            return indexedRecords;
        }

        await using var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var legacyRecords = await JsonSerializer.DeserializeAsync<List<StoredRecord>>(stream, JsonOptions);
        return legacyRecords ?? [];
    }

    public async Task<StoredRecord?> TryGetAsync(string dataPath, string key)
    {
        if (!await MightContainAsync(dataPath, key))
        {
            return null;
        }

        var records = await ReadCandidateRecordsAsync(dataPath, key);

        StoredRecord? newest = null;
        foreach (var record in records)
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

        return newest;
    }

    public async Task WriteTableAsync(IReadOnlyList<StoredRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var createdFiles = new List<string>();
        var fileBlocks = new List<EncodedBlock>();
        var fileBytes = 0;
        try
        {
            foreach (var block in BuildBlocks(records))
            {
                if (fileBlocks.Count > 0 && fileBytes + block.Bytes.Length > _maxFileSizeBytes)
                {
                    createdFiles.Add(await WriteTableFileAsync(fileBlocks));
                    fileBlocks.Clear();
                    fileBytes = 0;
                }

                fileBlocks.Add(block);
                fileBytes += block.Bytes.Length;
            }

            if (fileBlocks.Count > 0)
            {
                createdFiles.Add(await WriteTableFileAsync(fileBlocks));
            }
        }
        catch
        {
            await DeleteTablesAsync(createdFiles);
            throw;
        }
    }

    private async Task<string> WriteTableFileAsync(IReadOnlyList<EncodedBlock> blocks)
    {
        var tableName = $"sstable-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json";
        var finalPath = Path.Combine(_sstableDirectory, tableName);
        var bloomPath = GetBloomPath(finalPath);
        var indexPath = GetIndexPath(finalPath);
        var tempPath = finalPath + ".tmp";
        var bloomTempPath = bloomPath + ".tmp";
        var indexTempPath = indexPath + ".tmp";

        Directory.CreateDirectory(_sstableDirectory);
        try
        {
            var records = blocks.SelectMany(block => block.Records).ToList();
            var bloom = BloomFilter.CreateForItemCount(records.Count);
            foreach (var record in records)
            {
                bloom.Add(record.Key);
            }

            var sparseIndex = await WriteBlocksAsync(tempPath, blocks);
            await File.WriteAllTextAsync(
                bloomTempPath,
                JsonSerializer.Serialize(bloom.ToSnapshot(), JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllTextAsync(
                indexTempPath,
                JsonSerializer.Serialize(sparseIndex, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.Move(bloomTempPath, bloomPath, overwrite: true);
            File.Move(indexTempPath, indexPath, overwrite: true);
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            File.Delete(tempPath);
            File.Delete(bloomTempPath);
            File.Delete(indexTempPath);
            File.Delete(finalPath);
            File.Delete(bloomPath);
            File.Delete(indexPath);
            throw;
        }
    }

    public Task DeleteTablesAsync(IEnumerable<string> dataPaths)
    {
        foreach (var dataPath in dataPaths)
        {
            File.Delete(dataPath);
            File.Delete(GetBloomPath(dataPath));
            File.Delete(GetIndexPath(dataPath));
        }

        return Task.CompletedTask;
    }

    internal async Task<IReadOnlyList<SparseIndexEntry>?> ReadSparseIndexAsync(string dataPath)
    {
        var indexPath = GetIndexPath(dataPath);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        await using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var index = await JsonSerializer.DeserializeAsync<List<SparseIndexEntry>>(stream, JsonOptions);
        return index ?? [];
    }

    internal async Task<BloomFilter?> ReadBloomFilterAsync(string dataPath)
    {
        var bloomPath = GetBloomPath(dataPath);
        if (!File.Exists(bloomPath))
        {
            return null;
        }

        await using var stream = new FileStream(bloomPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshot = await JsonSerializer.DeserializeAsync<BloomFilterSnapshot>(stream, JsonOptions);
        return snapshot is null ? null : BloomFilter.FromSnapshot(snapshot);
    }

    private async Task<bool> MightContainAsync(string dataPath, string key)
    {
        var bloom = await ReadBloomFilterAsync(dataPath);
        return bloom is null || bloom.MightContain(key);
    }

    private async Task<IReadOnlyList<StoredRecord>> ReadCandidateRecordsAsync(string dataPath, string key)
    {
        var index = await ReadSparseIndexAsync(dataPath);
        if (index is null)
        {
            return await ReadTableAsync(dataPath);
        }

        foreach (var entry in index)
        {
            if (string.CompareOrdinal(key, entry.FirstKey) >= 0
                && string.CompareOrdinal(key, entry.LastKey) <= 0)
            {
                return await ReadBlockAsync(dataPath, entry);
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<StoredRecord>> ReadBlockAsync(string dataPath, SparseIndexEntry entry)
    {
        var buffer = new byte[entry.Length];
        await using var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(entry.Offset, SeekOrigin.Begin);

        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of SSTable block at offset {entry.Offset}.");
            }

            totalRead += read;
        }

        if (entry.Checksum is not null && !string.Equals(Convert.ToHexString(SHA256.HashData(buffer)), entry.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SSTable checksum mismatch at offset {entry.Offset}.");
        }

        return JsonSerializer.Deserialize<List<StoredRecord>>(buffer, JsonOptions) ?? [];
    }

    private async Task<IReadOnlyList<SparseIndexEntry>> WriteBlocksAsync(
        string tempPath,
        IReadOnlyList<EncodedBlock> blocks)
    {
        var index = new List<SparseIndexEntry>();
        await using var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        foreach (var block in blocks)
        {
            var offset = stream.Position;
            await stream.WriteAsync(block.Bytes);
            index.Add(new SparseIndexEntry(
                block.Records[0].Key,
                block.Records[^1].Key,
                offset,
                block.Bytes.Length,
                block.Records.Count,
                Convert.ToHexString(SHA256.HashData(block.Bytes))));
        }

        return index;
    }

    private IEnumerable<EncodedBlock> BuildBlocks(IReadOnlyList<StoredRecord> records)
    {
        var current = new List<StoredRecord>();
        foreach (var record in records)
        {
            current.Add(record);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions);
            if (bytes.Length >= _blockSizeBytes)
            {
                yield return new EncodedBlock(current.ToList(), bytes);
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            yield return new EncodedBlock(
                current.ToList(),
                JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions));
        }
    }

    private static bool IsDataFile(string path)
    {
        return !path.EndsWith(".bloom.json", StringComparison.Ordinal)
            && !path.EndsWith(".index.json", StringComparison.Ordinal);
    }

    private static string GetBloomPath(string dataPath)
    {
        return Path.ChangeExtension(dataPath, ".bloom.json");
    }

    private static string GetIndexPath(string dataPath)
    {
        return Path.ChangeExtension(dataPath, ".index.json");
    }
}

internal sealed record SparseIndexEntry(
    string FirstKey,
    string LastKey,
    long Offset,
    int Length,
    int RecordCount,
    string? Checksum = null);

internal sealed record EncodedBlock(
    IReadOnlyList<StoredRecord> Records,
    byte[] Bytes);
