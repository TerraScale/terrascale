# FTS scale fixture diagnostic

Local debug-profile observations on 2026-09-04, using
`nice -n 19 cargo test --locked -p terrabase --lib scale_100k_fts_top_k_is_exact`.
The fixture inserts 100,000 documents in ten transactions, pumps governed
FTS backfill, and checks exact ranked matches. Times include the fixture's
ingestion and indexing, but exclude compilation.

| Implementation | Observed test time | Result |
| --- | ---: | --- |
| Tenant scan for identifier-only reads; FTS metadata scans for deletes | More than 42 minutes | Stopped before completion; 70,000 committed documents observed during ingestion |
| Indexed identifier reads; indexed FTS key map with transactional rowid deletes | 96.44 seconds | Passed: 100,000 documents and exact FTS top-k |

These are diagnostic test runs on a shared development host, without the
production resource ceiling. The older run also executed other library
tests, and the corrected run overlapped workspace Clippy. They do not
establish a controlled speedup ratio or qualify launch performance.

The identifier query explicitly selects its index because SQLite otherwise
preferred the tenant primary-key scan. The FTS key map preserves existing
rowids during an atomic migration; deletes remove the term stream in the
same transaction. Separate regression tests cover schema upgrade, query
planning, rollback, tenant isolation, and search behavior.
