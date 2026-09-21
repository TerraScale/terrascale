# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `e3db6b5ddd8cc11754edefd3a3fc98f8f4f6bc92d2b06943bd2c9fa047479eb2`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|Memory | PointRead | Warm | complete | 10 | — | 398905.40 | 2.04 | 3.62 | 4.26 | 1148.57 | 13.12 | 65,540,096 | — | — | `raw-samples-Memory-PointRead-Warm.jsonl` |
|Memory | PointWrite | Warm | complete | 10 | — | 227765.53 | 3.61 | 5.60 | 7.66 | 1133.13 | 12.82 | 67,837,952 | — | — | `raw-samples-Memory-PointWrite-Warm.jsonl` |
|Memory | Scan | Warm | complete | 10 | — | 2826.09 | 326.25 | 571.58 | 657.31 | 576309.10 | 12.50 | 78,204,928 | — | — | `raw-samples-Memory-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage memory --workload all --temperature warm --dataset-size 1000 --payload-size 256 --samples 10 --operations 5000 --warmup 2000 --output bench/receipts/zlinq-final-memory-zlinq-1`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
