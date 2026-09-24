# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `5cb63a9c7bfa6ecb88e89ed5ae9c4856042bf24f4bd78fb908118dcad80d381a`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/tmp/terrabase-bounded-memory-build/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 1 --dataset-size 8 --payload-size 16 --samples 1 --operations 4 --writes 0 --warmup 2 --concurrency 1 --output bench/bounded-key-space-smoke-release-20260923`

## Configuration

Backend: `StorageEngine`; dataset: `8` rows; payload: `16` bytes; payload mode: `Repeated`; generator: `repeated-x`; memory cap: `1` bytes.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `Repeated` |
| Payload generator | `repeated-x` |
| Estimated logical dataset bytes | 352 |
| Configured memory cap bytes | 1 |
| Seeded physical storage bytes | 67,121,871 |
| Seeded storage exceeds memory cap | True |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 4 | 1861.68 | 58.37 | 805.08 | 805.08 | 17784.00 | 6.07 | 79,855,616 | 67,108,864 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 1.90 | 83,988,480 | 67,108,864 | — |
|1 | cold-cache | operations | complete | 4 | 2744.05 | 62.38 | 1054.26 | 1054.26 | 18380.00 | 3.16 | 84,193,280 | 67,108,864 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 5234.64 | 43.41 | 701.94 | 1684.53 | 3493.49 | 1.48 | 89,915,392 | 67,108,864 | — |
|1 | compaction | compaction | complete | 1 | 8.64 | 115782.90 | 115782.90 | 115782.90 | 1982376.00 | 1.93 | 90,890,240 | 0 | — |
