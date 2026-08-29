using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Search;
using LsmWriteDb.Raft;
using LsmWriteDb.Storage;
using LsmWriteDb.Sql;

namespace LsmWriteDb.Tests;

public sealed class FullTextSearchTests
{
    [Fact]
    public async Task SearchIndex_BuildsFromTableAndRanksMatches()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var database = await CreateDatabaseAsync(dataPath);
            await database.CreateTableAsync("articles");
            await database.PutAsync("articles", "1", "{\"title\":\"Distributed database\",\"body\":\"database database\"}");
            await database.PutAsync("articles", "2", "{\"title\":\"Database\",\"body\":\"storage\"}");
            Assert.True(await database.CreateSearchIndexAsync("articles", "articles_text", ["value.title", "value.body"]));

            var result = await database.SearchAsync("articles_text", new SearchRequest("database"));

            Assert.Equal(2, result.Total);
            Assert.Equal(["1", "2"], result.Hits.Select(hit => hit.Key));
            Assert.True(result.Hits[0].Score >= result.Hits[1].Score);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(dataPath, "search-indexes", "articles_text", "sstables"), "sstable-*.json"));
        }
        finally { DeleteTempDataPath(dataPath); }
    }

    [Fact]
    public async Task SearchIndex_UpdatesAndDeletesPostings()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var database = await CreateDatabaseAsync(dataPath);
            await database.CreateTableAsync("articles");
            await database.CreateSearchIndexAsync("articles", "articles_text", ["value.title"]);
            await database.PutAsync("articles", "1", "{\"title\":\"alpha\"}");
            Assert.Equal(["1"], (await database.SearchAsync("articles_text", new SearchRequest("alpha"))).Hits.Select(x => x.Key));

            await database.PutAsync("articles", "1", "{\"title\":\"beta\"}");
            await database.DeleteAsync("articles", "1");

            Assert.Empty((await database.SearchAsync("articles_text", new SearchRequest("alpha"))).Hits);
            Assert.Empty((await database.SearchAsync("articles_text", new SearchRequest("beta"))).Hits);
        }
        finally { DeleteTempDataPath(dataPath); }
    }

    [Fact]
    public async Task SearchIndex_SupportsAndAndPhraseQueries()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var database = await CreateDatabaseAsync(dataPath);
            await database.CreateTableAsync("articles");
            await database.PutAsync("articles", "1", "{\"body\":\"distributed database engine\"}");
            await database.PutAsync("articles", "2", "{\"body\":\"distributed storage engine\"}");
            await database.CreateSearchIndexAsync("articles", "articles_text", ["value.body"]);

            var andResult = await database.SearchAsync("articles_text", new SearchRequest("distributed engine", "and"));
            var phraseResult = await database.SearchAsync("articles_text", new SearchRequest("\"database engine\""));

            Assert.Equal(["1", "2"], andResult.Hits.Select(x => x.Key));
            Assert.Equal(["1"], phraseResult.Hits.Select(x => x.Key));
        }
        finally { DeleteTempDataPath(dataPath); }
    }

    [Fact]
    public async Task SearchIndex_RestoresDefinitionsAndPostingsOnRestart()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var first = await CreateDatabaseAsync(dataPath);
            await first.CreateTableAsync("articles");
            await first.PutAsync("articles", "1", "{\"title\":\"persistent search\"}");
            await first.CreateSearchIndexAsync("articles", "articles_text", ["value.title"]);

            var restored = await CreateDatabaseAsync(dataPath);
            var result = await restored.SearchAsync("articles_text", new SearchRequest("persistent"));

            Assert.Equal(["1"], result.Hits.Select(x => x.Key));
            Assert.Equal("articles", Assert.Single(await restored.ListSearchIndexesAsync()).Table);
        }
        finally { DeleteTempDataPath(dataPath); }
    }

    [Fact]
    public async Task SqlSurface_CreatesAndQueriesSearchIndex()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var database = await CreateDatabaseAsync(dataPath);
            var sql = new SqlEngine(database, new LsmWriteDb.Transactions.TransactionManager(database));
            await sql.ExecuteAsync(new SqlQueryRequest("CREATE TABLE articles", null));
            await sql.ExecuteAsync(new SqlQueryRequest("INSERT INTO articles (key, value) VALUES ('1', '{\"title\":\"searchable text\"}')", null));
            var create = await sql.ExecuteAsync(new SqlQueryRequest("CREATE SEARCH INDEX articles_text ON articles (value.title)", null));
            var result = await sql.ExecuteAsync(new SqlQueryRequest("SEARCH articles_text MATCH 'searchable' LIMIT 5", null));

            Assert.Equal("CREATE SEARCH INDEX", create.StatementType);
            Assert.Equal("SEARCH", result.StatementType);
            Assert.Equal("1", result.Rows[0]["key"]);
        }
        finally { DeleteTempDataPath(dataPath); }
    }
    [Fact]
    public async Task TableRaftCoordinator_PropagatesSearchIndexDefinitionToPeer()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var options = new LsmStoreOptions(dataPath, FlushThreshold: 100);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            var requestPath = string.Empty;
            var requestBody = string.Empty;
            using var client = new HttpClient(new RecordingHandler(request =>
            {
                requestPath = request.RequestUri!.AbsolutePath;
                requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }));
            var coordinator = new TableRaftCoordinator(new RaftOptions
            {
                Enabled = true,
                NodeId = "node-a",
                PublicUrl = "http://node-a",
                Peers = [new RaftPeerOptions { NodeId = "node-b", Url = "http://node-b" }]
            }, options, client, database);

            await coordinator.EnsureSearchIndexOnPeersAsync("Articles_Text", "articles", ["value.title"]);

            Assert.Equal("/raft/search-indexes/articles_text/ensure", requestPath);
            Assert.Contains("articles", requestBody, StringComparison.Ordinal);
            Assert.Contains("value.title", requestBody, StringComparison.Ordinal);
        }
        finally { DeleteTempDataPath(dataPath); }
    }
    private static async Task<DatabaseEngine> CreateDatabaseAsync(string dataPath)
    {
        var options = new LsmStoreOptions(dataPath, FlushThreshold: 2);
        var database = new DatabaseEngine(options, new ChangeLogService(options));
        await database.InitializeAsync();
        return database;
    }

    private static string CreateTempDataPath() => Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));
    private static void DeleteTempDataPath(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}