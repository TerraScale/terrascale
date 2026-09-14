# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `8213a13f6759c92b8dc19a19d3a8c9e291880c5a5110cbd27cea7e7eceba60fd`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|ZoneTree | PointWrite | Warm | complete | 5 | — | 98.36 | 9035.23 | 17839.56 | 22987.14 | 14360.48 | 1.42 | 83,927,040 | — | — | `raw-samples-ZoneTree-PointWrite-Warm.jsonl` |
|ZoneTree | PointWrite | Cold | complete | 5 | 97.47 | 79.48 | 10069.67 | 22758.31 | 45899.08 | 14143.84 | 0.29 | 90,025,984 | — | — | `raw-samples-ZoneTree-PointWrite-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage zonetree --workload point-write --temperature both --dataset-size 100 --payload-size 128 --samples 5 --warmup 20 --operations 100 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-zonetree-point-write-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
