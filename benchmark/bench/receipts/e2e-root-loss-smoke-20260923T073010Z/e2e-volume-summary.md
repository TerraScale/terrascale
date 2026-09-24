# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T07:30:30.6607376+00:00

1,000 deterministic random documents written in batches of 100, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 20 updates and 20 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 823 ms | 1,216 docs/s |
| Relationship write | 147 ms | 20 orders + users through the schema store |
| Relationship verify | 310 ms | 80 endpoints; all users and all relationship endpoints; traversal, restrict delete |
| Read-back verify | 81 ms | 1,000 exact matches |
| Point reads | 4 ms | 29,489 ops/s, p50 0.027 ms, p95 0.044 ms, p99 0.362 ms |
| Full backup | 1,279 ms | artifact 111,625 B |
| Bulk updates | 8 ms | 20 documents to v2 |
| Bulk deletes | 6 ms | 20 tombstones |
| Incremental backup | 1,011 ms | artifact 43,479 B |
| Full-boundary restore | 571 ms | 0 log entries |
| Full restore verify | 91 ms | 1,000 exact matches |
| PITR restore | 583 ms | 1 log entries |
| PITR restore verify | 97 ms | 1,000 exact matches |
| Final restore | 603 ms | full incremental chain |
| Final restore verify | 97 ms | 980 exact, 20 absent |
| Snapshot document pages | 46 ms | 1,000 exact documents |
| Snapshot collection pages | 13 ms | 3 collections |
| Cold RustFS reopen | 793 ms | 980 exact, 3 collections, 1 segment objects, SSD cache 357,918 B, restored local segments 0 B |
| Root-loss recovery | 2,037 ms | source deleted=True; 980 exact; 20 absent; 3 collections; 2 log entries; 1 remote segments; 2 artifacts/155,104 B preflighted; SSD cache 339,798 B; local segments after eviction 0 B (restore 809 ms, cold reopen 1,228 ms); checkpoint `root-loss-recovery-checkpoint.json` |
| Index build | 4,964 ms | automatic index on sequence, active |
| Indexed queries | 445 ms | 84 ops/s, p50 1.949 ms, p95 153.546 ms, p99 153.546 ms |
| Compaction | 118 ms | 1,066 records, 20 tombstones |
| Compaction verify | 102 ms | 980 exact matches |

Full artifact plaintext SHA-256: `612e97203f952e6949c34ae578d89ed4eaa6d755e0268236dde14cef1091c28b`
Incremental artifact plaintext SHA-256: `160380f1067cb1941d67b0b028ef8ed2e5ea1c4028a0dc1c42d3727e0599d34e`
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 14,696, bytes 569,414, work 14,716; physical counters available: True.