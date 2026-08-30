---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# Private Prometheus and Grafana observability

## Decision

TerraBase exports Prometheus text exposition on a private listener. The
listener has no public route and is reachable only from admitted monitoring
workloads. `ts-orchestrator` owns production listener configuration, workload
identity, network policy, scrape discovery, retention, alert delivery, and
dashboard deployment.

The private Studio exposes `/metrics/prometheus` on its loopback-only listener
for local inspection. That endpoint reports a Studio snapshot, not the
production registry. A Studio scrape cannot qualify the production engine host
or topology.

Metric names begin with `terrabase_`, use Prometheus base units, and keep their
type and meaning across compatible releases. TerraBase exports raw counters,
gauges, and histograms. Prometheus derives rates, ratios, and percentiles at
query time. In particular, requests per second comes from
`rate(terrabase_requests_total[...])`; TerraBase does not publish a sampled RPS
gauge that loses counter-reset and aggregation semantics.

## Baseline metric contract

Every production candidate exports these bounded families:

| Operator question | Metric | Dashboard calculation |
| --- | --- | --- |
| Is the process alive and eligible for traffic? | `terrabase_liveness`, `terrabase_started`, `terrabase_ready` | Minimum readiness across the selected targets. |
| How much traffic is completing? | `terrabase_requests_total{region,operation,outcome}` | Per-second rate over the selected interval, summed only where totals are meaningful. |
| Is traffic slow? | `terrabase_request_duration_seconds{region,operation,outcome}` | p50, p95, and p99 from histogram bucket rates. |
| How much storage is in use? | `terrabase_storage_bytes{tier}` | Bytes by tier and total bytes across the selected targets. |
| How much storage headroom remains? | `terrabase_storage_capacity_bytes{tier}` | Usage divided by finite configured capacity. Missing capacity is unknown, not zero. |
| Is replication keeping up? | `terrabase_replication_lag_seconds{region}` | Maximum observed lag by Region. |
| Does the cluster have its admitted Members? | `terrabase_active_members{region}` | Active Member count by Region. |
| Which bounded subsystem is failing? | `terrabase_component_health{component}` | Minimum health by component, where `1` is healthy, `0.5` is degraded, and `0` is unhealthy or unknown. |

Storage capacity means usable capacity enforced for that tier, after any
reserved headroom. A collector omits `terrabase_storage_capacity_bytes` when a
tier has no finite trustworthy capacity. It never invents a maximum for object
storage.

The GA contract also requires bounded signals for admission and in-flight
work, queue saturation, WAL durability and stalls, compaction debt, index
lifecycle, repair, subscriptions, tier movement, backup freshness, restore and
deletion progress, usage publication, key lifecycle, and graceful shutdown.
Each family needs a stable name, unit, type, closed label vocabulary, dashboard
panel, alert or recorded reason for having no alert, runbook action, and
contract test before a dashboard may query it. A dashboard with a permanently
missing query is not evidence that a signal exists.

## Labels and aggregation

Labels use closed vocabularies such as result class, operation family,
`RegionCode`, Member role, tier, and lifecycle state. Metrics never label or
contain tenant, Project, database, collection, document, query text, field
path, operation identity, storage path, endpoint, credential, grant, key
material, error text, or another unbounded value. Logs and traces do not
compensate for a missing bounded metric.

Dashboard aggregation must preserve meaning. Request rates sum. Cluster-wide
readiness and component health take the minimum. Replication lag takes the
maximum. Per-Member storage usage sums only when the selected Prometheus job
contains one scrape target per Member. Active Member observations take the
maximum when every Member reports the same admitted topology. The dashboard
is scoped to one deployment job; it must not silently combine unrelated
clusters.

Histograms use preregistered buckets tied to ADR 0033 SLOs. Counter resets are
expected only across process identity changes. Readiness is separate from
liveness and fails closed when the process must not accept user operations.

## Grafana and alerts

Versioned production dashboards and alert rules live under
`deploy/observability/`. The main dashboard answers traffic, errors, latency,
storage usage and headroom, replication lag, membership, and component health
without a local Grafana edit. Region and operation filters apply to traffic
and latency panels. Prometheus is the only data source.

Alerts cover a missing scrape, readiness loss, component failure, sustained
engine error rates, latency budget breaches, replication lag, admitted Member
loss, and storage headroom. Thresholds in the repository are starting policy.
Production owners pin them to the selected topology, capacity profile, and ADR
0033 SLO before GA. A missing capacity series creates a telemetry gap and must
not be interpreted as unlimited storage.

## Required evidence

Tests parse every exposition, enforce unique metric families, reject forbidden
label names, bound every label value, and verify redaction. CI parses the
dashboard and alert files and checks their required traffic and storage views.
A production lane proves that monitoring can scrape the private route while
public ingress cannot. Dashboard and alert checks bind every query to metric
names present in the candidate. The resulting receipt includes the candidate,
configuration, dashboard version, rule version, scrape target set, and known
telemetry gaps.
