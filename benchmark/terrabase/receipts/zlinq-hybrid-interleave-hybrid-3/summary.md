# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `8df9b534c1761802deb8dbf9d071d14c6effbf4f02f36bef5cbd61be5e271e46`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|Memory | Scan | Warm | complete | 5 | — | 1907.98 | 432.61 | 1048.28 | 1252.09 | 576314.00 | 2.23 | 74,436,608 | — | — | `raw-samples-Memory-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage memory --workload scan --temperature warm --dataset-size 1000 --payload-size 256 --samples 5 --operations 3000 --warmup 1000 --output bench/receipts/zlinq-hybrid-interleave-hybrid-3`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
