using System.Net.Sockets;
using System.Text;
using LsmWriteDb.Raft;
using LsmWriteDb.Sql;
using LsmWriteDb.Storage;
using Microsoft.AspNetCore.Http;

namespace LsmWriteDb.TcpSql;

internal sealed class TcpSqlSession
{
    private readonly TcpClient _client;
    private readonly SqlEngine _sql;
    private readonly TcpSqlOptions _options;
    private readonly ILogger<TcpSqlSession> _logger;
    private Guid? _transactionId;

    public TcpSqlSession(
        TcpClient client,
        SqlEngine sql,
        TcpSqlOptions options,
        ILogger<TcpSqlSession> logger)
    {
        _client = client;
        _sql = sql;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var stream = _client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);

        await TcpSqlProtocol.WriteLineAsync(
            stream,
            "LsmWriteDb TCP SQL ready. End statements with ';'. Use QUIT; to disconnect.",
            cancellationToken);
        await TcpSqlProtocol.WriteAsync(stream, TcpSqlProtocol.Prompt, cancellationToken);

        var buffer = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            if (buffer.Length + line.Length > _options.MaxQueryBytes)
            {
                buffer.Clear();
                await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatError("SQL statement is too large."), cancellationToken);
                await TcpSqlProtocol.WriteAsync(stream, TcpSqlProtocol.Prompt, cancellationToken);
                continue;
            }

            buffer.AppendLine(line);
            var text = buffer.ToString().Trim();
            if (text.Length == 0)
            {
                await TcpSqlProtocol.WriteAsync(stream, TcpSqlProtocol.Prompt, cancellationToken);
                continue;
            }

            if (!text.EndsWith(';'))
            {
                await TcpSqlProtocol.WriteAsync(stream, "...> ", cancellationToken);
                continue;
            }

            buffer.Clear();
            if (TcpSqlProtocol.IsExitCommand(text))
            {
                await TcpSqlProtocol.WriteLineAsync(stream, "BYE", cancellationToken);
                return;
            }

            await ExecuteAsync(stream, text, cancellationToken);
            await TcpSqlProtocol.WriteAsync(stream, TcpSqlProtocol.Prompt, cancellationToken);
        }
    }

    private async Task ExecuteAsync(
        NetworkStream stream,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _sql.ExecuteAsync(new SqlQueryRequest(query, _transactionId));
            UpdateTransaction(result);
            await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatResult(result), cancellationToken);
        }
        catch (SqlParseException ex)
        {
            await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatError(ex.Message), cancellationToken);
        }
        catch (SqlExecutionException ex)
        {
            await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatError(ex.Message), cancellationToken);
        }
        catch (TableNotFoundException ex)
        {
            await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatError(ex.Message), cancellationToken);
        }
        catch (RaftWriteRejectedException ex)
        {
            await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatError(ex.Message), cancellationToken);
        }
        catch (ArgumentException ex)
        {
            await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatError(ex.Message), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled TCP SQL session error.");
            await TcpSqlProtocol.WriteLineAsync(stream, TcpSqlProtocol.FormatError("internal server error"), cancellationToken);
        }
    }

    private void UpdateTransaction(SqlExecutionResult result)
    {
        if (string.Equals(result.StatementType, "BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            _transactionId = result.TransactionId;
            return;
        }

        if (string.Equals(result.StatementType, "COMMIT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.StatementType, "ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            _transactionId = null;
        }
    }
}
