using LsmWriteDb.Storage;
using LsmWriteDb.Transactions;

namespace LsmWriteDb.Tests;

public sealed class IsolationLevelTests
{
    [Fact]
    public async Task RepeatableRead_ReusesFirstPointRead()
    {
        var path = Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LsmStore(new LsmStoreOptions(path, 100));
            await store.InitializeAsync();
            await store.PutAsync("k", "one");
            var manager = new TransactionManager(store);
            var tx = manager.Begin(IsolationLevel.RepeatableRead);
            Assert.Equal(IsolationLevel.RepeatableRead, tx.IsolationLevel);
            Assert.Equal("one", (await manager.GetAsync(tx.TransactionId, "k")).Row!.Value);
            await store.PutAsync("k", "two");
            Assert.Equal("one", (await manager.GetAsync(tx.TransactionId, "k")).Row!.Value);
            Assert.True(manager.Rollback(tx.TransactionId));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public void Serializable_TransactionsUseTheSelectableLevel()
    {
        var manager = new TransactionManager(new LsmStore(new LsmStoreOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), 100)));
        var tx = manager.Begin(IsolationLevel.Serializable);
        Assert.Equal(IsolationLevel.Serializable, tx.IsolationLevel);
        Assert.True(manager.Rollback(tx.TransactionId));
    }
}