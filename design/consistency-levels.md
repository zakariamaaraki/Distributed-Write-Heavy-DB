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

## Session consistency

The database implements one intermediate session mode, `session`, which is
stronger than ordinary eventual reads without forcing every read to the leader.
`monotonic` and `consistent-prefix` are accepted aliases for `session`; they do
not select different routing algorithms. The guarantee is per table because
leadership and replication progress are per table.

With `X-Read-Consistency: session` (or either alias), the client carries the
last sequence it has observed:

```http
X-Read-Consistency: session
X-Read-After-Sequence: 120
```

The Router only chooses a healthy replica whose applied table sequence is at
least `120`. The response returns the sequence observed by that replica:

```http
X-Read-Sequence: 125
```

The client stores `125` and sends it on the next request. This prevents a
social-media user from updating their own profile, seeing the new profile, and
then seeing an older profile version when the next request is load-balanced to
another replica. If no replica has reached the requested sequence, the Router
uses the leader if it has reached it; otherwise it fails rather than returning
older data.

### Monotonic reads

Guarantee: once a session has seen a version, it never later sees an older
version. Without it, one request might read `likes = 10` from Replica A and a
later request read `likes = 8` from Replica B. With session consistency, the
later request reads `likes = 10` or higher.

### Consistent-prefix reads

Guarantee: a session never sees a later write without the earlier writes that
precede it. If W1 creates Alice's post and W2 adds Bob's comment, the session
may see W1 and W2, or only W1, but never W2 while W1 is missing. Think: “I
should not see effects before their causes.” The per-table sequence floor gives
this ordered-prefix behavior for requests routed through the Router.

These are two descriptions of the same implemented session mechanism here:
`session`, `monotonic`, and `consistent-prefix` all use the same sequence-floor
routing and require `X-Read-After-Sequence`. They are session guarantees, not
global strong consistency; another client may still observe a different
replica state. The Router compares each candidate replica's applied position
before selecting it.

## Operational trade-offs

| Level | Routing | Visibility | Trade-off |
| --- | --- | --- | --- |
| Eventual (default) | Healthy replica selected by Router | May lag committed leader state | Better read distribution and availability |
| Strong | Current table leader | Latest committed leader state | Leader load and leader availability are required |
| Session (aliases: monotonic, consistent-prefix) | Healthy replica at or beyond the session sequence | No backward movement and ordered per-table effects | Clients carry sequence tokens; a lagging cluster may wait or fail |
| Transactional read | Table leader | Committed state plus that transaction's staged writes | Required for read-your-writes |
