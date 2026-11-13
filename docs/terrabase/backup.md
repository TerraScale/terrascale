# Continuous backup

TerraBase streams every database file of a node (each database and `_system.db`) to Tigris,
about once a second, and restores a database as of a chosen second (TS-633).
Tigris remains managed through the Fly organization; compute runs on OCI.
The [Litestream](https://litestream.io/how-it-works/) design uses generations, snapshots and log segments.
Shipping captures the engine changes on each file's writer connection (the native capture table, mode `after`).
See [Tigris access](../tigris.md) for the retained storage integration.

Managed snapshots stay on the node as local snapshot files next to the database (`…/backups`). Restore copies that local file. Continuous shipping still uploads generations, snapshots and log segments.

## Status of the work

Slices 1 (shipping and restore), 2 (shipper leases) and 3 (retention by plan, daily restore drills, records in TerraBase)
of TS-633 are done. Not done yet:

- No disaster-recovery path that rebuilds a whole data directory (`_system.db` included) from the bucket.
- Objects of a deleted database stay in the bucket until its erasure pass deletes them, but its keys are destroyed at
  deletion, so they cannot be decrypted ([crypto-shredding](crypto-shredding.md)).
- No segment compaction (see [Compared with Litestream](#compared-with-litestream)).

## Production backup contract

Continuous backup is always on. Outside Development a node refuses to start (exit code 2, before it opens any file)
unless its Tigris bucket and master key are present, so no deployed node runs without backup. Only Development runs
without backup; those nodes emit `terrabase_backup_disabled = 1` and a critical startup log. Development refuses
cluster discovery through `TS_PEERS_FILE`. Retention and rotation use the correct settings in code; there is no
backup activation step or operator tuning table.

Tigris bucket settings use `AWS_ENDPOINT_URL_S3`, `AWS_REGION`, `BUCKET_NAME`,
`AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY`; `TERRABASE_MASTER_KEY` is a
32-byte base64 key delivered through OCI Vault. Mounted secret files are also supported.
The Tigris endpoint remains `https://fly.storage.tigris.dev`.

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
| POST | `D/point-in-time/restore` | `restorePointInTime` | `databases:write` | 202, `RestoreOperation` |
| GET | `D/backups` | `listBackups` | `databases:read` | 200 |
| POST | `D/backups` | `createBackup` | `databases:write` | 201 |
| POST | `D/backups/{backup}/restore` | `restoreBackup` | `databases:write` | 202, `RestoreOperation` |
| GET | `D/restores/{operation}` | `getRestoreOperation` | `databases:read` | 200 |
| GET | `D/backup-policy` | `getBackupPolicy` | `databases:read` | 200 |
| PUT | `D/backup-policy` | `setBackupPolicy` | `databases:write` | 200 |

Both restore routes answer `202` with a `RestoreOperation` and
`Location: …/databases/{database}/restores/{operation}`. Poll `GET D/restores/{operation}`: it reports `running`,
`completed` (its `database` names the new database) or `failed`. The restore runs in the background; the answer never
holds the new database itself.

The signed `project_id` must match `{project}`. Missing or invalid project credentials return 401;
missing scope returns 403; another project or a database outside the token's narrowing returns 404.
GET, PUT and DELETE of the project-wide destination also return 404 for a database-narrowed token.
User sign-in tokens reach these routes with their organization resolved from the project and
membership checked per operation; a project token cannot use another project's routes.

`mode=both` ships the project's encrypted snapshots and WAL segments to both buckets. Wrapped keys remain in the
unsnapshotted R2 custody prefix described by [F11](crypto-shredding-f11.md).
`mode=customer` ships that project's data only to the customer bucket. `_system.db` and other projects stay in the
platform bucket. Customer backup uses the same master key (`TERRABASE_MASTER_KEY`). This is customer-only **project shipping**, not a standalone backup service.

`GET` at the same path returns `endpoint`, `region`, `bucket`, `mode`, and `updatedAt`; it never returns credentials.
`DELETE` detaches the customer destination by dropping its record, sealed credentials included, and resumes platform
shipping. It keeps every object in the customer bucket. Reattaching the same bucket and credentials makes its older backups accessible again. Every save replaces the
credentials and starts a new generation with a snapshot, so a newly attached bucket never receives WAL without a base.
A viewer cannot save or detach destinations; other existing writable-project
roles can. Outsiders receive 404.
Deleting an unset destination is idempotent and returns 204. Saves admit at most four organizations concurrently,
with one save per organization, so one organization cannot occupy every slot. Each file's shipper refreshes
destination configuration at a ten-second cadence on successful passes; shipping work and retries can delay refresh.
Each lookup has a one-second deadline. A timeout cancels and joins the read, then uses the file's shipping backoff so
other files can be admitted.
Project deletion returns 409 while a customer destination remains configured; detach it first.
Saving, replacing or detaching an existing destination advances the organization's management version, invalidating
stale fences. Deleting an unset destination leaves that version unchanged.

The bucket must allow read, write, list and delete under `v1/<organization>/<project>/` and read/write/list/delete under
`v1/keys/<organization>/<project>/` (delete destroys a deleted database's keys). Saving checks the object permissions using a small non-secret probe and verifies
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
also return 503; corrupt snapshot or key material during restoration remains an integrity failure. A restore request answers
`202` with a `RestoreOperation` and `Location: /v1/projects/{project}/databases/{database}/restores/{operation}`; the
restored database's name comes from polling `GET …/restores/{operation}` until it reports `completed` (its `database`
field) or `failed`. A hidden legacy request retains its organization route and the source database's ID in `Location`.
Encryption and `TBK1` objects are unchanged; restoring either source still requires the master key (`TERRABASE_MASTER_KEY`).
In `both` mode, shipping succeeds only when both uploads succeed; a failed destination delays this project's progress
and retains the same segment until both copies are stored. A configured destination change starts a new generation with
a base snapshot in the new destination: a new lineage, not a hole ([Holes](#holes)). Platform files
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
project access token granting the scope above. User sign-in tokens reach these routes with their organization resolved from the project.
This TerraBase change supplies the server contract; it does not ship `ts-go` methods or a `ts-dashboard` bucket UI.
Those remain dependent integration work. Native dashboard sign-in is unchanged. No live dashboard
rollout is performed by this repository change.

### Leases

Each node ships the files it holds. `Engine/IShipperLeases` is `LocalShipperLeases`: `ShipperEpochAsync(database)` always
returns epoch 1, so the local node ships every local file and a generation changes only with a new snapshot. There is no
member ring, NATS membership or lease takeover. Every node taps every file and trims its change log as it ships it.
A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## How shipping works

Each file has one writer connection (the engine aborts the process when two connections write one file), and it captures
every change it commits in the table the native capture table, mode `after`: one row per inserted, updated or deleted row with its
after-image (SQLite's record format), a row for each schema change (`sqlite_schema`), and a COMMIT row ending each
transaction. The backup's tap reads that table:

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
2. Reading advances a cursor without acknowledging it. Only a stored snapshot or segment advances the stored cursor.
   Its generation, schema map, byte offset and capture position are saved immediately with each upload in the node's
   durable backup accounting record in the system database. An intent containing the object key and sealed-content hash
   precedes the upload. After a crash, a completed object matching that intent establishes the saved cursor; an absent
   object leaves the earlier cursor in place. This accounting is uncaptured engine bookkeeping, so storing the system
   database's cursor does not itself create another change to ship. The periodic public status is advisory.
   Acknowledgement follows the saved stored cursor, allowing the engine to trim older rows while retaining the anchor.
   On reopen, the saved account is loaded, an interrupted upload is resolved, and the decision of [Holes](#holes) runs.
3. A schema row the tap cannot place by name (an update such as `ALTER`, or a delete of a rowid it does not know) cannot
   be written to the log. Nothing the engine retains is lost, so it is not a hole: the next pass starts a new generation
   whose base snapshot holds the change (`terrabase_backup_generation_restarts_total{reason="log_unplaceable"}`).

   A delete in mode `after` carries only the rowid, and the engine does not capture every schema row (`ANALYZE` adds
   `sqlite_stat1` uncaptured), so `sqlite_schema` rowids differ between a file and a restore of it. The tap keeps the
   live schema's rowid → name map, as of its anchor, and logs a dropped object by name.
4. Nothing may write a file uncaptured, except state that a restore rebuilds itself (the change tailer's own bookkeeping,
   R3): a write transaction can turn capture off with `SuspendCaptureAsync`, and what it writes is in no backup. The
   writer connection holds no TEMP table, because the engine captures a TEMP table's rows as if they were in the file.

A **generation** is one unbroken run of the log from one file on one member. It starts with a **snapshot**: holding
the database's writer gate, so nothing commits, the tap reads what is left, moves its anchor to the newest change and
opens a read transaction; then writers go on, and the database is copied row by row, every rowid kept, as of that
transaction. The copy leaves out the engine's own tables (the native capture table, `sqlite_stat1`).
An intact generation resumes after a process restart or a lease handoff. A new generation starts for a file that has no
account yet, after a hole, for a new lineage (below), when a generation key is unavailable, or when the log cannot express
a schema change. Before a generation's snapshot holds
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
is due, the failed segment is sent again before anything more is read. Daily snapshots upload in the background while
segments ship; a failed upload keeps the segments going, and daily snapshot attempts back off on their own. Shutdown
ships each file's backlog within its budget and then closes the taps; it writes no accounting of its own.

### The backup account

Each file has one durable account in the node's system database (`_tb_records`, kind `backup_continuity`, keyed by the
file's bucket directory), replaced whole on every write. It holds:

- the lineage: the engine file's incarnation and the destination revision it ships to;
- the stored generation: its id, data key, snapshot time, byte offset, the time of the newest stored read and the
  proven restore time, and the **stored cursor**, the capture position up to which history is in the object store.
  It is empty while a new generation waits for its base snapshot;
- the upload intent: the object key and sealed-content hash of an upload that has not been confirmed yet;
- the hole history: the number of holes and the last hole's range (stored cursor and retained anchor, with their times).

No path writes an account before it has loaded the existing one, so a file whose recovery has not run is never written
to, shutdown included. A successful upload is always ordered the same way: the intent is saved, the bucket accepts the
object, the stored cursor is saved, and only then is the retained history released to the engine.

### Holes

A **hole** is history that the engine no longer retains and that never reached the object store. It is a fact about
two durable positions, read at any time: the **retained anchor**, the oldest capture position from which the engine can
still read the file's log forward, and the account's stored cursor. When the retained anchor is beyond the stored
cursor, the changes in between are gone.

That comparison is made in one place: at the start of every shipping pass of a file, before anything is read (the
recovery after a start runs the same code). When the anchor is beyond the cursor, one account write records the hole
(the count plus one, the range from the cursor to the anchor) and replaces the stored generation with an empty one; the
next due pass takes the new generation's base snapshot, and storing it closes the hole's range at the snapshot's
position and time. Because the write that counts the hole is the same write that removes the cursor it was measured
from, a crash at any point leaves either the old account (the next pass makes the same decision) or the new one (there
is no stored cursor left to compare), so a hole is never counted twice and never missed.

No event records a hole. Shutdown, a cancelled or failed capture or read, a lease or epoch change, a lost upload
acknowledgement and buffered log waiting in scratch stop the pass or retry it, nothing more. Whatever they cost shows up
in the two positions, and the next pass finds it. A SIGKILL with the engine's history intact finds the anchor at or
before the cursor and resumes; a restart after history was trimmed finds the anchor beyond it and records one hole.

A destination change, a restore or a fork gives the file a new lineage (another incarnation or destination than the
account's). It starts a new generation with a base snapshot and is not a hole, because nothing of the file's history
was lost; `terrabase_backup_generation_restarts_total{reason="lineage"}` counts it, beside `key_lost` (the
generation's data key is gone) and `log_unplaceable` (step 3 above). The hole history is kept across lineages.

The hole total of each file is published from its account: `terrabase_backup_holes` is the sum over the files this node
has loaded, so after a restart it equals the durable total again, and `GET /v1/backup` shows each file's `holes` and
`lastHole`. `terrabase_backup_holes_recorded_total` counts the account writes that recorded a hole on this process, and
each one logs a warning with the range.

### The cap

The engine keeps every change after the durable anchor, so an unreachable bucket would grow the file without bound.
Every pass, before the hole decision and also while the file backs off, the shipper measures the unacknowledged capture
payload and row overhead. When it exceeds 256 MiB, or the shared disk admission refuses that many bytes with its
protected headroom, the shipper discards: it moves the engine's durable anchor to the committed end, and the engine
trims older rows. That is the whole discard; it records nothing. Once the trim passes the stored cursor, the next pass
finds the anchor beyond it and records the hole. A later discard during the same outage finds no stored cursor (the
generation waits for its base snapshot) and adds no hole, so an outage of any length is exactly one hole.

### Lag and health

Lag is observed apart from shipping, so a stuck or stopped shipper cannot hide it. An observer runs every 5 s over the
files this node has open: it reads the engine's committed end (the newest COMMIT position) and the account's stored
cursor. When the committed end is beyond the cursor the file has a backlog, aged with the monotonic clock from the
moment this node first saw a committed position beyond the cursor; the age carries across passes and restarts only
when the stored cursor reaches that position. A wall-clock step cannot shorten it. `terrabase_backup_lag_seconds` is
the largest age over the files this node ships, `lagSeconds` in `GET /v1/backup` is each file's, and
`backlogCommittedAt` reports the commit time of the oldest unstored transaction for information only.

`/backupz` answers 200 only when all of these hold, and 503 otherwise:

- discovery has listed the files this node is responsible for, from the catalog and the local files, before any of
  them recovers (the system database always counts, so the set is never empty while the node has a catalog);
- every file in that set whose lease this node holds or has not checked yet has finished recovery (account loaded,
  intent resolved), has a stored generation and a first upload, and has less than 60 s of lag;
- the lag observer has run within the last 15 s.

A file that failed to open or to recover keeps the probe at 503. `/readyz` does not depend on backups: a backup problem
must be loud and must stop a rollout, but must not stop customers' queries.

Each object is sealed before upload (`Features/Backup/BackupObject.cs`, format 1): chunks of up to 1 MiB, LZ4-compressed
when smaller, AES-256-GCM. Each object's AES key is HKDF-SHA256 of its database's data key, a random salt and the object's
own key in the bucket, so an object moved elsewhere does not decrypt. Data keys are generated by TerraBase, wrapped with AES-256-GCM under the master key
(`TERRABASE_MASTER_KEY`) and stored in unsnapshotted R2 custody, outside every backup bucket. Only a node with the master key can unwrap them. Each database gets its own data
keys, and `_system.db` gets its own too. Deleting a database destroys its keys ([crypto-shredding](crypto-shredding.md)).

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
older object of the same length may already sit at the key. The upload counts only when a HEAD of the key then reports
exactly the uploaded length and one of two proofs:

- **Upload id.** Each upload starts with its own ULID as user metadata (`x-amz-meta-terrabase-upload`), which S3 copies
  to the completed object.
- **Multipart ETag.** S3's ETag of a multipart object: the MD5 of the binary MD5s its parts' uploads returned as ETags,
  then `-` and the part count, computed from this upload's own part ETags. An older object of the same length but
  another body has another ETag; one with the identical body (and part split) is the same data. Stores whose part
  ETags are not MD5s give no such proof.

Missing or other evidence fails the upload, so the shipper never records an object the bucket does not hold. The limit:
a store that keeps no user metadata through a completion and has no S3-style ETags (the PitrLab emulator before it kept
metadata was one) fails every multipart upload, so snapshots above 128 MiB never ship there. When a completion answered
200 but its object is unproven, a snapshot key is this attempt's alone (it names the pin time, a generation and a
position; the shipper stores each snapshot key once and pins a new snapshot under a new key to retry, and nothing else
writes snapshot keys), so the object there is deleted (logged, `terrabase_backup_unproven_uploads_deleted_total`) rather
than left as a whole unrecorded snapshot. Another key, such as a log segment's that a retry rewrites with the same bytes,
keeps its object. Tigris support for either proof (user metadata kept through `CompleteMultipartUpload` and returned by
HEAD; S3-style part and multipart ETags) is unverified until G1; if Tigris gave neither, large uploads would fail
visibly, never succeed falsely.

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
backups are still retained; only a database's deletion destroys its keys.

This rotation preserves the existing backup bytes, bucket layout and database files. Live database files, WAL files, local
managed snapshots and restore scratch files still require encryption of the node's storage; TS-702's disk-encryption
and in-place migration work follows separately.

## Bucket layout (version 1)

```text
v1/<organization>/<project>/<database>/snapshots/<unix ms>-<generation>-<log position>
v1/<organization>/<project>/<database>/wal/<generation>/<first log position>-<unix ms>
v1/_system/…                                  the same for _system.db
v1/keys/<organization>/<project>/<database>/<data key id> wrapped data keys (v1/keys/_system/… for _system.db)
```

Times are 13-digit Unix milliseconds and positions 16 digits, so key order is time order for snapshots and log order for
segments. A position counts bytes of the generation's log. The `wal/` prefix is kept from the SQLite format; it now
holds change log segments. A snapshot's time is when its read transaction was pinned under the writer gate, before the copy begins.
Copying and uploading do not move that boundary; later writes are shipped in subsequent log segments. A segment's time
is when its last change was read.

## Restore

A restore always creates a new database, with a new incarnation, in the region that serves the source database.
Native SQL tables, their indexes, views, triggers and constraints restore with their rows; a replica that pulls a
restored database with a revision of its source rebootstraps (`OtherIncarnation`): a restore never continues a
replica's incremental history.
Backups taken before the TS-602 wipe use the old schema and do not need to restore after
it (user decision, 2026-10-07). Production backup starts right after that cutover.

`GET /v1/projects/{project}/databases/{database}/point-in-time` returns the restore window: from the oldest snapshot,
but no earlier than the plan's retention window (a bucket rule may keep older objects), to the newest time the backup
proves (see [Proven history](#proven-history)): read from the bucket, the second at or before the newest read mark in
the newest generation's last three segments (at most three object reads; a segment that is gone, fails authentication
or is cut short counts as holding none), or its newest snapshot's time. While this node ships the database to the
platform bucket, the shipper gives `latest` from what it has stored, without reading the bucket: the later of its
generation's newest stored snapshot's time and, for the newest stored segment holding a read mark, the second at or
before its newest mark, or that mark's own time, to the millisecond, when the mark ends a segment read in that
millisecond. A daily snapshot moves it only once stored, and a
read stopped by its budget, which writes no mark, does not move it. It returns 404
when no stored data falls within the plan's window, including when retained objects have all expired under the plan.
Metadata scanning is bounded to 10,000 objects per scan; exhaustion returns `503` `pitr_plan_limit` (the preview's
bound, `TS-1301`).
`POST /v1/projects/{project}/databases/{database}/point-in-time/restore` with `{"pointInTime": "…", "name": "…"}`
restores into a new database of the same project (`branchedFrom` set, like a backup restore). Before any replay work,
the restore prechecks what the new database's registration would refuse: the organization's database allowance, and a
chosen `name` against the project's taken names (`409` `name_taken` or `409` `database_limit`, the same errors create
returns); an automatic `<source>-restore` name is picked after the replay, so only the allowance is prechecked for it.
The registration re-checks both when it commits. A rewind restores in place and creates nothing, so it prechecks
nothing. The restore:

1. takes the newest snapshot at or before `pointInTime`;
2. checks from object names that the history up to `pointInTime` can be complete (see
   [Proven history](#proven-history)), before downloading anything;
3. replays its generation's segments read by `pointInTime` in order, checking they are contiguous: one transaction per
   commit record, rows by rowid (`INSERT OR REPLACE`, `DELETE`), schema objects created from their SQL and dropped by
   name. The log already holds every row a trigger or a foreign-key action wrote, so the replay runs with foreign keys
   off and with the file's triggers set aside (dropped, tracked through replayed CREATE and DROP records, and created
   again once the replay ends); a trigger never fires twice and a replayed parent row never cascades. A storage rewrite
   replays its delta the same way. A log that ends inside a transaction, a malformed record or a change the file
   refuses fails the restore.
   Eight segments download at once, each retried up to three times if the stream drops. Then the history must be
   proven to reach `pointInTime`, replaying the transactions of later segments that ran before it;
4. runs `PRAGMA integrity_check`, then imports the file as a copy, so its replication starts over from a baseline.

A restore answers 400 only for a time no backup can ever hold: after now, or before the plan's retention window. A time
in between that lies outside the restore window (before its oldest stored snapshot, or after the newest backed-up data:
on the node that ships the database, the window's `latest`; otherwise the newest segment's time) answers `409`
`backup_history_incomplete`, which a later retry can turn into a restore once the backup holds it: an unshipped final
segment, or one or a snapshot lost from the bucket. So a delayed shipper cannot silently restore older data for a time it
has not shipped. Off the shipping node, between the window's `latest` and the newest segment's time, a restore succeeds
only when it proves the time.
The shared restore service validates these bounds once before downloading or importing a database. It captures the
retention reference time before reading metadata and uses one snapshot scan for both the window and snapshot selection,
so metadata latency cannot move the retention floor during a restore. Window reads and previews also capture their
reference time before reading metadata. Restore and window metadata scans are bounded like the preview's (10,000
objects per scan, `503` `pitr_plan_limit` beyond), so a long history cannot make a restore scan unbounded.

The result holds every transaction committed before `pointInTime`, or the restore answers `409`.

### Restore in place

`POST /v1/projects/{project}/databases/{database}/point-in-time/rewind` with `{"pointInTime": "…"}` (optional
`destination`) restores the database itself: it rebuilds the state as of `pointInTime` exactly like a restore, then, in
one transaction on the database's writer, drops its tables, indexes, views and triggers and recreates them with every
row (rowids kept) from that state; foreign keys are off and triggers are created after the rows. The database keeps its
id, name, credentials and database allowance, so a Free organization can recover from an accidental `DROP` or
`DELETE`. Changes committed after `pointInTime` are discarded. The rewind is captured like any write, so a later restore
can undo it, and it starts a new revision incarnation (D18): a replica holding an older revision rebootstraps
(`OtherIncarnation`) instead of continuing incrementally. The time rules and errors match a restore; nothing changes on
an error. CLI: `ts backup rewind <db> <time> -yes`; dashboard: Backups → point-in-time → Restore in place. A deleted
database stays unrestorable.

### Proven history

A restore at `pointInTime` (T, taken to the millisecond) returns `201` only with whole transactions: every transaction
committed before T, so every write acknowledged before T, and none whose COMMIT row was written at or after T.
Otherwise it answers `409` and creates nothing. T, segment and snapshot times, read marks and the engine's `change_time` are
all the node's wall clock, and "acknowledged before T" is measured on it.

What the engine records (measured on the driver, the capture timestamp tests): every captured row, the COMMIT row included, holds
`change_time`, the wall clock when the engine wrote that row, in whole Unix seconds rounded down; rows of one transaction
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
- Nothing after the replayed log proves T: an unshipped or missing final segment, a generation that ended (a hole or a
  new generation; the next generation's snapshot time restores), or a quiet period not yet closed by a later write or
  snapshot.
- T inside the last recorded hole of the file: after its last proven time and before the snapshot that followed the
  hole. The 409 `backup_history_incomplete` names that interval in its message and in `unavailableFrom` and
  `unavailableTo`; it is not retryable. A restore request is refused at admission, before any work starts.
- A snapshot or segment that disappears between listing and download (retention racing the restore). Admitting a
  point-in-time restore writes a generation lease (source database + the plan's generation, renewed every 5 minutes
  while the restore runs, deleted when it settles); continuous-backup retention skips that generation's snapshot and
  segment objects for the pass, and the database-deletion purge defers erasing continuous-backup objects (failure
  reason `restore_in_progress`) until the lease is gone, so a running restore never loses an object it still reads.

`409` `restore_point_unproven`: the history is there but T cannot be placed between two transactions, in a segment read
after T: a transaction whose COMMIT row's second contains T (times are whole seconds), one without recorded times (a
log written before they were recorded, or a clock that went back within it), or times that go back between two
transactions. A whole second, or a time in a second in which no transaction committed, can be placed; the message of a
time within a commit second names that second's start, the nearest earlier time it can place.

`409` `backup_unreadable`: an object the restore needs fails authentication (a corrupted object, a data key the node's
master key did not wrap) or is cut short, or the replayed file fails its integrity check. Each download is tried three
times first. Like the other refusals, nothing is created.

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
- A read mark rests on the engine showing a commit that returned on the writer's connection to the tap's next autocommit read
  on its own connection (the capture timestamp tests checks it under concurrent commits); the tap's connection must not hold a
  read transaction open across reads (only a snapshot opens one, and rolls it back).
- `_system.db` restores only at its snapshot times. A catalog follower applies the leader's writes without capture, so
  neither its log nor a later snapshot at the same position shows what was written after a snapshot. No point-in-time
  restore of `_system` exists yet; the reader refuses any other time for it.
- Integration tests run TerraBase on a test clock while the engine stamps rows with the machine's clock; tests that place T
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
Organization viewers keep their restricted role. Access is checked
before time validation: another tenant's database returns `404`. A missing time, or a time after now or before the
plan's retention window, returns `400`; any other time outside the restore window returns `409`
`backup_history_incomplete`, exactly as the restore answers it;
disabled/empty backup, a wholly expired retention window or no applicable snapshot returns `404`;
a history the object names show incomplete returns `409` `backup_history_incomplete`.
Preview uses the same restore-window bounds as restore. Metadata scanning is bounded to 10,000 items and 10 seconds;
exhaustion returns `503` without a partial plan (`pitr_plan_limit` or
`pitr_plan_timeout`). Object-store listing failures return `503`
`pitr_plan_unavailable` without exposing provider details.

This is an advisory preview. It does not reserve objects against retention, validate
their encrypted contents, or prove that segment lengths are contiguous. Restore still performs the download,
authentication, continuity and integrity checks. The project route follows [the HTTP API table](api.md).

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
destination and shutdown cancel a drill; a cancelled drill records no result. When a generation ends while its drill
or snapshot upload runs (a hole or a new generation), the next generation's snapshot is drilled.

A failed drill is logged as an error and counted in `terrabase_backup_drills_total{result="failed"}`.

## Records in TerraBase

After each hourly retention pass and each drill, the shipper writes the file's record into `_system`: table
`databases/<database id>/backup_status`, id `current` (`backup_status`/`_system` for `_system.db`). The record holds the
shipping member, generation, last shipped time, lag, bucket size (measured daily), retention days and the last drill.
The database's state is deleted with it. Each machine writes these records to its own catalog; they are not replicated
until R10a.

## Verify a rollout

`/backupz` answers as [Lag and health](#lag-and-health) defines; `/readyz` does not depend on it.
A `/backupz` 503 must stop rollout advancement. Verify that the current runtime
enforces this gate; the requirement is not evidence of an implemented OCI rollout gate.
A Tigris outage appears on `/backupz` and in backup metrics.

After a rollout, also check:

1. Metrics: `terrabase_backup_lag_seconds` stays under 2 s under writes,
   `terrabase_backup_last_success_seconds` advances, the failure counter does not rise and `terrabase_backup_holes`
   does not change. Lag approximates the recovery point only while the hole total stays the same.
2. Restore: `POST …/point-in-time/restore` of a scratch database to a second between two writes; check that the first
   write is in the new database and the second is not; delete the restored database.

## Operations

- `GET /v1/backup` on the loopback admin listener lists each shipped file: generation, log bytes read, last shipped time, lag,
  the commit time of the oldest unstored transaction, pending bytes (read and not yet stored, sealed or not), the kind of
  the last error, and its hole count and last hole.
- Metrics, over the files this member ships: `terrabase_backup_lag_seconds` (monotonic age of the oldest committed change
  not yet in the bucket, not a loss bound), `terrabase_backup_last_success_seconds` (Unix time of the least recently shipped file) and
  `terrabase_backup_files`. Also `terrabase_backup_uploaded_bytes_total{kind}`, `terrabase_backup_failures_total`,
  `terrabase_backup_holes` (durable hole total of the loaded accounts), `terrabase_backup_holes_recorded_total`,
  `terrabase_backup_generation_restarts_total{reason}` (lineage, key_lost, log_unplaceable),
  `terrabase_backup_snapshots_deferred_total{reason}` (passes that put off a due daily snapshot),
  `terrabase_backup_generations_total` (a rising count means snapshots too often), `terrabase_backup_drills_total{result}`
  and `terrabase_backup_stored_bytes` (bucket bytes of the files this member ships).
  `terrabase_backup_unproven_uploads_deleted_total` counts snapshots whose multipart upload completed without proof and
  were deleted ([Uploads](#uploads)); a rising count means the bucket keeps neither proof.
  Bucket snapshots: `terrabase_backup_bucket_snapshots_total{result}` (taken, deleted, failed) and
  `terrabase_backup_bucket_snapshot_seconds` (Unix time of the newest snapshot, on the member that takes them; 0 elsewhere).
- `GET /v1/backup` also shows, per file, whether this member ships it.
- Scratch files (log waiting for upload, snapshots, sealed objects) live in `<data-directory>/.backup`, which is
  emptied at start.

## Restore acceptance and catalog security

Production Tigris recovery needs separate acceptance evidence. The catalog security
gate is held separately in [PR #213](https://github.com/TerraScale/terrascale/pull/213).
