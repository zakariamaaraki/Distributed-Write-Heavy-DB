using System.Text;
using System.Text.Json;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Indexes;

public sealed record JsonValueIndexInfo(
    string Name,
    string Table,
    IReadOnlyList<string> Path);

public sealed record JsonValueIndexTreeDump(
    string Name,
    string Table,
    IReadOnlyList<string> Path,
    BPlusTreeDump<string, string> Tree);

internal sealed class JsonValueIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _indexDirectory;
    private readonly string _catalogPath;
    private readonly Dictionary<string, JsonValueIndex> _indexesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonValueIndex>> _indexesByTable = new(StringComparer.Ordinal);

    private bool _initialized;

    public JsonValueIndexStore(string dataPath)
    {
        _indexDirectory = Path.Combine(dataPath, "indexes");
        _catalogPath = Path.Combine(_indexDirectory, "catalog.json");
    }

    public async Task InitializeAsync(
        IReadOnlyCollection<string> tableNames,
        Func<string, Task<IReadOnlyList<KeyValueRow>>> readRowsAsync,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_indexDirectory);

            var knownTables = tableNames.ToHashSet(StringComparer.Ordinal);
            var definitions = (await ReadCatalogAsync(cancellationToken))
                .Where(index => knownTables.Contains(index.Table))
                .OrderBy(index => index.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var definition in definitions)
            {
                var rows = await readRowsAsync(definition.Table);
                AddIndex(BuildIndex(definition, IndexPath(definition.Name), rows, rebuild: false));
            }

            await WriteCatalogAsync(Definitions(), cancellationToken);
            _initialized = true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<JsonValueIndexInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            return Definitions()
                .Select(definition => new JsonValueIndexInfo(definition.Name, definition.Table, definition.Path))
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<JsonValueIndexTreeDump>> DumpTreesAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            return _indexesByName.Values
                .OrderBy(index => index.Definition.Name, StringComparer.Ordinal)
                .Select(index => index.DumpTree())
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<JsonValueIndexTreeDump?> DumpTreeAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = IndexNames.Normalize(name);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            return _indexesByName.TryGetValue(normalizedName, out var index)
                ? index.DumpTree()
                : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> CreateAsync(
        JsonValueIndexDefinition definition,
        IReadOnlyList<KeyValueRow> rows,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();

            if (_indexesByName.ContainsKey(definition.Name))
            {
                return false;
            }

            AddIndex(BuildIndex(definition, IndexPath(definition.Name), rows, rebuild: true));
            await WriteCatalogAsync(Definitions(), cancellationToken);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task RemoveTableAsync(string table, CancellationToken cancellationToken = default)
    {
        var normalizedTable = TableNames.Normalize(table);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            if (!_indexesByTable.Remove(normalizedTable, out var indexes))
                return;

            foreach (var index in indexes)
            {
                _indexesByName.Remove(index.Definition.Name);
                var path = IndexPath(index.Definition.Name);
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }

            await WriteCatalogAsync(Definitions(), cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }
    public async Task<IReadOnlyList<string>?> TrySearchAsync(
        string table,
        IReadOnlyList<string> path,
        string expected,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();

            var index = FindIndex(table, path);
            return index?.Search(expected);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ApplyPutAsync(
        string table,
        string key,
        string? oldValue,
        string newValue,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();

            foreach (var index in IndexesForTable(table))
            {
                index.Replace(key, oldValue, newValue);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ApplyDeleteAsync(
        string table,
        string key,
        string? oldValue,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();

            foreach (var index in IndexesForTable(table))
            {
                index.Delete(key, oldValue);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private JsonValueIndex? FindIndex(string table, IReadOnlyList<string> path)
    {
        return IndexesForTable(table)
            .FirstOrDefault(index => index.Definition.Path.SequenceEqual(path, StringComparer.Ordinal));
    }

    private IReadOnlyList<JsonValueIndex> IndexesForTable(string table)
    {
        return _indexesByTable.TryGetValue(table, out var indexes) ? indexes : [];
    }

    private void AddIndex(JsonValueIndex index)
    {
        _indexesByName[index.Definition.Name] = index;
        if (!_indexesByTable.TryGetValue(index.Definition.Table, out var tableIndexes))
        {
            tableIndexes = [];
            _indexesByTable[index.Definition.Table] = tableIndexes;
        }

        tableIndexes.Add(index);
    }

    private JsonValueIndex BuildIndex(
        JsonValueIndexDefinition definition,
        string indexPath,
        IReadOnlyList<KeyValueRow> rows,
        bool rebuild)
    {
        var existingTree = DiskBackedBPlusTree.Exists(indexPath);
        var tree = !rebuild && existingTree
            ? DiskBackedBPlusTree.Open(indexPath)
            : DiskBackedBPlusTree.CreateNew(indexPath);

        if (rebuild || !existingTree)
        {
            foreach (var row in rows)
            {
                if (JsonValueAccessor.TryReadComparableValue(row.Value, definition.Path, out var indexedValue))
                {
                    tree.Insert(indexedValue, row.Key);
                }
            }
        }

        return new JsonValueIndex(definition, tree);
    }

    private string IndexPath(string name)
    {
        return Path.Combine(_indexDirectory, IndexNames.Normalize(name));
    }

    private IReadOnlyList<JsonValueIndexDefinition> Definitions()
    {
        return _indexesByName.Values
            .Select(index => index.Definition)
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyList<JsonValueIndexDefinition>> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
        {
            return [];
        }

        await using var stream = new FileStream(_catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var catalog = await JsonSerializer.DeserializeAsync<JsonValueIndexCatalogSnapshot>(stream, JsonOptions, cancellationToken);
        return catalog?.Indexes
            .Select(NormalizeDefinition)
            .ToList()
            ?? [];
    }

    private async Task WriteCatalogAsync(IReadOnlyList<JsonValueIndexDefinition> definitions, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_indexDirectory);
        var catalog = new JsonValueIndexCatalogSnapshot(definitions);
        var json = JsonSerializer.Serialize(catalog, JsonOptions);
        await File.WriteAllTextAsync(_catalogPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private static JsonValueIndexDefinition NormalizeDefinition(JsonValueIndexDefinition definition)
    {
        return new JsonValueIndexDefinition(
            IndexNames.Normalize(definition.Name),
            TableNames.Normalize(definition.Table),
            definition.Path.Select(IndexNames.ValidatePathPart).ToList());
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Index store has not been initialized.");
        }
    }
}

internal sealed record JsonValueIndexDefinition(
    string Name,
    string Table,
    IReadOnlyList<string> Path);

internal sealed record JsonValueIndexCatalogSnapshot(IReadOnlyList<JsonValueIndexDefinition> Indexes);

internal sealed class JsonValueIndex
{
    private readonly DiskBackedBPlusTree _tree;

    public JsonValueIndex(JsonValueIndexDefinition definition, DiskBackedBPlusTree tree)
    {
        Definition = definition;
        _tree = tree;
    }

    public JsonValueIndexDefinition Definition { get; }

    public void Replace(string key, string? oldValue, string newValue)
    {
        Delete(key, oldValue);
        if (JsonValueAccessor.TryReadComparableValue(newValue, Definition.Path, out var indexedValue))
        {
            _tree.Insert(indexedValue, key);
        }
    }

    public void Delete(string key, string? oldValue)
    {
        if (oldValue is not null
            && JsonValueAccessor.TryReadComparableValue(oldValue, Definition.Path, out var indexedValue))
        {
            _tree.Remove(indexedValue, key);
        }
    }

    public IReadOnlyList<string> Search(string expected)
    {
        return _tree.Search(expected)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }

    public JsonValueIndexTreeDump DumpTree()
    {
        return new JsonValueIndexTreeDump(
            Definition.Name,
            Definition.Table,
            Definition.Path,
            _tree.Dump());
    }
}

internal static class IndexNames
{
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Index name is required.", nameof(name));
        }

        var normalized = name.Trim().ToLowerInvariant();
        if (normalized.Length > 64)
        {
            throw new ArgumentException("Index name cannot be longer than 64 characters.", nameof(name));
        }

        if (!IsIdentifierStart(normalized[0]) || normalized.Any(character => !IsIdentifierPart(character)))
        {
            throw new ArgumentException("Index name must start with a letter or underscore and contain only letters, digits, and underscores.", nameof(name));
        }

        return normalized;
    }

    public static string ValidatePathPart(string part)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            throw new ArgumentException("Index path parts are required.", nameof(part));
        }

        if (!IsIdentifierStart(part[0]) || part.Any(character => !IsIdentifierPart(character)))
        {
            throw new ArgumentException("Index path parts must start with a letter or underscore and contain only letters, digits, and underscores.", nameof(part));
        }

        return part;
    }

    private static bool IsIdentifierStart(char character)
    {
        return character == '_' || character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    }

    private static bool IsIdentifierPart(char character)
    {
        return IsIdentifierStart(character) || character is >= '0' and <= '9';
    }
}
