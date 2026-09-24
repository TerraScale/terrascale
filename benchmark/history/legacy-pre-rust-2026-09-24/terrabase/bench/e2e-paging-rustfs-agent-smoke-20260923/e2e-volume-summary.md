# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T01:01:21.9896965+00:00

200 deterministic random documents written in batches of 50, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 2 updates and 2 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 620 ms | 323 docs/s |
| Relationship write | 136 ms | 12 orders + users through the schema store |
| Relationship verify | 233 ms | related sets, traversal, restrict delete |
| Read-back verify | 46 ms | 200 exact matches |
| Point reads | 1 ms | 4,993 ops/s, p50 0.038 ms, p95 0.886 ms, p99 0.886 ms |
| Full backup | 1,006 ms | artifact 23,851 B |
| Bulk updates | 3 ms | 2 documents to v2 |
| Bulk deletes | 4 ms | 2 tombstones |
| Incremental backup | 724 ms | artifact 6,354 B |
| Full-boundary restore | 568 ms | 0 log entries |
| Full restore verify | 32 ms | 200 exact matches |
| PITR restore | 452 ms | 1 log entries |
| PITR restore verify | 25 ms | 200 exact matches |
| Final restore | 457 ms | full incremental chain |
| Final restore verify | 28 ms | 198 exact, 2 absent |
| Snapshot document pages | 27 ms | 200 exact documents |
| Snapshot collection pages | 10 ms | 3 collections |
| Cold RustFS reopen | 525 ms | 198 exact, 3 collections, 1 segment objects |
| Index build | 1,769 ms | automatic index on sequence, active |
| Indexed queries | 175 ms | 12 ops/s, p50 163.629 ms, p95 163.629 ms, p99 163.629 ms |
| Compaction | 105 ms | 240 records, 2 tombstones |
| Compaction verify | 56 ms | 198 exact matches |

Full artifact plaintext SHA-256: `8704d6a7b24961124e2a389a72018175d10a19544cae7693e5323bf5bb1ec50b`
Incremental artifact plaintext SHA-256: `5d957186545ceab2d84ddf76e6824ae2d160f0c71f9976344cdb9be92e1d8f94`