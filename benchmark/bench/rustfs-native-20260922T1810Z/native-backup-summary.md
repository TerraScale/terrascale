# TerraBase native backup qualification

Status: **complete**

## Measurement

- Source commit: `57d12ed6ae6db472a26318a9b2671b3aa08e26b0`; dirty `True`
- Benchmark artifact SHA-256: `4c1694f9b1f064692377dc1d4422e83613d86c73d3934a8a46bbcfce5d2fa8cf`
- TerraBase artifact SHA-256: `96db3fe3dd6b9692b69f297362eebe4e88daa7c6a68939f4b4e44402cdfe9c2a`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20,937,965,568 bytes
- GC: server `True`; latency mode `Interactive`
- Full boundary: `2`; incremental boundary: `6`
- PITR target: `2026-09-22T17:24:50.3135756+00:00`; restored sequence `3`; applied log entries `1`
- Checksums: `True` across `2` artifacts plus committed manifests

| Samples | Restore p50 ms | Clean reopen p50 ms | End-to-end p50 ms | Restore p95 ms | End-to-end p95 ms | Activated |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 3 | 275.954 | 81.184 | 357.142 | 465.645 | 539.056 | yes |

Raw samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/rustfs-native-20260922T1810Z/raw-samples-native-restore.jsonl`

## Limits

- Object store: `s3://terrabase-native-20260922 (http://127.0.0.1:19000/)`. This qualification measures the selected provider only; it does not qualify multi-region recovery or production network durability.
- The PITR cut replays the first incremental publication and verifies a new StorageEngine instance after activation. It does not simulate power loss during upload, manifest commit, generation materialization, or pointer replacement.
- Clean reopen is a new StorageEngine instance in the benchmark process. It is evidence of durable files and activation metadata, not a host reboot or process-crash drill.
- The observed local end-to-end time is a qualification fixture result; no production RTO/SLO is inferred from this receipt.

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload native-backup --store s3 --samples 3 --operations 256 --log-entries 4 --output ../bench/rustfs-native-20260922T1810Z`
