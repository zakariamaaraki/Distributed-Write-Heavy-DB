using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Raft;
using LsmWriteDb.Sql;
using LsmWriteDb.Storage;
using LsmWriteDb.Transactions;

namespace LsmWriteDb.Tests;

public sealed class RaftNodeTests
{
    [Fact]
    public async Task InitializeAsync_SingleNodeClusterStartsAsLeader()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var node = CreateNode(dataPath, new RaftOptions
            {
                Enabled = true,
                NodeId = "node-a",
                PublicUrl = "http://node-a"
            });

            await node.InitializeAsync();

            var status = node.GetStatus();
            Assert.Equal(RaftRole.Leader, status.Role);
            Assert.True(node.IsLeader);
            Assert.Equal("node-a", status.LeaderId);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task RequestVoteAsync_GrantsOnlyOneVotePerTerm()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var node = CreateNode(dataPath, ClusterOptions());
            await node.InitializeAsync();

            var firstVote = await node.RequestVoteAsync(new RaftRequestVoteRequest(1, "node-b"));
            var secondVote = await node.RequestVoteAsync(new RaftRequestVoteRequest(1, "node-c"));
            var newerTermVote = await node.RequestVoteAsync(new RaftRequestVoteRequest(2, "node-c"));

            Assert.True(firstVote.VoteGranted);
            Assert.False(secondVote.VoteGranted);
            Assert.True(newerTermVote.VoteGranted);
            Assert.Equal(2, node.GetStatus().CurrentTerm);
            Assert.Equal("node-c", node.GetStatus().VotedFor);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task AppendEntriesAsync_RecordsLeaderAndRejectsStaleTerms()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var node = CreateNode(dataPath, ClusterOptions());
            await node.InitializeAsync();
            await node.RequestVoteAsync(new RaftRequestVoteRequest(3, "node-b"));

            var staleHeartbeat = await node.AppendEntriesAsync(new RaftAppendEntriesRequest(2, "node-c"));
            var currentHeartbeat = await node.AppendEntriesAsync(new RaftAppendEntriesRequest(3, "node-b"));

            Assert.False(staleHeartbeat.Success);
            Assert.True(currentHeartbeat.Success);
            Assert.Equal("node-b", node.GetStatus().LeaderId);
            Assert.Equal("http://node-b", node.GetStatus().LeaderUrl);
            Assert.Equal(RaftRole.Follower, node.GetStatus().Role);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task SqlEngine_RejectsWritesOnFollowerAndAllowsReads()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var options = new LsmStoreOptions(dataPath, FlushThreshold: 100);
            var changeLog = new ChangeLogService(options);
            var store = new LsmStore(options, changeLog);
            await store.InitializeAsync();

            var raftNode = CreateNode(dataPath, ClusterOptions());
            await raftNode.InitializeAsync();

            var engine = new SqlEngine(
                store,
                new TransactionManager(store),
                new RaftRoleGuard(raftNode));

            await Assert.ThrowsAsync<RaftWriteRejectedException>(() => engine.ExecuteAsync(
                new SqlQueryRequest("INSERT INTO kv VALUES ('alpha', 'one')", TransactionId: null)));

            var read = await engine.ExecuteAsync(new SqlQueryRequest(
                "SELECT * FROM kv WHERE key = 'alpha'",
                TransactionId: null));

            Assert.Empty(read.Rows);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task ApplyReplicatedChangeAsync_UsesLeaderSequenceAndSkipsDuplicates()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var options = new LsmStoreOptions(dataPath, FlushThreshold: 100);
            var changeLog = new ChangeLogService(options);
            var store = new LsmStore(options, changeLog);
            await store.InitializeAsync();

            await store.ApplyReplicatedChangeAsync(Entry(10, "put", "alpha", "one"));
            await store.ApplyReplicatedChangeAsync(Entry(10, "put", "alpha", "duplicate"));
            await store.ApplyReplicatedChangeAsync(Entry(11, "delete", "alpha", null, isDeleted: true));

            var stats = await store.GetStatsAsync();
            var entries = await changeLog.ReadAfterAsync(0);

            Assert.Equal(11L, stats.LastSequence);
            Assert.Null(await store.GetAsync("alpha"));
            Assert.Equal([10, 11], entries.Select(entry => entry.Sequence));
            Assert.Equal(["put", "delete"], entries.Select(entry => entry.Operation));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    private static RaftNode CreateNode(string dataPath, RaftOptions raftOptions)
    {
        return new RaftNode(
            raftOptions,
            new RaftStateStore(new LsmStoreOptions(dataPath, FlushThreshold: 100)),
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
    }

    private static RaftOptions ClusterOptions()
    {
        return new RaftOptions
        {
            Enabled = true,
            NodeId = "node-a",
            PublicUrl = "http://node-a",
            Peers =
            [
                new RaftPeerOptions { NodeId = "node-b", Url = "http://node-b" },
                new RaftPeerOptions { NodeId = "node-c", Url = "http://node-c" }
            ]
        };
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
