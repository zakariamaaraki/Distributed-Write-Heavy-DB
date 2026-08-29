using System.Text;
using System.Text.Json;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Indexes;

public sealed record SstableValueIndexInfo(string Name, string Table, IReadOnlyList<string> Path);

public sealed class SstableValueIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const char Separator = '\u001F';
    private readonly string _root;
    private readonly string _catalogPath;
    private readonly LsmStoreOptions _options;
    private readonly Dictionary<string, SstableValueIndexDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LsmStore> _stores = new(StringComparer.Ordinal);

    public SstableValueIndexStore(LsmStoreOptions options)
    {
        _options = options;
        _root = Path.Combine(options.DataPath, "sstable-indexes");
        _catalogPath = Path.Combine(_root, "catalog.json");
    }

    public async Task InitializeAsync(IReadOnlyList<string> knownTables, Func<string, Task<IReadOnlyList<KeyValueRow>>> scan, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        if (File.Exists(_catalogPath))
        {
            await using var stream = new FileStream(_catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var catalog = await JsonSerializer.DeserializeAsync<SstableIndexCatalogSnapshot>(stream, JsonOptions, cancellationToken);
            foreach (var definition in catalog?.Indexes ?? [])
            {
                if (!knownTables.Contains(definition.Table, StringComparer.Ordinal)) continue;
                _definitions[definition.Name] = definition;
                var store = OpenStore(definition);
                await store.InitializeAsync();
                _stores[definition.Name] = store;
            }
        }
    }

    public IReadOnlyList<SstableValueIndexInfo> List() => _definitions.Values
        .OrderBy(x => x.Name, StringComparer.Ordinal)
        .Select(x => new SstableValueIndexInfo(x.Name, x.Table, x.Path))
        .ToList();

    public async Task<bool> CreateAsync(string table, string name, IReadOnlyList<string> path, Func<string, Task<IReadOnlyList<KeyValueRow>>> scan, CancellationToken cancellationToken = default)
    {
        var normalizedName = IndexNames.Normalize(name);
        var definition = new SstableValueIndexDefinition(normalizedName, TableNames.Normalize(table), path.Select(IndexNames.ValidatePathPart).ToList());
        if (_definitions.ContainsKey(normalizedName)) return false;
        _definitions[normalizedName] = definition;
        var store = OpenStore(definition);
        await store.InitializeAsync();
        _stores[normalizedName] = store;
        foreach (var batch in BuildOperations(await scan(definition.Table), definition).Chunk(500))
            await store.ApplyBatchAsync(batch);
        await SaveCatalogAsync(cancellationToken);
        return true;
    }

    public async Task RemoveTableAsync(string table, CancellationToken cancellationToken = default)
    {
        var normalized = TableNames.Normalize(table);
        var names = _definitions.Values.Where(x => x.Table == normalized).Select(x => x.Name).ToList();
        foreach (var name in names)
        {
            if (_stores.Remove(name, out var store)) await store.DeleteDataAsync();
            _definitions.Remove(name);
        }
        if (names.Count > 0) await SaveCatalogAsync(cancellationToken);
    }

    public async Task ApplyPutAsync(string table, string key, string? oldValue, string newValue)
    {
        foreach (var definition in _definitions.Values.Where(x => x.Table == TableNames.Normalize(table)))
        {
            var operations = new List<StoreWriteOperation>();
            if (oldValue is not null && JsonValueAccessor.TryReadComparableValue(oldValue, definition.Path, out var oldIndexed))
                operations.Add(StoreWriteOperation.Delete(StoreTable(definition), PostingKey(oldIndexed, key)));
            if (JsonValueAccessor.TryReadComparableValue(newValue, definition.Path, out var newIndexed))
                operations.Add(StoreWriteOperation.Put(StoreTable(definition), PostingKey(newIndexed, key), string.Empty));
            if (operations.Count > 0) await _stores[definition.Name].ApplyBatchAsync(operations);
        }
    }

    public async Task ApplyDeleteAsync(string table, string key, string? oldValue)
    {
        if (oldValue is null) return;
        foreach (var definition in _definitions.Values.Where(x => x.Table == TableNames.Normalize(table)))
        {
            if (JsonValueAccessor.TryReadComparableValue(oldValue, definition.Path, out var indexed))
                await _stores[definition.Name].ApplyBatchAsync([StoreWriteOperation.Delete(StoreTable(definition), PostingKey(indexed, key))]);
        }
    }

    public async Task<IReadOnlyList<string>?> TrySearchAsync(string table, IReadOnlyList<string> path, string expected, CancellationToken cancellationToken = default)
    {
        var definition = _definitions.Values.FirstOrDefault(x => x.Table == TableNames.Normalize(table) && x.Path.SequenceEqual(path, StringComparer.Ordinal));
        if (definition is null) return null;
        var prefix = PostingPrefix(expected);
        var rows = await _stores[definition.Name].RangeAsync(prefix, prefix + "\uFFFF", 1_000);
        return rows.Where(row => row.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(row => DecodeKey(row.Key[prefix.Length..]))
            .ToList();
    }

    private async Task SaveCatalogAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(_catalogPath, JsonSerializer.Serialize(new SstableIndexCatalogSnapshot(_definitions.Values.OrderBy(x => x.Name).ToList()), JsonOptions), cancellationToken);
    }

    private LsmStore OpenStore(SstableValueIndexDefinition definition) => new(new LsmStoreOptions(
        Path.Combine(_root, definition.Name), _options.FlushThreshold, StoreTable(definition), _options.BlockSizeBytes, _options.MaxSstableFileSizeBytes));

    private static IEnumerable<StoreWriteOperation> BuildOperations(IEnumerable<KeyValueRow> rows, SstableValueIndexDefinition definition)
    {
        foreach (var row in rows)
        {
            if (JsonValueAccessor.TryReadComparableValue(row.Value, definition.Path, out var indexed))
                yield return StoreWriteOperation.Put(StoreTable(definition), PostingKey(indexed, row.Key), string.Empty);
        }
    }

    private static string StoreTable(SstableValueIndexDefinition definition) => "__sstable_index_" + definition.Name;
    private static string PostingPrefix(string indexedValue) => Convert.ToBase64String(Encoding.UTF8.GetBytes(indexedValue)) + Separator;
    private static string PostingKey(string indexedValue, string rowKey) => PostingPrefix(indexedValue) + Convert.ToBase64String(Encoding.UTF8.GetBytes(rowKey));
    private static string DecodeKey(string encoded) => Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
}

internal sealed record SstableValueIndexDefinition(string Name, string Table, IReadOnlyList<string> Path);
internal sealed record SstableIndexCatalogSnapshot(IReadOnlyList<SstableValueIndexDefinition> Indexes);