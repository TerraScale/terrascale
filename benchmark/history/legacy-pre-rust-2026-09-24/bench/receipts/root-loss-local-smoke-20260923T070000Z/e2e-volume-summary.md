# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T06:54:56.1054710+00:00

100 deterministic random documents written in batches of 25, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 10 updates and 10 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 823 ms | 122 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | related sets, traversal, restrict delete |
| Read-back verify | 41 ms | 100 exact matches |
| Point reads | 2 ms | 7,589 ops/s, p50 0.081 ms, p95 0.638 ms, p99 0.638 ms |
| Full backup | 915 ms | artifact 12,588 B |
| Bulk updates | 6 ms | 10 documents to v2 |
| Bulk deletes | 6 ms | 10 tombstones |
| Incremental backup | 1,102 ms | artifact 22,941 B |
| Full-boundary restore | 865 ms | 0 log entries |
| Full restore verify | 36 ms | 100 exact matches |
| PITR restore | 508 ms | 1 log entries |
| PITR restore verify | 23 ms | 100 exact matches |
| Final restore | 364 ms | full incremental chain |
| Final restore verify | 12 ms | 90 exact, 10 absent |
| Snapshot document pages | 23 ms | 100 exact documents |
| Snapshot collection pages | 8 ms | 1 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 2,151 ms | automatic index on sequence, active |
| Indexed queries | 413 ms | 28 ops/s, p50 1.195 ms, p95 345.702 ms, p99 345.702 ms |
| Compaction | 162 ms | 101 records, 10 tombstones |
| Compaction verify | 29 ms | 90 exact matches |

Full artifact plaintext SHA-256: `cf5437494b08966a851c209cec71469f5b33412d296ec427d4d1aa925711cf27`
Incremental artifact plaintext SHA-256: `78fe83b4e1d4ed1faf6fb8c1edb23cb491c764d3b391e0968ee195c30fab13d5`
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 920, bytes 42,329, work 930; physical counters available: True.