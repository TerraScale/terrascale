# TerraBase larger-than-memory benchmark

Status: **failed**

- Commit: `0f73f7874fd873e5aa2605fd06ad6854d1378372` (dirty: `True`)
- Artifact SHA-256: `882042c174fa7e7bb3cfb309ff618167b72b3569b59d9f21a6a621335fd8f162`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 150000 --payload-size 2048 --payload-mode repeated --samples 5 --operations 20 --writes 20 --warmup 100 --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/larger-memory-state-map-budget-v6-repeated-150k-noswap-20260923T0200Z`

## Configuration

Backend: `StorageEngine`; dataset: `150,000` rows; payload: `2,048` bytes; payload mode: `Repeated`; generator: `repeated-x`; memory cap: `268,435,456` bytes.

## Storage and capacity

| Measurement | Result |
| --- | ---: |
| Payload mode | `Repeated` |
| Payload generator | `repeated-x` |
| Estimated logical dataset bytes | 311,400,000 |
| Configured memory cap bytes | 268,435,456 |
| Seeded physical storage bytes | — |
| Seeded storage exceeds memory cap | unknown |

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| — | — | — | failed | — | — | — | — | — | — | — | — | — | — |

Error: `System.TypeLoadException: Could not load type 'ZoneTree.Segments.Block.BlockCacheBudget' from assembly 'TerraBase.ZoneTree, Version=1.9.7.0, Culture=neutral, PublicKeyToken=null'.    at TerraBase.StorageEngine.StorageEngine..ctor(OpenedRoot opened, IStorageTierPlacement placement, StorageWalDurability walDurability, IStorageSegmentStore segmentStore)    at TerraBase.StorageEngine.StorageEngine..ctor(String path, IStorageTierPlacement placement, StorageWalDurability walDurability, IStorageSegmentStore segmentStore) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageEngine.cs:line 224    at LargerThanMemoryWorkload.Open(String path, Backend backend) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 550    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 87`
