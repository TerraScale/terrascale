# Observability deployment template

This directory contains the versioned Prometheus scrape template, alert rules,
and Grafana dashboard for TerraBase production metrics.

The Orchestrator supplies private targets, network policy, and rollout
configuration for the selected member set. The checked-in target list stays
empty because repository configuration cannot name a production member. Do
not expose `/internal/metrics` through public ingress.

The plaintext listener is protected by the Orchestrator's private-network
policy. Import `grafana-dashboard.json` into the operator Grafana instance
with Prometheus as its data source. Panels use bounded readiness, traffic,
latency, admission, commit, replication, usage-publication, clock, and
egress families; they contain no per-database, per-tenant, or per-view labels.

Scope the Prometheus job to one TerraBase deployment. Request rates sum across
its members, while readiness and component health reduce by minimum and
replication lag reduces by maximum. Egress is billable OCI outbound traffic;
CDN delivery is not a TerraBase counter.

Load `prometheus-rules.yml` through the Prometheus rule path shown in
`prometheus.yml`. It contains SLO burn recordings and the paging policy;
health, durability, replication, backup, usage, clock, egress, and telemetry
signals guide operators without becoming customer-facing metric dimensions.
