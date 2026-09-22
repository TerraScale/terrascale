# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-22T23:34:49.6736159+00:00

1,000 deterministic random documents written in batches of 100, verified by exact read-back and point reads, backed up (full + incremental around 20 updates and 20 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 842 ms | 1,187 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | related sets, traversal, restrict delete |
| Read-back verify | 74 ms | 1,000 exact matches |
| Point reads | 4 ms | 28,758 ops/s, p50 0.027 ms, p95 0.050 ms, p99 0.456 ms |
| Full backup | 896 ms | artifact 111,436 B |
| Bulk updates | 6 ms | 20 documents to v2 |
| Bulk deletes | 7 ms | 20 tombstones |
| Incremental backup | 871 ms | artifact 43,179 B |
| Full-boundary restore | 606 ms | 0 log entries |
| Full restore verify | 117 ms | 1,000 exact matches |
| PITR restore | 657 ms | 1 log entries |
| PITR restore verify | 240 ms | 1,000 exact matches |
| Final restore | 779 ms | full incremental chain |
| Final restore verify | 106 ms | 980 exact, 20 absent |
| Index build | 1,340 ms | automatic index on sequence, active |
| Indexed queries | 797 ms | 43 ops/s, p50 6.769 ms, p95 265.097 ms, p99 265.097 ms |
| Compaction | 93 ms | 1,001 records, 20 tombstones |
| Compaction verify | 218 ms | 980 exact matches |

Full artifact plaintext SHA-256: `a262a6832f064f5170a36c5dab04bb54331c2e51d870664d2ba63b9ecfcafd92`
Incremental artifact plaintext SHA-256: `f5b363638fe4ff5c9c11c0616aece78439a8bcef2e7df568d1313b771998c17a`