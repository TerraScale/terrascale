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
|StorageEngine | Scan | Warm | complete | 5 | — | 1176.11 | 648.41 | 1613.95 | 3899.81 | 576497.66 | 2.46 | 81,526,784 | 0 | — | `raw-samples-StorageEngine-Scan-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload scan --temperature warm --dataset-size 1000 --payload-size 256 --samples 5 --operations 1000 --warmup 200 --output bench/receipts/zlinq-durable-zlinq`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
