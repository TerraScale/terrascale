# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `ef6f743504666478a6df2d856afa00ddff9e5c5754538e742ce3630c50eaab4a`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|StorageEngine | PointRead | Warm | complete | 5 | — | 129644.51 | 5.20 | 9.15 | 23.04 | 1172.60 | 2.41 | 85,848,064 | 0 | — | `raw-samples-StorageEngine-PointRead-Warm.jsonl` |
|StorageEngine | PointRead | Cold | complete | 5 | 254.12 | 104385.22 | 5.87 | 18.96 | 32.73 | 1167.99 | 2.37 | 102,334,464 | 0 | — | `raw-samples-StorageEngine-PointRead-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload point-read --temperature both --dataset-size 1000 --payload-size 256 --samples 5 --warmup 100 --operations 1000 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-storage-engine-point-read-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
