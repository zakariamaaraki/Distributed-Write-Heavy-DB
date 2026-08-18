using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Tests;

public sealed class ChangeLogServiceTests
{
    [Fact]
    public async Task PublishAsync_PersistsEntriesAndReadAfterAsyncReplaysFromSequence()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var service = CreateService(dataPath);
            await service.PublishAsync([
                Entry(1, "put", "alpha", "one"),
                Entry(2, "put", "bravo", "two"),
                Entry(3, "delete", "alpha", null, isDeleted: true)
            ]);

            var replay = await service.ReadAfterAsync(fromSequence: 1);

            Assert.Equal([2, 3], replay.Select(entry => entry.Sequence));
            Assert.Equal(["bravo", "alpha"], replay.Select(entry => entry.Key));
            Assert.True(replay[1].IsDeleted);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task StreamAsync_ReplaysExistingEntriesAndStreamsLiveEntries()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var service = CreateService(dataPath);
            await service.PublishAsync([Entry(1, "put", "alpha", "one")]);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = new List<ChangeLogEntry>();
            var twoEvents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var streamTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var entry in service.StreamAsync(0, cancellation.Token))
                    {
                        received.Add(entry);
                        if (received.Count == 2)
                        {
                            twoEvents.TrySetResult();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, cancellation.Token);

            await service.PublishAsync([Entry(2, "put", "bravo", "two")], cancellation.Token);
            await twoEvents.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await streamTask;

            Assert.Equal([1, 2], received.Select(entry => entry.Sequence));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task LsmStore_CommitsDirectAndBatchWritesToChangeLog()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var options = new LsmStoreOptions(dataPath, FlushThreshold: 100);
            var changeLog = new ChangeLogService(options);
            var store = new LsmStore(options, changeLog);
            await store.InitializeAsync();

            await store.PutAsync("alpha", "one");
            await store.ApplyBatchAsync([
                StoreWriteOperation.Put("bravo", "two"),
                StoreWriteOperation.Delete("alpha")
            ]);

            var entries = await changeLog.ReadAfterAsync(0);

            Assert.Equal([1, 2, 3], entries.Select(entry => entry.Sequence));
            Assert.Equal(["put", "put", "delete"], entries.Select(entry => entry.Operation));
            Assert.Equal(["alpha", "bravo", "alpha"], entries.Select(entry => entry.Key));
            Assert.True(entries[2].IsDeleted);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task InitializeAsync_BackfillsChangeLogFromUnflushedWalWithoutDuplicates()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var options = new LsmStoreOptions(dataPath, FlushThreshold: 100);
            var firstChangeLog = new ChangeLogService(options);
            var firstStore = new LsmStore(options, firstChangeLog);
            await firstStore.InitializeAsync();
            await firstStore.PutAsync("alpha", "one");

            File.Delete(Path.Combine(dataPath, "changelog.log"));

            var restoredChangeLog = new ChangeLogService(options);
            var restoredStore = new LsmStore(options, restoredChangeLog);
            await restoredStore.InitializeAsync();
            await restoredStore.InitializeAsync();

            var entries = await restoredChangeLog.ReadAfterAsync(0);

            Assert.Single(entries);
            Assert.Equal(1, entries[0].Sequence);
            Assert.Equal("alpha", entries[0].Key);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task StreamAsync_ReconnectsFromLastAppliedSequence()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var service = CreateService(dataPath);
            await service.PublishAsync([
                Entry(1, "put", "alpha", "one"),
                Entry(2, "put", "bravo", "two"),
                Entry(3, "put", "charlie", "three")
            ]);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var replay = new List<ChangeLogEntry>();
            await foreach (var entry in service.StreamAsync(1, cancellation.Token))
            {
                replay.Add(entry);
                if (replay.Count == 2)
                {
                    break;
                }
            }

            Assert.Equal([2, 3], replay.Select(entry => entry.Sequence));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }
    private static ChangeLogService CreateService(string dataPath)
    {
        return new ChangeLogService(new LsmStoreOptions(dataPath, FlushThreshold: 100));
    }

    private static ChangeLogEntry Entry(
        long sequence,
        string operation,
        string key,
        string? value,
        bool isDeleted = false)
    {
        return new ChangeLogEntry(sequence, operation, key, value, isDeleted, DateTimeOffset.UtcNow);
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
