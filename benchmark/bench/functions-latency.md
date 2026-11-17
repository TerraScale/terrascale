# Function invocation latency

Baseline: origin/main at 1fcffcf12484f8191bbc8e9de8ca1e710b434b73 (2026-11-17).

## Method

Release build, .NET SDK 11.0.100 / runtime 11.0.0, Jint 4.17.0, on mariogk-dev: Linux x86-64, Intel Xeon E5-2660 v4 @ 2 GHz, 46 visible CPUs, 32 GiB RAM. Shared host; initial load average 12.02 / 14.34 / 17.60. DOTNET_PROCESSOR_COUNT=4, nice -n 19, server GC and repository build settings unchanged.

An uncommitted harness called the real FunctionService.InvokeAsync in-process, with its real DI graph, encrypted on-disk storage engine, system catalog, SQL sessions, log retention and metrics buckets. It started only the local catalog gate, without HTTP listeners or another journey stack. Each workload had 100 untimed warmups, then three runs of 200 sequential awaited invocations. Every response status and exact JSON value was checked. Percentiles use nearest rank over individual invocation wall times; milliseconds throughout. HTTP, TLS, authentication and network transport are excluded.

Workloads:

- constant: function handler(event) { return {ok:true}; }
- three-sql: async function handler(event) { const a = await sql.query('SELECT 1 AS value'); const b = await sql.query('SELECT 2 AS value'); const c = await sql.query('SELECT 3 AS value'); return {a,b,c}; }

Temporary Stopwatch spans covered fresh engine construction, prelude and customer source parsing/execution, handler execution excluding measured SQL, the raw SQL-binding time, and the awaited log/metrics write including retention. SQL spans use the same boundaries as FunctionSql.SqlMs, before its integer ceiling. Stage values below are arithmetic means, not additive percentiles. The remainder covers lookup, memory admission, scheduling, session disposal and response construction. No harness or instrumentation is committed.

## Raw runs

The measurement order was baseline, initial implementation, retained baseline again, then the final implementation. The final pair ran after the verification build finished, without overlapping our own builds or another harness process. Each process used a fresh encrypted data directory. No queue flush was inserted between measured runs; pending work and admission pressure carry into the next run. The final after process then drained while eight invocations were still active.

A diagnostic baseline repeat during a verification build is excluded from the comparison because our own compilation added competing load. Its constant p50 values were 14.8081, 11.1164 and 9.2867 ms; three-SQL p50 values were 11.2180, 9.7062 and 9.4732 ms.

| Version | Workload | Run | p50 ms | p95 ms | p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| before | constant | 1 | 11.1960 | 16.1806 | 31.6951 |
| before | constant | 2 | 10.7689 | 14.0529 | 19.2946 |
| before | constant | 3 | 8.7319 | 11.9336 | 16.6455 |
| before | three-sql | 1 | 9.9393 | 12.5652 | 17.7423 |
| before | three-sql | 2 | 10.3984 | 15.9499 | 19.3040 |
| before | three-sql | 3 | 9.8962 | 13.6835 | 19.0931 |
| after | constant | 1 | 2.0856 | 3.3500 | 4.9311 |
| after | constant | 2 | 2.2180 | 3.0932 | 6.7664 |
| after | constant | 3 | 2.3959 | 3.2783 | 5.9858 |
| after | three-sql | 1 | 6.0482 | 8.2806 | 11.7722 |
| after | three-sql | 2 | 6.1210 | 8.3512 | 14.2961 |
| after | three-sql | 3 | 6.1449 | 9.2051 | 10.7570 |
| before-final | constant | 1 | 10.5942 | 15.2549 | 22.4476 |
| before-final | constant | 2 | 13.2097 | 59.6568 | 97.2669 |
| before-final | constant | 3 | 9.0031 | 13.9155 | 16.2346 |
| before-final | three-sql | 1 | 9.8185 | 13.7315 | 20.6433 |
| before-final | three-sql | 2 | 10.9172 | 15.2185 | 22.5500 |
| before-final | three-sql | 3 | 10.1571 | 12.7921 | 18.9255 |
| after-final | constant | 1 | 2.5040 | 4.5985 | 29.4917 |
| after-final | constant | 2 | 2.5780 | 3.5709 | 18.1959 |
| after-final | constant | 3 | 2.4895 | 3.3091 | 4.6251 |
| after-final | three-sql | 1 | 6.3553 | 12.4359 | 16.2598 |
| after-final | three-sql | 2 | 6.1735 | 8.7410 | 12.6170 |
| after-final | three-sql | 3 | 4.9135 | 6.8079 | 8.6774 |

## Final before/after comparison

Pooled nearest-rank percentiles over 600 measured invocations per workload per version, from the final adjacent before/after pair. Shared host load and JIT tiering affect absolute values; this is a local in-process latency measurement, not a production HTTP SLA or a throughput claim.

| Workload | Before p50 / p95 / p99 ms | After p50 / p95 / p99 ms | p50 reduction |
| --- | ---: | ---: | ---: |
| constant | 10.6924 / 24.7084 / 89.2082 | 2.5525 / 3.8336 / 18.1959 | 76.1% |
| three-sql | 10.2894 / 13.9106 / 22.5500 | 5.8178 / 9.0305 / 15.8976 | 43.5% |

## Cause breakdown

Arithmetic means across 600 measured invocations per row. After's recording column is queue admission on the response path; actual background persistence is reported separately below. The handler column excludes SQL-binding spans. Parse + execute includes top-level prelude and customer-source evaluation as well as parsing. Remaining time includes catalog lookup, capacity admission, task scheduling, bindings, session disposal and response construction.

| Version | Workload | Engine ms | Parse + execute ms | Handler excluding SQL ms | SQL ms | Recording on response path ms | Total mean ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| before | constant | 0.0493 | 0.9566 | 0.2878 | 0.0000 | 8.5003 | 10.8409 |
| before | three-sql | 0.0255 | 0.4056 | 0.7851 | 1.6195 | 7.0285 | 10.5700 |
| before-final | constant | 0.0625 | 0.9583 | 0.2863 | 0.0000 | 10.9060 | 13.4992 |
| before-final | three-sql | 0.0233 | 0.3902 | 0.7388 | 1.5507 | 7.7706 | 11.1788 |
| after-final | constant | 0.0448 | 1.4069 | 0.4345 | 0.0000 | 0.0028 | 3.0879 |
| after-final | three-sql | 0.0827 | 0.8194 | 1.8481 | 2.5262 | 0.0034 | 6.3247 |

Final recorder and shutdown evidence:

```text
C2 active shutdown + fresh-engine isolation: 8/8 responses count=1
C2 shutdown proof: constant invocations=700 sqlCalls=0 retainedLogs=100
C2 shutdown proof: three-sql invocations=700 sqlCalls=2100 retainedLogs=100
C2 shutdown proof: drain invocations=8 sqlCalls=0 retainedLogs=8
C2 recording batches=539 records=1408 total_ms=6683.7162 mean_per_record_ms=4.7470
```

## Change and limits

A bounded channel holds invocation records for a single background writer. A shared limit of 64 slots includes running functions, queued records and the active write batch; capacity is acquired before execution, so overload returns the existing unavailable result. The invocation's tenant memory reservation transfers to its record and shrinks to the retained payload estimate plus bookkeeping. At most 32 records share one catalog transaction; metrics are aggregated once per function and time bucket, and retention runs once per function per batch. Batching matters because a queue alone cannot sustain a faster producer than its writer.

Logs, stats and deletion wait for records queued before the read or deletion. The hosted lifecycle stops invocation admission, lets active invocations finish, then drains all records before other hosted services stop. Persistence errors fault the hosted worker and its read barriers rather than silently dropping records. This is memory buffering: abrupt process termination can lose queued records; normal shutdown drains them. No persisted format, storage engine, API shape, JavaScript engine reuse or execution limit changed. Every invocation still constructs and disposes its own engine with the existing 10-second deadline, 32-MiB memory limit, recursion limit of 256 and 10,000,000-statement limit.

The measured dominant cause was synchronous log/metrics persistence and repeated per-record retention/aggregation work. The fix addresses that cause; parsed-source caching and SQL JSON-binding changes were not needed to meet the latency target and were left untouched.
