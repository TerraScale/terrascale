# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `409cd9765a22382a90be63e3f6e417601e36821e` (dirty: `True`)
- Artifact SHA-256: `3574c8760d16d03a8dab9c488bd129d1ec30bb6c03bc99064d56b63b95625907`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|StorageEngine | PointWrite | Warm | complete | 5 | — | 40.66 | 24282.18 | 30333.18 | 35801.61 | 8714390.40 | 3.05 | 98,693,120 | 67,158 | 9.46 | `raw-samples-StorageEngine-PointWrite-Warm.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload point-write --temperature warm --dataset-size 100 --payload-size 128 --samples 5 --warmup 20 --operations 50 --output /tmp/terrabase-bench-current-storage-20260914`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
