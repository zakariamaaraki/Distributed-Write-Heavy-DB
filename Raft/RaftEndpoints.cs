namespace LsmWriteDb.Raft;

public static class RaftEndpoints
{
    public static WebApplication MapRaftEndpoints(this WebApplication app)
    {
        app.MapGet("/raft/state", (RaftNode node) => Results.Ok(node.GetStatus()));

        app.MapPost("/raft/request-vote", async (
            RaftRequestVoteRequest request,
            RaftNode node,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await node.RequestVoteAsync(request, cancellationToken));
        });

        app.MapPost("/raft/append-entries", async (
            RaftAppendEntriesRequest request,
            RaftNode node,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await node.AppendEntriesAsync(request, cancellationToken));
        });

        return app;
    }
}
