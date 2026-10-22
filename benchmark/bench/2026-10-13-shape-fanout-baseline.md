# TerraBase R8 shape fan-out baseline — 2026-10-13

## FR-17 instrumented rerun — measured origin reads

The rerun after B2 PR #223 merged measures origin feed reads with `terrabase.storage.reads{path="feed"}` on the gru
node-scoped scrape. These are attempted backend document/feed SELECT executions, including empty results, misses and
SQLite page-cache hits; they do not measure physical disk I/O.

| requested N | admitted | feed counter before | feed counter after | measured origin reads (delta) | delivered in-window writes |
| --- | --- | --- | --- | --- | --- |
| 1 | 1 | 2 | 2100 | 2098 | 600 |
| 10 | 10 | 2100 | 22988 | 20888 | 6000 |
| 100 | 32 | 22988 | 89752 | 66764 | 19200 |
| 1000 | 32 | 89752 | 156778 | 67026 | 19200 |

Every row has 600 measured writes, zero missed deliveries and an accounting-read delta of zero. Each before/after
difference equals the run JSON's `storage_reads_delta` (2098, 20888, 66764, 67026). The source-derived minimum
document SELECT counts remain secondary estimates (600, 6000, 19200, 19200), distinct from the measured origin-read
deltas above. The 32-stream database cap admits 1, 10, 32 and 32 followers and rejects 68 and 968 in the last two rows.
The first row's before value (2) is the real feed series after host startup (floor SELECT and calibration reads), not
a zero baseline.

**Sampling scope:** before follower admission through 5 s warmup, the 30 s delivery window, 5 s grace and 6 s accounting
settle, on gru only. Each row includes all 700 writes in warmup plus measurement, feed pages, head/floor and other feed
SELECTs across that whole step. It excludes sjc, point-path write preconditions and system lookups. Consequently these
read deltas cover a wider interval and more operations than the 600 in-window writes' inferred document SELECT count.
State lookups that load document bodies now count under `path="point"` (c005b9bd), so the point-path series in the raw
lines is not comparable with the earlier rerun; the feed series stays isolated from it.

| provenance | value |
| --- | --- |
| UTC run interval | `2026-10-13T23:47:45.927524571Z`–`2026-10-13T23:51:57.306120774Z` |
| Run ID | `01M4VTW4KTKF2VMVNQRG89034K` |
| Measured HEAD / last product-host commit | `cc89673676657f9af9e3d9fff920e0a47b248411` |
| Last harness commit | `8ebdf30c3ccb8f91f4bea5afdf171e74983f4e44` |
| Working tree / build | clean; fresh Release build from measured HEAD |
| Host / CPU | mariogk-dev; Intel Xeon E5-2660 v4 at 2.00 GHz; 46 logical CPUs, affinity 0–45 |
| OS / RAM | Linux 7.2.10 x86_64; 33,614,655,488 bytes |
| Toolchains | benchmark runtime Go 1.25.0; .NET SDK 11.0.100-rc.1.26425.128 |
| Pinned ts-dotnet commit | `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` |
| Scheduling | shared host, nice 19 through two-slot `ts-heavy`; no CPU isolation |

This is one local two-region test-host run, with all clients targeting gru, using the same B2 workload described below.
The machine snapshot at `2026-10-13T23:47:45.051111+00:00` recorded load averages 15.51 / 19.83 / 16.83 and
25,932,566,528 available RAM bytes. This rerun used a different host from the historical run;
CPU, RSS, latency and read-count differences between runs do not establish performance improvement.

At measured HEAD, `ts-heavy` ran all 10 focused ShapeMetricsTests, `make check` (formatting, build/analyzers,
security/OpenAPI, Go race tests and Fast: Endpoints 88, Logging 46, Auth 668, Turso 7, TerraBase 1616), and the Release
host build successfully. The baseline exited 0 and cleaned up its host. Slow was not run.

Raw evidence: [generated report and before/after metric lines](2026-10-13-mariogk-dev-01M4VTW4KTKF2VMVNQRG89034K.md),
[run JSON](2026-10-13-mariogk-dev-01M4VTW4KTKF2VMVNQRG89034K.json), [machine snapshot](2026-10-13-fr17-machine-r3.json).
The earlier rerun at `fc0cfdd7` (run `01M4VRQD55KZP7NR42MJ36GFD4`) keeps its
[report](2026-10-13-mariogk-dev-01M4VRQD55KZP7NR42MJ36GFD4.md),
[JSON](2026-10-13-mariogk-dev-01M4VRQD55KZP7NR42MJ36GFD4.json) and [snapshot](2026-10-13-fr17-machine.json).

## Original B2 run — historical, uninstrumented

The existing changes feed delivers each write separately to each admitted follower. SSE body bytes increased exactly
10× from one to ten followers. The requested 100 and 1000 followers both reached the **32-stream per-database cap**;
68 and 968 requests returned `429 changes_busy`. Every admitted follower received all 600 measured writes.

**Physical origin storage reads are uninstrumented, not zero.** The available document-accounting metric stayed at
zero because this feed bypasses it. The source-derived minimum document SELECT count is an inference, not a measured
storage counter. This baseline supports byte, lag and process-resource comparisons; proving a reduction in physical
storage reads requires instrumentation in a later lane.

### Provenance and machine

| field | value |
| --- | --- |
| Date / run interval (UTC) | 2026-10-13, 21:11:05.395158108–21:15:15.567131860 |
| Run ID | `01M4VHX77FSNPD4CBNYTSSTCEX` |
| Measured HEAD and harness commit | `807c189f09cbacac960c11b345732eadc203385f` |
| Last product/host commit (`src`, `tests`) | `df043b49e7ba0e975fdb902ed0849d5c0af01967` |
| Working tree at run start | clean; fresh Release build from measured HEAD |
| Host | delta; Linux 7.2.10, x86_64 |
| CPU | Intel Core i7-13620H, 16 logical CPUs, affinity 0–15 |
| RAM | 33,335,537,664 bytes (31.05 GiB) |
| Toolchains | Go 1.25.0; .NET SDK 11.0.100-rc.1.26425.128 |
| ts-dotnet checkout | `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` |
| Scheduling | shared machine, `nice -n 19`, default two-slot `ts-heavy`; no CPU isolation |
| Host startup | one launch, 8,032 ms until targets appeared |

The machine snapshot at 21:11:59 UTC recorded load averages 13.07 / 20.12 / 22.81 and 19,215,134,720 available RAM bytes.
These are one shared-host run, not repeated trials. CPU is not monotonic with N here; do not infer a precise CPU slope
from these samples. Delivery p99 ranged from 993.791 to 2228.223 ms under this load.

### Method

The harness uses `apibench.Startup`, `Ready`, `Serve`, `RunSweep` and `WriteReport`. Its fixed target list currently
contains `changes.fanout`; a later shape target registers its endpoint, decoder and readiness adapter on the same
writer, measurement and reporting path, without flags.

Two local `BenchmarkClusterHost` regions (`gru,sjc`) share one process because database placement requires two
serving regions. All client traffic targets gru, over HTTP/1.1 on loopback, with the existing test-only ts-auth stand-in
and temporary storage. No cloud run, WAN, proxy or TLS is included; sjc is the local replica.
This local test-binary baseline includes no cloud discovery or production credentials.

For each requested N = 1, 10, 100, 1000, the harness opens followers at 20/s, one at a time, to avoid the fixture's
independent Hobby QoS admission limit. Only `changes_busy` counts as an expected rejection. After admission, a writer
PUTs unique 256-byte timestamped documents to one collection at a fixed open-loop 20/s: 5 s warmup, 30 s measured,
5 s delivery grace. Each step wrote 700 documents total, 600 in its measured window. The same database and collection
persist across steps, so RSS also includes cumulative data and retained allocations.

Followers use `GET /v1/projects/{p}/databases/{d}/changes?cursor=now&collections={c}`. Delivery lag runs from the
write's send timestamp to receipt of its event, including the write request and storage path; it is not measured
from commit acknowledgement. Deduplicated deliveries of in-window writes count through the 5 s grace. SSE body bytes
count only bytes received during the 30 s window, excluding HTTP headers, admission/rejection traffic and grace bytes.
The final row has fewer in-window bytes despite full grace-inclusive delivery. Window cutoffs can explain this,
but per-event byte arrival times were not recorded.

CPU and RSS are sampled once per second from `/proc/1118268`, the combined host process: **both nodes**, replication,
ts-auth stand-in and test runner. CPU % is mean cores busy × 100 (100% = one core); CPU seconds below are that sampled
mean extrapolated over 30 s. RSS is peak sampled resident memory in MiB. Go follower/writer client resources are
excluded. Admission and warmup CPU are outside the measured interval.

A temporary gru calibration stream identifies exactly one node-scoped `/metrics` listener before measurements,
then closes. Accounting reads and inferred document SELECTs cover **gru only**; sjc reads are excluded. The metric
is published per closed 5 s accounting window; the harness waits 6 s after each step before its final scrape.

### Measured results

| requested N | admitted | rejected (`changes_busy`) | writes | delivered | missed | SSE body bytes in 30 s |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 0 | 600 | 600 | 0 | 329872 |
| 10 | 10 | 0 | 600 | 6000 | 0 | 3298720 |
| 100 | 32 | 68 | 600 | 19200 | 0 | 10555904 |
| 1000 | 32 | 968 | 600 | 19200 | 0 | 10021727 |

| requested N | p50 lag ms | p99 lag ms | sampled CPU s | CPU % (one core = 100) | peak RSS MiB |
| --- | --- | --- | --- | --- | --- |
| 1 | 11.095 | 993.791 | 19.148321 | 63.827735 | 252.433594 |
| 10 | 13.655 | 2046.975 | 16.862252 | 56.207505 | 259.105469 |
| 100 | 18.991 | 1751.039 | 27.309085 | 91.030283 | 286.578125 |
| 1000 | 55.167 | 2228.223 | 30.267173 | 100.890577 | 281.515625 |

| requested N | physical feed storage reads | accounting reads delta (measured) | minimum document SELECTs for in-window writes (inferred) |
| --- | --- | --- | --- |
| 1 | N/A — uninstrumented | 0 | 600 |
| 10 | N/A — uninstrumented | 0 | 6000 |
| 100 | N/A — uninstrumented | 0 | 19200 |
| 1000 | N/A — uninstrumented | 0 | 19200 |

[DatabaseChanges.cs](../../src/TerraBase/Features/Documents/DatabaseChanges.cs) sets 32 open streams per database on a
machine and 1024 per machine. This single-database experiment hits the first limit; **it cannot exercise the 1024
machine limit**. N=1000 is a request count, not 1000 active readers. No scaling gain beyond 32 admitted readers is claimed.

[SqliteStorageTransaction.Feed.cs](../../src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Feed.cs) executes a document
SELECT for each returned upsert per stream. The inference counts received upsert events **of in-window writes** through delivery grace (including redeliveries,
if any); it excludes the 100 warmup writes per follower, feed-table pages, cursor/head/floor reads, other server work
and actual disk I/O/cache behavior. It is a lower bound for this write cohort, not a total for the entire step.
It therefore cannot substitute for physical-read metrics. Byte fan-out is directly measured; linear document SELECT
fan-out is source-derived.

### Reproduce and validation

From the measured commit, with the pinned sibling ts-dotnet checkout:

```sh
TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet \
  ~/.local/bin/ts-heavy make terrabase-build CONFIGURATION=Release
cd benchmark
~/.local/bin/ts-heavy go run ./cmd/shape-bench
```

At measured HEAD, `ts-heavy make check TS_DOTNET_DIR=/home/mariogk/Projects/TerraScale/ts-dotnet` passed formatting,
build/analyzers/security checks, all Go race tests and Fast suites: Endpoints 88, Logging 46, Auth 664, vendored Turso 6,
TerraBase 1586. The Release host build passed with zero warnings/errors. The benchmark exited 0 and cleaned up its host.
Slow suites were not run locally. The initial one-region attempt failed before measurements with
`placement_needs_two_regions`; this successful baseline uses the corrected two-region host.

### Raw metric lines

changes.fanout N=1 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 0
```

changes.fanout N=1 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
```

changes.fanout N=10 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
```

changes.fanout N=10 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
```

changes.fanout N=100 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
```

changes.fanout N=100 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
```

changes.fanout N=1000 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
```

changes.fanout N=1000 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2800
```

### Raw machine snapshot

```json
{
  "recorded_utc": "2026-10-13T21:11:59.511426+00:00",
  "host": "delta",
  "kernel": "7.2.10",
  "architecture": "x86_64",
  "cpu": "13th Gen Intel(R) Core(TM) i7-13620H",
  "logical_cpus": 16,
  "ram_bytes": 33335537664,
  "available_ram_bytes": 19215134720,
  "cpu_affinity": [
    0,
    1,
    2,
    3,
    4,
    5,
    6,
    7,
    8,
    9,
    10,
    11,
    12,
    13,
    14,
    15
  ],
  "loadavg": "13.07 20.12 22.81 1/2946 1121113",
  "go": "go version go1.25.0 linux/amd64",
  "dotnet_sdk": "11.0.100-rc.1.26425.128",
  "ts_dotnet_commit": "bcd20603f11f3aa54dc54cec6e410b4fe9e24151"
}
```

### Raw shared report JSON

The following is the unmodified `apibench.WriteReport` JSON. Network probe, auth timing, startup RSS and `bytes_out`
zeros are unmeasured placeholders. Its companion Markdown uses a generic “5 launches” heading; this run launched
one host. Latency units below are microseconds; `rss_mb` is MiB. These raw values retain precision beyond the tables.

```json
{
  "id": "01M4VHX77FSNPD4CBNYTSSTCEX",
  "host": "delta",
  "cluster_state": "local-gru,sjc",
  "endpoint": "http://127.0.0.1:38313/",
  "start": "2026-10-13T21:11:05.395158108Z",
  "end": "2026-10-13T21:15:15.56713186Z",
  "probe": {
    "tcp_connect_ms": {
      "min": 0,
      "median": 0
    },
    "tls_handshake_ms": {
      "min": 0,
      "median": 0
    },
    "healthz_ms": {
      "min": 0,
      "median": 0
    }
  },
  "startup": [
    {
      "client": "local terrabase host (1 launch)",
      "wall_ms": {
        "min": 8032,
        "median": 8032
      },
      "init_ms": {
        "min": 8032,
        "median": 8032
      },
      "max_rss_mb": {
        "min": 0,
        "median": 0
      }
    }
  ],
  "clients": [
    {
      "client": "shape-bench",
      "ready": {
        "type": "",
        "client": "shape-bench",
        "sdk": "raw HTTP/1.1 SSE",
        "sdk_version": "-",
        "runtime": "go1.25.0",
        "database": "canary",
        "setup_ms": 106,
        "auth_ms": 0
      },
      "results": [
        {
          "type": "result",
          "scenario": "changes.fanout",
          "transport": "sse-http1",
          "payload_bytes": 256,
          "mode": "open",
          "concurrency": 1,
          "rate": 20,
          "start": "2026-10-13T21:11:18.5442999Z",
          "duration_s": 30,
          "ops": 600,
          "ok": 600,
          "errors": 0,
          "error_rate": 0,
          "rps": 20,
          "status": {
            "accounting_reads_delta": 0,
            "admitted": 1,
            "inferred_min_doc_selects": 600,
            "writes": 600
          },
          "latency_us": {
            "p50": 11095,
            "p90": 16351,
            "p99": 993791,
            "p999": 1245183,
            "max": 1249279,
            "mean": 39942.86666666667
          },
          "bytes_out": 0,
          "bytes_in": 329872,
          "cpu_s": 19.14832059177839,
          "rss_mb": 252.43359375,
          "stopped": ""
        },
        {
          "type": "result",
          "scenario": "changes.fanout",
          "transport": "sse-http1",
          "payload_bytes": 256,
          "mode": "open",
          "concurrency": 10,
          "rate": 20,
          "start": "2026-10-13T21:12:05.082034622Z",
          "duration_s": 30,
          "ops": 6000,
          "ok": 6000,
          "errors": 0,
          "error_rate": 0,
          "rps": 200,
          "status": {
            "accounting_reads_delta": 0,
            "admitted": 10,
            "inferred_min_doc_selects": 6000,
            "writes": 600
          },
          "latency_us": {
            "p50": 13655,
            "p90": 650751,
            "p99": 2046975,
            "p999": 2347007,
            "max": 2349055,
            "mean": 170354.08366666667
          },
          "bytes_out": 0,
          "bytes_in": 3298720,
          "cpu_s": 16.86225159403346,
          "rss_mb": 259.10546875,
          "stopped": ""
        },
        {
          "type": "result",
          "scenario": "changes.fanout",
          "transport": "sse-http1",
          "payload_bytes": 256,
          "mode": "open",
          "concurrency": 100,
          "rate": 20,
          "start": "2026-10-13T21:12:56.096290599Z",
          "duration_s": 30,
          "ops": 19200,
          "ok": 19200,
          "errors": 0,
          "error_rate": 0,
          "rps": 640,
          "status": {
            "accounting_reads_delta": 0,
            "admitted": 32,
            "inferred_min_doc_selects": 19200,
            "rejected_changes_busy": 68,
            "writes": 600
          },
          "latency_us": {
            "p50": 18991,
            "p90": 285695,
            "p99": 1751039,
            "p999": 2035711,
            "max": 2050047,
            "mean": 123550.8634375
          },
          "bytes_out": 0,
          "bytes_in": 10555904,
          "cpu_s": 27.309084891717063,
          "rss_mb": 286.578125,
          "stopped": ""
        },
        {
          "type": "result",
          "scenario": "changes.fanout",
          "transport": "sse-http1",
          "payload_bytes": 256,
          "mode": "open",
          "concurrency": 1000,
          "rate": 20,
          "start": "2026-10-13T21:14:32.112342365Z",
          "duration_s": 30,
          "ops": 19200,
          "ok": 19200,
          "errors": 0,
          "error_rate": 0,
          "rps": 640,
          "status": {
            "accounting_reads_delta": 0,
            "admitted": 32,
            "inferred_min_doc_selects": 19200,
            "rejected_changes_busy": 968,
            "writes": 600
          },
          "latency_us": {
            "p50": 55167,
            "p90": 1611775,
            "p99": 2228223,
            "p999": 2512895,
            "max": 2529279,
            "mean": 514809.7415625
          },
          "bytes_out": 0,
          "bytes_in": 10021727,
          "cpu_s": 30.26717296292045,
          "rss_mb": 281.515625,
          "stopped": ""
        }
      ],
      "done": {
        "type": "done",
        "cleanup": true,
        "cleanup_error": ""
      }
    }
  ]
}

```
