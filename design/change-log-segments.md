# Segmented Change Log Design

## Goals

The committed change log is a global, ordered stream used for follower catch-up and external subscriptions. It must remain append-only and durable without allowing one ever-growing file to become difficult to copy, scan, or recover.

The design therefore stores the log as bounded numbered segments:

```text
data/
+-- changelog.log                         # legacy segment, read-compatible
+-- changelog-00000000000000000001.log   # immutable older segment
+-- changelog-00000000000000000002.log   # immutable older segment
+-- changelog-00000000000000000003.log   # active append segment
```

The default segment limit is 64 MiB and is configurable with `Lsm:ChangeLogSegmentMaxBytes` or `Lsm__ChangeLogSegmentMaxBytes`.

## Relationship to SSTable runs

The change log is split into bounded files for the same operational reason that
sorted flushes and compactions are split into bounded SSTable files. An SSTable
run has block-level read structures; a change-log segment is an append-only
JSONL stream and does not contain blocks, sparse indexes, or Bloom filters.

```text
SSTable run
+-- sstable-001
|   +-- block 1
|   +-- block 2
|   +-- sparse index + Bloom filter
+-- sstable-002
    +-- block 3
    +-- block 4
    +-- sparse index + Bloom filter
```

## Write path

1. A committed storage operation receives its global sequence number and is published to `ChangeLogService`.
2. The service serializes each event as one UTF-8 JSON line.
3. The active numbered segment is opened in append mode with write-through enabled.
4. If the next event would exceed the configured segment limit, the service starts the next zero-padded segment before writing it.
5. The event is broadcast to live SSE subscribers only after it has been written and flushed.

An individual event is never split between files. A single event larger than the configured limit is allowed to occupy one segment so it remains readable and atomic.

## Read and replay path

Readers enumerate `changelog-*.log` in numeric order. A legacy `changelog.log`, when present, is read first for backward compatibility. Each file is read linearly, malformed lines are ignored as before, and events are filtered by global sequence.

`GET /changes?fromSequence=N` and follower bootstrap therefore replay across segment boundaries without needing a single monolithic file. The SSE endpoint uses the same replay path, then switches to the in-memory subscriber channel for new events.

The implementation deliberately keeps the global sequence as the ordering authority. Segment names identify physical order; they are not a second sequence source and are never used to infer missing events.

## Recovery and compatibility

- Existing `changelog.log` files remain readable after upgrade.
- New writes continue an existing legacy file until it reaches the configured limit, then create `changelog-00000000000000000001.log`.
- Once numbered segments exist, new writes append only to the newest numbered segment.
- Rotation happens while the append mutex is held, so concurrent publishers cannot create duplicate segment numbers or interleave lines.
- Segment files are append-only. A future compaction/archive process can delete old segments only after every consumer has advanced beyond their final sequence.

## Operational trade-offs

Segmenting bounds the largest file and makes backup, transfer, and retention manageable. Replay still preserves correctness by walking segments in order; it is not intended to replace a sparse sequence index. A future optimization can add a per-segment sequence manifest without changing the on-disk event format or stream protocol.
