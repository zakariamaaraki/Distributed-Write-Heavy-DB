using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LsmWriteDb.Storage;

namespace LsmWriteDb.ChangeLogs;

public sealed class ChangeLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _changeLogPath;
    private readonly SemaphoreSlim _appendMutex = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<ChangeLogEntry>> _subscribers = new();

    public ChangeLogService(LsmStoreOptions options)
    {
        _changeLogPath = Path.Combine(options.DataPath, "changelog.log");
    }

    public async Task PublishAsync(IReadOnlyList<ChangeLogEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        List<ChangeLogEntry> newEntries;

        await _appendMutex.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_changeLogPath)!);
            var existingSequences = await ReadExistingSequencesAsync(cancellationToken);
            newEntries = entries
                .OrderBy(entry => entry.Sequence)
                .Where(entry => !existingSequences.Contains(entry.Sequence))
                .ToList();

            if (newEntries.Count == 0)
            {
                return;
            }

            await using var stream = new FileStream(
                _changeLogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);

            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var entry in newEntries)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions));
            }

            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _appendMutex.Release();
        }

        foreach (var entry in newEntries)
        {
            Broadcast(entry);
        }
    }

    public async Task<IReadOnlyList<ChangeLogEntry>> ReadAfterAsync(
        long fromSequence,
        int limit = 1_000,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_changeLogPath))
        {
            return [];
        }

        var boundedLimit = Math.Clamp(limit, 1, 10_000);
        var entries = new List<ChangeLogEntry>();

        using var stream = new FileStream(_changeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (entries.Count < boundedLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = TryParse(line);
            if (entry is not null && entry.Sequence > fromSequence)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public async IAsyncEnumerable<ChangeLogEntry> StreamAsync(
        long fromSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<ChangeLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers[subscriberId] = channel;

        try
        {
            var lastSentSequence = fromSequence;
            foreach (var entry in await ReadAfterAsync(fromSequence, cancellationToken: cancellationToken))
            {
                yield return entry;
                lastSentSequence = entry.Sequence;
            }

            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (entry.Sequence <= lastSentSequence)
                {
                    continue;
                }

                yield return entry;
                lastSentSequence = entry.Sequence;
            }
        }
        finally
        {
            _subscribers.TryRemove(subscriberId, out _);
            channel.Writer.TryComplete();
        }
    }

    private void Broadcast(ChangeLogEntry entry)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(entry);
        }
    }

    private static ChangeLogEntry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ChangeLogEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HashSet<long>> ReadExistingSequencesAsync(CancellationToken cancellationToken)
    {
        var sequences = new HashSet<long>();
        if (!File.Exists(_changeLogPath))
        {
            return sequences;
        }

        using var stream = new FileStream(_changeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = TryParse(line);
            if (entry is not null)
            {
                sequences.Add(entry.Sequence);
            }
        }

        return sequences;
    }
}
