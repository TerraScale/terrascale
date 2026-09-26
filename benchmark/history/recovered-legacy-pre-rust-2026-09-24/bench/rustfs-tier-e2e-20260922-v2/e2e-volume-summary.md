# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-22T18:15:15.1281555+00:00

10,000 deterministic random documents written in batches of 256, verified by exact read-back and point reads, backed up (full + incremental around 100 updates and 100 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 1,319 ms | 7,581 docs/s |
| Relationship write | 188 ms | 100 orders + users through the schema store |
| Relationship verify | 1,289 ms | related sets, traversal, restrict delete |
| Read-back verify | 483 ms | 10,000 exact matches |
| Point reads | 33 ms | 104,537 ops/s, p50 0.008 ms, p95 0.011 ms, p99 0.028 ms |
| Full backup | 1,231 ms | artifact 1,102,164 B |
| Bulk updates | 6 ms | 100 documents to v2 |
| Bulk deletes | 3 ms | 100 tombstones |
| Incremental backup | 1,708 ms | artifact 207,654 B |
| Full-boundary restore | 1,380 ms | 0 log entries |
| Full restore verify | 356 ms | 10,000 exact matches |
| PITR restore | 986 ms | 1 log entries |
| PITR restore verify | 203 ms | 10,000 exact matches |
| Final restore | 702 ms | full incremental chain |
| Final restore verify | 147 ms | 9,900 exact, 100 absent |
| Index build | 3,642 ms | automatic index on sequence, active |
| Indexed queries | 1,617 ms | 84 ops/s, p50 7.798 ms, p95 35.740 ms, p99 152.607 ms |
| Compaction | 151 ms | 10,326 records, 100 tombstones |
| Compaction verify | 168 ms | 9,900 exact matches |

Full artifact plaintext SHA-256: `17e23016bccac7c376789bf1d6555328d302ba232ae20c311e24a8322177672d`
Incremental artifact plaintext SHA-256: `5555b9486c76d0d5711880a2c40f839061384ba288b5907f4e7e493d4ac720f1`