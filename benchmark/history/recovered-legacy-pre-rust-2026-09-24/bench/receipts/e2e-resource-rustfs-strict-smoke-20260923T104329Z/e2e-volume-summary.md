# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T10:44:03.3113852+00:00

20 deterministic random documents written in batches of 10, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 2 updates and 2 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,577 ms | 13 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 879 ms | 20 exact matches |
| Point reads | 623 ms | 681 ops/s, p50 2.643 ms, p95 2.643 ms, p99 2.643 ms |
| Full backup | 894 ms | artifact 3,616 B |
| Bulk updates | 608 ms | 2 documents to v2 |
| Bulk deletes | 499 ms | 2 tombstones |
| Incremental backup | 765 ms | artifact 6,628 B |
| Full-boundary restore | 1,928 ms | 0 log entries |
| Full restore verify | 993 ms | 20 exact matches |
| PITR restore | 1,510 ms | 1 log entries |
| PITR restore verify | 1,115 ms | 20 exact matches |
| Final restore | 2,584 ms | full incremental chain |
| Final restore verify | 1,637 ms | 18 exact, 2 absent |
| Snapshot document pages | 962 ms | 20 exact documents |
| Snapshot collection pages | 10 ms | 1 collections |
| Cold RustFS reopen | 3,198 ms | 18 exact, 1 collections, 1 segment objects, SSD cache 6,746 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 47 ms | 21 records, 2 tombstones |
| Compaction verify | 2,099 ms | 18 exact matches |

Full artifact plaintext SHA-256: `90e61ced047fe8b9d25c8c1240e6dc96a8a0ed2cbb3a7f4826f2bcc6b1fd925b`
Incremental artifact plaintext SHA-256: `9dd66b1ec7057e9f7e32ff43cac12143c0c5b34c68cbec6c44393a83165342d7`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | 21,474,836,480 B |
| Peak local bytes | 335,872 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 0 B |
| Peak target bytes | 12,288 B |
| Peak upload/checkpoint bytes | 15,961,485,312 B |
| Peak temporary spool bytes | 15,960,887,296 B |
| Peak RustFS data-root bytes | 0 B |
| RustFS data-root baseline | 12,288 B |
| RustFS data-root growth | 372,472,020,992 B |
| Peak SSD cache bytes | 12,288 B |
| Minimum free bytes observed | 372,452,732,928 B (floor 1,073,741,824 B) |
| Peak RSS | 184,950,784 B |
| RustFS objects | 6 |
| RustFS bytes | 29,076 B |
| RustFS data-root accounting | validated |
| Swap current at final sample | 3,682,934,784 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom -1->-1; oom_kill -1->-1 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-strict-smoke-20260923T104329Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-strict-smoke-20260923T104329Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query diagnostics: skipped.