# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-22T19:05:07.9629981+00:00

10,000 deterministic random documents written in batches of 256, verified by exact read-back and point reads, backed up (full + incremental around 100 updates and 100 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,197 ms | 8,355 docs/s |
| Relationship write | 166 ms | 100 orders + users through the schema store |
| Relationship verify | 997 ms | related sets, traversal, restrict delete |
| Read-back verify | 449 ms | 10,000 exact matches |
| Point reads | 19 ms | 176,872 ops/s, p50 0.005 ms, p95 0.006 ms, p99 0.013 ms |
| Full backup | 1,420 ms | artifact 1,102,467 B |
| Bulk updates | 7 ms | 100 documents to v2 |
| Bulk deletes | 3 ms | 100 tombstones |
| Incremental backup | 1,463 ms | artifact 207,651 B |
| Full-boundary restore | 1,307 ms | 0 log entries |
| Full restore verify | 198 ms | 10,000 exact matches |
| PITR restore | 786 ms | 1 log entries |
| PITR restore verify | 247 ms | 10,000 exact matches |
| Final restore | 725 ms | full incremental chain |
| Final restore verify | 190 ms | 9,900 exact, 100 absent |
| Index build | 4,557 ms | automatic index on sequence, active |
| Indexed queries | 2,246 ms | 63 ops/s, p50 9.360 ms, p95 39.855 ms, p99 189.855 ms |
| Compaction | 206 ms | 10,326 records, 100 tombstones |
| Compaction verify | 234 ms | 9,900 exact matches |

Full artifact plaintext SHA-256: `7cacba92410ad60b8bc4fb943a44cfe908eb824e94aae62b42b2fb840c7c9c4e`
Incremental artifact plaintext SHA-256: `66d38f6ccad1bb2ffc4948af140aac3186d9d48ba508595e9a200e6377135cf1`