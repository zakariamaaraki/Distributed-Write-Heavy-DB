using System.Text.RegularExpressions;

namespace Router;

public enum ReadConsistencyLevel
{
    Eventual,
    Monotonic,
    ConsistentPrefix,
    BoundedStaleness,
    Strong
}

public static class ReadConsistencyPolicy
{
    public const string HeaderName = "X-Read-Consistency";
    public const string AfterSequenceHeader = "X-Read-After-Sequence";
    public const string ReadSequenceHeader = "X-Read-Sequence";
    public const string MaxSequenceLagHeader = "X-Max-Sequence-Lag";

    public static bool TryParse(string? value, out ReadConsistencyLevel level)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "eventual", StringComparison.OrdinalIgnoreCase))
        { level = ReadConsistencyLevel.Eventual; return true; }
        if (string.Equals(value, "monotonic", StringComparison.OrdinalIgnoreCase))
        { level = ReadConsistencyLevel.Monotonic; return true; }
        if (string.Equals(value, "consistent-prefix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "consistent_prefix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "session", StringComparison.OrdinalIgnoreCase))
        { level = ReadConsistencyLevel.ConsistentPrefix; return true; }
        if (string.Equals(value, "bounded-staleness", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "bounded_staleness", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "bounded", StringComparison.OrdinalIgnoreCase))
        { level = ReadConsistencyLevel.BoundedStaleness; return true; }
        if (string.Equals(value, "strong", StringComparison.OrdinalIgnoreCase))
        { level = ReadConsistencyLevel.Strong; return true; }
        level = default;
        return false;
    }

    public static string? ClassifySqlStatement(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var value = Regex.Match(query.Trim(), "^(SELECT|SHOW\\s+TABLES|SEARCH|BEGIN|COMMIT|ROLLBACK)\\b", RegexOptions.IgnoreCase).Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.ToUpperInvariant();
    }

    public static bool ShouldRouteToLeader(bool isRead, bool isTransactionalRead, ReadConsistencyLevel level)
        => isRead && (isTransactionalRead || level == ReadConsistencyLevel.Strong);

    public static bool RequiresSessionSequence(ReadConsistencyLevel level)
        => level is ReadConsistencyLevel.Monotonic or ReadConsistencyLevel.ConsistentPrefix;

    public static long MinimumEligibleSequence(long leaderSequence, long maxSequenceLag)
        => Math.Max(0, leaderSequence - maxSequenceLag);
}
