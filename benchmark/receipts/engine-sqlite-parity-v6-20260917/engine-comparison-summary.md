# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `145fd96c43009c9c7bd0ef6b9705cd50056f713b` (dirty: `True`)
- Artifact SHA-256: `fbc5e92d0f242562f71995f5428ecb9c22066ae43c8885fccabca6a520f3280d`
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
|sqlite | NORMAL | write | 1 | complete | 10562.00 | 51.32 | 105.17 | 416.50 | 850.66 | 23.61 | 89,821,184 | 4,022,272 | — | `raw-samples-sqlite-normal-write-c1.jsonl` |
|sqlite | NORMAL | mixed | 1 | complete | 31639.45 | 19.20 | 56.52 | 90.73 | 1262.86 | 24.86 | 97,755,136 | 1,187,840 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|terrabase-engine | — | read | 1 | complete | 571964.58 | 1.47 | 2.59 | 3.66 | 140.84 | 29.25 | 123,138,048 | 0 | — | `raw-samples-terrabase-engine-read-c1.jsonl` |
|sqlite | FULL | read | 1 | complete | 112957.72 | 7.80 | 12.10 | 18.68 | 1436.32 | 25.35 | 123,289,600 | 0 | — | `raw-samples-sqlite-full-read-c1.jsonl` |
|terrabase-engine | — | query | 1 | complete | 42748.63 | 15.65 | 33.80 | 62.80 | 2123.14 | 37.82 | 129,548,288 | 0 | EqualitySeek | `raw-samples-terrabase-engine-query-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 65446.35 | 14.23 | 18.54 | 43.35 | 1451.98 | 49.73 | 129,691,648 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|terrabase-engine | — | write | 1 | complete | 2705.72 | 284.32 | 852.22 | 1348.66 | 20918.64 | 16.63 | 247,836,672 | 1,291,035 | — | `raw-samples-terrabase-engine-write-c1.jsonl` |
|sqlite | FULL | write | 1 | complete | 2058.55 | 370.11 | 819.29 | 2078.63 | 883.74 | 5.91 | 247,906,304 | 4,022,272 | — | `raw-samples-sqlite-full-write-c1.jsonl` |
|terrabase-engine | — | mixed | 1 | complete | 6375.03 | 25.33 | 498.67 | 859.69 | 10780.64 | 21.27 | 306,143,232 | 433,668 | EqualitySeek | `raw-samples-terrabase-engine-mixed-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 6640.85 | 17.84 | 594.48 | 776.09 | 1282.82 | 7.57 | 306,151,424 | 1,187,840 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |
|terrabase-engine | — | read | 16 | complete | 1647229.36 | 1.63 | 2.50 | 4.30 | 142.44 | 101.84 | 267,726,848 | 0 | — | `raw-samples-terrabase-engine-read-c16.jsonl` |
|sqlite | FULL | read | 16 | complete | 264362.83 | 9.96 | 25.30 | 49.98 | 1436.22 | 76.96 | 267,726,848 | 0 | — | `raw-samples-sqlite-full-read-c16.jsonl` |
|terrabase-engine | — | write | 16 | complete | 2965.09 | 267.35 | 5339.60 | 40520.73 | 20359.35 | 15.48 | 268,300,288 | 1,191,754 | — | `raw-samples-terrabase-engine-write-c16.jsonl` |
|sqlite | FULL | write | 16 | complete | 2111.35 | 7259.60 | 9846.85 | 14462.62 | 1449.50 | 13.25 | 272,769,024 | 4,022,272 | — | `raw-samples-sqlite-full-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 6640.85 | — | — | 6375.03 | — | — | 0.96 |
|query | 1 | 65446.35 | — | — | 42748.63 | — | — | 0.65 |
|read | 1 | 112957.72 | — | — | 571964.58 | — | — | 5.06 |
|write | 1 | 2058.55 | — | — | 2705.72 | — | — | 1.31 |
|read | 16 | 264362.83 | — | — | 1647229.36 | — | — | 6.23 |
|write | 16 | 2111.35 | — | — | 2965.09 | — | — | 1.40 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 17.84 | — | — | 25.33 | — | — | 1.42 |
|query | 1 | 14.23 | — | — | 15.65 | — | — | 1.10 |
|read | 1 | 7.80 | — | — | 1.47 | — | — | 0.19 |
|write | 1 | 370.11 | — | — | 284.32 | — | — | 0.77 |
|read | 16 | 9.96 | — | — | 1.63 | — | — | 0.16 |
|write | 16 | 7259.60 | — | — | 267.35 | — | — | 0.04 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-engine,sqlite --output bench/receipts/engine-sqlite-parity-v6-20260917`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
