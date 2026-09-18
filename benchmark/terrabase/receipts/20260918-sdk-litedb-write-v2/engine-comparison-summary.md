# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `f4d1608eca920648276d02e2bcd5c97eebb2b205` (dirty: `True`)
- Artifact SHA-256: `2293b85d0f308d37bd4d56103940ea0655ee93a8b6ae581ccf568f93556dcf58`
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
|litedb | — | read | 1 | complete | 14541.49 | 59.47 | 91.52 | 120.58 | 35031.00 | 37.45 | 102,236,160 | 0 | — | `raw-samples-litedb-read-c1.jsonl` |
|litedb | — | query | 1 | complete | 590.61 | 1998.36 | 3662.71 | 5273.77 | 33681.05 | 24.69 | 123,588,608 | 0 | IndexLookup | `raw-samples-litedb-query-c1.jsonl` |
|litedb | — | write | 1 | complete | 3579.37 | 194.20 | 353.33 | 845.65 | 44300.73 | 24.06 | 121,405,440 | 7,299,072 | — | `raw-samples-litedb-write-c1.jsonl` |
|litedb | — | mixed | 1 | complete | 2292.18 | 103.50 | 2536.70 | 3932.82 | 36063.96 | 24.93 | 127,709,184 | 475,136 | IndexLookup | `raw-samples-litedb-mixed-c1.jsonl` |
|litedb | — | read | 16 | complete | 29732.37 | 71.61 | 100.79 | 2939.27 | 34962.86 | 64.02 | 128,651,264 | 0 | — | `raw-samples-litedb-read-c16.jsonl` |
|litedb | — | write | 16 | complete | 3250.80 | 238.59 | 340.61 | 1110.10 | 46685.44 | 27.19 | 195,211,264 | 6,209,536 | — | `raw-samples-litedb-write-c16.jsonl` |
|terrabase-sdk | — | read | 1 | complete | 11501.30 | 86.81 | 110.96 | 120.05 | 14722.10 | 53.53 | 222,351,360 | 0 | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |
|terrabase-sdk | — | query | 1 | complete | 6284.37 | 152.40 | 194.22 | 885.26 | 108385.89 | 35.15 | 206,315,520 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-query-c1.jsonl` |
|terrabase-sdk | — | write | 1 | complete | 1711.43 | 475.76 | 1090.12 | 1696.03 | 37109.15 | 23.91 | 315,101,184 | 1,712,913 | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |
|terrabase-sdk | — | mixed | 1 | complete | 3725.56 | 155.39 | 829.65 | 1194.63 | 39463.85 | 25.52 | 374,480,896 | 515,844 | EqualitySeek | `raw-samples-terrabase-sdk-mixed-c1.jsonl` |
|terrabase-sdk | — | read | 16 | complete | 36498.71 | 55.33 | 80.28 | 3702.90 | 11128.32 | 67.86 | 297,914,368 | 0 | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |
|terrabase-sdk | — | write | 16 | complete | 2041.46 | 7117.66 | 13167.78 | 19742.93 | 34368.89 | 25.78 | 314,134,528 | 1,338,644 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |
|terrabase-sdk | ASYNC | read | 1 | complete | 15721.01 | 57.04 | 81.02 | 93.19 | 11128.75 | 27.72 | 336,523,264 | 0 | — | `raw-samples-terrabase-sdk-async-read-c1.jsonl` |
|terrabase-sdk | ASYNC | query | 1 | complete | 7265.08 | 106.71 | 195.48 | 859.07 | 102974.87 | 25.00 | 246,591,488 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-async-query-c1.jsonl` |
|terrabase-sdk | ASYNC | write | 1 | complete | 4427.99 | 196.84 | 247.44 | 363.80 | 35673.39 | 44.95 | 341,327,872 | 1,517,874 | — | `raw-samples-terrabase-sdk-async-write-c1.jsonl` |
|terrabase-sdk | ASYNC | mixed | 1 | complete | 5863.50 | 124.02 | 439.20 | 646.00 | 39706.80 | 34.02 | 385,978,368 | 500,805 | EqualitySeek | `raw-samples-terrabase-sdk-async-mixed-c1.jsonl` |
|terrabase-sdk | ASYNC | read | 16 | complete | 50605.73 | 53.93 | 80.84 | 561.62 | 11140.56 | 76.15 | 305,586,176 | 0 | — | `raw-samples-terrabase-sdk-async-read-c16.jsonl` |
|terrabase-sdk | ASYNC | write | 16 | complete | 6531.66 | 201.92 | 3290.11 | 8326.64 | 33818.14 | 59.17 | 331,730,944 | 1,189,103 | — | `raw-samples-terrabase-sdk-async-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-sdk,litedb --output bench/receipts/20260918-sdk-litedb-write-v2`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
