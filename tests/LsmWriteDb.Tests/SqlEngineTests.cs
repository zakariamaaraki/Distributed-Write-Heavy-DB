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
    public async Task ExecuteAsync_CreatesIndexForJsonValuePropertySearches()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath, flushThreshold: 2_000);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));

            for (var i = 0; i < 1_005; i++)
            {
                var key = $"user:{i:0000}";
                var tier = i == 1_004 ? "gold" : "silver";
                await engine.ExecuteAsync(new SqlQueryRequest(
                    $"INSERT INTO users VALUES ('{key}', '{{\"tier\":\"{tier}\"}}')",
                    null));
            }

            var createIndex = await engine.ExecuteAsync(new SqlQueryRequest(
                "CREATE INDEX idx_users_tier ON users (value.tier)",
                null));
            var goldUsers = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key FROM users WHERE value.tier = 'gold' LIMIT 10",
                null));

            await engine.ExecuteAsync(new SqlQueryRequest(
                "UPDATE users SET value = '{\"tier\":\"platinum\"}' WHERE key = 'user:1004'",
                null));
            var goldAfterUpdate = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key FROM users WHERE value.tier = 'gold' LIMIT 10",
                null));
            var platinumAfterUpdate = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT key FROM users WHERE value.tier = 'platinum' LIMIT 10",
                null));

            Assert.Equal("CREATE INDEX", createIndex.StatementType);
            Assert.Equal(1, createIndex.RowsAffected);
            Assert.Equal(["user:1004"], goldUsers.Rows.Select(row => row["key"]));
            Assert.Empty(goldAfterUpdate.Rows);
            Assert.Equal(["user:1004"], platinumAfterUpdate.Rows.Select(row => row["key"]));
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

    [Fact]
    public async Task ExecuteAsync_JoinsTablesOnMatchingKeys()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE orders", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users VALUES ('u1', '{\"name\":\"Ada\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users VALUES ('u2', '{\"name\":\"Grace\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO orders VALUES ('u1', '{\"item\":\"Book\"}')", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT users.value, orders.value FROM users JOIN orders ON users.key = orders.key LIMIT 10",
                null));

            Assert.Single(result.Rows);
            Assert.Equal("{\"name\":\"Ada\"}", result.Rows[0]["users.value"]);
            Assert.Equal("{\"item\":\"Book\"}", result.Rows[0]["orders.value"]);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }
    [Fact]
    public async Task ExecuteAsync_JoinsTablesOnJsonProperties()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE orders", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users VALUES ('u1', '{\"customerId\":\"c1\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO orders VALUES ('o1', '{\"customerId\":\"c1\"}')", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO orders VALUES ('o2', '{\"item\":\"Book\"}')", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT users.key, orders.key FROM users JOIN orders ON users.value.customerId = orders.value.customerId LIMIT 10",
                null));

            Assert.Single(result.Rows);
            Assert.Equal("u1", result.Rows[0]["users.key"]);
            Assert.Equal("o1", result.Rows[0]["orders.key"]);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }
    [Fact]
    public async Task ExecuteAsync_ShowTablesReturnsTablesAndLeaderColumns()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users", null));
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE accounts", null));

            var result = await engine.ExecuteAsync(new SqlQueryRequest("SHOW TABLES", null));

            Assert.Equal("SHOW TABLES", result.StatementType);
            Assert.Equal(["accounts", "kv", "users"], result.Rows.Select(row => row["table"]));
            Assert.All(result.Rows, row =>
            {
                Assert.True(row.ContainsKey("leader"));
                Assert.True(row.ContainsKey("leaderUrl"));
            });
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RelationalTableValidatesJsonRowsAndTransactions()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var engine = await CreateEngineAsync(dataPath);
            var create = await engine.ExecuteAsync(new SqlQueryRequest(
                "CREATE RELATIONAL TABLE users (id INT PRIMARY KEY, name TEXT NOT NULL, active BOOLEAN)", null));
            Assert.Equal("CREATE RELATIONAL TABLE", create.StatementType);

            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('1', '{\"name\":\"Ada\",\"active\":true}')", null));
            var row = await engine.ExecuteAsync(new SqlQueryRequest("SELECT key, value FROM users WHERE key = '1'", null));
            Assert.Single(row.Rows);

            await Assert.ThrowsAsync<SqlExecutionException>(() => engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('2', '{\"active\":true}')", null)));
            await Assert.ThrowsAsync<SqlExecutionException>(() => engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('3', '{\"name\":42,\"active\":true}')", null)));
            await Assert.ThrowsAsync<SqlExecutionException>(() => engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('bad', '{\"name\":\"Bob\",\"active\":true}')", null)));

            var begin = await engine.ExecuteAsync(new SqlQueryRequest("BEGIN", null));
            await engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('4', '{\"name\":\"Grace\",\"active\":false}')", begin.TransactionId));
            await Assert.ThrowsAsync<SqlExecutionException>(() => engine.ExecuteAsync(new SqlQueryRequest(
                "INSERT INTO users VALUES ('5', '{\"name\":\"Linus\",\"unknown\":true}')", begin.TransactionId)));
            await engine.ExecuteAsync(new SqlQueryRequest("ROLLBACK", begin.TransactionId));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }
    [Fact]
    public async Task ExecuteAsync_UsesPortableSqlForRelationalTables()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var engine = await CreateEngineAsync(dataPath);
            await engine.ExecuteAsync(new SqlQueryRequest("CREATE TABLE users (id INTEGER PRIMARY KEY, name VARCHAR(255) NOT NULL, active BOOLEAN)", null));
            await engine.ExecuteAsync(new SqlQueryRequest("INSERT INTO users (id, name, active) VALUES (42, 'Ada', TRUE)", null));

            var selected = await engine.ExecuteAsync(new SqlQueryRequest("SELECT id, name, active FROM users WHERE id = 42", null));
            Assert.Single(selected.Rows);
            Assert.Equal("42", selected.Rows[0]["id"]);
            Assert.Equal("Ada", selected.Rows[0]["name"]);
            Assert.Equal("True", selected.Rows[0]["active"]);

            await engine.ExecuteAsync(new SqlQueryRequest("UPDATE users SET name = 'Grace' WHERE id = 42", null));
            var updated = await engine.ExecuteAsync(new SqlQueryRequest("SELECT name FROM users WHERE id = 42", null));
            Assert.Equal("Grace", updated.Rows[0]["name"]);

            await engine.ExecuteAsync(new SqlQueryRequest("DELETE FROM users WHERE id = 42", null));
            var deleted = await engine.ExecuteAsync(new SqlQueryRequest("SELECT * FROM users WHERE id = 42", null));
            Assert.Empty(deleted.Rows);
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
