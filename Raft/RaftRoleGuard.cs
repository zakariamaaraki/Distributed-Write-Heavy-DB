namespace LsmWriteDb.Raft;

public sealed class RaftRoleGuard
{
    private readonly RaftNode _node;

    public RaftRoleGuard(RaftNode node)
    {
        _node = node;
    }

    public bool CanAcceptWrites => _node.IsLeader;

    public IResult WriteRejectedResult()
    {
        var status = _node.GetStatus();
        return Results.Json(
            new
            {
                error = "writes are accepted only by the Raft leader",
                role = status.Role.ToString(),
                leaderId = status.LeaderId,
                leaderUrl = status.LeaderUrl
            },
            statusCode: StatusCodes.Status409Conflict);
    }

    public void EnsureLeader()
    {
        if (!CanAcceptWrites)
        {
            var status = _node.GetStatus();
            throw new RaftWriteRejectedException(status.Role, status.LeaderId, status.LeaderUrl);
        }
    }
}

public sealed class RaftWriteRejectedException : Exception
{
    public RaftWriteRejectedException(RaftRole role, string? leaderId, string? leaderUrl)
        : base("writes are accepted only by the Raft leader")
    {
        Role = role;
        LeaderId = leaderId;
        LeaderUrl = leaderUrl;
    }

    public RaftRole Role { get; }

    public string? LeaderId { get; }

    public string? LeaderUrl { get; }
}
