# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T18:39:12.2999722+00:00

1,000 deterministic random documents written in batches of 100, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 20 updates and 20 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

Source acknowledgement durability: **strict remote commit journal; frontier 1,106; 16 frames/516,553 B; database 01ARZ3NDEKTSV4RRFFQ69G5FAV; writer 01ARZ3NDEKTSV4RRFFQ69G5FAX; prefix `e2e-volume-source-journal/01ARZ3NDEKTSV4RRFFQ69G5FAV/journal/v1/rustfs/frames/`**

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 4,076 ms | 245 docs/s |
| Relationship write | 354 ms | 20 orders + users through the schema store |
| Relationship verify | 1,038 ms | 80 endpoints; all users and all relationship endpoints; traversal, restrict delete |
| Read-back verify | 1,314 ms | 1,000 exact matches |
| Point reads | 1,345 ms | 2,037 ops/s, p50 0.182 ms, p95 6.356 ms, p99 6.356 ms |
| Full backup | 1,556 ms | artifact 111,854 B |
| Bulk updates | 637 ms | 20 documents to v2 |
| Bulk deletes | 519 ms | 20 tombstones |
| Incremental backup | 394 ms | artifact 43,581 B |
| Full-boundary restore | 1,493 ms | 0 log entries |
| Full restore verify | 1,079 ms | 1,000 exact matches |
| PITR restore | 1,676 ms | 1 log entries |
| PITR restore verify | 1,162 ms | 1,000 exact matches |
| Final restore | 2,125 ms | full incremental chain |
| Final restore verify | 1,233 ms | 980 exact, 20 absent |
| Snapshot document pages | 1,540 ms | 1,000 exact documents |
| Snapshot collection pages | 41 ms | 3 collections |
| Cold RustFS reopen | 3,978 ms | 980 exact, 3 collections, 1 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | 9,114 ms | source deleted=True; 980 exact; 20 absent; 3 collections; 2 log entries; 1 remote segments; 2 artifacts/155,435 B preflighted; journal source recovery=True (980 exact, frontier 1,106, 2,451 ms); SSD cache 341,798 B; local segments after eviction 0 B (restore 2,413 ms, cold reopen 4,251 ms); checkpoint `root-loss-recovery-checkpoint.json` |
| Restore-target cleanup | 4 roots | final deleted=True; reclaimed 4,263,936 B; remaining entries [], 0 B after cleanup |
| Index build | 15,307 ms | automatic index on sequence, active |
| Indexed queries | 1,419 ms | 106 ops/s, p50 2.019 ms, p95 129.284 ms, p99 129.284 ms |
| Compaction | 129 ms | 1,066 records, 20 tombstones |
| Compaction verify | 2,898 ms | 980 exact matches |

Full artifact plaintext SHA-256: `75eb2c1aee15ff548c1bb9aed9fe99b1dad273c3c4b6e9510aca6f0a7f7963d9`
Incremental artifact plaintext SHA-256: `1fed4ce81d14b9450e6e45a4d99a2944c490b39534f3fe91bd467bc80c269573`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | 68,719,476,736 B |
| Peak local bytes | 28,210,290,688 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 2,154,496 B |
| Peak target bytes | 1,449,984 B |
| Peak upload/checkpoint bytes | 0 B |
| Peak temporary spool bytes | 163,840 B |
| Resolved `Path.GetTempPath()` | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1k-strict-journal-20260923T185200Z-tmp` |
| Peak RustFS data-root bytes | 28,208,627,712 B |
| RustFS data-root baseline | 28,000,219,136 B |
| RustFS data-root growth | 208,408,576 B |
| Peak SSD cache bytes | 495,616 B |
| Minimum free bytes observed | 348,910,854,144 B (floor 1,073,741,824 B) |
| Peak RSS | 211,738,624 B |
| RustFS objects | 23 |
| RustFS bytes | 1,867,728 B |
| RustFS data-root accounting | validated |
| Swap current at final sample | 7,583,383,552 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom -1->-1; oom_kill -1->-1 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1k-strict-journal-20260923T185200Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1k-strict-journal-20260923T185200Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 23,192, bytes 779,518, work 23,224; physical counters available: True.