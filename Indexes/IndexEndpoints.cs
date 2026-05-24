using LsmWriteDb.Storage;

namespace LsmWriteDb.Indexes;

public static class IndexEndpoints
{
    public static WebApplication MapIndexEndpoints(this WebApplication app)
    {
        app.MapGet("/indexes", async (DatabaseEngine db) =>
        {
            try
            {
                return Results.Ok(await db.ListIndexesAsync());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/indexes/btrees", async (DatabaseEngine db) =>
        {
            try
            {
                return Results.Ok(await db.DumpIndexTreesAsync());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/indexes/{name}/btree", async (string name, DatabaseEngine db) =>
        {
            try
            {
                var dump = await db.DumpIndexTreeAsync(name);
                return dump is null
                    ? Results.NotFound(new { error = $"index '{name}' not found" })
                    : Results.Ok(dump);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
