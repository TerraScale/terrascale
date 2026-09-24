# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T01:03:09.5079070+00:00

50 deterministic random documents written in batches of 25, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1 updates and 1 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 627 ms | 80 docs/s |
| Relationship write | 145 ms | 4 orders + users through the schema store |
| Relationship verify | 211 ms | related sets, traversal, restrict delete |
| Read-back verify | 17 ms | 50 exact matches |
| Point reads | 0 ms | skipped |
| Full backup | 684 ms | artifact 7,044 B |
| Bulk updates | 3 ms | 1 documents to v2 |
| Bulk deletes | 3 ms | 1 tombstones |
| Incremental backup | 568 ms | artifact 4,317 B |
| Full-boundary restore | 552 ms | 0 log entries |
| Full restore verify | 5 ms | 50 exact matches |
| PITR restore | 405 ms | 1 log entries |
| PITR restore verify | 8 ms | 50 exact matches |
| Final restore | 404 ms | full incremental chain |
| Final restore verify | 7 ms | 49 exact, 1 absent |
| Snapshot document pages | 29 ms | 50 exact documents |
| Snapshot collection pages | 12 ms | 3 collections |
| Cold RustFS reopen | 316 ms | 49 exact, 3 collections, 1 segment objects, SSD cache 21,383 B, restored local segments 0 B |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 54 ms | 64 records, 1 tombstones |
| Compaction verify | 16 ms | 49 exact matches |

Full artifact plaintext SHA-256: `3eaaff026423979866468051e1c9006292bda422460074cb89110c5135a90aff`
Incremental artifact plaintext SHA-256: `e5cdfbf472c79d5cc195f3f0616a4a478e45680bbd308dbbffde059cbff6b26a`