# TerraBase vs embedded engines full-flow comparison receipt

Status: **failed**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `145fd96c43009c9c7bd0ef6b9705cd50056f713b` (dirty: `True`)
- Artifact SHA-256: `583c9ca11fe7a241f2b7d982b55b5391620e0dfeb50881d52abb58222f17a5cc`
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
|terrabase-sdk | — | write | 1 | failed | — | — | — | — | — | — | — | — | — | `raw-samples-terrabase-sdk-write-c1.jsonl` |

Failure for `terrabase-sdk/-/write/c1`: `TerraScale.DotNet.TerraScaleApiException: The document version did not satisfy the request precondition.    at TerraScale.DotNet.TerraScaleClient.ReadJsonResponseAsync[TResponse](HttpResponseMessage response, Activity activity, CancellationToken cancellationToken, TerraScaleErrorContract errorContract)    at TerraScale.DotNet.TerraScaleClient.SendPreparedJsonAsync[TResponse,TResult](HttpMethod method, String requestUri, Byte[] payload, Action`1 configureRequest, Func`3 responseFactory, CancellationToken cancellationToken, String idempotencyKeyOverride, Boolean allowUnkeyedPutRetry)    at TerraBase.Benchmarks.EngineComparisonWorkload.TerrabaseSdkBackend.SeedAsync(Int32 index, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/TerrabaseSdkBackend.cs:line 107    at TerraBase.Benchmarks.EngineComparisonWorkload.PrepareCaseAsync(ComparisonOptions options, CaseDefinition definition, ComparisonCaseResult result, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 538    at TerraBase.Benchmarks.EngineComparisonWorkload.PrepareCaseAsync(ComparisonOptions options, CaseDefinition definition, ComparisonCaseResult result, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 590    at TerraBase.Benchmarks.EngineComparisonWorkload.RunCaseAsync(ComparisonOptions options, CaseDefinition definition, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 463`
|terrabase-sdk | — | write | 16 | complete | 1814.49 | 7099.52 | 14345.10 | 22483.02 | 36721.30 | 29.52 | 281,702,400 | 1,278,919 | — | `raw-samples-terrabase-sdk-write-c16.jsonl` |
|terrabase-sdk | ASYNC | write | 1 | complete | 3933.30 | 215.32 | 260.07 | 555.84 | 38939.02 | 53.83 | 364,781,568 | 1,756,983 | — | `raw-samples-terrabase-sdk-async-write-c1.jsonl` |
|terrabase-sdk | ASYNC | write | 16 | complete | 5787.54 | 214.79 | 14434.63 | 27188.34 | 35224.11 | 56.54 | 420,392,960 | 1,247,364 | — | `raw-samples-terrabase-sdk-async-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-sdk --lanes write --output bench/receipts/20260917-sdk-asyncwal-v2`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
