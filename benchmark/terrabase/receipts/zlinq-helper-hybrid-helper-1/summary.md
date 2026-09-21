# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `53554e6e31f8276d001d222a9786988ea27d44d519d166c790276274dc9be823`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|Memory | Scan | Warm | complete | 5 | — | 2072.06 | 332.37 | 990.97 | 1829.78 | 576311.27 | 12.76 | 74,280,960 | — | — | `raw-samples-Memory-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage memory --workload scan --temperature warm --dataset-size 1000 --payload-size 256 --samples 5 --operations 3000 --warmup 1000 --output bench/receipts/zlinq-helper-hybrid-helper-1`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
