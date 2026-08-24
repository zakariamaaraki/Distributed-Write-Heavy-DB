using LsmWriteDb.Storage;
using Microsoft.Extensions.Logging;

namespace LsmWriteDb.Raft;

public sealed class TableRaftElectionService : BackgroundService
{
    private readonly DatabaseEngine _database;
    private readonly TableRaftCoordinator _coordinator;
    private readonly ILogger<TableRaftElectionService> _logger;
    private DateTimeOffset _nextRebalanceAt = DateTimeOffset.MinValue;

    public TableRaftElectionService(
        DatabaseEngine database,
        TableRaftCoordinator coordinator,
        ILogger<TableRaftElectionService> logger)
    {
        _database = database;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tables = await _database.ListAllTablesAsync(stoppingToken);
                foreach (var table in tables)
                {
                    await _coordinator.EnsureTableAsync(table.Name, stoppingToken);
                    _logger.LogInformation("table raft loop ensured table={Table}", table.Name);
                }

                if (DateTimeOffset.UtcNow >= _nextRebalanceAt)
                {
                    await _coordinator.RebalanceAsync(stoppingToken);
                    _nextRebalanceAt = DateTimeOffset.UtcNow.AddSeconds(10);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "table raft maintenance iteration failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}