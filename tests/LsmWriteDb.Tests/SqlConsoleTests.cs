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
        Assert.Contains("X-Read-Consistency", script);
        Assert.Contains("id=\"transactionId\"", html);
        Assert.Contains("id=\"readConsistency\"", html);
        Assert.Contains("Strong (table leader)", html);
        Assert.Contains("/changes-console", html);
        Assert.Contains("ANSI: CREATE TABLE relational_users (...)", html);
        Assert.Contains("CREATE TABLE relational_users (id INTEGER PRIMARY KEY, name VARCHAR(255) NOT NULL, active BOOLEAN)", html);
        Assert.Contains("SELECT id, name, active FROM relational_users WHERE id = 42", html);
        Assert.Contains("CREATE INDEX idx_users_tier ON users (value.tier)", html);
        Assert.Contains("SELECT key, value FROM users WHERE value.tier = 'gold' LIMIT 50", html);
    }
}
