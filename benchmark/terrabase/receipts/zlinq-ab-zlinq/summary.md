# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `eae8d363ed9620ecf6e60d6132628892cdb061b13f837fde4a60707cccba72ec`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|Memory | PointRead | Warm | complete | 10 | — | 370109.92 | 2.02 | 4.37 | 5.15 | 1148.59 | 2.20 | 65,736,704 | — | — | `raw-samples-Memory-PointRead-Warm.jsonl` |
|Memory | PointWrite | Warm | complete | 10 | — | 209383.74 | 3.56 | 5.80 | 7.14 | 1133.13 | 2.18 | 67,997,696 | — | — | `raw-samples-Memory-PointWrite-Warm.jsonl` |
|Memory | Scan | Warm | complete | 10 | — | 1774.18 | 523.31 | 874.96 | 1059.15 | 576437.98 | 2.17 | 79,638,528 | — | — | `raw-samples-Memory-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage memory --workload all --temperature warm --dataset-size 1000 --payload-size 256 --samples 10 --operations 5000 --warmup 2000 --output bench/receipts/zlinq-ab-zlinq`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
