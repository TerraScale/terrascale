# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T00:20:08.1242766+00:00

100,000 deterministic random documents written in batches of 1,000, verified by exact read-back and point reads, backed up (full + incremental around 1,000 updates and 1,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 13,787 ms | 7,253 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | related sets, traversal, restrict delete |
| Read-back verify | 3,800 ms | 100,000 exact matches |
| Point reads | 2,016 ms | 249 ops/s, p50 3.717 ms, p95 7.533 ms, p99 9.871 ms |
| Full backup | 34,247 ms | artifact 11,002,333 B |
| Bulk updates | 328 ms | 1,000 documents to v2 |
| Bulk deletes | 1,008 ms | 1,000 tombstones |
| Incremental backup | 26,192 ms | artifact 2,067,977 B |
| Full-boundary restore | 26,664 ms | 0 log entries |
| Full restore verify | 3,388 ms | 100,000 exact matches |
| PITR restore | 27,110 ms | 1 log entries |
| PITR restore verify | 2,912 ms | 100,000 exact matches |
| Final restore | 25,600 ms | full incremental chain |
| Final restore verify | 3,108 ms | 99,000 exact, 1,000 absent |
| Index build | 26,506 ms | automatic index on sequence, active |
| Indexed queries | 9,912 ms | 11 ops/s, p50 9.309 ms, p95 31.416 ms, p99 8,177.288 ms |
| Compaction | 158,019 ms | 100,001 records, 1,000 tombstones |
| Compaction verify | 5,753 ms | 99,000 exact matches |

Full artifact plaintext SHA-256: `a0c5487b0052447d084545fd2b2231caecced3f365964ed317cb2540624923b9`
Incremental artifact plaintext SHA-256: `3b5d70f36351fc98c8add6a68d5a9f9c4aaf7b92fe1b505f2fc9f14d1be03695`