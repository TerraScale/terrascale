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
|StorageEngine | Scan | Warm | complete | 5 | — | 107.98 | 9292.03 | 11728.36 | 12563.12 | 686310.40 | 3.28 | 89,653,248 | 0 | — | `raw-samples-StorageEngine-Scan-Warm.jsonl` |
|StorageEngine | Scan | Cold | complete | 5 | 242.47 | 197.65 | 5034.97 | 5792.23 | 6015.69 | 622426.00 | 4.36 | 104,001,536 | 0 | — | `raw-samples-StorageEngine-Scan-Cold.jsonl` |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload scan --temperature both --dataset-size 1000 --payload-size 256 --samples 5 --warmup 5 --operations 20 --concurrency 1 --seed 1337 --output bench/receipts/20260914-source-matched-storage-engine-scan-both`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
