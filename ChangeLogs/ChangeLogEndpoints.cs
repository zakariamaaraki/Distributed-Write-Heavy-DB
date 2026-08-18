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

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var writerMutex = new SemaphoreSlim(1, 1);
        var heartbeatTask = SendHeartbeatsAsync(context, heartbeatCancellation.Token, writerMutex);

        try
        {
            await foreach (var entry in changeLog.StreamAsync(fromSequence ?? 0, context.RequestAborted))
            {
                await writerMutex.WaitAsync(context.RequestAborted);
                try
                {
                    await context.Response.WriteAsync($"id: {entry.Sequence}\n", context.RequestAborted);
                    await context.Response.WriteAsync("event: change\n", context.RequestAborted);
                    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(entry, JsonOptions)}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                finally
                {
                    writerMutex.Release();
                }
            }
        }
        finally
        {
            await heartbeatCancellation.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task SendHeartbeatsAsync(
        HttpContext context,
        CancellationToken cancellationToken,
        SemaphoreSlim writerMutex)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await writerMutex.WaitAsync(cancellationToken);
            try
            {
                await context.Response.WriteAsync(": heartbeat\\n\\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
            finally
            {
                writerMutex.Release();
            }
        }
    }
}
