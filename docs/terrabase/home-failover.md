# Home leases and home outages (PIVOT R8)

An acknowledged write is never lost or undone. A database's home, and a region's writer role, move only to a node that
has proven it holds everything the previous holder acknowledged; without that proof TerraBase waits, and the database is
unavailable for the writes that need its home, rather than moving. No node deletes a database file unless another node
has proven it holds its content. Availability is given up before durability.

## Lease store

Leases live in the JetStream KV bucket `terrabase-homes` of the `TERRABASE` account (`docs/mesh-jetstream.md` in
ts-orchestrator): file storage, history 1, three replicas (one per region) in the cluster, per-key TTL
(`LimitMarkerTTL` 10 s).

| Key | Value | Written by |
| --- | --- | --- |
| `node.<node>` | `{node, region, session}`, TTL 10 s | that node, every 2 s, compare-and-set on the key's revision |
| `home.<databaseId>` | `{organizationId, projectId, databaseId, home, node, epoch, session, adopted}`, no TTL | the database's home, the target of a planned move, a regional writer at activation, an old home taking its database back |

- Liveness is per node, so renewal traffic does not grow with the number of databases.
- `epoch` is the database's catalog `placement_version`. Every home change (a planned move, R9, or an old home taking its database back) is a
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

A home whose node and disk are gone for good needs an operator: there is no automatic takeover and, today, no takeover
call. The lost home's acknowledged writes that no replica pulled cannot be recovered by any automatic path.

## Moving a home (all or nothing)

A home moves only through a planned move (R9), to the current writer of the target region:

1. The target refuses with `move_aborted` when it is not its region's writer, before anything changes
   (`terrabase.home.refusals{reason="not_writer"}`).
2. The source pauses writes; the target pulls from the source until a pull changes nothing, so its replica holds the
   source's final position.
3. The target imports its replica as the home file and takes the lease with one compare-and-set,
   `{home: old, epoch}` → `{home: new, epoch + 1, adopted}`. `adopted` is the replica's position before the import,
   in the old home's incarnation: the proof that the new home holds what the old home acknowledged.
4. The target records the new placement and prepares home apply. When either fails, it rolls back: the lease returns to
   the old home's node and session at the next epoch, the target's catalog records the old home at that epoch, the
   imported copy is removed (the old home holds all of it), and the move answers `move_aborted`. The old home's catalog
   follows its own lease's epoch, so every region agrees on the version.

A non-writer never holds a home lease: taking one needs the node to be its region's writer.

## The old home

An old home never deletes its file on its own. When it confirms its leases and `home.<databaseId>` names another node at
an epoch at least its own placement version, it reads its own file's final position and compares it with the lease's
`adopted` position:

- **Proven**: `adopted` is in the same incarnation and at or beyond the final position. The old home follows the new
  home as a replica and removes its file (`terrabase.home.demotions`). A planned move's source does the same after the
  promote answer, and only with this proof.
- **Not proven, and the recorded node cannot serve the database**: the lease names a node that is not the current
  writer of its region, so that node never committed a write as home. The old home, whose copy is the most complete,
  takes the home back: one compare-and-set to `{home: self, epoch + 1}`, its catalog follows, it drains the pushes
  addressed to the recorded home and serves again (`terrabase.home.reclaims`, `This node took a database back as home`).
  The recorded node keeps its stale lease history and any file it imported; nothing is deleted there either. It follows
  the new lease as a replica region, and an operator removes its leftover file.
- **Not proven, and the recorded node is its region's writer**: that node may have committed writes the old home lacks,
  and the old home may hold writes it lacks. The old home keeps its file, stops serving the database as home and logs
  `Old home kept its copy` (`terrabase.home.kept`). It checks again on every lease scan (15 s), so it takes the home
  back if the recorded node stops being its region's writer. An operator decides which copy wins.

## Regional writer proof

Only the certificate the target writes in the drain protocol (`prepared.<region>.<target>`, written by the target after
it applied every position the source certified) authorizes a new owner. The owner record's own `prepared` field is never
read, so rewriting the owner record, `prepared` field or not, is refused: the named node logs `Regional writer refused`
and stays a non-writer (`terrabase.home.refusals{scope="writer",reason="unproven"}`).

On every activation, including a new owner key at epoch 1, a node refuses to become writer while any database homed in
the region has a local replica newer than its primary file, or a replica and no primary file
(`reason="replica_newer"`, `reason="primary_missing"`). It reports the database and both positions and stays a
non-writer for that epoch. A missing owner key with existing data is therefore not a new region. A genuinely new region
has no homed databases, so its first node in name order becomes writer at epoch 1; a restarted writer whose primary files
are current passes the same check.

## Mixed builds during a rollout

A node on an earlier build still claims homes automatically when a home's lease lapses. A node on this build never
deletes its file because of such a claim: it takes the home back when the claimant is not its region's writer (the
claim could not have been served), and keeps its copy and waits for an operator when the claimant is its region's writer.
Release instruction: upgrade every node before any region's writer is stopped for longer than the lease (10 s), and
restart writers one at a time through the planned drain.

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
- Every replica then holds every `all` write, so a replica an operator promotes after a home is lost for good keeps them.

## Metrics

- `terrabase.home.lapses`: home leases whose node lapsed while this node watched; the home stayed where it was.
- `terrabase.home.refusals{scope, reason}`: home moves (`scope="home"`) and regional writer changes (`scope="writer"`)
  this node refused, by reason.
- `terrabase.home.reclaims`: databases this node took back as home because the recorded home could not serve them.
- `terrabase.home.kept`: database files this node kept as a former home because the recorded home has not proven it
  holds them.
- `terrabase.home.demotions`: databases this node stopped homing after the new home proved it holds their content.
- `terrabase.home.leased`: databases whose writes on this node need its lease.

## Known gaps

- `durability` is accepted on SQL REST only; the WebSocket frame field and `SET terrascale.durability` follow.
- A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
- No operator call promotes a replica when a home's node and disk are lost for good.
- The activation check sees only the replicas this node has open; a node that restarted and has not reopened a replica
  cannot compare it with its primary file.
