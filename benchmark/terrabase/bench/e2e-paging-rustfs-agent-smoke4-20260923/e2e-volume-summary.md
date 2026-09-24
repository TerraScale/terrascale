# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T01:07:02.0808766+00:00

50 deterministic random documents written in batches of 10, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 5 updates and 5 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 682 ms | 73 docs/s |
| Relationship write | 137 ms | 10 orders + users through the schema store |
| Relationship verify | 186 ms | related sets, traversal, restrict delete |
| Read-back verify | 16 ms | 50 exact matches |
| Point reads | 1 ms | 13,674 ops/s, p50 0.027 ms, p95 0.487 ms, p99 0.487 ms |
| Full backup | 721 ms | artifact 7,137 B |
| Bulk updates | 3 ms | 5 documents to v2 |
| Bulk deletes | 4 ms | 5 tombstones |
| Incremental backup | 588 ms | artifact 12,515 B |
| Full-boundary restore | 595 ms | 0 log entries |
| Full restore verify | 12 ms | 50 exact matches |
| PITR restore | 408 ms | 1 log entries |
| PITR restore verify | 6 ms | 50 exact matches |
| Final restore | 376 ms | full incremental chain |
| Final restore verify | 6 ms | 45 exact, 5 absent |
| Snapshot document pages | 25 ms | 50 exact documents |
| Snapshot collection pages | 8 ms | 3 collections |
| Cold RustFS reopen | 477 ms | 45 exact, 3 collections, 1 segment objects, SSD cache 26,765 B, restored local segments 0 B |
| Index build | 1,021 ms | automatic index on sequence, active |
| Indexed queries | 155 ms | 34 ops/s, p50 2.635 ms, p95 141.377 ms, p99 141.377 ms |
| Compaction | 59 ms | 83 records, 5 tombstones |
| Compaction verify | 14 ms | 45 exact matches |

Full artifact plaintext SHA-256: `72f17df3b394ddd8bf8e20fdbdffa0709d4eb55e2ab1e10180c53faba444c9a4`
Incremental artifact plaintext SHA-256: `5c46d8bcfa1004fcf66edb9d74667933cffd5f87361a7d9bb5610cb9422c679e`