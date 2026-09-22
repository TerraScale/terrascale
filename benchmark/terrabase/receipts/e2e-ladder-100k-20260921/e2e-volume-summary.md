# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-21T23:21:18.7484399+00:00

100,000 deterministic random documents written in batches of 5,000, verified by exact read-back and point reads, backed up (full + incremental around 10,000 updates and 10,000 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 4,691 ms | 21,317 docs/s |
| Relationship write | 1,331 ms | 10,000 orders + users through the schema store |
| Relationship verify | 65,151 ms | related sets, traversal, restrict delete |
| Read-back verify | 1,275 ms | 100,000 exact matches |
| Point reads | 412 ms | 858,021 ops/s, p50 0.001 ms, p95 0.002 ms, p99 0.003 ms |
| Full backup | 4,885 ms | artifact 11,129,763 B |
| Bulk updates | 320 ms | 10,000 documents to v2 |
| Bulk deletes | 120 ms | 10,000 tombstones |
| Incremental backup | 7,466 ms | artifact 20,703,597 B |
| Full-boundary restore | 3,722 ms | 0 log entries |
| Full restore verify | 701 ms | 100,000 exact matches |
| PITR restore | 6,271 ms | 2 log entries |
| PITR restore verify | 764 ms | 100,000 exact matches |
| Final restore | 4,707 ms | full incremental chain |
| Final restore verify | 1,314 ms | 90,000 exact, 10,000 absent |
| Index build | 12,894 ms | automatic index on sequence, active |
| Indexed queries | 84,588 ms | 6 ops/s, p50 5.087 ms, p95 112.568 ms, p99 5,679.052 ms |
| Compaction | 1,786 ms | 132,501 records, 10,000 tombstones |
| Compaction verify | 1,500 ms | 90,000 exact matches |

Full artifact plaintext SHA-256: `46f9cfbe502599a803d3c9167d227d5e2e4e5dfb6876eaeb9066991a44ad56c0`
Incremental artifact plaintext SHA-256: `aa1a0a8f1b565a1cec4ff85e54d32d61dae7b5ac33740e9783cad28258d6705c`