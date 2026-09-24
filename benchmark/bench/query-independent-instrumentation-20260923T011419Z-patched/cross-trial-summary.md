# Query-independent instrumentation: patched indexing subject

Configuration: 2,000 primary documents, 100 seeded maintenance documents, 100 post-build updates, 30 repeat samples, and `MaximumIndexes=2`.

Each trial is a paired fresh process and fresh TerraBase root. The cold lane omits `--index-perf`; its first query is measured immediately after maintenance. The diagnostic lane uses `--index-perf`; its post-probe first-query cells are suppressed and it supplies per-index lifecycle build and catch-up plus one physical index probe.

Patched subject provenance: commit `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6`, base tree `6ac4eeb26a369bf87e645158802da3873f6dca23`, dirty `True`, working-tree digest `6ed430b0d29e769a2b1a868febe6bce042b7e3c76c5e0247f7bfd794338d27e5.
Patch capture: [patched-indexing.diff](../query-independent-instrumentation-20260923T011204Z/patched-subject-prep/patched-indexing.diff), SHA-256 `a606a65592f0ebf2c6417e87943e4c17a91e5288f10d1db571a158528f346084`. The overlay contains six indexing files only; per-file hashes are in [source-manifest.txt](../query-independent-instrumentation-20260923T011204Z/patched-subject-prep/source-manifest.txt).

| Metric | status | profile.segment | status vs clean median | profile.segment vs clean median |
| --- | ---: | ---: | ---: | ---: |
| Cold first query µs | median 814773.8; range 742073.1–837810.7 | median 1708.8; range 1676.9–2300.5 | -8.1% | +11.4% |
| Cold repeat p50 µs | median 263.5; range 256.4–268.1 | median 295.4; range 292.4–296.1 | +1076.3% | +1399.5% |
| Cold repeat p95 µs | median 412.0; range 295.4–486.0 | median 338.2; range 318.1–351.8 | +807.5% | +653.2% |
| Lifecycle build ms | median 1496.0; range 1494.1–1544.8 | median 1074.9; range 1071.8–1082.7 | -85.9% | -89.5% |
| Lifecycle build CPU ms | median 1532.6; range 1494.7–1640.2 | median 1371.2; range 1367.4–1430.1 | -66.3% | -49.6% |
| Catch-up + index probe ms | median 835.1; range 708.7–846.1 | median 1.5; range 1.5–1.7 | +8.9% | +87.5% |
| Catch-up + index probe CPU ms | median 791.6; range 654.2–802.0 | median 1.5; range 1.5–1.7 | +20.1% | +66.7% |
| Probe rows examined | median 42.0; range 42.0–42.0 | median 42.0; range 42.0–42.0 | +0.0% | +0.0% |
| Probe work units | median 44.0; range 44.0–44.0 | median 44.0; range 44.0–44.0 | n/a | n/a |

Result coverage was `True` for every TerraBase evaluation row and all six receipts completed with exit code 0. Percentage deltas compare medians with the preserved clean three-trial summary; they are observations for this paired run and do not establish causality or speedup. Catch-up timings include the required one-index physical probe and are not pure drain-only timings. CPU, RSS, and allocation values are process-wide diagnostics; storage values are logical file lengths.

## Receipts

| Trial | Cold receipt | Diagnostic receipt |
| ---: | --- | --- |
| 1 | `ts-benchmark/bench/query-independent-instrumentation-20260923T011419Z-patched/trial-1-cold/run-receipt.json` (`cf995dd3ec4cfe53997c6dd3f02351642f7feb3fe03c93499e21aba56047ff7c`) | `ts-benchmark/bench/query-independent-instrumentation-20260923T011419Z-patched/trial-1-diagnostic/run-receipt.json` (`832c5e57a77a66e449a5c45b1277864e603ec2f112021748c2af961644b8ebeb`) |
| 2 | `ts-benchmark/bench/query-independent-instrumentation-20260923T011419Z-patched/trial-2-cold/run-receipt.json` (`7886f5eb6af38474ca7770b74952023a200ce8c230b9cd2e2571be3b3d5b73b9`) | `ts-benchmark/bench/query-independent-instrumentation-20260923T011419Z-patched/trial-2-diagnostic/run-receipt.json` (`124723d76e4acc52d97dbadd1ec6ca753a6d23e85a11345ac353af7c324b7cc6`) |
| 3 | `ts-benchmark/bench/query-independent-instrumentation-20260923T011419Z-patched/trial-3-cold/run-receipt.json` (`24a8f83f507a8dde5a38c41ebd6d59a6df4c417ed7d99d28251c0bd0b9add66a`) | `ts-benchmark/bench/query-independent-instrumentation-20260923T011419Z-patched/trial-3-diagnostic/run-receipt.json` (`9e2bdee1532672bade37dd71b4cdc3b3d0036b9015775dc664d543398595b930`) |

Raw Markdown and console logs remain beside each receipt.
