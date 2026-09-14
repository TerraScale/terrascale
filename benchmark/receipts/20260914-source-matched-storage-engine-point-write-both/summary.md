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
|StorageEngine | PointWrite | Warm | complete | 5 | — | 581.40 | 1393.32 | 3317.77 | 7345.81 | 79449.84 | 1.06 | 78,864,384 | 134,313 | 9.46 | `raw-samples-StorageEngine-PointWrite-Warm.jsonl` |
|StorageEngine | PointWrite | Cold | complete | 5 | 88.57 | 740.69 | 1187.08 | 2367.00 | 4983.08 | 77756.48 | 1.35 | 83,238,912 | 134,303 | 9.46 | `raw-samples-StorageEngine-PointWrite-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload point-write --temperature both --dataset-size 100 --payload-size 128 --samples 5 --warmup 20 --operations 100 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-storage-engine-point-write-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
