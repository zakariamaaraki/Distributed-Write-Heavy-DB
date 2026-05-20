using LsmWriteDb.StaticAssets;

namespace LsmWriteDb.SqlConsole;

public static class SqlConsoleEndpoints
{
    public static WebApplication MapSqlConsoleEndpoints(this WebApplication app)
    {
        app.MapGet("/sql-console", (IWebHostEnvironment environment) =>
            StaticPageResults.Html(environment, "sql-console", "index.html"));

        return app;
    }
}
