# Read Consistency Levels

## Purpose

The Router supports selectable read consistency for non-transactional point and
range reads. The default favors read availability and replica utilization. Clients
that require the latest committed leader state can opt into strong consistency with
an HTTP header.

## Client contract

Use the following header on read requests:

```http
X-Read-Consistency: eventual
```

`eventual` is the default when the header is absent. It allows the Router to select
an available replica and load-balance reads across the cluster. A selected follower
may be briefly behind the table leader while it applies committed change-log events.

To require leader-consistent reads, send:

```http
X-Read-Consistency: strong
```

The Router discovers the current leader for the requested table and forwards the
read there. If no leader is available, the strong read fails instead of silently
falling back to a follower.

The header applies to point reads, range reads, and SQL `SELECT` requests.
Router-served reads also return `X-Read-From` with the selected database node URL, so
clients can observe whether an eventual read used a follower or a strong/transactional
read used the leader. Writes,
DDL, and transaction control are always routed according to their existing leader
or coordinator rules.

## Transaction rules

A read carrying a `transactionId` is always routed to the relevant table leader,
regardless of the header value. The leader can overlay that transaction's staged
writes, preserving read-your-writes behavior. Uncommitted writes are not visible to
other replicas and are not placed in the change log until `COMMIT`.

A transaction that reads multiple tables routes each read to that table's leader.
The transaction coordinator remains responsible for cross-table commit; the read
consistency header does not change two-phase commit behavior.

## Router behavior

```text
Read request
    |
    +-- transactionId present? -- yes --> table leader
    |
    +-- X-Read-Consistency: strong --> table leader
    |
    `-- absent/eventual -----------> healthy replica selected by load balancing
```

For eventual reads, the Router selects from healthy replicas known to host the
table. It should avoid routing to an unavailable node and retry another eligible
replica when a request fails before a response is received. The selection policy
is an implementation detail, but it must distribute ordinary reads rather than
pinning every request to the table leader.

For strong reads, the Router invalidates stale leader-cache entries after a leader
redirect or conflict and rediscovers the leader once, matching the existing write
routing behavior.

## SQL console

The Router SQL console exposes a read-consistency selector with `Eventual` as the
default and `Strong` as the explicit alternative. The console sends the selected
value as `X-Read-Consistency` with each SQL request. The selector is informational
for writes and transaction statements; transactional reads are leader-routed by
the server regardless of the selected value.

## Direct database-node requests

Requests sent directly to a database node bypass Router-level load balancing. They
read that node's local replica, unless the endpoint itself requires a leader for a
write or transactional operation. Applications that need the consistency-level
contract should use a Router URL.

## Operational trade-offs

| Level | Routing | Visibility | Trade-off |
| --- | --- | --- | --- |
| Eventual (default) | Healthy replica selected by Router | May lag committed leader state | Better read distribution and availability |
| Strong | Current table leader | Latest committed leader state | Leader load and leader availability are required |
| Transactional read | Table leader | Committed state plus that transaction's staged writes | Required for read-your-writes |

## Compatibility and validation

Unknown values are rejected with a client error. The Router should normalize the
header value case-insensitively and preserve the header when forwarding requests.
Existing clients that omit the header retain the default eventual behavior.
