# TerraBase ADR index

Architecture Decision Records (ADRs) document decisions that are expensive to
reverse. The workspace charter remains the product authority. `accepted`
records are current decisions; `Proposed` records capture an unresolved
material choice and must not be treated as release authorization.

## Accepted decisions

- [0007 — NoSQL, optional schemas, and migrations](0007-nosql-optional-schemas-and-migrations.md)
- [0010 — Automatic indexing and query optimizer](0010-auto-indexing-and-query-optimizer.md)
- [0021 — Native document relationships](0021-native-document-relationships.md)
- [0022 — Native causal model and CRDT algebra](0022-native-causal-model-and-crdt-algebra.md)
- [0025 — Private protobuf and gRPC protocol](0025-private-protobuf-grpc.md)
- [0026 — Decision-grade performance gates](0026-decision-grade-performance-gates.md)
- [0027 — ULID object identities](0027-ulid-object-identities.md)
- [0028 — Durable long-running operation progress](0028-durable-long-running-operation-progress.md)
- [0030 — Service-managed encryption and key lifecycle](0030-service-managed-encryption-and-key-lifecycle.md)
- [0032 — Public contract limits and compatibility](0032-public-contract-limits-and-compatibility.md)
- [0033 — Production SLOs, OCI placement, and operability](0033-production-slos-oci-placement-and-operability.md)
- [0035 — One-Region `GlobalDurable` semantics](0035-one-region-global-durable-semantics.md)
- [0036 — Private Prometheus and Grafana observability](0036-private-prometheus-and-grafana-observability.md)
- [0038 — Unified multi-database Member process](0038-unified-multi-database-member-process.md)
- [0039 — Multi-tenant authorization and isolation](0039-multi-tenant-authorization-and-isolation.md)
- [0040 — SQLite storage, parse-once byte pipeline, and auto-indexing](0040-sqlite-storage-parse-once-and-auto-indexing.md) — records the SQLite storage, parse-once bytes, backup, sharding, and auto-indexing decisions.
- [0041 — Replication on SQLite: WAL-frame follower and journal tailer](0041-replication-on-sqlite.md) — records owner decisions D-35/D-36/D-45: intra-region RF=2 as a physical WAL-frame follower, leaderless cross-region journal tailer with document-level causal metadata and remove-wins field merge, and the wave/zstd/credit-window/range-reconciliation mechanics.
- [0042 — Production estate and launch prerequisites](0042-production-estate-and-launch-prerequisites.md) — the nine-instance OCI estate, managed services, cost envelope, and the non-hardware launch checklist; supersedes the release-posture, licence, and soak paragraphs of 0033.
- [0043 — Edge routing contract](0043-edge-routing-contract.md) — `X-TerraScale-Placement`/`Residency` headers on every database-scoped response, `421 Misdirected Request` for databases a region does not host, `GET /regions`; the member side of GOAL D-58 (`ts-proxy`).
- [0044 — The two engines: replication and sync, on a research base](0044-sync-and-replication-engines.md) — causal+ delivery over the per-origin journal (COPS), Occult-style session tokens, UNISTORE-shaped `GlobalDurable`, delta-state document CRDT, range-based set reconciliation, DBSP Z-set deltas, and the derived launch live subset for SQL live queries (D-78); supersedes 0041's anti-entropy and the legacy query portions of 0022.
- [0045 — The auto-indexing engine, on a research base](0045-auto-indexing-engine.md) — observe → compress → candidates → compile check (`sqlite3_expert` technique) → bandit selection (DBA Bandits / C²UCB, UTune uncertainty weighting) → invisible budgeted build with watermark hybrid plans → shadow verification on logical cost (Azure SQL, Oracle) with plan pins → monitor, revert, retire, merge; supersedes the D-31 section of 0040.

## Authoring and release rules

Use the next available number and include status, date, authority, context,
decision, consequences, and evidence required for acceptance. Keep proposed
choices explicit when they affect product scope, data guarantees, security
authority, topology, cost, or production direction. Implementation and
qualification receipts belong under [../evidence](../evidence/), and a receipt
never changes an ADR's status by itself.

There are currently no Proposed ADRs in this repository. New material choices
must remain Proposed until their owner accepts them.
