# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T01:04:33.4837505+00:00

50 deterministic random documents written in batches of 25, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1 updates and 1 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 976 ms | 51 docs/s |
| Relationship write | 205 ms | 4 orders + users through the schema store |
| Relationship verify | 172 ms | related sets, traversal, restrict delete |
| Read-back verify | 19 ms | 50 exact matches |
| Point reads | 0 ms | skipped |
| Full backup | 713 ms | artifact 7,038 B |
| Bulk updates | 3 ms | 1 documents to v2 |
| Bulk deletes | 3 ms | 1 tombstones |
| Incremental backup | 601 ms | artifact 4,317 B |
| Full-boundary restore | 625 ms | 0 log entries |
| Full restore verify | 10 ms | 50 exact matches |
| PITR restore | 464 ms | 1 log entries |
| PITR restore verify | 5 ms | 50 exact matches |
| Final restore | 453 ms | full incremental chain |
| Final restore verify | 5 ms | 49 exact, 1 absent |
| Snapshot document pages | 22 ms | 50 exact documents |
| Snapshot collection pages | 8 ms | 3 collections |
| Cold RustFS reopen | 467 ms | 49 exact, 3 collections, 1 segment objects, SSD cache 21,383 B, restored local segments 0 B |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 52 ms | 64 records, 1 tombstones |
| Compaction verify | 14 ms | 49 exact matches |

Full artifact plaintext SHA-256: `556090eafff3c3b65cf117201e1b3b1ffa41de48e46ac9fe4356ce498a73d696`
Incremental artifact plaintext SHA-256: `ea07d32ccc0a3509eeb9f87db4b9070a52a700a316000813101f737d34d41622`