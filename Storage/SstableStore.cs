using System.Text;
using System.Text.Json;

namespace LsmWriteDb.Storage;

internal sealed class SstableStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _sstableDirectory;

    public SstableStore(string dataPath)
    {
        _sstableDirectory = Path.Combine(dataPath, "sstables");
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
            .Where(path => !path.EndsWith(".bloom.json", StringComparison.Ordinal))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<StoredRecord>> ReadTableAsync(string dataPath)
    {
        await using var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var records = await JsonSerializer.DeserializeAsync<List<StoredRecord>>(stream, JsonOptions);
        return records ?? [];
    }

    public async Task<StoredRecord?> TryGetAsync(string dataPath, string key)
    {
        if (!await MightContainAsync(dataPath, key))
        {
            return null;
        }

        StoredRecord? newest = null;
        foreach (var record in await ReadTableAsync(dataPath))
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
        var tableName = $"sstable-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json";
        var finalPath = Path.Combine(_sstableDirectory, tableName);
        var bloomPath = GetBloomPath(finalPath);
        var tempPath = finalPath + ".tmp";
        var bloomTempPath = bloomPath + ".tmp";
        var json = JsonSerializer.Serialize(records, JsonOptions);

        Directory.CreateDirectory(_sstableDirectory);

        var bloom = BloomFilter.CreateForItemCount(records.Count);
        foreach (var record in records)
        {
            bloom.Add(record.Key);
        }

        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await File.WriteAllTextAsync(bloomTempPath, JsonSerializer.Serialize(bloom.ToSnapshot(), JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        File.Move(tempPath, finalPath, overwrite: true);
        File.Move(bloomTempPath, bloomPath, overwrite: true);
    }

    public Task DeleteTablesAsync(IEnumerable<string> dataPaths)
    {
        foreach (var dataPath in dataPaths)
        {
            File.Delete(dataPath);
            File.Delete(GetBloomPath(dataPath));
        }

        return Task.CompletedTask;
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

    private static string GetBloomPath(string dataPath)
    {
        return Path.ChangeExtension(dataPath, ".bloom.json");
    }
}
