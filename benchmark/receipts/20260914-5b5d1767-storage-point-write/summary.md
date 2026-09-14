# TerraBase benchmark receipt

Status: **complete**

Receipt JSON: `receipt.json`

## Environment

- Commit: `5b5d1767d16211794020a111eeb5467bd73b0e5f` (dirty: `True`)
- Artifact SHA-256: `c7d202a8b3d790d92711c864a42c47a2ca65862465560ce7fda0bcb274ec66ac`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|StorageEngine | PointWrite | Warm | complete | 5 | — | 40.41 | 23816.13 | 30250.68 | 36456.07 | 8713948.64 | 3.07 | 98,619,392 | 67,153 | 9.46 | [`raw-samples.jsonl`](raw-samples.jsonl) |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage storage-engine --workload point-write --temperature warm --dataset-size 100 --payload-size 128 --samples 5 --warmup 20 --operations 50 --output /tmp/terrabase-bench-storage-1789365739`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
