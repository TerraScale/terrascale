# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `052c9529c678ceaa88212aeadc3d57607da372172c62ffe874e355b57d02673e`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 1048576 --dataset-size 1024 --payload-size 4096 --payload-mode per-key-incompressible --samples 1 --operations 20 --writes 20 --deletes 16 --warmup 10 --output bench/larger-memory-delete-smoke-agent-final-20260923`

## Configuration

Backend: `StorageEngine`; dataset: `1,024` rows; payload: `4,096` bytes; payload mode: `PerKeyIncompressible`; generator: `sha256-base64url-per-key`; memory cap: `1,048,576` bytes; final delete set: `16` rows.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `PerKeyIncompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Estimated logical dataset bytes | 4,222,976 |
| Configured memory cap bytes | 1,048,576 |
| Seeded physical storage bytes | 67,122,396 |
| Seeded storage exceeds memory cap | True |

## Delete qualification

The final sample applies the bounded delete set after sustained upserts. The StorageEngine lane verifies both the ordered surviving bodies and point-read absence before and after a cold reopen.

| Sample | Status | Requested | Deleted | Survivors | Delete absence verified | Survivor bodies verified | Delete elapsed ms | Cold reopen elapsed ms | Cold absence verified | Cold survivor bodies verified |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | complete | 16 | 16 | 1,008 | 16 | 1,008 | 28.09 | 685.48 | 16 | 1,008 |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 20 | 1487.93 | 232.31 | 2054.98 | 2891.71 | 83599.20 | 2.43 | 110,243,840 | 67,108,864 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.08 | 114,634,752 | 67,108,864 | — |
|1 | cold-cache | operations | complete | 20 | 1799.55 | 249.61 | 1710.48 | 3186.73 | 83219.20 | 2.30 | 114,806,784 | 67,108,864 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 1838.80 | 221.55 | 1769.50 | 2582.97 | 84907.82 | 2.28 | 118,550,528 | 67,108,864 | — |
|1 | sustained-writes | operations | complete | 20 | 147.14 | 1468.11 | 1820.20 | 107359.16 | 1260844.40 | 1.95 | 134,062,080 | 67,108,864 | — |
|1 | deletes | delete | complete | 16 | 569.60 | 1088.82 | 7102.41 | 7102.41 | 116642.00 | 1.35 | 134,127,616 | 67,108,864 | — |
|1 | delete-cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.58 | 122,507,264 | 67,108,864 | — |
|1 | compaction | compaction | complete | 1 | 1.99 | 501412.00 | 501412.00 | 501412.00 | 124348720.00 | 1.88 | 135,327,744 | 0 | — |
