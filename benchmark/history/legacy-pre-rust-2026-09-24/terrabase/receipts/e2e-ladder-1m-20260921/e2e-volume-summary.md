# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-21T23:45:54.1415560+00:00

1,000,000 deterministic random documents written in batches of 5,000, verified by exact read-back and point reads, backed up (full + incremental around 25,000 updates and 25,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 22,284 ms | 44,876 docs/s |
| Relationship write | 1,510 ms | 10,000 orders + users through the schema store |
| Relationship verify | 80,791 ms | related sets, traversal, restrict delete |
| Read-back verify | 8,691 ms | 1,000,000 exact matches |
| Point reads | 422 ms | 704,797 ops/s, p50 0.001 ms, p95 0.002 ms, p99 0.003 ms |
| Full backup | 40,730 ms | artifact 110,074,889 B |
| Bulk updates | 820 ms | 25,000 documents to v2 |
| Bulk deletes | 265 ms | 25,000 tombstones |
| Incremental backup | 50,151 ms | artifact 51,904,543 B |
| Full-boundary restore | 36,937 ms | 0 log entries |
| Full restore verify | 8,487 ms | 1,000,000 exact matches |
| PITR restore | 49,037 ms | 5 log entries |
| PITR restore verify | 11,884 ms | 1,000,000 exact matches |
| Final restore | 37,168 ms | full incremental chain |
| Final restore verify | 12,529 ms | 975,000 exact, 25,000 absent |
| Index build | 126,774 ms | automatic index on sequence, active |
| Indexed queries | 215,348 ms | 5 ops/s, p50 5.981 ms, p95 13.348 ms, p99 25.193 ms |
| Compaction | 0 ms | 0 records, 0 tombstones |
| Compaction verify | 0 ms | 975,000 exact matches |

Full artifact plaintext SHA-256: `4d692e405461484eedbcdb4b773759619a0200d93c83c984aa0269a19cdc96d3`
Incremental artifact plaintext SHA-256: `f9b8d1448559f1c74cc2408053eb65464bc3a2a2331a14fe217f7e6558cb419d`