# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `9a4fd71e58057954c8b0840d2688c61bb89eb043a13fd5078dd3494e53962e3b`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|StorageEngine | PointWrite | Warm | complete | 5 | — | 365.28 | 2052.22 | 6044.68 | 9041.93 | 79466.24 | 0.94 | 79,626,240 | 134,314 | 9.46 | `raw-samples-StorageEngine-PointWrite-Warm.jsonl` |
|StorageEngine | PointWrite | Cold | complete | 5 | 131.42 | 253.20 | 2824.70 | 8775.58 | 11658.83 | 77780.24 | 1.52 | 86,941,696 | 134,303 | 9.46 | `raw-samples-StorageEngine-PointWrite-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload point-write --temperature both --dataset-size 100 --payload-size 128 --samples 5 --warmup 20 --operations 100 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-v3-storage-engine-point-write-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
