# TerraBase ADR index

Architecture Decision Records document decisions that are expensive to
reverse. The workspace charter remains the product authority; accepted
records are current local decisions and proposed records are unresolved
choices, not release authorization.

## Accepted decisions

- [0010 — Automatic indexing and query optimizer](0010-auto-indexing-and-query-optimizer.md)
- [0021 — Native document relationships](0021-native-document-relationships.md)
- [0022 — Native causal model and CRDT algebra](0022-native-causal-model-and-crdt-algebra.md)
- [0025 — Private protobuf and gRPC protocol](0025-private-protobuf-grpc.md)
- [0027 — ULID object identities](0027-ulid-object-identities.md)
- [0028 — Durable long-running operation progress](0028-durable-long-running-operation-progress.md)
- [0030 — Service-managed encryption and key lifecycle](0030-service-managed-encryption-and-key-lifecycle.md) (customer-key restriction superseded by 0049)
- [0032 — Public contract limits and compatibility](0032-public-contract-limits-and-compatibility.md)
- [0033 — Production SLOs, OCI placement, and operability](0033-production-slos-oci-placement-and-operability.md)
- [0035 — `GlobalDurable` requires multiple Regions](0035-one-region-global-durable-semantics.md)
- [0036 — Private Prometheus and Grafana observability](0036-private-prometheus-and-grafana-observability.md)
- [0038 — Unified multi-database Member process](0038-unified-multi-database-member-process.md)
- [0039 — Multi-tenant authorization and isolation](0039-multi-tenant-authorization-and-isolation.md)
- [0043 — Edge routing contract](0043-edge-routing-contract.md)
- [0044 — The two engines: replication and sync, on a research base](0044-sync-and-replication-engines.md)
- [0049 — Customer key policy and production security boundary](0049-customer-key-policy-and-production-security.md)

## In-progress records

- [0047 — Strict auth persistence](0047-strict-auth-persistence.md) (partially implemented; production adapter composition superseded 2026-09-10; strict-auth qualification remains open)

## Authoring

Use the next available number and include the date, authority, context,
decision, and consequences. Keep proposed choices explicit when they affect
product scope, data guarantees, security authority, topology, cost, or
production direction. Implementation details belong with the owning source,
tests, or deployment configuration.
