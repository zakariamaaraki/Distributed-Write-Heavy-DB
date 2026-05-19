using System.Text;
using System.Text.Json;
using LsmWriteDb.Storage;

namespace LsmWriteDb.Raft;

public sealed class RaftStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private readonly string _replicationPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public RaftStateStore(LsmStoreOptions options)
    {
        _statePath = Path.Combine(options.DataPath, "raft-state.json");
        _replicationPath = Path.Combine(options.DataPath, "raft-replication.json");
    }

    internal async Task<RaftPersistentState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        return await ReadAsync(_statePath, new RaftPersistentState(CurrentTerm: 0, VotedFor: null), cancellationToken);
    }

    internal async Task WriteStateAsync(RaftPersistentState state, CancellationToken cancellationToken = default)
    {
        await WriteAsync(_statePath, state, cancellationToken);
    }

    internal async Task<RaftReplicationPersistentState> ReadReplicationStateAsync(CancellationToken cancellationToken = default)
    {
        return await ReadAsync(
            _replicationPath,
            new RaftReplicationPersistentState(LastAppliedChangeSequence: 0),
            cancellationToken);
    }

    internal async Task WriteReplicationStateAsync(
        RaftReplicationPersistentState state,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(_replicationPath, state, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(string path, T defaultValue, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return defaultValue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
            return value ?? defaultValue;
        }
        catch (JsonException)
        {
            return defaultValue;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(value, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
