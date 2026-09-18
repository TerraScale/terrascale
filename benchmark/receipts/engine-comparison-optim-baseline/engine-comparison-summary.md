# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `3d12aea0d7c8d6fe2e1c39506afc7b5d7b05204b` (dirty: `True`)
- Artifact SHA-256: `0d6ccdb998876d46db3518d1d31c00d6cc0703882df46c539d44795c11ae46bb`
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

TerraBase is driven through its HTTP document API (PUT/GET/bounded JSON-DSL query) against a real in-process host using the durable StorageEngine backend. SQLite (Microsoft.Data.Sqlite), LiteDB, and Turso (Turso.Data.Sqlite.Provider over the Turso native runtime) are embedded in-process engines. All four share one process, one C#/.NET runtime, and one resource envelope; each case owns a fresh database seeded with the same documents.

## Results

| Engine | Sync | Lane | Conc | Status | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | Bytes written | Query plan | Raw samples |
| --- | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
|terrabase-http | — | read | 1 | complete | 1477.54 | 240.62 | 301.68 | 477.83 | 137138.92 | 40.22 | 167,231,488 | 0 | — | `raw-samples-terrabase-http-read-c1.jsonl` |
|terrabase-http | — | query | 1 | complete | 643.67 | 548.67 | 992.69 | 71015.05 | 440501.25 | 31.77 | 188,620,800 | 0 | EqualitySeek | `raw-samples-terrabase-http-query-c1.jsonl` |
|terrabase-http | — | write | 1 | complete | 266.97 | 3161.59 | 7052.39 | 11550.05 | 2190386.26 | 36.15 | 290,398,208 | 6,317,016 | — | `raw-samples-terrabase-http-write-c1.jsonl` |
|terrabase-http | — | read | 16 | complete | 5770.04 | 641.63 | 4719.01 | 85556.80 | 43519.39 | 47.08 | 357,163,008 | 0 | — | `raw-samples-terrabase-http-read-c16.jsonl` |
|terrabase-http | — | write | 16 | complete | 265.87 | 59796.95 | 76334.16 | 81903.03 | 2188061.15 | 39.34 | 281,251,840 | 5,940,403 | — | `raw-samples-terrabase-http-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-http --lanes read,query,write --operations 3000 --warmup 500 --output bench/receipts/engine-comparison-optim-baseline`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
