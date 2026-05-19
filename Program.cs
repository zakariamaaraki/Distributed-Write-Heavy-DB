using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Raft;
using LsmWriteDb.Storage;
using LsmWriteDb.Sql;
using LsmWriteDb.SqlConsole;
using LsmWriteDb.Transactions;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var dataPath = Path.Combine(builder.Environment.ContentRootPath, "data");
var flushThreshold = builder.Configuration.GetValue("Lsm:FlushThreshold", 100);
var raftOptions = builder.Configuration.GetSection("Raft").Get<RaftOptions>() ?? new RaftOptions();

builder.Services.AddSingleton(new LsmStoreOptions(dataPath, flushThreshold));
builder.Services.AddSingleton<ChangeLogService>();
builder.Services.AddSingleton<LsmStore>();
builder.Services.AddSingleton<TransactionManager>();
builder.Services.AddSingleton<SqlEngine>();
builder.Services.AddSingleton(raftOptions);
builder.Services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<RaftStateStore>();
builder.Services.AddSingleton<RaftNode>();
builder.Services.AddSingleton<RaftRoleGuard>();
builder.Services.AddHostedService<RaftElectionService>();
builder.Services.AddHostedService<RaftChangeLogReplicationService>();

var app = builder.Build();

var store = app.Services.GetRequiredService<LsmStore>();
await store.InitializeAsync();

app.MapGet("/", () => Results.Ok(new { name = "Simple LSM Write Database" }));

app.MapGet("/kv/range", async (
    [FromQuery] string? start,
    [FromQuery] string? end,
    [FromQuery] int? limit,
    LsmStore db) =>
{
    if (start is not null && end is not null && string.CompareOrdinal(start, end) > 0)
    {
        return Results.BadRequest(new { error = "start must be less than or equal to end" });
    }

    var rows = await db.RangeAsync(start, end, limit ?? 100);
    return Results.Ok(rows);
});

app.MapGet("/kv/{key}", async (string key, LsmStore db) =>
{
    var row = await db.GetAsync(key);
    return row is null ? Results.NotFound() : Results.Ok(row);
});

app.MapPut("/kv/{key}", async (string key, [FromBody] PutValueRequest request, LsmStore db, RaftRoleGuard raft) =>
{
    if (!raft.CanAcceptWrites)
    {
        return raft.WriteRejectedResult();
    }

    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest(new { error = "key is required" });
    }

    if (request.Value is null)
    {
        return Results.BadRequest(new { error = "value is required" });
    }

    await db.PutAsync(key, request.Value);
    return Results.NoContent();
});

app.MapDelete("/kv/{key}", async (string key, LsmStore db, RaftRoleGuard raft) =>
{
    if (!raft.CanAcceptWrites)
    {
        return raft.WriteRejectedResult();
    }

    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest(new { error = "key is required" });
    }

    await db.DeleteAsync(key);
    return Results.NoContent();
});

app.MapTransactionEndpoints();
app.MapSqlEndpoints();
app.MapSqlConsoleEndpoints();
app.MapChangeLogEndpoints();
app.MapRaftEndpoints();

app.MapGet("/stats", async (LsmStore db) => Results.Ok(await db.GetStatsAsync()));

app.Run();

internal sealed record PutValueRequest(string? Value);
