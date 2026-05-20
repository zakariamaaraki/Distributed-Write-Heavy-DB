using System.Text.Json;
using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Raft;

public sealed class RaftChangeLogReplicationService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RaftOptions _options;
    private readonly RaftNode _node;
    private readonly DatabaseEngine _database;
    private readonly HttpClient _httpClient;

    public RaftChangeLogReplicationService(
        RaftOptions options,
        RaftNode node,
        DatabaseEngine database,
        HttpClient httpClient)
    {
        _options = options;
        _node = node;
        _database = database;
        _httpClient = httpClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_options.Enabled || _options.IsSingleNode)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var status = _node.GetStatus();
            if (status.Role != RaftRole.Follower || string.IsNullOrWhiteSpace(status.LeaderUrl))
            {
                await Task.Delay(_options.ReplicationReconnectDelayMilliseconds, stoppingToken);
                continue;
            }

            await ReplicateFromLeaderAsync(status.LeaderUrl, status.LastAppliedChangeSequence, stoppingToken);
            await Task.Delay(_options.ReplicationReconnectDelayMilliseconds, stoppingToken);
        }
    }

    private async Task ReplicateFromLeaderAsync(
        string leaderUrl,
        long fromSequence,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{TrimUrl(leaderUrl)}/changes/stream?fromSequence={fromSequence}");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_node.GetStatus().Role != RaftRole.Follower)
                {
                    return;
                }

                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var json = line["data:".Length..].Trim();
                var entry = JsonSerializer.Deserialize<ChangeLogEntry>(json, JsonOptions);
                if (entry is null)
                {
                    continue;
                }

                await _database.ApplyReplicatedChangeAsync(entry);
                await _node.RecordAppliedChangeAsync(entry.Sequence, cancellationToken);
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string TrimUrl(string url)
    {
        return url.TrimEnd('/');
    }
}
