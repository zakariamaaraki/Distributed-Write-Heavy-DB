namespace LsmWriteDb.Raft;

public sealed class RaftElectionService : BackgroundService
{
    private readonly RaftNode _node;

    public RaftElectionService(RaftNode node)
    {
        _node = node;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _node.RunElectionLoopAsync(stoppingToken);
    }
}
