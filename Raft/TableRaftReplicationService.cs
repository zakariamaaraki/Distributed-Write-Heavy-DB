using System.Collections.Concurrent;
using System.Text.Json;
using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Raft;

public sealed class TableRaftReplicationService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DatabaseEngine _database;
    private readonly TableRaftCoordinator _coordinator;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Task> _replications = new(StringComparer.Ordinal);

    public TableRaftReplicationService(DatabaseEngine database, TableRaftCoordinator coordinator, HttpClient httpClient)
    {
        _database = database;
        _coordinator = coordinator;
        _httpClient = httpClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var table in await _database.ListAllTablesAsync(stoppingToken))
                _ = _replications.GetOrAdd(table.Name, name => RunReplicationAsync(name, stoppingToken));
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task RunReplicationAsync(string table, CancellationToken cancellationToken)
    {
        try
        {
            await ReplicateTableAsync(table, cancellationToken);
        }
        finally
        {
            // Allow the next maintenance pass to retry after an election race,
            // a disconnected stream, or a transient peer failure.
            _replications.TryRemove(table, out _);
        }
    }

    private async Task ReplicateTableAsync(string table, CancellationToken cancellationToken)
    {
        try
        {
            var status = _coordinator.GetStatus(table);
            if (status.Role != RaftRole.Follower || string.IsNullOrWhiteSpace(status.LeaderUrl))
                return;

            var fromSequence = _coordinator.LastAppliedSequence(table);
            if (fromSequence == 0)
            {
                using var snapshotResponse = await _httpClient.GetAsync(
                    $"{status.LeaderUrl.TrimEnd('/')}/tables/{table}/snapshot",
                    cancellationToken);
                if (snapshotResponse.IsSuccessStatusCode)
                {
                    var snapshot = await snapshotResponse.Content.ReadFromJsonAsync<TableSnapshot>(JsonOptions, cancellationToken);
                    if (snapshot is not null)
                    {
                        var operations = snapshot.Rows.Select(row => StoreWriteOperation.Put(table, row.Key, row.Value)).ToList();
                        if (operations.Count > 0)
                            await _database.ApplyBatchAsync(operations);
                        await _coordinator.RecordAppliedChangeAsync(table, snapshot.Sequence, cancellationToken);
                        fromSequence = snapshot.Sequence;
                    }
                }
            }

            using var response = await _httpClient.GetAsync(
                $"{status.LeaderUrl.TrimEnd('/')}/changes/stream?fromSequence={fromSequence}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    return;
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var entry = JsonSerializer.Deserialize<ChangeLogEntry>(line["data:".Length..].Trim(), JsonOptions);
                if (entry is null)
                    continue;
                if (string.Equals(entry.Table, table, StringComparison.Ordinal))
                    await _database.ApplyReplicatedChangeAsync(entry);
                await _coordinator.RecordAppliedChangeAsync(table, entry.Sequence, cancellationToken);
            }
        }
        catch (HttpRequestException) { }
        catch (JsonException) { }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }
}
