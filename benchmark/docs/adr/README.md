# TerraBase ADR index

Architecture Decision Records (ADRs) document decisions that are expensive to
reverse. The workspace charter remains the product authority. `accepted`
records are current decisions; `Proposed` records capture an unresolved
material choice and must not be treated as release authorization.

## Accepted decisions

- [0001 — Independent Rust repository and storage format](0001-independent-rust-repository-and-storage-format.md)
- [0006 — Private Operations Studio on Datastar](0006-operations-studio-on-datastar.md)
- [0007 — NoSQL, optional schemas, and migrations](0007-nosql-optional-schemas-and-migrations.md)
- [0009 — TQL query language](0009-tql-query-language.md)
- [0010 — Automatic indexing and query optimizer](0010-auto-indexing-and-query-optimizer.md)
- [0016 — Native storage tiers](0016-zonetree-inspired-storage-tiers.md)
- [0017 — Tenant tiering and backup clusters](0017-tenant-tiering-and-backup-clusters.md)
- [0021 — Native document relationships](0021-native-document-relationships.md)
- [0022 — Native causal model and CRDT algebra](0022-native-causal-model-and-crdt-algebra.md)
- [0023 — Native replication, repair, and compaction](0023-native-replication-repair-and-compaction.md)
- [0024 — Reactive query sync state machine](0024-reactive-query-sync-state-machine.md)
- [0025 — Private protobuf and gRPC protocol](0025-private-protobuf-grpc.md)
- [0026 — Decision-grade performance gates](0026-decision-grade-performance-gates.md)
- [0027 — ULID object identities](0027-ulid-object-identities.md)
- [0028 — Durable long-running operation progress](0028-durable-long-running-operation-progress.md)
- [0029 — Coordinated consensus and transaction recovery](0029-coordinated-consensus-and-transaction-recovery.md)
- [0030 — Service-managed encryption and key lifecycle](0030-service-managed-encryption-and-key-lifecycle.md)
- [0031 — Rolling upgrades and format activation](0031-rolling-upgrades-and-format-activation.md)
- [0032 — Public contract limits and compatibility](0032-public-contract-limits-and-compatibility.md)
- [0033 — Production SLOs, OCI placement, and operability](0033-production-slos-oci-placement-and-operability.md)
- [0034 — Sole production Go gateway](0034-sole-production-go-gateway.md)
- [0035 — One-Region `GlobalDurable` semantics](0035-one-region-global-durable-semantics.md)
- [0036 — Private Prometheus and Grafana observability](0036-private-prometheus-and-grafana-observability.md)
- [0037 — S3 recovery-point repository](0037-s3-recovery-point-repository.md)
- [0038 — Unified multi-database Member process](0038-unified-multi-database-member-process.md)
- [0039 — Multi-tenant authorization and isolation](0039-multi-tenant-authorization-and-isolation.md)
- [0040 — SQLite storage, parse-once byte pipeline, and auto-indexing](0040-sqlite-storage-parse-once-and-auto-indexing.md) — supersedes the storage-engine portions of 0010, 0016, and 0023; their optimizer, tiering-intent, and replication/repair semantics survive where the workspace goal keeps them.

## Authoring and release rules

Use the next available number and include status, date, authority, context,
decision, consequences, and evidence required for acceptance. Keep proposed
choices explicit when they affect product scope, data guarantees, security
authority, topology, cost, or production direction. Implementation and
qualification receipts belong under [../evidence](../evidence/), and a receipt
never changes an ADR's status by itself.

There are currently no Proposed ADRs in this repository. New material choices
must remain Proposed until their owner accepts them.
