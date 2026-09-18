# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `3d12aea0d7c8d6fe2e1c39506afc7b5d7b05204b` (dirty: `True`)
- Artifact SHA-256: `2e5a1f05961f3cfc2a4d0a33fa2d3bb3562bea486d1815690a2ca9ee11ad94aa`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20,937,965,568 bytes
- GC: server `False`, latency mode `Interactive`

## Comparators

| Engine | Package | Version | Assembly SHA-256 | Native runtime SHA-256 |
| --- | --- | --- | --- | --- |
|sqlite | Microsoft.Data.Sqlite | 10.0.12 | `b98d957462be432895b00dccca012d3c127e118d3d4262418014b92c7bbd66cf` | `—` |
|litedb | LiteDB | 6.0.0-prerelease.181+Branch.dev.Sha.a50661a9d1a25b5713586d0096c6325d76bf5dfe.a50661a9d1a25b5713586d0096c6325d76bf5dfe | `161aa0c8acd86287b4c796fd2c92e637246f39450a45ff6d2ee06d6a8894e66b` | `—` |
|turso | Turso.Data.Sqlite.Provider | 0.8.0-pre.11 | `14cb8c93db582aa0638e6774d7a0433aca3f5346d8cfa8d06985ac12bd06c314` | `4be766c05ff6a48959498df47f66212f829d761cd0074276dc6aae23097a6985` |

## Boundaries

TerraBase is driven through its HTTP document API (PUT/GET/bounded JSON-DSL query) against a real in-process host using the durable StorageEngine backend. SQLite (Microsoft.Data.Sqlite), LiteDB, and Turso (Turso.Data.Sqlite.Provider over the Turso native runtime) are embedded in-process engines. All four share one process, one C#/.NET runtime, and one resource envelope; each case owns a fresh database seeded with the same documents.

## Results

| Engine | Sync | Lane | Conc | Status | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | Bytes written | Query plan | Raw samples |
| --- | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
|sqlite | FULL | read | 1 | complete | 56353.90 | 13.02 | 34.73 | 52.65 | 1625.48 | 2.52 | 70,561,792 | 0 | — | `raw-samples-sqlite-full-read-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 63061.64 | 12.54 | 27.82 | 44.70 | 1631.00 | 2.59 | 72,966,144 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|sqlite | FULL | write | 1 | complete | 1699.82 | 569.94 | 987.97 | 2700.43 | 1021.16 | 0.56 | 75,907,072 | 118,832 | — | `raw-samples-sqlite-full-write-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 3976.98 | 29.99 | 803.97 | 2058.86 | 1429.88 | 0.81 | 79,896,576 | 803,400 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |
|sqlite | FULL | read | 16 | complete | 141743.44 | 13.54 | 59.52 | 92.99 | 1664.72 | 11.17 | 84,860,928 | 0 | — | `raw-samples-sqlite-full-read-c16.jsonl` |
|sqlite | FULL | write | 16 | complete | 1628.32 | 8863.86 | 14805.83 | 15693.95 | 1783.96 | 1.58 | 90,656,768 | 118,832 | — | `raw-samples-sqlite-full-write-c16.jsonl` |
|sqlite | NORMAL | write | 1 | complete | 15277.09 | 33.59 | 76.49 | 123.19 | 1148.00 | 1.76 | 93,372,416 | 118,832 | — | `raw-samples-sqlite-normal-write-c1.jsonl` |
|sqlite | NORMAL | mixed | 1 | complete | 40255.22 | 15.80 | 58.13 | 85.21 | 1558.00 | 5.05 | 93,822,976 | 803,400 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|litedb | — | read | 1 | complete | 5026.26 | 177.30 | 239.78 | 387.27 | 24211.36 | 2.09 | 102,182,912 | 0 | — | `raw-samples-litedb-read-c1.jsonl` |
|litedb | — | query | 1 | complete | 5225.64 | 166.30 | 240.49 | 545.73 | 23162.08 | 2.18 | 104,300,544 | 0 | IndexLookup | `raw-samples-litedb-query-c1.jsonl` |
|litedb | — | write | 1 | complete | 2514.72 | 313.82 | 548.38 | 2003.76 | 31363.00 | 3.63 | 111,132,672 | 966,656 | — | `raw-samples-litedb-write-c1.jsonl` |
|litedb | — | mixed | 1 | complete | 4363.66 | 185.36 | 367.53 | 459.11 | 34627.20 | 2.05 | 119,963,648 | 2,555,904 | IndexLookup | `raw-samples-litedb-mixed-c1.jsonl` |
|litedb | — | read | 16 | complete | 29044.86 | 251.22 | 1697.37 | 2439.88 | 25287.68 | 17.52 | 121,610,240 | 0 | — | `raw-samples-litedb-read-c16.jsonl` |
|litedb | — | write | 16 | complete | 2161.29 | 358.18 | 26879.86 | 46790.46 | 33994.96 | 3.35 | 133,459,968 | 81,920 | — | `raw-samples-litedb-write-c16.jsonl` |
|turso | — | read | 1 | complete | 4004.16 | 244.08 | 284.67 | 294.13 | 3999.96 | 2.21 | 146,030,592 | 0 | — | `raw-samples-turso-read-c1.jsonl` |
|turso | — | query | 1 | complete | 4168.22 | 232.93 | 270.22 | 278.24 | 3977.00 | 2.23 | 146,636,800 | 0 | IndexSeek | `raw-samples-turso-query-c1.jsonl` |
|turso | — | write | 1 | complete | 959.82 | 934.88 | 1408.62 | 2702.41 | 4121.44 | 2.40 | 148,377,600 | 0 | — | `raw-samples-turso-write-c1.jsonl` |
|turso | — | mixed | 1 | complete | 1818.91 | 324.46 | 1158.27 | 1861.47 | 3949.36 | 2.70 | 150,048,768 | 824,000 | IndexSeek | `raw-samples-turso-mixed-c1.jsonl` |
|turso | — | read | 16 | complete | 62013.58 | 173.63 | 234.36 | 274.40 | 4137.08 | 24.92 | 151,183,360 | 0 | — | `raw-samples-turso-read-c16.jsonl` |
|turso | — | write | 16 | complete | 1081.16 | 14499.62 | 18589.92 | 19699.23 | 4468.60 | 1.64 | 151,306,240 | 0 | — | `raw-samples-turso-write-c16.jsonl` |
|terrabase-http | — | read | 1 | complete | 1547.48 | 498.30 | 752.23 | 2453.00 | 24856.40 | 3.38 | 206,000,128 | 0 | — | `raw-samples-terrabase-http-read-c1.jsonl` |
|terrabase-http | — | query | 1 | complete | 786.96 | 931.96 | 2220.81 | 8641.50 | 159160.00 | 3.32 | 189,394,944 | 0 | EqualitySeek | `raw-samples-terrabase-http-query-c1.jsonl` |
|terrabase-http | — | write | 1 | complete | 410.74 | 1950.75 | 4000.29 | 6526.66 | 270107.44 | 2.60 | 197,799,936 | 368,089 | — | `raw-samples-terrabase-http-write-c1.jsonl` |
|terrabase-http | — | mixed | 1 | complete | 186.03 | 1006.52 | 26220.80 | 138974.68 | 375558.08 | 2.59 | 204,058,624 | 191,378 | EqualitySeek | `raw-samples-terrabase-http-mixed-c1.jsonl` |
|terrabase-http | — | read | 16 | complete | 9941.00 | 358.50 | 4097.27 | 6083.22 | 11648.72 | 11.63 | 211,075,072 | 0 | — | `raw-samples-terrabase-http-read-c16.jsonl` |
|terrabase-http | — | write | 16 | complete | 579.88 | 26322.18 | 33298.69 | 37906.31 | 308864.56 | 3.58 | 211,492,864 | 345,862 | — | `raw-samples-terrabase-http-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 3976.98 | 4363.66 | 1818.91 | 186.03 | 0.05 |
|query | 1 | 63061.64 | 5225.64 | 4168.22 | 786.96 | 0.01 |
|read | 1 | 56353.90 | 5026.26 | 4004.16 | 1547.48 | 0.03 |
|write | 1 | 1699.82 | 2514.72 | 959.82 | 410.74 | 0.24 |
|read | 16 | 141743.44 | 29044.86 | 62013.58 | 9941.00 | 0.07 |
|write | 16 | 1628.32 | 2161.29 | 1081.16 | 579.88 | 0.36 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 29.99 | 185.36 | 324.46 | 1006.52 | 33.56 |
|query | 1 | 12.54 | 166.30 | 232.93 | 931.96 | 74.31 |
|read | 1 | 13.02 | 177.30 | 244.08 | 498.30 | 38.28 |
|write | 1 | 569.94 | 313.82 | 934.88 | 1950.75 | 3.42 |
|read | 16 | 13.54 | 251.22 | 173.63 | 358.50 | 26.47 |
|write | 16 | 8863.86 | 358.18 | 14499.62 | 26322.18 | 2.97 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --dataset-size 50 --samples 5 --operations 200 --warmup 50 --output bench/receipts/engine-comparison-smoke`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
