# Turso spike write throughput (2026-10-11)

Source: `benchmark/dotnet/TursoSpike` (`bench <seconds>`), analysis in `docs/terrabase/turso-spike.md`.
Fly app `spike-turso`, gru, shared-cpu-2x, 512 MB + 4 GB swap, 1 GB volume, burst CPU. One upsert per transaction,
50% inserts / 50% updates, body 222 B, state 256 B, `synchronous=NORMAL`. `Turso.Data.Sqlite.Provider` 0.8.2 vs
Microsoft.Data.Sqlite 11.0.0-rc.1. Rows missing after a pass ended in a Turso panic (process abort).

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
