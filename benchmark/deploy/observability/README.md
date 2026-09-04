# Observability deployment template

This directory contains the versioned Prometheus scrape template, alert rules,
and Grafana dashboard for TerraBase production metrics.

`ts-orchestrator` supplies private targets, network policy, and rollout
configuration for the exact candidate. The checked-in target list stays empty
because repository configuration cannot name a production Member. Do not
expose `/internal/metrics` through the public Go gateway.

The scrape template uses plaintext HTTP with no application authentication,
as required by ADR 0036. The orchestrator's private-network policy is the
trust boundary. Never add a public target or route this listener through public
ingress during incident response.

Import `grafana-dashboard.json` into the operator Grafana instance and select
its Prometheus data source. The dashboard has Region and operation filters and
shows only metric families in the current bounded registry:

- readiness, requests per second, error percentage, and request p99;
- traffic by operation and outcome, plus p50/p95/p99 request latency;
- replication frontier health and component health;
- admission rejections, commit latency/outcomes, and usage-export
  frontier/failures;
- host clock offset/refusals and direct-member egress;
- dashboard-only API burn-rate windows and projected monthly egress.

No retired storage-stack panels or queries are included; the current member
does not produce those legacy families.

Scope the Prometheus job to one TerraBase deployment. Request rates and
replication observations can be summed or reduced across its Member targets as
described by each panel. Do not merge unrelated clusters into one dashboard
total. Egress is billable OCI outbound only; CDN delivery is not in the
TerraBase counter.

Load `prometheus-rules.yml` through the Prometheus rule path shown in
`prometheus.yml`. It contains recording rules for the four SLO burn windows
and exactly two paging alerts (fast 1h/5m and slow 6h/30m). All lifecycle,
health, durability, replication, backup, usage-export, clock, egress, and
telemetry-gap signals are dashboard-only; OnCall must receive only the SLO
burn-rate events.

The SLO recordings currently use TBEP private host-call outcomes as a proxy.
The public HTTP API request middleware and the independent external probe are
not wired in this repository, so these recordings do not yet prove the D-75
public API SLO. The external probe and Grafana/OnCall routing are deployment
requirements owned by `ts-orchestrator`.
