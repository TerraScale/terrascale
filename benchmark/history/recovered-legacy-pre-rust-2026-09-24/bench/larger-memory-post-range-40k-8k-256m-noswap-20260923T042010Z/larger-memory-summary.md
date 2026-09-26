# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `a5e277a8c9eb436d9037984dfe37d472ce46cd89390ec755a0733767c6d24358`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 40000 --payload-size 8192 --payload-mode per-key-incompressible --samples 1 --operations 100 --writes 100 --warmup 100 --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/larger-memory-post-range-40k-8k-256m-noswap-20260923T042010Z`

## Configuration

Backend: `StorageEngine`; dataset: `40,000` rows; payload: `8,192` bytes; payload mode: `PerKeyIncompressible`; generator: `sha256-base64url-per-key`; memory cap: `268,435,456` bytes.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `PerKeyIncompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Estimated logical dataset bytes | 328,800,000 |
| Configured memory cap bytes | 268,435,456 |
| Seeded physical storage bytes | 1,055,272,974 |
| Seeded storage exceeds memory cap | True |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 100 | 92.37 | 10559.46 | 19271.06 | 22175.73 | 5250093.44 | 2.17 | 187,662,336 | 402,653,184 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.32 | 168,730,624 | 402,653,184 | — |
|1 | cold-cache | operations | complete | 100 | 65.57 | 14475.57 | 27531.21 | 35369.17 | 5602420.64 | 1.87 | 180,830,208 | 402,653,184 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 20.73 | 40404.86 | 144192.54 | 168039.42 | 30695929.25 | 2.28 | 164,950,016 | 402,653,184 | — |
|1 | sustained-writes | operations | complete | 100 | 8.64 | 122926.95 | 154450.91 | 173318.03 | 84676135.68 | 2.35 | 171,433,984 | 402,653,184 | — |
|1 | compaction | compaction | complete | 1 | 0.00 | 231252312.50 | 231252312.50 | 231252312.50 | 157638074904.00 | 2.17 | 218,202,112 | 0 | — |
