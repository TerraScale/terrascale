# Execution brief: reduce origin sequence allocation scan

Observed candidate: `src/storage/sqlite/mod.rs:2472-2481` allocates the next
local origin sequence with one `MAX` over a `UNION ALL` of raw rows from
`documents`, `origin_prefixes`, and `replication_publications`. The existing
indexes already cover the lookup keys: `documents_origin_frontier` on
`(tenant, origin, origin_seq, hlc)` (`mod.rs:1068-1072`), the origin-prefix
WITHOUT ROWID primary key `(tenant, origin)` with `seq` payload
(`mod.rs:1203-1208`), and the replication-publication WITHOUT ROWID primary key
`(tenant, origin, origin_seq)` (`mod.rs:951-963`).

Bounded implementation: replace the raw-row union with one scalar aggregate per
table, then aggregate those at most three rows:

```sql
SELECT COALESCE(MAX(seq), 0) + 1 FROM (
  SELECT MAX(origin_seq) AS seq FROM documents WHERE origin = ?1 AND tenant = ?2
  UNION ALL SELECT MAX(seq) FROM origin_prefixes WHERE origin = ?1 AND tenant = ?2
  UNION ALL SELECT MAX(origin_seq) FROM replication_publications WHERE origin = ?1 AND tenant = ?2
)
```

Each aggregate always contributes one row; outer `MAX` ignores NULLs, and
`COALESCE` preserves the old empty-table result of sequence 1. Do not alter
sequence authority, replication provenance, retry identity, or add a cached
counter. The transaction remains the authority and all three tables remain in
the same snapshot/transaction.

Acceptance: existing origin allocation and migration coverage in
`src/storage/sqlite/tests.rs:419-447` and causal-frontier coverage in
`src/storage/sqlite/causal_frontier_tests.rs` still prove first sequence,
restart/backfill, and independent origin progression. Add a discriminating
test only if current coverage cannot demonstrate that the maximum is selected
across all three sources and empty sources remain valid. A tiny EXPLAIN QUERY
PLAN/VM probe may compare the old and proposed SQL on a throwaway database,
outside the shared load lane; preserve it as evidence only, never as a
benchmark result. Bench_owner owns any real write-lane comparison after the
source-frozen build.

Forbidden shortcuts: no global history cache, no removal of a source table,
no unverified index claim, no weakened overflow conversion, and no concurrent
Cargo build against the shared target. Any changed source must be rebuilt from
the coherent frozen snapshot and receive the normal release artifact digest.
