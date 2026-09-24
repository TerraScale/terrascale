# First-repeat query trace diagnostic

Generated 2026-09-24. The traced run completed at 2026-09-24T05:39:45Z with exit code 0. Its output is in [`run-traced`](run-traced/), with the raw benchmark receipt in [`query-independent-indexing-receipt.json`](run-traced/query-independent-indexing-receipt.json) and execution metadata in [`run-receipt.json`](run-traced/run-receipt.json).

## Build and input identity

- Workload: 2,000 documents, 10 repeat samples, two-index maximum, 100 post-build updates, 8 MiB physical index budget, with a Von attempt.
- TerraBase source commit: `47b235dcc78240a9eb5330e454a7bf3273ffcba9`; dirty working-tree digest: `c78d2006812349a45d7cca7dc623e6b600027ca3060ebde499018c069f8e9f87`.
- Diagnostic runner, TerraBase, and ZoneTree assembly SHA-256 values: `a90c98a989609c512ddb15b876b147e27990af68cc5f9bbea5152478bec07dcf`, `b592043c1b66df17d9c8e6c4c365df96aeaf5dfba084826bb9028c7c4dc92954`, and `79e8f6480e98b0cc162fb3b3474b5fa9434b68fc7483d7453ff33b54ede06af2` respectively.
- The runner was rebuilt from the dirty working tree with 0 warnings and 0 errors. Its generated dependency manifest omitted the TerraBase runtime reference, so the diagnostic copy's deps graph was repaired. No TerraBase source was edited for this trace.
- The held-out benchmark snapshot loaded TerraBase assembly `2bb2f209fa4158132aa446f4168b4516d4540b1d51cc571c9324949c5cd06bbc`, which lacks `IndexQueryDiagnostics`. Treat this trace as evidence about phase mechanisms, not as a same-binary latency comparison with the held-out run.

## Observations

The earlier uninstrumented run recorded a deterministic status first-repeat latency of **978.382 ms**. That runner was stale for the diagnostic fields. In the rebuilt traced run, the deterministic status first repeat was **17.151 ms**; the Von-attempt lane's first status query took **894.545 ms**, while its first repeat took **0.198 ms**. The first-query listener was not active for that slow initial query.

| Traced status repeat | Query access | Outer query span | Captured phase spans |
| --- | --- | ---: | --- |
| Deterministic schema statistics | Equality seek; no snapshot fallback | 17.114 ms | admission 0.003 ms; planner explain 1.192 ms; selected execute 8.915 ms; physical point seek 8.420 ms; index inspections 0.066, 0.027, 0.033, and 0.026 ms; observation publish 0.013 ms |
| Deterministic fallback after Von attempt | Equality seek; no snapshot fallback | 0.185 ms | admission 0.003 ms; observation publish 0.014 ms |

For the deterministic traced repeat, the source commit sequence remained 22 before and after. Both visible index records reported applied and target position 21, while their physical store commit watermark was 22. These values were also unchanged across the repeat. The Von lane used deterministic fallback after the inference attempt; no Von recommendation was accepted.

## Attribution limits

**Direct phase attribution for the slow event is unverified.** The 894.545 ms initial status query was outside the listener window, and the earlier 978.382 ms repeat came from an uninstrumented runner. Therefore the fast traced point seek cannot explain or rule out the slow event. The available spans time query admission, planner and physical query work, but do not separately time background `ApplyFramesAsync` or physical store-gate wait. Query-driven catch-up is visible in `physical.stale_tail.drain` only when that path runs. No further performance run was needed for this diagnostic; a direct attribution would require capturing spans around the slow initial query and separately instrumenting the missing waits.
