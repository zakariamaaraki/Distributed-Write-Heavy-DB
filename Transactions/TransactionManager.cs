using System.Collections.Concurrent;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Transactions;

public sealed record TransactionInfo(
    Guid TransactionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int OperationCount);

public sealed record TransactionCommit(Guid TransactionId, int OperationCount);

public sealed record TransactionValueRead(bool FoundTransaction, KeyValueRow? Row);

public sealed record TransactionRangeRead(bool FoundTransaction, IReadOnlyList<KeyValueRow> Rows);

public sealed class TransactionManager
{
    private readonly DatabaseEngine? _database;
    private readonly LsmStore? _singleStore;
    private readonly ConcurrentDictionary<Guid, TransactionBuffer> _transactions = new();

    public TransactionManager(DatabaseEngine database)
    {
        _database = database;
    }

    public TransactionManager(LsmStore store)
    {
        _singleStore = store;
    }

    public TransactionInfo Begin()
    {
        var transaction = TransactionBuffer.Create();
        if (!_transactions.TryAdd(transaction.Id, transaction))
        {
            throw new InvalidOperationException("Could not create transaction.");
        }

        return transaction.ToInfo();
    }

    public bool TryStagePut(Guid transactionId, string key, string value, out TransactionInfo? transaction)
    {
        return TryStagePut(transactionId, TableNames.Default, key, value, out transaction);
    }

    public bool TryStagePut(Guid transactionId, string table, string key, string value, out TransactionInfo? transaction)
    {
        var normalizedTable = TableNames.Normalize(table);
        ValidateKey(key);

        if (!_transactions.TryGetValue(transactionId, out var buffer))
        {
            transaction = null;
            return false;
        }

        return buffer.TryStage(TransactionWrite.Put(normalizedTable, key, value), out transaction);
    }

    public bool TryStageDelete(Guid transactionId, string key, out TransactionInfo? transaction)
    {
        return TryStageDelete(transactionId, TableNames.Default, key, out transaction);
    }

    public bool TryStageDelete(Guid transactionId, string table, string key, out TransactionInfo? transaction)
    {
        var normalizedTable = TableNames.Normalize(table);
        ValidateKey(key);

        if (!_transactions.TryGetValue(transactionId, out var buffer))
        {
            transaction = null;
            return false;
        }

        return buffer.TryStage(TransactionWrite.Delete(normalizedTable, key), out transaction);
    }

    public async Task<TransactionValueRead> GetAsync(Guid transactionId, string key)
    {
        return await GetAsync(transactionId, TableNames.Default, key);
    }

    public async Task<TransactionValueRead> GetAsync(Guid transactionId, string table, string key)
    {
        var normalizedTable = TableNames.Normalize(table);
        ValidateKey(key);

        if (!_transactions.TryGetValue(transactionId, out var buffer))
        {
            return new TransactionValueRead(FoundTransaction: false, Row: null);
        }

        if (!buffer.TryGetStaged(normalizedTable, key, out var staged))
        {
            if (buffer.IsClosed)
            {
                return new TransactionValueRead(FoundTransaction: false, Row: null);
            }

            return new TransactionValueRead(FoundTransaction: true, Row: await ReadCommittedAsync(normalizedTable, key));
        }

        return new TransactionValueRead(FoundTransaction: true, Row: staged.ToRowOrNull());
    }

    public async Task<TransactionRangeRead> RangeAsync(Guid transactionId, string? start, string? end, int limit)
    {
        return await RangeAsync(transactionId, TableNames.Default, start, end, limit);
    }

    public async Task<TransactionRangeRead> RangeAsync(Guid transactionId, string table, string? start, string? end, int limit)
    {
        var normalizedTable = TableNames.Normalize(table);
        if (!_transactions.TryGetValue(transactionId, out var buffer))
        {
            return new TransactionRangeRead(FoundTransaction: false, Rows: []);
        }

        if (!buffer.TrySnapshotWrites(out var stagedWrites))
        {
            return new TransactionRangeRead(FoundTransaction: false, Rows: []);
        }

        var boundedLimit = Math.Clamp(limit, 1, 1_000);
        var rowsByKey = new SortedDictionary<string, KeyValueRow>(StringComparer.Ordinal);

        foreach (var row in await ReadCommittedRangeAsync(normalizedTable, start, end, 1_000))
        {
            rowsByKey[row.Key] = row;
        }

        foreach (var write in stagedWrites.Where(write => string.Equals(write.Table, normalizedTable, StringComparison.Ordinal)))
        {
            if (!IsInsideRange(write.Key, start, end))
            {
                continue;
            }

            if (write.IsDeleted)
            {
                rowsByKey.Remove(write.Key);
                continue;
            }

            rowsByKey[write.Key] = new KeyValueRow(write.Key, write.Value ?? string.Empty);
        }

        var rows = rowsByKey.Values
            .Take(boundedLimit)
            .ToList();

        return new TransactionRangeRead(FoundTransaction: true, rows);
    }

    public async Task<TransactionCommit?> CommitAsync(Guid transactionId)
    {
        if (!_transactions.TryGetValue(transactionId, out var buffer))
        {
            return null;
        }

        if (!buffer.TryCloseAndSnapshot(out var operations))
        {
            return null;
        }

        await ApplyBatchAsync(operations);

        _transactions.TryRemove(transactionId, out _);
        return new TransactionCommit(transactionId, operations.Count);
    }

    public bool Rollback(Guid transactionId)
    {
        if (!_transactions.TryGetValue(transactionId, out var buffer))
        {
            return false;
        }

        if (!buffer.TryClose())
        {
            return false;
        }

        _transactions.TryRemove(transactionId, out _);
        return true;
    }

    private static bool IsInsideRange(string key, string? start, string? end)
    {
        if (start is not null && string.CompareOrdinal(key, start) < 0)
        {
            return false;
        }

        if (end is not null && string.CompareOrdinal(key, end) > 0)
        {
            return false;
        }

        return true;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }
    }

    private async Task<KeyValueRow?> ReadCommittedAsync(string table, string key)
    {
        if (_database is not null)
        {
            return await _database.GetAsync(table, key);
        }

        EnsureDefaultTable(table);
        return await _singleStore!.GetAsync(key);
    }

    private async Task<IReadOnlyList<KeyValueRow>> ReadCommittedRangeAsync(
        string table,
        string? start,
        string? end,
        int limit)
    {
        if (_database is not null)
        {
            return await _database.RangeAsync(table, start, end, limit);
        }

        EnsureDefaultTable(table);
        return await _singleStore!.RangeAsync(start, end, limit);
    }

    private async Task ApplyBatchAsync(IReadOnlyList<StoreWriteOperation> operations)
    {
        if (_database is not null)
        {
            await _database.ApplyBatchAsync(operations);
            return;
        }

        await _singleStore!.ApplyBatchAsync(operations);
    }

    private static void EnsureDefaultTable(string table)
    {
        if (!string.Equals(table, TableNames.Default, StringComparison.Ordinal))
        {
            throw new TableNotFoundException(table);
        }
    }
}

internal sealed class TransactionBuffer
{
    private readonly object _mutex = new();
    private readonly Dictionary<TransactionWriteKey, TransactionWrite> _writes = new();
    private bool _closed;
    private DateTimeOffset _updatedAt;

    private TransactionBuffer(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        CreatedAt = createdAt;
        _updatedAt = createdAt;
    }

    public Guid Id { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool IsClosed
    {
        get
        {
            lock (_mutex)
            {
                return _closed;
            }
        }
    }

    public static TransactionBuffer Create()
    {
        return new TransactionBuffer(Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    public TransactionInfo ToInfo()
    {
        lock (_mutex)
        {
            return ToInfoCore();
        }
    }

    public bool TryStage(TransactionWrite write, out TransactionInfo? transaction)
    {
        lock (_mutex)
        {
            if (_closed)
            {
                transaction = null;
                return false;
            }

            _writes[new TransactionWriteKey(write.Table, write.Key)] = write;
            _updatedAt = DateTimeOffset.UtcNow;
            transaction = ToInfoCore();
            return true;
        }
    }

    public bool TryGetStaged(string table, string key, out TransactionWrite write)
    {
        lock (_mutex)
        {
            if (_closed)
            {
                write = default!;
                return false;
            }

            return _writes.TryGetValue(new TransactionWriteKey(table, key), out write!);
        }
    }

    public bool TrySnapshotWrites(out IReadOnlyList<TransactionWrite> writes)
    {
        lock (_mutex)
        {
            if (_closed)
            {
                writes = [];
                return false;
            }

            writes = _writes.Values.ToList();
            return true;
        }
    }

    public bool TryCloseAndSnapshot(out IReadOnlyList<StoreWriteOperation> operations)
    {
        lock (_mutex)
        {
            if (_closed)
            {
                operations = [];
                return false;
            }

            _closed = true;
            operations = _writes.Values
                .Select(write => write.ToStoreOperation())
                .ToList();
            return true;
        }
    }

    public bool TryClose()
    {
        lock (_mutex)
        {
            if (_closed)
            {
                return false;
            }

            _closed = true;
            return true;
        }
    }

    private TransactionInfo ToInfoCore()
    {
        return new TransactionInfo(Id, CreatedAt, _updatedAt, _writes.Count);
    }
}

internal sealed record TransactionWriteKey(string Table, string Key);

internal sealed record TransactionWrite(string Table, string Key, string? Value, bool IsDeleted)
{
    public static TransactionWrite Put(string table, string key, string value)
    {
        return new TransactionWrite(TableNames.Normalize(table), key, value, IsDeleted: false);
    }

    public static TransactionWrite Delete(string table, string key)
    {
        return new TransactionWrite(TableNames.Normalize(table), key, null, IsDeleted: true);
    }

    public StoreWriteOperation ToStoreOperation()
    {
        return new StoreWriteOperation(Key, Value, IsDeleted)
        {
            Table = Table
        };
    }

    public KeyValueRow? ToRowOrNull()
    {
        return IsDeleted ? null : new KeyValueRow(Key, Value ?? string.Empty);
    }
}
