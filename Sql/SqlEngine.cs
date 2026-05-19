using LsmWriteDb.Storage;
using LsmWriteDb.Raft;
using LsmWriteDb.Transactions;
using Microsoft.AspNetCore.Http;

namespace LsmWriteDb.Sql;

public sealed class SqlEngine
{
    private readonly LsmStore _store;
    private readonly TransactionManager _transactions;
    private readonly RaftRoleGuard? _roleGuard;

    public SqlEngine(LsmStore store, TransactionManager transactions)
        : this(store, transactions, roleGuard: null)
    {
    }

    public SqlEngine(LsmStore store, TransactionManager transactions, RaftRoleGuard? roleGuard)
    {
        _store = store;
        _transactions = transactions;
        _roleGuard = roleGuard;
    }

    public async Task<SqlExecutionResult> ExecuteAsync(SqlQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new SqlParseException("SQL query is required.");
        }

        var statement = SqlParser.Parse(request.Query);
        if (statement is not SqlSelectStatement)
        {
            _roleGuard?.EnsureLeader();
        }

        return statement switch
        {
            SqlBeginStatement => Begin(),
            SqlCommitStatement => await CommitAsync(request.TransactionId),
            SqlRollbackStatement => Rollback(request.TransactionId),
            SqlInsertStatement insert => await InsertAsync(insert, request.TransactionId),
            SqlSelectStatement select => await SelectAsync(select, request.TransactionId),
            SqlUpdateStatement update => await UpdateAsync(update, request.TransactionId),
            SqlDeleteStatement delete => await DeleteAsync(delete, request.TransactionId),
            _ => throw new SqlExecutionException("Unsupported SQL statement.")
        };
    }

    private SqlExecutionResult Begin()
    {
        var transaction = _transactions.Begin();
        return SqlExecutionResult.Acknowledged(
            "BEGIN",
            rowsAffected: 0,
            transaction.TransactionId,
            "transaction started");
    }

    private async Task<SqlExecutionResult> CommitAsync(Guid? transactionId)
    {
        var id = RequireTransactionId(transactionId, "COMMIT");
        var commit = await _transactions.CommitAsync(id);
        if (commit is null)
        {
            throw TransactionNotFound();
        }

        return SqlExecutionResult.Acknowledged(
            "COMMIT",
            commit.OperationCount,
            commit.TransactionId,
            "transaction committed");
    }

    private SqlExecutionResult Rollback(Guid? transactionId)
    {
        var id = RequireTransactionId(transactionId, "ROLLBACK");
        if (!_transactions.Rollback(id))
        {
            throw TransactionNotFound();
        }

        return SqlExecutionResult.Acknowledged("ROLLBACK", rowsAffected: 0, id, "transaction rolled back");
    }

    private async Task<SqlExecutionResult> InsertAsync(SqlInsertStatement statement, Guid? transactionId)
    {
        if (transactionId is Guid id)
        {
            if (!_transactions.TryStagePut(id, statement.Key, statement.Value, out _))
            {
                throw TransactionNotFound();
            }
        }
        else
        {
            await _store.PutAsync(statement.Key, statement.Value);
        }

        return SqlExecutionResult.Acknowledged("INSERT", rowsAffected: 1, transactionId);
    }

    private async Task<SqlExecutionResult> SelectAsync(SqlSelectStatement statement, Guid? transactionId)
    {
        IReadOnlyList<KeyValueRow> rows;

        if (statement.Key is not null)
        {
            var row = transactionId is Guid id
                ? await GetTransactionRowAsync(id, statement.Key)
                : await _store.GetAsync(statement.Key);

            rows = row is null ? [] : [row];
        }
        else if (transactionId is Guid id)
        {
            var result = await _transactions.RangeAsync(id, statement.Start, statement.End, statement.Limit);
            if (!result.FoundTransaction)
            {
                throw TransactionNotFound();
            }

            rows = result.Rows;
        }
        else
        {
            rows = await _store.RangeAsync(statement.Start, statement.End, statement.Limit);
        }

        var projectedRows = rows
            .Take(Math.Clamp(statement.Limit, 1, 1_000))
            .Select(row => Project(row, statement.Columns))
            .ToList();

        return SqlExecutionResult.WithRows("SELECT", projectedRows, transactionId);
    }

    private async Task<SqlExecutionResult> UpdateAsync(SqlUpdateStatement statement, Guid? transactionId)
    {
        if (transactionId is Guid id)
        {
            if (!_transactions.TryStagePut(id, statement.Key, statement.Value, out _))
            {
                throw TransactionNotFound();
            }
        }
        else
        {
            await _store.PutAsync(statement.Key, statement.Value);
        }

        return SqlExecutionResult.Acknowledged("UPDATE", rowsAffected: 1, transactionId);
    }

    private async Task<SqlExecutionResult> DeleteAsync(SqlDeleteStatement statement, Guid? transactionId)
    {
        if (transactionId is Guid id)
        {
            if (!_transactions.TryStageDelete(id, statement.Key, out _))
            {
                throw TransactionNotFound();
            }
        }
        else
        {
            await _store.DeleteAsync(statement.Key);
        }

        return SqlExecutionResult.Acknowledged("DELETE", rowsAffected: 1, transactionId);
    }

    private async Task<KeyValueRow?> GetTransactionRowAsync(Guid transactionId, string key)
    {
        var result = await _transactions.GetAsync(transactionId, key);
        if (!result.FoundTransaction)
        {
            throw TransactionNotFound();
        }

        return result.Row;
    }

    private static IReadOnlyDictionary<string, string> Project(KeyValueRow row, IReadOnlyList<string> columns)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            values[column] = column switch
            {
                "key" => row.Key,
                "value" => row.Value,
                _ => throw new SqlExecutionException($"Unsupported column {column}.")
            };
        }

        return values;
    }

    private static Guid RequireTransactionId(Guid? transactionId, string statementType)
    {
        if (transactionId is not Guid id)
        {
            throw new SqlExecutionException($"{statementType} requires transactionId in the request body.");
        }

        return id;
    }

    private static SqlExecutionException TransactionNotFound()
    {
        return new SqlExecutionException("transaction not found", StatusCodes.Status404NotFound);
    }
}
