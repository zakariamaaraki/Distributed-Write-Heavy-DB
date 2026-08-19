namespace LsmWriteDb.Raft;

public sealed class TableRaftRoleGuard
{
    private readonly TableRaftCoordinator _coordinator;

    public TableRaftRoleGuard(TableRaftCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public bool CanAcceptWrites(string table) => _coordinator.IsLeader(table);

    public void EnsureLeader(string table)
    {
        if (!_coordinator.IsLeader(table))
        {
            var status = _coordinator.GetStatus(table);
            throw new TableWriteRejectedException(table, status.LeaderId, status.LeaderUrl);
        }
    }

    public IResult WriteRejectedResult(string table)
    {
        var status = _coordinator.GetStatus(table);
        return Results.Conflict(new
        {
            error = "writes are accepted only by the table leader",
            table,
            leaderId = status.LeaderId,
            leaderUrl = status.LeaderUrl,
            term = status.CurrentTerm
        });
    }
}
