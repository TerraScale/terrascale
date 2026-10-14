# API benchmark 01M4VTW4KTKF2VMVNQRG89034K

- Host: mariogk-dev
- Start (UTC): 2026-10-13T23:47:45Z
- End (UTC): 2026-10-13T23:51:57Z
- Endpoint: http://127.0.0.1:43645/
- Cluster state: local-gru,sjc
- shape-bench: raw HTTP/1.1 SSE -, runtime go1.25.0, database canary, setup 466.0 ms, auth 0.0 ms, cleanup true

## Network probe

| check | min ms | median ms |
| --- | --- | --- |
| TCP connect | 0.00 | 0.00 |
| TLS handshake | 0.00 | 0.00 |
| GET /healthz | 0.00 | 0.00 |

## Startup (5 launches, min / median)

| client | wall ms | init ms | max RSS MB |
| --- | --- | --- | --- |
| local terrabase host (1 launch) | 10040.0 / 10040.0 | 10040.00 / 10040.00 | 0.0 / 0.0 |

## Results

### changes.fanout

| client | transport | payload | mode | conc | rate | req/s | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms | err % | statuses | MB out | MB in | CPU % | RSS MB | stopped |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| shape-bench | sse-http1 | 256 | open | 1 | 20 | 20.0 | 22.40 | 32.66 | 77.95 | 153.09 | 175.74 | 0.00 | accounting_reads_delta:0 admitted:1 inferred_min_doc_selects:600 storage_reads_delta:2098 writes:600 | 0.00 | 0.31 | 104.9 | 258.7 | - |
| shape-bench | sse-http1 | 256 | open | 10 | 20 | 200.0 | 31.31 | 60.64 | 165.25 | 311.81 | 360.96 | 0.00 | accounting_reads_delta:0 admitted:10 inferred_min_doc_selects:6000 storage_reads_delta:20888 writes:600 | 0.00 | 3.15 | 120.7 | 251.1 | - |
| shape-bench | sse-http1 | 256 | open | 100 | 20 | 640.0 | 44.70 | 76.67 | 245.12 | 361.47 | 406.78 | 0.00 | accounting_reads_delta:0 admitted:32 inferred_min_doc_selects:19200 rejected_changes_busy:68 storage_reads_delta:66764 writes:600 | 0.00 | 10.10 | 171.1 | 274.8 | - |
| shape-bench | sse-http1 | 256 | open | 1000 | 20 | 640.0 | 45.95 | 71.55 | 378.62 | 624.13 | 637.95 | 0.00 | accounting_reads_delta:0 admitted:32 inferred_min_doc_selects:19200 rejected_changes_busy:968 storage_reads_delta:67026 writes:600 | 0.00 | 10.08 | 168.7 | 274.6 | - |

## Fan-out notes

- Commit: cc89673676657f9af9e3d9fff920e0a47b248411; last product/host commit (src, tests): cc89673676657f9af9e3d9fff920e0a47b248411; last harness commit (benchmark/cmd/shape-bench): 8ebdf30c3ccb8f91f4bea5afdf171e74983f4e44; working tree: clean
- Two local nodes (gru,sjc) in the tests/TerraBase.E2E BenchmarkClusterHost process: placement requires two regions; writes and followers target only gru. Plain HTTP on loopback: no WAN, no Fly Proxy, no TLS. No network probe ran; the startup row is one host launch until its targets file appeared.
- Shared reporter placeholders: network probe, startup max RSS, MB out, and auth ms are unmeasured here; their zeros are placeholders. The shared startup heading says 5 launches, but this run has one launch.
- Writer: 20 PUTs/s of 256-byte documents into one collection, open loop; every write must succeed. Followers: GET …/changes?cursor=now&collections=<that collection>, opened one at a time at 20/s, each awaiting its admission or rejection (the local tenant is on the Hobby QoS plan), then all read together through the measured window.
- Columns: conc = requested N; rate = writes/s; req/s = deliveries/s summed over admitted followers; p50…max = delivery lag from the write's send time (in the document) to the follower reading its change event; err % = missed deliveries of in-window writes to admitted followers (grace 5s); MB in = SSE response body bytes of admitted streams in the window; CPU % and RSS MB = the combined host process (/proc/627656: both nodes, replication work, ts-auth stand-in and test runner), not this client.
- Admission: the server admits at most 32 streams per database per machine and 1024 per machine (DatabaseChanges.cs). Above 32, only admitted followers are measured: req/s and MB in do not grow with requested N, and no gain past the cap is claimed.
- storage_reads_delta (primary): terrabase.storage.reads{path="feed"} (terrabase_storage_reads_total) from http://127.0.0.1:45869/metrics, after minus before the step, gru node only; sjc reads are not included. It counts every physical backend SELECT the feed runs on this node: head, floor, prefix and retention reads, and each document body, including zero-row reads and misses. Page cache hits count; this is not disk I/O.
- Storage window: the scrape before admission (before the first follower opens) through the scrape after warmup, the 30 s measured window, grace and a 6 s settle. A step's delta therefore includes admission, warmup and grace reads, not the measured window alone. A fall of the counter fails the step (reset or restarted node); an absent feed series fails it too, never as zero.
- accounting_reads_delta (secondary): terrabase.stats.operations{kind="read"} from http://127.0.0.1:45869/metrics, after minus before the step, gru node only. It is document accounting (document GETs and queries), published per closed 5 s window; it is not a storage read count.
- inferred_min_doc_selects (secondary, a source-based estimate, not measured): gru feed only. SqliteStorageTransaction.Feed.cs reads each upsert's current document with one SELECT per stream. The estimate counts received upserts of in-window writes through delivery grace, including redeliveries; warmup upserts, feed-table reads and other storage work are excluded. Compare it with storage_reads_delta, which is measured.

| target | requested N | admitted | rejected | writes in window | delivered | missed | deliveries/s per admitted follower | storage reads delta (feed) | accounting reads delta (secondary) | inferred min doc SELECTs (secondary estimate) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| changes.fanout | 1 | 1 | 0 | 600 | 600 | 0 | 20.00 | 2098 | 0 | 600 |
| changes.fanout | 10 | 10 | 0 | 600 | 6000 | 0 | 20.00 | 20888 | 0 | 6000 |
| changes.fanout | 100 | 32 | 68 changes_busy | 600 | 19200 | 0 | 20.00 | 66764 | 0 | 19200 |
| changes.fanout | 1000 | 32 | 968 changes_busy | 600 | 19200 | 0 | 20.00 | 67026 | 0 | 19200 |

### Raw metric lines

changes.fanout N=1 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 0
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 182
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 3
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2
```

changes.fanout N=1 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 8126
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2100
```

changes.fanout N=10 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 700
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 8126
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2100
```

changes.fanout N=10 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 16117
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 22988
```

changes.fanout N=100 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 1400
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 16117
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 22988
```

changes.fanout N=100 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 24874
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 89752
```

changes.fanout N=1000 before:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2100
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 24874
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 89752
```

changes.fanout N=1000 after:

```
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 0
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 2800
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 41112
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 87
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 156778
```
