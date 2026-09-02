# Tiered SSTable Compaction

## Problem

A table must not be reduced to one ever-growing SSTable after every flush.
Large tables need multiple immutable files so writes remain bounded, reads can
use Bloom filters and sparse indexes, and compaction work can be distributed
across manageable runs.

## Design goals

- Keep flushes append-oriented and create immutable level-0 runs.
- Keep each physical SSTable below the configured file-size target when records
  can be split between blocks.
- Compact groups of logical runs rather than treating every physical split file
  as an independent run.
- Promote compacted output to older tiers while retaining multiple SSTables.
- Preserve read correctness across overlapping tiers and restart recovery.
- Never expose a partially written output run as live data.

## Terminology

A **logical run** is one sorted output of a flush or compaction. A large logical
run may contain several physical SSTable files because of the maximum file-size
limit. Every physical file in the run shares a run identifier.

A **tier** is an age/compaction generation. New writes enter tier 0. When a tier
contains four logical runs, those runs are merged and emitted as one logical run
in the next tier. The output can still contain multiple physical SSTables.

```text
memtable flush
     |
     v
Tier 0:  R1=[sstable-1, sstable-2]  R2=[sstable-3]  R3=[sstable-4]  R4=[sstable-5]
                                      | four logical runs
                                      v
Tier 1:  R5=[sstable-6, sstable-7]
                                      | after four tier-1 runs
                                      v
Tier 2:  R6=[sstable-8, sstable-9]
```

The exact physical file count depends on record and block sizes. The compaction
trigger counts logical run IDs, not physical files.

## File format and compatibility

New files use a tier and run-aware name:

```text
sstable-L00-R<run-id>-<timestamp>-<file-id>.json
sstable-L00-R<run-id>-<timestamp>-<file-id>.bloom.json
sstable-L00-R<run-id>-<timestamp>-<file-id>.index.json
```

Legacy `sstable-*.json` files without tier/run markers remain readable as
independent tier-0 runs. No migration is required.

Each physical data file remains an immutable sequence of JSON blocks with its
Bloom filter and sparse-index sidecars. A run is published only after every
output data file and sidecar has been completed.

## Flush and promotion algorithm

1. A full memtable is sorted and written as one new tier-0 logical run.
2. The memtable is cleared and its WAL can be cleared after the run is durable.
3. Compaction scans tiers from newest to oldest.
4. The first tier with at least four logical runs is selected.
5. All physical files belonging to those runs are read and merged by key.
6. The newest sequence wins for duplicate keys.
7. The newest tombstone is retained; it must not be dropped while older tiers
   may still contain the deleted value.
8. The merged records are written as one logical run in the next tier.
9. Only after output publication succeeds are the input files and sidecars
   deleted.
10. At most one tier is promoted per flush invocation when a tier is eligible;
    this bounds foreground compaction work and avoids repeated same-size
    promotions. A later flush can continue promotion.

The current trigger is four logical runs. It is intentionally a simple tiered
policy; size-based scoring, overlap selection, and background scheduling can be
added without changing the on-disk read contract.

## Read path

Reads continue to inspect the memtable and all SSTable files across all tiers.
Candidate selection uses sparse-index ranges and Bloom filters. Point and range
reads merge records by key and retain the highest sequence. Tombstones suppress
older values. The tier is a compaction-placement hint, not a visibility rule.

## Crash safety

Output files are written through temporary files and atomically renamed. If a
write fails, newly created output files are removed and input runs remain. If a
process fails after output publication but before input deletion, duplicate
versions are safe because reads select the highest sequence; restart can safely
recompact the remaining runs.

## Trade-offs and future work

Tiered compaction reduces write amplification compared with rewriting the whole
table after every flush, at the cost of temporarily higher read amplification
while several tiers coexist. The current implementation preserves tombstones
conservatively. A future garbage-collection pass can remove a tombstone only
when it is provably older than every retained version in every lower/older tier.

Future improvements may add configurable tier fan-in, size-based triggers,
background compaction, overlap-aware leveled tiers, and a manifest for atomic
run publication. The current filename grouping is deliberately lightweight and
keeps existing SSTable files backward compatible.