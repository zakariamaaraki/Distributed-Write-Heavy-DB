namespace Router;

public enum ReadConsistencyLevel
{
    Eventual,
    Strong
}

public static class ReadConsistencyPolicy
{
    public const string HeaderName = "X-Read-Consistency";

    public static bool TryParse(string? value, out ReadConsistencyLevel level)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "eventual", StringComparison.OrdinalIgnoreCase))
        {
            level = ReadConsistencyLevel.Eventual;
            return true;
        }

        if (string.Equals(value, "strong", StringComparison.OrdinalIgnoreCase))
        {
            level = ReadConsistencyLevel.Strong;
            return true;
        }

        level = default;
        return false;
    }

    public static bool ShouldRouteToLeader(
        bool isRead,
        bool isTransactionalRead,
        ReadConsistencyLevel level)
        => isRead && (isTransactionalRead || level == ReadConsistencyLevel.Strong);
}
