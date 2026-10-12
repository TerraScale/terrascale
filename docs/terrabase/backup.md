# Continuous backup

TerraBase streams every SQLite file of a node (each database and `_system.db`) to an S3-compatible bucket, Tigris in
production (Fly's object store), about once a second, and restores any database as of a chosen second (TS-633). The design follows
[Litestream](https://litestream.io/how-it-works/) (generations, snapshots, log segments), written in C# inside the
server. It ships the SQLite write-ahead log per file, not per API database, so it keeps working whatever sits above SQLite.

The older scheduled backups (`…/backups`, local snapshot files next to the database) are unchanged. They are not off-node.

## Status of the work

Slices 1 (shipping and restore), 2 (shipper leases) and 3 (retention by plan, daily restore drills, records in TerraBase)
of TS-633 are done. Not done yet:

- No restore over an existing database, and no disaster-recovery path that rebuilds a whole data directory
  (`_system.db` included) from the bucket.
- Backups of a deleted database stay in the bucket: retention runs on files a member still ships.
- No segment compaction (see [Compared with Litestream](#compared-with-litestream)).

## Production backup contract

Continuous backup is always on. Outside Development a node refuses to start (exit code 2, before it opens any file)
unless its Tigris bucket and master key are present, so no deployed node runs without backup. Only Development runs
without backup; tests use an in-memory S3 fake. Retention and rotation use the correct settings in code; there is no
backup activation step or operator tuning table.

Settings come from the environment. The Tigris variables that Fly sets when a bucket is attached fill
`backup-endpoint`, `backup-region`, `backup-bucket`, `backup-access-key-id` and `backup-secret-key`
(`AWS_ENDPOINT_URL_S3`, `AWS_REGION`, `BUCKET_NAME`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`).
`master-key` comes from the fly secret `TERRABASE_MASTER_KEY` (32 bytes, base64).

Production (`ts-terrabase`) has its bucket `ts-terrabase` attached and the secret set. Each PR preview gets its own
bucket `terrascale-ts-pr-<n>` and a random master key from `scripts/fly/preview.sh`, destroyed with the preview.

### Customer buckets (TS-635)

A project can attach one S3-compatible customer bucket through the public `/v1` project API:

```http
PUT /v1/projects/{project}/backup-destination
Content-Type: application/json
Authorization: Bearer <ts-auth project access token granting databases:write>

{"endpoint":"https://s3.example.com","region":"us-east-1","bucket":"customer-backups","accessKeyId":"…","secretKey":"…","mode":"both"}
```

These operations appear under the `Backups` tag in OpenAPI. `P` below is `/v1/projects/{project}`;
`D` is `P/databases/{database}`, where `{database}` is the database name.

| Method | Route | operationId | Required JWT scope | Success |
| --- | --- | --- | --- | --- |
| GET | `P/backup-destination` | `getBackupDestination` | `databases:read` | 200 |
| PUT | `P/backup-destination` | `setBackupDestination` | `databases:write` | 200 |
| DELETE | `P/backup-destination` | `deleteBackupDestination` | `databases:write` | 204, no body |
| GET | `D/point-in-time` | `getRestoreWindow` | `databases:read` | 200 |
| POST | `D/point-in-time/restore` | `restorePointInTime` | `databases:write` | 201 |

The signed `project_id` must match `{project}`. Missing or invalid project credentials return 401;
missing scope returns 403; another project or a database outside the token's narrowing returns 404.
GET, PUT and DELETE of the project-wide destination also return 404 for a database-narrowed token.
Published organization routes remain hidden aliases under
`/v1/management/organizations/{organization}/projects/{project}`. They retain user membership and role checks,
and their `{database}` remains a catalog database ID. A project token cannot use those aliases.

`mode=both` ships the project's encrypted snapshots, WAL segments and wrapped data keys to both buckets.
`mode=customer` ships that project's data only to the customer bucket. `_system.db` and other projects stay in the
platform bucket. Customer backup uses the same master key (`TERRABASE_MASTER_KEY`). This is customer-only **project shipping**, not a standalone backup service.

`GET` at the same path returns `endpoint`, `region`, `bucket`, `mode`, and `updatedAt`; it never returns credentials.
`DELETE` detaches the customer destination by dropping its record, sealed credentials included, and resumes platform
shipping. It keeps every object in the customer bucket. Reattaching the same bucket and credentials makes its older backups accessible again. Every save replaces the
credentials and starts a new generation with a snapshot, so a newly attached bucket never receives WAL without a base.
On the hidden organization aliases, a viewer cannot save or detach destinations; other existing writable-project
roles can. Outsiders receive 404.
Deleting an unset destination is idempotent and returns 204. Saves admit at most four organizations concurrently,
with one save per organization, so one organization cannot occupy every slot. Each file's shipper refreshes
destination configuration at a ten-second cadence on successful passes; shipping work and retries can delay refresh.
Each lookup has a one-second deadline. A timeout cancels and joins the read, then uses the file's shipping backoff so
other files can be admitted.
Project deletion returns 409 while a customer destination remains configured; detach it first.
Saving, replacing or detaching an existing destination advances the organization's management version, invalidating
stale fences. Deleting an unset destination leaves that version unchanged.

The bucket must allow read, write, list and delete under `v1/<organization>/<project>/` and read/write/list under
`v1/keys/<organization>/<project>/`. Saving checks the object permissions using a small non-secret probe and verifies
the exact returned bytes, reading at most one byte beyond the expected payload. Probe cleanup uses a separate five-second
deadline after request cancellation, linked to host shutdown. The probe may remain if cleanup fails or a bucket
retention rule protects it. Customer endpoints must use HTTPS and globally reachable IP addresses. Redirects,
proxies and connections to private, loopback or metadata addresses are blocked; private-network S3 endpoints are not
supported.

Customer listing pages request at most 250 objects and are bounded to 2 MiB before XML parsing. The smaller page leaves
room for XML escaping of S3's 1,024-byte UTF-8 keys. Oversized keys are skipped; malformed entries still count toward
metadata scan limits.

Customer credentials are sealed in the catalog. Save probes the bucket, seals the key pair with AES-256-GCM under a
per-record data key, and wraps that data key under the master key (`TERRABASE_MASTER_KEY`). The catalog record
`projects/<project>/backup_destination` holds the public settings and the sealed credentials (`SealedCredentials`:
wrapped key and ciphertext), written in one durable catalog write. Credentials are write-only, and errors never include
provider response bodies.

Restore selection is explicit and never falls back between buckets:

- `GET /v1/projects/{project}/databases/{database}/point-in-time?destination=platform|customer`
- `POST /v1/projects/{project}/databases/{database}/point-in-time/restore` adds `"destination":"platform"` or `"destination":"customer"`
  to the existing `pointInTime`/`name` body. Omission keeps the original platform behavior.
- `POST /v1/projects/{project}/databases/{database}/point-in-time/plan` accepts the same optional `destination` in its
  `pointInTime` body and requires `databases:read` (see [Restore preview](#restore-preview-ts-493)).

Each source's window comes from its own stored objects, so old platform backups stay restorable after switching to
customer-only. An invalid selector returns 400; a disabled or empty source, including an unset customer destination,
returns 404. Window and restore bucket transport failures return a sanitized 503. Malformed window listings
also return 503; corrupt snapshot or key material during restoration remains an integrity failure. A successful restore returns
the new database and `Location: /v1/projects/{project}/databases/{restored-name}`; a hidden legacy request retains its
organization route and restored database ID in `Location`.
Encryption and `TBK1` objects are unchanged; restoring either source still requires the master key (`TERRABASE_MASTER_KEY`).
In `both` mode, shipping succeeds only when both uploads succeed; a failed destination delays this project's progress
and starts a fresh generation after backoff to avoid overlapping WAL segments after a partial upload. Platform files
and eligible customer jobs in other organizations continue shipping. Retention scans both configured buckets
independently, including earlier platform backups in customer-only mode; drills exercise the customer copy.
Retention processes at most 10,000 expired snapshots per file
and bucket per pass, checking later snapshots before removing their generations' segments. Expired segments are deleted
in batches of sixteen. Object locks may delay cleanup, and a bucket entirely outside the plan's window returns no
restore window. Customer-written junk keys inside the requested prefix are skipped; foreign or nonascending listing
responses are rejected. Listing order follows S3's UTF-8 byte ordering, including unrelated Unicode objects.
Customer object operations have a fifteen-minute deadline covering response headers, streamed body decoding and all
pages of a listing.
Shipping admits at most four file jobs, with at most two customer jobs and one customer job per organization;
platform-only files, including `_system`, can use the remaining capacity. A stalled customer job holds its organization's
customer slot until completion or cancellation. Background ticks skip busy files and admit
the least recently scheduled eligible files without waiting for earlier customer jobs. Each file keeps its target
until its job finishes. Database removal and shutdown cancel and join active jobs before disposing their resources;
shutdown refreshes and flushes known platform files first, then checks cached customer configurations for detachments
before starting customer I/O, using the supplied shutdown token. Mirrored acknowledgements also provide platform
freshness for their matching generation.
Configuration reads are limited to four admission candidates per scheduling pass. Wrapped-key reads have separate
host-wide admission pools of eight for platform and customer work, so customer prefetch cannot exhaust platform reads.
Cold platform key reads wait with request cancellation when their pool is occupied.

SDK/dashboard integration must consume these `/v1` project contracts through typed `ts-go` methods, with a ts-auth
project access token granting the scope above. User sign-in tokens belong to the hidden organization aliases.
This TerraBase change supplies the server contract; it does not ship `ts-go` methods or a `ts-dashboard` bucket UI.
Those remain dependent integration work. Native dashboard sign-in is unchanged. No live dashboard
rollout is performed by this repository change.

### Leases

One member ships each file. `Engine/IShipperLeases` chooses it: `ShipperEpochAsync(database)` returns the epoch of this
member's lease on that file while it ships it, or null. `Engine/Cluster` implements it with the cell's membership
(`docs/terrabase/design/anna-core.md` §5): each file is shipped by its database's **primary** on the ring of live members, while
that member holds its member lease.

- **Without `nats-url`** the members are the static `peers` list (one member when unset), whose leases never end. This
  is for one member and tests: backup with `peers`, `peer-addresses` or `catalog-primary` but no `nats-url` stops the
  process with exit code 2.
- **With `nats-url`** the members share ts-orchestrator's NATS (JetStream KV), the store TerraBase uses for external
  persistence. Each member keeps its key `member.<base64url node-name>` in the bucket
  `terrabase_backup_leases`.

| Setting | Meaning |
| --- | --- |
| `nats-url` | Comma-separated `tls://host:port` (or `nats://host:port`) servers of the cell's NATS |
| `nats-credentials-file` | NATS user credentials (`.creds`: user JWT and nkey seed), required with `nats-url` |
| `nats-certificate`, `nats-private-key`, `nats-ca` | PEM client certificate (followed by its intermediates, which are sent with it), its key and the servers' CA, for mutual TLS; all or none. With them the connection is TLS first, as ts-orchestrator's broker bridge expects; a plain nats-server needs `handshake_first: true` in its `tls` block |

- Each member rewrites its key every 5 s and reads the others in the same refresh. The bucket's `max_age` of 15 s (with
  subject delete markers) expires a member nobody rewrites; the bucket is created on first use with three replicas, one
  on a single server.
- A member holds its lease while its last successful renewal began less than 10 s ago by its own monotonic clock, so
  it stops shipping 5 s before its key can expire for the others. Two members ship the same file only under clock
  drift or a process pause beyond that margin. Even then nothing breaks: each ships its own generation, and a restore
  uses one generation.
- A member that becomes a file's shipper starts at once when the previous one left the membership; otherwise it waits
  10 s, so the previous shipper has either seen the new ring or lost its lease. A new epoch starts a new generation
  with a snapshot: the member taking the file over ships a different physical file than the previous holder.
- At shutdown a member ships what is left, then deletes its key, so the others take its files over at their next
  refresh (within 5 s). After a crash, takeover waits for the key to expire: about 15 to 20 s.
- ts-orchestrator delivers the NATS files and renews them every 24 h (TS-651). Every connect and reconnect reads them
  again, and a reconnect refused with an expired JWT keeps retrying, so no restart is needed. Backup settings come from
  the environment and are read at start: changing them needs a restart.
- Every member taps every file and checkpoints it (`wal_autocheckpoint = 0` wherever backup is configured), whether or
  not it ships it.

## How shipping works

On the shipping member each SQLite connection runs with `wal_autocheckpoint = 0`, and the backup's log tap
(`Engine/Sqlite/SqliteWalTap.cs`) checkpoints instead:

1. Each second it reads the frames committed since its last read straight from `<file>-wal`. It validates salts and
   checksums as SQLite recovery does and stops at the last commit frame.
2. Once the log holds 1000 frames (SQLite's default), it takes the database's in-process writer gate, reads the rest,
   and runs `PRAGMA wal_checkpoint(PASSIVE)`. The log restarts (salt1 + 1, new salt2) only after a checkpoint copied
   every frame back, so no frame is overwritten before the tap reads it. The header's checkpoint sequence is not a
   restart counter: each connection keeps its own, so a restart by another connection repeats it.
3. Anything else that moves the log is a **hole**: the file starts a new generation, and `terrabase_backup_holes_total`
   counts it. These are Litestream's "wal overwritten/truncated by another process" checks:
   - a restart not preceded by the tap's own complete checkpoint, with nothing read since (busy 0, every frame copied
     back), or a restart that moves salt1 by more than one;
   - a log that vanished or got shorter than what was read (a `TRUNCATE` checkpoint by another process);
   - a last read frame whose checksum changed under the same salts.
4. The tap keeps a connection open, so SQLite never checkpoints and deletes the log when the last connection closes.

A **generation** is one unbroken run of frames from one file on one member. It starts with a **snapshot**: under the
writer gate the tap reads every committed frame and starts a read transaction, then copies the database page for page
with SQLite's online backup API inside it. A generation starts when shipping starts (every process start), after a hole,
or after 256 MiB of frames could not be stored. Within a generation a snapshot is taken daily. Frames read in one pass
become one **segment**.

After a failed upload or snapshot, a file waits before the next attempt: 1 s, doubling to 5 minutes. Reads and
checkpoints go on meanwhile, so the log stays small. Frames that cannot be stored past 256 MiB are dropped, and the file
starts a new generation once the bucket answers.

Each object is sealed before upload (`Features/Backup/BackupObject.cs`, format 1): chunks of up to 1 MiB, LZ4-compressed
when smaller, AES-256-GCM. Each object's AES key is HKDF-SHA256 of its project's data key, a random salt and the object's
own key in the bucket, so an object moved elsewhere does not decrypt. Data keys are generated by TerraBase, wrapped with AES-256-GCM under the master key
(`TERRABASE_MASTER_KEY`) and stored in the bucket. Only a node with the master key can unwrap them. Each project gets its own data
key, and `_system.db` gets one too.

### Data-key rotation

Before an upload, TerraBase checks the data key's age using the creation time in its existing ULID. An expired key is
replaced with a new data key, whose wrapped copy is uploaded before any object uses it. Running nodes refresh
their current selection at most once a minute, listing only keys after their cached selection, so they adopt a newer key
published by another member without rescanning retained history. If two nodes rotate together, both wrapped keys remain
available and each object names the key it used. A failed key upload leaves
the previous selection intact and backup shipping retries; it never publishes an object with an unavailable key.

Old snapshots and log segments are not rewritten. Old wrapped data keys are retained for historical restores, including
generations whose snapshot and log segments use different keys. Current plaintext keys are cached for at most 65536
scopes, with another 65536 unwrapped keys cached for restores (at most 4 MiB of key material in both caches). Entries
expire after a day without access. At capacity, cache admission synchronously makes room for new results so waiting
downloads can reuse them. Evicted-key lookups retrieve keys from the bucket and unwrap them;
size nodes to keep active scopes and restore keys within these limits. Generated keys enter the restore cache only
after their wrapped copy is persisted. Concurrent downloads using one historical key share its retrieval and unwrap.
Wrapped key bodies are limited to 16 KiB both before publication and during cold retrieval.
Distinct keys use bounded key stripes independently of selection and publication. Unrelated, nested, and future-dated
key objects are ignored during selection; a future-dated key can still decrypt historical objects naming it.
Changing `backup-key-rotation-days` requires a rolling
restart; use `1` for the next daily rotation when shortening the schedule.

The master key does not rotate yet. Changing `TERRABASE_MASTER_KEY` makes retained backups unreadable, because
their wrapped data keys no longer unwrap. There is no key versioning yet. Do not delete wrapped keys while their
backups are still retained.

This rotation preserves the existing backup bytes, bucket layout and SQLite files. Live SQLite files, WAL files, local
managed snapshots and restore scratch files still require encryption of the node's storage; TS-702's disk-encryption
and in-place migration work follows separately.

## Bucket layout (version 1)

```text
v1/<organization>/<project>/<database>/snapshots/<unix ms>-<generation>-<frame position>
v1/<organization>/<project>/<database>/wal/<generation>/<first frame position>-<unix ms>
v1/_system/…                                  the same for _system.db
v1/keys/<organization>/<project>/<data key id> wrapped data keys (v1/keys/_system/… for _system.db)
```

Times are 13-digit Unix milliseconds and positions 16 digits, so key order is time order for snapshots and frame order for
segments. A snapshot's time is when its read transaction was pinned under the writer gate, before the copy begins.
Copying and uploading do not move that boundary; later writes are shipped in subsequent log segments. A segment's time
is when its last frame was read.

## Restore

A restore always creates a new database, so a new incarnation that reaches the other members through replication (with
TS-647, through anti-entropy). Backups taken before the TS-602 wipe use the old schema and do not need to restore after
it (user decision, 2026-10-07). Production backup starts right after that cutover.

`GET /v1/projects/{project}/databases/{database}/point-in-time` returns the restore window: from the oldest snapshot,
but no earlier than the plan's retention window (a bucket rule may keep older objects), to the newest data in the
bucket. It returns 404 when no stored data falls within the plan's window, including when retained objects have all
expired under the plan.
`POST /v1/projects/{project}/databases/{database}/point-in-time/restore` with `{"pointInTime": "…", "name": "…"}`
restores into a new database of the same project (`branchedFrom` set, like a backup restore). The restore:

1. takes the newest snapshot at or before `pointInTime`;
2. applies its generation's segments up to `pointInTime` in order, checking they are contiguous. Each frame's page is
   written in place, and the file is cut at every commit that shrinks the database, as a checkpoint at that commit
   would. Eight segments download at once, each retried up to three times if the stream drops;
3. runs `PRAGMA integrity_check`, then imports the file as a copy, so its replication starts over from a baseline.

`pointInTime` must fall between `earliest` and `latest`, inclusive; requests outside those bounds return 400. A time after
`latest` is refused even when it is before now, so a delayed shipper cannot silently restore older data for that time.
The shared restore service validates these bounds once before downloading or importing a database. It captures the
retention reference time before reading metadata and uses one snapshot scan for both the window and snapshot selection,
so metadata latency cannot move the retention floor during a restore. Window reads and previews also capture their
reference time before reading metadata.

The result holds every transaction the shipper read by `pointInTime`. The shipper reads every second, so a transaction
committed in the second before `pointInTime` may be missing.

### Restore preview (TS-493)

`POST /v1/projects/{project}/databases/{database}/point-in-time/plan` with
`{"pointInTime":"…","destination":"customer"}` reads the selected source's
backup metadata without downloading or decrypting backup contents, creating scratch files,
or changing databases. It returns:

```json
{
  "window": {"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"},
  "pointInTime":"2026-10-07T12:00:13Z",
  "snapshotAt":"2026-10-07T12:00:10Z",
  "latestSegmentAt":null,
  "segmentCount":0
}
```

`destination` is optional and defaults to `platform`; `customer` inspects only the attached customer bucket.
The snapshot is the newest at or before `pointInTime`, selected by the same code as
restore. `segmentCount` and `latestSegmentAt` describe that snapshot generation's
segments after its position and read by the requested time. The latter is null when
none apply. No object keys or credentials are returned.

The request needs a ts-auth project token granting `databases:read`; `{database}` is its name.
The hidden legacy management alias retains membership checks, including organization viewers. Access is checked
before time validation: another tenant's database returns `404`. Missing time, a time
outside the inclusive `window.earliest` and `window.latest` bounds returns `400`;
disabled/empty backup, a wholly expired retention window or no applicable snapshot returns `404`.
Preview uses the same restore-window bounds as restore. Metadata scanning is bounded to 10,000 items and 10 seconds;
exhaustion returns `503` without a partial plan (`pitr_plan_limit` or
`pitr_plan_timeout`). Object-store listing failures return `503`
`pitr_plan_unavailable` without exposing provider details.

This is an advisory preview. It does not reserve objects against retention, validate
their encrypted contents, or prove WAL continuity. Restore still performs the download,
authentication, continuity and integrity checks. The public project route and its hidden
legacy alias follow [the HTTP API table](api.md).

## Retention

Every hour the member that ships a file trims its backup to the plan of the file's organization (`backup-retention-days`;
`_system.db` keeps the longest). With a window of `d` days it keeps every snapshot taken in the last `d` days plus one hour,
and the newest snapshot before that, so any time in the window still restores. It deletes older snapshots, the segments
before the oldest kept snapshot in its generation, and generations left without snapshots. Segments go first, so an
interrupted pass never leaves segments that no snapshot reaches.

The extra hour covers a restore that picked a snapshot just inside the window, as Litestream learned (#1450). Plans move
to ts-dashboard (TS-599); this setting stays the fallback.

A bucket retention rule refuses deleting objects younger than its duration
(`RetentionRuleViolation`). Retention deletes segments oldest first, stops at the first refused one without counting a
failure, and deletes a generation's snapshots only once all its expired segments are gone. Objects therefore stay as long
as the longer of the plan and the rule, and a pass costs at most one refused call while the rule holds.

### Bucket snapshots

Tigris snapshots the whole platform bucket, so a bad delete (by TerraBase, a person or a stolen key) can be undone by
forking the bucket from a snapshot (`tigris buckets create <fork> --fork-of ts-terrabase --source-snapshot <version>`).
Tigris has no snapshot schedule or retention, so TerraBase runs both. The member that ships `_system` checks once an
hour, during `_system`'s retention:

- It takes a snapshot when the newest one is a day old or more.
- It deletes snapshots older than 14 days, but never the newest, so a failing take never leaves the bucket without one.
- It logs every take and delete, and a failure as a warning ("retries next hour").
- A bucket without Tigris snapshots (a PR preview's) is detected once per process from HeadBucket's
  `X-Tigris-Enable-Snapshot` header; that process logs it once and skips the job.

Snapshots use the S3 API with Tigris headers: `X-Tigris-Snapshot: true` on CreateBucket takes one,
`X-Tigris-Snapshot: <bucket>` on ListBuckets lists them, and `X-Tigris-Snapshot-Version: <version>` on DeleteBucket
deletes one. TerraBase refuses to send a DeleteBucket without a numeric version. Because snapshots go through
CreateBucket and ListBuckets, the bucket's key policy (`ts-terrabase-only`) allows those; Tigris cannot scope them to
one bucket. The dev key's policy (`ts-terrabase-dev-only`) still denies both.

Both buckets (`ts-terrabase`, `ts-terrabase-dev`) have snapshots and **soft delete** (14 days) enabled. A deleted bucket
can be restored with `tigris buckets restore` within 14 days. Delete protection is off on purpose: it also refuses
snapshot deletes, which would stop retention.

## Restore drills

At least once a day, when it stores a snapshot, the shipper checks it: it rebuilds the file from the bucket alone (the
generation's previous snapshot, or the new one at generation start, rolled forward through the segments up to the new
snapshot's exact frame position), then compares a SHA-256 of the schema and every row of every table with the local copy
the snapshot was made from, which is the live file at that position. A drill exercises download, decryption, contiguity,
roll-forward and `integrity_check`.

A failed drill is logged as an error and counted in `terrabase_backup_drills_total{result="failed"}`.

## Records in TerraBase

After each hourly retention pass and each drill, the shipper writes the file's record into `_system`: collection
`databases/<database id>/backup_status`, id `current` (`backup_status`/`_system` for `_system.db`). The record holds the
shipping member, generation, last shipped time, lag, bucket size (measured daily), retention days and the last drill.
The database's state is deleted with it. A catalog follower cannot write the catalog, so it keeps the record in memory
only (`GET /v1/backup`) until TS-647 makes `_system` writable on every member.

## Verify a rollout

`scripts/fly/deploy.sh` checks each region's new machine twice before it moves on: `/healthz`, then `/backupz`
(pinned with `fly-force-instance-id`, retried for about 90 s). `/backupz` answers 200 once backup has found the
node's files and every file the node ships has reached the bucket with less than a minute of lag, and 503 otherwise.
A 503 stops the rollout (fix forward). `/backupz` is not a Fly health check: a Tigris outage never takes a node out of
service, it only shows on `/backupz` and in the metrics.

After a rollout, also check:

1. Grafana (fly-metrics.net): `terrabase_backup_lag_seconds` (the RPO) stays under 2 s under writes,
   `terrabase_backup_last_success_seconds` keeps moving, and `terrabase_backup_failures_total` and
   `terrabase_backup_holes_total` do not rise. `/v1/backup` is on the loopback admin listener and the image has no
   shell, so the metrics are the way in.
2. Restore: `POST …/point-in-time/restore` of a scratch database to a second between two writes; check that the first
   write is in the new database and the second is not; delete the restored database.

## Tigris check (2026-10-11)

Checked against the Tigris dev bucket `ts-terrabase-dev`:

- A node shipped `v1/_system`: 1 snapshot, 2 WAL segments and 1 wrapped data key of 80 bytes.
- A second run with the same master key and a fresh data directory reused that key.
- A run with another master key failed closed: `wrapped data key is not authentic under the master key`.

## Compared with Litestream

Checked against Litestream 0.5.17 (August 2026): its design and its bug fixes from 0.3 to 0.5.17.

| Litestream | TerraBase |
| --- | --- |
| Separate process; a long read transaction stops other connections from restarting the log, plus checks for "overwritten by another process". Its read lock has been lost by mistake before (#1552). | In-process: the only checkpointer is the tap, under the database's writer gate, after reading every frame. A checkpoint never runs during a write, so SQLite's WAL-reset race (#1432, fixed in 3.51.3; we bundle 3.53.4) cannot happen. Litestream's overwrite checks run as the safety net. |
| PASSIVE checkpoint at 1000 pages or each second; TRUNCATE above about 500 MB when long readers stop PASSIVE (RESTART removed). | PASSIVE at 1000 frames only. No forced TRUNCATE: a long reader grows the log as it did before backup. |
| Snapshot read from the database file while writes go on; a slow first snapshot gave restores that fail `integrity_check` (#1309). | The online backup API inside a read transaction started under the writer gate, at an exact log position. Every restore runs `integrity_check`. |
| LTX files with TXIDs, compacted to 30 s, 5 min and 1 h levels for fast restores. | One segment per second per busy file, no compaction yet. Restore downloads 8 at a time; a busy file can need up to 86,400 segments a day. Compaction is a follow-up. |
| Restore applies whole files whose range fits the target; checks for gaps (0.5.12). | Same rule per segment; gaps fail the restore; GCM tags and a last-chunk flag catch truncated objects (0.5.17 "truncated page index"). |
| Stale pages after the database shrinks (0.5.3); all new pages are written on growth (0.5.8). | The file is cut at every shrinking commit. |
| Restores retry throttling and resume dropped S3 streams (0.5.8, 0.5.17). | Each object retried up to three times. |
| aws-chunked off for S3-compatible stores (0.5.6); payload signed (0.5.5). | Same: `UseChunkEncoding = false`, signed payload hash, checksums only when required. |
| Sync backoff up to 5 minutes. | Same. |
| Resumes a generation across restarts. | A new generation, so a new snapshot, per member start. |
| Retention keeps the snapshot before the window (0.5.14); a restore raced retention (#1450). | Same: the newest snapshot before the window is kept, plus one hour of grace. |
| Leases: S3 conditional writes (0.5.8) or a single process. | NATS KV leases with self-fencing before expiry. |
| Optional age encryption (0.3). | Always sealed: AES-256-GCM, per-project data keys wrapped with AES-256-GCM under the master key (`TERRABASE_MASTER_KEY`). |

## Operations

- `GET /v1/backup` on the loopback admin listener lists each shipped file: generation, frames, last shipped time, lag,
  pending bytes and the kind of the last error.
- Metrics, over the files this member ships: `terrabase_backup_lag_seconds` (oldest data not yet in the bucket),
  `terrabase_backup_last_success_seconds` (Unix time of the least recently shipped file) and `terrabase_backup_files`.
  Also `terrabase_backup_uploaded_bytes_total{kind}`, `terrabase_backup_failures_total`, `terrabase_backup_holes_total`
  and `terrabase_backup_generations_total` (a rising count means snapshots too often), `terrabase_backup_drills_total{result}`
  and `terrabase_backup_stored_bytes` (bucket bytes of the files this member ships).
  Bucket snapshots: `terrabase_backup_bucket_snapshots_total{result}` (taken, deleted, failed) and
  `terrabase_backup_bucket_snapshot_seconds` (Unix time of the newest snapshot, on the member that takes them; 0 elsewhere).
- `GET /v1/backup` also shows, per file, whether this member ships it.
- Scratch files (frames waiting for upload, snapshots, sealed objects) live in `<data-directory>/.backup`, which is
  emptied at start.
