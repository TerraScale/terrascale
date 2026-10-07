# Continuous backup

TerraBase streams every SQLite file of a node (each database and `_system.db`) to an S3-compatible bucket, OCI Object
Storage in production, about once a second, and restores any database as of a chosen second (TS-633). The design follows
[Litestream](https://litestream.io/how-it-works/) (generations, snapshots, log segments), written in C# inside the
server. It ships the SQLite write-ahead log per file, not per API database, so it keeps working whatever sits above SQLite.

The older scheduled backups (`…/backups`, local snapshot files next to the database) are unchanged. They are not off-node.

## Status of the work

Slices 1 (shipping and restore) and 2 (NATS leases) of TS-633 are done. Not done yet:

- Nothing is deleted from the bucket. Next: retention by plan (1 day hobby, 7 days pro, 30 days enterprise).
- No daily restore drill, metrics stored in TerraBase, or restore over an existing database.
- No disaster-recovery path that rebuilds a whole data directory (`_system.db` included) from the bucket.

## Settings

Backup is off unless all of these are set. Partial settings stop the process (exit code 2). Errors name settings, never
values.

| Setting | Meaning |
| --- | --- |
| `backup-endpoint` | S3-compatible endpoint, `https://<namespace>.compat.objectstorage.<region>.oraclecloud.com` |
| `backup-region` | Its signing region, e.g. `sa-saopaulo-1` |
| `backup-bucket` | Bucket, e.g. `gru-1-backup` |
| `backup-access-key-id` | Access key of the S3 credential (an OCI customer secret key) |
| `backup-secret-key-file` | File holding its secret |
| `backup-kms-endpoint` | Cryptographic endpoint of the OCI Vault holding the master key |
| `backup-kms-key-id` | OCID of the master encryption key |
| `backup-kms-api-key` | OCI API signing key id, `<tenancy OCID>/<user OCID>/<fingerprint>` |
| `backup-kms-private-key-file` | PEM private key of that API key |

Give the backup identity only what it needs: object read, write and list in the backup bucket, and `use` of the one
master key.

### Leases

One member ships each file. `Engine/IShipperLeases` chooses it: `ShipperEpochAsync(database)` returns the epoch of this
member's lease on that file while it ships it, or null. TS-647 implements it in `Engine/Cluster` with the database's
primary member lease, so the shipper follows the primary. Until then there are two interim implementations, neither tied
to the catalog primary:

- **Without `nats-url`**, the member ships every file (`SingleShipper`). This is for one member and tests: backup with
  `peers`, `peer-addresses` or `catalog-primary` but no `nats-url` stops the process with exit code 2.
- **With `nats-url`**, the members share ts-orchestrator's NATS (JetStream KV), the store TerraBase uses for external
  persistence, and each file has a lease (`LeasedShippers`), described below.

| Setting | Meaning |
| --- | --- |
| `nats-url` | Comma-separated `tls://host:port` (or `nats://host:port`) servers of the cell's NATS |
| `nats-credentials-file` | NATS user credentials (`.creds`: user JWT and nkey seed), required with `nats-url` |
| `nats-certificate`, `nats-private-key`, `nats-ca` | PEM client certificate, its key and the servers' CA, for mutual TLS; all or none |

- Each file's lease is the key `<file directory with dots for slashes>` of the KV bucket `terrabase_backup_leases`
  (`max_age` 15 s, created on first use with three replicas, one on a single server). The value names the member and its
  process, so a restarted member never mistakes its previous lease for its own.
- Every second each member tries, at most every 5 s, to create the keys of the files it does not ship. The holder
  rewrites its keys every 5 s at the revision it holds. A lost revision means another member holds the file: it stops.
- A holder that could not renew for 10 s stops shipping by its own monotonic clock, 5 s before the key can expire for
  the others, so two members ship the same file only under clock drift beyond that margin. Even then nothing breaks:
  each ships its own generation, and a restore uses one generation.
- Whatever the implementation, a new epoch starts a new generation with a snapshot: the member taking the file over
  ships a different physical file than the previous holder. At shutdown a holder ships what is left and deletes its keys, so another member takes over at once.
  Otherwise takeover waits for the key to expire: about 15 to 20 s.
- Every member taps every file and checkpoints it (`wal_autocheckpoint = 0` wherever backup is configured), whether or
  not it ships it.

## How shipping works

On the shipping member each SQLite connection runs with `wal_autocheckpoint = 0`, and the backup's log tap
(`Engine/Sqlite/SqliteWalTap.cs`) checkpoints instead:

1. Each second it reads the frames committed since its last read straight from `<file>-wal`. It validates salts and
   checksums as SQLite recovery does and stops at the last commit frame.
2. Once the log holds 1000 frames (SQLite's default), it takes the database's in-process writer gate, reads the rest,
   and runs `PRAGMA wal_checkpoint(PASSIVE)`. The log restarts (new salts, checkpoint sequence + 1) only after a
   checkpoint copied every frame back, so no frame is overwritten before the tap reads it.
3. Anything else that moves the log is a **hole**: the file starts a new generation, and `terrabase_backup_holes_total`
   counts it. These are Litestream's "wal overwritten/truncated by another process" checks:
   - a restart not preceded by the tap's own complete checkpoint, with nothing read since (busy 0, every frame copied
     back), or a restart that moves the sequence by more than one;
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
own key in the bucket, so an object moved elsewhere does not decrypt. Data keys come from OCI Vault (`generateDataKey`
on the master key) and are stored in the bucket wrapped. Only the Vault can unwrap them. Each project gets its own data
key, and `_system.db` gets one too.

## Bucket layout (version 1)

```text
v1/<organization>/<project>/<database>/snapshots/<unix ms>-<generation>-<frame position>
v1/<organization>/<project>/<database>/wal/<generation>/<first frame position>-<unix ms>
v1/_system/…                                  the same for _system.db
v1/keys/<organization>/<project>/<data key id> wrapped data keys (v1/keys/_system/… for _system.db)
```

Times are 13-digit Unix milliseconds and positions 16 digits, so key order is time order for snapshots and frame order for
segments. A segment's time is when its last frame was read.

## Restore

A restore always creates a new database, so a new incarnation that reaches the other members through replication (with
TS-647, through anti-entropy). Backups taken before the TS-602 wipe use the old schema and do not need to restore after
it (user decision, 2026-10-07). Production backup starts right after that cutover.

`GET …/databases/{database}/point-in-time` returns the restore window: the oldest snapshot and the newest data in the
bucket. `POST …/databases/{database}/point-in-time/restore` with `{"pointInTime": "…", "name": "…"}` restores into a
new database of the same project (`branchedFrom` set, like a backup restore). The restore:

1. takes the newest snapshot at or before `pointInTime`;
2. applies its generation's segments up to `pointInTime` in order, checking they are contiguous. Each frame's page is
   written in place, and the file is cut at every commit that shrinks the database, as a checkpoint at that commit
   would. Eight segments download at once, each retried up to three times if the stream drops;
3. runs `PRAGMA integrity_check`, then imports the file as a copy, so its replication starts over from a baseline.

The result holds every transaction the shipper read by `pointInTime`. The shipper reads every second, so a transaction
committed in the second before `pointInTime` may be missing.

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
| Retention keeps the snapshot before the window (0.5.14); a restore raced retention (#1450). | Retention is not built yet; it must keep the snapshot before the window start and leave a margin for running restores. |
| Leases: S3 conditional writes (0.5.8) or a single process. | NATS KV leases with self-fencing before expiry. |
| Optional age encryption (0.3). | Always sealed: AES-256-GCM, per-project data keys wrapped by OCI Vault. |

## Operations

- `GET /v1/backup` on the loopback admin listener lists each shipped file: generation, frames, last shipped time, lag,
  pending bytes and the kind of the last error.
- Metrics, over the files this member ships: `terrabase_backup_lag_seconds` (oldest data not yet in the bucket),
  `terrabase_backup_last_success_seconds` (Unix time of the least recently shipped file) and `terrabase_backup_files`.
  Also `terrabase_backup_uploaded_bytes_total{kind}`, `terrabase_backup_failures_total`, `terrabase_backup_holes_total`
  and `terrabase_backup_generations_total` (a rising count means snapshots too often).
- `GET /v1/backup` also shows, per file, whether this member ships it.
- Scratch files (frames waiting for upload, snapshots, sealed objects) live in `<data-directory>/.backup`, which is
  emptied at start.
