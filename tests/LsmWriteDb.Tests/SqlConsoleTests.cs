namespace LsmWriteDb.Tests;

public sealed class SqlConsoleTests
{
    [Fact]
    public void StaticAssets_IncludeSqlConsoleShellAndSqlEndpoint()
    {
        var html = StaticAssetTestHelper.Read(Path.Combine("sql-console", "index.html"));
        var script = StaticAssetTestHelper.Read(Path.Combine("sql-console", "sql-console.js"));

        Assert.Contains("LsmWriteDb Console", html);
        Assert.Contains("id=\"queryEditor\"", html);
        Assert.Contains("id=\"runQuery\"", html);
        Assert.Contains("/sql-console/sql-console.css", html);
        Assert.Contains("/sql-console/sql-console.js", html);
        Assert.Contains("fetch('/sql'", script);
        Assert.Contains("id=\"transactionId\"", html);
        Assert.Contains("/changes-console", html);
    }
}
