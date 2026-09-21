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
|StorageEngine | Scan | Warm | complete | 5 | — | 1987.42 | 419.99 | 999.85 | 1179.66 | 620842.78 | 16.28 | 79,589,376 | 0 | — | `raw-samples-StorageEngine-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-baseline-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload scan --temperature warm --dataset-size 1000 --payload-size 256 --samples 5 --operations 1000 --warmup 200 --output bench/receipts/zlinq-final-durable-baseline`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
