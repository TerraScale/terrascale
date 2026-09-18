# TerraBase vs embedded engines full-flow comparison receipt

Status: **failed**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `145fd96c43009c9c7bd0ef6b9705cd50056f713b` (dirty: `True`)
- Artifact SHA-256: `dd2c0ce4c86497a0609093f91188bec39abd614121512544feacca18106a1e96`
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
|terrabase-sdk | — | read | 1 | failed | — | — | — | — | — | — | — | — | — | `raw-samples-terrabase-sdk-read-c1.jsonl` |

Failure for `terrabase-sdk/-/read/c1`: `System.ArgumentException: HTTP is allowed only for loopback or explicit test endpoints; production endpoints must use HTTPS. (Parameter 'BaseAddress')    at TerraScale.DotNet.TerraScaleClientOptions.ValidateEndpoint(Uri endpoint, String name)    at TerraScale.DotNet.TerraScaleClientOptions.Validate()    at TerraScale.DotNet.TerraScaleClient..ctor(HttpClient httpClient, TerraScaleClientOptions options)    at TerraBase.Benchmarks.EngineComparisonWorkload.TerrabaseSdkBackend.OpenAsync(String storagePath, Int32 datasetSize, Int32 payloadSize, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/TerrabaseSdkBackend.cs:line 80    at TerraBase.Benchmarks.EngineComparisonWorkload.TerrabaseSdkBackend.OpenAsync(String storagePath, Int32 datasetSize, Int32 payloadSize, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/TerrabaseSdkBackend.cs:line 95    at TerraBase.Benchmarks.EngineComparisonWorkload.PrepareCaseAsync(ComparisonOptions options, CaseDefinition definition, ComparisonCaseResult result, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 501    at TerraBase.Benchmarks.EngineComparisonWorkload.PrepareCaseAsync(ComparisonOptions options, CaseDefinition definition, ComparisonCaseResult result, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 576    at TerraBase.Benchmarks.EngineComparisonWorkload.RunCaseAsync(ComparisonOptions options, CaseDefinition definition, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 453`
|terrabase-sdk | — | read | 16 | failed | — | — | — | — | — | — | — | — | — | `raw-samples-terrabase-sdk-read-c16.jsonl` |

Failure for `terrabase-sdk/-/read/c16`: `System.ArgumentException: HTTP is allowed only for loopback or explicit test endpoints; production endpoints must use HTTPS. (Parameter 'BaseAddress')    at TerraScale.DotNet.TerraScaleClientOptions.ValidateEndpoint(Uri endpoint, String name)    at TerraScale.DotNet.TerraScaleClientOptions.Validate()    at TerraScale.DotNet.TerraScaleClient..ctor(HttpClient httpClient, TerraScaleClientOptions options)    at TerraBase.Benchmarks.EngineComparisonWorkload.TerrabaseSdkBackend.OpenAsync(String storagePath, Int32 datasetSize, Int32 payloadSize, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/TerrabaseSdkBackend.cs:line 80    at TerraBase.Benchmarks.EngineComparisonWorkload.TerrabaseSdkBackend.OpenAsync(String storagePath, Int32 datasetSize, Int32 payloadSize, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/TerrabaseSdkBackend.cs:line 95    at TerraBase.Benchmarks.EngineComparisonWorkload.PrepareCaseAsync(ComparisonOptions options, CaseDefinition definition, ComparisonCaseResult result, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 501    at TerraBase.Benchmarks.EngineComparisonWorkload.PrepareCaseAsync(ComparisonOptions options, CaseDefinition definition, ComparisonCaseResult result, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 576    at TerraBase.Benchmarks.EngineComparisonWorkload.RunCaseAsync(ComparisonOptions options, CaseDefinition definition, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/EngineComparisonWorkload.cs:line 453`

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines terrabase-sdk --lanes read --output bench/receipts/20260917-embedded-smoke2`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
