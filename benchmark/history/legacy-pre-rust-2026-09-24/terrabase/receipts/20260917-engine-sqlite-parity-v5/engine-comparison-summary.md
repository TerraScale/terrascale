# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `3d12aea0d7c8d6fe2e1c39506afc7b5d7b05204b` (dirty: `True`)
- Artifact SHA-256: `a5ddc314c411a4d325daa931c624ce1fb1031268857543a0c4804e6176e06dee`
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
|sqlite | FULL | read | 1 | complete | 71034.53 | 13.04 | 18.07 | 27.49 | 1527.72 | 24.93 | 82,567,168 | 0 | — | `raw-samples-sqlite-full-read-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 62946.17 | 12.65 | 20.06 | 35.20 | 1450.53 | 47.33 | 89,776,128 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|sqlite | FULL | write | 1 | complete | 2293.98 | 337.54 | 715.41 | 1565.92 | 850.37 | 6.58 | 95,199,232 | 4,022,272 | — | `raw-samples-sqlite-full-write-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 6914.45 | 17.56 | 583.88 | 711.77 | 1262.75 | 8.21 | 95,895,552 | 1,187,840 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |
|sqlite | FULL | read | 16 | complete | 253216.48 | 10.73 | 38.25 | 60.83 | 1436.18 | 83.10 | 102,895,616 | 0 | — | `raw-samples-sqlite-full-read-c16.jsonl` |
|sqlite | FULL | write | 16 | complete | 1935.64 | 8039.91 | 11143.46 | 14419.74 | 1412.03 | 12.71 | 109,162,496 | 4,022,272 | — | `raw-samples-sqlite-full-write-c16.jsonl` |
|sqlite | NORMAL | write | 1 | complete | 15836.33 | 40.14 | 78.19 | 118.91 | 851.18 | 20.48 | 100,483,072 | 4,022,272 | — | `raw-samples-sqlite-normal-write-c1.jsonl` |
|sqlite | NORMAL | mixed | 1 | complete | 37049.37 | 15.28 | 49.48 | 75.14 | 1262.75 | 21.84 | 100,904,960 | 1,187,840 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|terrabase-engine | — | read | 1 | complete | 189087.39 | 4.95 | 6.63 | 11.14 | 237.60 | 27.33 | 120,127,488 | 0 | — | `raw-samples-terrabase-engine-read-c1.jsonl` |
|terrabase-engine | — | query | 1 | complete | 69619.46 | 12.67 | 16.17 | 30.19 | 2459.32 | 49.21 | 129,650,688 | 0 | EqualitySeek | `raw-samples-terrabase-engine-query-c1.jsonl` |
|terrabase-engine | — | write | 1 | complete | 2201.43 | 320.29 | 984.80 | 1561.66 | 21807.38 | 14.52 | 242,307,072 | 1,272,273 | — | `raw-samples-terrabase-engine-write-c1.jsonl` |
|terrabase-engine | — | mixed | 1 | complete | 6050.88 | 23.71 | 521.57 | 893.39 | 11109.97 | 20.28 | 302,821,376 | 459,660 | EqualitySeek | `raw-samples-terrabase-engine-mixed-c1.jsonl` |
|terrabase-engine | — | read | 16 | complete | 1410437.24 | 1.55 | 1.89 | 4.20 | 144.48 | 98.53 | 258,965,504 | 0 | — | `raw-samples-terrabase-engine-read-c16.jsonl` |
|terrabase-engine | — | write | 16 | complete | 2599.23 | 286.06 | 4138.33 | 37259.65 | 21006.46 | 14.71 | 256,401,408 | 1,184,127 | — | `raw-samples-terrabase-engine-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 6914.45 | — | — | 6050.88 | — | — | 0.88 |
|query | 1 | 62946.17 | — | — | 69619.46 | — | — | 1.11 |
|read | 1 | 71034.53 | — | — | 189087.39 | — | — | 2.66 |
|write | 1 | 2293.98 | — | — | 2201.43 | — | — | 0.96 |
|read | 16 | 253216.48 | — | — | 1410437.24 | — | — | 5.57 |
|write | 16 | 1935.64 | — | — | 2599.23 | — | — | 1.34 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 17.56 | — | — | 23.71 | — | — | 1.35 |
|query | 1 | 12.65 | — | — | 12.67 | — | — | 1.00 |
|read | 1 | 13.04 | — | — | 4.95 | — | — | 0.38 |
|write | 1 | 337.54 | — | — | 320.29 | — | — | 0.95 |
|read | 16 | 10.73 | — | — | 1.55 | — | — | 0.14 |
|write | 16 | 8039.91 | — | — | 286.06 | — | — | 0.04 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-engine,sqlite --output bench/receipts/20260917-engine-sqlite-parity-v5`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
