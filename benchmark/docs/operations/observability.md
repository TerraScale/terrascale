# TerraBase observability

The metrics path is the private Prometheus contract used by Grafana and
alerts (D-49: the dashboard plus Grafana are the operator surface; there
is no Studio).

## Production metrics

`ts-orchestrator` configures the private production listener, scrape target,
and network policy. The listener uses plaintext HTTP without application
authentication. Its path is `/internal/metrics` in the checked-in deployment
template.

The production registry reports request counters and latency histograms,
in-flight work, admission refusals, queue saturation, WAL and commit outcomes,
storage usage and finite capacity, storage errors, replication lag and
active Members, backup freshness, usage publication,
component health, liveness, startup, and readiness. Prometheus derives these
operator views:

| View | Source |
| --- | --- |
| Requests per second | `rate(terrabase_requests_total[$__rate_interval])` |
| Error and timeout percentage | Bad-outcome rate divided by total request rate. |
| p50, p95, and p99 latency | `histogram_quantile` over request-duration bucket rates. |
| Admission pressure | In-flight operations, rejection rate by reason, and queue utilization. |
| Durability | Commit result rate plus successful commit and WAL fsync p99. |
| Storage used | `terrabase_storage_bytes` by tier and summed across selected Members. |
| Storage utilization | Storage bytes divided by `terrabase_storage_capacity_bytes`. |
| Replication health | Maximum lag and backlog, minimum frontier health, and active Members by Region. |
| Background work | Compaction backlog and I/O, backup age and outcomes, and the usage-export frontier and failures. |

Import `deploy/observability/grafana-dashboard.json` with the production
Prometheus data source. Set the dashboard's Region and operation filters as
needed. The selected Prometheus job must represent one TerraBase deployment;
combining unrelated deployments makes totals misleading.

The starting alerts live in
`deploy/observability/prometheus-rules.yml`. Review their error, latency,
saturation, durability, replication, Member, storage, backup, and
usage-export thresholds against the admitted topology and ADR 0033 before
production. A missing storage-capacity series is an instrumentation gap, not
evidence of unlimited headroom.

Production promotion requires the
private-network checks, populated collectors, dashboard queries, alert tests,
and candidate-bound receipts in ADR 0036.
