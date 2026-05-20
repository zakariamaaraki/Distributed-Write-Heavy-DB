using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Tests;

public sealed class DatabaseEngineTests
{
    [Fact]
    public async Task InitializeAsync_CreatesDefaultTable()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var database = await CreateDatabaseAsync(dataPath);

            var tables = await database.ListTablesAsync();

            Assert.Equal(["kv"], tables.Select(table => table.Name));
            Assert.True(File.Exists(Path.Combine(dataPath, "catalog.json")));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task CreateTableAsync_IsolatesRowsAndSstablesPerTable()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var database = await CreateDatabaseAsync(dataPath, flushThreshold: 2);
            await database.CreateTableAsync("users");
            await database.CreateTableAsync("orders");

            await database.PutAsync("users", "same-key", "user-value");
            await database.PutAsync("orders", "same-key", "order-value");
            await database.PutAsync("users", "second-key", "second-user");

            var userRow = await database.GetAsync("users", "same-key");
            var orderRow = await database.GetAsync("orders", "same-key");

            Assert.NotNull(userRow);
            Assert.NotNull(orderRow);
            Assert.Equal("user-value", userRow.Value);
            Assert.Equal("order-value", orderRow.Value);

            var userSstables = Directory.GetFiles(
                Path.Combine(dataPath, "tables", "users", "sstables"),
                "sstable-*.json");

            Assert.Contains(userSstables, path => !path.EndsWith(".bloom.json", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(dataPath, "tables", "orders", "sstables")));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ChangeLog_RecordsTableNamesAndGlobalSequences()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var options = new LsmStoreOptions(dataPath, FlushThreshold: 100);
            var changeLog = new ChangeLogService(options);
            var database = new DatabaseEngine(options, changeLog);
            await database.InitializeAsync();
            await database.CreateTableAsync("users");
            await database.CreateTableAsync("orders");

            await database.PutAsync("users", "user:1", "Ada");
            await database.PutAsync("orders", "order:1", "Book");

            var entries = await changeLog.ReadAfterAsync(0);

            Assert.Equal([1, 2], entries.Select(entry => entry.Sequence));
            Assert.Equal(["users", "orders"], entries.Select(entry => entry.Table));
            Assert.Equal(["user:1", "order:1"], entries.Select(entry => entry.Key));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ApplyReplicatedChangeAsync_CreatesMissingTableAndPreservesLeaderSequence()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var options = new LsmStoreOptions(dataPath, FlushThreshold: 100);
            var changeLog = new ChangeLogService(options);
            var database = new DatabaseEngine(options, changeLog);
            await database.InitializeAsync();

            await database.ApplyReplicatedChangeAsync(new ChangeLogEntry(
                25,
                "put",
                "user:1",
                "Ada",
                IsDeleted: false,
                DateTimeOffset.UtcNow)
            {
                Table = "users"
            });

            var row = await database.GetAsync("users", "user:1");
            var stats = await database.GetStatsAsync();
            var entries = await changeLog.ReadAfterAsync(0);

            Assert.NotNull(row);
            Assert.Equal("Ada", row.Value);
            Assert.Equal(25, stats.LastSequence);
            Assert.Equal(["kv", "users"], stats.Tables.Select(table => table.Table));
            Assert.Single(entries);
            Assert.Equal("users", entries[0].Table);
            Assert.Equal(25, entries[0].Sequence);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    private static async Task<DatabaseEngine> CreateDatabaseAsync(string dataPath, int flushThreshold = 100)
    {
        var options = new LsmStoreOptions(dataPath, flushThreshold);
        var database = new DatabaseEngine(options, new ChangeLogService(options));
        await database.InitializeAsync();
        return database;
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
