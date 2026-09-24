# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `34ad2a60f43a5e1be02ba731443bb5a80abc71a9` (dirty: `True`)
- Artifact SHA-256: `3f21740dd95570865a30ac6d48ba69867c8ecd1bcc6175f055df3dd4b0594abd`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
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
|terrabase-sdk | — | read | 1 | complete | 3379.66 | 278.72 | 347.27 | 451.52 | 13121.03 | 40.43 | 153,907,200 | 0 | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |
|terrabase-sdk | — | query | 1 | complete | 4153.57 | 218.06 | 265.96 | 1306.27 | 103750.41 | 43.71 | 176,410,624 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-query-c1.jsonl` |
|terrabase-sdk | — | write | 1 | complete | 1823.92 | 441.80 | 1065.29 | 2367.05 | 37301.55 | 19.40 | 240,918,528 | 818,570 | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |
|terrabase-sdk | — | mixed | 1 | complete | 4134.10 | 120.14 | 564.54 | 1597.05 | 39585.83 | 20.60 | 265,244,672 | 279,871 | EqualitySeek | `raw-samples-terrabase-sdk-mixed-c1.jsonl` |
|terrabase-sdk | — | read | 16 | complete | 46696.28 | 54.50 | 76.82 | 1395.43 | 12574.36 | 85.45 | 226,840,576 | 0 | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |
|terrabase-sdk | — | write | 16 | complete | 2142.02 | 2281.60 | 3514.17 | 8600.35 | 34781.89 | 21.22 | 243,716,096 | 677,311 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |
|terrabase-sdk | ASYNC | read | 1 | complete | 23844.59 | 33.78 | 60.92 | 77.02 | 12572.46 | 47.58 | 256,847,872 | 0 | — | `raw-samples-terrabase-sdk-async-read-c1.jsonl` |
|terrabase-sdk | ASYNC | query | 1 | complete | 7743.64 | 95.41 | 160.27 | 1405.58 | 103716.59 | 24.22 | 211,484,672 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-async-query-c1.jsonl` |
|terrabase-sdk | ASYNC | write | 1 | complete | 4436.32 | 183.14 | 257.02 | 1265.76 | 35863.11 | 36.53 | 279,265,280 | 759,225 | — | `raw-samples-terrabase-sdk-async-write-c1.jsonl` |
|terrabase-sdk | ASYNC | mixed | 1 | complete | 7085.78 | 114.89 | 369.22 | 551.49 | 39788.59 | 28.11 | 287,154,176 | 326,029 | EqualitySeek | `raw-samples-terrabase-sdk-async-mixed-c1.jsonl` |
|terrabase-sdk | ASYNC | read | 16 | complete | 45917.69 | 53.59 | 76.75 | 1166.92 | 12577.54 | 85.85 | 249,655,296 | 0 | — | `raw-samples-terrabase-sdk-async-read-c16.jsonl` |
|terrabase-sdk | ASYNC | write | 16 | complete | 7142.39 | 287.69 | 1218.17 | 5334.83 | 34617.93 | 60.44 | 263,094,272 | 686,515 | — | `raw-samples-terrabase-sdk-async-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | ts-auth SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | ts-auth SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-sdk --samples 5 --operations 5000 --warmup 1000 --seed 1337 --output terrabase/receipts/20260920-terrabase-sdk-after`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
