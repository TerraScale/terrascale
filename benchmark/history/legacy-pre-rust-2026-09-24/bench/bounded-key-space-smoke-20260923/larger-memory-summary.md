# TerraBase larger-than-memory benchmark

Status: **invalid**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `f0a21f9b8785ae99d30521586f671ca140782e53837e26eb46699f8dfc08a6f5`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Debug`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Debug/net11.0/TerraBase.Benchmarks --workload larger-memory --backend storage-engine --memory-cap-bytes 1 --dataset-size 8 --payload-size 16 --samples 1 --operations 4 --writes 0 --warmup 2 --concurrency 1 --output bench/bounded-key-space-smoke-20260923`

## Configuration

Backend: `StorageEngine`; dataset: `8` rows; payload: `16` bytes; payload mode: `Repeated`; generator: `unknown`; memory cap: `1` bytes.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `Repeated` |
| Payload generator | `unknown` |
| Estimated logical dataset bytes | 0 |
| Configured memory cap bytes | 0 |
| Seeded physical storage bytes | — |
| Seeded storage exceeds memory cap | unknown |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| — | — | — | invalid | — | — | — | — | — | — | — | — | — | — |

Error: `Benchmark execution requires a Release artifact.`
