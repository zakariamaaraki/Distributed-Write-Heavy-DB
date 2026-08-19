using LsmWriteDb.Raft;
using Microsoft.AspNetCore.Mvc;

namespace LsmWriteDb.Storage;

public static class TableEndpoints
{
    public static WebApplication MapTableEndpoints(this WebApplication app)
    {
        app.MapGet("/tables", async (DatabaseEngine db) => Results.Ok(await db.ListTablesAsync()));

        app.MapPut("/tables/{table}", async (string table, DatabaseEngine db, TableRaftRoleGuard tableRaft) =>
        {
            var targetTable = table;
            if (!tableRaft.CanAcceptWrites(targetTable))
            {
                return tableRaft.WriteRejectedResult(targetTable);
            }

            try
            {
                var created = await db.CreateTableAsync(table);
                return created
                    ? Results.Created($"/tables/{TableNames.Normalize(table)}", new { table = TableNames.Normalize(table) })
                    : Results.Ok(new { table = TableNames.Normalize(table), message = "table already exists" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        MapKeyValueRoutes(app, "/kv", TableNames.Default);
        MapKeyValueRoutes(app, "/tables/{table}/kv", tableName: null);

        app.MapGet("/tables/{table}/snapshot", async (string table, DatabaseEngine db) =>
        {
            try { return Results.Ok(await db.GetSnapshotAsync(table)); }
            catch (TableNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/stats", async (DatabaseEngine db) => Results.Ok(await db.GetStatsAsync()));

        app.MapGet("/tables/{table}/stats", async (string table, DatabaseEngine db) =>
        {
            try
            {
                return Results.Ok(await db.GetTableStatsAsync(table));
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }

    private static void MapKeyValueRoutes(WebApplication app, string routePrefix, string? tableName)
    {
        app.MapGet($"{routePrefix}/range", async (
            string? table,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] int? limit,
            DatabaseEngine db) =>
        {
            if (start is not null && end is not null && string.CompareOrdinal(start, end) > 0)
            {
                return Results.BadRequest(new { error = "start must be less than or equal to end" });
            }

            try
            {
                var rows = await db.RangeAsync(tableName ?? table ?? TableNames.Default, start, end, limit ?? 100);
                return Results.Ok(rows);
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet($"{routePrefix}/{{key}}", async (string? table, string key, DatabaseEngine db) =>
        {
            try
            {
                var row = await db.GetAsync(tableName ?? table ?? TableNames.Default, key);
                return row is null ? Results.NotFound() : Results.Ok(row);
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPut($"{routePrefix}/{{key}}", async (
            string? table,
            string key,
            [FromBody] PutValueRequest request,
            DatabaseEngine db,
            TableRaftRoleGuard tableRaft) =>
        {
            var targetTable = tableName ?? table ?? TableNames.Default;
            if (!tableRaft.CanAcceptWrites(targetTable))
            {
                return tableRaft.WriteRejectedResult(targetTable);
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.BadRequest(new { error = "key is required" });
            }

            if (request.Value is null)
            {
                return Results.BadRequest(new { error = "value is required" });
            }

            try
            {
                await db.PutAsync(tableName ?? table ?? TableNames.Default, key, request.Value);
                return Results.NoContent();
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete($"{routePrefix}/{{key}}", async (
            string? table,
            string key,
            DatabaseEngine db,
            TableRaftRoleGuard tableRaft) =>
        {
            var targetTable = tableName ?? table ?? TableNames.Default;
            if (!tableRaft.CanAcceptWrites(targetTable))
            {
                return tableRaft.WriteRejectedResult(targetTable);
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.BadRequest(new { error = "key is required" });
            }

            try
            {
                await db.DeleteAsync(tableName ?? table ?? TableNames.Default, key);
                return Results.NoContent();
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public sealed record PutValueRequest(string? Value);
