# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-21T23:04:22.4394129+00:00

10,000 deterministic random documents written in batches of 1,000, verified by exact read-back and point reads, backed up (full + incremental around 2,000 updates and 2,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,415 ms | 7,068 docs/s |
| Relationship write | 622 ms | 2,500 orders + users through the schema store |
| Relationship verify | 19,169 ms | related sets, traversal, restrict delete |
| Read-back verify | 453 ms | 10,000 exact matches |
| Point reads | 434 ms | 144,559 ops/s, p50 0.006 ms, p95 0.009 ms, p99 0.020 ms |
| Full backup | 1,342 ms | artifact 1,136,751 B |
| Bulk updates | 51 ms | 2,000 documents to v2 |
| Bulk deletes | 28 ms | 2,000 tombstones |
| Incremental backup | 888 ms | artifact 4,130,000 B |
| Full-boundary restore | 734 ms | 0 log entries |
| Full restore verify | 193 ms | 10,000 exact matches |
| PITR restore | 673 ms | 2 log entries |
| PITR restore verify | 87 ms | 10,000 exact matches |
| Final restore | 684 ms | full incremental chain |
| Final restore verify | 68 ms | 8,000 exact, 2,000 absent |
| Index build | 2,468 ms | automatic index on sequence, active |
| Indexed queries | 15,633 ms | 13 ops/s, p50 1.035 ms, p95 438.127 ms, p99 533.973 ms |
| Compaction | 361 ms | 18,126 records, 2,000 tombstones |
| Compaction verify | 110 ms | 8,000 exact matches |

Full artifact plaintext SHA-256: `23532fac8d0ec7ce5f2f8f462f46fe73ab25aafc558294a4ed3f6b3d2d2e043e`
Incremental artifact plaintext SHA-256: `32a38c4f8423bf4290d9d55321e187160bc229d7faed8d55d8f918321432e6af`