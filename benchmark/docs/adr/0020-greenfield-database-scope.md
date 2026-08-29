---
status: accepted
---

# Greenfield database scope

Accepted on 2026-08-29 by direct workspace-owner instruction.

## Decision

TerraBaseV2 is a greenfield Rust database engine. It is not a RavenDB rewrite
and has no TerraBase V1 compatibility or parity obligation.

Delete these from active product scope and qualification:

- the RavenDB-derived TerraBase V1 repository and runtime;
- the 258-row V1 parity inventory, overlays, Proof IDs, validators, and source
  citations;
- V1 logical migration, import, Smuggler, backup, and recovery readers;
- RQL compatibility and V1 index-definition translation;
- V1 HTTP, TCP, client, administration, storage, and embedded behavior; and
- features justified only because RavenDB implemented them.

There are no customer databases to migrate. V2 begins with empty native
databases and native causal history. It needs no snapshot import, change tail,
dual write, compatibility runtime, or V1 rollback path.

TerraBaseV2 owns database behavior only. It does not host queues, jobs,
workflows, ETL, functions, agents, or a generic plugin runtime.

## Native product

The engine is built around:

- document storage with schema-free, validated, and strict modes;
- native delta CRDT fields and deterministic conflict semantics;
- leaderless active-active replication across selected Regions;
- partition-scoped causality, HLC, anti-entropy, and Merkle verification;
- TQL and engine-owned automatic indexing;
- explicit local and global durability levels;
- tenant-aware sharding, isolation, and resource governance;
- reactive query subscriptions and causal reconnect;
- snapshots, PITR, compaction, optional immutable backup, and recovery; and
- explicit coordination for invariants that cannot merge safely.

A new feature needs a TerraScale database use case, a correctness contract, a
bounded resource model, and native qualification evidence. V1 behavior is not
evidence that the feature belongs in V2.

## Consequences

- ADR 0001 remains accepted only for the independent Rust repository and new
  storage format. Its V1 logical-migration decision is superseded.
- ADR 0003 remains accepted only for evidence-gated performance claims. Its V1
  parity matrix and V1 benchmark requirement are superseded.
- ADR 0007 remains accepted for native document schemas and schema migrations.
  Its V1 parity and logical-migration language is superseded.
- ADR 0009 remains accepted for TQL. RQL is neither a runtime nor a migration
  compatibility requirement.
- ADR 0010 remains accepted for automatic indexing and optimizer ownership.
  V1 extraction records are historical research, not normative requirements.
- Historical parity, V1 mapping, migration, benchmark, and delivery records may
  remain only until their deletion change lands. They must carry a supersession
  notice and cannot route new work.
- PostgreSQL comparisons remain useful under TerraBase.Benchmark. They are
  performance evidence, not compatibility scope.

## Acceptance

The greenfield boundary is complete when no active build, CI gate, generated
artifact, current instruction, product documentation, or qualification claim
depends on RavenDB or TerraBase V1 code, data, parity, migration, query, backup,
client, or Proof IDs.
