# Litestream backup supervision

TerraBase stores one SQLite file per tenant database under the member's
engine location. Each member process can supervise an embedded
[Litestream](https://litestream.io/) child that continuously replicates
those files to a replica for point-in-time recovery. Backup is disaster
recovery only (D-29): replication never sits in the serving path, and a
member keeps serving when the replica or the replication child is down.

## Supervision model

The `terrabase-backup` crate writes one Litestream config and runs
`litestream replicate` as a supervised child:

- The config watches the whole engine location (`dir` + `pattern` +
  `watch`), so databases created after startup start replicating on their
  own and deleted databases stop replicating without touching the child.
- The child runs in its own process group with a cleared environment. Only
  the S3 credential variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`,
  `AWS_SESSION_TOKEN`, `LITESTREAM_ACCESS_KEY_ID`,
  `LITESTREAM_SECRET_ACCESS_KEY`) are copied through, and only when an S3
  replica is configured.
- An unexpected exit restarts the child with bounded exponential backoff
  (1 s initial, doubling, 60 s cap, reset after a stable run).
- Shutdown is SIGTERM, a bounded grace period, then SIGKILL to the whole
  process group. Dropping the supervisor handle without calling shutdown
  terminates the child the same way.
- Retention is disabled in the child (`retention.enabled: false`): the
  replica provider's lifecycle rules own expiration (D-25).

Litestream has no SIGHUP and no config hot reload. Every settings change —
replica destination, sync interval, pattern — means restarting the child.
The supervisor treats that as normal operation: change the environment,
restart the member.

Litestream assumes WAL mode and takes over checkpointing on the databases
it manages. Nothing in TerraBase may schedule `TRUNCATE` checkpoints on
replicated database files.

## Environment variables

Supervision is opt-in and configured entirely through the environment:

| Variable | Meaning |
| --- | --- |
| `TERRABASE_BACKUP_SUPERVISION` | Set to `1` to supervise replication. |
| `TERRABASE_BACKUP_REPLICA_DIR` | Local directory for the default `file` replica. |
| `TERRABASE_BACKUP_S3_ENDPOINT` | S3-compatible endpoint. Setting the full S3 set below selects the S3 replica instead of the file replica. |
| `TERRABASE_BACKUP_S3_BUCKET` | Destination bucket. |
| `TERRABASE_BACKUP_S3_PATH` | Prefix inside the bucket dedicated to this member. |
| `TERRABASE_BACKUP_S3_REGION` | Signing region (ignored by OCI, required by Litestream). |
| `TERRABASE_BACKUP_PATTERN` | Watched-file glob; defaults to `*.sqlite3`. |
| `TERRABASE_BACKUP_METRICS_ADDR` | Bind address for the child's Prometheus `/metrics` endpoint, for example `127.0.0.1:19090`. Off by default. |
| `TERRABASE_LITESTREAM_BIN` | Overrides the Litestream binary path; otherwise `litestream` is searched on `PATH`. |

S3 credentials are never configured through TerraBase: the child reads the
standard `AWS_*` or `LITESTREAM_*` credential variables from the member's
environment.

A requested-but-failed start is loud but never fatal: the daemon logs the
failure, marks the `backup` component unhealthy, and keeps serving.

### OCI Object Storage example

```sh
export TERRABASE_BACKUP_SUPERVISION=1
export TERRABASE_BACKUP_S3_ENDPOINT='https://<namespace>.compat.objectstorage.<region>.oraclecloud.com'
export TERRABASE_BACKUP_S3_BUCKET='terrabase-backups'
export TERRABASE_BACKUP_S3_PATH='members/<member>'
export TERRABASE_BACKUP_S3_REGION='<region>'
export AWS_ACCESS_KEY_ID='...'
export AWS_SECRET_ACCESS_KEY='...'
```

Path-style addressing is enabled automatically for a custom endpoint, which
is what the OCI S3 compatibility API requires.

## Restore and point-in-time recovery

Litestream exposes the Prometheus metrics `litestream_sync_count`,
`litestream_sync_seconds`, `litestream_wal_size`, and
`litestream_sync_error_count` when `TERRABASE_BACKUP_METRICS_ADDR` is set;
watch sync errors there during an incident.

Restore runs from the replica, on a machine with the `litestream` binary,
into a path that does not exist yet:

```sh
litestream restore \
  -o /var/lib/terrabase/restored/tenant-a.sqlite3 \
  -timestamp 2026-08-30T12:00:00Z \
  -integrity-check quick \
  <REPLICA_URL>
```

- For a file replica: `file://<replica-dir>/<path-under-the-watch>/<database-file>`.
  The replica mirrors the watched directory's layout, for example
  `file:///backups/tenants/a/tenant-a.sqlite3`.
- For an S3 replica:
  `s3://<bucket>/<path>/<path-under-the-watch>/<database-file>?endpoint=<endpoint>&region=<region>&forcePathStyle=true`.
- Omit `-timestamp` for the latest recovery point; add `-force` only to
  overwrite an existing target. Omit `-integrity-check quick` only when
  speed matters more than immediate verification.

`restore` cannot resolve a database through the supervised `dir:`-watch
config entry: always address the replica URL directly, one database at a
time. There is no bulk restore. Follow mode (`restore -f` with
`-follow-interval`) keeps a local copy continuously refreshed from the
replica, which is useful while investigating a corrupted primary.
