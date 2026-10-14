# 21. Slice plan: R2b as built

**R2b as built** (2026-10-12):

- Both query compilers (the document query API's and the SQL subset's) read values with `json_extract(doc, '$.path')`
  only, never `->>`. A document-query path that is a valid field is embedded as that literal, so it matches the
  index expression; other paths stay bound parameters and cannot use an index. `json_type` stays as a residual
  type guard: `json_extract` alone cannot tell `true` from `1`, `null` from a missing member, or a string from an
  object. It does not stop the index seek. `json_each` remains for `contains` and long `in` lists.
- `IN` lists compile to equalities joined by a balanced `OR` tree (a `MULTI-INDEX OR` plan). A document-query `in`
  of more than 64 values stays one JSON-array parameter read with `json_each` (a scan), because a filter of 256 such
  lists would pass SQLite's 32766 bound parameters. A document-query
  `prefix` and the SQL subset's new `LIKE 'prefix%'` (case-sensitive, prefix patterns only) compile to the range
  `>= prefix AND < successor` (UTF-8 byte order). A scan budget is a plain `AND` term, so it no longer hides the
  filter from the planner.
- `IndexPlanTests` (Fast) checks with `EXPLAIN QUERY PLAN` that an index on the field serves equality, a range, `IN`
  and a prefix in both compilers (with and without a scan budget), and `ORDER BY … LIMIT` in the SQL subset, and
  that the rewritten filters return the same rows.
- A document-query sort orders by JSON type first (missing < null < booleans < numbers < strings < containers), so it
  still sorts in a temporary B-tree; only the SQL subset's `ORDER BY` reads an index.
- The unique-constraint tables are `_tb_unique_keys`, `_tb_unique_invalid` and `_tb_unique_conflicts`, rowid tables
  with `UNIQUE` keys, so `unique_*` names are free for collections.
- `_rev` stays unindexable: versions live in `_tb_state`, not in the collection's table, so no expression index on
  the table can key them.
