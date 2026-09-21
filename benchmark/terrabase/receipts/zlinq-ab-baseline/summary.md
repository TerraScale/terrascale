# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `99b4f646b728562f26ee6bdbe0049e5f3026c70c80d495d948836da08f38a648`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|Memory | PointRead | Warm | complete | 10 | — | 361180.34 | 2.10 | 4.04 | 5.50 | 1148.86 | 2.26 | 74,854,400 | — | — | `raw-samples-Memory-PointRead-Warm.jsonl` |
|Memory | PointWrite | Warm | complete | 10 | — | 220635.61 | 3.62 | 5.70 | 9.10 | 1133.10 | 2.22 | 77,082,624 | — | — | `raw-samples-Memory-PointWrite-Warm.jsonl` |
|Memory | Scan | Warm | complete | 10 | — | 2205.18 | 418.51 | 723.16 | 958.18 | 620820.78 | 2.16 | 87,072,768 | — | — | `raw-samples-Memory-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-baseline-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage memory --workload all --temperature warm --dataset-size 1000 --payload-size 256 --samples 10 --operations 5000 --warmup 2000 --output bench/receipts/zlinq-ab-baseline`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
