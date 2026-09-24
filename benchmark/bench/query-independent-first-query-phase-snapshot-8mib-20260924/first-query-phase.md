# Held-out status first-query phase probe

Same deterministic status candidate, 2,000 plus 100 seeded documents, fresh root per row, empty query history before execution, and exact result IDs in the raw receipt.

| Updates | Lane | First query ms | Index inspections ms | Pure planner ms | Direct index probe ms | Point ms | Range ms | Iterator ms | Source fallback ms | Rows | Access | Result IDs | Observation delta |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| 0 | cold-first-query | 175.548 | 0.000 | — | — | — | — | — | — | 42 | EqualitySeek | 21 | 1 |
| 0 | diagnostic-boundaries | 1.031 | 2.419 | 0.729 | 5.643 | 0.700 | — | — | — | 42 | EqualitySeek | 21 | 1 |
| 100 | cold-first-query | 75.264 | 0.000 | — | — | — | — | — | 23.030 | 2100 | Scan | 21 | 1 |
| 100 | diagnostic-boundaries | 0.768 | 0.574 | 0.393 | 1298.458 | 0.415 | — | — | — | 42 | EqualitySeek | 21 | 1 |

The cold lane executes the held-out predicate before any diagnostic probe. The diagnostic lane times physical inspection, a standalone planner Explain, a direct index probe, and the point/range/iterator branch attempts in that order on another freshly built equivalent root. The direct probe includes any index catch-up plus physical execution. Only eligible physical branches execute; a range or iterator dash means a prior branch served the query. Its following full query is warmed by those probes. These are boundary measurements, not additive spans inside the cold first query. Observation publication has no external timing hook; only its count delta is recorded. CPU and allocation metrics are process-wide, so concurrent work can contribute. No production code is instrumented.

Raw receipt SHA-256: `a1dd9636735df79605de6f40edf060edecd347e0a09ede9ffed83ff00775987e`. The raw receipt includes exact IDs, source commit and working-tree digests; `run-receipt.json` records runner provenance and artifact digests.
