# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-20T17:41:04.0301020+00:00

1,000,000 deterministic random documents written in batches of 5,000, verified by exact read-back and point reads, backed up (full + incremental around 25,000 updates and 25,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 20,204 ms | 49,496 docs/s |
| Relationship write | 1,042 ms | 10,000 orders + users through the schema store |
| Relationship verify | 65,327 ms | related sets, traversal, restrict delete |
| Read-back verify | 8,101 ms | 1,000,000 exact matches |
| Point reads | 410 ms | 761,457 ops/s, p50 0.001 ms, p95 0.002 ms, p99 0.003 ms |
| Full backup | 25,953 ms | artifact 110,059,670 B |
| Bulk updates | 1,406 ms | 25,000 documents to v2 |
| Bulk deletes | 258 ms | 25,000 tombstones |
| Incremental backup | 13,776 ms | artifact 51,904,493 B |
| Full-boundary restore | 44,336 ms | 0 log entries |
| Full restore verify | 7,828 ms | 1,000,000 exact matches |
| PITR restore | 35,339 ms | 5 log entries |
| PITR restore verify | 7,930 ms | 1,000,000 exact matches |
| Final restore | 34,801 ms | full incremental chain |
| Final restore verify | 8,150 ms | 975,000 exact, 25,000 absent |
| Index build | 57,202 ms | automatic index on sequence, active |
| Indexed queries | 915,767 ms | 1 ops/s, p50 5.831 ms, p95 15.735 ms, p99 222.203 ms |
| Compaction | 0 ms | 0 records, 0 tombstones |
| Compaction verify | 0 ms | 975,000 exact matches |

Full artifact plaintext SHA-256: `32cd34954e0cc2634f150072964cfad9a4bfc33646a2ef799f4bd364e1e0d075`
Incremental artifact plaintext SHA-256: `d05ddcc412c29646024ad201faff47210687e1a7b218c55799d57e620df49c2c`