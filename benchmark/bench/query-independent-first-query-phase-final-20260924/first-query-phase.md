# Held-out status first-query phase probe

Same deterministic status candidate, 2,000 plus 100 seeded documents, fresh root per row, empty query history before execution, and exact result IDs in the raw receipt.

| Updates | Lane | First query ms | Index inspections ms | Pure planner ms | Direct index probe ms | Point ms | Range ms | Iterator ms | Source fallback ms | Rows | Access | Result IDs | Observation delta |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| 0 | cold-first-query | 78.267 | 0.000 | — | — | — | — | — | — | 42 | EqualitySeek | 21 | 1 |
| 0 | diagnostic-boundaries | 1.000 | 2.256 | 0.840 | 6.296 | 0.895 | — | — | — | 42 | EqualitySeek | 21 | 1 |
| 100 | cold-first-query | 62.633 | 0.000 | — | — | — | — | — | 26.792 | 2100 | Scan | 21 | 1 |
| 100 | diagnostic-boundaries | 0.637 | 633.224 | 0.398 | 0.761 | 0.379 | — | — | — | 42 | EqualitySeek | 21 | 1 |

The cold lane executes the held-out predicate before any diagnostic probe. The diagnostic lane times physical inspection, a standalone planner Explain with the current source cut, a direct index probe, and the point/range/iterator branch attempts in that order on another freshly built equivalent root. The direct probe includes any index catch-up plus physical execution. Only eligible physical branches execute; a range or iterator dash means a prior branch served the query. Its following full query is warmed by those probes. These are boundary measurements, not additive spans inside the cold first query. Observation publication has no external timing hook; only its count delta is recorded. CPU and allocation metrics are process-wide, so concurrent work can contribute. No production code is instrumented.

Raw receipt SHA-256: `40174224d0554165056680c72a9bd3e8020688f5aea01eb7aab9aa279560285a`. The raw receipt includes exact IDs, source commit and working-tree digests; `run-receipt.json` records runner provenance and artifact digests.
