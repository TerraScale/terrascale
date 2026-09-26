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
|Memory | PointRead | Warm | complete | 5 | — | 260010.40 | 1.87 | 10.05 | 22.32 | 896.88 | 6.50 | 63,873,024 | — | — | [`raw-samples.jsonl`](raw-samples.jsonl) |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --storage memory --workload point-read --temperature warm --dataset-size 100 --payload-size 128 --samples 5 --warmup 50 --operations 100 --output /tmp/terrabase-bench-memory-1789365718`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
