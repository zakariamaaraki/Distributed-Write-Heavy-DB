using Microsoft.AspNetCore.Mvc;

namespace LsmWriteDb.Transactions;

public static class DistributedTransactionEndpoints
{
    public static WebApplication MapDistributedTransactionEndpoints(this WebApplication app)
    {
        app.MapPost("/distributed-transactions", (DistributedTransactionManager manager) => Results.Created("/distributed-transactions", manager.Begin()));
        app.MapPut("/distributed-transactions/{id:guid}/writes", (Guid id, DistributedWrite write, DistributedTransactionManager manager) =>
            manager.Stage(id, write, out var info) ? Results.Ok(info) : Results.NotFound(new { error = "transaction not found" }));
        app.MapPost("/distributed-transactions/{id:guid}/commit", async (Guid id, DistributedTransactionManager manager, CancellationToken token) =>
            Results.Ok(await manager.CommitAsync(id, token)));
        app.MapGet("/distributed-transactions/metrics", (DistributedTransactionManager manager) => Results.Ok(manager.Metrics()));
        app.MapGet("/distributed-transactions/{id:guid}", (Guid id, DistributedTransactionManager manager) => manager.Status(id) is { } status ? Results.Ok(status) : Results.NotFound());
        app.MapPost("/distributed-transactions/{id:guid}/recover", async (Guid id, DistributedTransactionManager manager, CancellationToken token) =>
            Results.Ok(await manager.RecoverAsync(id, token)));
        app.MapDelete("/distributed-transactions/{id:guid}", (Guid id, DistributedTransactionManager manager) =>
            manager.Rollback(id) ? Results.NoContent() : Results.NotFound(new { error = "transaction not found" }));

        app.MapPost("/distributed-transactions/prepare", async ([FromBody] DistributedPrepareRequest request, DistributedTransactionManager manager) =>
            await manager.PrepareParticipantAsync(request) ? Results.Ok(new { prepared = true }) : Results.BadRequest(new { prepared = false }));
        app.MapPost("/distributed-transactions/commit", async ([FromBody] DistributedDecisionRequest request, DistributedTransactionManager manager) =>
            await manager.CommitParticipantAsync(request.TransactionId) ? Results.Ok(new { committed = true }) : Results.NotFound());
        app.MapPost("/distributed-transactions/abort", ([FromBody] DistributedDecisionRequest request, DistributedTransactionManager manager) =>
            manager.AbortParticipant(request.TransactionId) ? Results.Ok(new { aborted = true }) : Results.NotFound());
        return app;
    }
}
