# Replica lag in the monitoring page

## Goal

Show the replication freshness of every table replica in the Router monitoring
page. Operators should be able to distinguish a caught-up follower from one
that is falling behind, and should be able to see the exact sequence positions
used to calculate the result.

## Definition

Each table has a committed change-log sequence. The table leader's current
sequence is the freshness watermark for that table. Every replica persists the
last sequence it has applied locally.

For a reachable replica:

```text
lag = max(0, leaderSequence - appliedSequence)
```

The leader normally has zero lag because its applied sequence is the source
watermark. A follower with applied sequence `9,975` and leader sequence
`10,000` is `25` committed changes behind. This is sequence lag, not elapsed
wall-clock time; it remains meaningful when node clocks differ.

## API shape

`GET /monitoring/api/status` keeps its existing response shape and adds these
fields to each table state:

```json
{
  "node": "node-2",
  "role": "Follower",
  "leader": "node-1",
  "leaderSequence": 10000,
  "appliedSequence": 9975,
  "sequenceLag": 25,
  "lagStatus": "behind"
}
```

The Router calculates lag from the state responses it already gathers. This
keeps the database node endpoint backward-compatible and avoids adding a
second monitoring-specific replication protocol.

For a table whose leader state cannot be read, `leaderSequence` and
`sequenceLag` are `null`, and the UI displays `unknown`. For an unreachable
replica, `appliedSequence` and `sequenceLag` are also `null`; this is different
from zero lag and must not be presented as healthy.

## Status levels

The first version uses stable semantic statuses rather than hard-coding a
cluster-wide alert threshold:

| Status | Meaning |
|---|---|
| `caught-up` | Sequence lag is zero. |
| `behind` | Sequence lag is greater than zero. |
| `unknown` | The replica or leader sequence could not be observed. |

The page displays the numeric lag and status. Alert thresholds can be added
later without changing the API contract; different workloads may reasonably
choose different limits.

## Monitoring-page design

The table-ownership table gains the following columns:

```text
Kind | Node | Role | Applied sequence | Leader sequence | Lag | Size | Term | Leader
```

Leader rows show `0 (caught up)`. Follower rows show the number of committed
sequence positions behind the leader, for example `25 (behind)`. Unknown
values use `—` and a muted/ warning style. The existing green leader-row
highlight remains, while lag status gets its own visual treatment so role and
freshness are not conflated.

The page continues polling every three seconds. No database state is changed by
the monitoring endpoint or page.

## Data-flow

```text
Follower Raft state ── applied sequence ──┐
                                          ├─ Router calculates lag
Leader Raft state   ── current sequence ──┘
                                                    │
                                                    ▼
                                  monitoring JSON + ownership table
```

The current Raft status already exposes `LastAppliedChangeSequence`. The
implementation should use that field directly, preserving the same table-local
sequence semantics used by session and bounded-staleness reads.

## Testing

- Unit-test lag calculation for leader, caught-up follower, follower behind,
  sequence regression protection, and missing sequence values.
- Test the monitoring response mapping with a leader and follower state.
- Verify the monitoring page renders the new columns and does not label an
  unavailable node as caught up.
- Keep existing Router and full test suites passing.

## Future extensions

The API can later add replication rate, last successful application time,
connection state, and configurable warning/critical thresholds. Those metrics
should supplement sequence lag rather than replace it, because sequence lag is
the consistency-relevant measure already used by the database.
