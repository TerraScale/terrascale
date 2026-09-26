# Von local index advisor resource benchmark

Date: 2026-09-24T01:57:07.6333805+00:00; Von source: wfzyx/von 946d4eb95071982ec0b67f8c762759c04c4080ff; model: von-1.2.0; server PID: 2131099; requests: 1 cold + 10 steady; cadence: 1.1 s; OMP_NUM_THREADS=4; device=CPU. Each request has two candidate fields and five questions. Each request uses a different schema version to avoid advisor cache hits.

| Measure | Value |
| --- | ---: |
| Idle server RSS | 71.0 MiB |
| Final server RSS | 3864.8 MiB |
| Sampled peak server RSS | 3966.2 MiB |
| Server CPU time during run | 416650.0 ms |
| Cold call wall / server CPU | 30100.2 / 17620.0 ms |
| Steady wall p50 / p95 | 9789.3 / 30002.6 ms |
| Steady server CPU mean | 38748.0 ms/call |
| Von advice / fallback calls | 0 / 11 |

| Call | Phase | Wall ms | Server CPU ms | Server RSS MiB | Advice | Reason |
| ---: | --- | ---: | ---: | ---: | --- | --- |
| 1 | cold model load | 30100.2 | 17620.0 | 556.2 | schema-statistics | von-timeout |
| 2 | steady | 30002.6 | 10580.0 | 1576.0 | schema-statistics | von-timeout |
| 3 | steady | 30001.4 | 99590.0 | 3818.5 | schema-statistics | von-timeout |
| 4 | steady | 7938.1 | 38090.0 | 3856.1 | schema-statistics | von-uncertain-or-abstained |
| 5 | steady | 6849.3 | 25660.0 | 3856.0 | schema-statistics | von-uncertain-or-abstained |
| 6 | steady | 10553.4 | 37010.0 | 3858.8 | schema-statistics | von-uncertain-or-abstained |
| 7 | steady | 9230.1 | 32520.0 | 3861.8 | schema-statistics | von-uncertain-or-abstained |
| 8 | steady | 10861.6 | 40540.0 | 3864.8 | schema-statistics | von-uncertain-or-abstained |
| 9 | steady | 9789.3 | 35030.0 | 3864.8 | schema-statistics | von-uncertain-or-abstained |
| 10 | steady | 10048.1 | 35790.0 | 3864.8 | schema-statistics | von-uncertain-or-abstained |
| 11 | steady | 9282.6 | 32670.0 | 3864.8 | schema-statistics | von-uncertain-or-abstained |

CPU is cumulative process CPU time, so a 4-thread inference can consume more CPU milliseconds than wall milliseconds. RSS is the server process working set; the peak is sampled every 20 ms and may miss a shorter spike. This measures the current CPU host and this two-candidate advisor request, not throughput of the full database.
