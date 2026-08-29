using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Indexes;
using LsmWriteDb.Raft;
using LsmWriteDb.Storage;
using LsmWriteDb.Sql;
using LsmWriteDb.Search;
using LsmWriteDb.SqlConsole;
using LsmWriteDb.TcpSql;
using LsmWriteDb.Transactions;

var builder = WebApplication.CreateBuilder(args);

var dataPath = Path.Combine(builder.Environment.ContentRootPath, "data");
var flushThreshold = builder.Configuration.GetValue("Lsm:FlushThreshold", 100);
var blockSizeBytes = builder.Configuration.GetValue("Lsm:BlockSizeBytes", LsmStoreOptions.DefaultBlockSizeBytes);
var maxSstableFileSizeBytes = builder.Configuration.GetValue("Lsm:MaxSstableFileSizeBytes", LsmStoreOptions.DefaultMaxSstableFileSizeBytes);
var changeLogSegmentMaxBytes = builder.Configuration.GetValue("Lsm:ChangeLogSegmentMaxBytes", LsmStoreOptions.DefaultChangeLogSegmentMaxBytes);
var maxConcurrentRequests = builder.Configuration.GetValue("Lsm:MaxConcurrentRequests", 5000);
var maxConcurrentReads = builder.Configuration.GetValue("Lsm:MaxConcurrentReads", maxConcurrentRequests);
var maxConcurrentWrites = builder.Configuration.GetValue("Lsm:MaxConcurrentWrites", maxConcurrentRequests);
var raftOptions = builder.Configuration.GetSection("Raft").Get<RaftOptions>() ?? new RaftOptions();

builder.Services.AddSingleton(new RequestMetrics(maxConcurrentReads, maxConcurrentWrites));
builder.Services.AddSingleton(new LsmStoreOptions(
    dataPath,
    flushThreshold,
    BlockSizeBytes: blockSizeBytes,
    MaxSstableFileSizeBytes: maxSstableFileSizeBytes,
    ChangeLogSegmentMaxBytes: changeLogSegmentMaxBytes));
builder.Services.AddSingleton<ChangeLogService>();
builder.Services.AddSingleton<DatabaseEngine>();
builder.Services.AddSingleton<TransactionManager>();
builder.Services.AddSingleton<DistributedTransactionManager>();
builder.Services.AddHostedService<DistributedTransactionCleanupService>();
builder.Services.AddSingleton<SqlEngine>();
builder.Services.AddSingleton(raftOptions);
builder.Services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<RaftStateStore>();
builder.Services.AddSingleton<RaftNode>();
builder.Services.AddSingleton<RaftRoleGuard>();
builder.Services.AddSingleton<TableRaftCoordinator>();
builder.Services.AddSingleton<TableRaftRoleGuard>();
builder.Services.Configure<TcpSqlOptions>(builder.Configuration.GetSection("TcpSql"));
builder.Services.AddHostedService<RaftElectionService>();
builder.Services.AddHostedService<TableRaftReplicationService>();
builder.Services.AddHostedService<TableRaftElectionService>();
builder.Services.AddHostedService<TcpSqlServer>();

var app = builder.Build();

var database = app.Services.GetRequiredService<DatabaseEngine>();
await database.InitializeAsync();

app.UseMiddleware<RequestMetricsMiddleware>();

app.UseStaticFiles();

app.MapGet("/", () => Results.Ok(new { name = "Simple LSM Write Database" }));

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/metrics", (RequestMetrics metrics, DatabaseEngine database) =>
{
    var snapshot = metrics.Snapshot();
    return Results.Ok(new
    {
        snapshot.ActiveRequests,
        snapshot.QueuedRequests,
        snapshot.MaxConcurrentRequests,
        snapshot.ActiveReads,
        snapshot.QueuedReads,
        snapshot.MaxConcurrentReads,
        snapshot.ActiveWrites,
        snapshot.QueuedWrites,
        snapshot.MaxConcurrentWrites,
        TotalDiskSizeBytes = database.GetTotalDiskSizeBytes()
    });
});

app.MapTableEndpoints();
app.MapTransactionEndpoints();
app.MapDistributedTransactionEndpoints();
app.MapIndexEndpoints();
app.MapSearchEndpoints();
app.MapSqlEndpoints();
app.MapSqlConsoleEndpoints();
app.MapChangeLogEndpoints();
app.MapRaftEndpoints();
app.MapTableRaftEndpoints();

app.Run();
