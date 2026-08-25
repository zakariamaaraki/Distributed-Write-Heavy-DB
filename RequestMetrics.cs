using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

public sealed record RequestMetricsSnapshot(
    int ActiveRequests,
    int QueuedRequests,
    int MaxConcurrentRequests,
    int ActiveReads,
    int QueuedReads,
    int MaxConcurrentReads,
    int ActiveWrites,
    int QueuedWrites,
    int MaxConcurrentWrites);

public sealed class RequestMetrics
{
    private readonly SemaphoreSlim _readSlots;
    private readonly SemaphoreSlim _writeSlots;
    private readonly int _maxConcurrentReads;
    private readonly int _maxConcurrentWrites;
    private int _activeReads;
    private int _queuedReads;
    private int _activeWrites;
    private int _queuedWrites;

    public RequestMetrics(int maxConcurrentRequests)
        : this(maxConcurrentRequests, maxConcurrentRequests) { }

    public RequestMetrics(int maxConcurrentReads, int maxConcurrentWrites)
    {
        if (maxConcurrentReads <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentReads));
        if (maxConcurrentWrites <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentWrites));
        _maxConcurrentReads = maxConcurrentReads;
        _maxConcurrentWrites = maxConcurrentWrites;
        _readSlots = new SemaphoreSlim(maxConcurrentReads, maxConcurrentReads);
        _writeSlots = new SemaphoreSlim(maxConcurrentWrites, maxConcurrentWrites);
    }

    public RequestMetricsSnapshot Snapshot()
        => new(
            ActiveRequests: Volatile.Read(ref _activeReads) + Volatile.Read(ref _activeWrites),
            QueuedRequests: Volatile.Read(ref _queuedReads) + Volatile.Read(ref _queuedWrites),
            MaxConcurrentRequests: Math.Max(_maxConcurrentReads, _maxConcurrentWrites),
            ActiveReads: Volatile.Read(ref _activeReads),
            QueuedReads: Volatile.Read(ref _queuedReads),
            MaxConcurrentReads: _maxConcurrentReads,
            ActiveWrites: Volatile.Read(ref _activeWrites),
            QueuedWrites: Volatile.Read(ref _queuedWrites),
            MaxConcurrentWrites: _maxConcurrentWrites);

    public Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
        => EnterAsync(isWrite: false, cancellationToken);

    public async Task<IDisposable> EnterAsync(bool isWrite, CancellationToken cancellationToken)
    {
        var slots = isWrite ? _writeSlots : _readSlots;
        if (!slots.Wait(0))
        {
            IncrementQueued(isWrite);
            try { await slots.WaitAsync(cancellationToken); }
            finally { DecrementQueued(isWrite); }
        }

        IncrementActive(isWrite);
        return new Lease(this, slots, isWrite);
    }

    private void IncrementQueued(bool isWrite)
    {
        if (isWrite) Interlocked.Increment(ref _queuedWrites);
        else Interlocked.Increment(ref _queuedReads);
    }

    private void DecrementQueued(bool isWrite)
    {
        if (isWrite) Interlocked.Decrement(ref _queuedWrites);
        else Interlocked.Decrement(ref _queuedReads);
    }

    private void IncrementActive(bool isWrite)
    {
        if (isWrite) Interlocked.Increment(ref _activeWrites);
        else Interlocked.Increment(ref _activeReads);
    }

    private void Exit(SemaphoreSlim slots, bool isWrite)
    {
        if (isWrite) Interlocked.Decrement(ref _activeWrites);
        else Interlocked.Decrement(ref _activeReads);
        slots.Release();
    }

    private sealed class Lease(RequestMetrics owner, SemaphoreSlim slots, bool isWrite) : IDisposable
    {
        private RequestMetrics? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit(slots, isWrite);
    }
}

public sealed class RequestMetricsMiddleware(RequestDelegate next, RequestMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Monitoring-Request", out _)
            || !context.Request.Path.Equals("/sql", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var isWrite = await IsWriteRequestAsync(context);
        using var lease = await metrics.EnterAsync(isWrite, context.RequestAborted);
        await next(context);
    }

    private static async Task<bool> IsWriteRequestAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            || !context.Request.Path.Equals("/sql", StringComparison.OrdinalIgnoreCase))
        {
            return context.Request.Method is not ("GET" or "HEAD" or "OPTIONS");
        }

        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;

        try
        {
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.TryGetProperty("query", out var queryValue)
                ? queryValue.GetString()
                : null;
            var keyword = Regex.Match(query?.TrimStart() ?? string.Empty, "^[A-Za-z]+", RegexOptions.CultureInvariant).Value;
            return !keyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                && !keyword.Equals("SHOW", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return true;
        }
    }}