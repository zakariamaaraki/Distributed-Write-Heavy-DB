using LsmWriteDb.Sql;
using LsmWriteDb.TcpSql;
using System.Text.Json;

namespace LsmWriteDb.Tests;

public sealed class TcpSqlProtocolTests
{
    [Theory]
    [InlineData("quit")]
    [InlineData("QUIT;")]
    [InlineData("exit;")]
    [InlineData("\\q")]
    public void IsExitCommand_AcceptsSessionExitCommands(string command)
    {
        Assert.True(TcpSqlProtocol.IsExitCommand(command));
    }

    [Fact]
    public void FormatResult_ReturnsLineFriendlyJsonPayload()
    {
        var result = SqlExecutionResult.WithRows(
            "SELECT",
            [new Dictionary<string, string> { ["key"] = "user:1", ["value"] = "{\"tier\":\"gold\"}" }]);

        var formatted = TcpSqlProtocol.FormatResult(result);

        Assert.StartsWith("OK ", formatted);
        Assert.Contains("\"statementType\":\"SELECT\"", formatted);
        Assert.DoesNotContain('\n', formatted);
    }

    [Fact]
    public void FormatError_ReturnsJsonEscapedMessage()
    {
        var formatted = TcpSqlProtocol.FormatError("bad 'query'");

        Assert.StartsWith("ERR ", formatted);
        Assert.Equal("bad 'query'", JsonSerializer.Deserialize<string>(formatted["ERR ".Length..]));
    }
}
