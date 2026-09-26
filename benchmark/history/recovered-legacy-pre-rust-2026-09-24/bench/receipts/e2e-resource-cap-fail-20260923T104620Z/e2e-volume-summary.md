# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T10:46:38.6309076+00:00

20 deterministic random documents written in batches of 10, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1 updates and 1 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,205 ms | 17 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 81 ms | 20 exact matches |
| Point reads | 0 ms | skipped |
| Full backup | 547 ms | artifact 3,619 B |
| Bulk updates | 31 ms | 1 documents to v2 |
| Bulk deletes | 27 ms | 1 tombstones |
| Incremental backup | 345 ms | artifact 4,593 B |
| Full-boundary restore | 1,599 ms | 0 log entries |
| Full restore verify | 93 ms | 20 exact matches |
| PITR restore | 1,391 ms | 1 log entries |
| PITR restore verify | 86 ms | 20 exact matches |
| Final restore | 1,496 ms | full incremental chain |
| Final restore verify | 186 ms | 19 exact, 1 absent |
| Snapshot document pages | 102 ms | 20 exact documents |
| Snapshot collection pages | 10 ms | 1 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 102 ms | 21 records, 1 tombstones |
| Compaction verify | 172 ms | 19 exact matches |

Full artifact plaintext SHA-256: `eabe8c41d0647f3d58510c5f7d6c81db00b9f6c7f31a3d78fb65dafe8f7d61f7`
Incremental artifact plaintext SHA-256: `34402bfb0cf2e403142c2f3ddbf35f8c0557886760e991d62a6088f3d352fa23`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | 10,000,000 B |
| Peak local bytes | 679,936 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 409,600 B |
| Peak target bytes | 339,968 B |
| Peak upload/checkpoint bytes | 0 B |
| Peak temporary spool bytes | 12,288 B |
| Peak RustFS data-root bytes | 0 B |
| RustFS data-root baseline | 0 B |
| RustFS data-root growth | 0 B |
| Peak SSD cache bytes | 0 B |
| Minimum free bytes observed | 372,315,959,296 B (floor 0 B) |
| Peak RSS | 140,636,160 B |
| RustFS objects | 4 |
| RustFS bytes | 10,633 B |
| RustFS data-root accounting | not-applicable |
| Swap current at final sample | 3,676,397,568 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom -1->-1; oom_kill -1->-1 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-cap-fail-20260923T104620Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-cap-fail-20260923T104620Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query diagnostics: skipped.