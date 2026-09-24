# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `3d12aea0d7c8d6fe2e1c39506afc7b5d7b05204b` (dirty: `True`)
- Artifact SHA-256: `5ce695844660630f7739669173ca07af43db079fa04436b0f2213890769a8071`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 4 logical processors; available memory 4,294,967,296 bytes
- GC: server `False`, latency mode `Interactive`

## Comparators

| Engine | Package | Version | Assembly SHA-256 | Native runtime SHA-256 |
| --- | --- | --- | --- | --- |
|sqlite | Microsoft.Data.Sqlite | 10.0.12 | `b98d957462be432895b00dccca012d3c127e118d3d4262418014b92c7bbd66cf` | `—` |
|litedb | LiteDB | 6.0.0-prerelease.181+Branch.dev.Sha.a50661a9d1a25b5713586d0096c6325d76bf5dfe.a50661a9d1a25b5713586d0096c6325d76bf5dfe | `161aa0c8acd86287b4c796fd2c92e637246f39450a45ff6d2ee06d6a8894e66b` | `—` |
|turso | Turso.Data.Sqlite.Provider | 0.8.0-pre.11 | `14cb8c93db582aa0638e6774d7a0433aca3f5346d8cfa8d06985ac12bd06c314` | `4be766c05ff6a48959498df47f66212f829d761cd0074276dc6aae23097a6985` |

## Boundaries

TerraBase runs in three lanes: the `terrabase-engine` lane drives the durable StorageEngine and AutomaticIndexRuntime directly in-process (no HTTP — the same comparison class as the embedded engines), while the `terrabase-http` lane serves the real HTTP document API (PUT/GET/bounded JSON-DSL query) over a Unix-socket host using a bare HttpClient, and the `terrabase-sdk` lane drives that host through the official TerraScale.DotNet SDK (`TerraScaleClient`) as the interface. SQLite (Microsoft.Data.Sqlite), LiteDB, and Turso (Turso.Data.Sqlite.Provider over the Turso native runtime) are embedded in-process engines. All engines share one process, one C#/.NET runtime, and one resource envelope; each case owns a fresh database seeded with the same documents.

## Results

| Engine | Sync | Lane | Conc | Status | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | Bytes written | Query plan | Raw samples |
| --- | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
|sqlite | FULL | read | 1 | complete | 74343.97 | 10.64 | 19.59 | 32.01 | 1479.78 | 24.45 | 83,189,760 | 0 | — | `raw-samples-sqlite-full-read-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 90806.64 | 8.09 | 16.96 | 31.47 | 1450.55 | 42.41 | 88,850,432 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|sqlite | FULL | write | 1 | complete | 1672.50 | 542.40 | 1255.54 | 2756.12 | 850.36 | 5.23 | 95,461,376 | 4,022,272 | — | `raw-samples-sqlite-full-write-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 4939.72 | 20.42 | 780.28 | 1354.93 | 1262.05 | 7.02 | 95,756,288 | 1,187,840 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |
|sqlite | FULL | read | 16 | complete | 289074.99 | 9.92 | 21.54 | 48.75 | 1436.66 | 83.87 | 101,560,320 | 0 | — | `raw-samples-sqlite-full-read-c16.jsonl` |
|sqlite | FULL | write | 16 | complete | 1589.63 | 9141.73 | 15402.33 | 19967.16 | 1411.90 | 10.95 | 105,590,784 | 4,022,272 | — | `raw-samples-sqlite-full-write-c16.jsonl` |
|sqlite | NORMAL | write | 1 | complete | 12101.96 | 49.43 | 110.41 | 197.21 | 850.36 | 20.85 | 104,173,568 | 4,022,272 | — | `raw-samples-sqlite-normal-write-c1.jsonl` |
|sqlite | NORMAL | mixed | 1 | complete | 26591.85 | 18.91 | 74.21 | 115.59 | 1262.77 | 20.56 | 104,275,968 | 1,187,840 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|terrabase-engine | — | read | 1 | complete | 354475.43 | 2.35 | 3.50 | 4.91 | 140.84 | 25.92 | 126,283,776 | 0 | — | `raw-samples-terrabase-engine-read-c1.jsonl` |
|terrabase-engine | — | query | 1 | complete | 12708.98 | 58.82 | 107.06 | 165.66 | 28748.11 | 29.85 | 142,082,048 | 0 | EqualitySeek | `raw-samples-terrabase-engine-query-c1.jsonl` |
|terrabase-engine | — | write | 1 | complete | 1333.94 | 658.36 | 1113.24 | 2580.10 | 20991.19 | 9.57 | 239,919,104 | 7,264,528 | — | `raw-samples-terrabase-engine-write-c1.jsonl` |
|terrabase-engine | — | mixed | 1 | complete | 3169.71 | 71.98 | 926.11 | 2089.89 | 13338.80 | 13.60 | 299,237,376 | 2,256,860 | EqualitySeek | `raw-samples-terrabase-engine-mixed-c1.jsonl` |
|terrabase-engine | — | read | 16 | complete | 503884.95 | 3.34 | 6.09 | 15.12 | 142.35 | 75.23 | 208,424,960 | 0 | — | `raw-samples-terrabase-engine-read-c16.jsonl` |
|terrabase-engine | — | write | 16 | complete | 1326.91 | 654.37 | 13224.27 | 237505.37 | 20677.31 | 8.83 | 246,337,536 | 7,123,195 | — | `raw-samples-terrabase-engine-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 4939.72 | — | — | 3169.71 | — | — | 0.64 |
|query | 1 | 90806.64 | — | — | 12708.98 | — | — | 0.14 |
|read | 1 | 74343.97 | — | — | 354475.43 | — | — | 4.77 |
|write | 1 | 1672.50 | — | — | 1333.94 | — | — | 0.80 |
|read | 16 | 289074.99 | — | — | 503884.95 | — | — | 1.74 |
|write | 16 | 1589.63 | — | — | 1326.91 | — | — | 0.83 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 20.42 | — | — | 71.98 | — | — | 3.52 |
|query | 1 | 8.09 | — | — | 58.82 | — | — | 7.27 |
|read | 1 | 10.64 | — | — | 2.35 | — | — | 0.22 |
|write | 1 | 542.40 | — | — | 658.36 | — | — | 1.21 |
|read | 16 | 9.92 | — | — | 3.34 | — | — | 0.34 |
|write | 16 | 9141.73 | — | — | 654.37 | — | — | 0.07 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-engine,sqlite --output bench/receipts/20260917-engine-sqlite-parity-v1`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
