# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-22T17:03:43.0750879+00:00

100,000 deterministic random documents written in batches of 5,000, verified by exact read-back and point reads, backed up (full + incremental around 1,000 updates and 1,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 4,352 ms | 22,979 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | related sets, traversal, restrict delete |
| Read-back verify | 1,253 ms | 100,000 exact matches |
| Point reads | 114 ms | 707,499 ops/s, p50 0.001 ms, p95 0.002 ms, p99 0.005 ms |
| Full backup | 4,254 ms | artifact 10,982,163 B |
| Bulk updates | 28 ms | 1,000 documents to v2 |
| Bulk deletes | 17 ms | 1,000 tombstones |
| Incremental backup | 3,704 ms | artifact 2,067,971 B |
| Full-boundary restore | 4,100 ms | 0 log entries |
| Full restore verify | 833 ms | 100,000 exact matches |
| PITR restore | 3,939 ms | 1 log entries |
| PITR restore verify | 790 ms | 100,000 exact matches |
| Final restore | 3,960 ms | full incremental chain |
| Final restore verify | 745 ms | 99,000 exact, 1,000 absent |
| Index build | 13,342 ms | automatic index on sequence, active |
| Indexed queries | 1,889 ms | 65 ops/s, p50 6.085 ms, p95 16.018 ms, p99 893.388 ms |
| Compaction | 1,844 ms | 100,001 records, 1,000 tombstones |
| Compaction verify | 1,556 ms | 99,000 exact matches |

Full artifact plaintext SHA-256: `389844b56879b77b9fd470f9d8887f95d0389097adb15f2dcfa1798d1b8f8e2a`
Incremental artifact plaintext SHA-256: `9eaf38d9003a324d8618612ef136371ee3d2e7267a838da464282f2710cecf07`