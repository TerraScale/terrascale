# TerraBase native filesystem backup qualification

Status: **complete**

## Measurement

- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: 46 logical processors; available memory 20,937,965,568 bytes
- GC: server `False`; latency mode `Interactive`
- Full boundary: `1`; incremental boundary: `9`
- PITR target: `2026-09-14T17:34:16.3846941+00:00`; restored sequence `2`; applied log entries `1`
- Checksums: `True` across `2` artifacts plus committed manifests

| Samples | Restore p50 ms | Clean reopen p50 ms | End-to-end p50 ms | Restore p95 ms | End-to-end p95 ms | Activated |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
|5 | 68.820 | 6.762 | 76.270 | 180.010 | 189.350 | yes |

Raw samples: `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/native-backup-20260914/raw-samples-native-restore.jsonl`

## Limits

- The source engine, object store, and restore target are local filesystem directories on one host; this does not qualify S3/OCI/Tigris latency, remote durability, network transfer, or multi-region recovery.
- The PITR cut replays the first incremental publication and verifies a new StorageEngine instance after activation. It does not simulate power loss during upload, manifest commit, generation materialization, or pointer replacement.
- Clean reopen is a new StorageEngine instance in the benchmark process. It is evidence of durable files and activation metadata, not a host reboot or process-crash drill.
- The observed local end-to-end time is a qualification fixture result; no production RTO/SLO is inferred from this receipt.

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload native-restore --samples 5 --operations 128 --log-entries 8 --seed 1337 --output bench/receipts/native-backup-20260914`
