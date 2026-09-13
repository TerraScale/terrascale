# TerraBase observability

The private Prometheus boundary is the operator contract managed by the
Orchestrator. TerraBase renders Prometheus 0.0.4 text and keeps liveness,
startup, readiness, and component health as separate facts. The dashboard is
the operator surface; member logs provide correlation. Never expose the
metrics listener publicly.

## Metric families and wiring

Every family has bounded labels. “Defined only” means the registry can render
the family, but the current composition has not connected a collector to it.
An absent or zero series is not evidence that the underlying condition is
healthy.

| Family | Labels | Current emitter/status |
| --- | --- | --- |
| `terrabase_liveness`, `terrabase_started`, `terrabase_ready` | none | TerraBase lifecycle and private probe routes. |
| `terrabase_requests_total` | `region`, `operation`, `outcome` | TBEP host calls record this family; public HTTP middleware does not currently record it. |
| `terrabase_request_duration_seconds` | `region`, `operation`, `outcome` | Same TBEP path as the request counter. |
| `terrabase_in_flight_operations` | `operation` | Defined only. |
| `terrabase_admission_rejections_total` | `operation`, `reason` | Public API read/query, webhook, and reactive-view guards. |
| `terrabase_queue_depth`, `terrabase_queue_capacity` | `queue` | Defined only. |
| `terrabase_wal_fsync_total`, `terrabase_wal_fsync_duration_seconds` | `outcome` | Defined only; the current SQLite commit path does not call these methods. |
| `terrabase_commits_total`, `terrabase_commit_duration_seconds` | `outcome` | Engine boundary records durability result and duration. |
| `terrabase_replication_lag_seconds`, `terrabase_replication_backlog_bytes` | `region` | Defined only. |
| `terrabase_replication_frontier_health` | `region` | Configured lanes start as `unknown` until replication supplies an observation. |
| `terrabase_active_members` | `region` | Defined only. |
| `terrabase_component_health` | `component` | Runtime reports storage and metrics health; unset slots are `unknown`. |
| `terrabase_backup_last_success_age_seconds`, `terrabase_backup_runs_total` | `mode`, `outcome` | Defined only. |
| `terrabase_usage_export_frontier`, `terrabase_usage_export_failures_total` | none | Engine boundary records frontiers and failures. |
| `terrabase_clock_offset_seconds`, `terrabase_clock_write_refusals_total` | none | Host-fed clock observations and origin-write refusal. |
| `terrabase_egress_bytes_total` | `region`, `kind` | Public direct reads record response bytes; replication and snapshot callers are not wired. |

Operations are `read`, `write`, `query`, `subscribe`, `backup`, `restore`,
`repair`, and `other`. Outcomes are `success`, `error`, `rejected`, and
`timeout`. Labels never contain database, tenant, or view identifiers.

The host serves `/internal/metrics`, `/live`, `/startup`, and `/ready` from one
immutable snapshot. Startup becomes true after durable engine open and listener
binding; draining clears readiness before shutdown; terminal shutdown clears
liveness.

## SLO and alert policy

The API target is a 99.9% monthly availability SLO per Region. `error` and
`timeout` are bad outcomes; rejected requests are excluded. Only fast burn
(1h and 5m at least 14.4x) and slow burn (6h and 30m at least 6x) page. All
other signals guide operators without paging.

The current request series is emitted from private TBEP calls, not public HTTP
middleware, so it is an internal availability proxy until the Orchestrator
wires external probes and the public request SLI.

Useful PromQL:

| View | Query |
| --- | --- |
| Requests per second | `sum(rate(terrabase_requests_total[5m]))` |
| Error percentage | `100 * sum(rate(terrabase_requests_total{outcome=~"error\|timeout"}[5m])) / clamp_min(sum(rate(terrabase_requests_total{outcome!="rejected"}[5m])), 0.000000001)` |
| Successful p99 | `histogram_quantile(0.99, sum by (le) (rate(terrabase_request_duration_seconds_bucket{outcome="success"}[5m])))` |
| Frontier health | `min by (region) (terrabase_replication_frontier_health)` |
| Clock offset | `abs(terrabase_clock_offset_seconds)` |

## Operator response

1. Confirm private scrape reachability and content type before treating an
   empty dashboard as healthy.
2. Check liveness, startup, readiness, and component health separately.
3. Compare request outcomes and p50/p95/p99 latency, then inspect storage,
   replication frontier, and member capacity.
4. Treat WAL fsync or commit errors as durability incidents; retain WAL,
   history, snapshots, and backups during recovery.
5. `GlobalDurable` requires two active selected Regions. If that condition is
   lost, refuse new acknowledgements of that class.
6. Repair private routing, readiness, or placement through the Orchestrator;
   never make the metrics endpoint public or delete storage artifacts by hand.

Source tests cover deterministic exposition, histogram accounting, bounded
labels, response-size limits, lifecycle separation, control-plane loss,
optional components, usage frontiers, path agreement, and redaction. They do
not qualify external probes, Grafana/OnCall connectivity, or a production
multi-Region deployment.
