# Query-independent instrumentation: runtime-order paired trials

Configuration: 2,000 primary documents, 100 seeded maintenance documents, 100 post-build updates, 30 repeat samples, and `MaximumIndexes=2`.
Three paired fresh-process trials. Cold lanes omit `--index-perf`; diagnostic lanes use `--index-perf`. Diagnostic first-query cells are suppressed because probes warm the process.

Subject: `/tmp/terrabase-indexing-mirror-fixed-20260923T013001Z` commit `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6`, tree `6ac4eeb26a369bf87e645158802da3873f6dca23`, working-tree digest `2a27753aa2a328d3bed7066b34ca4f04ec84876626e99adc9edb056484d2764d`.
The runtime-order set includes `AutomaticIndexRuntime.cs` SHA-256 `14652b784401f132f7003255a95c2e05c7f70f327e9bd072c5f2091c404d29db`. Patch diff SHA-256: `1c0336c11e35922bd2bfdc98fa076a6ebf49faaa913befd8546411c1c8e9dc12`.
The prior mirror-fixed set used working-tree digest `785978ada2252da5d151b9aefff81ddae0c68a6a494b51340663cb94f1362d29`; the digest changed because the latest runtime overlay was applied before this set.

| Metric | status | profile.segment | status vs clean | profile.segment vs clean | status vs prior mirror-fixed | profile.segment vs prior mirror-fixed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Cold first query µs | median 767888.6; range 748677.9–845647.8 | median 1796.1; range 1725.1–1999.3 | -13.3% | +17.1% | -6.7% | -0.8% |
| Cold repeat p50 µs | median 29.1; range 28.6–29.1 | median 27.3; range 26.9–27.5 | +29.9% | +38.6% | +2.8% | -1.8% |
| Cold repeat p95 µs | median 49.3; range 47.5–52.7 | median 43.4; range 42.9–47.3 | +8.6% | -3.3% | -2.6% | -2.3% |
| Lifecycle build ms | median 1628.2; range 1569.1–1637.8 | median 1159.4; range 1145.7–1178.1 | -84.6% | -88.6% | -8.2% | -5.4% |
| Catch-up + index probe ms | median 868.4; range 787.5–926.2 | median 2.4; range 1.4–2.5 | +13.2% | +200.0% | +7.2% | +41.2% |

Repeat index service: status 100%; profile.segment 100%. Result coverage: status True; profile.segment True.
All six run receipts completed with exit code 0. The indexed cold rows examined 42 results on the first and repeat calls; catch-up probes examined 42 rows with 43 probe work units.

Compared with the clean three-trial baseline, runtime-order cold repeat p50 is +29.9% for `status` (29.1 µs vs 22.4 µs) and +38.6% for `profile.segment` (27.3 µs vs 19.7 µs).
The prior mirror-fixed comparison uses the preceding 013721Z receipt set and is qualified by the working-tree digest change above. These are paired observations and do not establish causality.

## Trials

| Trial | Cold receipt | Diagnostic receipt |
| ---: | --- | --- |
| 1 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T014427Z-runtime-order/trial-1-cold/run-receipt.json` (`298aa53cb2574628546f038e29d08e2ea4fcbed8069b90d0ea67189d02a2ad17`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T014427Z-runtime-order/trial-1-diagnostic/run-receipt.json` (`6240a0fc927a6a8e8d819d143aab8284ec809d3d9f38bac766a1814e4b5b17b9`) |
| 2 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T014427Z-runtime-order/trial-2-cold/run-receipt.json` (`5fd85af9b58f3123ff9e9e8a9a7f0e65fa1659ebd58feb2760eb076b17e88f5b`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T014427Z-runtime-order/trial-2-diagnostic/run-receipt.json` (`80b45642b3889be5c62e864c281de9490c18f2a6fc601b6f8b92f5d2abbe3823`) |
| 3 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T014427Z-runtime-order/trial-3-cold/run-receipt.json` (`65859078abf6090976ee4b2893b093898a28840379e491f80718157839b538e4`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T014427Z-runtime-order/trial-3-diagnostic/run-receipt.json` (`bf48e7eb21d7deb23e8c8f7b6af0b7b1abd5f90238e69771baab095481bc438d`) |

Raw Markdown and console logs remain beside each receipt in the trial directories. The JSON summary contains per-trial values, ranges, provenance, and both comparison sets.
