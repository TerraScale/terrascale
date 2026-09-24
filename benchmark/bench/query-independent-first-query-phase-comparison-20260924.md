# Held-out first-query phase comparison

Both completed runs used the immutable `/tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase` subject, 2,000 primary plus 100 seeded maintenance documents, the same deterministic `status` candidate ID, an 8 MiB physical index budget, and four fresh roots per run. Each lane verified the exact same 21 result IDs, zero query observations before candidate generation and the first query, and one observation from that query. The source commit was 21 without updates and 22 after 100 post-build updates; the status index was built at commit 21.

| Run | Updates | Cold first query | Cold access / rows examined | Diagnostic index inspection | Diagnostic direct index probe | Index watermark before → after direct probe | Warm physical branch |
|---|---:|---:|---|---:|---:|---|---|
| [Initial 8 MiB](query-independent-first-query-phase-snapshot-8mib-20260924/first-query-phase.md) | 0 | 175.5 ms | EqualitySeek / 42 | 2.4 ms | 5.6 ms | 21 → 21 | point EqualitySeek, 0.7 ms |
| Initial 8 MiB | 100 | 75.3 ms | Scan / 2,100 | 0.6 ms | 1,298.5 ms | 21 → 22 | point EqualitySeek, 0.4 ms |
| [Final fenced planner](query-independent-first-query-phase-final-20260924/first-query-phase.md) | 0 | 78.3 ms | EqualitySeek / 42 | 2.3 ms | 6.3 ms | 21 → 21 | point EqualitySeek, 0.9 ms |
| Final fenced planner | 100 | 62.6 ms | Scan / 2,100 | 633.2 ms | 0.8 ms | 22 → 22 | point EqualitySeek, 0.4 ms |

The 100-update cold query selected a source scan in both runs and returned the exact IDs; the timed source executor took 23.0 and 26.8 ms respectively. The 0-update cold query used the index. The 100-update diagnostic root spent its expensive interval in different calls across runs: first in the direct index probe while the watermark advanced, then during physical inspection while a concurrent feed advanced the watermark before the direct probe. The probe boundaries therefore show that stale-tail catch-up can dominate a diagnostic call, but they cannot assign that work exclusively to physical inspection, planning, execution, or observation inside a cold query. The warm physical branch was a point equality seek; range and iterator branches did not execute for this predicate. First-call latency varied substantially between runs, so these are phase diagnostics rather than comparative performance estimates.

The independent diagnostic roots are warmed by inspection and direct probes before their full query; those timings cannot be added to a cold query or compared as identical execution paths. CPU and allocation counters in the raw receipts are process-wide. Observation publication has no external timing hook, so only its count delta is reported. The [harness freshness diagnostic](query-independent-freshness-diagnostic-20260924T0408Z/) independently found that pre-query plans and active index watermarks can differ from actual first-query access as background catch-up races the query.

The initial run's standalone planner probe did not pass the current source commit as a freshness fence. The final run corrected that probe. Planning timings from the initial receipt should not be interpreted as a current-cut plan; the table above omits them.

| Evidence | SHA-256 |
|---|---|
| [Initial raw receipt](query-independent-first-query-phase-snapshot-8mib-20260924/first-query-phase-receipt.json) | `a1dd9636735df79605de6f40edf060edecd347e0a09ede9ffed83ff00775987e` |
| [Final raw receipt](query-independent-first-query-phase-final-20260924/first-query-phase-receipt.json) | `40174224d0554165056680c72a9bd3e8020688f5aea01eb7aab9aa279560285a` |

The final [run receipt](query-independent-first-query-phase-final-20260924/run-receipt.json) records subject commit `47b235dcc78240a9eb5330e454a7bf3273ffcba9`, subject working-tree digest `489e1bc0d39ff9ccaf0ac472e1b733aa852189eef46ccc4aee74030bdd78f597`, runner assembly digest, command, toolchain, and artifact digest. A separate failed [default-budget attempt](query-independent-first-query-phase-snapshot-20260924/run-receipt.json) records a physical budget rejection during build; no query ran in that attempt. The probe now defaults to the accepted 8 MiB cap.
