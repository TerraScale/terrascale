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
|StorageEngine | PointWrite | Warm | complete | 5 | — | 560.18 | 1467.40 | 2336.79 | 4847.15 | 79463.52 | 0.91 | 79,134,720 | 134,313 | 9.46 | `raw-samples-StorageEngine-PointWrite-Warm.jsonl` |
|StorageEngine | PointWrite | Cold | complete | 5 | 83.11 | 667.38 | 1305.00 | 2602.07 | 4378.20 | 77739.28 | 1.17 | 83,050,496 | 134,308 | 9.46 | `raw-samples-StorageEngine-PointWrite-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload point-write --temperature both --dataset-size 100 --payload-size 128 --samples 5 --warmup 20 --operations 100 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-v2-storage-engine-point-write-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
