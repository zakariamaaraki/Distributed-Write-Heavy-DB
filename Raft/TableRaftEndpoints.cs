using LsmWriteDb.Storage;
namespace LsmWriteDb.Raft;

public static class TableRaftEndpoints
{
    public static WebApplication MapTableRaftEndpoints(this WebApplication app)
    {
        app.MapPost("/raft/membership/register", async (PeerRegistrationRequest request, TableRaftCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try
            {
                coordinator.RegisterPeer(new RaftPeerOptions { NodeId = request.NodeId, Url = request.Url });
                return Results.Ok(await coordinator.RebalanceAsync(cancellationToken));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/raft/rebalance", async (TableRaftCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            var records = await coordinator.RebalanceAsync(cancellationToken);
            return Results.Ok(records);
        });

        app.MapGet("/raft/tables/{table}/state", (string table, TableRaftCoordinator coordinator) =>
        {
            try { return Results.Ok(coordinator.GetStatus(table)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/raft/tables/{table}/ensure", async (
            string table,
            DatabaseEngine database,
            TableRaftCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await database.CreateTableAsync(table, cancellationToken);
                await coordinator.EnsureTableAsync(table, cancellationToken);
                var ready = await coordinator.WaitForLeaderAsync(table, cancellationToken);
                return ready is null
                    ? Results.Json(new { error = "table leader election is not ready" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(new { table = TableNames.Normalize(table), leaderId = ready.LeaderId, leaderUrl = ready.LeaderUrl, term = ready.CurrentTerm });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapDelete("/raft/tables/{table}", async (
            string table,
            DatabaseEngine database,
            TableRaftCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var normalized = TableNames.Normalize(table);
                await database.DropTableAsync(normalized, cancellationToken);
                coordinator.RemoveTable(normalized);
                return Results.NoContent();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/raft/tables/{table}/request-vote", async (
            string table,
            RaftRequestVoteRequest request,
            TableRaftCoordinator coordinator,
            CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.RequestVoteAsync(table, request, cancellationToken)));

        app.MapPost("/raft/tables/{table}/append-entries", async (
            string table,
            RaftAppendEntriesRequest request,
            TableRaftCoordinator coordinator,
            CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.AppendEntriesAsync(table, request, cancellationToken)));

        return app;
    }
}

public sealed record PeerRegistrationRequest(string NodeId, string Url);
