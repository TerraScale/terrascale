# Continuous backup

TerraBase streams every Turso database file of a node (each database and `_system.db`) to an S3-compatible bucket, Tigris in
production (Fly's object store), about once a second, and restores any database as of a chosen second (TS-633). The design follows
[Litestream](https://litestream.io/how-it-works/) (generations, snapshots, log segments), written in C# inside the
server. It ships, per file, the changes Turso captures on the file's writer connection (`turso_cdc`, mode `after`), not
per API database, so it keeps working whatever sits above the storage engine. [Why not the write-ahead log](#why-the-change-log-not-the-write-ahead-log).

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
metadata scan limits. Multipart start and completion responses, like error responses, are bounded to 64 KiB.

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
and starts a fresh generation after backoff to avoid overlapping WAL segments after a partial upload (a drop with
reason `destination`, see [How shipping works](#how-shipping-works)). Platform files
and eligible customer jobs in other organizations continue shipping. Retention scans both configured buckets
independently, including earlier platform backups in customer-only mode; drills exercise the customer copy.
Retention processes at most 10,000 expired snapshots per file
and bucket per pass, checking later snapshots before removing their generations' segments. Expired segments are deleted
in batches of sixteen. Object locks may delay cleanup, and a bucket entirely outside the plan's window returns no
restore window. Customer-written junk keys inside the requested prefix are skipped; foreign or nonascending listing
responses are rejected. Listing order follows S3's UTF-8 byte ordering, including unrelated Unicode objects.
Customer object operations have a fifteen-minute deadline covering response headers, streamed body decoding and all
pages of a listing. A multipart upload (see [Uploads](#uploads)), platform or customer, keeps one deadline for the
whole object: fifteen minutes plus one minute per part after the first, which a bucket meets at a sustained ~2 MiB/s.
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
- Every member taps every file and trims its change log, whether or not it ships it.

## How shipping works

Each file has one writer connection (Turso aborts the process when two connections write one file), and it captures
every change it commits in the table `turso_cdc`, mode `after`: one row per inserted, updated or deleted row with its
after-image (SQLite's record format), a row for each schema change (`sqlite_schema`), and a COMMIT row ending each
transaction. The backup's tap (`Engine/Sqlite/TursoChangeTap.cs`) reads that table:

1. Each second it reads, on a connection of its own, the rows after the last one it read (its anchor), in pages of at
   most 1024 rows or 4 MiB, and appends them to the log as records (`ChangeLog`, format 1): upsert (table, rowid,
   record), delete (table, rowid), commit, create (the object's SQL), drop (type, name), timed commit and read mark. A
   read stops at the last COMMIT row, or at the first COMMIT row once it has appended 32 MiB, so it always ends between
   transactions; the rest waits for the next read. One transaction is never split, so a read can exceed 32 MiB by one
   transaction. A timed commit carries the `change_time` of the transaction's first row and of its COMMIT row; a
   transaction whose times are missing or go back is logged with a plain commit. When a read appended changes up to the
   newest COMMIT row that existed when it started (not a read stopped at 32 MiB), a read mark follows them with that
   start time: every transaction committed before it is in the log before the mark. Both records are additive: the
   object format, the bucket layout and older logs (plain commits only) stay readable.
2. It then deletes, on the writer, the rows before its anchor, always keeping the newest: an empty `turso_cdc`
   restarts its ids at 1 after a reopen. Turso does not capture changes to `turso_cdc` itself. A read that fails,
   including this delete, puts its anchor back: the shipper drops what that read appended, and the next read appends
   the same changes again (or finds a hole, if the delete ran). The change retention of peer streams follows the new
   anchor only once the delete has succeeded.
3. Anything that moves the log under the tap is a **hole**: the file starts a new generation, and
   `terrabase_backup_holes_total` counts it. Two cases:
   - the anchor row is gone: something else deleted changes the tap had not read;
   - a schema row the tap cannot place by name: an update (`ALTER`), or a delete of a rowid it does not know.

   A delete in mode `after` carries only the rowid, and Turso does not capture every schema row (`ANALYZE` adds
   `sqlite_stat1` uncaptured), so `sqlite_schema` rowids differ between a file and a restore of it. The tap keeps the
   live schema's rowid → name map, as of its anchor, and logs a dropped object by name.
4. Nothing may write a file uncaptured, except state that a restore rebuilds itself (the change tailer's own bookkeeping,
   R3): a write transaction can turn capture off with `SuspendCaptureAsync`, and what it writes is in no backup. The
   writer connection holds no TEMP table, because Turso captures a TEMP table's rows as if they were in the file.

A **generation** is one unbroken run of the log from one file on one member. It starts with a **snapshot**: holding
the database's writer gate, so nothing commits, the tap reads what is left, moves its anchor to the newest change and
opens a read transaction; then writers go on, and the database is copied row by row, every rowid kept, as of that
transaction (`Engine/Sqlite/TursoCopy.cs`). The copy leaves out the engine's own tables (`turso_cdc`, `sqlite_stat1`).
A generation starts when shipping starts (every process start), when another member's lease or a new customer
destination takes over, after a hole, or after unstored log was dropped (below). Before a generation's snapshot holds
writers, the tap reads any backlog to discard it, so the gate covers only what commits meanwhile. Within a generation a
snapshot is taken daily, but not while a backlog remains, because the snapshot reads what is left while it holds
writers. The log read in one pass becomes one **segment**. A backlog, such as the writes committed during a long
snapshot upload, ships as one segment per read budget, one per pass, so a pass that keeps up with the bucket drains it
at up to 32 MiB a second.

Snapshots and segments are ordered so that every segment a generation stores has a base:

- A generation's first snapshot is copied and uploaded in the file's shipping pass, because a segment is useless (and
  in a customer bucket, WAL without a base) until the snapshot it starts from exists. The generation's segments ship
  from the next pass on, right after that object is stored.
- A daily snapshot is pinned and copied in the pass, and the log read up to its position is stored first, so the next
  segment starts at that position. Its upload then runs beside shipping: segments after it keep shipping while it
  uploads, and the snapshot becomes a new base once stored. At most four daily snapshot uploads run on a node at a
  time, each holding its scratch copy until stored; a file whose turn has not come tries again next pass.
- Restore drills run beside shipping too (see [Restore drills](#restore-drills)).

A segment is sealed once. When its upload fails, even a PUT the bucket stored before the failure, the next attempt
sends exactly those bytes under the same key before anything later is sealed, and the log read meanwhile becomes the
next segment, starting where the failed one ends. So two segments never share a start, and an overwrite of a stored
segment only repeats its own bytes.

After a failed upload or snapshot, a file waits before the next attempt: 1 s, doubling to 5 minutes. When the attempt
is due, the failed segment is sent again before anything more is read. Between attempts reads and trimming go on, so
`turso_cdc` stays small, but only while no backlog remains and less than 256 MiB waits in scratch; otherwise the rest
stays in `turso_cdc`, where the backup's anchor keeps it, and a short outage costs nothing. Once failures reach the
longest backoff (about eight and a half minutes into an outage) while log still waits there, the outage counts as
sustained: the log is **dropped**, the generation ends, reads trim `turso_cdc` again, and the file starts a new
generation with a snapshot once the bucket answers. A log is dropped only then, after a failed snapshot read, or after
any failure on a customer destination (below). Every drop logs a warning and counts in
`terrabase_backup_abandoned_total{reason}` (`outage`, `read`, `destination`); a hole counts in
`terrabase_backup_holes_total`. A new shipper lease, a destination change and shutdown also end a generation, on
purpose, without counting a drop. At shutdown a file ships its whole backlog, read after read, while the shutdown
budget lasts. A daily snapshot due while a backlog remains, or while snapshot uploads are busy, waits for a later pass;
`terrabase_backup_snapshots_deferred_total{reason}` (`backlog`, `busy`) counts each such pass.

Lag (`terrabase_backup_lag_seconds`, `lagSeconds`) is how long the oldest committed data not yet in the bucket has
waited, as far as the shipper can tell: it starts when a read first finds unstored changes (or a snapshot is pinned,
or a read fails), stays above zero while a backlog remains, a failed upload is pending or the file has no generation
after a drop or hole, and returns to 0 once a pass stores everything it read and nothing is left. It is not a bound on
loss: data dropped or behind a hole is not in the bucket even after lag returns to 0. While a generation's first
snapshot is copied and uploaded nothing of that file ships, so lag counts from before the copy until the pass after the
snapshot is stored.

Each object is sealed before upload (`Features/Backup/BackupObject.cs`, format 1): chunks of up to 1 MiB, LZ4-compressed
when smaller, AES-256-GCM. Each object's AES key is HKDF-SHA256 of its project's data key, a random salt and the object's
own key in the bucket, so an object moved elsewhere does not decrypt. Data keys are generated by TerraBase, wrapped with AES-256-GCM under the master key
(`TERRABASE_MASTER_KEY`) and stored in the bucket. Only a node with the master key can unwrap them. Each project gets its own data
key, and `_system.db` gets one too.

### Uploads

A sealed object of at most 128 MiB is one `PutObject`. A larger one, such as a whole-database snapshot, is an S3
multipart upload (`Features/Backup/ObjectStore.cs`): 128 MiB parts (S3's 10,000-part limit then covers objects up to
1.25 TiB; a larger object fails before it starts), at most four parts in flight, each streamed from the sealed scratch
file at its own offset, so no part is held in memory. Every part uses the signing and checksum settings of a single
upload: a signed SHA-256 payload hash without `aws-chunked` encoding, and checksums only where S3 requires them. The
multipart path has not yet run against Tigris itself, only against the in-memory S3 fake. Each request has the
fifteen-minute request timeout. A failed or timed-out part is retried alone, up to
three attempts (each attempt includes the SDK's own retries of transient errors); the parts already stored stay.

A completion answer alone does not count. Some S3-compatible services answer a failed completion with 200 and an error
body (the AWS SDK in use turns that body into an error; a success answer that stored nothing would pass it), and an
older object of the same length may already sit at the key. Each upload therefore starts with its own ULID as user
metadata (`x-amz-meta-terrabase-upload`), which S3 copies to the completed object, and the upload counts only when a
HEAD of the key then reports both that ULID and exactly the uploaded length. Missing or other evidence fails the upload,
so the shipper never records an object the bucket does not hold. Tigris support for this evidence (user metadata kept
through a multipart completion and returned by HEAD) is unverified until G1; if Tigris lacked it, large uploads would
fail visibly, never succeed falsely.

After a part's last attempt fails, a failed or unproven completion or a cancellation, TerraBase aborts the upload (best
effort, with its own five-second deadline, logged when it fails) and the file's normal backoff applies. Every multipart
upload keeps the whole-object deadline in [Customer buckets](#customer-buckets-ts-635), at most about seven days for
10,000 parts. An upload whose abort fails, or whose node stops, is never visible as an object, but its parts hold bucket
storage. No bucket lifecycle rule removes them. Instead, the member that ships a file lists the platform bucket's
in-progress uploads under that file's directory (`ListMultipartUploads`, which
[Tigris supports](https://www.tigrisdata.com/docs/objects/multipart-uploads/)) when it first retains the file after
starting and then daily. It aborts those initiated more than the largest upload's deadline plus an hour ago, which no
live upload on any member can still own. While its file is shipped and the bucket answers, an orphan therefore holds
storage for about eight days at most. Uploads under directories that no member ships, such as a deleted database's, and
orphans in customer buckets are not cleaned up.

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

This rotation preserves the existing backup bytes, bucket layout and database files. Live database files, WAL files, local
managed snapshots and restore scratch files still require encryption of the node's storage; TS-702's disk-encryption
and in-place migration work follows separately.

## Bucket layout (version 1)

```text
v1/<organization>/<project>/<database>/snapshots/<unix ms>-<generation>-<log position>
v1/<organization>/<project>/<database>/wal/<generation>/<first log position>-<unix ms>
v1/_system/…                                  the same for _system.db
v1/keys/<organization>/<project>/<data key id> wrapped data keys (v1/keys/_system/… for _system.db)
```

Times are 13-digit Unix milliseconds and positions 16 digits, so key order is time order for snapshots and log order for
segments. A position counts bytes of the generation's log. The `wal/` prefix is kept from the SQLite format; it now
holds change log segments. A snapshot's time is when its read transaction was pinned under the writer gate, before the copy begins.
Copying and uploading do not move that boundary; later writes are shipped in subsequent log segments. A segment's time
is when its last change was read.

## Restore

A restore always creates a new database, so a new incarnation that reaches the other members through replication (with
TS-647, through anti-entropy). Backups taken before the TS-602 wipe use the old schema and do not need to restore after
it (user decision, 2026-10-07). Production backup starts right after that cutover.

`GET /v1/projects/{project}/databases/{database}/point-in-time` returns the restore window: from the oldest snapshot,
but no earlier than the plan's retention window (a bucket rule may keep older objects), to the newest time the backup
proves (see [Proven history](#proven-history)): read from the bucket, the second at or before the newest read mark in
the newest generation's last three segments (at most three object reads), or its newest snapshot's time. While this
node ships the database to the platform bucket, `latest` is instead the shipper's acknowledged progress, the end of its
newest stored read, which a restore can refuse with `409` until a later write or read mark proves it. It returns 404
when no stored data falls within the plan's window, including when retained objects have all expired under the plan.
`POST /v1/projects/{project}/databases/{database}/point-in-time/restore` with `{"pointInTime": "…", "name": "…"}`
restores into a new database of the same project (`branchedFrom` set, like a backup restore). The restore:

1. takes the newest snapshot at or before `pointInTime`;
2. checks from object names that the history up to `pointInTime` can be complete (see
   [Proven history](#proven-history)), before downloading anything;
3. replays its generation's segments read by `pointInTime` in order, checking they are contiguous: one transaction per
   commit record, rows by rowid (`INSERT OR REPLACE`, `DELETE`), schema objects created from their SQL and dropped by
   name. A log that ends inside a transaction, a malformed record or a change the file refuses fails the restore.
   Eight segments download at once, each retried up to three times if the stream drops. Then the history must be
   proven to reach `pointInTime`, replaying the transactions of later segments that ran before it;
4. runs `PRAGMA integrity_check`, then imports the file as a copy, so its replication starts over from a baseline.

`pointInTime` must fall between `earliest` and the newest backed-up data (the shipper's acknowledged progress, or the
newest segment's time), inclusive; requests outside those bounds return 400. That bound is at or after the window's
`latest`: between them a restore succeeds only when it proves the time, and answers `409` otherwise. A time after the
newest data is refused even when it is before now, so a delayed shipper cannot silently restore older data for that time.
The shared restore service validates these bounds once before downloading or importing a database. It captures the
retention reference time before reading metadata and uses one snapshot scan for both the window and snapshot selection,
so metadata latency cannot move the retention floor during a restore. Window reads and previews also capture their
reference time before reading metadata.

The result holds every transaction committed before `pointInTime`, or the restore answers `409`.

### Proven history

A restore at `pointInTime` (T, taken to the millisecond) returns `201` only with whole transactions: every transaction
committed before T, so every write acknowledged before T, and none whose COMMIT row was written at or after T.
Otherwise it answers `409` and creates nothing. T, segment and snapshot times, read marks and Turso's `change_time` are
all the node's wall clock, and "acknowledged before T" is measured on it.

What Turso records (measured on the driver, `TursoChangeTimeTests`): every captured row, the COMMIT row included, holds
`change_time`, the wall clock when Turso wrote that row, in whole Unix seconds rounded down; rows of one transaction
differ when its statements run in different seconds. The COMMIT row is written inside the transaction, before it is
durable, so a transaction commits, and is acknowledged, after its COMMIT row's time; a statement's time is never taken as
a commit time. One transaction may hold many client writes (a group commit or a transaction request); a restore never
splits it.

The restore takes the newest snapshot at or before T and replays its generation's segments in log order:

- segments read by T (their time at or before T, to the millisecond) are replayed whole: what they hold committed
  before their read ended;
- the history must then be proven complete up to T, by one of:
  - the snapshot was taken in T's millisecond (it holds every writer while it reads);
  - the last segment read by T was read in T's millisecond and ends with a read mark at or after T;
  - the segments read after T, read on transaction by transaction: those whose COMMIT row's second ends by T (every
    row ran before T) are replayed, up to the first whose COMMIT row's second starts at or after T (the witness: it and
    every later transaction committed after T), or up to a read mark at or after T;
  - the generation's next snapshot holds exactly the replayed log.

A segment's own time is when its read finished: it shows nothing about commits made while the read ran, so it proves no
time by itself.

`409` `backup_history_incomplete`: the bucket cannot show the history complete up to T.

- A missing or overlapping segment (a lost upload, a bucket-side deletion, two segments with the same start where the
  first holds log; a segment holding no log ends where it starts, so the next may share its start). Times up to the
  last segment before it can still restore.
- Nothing after the replayed log proves T: an unshipped or missing final segment, a generation that ended (a hole, a
  dropped backlog, a shipper move or restart; the next generation's snapshot time restores), or a quiet period not yet
  closed by a later write or snapshot.
- A snapshot or segment that disappears between listing and download (retention racing the restore).

`409` `restore_point_unproven`: the history is there but T cannot be placed between two transactions, in a segment read
after T: a transaction whose COMMIT row's second contains T (times are whole seconds), one without recorded times (a
log written before they were recorded, or a clock that went back within it), or times that go back between two
transactions. A whole second, or a time in a second in which no transaction committed, can be placed.

Guaranteed to restore exactly (`201`), given contiguous segments and timed transactions:

- **Snapshot times**: T in a snapshot's millisecond.
- **Witnessed times**: T at a whole second, or any time in a second in which no transaction read after T committed,
  when a transaction committed at or after T (or a read mark at or after T) is shipped later in the same generation.
  A quiesced cut is this case: writes stop, T is the next whole second, and a later write is the witness.
- **The latest durable point**: the second at or before the newest read mark's time, with no later write needed.
- **A quiet time closed by a snapshot**: a time up to the generation's next snapshot when no segment follows the
  replayed log before it.

Limits:

- T is resolved to the millisecond: a transaction that committed within T's own millisecond, after T, can be included
  through a segment or snapshot taken in that millisecond.
- A backward wall-clock step between a commit and the backup read that picks it up, or between a COMMIT row and its
  acknowledgement, cannot be detected. A step back between two transactions read after T is refused.
- Only the transactions downloaded after T are checked for times going back; earlier segments are trusted by their read
  times.
- A read writes a mark only when it appended changes and reached the newest commit: a read that stops at its byte
  budget, or an idle database, writes none (an idle database ships nothing). A commit made between a read's start
  and its query is not covered by that mark; no T at or before the mark needs it.
- A log written before timed commits and read marks restores only at snapshot times and at times its generation's next
  snapshot closes.
- Integration tests run TerraBase on a test clock while Turso stamps rows with the machine's clock; tests that place T
  between transactions set the recorded `change_time` themselves.

### Restore preview (TS-493)

`POST /v1/projects/{project}/databases/{database}/point-in-time/plan` with
`{"pointInTime":"…","destination":"customer"}` reads the selected source's
backup metadata without changing databases. It downloads only what the window's `latest` needs when it comes from the
bucket: at most three of the newest segments, for their read marks. It returns:

```json
{
  "window": {"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"},
  "pointInTime":"2026-10-07T12:00:13Z",
  "snapshotAt":"2026-10-07T12:00:10Z",
  "latestSegmentAt":null,
  "segmentCount":0,
  "historyCheck":"metadata"
}
```

`destination` is optional and defaults to `platform`; `customer` inspects only the attached customer bucket.
The snapshot is the newest at or before `pointInTime`, selected by the same code as
restore. `segmentCount` and `latestSegmentAt` describe that snapshot generation's
segments after its position and read by the requested time. The latter is null when
none apply. No object keys or credentials are returned.

The preview runs the restore's name-only check of [Proven history](#proven-history): the first segment starts at the
snapshot's position, each later one starts at or after the previous one, and the history reaches `pointInTime`. When
object names already show the history incomplete (a missing final segment, an ended generation, a later snapshot whose
position no stored segment reaches), it answers `409` `backup_history_incomplete`, as the restore would. `historyCheck`
is always `metadata`: segment names hold their start but not their length, so whether each segment begins exactly where
the previous one ends (a missing middle segment, or two segments with the same start where the first holds log) is
checked only by an actual restore, which can still answer `409`. The plan does not read transaction times either: a
restore can answer `409` `restore_point_unproven` where the plan succeeds. `window.latest` is the window's, as
`GET …/point-in-time` reports it; `pointInTime` may lie after it, up to the newest backed-up data.

The request needs a ts-auth project token granting `databases:read`; `{database}` is its name.
The hidden legacy management alias retains membership checks, including organization viewers. Access is checked
before time validation: another tenant's database returns `404`. Missing time, a time
outside the inclusive `window.earliest` and `window.latest` bounds returns `400`;
disabled/empty backup, a wholly expired retention window or no applicable snapshot returns `404`;
a history the object names show incomplete returns `409` `backup_history_incomplete`.
Preview uses the same restore-window bounds as restore. Metadata scanning is bounded to 10,000 items and 10 seconds;
exhaustion returns `503` without a partial plan (`pitr_plan_limit` or
`pitr_plan_timeout`). Object-store listing failures return `503`
`pitr_plan_unavailable` without exposing provider details.

This is an advisory preview. It does not reserve objects against retention, validate
their encrypted contents, or prove that segment lengths are contiguous. Restore still performs the download,
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
snapshot's exact log position), then compares a SHA-256 of the schema and every row of every table with the local copy
the snapshot was made from, which is the live file at that position. A drill exercises download, decryption, contiguity,
roll-forward and `integrity_check`. Every start snapshots every file, so every start also drills each file.

The checksum of the local copy is taken before the snapshot is uploaded, so the copy is deleted with the pass and no
copy waits for a drill. The drill itself then runs beside shipping: the file's segments keep shipping while it
downloads and rebuilds. A node runs one platform drill and up to two customer drills at a time, each rebuilding one
file in scratch, so a stalled customer bucket never holds a platform drill; the others wait their turn. Removal, a new
destination and shutdown cancel a drill; a cancelled drill records no result.

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

1. Grafana (fly-metrics.net): `terrabase_backup_lag_seconds` stays under 2 s under writes,
   `terrabase_backup_last_success_seconds` keeps moving, and `terrabase_backup_failures_total`,
   `terrabase_backup_holes_total` and `terrabase_backup_abandoned_total` do not rise. Lag approximates the recovery
   point only while holes and drops stay at zero (see [How shipping works](#how-shipping-works)). `/v1/backup` is on
   the loopback admin listener and the image has no shell, so the metrics are the way in.
2. Restore: `POST …/point-in-time/restore` of a scratch database to a second between two writes; check that the first
   write is in the new database and the second is not; delete the restored database.

## Tigris check (2026-10-11)

Checked against the Tigris dev bucket `ts-terrabase-dev`:

- A node shipped `v1/_system`: 1 snapshot, 2 log segments and 1 wrapped data key of 80 bytes (SQLite engine, before Turso).
- A second run with the same master key and a fresh data directory reused that key.
- A run with another master key failed closed: `wrapped data key is not authentic under the master key`.

## Compared with Litestream

Checked against Litestream 0.5.17 (August 2026): its design and its bug fixes from 0.3 to 0.5.17.

| Litestream | TerraBase |
| --- | --- |
| Separate process; a long read transaction stops other connections from restarting the log, plus checks for "overwritten by another process". Its read lock has been lost by mistake before (#1552). | In-process, and not the write-ahead log at all: Turso checkpoints it by itself. The tap reads the changes Turso captured on the only writer connection, anchored on the last row it read; a missing anchor is the "overwritten" case. |
| PASSIVE checkpoint at 1000 pages or each second; TRUNCATE above about 500 MB when long readers stop PASSIVE (RESTART removed). | Turso's own checkpoints. The tap trims `turso_cdc` after each read, keeping the newest row. |
| Snapshot read from the database file while writes go on; a slow first snapshot gave restores that fail `integrity_check` (#1309). | A row-by-row copy inside a read transaction started under the writer gate, at an exact log position. Every restore runs `integrity_check`, and the daily drill compares every row. |
| LTX files with TXIDs, compacted to 30 s, 5 min and 1 h levels for fast restores. | One segment per second per busy file, no compaction yet. Restore downloads 8 at a time; a busy file can need up to 86,400 segments a day. Compaction is a follow-up. |
| Restore applies whole files whose range fits the target; checks for gaps (0.5.12). | Same rule per segment; gaps fail the restore; GCM tags and a last-chunk flag catch truncated objects (0.5.17 "truncated page index"), and a log cut inside a transaction fails the replay. |
| Restores retry throttling and resume dropped S3 streams (0.5.8, 0.5.17). | Each object retried up to three times. |
| aws-chunked off for S3-compatible stores (0.5.6); payload signed (0.5.5). | Same: `UseChunkEncoding = false`, signed payload hash, checksums only when required. |
| Sync backoff up to 5 minutes. | Same. |
| Resumes a generation across restarts. | A new generation, so a new snapshot, per member start. |
| Retention keeps the snapshot before the window (0.5.14); a restore raced retention (#1450). | Same: the newest snapshot before the window is kept, plus one hour of grace. |
| Leases: S3 conditional writes (0.5.8) or a single process. | NATS KV leases with self-fencing before expiry. |
| Optional age encryption (0.3). | Always sealed: AES-256-GCM, per-project data keys wrapped with AES-256-GCM under the master key (`TERRABASE_MASTER_KEY`). |

## Why the change log, not the write-ahead log

Until the Turso engine (T1, 2026-10-12) the tap read SQLite's write-ahead log frames and checkpointed the log itself.
On Turso 0.8.2 that cannot work, measured with the engine itself:

- Turso checkpoints the log on its own and ignores `wal_autocheckpoint = 0`: 3000 commits restarted it 11 times, so
  the tap would find a hole on almost every pass.
- A checkpoint from a second connection is a second writer, which Turso does not survive (`turso-spike.md`).
- There is no online backup API (`BackupDatabase` fails with "database is locked"), `VACUUM INTO` on any connection but
  the writer copies an older view, and `wal_checkpoint(TRUNCATE)`, which would let the file be copied as is, waits for
  every reader, so a stalled reader would stop snapshots.

Turso's change capture is the mechanism design C already relies on (`multi-region-replication.md`, section 7), so the
backup reads the same table the change tailer will. What it keeps: sealed `TBK1` objects, the bucket layout and keys,
generations, daily snapshots, positions that prove contiguity, PITR to the second a pass read, daily drills, retention,
customer buckets and leases. What it changes: a snapshot is a logical copy (every rowid kept, the schema recreated from
its SQL), a segment is a change log, and replay applies rows instead of pages.

## Operations

- `GET /v1/backup` on the loopback admin listener lists each shipped file: generation, log bytes read, last shipped time, lag,
  pending bytes (read and not yet stored, sealed or not) and the kind of the last error.
- Metrics, over the files this member ships: `terrabase_backup_lag_seconds` (oldest data not yet in the bucket, not a
  loss bound), `terrabase_backup_last_success_seconds` (Unix time of the least recently shipped file) and
  `terrabase_backup_files`. Also `terrabase_backup_uploaded_bytes_total{kind}`, `terrabase_backup_failures_total`,
  `terrabase_backup_holes_total`, `terrabase_backup_abandoned_total{reason}` (unstored log dropped),
  `terrabase_backup_snapshots_deferred_total{reason}` (passes that put off a due daily snapshot),
  `terrabase_backup_generations_total` (a rising count means snapshots too often), `terrabase_backup_drills_total{result}`
  and `terrabase_backup_stored_bytes` (bucket bytes of the files this member ships).
  Bucket snapshots: `terrabase_backup_bucket_snapshots_total{result}` (taken, deleted, failed) and
  `terrabase_backup_bucket_snapshot_seconds` (Unix time of the newest snapshot, on the member that takes them; 0 elsewhere).
- `GET /v1/backup` also shows, per file, whether this member ships it.
- Scratch files (log waiting for upload, snapshots, sealed objects) live in `<data-directory>/.backup`, which is
  emptied at start.
