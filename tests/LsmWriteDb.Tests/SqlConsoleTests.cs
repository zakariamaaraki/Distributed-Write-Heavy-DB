using LsmWriteDb.SqlConsole;

namespace LsmWriteDb.Tests;

public sealed class SqlConsoleTests
{
    [Fact]
    public void Html_IncludesSqlConsoleShellAndSqlEndpoint()
    {
        var html = SqlConsolePage.Html;

        Assert.Contains("LsmWriteDb Console", html);
        Assert.Contains("id=\"queryEditor\"", html);
        Assert.Contains("id=\"runQuery\"", html);
        Assert.Contains("fetch('/sql'", html);
        Assert.Contains("id=\"transactionId\"", html);
    }
}
