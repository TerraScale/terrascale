# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `a1da590345bf995fa3766a45b5f8994ea12b8d681bcf97ba84ed548f9fc48a54`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes
- Cgroup memory/cpu limits: unavailable in the receipt; the configured cap below was not enforced by a recorded cgroup.

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 8388608 --dataset-size 8192 --payload-size 2048 --samples 5 --operations 50 --writes 50 --warmup 20 --concurrency 1 --seed 1337 --zipf-exponent 1.2 --read-ratio 0.8 --output bench/receipts/20260914-source-matched-larger-memory-storage-engine-8192`

## Configuration

Backend: `StorageEngine`; dataset: `8,192` rows; payload: `2,048` bytes; configured memory threshold: `8,388,608` bytes (not an enforced cgroup limit).

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 50 | 932.99 | 7.98 | 8109.72 | 12054.15 | 301082.08 | 3.13 | 373,190,656 | 41,739,786 | 0.55 |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 1.81 | 421,683,200 | 41,739,786 | — |
|1 | cold-cache | operations | complete | 50 | 632.77 | 10.89 | 8268.70 | 12948.97 | 412883.68 | 1.23 | 436,789,248 | 41,818,389 | 0.76 |
|1 | uniform-read-heavy | operations | complete | 2000 | 389.75 | 9.71 | 10916.34 | 39508.89 | 382939.84 | 1.19 | 494,415,872 | 44,591,315 | 0.67 |
|1 | sustained-writes | operations | complete | 50 | 65.80 | 5508.01 | 33573.46 | 207767.93 | 2050248.64 | 1.22 | 325,750,784 | 44,948,721 | 3.47 |
|1 | compaction | compaction | complete | 1 | 0.20 | 4908867.80 | 4908867.80 | 4908867.80 | 152985136.00 | 0.86 | 385,093,632 | 0 | — |
|2 | warm-cache | operations | complete | 50 | 710.58 | 8.50 | 9385.29 | 28938.73 | 372967.68 | 0.78 | 385,413,120 | 64,325 | 0.62 |
|2 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 0.88 | 507,961,344 | 64,325 | — |
|2 | cold-cache | operations | complete | 50 | 622.35 | 7.54 | 10075.01 | 11556.82 | 458776.32 | 1.45 | 513,957,888 | 142,968 | 0.76 |
|2 | zipf-read-heavy | operations | complete | 2000 | 852.10 | 6.93 | 5745.10 | 12838.19 | 412335.21 | 1.68 | 236,576,768 | 2,994,902 | 0.69 |
|2 | sustained-writes | operations | complete | 50 | 276.54 | 3132.03 | 6303.84 | 13791.47 | 2050446.40 | 1.95 | 240,046,080 | 3,352,263 | 3.47 |
|2 | compaction | compaction | complete | 1 | 0.32 | 3091361.90 | 3091361.90 | 3091361.90 | 303649256.00 | 1.64 | 370,819,072 | 0 | — |
|3 | warm-cache | operations | complete | 50 | 1483.06 | 5.08 | 3830.17 | 12939.74 | 291156.48 | 2.46 | 370,851,840 | 50,023 | 0.49 |
|3 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.23 | 397,094,912 | 50,023 | — |
|3 | cold-cache | operations | complete | 50 | 1227.17 | 6.62 | 3917.76 | 5372.27 | 498418.24 | 1.53 | 397,099,008 | 135,788 | 0.83 |
|3 | zipf-write-heavy | operations | complete | 2000 | 141.79 | 4508.07 | 19001.04 | 40917.00 | 2177073.44 | 1.67 | 312,307,712 | 14,431,867 | 3.47 |
|3 | sustained-writes | operations | complete | 50 | 137.16 | 5989.18 | 17637.01 | 26942.36 | 2296735.84 | 2.88 | 323,039,232 | 14,789,269 | 3.47 |
|3 | compaction | compaction | complete | 1 | 0.38 | 2611342.70 | 2611342.70 | 2611342.70 | 331784368.00 | 1.82 | 480,489,472 | 0 | — |
|4 | warm-cache | operations | complete | 50 | 2505.55 | 6.67 | 4695.56 | 5108.82 | 187987.04 | 2.14 | 487,071,744 | 28,586 | 0.28 |
|4 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.40 | 520,409,088 | 28,586 | — |
|4 | cold-cache | operations | complete | 50 | 641.26 | 6.42 | 5120.81 | 10559.77 | 786235.36 | 1.53 | 550,334,464 | 150,082 | 1.18 |
|4 | uniform-read-heavy | operations | complete | 2000 | 881.68 | 6.99 | 5305.66 | 15728.15 | 471475.33 | 2.46 | 316,846,080 | 3,066,123 | 0.71 |
|4 | sustained-writes | operations | complete | 50 | 150.08 | 4359.80 | 18425.27 | 24141.07 | 2296481.76 | 2.91 | 300,961,792 | 3,423,517 | 3.47 |
|4 | compaction | compaction | complete | 1 | 0.28 | 3633932.20 | 3633932.20 | 3633932.20 | 360490328.00 | 1.87 | 478,285,824 | 0 | — |
|5 | warm-cache | operations | complete | 50 | 1231.23 | 6.74 | 5019.40 | 6864.58 | 418389.60 | 3.66 | 487,063,552 | 64,336 | 0.62 |
|5 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.15 | 646,246,400 | 64,336 | — |
|5 | cold-cache | operations | complete | 50 | 658.57 | 8.02 | 6029.64 | 11590.05 | 648790.24 | 1.45 | 668,078,080 | 164,386 | 0.97 |
|5 | zipf-read-heavy | operations | complete | 2000 | 804.75 | 7.31 | 5897.33 | 12653.69 | 463449.88 | 1.98 | 621,641,728 | 3,030,720 | 0.70 |
|5 | sustained-writes | operations | complete | 50 | 249.36 | 3117.83 | 9782.50 | 14882.33 | 2301146.88 | 2.13 | 590,323,712 | 3,388,114 | 3.47 |
|5 | compaction | compaction | complete | 1 | 0.37 | 2712712.90 | 2712712.90 | 2712712.90 | 372254512.00 | 1.96 | 469,934,080 | 0 | — |
