# 1M RustFS functional/resource run failure

Status: **failed during full backup; not a qualification pass**.

This run exercised the real TerraBase storage engine, authenticated AWS S3 SDK
calls, and local RustFS. It is functional/resource evidence only. The
`E2eVolumeWorkload.OpenSource` path does not supply a remote commit journal, so
this run is not evidence that every acknowledged write survives total SSD loss.

## Identity and command

| Field | Value |
| --- | --- |
| Run output | `e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z` |
| RustFS bucket | `terrabase-e2e-19fb8448f7d14449b9c490d835e17ef2` |
| Endpoint | `http://127.0.0.1:19000` |
| RustFS data root | `/home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data` |
| Unit | `terrabase-e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z.service` |
| Cgroup | `/user.slice/user-1000.slice/user@1000.service/app.slice/terrabase-e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z.service` |
| Source commit / tree | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` / `69203400f13c7e311cf5fb569a081eeab36e8cbc` |
| Authentication | Private `/run/user/1000/terrabase-rustfs-credentials.sh`; values omitted |

The launched payload was:

```text
--workload e2e-volume --object-tier s3 --documents 1000000 --batch-size 8192 --point-reads 10000 --updates 1000 --deletes 1000 --queries 1000 --relationships 10000 --checkpoint-records 500000 --resource-sample-ms 10000 --cache-bytes 16777216 --max-local-storage-bytes 25769803776 --min-free-space-bytes 1073741824 --rustfs-data-path /home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data --require-isolated-cgroup --root-loss-recovery --temp-root /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z-tmp --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z
```

The unit was started with `MemoryMax=4294967296`, `MemorySwapMax=0`, and
`OOMPolicy=stop`. The benchmark loaded `TMPDIR`, `TMP`, and `TEMP` from the
empty run-owned temp root before starting the .NET process.

## Completed phases and failure

| Phase | Result |
| --- | --- |
| Bulk write | 1,000,000 / 1,000,000 reached |
| Relationship write | Completed; 10,000 relationships configured |
| Exact scan | 1,000,000 exact records reached |
| Snapshot paging | 1,000,000 records reached |
| Point reads | 10,000 / 10,000 reached |
| Relationship verification | Completed; full backup began |
| Full backup | Failed before completion at 2026-09-23T14:41:45.719Z |
| Updates, deletes, incremental backup, restore, indexed queries | Not reached |

The failure is:

```text
System.ObjectDisposedException: Cannot access a closed Stream.
  at System.IO.MemoryStream.get_Length()
  at TerraBase.Backup.S3NativeBackupObjectStore.PutSmallIfAbsentAsync(...)
      S3NativeObjectStore.cs:line 785
  at TerraBase.Backup.S3NativeBackupObjectStore.PutCoreAsync(...)
      S3NativeObjectStore.cs:line 98
  at TerraBase.Backup.S3NativeBackupObjectStore.TryPutIfAbsentAsync(...)
  at TerraBase.Backup.DurableBackupSourceChainBarrierOwner.TryAcquireAsync(...)
  at TerraBase.Backup.BackupManager.AcquireSourceChainBarrierAsync(...)
  at TerraBase.Backup.BackupManager.CreateFullBackupAsync(...)
```

The corrected external sort was loaded but was not reached: the failure occurred
while acquiring the S3 source-chain barrier, before backup frame indexing and
external sort execution.

## Resource result

| Measure | Result |
| --- | ---: |
| Runtime | 15m43.595s wall; 18m16.730s CPU |
| Cgroup memory limit / peak | 4,294,967,296 / 1,256,841,216 B |
| Process RSS peak | 356,732,928 B |
| Swap limit / current | 0 / 0 B |
| OOM / OOM kill events | 0 / 0 |
| Peak local accounting | 23,011,581,952 B of 25,769,803,776 B cap |
| Minimum free space observed | 367,940,415,488 B |
| Peak temporary spool bytes | 0 B |
| RustFS objects at failure | 4; 482,237,361 B |
| RustFS data-root growth | 480,063,488 B |

## Preserved evidence

- [`run-receipt.json`](run-receipt.json), exit code 1, receipt digest `254616df03b2c5468e1e8423f9192c5512df3f33df8e212babb2b846c788f2ef`
- [`e2e-volume-resource-failure.json`](e2e-volume-resource-failure.json)
- [`progress.jsonl`](progress.jsonl)
- [`resource-samples.jsonl`](resource-samples.jsonl)
- [`resource-envelope.json`](resource-envelope.json)
- [`rustfs-bucket.txt`](rustfs-bucket.txt)
- [`console log`](../e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z.console.log)
- [Frozen Release binaries](../../binaries/e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z)

Frozen hashes:

```text
6373beea4514dda9d33b426a9b040b7087b5e933dc108c8ccd0efe7376a351e2  TerraBase.Benchmarks.dll
57db2b80d0d3824968968eaba09042bdac6cdd34d764cc3b5623a5d297798fdc  TerraBase.dll
eeaf27115aa0cd3ff424427dd6d78ed2e841131dbaa7b95a54adf20f464822d0  TerraBase.ZoneTree.dll
```

Next handoff: fix and rebuild the closed-stream path, then rerun with a new
output and benchmark-generated RustFS bucket. Do not delete this bucket or
reuse this output directory.
