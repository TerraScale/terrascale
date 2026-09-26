# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `34ad2a60f43a5e1be02ba731443bb5a80abc71a9` (dirty: `True`)
- Artifact SHA-256: `2bcfd0113f02c80fe21667190ecfff8c998d0e74d0285a69a1378c4f4082aea5`
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
|terrabase-sdk | — | read | 1 | complete | 4749.97 | 192.14 | 268.70 | 331.24 | 12982.98 | 39.54 | 153,903,104 | 0 | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |
|terrabase-sdk | — | query | 1 | complete | 3614.06 | 209.57 | 324.88 | 1336.94 | 103751.75 | 42.41 | 180,293,632 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-query-c1.jsonl` |
|terrabase-sdk | — | write | 1 | complete | 1958.13 | 431.10 | 953.99 | 1654.26 | 37426.90 | 20.60 | 240,791,552 | 812,706 | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |
|terrabase-sdk | — | mixed | 1 | complete | 4262.06 | 126.91 | 498.01 | 1230.39 | 39766.78 | 21.04 | 261,345,280 | 277,798 | EqualitySeek | `raw-samples-terrabase-sdk-mixed-c1.jsonl` |
|terrabase-sdk | — | read | 16 | complete | 50295.13 | 53.15 | 75.00 | 2898.32 | 12574.61 | 86.44 | 229,191,680 | 0 | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |
|terrabase-sdk | — | write | 16 | complete | 2243.97 | 4222.19 | 7749.70 | 16319.91 | 34870.41 | 22.62 | 242,958,336 | 663,977 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |
|terrabase-sdk | ASYNC | read | 1 | complete | 27225.63 | 32.55 | 54.82 | 66.51 | 12574.87 | 49.25 | 256,200,704 | 0 | — | `raw-samples-terrabase-sdk-async-read-c1.jsonl` |
|terrabase-sdk | ASYNC | query | 1 | complete | 8471.53 | 96.27 | 139.72 | 1155.77 | 103716.95 | 24.76 | 210,984,960 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-async-query-c1.jsonl` |
|terrabase-sdk | ASYNC | write | 1 | complete | 5473.02 | 140.02 | 214.18 | 306.49 | 36256.44 | 39.50 | 273,858,560 | 787,947 | — | `raw-samples-terrabase-sdk-async-write-c1.jsonl` |
|terrabase-sdk | ASYNC | mixed | 1 | complete | 7266.99 | 111.84 | 365.25 | 475.53 | 39776.13 | 29.04 | 288,817,152 | 323,219 | EqualitySeek | `raw-samples-terrabase-sdk-async-mixed-c1.jsonl` |
|terrabase-sdk | ASYNC | read | 16 | complete | 47813.90 | 53.37 | 77.46 | 4064.71 | 12580.55 | 83.44 | 255,492,096 | 0 | — | `raw-samples-terrabase-sdk-async-read-c16.jsonl` |
|terrabase-sdk | ASYNC | write | 16 | complete | 7477.66 | 1103.17 | 7228.05 | 15451.08 | 30260.03 | 58.35 | 266,715,136 | 425,776 | — | `raw-samples-terrabase-sdk-async-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-sdk --samples 5 --operations 5000 --warmup 1000 --seed 1337 --output terrabase/receipts/20260920-terrabase-sdk-before`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
