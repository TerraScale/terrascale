# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T18:38:12.4268494+00:00

1,000 deterministic random documents written in batches of 100, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 20 updates and 20 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

Source acknowledgement durability: **strict remote commit journal; frontier 1,106; 16 frames/516,553 B; database 01ARZ3NDEKTSV4RRFFQ69G5FAV; writer 01ARZ3NDEKTSV4RRFFQ69G5FAX; prefix `e2e-volume-source-journal/01ARZ3NDEKTSV4RRFFQ69G5FAV/journal/v1/rustfs/frames/`**

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 2,588 ms | 386 docs/s |
| Relationship write | 210 ms | 20 orders + users through the schema store |
| Relationship verify | 618 ms | 80 endpoints; all users and all relationship endpoints; traversal, restrict delete |
| Read-back verify | 844 ms | 1,000 exact matches |
| Point reads | 719 ms | 5,991 ops/s, p50 0.117 ms, p95 0.833 ms, p99 0.833 ms |
| Full backup | 1,244 ms | artifact 111,925 B |
| Bulk updates | 347 ms | 20 documents to v2 |
| Bulk deletes | 385 ms | 20 tombstones |
| Incremental backup | 379 ms | artifact 43,591 B |
| Full-boundary restore | 2,181 ms | 0 log entries |
| Full restore verify | 707 ms | 1,000 exact matches |
| PITR restore | 1,788 ms | 1 log entries |
| PITR restore verify | 731 ms | 1,000 exact matches |
| Final restore | 1,606 ms | full incremental chain |
| Final restore verify | 706 ms | 980 exact, 20 absent |
| Snapshot document pages | 759 ms | 1,000 exact documents |
| Snapshot collection pages | 14 ms | 3 collections |
| Cold RustFS reopen | 3,820 ms | 980 exact, 3 collections, 1 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Restore-target cleanup | 3 roots | final deleted=True; reclaimed 3,010,560 B; remaining entries [], 0 B after cleanup |
| Index build | 6,772 ms | automatic index on sequence, active |
| Indexed queries | 2,283 ms | 89 ops/s, p50 2.068 ms, p95 180.464 ms, p99 180.464 ms |
| Compaction | 190 ms | 1,066 records, 20 tombstones |
| Compaction verify | 1,555 ms | 980 exact matches |

Full artifact plaintext SHA-256: `2a65d40d276a1e552cb48e7f9df6b950a27f937a252b318509be8d919e51f517`
Incremental artifact plaintext SHA-256: `e95bccf9c3c3b57acfd864979619532feef0082eb5850abfc83d10b0e65ca143`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | unbounded B |
| Peak local bytes | 28,032,376,832 B |
| Maximum observed cap overshoot | 0 B (qualification requires 0 B) |
| Peak source bytes | 2,154,496 B |
| Peak target bytes | 1,449,984 B |
| Peak upload/checkpoint bytes | 0 B |
| Peak temporary spool bytes | 163,840 B |
| Resolved `Path.GetTempPath()` | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-rustfs-1k-functions-20260923T1840Z-tmp` |
| Peak RustFS data-root bytes | 28,030,119,936 B |
| RustFS data-root baseline | 27,861,204,992 B |
| RustFS data-root growth | 167,960,576 B |
| Peak SSD cache bytes | 495,616 B |
| Minimum free bytes observed | 349,905,059,840 B (floor 0 B) |
| Peak RSS | 249,548,800 B |
| RustFS objects | 22 |
| RustFS bytes | 1,526,013 B |
| RustFS data-root accounting | validated |
| Swap current at final sample | 6,980,055,040 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |
| cgroup OOM deltas | oom -1->-1; oom_kill -1->-1 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-rustfs-1k-functions-20260923T1840Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-rustfs-1k-functions-20260923T1840Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query samples: `indexed-query-samples.jsonl`; worst-query table: `indexed-query-worst.md`; logical totals rows 23,192, bytes 779,518, work 23,224; physical counters available: True.