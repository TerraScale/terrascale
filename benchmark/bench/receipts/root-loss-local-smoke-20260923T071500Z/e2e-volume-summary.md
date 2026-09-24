# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T07:04:13.4691360+00:00

100 deterministic random documents written in batches of 25, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 10 updates and 10 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 513 ms | 195 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 27 ms | 100 exact matches |
| Point reads | 1 ms | 16,745 ops/s, p50 0.033 ms, p95 0.330 ms, p99 0.330 ms |
| Full backup | 505 ms | artifact 12,566 B |
| Bulk updates | 5 ms | 10 documents to v2 |
| Bulk deletes | 5 ms | 10 tombstones |
| Incremental backup | 344 ms | artifact 22,937 B |
| Full-boundary restore | 396 ms | 0 log entries |
| Full restore verify | 11 ms | 100 exact matches |
| PITR restore | 291 ms | 1 log entries |
| PITR restore verify | 7 ms | 100 exact matches |
| Final restore | 231 ms | full incremental chain |
| Final restore verify | 8 ms | 90 exact, 10 absent |
| Snapshot document pages | 17 ms | 100 exact documents |
| Snapshot collection pages | 8 ms | 1 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 1,154 ms | automatic index on sequence, active |
| Indexed queries | 167 ms | 66 ops/s, p50 0.432 ms, p95 144.325 ms, p99 144.325 ms |
| Compaction | 83 ms | 101 records, 10 tombstones |
| Compaction verify | 16 ms | 90 exact matches |

Full artifact plaintext SHA-256: `d64715d4ae189495032de87d0c5ad522cfba456f7312597d5fcb3a8211d4033a`
Incremental artifact plaintext SHA-256: `9781cde1bb9233168a42fe0965477ca5acbda41c9574bcf84936be895366ee99`
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 920, bytes 42,329, work 930; physical counters available: True.