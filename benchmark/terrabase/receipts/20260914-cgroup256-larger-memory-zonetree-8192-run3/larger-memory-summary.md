# TerraBase larger-than-memory benchmark

Status: **complete**

- Commit: `unavailable` (dirty: `unknown`)
- Artifact SHA-256: `bb2dde0ca26db0c408039d16ff5d8ebaf50dcf31b3ceb7d583ee5a720819dbdb`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes
- Cgroup: `memory.max=268435456` bytes, launched with `systemd-run --user --scope --collect -p MemoryMax=256M -p OOMPolicy=stop`; [scope probe evidence](../../../.codex/evidence/cgroup256-scope-probe.log)
- Cgroup OOM events: `0`; cgroup memory limit is recorded from a matching scope probe because this benchmark artifact does not sample its own `memory.max`.

Command: `systemd-run --user --scope --collect -p MemoryMax=256M -p OOMPolicy=stop nice -n 19 /tmp/terrabase-bench-cgroup.IcBhkq/TerraBase.Benchmarks.dll --workload larger-memory --backend zonetree --memory-cap-bytes 4194304 --dataset-size 8192 --payload-size 2048 --samples 5 --operations 50 --writes 50 --warmup 20 --concurrency 1 --seed 1337 --zipf-exponent 1.2 --read-ratio 0.8 --output /home/mariogk/Projects/TerraScale/terrabase/bench/receipts/20260914-cgroup256-larger-memory-zonetree-8192-run3`

## Configuration

Backend: `ZoneTree`; dataset: `8,192` rows; payload: `2,048` bytes; configured logical cap: `4,194,304` bytes; enforced cgroup limit: `268,435,456` bytes.

The estimated logical dataset is `17,006,592` bytes. It exceeds the configured logical cap, but it is smaller than the enforced cgroup limit. This receipt is therefore a cgroup constrained capacity exercise and does not establish that the dataset exceeded available cgroup memory.

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|1 | warm-cache | operations | complete | 50 | 511.31 | 48.76 | 12390.59 | 14135.65 | 32109.60 | 0.69 | 107,053,056 | 0 | — |
|1 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.05 | 161,755,136 | 0 | — |
|1 | cold-cache | operations | complete | 50 | 226.28 | 146.69 | 20646.76 | 38599.00 | 32831.20 | 0.41 | 163,516,416 | 0 | — |
|1 | uniform-read-heavy | operations | complete | 2000 | 452.54 | 31.05 | 12013.86 | 15474.51 | 32430.10 | 0.51 | 167,538,688 | 0 | — |
|1 | sustained-writes | operations | complete | 50 | 95.26 | 9834.84 | 14367.42 | 16266.43 | 52482.88 | 0.81 | 167,661,568 | 0 | — |
|1 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|2 | warm-cache | operations | complete | 50 | 522.83 | 25.86 | 10995.42 | 13301.22 | 31976.64 | 0.37 | 168,402,944 | 0 | — |
|2 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 3.01 | 177,000,448 | 0 | — |
|2 | cold-cache | operations | complete | 50 | 460.25 | 33.65 | 10277.82 | 13580.50 | 32870.08 | 0.31 | 177,004,544 | 0 | — |
|2 | zipf-read-heavy | operations | complete | 2000 | 483.38 | 22.43 | 10806.27 | 13761.10 | 32555.92 | 0.45 | 184,750,080 | 0 | — |
|2 | sustained-writes | operations | complete | 50 | 97.46 | 9638.67 | 14578.53 | 15932.79 | 52494.88 | 0.35 | 184,762,368 | 0 | — |
|2 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|3 | warm-cache | operations | complete | 50 | 704.83 | 20.38 | 9610.39 | 12568.55 | 31238.08 | 0.29 | 184,770,560 | 0 | — |
|3 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 3.39 | 234,008,576 | 0 | — |
|3 | cold-cache | operations | complete | 50 | 304.55 | 33.18 | 10318.96 | 47070.04 | 33305.60 | 1.63 | 234,184,704 | 0 | — |
|3 | zipf-write-heavy | operations | complete | 2000 | 76.39 | 11399.36 | 22481.18 | 33009.36 | 52687.20 | 0.24 | 220,803,072 | 0 | — |
|3 | sustained-writes | operations | complete | 50 | 76.94 | 11619.49 | 19978.36 | 40542.18 | 53075.20 | 0.24 | 220,823,552 | 0 | — |
|3 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|4 | warm-cache | operations | complete | 50 | 927.29 | 21.66 | 9874.88 | 16866.24 | 29578.56 | 0.40 | 220,876,800 | 0 | — |
|4 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.86 | 225,914,880 | 0 | — |
|4 | cold-cache | operations | complete | 50 | 248.14 | 52.34 | 16307.30 | 17723.93 | 35831.20 | 0.75 | 226,193,408 | 0 | — |
|4 | uniform-read-heavy | operations | complete | 2000 | 293.47 | 36.29 | 18739.65 | 31832.31 | 32670.10 | 0.25 | 226,394,112 | 0 | — |
|4 | sustained-writes | operations | complete | 50 | 77.30 | 12130.62 | 18639.87 | 26292.40 | 52339.52 | 0.31 | 226,410,496 | 0 | — |
|4 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
|5 | warm-cache | operations | complete | 50 | 322.84 | 54.10 | 17966.23 | 22868.64 | 31812.00 | 0.30 | 226,418,688 | 0 | — |
|5 | cold-reopen | reopen | complete | 0 | 0.00 | — | — | — | — | 2.45 | 236,781,568 | 0 | — |
|5 | cold-cache | operations | complete | 50 | 226.71 | 38.18 | 20447.99 | 26367.78 | 34471.04 | 0.29 | 236,797,952 | 0 | — |
|5 | zipf-read-heavy | operations | complete | 2000 | 380.50 | 27.89 | 14154.62 | 20504.27 | 32582.18 | 0.27 | 235,573,248 | 0 | — |
|5 | sustained-writes | operations | complete | 50 | 91.06 | 10214.08 | 16433.97 | 19210.07 | 52354.72 | 0.26 | 235,618,304 | 0 | — |
|5 | compaction | compaction | complete | 0 | 0.00 | — | — | — | — | 0.00 | 0 | — | — |
