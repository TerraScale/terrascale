# Backup qualification goal and evidence

Status: active, not production-qualified. Passing object-transfer tests do not prove continuous SQLite WAL capture or the native TerraBase recovery path.

## Current transport and SQLite recovery test

`backup::object_store::tests::minio_round_trip_100k_wal_records` now:

1. Creates a SQLite database in WAL mode and checkpoints the empty schema.
2. Saves that base database and disables automatic checkpointing.
3. Commits 100,000 deterministic records, producing a 29,511,592-byte WAL.
4. Uploads the base and WAL through `ObjectDestination::upload_point`, exercising multiple 8 MiB multipart parts.
5. Closes the source connection and deletes the source database and all local upload artifacts.
6. Downloads from S3, installs the base and WAL under SQLite filenames, and opens a fresh connection.
7. Requires `PRAGMA integrity_check = ok`, exactly 100,000 rows, matching IDs and all values, and no extra rows.
8. Corrupts, truncates, and deletes the remote WAL in turn. Each restore must fail and leave no target directory.
9. Deletes the remote test point. Each invocation uses an isolated object prefix.

This is a **transport/recovery fixture**, not an encrypted native backup. Its placeholder manifest and copied files do not qualify native capture, continuous streaming during writes, PITR, or encryption. Earlier 2 MB byte-comparison results did not prove database recovery or multiple upload parts; they are superseded by this test.

## Evidence

- MinIO: recovery and all three remote fault checks passed. 29,511,592-byte WAL, upload 506.7 ms, download 417.3 ms. These are single local observations, not performance thresholds or full recovery time.
- Earlier backup module run: 74 passed, one explicit emulator test ignored. Individual tests cover native snapshot/incremental restore, retention, PITR, provider loss and restart behavior in their respective fixtures; this is not evidence of full cloud qualification.
- S3Mock: stronger recovery/fault test pending current run; the earlier byte-only test passed.
- LocalStack latest (2026.8.1) exited 55 requiring `LOCALSTACK_AUTH_TOKEN`. This applies to that image, not all LocalStack versions.
- Historical workspace run had 34 failures. Their causes were not established; labeling them unrelated or pre-existing was unsupported. Full workspace success remains outstanding.

Commands:

```sh
TERRABASE_MINIO_ENDPOINT=http://127.0.0.1:19000 nice -n 10 cargo test -p terrabase --lib backup::object_store::tests::minio_round_trip_100k_wal_records -- --ignored --nocapture
scripts/backup-s3-roundtrip.sh
```

The S3Mock script pins the tested image digest, binds loopback, requires readiness and successful bucket creation, and removes only the container it created.

## Required work and proposed changes

- Trace production native capture from SQLite writes/checkpoints through sealed segments, scheduling, upload acknowledgment and restore. Compare with a pinned Litestream revision. Multipart upload alone is not Litestream-style continuous replication.
- Add an engine/fleet test with 100,000+ records written while backup is running. Prove remote progress before the writer ends and recover all records after deleting source/cache.
- Test interrupted multipart uploads and cleanup, source mutation during transfer, crash/restart, disk exhaustion, bounded staging, and cancellation. Current error paths require an explicit multipart-abort audit.
- Qualify authenticated native chains and PITR on emulators, including tampered manifests, bad keys, missing predecessors, timestamp regressions, policy changes and independent-provider recovery.
- Exercise TLS, authentication rejection and credential rotation; HTTP fixtures bypass provider admission and do not qualify OCI or Tigris.
- Establish measured CPU/RSS/I/O and p50/p95/p99 commit latency baselines with backup off/on; prove bounded resources and the configured RPO. No zero-overhead claim is justified.
- Run LocalStack where an available version permits it and retain exact image/version evidence. Cloud-only semantics remain unverified without provider access.
- Fix relevant workspace failures, run required checks, and retain logs with exact source revision and dirty diff identity.

The goal remains active until each requested requirement has direct, correctly scoped evidence.
- Latest MinIO run also measured SQLite writes: 100,000-row transaction 1.115 s; 29,511,592-byte WAL upload 498 ms; restore 262 ms. This is a baseline only; backup-on versus backup-off comparison and p95/p99 thresholds remain outstanding.

## Proposed acceptance thresholds

These thresholds are intentionally proposed pending production workload calibration: p99 commit latency with backup enabled must be no more than 10% above the same workload with backup disabled; backup RSS must remain below 256 MiB per database stream; capture-to-object lag must remain below 10 seconds for 99% of intervals; a successful restore must pass SQLite integrity check and exact row/digest verification; and any failed or interrupted transfer must publish no incomplete recovery point. Thresholds must be measured over repeated runs, not inferred from the single-run timings above.
- Hardened multipart upload error handling: any source read, digest, part upload, or completion failure now explicitly aborts the multipart session; object-store regression tests pass after the change.
