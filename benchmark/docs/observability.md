# TerraBase observability

The C# observability slice provides bounded Prometheus exposition and lifecycle
health. [ADR 0036](adr/0036-private-prometheus-and-grafana-observability.md)
defines the production policy; [deployment templates](../deploy/observability/)
hold scrape configuration, alert rules, and the Grafana dashboard.

## Current implementation

[`MetricsRegistry`](../src/TerraBase/Observability/Metrics/MetricsRegistry.cs)
produces immutable snapshots with these metric families:

| Family | Labels | Source |
| --- | --- | --- |
| `terrabase_liveness`, `terrabase_started`, `terrabase_ready` | none | Service state and component health. |
| `terrabase_requests_total` | `region`, `operation`, `outcome` | Recorded request completions. |
| `terrabase_request_duration_seconds` | `region`, `operation`, `outcome` | Fixed cumulative latency histogram. |
| `terrabase_component_health` | `component` | Explicit component observations. |

The local `DocumentStore` records reads, writes, and deletes with
`region="unknown"`. This does not measure requests outside the local registry. The registry's request scope tracks in-flight work internally, but its
snapshot does not export an in-flight family. Queue, WAL, commit, replication,
backup, usage, clock, and egress families referenced by deployment templates are
not implemented by this registry.

Labels use closed enums: Regions are `GRU`, `SFO`, `FRA`, and `unknown`;
operations are `read`, `write`, `query`, `delete`, `subscribe`, `backup`,
`restore`, `repair`, and `other`; outcomes are `success`, `error`, `rejected`,
`timeout`, `conflict`, and `not_found`. No label contains a database, tenant,
view identifier, or customer value.

The renderer emits Prometheus 0.0.4 text with cumulative buckets, deterministic
series order, no scrape timestamps, and a 512 KiB body limit. The content type
is `text/plain; version=0.0.4; charset=utf-8`.

`PrometheusMetrics.Path` defines `/internal/metrics`, but the current ASP.NET
host does not map that listener or `/live`, `/startup`, and `/ready` probes.
Its role-gated `/v1/metrics` endpoint returns JSON host diagnostics instead.
Private Prometheus listener integration remains required; the Orchestrator owns
private targets, network policy, retention, and alert delivery.

## Production policy and qualification

The target is 99.9% monthly API availability per Region. Only the configured
fast and slow SLO burn alerts page; other signals guide operators. The current
local request counters do not establish the public API SLI. Public request
instrumentation, external probes, private scraping, and Grafana/OnCall delivery
must be qualified through the real host and deployment.

Missing series and placeholder health values are instrumentation gaps, never
proof of healthy replication, spare capacity, or recoverable backups. The local
engine seeds replication and membership health when opened; these values are
not observations of a multi-Region runtime.

## Operator response

1. Confirm private scrape reachability before treating an empty dashboard as
   healthy. Keep metrics off public ingress.
2. Inspect liveness, startup, readiness, and component health separately. A
   live but unready process does not by itself require termination.
3. Treat failed or indeterminate commits as durability incidents. Preserve WAL,
   history, snapshots, and backups while following recovery.
4. `GlobalDurable` requires at least two active selected Regions and durability
   in every selected Region, including causal dependencies. Refuse an
   acknowledgement when its requirements are unmet.
5. Use the Orchestrator for placement, drain, and capacity actions. Never reset
   frontiers or delete storage artifacts to hide an unhealthy signal.

Current tests exercise exposition, histogram accounting, body bounds, lifecycle
transitions, and local storage metrics. They do not qualify production SLOs or
multi-Region durability.
