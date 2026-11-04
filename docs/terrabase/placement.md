# Database placement (PIVOT R9)

Each database chooses where it lives: one **home region**, which commits its writes, and **replica regions**, which
hold read replicas ([region replicas](region-replicas.md)). A database can move its home to one of its replica regions
while it takes writes.

## API

All routes need `databases:write` and go to the database's home region; behind `api.terrascale.tech` the edge replays a
request that reached another region (`421 wrong_region`, `ts-replay: region=<home>`).

| Operation | Request | Answer |
|---|---|---|
| `createDatabase` | `POST /v1/projects/{project}/databases` `{"name","homeRegion"?,"replicaRegions"?,"allRegions"?}` | `201` database |
| `updateDatabase` | `PATCH /v1/projects/{project}/databases/{database}` `{"replicaRegions":[…]}` or `{"allRegions":true}` | `200` database |
| `moveDatabaseHome` | `POST /v1/projects/{project}/databases/{database}/move-home` `{"homeRegion":"sjc"}` | `200` database |

The database body carries `homeRegion`, `replicaRegions` (sorted) and `allRegions`.

- `replicaRegions` names at most 8 distinct serving regions other than the home, and turns all-regions mode off.
- `allRegions: true` places a replica in every serving region other than the home. The home keeps the replica set equal
  to the serving regions: a region that joins (`TS_PEERS_FILE`) is added, and one that leaves is removed, within one
  replica sweep (30 s). Sending both fields is refused.
- `move-home` names one of the database's replica regions. It answers when the move is done; the old home is then a
  replica region. Moving to the current home answers the database unchanged.

| Error | When |
|---|---|
| `400 invalid_request` | bad or unserved regions; `homeRegion` is not a replica region |
| `409 move_in_progress` | another move of this database is running |
| `421 wrong_region` | the request reached a region other than the home |
| `503 replica_not_ready` | the target replica could not catch up or be reached; nothing changed |
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

### Timings

The pause lasts from step 2 to step 4: one incremental pull, an empty pull, the copy of the database into its new file and
two catalog writes. The copy grows with the database size. The move logs `catch_up_ms`, `pause_ms` and `total_ms`, and
records `terrabase.placement.move.pause` (ms). In `HomeMoveTests` (two nodes on one host, a small table under REST
writes) a move took 512 ms: 227 ms catch-up and a 276 ms write pause.

## Tests

- Slow `HomeMoveTests`: gru and sjc nodes; a writer commits rows through REST during a move from gru to sjc; every
  acknowledged row is in sjc afterwards, sjc takes writes, and a WebSocket session opened on gru before the move is
  refused a write after it (`08004`).
