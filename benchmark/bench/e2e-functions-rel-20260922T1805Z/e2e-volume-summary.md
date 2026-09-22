# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-22T17:20:57.3464515+00:00

10,000 deterministic random documents written in batches of 1,000, verified by exact read-back and point reads, backed up (full + incremental around 100 updates and 100 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 978 ms | 10,225 docs/s |
| Relationship write | 156 ms | 100 orders + users through the schema store |
| Relationship verify | 1,056 ms | related sets, traversal, restrict delete |
| Read-back verify | 452 ms | 10,000 exact matches |
| Point reads | 42 ms | 148,880 ops/s, p50 0.006 ms, p95 0.008 ms, p99 0.020 ms |
| Full backup | 925 ms | artifact 1,101,960 B |
| Bulk updates | 6 ms | 100 documents to v2 |
| Bulk deletes | 3 ms | 100 tombstones |
| Incremental backup | 610 ms | artifact 207,651 B |
| Full-boundary restore | 790 ms | 0 log entries |
| Full restore verify | 229 ms | 10,000 exact matches |
| PITR restore | 542 ms | 1 log entries |
| PITR restore verify | 191 ms | 10,000 exact matches |
| Final restore | 645 ms | full incremental chain |
| Final restore verify | 94 ms | 9,900 exact, 100 absent |
| Index build | 3,723 ms | automatic index on sequence, active |
| Indexed queries | 406 ms | 69 ops/s, p50 6.417 ms, p95 149.593 ms, p99 149.593 ms |
| Compaction | 342 ms | 10,326 records, 100 tombstones |
| Compaction verify | 130 ms | 9,900 exact matches |

Full artifact plaintext SHA-256: `61e73a045673096d7cee2226e3cddec0c649c4497b35688955b2ee8f54ed70d9`
Incremental artifact plaintext SHA-256: `5b06713f15deef66344f6bee48c60e4e8ee4cab0ac55d10d5ccd53ff1241d3f5`