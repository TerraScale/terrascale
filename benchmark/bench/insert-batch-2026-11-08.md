# insert-batch: batched INSERT throughput, local single node

One parameterized multi-row `INSERT` per batch into `items (id TEXT PRIMARY KEY, n INTEGER, body TEXT)` over the SQL REST
endpoint (`POST /v1/projects/{project}/databases/{database}/sql/execute`) and the Postgres wire (extended protocol),
against a local TerraBase node started by `benchmark/cmd/insert-bench` with a test-only token issuer.

| Item | Value |
| --- | --- |
| Host | beta, AMD Ryzen 5 PRO 6650H with Radeon Graphics, 12 cores |
| Commit | `3f308f4a38744110304d4ad16cbe8fcdb4e7d234` |
| TerraBase | `/home/mariogk/.t3/worktrees/terrascale/cos-T54-insert-batch-bench/src/TerraBase/bin/Debug/net11.0/terrabase` |
| Batches | 20 measured per size, transport and repeat, 2 warmup batches first; 3 repeats |
| Row | ULID id, integer, ~100-byte body |

| Transport | Rows/batch | p50 ms | p95 ms | rows/s |
| --- | --- | --- | --- | --- |
| rest | 1 | 5.33 | 10.72 | 165 |
| rest | 50 | 6.43 | 8.46 | 7469 |
| rest | 250 | 10.14 | 14.87 | 22616 |
| rest | 1000 | 24.51 | 43.92 | 36724 |
| pg_wire | 1 | 3.69 | 6.31 | 234 |
| pg_wire | 50 | 4.74 | 11.98 | 8592 |
| pg_wire | 250 | 6.34 | 10.66 | 33278 |
| pg_wire | 1000 | refused | refused | refused |

- a 250-row batch takes 10 ms over REST.
- a 250-row batch takes 6 ms over the pg wire.

The pg_wire transport refused the 1000-row batch: the statement exceeds the 16384-byte SQL limit (3000 placeholders).
