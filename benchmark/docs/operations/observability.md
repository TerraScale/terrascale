# TerraBase observability

TerraBase has two metrics paths with different purposes. The local Studio path
is for one operator inspecting one engine location. The production path is the
private Prometheus contract used by Grafana and alerts.

## Local Studio metrics

The Studio exposes Prometheus text at
`http://127.0.0.1:8090/metrics/prometheus` by default. Its listener refuses
non-loopback addresses because Studio has no authentication.

```sh
curl --fail --silent http://127.0.0.1:8090/metrics/prometheus
```

The Studio endpoint reports engine build information, published storage
sequence, location bytes and files, aggregate document count, aggregate probe
failures, index counts, tier-control flags, and S3 backup scheduler outcomes
and freshness. It never emits tenant,
collection, document, query, path, or error-text labels. It does not report
production RPS, latency, topology, or capacity headroom.

## Production metrics

`ts-orchestrator` configures the private production listener and authenticated
Prometheus target. Its path is `/internal/metrics` in the checked-in deployment
template. Do not route it through the public Go gateway.

The production registry reports request counters and latency histograms,
in-flight work, admission refusals, queue saturation, WAL and commit outcomes,
storage usage and finite capacity, storage errors, replication lag and
backlog, active Members, compaction, backup freshness, usage publication,
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
| Background work | Compaction backlog and I/O, backup age and outcomes, and usage-export failures. |

Import `deploy/observability/grafana-dashboard.json` with the production
Prometheus data source. Set the dashboard's Region and operation filters as
needed. The selected Prometheus job must represent one TerraBase deployment;
combining unrelated deployments makes totals misleading.

The starting alerts live in
`deploy/observability/prometheus-rules.yml`. Review their error, latency,
saturation, durability, replication, Member, storage, compaction, backup, and
usage-export thresholds against the admitted topology and ADR 0033 before
production. A missing storage-capacity series is an instrumentation gap, not
evidence of unlimited headroom.

Studio metrics are not production evidence. Production promotion requires the
private-network checks, populated collectors, dashboard queries, alert tests,
and candidate-bound receipts in ADR 0036.
