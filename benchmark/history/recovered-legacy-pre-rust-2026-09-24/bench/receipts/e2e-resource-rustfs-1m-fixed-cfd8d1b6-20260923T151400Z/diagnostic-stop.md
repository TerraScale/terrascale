# 1M RustFS E2E diagnostic stop

Status: **incomplete diagnostic; not a pass**.

The frozen Release `e2e-volume` run was stopped gracefully after the incremental
backup remained in the same phase for 1 h 25 min 44 s without producing a
phase completion, artifact, normal run receipt, or summary. The source root,
RustFS bucket, temporary root, and all samples remain preserved for inspection.

## Call path and environment

`TerraBase.Benchmarks.dll --workload e2e-volume` -> `E2eVolumeWorkload` ->
`DurableStorageEngine` / ZoneTree source -> `StorageEngineBackupSource` ->
`BackupManager.CreateFullBackupAsync` and
`BackupManager.CreateIncrementalBackupAsync` -> AWS SDK S3 object store ->
authenticated local RustFS at `http://127.0.0.1:19000`.

- Object tier: `s3`; bucket: `terrabase-e2e-cd948f134a354ae0bcfdc96677f54b6f`.
- Credentials were loaded privately from `/run/user/1000/terrabase-rustfs-credentials.sh`.
- RustFS data path: `/home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data`.
- Cgroup: `MemoryMax=4294967296`, `MemorySwapMax=0`, `OOMPolicy=stop`.
- Local accounting: cap `68719476736` B, free-space floor `1073741824` B.

## Exact command

```text
nice -n 19 dotnet /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/e2e-resource-rustfs-fixed-cfd8d1b6-20260923T151114Z/TerraBase.Benchmarks.dll --workload e2e-volume --object-tier s3 --documents 1000000 --batch-size 8192 --point-reads 10000 --updates 1000 --deletes 1000 --queries 1000 --relationships 10000 --checkpoint-records 500000 --resource-sample-ms 10000 --cache-bytes 16777216 --max-local-storage-bytes 68719476736 --min-free-space-bytes 1073741824 --rustfs-data-path /home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data --require-isolated-cgroup --root-loss-recovery --temp-root /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-fixed-cfd8d1b6-20260923T151400Z-tmp --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-fixed-cfd8d1b6-20260923T151400Z
```

## Phase outcome

The run reached and recorded:

- 1,000,000 / 1,000,000 bulk writes.
- Relationship write and relationship verification for 10,000 configured relationships.
- Exact scan and snapshot document paging for 1,000,000 documents.
- 10,000 point reads.
- Full backup phase completion, followed by 1,000 updates and 1,000 deletes.
- Incremental backup entered at `2026-09-23T16:03:29Z` and remained active until stop.

The run did not reach incremental completion, root-loss recovery, full-boundary
restore, PITR restore, final restore, indexed queries, compaction verification,
cold RustFS reopen, or a normal receipt. The last phase sample was
`2026-09-23T17:29:11.9976243Z`, with `completed=0,total=0`.

## Stop and resource evidence

Stop command:

```bash
systemctl --user stop terrabase-e2e-resource-rustfs-1m-fixed-cfd8d1b6-20260923T151400Z.service
```

The user journal records the stop at `2026-09-23T14:29:13-03:00` (`17:29:13Z`) and reports:

```text
Consumed 2h 47min 58.926s CPU time over 2h 15min 8.742s wall clock time, 3.6G memory peak.
```

Systemd ended the transient unit with `Result=success` after the requested
stop; the transient unit was collected, so no benchmark process exit code or
`ExecMainStatus` was retained. This is an intentional diagnostic stop, not a
successful workload completion.

Last resource sample (`resource-samples.jsonl`):

| Measure | Value |
| --- | ---: |
| Local storage / source bytes | 23,137,800,192 / 440,913,920 B |
| RustFS data root | 22,695,948,288 B |
| RustFS objects / bytes | 8 / 594,416,083 B |
| Working set / peak RSS sample | 1,565,454,336 / 3,413,217,280 B |
| Cgroup memory current / peak | 2,001,051,648 / 3,927,093,248 B |
| Cgroup memory limit / swap | 4,294,967,296 / 0 B |
| OOM, OOM-kill, max, swap events | 0 / 0 / 0 / 0 |
| Cgroup I/O reads / writes | 786,411,520 / 10,006,176,768 B |

## Frozen binary identity

```text
cfd8d1b69831b93c7b9d934e7a1548edeca6845999e5c356d0f040bdf16346a7  TerraBase.Benchmarks.dll
3d6c5dfa960788f6e8f5774761df438026c4f0657e8931459e0537a895fc9eaf  TerraBase.dll
eeaf27115aa0cd3ff424427dd6d78ed2e841131dbaa7b95a54adf20f464822d0  TerraBase.ZoneTree.dll
```

The first hash line is the frozen benchmark assembly. The exact source and
environment samples are in `resource-samples.jsonl`, `progress.jsonl`,
`rustfs-bucket.txt`, `source/MANIFEST`, `source/CHANGE_FEED_STATE`, and the
preserved systemd journal. Do not reuse this bucket or output root for a rerun.
