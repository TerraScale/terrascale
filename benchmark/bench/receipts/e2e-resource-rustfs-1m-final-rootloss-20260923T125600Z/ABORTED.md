# Aborted RustFS 1M end to end diagnostic

| Field | Observed result |
| --- | --- |
| Status | **Aborted during full backup; not a qualification pass** |
| Workload | 1,000,000 documents; 10,000 relationships; S3 object tier; root loss recovery enabled |
| RustFS endpoint | `http://127.0.0.1:19000` (local RustFS S3 provider) |
| RustFS bucket | `terrabase-e2e-6548af16603247bab7e6758d4e2cc33c` |
| RustFS data root | `/home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data` |
| Cgroup | `/sys/fs/cgroup/user.slice/user-1000.slice/user@1000.service/app.slice/run-p1529273-i9982516.service` |
| Cgroup limit | `memory.max=4,294,967,296`; `memory.swap.max=0` |
| Cgroup memory peak | `4,047,474,688` bytes |
| Process RSS peak | `3,400,101,888` bytes |
| Cgroup events | `oom=0`, `oom_kill=0`, `max=0` |
| Peak measured local storage | `21,788,221,440` bytes |
| Peak RustFS data root usage | `20,751,941,632` bytes |
| SSD cache | `134,217,728` bytes during full backup |
| RustFS objects at last sample | 5 objects; `482,237,644` bytes |
| Last evidence sample | `2026-09-23T13:33:18.9880836Z`, phase `full-backup` |
| Systemd wall and CPU | 48 min 20.717 s wall; 1 h 1 min 1.016 s CPU |

## Exact executed payload

The systemd journal records this exact payload (credentials were loaded from the
local environment file; secret values are intentionally not copied into this
receipt):

```text
set -a; . /run/user/1000/terrabase-rustfs-credentials.sh; set +a; exec /run/current-system/sw/bin/nice -n 19 dotnet /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload e2e-volume --object-tier s3 --documents 1000000 --batch-size 8192 --point-reads 10000 --updates 1000 --deletes 1000 --queries 1000 --relationships 10000 --checkpoint-records 500000 --resource-sample-ms 10000 --cache-bytes 16777216 --max-local-storage-bytes 25769803776 --min-free-space-bytes 1073741824 --rustfs-data-path /home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data --require-isolated-cgroup --root-loss-recovery --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-final-rootloss-20260923T125600Z
```

The authentication boundary was real: `RustFsWorkloadStore.OpenAsync` read
`TERRABASE_S3_ENDPOINT`, `TERRABASE_S3_ACCESS_KEY`,
`TERRABASE_S3_SECRET_KEY`, and `TERRABASE_S3_REGION`, then used the AWS SDK
`AmazonS3Client` with `BasicAWSCredentials`, path style, and the local RustFS
HTTP endpoint. The benchmark used `S3NativeBackupObjectStore` and
`TieredStorageSegmentStore`; no S3 or storage boundary was mocked.

## Call path

```text
TerraBase.Benchmarks.dll
  -> E2eVolumeWorkload.RunAsync / RunCoreAsync
  -> DurableStorageEngine + TieredStorageSegmentStore
  -> S3NativeBackupObjectStore / AmazonS3Client
  -> HTTP S3 endpoint 127.0.0.1:19000
  -> RustFS bucket terrabase-e2e-6548af16603247bab7e6758d4e2cc33c
```

The source engine wrote its owned source root and SSD cache locally, compacted
and published immutable segment objects through RustFS, and later opened the
same source through the tier store. Backup capture also used the same authenticated
RustFS object store.

## Phases observed before the stop

| Phase | Evidence |
| --- | --- |
| Bulk write | Reached `1,000,000 / 1,000,000`; last progress checkpoint `2026-09-23T12:46:30.128035Z` |
| Relationship write | Phase completed and the engine advanced to exact scan; 10,000 relationships were configured |
| Compaction/publication | Exact scan opened with 4 RustFS objects totaling `482,237,361` bytes |
| Exact scan | Reached `1,000,000 / 1,000,000` |
| Snapshot document paging/listing | Reached `1,000,000 / 1,000,000` |
| Point reads | Reached `10,000 / 10,000` |
| Relationship verification | Phase completed and the engine advanced to full backup |
| Full backup | Began at `2026-09-23T12:58:23Z`; remained at `completed=0`, with no completion receipt before stop |

The process was stopped after approximately 35 minutes without full-backup
completion. The latest resource sample is at `13:33:18.9880836Z`; the systemd
journal then records the transient service ending at `10:33:22` local time with
`3.7G memory peak`. There is no timeout, OOM, exception, or signal record in
the journal. The service is now inactive and reports `Result=success`, but the
transient unit no longer retains exit-code fields. Therefore the evidence
supports a controlled external stop after the backup stall, while the exact
signal or initiating command cannot be recovered from the retained logs.

No `e2e-volume-summary.md`, `e2e-volume-receipt.json`, or normal
`run-receipt.json` was emitted. The raw progress and resource streams remain the
source of truth for this interrupted run.

## Source and binary identity

The subject checkout was commit
`67e35f443565c54eb731d6ea2dd989cb5c87b7be` with HEAD tree
`69203400f13c7e311cf5fb569a081eeab36e8cbc`; the working tree was dirty during
this benchmark lane. The frozen Release output is
[`bench/binaries/e2e-rustfs-1m-final-rootloss-20260923T125600Z`](../../binaries/e2e-rustfs-1m-final-rootloss-20260923T125600Z)
with hashes in [`binary-sha256.txt`](../../binaries/e2e-rustfs-1m-final-rootloss-20260923T125600Z/binary-sha256.txt):

- `TerraBase.Benchmarks.dll`: `cf312419552d723ad250083614fc2712e7691b2bd6549d4b03c4cac867de2e15`
- `TerraBase.dll`: `46feeb80b40b79f5957cb7c720cb5083e3d3b2324f5b75a3894554129127043d`
- `TerraBase.ZoneTree.dll`: `eeaf27115aa0cd3ff424427dd6d78ed2e841131dbaa7b95a54adf20f464822d0`

Evidence files:

- [`progress.jsonl`](progress.jsonl)
- [`resource-samples.jsonl`](resource-samples.jsonl)
- [`rustfs-bucket.txt`](rustfs-bucket.txt)

This diagnostic does not qualify updates, deletes, indexed queries, backup
completion, restore, total SSD-loss recovery, or 100 million records. The
strict 100K RustFS receipt remains the completed all-functions rung; a new
remote-journal/acknowledgement design and a successful larger backup rung are
required before a direct 100M run is admitted.
