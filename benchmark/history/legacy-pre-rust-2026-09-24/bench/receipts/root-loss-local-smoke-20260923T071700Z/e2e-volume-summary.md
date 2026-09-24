# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T07:05:05.2185713+00:00

100 deterministic random documents written in batches of 25, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 10 updates and 10 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 546 ms | 183 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 30 ms | 100 exact matches |
| Point reads | 1 ms | 15,340 ops/s, p50 0.029 ms, p95 0.400 ms, p99 0.400 ms |
| Full backup | 518 ms | artifact 12,581 B |
| Bulk updates | 7 ms | 10 documents to v2 |
| Bulk deletes | 5 ms | 10 tombstones |
| Incremental backup | 317 ms | artifact 22,936 B |
| Full-boundary restore | 429 ms | 0 log entries |
| Full restore verify | 22 ms | 100 exact matches |
| PITR restore | 305 ms | 1 log entries |
| PITR restore verify | 11 ms | 100 exact matches |
| Final restore | 240 ms | full incremental chain |
| Final restore verify | 12 ms | 90 exact, 10 absent |
| Snapshot document pages | 25 ms | 100 exact documents |
| Snapshot collection pages | 11 ms | 1 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 1,239 ms | automatic index on sequence, active |
| Indexed queries | 244 ms | 50 ops/s, p50 0.758 ms, p95 188.535 ms, p99 188.535 ms |
| Compaction | 92 ms | 101 records, 10 tombstones |
| Compaction verify | 25 ms | 90 exact matches |

Full artifact plaintext SHA-256: `437d45a2c929447b1972b363830d496c1e16f3e3c0294de97860b6dba0cbdf9c`
Incremental artifact plaintext SHA-256: `e5b879a5d2aeffdff2e60999da5791f5af57fbde95354cf2c25eae62218e0311`
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 920, bytes 42,329, work 930; physical counters available: True.