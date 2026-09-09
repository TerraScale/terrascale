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
