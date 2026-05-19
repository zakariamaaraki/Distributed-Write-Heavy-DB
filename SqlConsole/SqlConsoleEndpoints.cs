namespace LsmWriteDb.SqlConsole;

public static class SqlConsoleEndpoints
{
    public static WebApplication MapSqlConsoleEndpoints(this WebApplication app)
    {
        app.MapGet("/sql-console", () => Results.Content(SqlConsolePage.Html, "text/html; charset=utf-8"));
        return app;
    }
}
