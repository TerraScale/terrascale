# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T15:13:28.1454005+00:00

1,000 deterministic random documents written in batches of 100, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 20 updates and 20 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 3,066 ms | 326 docs/s |
| Relationship write | 166 ms | 20 orders + users through the schema store |
| Relationship verify | 1,039 ms | 80 endpoints; all users and all relationship endpoints; traversal, restrict delete |
| Read-back verify | 2,028 ms | 1,000 exact matches |
| Point reads | 1,087 ms | 2,219 ops/s, p50 0.097 ms, p95 1.157 ms, p99 18.995 ms |
| Full backup | 1,939 ms | artifact 111,922 B |
| Bulk updates | 434 ms | 20 documents to v2 |
| Bulk deletes | 638 ms | 20 tombstones |
| Incremental backup | 1,770 ms | artifact 43,583 B |
| Full-boundary restore | 3,194 ms | 0 log entries |
| Full restore verify | 3,215 ms | 1,000 exact matches |
| PITR restore | 3,961 ms | 1 log entries |
| PITR restore verify | 1,702 ms | 1,000 exact matches |
| Final restore | 3,258 ms | full incremental chain |
| Final restore verify | 2,625 ms | 980 exact, 20 absent |
| Snapshot document pages | 1,552 ms | 1,000 exact documents |
| Snapshot collection pages | 57 ms | 3 collections |
| Cold RustFS reopen | 5,423 ms | 980 exact, 3 collections, 1 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | 12,570 ms | source deleted=True; 980 exact; 20 absent; 3 collections; 2 log entries; 1 remote segments; 2 artifacts/155,505 B preflighted; SSD cache 341,798 B; local segments after eviction 0 B (restore 4,506 ms, cold reopen 8,064 ms); checkpoint `root-loss-recovery-checkpoint.json` |
| Restore-target cleanup | 4 roots | final deleted=True; reclaimed 4,263,936 B; remaining entries [], 0 B after cleanup |
| Index build | 22,762 ms | automatic index on sequence, active |
| Indexed queries | 1,113 ms | 91 ops/s, p50 1.622 ms, p95 105.498 ms, p99 105.498 ms |
| Compaction | 531 ms | 1,066 records, 20 tombstones |
| Compaction verify | 3,862 ms | 980 exact matches |

Full artifact plaintext SHA-256: `0d6db0b180c4e3b68282d1eb370824d61fa756ea9a5e73103fa23074d38462b2`
Incremental artifact plaintext SHA-256: `8e207a2b645e62cf4769daded17a33fc366adfe55065f834f86771c95281a7f1`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | True |
| Configured local cap | 68,719,476,736 B |
| Peak local bytes | 22,104,649,728 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 2,154,496 B |
| Peak target bytes | 1,449,984 B |
| Peak upload/checkpoint bytes | 0 B |
| Peak temporary spool bytes | 208,896 B |
| Resolved `Path.GetTempPath()` | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1k-fixed-cfd8d1b6-20260923T150000Z-tmp` |
| Peak RustFS data-root bytes | 22,103,080,960 B |
| RustFS data-root baseline | 22,099,316,736 B |
| RustFS data-root growth | 3,764,224 B |
| Peak SSD cache bytes | 495,616 B |
| Minimum free bytes observed | 367,892,123,648 B (floor 1,073,741,824 B) |
| Peak RSS | 207,376,384 B |
| RustFS objects | 7 |
| RustFS bytes | 1,351,246 B |
| RustFS data-root accounting | validated |
| Swap current at final sample | 0 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom 0->0; oom_kill 0->0 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1k-fixed-cfd8d1b6-20260923T150000Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-1k-fixed-cfd8d1b6-20260923T150000Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 14,696, bytes 584,110, work 14,716; physical counters available: True.