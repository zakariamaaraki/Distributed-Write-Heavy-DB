namespace LsmWriteDb.Tests;

public sealed class RequestMetricsTests
{
    [Fact]
    public async Task RequestMetrics_TracksActiveAndQueuedRequests()
    {
        var metrics = new RequestMetrics(1);
        using var first = await metrics.EnterAsync(CancellationToken.None);
        var secondTask = metrics.EnterAsync(CancellationToken.None);

        for (var attempt = 0; attempt < 50 && metrics.Snapshot().QueuedRequests == 0; attempt++)
            await Task.Delay(10);

        var queued = metrics.Snapshot();
        Assert.Equal(1, queued.ActiveRequests);
        Assert.Equal(1, queued.QueuedRequests);
        Assert.Equal(1, queued.MaxConcurrentRequests);

        first.Dispose();
        using var second = await secondTask;
        var running = metrics.Snapshot();
        Assert.Equal(1, running.ActiveRequests);
        Assert.Equal(0, running.QueuedRequests);
    }

    [Fact]
    public async Task RequestMetrics_ReadAndWritePoolsAreIndependent()
    {
        var metrics = new RequestMetrics(1, 1);
        using var write = await metrics.EnterAsync(isWrite: true, CancellationToken.None);
        using var read = await metrics.EnterAsync(isWrite: false, CancellationToken.None);
        var secondWriteTask = metrics.EnterAsync(isWrite: true, CancellationToken.None);

        for (var attempt = 0; attempt < 50 && metrics.Snapshot().QueuedWrites == 0; attempt++)
            await Task.Delay(10);

        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.QueuedWrites);
        Assert.Equal(0, snapshot.QueuedReads);
        Assert.Equal(1, snapshot.MaxConcurrentReads);
        Assert.Equal(1, snapshot.MaxConcurrentWrites);

        write.Dispose();
        using var secondWrite = await secondWriteTask;
    }
}