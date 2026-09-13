# TerraBase documentation

The source and tests are authoritative. These documents hold the small amount
of durable context that is not self-evident from the code.

- [Architecture decisions](adr/README.md): accepted and proposed ADRs.
- [Contracts](contracts/): externally observable lifecycle, placement, schema,
  key-delivery, webhook, and bundle boundaries.
- [Observability](observability.md): metrics, SLO policy, PromQL, and operator
  response.
- [Reactive transport](reactive-transport.md): snapshots, logs, retention, and
  view lifecycle.
- [Development contract](development.md): build, format, test, and generated
  contract workflow.
- [Test resources](development/test-resources.md): vendored protocol and sync
  vectors.
- [Competitive evidence](competitive/evidence/): dated external comparisons.

Retired implementation notes, historical qualification logs, and unpublished
architecture drafts are removed rather than kept beside current guidance.
