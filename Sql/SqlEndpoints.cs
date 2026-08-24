using Microsoft.AspNetCore.Mvc;
using LsmWriteDb.Raft;
using LsmWriteDb.Storage;

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
            catch (SqlExecutionException ex) when (ex.StatusCode == StatusCodes.Status503ServiceUnavailable)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
            }
            catch (SqlExecutionException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (SqlExecutionException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (RaftWriteRejectedException ex)
            {
                return Results.Json(
                    new
                    {
                        error = ex.Message,
                        role = ex.Role.ToString(),
                        leaderId = ex.LeaderId,
                        leaderUrl = ex.LeaderUrl
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (TableWriteRejectedException ex)
            {
                return Results.Json(
                    new
                    {
                        error = ex.Message,
                        table = ex.Table,
                        leaderId = ex.LeaderId,
                        leaderUrl = ex.LeaderUrl
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
