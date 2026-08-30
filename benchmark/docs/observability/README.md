# TerraBase observability contract

This directory documents the in-process observability contract implemented in
`src/observability/mod.rs`. The module renders Prometheus 0.0.4 text and keeps
health, startup, and readiness as separate facts. `ts-orchestrator` owns the
private production listener, workload authentication, target discovery, and
network policy. TerraBase supplies the bounded registry and exposition body.

## Metric families

| Family | Type | Labels | Meaning |
| --- | --- | --- | --- |
| `terrabase_liveness` | gauge | none | `1` while this process lifecycle is live. Dependency failure does not make liveness false. |
| `terrabase_started` | gauge | none | `1` after the service first reaches the serving state. It stays `1` while draining. |
| `terrabase_ready` | gauge | none | `1` only while the service may accept traffic and every required health component is ready. |
| `terrabase_requests_total` | counter | `region`, `operation`, `outcome` | Completed requests. Prometheus derives requests per second from this counter. |
| `terrabase_request_duration_seconds` | histogram | `region`, `operation`, `outcome` | Request latency with fixed 5 ms through 10 s buckets. |
| `terrabase_replication_lag_seconds` | gauge | `region` | Current observed replication lag. |
| `terrabase_storage_bytes` | gauge | `tier` | Measured bytes in `tier1`, `tier2`, or `tier3`. |
| `terrabase_storage_capacity_bytes` | gauge | `tier` | Finite usable capacity by tier. The series is absent when capacity is unknown or unbounded. |
| `terrabase_active_members` | gauge | `region` | Active admitted Members by Region. |
| `terrabase_component_health` | gauge | `component` | `1` healthy, `0.5` degraded, `0` unhealthy or unknown. |

The only named Region label values are `GRU`, `SFO`, and `FRA`; `unknown` is an
aggregate for incomplete or invalid topology observations. Input is not
normalized, so lowercase values, provider names, aliases, and malformed codes
cannot become labels. Operations, outcomes, tiers, and components are closed
enums in the Rust API. The values are `read`, `write`, `query`, `subscribe`,
`backup`, `restore`, `repair`, `compaction`, `other` for operations;
`success`, `error`, `rejected`, `timeout` for outcomes; `tier1`, `tier2`,
`tier3` for storage; and `storage`, `replication`, `membership`,
`control_plane`, `backup`, `metrics` for components.

The renderer emits `# HELP` and `# TYPE` once per family, sorts map-backed
series deterministically, emits no scrape timestamp, and uses cumulative
histogram buckets. A scrape adapter must preserve the body and content type
`text/plain; version=0.0.4; charset=utf-8`.

## Useful PromQL

Prometheus calculates operator views from raw cumulative facts. A sampled
`terrabase_requests_per_second` gauge is intentionally absent.

| View | Query |
| --- | --- |
| Total requests per second | `sum(rate(terrabase_requests_total[5m]))` |
| Requests per second by operation | `sum by (operation) (rate(terrabase_requests_total[5m]))` |
| Error and timeout percentage | `100 * sum(rate(terrabase_requests_total{outcome=~"error\|timeout"}[5m])) / clamp_min(sum(rate(terrabase_requests_total[5m])), 0.001)` |
| Successful request p99 | `histogram_quantile(0.99, sum by (le) (rate(terrabase_request_duration_seconds_bucket{outcome="success"}[5m])))` |
| Total storage usage | `sum(terrabase_storage_bytes)` |
| Storage utilization by tier | `100 * terrabase_storage_bytes / clamp_min(terrabase_storage_capacity_bytes, 1)` |
| Storage change over 24 hours | `sum(terrabase_storage_bytes) - sum(terrabase_storage_bytes offset 24h)` |
| Worst replication lag by Region | `max by (region) (terrabase_replication_lag_seconds)` |

The checked-in Grafana dashboard uses `$__rate_interval` instead of a fixed
five-minute rate window and provides `region` and `operation` filters. Scope its
Prometheus job to one TerraBase deployment. Summing storage across unrelated
deployments produces a valid number with the wrong meaning.

## Boundary requirements

The production adapter exposes the rendered body only on the private,
authenticated operator path managed by `ts-orchestrator`. It enforces a
bounded response size, short scrape timeout, and connection limits. It does
not proxy customer request paths or accept arbitrary labels. TLS identity,
network allow-listing, and authorization remain at the production boundary.

Health and readiness adapters map `HealthRegistry::liveness()` to a liveness
probe and `HealthReport::readiness_status_code()` to a readiness probe. A
dependency outage returns readiness failure while the process remains live.
Probe bodies may include component names and bounded status values, but must
pass free-form diagnostic text through `redact_diagnostic` first.

## Redaction

Metrics never carry free-form details. The redaction helper is for an
operator-facing adapter that needs a safe diagnostic reason. It masks bearer
credentials and common `password`, `secret`, `token`, `api_key`, access-key,
private-key, and credential key/value forms. Customer IDs, request IDs, raw
errors, URLs with query secrets, provider identifiers, and credentials must
not appear in labels or unredacted probe bodies. Redaction is a second line of
defense, not permission to collect sensitive values.

## Qualification status

Source unit tests and `tests/observability/contract.rs` cover deterministic
exposition, histogram accounting, label bounds, storage capacity semantics,
health and readiness separation, optional dependency degradation, and secret
redaction. They are `implemented` evidence only. An integrated private
authenticated scrape, populated collectors, dashboard queries, alerts, and
probe boundary still need qualification on the production topology.
