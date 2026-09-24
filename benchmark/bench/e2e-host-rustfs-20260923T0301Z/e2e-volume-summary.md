# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T06:02:25.1732658+00:00

1,000 deterministic random documents written in batches of 100, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 20 updates and 20 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,030 ms | 971 docs/s |
| Relationship write | 204 ms | 20 orders + users through the schema store |
| Relationship verify | 407 ms | related sets, traversal, restrict delete |
| Read-back verify | 146 ms | 1,000 exact matches |
| Point reads | 4 ms | 26,985 ops/s, p50 0.028 ms, p95 0.091 ms, p99 0.418 ms |
| Full backup | 1,283 ms | artifact 111,975 B |
| Bulk updates | 9 ms | 20 documents to v2 |
| Bulk deletes | 8 ms | 20 tombstones |
| Incremental backup | 1,052 ms | artifact 43,479 B |
| Full-boundary restore | 819 ms | 0 log entries |
| Full restore verify | 122 ms | 1,000 exact matches |
| PITR restore | 660 ms | 1 log entries |
| PITR restore verify | 96 ms | 1,000 exact matches |
| Final restore | 552 ms | full incremental chain |
| Final restore verify | 98 ms | 980 exact, 20 absent |
| Snapshot document pages | 53 ms | 1,000 exact documents |
| Snapshot collection pages | 14 ms | 3 collections |
| Cold RustFS reopen | 923 ms | 980 exact, 3 collections, 1 segment objects, SSD cache 357,918 B, restored local segments 0 B |
| Index build | 6,237 ms | automatic index on sequence, active |
| Indexed queries | 610 ms | 68 ops/s, p50 2.928 ms, p95 176.506 ms, p99 176.506 ms |
| Compaction | 108 ms | 1,066 records, 20 tombstones |
| Compaction verify | 107 ms | 980 exact matches |

Full artifact plaintext SHA-256: `7ee580ed9c7d88d32f0fdc45599b1f38beaa05e92822eae74de42f6ae53c461d`
Incremental artifact plaintext SHA-256: `e03fa8c1d6e450e8bb508ce3e1f9accaf14f235ed683754fe60072318ea84712`
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 14,696, bytes 569,414, work 14,716; physical counters available: True.