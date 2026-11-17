# Home leases and home outages (PIVOT R8)

An acknowledged write is never lost or undone by anything this build does on its own. A database's home, and a region's
writer role, move only through a planned move or a planned drain to a node that has proven it holds everything the
previous holder acknowledged. Nothing moves when a node stops: the database waits, and is unavailable for the writes
that need its home. No node deletes a database file unless another node has proven, at a lease revision read again
just before the delete, that it holds its content. A file that no other copy is proven to cover stays in place and
fenced, or is preserved and inventoried; nothing discards it.
Availability is given up before durability.

## Lease store

Leases live in the JetStream KV bucket `terrabase-homes` of the `TERRABASE` account (`docs/mesh-jetstream.md` in
ts-orchestrator): file storage, history 1, three replicas (one per region) in the cluster, per-key TTL
(`LimitMarkerTTL` 10 s).

| Key | Value | Written by |
| --- | --- | --- |
| `node.<node>` | `{node, region, session}`, TTL 10 s | that node, every 2 s, compare-and-set on the key's revision |
| `home.<databaseId>` | `{organizationId, projectId, databaseId, home, node, epoch, session, adopted}`, no TTL | the node the lease names (create when missing, renew, rewrite after a restart); the target of a planned move (take, then commit); a target rolling a move back (restore); a region's new writer at activation after a certified drain |

- Liveness is per node, so renewal traffic does not grow with the number of databases.
- `epoch` is the database's catalog `placement_version`. Every home change is a compare-and-set on `home.<databaseId>`
  that raises it.
- `session` is a ULID a node picks each time it creates its `node.<node>` key. A node that restarts or whose key expired
  starts a new session.
- `adopted` is the position a planned move's target holds, in the old home's incarnation. Only the move's commit writes
  it; every later rewrite (restart, regional activation) carries it forward.
- A lease key is never deleted.
- Only a proven path writes a lease that names another node: a move's take and commit, its restore, and a regional
  activation after a certified drain. A node confirming its leases renews or rewrites only a lease that names itself;
  when the lease names another node at an epoch below its own placement version it writes nothing, refuses writes and
  logs `Home lease not taken over` (`terrabase.home.refusals{scope="home",reason="unproven"}`).
- A placement change that keeps the home (a replica-set change, or an all-regions database following the serving
  regions) happens only on the home node while it holds the lease, and that node rewrites its lease at the new placement
  version in the same step. A regions change on a node that does not hold the lease answers 503 `unavailable`; an
  all-regions database waits for the next pass. A node confirming a lease that names itself at an epoch below the
  placement version rewrites it at the placement version.
- A region's new writer takes a lease of its own region whose epoch is below the placement version, written by the
  previous writer or by itself, only when the activation proved that database's applied position; the new lease's epoch
  is the placement version. Without that proof the lease is isolated, as before. A lease of another region below the
  placement version is never taken automatically: the database is unserved (`stale_lease`, below) until an operator
  repairs it.

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

## When a home's node stops

1. Every node watches the bucket. When `node.<node>` of a database's home expires, each node logs
   `Home lease lapsed, not moved` once per database and that node's session, and counts it in `terrabase.home.lapses`.
   No node claims the lease: the home stays where it is.
2. While the home is down:
   - reads in every region that holds a pulled replica keep working, from the position that replica last pulled;
   - safe writes that commit in another region's quorum keep being acknowledged there, and their pushes wait in the
     push stream for the home;
   - writes that need the home (unsafe statements, transactions forwarded to the home, writes in the home region whose
     writer is down) fail with a retryable error until the home is back;
   - nothing that was acknowledged is lost, and no other node serves the database as home.
3. When the home's node returns with its disk, it starts a new session and confirms its leases (`Holding a lease`), and
   serves its databases again; pushes that waited are applied in order.
4. A region's writer that stays down does not hand its role to the other node of the region on its own either
   (`regional-rollout-handoff.md`): a planned drain is the only way the writer role moves, and it needs the target's
   certificate (`Regional writer proof`).

The availability trade, plainly: while a home is down, its databases accept no home-committed write; nothing moves by
itself; a home whose node and disk are lost for good stays unavailable for those writes, and this build has no path
that moves it.
The lost home's acknowledged writes that no replica pulled cannot be recovered by any automatic path.

## A lease that names a node that cannot serve

Every node checks every lease it watches on each 15 s scan. A lease whose node has no live `node.<node>` key
(`lapsed`), or is not the current writer of the lease's region (`not_writer`), or that names another region than the
catalog's home at an epoch below the placement version (`stale_lease`), leaves the database unserved:

- nothing serves it as home and nothing claims it; a write that needs its home answers the retryable refusal above;
- each node logs `Database <id> is unserved` once per change of that state, naming the lease's node, region and epoch
  and the catalog's placement version and home, and `is served again` when it ends;
- `terrabase.home.unserved{region}` counts such databases by the lease's region, and `/v1/rollout/status` lists them
  (`unserved`: database, region, node, epoch, cause);
- the old home, if it still holds a file, keeps it in place and fenced, does not serve it and does not take the lease
  back: the lease carries no `adopted`, so nothing proves which copy holds the acknowledged writes. It logs
  `keeps its file in place and fenced` once per lease epoch, and its catalog keeps naming its own region.

## Repairing a stale home lease

An operator repairs a lease that keeps the home writer from serving its database with
`POST /admin/organizations/{organization}/projects/{project}/databases/{database}/home-lease` on the internal listener
of the home region's writer (peer credential or an `admin` caller key):
`ssh <home writer host> 'bash -s' -- ORGANIZATION PROJECT DATABASE < scripts/terrabase/home-lease-repair.sh`.
It changes only `home.<databaseId>`, never table data, and rewrites it with one compare-and-set to this node at epoch
= the placement version. It answers 409 with a code, writing nothing, unless all of these hold:

- this node is its region's writer and the catalog homes the database here with replicas (`not_writer`, `not_home`,
  `not_leased`);
- the lease is older than the placement version, or missing (`lease_newer`);
- the confirmed writer of every other region records the same home and placement version (`writer_unknown`,
  `placement_disagrees`);
- the node the lease names answers that it is not its region's writer, or records an older placement or another home
  (`lease_node_unknown`, `lease_node_disagrees`);
- this node's applied-position proof for the database passes (`unproven`);
- the lease did not change while it checked (`lease_changed`).

A lease this node already holds at the placement version answers 200 `unchanged`; a rewrite answers 200 `restamped`
with the previous node and epoch. Every call is logged once with the caller and counted in
`terrabase.home.repairs{result}`.

## Moving a home (one commit point)

A home moves only through a planned move (R9), to the current writer of the target region:

1. The target refuses with `move_aborted` when it is not its region's writer, or when it cannot name the previous home's
   node (from the lease, or the request's `fromNode`), before anything changes (`terrabase.home.refusals{reason="not_writer"}`).
2. The source pauses writes; the target pulls from the source until a pull changes nothing, so its replica holds the
   source's final position.
3. The target writes a promotion fence (`<data>/.fences/promoting/<databaseId>`: previous region, node and the move's
   version). Until the fence is lifted the target accepts no write, direct or pushed: every write answers the retryable
   `the database is moving its home region`, and a pushed transaction is not acknowledged, so it is delivered again.
   Only the move's own prepare and drain transactions pass the fence.
4. A file already at the target's database path is preserved as a kept copy (`move_target`). The target imports its
   replica as the home file and takes the lease with one compare-and-set, `{home: old}` → `{home: new, node, epoch}`,
   without `adopted`.
5. The target records the new placement, prepares home apply, and applies the pushes still addressed to the former home
   (epoch below the move's). It holds the applied notices those pushes would send to their origins. A push that cannot
   be decoded yet because its data key or the object store is unavailable stops the drain of its origin: the former home
   never applied it either, and the target's sequencer applies it and every later push of that origin in stream order.
   Before the drain applies anything, the target reads its imported copy's frontier on every origin stream once. Every
   drain attempt of the move, including one retried after a broker failure, ends by comparing each origin's purge marker
   with that import frontier: a marker beyond it means a push after the import may have been removed, and the move is
   refused (`drain_failed`).
6. **Commit point**: one compare-and-set on the lease adds `adopted`, the replica's position before the import. It runs
   only after the placement update, the prepare and the drain have all succeeded. Then the target lifts the fence,
   sends the held notices and serves. After the commit point nothing rolls back: the target is the home and the move
   only goes forward.
7. When any step before the commit point fails, the target rolls back: it stops holding the lease and reads it. Only
   two states let the rollback go on: the lease is this move's own uncommitted lease (names the target at the move's
   epoch, without `adopted`), which the target rewrites with one compare-and-set to the previous home's node at the next
   epoch; or the lease already names the previous home's node. The target then records the previous home in its
   catalog when its catalog had taken the new placement, sets its imported copy aside as a kept copy
   (`move_rolled_back`), lifts the fence and answers `move_aborted`. It acknowledged no write before the commit point,
   and the old home never sets its file aside on a lease without `adopted`, so the old home resumes at its paused
   position with nothing lost. In every other state (no lease, a lease naming another node, a lease with `adopted`) the
   target keeps its imported copy in place and fenced, logs `is not rolled back`, and answers `503 move_unresolved`;
   the old home stays paused with its file. The next `promote/abort` tries again. Nothing is deleted.
8. A target that stops between the fence and its answer keeps the fence across the restart. The old home asks again
   (`promote/abort`, every 5 s); the target then reads the lease: committed (names it at the move's epoch with
   `adopted`) means it lifts the fence and answers promoted; not committed means it rolls back as in step 7, under the
   same two states; a lease it cannot read keeps the fence and answers `503 move_unresolved`.

Pushes during a move: every region's writer consumes every origin's push stream with its own durable consumer and
acknowledges a pushed transaction only after applying it, or after its catalog says another region is the home. While
the move runs, the old home is paused and the target is fenced, so neither acknowledges a push; JetStream delivers it
again. After a rollback the target's catalog names the old home, so the target drops its copy of each push and the old
home applies them, including those stamped with the move's epoch. After the commit the old home drops them and the
target applies them. No push is lost either way.

A non-writer never holds a home lease: taking one needs the node to be its region's writer.

## The old home

An old home judges its file only when it is not fenced and has no pending move marker, so it never judges its own move
while the move runs. After a planned move, or when its lease scan finds `home.<databaseId>` naming another node at an
epoch at least its own placement version, it follows the new home and compares its file's final position with the
lease's `adopted`:

- **Proven**: `adopted` is in the same incarnation and at or beyond the final position. The node reads the lease again
  and deletes its file only if the lease's revision is still the one the proof was read at; otherwise it starts over
  (`terrabase.home.demotions`).
- **Not proven**: a lease without `adopted` (an uncommitted move, or a claim by an earlier build), or no lease at all,
  proves nothing about any copy: the file stays in place and fenced and the catalog is not changed. A lease with
  `adopted` that does not cover the final position means the new home serves without part of this file: the file is
  preserved as a kept copy (`move_unproven` after a planned move, `lease_unproven` otherwise). The node never takes the
  home back; the only way a home returns is a planned move, with the same proof as any other.

## Kept copies

A kept copy is a database file a node holds and does not serve. It is set aside through the storage engine's one
preservation path, the same one a regional drain's prepare uses: the store is closed and drained, then the database file
and its sidecars (key, revision manifest, WAL) are moved by rename to `<data>/.handoff/<id>/<organizationId>/<projectId>/`.
The reason (`move_rolled_back`, `move_unproven`, `lease_unproven` or `move_target`) and the position the copy held are
logged when it is preserved.

- No automatic path deletes, truncates or overwrites a kept copy. Only the erasure of a deleted database removes its
  preserved copies, as it removes every other file of that database.
- A path that needs a database file's place (a planned move to this node, a regional drain's prepare on this node)
  preserves the file and proceeds; it never deletes it and never fails because of it.
- Before a kept copy is preserved, a completed detached import of that database (published or put back) moves with its
  import record to the replica's `.retained` archive, so preserving the imported file never leaves a published import
  without its destination.
- The local key inventory counts every preserved file: `terrabase.storage.retained_files` and
  `terrabase.storage.retained_bytes` measure them, and a non-empty preserved file makes the key-retirement counts
  unknown. `/v1/rollout/status` lists the kept copies (`kept`: `{id, organizationId, projectId, databaseId, files, bytes}`).
- Removing kept copies is an operator action and not part of this build.

## Regional writer proof

Only the certificate the target writes in the drain protocol (`prepared.<region>.<target>`, written by the target after
it applied every position the source certified) authorizes a new owner. The owner record's own `prepared` field is never
read. The one exception is a first writer: an owner record at epoch 1 with no previous node needs no certificate, so
rewriting the key to such a record names that node as writer, subject to the activation check below. Every other
rewrite of the owner record is refused: the named node logs `Regional writer refused` once per revision of the owner
record and stays a non-writer (`terrabase.home.refusals{scope="writer",reason="unproven"}`).

On every activation, including a first writer at epoch 1, the node checks each database homed in the region whose home
lease (when there is one) names this region. A database whose local replica is newer than its primary file, whose replica of another incarnation holds a user
table, or that has a replica and no primary file (`reason="replica_newer"`, `reason="primary_missing"`), is fenced: the node still becomes the
region's writer and serves every other database, but it does not take that database's home lease and answers each write
to it, and each read of it on any protocol (SQL over HTTP, the PostgreSQL wire, shapes, exports), with the retryable
503 `unavailable` (PostgreSQL SQLSTATE `57P03`): its primary file may miss an acknowledged write, so the node serves no
read from it until the fence lifts. A database whose lease names another region is not served by
this region's writer, so it is not checked. A replica of another incarnation that holds only the engine's own tables proves no write is missing, so it fences nothing. Before comparing, the node catches up the replicas whose position it cannot
read through their followers, together and once per page of homed databases, within 1 s, so the check ends inside one
2 s activation attempt. A replica whose position still cannot be read (its last pull failed, or it is reopening or was
reopened from a file it pulled before and has not pulled since) proves nothing; a replica still being created holds no
data and is not compared, so that database is fenced too (`reason="position_unknown"`); no earlier position is
remembered as proof. A check that fails to read a database's files (for example a database whose detached import
recovery was refused) leaves that one database unproven and fenced the same way, with the error in `detail`; it never
stops the activation or renewal of the region's other databases. A fence is logged and counted once per database, cause and epoch
(`terrabase.home.refusals{scope="database"}`). Every writer renewal (about every 250 ms) checks each fenced database
again, catching up the replicas whose position it cannot read first; once its primary file covers its replica, the node
takes its home lease and lifts the fence, so a transient read failure clears by itself. A lease left in place,
isolated or lost to a concurrent update does not lift a fence; a database whose home has left the region is no
longer served from this node's primary file, so its fence lifts. After taking the lease, the node lifts the fence
only when its primary file covers the lease's adopted position or its readable local replica.
A replica that cannot reopen is set aside as `<database>.db.retained.<id>*`. Recovery keeps these files,
including when the replacement replica's first pull fails. While any set-aside copy exists, the replica position
stays unknown, even after a replacement pulls successfully: that pull does not prove the primary file covers the
set-aside copy's writes, and a set-aside copy's own position cannot be read (a replica learns its position only from
its pulls). Such a database stays fenced on that node, with its set-aside copy left in place, until one of these proves
the primary file covers every acknowledged write: a drain onto the node, whose prepare catches the replica up to the
position the source writer certified (the highest position acknowledged at that epoch; a source that has the
database fenced marks its position `fenced`, which proves nothing), imports it as the primary file, and only then moves the set-aside files to `.replicas/<organizationId>/<projectId>/.retained/<id>/`, so the
fence does not return on the next activation; a home lease whose adopted position the primary file covers (a home
move onto the node), which lifts the fence and leaves the copy in place; or its home leaving the region. No other path
lifts the fence, so a SIGKILL that tears a replica during sync costs that node the database only until the next drain
onto it; any other node of the region can take the writer role and serve it meanwhile. Archived and in-place
set-aside files count in `terrabase.storage.retained_files` and `terrabase.storage.retained_bytes`; recovery never
deletes them, and only database erasure removes them.
A user read first checks the database fence, then requires that the owner record names this node and its activation
check for that owner's epoch has finished. Otherwise it answers the retryable 503 `unavailable`. Replica pulls and
checksum rounds also check the fence, but may use either the activated current owner or a node retired from an
activated term that proved that database. A database created after retirement has no such proof. A retired source
also refuses unless its primary still covers its local replica.
Source eligibility ends as soon as a new owner record names this node, before activation replication starts.
The pull checks eligibility again when its snapshot starts streaming, and a checksum round checks again before
returning. This lets the next writer catch up from its previous writer without exposing an unproved primary.
Revision notices carrying pages check the same activation and database
fence before building their payload and again before publishing it. A commit checks its database fence after its
writer check, so a role change during that check cannot let it commit an unproved copy.
`/v1/rollout/status` lists the
fenced databases in `fenced` `[{databaseId, reason, epoch, detail}]` and `terrabase.regional.fenced{reason}` counts
them, so a database that stays fenced is visible, never silent. While the node is not ready, `/v1/rollout/status`
names the condition that holds readiness in `unready` (`stopping`, `catalog_closed`, `platform_seed_pending`,
`regional_writer_unknown`, `home_push_stopped`, `home_leases_pending` or `storage_unready`), and a node that stays
not ready for 10 s logs that condition once per change. On demotion, fences leave the status list and gauge but stay
in the serving gate as retired fences. The next activation releases retired fences before checking every database
again; the node remains ineligible to serve or publish until that activation finishes. A missing owner key with existing data is therefore not a new region. A genuinely new region
has no homed databases, so its first node in name order becomes writer at epoch 1; a restarted writer whose primary files
are current passes the same check.

The new writer's lease rewrite (`TakeRegionalAsync`) carries `adopted` forward. A regional drain's prepare imports the
target's replica as its primary file; a file already at that path is preserved the same way and listed as a kept copy.

## Mixed builds during a rollout

A node on an earlier build still claims a home automatically when its lease lapses for about 15 s. A node on this build
never deletes its file because of such a claim: the claim carries no `adopted`, so the old home keeps its file in place
and fenced. The claimant serves the database when it is its region's writer; otherwise the database is unserved
(`A lease that names a node that cannot serve`). Release order:

1. Upgrade every non-writer node first.
2. Upgrade each region's writer only after its planned drain has finished and every lease of the region's databases
   names the new writer.
3. Stop on any refusal (`terrabase.home.refusals`), unserved database (`terrabase.home.unserved`) or new kept copy.
4. While any node runs the earlier build, no writer may be down for longer than 10 s.

## Every write path carries the epoch

- Home commits: the lease check above.
- Home sessions (`/v1/internal/sql/session`): the open frame carries the replica region's placement epoch; a home whose
  placement version differs refuses with `wrong_region`, so the caller reads its placement again.
- Home apply (R6, R7): applies commit through the same engine path, so a node without the lease refuses them.

## Durability and RPO

| Mode | Acknowledged when | Lost only if the home's disk is lost for good |
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
- Every replica then holds every `all` write.

## Metrics

- `terrabase.home.lapses`: home leases whose node lapsed while this node watched; the home stayed where it was.
- `terrabase.home.refusals{scope, reason}`: home moves and lease writes (`scope="home"`) and regional writer changes
  (`scope="writer"`) and databases the regional writer fenced (`scope="database"`) this node refused, by reason (`not_writer`, `lease_changed`, `placement_refused`, `prepare_failed`,
  `drain_failed`, `unproven`, `replica_newer`, `primary_missing`, `position_unknown`).
- `terrabase.regional.fenced{reason}`: databases of this node's region that refuse writes because the writer's activation
  check cannot prove their primary file covers their replica; `/v1/rollout/status` `fenced` names them.
- `terrabase.home.unserved{region}`: databases whose lease names a node that cannot serve them, by the lease's region.
- `terrabase.storage.retained_files`, `terrabase.storage.retained_bytes`: the kept copies and other retained files on this node.
- `terrabase.home.demotions`: database files this node deleted after the new home proved it holds their content.
- `terrabase.home.leased`: databases whose writes on this node need its lease.

## Known gaps

- `durability` is accepted on SQL REST only; the WebSocket frame field and `SET terrascale.durability` follow.
- A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
- A database whose lease names a node that cannot serve it, or whose home's node and disk are lost for good, stays
  unserved: this build has no path that serves it again.
- Kept copies are never removed automatically, including those of a database a user deleted; each one is disk the
  operator reclaims. A regional drain back to a former writer sets aside that node's earlier primary files.
- The activation check cannot compare a replica whose follower could not reach its source within the 1 s it gives each
  page of homed databases (their replicas catch up together, 16 at a time, once per page, so that one activation
  attempt of 2 s can finish).
- Setting a file aside renames the database file first and then each sidecar; a crash between two renames leaves a
  sidecar at the old path.
- The applied notices a target held for drained pushes are not sent when the target stops between its commit point and
  sending them; the origins read the outcome from the home apply state.
