# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `3ba9b91b4a172d6737a3d7e46ea65b680eb7305e74392a61ee2046f22d39633c`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/tmp/terrabase-bounded-memory-build-v2/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 1 --dataset-size 2 --payload-size 16 --samples 1 --operations 4 --writes 0 --warmup 2 --concurrency 1 --output bench/bounded-key-space-endpoint-smoke-20260923`

## Configuration

Backend: `StorageEngine`; dataset: `2` rows; payload: `16` bytes; payload mode: `Repeated`; generator: `repeated-x`; memory cap: `1` bytes.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `Repeated` |
| Payload generator | `repeated-x` |
| Estimated logical dataset bytes | 88 |
| Configured memory cap bytes | 1 |
| Seeded physical storage bytes | 67,121,871 |
| Seeded storage exceeds memory cap | True |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 4 | 1326.17 | 41.99 | 1089.09 | 1089.09 | 18366.00 | 6.07 | 79,953,920 | 67,108,864 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 1.89 | 84,119,552 | 67,108,864 | — |
|1 | cold-cache | operations | complete | 4 | 4848.48 | 40.31 | 566.89 | 566.89 | 16666.00 | 4.46 | 84,271,104 | 67,108,864 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 7747.33 | 22.31 | 468.45 | 986.74 | 3503.92 | 1.23 | 89,042,944 | 67,108,864 | — |
|1 | compaction | compaction | complete | 1 | 10.54 | 94871.10 | 94871.10 | 94871.10 | 1869456.00 | 1.85 | 89,812,992 | 0 | — |
