# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T10:19:50.4127233+00:00

200 deterministic random documents written in batches of 50, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 10 updates and 10 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,028 ms | 195 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 50 ms | 200 exact matches |
| Point reads | 15 ms | 2,023 ops/s, p50 0.087 ms, p95 2.247 ms, p99 2.247 ms |
| Full backup | 578 ms | artifact 23,766 B |
| Bulk updates | 18 ms | 10 documents to v2 |
| Bulk deletes | 12 ms | 10 tombstones |
| Incremental backup | 368 ms | artifact 23,060 B |
| Full-boundary restore | 1,682 ms | 0 log entries |
| Full restore verify | 53 ms | 200 exact matches |
| PITR restore | 1,384 ms | 1 log entries |
| PITR restore verify | 42 ms | 200 exact matches |
| Final restore | 1,355 ms | full incremental chain |
| Final restore verify | 35 ms | 190 exact, 10 absent |
| Snapshot document pages | 51 ms | 200 exact documents |
| Snapshot collection pages | 10 ms | 1 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 85 ms | 201 records, 10 tombstones |
| Compaction verify | 59 ms | 190 exact matches |

Full artifact plaintext SHA-256: `477cedf0f367d337475ece045c292cae53f3596a9ce5d21ba7df67cb3fa973ec`
Incremental artifact plaintext SHA-256: `9b00ce0372cf92563b0b68d05328aff4bec92d98e278be87a8a8dc2886344658`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | 2,147,483,648 B |
| Peak local bytes | 134,787,145 B |
| Peak source bytes | 67,348,987 B |
| Peak target bytes | 67,276,836 B |
| Peak upload/checkpoint bytes | 0 B |
| Peak temporary spool bytes | 46,826 B |
| Peak RustFS data-root bytes | 0 B |
| Peak SSD cache bytes | 0 B |
| Minimum free bytes observed | 377,190,932,480 B (floor 1,073,741,824 B) |
| Peak RSS | 139,874,304 B |
| RustFS objects | 4 |
| RustFS bytes | 49,254 B |
| Swap current at final sample | 3,335,651,328 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-smoke-20260923T101933Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-smoke-20260923T101933Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query diagnostics: skipped.