# Query-independent instrumentation: mirror-fixed indexing subject

Configuration: 2,000 primary documents, 100 seeded maintenance documents, 100 post-build updates, 30 repeat samples, and `MaximumIndexes=2`.
Three paired fresh-process trials. Cold lanes omit `--index-perf`; diagnostic lanes use `--index-perf`. Diagnostic first-query cells are suppressed because probes warm the process.

Subject: `/tmp/terrabase-indexing-mirror-fixed-20260923T013001Z` commit `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6`, tree `6ac4eeb26a369bf87e645158802da3873f6dca23`, working-tree digest `785978ada2252da5d151b9aefff81ddae0c68a6a494b51340663cb94f1362d29`.
Patch diff SHA-256: `5b25a39aae3e8cec0a1ef2838f9a800a5a42334c6daa1f55a2624452fd0aaa9c`. The prior failed pre-mirror results remain in `query-independent-instrumentation-20260923T011419Z-patched`.

| Metric | status | profile.segment | status vs clean | profile.segment vs clean |
| --- | ---: | ---: | ---: | ---: |
| Cold first query µs | median 823383.2; range 782562.6–952847.2 | median 1810.2; range 1691.7–1820.3 | -7.1% | +18.0% |
| Cold repeat p50 µs | median 28.3; range 28.3–29.7 | median 27.8; range 27.0–28.4 | +26.3% | +41.1% |
| Cold repeat p95 µs | median 50.6; range 47.8–53.8 | median 44.4; range 40.6–44.5 | +11.5% | -1.1% |
| Lifecycle build ms | 1773.1 (range 1638.2–2878.2) | 1225.4 (range 1115.9–1757.2) | — | — |
| Catch-up + index probe ms | 809.7 (range 741.9–1015.2) | 1.7 (range 1.4–2.3) | — | — |

Repeat index service: status 100%; profile.segment 100%. Result coverage: status True; profile.segment True.
All six run receipts completed with exit code 0. No additional trials were run after this set.

Compared with the preserved clean three-trial baseline, mirror-fixed cold repeat p50 is +26.3% for `status` (28.3 µs vs 22.4 µs) and +41.1% for `profile.segment` (27.8 µs vs 19.7 µs). This is the remaining repeat-latency difference; the pre-mirror values were 263.5 µs and 295.4 µs. Repeat p95 is +11.5% and -1.1%, respectively. Cold first query is -7.1% for `status` and +18.0% for `profile.segment`. Lifecycle build is -83.2% and -88.0% versus clean; catch-up plus probe is +5.6% and +112.5% (the latter compares 1.7 ms with a 0.8 ms clean median). These are paired observations and do not establish causality.

## Trials

| Trial | Cold receipt | Diagnostic receipt |
| ---: | --- | --- |
| 1 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T013721Z-mirror-fixed/trial-1-cold/run-receipt.json` (`cce289612a2adc30cf144f3a74cd0028ca2ee21a02d5772df73d3bf9e5024578`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T013721Z-mirror-fixed/trial-1-diagnostic/run-receipt.json` (`bafeed7b85e299cebe6b5a58f57d5ebad4175f4668d4b35eb8c9c9b87a4f0e51`) |
| 2 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T013721Z-mirror-fixed/trial-2-cold/run-receipt.json` (`bbc2328a8389ab45cb794489fa6850c63e2d8b2fa64214b7876f73d37d0be33d`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T013721Z-mirror-fixed/trial-2-diagnostic/run-receipt.json` (`9255e10fa8613145f953a873596ad4f25709a9b20ecde9e202a9a21c19f3a097`) |
| 3 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T013721Z-mirror-fixed/trial-3-cold/run-receipt.json` (`8f7f1215e5495f6bc70f3ea5cc5a5e19c9278b415e427229b3b8c69f6f8ce7ec`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-instrumentation-20260923T013721Z-mirror-fixed/trial-3-diagnostic/run-receipt.json` (`b9362524fdcaba3c9de3d936e4a7be63197a687bb38fbd320bd4de28e108e4e9`) |
