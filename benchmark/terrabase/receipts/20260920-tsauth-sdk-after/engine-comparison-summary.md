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
|tsauth-sdk | — | read | 1 | complete | 35884.58 | 24.22 | 34.57 | 48.42 | 6550.75 | 26.25 | 82,829,312 | 0 | — | `raw-samples-tsauth-sdk-read-c1.jsonl` |
|tsauth-sdk | — | query | 1 | complete | 3735.39 | 229.78 | 427.57 | 918.99 | 339624.66 | 26.08 | 97,923,072 | 0 | — | `raw-samples-tsauth-sdk-query-c1.jsonl` |
|tsauth-sdk | — | write | 1 | complete | 50108.59 | 18.97 | 31.69 | 48.86 | 5997.41 | 36.24 | 98,832,384 | 0 | — | `raw-samples-tsauth-sdk-write-c1.jsonl` |
|tsauth-sdk | — | mixed | 1 | complete | 16563.60 | 11.61 | 239.81 | 400.09 | 72978.72 | 33.89 | 99,520,512 | 0 | — | `raw-samples-tsauth-sdk-mixed-c1.jsonl` |
|tsauth-sdk | — | read | 16 | complete | 244544.22 | 13.35 | 14.55 | 29.83 | 6552.49 | 88.28 | 99,954,688 | 0 | — | `raw-samples-tsauth-sdk-read-c16.jsonl` |
|tsauth-sdk | — | write | 16 | complete | 188153.13 | 16.34 | 21.67 | 36.00 | 5943.63 | 86.67 | 99,991,552 | 0 | — | `raw-samples-tsauth-sdk-write-c16.jsonl` |

## Matched comparison — Throughput ops/s (median of per-sample medians; higher is better)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | ts-auth SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Matched comparison — p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)

| Lane | Conc | SQLite FULL | LiteDB | Turso | TerraBase engine | TerraBase HTTP | TerraBase SDK | ts-auth SDK | TerraBase/SQLite |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload engine-comparison --engines tsauth-sdk --samples 5 --operations 5000 --warmup 1000 --seed 1337 --output terrabase/receipts/20260920-tsauth-sdk-after`

All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).
