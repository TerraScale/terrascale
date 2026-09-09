# Backup qualification status

## Verified

- `cargo test -p terrabase --lib backup --no-fail-fast`: 74 passed.
- MinIO runs under Podman at `http://127.0.0.1:19000`; bucket `terrabase` was created with MinIO credentials.
- `minio_round_trip_100k_wal_records` uploads and restores 100,000 records and verifies every restored line.
- Artifact uploads now use S3 multipart upload with fixed 8 MiB chunks and incremental BLAKE3 hashing; full artifacts are not buffered in memory.
- Existing backup tests cover encrypted snapshots, incremental chains, PITR, retries, provider loss/fallback, retention, corruption, torn publication, and restart recovery.

## Remaining qualification work

The 100k test currently exercises the object-store streaming boundary with a WAL-shaped artifact. It does not yet drive 100,000 committed rows through a live SQLite engine while a concurrent writer measures commit latency. That test is required before claiming Litestream-equivalent continuous capture and write-path performance.

The full workspace suite compiles but currently reports unrelated engine/storage regressions. Those failures must be triaged separately; they cannot be counted as backup qualification evidence.

## Required next changes

1. Add a live SQLite WAL integration harness that writes 100,000 rows, tails WAL growth while writes continue, and restores into a fresh engine.
2. Add bounded queue/backpressure metrics and assertions for capture lag, dropped segments, and writer latency.
3. Run the harness against MinIO and LocalStack with injected interruption, truncation, corruption, and restart faults.
4. Record CPU, RSS, throughput, p95/p99 commit latency, upload lag, and restore duration with explicit thresholds.
5. Keep the goal active until each requirement has direct test evidence.

## Latest evidence

- `cargo test -p terrabase --lib backup --no-fail-fast`: 74 passed, 0 failed, 1 ignored.
- `TERRABASE_MINIO_ENDPOINT=http://127.0.0.1:19000 cargo test -p terrabase --lib backup::object_store::tests::minio_round_trip_100k_wal_records -- --ignored`: 1 passed, 0 failed.
- `cargo fmt --all -- --check`: passed.

- The MinIO 100k test now creates a real SQLite database in WAL mode, commits 100,000 rows, streams the resulting WAL bytes through multipart S3 upload, removes local artifacts, restores from MinIO, and verifies the restored WAL bytes exactly.

- Latest live SQLite/MinIO measurement: 2,018,832-byte WAL; multipart upload 163 ms; restore 38 ms (single local run, not a production SLO).
- Fixed zero-length artifact uploads to use direct PUT instead of empty multipart sessions; object-store tests and live MinIO SQLite WAL test remain green afterward.
- After zero-length handling and multipart changes: `cargo test -p terrabase --lib backup --no-fail-fast` passed 74/74 (one emulator test ignored by default).
