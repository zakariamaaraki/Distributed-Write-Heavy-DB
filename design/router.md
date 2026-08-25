# Router Design

## Purpose

The Router is a sidecar process that runs alongside each database node. Applications
send requests to the local Router instead of needing to know which replica currently
owns a table. The Router discovers table ownership and routes writes and transactional reads to the
current leader. Ordinary reads use load-balanced healthy replicas by default, while
`X-Read-Consistency: strong` selects the current table leader.

The Router is an HTTP reverse proxy and does not store database data or participate in
Raft elections. The database nodes remain responsible for replication, elections,
storage, and transaction processing.

## Read consistency

The Router defaults ordinary reads to healthy replica load balancing. A read with
`X-Read-Consistency: strong` goes to the current table leader. A transactional read
carrying its transaction ID always reaches the relevant table leader and can see that
leader's staged writes. Every Router-served read returns `X-Read-From` with the
selected database node URL. Direct database-node requests bypass Router routing and
may read a follower that is briefly behind.

## Deployment

Each database node has one colocated Router:

```text
Application -> Router A -> Database A
                         -> Database B
                         -> Database C

Application -> Router B -> Database A / B / C
Application -> Router C -> Database A / B / C
```

The Router is configured with:

- `ROUTER_DATABASE_URL`: the colocated database URL.
- `ROUTER_PEERS`: a comma-separated list such as
  `node-a=http://node-a:8080,node-b=http://node-b:8080`.

The Docker Compose example starts one Router beside each database node on ports
9081, 9082, and 9083.

## Connection management

The Router maintains a reusable HTTP connection pool to every configured database
peer. Connections are kept available for subsequent requests, with HTTP keepalive
and HTTP/2 support enabled where available. This avoids creating a new connection
for every forwarded request while still allowing the pool to refresh connections
periodically.

The Router is stateless apart from its in-memory leader cache. Restarting it does not
lose database data; it simply rediscovers table ownership.

## Leader discovery and routing

1. The Router determines the table associated with a request.
2. It checks its in-memory table-to-leader cache.
3. On a cache miss, it queries database peers through
   `GET /raft/tables/{table}/state`.
4. It selects the reported table leader and forwards the original method, path,
   query string, headers, and request body to that node.
5. A successful response is returned to the client unchanged as far as practical.

REST table and key/value requests use their URL table. SQL requests are inspected
   to identify the table mentioned by `FROM`, `INTO`, `UPDATE`, `DELETE FROM`, or
   `JOIN`. Requests without a determinable table use the colocated database as the
   fallback.

## Failover and stale ownership

Leader ownership can change after an election or rebalance. If the selected node
rejects the request with a leader redirect/conflict response, the Router invalidates
the cached entry, rediscovers the leader, and retries the request once. A Router can
also query another configured peer when the preferred discovery peer is unavailable.

If no leader can be discovered, the Router returns an error to the client rather than
silently writing to a follower. This preserves the database's per-table leader
write rule.

## Scope and limitations

- The Router does not provide an additional consistency or transaction protocol.
- It does not buffer or replay an entire transaction; transaction requests remain
  governed by the database transaction manager.
- A request that spans multiple tables is routed using the table detected from the
  SQL statement; the database transaction coordinator then contacts each table leader
  for distributed prepare and commit.
- The leader cache is intentionally local and temporary; each Router independently
  refreshes ownership after a miss or routing failure.

## Operational endpoints

- `GET /health` verifies that the Router process is running.
- All other supported HTTP methods are proxied to the selected database node.

## Security and production considerations

The current Router is intended for the local/container network and does not add
authentication or authorization. Production deployments should place TLS and the
required client authentication at the Router or an upstream gateway, restrict peer
connectivity, and configure request, connection, and retry timeouts appropriate for
the workload.

## Monitoring

The Router serves `/monitoring`, a browser page that polls `/monitoring/api/status` every three seconds. It aggregates node reachability, aggregate and read/write-specific active and queued request counts, separate read/write capacity, total node storage, per-table disk sizes, table discovery including `kind` (`kv`, `document`, `relational`, or `view`), and physical-table Raft ownership. Views are displayed as non-Raft objects. It aggregates configured node reachability, table discovery, and each node's table Raft role, term, leader id, and leader URL. This gives operators a live view of per-table ownership during elections and rebalancing without routing or mutating data.

Request-admission counters apply only to POST /sql. Statements beginning with SELECT or SHOW enter the read pool; all other SQL statements enter the write pool. The two pools are independent, and monitoring requests are marked and excluded so dashboard polling does not consume SQL capacity.
## SQL console routing

The Router exposes the database's existing SQL console at `/sql-console`. The page and its static assets are served through the Router. Browser `POST /sql` calls use the selected consistency level, and read responses expose `X-Read-From`. Users keep one Router URL and do not need to select a database node manually.