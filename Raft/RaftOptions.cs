namespace LsmWriteDb.Raft;

public sealed class RaftOptions
{
    public bool Enabled { get; init; }

    public string NodeId { get; init; } = Environment.MachineName;

    public string? PublicUrl { get; init; }

    public int ElectionTimeoutMinMilliseconds { get; init; } = 1_500;

    public int ElectionTimeoutMaxMilliseconds { get; init; } = 3_000;

    public int HeartbeatIntervalMilliseconds { get; init; } = 500;

    public int ReplicationReconnectDelayMilliseconds { get; init; } = 1_000;

    public int LeaderElectionReadyTimeoutMilliseconds { get; init; } = 15_000;

    public IReadOnlyList<RaftPeerOptions> Peers { get; init; } = [];

    public int ClusterSize => Peers.Count + 1;

    public int Majority => (ClusterSize / 2) + 1;

    public bool IsSingleNode => ClusterSize == 1;
}

public sealed class RaftPeerOptions
{
    public string NodeId { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;
}
