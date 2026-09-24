# Corrected-source deterministic index comparison

One local Release trial completed with 2,000 documents, 30 repeat samples,
two automatic indexes, and 100 post-build maintenance updates. All eight
comparison rows returned the expected document IDs. Candidate selection saw
zero query-history observations. Jev was `UNRUN`; the command omitted `--jev`
and removed `TERRABASE_JEV_API_KEY` from its environment.

| Measure | Observation |
| --- | ---: |
| Collection logical index cap | 1,048,576 B |
| Retained encoded document/entry bytes | 1,004,100 B |
| Recognized WAL / WAL-compaction staging | 104 B / 0 B |
| Whole-root index file delta | 449,808 B |
| Automatic-index build | 21,192.5 ms |
| Status / nested repeat p50 | 34.1 µs / 31.6 µs |
| Cold first status query | 877,261.2 µs |
| Same-engine no-index first status query | 95,550.6 µs |

The cold first status query regressed in this trial, while repeat indexed
queries were faster than the same-engine scans. This is one process and one
seeded trial, so it does not establish a latency distribution or performance
superiority. The WAL figures are quiescent recognized-file subsets; they do
not measure total compaction work or write amplification.

See the [full report](query-independent-indexing.md) and
[run receipt](run-receipt.json) for all lanes, first-query costs, SQLite plans,
resource settings, and source identity.
