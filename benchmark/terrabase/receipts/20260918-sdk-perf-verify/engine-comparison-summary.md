# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `f4d1608eca920648276d02e2bcd5c97eebb2b205` (dirty: `True`)
- Artifact SHA-256: `849301db5e27d6ae86d9d654cdba12a0f4cbeb5969c428aa459181e715105c89`
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
|sqlite | NORMAL | write | 1 | complete | 11322.58 | 63.59 | 130.91 | 195.18 | 850.81 | 28.70 | 89,964,544 | 4,022,272 | — | `raw-samples-sqlite-normal-write-c1.jsonl` |
|sqlite | NORMAL | mixed | 1 | complete | 23407.70 | 25.02 | 78.74 | 121.88 | 1262.80 | 25.66 | 96,321,536 | 1,187,840 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|litedb | — | read | 1 | complete | 12836.08 | 71.94 | 109.67 | 129.60 | 34834.38 | 25.29 | 114,642,944 | 0 | — | `raw-samples-litedb-read-c1.jsonl` |
|litedb | — | query | 1 | complete | 644.57 | 2029.61 | 3622.82 | 4880.97 | 33664.23 | 24.83 | 135,139,328 | 0 | IndexLookup | `raw-samples-litedb-query-c1.jsonl` |
|litedb | — | write | 1 | complete | 3974.80 | 171.95 | 280.65 | 1102.03 | 44300.39 | 24.38 | 131,932,160 | 7,299,072 | — | `raw-samples-litedb-write-c1.jsonl` |
|litedb | — | mixed | 1 | complete | 2224.39 | 106.09 | 2835.08 | 3793.70 | 35942.07 | 25.03 | 137,633,792 | 475,136 | IndexLookup | `raw-samples-litedb-mixed-c1.jsonl` |
|litedb | — | read | 16 | complete | 30631.81 | 67.53 | 98.44 | 2946.84 | 34830.35 | 65.93 | 136,015,872 | 0 | — | `raw-samples-litedb-read-c16.jsonl` |
|litedb | — | write | 16 | complete | 2636.43 | 295.75 | 494.74 | 41340.38 | 47071.44 | 25.84 | 200,675,328 | 6,258,688 | — | `raw-samples-litedb-write-c16.jsonl` |
|turso | — | read | 1 | complete | 1092.16 | 902.10 | 1158.72 | 1189.12 | 3650.48 | 24.68 | 181,415,936 | 0 | — | `raw-samples-turso-read-c1.jsonl` |
|turso | — | query | 1 | complete | 1030.62 | 1001.18 | 1172.69 | 1232.93 | 3626.25 | 24.58 | 183,476,224 | 0 | IndexSeek | `raw-samples-turso-query-c1.jsonl` |
|turso | — | write | 1 | complete | 1021.03 | 839.39 | 1288.97 | 2672.67 | 3642.53 | 7.91 | 170,450,944 | 4,302,576 | — | `raw-samples-turso-write-c1.jsonl` |
|turso | — | mixed | 1 | complete | 208.61 | 5612.09 | 8803.68 | 10226.51 | 3643.32 | 23.56 | 200,351,744 | 2,638,200 | IndexSeek | `raw-samples-turso-mixed-c1.jsonl` |
|turso | — | read | 16 | complete | 3298.00 | 1205.01 | 12605.90 | 15407.19 | 3669.23 | 97.64 | 208,891,904 | 0 | — | `raw-samples-turso-read-c16.jsonl` |
|turso | — | write | 16 | complete | 1120.56 | 13822.47 | 17501.79 | 20466.85 | 4204.23 | 12.24 | 223,956,992 | 4,302,576 | — | `raw-samples-turso-write-c16.jsonl` |
|terrabase-engine | — | read | 1 | complete | 698387.42 | 1.12 | 2.09 | 3.19 | 140.84 | 51.29 | 257,638,400 | 0 | — | `raw-samples-terrabase-engine-read-c1.jsonl` |
|sqlite | FULL | read | 1 | complete | 111962.40 | 7.62 | 13.85 | 22.76 | 1435.25 | 49.39 | 257,720,320 | 0 | — | `raw-samples-sqlite-full-read-c1.jsonl` |
|terrabase-engine | — | query | 1 | complete | 133176.54 | 6.35 | 10.45 | 17.86 | 1868.09 | 49.24 | 267,845,632 | 0 | EqualitySeek | `raw-samples-terrabase-engine-query-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 114632.23 | 7.62 | 13.13 | 19.68 | 1451.79 | 49.27 | 267,927,552 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|terrabase-engine | — | write | 1 | complete | 2484.10 | 286.43 | 868.71 | 1410.35 | 20683.37 | 14.36 | 373,542,912 | 1,332,834 | — | `raw-samples-terrabase-engine-write-c1.jsonl` |
|sqlite | FULL | write | 1 | complete | 2127.05 | 398.73 | 808.95 | 1605.42 | 904.02 | 6.53 | 377,757,696 | 4,022,272 | — | `raw-samples-sqlite-full-write-c1.jsonl` |
|terrabase-engine | — | mixed | 1 | complete | 6042.37 | 26.33 | 536.98 | 908.39 | 10350.95 | 19.10 | 457,932,800 | 464,038 | EqualitySeek | `raw-samples-terrabase-engine-mixed-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 6782.01 | 17.55 | 596.30 | 822.65 | 1276.27 | 8.02 | 457,940,992 | 1,187,840 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |
|terrabase-engine | — | read | 16 | complete | 1568233.86 | 1.56 | 2.82 | 4.74 | 142.34 | 95.02 | 384,864,256 | 0 | — | `raw-samples-terrabase-engine-read-c16.jsonl` |
|sqlite | FULL | read | 16 | complete | 263961.59 | 9.85 | 33.76 | 54.68 | 1435.75 | 78.06 | 384,864,256 | 0 | — | `raw-samples-sqlite-full-read-c16.jsonl` |
|terrabase-engine | — | write | 16 | complete | 2692.80 | 274.40 | 6055.45 | 49792.54 | 20616.47 | 14.60 | 376,573,952 | 1,168,529 | — | `raw-samples-terrabase-engine-write-c16.jsonl` |
|sqlite | FULL | write | 16 | complete | 2016.49 | 7756.77 | 10107.59 | 13450.50 | 1446.96 | 13.94 | 381,284,352 | 4,022,272 | — | `raw-samples-sqlite-full-write-c16.jsonl` |
|terrabase-http | — | read | 1 | complete | 6265.42 | 153.13 | 181.91 | 219.45 | 8427.58 | 77.64 | 446,574,592 | 0 | — | `raw-samples-terrabase-http-read-c1.jsonl` |
|terrabase-http | — | query | 1 | complete | 3530.10 | 268.58 | 337.00 | 1059.48 | 97922.67 | 60.23 | 365,953,024 | 0 | EqualitySeek | `raw-samples-terrabase-http-query-c1.jsonl` |
|terrabase-http | — | write | 1 | complete | 1387.01 | 609.63 | 1193.54 | 1829.27 | 31524.38 | 37.28 | 454,504,448 | 1,552,206 | — | `raw-samples-terrabase-http-write-c1.jsonl` |
|terrabase-http | — | mixed | 1 | complete | 2450.64 | 315.65 | 917.98 | 1320.09 | 35829.72 | 46.14 | 513,638,400 | 504,933 | EqualitySeek | `raw-samples-terrabase-http-mixed-c1.jsonl` |
|terrabase-http | — | read | 16 | complete | 29077.74 | 503.32 | 672.50 | 2039.14 | 8423.11 | 93.49 | 440,795,136 | 0 | — | `raw-samples-terrabase-http-read-c16.jsonl` |
|terrabase-http | — | write | 16 | complete | 2080.04 | 6950.92 | 12680.06 | 18503.94 | 30446.97 | 28.16 | 451,129,344 | 1,436,175 | — | `raw-samples-terrabase-http-write-c16.jsonl` |
|terrabase-sdk | — | read | 1 | complete | 18544.70 | 48.35 | 86.84 | 97.89 | 11663.02 | 25.01 | 486,440,960 | 0 | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |
|terrabase-sdk | — | query | 1 | complete | 7269.32 | 120.31 | 178.04 | 790.84 | 103485.60 | 24.95 | 395,522,048 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-query-c1.jsonl` |
|terrabase-sdk | — | write | 1 | complete | 1825.38 | 458.75 | 1017.58 | 1518.60 | 37078.37 | 23.16 | 489,902,080 | 1,529,321 | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |
|terrabase-sdk | — | mixed | 1 | complete | 3728.23 | 176.13 | 711.77 | 1185.72 | 40286.21 | 23.97 | 537,505,792 | 511,330 | EqualitySeek | `raw-samples-terrabase-sdk-mixed-c1.jsonl` |
|terrabase-sdk | — | read | 16 | complete | 47339.77 | 72.50 | 97.37 | 124.86 | 11663.19 | 91.80 | 459,116,544 | 0 | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |
|terrabase-sdk | — | write | 16 | complete | 2156.67 | 3412.60 | 5799.49 | 9238.85 | 35629.55 | 25.88 | 472,743,936 | 1,298,669 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |
|terrabase-sdk | ASYNC | read | 1 | complete | 18858.89 | 47.85 | 79.86 | 100.35 | 11664.55 | 25.05 | 497,758,208 | 0 | — | `raw-samples-terrabase-sdk-async-read-c1.jsonl` |
|terrabase-sdk | ASYNC | query | 1 | complete | 7193.52 | 117.24 | 182.34 | 896.16 | 103487.58 | 24.90 | 408,035,328 | 0 | EqualitySeek | `raw-samples-terrabase-sdk-async-query-c1.jsonl` |
|terrabase-sdk | ASYNC | write | 1 | complete | 4641.14 | 190.76 | 257.54 | 327.36 | 36322.63 | 42.78 | 503,832,576 | 1,409,298 | — | `raw-samples-terrabase-sdk-async-write-c1.jsonl` |
|terrabase-sdk | ASYNC | mixed | 1 | complete | 5962.52 | 135.19 | 409.96 | 563.97 | 40208.40 | 32.80 | 532,422,656 | 507,888 | EqualitySeek | `raw-samples-terrabase-sdk-async-mixed-c1.jsonl` |
|terrabase-sdk | ASYNC | read | 16 | complete | 47817.86 | 72.66 | 107.00 | 138.38 | 11663.05 | 92.99 | 462,999,552 | 0 | — | `raw-samples-terrabase-sdk-async-read-c16.jsonl` |
|terrabase-sdk | ASYNC | write | 16 | complete | 7433.56 | 213.72 | 1507.20 | 4507.61 | 34622.95 | 66.87 | 483,463,168 | 1,215,877 | — | `raw-samples-terrabase-sdk-async-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 6782.01 | 2224.39 | 208.61 | 6042.37 | 2450.64 | 3728.23 | 0.89 |
|query | 1 | 114632.23 | 644.57 | 1030.62 | 133176.54 | 3530.10 | 7269.32 | 1.16 |
|read | 1 | 111962.40 | 12836.08 | 1092.16 | 698387.42 | 6265.42 | 18544.70 | 6.24 |
|write | 1 | 2127.05 | 3974.80 | 1021.03 | 2484.10 | 1387.01 | 1825.38 | 1.17 |
|read | 16 | 263961.59 | 30631.81 | 3298.00 | 1568233.86 | 29077.74 | 47339.77 | 5.94 |
|write | 16 | 2016.49 | 2636.43 | 1120.56 | 2692.80 | 2080.04 | 2156.67 | 1.34 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 17.55 | 106.09 | 5612.09 | 26.33 | 315.65 | 176.13 | 1.50 |
|query | 1 | 7.62 | 2029.61 | 1001.18 | 6.35 | 268.58 | 120.31 | 0.83 |
|read | 1 | 7.62 | 71.94 | 902.10 | 1.12 | 153.13 | 48.35 | 0.15 |
|write | 1 | 398.73 | 171.95 | 839.39 | 286.43 | 609.63 | 458.75 | 0.72 |
|read | 16 | 9.85 | 67.53 | 1205.01 | 1.56 | 503.32 | 72.50 | 0.16 |
|write | 16 | 7756.77 | 295.75 | 13822.47 | 274.40 | 6950.92 | 3412.60 | 0.04 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --output bench/receipts/20260918-sdk-perf-verify`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
