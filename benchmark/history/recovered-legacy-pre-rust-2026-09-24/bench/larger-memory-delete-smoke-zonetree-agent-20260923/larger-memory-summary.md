# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `0d143d680599f0092c2c59cd93380ea640bba3b3f79f5b9e0d28bd96983e609b`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend zonetree --memory-cap-bytes 1048576 --dataset-size 1024 --payload-size 4096 --payload-mode repeated --samples 1 --operations 20 --writes 20 --deletes 16 --warmup 10 --output bench/larger-memory-delete-smoke-zonetree-agent-20260923`

## Configuration

Backend: `ZoneTree`; dataset: `1,024` rows; payload: `4,096` bytes; payload mode: `Repeated`; generator: `repeated-x`; memory cap: `1,048,576` bytes; final delete set: `16` rows.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `Repeated` |
| Payload generator | `repeated-x` |
| Estimated logical dataset bytes | 4,222,976 |
| Configured memory cap bytes | 1,048,576 |
| Seeded physical storage bytes | 6,019,063 |
| Seeded storage exceeds memory cap | True |

## Delete qualification

The final sample applies the bounded delete set after sustained upserts. The StorageEngine lane verifies both the ordered surviving bodies and point-read absence before and after a cold reopen.

| Sample | Status | Requested | Deleted | Survivors | Delete absence verified | Survivor bodies verified | Delete elapsed ms | Cold reopen elapsed ms | Cold absence verified | Cold survivor bodies verified |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | skipped | 16 | 0 | 1,024 | 0 | 0 | 0.00 | 0.00 | 0 | 0 |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 20 | 581.18 | 51.13 | 10032.47 | 11890.55 | 69168.40 | 0.88 | 102,096,896 | 0 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.11 | 105,316,352 | 0 | — |
|1 | cold-cache | operations | complete | 20 | 615.17 | 62.84 | 10833.92 | 11768.53 | 69678.80 | 0.50 | 111,775,744 | 0 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 549.93 | 40.86 | 9709.74 | 12339.35 | 67081.18 | 0.59 | 117,784,576 | 0 | — |
|1 | sustained-writes | operations | complete | 20 | 114.31 | 8391.69 | 10597.92 | 10621.45 | 100986.40 | 0.26 | 117,784,576 | 0 | — |
|1 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
