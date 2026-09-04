# TerraBase observability

The private Prometheus path is the operator contract used by Grafana and
alerts. The Orchestrator configures the listener, targets, network policy,
retention, and alert delivery; the dashboard is the operator surface.

The listener is plaintext HTTP on the private network at `/internal/metrics`
and has no application authentication. It exports bounded request counters,
latency histograms, admission and queue signals, WAL and commit outcomes,
storage usage and finite capacity, replication health, active Members, backup
freshness, usage publication, component health, liveness, startup, and
readiness. It never exports customer values or unbounded labels.

The checked-in dashboard uses Prometheus to show request rate and errors,
latency, admission pressure, durability, storage headroom, replication and
membership, usage publication, clock refusals, and component health. Region
and operation filters use only their closed label vocabularies. Scope a job to
one TerraBase deployment; combining unrelated deployments makes totals
misleading. Missing storage capacity is an instrumentation gap, not unlimited
headroom.

Alert rules live in `deploy/observability/prometheus-rules.yml`. They cover
scrape and readiness loss, SLO burn, errors, latency, queue saturation,
durability, replication, member loss, storage, backup freshness, usage
publication, clock, and telemetry gaps. Only customer-visible SLO burn pages;
the remaining signals guide operator action.
