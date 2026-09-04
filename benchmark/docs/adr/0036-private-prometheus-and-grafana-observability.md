---
date: 2026-08-30
authority: ../../../GOAL.md
---

# Private Prometheus and Grafana observability

## Decision

TerraBase exposes Prometheus text exposition on a private plaintext HTTP
listener. It contains bounded metrics and fixed health probes only; it has no
application authentication, customer data, configuration, diagnostics, or
mutation routes. The Orchestrator owns private routing, scrape discovery,
retention, alert delivery, and dashboard deployment. A public route to the
listener is a deployment failure.

Metric names begin with `terrabase_` and use Prometheus base units. Counters,
gauges, and histograms are exported raw; Prometheus derives rates, ratios, and
percentiles. No metric has a per-database, per-tenant, per-view, document,
query, credential, path, or other unbounded label. Labels are closed
vocabularies such as Region, operation family, outcome, queue, tier, and
component.

## Bounded metric contract

The production registry includes these families:

| Operator question | Metric families |
| --- | --- |
| Is the member eligible for traffic? | `terrabase_liveness`, `terrabase_started`, `terrabase_ready` |
| Is traffic completing and fast enough? | `terrabase_requests_total`, `terrabase_request_duration_seconds` |
| Is admission saturated? | `terrabase_in_flight_operations`, `terrabase_admission_rejections_total`, queue depth and capacity |
| Is the durability path healthy? | WAL fsync and commit counters and durations |
| Is storage healthy? | storage bytes, finite capacity, and storage errors |
| Is replication keeping up? | replication lag, backlog, frontier health, and active Members |
| Is background publication healthy? | usage-export frontier and failures |
| Which bounded subsystem is unhealthy? | `terrabase_component_health` |

Per-database and per-tenant usage values are engine usage facts queried from
the control-plane database and shown in the dashboard. They do not become
Prometheus labels. Logs are structured, contain no document bodies or key
material, are sampled above 100 lines/s per host, and are retained for 14
days. The dashboard scopes one deployment job and uses Prometheus as its only
data source.

Readiness is distinct from liveness and fails closed when the member must not
accept user operations. Replication lag is reduced by maximum, frontier
health by minimum, request rates by sum, and storage sums only across one
target per member. Missing finite capacity is unknown rather than unlimited.

## Dashboards and alerts

Versioned dashboard and alert files live under `deploy/observability/`.
Panels cover traffic, errors, latency, admission, commit and WAL health,
storage, replication, membership, usage publication, clock refusals, and
component health. Alerts cover scrape loss, readiness loss, sustained API
errors, latency budgets, queue saturation, admission refusal, durability,
replication, member loss, storage, backup freshness, and usage publication.
Only customer-visible SLO burn pages the owner; cause-level signals remain
dashboard alerts.
