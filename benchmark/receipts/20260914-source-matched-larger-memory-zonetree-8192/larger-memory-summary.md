# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `a1da590345bf995fa3766a45b5f8994ea12b8d681bcf97ba84ed548f9fc48a54`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- Cgroup memory/cpu limits: unavailable in the receipt; the configured cap below was not enforced by a recorded cgroup.

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload larger-memory --backend zonetree --memory-cap-bytes 4194304 --dataset-size 8192 --payload-size 2048 --samples 5 --operations 50 --writes 50 --warmup 20 --concurrency 1 --seed 1337 --zipf-exponent 1.2 --read-ratio 0.8 --output bench/receipts/20260914-source-matched-larger-memory-zonetree-8192`

## Configuration

Backend: `ZoneTree`; dataset: `8,192` rows; payload: `2,048` bytes; configured memory threshold: `4,194,304` bytes (not an enforced cgroup limit).

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 50 | 530.23 | 55.91 | 9953.56 | 16065.86 | 31785.92 | 0.89 | 118,992,896 | 0 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.18 | 172,023,808 | 0 | — |
|1 | cold-cache | operations | complete | 50 | 311.23 | 87.94 | 14824.19 | 25333.61 | 33622.56 | 0.78 | 170,934,272 | 0 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 400.77 | 30.36 | 14231.12 | 26199.44 | 32479.80 | 0.58 | 176,189,440 | 0 | — |
|1 | sustained-writes | operations | complete | 50 | 113.57 | 8119.90 | 14606.65 | 16098.76 | 52584.48 | 1.17 | 176,336,896 | 0 | — |
|1 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|2 | warm-cache | operations | complete | 50 | 599.46 | 20.54 | 8988.77 | 19610.16 | 32039.20 | 0.32 | 177,127,424 | 0 | — |
|2 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 3.23 | 225,390,592 | 0 | — |
|2 | cold-cache | operations | complete | 50 | 578.45 | 25.14 | 8055.21 | 10513.31 | 33025.92 | 0.36 | 225,394,688 | 0 | — |
|2 | zipf-read-heavy | operations | complete | 2000 | 617.13 | 21.57 | 8313.27 | 11335.48 | 32606.91 | 0.41 | 174,448,640 | 0 | — |
|2 | sustained-writes | operations | complete | 50 | 126.20 | 7439.53 | 10339.83 | 11960.36 | 52624.16 | 0.36 | 174,481,408 | 0 | — |
|2 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|3 | warm-cache | operations | complete | 50 | 846.98 | 21.59 | 8472.70 | 9276.60 | 30692.48 | 0.42 | 174,493,696 | 0 | — |
|3 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.91 | 193,916,928 | 0 | — |
|3 | cold-cache | operations | complete | 50 | 525.42 | 23.55 | 8797.55 | 9926.41 | 33514.88 | 0.28 | 193,916,928 | 0 | — |
|3 | zipf-write-heavy | operations | complete | 2000 | 94.39 | 8951.47 | 19174.55 | 31674.31 | 52900.18 | 0.28 | 201,183,232 | 0 | — |
|3 | sustained-writes | operations | complete | 50 | 121.31 | 7859.09 | 10864.66 | 11934.59 | 52650.08 | 0.34 | 201,203,712 | 0 | — |
|3 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|4 | warm-cache | operations | complete | 50 | 1260.55 | 31.77 | 8516.66 | 13770.24 | 29957.12 | 0.42 | 201,392,128 | 0 | — |
|4 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.91 | 232,452,096 | 0 | — |
|4 | cold-cache | operations | complete | 50 | 309.79 | 51.31 | 10515.57 | 12566.14 | 35922.40 | 0.62 | 232,468,480 | 0 | — |
|4 | uniform-read-heavy | operations | complete | 2000 | 567.69 | 24.50 | 8649.28 | 12970.13 | 32712.08 | 0.31 | 232,484,864 | 0 | — |
|4 | sustained-writes | operations | complete | 50 | 118.22 | 7896.79 | 11498.43 | 13093.72 | 52814.88 | 0.27 | 232,484,864 | 0 | — |
|4 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|5 | warm-cache | operations | complete | 50 | 658.36 | 22.04 | 8013.57 | 11540.52 | 31963.52 | 0.60 | 232,509,440 | 0 | — |
|5 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.30 | 273,604,608 | 0 | — |
|5 | cold-cache | operations | complete | 50 | 344.64 | 28.38 | 11928.83 | 14633.93 | 34430.24 | 0.35 | 273,633,280 | 0 | — |
|5 | zipf-read-heavy | operations | complete | 2000 | 555.93 | 22.96 | 9080.61 | 14593.29 | 32635.11 | 0.33 | 246,394,880 | 0 | — |
|5 | sustained-writes | operations | complete | 50 | 120.13 | 7931.89 | 12526.76 | 14694.76 | 52820.32 | 0.30 | 246,427,648 | 0 | — |
|5 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
