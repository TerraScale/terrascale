# API benchmark 01M4VRQD55KZP7NR42MJ36GFD4

- Host: mariogk-dev
- Start (UTC): 2026-10-13T23:10:13Z
- End (UTC): 2026-10-13T23:14:25Z
- Endpoint: http://127.0.0.1:46483/
- Cluster state: local-gru,sjc
- shape-bench: raw HTTP/1.1 SSE -, runtime go1.25.0, database canary, setup 230.0 ms, auth 0.0 ms, cleanup true

## Network probe

| check | min ms | median ms |
| --- | --- | --- |
| TCP connect | 0.00 | 0.00 |
| TLS handshake | 0.00 | 0.00 |
| GET /healthz | 0.00 | 0.00 |

## Startup (5 launches, min / median)

| client | wall ms | init ms | max RSS MB |
| --- | --- | --- | --- |
| local terrabase host (1 launch) | 10037.0 / 10037.0 | 10037.00 / 10037.00 | 0.0 / 0.0 |

## Results

### changes.fanout

| client | transport | payload | mode | conc | rate | req/s | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms | err % | statuses | MB out | MB in | CPU % | RSS MB | stopped |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| shape-bench | sse-http1 | 256 | open | 1 | 20 | 20.0 | 15.52 | 22.02 | 37.73 | 91.33 | 120.13 | 0.00 | accounting_reads_delta:0 admitted:1 inferred_min_doc_selects:600 storage_reads_delta:2104 writes:600 | 0.00 | 0.31 | 87.4 | 255.8 | - |
| shape-bench | sse-http1 | 256 | open | 10 | 20 | 200.0 | 27.26 | 37.38 | 119.55 | 267.77 | 269.31 | 0.00 | accounting_reads_delta:0 admitted:10 inferred_min_doc_selects:6000 storage_reads_delta:20998 writes:600 | 0.00 | 3.15 | 113.5 | 251.6 | - |
| shape-bench | sse-http1 | 256 | open | 100 | 20 | 640.0 | 34.17 | 42.43 | 71.74 | 113.86 | 127.10 | 0.00 | accounting_reads_delta:0 admitted:32 inferred_min_doc_selects:19200 rejected_changes_busy:68 storage_reads_delta:67496 writes:600 | 0.00 | 10.07 | 158.4 | 267.9 | - |
| shape-bench | sse-http1 | 256 | open | 1000 | 20 | 640.0 | 37.31 | 54.37 | 156.29 | 214.66 | 218.24 | 0.00 | accounting_reads_delta:0 admitted:32 inferred_min_doc_selects:19200 rejected_changes_busy:968 storage_reads_delta:67670 writes:600 | 0.00 | 10.07 | 164.3 | 273.4 | - |

## Fan-out notes

- Commit: fc0cfdd7e962a3689f5ea163afc1e5dd49861a9f; last product/host commit (src, tests): fc0cfdd7e962a3689f5ea163afc1e5dd49861a9f; last harness commit (benchmark/cmd/shape-bench): 8ebdf30c3ccb8f91f4bea5afdf171e74983f4e44; working tree: clean
- Two local nodes (gru,sjc) in the tests/TerraBase.E2E BenchmarkClusterHost process: placement requires two regions; writes and followers target only gru. Plain HTTP on loopback: no WAN, no Fly Proxy, no TLS. No network probe ran; the startup row is one host launch until its targets file appeared.
- Shared reporter placeholders: network probe, startup max RSS, MB out, and auth ms are unmeasured here; their zeros are placeholders. The shared startup heading says 5 launches, but this run has one launch.
- Writer: 20 PUTs/s of 256-byte documents into one collection, open loop; every write must succeed. Followers: GET …/changes?cursor=now&collections=<that collection>, opened one at a time at 20/s, each awaiting its admission or rejection (the local tenant is on the Hobby QoS plan), then all read together through the measured window.
- Columns: conc = requested N; rate = writes/s; req/s = deliveries/s summed over admitted followers; p50…max = delivery lag from the write's send time (in the document) to the follower reading its change event; err % = missed deliveries of in-window writes to admitted followers (grace 5s); MB in = SSE response body bytes of admitted streams in the window; CPU % and RSS MB = the combined host process (/proc/539187: both nodes, replication work, ts-auth stand-in and test runner), not this client.
- Admission: the server admits at most 32 streams per database per machine and 1024 per machine (DatabaseChanges.cs). Above 32, only admitted followers are measured: req/s and MB in do not grow with requested N, and no gain past the cap is claimed.
- storage_reads_delta (primary): terrabase.storage.reads{path="feed"} (terrabase_storage_reads_total) from http://127.0.0.1:38855/metrics, after minus before the step, gru node only; sjc reads are not included. It counts every physical backend SELECT the feed runs on this node: head, floor, prefix and retention reads, and each document body, including zero-row reads and misses. Page cache hits count; this is not disk I/O.
- Storage window: the scrape before admission (before the first follower opens) through the scrape after warmup, the 30 s measured window, grace and a 6 s settle. A step's delta therefore includes admission, warmup and grace reads, not the measured window alone. A fall of the counter fails the step (reset or restarted node); an absent feed series fails it too, never as zero.
- accounting_reads_delta (secondary): terrabase.stats.operations{kind="read"} from http://127.0.0.1:38855/metrics, after minus before the step, gru node only. It is document accounting (document GETs and queries), published per closed 5 s window; it is not a storage read count.
- inferred_min_doc_selects (secondary, a source-based estimate, not measured): gru feed only. SqliteStorageTransaction.Feed.cs reads each upsert's current document with one SELECT per stream. The estimate counts received upserts of in-window writes through delivery grace, including redeliveries; warmup upserts, feed-table reads and other storage work are excluded. Compare it with storage_reads_delta, which is measured.

| target | requested N | admitted | rejected | writes in window | delivered | missed | deliveries/s per admitted follower | storage reads delta (feed) | accounting reads delta (secondary) | inferred min doc SELECTs (secondary estimate) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| changes.fanout | 1 | 1 | 0 | 600 | 600 | 0 | 20.00 | 2104 | 0 | 600 |
| changes.fanout | 10 | 10 | 0 | 600 | 6000 | 0 | 20.00 | 20998 | 0 | 6000 |
| changes.fanout | 100 | 32 | 68 changes_busy | 600 | 19200 | 0 | 20.00 | 67496 | 0 | 19200 |
| changes.fanout | 1000 | 32 | 968 changes_busy | 600 | 19200 | 0 | 20.00 | 67670 | 0 | 19200 |

### Raw metric lines

changes.fanout N=1 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 0
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 117
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 3
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2
```

changes.fanout N=1 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 7321
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2106
```

changes.fanout N=10 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 7321
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2106
```

changes.fanout N=10 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 14570
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 23104
```

changes.fanout N=100 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 14570
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 23104
```

changes.fanout N=100 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 22580
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 90600
```

changes.fanout N=1000 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 22580
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 90600
```

changes.fanout N=1000 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2800
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 38013
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 87
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158270
```
