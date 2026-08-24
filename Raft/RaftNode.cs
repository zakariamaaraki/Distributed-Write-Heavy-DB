using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace LsmWriteDb.Raft;

public sealed class RaftNode
{
    private RaftOptions _options;
    private readonly RaftStateStore _stateStore;
    private readonly HttpClient _httpClient;
    private readonly object _mutex = new();
    private readonly Random _random = new();
    private readonly string? _table;
    private readonly ILogger<RaftNode>? _logger;

    private RaftRole _role = RaftRole.Follower;
    private long _currentTerm;
    private string? _votedFor;
    private string? _leaderId;
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.UtcNow;
    private bool _started;
    private long _lastAppliedChangeSequence;

    public RaftNode(RaftOptions options, RaftStateStore stateStore, HttpClient httpClient, string? table = null, ILogger<RaftNode>? logger = null)
    {
        _options = options;
        _stateStore = stateStore;
        _httpClient = httpClient;
        _table = string.IsNullOrWhiteSpace(table) ? null : Storage.TableNames.Normalize(table);
        _logger = logger;
    }

    public void UpdatePeers(IReadOnlyList<RaftPeerOptions> peers)
    {
        lock (_mutex)
        {
            _options = new RaftOptions
            {
                Enabled = _options.Enabled,
                NodeId = _options.NodeId,
                PublicUrl = _options.PublicUrl,
                ElectionTimeoutMinMilliseconds = _options.ElectionTimeoutMinMilliseconds,
                ElectionTimeoutMaxMilliseconds = _options.ElectionTimeoutMaxMilliseconds,
                HeartbeatIntervalMilliseconds = _options.HeartbeatIntervalMilliseconds,
                ReplicationReconnectDelayMilliseconds = _options.ReplicationReconnectDelayMilliseconds,
                LeaderElectionReadyTimeoutMilliseconds = _options.LeaderElectionReadyTimeoutMilliseconds,
                Peers = peers.ToList()
            };
        }
    }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_mutex)
        {
            if (_started)
                return;
        }

        if (!_options.Enabled)
        {
            lock (_mutex)
            {
                _role = RaftRole.Leader;
                _leaderId = _options.NodeId;
                _started = true;
            }

            return;
        }

        var state = await _stateStore.ReadStateAsync(cancellationToken);
        var replicationState = await _stateStore.ReadReplicationStateAsync(cancellationToken);

        lock (_mutex)
        {
            _currentTerm = state.CurrentTerm;
            _votedFor = state.VotedFor;
            _lastAppliedChangeSequence = replicationState.LastAppliedChangeSequence;
            _lastHeartbeatAt = DateTimeOffset.UtcNow;
            _role = _options.IsSingleNode ? RaftRole.Leader : RaftRole.Follower;
            _leaderId = _role == RaftRole.Leader ? _options.NodeId : null;
            _started = true;
        }
    }

    public RaftNodeStatus GetStatus()
    {
        lock (_mutex)
        {
            return new RaftNodeStatus(
                _options.NodeId,
                _options.Enabled,
                _role,
                _currentTerm,
                _votedFor,
                _leaderId,
                GetLeaderUrlCore(),
                _options.ClusterSize,
                _options.Majority,
                _lastAppliedChangeSequence);
        }
    }

    public bool IsLeader
    {
        get
        {
            lock (_mutex)
            {
                return !_options.Enabled || _role == RaftRole.Leader;
            }
        }
    }

    public string? LeaderUrl
    {
        get
        {
            lock (_mutex)
            {
                return GetLeaderUrlCore();
            }
        }
    }

    public long LastAppliedChangeSequence
    {
        get
        {
            lock (_mutex)
            {
                return _lastAppliedChangeSequence;
            }
        }
    }

    public async Task RecordAppliedChangeAsync(long sequence, CancellationToken cancellationToken = default)
    {
        var shouldPersist = false;
        lock (_mutex)
        {
            if (sequence > _lastAppliedChangeSequence)
            {
                _lastAppliedChangeSequence = sequence;
                shouldPersist = true;
            }
        }

        if (shouldPersist)
        {
            await _stateStore.WriteReplicationStateAsync(
                new RaftReplicationPersistentState(sequence),
                cancellationToken);
        }
    }

    public async Task<RaftRequestVoteResponse> RequestVoteAsync(
        RaftRequestVoteRequest request,
        CancellationToken cancellationToken = default)
    {
        RaftPersistentState? stateToPersist = null;
        RaftRequestVoteResponse response;

        lock (_mutex)
        {
            if (request.Term < _currentTerm)
            {
                return new RaftRequestVoteResponse(_currentTerm, VoteGranted: false);
            }

            if (request.Term > _currentTerm)
            {
                BecomeFollowerCore(request.Term, leaderId: null);
                stateToPersist = new RaftPersistentState(_currentTerm, _votedFor);
            }

            var canVote = _votedFor is null || string.Equals(_votedFor, request.CandidateId, StringComparison.Ordinal);
            if (canVote)
            {
                _votedFor = request.CandidateId;
                _lastHeartbeatAt = DateTimeOffset.UtcNow;
                stateToPersist = new RaftPersistentState(_currentTerm, _votedFor);
            }

            response = new RaftRequestVoteResponse(_currentTerm, canVote);
        }

        if (stateToPersist is not null)
        {
            await _stateStore.WriteStateAsync(stateToPersist, cancellationToken);
        }

        return response;
    }

    public async Task<RaftAppendEntriesResponse> AppendEntriesAsync(
        RaftAppendEntriesRequest request,
        CancellationToken cancellationToken = default)
    {
        RaftPersistentState? stateToPersist = null;
        RaftAppendEntriesResponse response;

        lock (_mutex)
        {
            if (request.Term < _currentTerm)
            {
                return new RaftAppendEntriesResponse(_currentTerm, Success: false);
            }

            if (request.Term > _currentTerm || _role != RaftRole.Follower)
            {
                BecomeFollowerCore(request.Term, request.LeaderId);
                stateToPersist = new RaftPersistentState(_currentTerm, _votedFor);
            }
            else
            {
                _leaderId = request.LeaderId;
                _lastHeartbeatAt = DateTimeOffset.UtcNow;
            }

            response = new RaftAppendEntriesResponse(_currentTerm, Success: true);
        }

        if (stateToPersist is not null)
        {
            await _stateStore.WriteStateAsync(stateToPersist, cancellationToken);
        }

        return response;
    }

    public async Task RunElectionLoopAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_options.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            if (_options.IsSingleNode)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            if (GetStatus().Role == RaftRole.Leader)
            {
                await SendHeartbeatsAsync(cancellationToken);
                await Task.Delay(_options.HeartbeatIntervalMilliseconds, cancellationToken);
                continue;
            }

            var timeout = NextElectionTimeout();
            await Task.Delay(timeout, cancellationToken);

            if (ShouldStartElection(timeout))
            {
                await StartElectionAsync(cancellationToken);
            }
        }
    }

    private async Task StartElectionAsync(CancellationToken cancellationToken)
    {
        long term;
        lock (_mutex)
        {
            _role = RaftRole.Candidate;
            _currentTerm++;
            _votedFor = _options.NodeId;
            _leaderId = null;
            _lastHeartbeatAt = DateTimeOffset.UtcNow;
            term = _currentTerm;
        }

        await _stateStore.WriteStateAsync(new RaftPersistentState(term, _options.NodeId), cancellationToken);

        var votes = 1;
        _logger?.LogInformation("raft election started table={Table} node={NodeId} term={Term} majority={Majority}", _table ?? "global", _options.NodeId, term, _options.Majority);
        foreach (var peer in _options.Peers)
        {
            if (await RequestPeerVoteAsync(peer, term, cancellationToken))
            {
                votes++;
            }

            if (votes >= _options.Majority)
            {
                BecomeLeader(term);
                return;
            }
        }

        lock (_mutex)
        {
            if (_currentTerm == term && _role == RaftRole.Candidate)
            {
                _role = RaftRole.Follower;
                _lastHeartbeatAt = DateTimeOffset.UtcNow;
            }
        }
        _logger?.LogWarning("raft election lost table={Table} node={NodeId} term={Term} votes={Votes} majority={Majority}", _table ?? "global", _options.NodeId, term, votes, _options.Majority);
    }

    private async Task<bool> RequestPeerVoteAsync(RaftPeerOptions peer, long term, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"{TrimUrl(peer.Url)}{RaftPath("request-vote")}",
                new RaftRequestVoteRequest(term, _options.NodeId),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var vote = await response.Content.ReadFromJsonAsync<RaftRequestVoteResponse>(cancellationToken);
            if (vote is null)
            {
                return false;
            }

            if (vote.Term > term)
            {
                await StepDownAsync(vote.Term, leaderId: null, cancellationToken);
                return false;
            }

            return vote.VoteGranted;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
    {
        var status = GetStatus();
        foreach (var peer in _options.Peers)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"{TrimUrl(peer.Url)}{RaftPath("append-entries")}",
                    new RaftAppendEntriesRequest(status.CurrentTerm, _options.NodeId),
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var heartbeat = await response.Content.ReadFromJsonAsync<RaftAppendEntriesResponse>(cancellationToken);
                if (heartbeat is not null && heartbeat.Term > status.CurrentTerm)
                {
                    await StepDownAsync(heartbeat.Term, leaderId: null, cancellationToken);
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private bool ShouldStartElection(TimeSpan timeout)
    {
        lock (_mutex)
        {
            return _started
                && _role != RaftRole.Leader
                && DateTimeOffset.UtcNow - _lastHeartbeatAt >= timeout;
        }
    }

    private void BecomeLeader(long term)
    {
        lock (_mutex)
        {
            if (_currentTerm != term || _role != RaftRole.Candidate)
            {
                return;
            }

            _role = RaftRole.Leader;
            _leaderId = _options.NodeId;
            _lastHeartbeatAt = DateTimeOffset.UtcNow;
        }
        _logger?.LogInformation("raft leader elected table={Table} node={NodeId} term={Term}", _table ?? "global", _options.NodeId, term);
    }

    private async Task StepDownAsync(long term, string? leaderId, CancellationToken cancellationToken)
    {
        lock (_mutex)
        {
            BecomeFollowerCore(term, leaderId);
        }

        await _stateStore.WriteStateAsync(new RaftPersistentState(term, VotedFor: null), cancellationToken);
    }

    private void BecomeFollowerCore(long term, string? leaderId)
    {
        _role = RaftRole.Follower;
        _currentTerm = term;
        _votedFor = null;
        _leaderId = leaderId;
        _lastHeartbeatAt = DateTimeOffset.UtcNow;
    }

    private TimeSpan NextElectionTimeout()
    {
        var min = Math.Max(250, _options.ElectionTimeoutMinMilliseconds);
        var max = Math.Max(min + 1, _options.ElectionTimeoutMaxMilliseconds);

        lock (_mutex)
        {
            return TimeSpan.FromMilliseconds(_random.Next(min, max));
        }
    }

    private string? GetLeaderUrlCore()
    {
        if (_leaderId is null)
        {
            return null;
        }

        if (string.Equals(_leaderId, _options.NodeId, StringComparison.Ordinal))
        {
            return _options.PublicUrl;
        }

        return _options.Peers.FirstOrDefault(peer => string.Equals(peer.NodeId, _leaderId, StringComparison.Ordinal))?.Url;
    }

    private string RaftPath(string operation)
    {
        return _table is null ? $"/raft/{operation}" : $"/raft/tables/{_table}/{operation}";
    }

    private static string TrimUrl(string url)
    {
        return url.TrimEnd('/');
    }
}
