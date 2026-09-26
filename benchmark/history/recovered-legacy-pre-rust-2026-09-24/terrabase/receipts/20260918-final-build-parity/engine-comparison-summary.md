# TerraBase vs embedded engines full-flow comparison receipt

Status: **complete**

Receipt JSON: `engine-comparison-receipt.json`

## Environment

- Commit: `98a083df7df9aad6ddaf6df04686e259baf68cd9` (dirty: `True`)
- Artifact SHA-256: `b2e8dfe2d6ea093019113aea6d281c8613fa54335c27048db3ad26c016cb67fd`
- Runtime: .NET 10.0.11; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 4 logical processors; available memory 20,937,965,568 bytes
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
|sqlite | NORMAL | mixed | 1 | complete | 25583.42 | 24.78 | 64.84 | 89.71 | 1317.67 | 28.99 | 85,803,008 | 1,187,840 | IndexSeek | `raw-samples-sqlite-normal-mixed-c1.jsonl` |
|terrabase-engine | — | query | 1 | complete | 118028.92 | 7.97 | 10.16 | 18.21 | 421.50 | 48.56 | 122,560,512 | 0 | EqualitySeek | `raw-samples-terrabase-engine-query-c1.jsonl` |
|sqlite | FULL | query | 1 | complete | 123140.43 | 7.45 | 9.51 | 16.22 | 1451.91 | 49.17 | 122,638,336 | 0 | IndexSeek | `raw-samples-sqlite-full-query-c1.jsonl` |
|terrabase-engine | — | mixed | 1 | complete | 7095.60 | 5.94 | 357.57 | 868.71 | 9355.08 | 21.16 | 167,956,480 | 465,408 | EqualitySeek | `raw-samples-terrabase-engine-mixed-c1.jsonl` |
|sqlite | FULL | mixed | 1 | complete | 7267.58 | 15.67 | 567.74 | 687.37 | 1276.22 | 7.77 | 169,668,608 | 1,187,840 | IndexSeek | `raw-samples-sqlite-full-mixed-c1.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 7267.58 | — | — | 7095.60 | — | 0.98 |
|query | 1 | 123140.43 | — | — | 118028.92 | — | 0.96 |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
|mixed | 1 | 15.67 | — | — | 5.94 | — | 0.38 |
|query | 1 | 7.45 | — | — | 7.97 | — | 1.07 |

Command: `/home/mariogk/Projects/TerraScale/terrabase/.worktrees/anna/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines sqlite,terrabase-engine --lanes query,mixed --output scratch/final-parity`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
