# API benchmark 01M4WQ54KYRK9ZMR8CHRB3FENA

- Host: mariogk-dev
- Start (UTC): 2026-10-14T07:57:28Z
- End (UTC): 2026-10-14T08:06:12Z
- Endpoint: http://127.0.0.1:32775/
- Cluster state: local-gru,sjc
- shape-bench: raw HTTP/1.1 (SSE and JSON long-poll) -, runtime go1.25.0, database canary, setup 232.0 ms, auth 0.0 ms, cleanup true

## Network probe

| check | min ms | median ms |
| --- | --- | --- |
| TCP connect | 0.00 | 0.00 |
| TLS handshake | 0.00 | 0.00 |
| GET /healthz | 0.00 | 0.00 |

## Startup (5 launches, min / median)

| client | wall ms | init ms | max RSS MB |
| --- | --- | --- | --- |
| local terrabase host (1 launch) | 9037.0 / 9037.0 | 9037.00 / 9037.00 | 0.0 / 0.0 |

## Results

### changes.fanout

| client | transport | payload | mode | conc | rate | req/s | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms | err % | statuses | MB out | MB in | CPU % | RSS MB | stopped |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| shape-bench | sse-http1 | 256 | open | 1 | 20 | 20.0 | 14.54 | 18.14 | 24.22 | 29.73 | 53.12 | 0.00 | accounting_reads_delta:0 admitted:1 inferred_min_doc_selects:600 qos_busy_followers:0 qos_busy_writes:0 retries:0 server_at_ready:1 storage_reads_delta:2106 writes:600 | 0.00 | 0.31 | 96.1 | 250.7 | - |
| shape-bench | sse-http1 | 256 | open | 10 | 20 | 200.0 | 18.62 | 23.01 | 31.58 | 55.90 | 56.32 | 0.00 | accounting_reads_delta:0 admitted:10 inferred_min_doc_selects:6000 qos_busy_followers:0 qos_busy_writes:0 retries:0 server_at_ready:10 storage_reads_delta:21112 writes:600 | 0.00 | 3.15 | 107.7 | 251.4 | - |
| shape-bench | sse-http1 | 256 | open | 100 | 20 | 640.0 | 28.46 | 34.21 | 43.81 | 89.22 | 89.66 | 0.00 | accounting_reads_delta:0 admitted:32 inferred_min_doc_selects:19200 qos_busy_followers:0 qos_busy_writes:0 rejected_changes_busy:68 retries:0 server_at_ready:32 storage_reads_delta:67596 writes:600 | 0.00 | 10.07 | 150.4 | 269.9 | - |
| shape-bench | sse-http1 | 256 | open | 1000 | 20 | 640.0 | 28.89 | 34.78 | 43.58 | 67.90 | 68.42 | 0.00 | accounting_reads_delta:0 admitted:32 inferred_min_doc_selects:19200 qos_busy_followers:0 qos_busy_writes:0 rejected_changes_busy:968 retries:0 server_at_ready:32 storage_reads_delta:67962 writes:600 | 0.00 | 10.07 | 151.9 | 277.2 | - |

### shape.fanout

| client | transport | payload | mode | conc | rate | req/s | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms | err % | statuses | MB out | MB in | CPU % | RSS MB | stopped |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| shape-bench | http1-long-poll | 256 | open | 1 | 20 | 20.0 | 1126.40 | 1544.19 | 1732.61 | 1764.35 | 1770.49 | 0.00 | accounting_reads_delta:0 admitted:1 qos_busy_followers:0 qos_busy_writes:0 retries:0 server_at_ready:1 storage_reads_delta_point:8568 storage_reads_delta_shape:86136 storage_reads_delta_shape_body:50 writes:600 | 0.00 | 0.24 | 129.1 | 285.9 | - |
| shape-bench | http1-long-poll | 256 | open | 10 | 20 | 200.0 | 722.94 | 922.62 | 998.40 | 1010.69 | 1010.69 | 0.00 | accounting_reads_delta:0 admitted:10 qos_busy_followers:0 qos_busy_writes:0 retries:6 server_at_ready:10 storage_reads_delta_point:18539 storage_reads_delta_shape:46212 storage_reads_delta_shape_body:537 writes:600 | 0.00 | 2.31 | 137.9 | 320.2 | - |
| shape-bench | http1-long-poll | 256 | open | 100 | 0 | 0.0 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | admitted:68 qos_busy_followers:0 rejected_shape_busy:32 retries:1156 | 0.00 | 0.00 | 0.0 | 0.0 | shape step not ready within 1m0s: 66 of 68 admitted followers sent a live poll, the node holds 64 parked requesters |
| shape-bench | http1-long-poll | 256 | open | 1000 | 0 | 0.0 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | admitted:76 qos_busy_followers:0 rejected_shape_busy:924 retries:4419 | 0.00 | 0.00 | 0.0 | 0.0 | shape step not ready within 1m0s: 73 of 76 admitted followers sent a live poll, the node holds 64 parked requesters |

## Fan-out notes

- Commit: 1c5e7814c6935beabcef369d9bb59313690a057b; last product/host commit (src, tests): 1c5e7814c6935beabcef369d9bb59313690a057b; last harness commit (benchmark/cmd/shape-bench): 1c5e7814c6935beabcef369d9bb59313690a057b; working tree: clean
- Two local nodes (gru,sjc) in the tests/TerraBase.E2E BenchmarkClusterHost process: placement requires two regions; writes and followers target only gru. Plain HTTP on loopback: no WAN, no Fly Proxy, no TLS. No network probe ran; the startup row is one host launch until its targets file appeared.
- Shared reporter placeholders: network probe, startup max RSS, MB out, and auth ms are unmeasured here; their zeros are placeholders. The shared startup heading says 5 launches, but this run has one launch.
- Writer: 20 PUTs/s of 256-byte documents into one collection, open loop; every write must succeed. Followers are opened one at a time at 20/s, each awaiting its admission or rejection (the local tenant is on the Hobby QoS plan), then all read together through the measured window. Per target: changes.fanout opens GET …/changes?cursor=now&collections=<that collection> as an SSE stream; shape.fanout opens GET …/shape as HTTP/1.1 JSON requests (bootstrap offset=-1, then snapshot pages, chunks and live long-polls).
- Columns: conc = requested N; rate = writes/s; req/s = deliveries/s summed over admitted followers; p50…max = delivery lag from the write's send time (in the document) to the follower reading its change event; err % = missed deliveries of in-window writes to admitted followers (grace 5s); MB in = response body bytes read by admitted followers in the window (changes: SSE stream bytes; shape: JSON response bodies); CPU % and RSS MB = the combined host process (/proc/1041027: both nodes, replication work, ts-auth stand-in and test runner), not this client.
- QoS budget: the local tenant is on the Hobby plan (QosPolicy.cs: 50 requests/s sustained, burst 100, concurrency 4). The writer and every follower's request share it. A refusal is 429 tenant_busy with Retry-After; each is repeated after its Retry-After (at least 200 ms) and counted: qos_busy_followers (both targets) and qos_busy_writes. A repeat delays a delivery and is not a loss; a delivery that misses the window and grace is counted as missed.
- Admission, changes.fanout: the server admits at most 32 streams per database per machine and 1024 per machine (DatabaseChanges.cs); rejections are changes_busy. Above 32, only admitted followers are measured: req/s and MB in do not grow with requested N, and no gain past the cap is claimed.
- Admission, shape.fanout: a follower is admitted on its first 200 body. Refusals before admission are shape_busy (429, the running-flight caps: 32 per machine and 8 per organization (ShapeFlights.cs MaxFlights, MaxFlightsPerOrganization), no queue) and shape_too_large (503); anything else is an invalid measurement. Concurrent identical live polls join one flight. Live requesters are capped at 256 per machine and 64 per organization, across all flights (ShapeFlights.cs MaxRequesters, MaxRequestersPerOrganization); a live poll beyond that answers shape_busy and is retried, so at N above 256 only the parked ones receive writes. server_at_ready is the gauge terrabase.shape.requests.waiting when the step became ready (shape), or the open feed streams (changes). req/s and origin reads are the measured outcome, not an assumed collapse.
- Capacity rows: a step that reaches a limit is a row, not a failed run. Its stopped column names the limit: no follower admitted, the admission deadline, or a readiness timeout (all before the window, so writes, deliveries, storage reads and latency are n/a; the shared table's zeros there are placeholders, not measurements), or no delivery in the window (measured: every expected delivery is missed; its latency is n/a, since no delivery was measured, and the shared table's zeros there are placeholders). Rejected counts and qos_busy_followers are kept for every row.
- Storage reads (primary, measured): terrabase.storage.reads{path} (terrabase_storage_reads_total) from http://127.0.0.1:39211/metrics, after minus before the step, gru node only; sjc reads are not included. Each target names its paths. The feed reports path="feed": head, floor, prefix and retention reads, and each document body. The shape target reports path="shape" (metadata SQL), path="shape_body" (payload-body SELECTs) and path="point" (document checks) as separate deltas, never summed here. The point delta is node-wide: it also counts the writer's point reads during the step, so it is not attributable to shape followers alone. Every count includes zero-row reads and misses; page cache hits count; this is not disk I/O.
- Storage window: the scrape before admission (before the first follower opens) through the scrape after warmup, the 30 s measured window, grace and a 6 s settle. A step's delta therefore includes admission, warmup and grace reads, not the measured window alone. A fall of a counter fails the step (reset or restarted node). A path absent before the step counts as zero, since its counter starts at zero in a process; a path absent after it fails the step, never as zero.
- Shape followers: each bootstraps at offset=-1 (307 to the newest snapshot's first page), pages the snapshot, catches up through chunks, then long-polls live at the sealed head. Their response bytes are JSON bodies over HTTP/1.1, not SSE; each request is one answer, and each follower holds at most one open live poll. Retries after shape_busy or unavailable are counted in the status "retries"; they are neither delivered nor missed.
- accounting_reads_delta (secondary): terrabase.stats.operations{kind="read"} from http://127.0.0.1:39211/metrics, after minus before the step, gru node only. It is document accounting (document GETs and queries), published per closed 5 s window; it is not a storage read count.
- inferred_min_doc_selects (secondary, a source-based estimate, not measured; changes target only): gru feed only. SqliteStorageTransaction.Feed.cs reads each upsert's current document with one SELECT per stream. The estimate counts received upserts of in-window writes through delivery grace, including redeliveries; warmup upserts, feed-table reads and other storage work are excluded. Compare it with the measured storage reads, not as equivalent to them.

| target | requested N | admitted | rejected | writes in window | delivered | missed | deliveries/s per admitted follower | storage reads delta (by path) | accounting reads delta (secondary) | inferred min doc SELECTs (secondary estimate) | stopped |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| changes.fanout | 1 | 1 | 0 | 600 | 600 | 0 | 20.00 | 2106 | 0 | 600 | - |
| changes.fanout | 10 | 10 | 0 | 600 | 6000 | 0 | 20.00 | 21112 | 0 | 6000 | - |
| changes.fanout | 100 | 32 | 68 changes_busy | 600 | 19200 | 0 | 20.00 | 67596 | 0 | 19200 | - |
| changes.fanout | 1000 | 32 | 968 changes_busy | 600 | 19200 | 0 | 20.00 | 67962 | 0 | 19200 | - |
| shape.fanout | 1 | 1 | 0 | 600 | 600 | 0 | 20.00 | point 8568; shape 86136; shape_body 50 | 0 | n/a | - |
| shape.fanout | 10 | 10 | 0 | 600 | 6000 | 0 | 20.00 | point 18539; shape 46212; shape_body 537 | 0 | n/a | - |
| shape.fanout | 100 | 68 | 32 shape_busy | n/a | n/a | n/a | n/a | n/a | n/a | n/a | shape step not ready within 1m0s: 66 of 68 admitted followers sent a live poll, the node holds 64 parked requesters |
| shape.fanout | 1000 | 76 | 924 shape_busy | n/a | n/a | n/a | n/a | n/a | n/a | n/a | shape step not ready within 1m0s: 73 of 76 admitted followers sent a live poll, the node holds 64 parked requesters |

### Raw metric lines

changes.fanout N=1 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 0
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 196
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 3
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2
```

changes.fanout N=1 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 8160
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2108
```

changes.fanout N=10 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 8160
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2108
```

changes.fanout N=10 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 16175
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 23220
```

changes.fanout N=100 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 16175
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 23220
```

changes.fanout N=100 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 24960
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 90816
```

changes.fanout N=1000 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 24960
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 90816
```

changes.fanout N=1000 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2800
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 41239
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 87
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
```

shape.fanout N=1 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2800
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 41239
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 87
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
```

shape.fanout N=1 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 3500
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 49807
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 102
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape"} 86136
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape_body"} 50
terrabase_shape_reads_total{otel_scope_name="TerraScale.TerraBase"} 73
terrabase_shape_requests_waiting{otel_scope_name="TerraScale.TerraBase"} 0
terrabase_shape_flights_active{otel_scope_name="TerraScale.TerraBase"} 0
```

shape.fanout N=10 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 3500
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 49807
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 102
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape"} 86136
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape_body"} 50
terrabase_shape_reads_total{otel_scope_name="TerraScale.TerraBase"} 73
terrabase_shape_requests_waiting{otel_scope_name="TerraScale.TerraBase"} 0
terrabase_shape_flights_active{otel_scope_name="TerraScale.TerraBase"} 0
```

shape.fanout N=10 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 4200
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 68346
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 120
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape"} 132348
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape_body"} 587
terrabase_shape_reads_total{otel_scope_name="TerraScale.TerraBase"} 1363
terrabase_shape_requests_waiting{otel_scope_name="TerraScale.TerraBase"} 0
terrabase_shape_flights_active{otel_scope_name="TerraScale.TerraBase"} 0
```

shape.fanout N=100: no window or final scrape recorded; see the result's stopped reason or the client error for the outcome.

shape.fanout N=1000: no window or final scrape recorded; see the result's stopped reason or the client error for the outcome.
