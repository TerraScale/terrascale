# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-20T17:06:27.4775683+00:00

100,000 deterministic random documents written in batches of 5,000, verified by exact read-back and point reads, backed up (full + incremental around 10,000 updates and 10,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 4,404 ms | 22,707 docs/s |
| Relationship write | 1,235 ms | 10,000 orders + users through the schema store |
| Relationship verify | 65,898 ms | related sets, traversal, restrict delete |
| Read-back verify | 1,254 ms | 100,000 exact matches |
| Point reads | 437 ms | 462,812 ops/s, p50 0.001 ms, p95 0.001 ms, p99 0.002 ms |
| Full backup | 2,909 ms | artifact 11,130,413 B |
| Bulk updates | 247 ms | 10,000 documents to v2 |
| Bulk deletes | 121 ms | 10,000 tombstones |
| Incremental backup | 2,110 ms | artifact 20,703,629 B |
| Full-boundary restore | 4,039 ms | 0 log entries |
| Full restore verify | 702 ms | 100,000 exact matches |
| PITR restore | 4,339 ms | 2 log entries |
| PITR restore verify | 775 ms | 100,000 exact matches |
| Final restore | 4,565 ms | full incremental chain |
| Final restore verify | 863 ms | 90,000 exact, 10,000 absent |
| Index build | 6,305 ms | automatic index on sequence, active |
| Indexed queries | 294,987 ms | 2 ops/s, p50 5.199 ms, p95 5,559.736 ms, p99 6,075.840 ms |
| Compaction | 1,853 ms | 132,501 records, 10,000 tombstones |
| Compaction verify | 1,278 ms | 90,000 exact matches |

Full artifact plaintext SHA-256: `ad5d5dc60d0518840f30348853da5d8135c88cade7bbd82b681d3c17a9903d0a`
Incremental artifact plaintext SHA-256: `b746ba5f010e037db4cb790220ea866229ee25a9f6fbfeed52e72b8a71e2005d`