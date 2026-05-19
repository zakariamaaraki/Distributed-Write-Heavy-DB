using Microsoft.AspNetCore.Mvc;

namespace LsmWriteDb.Sql;

public static class SqlEndpoints
{
    public static WebApplication MapSqlEndpoints(this WebApplication app)
    {
        app.MapPost("/sql", async ([FromBody] SqlQueryRequest request, SqlEngine engine) =>
        {
            try
            {
                return Results.Ok(await engine.ExecuteAsync(request));
            }
            catch (SqlParseException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SqlExecutionException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (SqlExecutionException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
