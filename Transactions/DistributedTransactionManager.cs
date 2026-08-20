using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LsmWriteDb.Raft;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Transactions;

public sealed record DistributedTransactionInfo(Guid TransactionId, string Status, int OperationCount);
public sealed record DistributedWrite(string Table, string Key, string? Value, bool IsDeleted);
public sealed record DistributedPrepareRequest(Guid TransactionId, IReadOnlyList<DistributedWrite> Writes);
public sealed record DistributedDecisionRequest(Guid TransactionId);

public sealed class DistributedTransactionManager
{
    private readonly DatabaseEngine _database;
    private readonly RaftOptions _options;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<Guid, DistributedBuffer> _transactions = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<DistributedWrite>> _prepared = new();
    private readonly ConcurrentDictionary<Guid, string> _decisions = new();
    private readonly string _journalPath;
    private readonly ILogger<DistributedTransactionManager> _logger;
    private long _prepareCount, _commitCount, _abortCount, _inDoubtCount;

    public DistributedTransactionManager(DatabaseEngine database, RaftOptions options, HttpClient http, LsmStoreOptions storage, ILogger<DistributedTransactionManager> logger)
    {
        _database = database;
        _options = options;
        _http = http;
        _logger = logger;
        _journalPath = Path.Combine(storage.DataPath, "distributed-transactions.json");
        LoadJournal();
    }

    public DistributedTransactionInfo Begin()
    {
        var id = Guid.NewGuid();
        _transactions[id] = new DistributedBuffer();
        PersistJournal();
        return Info(id, "active");
    }

    public bool Stage(Guid id, DistributedWrite write, out DistributedTransactionInfo info)
    {
        if (!_transactions.TryGetValue(id, out var buffer)) { info = default!; return false; }
        buffer.Writes.Add(new DistributedWrite(TableNames.Normalize(write.Table), write.Key, write.Value, write.IsDeleted));
        info = Info(id, "active");
        return true;
    }

    public async Task<bool> PrepareParticipantAsync(DistributedPrepareRequest request)
    {
        if (request.Writes.Count == 0) return false;
        if (request.Writes.Any(write => string.IsNullOrWhiteSpace(write.Table) || string.IsNullOrWhiteSpace(write.Key))) return false;
        if (_decisions.TryGetValue(request.TransactionId, out var existingDecision)) return existingDecision is "prepared" or "committed";
        _prepared[request.TransactionId] = request.Writes;
        Interlocked.Increment(ref _prepareCount);
        _decisions[request.TransactionId] = "prepared";
        PersistJournal();
        _logger.LogInformation("distributed transaction {TransactionId} prepared", request.TransactionId);
        return await Task.FromResult(true);
    }

    public async Task<bool> CommitParticipantAsync(Guid id)
    {
        if (_decisions.TryGetValue(id, out var existingDecision) && existingDecision == "committed") return true;
        if (!_prepared.TryGetValue(id, out var writes)) return false;
        await _database.ApplyBatchAsync(writes.Select(write => new StoreWriteOperation(write.Key, write.Value, write.IsDeleted) { Table = write.Table }).ToList());
        _prepared.TryRemove(id, out _);
        _decisions[id] = "committed";
        Interlocked.Increment(ref _commitCount);
        PersistJournal();
        _logger.LogInformation("distributed transaction {TransactionId} committed", id);
        return true;
    }

    public bool AbortParticipant(Guid id)
    {
        if (_decisions.TryGetValue(id, out var decision) && decision == "committed") return false;
        var removed = _prepared.TryRemove(id, out _);
        Interlocked.Increment(ref _abortCount);
        _decisions[id] = "aborted";
        PersistJournal();
        _logger.LogInformation("distributed transaction {TransactionId} aborted", id);
        return removed;
    }

    public object Metrics() => new { Prepare = Interlocked.Read(ref _prepareCount), Commit = Interlocked.Read(ref _commitCount), Abort = Interlocked.Read(ref _abortCount), InDoubt = Interlocked.Read(ref _inDoubtCount), Prepared = _prepared.Count, Active = _transactions.Count };

    public int CleanupExpired(TimeSpan maxAge)
    {
        var removed = 0;
        foreach (var item in _transactions) if (DateTimeOffset.UtcNow - item.Value.CreatedAt > maxAge && _transactions.TryRemove(item.Key, out _)) { AbortParticipant(item.Key); removed++; }
        if (removed > 0) PersistJournal();
        return removed;
    }

    public DistributedTransactionInfo? Status(Guid id)
    {
        if (_decisions.TryGetValue(id, out var decision)) return new DistributedTransactionInfo(id, decision, _prepared.TryGetValue(id, out var writes) ? writes.Count : 0);
        return _transactions.ContainsKey(id) ? Info(id, "active") : null;
    }

    public async Task<IReadOnlySet<string>?> ResolveParticipantUrlsAsync(IReadOnlyList<DistributedWrite> writes, CancellationToken cancellationToken)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in writes.Select(write => write.Table).Distinct(StringComparer.Ordinal))
        {
            var url = await FindLeaderAsync(table, cancellationToken);
            if (url is null) return null;
            urls.Add(url.TrimEnd('/').ToLowerInvariant());
        }
        return urls;
    }

    public async Task<DistributedTransactionInfo?> CommitAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_transactions.TryGetValue(id, out var buffer)) return null;
        var groups = buffer.Writes.GroupBy(write => write.Table, StringComparer.Ordinal).ToList();
        var participants = new List<(string Url, IReadOnlyList<DistributedWrite> Writes)>();
        foreach (var group in groups)
        {
            var url = await FindLeaderAsync(group.Key, cancellationToken);
            if (url is null) { await AbortPreparedAsync(participants, id, cancellationToken); return Info(id, "aborted"); }
            participants.Add((url, group.ToList()));
        }

        var prepared = new List<(string Url, IReadOnlyList<DistributedWrite> Writes)>();
        foreach (var participant in participants)
        {
            if (!await SendPrepareAsync(participant.Url, id, participant.Writes, cancellationToken))
            {
                await AbortPreparedAsync(prepared, id, cancellationToken);
                return Info(id, "aborted");
            }
            prepared.Add(participant);
        }

        _decisions[id] = "committing";
        PersistJournal();
        foreach (var participant in prepared)
        {
            if (!await SendDecisionAsync(participant.Url, "/commit", id, cancellationToken))
                Interlocked.Increment(ref _inDoubtCount);
                return Info(id, "in-doubt");
        }

        _transactions.TryRemove(id, out _);
        _decisions[id] = "committed";
        Interlocked.Increment(ref _commitCount);
        PersistJournal();
        _logger.LogInformation("distributed transaction {TransactionId} committed participants={Participants}", id, prepared.Count);
        return new DistributedTransactionInfo(id, "committed", buffer.Writes.Count);
    }

    public async Task<int> RecoverOutstandingAsync(CancellationToken cancellationToken = default)
    {
        var recovered = 0;
        foreach (var item in _prepared.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_decisions.TryGetValue(item.Key, out var decision) && decision == "committing" && await CommitParticipantAsync(item.Key)) recovered++;
        }
        return recovered;
    }

    public Task<DistributedTransactionInfo?> RecoverAsync(Guid id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("distributed transaction {TransactionId} recovery requested", id);
        return Task.FromResult(Status(id));
    }

    public bool Rollback(Guid id)
    {
        var removed = _transactions.TryRemove(id, out _);
        PersistJournal();
        return removed || AbortParticipant(id);
    }

    private DistributedTransactionInfo Info(Guid id, string status) =>
        new(id, status, _transactions.TryGetValue(id, out var b) ? b.Writes.Count : 0);

    private async Task<string?> FindLeaderAsync(string table, CancellationToken token)
    {
        var candidates = new[] { _options.PublicUrl }.Concat(_options.Peers.Select(peer => peer.Url)).Where(url => !string.IsNullOrWhiteSpace(url));
        foreach (var candidate in candidates)
        {
            try
            {
                var state = await _http.GetFromJsonAsync<TableStateResponse>(candidate!.TrimEnd('/') + $"/raft/tables/{Uri.EscapeDataString(table)}/state", token);
                if (state?.Role is 2 or "Leader") return state.LeaderUrl ?? candidate;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
        }
        return null;
    }

    private async Task<bool> SendPrepareAsync(string url, Guid id, IReadOnlyList<DistributedWrite> writes, CancellationToken token)
    {
        if (IsLocal(url)) return await PrepareParticipantAsync(new DistributedPrepareRequest(id, writes));
        using var response = await _http.PostAsJsonAsync(url.TrimEnd('/') + "/distributed-transactions/prepare", new DistributedPrepareRequest(id, writes), token);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> SendDecisionAsync(string url, string path, Guid id, CancellationToken token)
    {
        if (IsLocal(url)) return path == "/commit" ? await CommitParticipantAsync(id) : AbortParticipant(id);
        using var response = await _http.PostAsJsonAsync(url.TrimEnd('/') + "/distributed-transactions" + path, new DistributedDecisionRequest(id), token);
        return response.IsSuccessStatusCode;
    }

    private async Task AbortPreparedAsync(IEnumerable<(string Url, IReadOnlyList<DistributedWrite> Writes)> participants, Guid id, CancellationToken token)
    {
        foreach (var participant in participants) await SendDecisionAsync(participant.Url, "/abort", id, token);
    }

    private void LoadJournal()
    {
        try
        {
            if (!File.Exists(_journalPath)) return;
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath));
            if (journal is null) return;
            foreach (var item in journal.Prepared) _prepared[item.TransactionId] = item.Writes;
            foreach (var item in journal.Decisions) _decisions[item.TransactionId] = item.Status;
            foreach (var item in journal.Coordinators) _transactions[item.TransactionId] = new DistributedBuffer(item.CreatedAt, item.Writes);
        }
        catch (Exception ex) { _logger.LogError(ex, "could not load distributed transaction journal"); }
    }

    private void PersistJournal()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            var journal = new Journal(_prepared.Select(item => new PreparedJournal(item.Key, item.Value)).ToList(), _decisions.Select(item => new DecisionJournal(item.Key, item.Value)).ToList(), _transactions.Select(item => new CoordinatorJournal(item.Key, item.Value.CreatedAt, item.Value.Writes.ToList())).ToList());
            var temp = _journalPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(journal));
            File.Move(temp, _journalPath, true);
        }
        catch (Exception ex) { _logger.LogError(ex, "could not persist distributed transaction journal"); }
    }

    private sealed record Journal(IReadOnlyList<PreparedJournal> Prepared, IReadOnlyList<DecisionJournal> Decisions, IReadOnlyList<CoordinatorJournal> Coordinators);
    private sealed record CoordinatorJournal(Guid TransactionId, DateTimeOffset CreatedAt, IReadOnlyList<DistributedWrite> Writes);
    private sealed record PreparedJournal(Guid TransactionId, IReadOnlyList<DistributedWrite> Writes);
    private sealed record DecisionJournal(Guid TransactionId, string Status);

    private bool IsLocal(string url) => string.Equals(url.TrimEnd('/'), (_options.PublicUrl ?? string.Empty).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    private sealed class DistributedBuffer { public DistributedBuffer() { } public DistributedBuffer(DateTimeOffset createdAt, IReadOnlyList<DistributedWrite> writes) { CreatedAt = createdAt; Writes.AddRange(writes); } public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow; public List<DistributedWrite> Writes { get; } = []; }
    private sealed record TableStateResponse(string? LeaderUrl, object? Role);
}
