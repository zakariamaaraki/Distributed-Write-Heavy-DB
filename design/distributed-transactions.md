# Distributed Transactions with Two-Phase Commit
## Table catalog prerequisite
## SQL transaction routing

The Router maintains transaction affinity. When BEGIN creates a client-visible
transaction ID, the Router records the coordinator node that created it. Later
SQL statements carrying that ID are forwarded to the same coordinator, even when
their table name would otherwise route to a different table leader. The
coordinator reads the staged write set and promotes a cross-leader COMMIT to 2PC.
The mapping is removed after COMMIT or ROLLBACK.

Clients should use one Router endpoint for the complete SQL transaction. Direct
requests to arbitrary database-node ports do not transfer coordinator state;
reusing a transaction ID on another node therefore returns transaction not found.

Before using a distributed transaction, every participating table must exist in
the catalog of every configured node. Table creation is a cluster-wide operation:
the Router places a new table on the healthy node with the fewest user tables,
then the creating node propagates the catalog entry and initializes the
table-specific Raft state on every peer. This prevents a table leader from being
elected on a node that cannot open the table store.

If a peer is unavailable during creation, verify the table lists and monitoring
page before using the table in a distributed transaction.

## Scope

Distributed transactions allow one client transaction to write keys in multiple
tables whose leaders are different replicas. The transaction coordinator runs on
the node that received the client request. Each table leader is a participant.
Raft still owns table leadership and replication; 2PC coordinates the commit
decision across those independent table groups.

This is classic coordinator-driven two-phase commit:

```text
Client -> Router -> Coordinator
                       | prepare(table A leader)
                       | prepare(table B leader)
                       | commit(table A leader)
                       ` commit(table B leader)
```

## Protocol

### 1. Begin and stage

`POST /distributed-transactions` creates a transaction id. Each write is staged
with `PUT /distributed-transactions/{id}/writes`. The coordinator keeps the
small write set in memory and groups writes by normalized table name. It does
not copy a memtable.

### 2. Participant discovery

At commit, the coordinator queries `/raft/tables/{table}/state` on the configured
nodes. The node reporting the current leader URL becomes the participant for that
table. A table without a discoverable leader causes the transaction to abort
before any participant is prepared.

### 3. Prepare phase

The coordinator sends each participant:

```json
{
  "transactionId": "...",
  "writes": [{ "table": "users", "key": "u1", "value": "...", "isDeleted": false }]
}
```

A participant validates the request and records the prepared write set under the
transaction id without making the values visible. It returns `prepared: true`.
If any participant rejects or times out, the coordinator sends abort to every
participant that already prepared and returns `aborted`.

### 4. Commit phase

After every participant has acknowledged prepare, the coordinator sends commit to
all participants. Each participant applies its grouped batch through the normal
`DatabaseEngine`, so WAL, memtable, indexes, and change-log publication use the
same storage path as a local committed batch. A successful response is returned
only after all commit calls succeed.

## Success and failure cases

| Situation | Coordinator action | Result |
| --- | --- | --- |
| One table | Prepare and commit its leader | `committed` |
| Multiple table leaders | Prepare every leader, then commit every leader | `committed` |
| No leader found | Do not prepare any participant | `aborted` |
| Prepare rejection | Abort already-prepared participants | `aborted` |
| Prepare timeout | Abort already-prepared participants | `aborted` |
| Abort request lost | Participant's prepared data remains unapplied until cleanup/recovery | no visible write |
| Commit timeout/failure | Return `in-doubt`; do not claim success | coordinator recovery required |
| Coordinator crash before prepare completes | Participants either have no prepared record or receive abort during recovery | no visible write |
| Coordinator crash after all prepares | Participants may remain prepared and block the decision | recover from the durable decision record when enabled |
| Coordinator crash during commit | Some participants may commit before others | resolve using the coordinator's decision; clients must retry status |
| Participant crash before commit | It recovers its prepared record and waits for the decision | commit or abort recovery |
| Participant crash after commit | WAL recovery restores the committed batch | committed data remains durable |

2PC intentionally has a blocking window: after prepare succeeds, a participant
cannot safely discard its prepared state merely because the coordinator is
unreachable. The current implementation exposes `in-doubt` so callers do not
mistake a partial failure for a successful atomic commit. A production hardening
step is to persist coordinator decisions and participant prepared records and add
recovery/status polling; the participant API is separated to support that step.

## Atomicity boundary

The atomicity boundary is the set of participant batches selected during prepare.
Each participant uses the existing table store commit path, which gives durable
local WAL recovery and change-log events. Raft replication remains table-scoped;
2PC does not replicate a transaction log between tables and does not turn the
independent table Raft groups into one consensus group.

## Client API

```powershell
$tx = curl.exe -s -X POST http://localhost:9081/distributed-transactions
curl.exe -X PUT http://localhost:9081/distributed-transactions/{id}/writes `
  -H "Content-Type: application/json" `
  -d '{"table":"users","key":"u1","value":"Alice","isDeleted":false}'
curl.exe -X PUT http://localhost:9081/distributed-transactions/{id}/writes `
  -H "Content-Type: application/json" `
  -d '{"table":"orders","key":"o1","value":"Pending","isDeleted":false}'
curl.exe -X POST http://localhost:9081/distributed-transactions/{id}/commit
```

Use the Router URL for the coordinator request. The Router routes the request to
the receiving node; the coordinator then routes each participant operation to the
leader of its table.

## Implemented recovery and operations

The implementation journals state in `data/distributed-transactions.json`. The journal contains active coordinator write sets, participant prepared write sets, and phase decisions. It is written using a temporary file followed by replacement so a process restart can reload the last complete journal.

Participant phase requests are idempotent. Repeating prepare for the same transaction returns success when the participant already prepared or committed. Repeating commit after commit returns success without applying the batch again. Abort never removes a committed decision.

The background cleanup service runs every minute. It asks the manager to replay journaled `committing` participant decisions and expires coordinator transactions older than one hour. `/distributed-transactions/{id}/recover` exposes explicit status evaluation, while `/distributed-transactions/metrics` exposes phase counters and active/prepared counts.

The database batch path remains the durability boundary: participant commit writes the table WAL, applies the memtable, updates indexes, and publishes the change log. A participant does not expose prepared values to ordinary reads.

## Happy path

![2PC happy path](../docs/distributed-transaction-happy-path.svg?raw=true)

1. The client begins and stages writes for `users` and `orders`.
2. The coordinator journals the write set and discovers both table leaders.
3. Every participant validates and journals its prepared batch.
4. The coordinator journals the committing decision.
5. Every participant applies its batch and acknowledges commit.
6. The coordinator records committed and removes the active write set.

## Failure path

![2PC failure path](../docs/distributed-transaction-failure-path.svg?raw=true)

Before prepare completes, any discovery or prepare failure causes abort messages to
already-prepared participants. After the committing decision is journaled, a lost
response is not treated as an abort: the result is `in-doubt`, and recovery retries
the decision. This preserves the 2PC rule that a prepared participant must not
unilaterally choose abort after the coordinator may have chosen commit.

## Transparent SQL promotion

The SQL engine keeps the normal BEGIN, statement, and COMMIT contract. It exports the staged write set only at commit time. If all staged tables resolve to the same leader node, the existing local transaction path is used. If they resolve to different leader nodes, the engine creates an internal distributed transaction, copies the staged operations into it, and invokes leader discovery, prepare, commit, abort, journaling, and recovery. The SQL transaction id remains the client-visible id.

A failed prepare returns an SQL error and aborts prepared participants. A phase-two failure returns an in-doubt SQL execution error.

## Leader-only SQL writes

A SQL transaction must preserve table-leader ownership. The transaction ID is
not sufficient by itself to make arbitrary nodes interchangeable.

The intended flow is:

1. BEGIN creates a transaction ID and registers that ID on every database node.
2. Each INSERT, UPDATE, or DELETE is routed to the leader of its target table.
3. That leader stages the write under the shared transaction ID.
4. At COMMIT, the coordinator collects the staged write sets from the participating
   table leaders.
5. The coordinator runs prepare and commit across those leaders using 2PC.

A coordinator-affinity-only router is not sufficient: it would send a write for a
second table to the first table's coordinator, violating leader-only writes.
Direct database-node clients must use the same registration and leader-routing
protocol; reusing an ID on an unregistered node returns transaction not found.

The Router implements this protocol through `POST /transactions/{id}/register`,
which is idempotent on every node. The coordinator reads each node's staged
operations through `GET /transactions/{id}/operations`; an unavailable registered
node causes collection to fail rather than allowing an incomplete write set to
commit. After COMMIT or ROLLBACK, the Router deletes the registration from every
node through `DELETE /transactions/{id}/register`. The transaction buffers remain
process-local by design; the shared identity and coordinator collection are the
cross-node protocol.