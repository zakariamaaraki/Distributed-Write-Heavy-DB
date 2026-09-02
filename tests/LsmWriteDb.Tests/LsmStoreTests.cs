using LsmWriteDb.Storage;

namespace LsmWriteDb.Tests;

public sealed class LsmStoreTests
{
    [Fact]
    public async Task PutAsync_StoresValueForPointRead()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath);

            await store.PutAsync("alpha", "one");

            var row = await store.GetAsync("alpha");

            Assert.NotNull(row);
            Assert.Equal("alpha", row.Key);
            Assert.Equal("one", row.Value);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task RangeAsync_ReturnsOrderedRowsInsideBounds()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath);

            await store.PutAsync("delta", "4");
            await store.PutAsync("alpha", "1");
            await store.PutAsync("charlie", "3");
            await store.PutAsync("bravo", "2");

            var rows = await store.RangeAsync("bravo", "delta", limit: 10);

            Assert.Equal(["bravo", "charlie", "delta"], rows.Select(row => row.Key));
            Assert.Equal(["2", "3", "4"], rows.Select(row => row.Value));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task DeleteAsync_HidesValueFromPointAndRangeReads()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath);

            await store.PutAsync("alpha", "one");
            await store.PutAsync("bravo", "two");
            await store.DeleteAsync("alpha");

            var pointRead = await store.GetAsync("alpha");
            var rangeRows = await store.RangeAsync("alpha", "bravo", limit: 10);

            Assert.Null(pointRead);
            Assert.Equal(["bravo"], rangeRows.Select(row => row.Key));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task InitializeAsync_ReplaysUnflushedWal()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var firstStore = await CreateStoreAsync(dataPath, flushThreshold: 100);
            await firstStore.PutAsync("alpha", "one");
            await firstStore.PutAsync("bravo", "two");
            await firstStore.DeleteAsync("alpha");

            var restoredStore = await CreateStoreAsync(dataPath, flushThreshold: 100);

            Assert.Null(await restoredStore.GetAsync("alpha"));

            var bravo = await restoredStore.GetAsync("bravo");
            Assert.NotNull(bravo);
            Assert.Equal("two", bravo.Value);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task FlushAsyncSplitsCompactedRunsAndRestoresAcrossFiles()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var options = new LsmStoreOptions(
                dataPath,
                FlushThreshold: 2,
                BlockSizeBytes: 180,
                MaxSstableFileSizeBytes: 500);
            var store = new LsmStore(options);
            await store.InitializeAsync();

            for (var number = 1; number <= 20; number++)
            {
                await store.PutAsync($"key:{number:000}", new string('x', 80));
            }

            var stats = await store.GetStatsAsync();
            Assert.True(stats.SstableCount > 1);
            Assert.Equal(new string('x', 80), (await store.GetAsync("key:017"))?.Value);

            var restored = new LsmStore(options);
            await restored.InitializeAsync();
            Assert.Equal(new string('x', 80), (await restored.GetAsync("key:017"))?.Value);
            Assert.Equal(20, (await restored.ScanAsync()).Count);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }
    [Fact]
    public async Task FlushAsync_WritesSortedSstablesAndRetainsTieredRuns()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = await CreateStoreAsync(dataPath, flushThreshold: 2);

            await store.PutAsync("alpha", "one");
            await store.PutAsync("bravo", "two");
            await store.PutAsync("alpha", "updated");
            await store.PutAsync("charlie", "three");

            var stats = await store.GetStatsAsync();
            var rows = await store.RangeAsync("alpha", "charlie", limit: 10);

            Assert.Equal(0, stats.MemTableEntries);
            Assert.Equal(2, stats.SstableCount);
            Assert.Equal(["alpha", "bravo", "charlie"], rows.Select(row => row.Key));
            Assert.Equal(["updated", "two", "three"], rows.Select(row => row.Value));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task CompactionPromotesRunsBetweenTiersWithoutCollapsingTheTable()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var store = await CreateStoreAsync(dataPath, flushThreshold: 2);

            for (var number = 1; number <= 20; number++)
                await store.PutAsync($"key:{number:000}", $"value-{number}");

            var sstables = new SstableStore(dataPath);
            var levelZero = sstables.GetDataFilesByTier(0);
            var levelOne = sstables.GetDataFilesByTier(1);

            Assert.Equal(2, levelZero.Count);
            Assert.Equal(2, levelOne.Count);
            Assert.All(levelZero.Concat(levelOne), file => Assert.Contains("-L", Path.GetFileName(file)));
            Assert.Equal(20, (await store.ScanAsync()).Count);
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
