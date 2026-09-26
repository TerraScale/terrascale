# TerraBase benchmark receipt

Status: **failed**

Receipt JSON: `receipt.json`

## Environment

- Commit: `unavailable` (dirty: `False`)
- Artifact SHA-256: `f273f2c3399dfc7ed51ba28888f19b493de30efd9bbfe0ddfc0f5df3945f5deb`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20937965568 bytes
- GC: server `False`, latency mode `Interactive`

## Results

| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|StorageEngine | Scan | Warm | failed | 0 | — | — | — | — | — | — | — | — | — | — | `raw-samples-StorageEngine-Scan-Warm.jsonl` |

Failure for `Scan`: `System.InvalidOperationException: Dataset seed did not create key-00000000: Conflict.    at Program.BenchmarkRunner.SeedAsync(TerraBaseEngine engine, IReadOnlyList`1 keys, JsonElement body, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/Program.cs:line 582    at Program.BenchmarkRunner.RunWorkloadAsync(Workload workload, Temperature temperature, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/Program.cs:line 498    at Program.BenchmarkRunner.RunWorkloadAsync(Workload workload, Temperature temperature, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/Program.cs:line 536`

Command: `/home/mariogk/Projects/TerraScale/terrabase-zlinq-wt/bench/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks --storage storage-engine --workload scan --temperature warm --dataset-size 1000 --payload-size 256 --samples 5 --operations 1000 --warmup 200 --output bench/receipts/zlinq-net11-durable-zlinq`

All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.
