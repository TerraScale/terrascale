# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T10:56:55.3225123+00:00

100,000 deterministic random documents written in batches of 8,192, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1,000 updates and 1,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 14,895 ms | 6,714 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 2,632 ms | 100,000 exact matches |
| Point reads | 17,206 ms | 605 ops/s, p50 1.594 ms, p95 2.644 ms, p99 3.258 ms |
| Full backup | 23,252 ms | artifact 10,989,592 B |
| Bulk updates | 4,183 ms | 1,000 documents to v2 |
| Bulk deletes | 374 ms | 1,000 tombstones |
| Incremental backup | 25,261 ms | artifact 2,073,607 B |
| Full-boundary restore | 21,094 ms | 0 log entries |
| Full restore verify | 2,037 ms | 100,000 exact matches |
| PITR restore | 23,998 ms | 1 log entries |
| PITR restore verify | 2,108 ms | 100,000 exact matches |
| Final restore | 24,846 ms | full incremental chain |
| Final restore verify | 2,234 ms | 99,000 exact, 1,000 absent |
| Snapshot document pages | 2,087 ms | 100,000 exact documents |
| Snapshot collection pages | 241 ms | 1 collections |
| Cold RustFS reopen | 44,379 ms | 99,000 exact, 1 collections, 1 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 24,949 ms | automatic index on sequence, active |
| Indexed queries | 18,415 ms | 72 ops/s, p50 12.928 ms, p95 28.447 ms, p99 36.605 ms |
| Compaction | 10,713 ms | 100,001 records, 1,000 tombstones |
| Compaction verify | 4,099 ms | 99,000 exact matches |

Full artifact plaintext SHA-256: `0f90fbcd496771ec54cd1064ca24b290ed0ba4f684fbbd41e85daeefa1815168`
Incremental artifact plaintext SHA-256: `224772c65ca5982680675ac77b29819938f884ffde9d52a6e37dcb10f4314e2d`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | True |
| Configured local cap | 25,769,803,776 B |
| Peak local bytes | 16,277,192,704 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 122,433,536 B |
| Peak target bytes | 212,066,304 B |
| Peak upload/checkpoint bytes | 0 B |
| Peak temporary spool bytes | 13,070,336 B |
| Peak RustFS data-root bytes | 16,055,816,192 B |
| RustFS data-root baseline | 15,959,748,608 B |
| RustFS data-root growth | 96,067,584 B |
| Peak SSD cache bytes | 67,043,328 B |
| Minimum free bytes observed | 371,709,181,952 B (floor 1,073,741,824 B) |
| Peak RSS | 744,996,864 B |
| RustFS objects | 6 |
| RustFS bytes | 94,225,450 B |
| RustFS data-root accounting | validated |
| Swap current at final sample | 0 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom 0->0; oom_kill 0->0 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-100k-strict-20260923T110100Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-100k-strict-20260923T110100Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 1,025,820, bytes 211,408,429, work 1,026,828; physical counters available: True.