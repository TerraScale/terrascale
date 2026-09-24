# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T14:02:39.6643664+00:00

8 deterministic random documents written in batches of 4, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1 updates and 1 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,538 ms | 5 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 91 ms | 8 exact matches |
| Point reads | 0 ms | skipped |
| Full backup | 551 ms | artifact 2,226 B |
| Bulk updates | 35 ms | 1 documents to v2 |
| Bulk deletes | 38 ms | 1 tombstones |
| Incremental backup | 424 ms | artifact 4,585 B |
| Full-boundary restore | 2,958 ms | 0 log entries |
| Full restore verify | 108 ms | 8 exact matches |
| PITR restore | 1,587 ms | 1 log entries |
| PITR restore verify | 178 ms | 8 exact matches |
| Final restore | 2,497 ms | full incremental chain |
| Final restore verify | 188 ms | 7 exact, 1 absent |
| Snapshot document pages | 103 ms | 8 exact documents |
| Snapshot collection pages | 13 ms | 1 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Restore-target cleanup | 3 roots | final deleted=True; reclaimed 925,696 B; remaining entries [], 0 B after cleanup |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 163 ms | 9 records, 1 tombstones |
| Compaction verify | 251 ms | 7 exact matches |

Full artifact plaintext SHA-256: `4323a43fa944a17cde691c7ab25809f74b6293c7ad9cd5a194a64ffc658a7ea0`
Incremental artifact plaintext SHA-256: `12edb6696575cdcfa1cc3cf37b97c1bcf834b03cb4d2d473d8f19591933a5bd5`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | unbounded B |
| Peak local bytes | 626,688 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 372,736 B |
| Peak target bytes | 311,296 B |
| Peak upload/checkpoint bytes | 0 B |
| Peak temporary spool bytes | 16,384 B |
| Resolved `Path.GetTempPath()` | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-temp-root-smoke-20260923T135500Z/tmp-pass6` |
| Peak RustFS data-root bytes | 0 B |
| RustFS data-root baseline | 0 B |
| RustFS data-root growth | 0 B |
| Peak SSD cache bytes | 0 B |
| Minimum free bytes observed | 370,638,032,896 B (floor 0 B) |
| Peak RSS | 136,720,384 B |
| RustFS objects | 4 |
| RustFS bytes | 9,242 B |
| RustFS data-root accounting | not-applicable |
| Swap current at final sample | 5,376,393,216 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom -1->-1; oom_kill -1->-1 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-temp-root-smoke-20260923T135500Z/pass6/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-temp-root-smoke-20260923T135500Z/pass6/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query diagnostics: skipped.