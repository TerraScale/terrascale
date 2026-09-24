# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T10:45:49.4507231+00:00

10 deterministic random documents written in batches of 5, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1 updates and 1 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,742 ms | 6 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 990 ms | 10 exact matches |
| Point reads | 295 ms | 365 ops/s, p50 2.739 ms, p95 2.739 ms, p99 2.739 ms |
| Full backup | 840 ms | artifact 2,456 B |
| Bulk updates | 357 ms | 1 documents to v2 |
| Bulk deletes | 287 ms | 1 tombstones |
| Incremental backup | 682 ms | artifact 4,593 B |
| Full-boundary restore | 2,814 ms | 0 log entries |
| Full restore verify | 992 ms | 10 exact matches |
| PITR restore | 1,863 ms | 1 log entries |
| PITR restore verify | 1,035 ms | 10 exact matches |
| Final restore | 1,684 ms | full incremental chain |
| Final restore verify | 974 ms | 9 exact, 1 absent |
| Snapshot document pages | 976 ms | 10 exact documents |
| Snapshot collection pages | 9 ms | 1 collections |
| Cold RustFS reopen | 2,409 ms | 9 exact, 1 collections, 1 segment objects, SSD cache 3,541 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 32 ms | 11 records, 1 tombstones |
| Compaction verify | 1,881 ms | 9 exact matches |

Full artifact plaintext SHA-256: `cb7c496c5b323ac727bdba6ff4808197f12a4db27e2986aecc482071e4db2f01`
Incremental artifact plaintext SHA-256: `f22ab6e791dd8b3b31ce90065a234be99d5f1525ccf7bb91649993c9332ce9bb`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | 21,474,836,480 B |
| Peak local bytes | 15,961,911,296 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 368,640 B |
| Peak target bytes | 331,776 B |
| Peak upload/checkpoint bytes | 4,096 B |
| Peak temporary spool bytes | 4,096 B |
| Peak RustFS data-root bytes | 15,961,280,512 B |
| RustFS data-root baseline | 15,960,690,688 B |
| RustFS data-root growth | 589,824 B |
| Peak SSD cache bytes | 8,192 B |
| Minimum free bytes observed | 372,066,529,280 B (floor 1,073,741,824 B) |
| Peak RSS | 188,096,512 B |
| RustFS objects | 6 |
| RustFS bytes | 18,001 B |
| RustFS data-root accounting | validated |
| Swap current at final sample | 3,678,396,416 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom -1->-1; oom_kill -1->-1 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-strict-smoke-20260923T104517Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-strict-smoke-20260923T104517Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query diagnostics: skipped.