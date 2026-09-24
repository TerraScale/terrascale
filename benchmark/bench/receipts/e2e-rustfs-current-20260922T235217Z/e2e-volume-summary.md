# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-22T23:52:32.8229363+00:00

1,000 deterministic random documents written in batches of 100, verified by exact read-back and point reads, backed up (full + incremental around 20 updates and 20 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 610 ms | 1,640 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | related sets, traversal, restrict delete |
| Read-back verify | 98 ms | 1,000 exact matches |
| Point reads | 6 ms | 18,718 ops/s, p50 0.044 ms, p95 0.071 ms, p99 0.501 ms |
| Full backup | 1,279 ms | artifact 111,210 B |
| Bulk updates | 7 ms | 20 documents to v2 |
| Bulk deletes | 12 ms | 20 tombstones |
| Incremental backup | 945 ms | artifact 43,179 B |
| Full-boundary restore | 899 ms | 0 log entries |
| Full restore verify | 169 ms | 1,000 exact matches |
| PITR restore | 815 ms | 1 log entries |
| PITR restore verify | 131 ms | 1,000 exact matches |
| Final restore | 664 ms | full incremental chain |
| Final restore verify | 83 ms | 980 exact, 20 absent |
| Index build | 1,790 ms | automatic index on sequence, active |
| Indexed queries | 1,094 ms | 32 ops/s, p50 4.931 ms, p95 464.835 ms, p99 464.835 ms |
| Compaction | 342 ms | 1,001 records, 20 tombstones |
| Compaction verify | 231 ms | 980 exact matches |

Full artifact plaintext SHA-256: `dbdcbfeab67b11ede6f8edc2e7a8640efe7cbd0bb143de599f75b7589ef1132e`
Incremental artifact plaintext SHA-256: `4403aef6ff5798dc2d37dc8402b0dd13d696801d0025d4ee612a723e92d472ac`