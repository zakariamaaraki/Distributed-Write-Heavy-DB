# Transaction isolation design

## Current implementation

Transactions previously used one behavior: staged writes were overlaid on the latest committed read. That is read committed visibility with read-your-own-writes; it did not make repeated reads stable and it did not serialize transactions.

## User-facing API

SQL clients select a level when starting a transaction:

```sql
BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;
```

`BEGIN` without a clause remains `READ COMMITTED`.

## Implemented levels

### Read committed
Every point or range read consults the latest committed LSM state at the time that statement executes. Uncommitted writes from other transactions are invisible, while the transaction sees its own staged writes.

Example: transaction A reads `balance=100`; transaction B commits `balance=90`; A reads again and can see `90`. This is useful for short independent reads. It does not prevent non-repeatable reads or phantoms.

### Repeatable read
The first point read for a key and the first range read for a `(table, start, end, limit)` tuple are cached in the transaction. Later reads reuse that value and staged writes are overlaid. This prevents non-repeatable reads and stabilizes the same range query for the transaction lifetime.

It is not MVCC snapshot isolation: the cache is statement/result scoped, does not retain historical versions, and different range shapes can observe different committed states. A transaction can still see a phantom through a different range query.

### Serializable
Serializable transactions acquire a strict, manager-wide exclusive lock at `BEGIN` and release it only at `COMMIT` or `ROLLBACK`. This is a coarse-grained strict two-phase locking (2PL) implementation: the lock is acquired in the growing phase and released after the transaction closes in the shrinking phase. It prevents concurrent serializable transactions from interleaving.

The lock is local to one `TransactionManager` instance. Callers must use `SERIALIZABLE` for every conflicting transaction; weaker transactions, other processes, replicas, or distributed participants are not covered by this local gate. Long transactions block other serializable transactions and can fail operationally through timeout or process loss.

## 2PL and failure modes

Two-phase locking means locks are acquired before protected work and are not released until the transaction ends. The current serializable mode deliberately uses one coarse lock instead of key/range locks, avoiding deadlocks at the cost of concurrency. It can fail to provide global serializability across multiple nodes or transactions that bypass this transaction manager.

## Snapshot isolation
Snapshot isolation normally gives each transaction a consistent database snapshot and uses MVCC or version validation. LsmWriteDb does not implement snapshot isolation or MVCC. Repeatable read uses an explicit read cache, so it should not be advertised as a full snapshot.

## Linearizability
Linearizability is a real-time ordering guarantee: each operation appears to take effect atomically between invocation and response. It is stronger and different from transaction isolation. These local transaction levels do not make follower reads linearizable; use the existing strong/leader-routed read consistency for the latest leader state, and remember that a multi-operation transaction is not automatically linearizable as one operation.

## Guarantees summary

| Level | Dirty reads | Non-repeatable reads | Phantoms | Mechanism |
|---|---:|---:|---:|---|
| Read committed | No | Possible | Possible | Fresh committed read per statement |
| Repeatable read | No | Prevented for repeated cached point/range reads | Possible for different ranges | Transaction read cache |
| Serializable | No | Prevented for serializable peers | Prevented by serialization of serializable peers | Strict coarse-grained 2PL |