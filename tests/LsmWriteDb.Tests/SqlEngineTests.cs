using LsmWriteDb.Sql;
using LsmWriteDb.Storage;
using LsmWriteDb.Transactions;

namespace LsmWriteDb.Tests;

public sealed class SqlEngineTests
{
    [Fact]
    public async Task ExecuteAsync_InsertsSelectsUpdatesAndDeletesKvRows()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);

            var insert = await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO kv (key, value) VALUES ('alpha', 'one')",
                TransactionId: null));
            var firstSelect = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key, value FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            await engine.ExecuteAsync(new SqlQueryRequest(
                "UPDATE kv SET value = 'updated' WHERE key = 'alpha'",
                TransactionId: null));
            var secondSelect = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            await engine.ExecuteAsync(new SqlQueryRequest(
                "DELETE FROM kv WHERE key = 'alpha'",
                TransactionId: null));
            var afterDelete = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            Assert.Equal("INSERT", insert.StatementType);
            Assert.Equal(1, insert.RowsAffected);
            Assert.Single(firstSelect.Rows);
            Assert.Equal("alpha", firstSelect.Rows[0]["key"]);
            Assert.Equal("one", firstSelect.Rows[0]["value"]);
            Assert.Single(secondSelect.Rows);
            Assert.False(secondSelect.Rows[0].ContainsKey("key"));
            Assert.Equal("updated", secondSelect.Rows[0]["value"]);
            Assert.Empty(afterDelete.Rows);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SelectsRangesWithBetweenAndLimit()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);

            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('alpha', 'one')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('bravo', 'two')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('charlie', 'three')", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key FROM kv WHERE key BETWEEN 'alpha' AND 'charlie' LIMIT 2",
                TransactionId: null));

            Assert.Equal("SELECT", result.StatementType);
            Assert.Equal(2, result.RowsAffected);
            Assert.Equal(["alpha", "bravo"], result.Rows.Select(row => row["key"]));
            Assert.All(result.Rows, row => Assert.False(row.ContainsKey("value")));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesTransactionIdToStageAndCommitSqlWrites()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath, flushThreshold: 100);

            var begin = await engine.ExecuteAsync(new SqlQueryRequest("BEGIN", TransactionId: null));
            Assert.NotNull(begin.TransactionId);

            var transactionId = begin.TransactionId.Value;
            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO kv (key, value) VALUES ('alpha', 'one')",
                transactionId));

            var outsideTransaction = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                TransactionId: null));
            var insideTransaction = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                transactionId));

            Assert.Empty(outsideTransaction.Rows);
            Assert.Single(insideTransaction.Rows);
            Assert.Equal("one", insideTransaction.Rows[0]["value"]);

            var commit = await engine.ExecuteAsync(new SqlQueryRequest("COMMIT", transactionId));
            var afterCommit = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            Assert.Equal("COMMIT", commit.StatementType);
            Assert.Equal(1, commit.RowsAffected);
            Assert.Single(afterCommit.Rows);
            Assert.Equal("one", afterCommit.Rows[0]["value"]);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RollbackDiscardsSqlTransactionWrites()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);
            var begin = await engine.ExecuteAsync(new SqlQueryRequest("BEGIN TRANSACTION", TransactionId: null));
            var transactionId = begin.TransactionId!.Value;

            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO kv VALUES ('alpha', 'one')",
                transactionId));
            var rollback = await engine.ExecuteAsync(new SqlQueryRequest("ROLLBACK", transactionId));
            var afterRollback = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            Assert.Equal("ROLLBACK", rollback.StatementType);
            Assert.Empty(afterRollback.Rows);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HandlesEscapedStringLiterals()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);

            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO kv VALUES ('quote', 'it''s stored')",
                TransactionId: null));
            var result = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM kv WHERE key = 'quote'",
                TransactionId: null));

            Assert.Single(result.Rows);
            Assert.Equal("it's stored", result.Rows[0]["value"]);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    private static async Task<SqlEngine> CreateEngineAsync(string dataPath, int flushThreshold = 100)
    {
        var store = new LsmStore(new LsmStoreOptions(dataPath, flushThreshold));
        await store.InitializeAsync();
        var transactions = new TransactionManager(store);
        return new SqlEngine(store, transactions);
    }

    private static string CreateTempDataPath()
    {
        return Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempDataPath(string dataPath)
    {
        if (Directory.Exists(dataPath))
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
