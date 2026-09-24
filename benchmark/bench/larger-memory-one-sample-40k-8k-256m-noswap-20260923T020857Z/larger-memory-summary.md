# TerraBase larger-than-memory benchmark

Status: **failed**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `693659c2502924dc85addaaff53e317f67faa7a13dcb7b218507cdf5f6926617`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 40000 --payload-size 8192 --payload-mode per-key-incompressible --samples 1 --operations 500 --writes 500 --warmup 500 --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/larger-memory-one-sample-40k-8k-256m-noswap-20260923T020857Z`

## Configuration

Backend: `StorageEngine`; dataset: `40,000` rows; payload: `8,192` bytes; payload mode: `PerKeyIncompressible`; generator: `sha256-base64url-per-key`; memory cap: `268,435,456` bytes.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `PerKeyIncompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Estimated logical dataset bytes | 328,800,000 |
| Configured memory cap bytes | 268,435,456 |
| Seeded physical storage bytes | 1,055,283,074 |
| Seeded storage exceeds memory cap | True |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| — | — | — | failed | — | — | — | — | — | — | — | — | — | — |

Error: `Cancelled by user.`
