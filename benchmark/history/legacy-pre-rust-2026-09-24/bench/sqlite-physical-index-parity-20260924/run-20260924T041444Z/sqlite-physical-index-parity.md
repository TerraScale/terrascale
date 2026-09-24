# Independent SQLite physical-index parity baseline

Generated 2026-09-24T04:14:45.5357898+00:00. Raw receipt: `sqlite-physical-index-parity-raw.json` (SHA-256 `e6fe139334402dfc78c4823916ad19bcbabc9389d3a9e2a29f7428c3bcc7c7c3`).
The 2,000 held-out base documents plus 100 maintenance documents, two JSON paths, 100-row transaction batches, JSONB body, WAL/FULL durability, prepared query, and ID order match the query-independent workload. Each first call follows EXPLAIN and Prepare; the nested path follows status repeats. Inference and advisor work are absent from these engine timings.

| Trial | Lane | Shape | Result IDs | First µs | Repeat p50 µs | Repeat p95 µs | Seed ms | Index build ms | Maintenance ms | File bytes after seed | File bytes after build | File bytes after maintenance |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | no secondary index | status | 21 | 3371.0 | 1251.5 | 1303.2 | 109.3 | 0.0 | 3.5 | 696096 | 696096 | 708456 |
| 1 | no secondary index | nested profile.segment | 21 | 1495.8 | 1489.6 | 1519.5 | 109.3 | 0.0 | 3.5 | 696096 | 696096 | 708456 |
| 1 | two explicit physical indexes | status | 21 | 100.0 | 81.9 | 107.9 | 50.0 | 5.3 | 2.5 | 696096 | 762016 | 823816 |
| 1 | two explicit physical indexes | nested profile.segment | 21 | 79.0 | 80.9 | 98.9 | 50.0 | 5.3 | 2.5 | 696096 | 762016 | 823816 |
| 2 | no secondary index | status | 21 | 837.7 | 777.1 | 899.6 | 37.8 | 0.0 | 3.4 | 696096 | 696096 | 708456 |
| 2 | no secondary index | nested profile.segment | 21 | 958.0 | 959.3 | 1139.6 | 37.8 | 0.0 | 3.4 | 696096 | 696096 | 708456 |
| 2 | two explicit physical indexes | status | 21 | 115.5 | 100.2 | 158.6 | 44.8 | 5.2 | 2.6 | 696096 | 762016 | 823816 |
| 2 | two explicit physical indexes | nested profile.segment | 21 | 99.7 | 101.3 | 155.5 | 44.8 | 5.2 | 2.6 | 696096 | 762016 | 823816 |
| 3 | no secondary index | status | 21 | 1472.7 | 1330.6 | 1553.5 | 49.4 | 0.0 | 3.3 | 696096 | 696096 | 708456 |
| 3 | no secondary index | nested profile.segment | 21 | 926.4 | 900.9 | 1431.2 | 49.4 | 0.0 | 3.3 | 696096 | 696096 | 708456 |
| 3 | two explicit physical indexes | status | 21 | 125.0 | 88.9 | 110.7 | 48.1 | 5.1 | 2.6 | 696096 | 762016 | 823816 |
| 3 | two explicit physical indexes | nested profile.segment | 21 | 110.6 | 89.0 | 100.8 | 48.1 | 5.1 | 2.6 | 696096 | 762016 | 823816 |

All first and repeat ID sequences were checked against independently generated expected IDs. The raw receipt retains every ID, all repeat samples, observed PRAGMAs, query plans, source hashes, and runtime/resource settings.
SQLite's explicit fields are selected with knowledge of the held-out predicates; it is a tuned physical-index oracle, not a query-independent advisor peer. SQLite cache and mmap settings are soft limits, not the TerraBase 8 MiB physical-index admission cap; SQLite has no equivalent admission contract here. A two-engine speed ratio is therefore not claimed by this independent SQLite-only run. Existing TerraBase receipts also include planner and runtime work in their first calls, while these SQLite first calls follow Prepare. OS page cache was not evicted; first means first execution of that predicate in a freshly seeded connection, not a cold-disk query.
Logical file lengths include SQLite database/WAL/SHM at each sampled phase and are not allocated disk blocks or full write amplification. The final length is sampled after maintenance and before query execution. Three fresh seeded trials assess local noise; no confidence interval, crash recovery, sustained writes, or larger-than-memory claim follows from them.
