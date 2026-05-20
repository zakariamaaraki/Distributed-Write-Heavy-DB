using LsmWriteDb.Storage;

namespace LsmWriteDb.ChangeLogs;

public sealed record ChangeLogEntry(
    long Sequence,
    string Operation,
    string Key,
    string? Value,
    bool IsDeleted,
    DateTimeOffset CommittedAt)
{
    public string Table { get; init; } = TableNames.Default;
}
