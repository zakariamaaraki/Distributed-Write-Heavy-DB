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
    private const string LegacyFileName = "changelog.log";
    private const string SegmentPrefix = "changelog-";
    private const string SegmentSuffix = ".log";

    private readonly string _dataPath;
    private readonly string _legacyPath;
    private readonly long _segmentMaxBytes;
    private readonly SemaphoreSlim _appendMutex = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<ChangeLogEntry>> _subscribers = new();

    public ChangeLogService(LsmStoreOptions options)
    {
        _dataPath = options.DataPath;
        _legacyPath = Path.Combine(_dataPath, LegacyFileName);
        _segmentMaxBytes = Math.Max(1, options.ChangeLogSegmentMaxBytes);
    }

    public async Task PublishAsync(IReadOnlyList<ChangeLogEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            return;

        List<ChangeLogEntry> newEntries;
        await _appendMutex.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_dataPath);
            var existingSequences = await ReadExistingSequencesAsync(cancellationToken);
            newEntries = entries
                .OrderBy(entry => entry.Sequence)
                .Where(entry => !existingSequences.Contains(entry.Sequence))
                .ToList();

            foreach (var entry in newEntries)
                await AppendEntryAsync(entry, cancellationToken);
        }
        finally
        {
            _appendMutex.Release();
        }

        foreach (var entry in newEntries)
            Broadcast(entry);
    }

    public async Task<IReadOnlyList<ChangeLogEntry>> ReadAfterAsync(
        long fromSequence,
        int limit = 1_000,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 10_000);
        var entries = new List<ChangeLogEntry>();

        foreach (var path in GetLogPaths())
        {
            await foreach (var entry in ReadFileAsync(path, cancellationToken))
            {
                if (entry.Sequence > fromSequence)
                    entries.Add(entry);
                if (entries.Count >= boundedLimit)
                    return entries;
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
                    continue;
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

    private async Task AppendEntryAsync(ChangeLogEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        var path = GetWritablePath(bytes.Length);

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private string GetWritablePath(int entryBytes)
    {
        var segments = GetLogPaths().Where(path => !string.Equals(path, _legacyPath, StringComparison.OrdinalIgnoreCase)).ToList();
        var current = segments.LastOrDefault();
        if (current is null)
        {
            if (File.Exists(_legacyPath) && new FileInfo(_legacyPath).Length + entryBytes <= _segmentMaxBytes)
                return _legacyPath;
            return SegmentPath(1);
        }

        if (new FileInfo(current).Length > 0 && new FileInfo(current).Length + entryBytes > _segmentMaxBytes)
            return SegmentPath(GetSegmentNumber(current) + 1);
        return current;
    }

    private IReadOnlyList<string> GetLogPaths()
    {
        if (!Directory.Exists(_dataPath))
            return [];

        var paths = Directory.GetFiles(_dataPath, SegmentPrefix + "*" + SegmentSuffix)
            .Where(path => GetSegmentNumber(path) > 0)
            .OrderBy(GetSegmentNumber)
            .ToList();
        if (File.Exists(_legacyPath))
            paths.Insert(0, _legacyPath);
        return paths;
    }

    private async IAsyncEnumerable<ChangeLogEntry> ReadFileAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;
            if (!string.IsNullOrWhiteSpace(line) && TryParse(line) is { } entry)
                yield return entry;
        }
    }

    private async Task<HashSet<long>> ReadExistingSequencesAsync(CancellationToken cancellationToken)
    {
        var sequences = new HashSet<long>();
        foreach (var path in GetLogPaths())
        {
            await foreach (var entry in ReadFileAsync(path, cancellationToken))
                sequences.Add(entry.Sequence);
        }
        return sequences;
    }

    private string SegmentPath(long number) => Path.Combine(_dataPath, $"{SegmentPrefix}{number:D20}{SegmentSuffix}");

    private static long GetSegmentNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var number = name.StartsWith(SegmentPrefix, StringComparison.Ordinal)
            ? name[SegmentPrefix.Length..]
            : string.Empty;
        return long.TryParse(number, out var result) ? result : 0;
    }

    private void Broadcast(ChangeLogEntry entry)
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.Writer.TryWrite(entry);
    }

    private static ChangeLogEntry? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<ChangeLogEntry>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
