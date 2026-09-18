# TerraBase vs embedded engines full-flow comparison receipt

Status: **failed**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `3d12aea0d7c8d6fe2e1c39506afc7b5d7b05204b` (dirty: `True`)
- Artifact SHA-256: `a8a6e445ed8d6831c2c7a8bb52d84dcaebfbcbc956bb7fed4dfd9003568e5db5`
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
|sqlite | FULL | read | 1 | complete | 64373.84 | 13.44 | 23.16 | 33.64 | 1527.94 | 25.93 | 80,973,824 | 0 | — | `raw-samples-sqlite-full-read-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 61015.64 | 15.04 | 20.48 | 38.86 | 1450.57 | 48.50 | 87,433,216 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|sqlite | FULL | write | 1 | complete | 2181.11 | 318.95 | 750.57 | 2014.33 | 850.36 | 5.48 | 93,564,928 | 4,022,272 | — | `raw-samples-sqlite-full-write-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 6479.11 | 20.11 | 619.33 | 808.33 | 1262.72 | 7.40 | 95,023,104 | 1,187,840 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |
|sqlite | FULL | read | 16 | complete | 153526.82 | 8.94 | 15.67 | 33.01 | 1435.85 | 44.88 | 99,397,632 | 0 | — | `raw-samples-sqlite-full-read-c16.jsonl` |
|sqlite | FULL | write | 16 | complete | 1934.69 | 7870.92 | 11016.43 | 13653.66 | 1412.27 | 13.49 | 101,433,344 | 4,022,272 | — | `raw-samples-sqlite-full-write-c16.jsonl` |
|sqlite | NORMAL | write | 1 | complete | 17486.75 | 32.43 | 66.50 | 110.76 | 851.18 | 19.08 | 101,670,912 | 4,022,272 | — | `raw-samples-sqlite-normal-write-c1.jsonl` |
|sqlite | NORMAL | mixed | 1 | complete | 35931.89 | 14.53 | 52.56 | 74.12 | 1262.75 | 21.26 | 101,765,120 | 1,187,840 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|litedb | — | read | 1 | complete | 21017.36 | 41.37 | 61.41 | 99.12 | 34834.81 | 24.39 | 118,185,984 | 0 | — | `raw-samples-litedb-read-c1.jsonl` |
|litedb | — | query | 1 | complete | 702.91 | 2164.29 | 2847.82 | 4452.30 | 33698.95 | 24.51 | 137,424,896 | 0 | IndexLookup | `raw-samples-litedb-query-c1.jsonl` |
|litedb | — | write | 1 | complete | 4498.49 | 159.97 | 253.27 | 860.24 | 44300.97 | 23.46 | 133,087,232 | 7,299,072 | — | `raw-samples-litedb-write-c1.jsonl` |
|litedb | — | mixed | 1 | complete | 2522.41 | 82.35 | 2615.61 | 2994.04 | 35958.03 | 24.28 | 137,351,168 | 475,136 | IndexLookup | `raw-samples-litedb-mixed-c1.jsonl` |
|litedb | — | read | 16 | complete | 23176.53 | 67.49 | 92.63 | 7190.62 | 34817.48 | 46.10 | 133,554,176 | 0 | — | `raw-samples-litedb-read-c16.jsonl` |
|litedb | — | write | 16 | complete | 3438.35 | 247.58 | 300.87 | 1352.85 | 46710.66 | 27.21 | 196,308,992 | 7,536,640 | — | `raw-samples-litedb-write-c16.jsonl` |
|turso | — | read | 1 | complete | 1443.71 | 659.41 | 738.64 | 1454.11 | 3650.75 | 23.88 | 178,753,536 | 0 | — | `raw-samples-turso-read-c1.jsonl` |
|turso | — | query | 1 | complete | 1388.24 | 715.92 | 790.32 | 1030.07 | 3626.54 | 24.11 | 183,689,216 | 0 | IndexSeek | `raw-samples-turso-query-c1.jsonl` |
|turso | — | write | 1 | complete | 1116.59 | 825.57 | 1212.52 | 2518.21 | 3642.43 | 7.67 | 169,959,424 | 4,302,576 | — | `raw-samples-turso-write-c1.jsonl` |
|turso | — | mixed | 1 | complete | 195.93 | 5743.31 | 9735.36 | 12024.97 | 3643.43 | 23.03 | 195,002,368 | 2,638,200 | IndexSeek | `raw-samples-turso-mixed-c1.jsonl` |
|turso | — | read | 16 | complete | 2254.13 | 1243.50 | 19475.46 | 24117.44 | 3662.41 | 67.44 | 205,316,096 | 0 | — | `raw-samples-turso-read-c16.jsonl` |
|turso | — | write | 16 | complete | 1015.35 | 15181.04 | 19180.51 | 21884.74 | 4203.76 | 11.50 | 228,155,392 | 4,302,576 | — | `raw-samples-turso-write-c16.jsonl` |
|terrabase-http | — | read | 1 | complete | 1545.94 | 244.35 | 305.69 | 493.26 | 139680.40 | 37.03 | 321,466,368 | 0 | — | `raw-samples-terrabase-http-read-c1.jsonl` |
|terrabase-http | — | query | 1 | complete | 609.99 | 570.00 | 1248.43 | 70637.44 | 454798.90 | 31.67 | 341,626,880 | 0 | EqualitySeek | `raw-samples-terrabase-http-query-c1.jsonl` |
|terrabase-http | — | write | 1 | complete | 114.06 | 7252.87 | 15927.50 | 25748.75 | 6410418.13 | 40.19 | 852,176,896 | 27,692,424 | — | `raw-samples-terrabase-http-write-c1.jsonl` |
|terrabase-http | — | mixed | 1 | failed | 37.47 | 1844.08 | 536790.07 | 1152611.10 | 17266421.87 | 25.48 | 464,969,728 | 9,391,043 | Scan | `raw-samples-terrabase-http-mixed-c1.jsonl` |

Failure for `terrabase-http/-/mixed/c1`: `System.InvalidOperationException: TerraBase query user-367 returned 429 (TooManyRequests); body: {"error":{"code":"query_budget_exceeded","message":"The query exceeded its bounded execution budget.","fix":"reduce the query scope or page size and retry (row budget)"}}.    at TerraBase.Benchmarks.EngineComparisonWorkload.TerrabaseHttpBackend.ReadBodyAsync(HttpResponseMessage response, HttpStatusCode expected, String operation, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 759    at TerraBase.Benchmarks.EngineComparisonWorkload.TerrabaseHttpBackend.QueryAsync(Int32 index, Int32 slot, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 674    at TerraBase.Benchmarks.EngineComparisonWorkload.<>c__DisplayClass16_1.<<RunOperationsAsync>b__1>d.MoveNext() in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 431 --- End of stack trace from previous location ---    at TerraBase.Benchmarks.EngineComparisonWorkload.RunOperationsAsync(IComparisonBackend backend, ComparisonLane lane, Int32 concurrency, ComparisonOptions options, Int32 operationCount, Int64[] latencies, CaseCounters counters, Int32 randomSalt, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 447    at TerraBase.Benchmarks.EngineComparisonWorkload.RunSampleAsync(IComparisonBackend backend, CaseDefinition definition, ComparisonOptions options, CaseCounters counters, Int32 sampleIndex, String dataPath, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 469    at TerraBase.Benchmarks.EngineComparisonWorkload.RunCaseAsync(ComparisonOptions options, CaseDefinition definition, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 328    at TerraBase.Benchmarks.EngineComparisonWorkload.RunCaseAsync(ComparisonOptions options, CaseDefinition definition, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 337    at TerraBase.Benchmarks.EngineComparisonWorkload.RunCaseAsync(ComparisonOptions options, CaseDefinition definition, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 343`
|terrabase-http | — | read | 16 | complete | 5853.50 | 659.14 | 4800.70 | 84071.66 | 42694.33 | 47.09 | 361,316,352 | 0 | — | `raw-samples-terrabase-http-read-c16.jsonl` |
|terrabase-http | — | write | 16 | complete | 114.23 | 133765.20 | 190106.97 | 219945.98 | 6409807.38 | 42.26 | 861,958,144 | 27,136,924 | — | `raw-samples-terrabase-http-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 6479.11 | 2522.41 | 195.93 | — | — |
|query | 1 | 61015.64 | 702.91 | 1388.24 | 609.99 | 0.01 |
|read | 1 | 64373.84 | 21017.36 | 1443.71 | 1545.94 | 0.02 |
|write | 1 | 2181.11 | 4498.49 | 1116.59 | 114.06 | 0.05 |
|read | 16 | 153526.82 | 23176.53 | 2254.13 | 5853.50 | 0.04 |
|write | 16 | 1934.69 | 3438.35 | 1015.35 | 114.23 | 0.06 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 20.11 | 82.35 | 5743.31 | — | — |
|query | 1 | 15.04 | 2164.29 | 715.92 | 570.00 | 37.89 |
|read | 1 | 13.44 | 41.37 | 659.41 | 244.35 | 18.18 |
|write | 1 | 318.95 | 159.97 | 825.57 | 7252.87 | 22.74 |
|read | 16 | 8.94 | 67.49 | 1243.50 | 659.14 | 73.76 |
|write | 16 | 7870.92 | 247.58 | 15181.04 | 133765.20 | 16.99 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --output bench/receipts/20260915-engine-comparison-v1`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
