# Node pressure (TS-478)

`GET /v1/node/pressure` returns a cached JSON sample on the loopback administrative
listener, alongside `/metrics`. Both return 404 on client and peer listeners.
Responses have `Cache-Control: no-store`; fetching never refreshes measurements,
scans documents/files, or changes `sampled_at`. Concurrent readers share an
immutable sample. Access from another machine requires an operator-managed local
forward; this change adds no public listener or network access.

## Autoscaling remains gated

The consumer contract is ts-orchestrator PR #150, branch
`cos-hold/ts-476-autoscaler`, `internal/cellscale/pressure.go` at
`5e819df257a20facd0ec557bb65c29b4228e9bd6`: schema 1,
`/v1/node/pressure`, at most 64 KiB, UTC RFC3339 timestamps, rates per second,
utilizations in `[0,1]`, and p95 latency in milliseconds. Its freshness bounds
are two minutes old and five seconds ahead of the consumer clock. A producer
sample's timestamp is its measurement time, never fetch time.

**This endpoint does not enable autonomous scaling.** COS authorized telemetry
first, with explicit unknowns. The consumer rejects null required counters and
`state: "unknown"`, and its existing unknown-lag regression verifies a hold with
no mutations. Preserving those failures keeps autoscaling gated. Substituting
zero, HTTP liveness, request share, or connection-pool occupancy would invent
information used to authorize destructive autoscaler actions.

Follow up after [terrabase#13](https://git.terrascale.tech/TerraScale/terrabase/pulls/13)
(I/O admission pool and memory budgeting) and
[terrabase#11](https://git.terrascale.tech/TerraScale/terrabase/pulls/11)
(replication) merge: connect their real counters, membership identity/readiness
and authenticated peer frontiers; verify the producer against the autoscaler
consumer before removing the gate. These merges alone do not qualify live
execution. Tenant CPU attribution, logical storage accounting and device IOPS
still require real measurement sources or an agreed consumer availability
contract. Tenant movement remains TS-513 and is not implemented here.

## Measurements

| Fields | Meaning |
| --- | --- |
| `schema`, `node`, `region`, `process_id`, `release` | Schema 1; operator node name (machine name by default); configured TS-499 region; random ID for each host/process incarnation; full assembly informational version including build revision. Node identity is descriptive, not membership evidence. |
| `sampled_at`, `sample_window_seconds` | UTC sample completion and elapsed monotonic seconds for its counters. CPU and HTTP counters are sampled every second; a delayed sampler reports its actual elapsed interval. |
| `cpu_util` | Process CPU seconds divided by interval seconds and `Environment.ProcessorCount`, capped at one. Null without a baseline or when process measurement fails. The runtime's effective processor count may round fractional container quotas. |
| `memory_used_bytes`, `memory_budget_bytes`, `mem_util` | Process resident working set; telemetry budget; used/budget capped at one. Used bytes remain uncapped and can exceed the budget. Process failures yield null used/utilization. This budget is a denominator, not a hard memory-admission guarantee. |
| `disk_used_bytes`, `disk_free_bytes`, `disk_total_bytes` | Filesystem containing the data directory, including other users of that filesystem. Used = total minus total free. Free includes reserved space; it does not promise all bytes are writable by this process. Failures yield null. |
| `request_rate`, `p95_latency_ms`, `listeners` | Completed HTTP requests, including errors and admin scrapes, summed across admin/client/peer. Each listener has its rate, p95 and cumulative `requests_total`. gRPC duration covers the whole HTTP stream, not individual frames. An empty interval has zero rate/p95. |
| `top_tenants` | Up to ten organizations sorted by completed authenticated API-key request rate, descending with UUID tie-breaks. Management service-credential traffic is not attributed to tenants. `plan` is the last plan resolved by a document endpoint during that interval, null when unavailable. `cpu_share` remains null: elapsed request time is not CPU time. |
| `untracked_tenant_requests` | Count omitted from tenant ranking when more than 4,096 distinct organizations occur in one interval. The fixed tracking table clears each interval; node/listener rates always include all requests. A nonzero value makes the ranking incomplete. |
| `data_bytes`, `top_tenants_by_storage`, `storage_sampled_at` | Separate cached file-metadata sample every ten seconds, including unopened databases. Physical lengths of tenant `.db`, `.db-wal` and `.db-shm` files, excluding `_system`; top ten by physical bytes with UUID tie-breaks. Samples are non-atomic during writes/deletes/checkpoints and can decrease. Null total/timestamp with an empty ranking before the first complete scan or after failure. No document data is read. |
| `load.connections`, `load.write_qps` | Current open client transport connections, including TLS handshakes; successful changed client-database write commits per interval. Reads, empty transactions, rollbacks, system writes and migrations are excluded from write rate. |
| `state`, `io_queue_depth`, `io_admitted_count`, `disk_iops` | Membership state is `"unknown"`; pool queue/admitted counts and device IOPS are explicit null. The existing SQLite idle-session pool is not an I/O admission queue. |
| `load.storage_bytes`, `load.replication_lag_seq` | Explicit null. Physical file bytes are not logical document size; local commit offsets are not authenticated peer applied-frontier lag. |

P95 uses 256 fixed geometric histogram bins rather than retaining requests.
The bucket upper bound is within 10% of the selected latency (minimum 0.01 ms;
extreme values saturate the final bin). The request hot path increments counters
under one short lock, without scans or asynchronous work. Tracking uses a fixed
number of histogram bins and at most 4,096 UUID keys per one-second interval.
Storage enumeration retains one organization's running sum and at most ten
ranked UUIDs. Background work has no queued sampling backlog.

## Prometheus

All instruments use the host-scoped `TerraScale.TerraBase` meter and the
`terrabase_pressure_` prefix. Known gauges match the JSON names, with
`write_qps` and `connections` flattened from `load`. Timestamp gauges are
`sampled_at_seconds` and `storage_sampled_at_seconds` (Unix seconds).
`listener_request_rate`, `listener_p95_latency_ms` and
`listener_requests_total` have only `listener` labels (`admin`, `client`, `peer`).
`top_tenant_request_rate` and `top_tenant_storage_bytes` use ranks 1–10, never
organization IDs as labels. They represent ranking values, not persistent
per-tenant series.

Signals without implementations have no numeric instrument; temporarily
unavailable measured gauges export `NaN` rather than retaining a preceding
value. The `available{signal="…"}` gauge reports availability as 0/1. Empty
tenant ranks export zero, clearing previous ranking values; unknown storage
ranks export `NaN`. Its signal labels
are a fixed list. `info` has one node/region/incarnation/release/state series per
host; these values come from operator configuration or the running build.
Sample timestamps let a consumer detect a stalled sampler. Prometheus exporter
caching can add its own scrape delay.

## Configuration

`--node-name=value` / `TERRABASE_NODE_NAME` accepts 1–128 ASCII letters, digits,
dots, underscores, colons, pluses or hyphens. Set the actual node identity; the
endpoint does not derive a cell ordinal from region or claim membership.

`--memory-budget=bytes` / `TERRABASE_MEMORY_BUDGET` accepts an integer of at least
1 MiB. Default: one quarter of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`,
at least 1 MiB, matching #13's proposed default and byte unit. Arguments override
environment variables; invalid values stop startup with exit code 2.
