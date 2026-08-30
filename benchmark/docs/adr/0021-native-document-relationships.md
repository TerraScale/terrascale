---
status: accepted
---

# Native document relationships

Accepted on 2026-08-29 by direct workspace-owner instruction.

## Decision

TerraBase supports one-to-one, one-to-many, and many-to-many relationships
as native edge records. Edges have stable ULIDs under ADR 0027, CRDT field
semantics, and engine-owned forward and reverse Auto-Indexes.

The first release supports named forward and reverse one-hop queries and
bounded multi-hop TQL traversal. Every traversal has explicit depth, result,
work, and deadline limits. Reactive views track edge, endpoint,
authorization, and relationship-index dependencies.

Soft referential integrity is the default because it remains available during
network partitions. Strict source, target, edge, and delete-policy validation
is available when all records share one Partition and can commit in one
serializable transaction. Cross-Partition strict foreign keys are deferred
until a coordinated contract and product need justify their WAN and
availability cost.

Delete policies are explicit: `preserve`, `nullify`, `cascade`, or `restrict`.
Cascade and nullify use bounded idempotent mutation plans. The engine never
runs an unbounded synchronous cascade. Work that outlives the request deadline
is a durable long-running operation under ADR 0028.

## Consequences

- Relationship operations cannot cross tenant or database boundaries.
- Auto-Indexes cover both edge directions and participate in session-consistent
  visibility.
- Concurrent edge changes must pass the same convergence, recovery, snapshot,
  replication, and compaction tests as document fields.
- Authorization changes remove forbidden endpoints and derived relationship
  rows from reactive views.
- Arbitrary graph patterns, unbounded recursion, shortest path, centrality, and
  other graph analytics are not first-release features.
