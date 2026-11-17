# Function invocation latency

## Method and original baseline

Original baseline: origin/main at 1fcffcf12484f8191bbc8e9de8ca1e710b434b73, measured before implementation changes on 2026-11-17. The original baseline was committed separately before the implementation and is retained below.

Release build, .NET SDK 11.0.100 / runtime 11.0.0, Jint 4.17.0, on mariogk-dev: Linux x86-64, Intel Xeon E5-2660 v4 @ 2 GHz, 46 visible CPUs, 32 GiB RAM. Shared host. All builds and harness processes used DOTNET_PROCESSOR_COUNT=4, DOTNET_ROOT=$HOME/.local/share/mise/dotnet-root, and nice -n 19. Server GC and execution limits were unchanged. Only one harness process ran at a time; no harness overlapped our builds.

An uncommitted in-process harness called the real FunctionService.InvokeAsync with its real DI graph, encrypted on-disk storage, catalog, SQL sessions, log retention and metric buckets. HTTP/TLS/authentication/network transport are excluded. Each invocation's exact response status and JSON were checked. Percentiles use nearest rank; times are milliseconds.

Workloads:

- constant: function handler(event) { return {ok:true}; }
- three-sql: async function handler(event) { const a = await sql.query('SELECT 1 AS value'); const b = await sql.query('SELECT 2 AS value'); const c = await sql.query('SELECT 3 AS value'); return {a,b,c}; }

The original baseline used 100 warmups followed by three runs of 200 sequential invocations per workload:

| Workload | Original baseline run | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: |
| constant | 1 | 11.1960 | 16.1806 | 31.6951 |
| constant | 2 | 10.7689 | 14.0529 | 19.2946 |
| constant | 3 | 8.7319 | 11.9336 | 16.6455 |
| three-sql | 1 | 9.9393 | 12.5652 | 17.7423 |
| three-sql | 2 | 10.3984 | 15.9499 | 19.3040 |
| three-sql | 3 | 9.8962 | 13.6835 | 19.0931 |

## Controlled interleaved repeat

This comparison supersedes the initial two-process comparison. Both controls use the same single-run harness and dependency build. The baseline's function implementation was restored from the recorded origin/main revision; the after control contains the final function changes, including retained regional leases, tenant admission and shutdown cancellation. Temporary Stopwatch spans are the only instrumentation. These timings were collected at r1 revision 426b3df87e70fdea59cd7948cbad6bd32735d8e9. The r2 refinement makes batch failures recoverable and tracks the records captured by each read barrier; the interleaved timings were not repeated for that refinement. The production build has no instrumentation.

Each process starts a fresh encrypted data directory, provisions a real organization/project/database, performs 100 warmups followed by exactly 200 sequential measured invocations for each workload, verifies all 300 metric records and 100 retained logs, then stops. The three-query workload additionally verifies 900 SQL calls. The final comparison pools 600 measured invocations per workload per version. Logs/stats checks occur after each workload outside the timing window. There is no flush between warmups and measured invocations.

The exact chronological order is B1, A1, B2, A2, B3, A3. All six processes finished successfully. Absolute timings vary with shared-host load and JIT tiering; these are latency measurements, not a production HTTP SLA or throughput claim.

| Sequence | Control | UTC start | Host load average (1 / 5 / 15 min) |
| ---: | --- | --- | --- |
| 1 | before-1 | 2026-11-17T07:19:06.707744+00:00 | 8.04 / 14.95 / 17.39 |
| 2 | after-1 | 2026-11-17T07:19:16.055522+00:00 | 7.91 / 14.69 / 17.28 |
| 3 | before-2 | 2026-11-17T07:19:20.981042+00:00 | 7.91 / 14.69 / 17.28 |
| 4 | after-2 | 2026-11-17T07:19:30.789750+00:00 | 8.47 / 14.59 / 17.22 |
| 5 | before-3 | 2026-11-17T07:19:35.884070+00:00 | 8.75 / 14.54 / 17.19 |
| 6 | after-3 | 2026-11-17T07:19:45.109009+00:00 | 8.01 / 14.19 / 17.05 |

### Raw interleaved runs

| Control | Workload | Samples | p50 ms | p95 ms | p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| before-1 | constant | 200 | 10.8781 | 14.3710 | 21.6099 |
| before-1 | three-sql | 200 | 10.7600 | 14.4280 | 21.4454 |
| after-1 | constant | 200 | 2.3500 | 3.0723 | 5.0807 |
| after-1 | three-sql | 200 | 5.8394 | 7.6598 | 8.5573 |
| before-2 | constant | 200 | 11.0978 | 14.6642 | 19.8509 |
| before-2 | three-sql | 200 | 11.2949 | 17.3158 | 20.5161 |
| after-2 | constant | 200 | 2.2413 | 3.2614 | 8.2415 |
| after-2 | three-sql | 200 | 6.0287 | 8.1998 | 12.1670 |
| before-3 | constant | 200 | 10.9129 | 14.8830 | 18.8189 |
| before-3 | three-sql | 200 | 10.5584 | 15.3481 | 18.0767 |
| after-3 | constant | 200 | 2.0830 | 2.8868 | 4.3045 |
| after-3 | three-sql | 200 | 5.4619 | 7.7294 | 11.1101 |

### Before/after comparison

| Workload | Before p50 / p95 / p99 ms | After p50 / p95 / p99 ms | p50 reduction |
| --- | ---: | ---: | ---: |
| constant | 10.9795 / 14.6775 / 21.6099 | 2.2023 / 3.0977 / 5.9735 | 79.9% |
| three-sql | 10.8617 / 15.9440 / 19.6113 | 5.7595 / 7.7294 / 10.8959 | 47.0% |

## Cause breakdown

Arithmetic means across 600 measured invocations per row, not additive percentiles. Parse + execute includes top-level prelude/customer-source evaluation and parsing. Handler time excludes SQL-binding spans. SQL spans match FunctionSql.SqlMs before its integer ceiling. Before's recording span includes catalog writes and retention; after's span is queue publication on the response path. Background writes still run and consume resources. Remaining time includes lookup, tenant/global admission, regional ingress admission, capacity backpressure, scheduling, bindings, disposal and response construction.

| Control | Workload | Engine ms | Parse + execute ms | Handler excluding SQL ms | SQL ms | Recording on response path ms | Total mean ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| original | constant | 0.0493 | 0.9566 | 0.2878 | 0.0000 | 8.5003 | 10.8409 |
| original | three-sql | 0.0255 | 0.4056 | 0.7851 | 1.6195 | 7.0285 | 10.5700 |
| before | constant | 0.0486 | 1.2881 | 0.3312 | 0.0000 | 8.8947 | 11.6274 |
| before | three-sql | 0.0419 | 0.4534 | 1.1395 | 1.7695 | 7.3380 | 11.5652 |
| after | constant | 0.0494 | 1.1522 | 0.3128 | 0.0000 | 0.0059 | 2.4287 |
| after | three-sql | 0.0470 | 1.1975 | 1.7523 | 2.0998 | 0.0061 | 6.1396 |

Synchronous log/metrics persistence dominated the original response path. The fix moves that measured cost to a bounded recorder and shares catalog/metrics/retention work across batches. Fresh engines remain cheap. Parsing and SQL JSON conversion were left unchanged because they were not needed to meet the target.

## Handoff, isolation and shutdown proof

An external, uncommitted harness used the real catalog writer transaction to block persistence. It obtained 31 successful responses for tenant A, held the 32nd response under capacity backpressure, rejected A's 33rd invocation before execution, and obtained a successful response for tenant B while A's backlog remained blocked. Stats reads waited. The same RegionalIngress.HoldAsync used by RegionalHandoff could not finish while those acknowledged records remained queued. After releasing the writer, all 33 logs/metrics were present before the hold completed and RegionalOwners.Drain was called. All 33 handlers returned a fresh JavaScript global counter of 1.

The harness then used the actual Generic Host with Shutdown.HostedBudget (8 seconds), with nine invocations active including an endless JavaScript handler. The function stopping phase stopped admission, canceled running JavaScript, and persisted all nine invocation logs/metrics before host shutdown completed. Canceled invocations have error records, rather than being omitted. A separate real SystemCatalog with an unowned RegionalOwners exercised a two-record batch refusal: both captured reads returned typed not_writer and the host and recorder stayed running. Observe/Renew then restored ownership, and the same recorder accepted another invocation and persisted its logs/metrics. New reads succeeded while the captured failed barriers retained their error. A malformed persisted metrics bucket also caused a real JsonException: that batch returned typed function_recording_failed and left no partial logs; repairing the bucket allowed the next invocation to persist on the same recorder. All ingress leases were released. No fake SQL, storage, owners or ingress implementation was used. An initial quota-only comparison exposed a full tenant queue under fast sequential invocation; accepted-response backpressure fixed that behavior before the final six-cohort comparison.

```text
C2 proof: A has 31 acknowledged records plus record 32 applying backpressure on the real catalog writer; A rejects record 33; B still returns 200; stats and handoff wait
C2 proof: real RegionalIngress.HoldAsync completed only after all 33 records persisted; then RegionalOwners.Drain ran; recorder healthy=True
C2 proof: real Generic Host shutdown with production 8-second budget completed in 54.72 ms; all 9 active invocation records persisted, including explicit invocation-cancelled error for endless handler; fresh globals verified on 33 prior responses
C2 proof: malformed persisted metrics caused a real JsonException; only that batch returned function_recording_failed, its transaction left no partial logs; repairing storage let the same recorder persist the next invocation
C2 proof: real unowned RegionalOwners refused a 2-record batch; both captured reads returned typed not_writer, one counted failure log, host and recorder stayed running
C2 proof: Observe/Renew restored writer ownership; the same recorder accepted the next invocation and persisted its logs/metrics; new reads succeeded, old captured barriers retained not_writer, all ingress leases released
```

## Implementation and limits

The recorder holds at most 64 records across running invocations, queued records and its active batch. One organization holds at most 32, so a single tenant cannot occupy the entire recorder pool. Admission is nonblocking and happens before execution. When an accepted invocation fills its organization's share, its response waits for capacity to be released by persistence; this applies producer backpressure without rejecting the next sequential invocation or adding timers. Parallel requests arriving while full are still rejected before execution. The tenant's invocation memory reservation transfers to its record, shrinking to the retained payload estimate plus bookkeeping when no response backpressure is needed. A record whose response awaits capacity keeps its full invocation reservation through persistence, covering the response retained by that call. Each record also retains its RegionalIngress lease through the catalog transaction, so regional handoff drains recording before revoking ownership. A batch contains at most 32 records; metrics are aggregated once per function/time bucket, and retention runs once per function per batch.

Logs, stats and deletion capture and wait for all prior queued records. A refused or failed batch completes its records with typed Unavailable and emits one structured failure log containing its record count. Expected refusals preserve the catalog error code; unexpected persistence exceptions map to function_recording_failed and include the exception in that log. Captured read barriers retain their failed batch result even after a subsequent batch succeeds. A response applying capacity backpressure also receives its own record failure. Failures do not close admission or stop the reader: subsequent batches can succeed after catalog recovery. There are no retries. Only shutdown stops the subsystem. Normal shutdown stops function admission, signals cancellation to active invocations and drains their records before hosted dependencies stop. There is no increased timeout. The recorder is memory buffered: abrupt process termination can lose admitted records. Full HTTP/auth/TLS behavior and a complete multi-node NATS handoff were not run locally; the targeted handoff admission boundary and production Generic Host shutdown budget were exercised above.

Every invocation still constructs and disposes its own Engine. The existing 10-second deadline, 32-MiB memory limit, recursion limit of 256 and 10,000,000-statement limit are unchanged. No storage format, storage engine or native dependency changed. The pre-existing delete/recreate name-identity race noted in review is outside this change.
