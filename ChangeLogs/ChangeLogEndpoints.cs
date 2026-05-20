using System.Text.Json;
using LsmWriteDb.StaticAssets;
using Microsoft.AspNetCore.Mvc;

namespace LsmWriteDb.ChangeLogs;

public static class ChangeLogEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WebApplication MapChangeLogEndpoints(this WebApplication app)
    {
        app.MapGet("/changes", async (
            [FromQuery] long? fromSequence,
            [FromQuery] int? limit,
            ChangeLogService changeLog,
            CancellationToken cancellationToken) =>
        {
            var entries = await changeLog.ReadAfterAsync(
                fromSequence ?? 0,
                limit ?? 100,
                cancellationToken);

            return Results.Ok(entries);
        });

        app.MapGet("/changes/stream", StreamChangesAsync);
        app.MapGet("/changes-console", (IWebHostEnvironment environment) =>
            StaticPageResults.Html(environment, "changes-console", "index.html"));

        return app;
    }

    private static async Task StreamChangesAsync(
        HttpContext context,
        [FromQuery] long? fromSequence,
        ChangeLogService changeLog)
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        await foreach (var entry in changeLog.StreamAsync(fromSequence ?? 0, context.RequestAborted))
        {
            await context.Response.WriteAsync($"id: {entry.Sequence}\n", context.RequestAborted);
            await context.Response.WriteAsync("event: change\n", context.RequestAborted);
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(entry, JsonOptions)}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
}
