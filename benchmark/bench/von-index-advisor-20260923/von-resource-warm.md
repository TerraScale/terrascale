# Von local index advisor resource benchmark

Date: 2026-09-24T02:02:57.8015886+00:00; Von source: wfzyx/von 946d4eb95071982ec0b67f8c762759c04c4080ff; model: von-1.2.0; server PID: 2131099; requests: 1 warm + 10 steady; cadence: 1.1 s; OMP_NUM_THREADS=4; device=CPU. Each request has two candidate fields and five questions. Each request uses a different schema version to avoid advisor cache hits.

| Measure | Value |
| --- | ---: |
| Idle server RSS | 8.7 MiB |
| Final server RSS | 1466.4 MiB |
| Sampled peak server RSS | 1466.4 MiB |
| Server CPU time during run | 427150.0 ms |
| First call wall / server CPU | 20536.4 / 51950.0 ms |
| Steady wall p50 / p95 | 10842.6 / 14664.0 ms |
| Steady server CPU mean | 37518.0 ms/call |
| Von advice / fallback calls | 0 / 11 |

| Call | Phase | Wall ms | Server CPU ms | Server RSS MiB | Advice | Reason |
| ---: | --- | ---: | ---: | ---: | --- | --- |
| 1 | first warm call | 20536.4 | 51950.0 | 1379.3 | schema-statistics | von-uncertain-or-abstained |
| 2 | steady | 14558.8 | 42360.0 | 1396.6 | schema-statistics | von-uncertain-or-abstained |
| 3 | steady | 14664.0 | 43370.0 | 1419.3 | schema-statistics | von-uncertain-or-abstained |
| 4 | steady | 12133.3 | 41010.0 | 1451.4 | schema-statistics | von-uncertain-or-abstained |
| 5 | steady | 9917.4 | 33500.0 | 1465.5 | schema-statistics | von-uncertain-or-abstained |
| 6 | steady | 10998.1 | 36660.0 | 1465.1 | schema-statistics | von-uncertain-or-abstained |
| 7 | steady | 11287.1 | 38290.0 | 1465.2 | schema-statistics | von-uncertain-or-abstained |
| 8 | steady | 10171.9 | 35500.0 | 1466.1 | schema-statistics | von-uncertain-or-abstained |
| 9 | steady | 9724.2 | 34560.0 | 1466.1 | schema-statistics | von-uncertain-or-abstained |
| 10 | steady | 9344.8 | 33100.0 | 1466.3 | schema-statistics | von-uncertain-or-abstained |
| 11 | steady | 10842.6 | 36830.0 | 1466.4 | schema-statistics | von-uncertain-or-abstained |

CPU is cumulative process CPU time, so a 4-thread inference can consume more CPU milliseconds than wall milliseconds. RSS is the server process working set; the peak is sampled every 20 ms and may miss a shorter spike. This measures the current CPU host and this two-candidate advisor request, not throughput of the full database.

The same process reported `VmHWM=4053324 kB` and `VmSwap=2633284 kB` in `/proc/2131099/status` after this run. Heavy swapping explains why the warm run's final RSS is lower than the earlier sampled 3966.2 MiB peak; it does not show that the loaded model needs only 1.5 GiB of memory.
