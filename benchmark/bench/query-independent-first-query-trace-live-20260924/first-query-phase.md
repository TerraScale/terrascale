# Held-out status first-query phase probe

Same deterministic status candidate, 2,000 plus 100 seeded documents, fresh root per row, empty query history before execution, and exact result IDs in the raw receipt.

| Updates | Lane | First query ms | Index inspections ms | Pure planner ms | Direct index probe ms | Point ms | Range ms | Iterator ms | Source fallback ms | Rows | Access | Result IDs | Observation delta |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| 0 | cold-first-query | 82.951 | 0.000 | — | — | — | — | — | — | 42 | EqualitySeek | 21 | 1 |
| 0 | diagnostic-boundaries | 1.891 | 2.666 | 1.192 | 6.710 | 1.049 | — | — | — | 42 | EqualitySeek | 21 | 1 |
| 100 | cold-first-query | 81.408 | 0.000 | — | — | — | — | — | 44.060 | 2100 | Scan | 21 | 1 |
| 100 | diagnostic-boundaries | 1.203 | 1.435 | 681.260 | 1.631 | 0.592 | — | — | — | 42 | EqualitySeek | 21 | 1 |

The cold lane executes the held-out predicate before any diagnostic probe. The diagnostic lane times physical inspection, a standalone planner Explain with the current source cut, a direct index probe, and the point/range/iterator branch attempts in that order on another freshly built equivalent root. The direct probe includes any index catch-up plus physical execution. Only eligible physical branches execute; a range or iterator dash means a prior branch served the query. Its following full query is warmed by those probes. These boundary measurements are not additive spans inside the cold first query. CPU and allocation metrics are process-wide, so concurrent work can contribute.

## Real first-query ActivitySource spans

Enabled by `TERRABASE_FIRST_QUERY_TRACE=1`. These nested spans come from the same cold query; child durations are included in their parent. No tenant, document, predicate, or candidate identifiers are emitted.

| Updates | Span | Elapsed ms | Tags |
|---:|---|---:|---|
| 0 | automatic_index.query | 70.027 | query.access=EqualitySeek, query.snapshot_fallback=False, query.rows_examined=42 |
| 0 | index.inspect.initial | 0.332 | index.ordinal=0, index.visible=True |
| 0 | index.inspect.final | 0.055 | index.ordinal=0, index.visible=True |
| 0 | planner.explain | 7.639 | plan.access=EqualitySeek, plan.snapshot_fallback=False |
| 0 | planner.selected_execute | 15.559 | execution.source_selected=False, execution.access=EqualitySeek, execution.snapshot_fallback=False, execution.rows_examined=42, execution.full_document_reads=21 |
| 0 | physical.point_seek | 11.210 | physical.served=True, physical.access=EqualitySeek, physical.entry_reads=21, physical.full_document_reads=21, physical.rows_examined=42 |
| 0 | query.observation.publish | 5.956 | query.snapshot_fallback=False |
| 100 | automatic_index.query | 80.579 | query.access=Scan, query.snapshot_fallback=True, query.rows_examined=2100 |
| 100 | index.inspect.initial | 0.153 | index.ordinal=0, index.visible=True |
| 100 | index.stale_tail.estimate | 0.182 | index.ordinal=0, index.stale_at_entry=True, fallback.preferred=True |
| 100 | index.inspect.final | 0.048 | index.ordinal=0, index.visible=True |
| 100 | planner.explain | 0.153 | plan.access=Scan, plan.snapshot_fallback=True |
| 100 | planner.selected_execute | 46.864 | execution.source_selected=False, execution.access=Scan, execution.snapshot_fallback=True, execution.rows_examined=2100 |
| 100 | query.observation.publish | 0.057 | query.snapshot_fallback=True |

Raw receipt SHA-256: `169e1f0ad18d7dc4c5daed5122c326f0476811d04e9e6a920cf5c908bc63d990`. The raw receipt includes exact IDs, source commit and working-tree digests; `run-receipt.json` records runner provenance and artifact digests.
