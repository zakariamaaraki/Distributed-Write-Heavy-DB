using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Sql;
using LsmWriteDb.Storage;
using LsmWriteDb.Transactions;

namespace LsmWriteDb.Tests;

public sealed class SqlCompatibilityTests
{
    [Fact]
    public async Task LegacySql_RemainsKeyValueCompatible()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE legacy", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO legacy (key, value) VALUES ('k1', '{\"name\":\"Ada\"}')", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key, value FROM legacy WHERE key = 'k1'", null));

            Assert.Single(result.Rows);
            Assert.Equal("k1", result.Rows[0]["key"]);
            Assert.Equal("{\"name\":\"Ada\"}", result.Rows[0]["value"]);
        }
        finally { DeleteTempDataPath(dataPath); }
    }

    [Fact]
    public async Task AnsiSql_UsesSchemaColumnsWhileKeepingJsonStorage()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest(
                "CREATE TABLE users (id INTEGER PRIMARY KEY, name VARCHAR(255) NOT NULL, active BOOLEAN)", null));
            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users (id, name, active) VALUES (7, 'Grace', FALSE)", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT id, name, active FROM users WHERE id = 7", null));

            Assert.Single(result.Rows);
            Assert.Equal("7", result.Rows[0]["id"]);
            Assert.Equal("Grace", result.Rows[0]["name"]);
            Assert.Equal("False", result.Rows[0]["active"]);

            await Assert.ThrowsAsync<SqlExecutionException>(() => engine.ExecuteAsync(new SqlQueryRequest(
                "UPDATE users SET id = 8 WHERE id = 7", null)));
        }
        finally { DeleteTempDataPath(dataPath); }
    }

    [Fact]
    public async Task View_IsPersistedAsCatalogObjectAndEvaluatesSelect()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users (key, value) VALUES ('u1', '{\"tier\":\"gold\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE VIEW gold_users AS SELECT key, value FROM users WHERE value.tier = 'gold'", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest("SELECT * FROM gold_users", null));
            Assert.Single(result.Rows);
            Assert.Equal("u1", result.Rows[0]["key"]);

            var catalog = await File.ReadAllTextAsync(Path.Combine(dataPath, "catalog.json"));
            Assert.Contains("\"kind\": \"view\"", catalog, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gold_users", catalog, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteTempDataPath(dataPath); }
    }

    [Fact]
    public async Task View_IsReadOnlyAndCanReadRelationalDefinition()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users (id INTEGER PRIMARY KEY, name VARCHAR(255) NOT NULL)", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users (id, name) VALUES (7, 'Ada')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE VIEW named_users AS SELECT id, name FROM users", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest("SELECT * FROM named_users", null));
            Assert.Single(result.Rows);
            Assert.Equal("7", result.Rows[0]["id"]);
            Assert.Equal("Ada", result.Rows[0]["name"]);

            var error = await Assert.ThrowsAsync<SqlExecutionException>(() => engine.ExecuteAsync(
                new SqlQueryRequest("DELETE FROM named_users WHERE key = '7'", null)));
            Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteTempDataPath(dataPath); }
    }
    private static async Task<SqlEngine> CreateEngineAsync(string dataPath)
    {
        var options = new LsmStoreOptions(dataPath, 100);
        var database = new DatabaseEngine(options, new ChangeLogService(options));
        await database.InitializeAsync();
        return new SqlEngine(database, new TransactionManager(database));
    }

    private static string CreateTempDataPath()
        => Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempDataPath(string dataPath)
    {
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}