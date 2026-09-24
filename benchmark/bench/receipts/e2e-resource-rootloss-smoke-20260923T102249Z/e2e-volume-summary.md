# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T10:23:10.7420785+00:00

20 deterministic random documents written in batches of 10, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 2 updates and 2 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,020 ms | 20 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 42 ms | 20 exact matches |
| Point reads | 34 ms | 749 ops/s, p50 2.243 ms, p95 2.243 ms, p99 2.243 ms |
| Full backup | 791 ms | artifact 3,618 B |
| Bulk updates | 16 ms | 2 documents to v2 |
| Bulk deletes | 14 ms | 2 tombstones |
| Incremental backup | 594 ms | artifact 6,629 B |
| Full-boundary restore | 1,464 ms | 0 log entries |
| Full restore verify | 40 ms | 20 exact matches |
| PITR restore | 1,484 ms | 1 log entries |
| PITR restore verify | 39 ms | 20 exact matches |
| Final restore | 1,446 ms | full incremental chain |
| Final restore verify | 31 ms | 18 exact, 2 absent |
| Snapshot document pages | 53 ms | 20 exact documents |
| Snapshot collection pages | 8 ms | 1 collections |
| Cold RustFS reopen | 675 ms | 18 exact, 1 collections, 1 segment objects, SSD cache 6,746 B, restored local segments 0 B |
| Root-loss recovery | 2,788 ms | source deleted=True; 18 exact; 2 absent; 1 collections; 2 log entries; 1 remote segments; 2 artifacts/10,247 B preflighted; SSD cache 6,746 B; local segments after eviction 0 B (restore 1,768 ms, cold reopen 1,019 ms); checkpoint `root-loss-recovery-checkpoint.json` |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 22 ms | 21 records, 2 tombstones |
| Compaction verify | 54 ms | 18 exact matches |

Full artifact plaintext SHA-256: `1d7013ba4dc9744990aefc0967489f735803cade2a18ce96d199d282f589545a`
Incremental artifact plaintext SHA-256: `a158fdf90749948f93ea2937ac6cea0cb65d455cac88e946f43975e30cc420c8`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | 2,147,483,648 B |
| Peak local bytes | 67,346,916 B |
| Peak source bytes | 67,190,814 B |
| Peak target bytes | 67,168,907 B |
| Peak upload/checkpoint bytes | 533 B |
| Peak temporary spool bytes | 10,247 B |
| Peak RustFS data-root bytes | 0 B |
| Peak SSD cache bytes | 9,647 B |
| Minimum free bytes observed | 376,616,976,384 B (floor 1,073,741,824 B) |
| Peak RSS | 172,003,328 B |
| RustFS objects | 7 |
| RustFS bytes | 35,812 B |
| Swap current at final sample | 3,334,230,016 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rootloss-smoke-20260923T102249Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rootloss-smoke-20260923T102249Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query diagnostics: skipped.