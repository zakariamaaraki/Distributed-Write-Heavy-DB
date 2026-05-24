using LsmWriteDb.ChangeLogs;
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
                "INSERT INTO kv (key, value) VALUES ('alpha', '{\"text\":\"one\"}')",
                TransactionId: null));
            var firstSelect = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key, value FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            await engine.ExecuteAsync(new SqlQueryRequest(
                "UPDATE kv SET value = '{\"text\":\"updated\"}' WHERE key = 'alpha'",
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
            Assert.Equal("{\"text\":\"one\"}", firstSelect.Rows[0]["value"]);
            Assert.Single(secondSelect.Rows);
            Assert.False(secondSelect.Rows[0].ContainsKey("key"));
            Assert.Equal("{\"text\":\"updated\"}", secondSelect.Rows[0]["value"]);
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

            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('alpha', '{\"text\":\"one\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('bravo', '{\"text\":\"two\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('charlie', '{\"text\":\"three\"}')", null));

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
                "INSERT INTO kv (key, value) VALUES ('alpha', '{\"text\":\"one\"}')",
                transactionId));

            var outsideTransaction = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                TransactionId: null));
            var insideTransaction = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                transactionId));

            Assert.Empty(outsideTransaction.Rows);
            Assert.Single(insideTransaction.Rows);
            Assert.Equal("{\"text\":\"one\"}", insideTransaction.Rows[0]["value"]);

            var commit = await engine.ExecuteAsync(new SqlQueryRequest("COMMIT", transactionId));
            var afterCommit = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            Assert.Equal("COMMIT", commit.StatementType);
            Assert.Equal(1, commit.RowsAffected);
            Assert.Single(afterCommit.Rows);
            Assert.Equal("{\"text\":\"one\"}", afterCommit.Rows[0]["value"]);
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
                "INSERT INTO kv VALUES ('alpha', '{\"text\":\"one\"}')",
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
                "INSERT INTO kv VALUES ('quote', '{\"text\":\"it''s stored\"}')",
                TransactionId: null));
            var result = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM kv WHERE key = 'quote'",
                TransactionId: null));

            Assert.Single(result.Rows);
            Assert.Equal("{\"text\":\"it's stored\"}", result.Rows[0]["value"]);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesAndQueriesMultipleTables()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);

            var createUsers = await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE orders", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users VALUES ('same', '{\"text\":\"user-value\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO orders VALUES ('same', '{\"text\":\"order-value\"}')", null));

            var users = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM users WHERE key = 'same'",
                TransactionId: null));
            var orders = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM orders WHERE key = 'same'",
                TransactionId: null));
            var defaultTable = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'same'",
                TransactionId: null));

            Assert.Equal("CREATE TABLE", createUsers.StatementType);
            Assert.Equal(1, createUsers.RowsAffected);
            Assert.Equal("{\"text\":\"user-value\"}", users.Rows.Single()["value"]);
            Assert.Equal("{\"text\":\"order-value\"}", orders.Rows.Single()["value"]);
            Assert.Empty(defaultTable.Rows);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TransactionCanStageWritesAcrossTables()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE orders", null));

            var begin = await engine.ExecuteAsync(new SqlQueryRequest("BEGIN", null));
            var transactionId = begin.TransactionId!.Value;

            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users VALUES ('user:1', '{\"name\":\"Ada\"}')", transactionId));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO orders VALUES ('order:1', '{\"item\":\"Book\"}')", transactionId));

            var usersInside = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM users WHERE key = 'user:1'",
                transactionId));
            var usersOutside = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM users WHERE key = 'user:1'",
                TransactionId: null));
            var commit = await engine.ExecuteAsync(new SqlQueryRequest("COMMIT", transactionId));
            var ordersAfterCommit = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT value FROM orders WHERE key = 'order:1'",
                TransactionId: null));

            Assert.Equal("{\"name\":\"Ada\"}", usersInside.Rows.Single()["value"]);
            Assert.Empty(usersOutside.Rows);
            Assert.Equal(2, commit.RowsAffected);
            Assert.Equal("{\"item\":\"Book\"}", ordersAfterCommit.Rows.Single()["value"]);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SelectsRowsByJsonValueProperty()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));

            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('user:1001', '{\"name\":\"Ada\",\"tier\":\"gold\",\"profile\":{\"city\":\"Paris\"}}')",
                null));
            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('user:1002', '{\"name\":\"Grace\",\"tier\":\"silver\",\"profile\":{\"city\":\"Paris\"}}')",
                null));
            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('user:1003', '{\"name\":\"Linus\",\"tier\":\"gold\",\"profile\":{\"city\":\"Helsinki\"}}')",
                null));

            var goldUsers = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key FROM users WHERE value.tier = 'gold' LIMIT 10",
                null));
            var exactAda = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key FROM users WHERE value = '{\"name\":\"Ada\",\"tier\":\"gold\",\"profile\":{\"city\":\"Paris\"}}'",
                null));
            var parisUsersInRange = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key FROM users WHERE key >= 'user:1002' AND key <= 'user:1999' AND value.profile.city = 'Paris'",
                null));

            Assert.Equal(["user:1001", "user:1003"], goldUsers.Rows.Select(row => row["key"]));
            Assert.Equal(["user:1001"], exactAda.Rows.Select(row => row["key"]));
            Assert.Equal(["user:1002"], parisUsersInRange.Rows.Select(row => row["key"]));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsSqlWritesWithInvalidJsonValues()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);

            var insertError = await Assert.ThrowsAsync<SqlExecutionException>(() =>
                engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('alpha', 'one')", null)));

            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO kv VALUES ('alpha', '{\"text\":\"one\"}')", null));

            var updateError = await Assert.ThrowsAsync<SqlExecutionException>(() =>
                engine.ExecuteAsync(new SqlQueryRequest("UPDATE kv SET value = 'updated' WHERE key = 'alpha'", null)));

            Assert.StartsWith("value must be valid JSON", insertError.Message);
            Assert.StartsWith("value must be valid JSON", updateError.Message);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    private static async Task<SqlEngine> CreateEngineAsync(string dataPath, int flushThreshold = 100)
    {
        var options = new LsmStoreOptions(dataPath, flushThreshold);
        var database = new DatabaseEngine(options, new ChangeLogService(options));
        await database.InitializeAsync();
        var transactions = new TransactionManager(database);
        return new SqlEngine(database, transactions);
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
