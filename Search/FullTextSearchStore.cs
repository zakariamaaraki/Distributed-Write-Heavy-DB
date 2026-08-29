using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LsmWriteDb.Storage;
using LsmWriteDb.Indexes;

namespace LsmWriteDb.Search;

public sealed record SearchIndexDefinition(string Name, string Table, IReadOnlyList<string> Fields, string Analyzer = "standard", int Version = 1);
public sealed record SearchRequest(string Query, string Operator = "or", int Limit = 20, int Offset = 0);
public sealed record SearchHit(string Key, string Value, double Score);
public sealed record SearchResponse(string Index, string Query, IReadOnlyList<SearchHit> Hits, int Total);

public sealed class FullTextSearchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex TokenPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private readonly string _root;
    private readonly string _catalogPath;
    private readonly LsmStoreOptions _options;
    private readonly Dictionary<string, SearchIndexDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LsmStore> _stores = new(StringComparer.Ordinal);
    private Func<string, Task<IReadOnlyList<KeyValueRow>>>? _scan;

    public FullTextSearchStore(LsmStoreOptions options)
    {
        _options = options;
        _root = Path.Combine(options.DataPath, "search-indexes");
        _catalogPath = Path.Combine(_root, "catalog.json");
    }

    public async Task InitializeAsync(IReadOnlySet<string> knownTables, Func<string, Task<IReadOnlyList<KeyValueRow>>> scan, CancellationToken cancellationToken = default)
    {
        _scan = scan;
        Directory.CreateDirectory(_root);
        if (!File.Exists(_catalogPath)) return;
        await using var stream = new FileStream(_catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var catalog = await JsonSerializer.DeserializeAsync<SearchCatalogSnapshot>(stream, JsonOptions, cancellationToken);
        foreach (var definition in catalog?.Indexes ?? [])
        {
            if (!knownTables.Contains(definition.Table)) continue;
            _definitions[definition.Name] = definition;
            var store = OpenStore(definition);
            await store.InitializeAsync();
            _stores[definition.Name] = store;
        }
    }

    public IReadOnlyList<SearchIndexDefinition> List() => _definitions.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();

    public async Task<bool> CreateAsync(string table, string name, IReadOnlyList<string> fields, Func<string, Task<IReadOnlyList<KeyValueRow>>> scan, CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var normalizedTable = TableNames.Normalize(table);
        if (fields.Count == 0) throw new ArgumentException("At least one searchable field is required.", nameof(fields));
        if (_definitions.ContainsKey(normalizedName)) return false;
        var definition = new SearchIndexDefinition(normalizedName, normalizedTable, fields.Select(NormalizeField).Distinct(StringComparer.Ordinal).ToList());
        _definitions[normalizedName] = definition;
        var store = OpenStore(definition);
        await store.InitializeAsync();
        _stores[normalizedName] = store;
        await RebuildAsync(normalizedName, scan, cancellationToken);
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

    public async Task RebuildAsync(string name, Func<string, Task<IReadOnlyList<KeyValueRow>>> scan, CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryGetValue(NormalizeName(name), out var definition)) throw new ArgumentException($"Search index '{name}' does not exist.");
        var store = _stores[definition.Name];
        await store.DeleteDataAsync();
        await store.InitializeAsync();
        foreach (var batch in BuildOperations(await scan(definition.Table), definition).Chunk(500))
            await store.ApplyBatchAsync(batch);
    }

    public async Task ApplyPutAsync(string table, string key, string? oldValue, string newValue)
    {
        foreach (var definition in _definitions.Values.Where(x => x.Table == TableNames.Normalize(table)))
        {
            var operations = new List<StoreWriteOperation>();
            if (oldValue is not null) operations.AddRange(BuildPostingOperations(key, oldValue, definition, true));
            operations.AddRange(BuildPostingOperations(key, newValue, definition, false));
            if (operations.Count > 0) await _stores[definition.Name].ApplyBatchAsync(operations);
        }
    }

    public async Task ApplyDeleteAsync(string table, string key, string? oldValue)
    {
        if (oldValue is null) return;
        foreach (var definition in _definitions.Values.Where(x => x.Table == TableNames.Normalize(table)))
        {
            var operations = BuildPostingOperations(key, oldValue, definition, true).ToList();
            if (operations.Count > 0) await _stores[definition.Name].ApplyBatchAsync(operations);
        }
    }

    public async Task<SearchResponse> SearchAsync(string name, SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (_scan is null) throw new InvalidOperationException("Search store is not initialized.");
        if (!_definitions.TryGetValue(NormalizeName(name), out var definition)) throw new ArgumentException($"Search index '{name}' does not exist.");
        var clauses = ParseQuery(request.Query);
        if (clauses.Count == 0) return new SearchResponse(definition.Name, request.Query, [], 0);
        var maps = new List<Dictionary<string, List<Posting>>>();
        foreach (var clause in clauses)
        {
            var map = new Dictionary<string, List<Posting>>(StringComparer.Ordinal);
            var fields = clause.Field is null ? definition.Fields : definition.Fields.Where(f => FieldName(f) == clause.Field);
            foreach (var field in fields)
            {
                var prefix = Prefix(field, clause.Term);
                var rows = await _stores[definition.Name].RangeAsync(prefix, prefix + "\uFFFF", 1_000);
                foreach (var row in rows)
                {
                    if (!row.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var posting = JsonSerializer.Deserialize<Posting>(row.Value);
                    if (posting is null) continue;
                    var key = DecodeDocument(row.Key[prefix.Length..]);
                    if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                    list.Add(posting);
                }
            }
            maps.Add(map);
        }

        IEnumerable<string> keys = string.Equals(request.Operator, "and", StringComparison.OrdinalIgnoreCase)
            ? maps.Select(x => x.Keys.AsEnumerable()).Aggregate((left, right) => left.Intersect(right, StringComparer.Ordinal))
            : maps.SelectMany(x => x.Keys).Distinct(StringComparer.Ordinal);
        var source = (await _scan(definition.Table)).ToDictionary(x => x.Key, StringComparer.Ordinal);
        var hits = new List<SearchHit>();
        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var row) || !MatchesPhrases(clauses, maps, key)) continue;
            var score = 0d;
            for (var i = 0; i < maps.Count; i++)
            {
                if (maps[i].TryGetValue(key, out var postings))
                    score += postings.Max(p => Bm25(p, maps[i].Count, Math.Max(1, source.Count)));
            }
            hits.Add(new SearchHit(key, row.Value, score));
        }
        var ordered = hits.OrderByDescending(x => x.Score).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        var offset = Math.Clamp(request.Offset, 0, 10_000);
        var limit = Math.Clamp(request.Limit, 1, 1_000);
        return new SearchResponse(definition.Name, request.Query, ordered.Skip(offset).Take(limit).ToList(), ordered.Count);
    }

    private async Task SaveCatalogAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(_catalogPath, JsonSerializer.Serialize(new SearchCatalogSnapshot(List()), JsonOptions), cancellationToken);
    }

    private LsmStore OpenStore(SearchIndexDefinition definition) => new(new LsmStoreOptions(
        Path.Combine(_root, definition.Name), _options.FlushThreshold, "__search_" + definition.Name,
        _options.BlockSizeBytes, _options.MaxSstableFileSizeBytes));

    private static IEnumerable<StoreWriteOperation> BuildOperations(IEnumerable<KeyValueRow> rows, SearchIndexDefinition definition)
    {
        foreach (var row in rows) foreach (var operation in BuildPostingOperations(row.Key, row.Value, definition, false)) yield return operation;
    }

    private static IEnumerable<StoreWriteOperation> BuildPostingOperations(string documentKey, string json, SearchIndexDefinition definition, bool deleted)
    {
        foreach (var field in definition.Fields)
        {
            var tokens = Tokenize(ReadField(json, field)).ToList();
            foreach (var group in tokens.Select((term, position) => (term, position)).GroupBy(x => x.term))
            {
                var posting = new Posting(group.Count(), group.Select(x => x.position).ToArray(), tokens.Count, FieldName(field));
                var key = PostingKey(field, group.Key, documentKey);
                yield return deleted ? StoreWriteOperation.Delete(StoreTable(definition), key) : StoreWriteOperation.Put(StoreTable(definition), key, JsonSerializer.Serialize(posting));
            }
        }
    }

    private static string ReadField(string json, string field)
    {
        var parts = field.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && string.Equals(parts[0], "value", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        return parts.Count == 0 ? json : JsonValueAccessor.TryReadComparableValue(json, parts, out var value) ? value : string.Empty;
    }

    private static List<QueryClause> ParseQuery(string query)
    {
        var clauses = new List<QueryClause>();
        var phraseId = 0;
        foreach (Match match in Regex.Matches(query ?? string.Empty, "\"([^\"]+)\"|[^\\s]+"))
        {
            var phrase = match.Groups[1].Success;
            var raw = phrase ? match.Groups[1].Value : match.Value;
            string? field = null;
            var colon = raw.IndexOf(':');
            if (colon > 0) { field = raw[..colon].ToLowerInvariant(); raw = raw[(colon + 1)..]; }
            foreach (var term in Tokenize(raw)) clauses.Add(new QueryClause(field, term, phrase, phrase ? phraseId : -1));
            if (phrase) phraseId++;
        }
        return clauses;
    }

    private static bool MatchesPhrases(IReadOnlyList<QueryClause> clauses, IReadOnlyList<Dictionary<string, List<Posting>>> maps, string key)
    {
        foreach (var group in clauses.Select((clause, index) => (clause, index)).Where(x => x.clause.IsPhrase).GroupBy(x => x.clause.PhraseId))
        {
            var terms = group.ToList();
            if (!maps[terms[0].index].TryGetValue(key, out var first)) return false;
            var matched = first.Any(posting => posting.Positions.Any(start => terms.Skip(1).Select((term, offset) => maps[term.index].TryGetValue(key, out var next) && next.Any(p => p.Field == posting.Field && p.Positions.Contains(start + offset + 1))).All(x => x)));
            if (!matched) return false;
        }
        return true;
    }

    private static double Bm25(Posting posting, int documentFrequency, int totalDocuments)
    {
        const double k1 = 1.2, b = 0.75, averageLength = 100.0;
        var idf = Math.Log(1 + (totalDocuments - documentFrequency + 0.5) / (documentFrequency + 0.5));
        return idf * posting.Frequency * (k1 + 1) / (posting.Frequency + k1 * (1 - b + b * posting.Length / averageLength));
    }

    private static IEnumerable<string> Tokenize(string text) => TokenPattern.Matches(text.ToLowerInvariant()).Select(x => x.Value);
    private static string StoreTable(SearchIndexDefinition definition) => "__search_" + definition.Name;
    private static string Prefix(string field, string term) => FieldName(field) + "\u001F" + term + "\u001F";
    private static string PostingKey(string field, string term, string document) => Prefix(field, term) + Convert.ToBase64String(Encoding.UTF8.GetBytes(document));
    private static string DecodeDocument(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    private static string FieldName(string field) => field.StartsWith("value.", StringComparison.OrdinalIgnoreCase) ? field[6..] : field;
    private static string NormalizeField(string field) => field.Trim().ToLowerInvariant();
    private static string NormalizeName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(normalized, "^[a-z_][a-z0-9_]*$")) throw new ArgumentException("Search index names must contain only letters, digits, and underscores.", nameof(name));
        return normalized;
    }

    private sealed record SearchCatalogSnapshot(IReadOnlyList<SearchIndexDefinition> Indexes);
    private sealed record Posting(int Frequency, IReadOnlyList<int> Positions, int Length, string Field);
    private sealed record QueryClause(string? Field, string Term, bool IsPhrase, int PhraseId);
}