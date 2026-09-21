# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-20T13:44:15.6116618+00:00

1,000,000 deterministic random documents written in batches of 5,000, verified by exact read-back and point reads, backed up (full + incremental around 25,000 updates and 25,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 19,503 ms | 51,275 docs/s |
| Read-back verify | 8,076 ms | 1,000,000 exact matches |
| Point reads | 428 ms | 740,849 ops/s, p50 0.001 ms, p95 0.002 ms, p99 0.003 ms |
| Full backup | 19,805 ms | artifact 109,911,582 B |
| Bulk updates | 523 ms | 25,000 documents to v2 |
| Bulk deletes | 391 ms | 25,000 tombstones |
| Incremental backup | 9,897 ms | artifact 51,904,579 B |
| Full-boundary restore | 30,931 ms | 0 log entries |
| Full restore verify | 7,421 ms | 1,000,000 exact matches |
| PITR restore | 30,870 ms | 5 log entries |
| PITR restore verify | 7,459 ms | 1,000,000 exact matches |
| Final restore | 32,938 ms | full incremental chain |
| Final restore verify | 7,755 ms | 975,000 exact, 25,000 absent |
| Index build | 55,507 ms | automatic index on sequence, active |
| Indexed queries | 921,689 ms | 1 ops/s, p50 5.065 ms, p95 9.730 ms, p99 2,394.774 ms |
| Compaction | 0 ms | 0 records, 0 tombstones |
| Compaction verify | 0 ms | 975,000 exact matches |

Full artifact plaintext SHA-256: `ada1a099aabd1c5a2517f203ebf4960fc153a6c07dbc9d096f28693d736d3e54`
Incremental artifact plaintext SHA-256: `2fee5b63d5b75427ce2e7a62156f58a04c0f93be2f1c69679bca0c8cf9a9c39d`