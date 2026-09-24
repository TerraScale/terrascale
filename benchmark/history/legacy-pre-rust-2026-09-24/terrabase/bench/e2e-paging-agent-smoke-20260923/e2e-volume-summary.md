# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T01:00:54.0087190+00:00

200 deterministic random documents written in batches of 50, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 2 updates and 2 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 636 ms | 315 docs/s |
| Relationship write | 134 ms | 12 orders + users through the schema store |
| Relationship verify | 224 ms | related sets, traversal, restrict delete |
| Read-back verify | 36 ms | 200 exact matches |
| Point reads | 1 ms | 10,841 ops/s, p50 0.037 ms, p95 0.348 ms, p99 0.348 ms |
| Full backup | 557 ms | artifact 23,848 B |
| Bulk updates | 5 ms | 2 documents to v2 |
| Bulk deletes | 5 ms | 2 tombstones |
| Incremental backup | 406 ms | artifact 6,354 B |
| Full-boundary restore | 480 ms | 0 log entries |
| Full restore verify | 27 ms | 200 exact matches |
| PITR restore | 346 ms | 1 log entries |
| PITR restore verify | 23 ms | 200 exact matches |
| Final restore | 311 ms | full incremental chain |
| Final restore verify | 29 ms | 198 exact, 2 absent |
| Snapshot document pages | 26 ms | 200 exact documents |
| Snapshot collection pages | 9 ms | 3 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects |
| Index build | 2,016 ms | automatic index on sequence, active |
| Indexed queries | 177 ms | 12 ops/s, p50 166.998 ms, p95 166.998 ms, p99 166.998 ms |
| Compaction | 114 ms | 240 records, 2 tombstones |
| Compaction verify | 45 ms | 198 exact matches |

Full artifact plaintext SHA-256: `c73a6bfd873321b116ae16f3271fa4d30e8adad0046d194a9306c2443db2c63e`
Incremental artifact plaintext SHA-256: `1df8d4053f447bb7bfa567d26348879f859b3c57fe3e3dd4f7b0054102adbf0e`