# Restore-only live backup drill

Publish from the monorepo root (the runner references this checkout's TerraBase source):

```sh
nice -n 19 dotnet restore benchmark/dotnet/TerraBase.LiveRestoreDrill/TerraBase.LiveRestoreDrill.csproj --locked-mode
nice -n 19 dotnet publish benchmark/dotnet/TerraBase.LiveRestoreDrill/TerraBase.LiveRestoreDrill.csproj -c Release -r linux-arm64 --self-contained true --no-restore -o /tmp/terrabase-live-restore-drill-linux-arm64
```

Restore uses both repository RIDs (`linux-x64;linux-arm64`) from `Directory.Build.props`, matching the lockfiles; do not pass a single `-r` to restore or force-evaluate shared locks. Publish then selects ARM64 without another restore. This produces `TerraBase.LiveRestoreDrill`, a self-contained single-file Linux ARM64 executable. Publishing does not execute the drill. No workflow, serving data directory, listener, backup uploader, catalog status or `LastDrill` is changed by this tool.

## Authorized execution

1. Re-read the live gru deployment's **nonsecret** bucket metadata immediately before execution. Deployment can replace the bucket (including b2 → b3); do not reuse a bucket or snapshot from an earlier research session. Choose one current `v1/<organization>/<project>/<database>` user-data prefix. System databases are refused.
2. Deliver only the existing three credential references with `ts-orch vault deliver --cell gru --service terrabase --refs /path/to/exact3-refs.json --dir /run/terrabase-live-drill-refs --once`. Do this only within the parent's authorization. The JSON map must contain exactly `AWS_ACCESS_KEY_ID_FILE`, `AWS_SECRET_ACCESS_KEY_FILE` and `TERRABASE_MASTER_KEY_FILE`, with each `{vault, name}` copied from the current deployment's references. Never create credentials, expand IAM, print values, use OpenBao, or read unrelated secret references. Existing ts-orch delivery lowercases the names and writes mode 0600 files to a private memory-backed directory. The runner reads these files directly; it does not export values to environment variables.
3. Execute the published runner with only nonsecret arguments. Replace the bucket and IDs below using the fresh metadata:

```sh
/tmp/terrabase-live-restore-drill-linux-arm64/TerraBase.LiveRestoreDrill \
  --bucket=CURRENT_BUCKET \
  --endpoint=https://fly.storage.tigris.dev \
  --refs-directory=/run/terrabase-live-drill-refs \
  --file-prefix=v1/ORGANIZATION_ULID/PROJECT_ULID/DATABASE_ULID \
  --snapshot-key=latest
```

`latest` chooses the newest snapshot **for that file**, using its timestamp encoded in `SnapshotKey`, not object modification time. To pin a reviewed selection, supply its complete snapshot object key instead; the runner still lists current snapshots and refuses it if it is no longer the newest observed snapshot. The chosen key, generation, scope, target position and source snapshot appear in the proof. Concurrent backup/retention continues normally; a missing object or incomplete replay fails the drill rather than skipping checks. The listing is an observation, not an atomic view of a moving bucket.

## What the JSON proves

The only application output is one allowlisted JSON object; exit status is 0 only when the same-cut comparison and cleanup succeed. Exceptions, decrypted rows and credential contents are never printed. Failure `stage` identifies the operation without exposing SDK errors or signed requests.

The runner independently downloads and authenticates/decrypts the latest snapshot through `BackupObject.ReadAsync` and `DataKeys.GetAsync`, then computes its logical engine SHA-256 digest. Separately, `BackupReader.BuildAsync` restores a prior snapshot from the **same generation**, when available, replaying WAL to the latest snapshot's exact `Position`. Success requires the reached position and logical checksum to match the independent latest snapshot reference. Otherwise it restores the latest snapshot itself at that same position: `wal_exercised=false` and `wal_bytes_replayed=0` explicitly identify snapshot-only coverage. There is no silent snapshot-only fallback when a selected prior snapshot's WAL is missing or invalid.

Authentication includes wrapped data-key authentication, AES-GCM object/chunk authentication, object-key binding and truncation detection. The restored copy passes the engine's `PRAGMA integrity_check`; `database_integrity_check` reports that result. Digests exclude engine-local capture state and normalize the restored file incarnation. This runner additionally compares `_tb_peers` as `(node, applied)` for rows with nonzero applied progress: `acked`, `stable`, `seen`, `incarnation` and bookkeeping-only rows can change while capture is suspended. Schema, captured peer progress, document/lattice/Merkle state and logical commit offsets remain verified. The production drill's checksum projection is unchanged. Uncaptured retirement of an older peer epoch with nonzero applied progress still fails the comparison; this tool does not hide that difference or claim it was corruption.

`snapshot_rpo_ms` is the observed latest backup **snapshot age**, `observed_at - snapshot_at`. It is not measured write RPO. `reference_download_ms` measures the independent download/decryption; `download_restore_ms` measures `BuildAsync`, including download, replay and integrity checking, and excludes reference download and digest comparison. `backup_ms` is always `null`: the source upload duration is unknown. No full-cell DR or latest-PITR claim is made (`full_cell_dr_proven=false`, `latest_pitr_proven=false`). Credentials, routing, application readiness, regional failover and recovery of other database files remain outside this proof.

## Isolation and cleanup

Only `IObjectStore` reads and listings are exposed; uploads and deletes always throw, and a self-check exercises both rejections before any bucket access. A second HTTP guard allows only GET requests to the pinned HTTPS Tigris host and selected bucket; redirects and environment proxies are disabled. This is application-level read-only enforcement, not a claim about the credential's IAM permissions.

The engine is newly constructed in an atomically created mode 0700 `/tmp/terrabase-live-restore-*` directory, independent of production settings and `TMPDIR`. No server or `ContinuousBackup` instance starts. The runner retains the concrete engine and awaits `CleanupCompletion` even when its 10-second disposal grace expires, before recursively removing exactly its own directory on success, failure, cancellation and SIGTERM. Cleanup errors fail the proof and retain scratch. The operation has a 30-minute cancellation deadline; resource cleanup is awaited without that deadline. Checksum scans check cancellation between rows/tables and before returning; a native SQLite call already running must return before cancellation is observed. Cancellation is checked after the final checksum and after cleanup, so cancellation observed at either point cannot report success. `disposable_cleanup=true` is emitted only after cleanup succeeds; failure exits nonzero. SIGKILL, power loss and a filesystem refusing deletion cannot guarantee cleanup: run under the approved disposable job sandbox and have its owner remove any retained scratch after inspection. Delivery files belong to ts-orch's scoped handoff and are never deleted by the runner.
