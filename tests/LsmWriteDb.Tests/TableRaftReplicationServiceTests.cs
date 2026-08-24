using System.Net;
using System.Net.Http.Json;
using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Raft;
using LsmWriteDb.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace LsmWriteDb.Tests;

public sealed class TableRaftReplicationServiceTests
{
    [Fact]
    public async Task StreamEnding_ReconnectsAndKeepsFollowerWorkerAlive()
    {
        var path = CreateTempDataPath();
        try
        {
            var (database, coordinator) = await CreateFollowerAsync(path);
            var streamRequests = 0;
            var handler = new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/tables")
                    return JsonResponse(new[] { new { name = "users" } });
                if (request.RequestUri.AbsolutePath == "/tables/users/snapshot")
                    return JsonResponse(new TableSnapshot("users", 7, [new KeyValueRow("alpha", "one")]));
                if (request.RequestUri.AbsolutePath == "/changes/stream")
                {
                    Interlocked.Increment(ref streamRequests);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new TableRaftReplicationService(database, coordinator, http, NullLogger<TableRaftReplicationService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await EventuallyAsync(() => Task.FromResult(streamRequests >= 2));
            await service.StopAsync(CancellationToken.None);

            Assert.Equal("one", (await database.GetAsync("users", "alpha"))!.Value);
            Assert.True(streamRequests >= 2);
        }
        finally { DeleteTempDataPath(path); }
    }

    [Fact]
    public async Task TransientReplicationFailure_RetriesSnapshotAndAppliesRows()
    {
        var path = CreateTempDataPath();
        try
        {
            var (database, coordinator) = await CreateFollowerAsync(path);
            var snapshotRequests = 0;
            var handler = new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/tables")
                    return JsonResponse(new[] { new { name = "users" } });
                if (request.RequestUri.AbsolutePath == "/tables/users/snapshot")
                {
                    if (Interlocked.Increment(ref snapshotRequests) == 1)
                        throw new HttpRequestException("temporary disconnect");
                    return JsonResponse(new TableSnapshot("users", 3, [new KeyValueRow("beta", "two")]));
                }
                if (request.RequestUri.AbsolutePath == "/changes/stream")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new TableRaftReplicationService(database, coordinator, http, NullLogger<TableRaftReplicationService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await EventuallyAsync(async () => (await database.GetAsync("users", "beta")) is not null);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal("two", (await database.GetAsync("users", "beta"))!.Value);
            Assert.True(snapshotRequests >= 2);
        }
        finally { DeleteTempDataPath(path); }
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static async Task<(DatabaseEngine Database, TableRaftCoordinator Coordinator)> CreateFollowerAsync(string path)
    {
        var options = new LsmStoreOptions(path, FlushThreshold: 100);
        var database = new DatabaseEngine(options, new ChangeLogService(options));
        await database.InitializeAsync();
        await database.CreateTableAsync("users");

        var raftOptions = new RaftOptions
        {
            Enabled = true,
            NodeId = "node-b",
            PublicUrl = "http://node-b",
            ElectionTimeoutMinMilliseconds = 30_000,
            ElectionTimeoutMaxMilliseconds = 30_000,
            Peers = [new RaftPeerOptions { NodeId = "node-a", Url = "http://node-a" }]
        };
        var coordinator = new TableRaftCoordinator(raftOptions, options, new HttpClient(), database);
        await coordinator.EnsureTableAsync("users");
        await coordinator.AppendEntriesAsync("users", new RaftAppendEntriesRequest(1, "node-a"));
        return (database, coordinator);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Condition was not met before timeout.");
    }

    private static string CreateTempDataPath() => Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempDataPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
