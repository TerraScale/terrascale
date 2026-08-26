---
status: accepted
---

# ADR 0011: Sharding as owned ranges over the bucket-range table

Proposed by SH-01 (`TerraScale/TerraBaseV2#47`), 2026-08-26, under
charter PD-2026-08-26-10 D3; accepted by the parent the same day on
review of the companion records. Companion records:
`docs/sharding/model.md`, `docs/sharding/dispositions.md`,
`docs/sharding/topology-limits.md`, `docs/sharding/glossary-additions.md`
(promoted into `CONTEXT.md` at acceptance).

Owner: the parent fleet orchestrator, on the accepted ADR-0004
replication model, the pinned V1 sharding source identity
(`fa99b9257776dbe625e6a2388c74c9ce916637ee`), and the SH-01 brief.

## Context

V1 ships sharding as a separate layer: each shard is a whole database
with its own replication factor and Rachis-managed topology; a dedicated
orchestrator node set coordinates database records, bucket ranges, and
migrations through cluster consensus. V2 already accepted a different
foundation: ADR-0004's single-leader-per-data-range model with
cross-Region quorum acknowledgement, epoch fencing, and a
caller-supplied `TopologyView` (`src/replication/topology.rs`) whose
provisioning, placement, and routing stay with the Platform/Orchestrator
control plane. Charter PD-2026-08-26-10 D3 commits V2 to full sharding
parity and requires SH-01 to record the model and the sharded production
topology limits.

## Decision

1. **A shard is an owned data range in the `TopologyView`**, with a
   stable canonical identity independent of its bucket boundaries, one
   owning Member (leader), and one ownership epoch. Sharding composes
   with multi-region replication per ADR-0004 unchanged: the
   acknowledgement lattice applies per routed write; follower placement
   is Platform policy over the same view; a shard-local acknowledgement
   is never database success.
2. **A bucket is the permanent routing identity** in a fixed 2^20 space,
   derived from the document's shard key (identity-keyed by default).
   The bucket-range table — an exact, ordered, non-overlapping partition
   of [0, 2^20) into ranges mapped to shards, adjacent-same-shard
   entries merged — is Platform-constructed and engine-validated; the
   Platform derives one `RangeOwnership` per shard from it. The 2^20
   space is an invariant, never a configuration knob.
3. **Resharding is bucket migration over the replication plane plus an
   ownership transition.** Migration streams a bucket's committed
   records source-to-destination through the existing idempotent,
   restartable catch-up machinery, then transfers the bucket-range entry
   with raised epochs. No separate migration transport and no
   engine-internal orchestrator: the Platform drives typed engine
   operations.
4. **Cluster coordination is engine-owned through the existing
   replication plane and `TopologyView` epochs, not a new consensus
   service** (recommendation recorded in
   `docs/sharding/dispositions.md`): V1 Rachis rows PB-5016..PB-5020 are
   satisfied by the accepted single-leader/quorum/epoch mechanism, with
   openraft retained only as a named SH-05 fault-lane fallback.
5. **Sharded production topology limits** are as proposed in
   `docs/sharding/topology-limits.md` (2^20 buckets permanent; at most
   128 shards; 8,192-bucket initial allocation floor), resolving the
   charter's unresolved production-topology-limits item for sharded
   topologies on acceptance of this ADR.
6. An unsharded database is the degenerate one-shard case; the model has
   a single shape so the multi-region qualification contract stays
   uniform.

## Rejected alternatives

**V1-style separate sharding layer** (shards as independent databases,
engine-embedded orchestrator, engine-embedded cluster consensus for
topology state). Rejected because it re-creates a control plane inside
the engine that ADR-0004's consequences explicitly assign to the
Platform/Orchestrator; duplicates the accepted quorum/fencing mechanism
with a second consensus vocabulary and fault surface; multiplies
per-shard Rachis domains into a combinatorial qualification burden; and
buys nothing V2 needs — V1 needs Rachis because its orchestrator decides
topology in-database, which is exactly the concern V2 moved out.
Observable contracts (consistent quorum apply, fenced leader transition,
divergence repair, no stale-topology resurrection, bounded failover) are
satisfiable through the existing plane, and the PB-5016..PB-5020
dispositions commit to proving them there.

**No sharding** (single range only, scale-up only). Rejected on charter
PD-2026-08-26-10 D3: full sharding parity is committed scope
(PB-5036..PB-5041, PB-4036/4060, PB-5048), and the multi-region goal
requires distributing ranges across Members and Regions with independent
failover — which the single-range model cannot express.

**Direct hash-to-Member routing without buckets** (hash the shard key to
a Member directly). Rejected because ownership would then change for
every membership change (rehashing the whole keyspace), there is no
migration unit smaller than a shard (the property that makes resharding
incremental and restartable), and bucket-scoped accounting and routing
parity (PB-5037) would be lost. Buckets exist precisely to keep routing
stable while placement moves.

## Consequences

- The engine grows sharding as routing, validation, accounting, and
  migration execution over existing seams (`src/replication/`,
  engine/transaction, query/index, backup) — not as a new layer; SH-02..
  SH-05 implement and prove it; no capability claim precedes their
  proofs.
- `TopologyView`'s existing seam is sufficient (range text is opaque and
  validated); the canonical shard identity form is a convention the
  Platform and engine agree on in SH-02.
- The charter's production-topology-limits item is resolved for sharded
  topologies by this ADR plus `docs/sharding/topology-limits.md`; ADR
  0004 D-4's no-production-claims rule otherwise stays in force.
- PB-5016..PB-5020 progress to implemented/qualified only through the
  SH-05 fault lane named in `docs/sharding/dispositions.md`; this ADR
  unblocks them, it does not resolve them.
- Prefixed routing and sticky ids remain deferred (SH-02 decides), and
  no sharded performance claim exists without a Benchmark Receipt under
  PD-2026-08-26-03.
