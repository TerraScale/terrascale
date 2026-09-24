# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T08:32:02.9311010+00:00

100,000 deterministic random documents written in batches of 1,000, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1,000 updates and 1,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 11,967 ms | 8,357 docs/s |
| Relationship write | 2,975 ms | 10,000 orders + users through the schema store |
| Relationship verify | 17,409 ms | 40,000 endpoints; all users and all relationship endpoints; traversal, restrict delete |
| Read-back verify | 3,844 ms | 100,000 exact matches |
| Point reads | 880 ms | 587 ops/s, p50 1.620 ms, p95 2.861 ms, p99 3.656 ms |
| Full backup | 719,759 ms | artifact 11,138,247 B |
| Bulk updates | 3,951 ms | 1,000 documents to v2 |
| Bulk deletes | 978 ms | 1,000 tombstones |
| Incremental backup | 496,632 ms | artifact 2,068,329 B |
| Full-boundary restore | 15,030 ms | 0 log entries |
| Full restore verify | 2,557 ms | 100,000 exact matches |
| PITR restore | 18,294 ms | 1 log entries |
| PITR restore verify | 2,612 ms | 100,000 exact matches |
| Final restore | 19,366 ms | full incremental chain |
| Final restore verify | 2,472 ms | 99,000 exact, 1,000 absent |
| Snapshot document pages | 1,267 ms | 100,000 exact documents |
| Snapshot collection pages | 187 ms | 3 collections |
| Cold RustFS reopen | 66,040 ms | 99,000 exact, 3 collections, 1 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | 69,690 ms | source deleted=True; 99,000 exact; 1,000 absent; 3 collections; 2 log entries; 1 remote segments; 2 artifacts/13,206,576 B preflighted; SSD cache 0 B; local segments after eviction 0 B (restore 19,653 ms, cold reopen 50,038 ms); checkpoint `root-loss-recovery-checkpoint.json` |
| Index build | 383,136 ms | automatic index on sequence, active |
| Indexed queries | 9,064 ms | 12 ops/s, p50 6.306 ms, p95 22.992 ms, p99 7,876.426 ms |
| Compaction | 14,566 ms | 132,501 records, 1,000 tombstones |
| Compaction verify | 5,326 ms | 99,000 exact matches |

Full artifact plaintext SHA-256: `561855f883605561e6cf12521b6231f91dc3664f9da949170e089c056c4525bb`
Incremental artifact plaintext SHA-256: `3baa95f27d6c5bfcfb4cf3f9c1818f6d793147e935bf8860add02509fdcb9b90`
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 97,825, bytes 19,757,964, work 97,926; physical counters available: True.