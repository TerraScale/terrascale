# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T01:10:25.4036699+00:00

50 deterministic random documents written in batches of 10, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 5 updates and 5 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 583 ms | 86 docs/s |
| Relationship write | 125 ms | 10 orders + users through the schema store |
| Relationship verify | 175 ms | related sets, traversal, restrict delete |
| Read-back verify | 16 ms | 50 exact matches |
| Point reads | 1 ms | 16,722 ops/s, p50 0.026 ms, p95 0.361 ms, p99 0.361 ms |
| Full backup | 675 ms | artifact 7,141 B |
| Bulk updates | 3 ms | 5 documents to v2 |
| Bulk deletes | 4 ms | 5 tombstones |
| Incremental backup | 543 ms | artifact 12,515 B |
| Full-boundary restore | 486 ms | 0 log entries |
| Full restore verify | 4 ms | 50 exact matches |
| PITR restore | 407 ms | 1 log entries |
| PITR restore verify | 7 ms | 50 exact matches |
| Final restore | 381 ms | full incremental chain |
| Final restore verify | 7 ms | 45 exact, 5 absent |
| Snapshot document pages | 22 ms | 50 exact documents |
| Snapshot collection pages | 12 ms | 3 collections |
| Cold RustFS reopen | 571 ms | 45 exact, 3 collections, 1 segment objects, SSD cache 26,765 B, restored local segments 0 B |
| Index build | 816 ms | automatic index on sequence, active |
| Indexed queries | 140 ms | 37 ops/s, p50 2.714 ms, p95 125.795 ms, p99 125.795 ms |
| Compaction | 49 ms | 83 records, 5 tombstones |
| Compaction verify | 13 ms | 45 exact matches |

Full artifact plaintext SHA-256: `e2598453d5a777ca1f259593a0ee48c45e9481adfc2739b4c2c7a027714c1033`
Incremental artifact plaintext SHA-256: `de730f32d4543f8645584488d45bf782571e70f41dd939e089a9e8d854a5ce5e`