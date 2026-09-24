# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `002ba1e6f69fa3bd62987904117e1eea3d2cc4873cd4958dd3e62072f8549dd8`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/root-loss-20260923T073010Z/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 40000 --payload-size 8192 --payload-mode per-key-incompressible --samples 1 --operations 100 --writes 100 --deletes 1000 --warmup 100 --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/larger-memory-delete-40k-8k-256m-20260923T074230Z`

## Configuration

Backend: `StorageEngine`; dataset: `40,000` rows; payload: `8,192` bytes; payload mode: `PerKeyIncompressible`; generator: `sha256-base64url-per-key`; memory cap: `268,435,456` bytes; final delete set: `1,000` rows.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `PerKeyIncompressible` |
| Payload generator | `sha256-base64url-per-key` |
| Estimated logical dataset bytes | 328,800,000 |
| Configured memory cap bytes | 268,435,456 |
| Seeded physical storage bytes | 1,056,100,778 |
| Seeded storage exceeds memory cap | True |

## Delete qualification

The final sample applies the bounded delete set after sustained upserts. The StorageEngine lane verifies both the ordered surviving bodies and point-read absence before and after a cold reopen.

| Sample | Status | Requested | Deleted | Survivors | Delete absence verified | Survivor bodies verified | Delete elapsed ms | Cold reopen elapsed ms | Cold absence verified | Cold survivor bodies verified |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | complete | 1,000 | 1,000 | 39,000 | 1,000 | 39,000 | 8579.09 | 65093.56 | 1,000 | 39,000 |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 100 | 185.72 | 4623.67 | 11768.71 | 15000.55 | 2274806.56 | 2.19 | 179,625,984 | 402,653,184 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.30 | 191,479,808 | 402,653,184 | — |
|1 | cold-cache | operations | complete | 100 | 150.53 | 5280.18 | 15272.05 | 22970.20 | 2521204.64 | 1.91 | 201,560,064 | 402,653,184 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 190.55 | 4004.85 | 12194.64 | 14594.70 | 2458687.52 | 1.95 | 172,974,080 | 402,653,184 | — |
|1 | sustained-writes | operations | complete | 100 | 87.71 | 11443.35 | 16696.22 | 17686.70 | 5573329.28 | 2.06 | 173,363,200 | 402,653,184 | — |
|1 | deletes | delete | complete | 1000 | 116.56 | 7799.52 | 11466.24 | 15849.17 | 4978424.66 | 2.12 | 192,983,040 | 402,653,184 | — |
|1 | delete-cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.27 | 194,682,880 | 402,653,184 | — |
|1 | compaction | compaction | complete | 1 | 0.01 | 91852156.00 | 91852156.00 | 91852156.00 | 55294960672.00 | 2.09 | 224,727,040 | 0 | — |
