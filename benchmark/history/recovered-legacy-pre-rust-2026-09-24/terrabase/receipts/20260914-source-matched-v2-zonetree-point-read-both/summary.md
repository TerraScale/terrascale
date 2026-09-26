# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `a1da590345bf995fa3766a45b5f8994ea12b8d681bcf97ba84ed548f9fc48a54`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|ZoneTree | PointRead | Warm | complete | 5 | — | 40939.81 | 14.49 | 29.83 | 54.52 | 9101.46 | 2.24 | 89,198,592 | — | — | `raw-samples-ZoneTree-PointRead-Warm.jsonl` |
|ZoneTree | PointRead | Cold | complete | 5 | 119.73 | 44818.93 | 13.05 | 24.04 | 42.24 | 9101.42 | 2.24 | 99,180,544 | — | — | `raw-samples-ZoneTree-PointRead-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage zonetree --workload point-read --temperature both --dataset-size 1000 --payload-size 256 --samples 5 --warmup 100 --operations 1000 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-v2-zonetree-point-read-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
