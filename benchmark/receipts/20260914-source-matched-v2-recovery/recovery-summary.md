# TerraBase recovery and capacity benchmark

Status: **complete**

## Source and environment

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `a1da590345bf995fa3766a45b5f8994ea12b8d681bcf97ba84ed548f9fc48a54`
- TerraBase assembly SHA-256: `a6a8df94f16e59b0e96ad9ca5cfdc37a4cb71b92c7a6e7cea4b4ea24534ee51c` (/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.dll)
- ZoneTree assembly SHA-256: `4790dfa4b9385d3f32bfbfd4716e6ad9a1c06880eef84576f08270d33c48b0f9` (/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.ZoneTree.dll)
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20,937,965,568 bytes
- Cgroup memory limit: `—`; CPU quota: `unavailable`
- GC: server `False`, latency mode `Interactive`

## Replication lag

Boundary: `source apply -> source delta export -> destination apply; in-memory process-local boundary`. Percentiles are medians of per-sample operation percentiles; the lag timer covers one in-memory source-to-destination cycle.

| Subject | Status | Samples | Throughput ops/s | p50 lag µs | p95 lag µs | p99 lag µs | Allocated bytes/op | CPU % | RSS after bytes | Max backlog ops | Raw samples |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|LeaderlessReplicationEngine | complete | 5 | 8881.31 | 71.70 | 121.20 | 258.40 | 6452.40 | 2.25 | 68,800,512 | 0 | `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/20260914-source-matched-v2-recovery/raw-samples-replication-lag.jsonl` |

## Restore time

Boundary: `manifest chain load + checksum verification + in-memory transactional target activation`. The source and object store are in-memory; no filesystem reopen, object-store network, or ZoneTree restore adapter is measured.

| Subject | Status | Samples | Throughput restores/s | p50 restore ms | p95 restore ms | p99 restore ms | Allocated bytes/op | CPU % | RSS after bytes | Log entries | Raw samples |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|BackupManager | complete | 5 | 284.47 | 2.77 | 5.94 | 7.58 | 517052.16 | 2.85 | 81,354,752 | 100 | `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/20260914-source-matched-v2-recovery/raw-samples-restore.jsonl` |

## Fan-out boundary

Status: **unsupported**. The existing fan-out CLI has no durable commit-to-reactive source callback; run --workload subscriptions to retain the explicit unsupported receipt.
Use the existing `--workload subscriptions` command for retained claim-free receipts. It reports p50/p95/p99 delivery latency, allocations, CPU, RSS, subscriber counts, egress, and backpressure only when a real durable commit callback is supplied.

## Limits and next measurements

- Replication lag is process-local `LeaderlessReplicationEngine` apply/export/apply latency. It does not qualify MagicOnion, network RTT, durable acknowledgement, repair, or multi-region lag.
- Restore timing is `BackupManager` over `InMemoryBackupObjectStore` and a transactional in-memory target. It does not qualify StorageEngine or ZoneTree filesystem restore, object-store bandwidth, crash recovery, or RPO/RTO.
- Compare horizontal fan-out with the subscriber-count matrix and vertical throughput with `--concurrency` on the provider runner. Record the exact cgroup limits; host RAM alone is not a resource floor.

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload recovery --samples 5 --operations 100 --log-entries 100 --payload-size 256 --seed 1337 --output bench/receipts/20260914-source-matched-v2-recovery`
