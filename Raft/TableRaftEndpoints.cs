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
