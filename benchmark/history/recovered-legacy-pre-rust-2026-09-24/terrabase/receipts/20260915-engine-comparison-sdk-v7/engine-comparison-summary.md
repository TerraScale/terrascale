# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `3d12aea0d7c8d6fe2e1c39506afc7b5d7b05204b` (dirty: `True`)
- Artifact SHA-256: `f1e62c05a528c13ad989b9240962c7d23d69c62580f5336691ed6ca1d3dfdcd8`
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

TerraBase is driven through its HTTP document API (PUT/GET/bounded JSON-DSL query) against a real in-process host using the durable StorageEngine backend: the `terrabase-http` lane uses a bare HttpClient while the `terrabase-sdk` lane uses the official TerraScale.DotNet SDK (`TerraScaleClient`) as the interface. SQLite (Microsoft.Data.Sqlite), LiteDB, and Turso (Turso.Data.Sqlite.Provider over the Turso native runtime) are embedded in-process engines. All engines share one process, one C#/.NET runtime, and one resource envelope; each case owns a fresh database seeded with the same documents.

## Results

| Engine | Sync | Lane | Conc | Status | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | Bytes written | Query plan | Raw samples |
| --- | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
|sqlite | FULL | read | 1 | complete | 60739.21 | 11.10 | 24.42 | 41.44 | 1479.96 | 27.06 | 83,107,840 | 0 | — | `raw-samples-sqlite-full-read-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 67779.75 | 13.75 | 19.41 | 34.36 | 1450.57 | 45.29 | 88,793,088 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|sqlite | FULL | write | 1 | complete | 1887.49 | 511.67 | 899.32 | 2242.06 | 850.53 | 6.10 | 95,449,088 | 4,022,272 | — | `raw-samples-sqlite-full-write-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 5904.51 | 21.32 | 644.10 | 971.85 | 1262.61 | 8.12 | 95,358,976 | 1,187,840 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |
|sqlite | FULL | read | 16 | complete | 267882.50 | 10.22 | 36.49 | 58.44 | 1435.84 | 84.17 | 100,950,016 | 0 | — | `raw-samples-sqlite-full-read-c16.jsonl` |
|sqlite | FULL | write | 16 | complete | 1630.16 | 9071.77 | 14384.21 | 21696.72 | 1412.55 | 11.67 | 103,452,672 | 4,022,272 | — | `raw-samples-sqlite-full-write-c16.jsonl` |
|sqlite | NORMAL | write | 1 | complete | 14457.68 | 41.51 | 85.31 | 159.70 | 851.18 | 20.73 | 106,364,928 | 4,022,272 | — | `raw-samples-sqlite-normal-write-c1.jsonl` |
|sqlite | NORMAL | mixed | 1 | complete | 32651.91 | 15.40 | 58.46 | 88.85 | 1262.75 | 20.84 | 107,958,272 | 1,187,840 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|litedb | — | read | 1 | complete | 21490.33 | 40.23 | 68.73 | 118.31 | 34834.33 | 24.68 | 127,508,480 | 0 | — | `raw-samples-litedb-read-c1.jsonl` |
|litedb | — | query | 1 | complete | 705.65 | 2141.58 | 2917.07 | 4548.44 | 33760.55 | 24.59 | 146,313,216 | 0 | IndexLookup | `raw-samples-litedb-query-c1.jsonl` |
|litedb | — | write | 1 | complete | 3918.40 | 181.47 | 288.52 | 843.87 | 44300.66 | 23.49 | 140,398,592 | 7,299,072 | — | `raw-samples-litedb-write-c1.jsonl` |
|litedb | — | mixed | 1 | complete | 2319.49 | 88.51 | 2743.53 | 3422.70 | 35943.52 | 24.44 | 145,633,280 | 475,136 | IndexLookup | `raw-samples-litedb-mixed-c1.jsonl` |
|litedb | — | read | 16 | complete | 45848.24 | 65.08 | 87.40 | 953.92 | 34817.05 | 79.20 | 141,008,896 | 0 | — | `raw-samples-litedb-read-c16.jsonl` |
|litedb | — | write | 16 | complete | 3587.86 | 227.44 | 337.63 | 16069.54 | 46636.21 | 26.54 | 208,326,656 | 6,963,200 | — | `raw-samples-litedb-write-c16.jsonl` |
|turso | — | read | 1 | complete | 1412.00 | 685.94 | 786.87 | 1145.93 | 3650.82 | 23.96 | 187,916,288 | 0 | — | `raw-samples-turso-read-c1.jsonl` |
|turso | — | query | 1 | complete | 1342.53 | 720.61 | 819.77 | 1126.54 | 3626.60 | 23.98 | 189,743,104 | 0 | IndexSeek | `raw-samples-turso-query-c1.jsonl` |
|turso | — | write | 1 | complete | 1042.21 | 881.29 | 1343.32 | 2686.41 | 3642.47 | 7.54 | 175,955,968 | 4,302,576 | — | `raw-samples-turso-write-c1.jsonl` |
|turso | — | mixed | 1 | complete | 185.65 | 6164.29 | 10076.44 | 12231.68 | 3643.31 | 22.99 | 200,609,792 | 2,638,200 | IndexSeek | `raw-samples-turso-mixed-c1.jsonl` |
|turso | — | read | 16 | complete | 3259.23 | 1220.21 | 13158.13 | 16601.17 | 3667.44 | 96.99 | 212,045,824 | 0 | — | `raw-samples-turso-read-c16.jsonl` |
|turso | — | write | 16 | complete | 958.35 | 15798.84 | 22553.21 | 29659.98 | 4204.42 | 11.14 | 223,903,744 | 4,302,576 | — | `raw-samples-turso-write-c16.jsonl` |
|terrabase-http | — | read | 1 | complete | 5370.06 | 182.10 | 224.29 | 263.19 | 9245.52 | 77.17 | 299,020,288 | 0 | — | `raw-samples-terrabase-http-read-c1.jsonl` |
|terrabase-http | — | query | 1 | complete | 1803.26 | 509.97 | 636.87 | 2069.23 | 125061.60 | 42.14 | 317,939,712 | 0 | EqualitySeek | `raw-samples-terrabase-http-query-c1.jsonl` |
|terrabase-http | — | write | 1 | complete | 674.76 | 1286.89 | 2816.89 | 4750.28 | 109362.47 | 20.82 | 390,057,984 | 7,790,688 | — | `raw-samples-terrabase-http-write-c1.jsonl` |
|terrabase-http | — | mixed | 1 | complete | 259.39 | 620.11 | 29971.58 | 40515.17 | 74048.03 | 10.13 | 380,305,408 | 6,283,677 | EqualitySeek | `raw-samples-terrabase-http-mixed-c1.jsonl` |
|terrabase-http | — | read | 16 | complete | 22340.39 | 654.70 | 974.58 | 2843.99 | 9239.90 | 89.87 | 360,120,320 | 0 | — | `raw-samples-terrabase-http-read-c16.jsonl` |
|terrabase-http | — | write | 16 | complete | 893.91 | 15906.20 | 30839.37 | 39638.14 | 107179.27 | 15.95 | 390,270,976 | 7,151,503 | — | `raw-samples-terrabase-http-write-c16.jsonl` |
|terrabase-sdk | — | read | 1 | complete | 4734.64 | 204.97 | 256.25 | 306.76 | 10442.96 | 74.41 | 437,121,024 | 0 | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |
|terrabase-sdk | — | query | 1 | complete | 1605.86 | 587.75 | 700.48 | 2141.10 | 126916.75 | 39.40 | 362,205,184 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-query-c1.jsonl` |
|terrabase-sdk | — | write | 1 | complete | 642.48 | 1341.29 | 3088.37 | 4770.94 | 111520.42 | 20.96 | 408,928,256 | 7,928,374 | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |
|terrabase-sdk | — | mixed | 1 | complete | 296.29 | 646.43 | 26092.01 | 32307.31 | 75553.61 | 11.52 | 449,343,488 | 6,283,677 | EqualitySeek | `raw-samples-terrabase-sdk-mixed-c1.jsonl` |
|terrabase-sdk | — | read | 16 | complete | 20261.48 | 715.64 | 1017.19 | 3054.59 | 10438.83 | 90.63 | 375,304,192 | 0 | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |
|terrabase-sdk | — | write | 16 | complete | 1052.93 | 14238.86 | 24884.81 | 30794.26 | 109297.56 | 19.02 | 404,516,864 | 7,145,791 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 5904.51 | 2319.49 | 185.65 | 259.39 | 296.29 | 0.04 |
|query | 1 | 67779.75 | 705.65 | 1342.53 | 1803.26 | 1605.86 | 0.03 |
|read | 1 | 60739.21 | 21490.33 | 1412.00 | 5370.06 | 4734.64 | 0.09 |
|write | 1 | 1887.49 | 3918.40 | 1042.21 | 674.76 | 642.48 | 0.36 |
|read | 16 | 267882.50 | 45848.24 | 3259.23 | 22340.39 | 20261.48 | 0.08 |
|write | 16 | 1630.16 | 3587.86 | 958.35 | 893.91 | 1052.93 | 0.55 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 21.32 | 88.51 | 6164.29 | 620.11 | 646.43 | 29.08 |
|query | 1 | 13.75 | 2141.58 | 720.61 | 509.97 | 587.75 | 37.08 |
|read | 1 | 11.10 | 40.23 | 685.94 | 182.10 | 204.97 | 16.41 |
|write | 1 | 511.67 | 181.47 | 881.29 | 1286.89 | 1341.29 | 2.52 |
|read | 16 | 10.22 | 65.08 | 1220.21 | 654.70 | 715.64 | 64.05 |
|write | 16 | 9071.77 | 227.44 | 15798.84 | 15906.20 | 14238.86 | 1.75 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --output bench/receipts/20260915-engine-comparison-sdk-v7`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
