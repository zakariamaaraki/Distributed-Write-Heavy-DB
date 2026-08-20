namespace LsmWriteDb.Transactions;

public sealed class DistributedTransactionCleanupService : BackgroundService
{
    private readonly DistributedTransactionManager _manager;
    private readonly ILogger<DistributedTransactionCleanupService> _logger;

    public DistributedTransactionCleanupService(DistributedTransactionManager manager, ILogger<DistributedTransactionCleanupService> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _manager.RecoverOutstandingAsync(stoppingToken);
            var removed = _manager.CleanupExpired(TimeSpan.FromHours(1));
            if (removed > 0) _logger.LogWarning("removed {Count} expired distributed transactions", removed);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
