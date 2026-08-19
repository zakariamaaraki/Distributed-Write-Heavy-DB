using LsmWriteDb.Storage;

namespace LsmWriteDb.Raft;

public sealed class TableRaftElectionService : BackgroundService
{
    private readonly DatabaseEngine _database;
    private readonly TableRaftCoordinator _coordinator;
    private DateTimeOffset _nextRebalanceAt = DateTimeOffset.MinValue;

    public TableRaftElectionService(DatabaseEngine database, TableRaftCoordinator coordinator)
    {
        _database = database;
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= _nextRebalanceAt)
            {
                await _coordinator.RebalanceAsync(stoppingToken);
                _nextRebalanceAt = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            foreach (var table in await _database.ListAllTablesAsync(stoppingToken))
                await _coordinator.EnsureTableAsync(table.Name, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
