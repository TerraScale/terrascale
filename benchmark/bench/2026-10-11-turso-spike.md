# Turso spike write throughput (2026-10-11)

Source: `benchmark/dotnet/TursoSpike` (`bench <seconds>`), analysis in `docs/terrabase/turso-spike.md`.
Fly app `spike-turso`, gru, shared-cpu-2x, 512 MB + 4 GB swap, 1 GB volume, burst CPU. One upsert per transaction,
50% inserts / 50% updates, body 222 B, state 256 B, `synchronous=NORMAL`. `Turso.Data.Sqlite.Provider` 0.8.2 vs
Microsoft.Data.Sqlite 11.0.0-rc.1. Rows missing after a pass ended in a Turso panic (process abort).

> **Correction (2026-10-12).** In passes 1–4, `turso-wal-cdc-uring` and `turso-wal-cdc-aegis256` did **not** capture:
> the probe enabled CDC only for engine names ending in `cdc`. Read those rows as io_uring / aegis256 **without**
> CDC. Passes 5–6 (below) verify capture on every run.

## Pass 1

| engine | writers | upserts/s | p50 ms | p99 ms | db+wal MB |
|---|---|---|---|---|---|
| ms-wal-normal | 1 | 1439 | 0.033 | 0.134 | 4.5 |
| turso-wal | 1 | 1218 | 0.107 | 0.423 | 6.1 |
| turso-wal-cdc | 1 | 766 | 0.101 | 0.277 | 13.2 |
| turso-wal-cdc-uring | 1 | 1193 | 0.111 | 0.319 | 7.6 |
| turso-mvcc-concurrent | 1 | 5147 | 0.141 | 0.273 | 16.6 |
| turso-mvcc-concurrent-cdc | 1 | 1689 | 0.530 | 1.321 | 20.9 |
| ms-wal-normal | 2 | 1461 | 0.038 | 10.799 | 6.2 |
| turso-wal | 2 | 1605 | 0.135 | 11.815 | 211.9 |
| turso-wal-cdc | 2 | 882 | 0.144 | 14.068 | 177.6 |
| turso-wal-cdc-uring | 2 | 1134 | 0.167 | 11.066 | 132.3 (retries 59) |
| turso-mvcc-concurrent | 2 | 5625 | 0.145 | 0.340 | 18.6 |
| turso-mvcc-concurrent-cdc | 2 | 908 | 0.942 | 3.037 | 11.0 (retries 25583) |

## Pass 2

| engine | writers | upserts/s | p50 ms | p99 ms | db+wal MB |
|---|---|---|---|---|---|
| ms-wal-normal | 1 | 1369 | 0.030 | 0.095 | 4.3 |
| turso-wal | 1 | 1211 | 0.085 | 0.200 | 5.2 |
| turso-wal-cdc | 1 | 766 | 0.102 | 0.293 | 13.2 |
| turso-wal-cdc-uring | 1 | 1193 | 0.107 | 0.215 | 7.6 |
| turso-mvcc-concurrent | 1 | 5626 | 0.103 | 0.189 | 18.7 |
| turso-mvcc-concurrent-cdc | 1 | 1889 | 0.467 | 1.194 | 23.1 |
| ms-wal-normal | 2 | 1449 | 0.030 | 22.366 | 6.0 |
| turso-wal | 2 | 1572 | 0.091 | 14.334 | 210.2 |
| turso-wal-cdc | 2 | 928 | 0.125 | 18.877 | 179.7 |

Pass ended: `panicked at core/storage/wal.rs:3590:9:`

## Pass 3

| engine | writers | upserts/s | p50 ms | p99 ms | db+wal MB |
|---|---|---|---|---|---|
| ms-wal-normal | 1 | 1516 | 0.029 | 0.112 | 3.8 |
| turso-wal | 1 | 1231 | 0.123 | 0.364 | 7.0 |
| turso-wal-cdc | 1 | 772 | 0.103 | 0.512 | 11.4 |
| turso-wal-cdc-aegis256 | 1 | 1206 | 0.115 | 0.266 | 7.0 |
| turso-wal-cdc-uring | 1 | 1207 | 0.112 | 0.468 | 7.0 |
| turso-mvcc-concurrent | 1 | 5430 | 0.132 | 0.235 | 14.5 |
| turso-mvcc-concurrent-cdc | 1 | 1577 | 0.585 | 1.258 | 16.0 |
| ms-wal-normal | 2 | 1580 | 0.041 | 11.229 | 5.8 |
| turso-wal | 2 | 1586 | 0.113 | 10.780 | 178.5 |
| turso-wal-cdc | 2 | 880 | 0.158 | 13.782 | 143.4 |
| turso-wal-cdc-aegis256 | 2 | 1382 | 0.150 | 12.916 | 141.5 |
| turso-wal-cdc-uring | 2 | 1190 | 0.112 | 4.125 | 125.9 (retries 8) |
| turso-mvcc-concurrent | 2 | 5847 | 0.151 | 0.356 | 14.5 |
| turso-mvcc-concurrent-cdc | 2 | 728 | 0.868 | 4.370 | 7.2 (retries 14256) |

## Pass 4

| engine | writers | upserts/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB |
|---|---|---|---|---|---|---|---|
| ms-wal-normal | 1 | 1451 | 0.023 | 0.081 | 293.6 | 301 | 4.5 |
| turso-wal | 1 | 1202 | 0.113 | 0.290 | 259.8 | 273 | 7.7 |
| turso-wal-cdc | 1 | 765 | 0.119 | 0.569 | 275.2 | 277 | 13.2 |
| turso-wal-cdc-aegis256 | 1 | 1194 | 0.113 | 0.263 | 257.9 | 267 | 7.6 |
| turso-wal-cdc-uring | 1 | 1193 | 0.112 | 0.282 | 257.9 | 268 | 7.6 |
| turso-mvcc-concurrent | 1 | 5417 | 0.127 | 0.233 | 0.5 | 691 | 17.1 |
| turso-mvcc-concurrent-cdc | 1 | 1675 | 0.540 | 1.243 | 2.0 | 106 | 20.7 |
| ms-wal-normal | 2 | 1527 | 0.041 | 11.104 | 90.3 | 11587 | 6.4 |
| turso-wal | 2 | 1592 | 0.108 | 12.345 | 19.1 | 11245 | 205.5 |
| turso-wal-cdc | 2 | 914 | 0.156 | 14.233 | 44.2 | 10594 | 176.9 |
| turso-wal-cdc-aegis256 | 2 | 1143 | 0.125 | 13.311 | 30.4 | 9757 | 4.4 |

Pass ended: `panicked at core/storage/wal.rs:3590:9:`

## Pass 5 (2026-10-12, capture verified)

Fly gru, same machine shape, `bench 8 all <workload> 1`. `cdc data rows` counts `turso_cdc` rows of types 1/0/-1 after the run.

Workload `upsert`:

| engine | writers | writes/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB | cdc data rows | cdc rows/write |
|---|---|---|---|---|---|---|---|---|---|
| ms-wal-normal | 1 | 1397 | 0.024 | 0.126 | 288.9 | 298 | 3.5 | - | - |
| turso-wal | 1 | 1228 | 0.130 | 0.373 | 250.4 | 261 | 7.0 | no table | - |
| turso-wal-cdc | 1 | 772 | 0.161 | 0.607 | 256.8 | 262 | 11.4 | 6196 | 0.99 |
| turso-wal-cdc-after | 1 | 823 | 0.196 | 1.652 | 244.4 | 255 | 9.7 | 6548 | 0.99 |
| turso-wal-tc | 1 | 1209 | 0.119 | 0.370 | 256.4 | 265 | 7.0 | no table | - |
| turso-wal-cdc-tc | 1 | 773 | 0.133 | 0.530 | 263.5 | 270 | 11.4 | 6188 | 0.99 |
| turso-wal-cdc-after-tc | 1 | 825 | 0.143 | 0.430 | 259.0 | 261 | 9.7 | 6542 | 0.99 |
| turso-wal-uring | 1 | 1214 | 0.178 | 0.524 | 231.1 | 250 | 7.0 | no table | - |
| turso-wal-cdc-uring | 1 | 768 | 0.179 | 1.250 | 253.4 | 259 | 11.4 | 6178 | 0.99 |
| turso-wal-cdc-after-uring | 1 | 821 | 0.236 | 1.023 | 239.2 | 248 | 9.7 | 6545 | 0.99 |
| turso-wal-aegis256 | 1 | 1206 | 0.173 | 0.486 | 230.6 | 247 | 7.0 | no table | - |
| turso-wal-cdc-aegis256 | 1 | 771 | 0.209 | 0.566 | 246.5 | 251 | 11.4 | 6198 | 0.99 |
| turso-wal-cdc-after-aegis256 | 1 | 825 | 0.210 | 0.545 | 241.6 | 246 | 9.7 | 6548 | 0.99 |
| turso-mvcc-concurrent | 1 | 5018 | 0.154 | 0.320 | 0.6 | 595 | 12.5 | no table | - |
| turso-mvcc-concurrent-cdc | 1 | 858 | 1.070 | 3.729 | 6.3 | 130 | 9.1 | 6835 | 1.00 |
| ms-wal-normal-again | 1 | 1418 | 0.021 | 0.101 | 292.2 | 299 | 3.5 | - | - |

Workload `designc`:

| engine | writers | writes/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB | cdc data rows | cdc rows/write |
|---|---|---|---|---|---|---|---|---|---|
| ms-wal-normal | 1 | 1267 | 0.030 | 0.143 | 282.2 | 294 | 3.7 | - | - |
| turso-wal | 1 | 1028 | 0.227 | 1.270 | 227.2 | 240 | 7.0 | no table | - |
| turso-wal-cdc | 1 | 629 | 0.216 | 1.564 | 257.3 | 261 | 13.0 | 10228 | 2.00 |
| turso-wal-cdc-after | 1 | 722 | 0.183 | 1.336 | 251.9 | 259 | 8.5 | 11554 | 2.00 |
| turso-wal-tc | 1 | 1040 | 0.161 | 0.427 | 239.9 | 253 | 5.4 | no table | - |
| turso-wal-cdc-tc | 1 | 632 | 0.236 | 1.847 | 252.7 | 258 | 13.0 | 10202 | 2.00 |
| turso-wal-cdc-after-tc | 1 | 699 | 0.179 | 1.413 | 253.0 | 402 | 10.0 | 11312 | 2.00 |
| turso-wal-uring | 1 | 1027 | 0.227 | 0.761 | 221.5 | 235 | 3.9 | no table | - |
| turso-wal-cdc-uring | 1 | 628 | 0.295 | 2.183 | 238.3 | 246 | 12.9 | 10204 | 2.00 |
| turso-wal-cdc-after-uring | 1 | 711 | 0.217 | 1.444 | 244.8 | 249 | 6.5 | 11368 | 2.00 |
| turso-wal-aegis256 | 1 | 1025 | 0.205 | 0.522 | 224.3 | 233 | 4.2 | no table | - |
| turso-wal-cdc-aegis256 | 1 | 622 | 0.278 | 1.664 | 243.7 | 301 | 13.1 | 10188 | 2.00 |
| turso-wal-cdc-after-aegis256 | 1 | 728 | 0.338 | 1.996 | 228.0 | 231 | 9.6 | 11656 | 2.00 |
| turso-mvcc-concurrent | 1 | 3592 | 0.229 | 0.465 | 1.1 | 270 | 10.5 | no table | - |
| turso-mvcc-concurrent-cdc | 1 | 711 | 1.340 | 2.592 | 3.3 | 110 | 10.4 | 11368 | 2.00 |
| ms-wal-normal-again | 1 | 1160 | 0.024 | 0.120 | 284.1 | 294 | 3.4 | - | - |

## Pass 6 (2026-10-12, capture verified)

Fly gru, `bench 8 turso-wal both 1`.

Workload `upsert`:

| engine | writers | writes/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB | cdc data rows | cdc rows/write |
|---|---|---|---|---|---|---|---|---|---|
| turso-wal | 1 | 1428 | 0.172 | 0.453 | 245.0 | 257 | 7.5 | no table | - |
| turso-wal-cdc | 1 | 773 | 0.147 | 0.478 | 260.4 | 268 | 11.4 | 6196 | 0.99 |
| turso-wal-cdc-after | 1 | 822 | 0.146 | 0.436 | 255.5 | 262 | 9.7 | 6544 | 0.99 |
| turso-wal-tc | 1 | 1211 | 0.137 | 0.399 | 248.5 | 257 | 7.0 | no table | - |
| turso-wal-cdc-tc | 1 | 772 | 0.170 | 0.599 | 259.4 | 270 | 11.4 | 6193 | 0.99 |
| turso-wal-cdc-after-tc | 1 | 826 | 0.134 | 0.468 | 257.7 | 264 | 9.7 | 6544 | 0.98 |
| turso-wal-uring | 1 | 1210 | 0.191 | 0.656 | 228.3 | 240 | 7.0 | no table | - |
| turso-wal-cdc-uring | 1 | 767 | 0.236 | 1.350 | 252.3 | 260 | 11.3 | 6182 | 0.99 |
| turso-wal-cdc-after-uring | 1 | 821 | 0.193 | 0.968 | 246.5 | 251 | 9.7 | 6533 | 0.99 |
| turso-wal-aegis256 | 1 | 1209 | 0.174 | 0.468 | 229.9 | 242 | 7.0 | no table | - |
| turso-wal-cdc-aegis256 | 1 | 769 | 0.225 | 0.661 | 245.2 | 250 | 11.4 | 6198 | 0.99 |
| turso-wal-cdc-after-aegis256 | 1 | 823 | 0.212 | 0.613 | 241.3 | 247 | 9.7 | 6548 | 0.99 |
Workload `designc`:

| engine | writers | writes/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB | cdc data rows | cdc rows/write |
|---|---|---|---|---|---|---|---|---|---|
| turso-wal | 1 | 1028 | 0.171 | 0.537 | 240.6 | 251 | 4.0 | no table | - |
| turso-wal-cdc | 1 | 629 | 0.224 | 1.810 | 255.3 | 264 | 13.0 | 10226 | 2.00 |
| turso-wal-cdc-after | 1 | 708 | 0.190 | 1.347 | 250.8 | 253 | 6.1 | 11322 | 2.00 |
| turso-wal-tc | 1 | 1011 | 0.155 | 0.442 | 239.7 | 313 | 6.9 | no table | - |
| turso-wal-cdc-tc | 1 | 629 | 0.202 | 1.552 | 259.8 | 261 | 13.0 | 10226 | 2.00 |
| turso-wal-cdc-after-tc | 1 | 721 | 0.164 | 0.967 | 256.5 | 260 | 8.3 | 11530 | 2.00 |
| turso-wal-uring | 1 | 1025 | 0.215 | 0.651 | 225.7 | 241 | 7.0 | no table | - |
| turso-wal-cdc-uring | 1 | 629 | 0.291 | 2.295 | 245.3 | 260 | 13.0 | 10246 | 2.00 |
| turso-wal-cdc-after-uring | 1 | 716 | 0.226 | 1.435 | 244.0 | 247 | 7.3 | 11444 | 2.00 |
| turso-wal-aegis256 | 1 | 1034 | 0.207 | 0.553 | 225.3 | 232 | 4.6 | no table | - |
| turso-wal-cdc-aegis256 | 1 | 629 | 0.310 | 1.896 | 236.9 | 240 | 13.1 | 10190 | 2.00 |
| turso-wal-cdc-after-aegis256 | 1 | 705 | 0.259 | 1.462 | 235.0 | 300 | 10.0 | 11310 | 2.00 |

## Local (2026-10-12, capture verified, load average 14-25)

16-core dev box shared with other work: rankings only, not absolute numbers. `bench 5 all both 1`, first pass.

Workload `upsert`:

| engine | writers | writes/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB | cdc data rows | cdc rows/write |
|---|---|---|---|---|---|---|---|---|---|
| ms-wal-normal | 1 | 20484 | 0.017 | 0.068 | 9.4 | 19 | 32.1 | - | - |
| turso-wal | 1 | 11394 | 0.056 | 0.156 | 7.7 | 14 | 20.6 | no table | - |
| turso-wal-cdc | 1 | 8544 | 0.070 | 0.428 | 7.6 | 154 | 54.9 | 42689 | 1.00 |
| turso-wal-cdc-after | 1 | 8911 | 0.068 | 0.311 | 8.1 | 21 | 42.2 | 44492 | 1.00 |
| turso-wal-tc | 1 | 10137 | 0.052 | 0.259 | 8.9 | 43 | 17.7 | no table | - |
| turso-wal-cdc-tc | 1 | 8853 | 0.065 | 0.338 | 8.3 | 28 | 56.8 | 44230 | 1.00 |
| turso-wal-cdc-after-tc | 1 | 7541 | 0.065 | 1.423 | 9.9 | 28 | 34.2 | 37635 | 1.00 |
| turso-wal-uring | 1 | 5007 | 0.067 | 0.747 | 37.1 | 71 | 9.8 | no table | - |
| turso-wal-cdc-uring | 1 | 4539 | 0.083 | 0.762 | 24.7 | 961 | 31.1 | 22694 | 1.00 |
| turso-wal-cdc-after-uring | 1 | 6683 | 0.082 | 0.737 | 9.8 | 20 | 29.0 | 33358 | 1.00 |
| turso-wal-aegis256 | 1 | 7594 | 0.078 | 0.311 | 12.0 | 23 | 15.2 | no table | - |
| turso-wal-cdc-aegis256 | 1 | 5417 | 0.115 | 0.478 | 12.0 | 39 | 35.1 | 27047 | 1.00 |
| turso-wal-cdc-after-aegis256 | 1 | 5646 | 0.110 | 0.454 | 11.9 | 24 | 28.5 | 28238 | 1.00 |
| turso-mvcc-concurrent | 1 | 10823 | 0.069 | 0.165 | 0.5 | 224 | 17.1 | no table | - |
| turso-mvcc-concurrent-cdc | 1 | 1692 | 0.493 | 0.980 | 1.6 | 509 | 10.5 | 8442 | 1.00 |
| ms-wal-normal-again | 1 | 19713 | 0.018 | 0.071 | 10.0 | 19 | 30.8 | - | - |
Workload `designc`:

| engine | writers | writes/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB | cdc data rows | cdc rows/write |
|---|---|---|---|---|---|---|---|---|---|
| ms-wal-normal | 1 | 18018 | 0.024 | 0.087 | 8.7 | 28 | 32.9 | - | - |
| turso-wal | 1 | 9410 | 0.070 | 0.239 | 8.1 | 26 | 18.9 | no table | - |
| turso-wal-cdc | 1 | 6678 | 0.097 | 1.273 | 7.8 | 14 | 63.6 | 66868 | 2.00 |
| turso-wal-cdc-after | 1 | 6255 | 0.097 | 1.263 | 9.2 | 34 | 37.6 | 62610 | 2.00 |
| turso-wal-tc | 1 | 8571 | 0.070 | 0.364 | 8.8 | 20 | 19.5 | no table | - |
| turso-wal-cdc-tc | 1 | 7003 | 0.090 | 1.272 | 7.8 | 18 | 65.6 | 70014 | 2.00 |
| turso-wal-cdc-after-tc | 1 | 7393 | 0.085 | 1.176 | 7.6 | 15 | 42.2 | 73944 | 2.00 |
| turso-wal-uring | 1 | 9255 | 0.073 | 0.232 | 7.8 | 15 | 19.3 | no table | - |
| turso-wal-cdc-uring | 1 | 6664 | 0.098 | 1.322 | 7.6 | 37 | 59.1 | 66644 | 2.00 |
| turso-wal-cdc-after-uring | 1 | 6994 | 0.093 | 1.209 | 8.0 | 15 | 38.8 | 69938 | 2.00 |
| turso-wal-aegis256 | 1 | 6781 | 0.101 | 0.285 | 10.0 | 32 | 15.5 | no table | - |
| turso-wal-cdc-aegis256 | 1 | 3843 | 0.159 | 1.588 | 12.8 | 25 | 37.5 | 38426 | 2.00 |
| turso-wal-cdc-after-aegis256 | 1 | 4644 | 0.140 | 1.390 | 10.4 | 23 | 25.7 | 46438 | 2.00 |
| turso-mvcc-concurrent | 1 | 8462 | 0.086 | 0.282 | 0.5 | 161 | 16.4 | no table | - |
| turso-mvcc-concurrent-cdc | 1 | 1211 | 0.754 | 1.755 | 2.6 | 100 | 11.3 | 12112 | 2.00 |
| ms-wal-normal-again | 1 | 15393 | 0.025 | 0.126 | 9.6 | 33 | 28.0 | - | - |
