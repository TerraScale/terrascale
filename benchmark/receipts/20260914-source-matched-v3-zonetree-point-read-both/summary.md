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
|ZoneTree | PointRead | Warm | complete | 5 | — | 42394.44 | 16.43 | 27.56 | 62.30 | 9101.06 | 2.04 | 89,300,992 | — | — | `raw-samples-ZoneTree-PointRead-Warm.jsonl` |
|ZoneTree | PointRead | Cold | complete | 5 | 272.83 | 38511.16 | 19.16 | 29.70 | 43.63 | 9101.73 | 3.92 | 95,080,448 | — | — | `raw-samples-ZoneTree-PointRead-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage zonetree --workload point-read --temperature both --dataset-size 1000 --payload-size 256 --samples 5 --warmup 100 --operations 1000 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-v3-zonetree-point-read-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
