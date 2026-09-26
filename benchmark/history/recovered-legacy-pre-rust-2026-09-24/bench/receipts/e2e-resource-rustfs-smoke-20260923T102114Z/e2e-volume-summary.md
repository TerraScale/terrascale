# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T10:21:33.3688744+00:00

40 deterministic random documents written in batches of 10, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 4 updates and 4 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,306 ms | 31 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 54 ms | 40 exact matches |
| Point reads | 21 ms | 975 ops/s, p50 1.653 ms, p95 2.158 ms, p99 2.158 ms |
| Full backup | 744 ms | artifact 5,926 B |
| Bulk updates | 19 ms | 4 documents to v2 |
| Bulk deletes | 16 ms | 4 tombstones |
| Incremental backup | 603 ms | artifact 10,753 B |
| Full-boundary restore | 1,755 ms | 0 log entries |
| Full restore verify | 76 ms | 40 exact matches |
| PITR restore | 1,601 ms | 1 log entries |
| PITR restore verify | 87 ms | 40 exact matches |
| Final restore | 1,642 ms | full incremental chain |
| Final restore verify | 63 ms | 36 exact, 4 absent |
| Snapshot document pages | 60 ms | 40 exact documents |
| Snapshot collection pages | 8 ms | 1 collections |
| Cold RustFS reopen | 830 ms | 36 exact, 1 collections, 1 segment objects, SSD cache 13,119 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 49 ms | 41 records, 4 tombstones |
| Compaction verify | 107 ms | 36 exact matches |

Full artifact plaintext SHA-256: `c0e022001c29a7c72923aabf6b6e42f998b466a98b757772c102d664e3c4d234`
Incremental artifact plaintext SHA-256: `7daaa0f7dbd9b9c8a3108cfbddbef0d03f36d96dc13ce5cd65063e314470c28b`
## Resource envelope

| Measure | Value |
| --- | ---: |
| Isolated cgroup | False |
| Configured local cap | 2,147,483,648 B |
| Peak local bytes | 134,537,803 B |
| Peak source bytes | 67,213,705 B |
| Peak target bytes | 67,182,448 B |
| Peak upload/checkpoint bytes | 439 B |
| Peak temporary spool bytes | 27,432 B |
| Peak RustFS data-root bytes | 0 B |
| Peak SSD cache bytes | 18,984 B |
| Minimum free bytes observed | 377,180,565,504 B (floor 1,073,741,824 B) |
| Peak RSS | 170,180,608 B |
| RustFS objects | 6 |
| RustFS bytes | 51,202 B |
| Swap current at final sample | 3,334,709,248 B |
| cgroup oom events at final sample | 0 |
| cgroup oom kill events at final sample | 0 |

Resource samples: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-smoke-20260923T102114Z/resource-samples.jsonl`; progress: `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/e2e-resource-rustfs-smoke-20260923T102114Z/progress.jsonl`; envelope: `resource-envelope.json`.
Indexed query diagnostics: skipped.