using LsmWriteDb.Storage;
using LsmWriteDb.Transactions;

namespace LsmWriteDb.Tests;

public sealed class TransactionManagerTests
{
    [Fact]
    public async Task UncommittedWrites_AreNotVisibleOutsideTransactionAndDoNotSurviveRestart()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath);
            var transactions = new TransactionManager(store);
            var transaction = transactions.Begin();

            Assert.True(transactions.TryStagePut(transaction.TransactionId, "alpha", "one", out _));

            Assert.Null(await store.GetAsync("alpha"));

            var restoredStore = await CreateStoreAsync(dataPath);
            Assert.Null(await restoredStore.GetAsync("alpha"));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task CommitAsync_PersistsStagedWritesAndReplaysCommittedBatchFromWal()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath, flushThreshold: 100);
            var transactions = new TransactionManager(store);
            var transaction = transactions.Begin();

            Assert.True(transactions.TryStagePut(transaction.TransactionId, "alpha", "one", out _));
            Assert.True(transactions.TryStagePut(transaction.TransactionId, "bravo", "two", out _));

            var commit = await transactions.CommitAsync(transaction.TransactionId);

            Assert.NotNull(commit);
            Assert.Equal(2, commit.OperationCount);

            var restoredStore = await CreateStoreAsync(dataPath, flushThreshold: 100);

            var alpha = await restoredStore.GetAsync("alpha");
            var bravo = await restoredStore.GetAsync("bravo");

            Assert.NotNull(alpha);
            Assert.NotNull(bravo);
            Assert.Equal("one", alpha.Value);
            Assert.Equal("two", bravo.Value);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task RollbackAsync_DiscardsStagedWrites()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath);
            var transactions = new TransactionManager(store);
            var transaction = transactions.Begin();

            Assert.True(transactions.TryStagePut(transaction.TransactionId, "alpha", "one", out _));
            Assert.True(transactions.Rollback(transaction.TransactionId));

            Assert.Null(await store.GetAsync("alpha"));
            Assert.Null(await transactions.CommitAsync(transaction.TransactionId));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task TransactionReads_OverlayStagedWritesOnCommittedRows()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath);
            await store.PutAsync("alpha", "one");
            await store.PutAsync("bravo", "two");
            await store.PutAsync("charlie", "three");

            var transactions = new TransactionManager(store);
            var transaction = transactions.Begin();

            Assert.True(transactions.TryStagePut(transaction.TransactionId, "alpha", "updated", out _));
            Assert.True(transactions.TryStageDelete(transaction.TransactionId, "bravo", out _));
            Assert.True(transactions.TryStagePut(transaction.TransactionId, "delta", "four", out _));

            var alphaInsideTransaction = await transactions.GetAsync(transaction.TransactionId, "alpha");
            var bravoInsideTransaction = await transactions.GetAsync(transaction.TransactionId, "bravo");
            var alphaOutsideTransaction = await store.GetAsync("alpha");
            var range = await transactions.RangeAsync(transaction.TransactionId, "alpha", "delta", limit: 10);

            Assert.True(alphaInsideTransaction.FoundTransaction);
            Assert.True(bravoInsideTransaction.FoundTransaction);
            Assert.True(range.FoundTransaction);

            Assert.NotNull(alphaInsideTransaction.Row);
            Assert.NotNull(alphaOutsideTransaction);
            Assert.Null(bravoInsideTransaction.Row);
            Assert.Equal("updated", alphaInsideTransaction.Row.Value);
            Assert.Equal("one", alphaOutsideTransaction.Value);
            Assert.Equal(["alpha", "charlie", "delta"], range.Rows.Select(row => row.Key));
            Assert.Equal(["updated", "three", "four"], range.Rows.Select(row => row.Value));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task InitializeAsync_IgnoresPartialCommittedBatchWalLine()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            Directory.CreateDirectory(dataPath);
            await File.WriteAllTextAsync(
                Path.Combine(dataPath, "wal.log"),
                """{"type":"committedBatch","records":[{"sequence":1,"key":"alpha","value":"one","isDeleted":false}""");

            var store = await CreateStoreAsync(dataPath);

            Assert.Null(await store.GetAsync("alpha"));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    private static async Task<LsmStore> CreateStoreAsync(string dataPath, int flushThreshold = 100)
    {
        var store = new LsmStore(new LsmStoreOptions(dataPath, flushThreshold));
        await store.InitializeAsync();
        return store;
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
