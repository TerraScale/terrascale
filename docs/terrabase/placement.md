# Database placement

A database has one set of selected regions. All regions are equal in the public placement model. TerraBase internally keeps one writer and synchronizes the other selected regions; this API change does not add independent multi-writer consensus or change SQL write routing.

## API

Placement changes require `databases:write`. Send them to any region; TerraBase forwards them to the writer when necessary.

| Operation | Request | Answer |
|---|---|---|
| `createDatabase` | `POST /v1/projects/{project}/databases` `{"name":"main","regions":["gru","sjc"]}` | `201` database |
| `updateDatabase` | `PATCH /v1/projects/{project}/databases/{database}` `{"regions":["sjc"]}` | `200` database |
| `moveDatabaseHome` | `POST /v1/projects/{project}/databases/{database}/move-home` `{"homeRegion":"sjc"}` | `200` database |

`regions` is a JSON array with at least one serving region id, without duplicates. Unknown, non-serving, duplicate and empty sets answer the existing `400 invalid_request` envelope. Database responses include `regions` sorted by region id, the catalog order. Response order carries no meaning and does not identify the writer. Database responses also carry `placementVersion`, the current placement epoch; an unchanged region-set request retains it.

On create, the first region in the caller's array is the initial writer. On update, the writer stays if it remains selected. Otherwise TerraBase adds missing requested regions and keeps them as members so their followers can synchronize. A call that adds regions returns `503 replica_not_ready` without attempting promotion. The intermediate placement is the old writer and existing members plus the requested regions. If no regions need adding, TerraBase can promote on the first call: `{a,b}` to `{b}` returns `200` when `b` is ready. Nothing is unstaged on any refusal, and `regions` reports that superset meanwhile.

Repeat the same request after a requested region has synchronized this database at least once. TerraBase reads the engine's per-region pull observations and selects the first requested region, in region-id order, that has synchronized this database at least once; the move brings it up to date and refuses with 503 replica_not_ready when it can't. It tries at most that one candidate through the existing move. With no ready candidate it returns `503 replica_not_ready` immediately. A repeat of the same region set during its pending move also returns `503 replica_not_ready`; a different request's move retains `409 move_in_progress`. If a race removes the candidate before the move begins, the response is retryable `503 replica_not_ready`, and the repeat recomputes from the current placement. Promotion records the final selected region set at both the previous and new writer, removing every unselected region in the same placement update. Moving from `{"regions":["gru"]}` to `{"regions":["sjc"]}` therefore requires at least two calls: add, then move and remove. Each call is idempotent: placement versions change only when membership, the writer or all-regions mode changes. A rename is sent separately from placement. Explicit `regions` turns off all-regions placement.

The requested placement and any intermediate superset must pass the existing limit of eight copies other than the writer (nine total members). A set or superset that exceeds it returns `400 invalid_request` with `regions can place a database in at most 9 regions, including the writer and any intermediate placement` before any membership is written; remove other non-writers first to make room. A retryable refusal leaves the current valid placement in place, including additions. An unresolved move returns its existing refusal and is settled by the existing move mechanism before the caller continues.

For the placement fields currently exposed by the API, requests using `homeRegion`, `replicaRegions` or `allRegions`, and responses containing the old fields, keep working. The old-field PATCH retains its placement-version bump even when repeated without a membership change. It now returns `409 move_in_progress` while a move is pending: changing membership then could consume the move marker's next version and prevent the source catalog from recording the promoted writer, while the target holds the membership captured for promotion. Combining `regions` with any of those placement fields is invalid. The transitional fields retain these rules:

- `replicaRegions` names at most 8 distinct serving regions other than the writer, and turns all-regions mode off.
- `allRegions: true` places a copy in every serving region other than the writer. The writer keeps the copy set equal
  to the serving regions: a region that joins (`TS_PEERS_FILE`) is added, and one that leaves is removed, within one
  copy sweep (30 s). During a pending move, following changes nothing and resumes on the next pass after the move settles. Sending both fields is refused.
- `move-home` names one of the regions that hold the database. It answers when the move is done; the old writer is then a
  region that holds the database. Moving to the current writer answers the database unchanged.

| Error | When |
|---|---|
| `400 invalid_request` | bad or unserved regions; `homeRegion` is not a region that holds the database |
| `409 move_in_progress` | another move of this database is running |
| `409 name_taken` | another database of the project has this name in the target region; rename one and repeat |
| `503 replica_not_ready` | no requested copy can take over yet; added regions remain members (move-home itself changes nothing on this refusal) |
| `503 move_unresolved` | the move outcome is being settled with the target; writes stay paused until it is; retry |

## Database identity across regions

Catalog entries carry an optional `identity` containing the original `createdAt`, `branchedFrom` and absolute
`expiresAt`. The writer supplies these facts on catalog pages and placement lookups. Followers reconcile the
name and identity even at an unchanged placement version, so the next successful catalog sweep repairs records
adopted before this change while an upgraded writer still has the original facts. A rename changes only `name`;
`previousName` and `conflictOf` are currently always null and have no stored catalog columns.

Promotion also carries the paused writer's name and identity, so an unswept TTL or rename reaches the new writer.
Renames and TTL changes answer `409 move_in_progress` while a move marker is held. Expiry retains the absolute
deadline; moving never restarts or shortens its TTL. These additions change JSON peer messages, not storage.
Older senders omit identity and retain the former adoption behavior until a later sync supplies the facts;
older receivers ignore the extra JSON members. If a database already moved and its current writer has lost the
original facts, a follower keeps its earlier creation time and branch origin (or fills a missing origin from the writer)
when the writer's creation time is later. Expiry is mutable: it always comes from the current writer, including a cleared
or extended TTL, regardless of which creation time wins. The writer never takes identity facts from a follower. Its listing stays lossy until the writer
moves back to a region that retained the earlier facts; promotion preserves those facts by the same rule.
An identity with a missing or default creation time is treated as absent.

Names are unique within each region's project catalog. Renames reach followers through the writer's catalog sweep,
or through the paused writer's name carried during promotion. When another database whose writer is at that same source holds
the name, the follower resolves up to eight names from the listing or the existing single-database placement request.
Preparation captures each discovered member's id, name, writer and placement version in short read transactions,
releasing each before resolving the next holder from the listing or network. One write transaction rechecks every
captured member and every destination, then applies the placeholder and final renames atomically. A changed snapshot
applies no names and returns retry: the next sweep or repeated promotion prepares again. There is no network I/O in
the write transaction. An unchanged listed entry opens zero write transactions: N unchanged entries cost zero writes.
Missing-listing lookups share a three-second budget across the entire writer sweep (all pages), starting at the first
network lookup. After it expires, entries needing a lookup keep their names for this sweep; listing-only chains and
other catalog facts still proceed. Promotion uses the same rule before detaching, importing or taking the lease.
Its name preparation stays after the source pause because it checks the final paused name; its network lookups add
at most three seconds to that pause, outside catalog transactions and within the existing 60-second peer timeout.
Unavailable or non-writer answers, missing identity and longer chains keep the stored name for the next sweep, counted
and logged once for the current kept name per database, cleared when a name is applied or the database is forgotten.
A missed rotation of nine or more names cannot converge by itself: rename one database to a free temporary name,
let a sweep apply it, then rename it to the intended name and let another sweep run.
Different databases with writers in different regions can still have the same name in disjoint regions: a follower keeps its
stored name on that conflict while applying other facts. A regions change that would move the writer into a region
where another database of the project already has this name answers `409 name_taken`, from the promotion peer and
the regions API. Rename one of them and repeat. Older placement responders omit identity, so their answers cannot authorize a
chain. Catalog sync also runs from handoff paths; a follower can show the old name for up to one sweep.

## Moving the writer

Region A is the writer and region B the target. A coordinates; B's copy becomes the writer file.

1. **Catch up.** A asks B to pull now (`POST /v1/sync/…/databases/{database}/catch-up` on B's internal listener), while
   writes continue.
2. **Pause.** A records a move marker in its system database and a fence file under `<data-directory>/.fences/`, then
   waits for the write transactions in flight to finish. New write transactions wait at the fence (at most 30 seconds)
   before they take the database's writer. Reads and page pulls continue; a pull skips its CDC retention write while the
   database is fenced (the next pull records it), so the paused writer's revision stays fixed.
3. **Promote.** A asks B to promote at the next placement version (`POST …/promote`). B stops its follower, pulls until
   a pull brings nothing new (two pulls: the second must be empty, which proves A is paused), converts its copy into
   a writer file (a consistent copy into a new file under a new data key, as a restore does; a new incarnation, so every
   copy bootstraps again), and records itself as writer in its catalog with the next placement version.
4. **Demote.** A fences the database as moved (below), which ends the waiting writes, records B as writer and itself as a
   copy in its catalog, deletes the marker and the fence file, and deletes its writer file. A then follows B like any
   copy.

The epoch of a database is its placement version: B is writer at version `v + 1` only after A stopped writing at `v`, and a
region accepts a placement only when it is newer than the one it holds.

A request through a region that is no longer a member is proxied to the current writer. Its former copy can serve reads until the local catalog observes the removal ([removing a region](#removing-a-region)).

## Removing a region

A region leaves a database's set when a placement request no longer names it. The writer H records the new placement at the next placement version and sends the removed region R nothing. R acts in its own catalog pass ([region copies](region-replicas.md#how-a-replica-follows-its-home)), which runs every 30 seconds and at startup. R keeps a routing record after removing its local copy; removal never authorizes retirement or backup deletion.

| Step | What R does | A stop of R here leaves |
|---|---|---|
| Observe | R's record names H as writer and R as a member, and H's listing for R no longer names the database. R reads the placement H records (`GET /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/placement`) and writes it to its record when it is newer. | The record as it was. The next pass observes again. |
| Erase | In the same pass R stops its follower and deletes the copy's files under `.replicas/<organization>/<project>/`: the pages, the write-ahead log, the pending local changes, the sync state and the wrapped data key. A region that was the writer deletes its writer file, with its data key, when the writer moves away ([moving the writer](#moving-the-writer)). If that deletion fails, the orphan sweep deletes the writer file after its 24-hour grace period, while retaining the routing record and managed snapshots. | A record that does not name R. Every later pass deletes what is left of the files again. |
| Keep current | After reconciling followers, R refreshes at most 64 removed records per writer region with one shared five-second budget. A cursor carries unfinished rounds to the next pass. Each record is refreshed at most once every ten minutes; at most 4,096 recent attempts are held in memory, and further refreshes wait for a slot. Each page of a writer-region listing has its own five-second budget; listing failures are logged at Warning. Placement, name and identity follow the same catalog and name-chain reconciliation as a member, including renames without a placement-version change. The answering node returns `503 not_writer` unless it holds the regional writer lease; it rechecks ownership after a missing-record read before returning `404`. A `404` authorizes forgetting only when the contacted node matches the regional writer observed before and after the request. Without NATS, the region must have exactly one configured node. A standby, an unobserved writer or a writer change leaves the record intact for a later round. | The record, which the next pass reads again. |

### What R answers

R keeps its record of the database, with the placement that no longer names R, as the routing record of that database. It serves nothing from it:

- a REST request that names the database (management `GET`, regions, SQL) is proxied to the writer, preserving its method, query, body and credential;
- Postgres and WebSocket sessions and shape requests are relayed to the writer, as before ([forwarding between regions](cross-region.md#forwarding-between-regions));
- the project's database listing leaves out the record of a region that the record's own placement does not name, so the database is listed once, with the name and placement its writer records;
- file and bucket requests of the project do not depend on a database's placement and answer as before.

A region that never held the database holds no record and passes REST requests on to the writer instead ([forwarding between regions](cross-region.md#forwarding-between-regions)). The retained record lets a removed region route directly to the writer.

Removing a region is not a deletion of the database: R writes no retired id, no purge record, no cleanup intent and no backup sweep, and sends nothing to any region. It deletes only its own copy files.

An unanswered or rejected removed-record refresh is counted and never stops follower reconciliation: `terrabase_replica_removed_record_errors_total` with the labels `region` (the writer) and `error` (`unanswered`, `catalog_rejected`, or the exception type), and a warning at most once every 10 minutes for each writer and error.

### Adding the region again

H lists the database for R again and R's next pass, at most 30 seconds later, takes the placement that names it. Before it writes that placement R deletes the copy's files again, so a copy whose erasure was interrupted is never continued: the follower starts from an empty copy and pulls every page. Nothing refuses the request that adds R. Until R's pass has run, requests through R are still proxied to the writer, and R's row in `GET …/regions` shows no pull.

### What stays in a removed region

- The routing record: the database's id, project, name, identity and placement. Member sweeps and removed-record refreshes reconcile names from the current writer; a name conflict or failed adoption retains the existing record and is counted and logged.
- Managed snapshots the region took while it was the writer. Their restore answers `409 backup_region_not_placed` ([recovery copy placement](recovery-replica-placement.md)).
- The database's state in the region's catalog outside its record: functions, backup policy and schedule, usage and request logs. A move of the writer does not carry them either.
- The record of a database whose writer's region is no longer in the peers file: nothing answers for its placement.

### Fencing

A node keeps a fence per database it no longer writes, checked after a write transaction takes the database's writer:

- **Paused:** during the move, write transactions wait; a write that passed the fence just before the pause and takes
  the writer during it fails with `serialization_conflict` (retry).
  The HTTP `409` refusal occurs before the transaction begins and applies no writes; retrying the same row ids is safe; once removal is observed the former writer proxies new requests.
- **Moved:** the server closes sessions pinned to the previous writer: WebSocket close 1012 and PostgreSQL FATAL
  57P01. An open transaction is rolled back, including writes whose statement answers were already received.
  Reconnect for later work. A commit already in flight when the connection drops has an unknown outcome; inspect
  the committed state before deciding whether to send it again.
- **Unresolved:** after a restart with a move marker, or when A could not learn the promote outcome, writes fail with
  `tenant_busy` (retry) until A settles the move with B.

A settles an unresolved move by asking B (`POST …/promote/abort` with the move id): B answers whether it promoted. If it
did, A demotes; if not, B refuses that move id from then on and A lifts the fence. While B is unreachable A stays fenced,
so two regions never accept writes for one database. A retries every 5 seconds and at startup.

A delete and a move cannot both win: while A holds the move marker it still owns the record, so `deleteDatabase` (and a
TTL expiry) at A answers `409 move_in_progress`; once the move settles, the delete follows the database to its new writer.

### Timings

The pause lasts from step 2 to step 4: one incremental pull, an empty pull, the copy of the database into its new file and
two catalog writes. The copy grows with the database size. The move logs `catch_up_ms`, `pause_ms` and `total_ms`, and
records `terrabase.placement.move.pause` (ms). A measured move (two nodes on one host, a small table under REST
writes) took 512 ms: 227 ms catch-up and a 276 ms write pause.

All-regions placement remains authoritative when a lease observation carries an empty explicit copy list.
Catalog discovery, following, revision notices, copy status and writer sessions use the all-regions membership
rule; revision notices go to the currently discovered peer regions. This preserves synchronization after a writer move
and automatically includes newly serving regions. A push stream that returns NATS error 10074 is not retried, because the stream wants more copies than the cluster has.

## Connecting through any region

An SDK needs no knowledge of a database's region. Connect to any serving region. A region that does not hold the
database validates the customer's credential before discovering a destination or opening a downstream connection.
For browser SQL WebSockets it accepts the upgrade and checks the first auth frame locally, with the same 16 KiB
and five-second bounds as a local session, then replays that frame to the destination for its independent check.
REST, header-authenticated WebSockets and PostgreSQL complete project, database, scope and client-address authorization before destination discovery or connection. Restrictions that cannot be resolved locally fail closed.

A proxied request pays one additional inter-region round trip, about 170 ms between the current regions. It passes
two request admissions: the receiving region protects its forwarding resources, and the destination protects its
execution resources. Each region applies the organization's rate and concurrency limits and returns the usual
busy response and retry hint. Completed requests and bytes are attributed to the final destination; terminal forwarding failures are counted at the receiving region. Intermediate placement refusals are not counted. Operation units are recorded where executed. Session
limits are separate from request admission; relayed-session admission attaches where each relay opens a session.

The proxy carries remaining request time as a duration, so clocks in different regions need not agree. Discovery,
connection, sending and waiting for the response share the original request budget; accepting a connection does
not reset it. WebSocket and PostgreSQL statements have their own five-second transport budget. The proxy never
replays an unanswered write. After the request body has been sent, a dropped leg or expired deadline can leave
the outcome unknown: `write_outcome_unknown` for REST/WebSocket and SQLSTATE 08007 for PostgreSQL. An idle open
transaction whose session ends is rolled back; a commit already in flight may have committed. A proven single read on a WebSocket or PostgreSQL session returns ordinary unavailable instead (SQLSTATE 08006 for PostgreSQL). Multiple or unparsed statements and transactions with earlier writes retain an unknown outcome.

When a destination refuses a request because it does not hold the database, it has done no database work. The
receiving region invalidates the cached route and refreshes discovery once, at most once per database per second.
It may make a second attempt only when discovery names a different region, within the same deadline. Each attempt
has at most one inter-region hop. An unchanged or missing destination answers retryable 503 with `Retry-After: 1`.
