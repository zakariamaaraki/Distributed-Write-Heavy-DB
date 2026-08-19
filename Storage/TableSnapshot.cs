namespace LsmWriteDb.Storage;

public sealed record TableSnapshot(string Table, long Sequence, IReadOnlyList<KeyValueRow> Rows);
