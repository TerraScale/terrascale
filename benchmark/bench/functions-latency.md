# Function invocation latency

Baseline: origin/main at 1fcffcf12484f8191bbc8e9de8ca1e710b434b73 (2026-11-17).

## Method

Release build, .NET SDK 11.0.100 / runtime 11.0.0, Jint 4.17.0, on mariogk-dev: Linux x86-64, Intel Xeon E5-2660 v4 @ 2 GHz, 46 visible CPUs, 32 GiB RAM. Shared host; initial load average 12.02 / 14.34 / 17.60. DOTNET_PROCESSOR_COUNT=4, nice -n 19, server GC and repository build settings unchanged.

An uncommitted harness called the real FunctionService.InvokeAsync in-process, with its real DI graph, encrypted on-disk storage engine, system catalog, SQL sessions, log retention and metrics buckets. It started only the local catalog gate, without HTTP listeners or another journey stack. Each workload had 100 untimed warmups, then three runs of 200 sequential awaited invocations. Every response status and exact JSON value was checked. Percentiles use nearest rank over individual invocation wall times; milliseconds throughout. HTTP, TLS, authentication and network transport are excluded.

Workloads:

- constant: function handler(event) { return {ok:true}; }
- three-sql: async function handler(event) { const a = await sql.query('SELECT 1 AS value'); const b = await sql.query('SELECT 2 AS value'); const c = await sql.query('SELECT 3 AS value'); return {a,b,c}; }

Temporary Stopwatch spans covered fresh engine construction, prelude and customer source parsing/execution, handler execution excluding measured SQL, the raw SQL-binding time, and the awaited log/metrics write including retention. SQL spans use the same boundaries as FunctionSql.SqlMs, before its integer ceiling. Stage values below are arithmetic means, not additive percentiles. The remainder covers lookup, memory admission, scheduling, session disposal and response construction. No harness or instrumentation is committed.

## Baseline raw runs

| Workload | Run | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: |
| constant | 1 | 11.1960 | 16.1806 | 31.6951 |
| constant | 2 | 10.7689 | 14.0529 | 19.2946 |
| constant | 3 | 8.7319 | 11.9336 | 16.6455 |
| three-sql | 1 | 9.9393 | 12.5652 | 17.7423 |
| three-sql | 2 | 10.3984 | 15.9499 | 19.3040 |
| three-sql | 3 | 9.8962 | 13.6835 | 19.0931 |

## Baseline cause breakdown

Log/metrics persistence dominates the baseline response path (constant: 7.6575–9.2629 ms mean; three-sql: 6.6791–7.3979 ms). Fresh engine construction is small (0.0187–0.0531 ms). Parsing and prelude execution are secondary (0.3459–1.2891 ms). Real SQL-binding time is 1.5016–1.7578 ms across three queries.

After measurements will use the same workloads and retained baseline executable, interleaved on the shared host.
