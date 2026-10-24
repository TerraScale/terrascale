# Query insights: explain and slow query shapes

## Explain

`POST /v1/projects/{project}/databases/{database}/collections/{collection}/query` with `"explain": true` runs the
query as usual (bypassing the result cache) and adds:

```json
"explain": {
  "plan": ["SCAN items USING INDEX sqlite_autoindex_items_1", "CORRELATED SCALAR SUBQUERY 1", "SEARCH s USING INDEX …"],
  "indexes": [], "full_scan": true, "rows_scanned": 100, "work_units": 2400, "elapsed_ms": 9.4,
  "suggested_index": ["status"]
}
```

- `plan` holds the engine's query plan steps; `indexes` the TerraBase indexes it uses.
- `full_scan` is true when a step walks a whole table or index (`SCAN …`), including the id-order walk; seeks show
  as `SEARCH … USING INDEX tb_index_<name> (…)`.
- `rows_scanned` counts candidate documents visited; `work_units` is what the query charged against the per-query
  budget (5 million on the default plan; beyond it, 400 `invalid_query`, query too expensive).
- On a full scan, `suggested_index` names one filtered field (the first top-level equality, else range) in the
  `fields` form of `POST …/indexes`. One field: the engine's planner seeks a single-key expression index, while it
  keeps walking the id order for a compound one (measured in `QueryExplainTests`).
- `explain` also works with `aggregate`, `count` and `near`; `"explain": false` is the same as leaving it out.

## Slow query shapes

`GET /v1/projects/{project}/databases/{database}/query-shapes` items (see [query-shapes.md](query-shapes.md)) also
carry `latencyBuckets` (20 power-of-two millisecond buckets: under 1 ms, under 2^i ms for i = 1 to 18, the rest) and
`p95LatencyMs`: the upper bound of the bucket holding the 95th percentile, at most `maxLatencyMs`. Statistics
recorded before the buckets existed have neither. The list is cumulative per shape; a 24-hour view filters on
`lastSeenAt`. Index suggestions come from `explain`, not from the list.
