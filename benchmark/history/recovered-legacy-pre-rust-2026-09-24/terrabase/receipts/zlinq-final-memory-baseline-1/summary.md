# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `99b4f646b728562f26ee6bdbe0049e5f3026c70c80d495d948836da08f38a648`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|Memory | PointRead | Warm | complete | 10 | — | 231348.67 | 3.42 | 6.08 | 7.11 | 1148.23 | 12.91 | 65,708,032 | — | — | `raw-samples-Memory-PointRead-Warm.jsonl` |
|Memory | PointWrite | Warm | complete | 10 | — | 138156.28 | 5.87 | 8.90 | 20.32 | 1133.09 | 12.69 | 68,075,520 | — | — | `raw-samples-Memory-PointWrite-Warm.jsonl` |
|Memory | Scan | Warm | complete | 10 | — | 2374.38 | 386.19 | 660.46 | 797.94 | 620821.35 | 12.42 | 78,340,096 | — | — | `raw-samples-Memory-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-baseline-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage memory --workload all --temperature warm --dataset-size 1000 --payload-size 256 --samples 10 --operations 5000 --warmup 2000 --output bench/receipts/zlinq-final-memory-baseline-1`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
