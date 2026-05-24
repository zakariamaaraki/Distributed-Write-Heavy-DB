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
        Assert.Contains("CREATE INDEX idx_users_tier ON users (value.tier)", html);
        Assert.Contains("SELECT key, value FROM users WHERE value.tier = 'gold' LIMIT 50", html);
    }
}
