namespace LsmWriteDb.Transactions;

public enum IsolationLevel
{
    ReadCommitted,
    RepeatableRead,
    Serializable
}

public static class IsolationLevelParser
{
    public static IsolationLevel Parse(string text) => text.Trim().ToUpperInvariant() switch
    {
        "READ COMMITTED" => IsolationLevel.ReadCommitted,
        "REPEATABLE READ" => IsolationLevel.RepeatableRead,
        "SERIALIZABLE" => IsolationLevel.Serializable,
        _ => throw new ArgumentException($"Unsupported isolation level: {text}.")
    };
}