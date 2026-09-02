# Bounded-Staleness Reads

## Purpose

Bounded staleness provides a middle ground between eventual and strong reads.
A client accepts a replica that is behind the current table leader, but limits
how far behind it may be. This preserves replica read capacity while providing a
predictable freshness bound.

The first implementation uses a deterministic sequence bound rather than wall
clock time. A read may be served by any healthy replica whose applied table
sequence is no more than `N` committed sequence positions behind the leader.

## Client contract

Select the policy with:

```http
X-Read-Consistency: bounded-staleness
X-Max-Sequence-Lag: 25
```

`bounded`, `bounded_staleness`, and `bounded-staleness` are accepted policy
names. `X-Max-Sequence-Lag` is required and must be a non-negative integer.
`0` means the replica must have caught up to the leader; it can still be served
from a follower, but has the freshness of the leader at selection time.

Successful reads return the selected replica and its observed sequence:

```http
X-Read-From: http://node-b:8080
X-Read-Sequence: 9975
```

The sequence header is observability data for bounded reads. Unlike session
consistency, bounded staleness does not require the client to send a sequence
floor from a previous request.

## Routing algorithm

For a read over table `T`:

1. Discover the current leader for `T`.
2. Read the leader's applied sequence `L` from `/stats`.
3. Calculate the minimum eligible sequence:
   `max(0, L - X-Max-Sequence-Lag)`.
4. Discover healthy replicas that host `T`.
5. Read each candidate's applied sequence in parallel.
6. Keep candidates whose sequence is at least the minimum eligible sequence.
7. Load-balance among eligible candidates.
8. If none qualify, return an error instead of returning data outside the bound.

Example:

```text
Leader:   sequence 10,000
Allowed:  maximum lag 25
Minimum:  sequence 9,975

Replica A: 9,990  eligible
Replica B: 9,970  rejected
Replica C: 10,000 eligible
```

The policy is evaluated at routing time. Replication can advance or fall
behind after the watermark check, so the guarantee is about the selected
replica's observed applied position, not a lock held for the duration of the
response.

## Why sequence bounds first

A time bound requires a committed timestamp watermark from the leader and the
same watermark to be recorded when followers apply each change. Comparing local
wall clocks would be unsafe under clock skew. The sequence bound uses the
existing per-table replication position and is deterministic, auditable, and
already available in node statistics.

A future time-based policy can add a propagated leader commit timestamp and a
header such as `X-Max-Staleness-Ms`. It must use leader-provided timestamps,
not independently sampled follower clocks, and should define behavior when the
watermark is unavailable.

## Interaction with other consistency levels

| Level | Selection rule |
| --- | --- |
| Eventual | Any healthy replica; it may be arbitrarily behind while available |
| Session / monotonic / consistent-prefix | Replica at or beyond the client's sequence floor |
| Bounded staleness | Replica no more than the requested sequence lag behind the leader |
| Strong | Current table leader |
| Transactional read | Table leader with staged transaction writes overlaid |

Bounded staleness is a read-routing policy. It does not change WAL ordering,
Raft propagation, transactions, or the authoritative source table. A
transactional read always goes to the table leader regardless of this header.

The bound is per table because leadership and applied sequence positions are
per table. It is not a cross-table causal guarantee.

## Failure behavior

- Missing, negative, or malformed `X-Max-Sequence-Lag` is rejected with `400`.
- An unavailable node is excluded from the candidate set.
- If no healthy replica meets the bound, the Router fails rather than silently
  violating the requested freshness. The caller may retry with a larger bound
  or use `strong`.
- Leader discovery or leader statistics failure prevents the Router from
  calculating the bound and fails the bounded read.

## Testing

Unit coverage verifies policy parsing, aliases, non-leader routing, and the
minimum eligible sequence calculation. The Router path should also be exercised
in distributed tests with a deliberately lagging follower to verify that an
ineligible replica is never selected and that a qualifying replica is accepted.