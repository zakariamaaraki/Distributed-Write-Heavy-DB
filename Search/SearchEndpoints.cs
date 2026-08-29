using LsmWriteDb.Storage;
using LsmWriteDb.Raft;

namespace LsmWriteDb.Search;

public sealed record CreateSearchIndexRequest(string Table, IReadOnlyList<string> Fields);

public static class SearchEndpoints
{
    public static WebApplication MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/search/indexes", async (DatabaseEngine database, CancellationToken cancellationToken) =>
            Results.Ok(await database.ListSearchIndexesAsync(cancellationToken)));

        app.MapPut("/search/indexes/{name}", async (string name, CreateSearchIndexRequest request, DatabaseEngine database, TableRaftCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try
            {
                var created = await database.CreateSearchIndexAsync(request.Table, name, request.Fields, cancellationToken);
                if (created) await coordinator.EnsureSearchIndexOnPeersAsync(name, request.Table, request.Fields, cancellationToken);
                return created ? Results.Created($"/search/indexes/{name}", new { name, request.Table, request.Fields }) : Results.Ok(new { name, message = "search index already exists" });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (TableNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapPut("/raft/search-indexes/{name}/ensure", async (string name, CreateSearchIndexRequest request, DatabaseEngine database, CancellationToken cancellationToken) =>
        {
            try
            {
                var created = await database.CreateSearchIndexAsync(request.Table, name, request.Fields, cancellationToken);
                return Results.Ok(new { name, created });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (TableNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapPost("/search/{name}", async (string name, SearchRequest request, DatabaseEngine database, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await database.SearchAsync(name, request, cancellationToken)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/search/{name}/rebuild", async (string name, DatabaseEngine database, CancellationToken cancellationToken) =>
        {
            try { await database.RebuildSearchIndexAsync(name, cancellationToken); return Results.NoContent(); }
            catch (ArgumentException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        return app;
    }
}