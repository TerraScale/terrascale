# Continuous backup

TerraBase streams every SQLite file of a node (each database and `_system.db`) to an S3-compatible bucket, OCI Object
Storage in production, about once a second, and restores any database as of a chosen second (TS-633). The design follows
[Litestream](https://litestream.io/how-it-works/) (generations, snapshots, log segments), written in C# inside the
server. It ships the SQLite write-ahead log per file, not per API database, so it keeps working whatever sits above SQLite.

The older scheduled backups (`…/backups`, local snapshot files next to the database) are unchanged. They are not off-node.

## Status of the work

This is slice 1 of TS-633. Not done yet:

- The catalog primary is the only member that ships (`TerraBaseOptions.ShipsBackup`). If it is down, nothing ships.
  Next: a lease per file in ts-orchestrator's NATS, so any member ships and another takes over.
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

## How shipping works

On the shipping member each SQLite connection runs with `wal_autocheckpoint = 0`, and the backup's log tap
(`Engine/Sqlite/SqliteWalTap.cs`) checkpoints instead:

1. Each second it reads the frames committed since its last read straight from `<file>-wal`. It validates salts and
   checksums as SQLite recovery does and stops at the last commit frame.
2. Once the log holds 1000 frames (SQLite's default), it takes the database's in-process writer gate, reads the rest,
   and runs `PRAGMA wal_checkpoint(PASSIVE)`. The log restarts (new salts, checkpoint sequence + 1) only after a
   checkpoint copied every frame back, so no frame is overwritten before the tap reads it.
3. A restart that moves the checkpoint sequence by more than one means something else checkpointed. The tap reports a
   hole and the file starts a new generation.
4. The tap keeps a connection open, so SQLite never checkpoints and deletes the log when the last connection closes.

A **generation** is one unbroken run of frames from one file on one member. It starts with a **snapshot**: under the
writer gate the tap reads every committed frame and starts a read transaction, then copies the database page for page
with SQLite's online backup API inside it. A generation starts when shipping starts (every process start), after a hole,
or after 256 MiB of frames could not be stored. Within a generation a snapshot is taken daily. Frames read in one pass
become one **segment**.

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

`GET …/databases/{database}/point-in-time` returns the restore window: the oldest snapshot and the newest data in the
bucket. `POST …/databases/{database}/point-in-time/restore` with `{"pointInTime": "…", "name": "…"}` restores into a
new database of the same project (`branchedFrom` set, like a backup restore). The restore:

1. takes the newest snapshot at or before `pointInTime`;
2. applies its generation's segments up to `pointInTime` in order, checking they are contiguous (each frame's page is
   written in place, then the file is cut to the last commit's size, as a checkpoint does);
3. runs `PRAGMA integrity_check`, then imports the file as a copy, so its replication starts over from a baseline.

The result holds every transaction the shipper read by `pointInTime`. The shipper reads every second, so a transaction
committed in the second before `pointInTime` may be missing.

## Operations

- `GET /v1/backup` on the loopback admin listener lists each shipped file: generation, frames, last shipped time, lag,
  pending bytes and the kind of the last error.
- Metrics: `terrabase_backup_lag_seconds` (oldest data not yet in the bucket, over all files),
  `terrabase_backup_last_success_seconds` (Unix time of the least recently shipped file),
  `terrabase_backup_uploaded_bytes_total{kind}`, `terrabase_backup_failures_total`, `terrabase_backup_files`.
- Scratch files (frames waiting for upload, snapshots, sealed objects) live in `<data-directory>/.backup`, which is
  emptied at start.
