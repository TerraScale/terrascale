# Database placement

A database has one set of selected regions. All regions are equal in the public placement model. TerraBase internally keeps one writer and synchronizes the other selected regions; this API change does not add independent multi-writer consensus or change SQL write routing.

## API

All routes need `databases:write` and go to the database's home region; behind `api.terrascale.tech` the edge replays a
request that reached another region (`421 wrong_region`, `ts-replay: region=<home>`).

| Operation | Request | Answer |
|---|---|---|
| `createDatabase` | `POST /v1/projects/{project}/databases` `{"name":"main","regions":["gru","sjc"]}` | `201` database |
| `updateDatabase` | `PATCH /v1/projects/{project}/databases/{database}` `{"regions":["sjc"]}` | `200` database |
| `moveDatabaseHome` | `POST /v1/projects/{project}/databases/{database}/move-home` `{"homeRegion":"sjc"}` | `200` database |

`regions` is a JSON array with at least one serving region id, without duplicates. Unknown, non-serving, duplicate and empty sets answer the existing `400 invalid_request` envelope. Database responses include `regions` sorted by region id, the catalog order. Response order carries no meaning and does not identify the writer.

On create, the first region in the caller's array is the initial writer. On update, the writer stays if it remains selected. Otherwise TerraBase adds missing requested regions and keeps them as members so their followers can synchronize. A call that adds regions returns `503 replica_not_ready` without attempting promotion. The intermediate placement is the old writer and existing members plus the requested regions. If no regions need adding, TerraBase can promote on the first call: `{a,b}` to `{b}` returns `200` when `b` is ready. Nothing is unstaged on any refusal, and `regions` reports that superset meanwhile.

Repeat the same request after a requested region has replicated this database at least once. TerraBase reads the engine's per-region pull observations and selects the first requested region, in region-id order, that has replicated this database at least once; the move brings it up to date and refuses with 503 replica_not_ready when it can't. It tries at most that one candidate through the existing move. With no ready candidate it returns `503 replica_not_ready` immediately. A repeat of the same region set during its pending move also returns `503 replica_not_ready`; a different request's move retains `409 move_in_progress`. If a race removes the candidate before the move begins, the response is retryable `503 replica_not_ready`, and the repeat recomputes from the current placement. After promotion, the same request is forwarded to the new writer, which removes every unselected region. Moving from `{"regions":["gru"]}` to `{"regions":["sjc"]}` therefore requires at least two calls: add, then move and remove. Each call is idempotent: placement versions change only when membership, the writer or all-regions mode changes. A rename is sent separately from placement. Explicit `regions` turns off all-regions placement.

The requested placement and any intermediate superset must pass the existing limit of eight replicas other than the writer (nine total members). A set or superset that exceeds it returns `400 invalid_request` with `regions can place a database in at most 9 regions, including the writer and any intermediate placement` before any membership is written; remove other non-writers first to make room. A retryable refusal leaves the current valid placement in place, including additions. An unresolved move returns its existing refusal and is settled by the existing move mechanism before the caller continues.

Transitional: the old fields and the move-home endpoint are removed by category C23 of batch db4a5ba7 once every consumer sends `regions`.

Until then, requests using `homeRegion`, `replicaRegions` or `allRegions`, and responses containing the old fields, keep working. The old-field PATCH retains its placement-version bump even when repeated without a membership change. It now returns `409 move_in_progress` while a move is pending: changing membership then could consume the move marker's next version and prevent the source catalog from recording the promoted writer, while the target holds the membership captured for promotion. Combining `regions` with any of those placement fields is invalid. The transitional fields retain these rules:

- `replicaRegions` names at most 8 distinct serving regions other than the home, and turns all-regions mode off.
- `allRegions: true` places a replica in every serving region other than the home. The home keeps the replica set equal
  to the serving regions: a region that joins (`TS_PEERS_FILE`) is added, and one that leaves is removed, within one
  replica sweep (30 s). During a pending move, following changes nothing and resumes on the next pass after the move settles. Sending both fields is refused.
- `move-home` names one of the database's replica regions. It answers when the move is done; the old home is then a
  replica region. Moving to the current home answers the database unchanged.

| Error | When |
|---|---|
| `400 invalid_request` | bad or unserved regions; `homeRegion` is not a replica region |
| `409 move_in_progress` | another move of this database is running |
| `421 wrong_region` | the request reached a region other than the home |
| `503 replica_not_ready` | no requested replica can take over yet; added regions remain members (move-home itself changes nothing on this refusal) |
| `503 move_unresolved` | the move outcome is being settled with the target; writes stay paused until it is; retry |

## Moving the home

Region A is the home and region B the target. A coordinates; B's replica becomes the home file.

1. **Catch up.** A asks B to pull now (`POST /v1/sync/…/databases/{database}/catch-up` on B's internal listener), while
   writes continue.
2. **Pause.** A records a move marker in its system database and a fence file under `<data-directory>/.fences/`, then
   waits for the write transactions in flight to finish. New write transactions wait at the fence (at most 30 seconds)
   before they take the database's writer. Reads and page pulls continue; a pull skips its CDC retention write while the
   database is fenced (the next pull records it), so the paused home's revision stays fixed.
3. **Promote.** A asks B to promote at the next placement version (`POST …/promote`). B stops its follower, pulls until
   a pull brings nothing new (two pulls: the second must be empty, which proves A is paused), converts its replica into
   a home file (a consistent copy into a new file under a new data key, as a restore does; a new incarnation, so every
   replica bootstraps again), and records itself as home in its catalog with the next placement version.
4. **Demote.** A fences the database as moved (below), which ends the waiting writes, records B as home and itself as a
   replica in its catalog, deletes the marker and the fence file, and deletes its home file. A then follows B like any
   replica.

The epoch of a database is its placement version: B is home at version `v + 1` only after A stopped writing at `v`, and a
region accepts a placement only when it is newer than the one it holds.

A request through a region that is no longer a member answers `421 wrong_region`, naming the current home in `X-TerraBase-Database-Home`. Its former replica can serve reads until the local catalog observes the removal.

### Fencing

A node keeps a fence per database it no longer homes, checked after a write transaction takes the database's writer:

- **Paused:** during the move, write transactions wait; a write that passed the fence just before the pause and takes
  the writer during it fails with `serialization_conflict` (retry).
- **Moved:** waiting writes, and later writes on sessions opened before the move (WebSocket, Postgres), fail with
  `421 wrong_region` (SQL code `wrong_region`, SQLSTATE `08004`) naming the new home. They were never committed.
- **Unresolved:** after a restart with a move marker, or when A could not learn the promote outcome, writes fail with
  `tenant_busy` (retry) until A settles the move with B.

A settles an unresolved move by asking B (`POST …/promote/abort` with the move id): B answers whether it promoted. If it
did, A demotes; if not, B refuses that move id from then on and A lifts the fence. While B is unreachable A stays fenced,
so two regions never accept writes for one database. A retries every 5 seconds and at startup.

A delete and a move cannot both win: while A holds the move marker it still owns the record, so `deleteDatabase` (and a
TTL expiry) at A answers `409 move_in_progress`; once the move settles, the delete follows the database to its new home.

### Timings

The pause lasts from step 2 to step 4: one incremental pull, an empty pull, the copy of the database into its new file and
two catalog writes. The copy grows with the database size. The move logs `catch_up_ms`, `pause_ms` and `total_ms`, and
records `terrabase.placement.move.pause` (ms). A measured move (two nodes on one host, a small table under REST
writes) took 512 ms: 227 ms catch-up and a 276 ms write pause.

All-regions placement remains authoritative when a lease observation carries an empty explicit replica list.
Catalog discovery, following, revision notices, replica status and home sessions use the all-regions membership
rule; revision notices go to the currently discovered peer regions. This preserves replication after a home move
and automatically includes newly serving regions. A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
