using LsmWriteDb.Storage;
using LsmWriteDb.Raft;
using LsmWriteDb.Transactions;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace LsmWriteDb.Sql;

public sealed class SqlEngine
{
    private readonly DatabaseEngine? _database;
    private readonly LsmStore? _singleStore;
    private readonly TransactionManager _transactions;
    private readonly RaftRoleGuard? _roleGuard;

    public SqlEngine(LsmStore store, TransactionManager transactions)
        : this(store, transactions, roleGuard: null)
    {
    }

    public SqlEngine(LsmStore store, TransactionManager transactions, RaftRoleGuard? roleGuard)
    {
        _singleStore = store;
        _transactions = transactions;
        _roleGuard = roleGuard;
    }

    public SqlEngine(DatabaseEngine database, TransactionManager transactions)
        : this(database, transactions, roleGuard: null)
    {
    }

    public SqlEngine(DatabaseEngine database, TransactionManager transactions, RaftRoleGuard? roleGuard)
    {
        _database = database;
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
            SqlCreateTableStatement create => await CreateTableAsync(create),
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
        EnsureValidJsonValue(statement.Value);

        if (transactionId is Guid id)
        {
            if (!_transactions.TryStagePut(id, statement.Table, statement.Key, statement.Value, out _))
            {
                throw TransactionNotFound();
            }
        }
        else
        {
            await PutAsync(statement.Table, statement.Key, statement.Value);
        }

        return SqlExecutionResult.Acknowledged("INSERT", rowsAffected: 1, transactionId);
    }

    private async Task<SqlExecutionResult> SelectAsync(SqlSelectStatement statement, Guid? transactionId)
    {
        IReadOnlyList<KeyValueRow> rows;
        var where = statement.Where;
        var scanLimit = where.ValuePredicate is null ? statement.Limit : 1_000;

        if (where.Key is not null)
        {
            var row = transactionId is Guid id
                ? await GetTransactionRowAsync(id, statement.Table, where.Key)
                : await GetAsync(statement.Table, where.Key);

            rows = row is null ? [] : [row];
        }
        else if (transactionId is Guid id)
        {
            var result = await _transactions.RangeAsync(id, statement.Table, where.Start, where.End, scanLimit);
            if (!result.FoundTransaction)
            {
                throw TransactionNotFound();
            }

            rows = result.Rows;
        }
        else
        {
            rows = await RangeAsync(statement.Table, where.Start, where.End, scanLimit);
        }

        if (where.ValuePredicate is not null)
        {
            rows = rows
                .Where(row => MatchesValuePredicate(row.Value, where.ValuePredicate))
                .ToList();
        }

        var projectedRows = rows
            .Take(Math.Clamp(statement.Limit, 1, 1_000))
            .Select(row => Project(row, statement.Columns))
            .ToList();

        return SqlExecutionResult.WithRows("SELECT", projectedRows, transactionId);
    }

    private async Task<SqlExecutionResult> UpdateAsync(SqlUpdateStatement statement, Guid? transactionId)
    {
        EnsureValidJsonValue(statement.Value);

        if (transactionId is Guid id)
        {
            if (!_transactions.TryStagePut(id, statement.Table, statement.Key, statement.Value, out _))
            {
                throw TransactionNotFound();
            }
        }
        else
        {
            await PutAsync(statement.Table, statement.Key, statement.Value);
        }

        return SqlExecutionResult.Acknowledged("UPDATE", rowsAffected: 1, transactionId);
    }

    private async Task<SqlExecutionResult> DeleteAsync(SqlDeleteStatement statement, Guid? transactionId)
    {
        if (transactionId is Guid id)
        {
            if (!_transactions.TryStageDelete(id, statement.Table, statement.Key, out _))
            {
                throw TransactionNotFound();
            }
        }
        else
        {
            await DeleteAsync(statement.Table, statement.Key);
        }

        return SqlExecutionResult.Acknowledged("DELETE", rowsAffected: 1, transactionId);
    }

    private async Task<SqlExecutionResult> CreateTableAsync(SqlCreateTableStatement statement)
    {
        if (_database is null)
        {
            EnsureDefaultTable(statement.Table);
            return SqlExecutionResult.Acknowledged("CREATE TABLE", rowsAffected: 0, message: "table already exists");
        }

        var created = await _database.CreateTableAsync(statement.Table);
        return SqlExecutionResult.Acknowledged(
            "CREATE TABLE",
            rowsAffected: created ? 1 : 0,
            message: created ? "table created" : "table already exists");
    }

    private async Task<KeyValueRow?> GetTransactionRowAsync(Guid transactionId, string table, string key)
    {
        var result = await _transactions.GetAsync(transactionId, table, key);
        if (!result.FoundTransaction)
        {
            throw TransactionNotFound();
        }

        return result.Row;
    }

    private async Task PutAsync(string table, string key, string value)
    {
        if (_database is not null)
        {
            await _database.PutAsync(table, key, value);
            return;
        }

        EnsureDefaultTable(table);
        await _singleStore!.PutAsync(key, value);
    }

    private async Task DeleteAsync(string table, string key)
    {
        if (_database is not null)
        {
            await _database.DeleteAsync(table, key);
            return;
        }

        EnsureDefaultTable(table);
        await _singleStore!.DeleteAsync(key);
    }

    private async Task<KeyValueRow?> GetAsync(string table, string key)
    {
        if (_database is not null)
        {
            return await _database.GetAsync(table, key);
        }

        EnsureDefaultTable(table);
        return await _singleStore!.GetAsync(key);
    }

    private async Task<IReadOnlyList<KeyValueRow>> RangeAsync(string table, string? start, string? end, int limit)
    {
        if (_database is not null)
        {
            return await _database.RangeAsync(table, start, end, limit);
        }

        EnsureDefaultTable(table);
        return await _singleStore!.RangeAsync(start, end, limit);
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

    private static void EnsureValidJsonValue(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException ex)
        {
            throw new SqlExecutionException($"value must be valid JSON: {ex.Message}");
        }
    }

    private static bool MatchesValuePredicate(string value, SqlValuePredicate predicate)
    {
        if (predicate.Path.Count == 0)
        {
            return string.Equals(value, predicate.Expected, StringComparison.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var current = document.RootElement;

            foreach (var property in predicate.Path)
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(property, out var next))
                {
                    return false;
                }

                current = next;
            }

            return string.Equals(ToPredicateComparableValue(current), predicate.Expected, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ToPredicateComparableValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
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

    private static void EnsureDefaultTable(string table)
    {
        var normalized = TableNames.Normalize(table);
        if (!string.Equals(normalized, TableNames.Default, StringComparison.Ordinal))
        {
            throw new SqlExecutionException($"table '{normalized}' not found", StatusCodes.Status404NotFound);
        }
    }
}
