# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `0d143d680599f0092c2c59cd93380ea640bba3b3f79f5b9e0d28bd96983e609b`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 1048576 --dataset-size 1024 --payload-size 4096 --payload-mode per-key-incompressible --samples 1 --operations 20 --writes 20 --deletes 16 --warmup 10 --output bench/larger-memory-delete-smoke-agent-20260923`

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
|1 | complete | 16 | 16 | 1,008 | 16 | 1,008 | 28.82 | 689.70 | 16 | 1,008 |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 20 | 1217.18 | 260.14 | 2451.60 | 4046.16 | 83546.00 | 2.22 | 106,278,912 | 67,108,864 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.07 | 114,753,536 | 67,108,864 | — |
|1 | cold-cache | operations | complete | 20 | 1745.41 | 237.17 | 1872.71 | 2776.09 | 83227.20 | 1.90 | 114,806,784 | 67,108,864 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 1816.20 | 228.37 | 1882.02 | 2524.58 | 84759.00 | 3.01 | 122,253,312 | 67,108,864 | — |
|1 | sustained-writes | operations | complete | 20 | 144.27 | 1478.62 | 3734.36 | 106363.07 | 1260424.80 | 1.89 | 137,859,072 | 67,108,864 | — |
|1 | deletes | delete | complete | 16 | 555.10 | 1222.55 | 7076.97 | 7076.97 | 117809.50 | 1.44 | 137,924,608 | 67,108,864 | — |
|1 | delete-cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.64 | 126,169,088 | 67,108,864 | — |
|1 | compaction | compaction | complete | 1 | 2.02 | 496092.00 | 496092.00 | 496092.00 | 124330824.00 | 1.92 | 138,575,872 | 0 | — |
