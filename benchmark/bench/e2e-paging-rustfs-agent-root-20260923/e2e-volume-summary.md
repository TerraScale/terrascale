# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T01:13:58.7663542+00:00

40 deterministic random documents written in batches of 8, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 4 updates and 4 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 566 ms | 71 docs/s |
| Relationship write | 128 ms | 8 orders + users through the schema store |
| Relationship verify | 165 ms | related sets, traversal, restrict delete |
| Read-back verify | 14 ms | 40 exact matches |
| Point reads | 1 ms | 13,786 ops/s, p50 0.025 ms, p95 0.370 ms, p99 0.370 ms |
| Full backup | 680 ms | artifact 5,973 B |
| Bulk updates | 3 ms | 4 documents to v2 |
| Bulk deletes | 4 ms | 4 tombstones |
| Incremental backup | 548 ms | artifact 10,446 B |
| Full-boundary restore | 526 ms | 0 log entries |
| Full restore verify | 5 ms | 40 exact matches |
| PITR restore | 379 ms | 1 log entries |
| PITR restore verify | 6 ms | 40 exact matches |
| Final restore | 349 ms | full incremental chain |
| Final restore verify | 6 ms | 36 exact, 4 absent |
| Snapshot document pages | 19 ms | 40 exact documents |
| Snapshot collection pages | 8 ms | 3 collections |
| Cold RustFS reopen | 433 ms | 36 exact, 3 collections, 1 segment objects, SSD cache 21,545 B, restored local segments 0 B |
| Index build | 816 ms | automatic index on sequence, active |
| Indexed queries | 140 ms | 30 ops/s, p50 3.198 ms, p95 130.751 ms, p99 130.751 ms |
| Compaction | 41 ms | 67 records, 4 tombstones |
| Compaction verify | 11 ms | 36 exact matches |

Full artifact plaintext SHA-256: `1a10a4f7df485e5bc7556b5420e76e9c6f8c51fae896a3253a09237e65e4a998`
Incremental artifact plaintext SHA-256: `8540ccac1d8e50b4905c6110f4ec5ad64e625314b44666ed2e5c566bdd745891`