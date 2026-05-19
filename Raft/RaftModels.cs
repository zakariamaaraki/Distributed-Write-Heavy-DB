namespace LsmWriteDb.Raft;

public enum RaftRole
{
    Follower,
    Candidate,
    Leader
}

public sealed record RaftNodeStatus(
    string NodeId,
    bool Enabled,
    RaftRole Role,
    long CurrentTerm,
    string? VotedFor,
    string? LeaderId,
    string? LeaderUrl,
    int ClusterSize,
    int Majority,
    long LastAppliedChangeSequence);

public sealed record RaftRequestVoteRequest(long Term, string CandidateId);

public sealed record RaftRequestVoteResponse(long Term, bool VoteGranted);

public sealed record RaftAppendEntriesRequest(long Term, string LeaderId);

public sealed record RaftAppendEntriesResponse(long Term, bool Success);

internal sealed record RaftPersistentState(long CurrentTerm, string? VotedFor);

internal sealed record RaftReplicationPersistentState(long LastAppliedChangeSequence);
