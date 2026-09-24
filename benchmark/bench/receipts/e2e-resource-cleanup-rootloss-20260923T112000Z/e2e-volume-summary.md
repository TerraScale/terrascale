# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T11:02:17.9604967+00:00

20 deterministic random documents written in batches of 10, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 2 updates and 2 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,822 ms | 11 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 1,323 ms | 20 exact matches |
| Point reads | 808 ms | 456 ops/s, p50 4.070 ms, p95 4.070 ms, p99 4.070 ms |
| Full backup | 949 ms | artifact 3,617 B |
| Bulk updates | 287 ms | 2 documents to v2 |
| Bulk deletes | 248 ms | 2 tombstones |
| Incremental backup | 772 ms | artifact 6,629 B |
| Full-boundary restore | 1,625 ms | 0 log entries |
| Full restore verify | 1,248 ms | 20 exact matches |
| PITR restore | 1,451 ms | 1 log entries |
| PITR restore verify | 1,241 ms | 20 exact matches |
| Final restore | 1,602 ms | full incremental chain |
| Final restore verify | 1,248 ms | 18 exact, 2 absent |
| Snapshot document pages | 2,052 ms | 20 exact documents |
| Snapshot collection pages | 13 ms | 1 collections |
| Cold RustFS reopen | 3,004 ms | 18 exact, 1 collections, 1 segment objects, SSD cache 6,746 B, restored local segments 0 B |
| Root-loss recovery | 5,704 ms | source deleted=True; 18 exact; 2 absent; 1 collections; 2 log entries; 1 remote segments; 2 artifacts/10,246 B preflighted; SSD cache 6,746 B; local segments after eviction 0 B (restore 2,010 ms, cold reopen 3,694 ms); checkpoint `root-loss-recovery-checkpoint.json` |
| Restore-target cleanup | 4 roots | reclaimed 1,306,624 B; remaining entries [], 0 B after cleanup |
| Index build | 642 ms | automatic index on sequence, active |
| Indexed queries | 556 ms | 24 ops/s, p50 80.245 ms, p95 80.245 ms, p99 80.245 ms |
| Compaction | 26 ms | 21 records, 2 tombstones |
| Compaction verify | 2,306 ms | 18 exact matches |

Full artifact plaintext SHA-256: `0fed9ef994166a94a24ae68e02cfc71438b56c0d879fc80c11f395ece91b2e34`
Incremental artifact plaintext SHA-256: `877ec31e9d6d203af1a1532e388487f8b33fcb0f6e8ce700de66dd9f129d005b`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | True |
| Configured local cap | 21,474,836,480 B |
| Peak local bytes | 16,055,951,360 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 376,832 B |
| Peak target bytes | 352,256 B |
| Peak upload/checkpoint bytes | 12,288 B |
| Peak temporary spool bytes | 12,288 B |
| Peak RustFS data-root bytes | 16,055,508,992 B |
| RustFS data-root baseline | 16,054,145,024 B |
| RustFS data-root growth | 1,363,968 B |
| Peak SSD cache bytes | 12,288 B |
| Minimum free bytes observed | 376,833,556,480 B (floor 1,073,741,824 B) |
| Peak RSS | 191,655,936 B |
| RustFS objects | 7 |
| RustFS bytes | 35,814 B |
| RustFS data-root accounting | validated |
| Swap current at final sample | 0 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom 0->0; oom_kill 0->0 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-cleanup-rootloss-20260923T112000Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-cleanup-rootloss-20260923T112000Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 24, bytes 2,327, work 26; physical counters available: True.