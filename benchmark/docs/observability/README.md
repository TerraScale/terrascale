# TerraBase observability contract

This directory documents the in-process contract implemented by the C#
`src/TerraBase/Observability` feature slice. It renders Prometheus 0.0.4 text
and keeps liveness, startup, readiness, and component health as separate
facts. The `TerraBase` host owns the private listener and routes;
`ts-orchestrator` owns target discovery, the Grafana agent, retention, and
alert delivery. Where the table below names an earlier emitter, the current C#
source is authoritative.

## Metric families and current wiring

Every family below has bounded labels. “Defined only” means the registry can
render the family, but the current production composition has not connected a
collector to it; an absent or zero series is not evidence that the underlying
condition is healthy.

| Family | Labels | Current emitter/status |
| --- | --- | --- |
| `terrabase_liveness`, `terrabase_started`, `terrabase_ready` | none | `TerraBase` host lifecycle and private probe routes. |
| `terrabase_requests_total` | `region`, `operation`, `outcome` | TBEP `run_host` records private host calls. Public HTTP request middleware does not currently record this family. |
| `terrabase_request_duration_seconds` | `region`, `operation`, `outcome` | Same TBEP `run_host` path as the request counter; public HTTP latency is not represented. |
| `terrabase_in_flight_operations` | `operation` | Defined only; no production `set_in_flight` caller is currently wired. |
| `terrabase_admission_rejections_total` | `operation`, `reason` | Public API read/query, webhook, and reactive-view guards. |
| `terrabase_queue_depth`, `terrabase_queue_capacity` | `queue` | Defined only; no production queue collector currently calls `set_queue`. |
| `terrabase_wal_fsync_total`, `terrabase_wal_fsync_duration_seconds` | `outcome` | Defined only; the current SQLite commit path does not call the registry WAL-fsync methods. |
| `terrabase_commits_total`, `terrabase_commit_duration_seconds` | `outcome` | The C# engine boundary records the durability result and duration. |
| `terrabase_replication_lag_seconds`, `terrabase_replication_backlog_bytes` | `region` | Defined only; no current lag/backlog collector is wired. |
| `terrabase_replication_frontier_health` | `region` | Server runtime seeds configured lanes as `unknown` until a replication composition supplies an observation. |
| `terrabase_active_members` | `region` | Defined only; topology/member-count publication is not wired in this repository. |
| `terrabase_component_health` | `component` | Runtime sets metrics/storage health; unset slots render `-1` (`unknown`). |
| `terrabase_backup_last_success_age_seconds`, `terrabase_backup_runs_total` | `mode` and `mode`, `outcome` | Defined only; current backup code does not publish to this registry. |
| `terrabase_usage_export_frontier`, `terrabase_usage_export_failures_total` | none | The C# engine boundary records successful frontiers and failures. |
| `terrabase_clock_offset_seconds`, `terrabase_clock_write_refusals_total` | none | Host-fed `Engine` clock observations and the origin-write admission refusal. |
| `terrabase_egress_bytes_total` | `region`, `kind` | Public API `account_direct_read` records direct response bytes as `kind="read"`; replication and snapshot call sites are not wired. |

The operation values are `read`, `write`, `query`, `subscribe`, `backup`,
`restore`, `repair`, and `other`. Outcomes are `success`, `error`,
`rejected`, and `timeout`. Regions are the exact `GRU`, `SFO`, and `FRA`
codes plus `unknown`; input is not normalized. Queue values are `foreground`,
`commit`, `replication`, `backup`, and `usage_export`. Component values are
`storage`, `replication`, `membership`, `control_plane`, `backup`, and
`metrics`. Egress kinds are `replication`, `snapshot`, and `read`.

No metric carries a per-database, per-tenant, or per-view label (D-72).
Cardinality is bounded by the Prometheus `instance` target and closed enums:
at most 128 request counter/histogram series per host (`4 × 8 × 4`), 12
egress series (`4 × 3`), and the fixed unlabeled/lower-dimensional families.
Per-database and per-tenant values are usage facts queried from the
control-plane database, never Prometheus labels.

The renderer emits `# HELP` and `# TYPE` once per family, sorts map-backed
series deterministically, emits no scrape timestamp, and uses cumulative
histogram buckets. The adapter must preserve the body and content type
`text/plain; version=0.0.4; charset=utf-8`.

## SLO and alert policy

D-75 publishes a 99.9% monthly API availability SLO per Region. The checked-in
Prometheus rules use `error|timeout` as bad outcomes and exclude `rejected`
requests, matching the D-75 invalid-credential, customer-limit, and requested-
suspension exclusions. The recording rules calculate 5m, 30m, 1h, and 6h
error ratios and divide by the 0.001 error budget to produce burn rates.

Only two rules page: the fast tier requires both 1h and 5m burn rates to be at
least 14.4x, and the slow tier requires both 6h and 30m rates to be at least
6x. All other signals are dashboard-only, including readiness, health,
durability, replication, backup, usage export, clock, egress, scrape gaps,
and below-threshold SLO burn. OnCall must receive only these SLO burn-rate
events.

There is an important qualification gap: `terrabase_requests_total` is
currently emitted by private TBEP host calls, not by public HTTP request
middleware, and no independent external probe is configured in this
repository. The recording rules are therefore an internal availability proxy,
not proof of the published public API SLO. `ts-orchestrator` must wire the
external probe, status page, Grafana Cloud, and OnCall route before this is a
production SLO measurement.

## Useful PromQL

Prometheus calculates operator views from raw cumulative facts. A sampled
`terrabase_requests_per_second` gauge is intentionally absent.

| View | Query |
| --- | --- |
| Total requests per second | `sum(rate(terrabase_requests_total[5m]))` |
| Error and timeout percentage | `100 * sum(rate(terrabase_requests_total{outcome=~"error\|timeout"}[5m])) / clamp_min(sum(rate(terrabase_requests_total{outcome!="rejected"}[5m])), 0.000000001)` |
| Successful request p99 | `histogram_quantile(0.99, sum by (le) (rate(terrabase_request_duration_seconds_bucket{outcome="success"}[5m])))` |
| API burn rate (5m) | `terrabase:slo:api_burn_rate:5m` |
| API burn rate (1h) | `terrabase:slo:api_burn_rate:1h` |
| Admission rejections per second | `sum by (operation, reason) (rate(terrabase_admission_rejections_total[5m]))` |
| Committed durability p99 | `histogram_quantile(0.99, sum by (le) (rate(terrabase_commit_duration_seconds_bucket{outcome="committed"}[5m])))` |
| Frontier health by Region | `min by (region) (terrabase_replication_frontier_health)` |
| Clock offset magnitude | `abs(terrabase_clock_offset_seconds)` — writes stop past 0.5 s (D-69) |
| Clock write refusals per second | `sum(rate(terrabase_clock_write_refusals_total[5m]))` |
| Egress bytes per second by Region and class | `sum by (region, kind) (rate(terrabase_egress_bytes_total[5m]))` |
| Projected monthly egress budget | `100 * sum by (region) (increase(terrabase_egress_bytes_total[1h])) * 720 / 8000000000000` |

The egress budget is 8 TB (decimal bytes) per Region per month. Its 720-hour
pace threshold is `11,111,111,111` bytes in one hour, rounded down. CDN fills
and reader-side delivery are Cloudflare traffic and stay out of this counter.

## Private boundary and probes

The production adapter exposes the rendered body only on the private operator
path managed by `ts-orchestrator`. It enforces a bounded response size, short
scrape timeout, and connection limits. It does not proxy customer request
paths or accept arbitrary labels. The listener uses plaintext HTTP without
application authentication; private-network policy is the trust boundary.

The `TerraBase` host serves `/internal/metrics`, `/live`, `/startup`, and
`/ready` from one immutable `MetricsSnapshot`. Startup becomes true after
durable engine open and listener binding; draining clears readiness before
shutdown; terminal shutdown clears liveness.

## Structured logs

`LogRecord` and `redact_diagnostic` define a bounded, secret-safe log shape,
but no current TerraBase production call site emits `LogRecord` records to a
sink. The Grafana-agent sampling limit (above 100 lines/s per host) and
14-day retention are deployment responsibilities of `ts-orchestrator`, not
claims that the current member binary already fulfills. A production log
pipeline remains a qualification gap.

## Qualification status

Source tests cover deterministic exposition, histogram accounting, label and
response-size bounds, lifecycle/readiness separation, control-plane loss,
optional components, monotonic usage frontiers, path agreement, and redaction.
They do not provide an external public-API probe, public HTTP request SLI, or
production Grafana/OnCall connectivity. Those external deployment checks must
pass before the SLO or status page is called qualified.
