# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-20T15:47:40.5746552+00:00

10,000 deterministic random documents written in batches of 1,000, verified by exact read-back and point reads, backed up (full + incremental around 2,000 updates and 2,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 140,280 ms | 71 docs/s |
| Relationship write | 139,510 ms | 2,500 orders + users through the schema store |
| Relationship verify | 14,337 ms | related sets, traversal, restrict delete |
| Read-back verify | 257 ms | 10,000 exact matches |
| Point reads | 204 ms | 879,260 ops/s, p50 0.001 ms, p95 0.002 ms, p99 0.003 ms |
| Full backup | 770 ms | artifact 1,141,697 B |
| Bulk updates | 41 ms | 2,000 documents to v2 |
| Bulk deletes | 23 ms | 2,000 tombstones |
| Incremental backup | 528 ms | artifact 4,130,020 B |
| Full-boundary restore | 742 ms | 0 log entries |
| Full restore verify | 394 ms | 10,000 exact matches |
| PITR restore | 614 ms | 2 log entries |
| PITR restore verify | 85 ms | 10,000 exact matches |
| Final restore | 696 ms | full incremental chain |
| Final restore verify | 56 ms | 8,000 exact, 2,000 absent |
| Index build | 1,727 ms | automatic index on sequence, active |
| Indexed queries | 60,996 ms | 3 ops/s, p50 404.105 ms, p95 474.248 ms, p99 534.979 ms |
| Compaction | 352 ms | 18,126 records, 2,000 tombstones |
| Compaction verify | 106 ms | 8,000 exact matches |

Full artifact plaintext SHA-256: `16b4771b41381f5e0e2c1b919b51c240064dde0756c39cd01f2235ad4e5b7ecd`
Incremental artifact plaintext SHA-256: `a9f721ee0d12f20b6b94f11e9958b7e4c0060e4ece4fcbcdd7ed7e289e270415`