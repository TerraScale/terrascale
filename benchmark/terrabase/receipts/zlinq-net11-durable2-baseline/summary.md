# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `d5029bead004ad3de68004fc5d939be604359796acaacb7d86260ca118f0bb80`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|StorageEngine | Scan | Warm | complete | 5 | — | 1750.29 | 437.32 | 1120.26 | 1708.72 | 620821.84 | 14.25 | 78,041,088 | 0 | — | `raw-samples-StorageEngine-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-baseline-wt/bench/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks --storage storage-engine --workload scan --temperature warm --dataset-size 1000 --payload-size 256 --samples 5 --operations 1000 --warmup 200 --output bench/receipts/zlinq-net11-durable2-baseline`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
