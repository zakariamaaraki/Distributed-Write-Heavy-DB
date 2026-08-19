using System.Collections.Concurrent;
using System.Text.Json;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Raft;

public sealed class TableRaftCoordinator
{
    private readonly RaftOptions _options;
    private readonly LsmStoreOptions _storageOptions;
    private readonly HttpClient _httpClient;
    private readonly DatabaseEngine _database;
    private readonly ConcurrentDictionary<string, RaftNode> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RaftPeerOptions> _members = new(StringComparer.Ordinal);

    public TableRaftCoordinator(RaftOptions options, LsmStoreOptions storageOptions, HttpClient httpClient, DatabaseEngine database)
    {
        _options = options;
        _storageOptions = storageOptions;
        _httpClient = httpClient;
        _database = database;
        foreach (var peer in options.Peers)
            _members[peer.NodeId] = peer;
    }

    public void RegisterPeer(RaftPeerOptions peer)
    {
        if (string.IsNullOrWhiteSpace(peer.NodeId) || string.IsNullOrWhiteSpace(peer.Url))
            throw new ArgumentException("A peer requires nodeId and url.");
        _members[peer.NodeId] = peer;
        var peers = _members.Values.Where(candidate => candidate.NodeId != _options.NodeId).ToList();
        foreach (var node in _nodes.Values)
            node.UpdatePeers(peers);
    }

    public IReadOnlyList<RaftPeerOptions> Members()
        => _members.Values.OrderBy(peer => peer.NodeId, StringComparer.Ordinal).ToList();
    public async Task EnsureTableAsync(string table, CancellationToken cancellationToken = default)
    {
        var normalized = TableNames.Normalize(table);
        if (!_options.Enabled)
            return;

        var node = _nodes.GetOrAdd(normalized, CreateNode);
        await node.InitializeAsync(cancellationToken);
        _ = _loops.GetOrAdd(normalized, _ => node.RunElectionLoopAsync(cancellationToken));
        if (!TableNames.IsInternal(normalized))
            await PersistOwnershipAsync(normalized, node.GetStatus(), cancellationToken);
    }

    private async Task PersistOwnershipAsync(string table, RaftNodeStatus status, CancellationToken cancellationToken)
    {
        if (status.Role != RaftRole.Leader || string.IsNullOrWhiteSpace(status.LeaderUrl))
            return;

        var members = _members.Values.Select(peer => peer.NodeId).Append(_options.NodeId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var record = new TableOwnershipRecord(table, status.CurrentTerm, status.LeaderId ?? _options.NodeId,
            status.LeaderUrl, members, $"term-{status.CurrentTerm}", DateTimeOffset.UtcNow);
        await _database.PutAsync(TableNames.Ownership, table, JsonSerializer.Serialize(record));
    }
    public async Task<IReadOnlyList<TableOwnershipRecord>> RebalanceAsync(CancellationToken cancellationToken = default)
    {
        var tables = await _database.ListAllTablesAsync(cancellationToken);
        var healthy = new List<RaftPeerOptions>
        {
            new() { NodeId = _options.NodeId, Url = _options.PublicUrl ?? string.Empty }
        };
        foreach (var peer in _members.Values)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await _httpClient.GetAsync(peer.Url.TrimEnd('/') + "/", timeout.Token);
                if (response.IsSuccessStatusCode)
                    healthy.Add(peer);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        var healthyIds = healthy.Select(peer => peer.NodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var member in _members.Keys)
        {
            if (member != _options.NodeId && !healthyIds.Contains(member))
                _members.TryRemove(member, out _);
        }
        var currentPeers = _members.Values.Where(peer => peer.NodeId != _options.NodeId).ToList();
        foreach (var node in _nodes.Values)
            node.UpdatePeers(currentPeers);
        var names = tables.Select(table => table.Name).ToList();
        var planned = TableOwnershipPlanner.Rebalance(names, healthy, Math.Max(1, Math.Min(healthy.Count, _options.ClusterSize)));
        if (IsLeader(TableNames.Ownership))
        {
            foreach (var record in planned)
                await _database.PutAsync(TableNames.Ownership, record.Table, JsonSerializer.Serialize(record));
        }
        return planned;
    }
    public bool IsLeader(string table)
    {
        return !_options.Enabled || GetNode(table).IsLeader;
    }

    public RaftNodeStatus GetStatus(string table)
    {
        return GetNode(table).GetStatus();
    }

    public string? GetLeaderUrl(string table)
    {
        return GetNode(table).LeaderUrl;
    }

    public long LastAppliedSequence(string table) => GetNode(table).LastAppliedChangeSequence;

    public Task RecordAppliedChangeAsync(string table, long sequence, CancellationToken cancellationToken = default)
        => GetNode(table).RecordAppliedChangeAsync(sequence, cancellationToken);
    public void EnsureLeader(string table)
    {
        var status = GetStatus(table);
        if (_options.Enabled && status.Role != RaftRole.Leader)
            throw new TableWriteRejectedException(TableNames.Normalize(table), status.LeaderId, status.LeaderUrl);
    }

    public Task<RaftRequestVoteResponse> RequestVoteAsync(string table, RaftRequestVoteRequest request, CancellationToken cancellationToken = default)
    {
        return GetOrCreateNode(table).RequestVoteAsync(request, cancellationToken);
    }

    public Task<RaftAppendEntriesResponse> AppendEntriesAsync(string table, RaftAppendEntriesRequest request, CancellationToken cancellationToken = default)
    {
        return GetOrCreateNode(table).AppendEntriesAsync(request, cancellationToken);
    }

    private RaftNode GetNode(string table)
    {
        return GetOrCreateNode(table);
    }

    private RaftNode GetOrCreateNode(string table)
    {
        var normalized = TableNames.Normalize(table);
        var node = _nodes.GetOrAdd(normalized, CreateNode);
        _ = _loops.GetOrAdd(normalized, key => node.RunElectionLoopAsync(CancellationToken.None));
        return node;
    }

    private RaftNode CreateNode(string table)
    {
        var statePath = Path.Combine(_storageOptions.DataPath, "raft", "tables", table);
        var stateOptions = _storageOptions with { DataPath = statePath, TableName = table };
        var tableOptions = new RaftOptions
        {
            Enabled = _options.Enabled,
            NodeId = _options.NodeId,
            PublicUrl = _options.PublicUrl,
            ElectionTimeoutMinMilliseconds = _options.ElectionTimeoutMinMilliseconds,
            ElectionTimeoutMaxMilliseconds = _options.ElectionTimeoutMaxMilliseconds,
            HeartbeatIntervalMilliseconds = _options.HeartbeatIntervalMilliseconds,
            ReplicationReconnectDelayMilliseconds = _options.ReplicationReconnectDelayMilliseconds,
            Peers = _members.Values.Where(peer => peer.NodeId != _options.NodeId).ToList()
        };
        return new RaftNode(tableOptions, new RaftStateStore(stateOptions), _httpClient, table);
    }
}

public sealed class TableWriteRejectedException : Exception
{
    public TableWriteRejectedException(string table, string? leaderId, string? leaderUrl)
        : base($"writes for table '{table}' are accepted only by its table leader")
    {
        Table = table;
        LeaderId = leaderId;
        LeaderUrl = leaderUrl;
    }

    public string Table { get; }
    public string? LeaderId { get; }
    public string? LeaderUrl { get; }
}
