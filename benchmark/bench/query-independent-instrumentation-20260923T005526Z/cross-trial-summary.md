# Query-independent instrumentation baseline: paired three-trial summary

Configuration: 2,000 primary documents, 100 seeded maintenance documents, 100 post-build updates, 30 repeat samples, and `MaximumIndexes=2`.

Each trial is a paired fresh process and fresh TerraBase root. The cold lane omits `--index-perf`; its first query is measured immediately after post-build maintenance. The diagnostic lane uses `--index-perf`; it reports per-index lifecycle build and post-build catch-up plus one physical index probe. Diagnostic-lane first-query values are warmed after those probes and are excluded from the cold-query summary.

Subject provenance: commit `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6`, tree `6ac4eeb26a369bf87e645158802da3873f6dca23`, dirty `False`; repository `/tmp/terrabase-benchmark-subject-7e5a1851`.

| Metric | status | profile.segment |
| --- | ---: | ---: |
| Cold first query µs | median 886189.9; range 855248.0–975383.6 | median 1533.7; range 1293.9–1844.1 |
| Cold repeat p50 µs | median 22.4; range 20.0–24.6 | median 19.7; range 18.6–19.7 |
| Cold repeat p95 µs | median 45.4; range 45.3–63.2 | median 44.9; range 36.1–48.7 |
| Lifecycle build ms | median 10575.4; range 10376.4–11327.4 | median 10195.6; range 9920.4–10493.2 |
| Lifecycle build CPU ms | median 4550.2; range 4476.2–4691.9 | median 2720.0; range 2650.9–3012.0 |
| Catch-up + index probe ms | median 766.9; range 700.2–1005.0 | median 0.8; range 0.8–0.9 |
| Catch-up + index probe CPU ms | median 658.9; range 591.7–933.8 | median 0.9; range 0.8–1.7 |
| Probe rows examined | median 42.0; range 42.0–42.0 | median 42.0; range 42.0–42.0 |

The catch-up timings include the required one-index physical probe, so they are diagnostic drain-plus-probe timings rather than pure drain-only timings. CPU, RSS, and allocation deltas are process-wide diagnostics. File sizes are logical `FileInfo.Length` sums, not physical block I/O. These three trials are independent seeded repetitions for this configuration; no speedup or causal claim is made.

## Receipts

| Trial | Cold receipt | Diagnostic receipt |
| ---: | --- | --- |
| 1 | `ts-benchmark/bench/query-independent-instrumentation-20260923T005526Z/trial-1-cold/run-receipt.json` (`064a10a446dd10de8e968c3c08da421acc3f42330b8413e02b1a28e6cced362c`) | `ts-benchmark/bench/query-independent-instrumentation-20260923T005526Z/trial-1-diagnostic/run-receipt.json` (`257f8a85b91b31755c5b93d5a0e001a73ada66251196f001eae9ace7bbc74029`) |
| 2 | `ts-benchmark/bench/query-independent-instrumentation-20260923T005526Z/trial-2-cold/run-receipt.json` (`957081e5830c1a39fe590b0acd8306230712ff3ad2e4eba513ca7919a9f43401`) | `ts-benchmark/bench/query-independent-instrumentation-20260923T005526Z/trial-2-diagnostic/run-receipt.json` (`3b0fc2dc93950f23ea532ca5635aa1d47e2e48c1e2f29f4342d4cad910196378`) |
| 3 | `ts-benchmark/bench/query-independent-instrumentation-20260923T005526Z/trial-3-cold/run-receipt.json` (`8132f89ebed8bfa1e1ffe8c7043c601f64f7193c923d150c7331f6c33e1761e7`) | `ts-benchmark/bench/query-independent-instrumentation-20260923T005526Z/trial-3-diagnostic/run-receipt.json` (`cab3e397cac158e39445809b79d83e799b383d09cb737ca6d10db3303a0faa67`) |

Raw Markdown and console logs remain beside each receipt in the trial directories. Every receipt records the same clean subject provenance above.
