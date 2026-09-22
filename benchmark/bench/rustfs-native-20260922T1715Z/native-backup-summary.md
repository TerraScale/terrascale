# TerraBase native backup qualification

Status: **complete**

## Measurement

- Source commit: `57d12ed6ae6db472a26318a9b2671b3aa08e26b0`; dirty `True`
- Benchmark artifact SHA-256: `9b0cbb1c7ac623977964d1468465150bc28c42c864a5177b920706e12d7668e0`
- TerraBase artifact SHA-256: `6825d0074b40ac7e308fe3da814987fe1776b32a0d3cb21cc18f2e60bd23cd98`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20,937,965,568 bytes
- GC: server `True`; latency mode `Interactive`
- Full boundary: `2`; incremental boundary: `6`
- PITR target: `2026-09-22T17:01:08.4820585+00:00`; restored sequence `3`; applied log entries `1`
- Checksums: `True` across `2` artifacts plus committed manifests

| Samples | Restore p50 ms | Clean reopen p50 ms | End-to-end p50 ms | Restore p95 ms | End-to-end p95 ms | Activated |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 3 | 268.081 | 86.650 | 360.173 | 477.778 | 565.022 | yes |

Raw samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/rustfs-native-20260922T1715Z/raw-samples-native-restore.jsonl`

## Limits

- Object store: `s3://terrabase-native-20260922 (http://127.0.0.1:19000/)`. This qualification measures the selected provider only; it does not qualify multi-region recovery or production network durability.
- The PITR cut replays the first incremental publication and verifies a new StorageEngine instance after activation. It does not simulate power loss during upload, manifest commit, generation materialization, or pointer replacement.
- Clean reopen is a new StorageEngine instance in the benchmark process. It is evidence of durable files and activation metadata, not a host reboot or process-crash drill.
- The observed local end-to-end time is a qualification fixture result; no production RTO/SLO is inferred from this receipt.

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload native-backup --store s3 --s3-endpoint http://127.0.0.1:19000 --s3-bucket terrabase-native-20260922 --s3-access-key [redacted] --s3-secret-key [redacted] --samples 3 --operations 256 --log-entries 4 --output ../bench/rustfs-native-20260922T1715Z`
