using System.Net;
using System.Net.Http.Json;
using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Raft;
using LsmWriteDb.Storage;
using LsmWriteDb.Transactions;
using Microsoft.Extensions.Logging.Abstractions;

namespace LsmWriteDb.Tests;

public sealed class DistributedTransactionTests
{
    [Fact]
    public async Task ParticipantPrepareDoesNotPublishUntilCommit()
    {
        var path = Path.Combine(Path.GetTempPath(), "lsm-2pc-" + Guid.NewGuid());
        try
        {
            var options = new LsmStoreOptions(path, FlushThreshold: 100);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            await database.CreateTableAsync("users");
            var manager = new DistributedTransactionManager(database, new RaftOptions(), new HttpClient(), options, NullLogger<DistributedTransactionManager>.Instance);
            var id = Guid.NewGuid();

            Assert.True(await manager.PrepareParticipantAsync(new DistributedPrepareRequest(id,
                [new DistributedWrite("users", "u1", "Alice", false)])));
            Assert.Null(await database.GetAsync("users", "u1"));
            Assert.True(await manager.CommitParticipantAsync(id));
            Assert.Equal("Alice", (await database.GetAsync("users", "u1"))?.Value);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AbortParticipantDiscardsPreparedWrites()
    {
        var path = Path.Combine(Path.GetTempPath(), "lsm-2pc-" + Guid.NewGuid());
        try
        {
            var options = new LsmStoreOptions(path, FlushThreshold: 100);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            var manager = new DistributedTransactionManager(database, new RaftOptions(), new HttpClient(), options, NullLogger<DistributedTransactionManager>.Instance);
            var id = Guid.NewGuid();

            Assert.True(await manager.PrepareParticipantAsync(new DistributedPrepareRequest(id,
                [new DistributedWrite("kv", "u1", "Alice", false)])));
            Assert.True(manager.AbortParticipant(id));
            Assert.False(await manager.CommitParticipantAsync(id));
            Assert.Null(await database.GetAsync("kv", "u1"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
    [Fact]
    public async Task PreparedParticipantStateIsReloadedFromJournal()
    {
        var path = Path.Combine(Path.GetTempPath(), "lsm-2pc-" + Guid.NewGuid());
        try
        {
            var options = new LsmStoreOptions(path, FlushThreshold: 100);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            var id = Guid.NewGuid();
            var first = new DistributedTransactionManager(database, new RaftOptions(), new HttpClient(), options, NullLogger<DistributedTransactionManager>.Instance);
            Assert.True(await first.PrepareParticipantAsync(new DistributedPrepareRequest(id, [new DistributedWrite("kv", "journaled", "value", false)])));
            var restored = new DistributedTransactionManager(database, new RaftOptions(), new HttpClient(), options, NullLogger<DistributedTransactionManager>.Instance);
            Assert.Equal("prepared", restored.Status(id)?.Status);
            Assert.True(await restored.CommitParticipantAsync(id));
            Assert.Equal("value", (await database.GetAsync("kv", "journaled"))?.Value);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }    [Fact]
    public async Task CommitParticipantIsIdempotentAfterTheFirstCommit()
    {
        var path = Path.Combine(Path.GetTempPath(), "lsm-2pc-" + Guid.NewGuid());
        try
        {
            var options = new LsmStoreOptions(path, FlushThreshold: 100);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            var manager = new DistributedTransactionManager(database, new RaftOptions(), new HttpClient(), options, NullLogger<DistributedTransactionManager>.Instance);
            var id = Guid.NewGuid();
            await manager.PrepareParticipantAsync(new DistributedPrepareRequest(id, [new DistributedWrite("kv", "once", "value", false)]));
            Assert.True(await manager.CommitParticipantAsync(id));
            Assert.True(await manager.CommitParticipantAsync(id));
            Assert.Equal("committed", manager.Status(id)?.Status);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task MetricsExposePhaseCounters()
    {
        var path = Path.Combine(Path.GetTempPath(), "lsm-2pc-" + Guid.NewGuid());
        try
        {
            var options = new LsmStoreOptions(path, FlushThreshold: 100);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            var manager = new DistributedTransactionManager(database, new RaftOptions(), new HttpClient(), options, NullLogger<DistributedTransactionManager>.Instance);
            var id = Guid.NewGuid();
            await manager.PrepareParticipantAsync(new DistributedPrepareRequest(id, [new DistributedWrite("kv", "metric", "value", false)]));
            manager.AbortParticipant(id);
            Assert.NotNull(manager.Metrics());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task CollectTransactionOperationsReadsTheSharedIdFromPeerNodes()
    {
        var path = Path.Combine(Path.GetTempPath(), "lsm-2pc-collect-" + Guid.NewGuid());
        try
        {
            var options = new LsmStoreOptions(path, FlushThreshold: 100);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            var id = Guid.NewGuid();
            var handler = new StubHttpMessageHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith($"/transactions/{id}/operations", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new[]
                        {
                            new StoreWriteOperation("account-1", "{\"balance\":10}", false) { Table = "accounts" }
                        })
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            using var http = new HttpClient(handler);
            var raft = new RaftOptions
            {
                PublicUrl = "http://local",
                Peers = [new RaftPeerOptions { NodeId = "peer", Url = "http://peer" }]
            };
            var manager = new DistributedTransactionManager(database, raft, http, options, NullLogger<DistributedTransactionManager>.Instance);

            var writes = await manager.CollectTransactionOperationsAsync(id, [
                new DistributedWrite("users", "user-1", "{\"name\":\"Ada\"}", false)
            ], CancellationToken.None);

            Assert.NotNull(writes);
            Assert.Equal(2, writes!.Count);
            Assert.Contains(writes, write => write.Table == "accounts" && write.Key == "account-1");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
