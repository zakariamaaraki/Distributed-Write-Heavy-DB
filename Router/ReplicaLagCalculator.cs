namespace Router;

public static class ReplicaLagCalculator
{
    public static long? Calculate(long? leaderSequence, long? appliedSequence)
    {
        if (leaderSequence is null || appliedSequence is null)
            return null;

        return Math.Max(0, leaderSequence.Value - appliedSequence.Value);
    }

    public static string Status(long? sequenceLag)
        => sequenceLag is null ? "unknown" : sequenceLag.Value == 0 ? "caught-up" : "behind";
}
