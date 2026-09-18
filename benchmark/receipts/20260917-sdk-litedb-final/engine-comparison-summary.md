# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `145fd96c43009c9c7bd0ef6b9705cd50056f713b` (dirty: `True`)
- Artifact SHA-256: `b61f2676024036ed13310a940d281e9934d649d796650ea6e2dbc7cf411ef7e1`
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
|litedb | — | read | 1 | complete | 12587.93 | 71.42 | 95.91 | 115.18 | 34862.76 | 35.96 | 103,075,840 | 0 | — | `raw-samples-litedb-read-c1.jsonl` |
|litedb | — | query | 1 | complete | 734.96 | 2028.42 | 3188.93 | 4205.86 | 33728.74 | 25.09 | 129,855,488 | 0 | IndexLookup | `raw-samples-litedb-query-c1.jsonl` |
|litedb | — | write | 1 | complete | 4411.67 | 169.45 | 261.35 | 819.62 | 44300.69 | 24.20 | 125,145,088 | 7,299,072 | — | `raw-samples-litedb-write-c1.jsonl` |
|litedb | — | mixed | 1 | complete | 2118.12 | 99.51 | 2590.02 | 4170.30 | 35973.06 | 24.44 | 131,645,440 | 475,136 | IndexLookup | `raw-samples-litedb-mixed-c1.jsonl` |
|litedb | — | read | 16 | complete | 31689.86 | 67.72 | 95.51 | 2943.07 | 34828.42 | 68.21 | 127,029,248 | 0 | — | `raw-samples-litedb-read-c16.jsonl` |
|litedb | — | write | 16 | complete | 3102.40 | 258.46 | 384.16 | 23832.14 | 46820.56 | 26.05 | 199,618,560 | 5,963,776 | — | `raw-samples-litedb-write-c16.jsonl` |
|terrabase-sdk | — | read | 1 | complete | 9838.62 | 88.77 | 152.84 | 393.49 | 12019.82 | 42.04 | 227,893,248 | 0 | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |
|terrabase-sdk | — | query | 1 | complete | 6022.79 | 129.97 | 222.98 | 1174.68 | 103918.99 | 24.74 | 206,823,424 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-query-c1.jsonl` |
|terrabase-sdk | — | write | 1 | complete | 1428.84 | 543.36 | 1672.96 | 2923.89 | 39091.67 | 24.08 | 317,968,384 | 1,771,873 | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |
|terrabase-sdk | — | mixed | 1 | complete | 3370.65 | 160.76 | 691.08 | 2093.24 | 40283.13 | 27.79 | 373,858,304 | 528,415 | EqualitySeek | `raw-samples-terrabase-sdk-mixed-c1.jsonl` |
|terrabase-sdk | — | read | 16 | complete | 38113.94 | 70.86 | 104.41 | 1296.35 | 11838.14 | 90.06 | 303,210,496 | 0 | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |
|terrabase-sdk | — | write | 16 | complete | 1818.35 | 3026.40 | 6157.22 | 11906.52 | 35656.29 | 24.00 | 322,306,048 | 1,297,375 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |
|terrabase-sdk | ASYNC | read | 1 | complete | 18463.76 | 43.80 | 87.72 | 101.59 | 11840.24 | 25.86 | 342,499,328 | 0 | — | `raw-samples-terrabase-sdk-async-read-c1.jsonl` |
|terrabase-sdk | ASYNC | query | 1 | complete | 6259.99 | 127.97 | 217.31 | 1080.67 | 103919.67 | 24.99 | 252,006,400 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-async-query-c1.jsonl` |
|terrabase-sdk | ASYNC | write | 1 | complete | 3766.62 | 230.82 | 293.61 | 436.05 | 39464.34 | 53.25 | 355,663,872 | 1,755,115 | — | `raw-samples-terrabase-sdk-async-write-c1.jsonl` |
|terrabase-sdk | ASYNC | mixed | 1 | complete | 6007.16 | 150.78 | 285.16 | 451.64 | 40006.01 | 40.75 | 398,209,024 | 522,955 | EqualitySeek | `raw-samples-terrabase-sdk-async-mixed-c1.jsonl` |
|terrabase-sdk | ASYNC | read | 16 | complete | 45851.09 | 70.56 | 94.23 | 381.95 | 11838.60 | 86.35 | 319,651,840 | 0 | — | `raw-samples-terrabase-sdk-async-read-c16.jsonl` |
|terrabase-sdk | ASYNC | write | 16 | complete | 6098.92 | 244.88 | 2962.32 | 6837.11 | 35311.66 | 62.20 | 336,736,256 | 1,232,444 | — | `raw-samples-terrabase-sdk-async-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-sdk,litedb --output bench/receipts/20260917-sdk-litedb-final`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
