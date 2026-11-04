# Home leases, failover and fencing (PIVOT R8)

A database with replica regions keeps serving writes when its home region dies: the most caught-up replica takes over
as home, and the old home never accepts another write (D17, D18). A database without replica regions has no other copy,
so it has no failover; it waits for its home.

## Lease store

Leases live in the JetStream KV bucket `terrabase-homes` of the `TERRABASE` account (`docs/mesh-jetstream.md` in
ts-orchestrator): file storage, history 1, three replicas (one per region) in the cluster, per-key TTL
(`LimitMarkerTTL` 10 s).

| Key | Value | Written by |
| --- | --- | --- |
| `node.<node>` | `{node, region, session}`, TTL 10 s | that node, every 2 s, compare-and-set on the key's revision |
| `home.<databaseId>` | `{organizationId, projectId, databaseId, home, node, epoch, session}`, no TTL | the database's home, a claiming replica |

- Liveness is per node, so renewal traffic does not grow with the number of databases.
- `epoch` is the database's catalog `placement_version`. Every home change (failover or a planned move, R9) is a
  compare-and-set on `home.<databaseId>` that raises it by one.
- `session` is a ULID a node picks each time it creates its `node.<node>` key. A node that restarts or whose key expired
  starts a new session.

## Holding a lease

A node may commit a write to a database with replica regions only while it holds that database's lease:

1. its `node.<node>` renewal is current: the last renewal was sent less than 8 s ago (TTL 10 s minus a 2 s margin, on
   the node's monotonic clock), so the node stops writing before JetStream can expire its key; and
2. `home.<databaseId>` names this node with its current session.

The engine checks this when a write transaction begins and again just before `COMMIT`. A write without the lease answers
a retryable error (`DatabaseMovedException` without a home: SQLSTATE `40001`, REST `409 serialization_conflict`).

At start a node marks every database it homes with replica regions as leased but not held, so it writes none of them
until it confirmed each `home.<databaseId>` under its new session. Each node confirms its leases on every new session and
every 15 s, which also picks up databases that gained replica regions.

## Failover

1. Every node watches the bucket. When `node.<node>` of a database's home expires, each replica region that holds a
   pulled replica of the database waits 5 s and checks that the node did not come back.
2. It asks the other replica regions for their held revision (`GET /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/held`,
   internal listener, 2 s). The replica with the highest revision (incarnation, generation, frame; ties: region name)
   claims; the others wait. After three rounds a replica claims even when a better one did not.
3. The claimer detaches its replica and imports it as the home file (R9's detach), fenced until it holds the lease. The
   import starts a **new incarnation** (D18): every other replica takes a full answer on its next pull.
4. It takes the lease with the one compare-and-set that planned moves use too (`HomeLeases.TakeAsync`): `home.<databaseId>`
   goes from `{home: old, epoch}` to `{home: self, epoch: epoch + 1, session: own}`, only while the old home's node key is
   still absent. It fails when the old home came back and confirmed the key first, or another replica took it, so two
   nodes never both hold the lease; the claimer then deletes the imported file and follows the winner.
5. Only then does it record placement `{home: self, replicas: the others plus the old home, version: epoch + 1}` and
   prepare home apply (R6).
6. Every region watches `home.*` and takes the new placement at once, so home routing (421 `wrong_region`, Postgres and
   WebSocket home sessions) follows within the watch delay; the 30 s catalog sweep stays the backstop.

## The old home

When the old home reaches JetStream again, its renewal fails (its key expired), so it starts a new session and confirms
its leases. For a database whose `home.<databaseId>` names another node at an epoch at least its own placement version,
it demotes itself: it fences the database as moved (writes answer `421 wrong_region` / SQLSTATE `08004`), records the
new placement, deletes its file and follows the new home as a replica. Writes it committed that no replica pulled are
discarded with the file (`terrabase.home.demotions`).

## Every write path carries the epoch

- Home commits: the lease check above.
- Home sessions (`/v1/internal/sql/session`): the open frame carries the replica region's placement epoch; a home whose
  own placement version is lower refuses with `wrong_region`, so the caller reads its placement again.
- Home apply (R6, R7): applies commit through the same engine path, so a node without the lease refuses them.

## Durability and RPO

| Mode | Acknowledged when | Lost if the home region dies |
| --- | --- | --- |
| default | the home committed it (D16) | at most the writes no replica pulled yet: about the replica lag, ~100 ms with revision notices, plus up to 8 s when the home was cut off from its replicas but not yet from its clients |
| `"durability": "all"` | the home committed it and every replica region holds it | nothing |

- `durability` is a field of `POST …/sql/execute` (`"local"`, the default, or `"all"`).
- With `all`, after the commit the home reads the commit's revision and waits until every replica region of the placement
  (every serving region for all-regions placement) reported holding that revision or a later one. Each replica publishes
  `terrabase.held.<home>.<databaseId>` `{organizationId, projectId, databaseId, region, revision}` on NATS after every
  applied pull.
- A replica region that does not report within 10 s fails the request with `503 unavailable` after the commit: the
  write stays at the home, but it may be lost if the home region fails before a replica pulls it.
- Every replica then holds every `all` write, so whichever replica takes over keeps them.

## Metrics

- `terrabase.home.failovers`: databases this node took over.
- `terrabase.home.demotions`: databases this node stopped homing because another node holds their lease.
- `terrabase.home.leased`: databases whose writes on this node need its lease.

## Tests

`tests/TerraBase.Slow/Sync/HomeFailoverTests.cs` runs gru and sjc on one JetStream server, writes to two databases (one
with `durability: all`, one default), cuts gru off from JetStream and from its replica mid-write, and checks that sjc
takes over, keeps every `all` write, loses only a tail of default writes, and that gru refuses writes before and after
it reconnects.

## Known gaps

- R7 push replay: messages that the old home applied but no replica pulled are replayed by the new home once R7's
  consumer reads the old home's subjects for failed-over databases.
- `durability` is accepted on SQL REST only; the WebSocket frame field and `SET terrascale.durability` follow.

## Planned moves (R9)

A move from gru to sjc at epoch v runs R9's catch-up, pause and drain, then sjc's promote imports its replica and takes the
lease with the same compare-and-set, `{gru, v}` → `{sjc, v + 1}`, before its catalog takes the placement. A lost
compare-and-set refuses the promote (`move_aborted`), so gru lifts its pause. R9's abort exchange still settles a promote
whose answer gru never received. While gru holds R9's durable pause fence, the lease watch leaves the demotion to R9.
