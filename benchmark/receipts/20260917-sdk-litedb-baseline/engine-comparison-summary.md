# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `145fd96c43009c9c7bd0ef6b9705cd50056f713b` (dirty: `True`)
- Artifact SHA-256: `22e4a475fb87efaa32a3d352227e561fe35c13c0f67feddb5b2ebfa34f19a4a2`
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
|litedb | — | read | 1 | complete | 16545.43 | 43.75 | 81.49 | 121.55 | 34863.02 | 37.91 | 103,370,752 | 0 | — | `raw-samples-litedb-read-c1.jsonl` |
|litedb | — | query | 1 | complete | 666.19 | 2069.82 | 3689.11 | 4385.48 | 33736.94 | 25.01 | 122,904,576 | 0 | IndexLookup | `raw-samples-litedb-query-c1.jsonl` |
|litedb | — | write | 1 | complete | 3976.90 | 175.35 | 278.42 | 916.14 | 44300.90 | 23.34 | 119,676,928 | 7,299,072 | — | `raw-samples-litedb-write-c1.jsonl` |
|litedb | — | mixed | 1 | complete | 2395.78 | 83.77 | 2505.73 | 3250.33 | 35990.98 | 24.38 | 125,911,040 | 475,136 | IndexLookup | `raw-samples-litedb-mixed-c1.jsonl` |
|litedb | — | read | 16 | complete | 39638.53 | 69.09 | 92.66 | 1095.70 | 34823.24 | 82.79 | 120,864,768 | 0 | — | `raw-samples-litedb-read-c16.jsonl` |
|litedb | — | write | 16 | complete | 3480.19 | 230.30 | 350.23 | 31205.60 | 46649.95 | 26.86 | 187,551,744 | 7,045,120 | — | `raw-samples-litedb-write-c16.jsonl` |
|terrabase-sdk | — | read | 1 | complete | 4413.94 | 221.08 | 277.18 | 329.77 | 11774.81 | 73.48 | 227,172,352 | 0 | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |
|terrabase-sdk | — | query | 1 | complete | 2433.99 | 407.80 | 467.40 | 1454.72 | 102455.98 | 47.90 | 208,293,888 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-query-c1.jsonl` |
|terrabase-sdk | — | write | 1 | complete | 1096.85 | 749.41 | 1899.81 | 3395.55 | 120746.03 | 36.65 | 322,105,344 | 1,769,929 | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |
|terrabase-sdk | — | mixed | 1 | complete | 1991.79 | 367.39 | 942.10 | 2383.63 | 62604.36 | 45.42 | 378,286,080 | 543,025 | EqualitySeek | `raw-samples-terrabase-sdk-mixed-c1.jsonl` |
|terrabase-sdk | — | read | 16 | complete | 20704.99 | 711.11 | 969.70 | 2827.74 | 11760.11 | 92.62 | 307,273,728 | 0 | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |
|terrabase-sdk | — | write | 16 | complete | 1867.71 | 7917.91 | 14020.80 | 16874.17 | 116993.44 | 31.65 | 321,527,808 | 1,429,205 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-sdk,litedb --output bench/receipts/20260917-sdk-litedb-baseline`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
