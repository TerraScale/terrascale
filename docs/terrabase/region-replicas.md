# Region replicas (PIVOT R3)

A database has one home region, which serves its writes, and up to eight replica regions. A replica region holds a
read-only copy that it pulls from the home with Turso's official sync client in page mode, through the home's
[in-process page sync host](page-sync-host.md). The topology is a star: every replica pulls from its home, nothing is
ever pushed, and a replica can always be deleted and pulled again.

## Placement

- `POST …/databases` takes `replicaRegions`: distinct regions this cluster serves, other than the home, at most 8.
  `PATCH` changes them; the home raises the placement version on every change. `allRegions` stays refused
  (`replicas_unavailable`).
- Only the home holds a database file and backs it up. A replica region records the database, with the home's name and
  placement, and keeps its replica under `<data-directory>/.replicas/<organization>/<project>/<database>.db`.

## What a replica region serves

- `POST /v1/projects/{project}/databases/{database}/sql/execute` with only read statements runs on one snapshot of the
  replica, under the home's row, byte and time limits, and is metered as reads in the replica region.
- A statement that writes, a request before the replica's first pull, and every other data API answer
  `421 wrong_region` naming the home (`ts-replay: region=<home>`), so the edge replays them there.
- Reads are eventually consistent: a replica may lag its home. Read-your-writes needs the home.

## How a replica follows its home

1. **Catalog.** Every 30 seconds, at startup and when a revision notice names an unknown database, each node asks the
   other regions' nodes (from `TS_PEERS_FILE`, on their internal listener with the peer credential) for the databases
   they home that place this region as a replica: `GET /v1/sync/regions/{region}/databases`. It enrolls their
   organization and project as a project token would, records each database with the home's placement, and starts a
   follower. A recorded database the home no longer lists is checked with
   `GET /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/placement`: `404` means it was
   deleted, so the region forgets it and deletes the replica; otherwise the region takes the new placement and deletes
   the replica when it no longer names this region.
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

## Known gaps

- **NATS in production.** ts-orch does not yet pass `TS_NATS_URL` and the token to TerraBase, so production replicas
  follow on the 30-second sweep until it does.
- **One node per region.** A replica pulls from the first node the peers file lists for its home region.
- **Reads only through relational SQL.** Document, query, shape and Postgres reads of a replicated database are served by
  its home.
- **Buffered pulls.** The page sync host buffers each answer in memory (see [page sync host](page-sync-host.md)).
