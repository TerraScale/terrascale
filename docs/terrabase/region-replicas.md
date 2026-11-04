# Region replicas (PIVOT R3)

A database has one home region, which serves its writes, and up to eight replica regions. A replica region holds a
read-only copy that it pulls from the home with Turso's official sync client in page mode, through the home's
[in-process page sync host](page-sync-host.md). The topology is a star: every replica pulls from its home, nothing is
ever pushed, and a replica can always be deleted and pulled again.

## Placement

- `POST …/databases` takes `replicaRegions` (distinct serving regions other than the home, at most 8) or `allRegions`.
  `PATCH` changes them; the home raises the placement version on every change. `move-home` moves the home to a replica
  region. See [placement](placement.md).
- Only the home holds a database file and backs it up. A replica region records the database, with the home's name and
  placement, and keeps its replica under `<data-directory>/.replicas/<organization>/<project>/<database>.db`.

## What a replica region serves

- `POST /v1/projects/{project}/databases/{database}/sql/execute` with only read statements runs on one snapshot of the
  replica, under the home's row, byte and time limits, and is metered as reads in the replica region.
- A statement that writes, a request before the replica's first pull, and every other data API answer
  `421 wrong_region` naming the home (`ts-replay: region=<home>`), so the edge replays them there.
- **Postgres wire and SQL WebSocket** sessions stay in the region they reached. A read outside a transaction runs on the
  replica; so does a READ ONLY transaction (`BEGIN READ ONLY`, and every read-only WebSocket `execute` frame), on one
  replica snapshot. Read statements are described (Postgres Parse/Describe) on the replica; writes are described at the home, where they run. Postgres catalog
  queries (`\d`, `pg_tables`) list the replica's tables.
- A REST read is eventually consistent: a replica may lag its home.

## Writes and transactions

A write, a transaction that is not READ ONLY, and a read the replica cannot answer yet run on one SQL session at the
home: `POST /v1/internal/sql/session` on the home's internal listener, with the peer credential, over the private mesh.
Both bodies are streams of length-prefixed JSON frames (4-byte big-endian length, at most 24 MiB each, reserved against
the memory budget before they are read): an open frame naming the organization, project, database, replica region and
whether the session may write, then `execute` and `describe` calls, each answered in order with the result or error
and whether the home session is outside a transaction. The home serves only a database it homes whose placement names
the calling region. The replica region checks the client's credential, scope and IP allowlist before every statement,
as it does for its own reads, so revocation cuts both legs. Ending the request body ends the home session and rolls back
its open transaction. When the home cannot be reached, a write answers `unavailable` (SQLSTATE `57P03`) naming
the home; reads keep working from the replica.

## Read-your-writes

Each node keeps a pull position that grows when any replica pull starts. After anything a session ran at the home, it
takes the node's position and asks its replica to pull now. A later read of that session runs on the replica only when
the replica's last completed pull started after that position, so it holds the write; until then the read runs at the
home. A session that never wrote reads from the replica straight away. REST requests carry no session and get
eventual consistency.

## How a replica follows its home

1. **Catalog.** Every 30 seconds, at startup and when a revision notice names an unknown database, each node asks the
   other regions' nodes (from `TS_PEERS_FILE`, on their internal listener with the peer credential) for the databases
   they home that place this region as a replica: `GET /v1/sync/regions/{region}/databases`. It enrolls their
   organization and project as a project token would, records each database with the home's placement, and starts a
   follower. A recorded database the home no longer lists is checked with
   `GET /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/placement`, which answers the
   placement that region records, home or not: `404` means it was deleted, so the region forgets it and deletes the
   replica; otherwise the region takes the placement when it is newer and deletes the replica when it no longer names
   this region. A follower whose database's home changed (a [move](placement.md#moving-the-home)) is stopped, its
   replica deleted and pulled again from the new home. The peers file is read again on every sweep, so a region that
   joins is replicated without a restart.
2. **Notices.** After each commit that advances a database's revision, its home publishes
   `terrabase.revision.<home>.<database>` on NATS (`TS_NATS_URL`, token from `TERRABASE_NATS_TOKEN_FILE`) with the
   organization, project, database and commit time. Notices are coalesced per database and never delay the commit.
3. **Pulls.** A follower pulls once per batch of notices, at most 8 pulls at a time per node. A failed pull is retried
   with backoff from 50 ms to 2 s until it succeeds. Every replica also pulls on the 30-second sweep and after every
   NATS reconnect, so lost notices and network cuts converge. Without NATS settings a node logs a warning and replicas
   follow on the sweep alone.
4. **Restarts.** A restarted replica region reopens its replicas from their files and serves them before the home is
   reachable again; the next pull is incremental. A restarted home keeps its durable revision ledger, so replicas pull
   incrementally or, for a rewritten or restored home, take a full answer.

## Anti-entropy (PIVOT R11, D18)

Replicas never silently diverge from their home.

1. **Exact revisions.** Every pull answer carries the home's snapshot revision in the `TerraBase-Revision` header, so a
   replica knows the exact revision (`ts1:<incarnation>:<generation>:<frame>`) it holds after each applied pull.
2. **Rounds.** A follower runs a verification round about a minute after it starts and every 10 minutes after each
   completed pass. It asks its home for
   `GET /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/checksums?revision=<held>`: the home
   takes a read snapshot, and only when it is still at that revision hashes the schema and every table in rowid ranges of
   at most 256 rows and 4 MiB (SHA-256 over typed values). A round stops after 64 ranges or 16 MiB and returns a cursor;
   the next round continues 5 seconds later until the pass covers every table. The replica hashes the same ranges in one
   read snapshot at the same revision and compares. When the home moved on, the replica pulls and retries up to three
   times, then skips the round for 30 seconds (`result="skipped"`).
3. **Bounded cost.** One round runs at a time per replica node. On the home a round is bulk storage work attributed to
   the database's organization, so it shares that organization's I/O worker cap (E11) and never takes a customer's
   request or engine budget.
4. **Healing.** Equal revisions with unequal hashes are a divergence: the replica logs an error naming the database, counts
   `terrabase.replica.verifications{result="diverged"}`, deletes its files and bootstraps again from the home's full
   answer, then verifies again. A replica that diverges again within 15 minutes of a rebootstrap waits out that window,
   so a persistent fault alerts instead of looping. A restore or failover starts a new incarnation (D18), so replicas
   take a full answer on their next pull.

## Replica status

After each pull (at most every 10 seconds, and on every state change) a replica reports to its home with
`POST …/databases/{database}/replica-status`: its state (`bootstrapping`, `following`, `healing`), lag, last pull and last
completed verification. The home keeps the reports in memory; one older than 90 seconds reads as `stale`, and a region
that has not reported since the home started reads as `unknown`. The per-database regions read API takes each replica's
last completed verification from these reports; a node that is not the home reads them from the home with a 2-second
timeout.

Lag is the time since the oldest home commit the replica has been told about and does not hold yet; with NATS notices it
is 0 once a pull delivered every notice, and without NATS (sweep only) it is the time since the start of the last
successful pull, an upper bound.

## Encryption

Every replica file is encrypted at rest under the replica's own aegis256 data key, wrapped by the replica node's master key
in `<replica>.db.key`; a replica never shares key material with its home. The sync client writes the main file, its WAL,
the revert WAL and each staged pull answer (`<replica>.db-changes`) through that key (native patches
`src/vendor/Turso.Native/patches/0007-sync-engine-local-encryption.patch` and `0008-upsert-cdc-before-image.patch`). A
replica whose key does not unwrap is deleted and pulled again. Page pulls travel over the private mesh to the internal
listener, as for every peer request.

## Metrics

- `terrabase.replica.lag` (ms): from the home's commit, on its clock, to the replica holding it after the notice's pull.
- `terrabase.replica.pulls` (`result` = `changed`, `unchanged`, `failed`).
- `terrabase.replica.verifications` (`result` = `equal`, `diverged`, `skipped`, `failed`).
- `terrabase.replica.rebootstraps`: replicas deleted and bootstrapped again after a divergence.
- `terrabase.replica.max_lag` (ms) and `terrabase.replica.max_verification_age` (s): the worst replica on the node.

## Known gaps

- **NATS in production.** ts-orch does not yet pass `TS_NATS_URL` and the token to TerraBase, so production replicas
  follow on the 30-second sweep until it does.
- **One node per region.** A replica pulls from the first node the peers file lists for its home region.
- **Reads only through relational SQL.** Document, query and shape reads of a replicated database are served by its home.
- **Replica status is in memory.** After a home restarts, replica status reads `unknown` until each replica's next report
  (at most 30 seconds).
- **Buffered pulls.** The page sync host buffers each answer in memory (see [page sync host](page-sync-host.md)).
