# Per-Table Leadership and Replica Rebalancing

## Status

Design and implementation plan. Distributed transactions are explicitly out of
scope for this version.

## Goals

- Allow each user table to have one elected leader and multiple followers.
- Allow different tables to have different leaders so write load is distributed.
- Replicate each table's committed changes to that table's followers.
- Reassign table ownership when a node joins, leaves, or becomes unavailable.
- Persist ownership metadata in an internal replicated table.
- Place a newly created table on the node with the fewest user tables.
- Replicate table-catalog creation to every configured peer before writes begin.
- Keep the internal ownership table itself under the same leader/follower model.
- Route writes to the table leader and return its URL when a request reaches a
  follower.

## Non-goals


- No atomic multi-table writes.
- No cross-table read snapshot guarantee.
- No claim that ownership movement is complete until the destination has caught
  up with the source table's change sequence.

## Terminology

- `__table_ownership`: internal table containing the authoritative assignment
  for every table.
- Ownership group: the nodes assigned to one table, consisting of one leader and
  zero or more followers.
- Table term: the election term for one table. It is independent of every other
  table's term.
- Table sequence: the change-log sequence through which a replica has applied a
  table's changes.

## Ownership records

The internal table stores one JSON record per logical table, keyed by table name:

```json
{
  "table": "users",
  "term": 7,
  "leaderId": "node-b",
  "leaderUrl": "http://node-b:8080",
  "members": ["node-a", "node-b", "node-c"],
  "lastRebalanceId": "rebalance-19",
  "updatedAt": "2026-08-18T12:00:00Z"
}
```

Ownership metadata is itself replicated through the internal table's Raft group.
A metadata update is committed only by the metadata leader. Nodes never infer a
new owner from local process state alone; they first observe the committed
metadata record.

The internal table has a bootstrap owner selected by the cluster configuration.
After bootstrap, it follows the same election and failover rules as any other
table. The bootstrap configuration is required to avoid a circular dependency
while the ownership table is being created.

## Election model

Each table has its own Raft state:

- `currentTerm`, `votedFor`, `leaderId`, and heartbeat timestamp are scoped by
  table.
- Vote and heartbeat messages include the table name.
- A candidate wins a table election only with a majority of that table's current
  members.
- A node may lead `users` while following `orders`.
- A node that is not a member of a table does not vote in that table's election.

The existing global Raft group remains responsible for cluster membership and
metadata bootstrap during migration. Table groups use the same peer transport,
but maintain independent persistent state and replication cursors.

## Table creation and placement

CREATE TABLE is a cluster-wide catalog operation. The Router selects a healthy
database node with the fewest user tables when no leader exists for the new
table. The selected node creates the local store and notifies every configured
peer through the internal table-ensure endpoint. Each peer initializes the
same table and its table-scoped Raft state.

The table is therefore present on every replica before the table leader accepts
writes. Table placement balances the initial leader assignment by current table
count; later elections and explicit rebalancing may move leadership. A failed
peer notification must be resolved before relying on cross-table transactions.

## Write and read routing

1. A request names a table.
2. The node reads the committed ownership record.
3. A write is accepted only if the node is the table leader.
4. A follower returns a redirect/rejection containing `leaderUrl` and `term`.
5. The Router routes reads to the current table leader; direct node reads use the
   local replica and may be stale on followers.
6. SQL statements that touch multiple tables use the distributed transaction
   coordinator when they write. The coordinator prepares and commits each table
   leader, while reads are routed to the relevant table leader.

## Read routing and consistency

The Router resolves the requested table and forwards both reads and writes to its
current leader. Non-transactional reads return committed leader state. A
transactional read carrying the transaction ID is also sent to the table leader,
where it can overlay that leader's staged, uncommitted writes. Staged writes stay
in leader memory and are not published to followers or the change log before
COMMIT.

Direct database-node requests bypass the Router and read the local replica. A
follower can be briefly stale while its table change stream catches up, so clients
that require leader-consistent reads should use a Router endpoint.
## Replication and rebalancing

Each table leader appends committed writes to the table WAL and publishes a
change event containing the table name and global event sequence. Followers
subscribe from their last table sequence and apply only events for that table.

When a node joins or a failure is detected:

1. The metadata leader computes a new member set using the configured replication
   factor and a deterministic balancing policy.
2. It writes a new ownership record with a higher metadata version/term.
3. A new follower bootstraps from a snapshot (or SSTable copy) plus change-log
   events after the snapshot sequence.
4. The new member reports caught-up status.
5. Leadership moves only after the destination is eligible to serve writes.
6. The old leader remains a follower until the new record is committed and the
   new leader has acknowledged readiness.

Ownership changes must be epoch/term checked so an old leader cannot continue
accepting writes after a newer assignment is committed.

## Failure handling

A follower that loses its SSE connection reconnects using its last applied table
sequence. Heartbeats keep idle connections alive and allow the follower to
notice a dead leader. The table election starts after the table-specific
heartbeat timeout and requires the table's majority.

If the table has no available majority, it becomes unavailable for writes rather
than accepting split-brain writes. Last-write-wins remains the transaction
conflict policy only within the current non-serializable transaction model; it is
not used to resolve ownership epochs.

## Storage layout

Each table owns a WAL, memtable, and a directory of bounded immutable SSTable
files. A sorted flush or compaction run may produce several `sstable-*.json`
files. Every data file has matching `.bloom.json` and `.index.json` sidecars.
Point reads inspect sparse-index ranges first, then open only candidate data
files; range reads merge records from all files and keep the highest sequence.
The file-size target is configurable through `Lsm:MaxSstableFileSizeBytes`.


```text
data/
├── tables/
│   ├── __table_ownership/       # internal metadata table
│   ├── users/                   # table-local WAL and bounded SSTable files
│   └── orders/
└── raft/
    ├── cluster-state.json       # bootstrap/global membership
    └── tables/
        ├── __table_ownership.json
        ├── users.json
        └── orders.json
```

## Testing strategy

- Unit-test independent terms and votes for two tables on the same node.
- Verify a node can lead one table and follow another.
- Verify writes are rejected on a table follower with the correct leader URL.
- Verify ownership records survive restart.
- Verify a new follower catches up from a sequence and does not replay another
  table's events.
- Integration-test table-specific failover and deterministic rebalancing.
- Integration-test that cross-table transactions are rejected.
