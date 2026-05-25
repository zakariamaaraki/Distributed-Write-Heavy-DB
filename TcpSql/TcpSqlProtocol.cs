using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LsmWriteDb.Sql;

namespace LsmWriteDb.TcpSql;

internal static class TcpSqlProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string Prompt = "lsm> ";

    public static bool IsExitCommand(string command)
    {
        var normalized = command.Trim().TrimEnd(';');
        return string.Equals(normalized, "quit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "exit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "\\q", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatResult(SqlExecutionResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return $"OK {json}";
    }

    public static string FormatError(string message)
    {
        return $"ERR {JsonSerializer.Serialize(message, JsonOptions)}";
    }

    public static async Task WriteAsync(
        NetworkStream stream,
        string value,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task WriteLineAsync(
        NetworkStream stream,
        string value,
        CancellationToken cancellationToken)
    {
        await WriteAsync(stream, value + "\n", cancellationToken);
    }
}
