# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-22T22:59:04.1154860+00:00

10,000 deterministic random documents written in batches of 256, verified by exact read-back and point reads, backed up (full + incremental around 100 updates and 100 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,835 ms | 5,448 docs/s |
| Relationship write | 270 ms | 100 orders + users through the schema store |
| Relationship verify | 2,165 ms | related sets, traversal, restrict delete |
| Read-back verify | 1,397 ms | 10,000 exact matches |
| Point reads | 38 ms | 35,540 ops/s, p50 0.016 ms, p95 0.037 ms, p99 0.335 ms |
| Full backup | 1,807 ms | artifact 1,102,777 B |
| Bulk updates | 7 ms | 100 documents to v2 |
| Bulk deletes | 20 ms | 100 tombstones |
| Incremental backup | 1,761 ms | artifact 207,655 B |
| Full-boundary restore | 1,175 ms | 0 log entries |
| Full restore verify | 389 ms | 10,000 exact matches |
| PITR restore | 841 ms | 1 log entries |
| PITR restore verify | 707 ms | 10,000 exact matches |
| Final restore | 862 ms | full incremental chain |
| Final restore verify | 284 ms | 9,900 exact, 100 absent |
| Index build | 3,628 ms | automatic index on sequence, active |
| Indexed queries | 1,196 ms | 135 ops/s, p50 4.102 ms, p95 14.558 ms, p99 232.979 ms |
| Compaction | 266 ms | 10,326 records, 100 tombstones |
| Compaction verify | 546 ms | 9,900 exact matches |

Full artifact plaintext SHA-256: `638398d5011e947d9141efe15323a243d4923ae83f23259e3f95e5e40d85fda1`
Incremental artifact plaintext SHA-256: `5dca55ec92ad0c6963b0e02c8207f5770f2d60d65622c9e0732d089ea229096f`