# TerraBase ADR index

Architecture Decision Records document decisions that are expensive to
reverse. The workspace charter remains the product authority; accepted
records are current local decisions and proposed records are unresolved
choices, not release authorization.

## Accepted decisions

- [0007 — NoSQL, optional schemas, and migrations](0007-nosql-optional-schemas-and-migrations.md)
- [0010 — Automatic indexing and query optimizer](0010-auto-indexing-and-query-optimizer.md)
- [0021 — Native document relationships](0021-native-document-relationships.md)
- [0022 — Native causal model and CRDT algebra](0022-native-causal-model-and-crdt-algebra.md)
- [0025 — Private protobuf and gRPC protocol](0025-private-protobuf-grpc.md)
- [0027 — ULID object identities](0027-ulid-object-identities.md)
- [0028 — Durable long-running operation progress](0028-durable-long-running-operation-progress.md)
- [0030 — Service-managed encryption and key lifecycle](0030-service-managed-encryption-and-key-lifecycle.md)
- [0032 — Public contract limits and compatibility](0032-public-contract-limits-and-compatibility.md)
- [0033 — Production SLOs, OCI placement, and operability](0033-production-slos-oci-placement-and-operability.md)
- [0035 — `GlobalDurable` requires multiple Regions](0035-one-region-global-durable-semantics.md)
- [0036 — Private Prometheus and Grafana observability](0036-private-prometheus-and-grafana-observability.md)
- [0038 — Unified multi-database Member process](0038-unified-multi-database-member-process.md)
- [0039 — Multi-tenant authorization and isolation](0039-multi-tenant-authorization-and-isolation.md)
- [0040 — SQLite storage, parse-once byte pipeline, and auto-indexing](0040-sqlite-storage-parse-once-and-auto-indexing.md)
- [0041 — Replication on SQLite: WAL-frame follower and journal tailer](0041-replication-on-sqlite.md)
- [0042 — Production estate and launch prerequisites](0042-production-estate-and-launch-prerequisites.md)
- [0043 — Edge routing contract](0043-edge-routing-contract.md)
- [0044 — The two engines: replication and sync, on a research base](0044-sync-and-replication-engines.md)
- [0045 — The auto-indexing engine, on a research base](0045-auto-indexing-engine.md)

## Authoring

Use the next available number and include the date, authority, context,
decision, and consequences. Keep proposed choices explicit when they affect
product scope, data guarantees, security authority, topology, cost, or
production direction. Implementation details belong with the owning source,
tests, or deployment configuration.
