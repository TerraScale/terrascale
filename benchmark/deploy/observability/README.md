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
shows:

- readiness, requests per second, error percentage, p99 latency, storage
  usage, storage utilization, and worst replication lag at a glance;
- traffic by operation and outcome;
- p50, p95, and p99 request latency;
- storage by tier and its 24-hour change;
- replication lag, backlog, frontier health, active Members, and component
  health;
- in-flight work, queue utilization, admission rejections, commit and WAL
  latency, and durability outcomes;
- storage errors, compaction backlog and I/O, backup age and results, and the
  usage-export frontier and failures.

Scope the Prometheus job to one TerraBase deployment. Request rates and local
storage bytes can be summed across its Member targets. Readiness and component
health use the minimum; replication lag uses the maximum. Do not merge
unrelated clusters into one dashboard total.

Load `prometheus-rules.yml` through the Prometheus rule path shown in
`prometheus.yml`. Dashboard and rule edits enter production only through
versioned deployment configuration and a new qualification receipt.
