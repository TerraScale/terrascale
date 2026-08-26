# Sharding parity dispositions

Status: proposed disposition record for SH-01 (`TerraScale/TerraBaseV2#47`),
2026-08-26. Authority: charter PD-2026-08-26-10 D3/D5, ADR-0004,
`docs/sharding/model.md`, and ADR 0011 (proposed). These are
recommendations recorded by SH-01 for parent acceptance. No overlay is
written by this record; overlay writes belong to the named rows' waves
under WS-PARITY rules.

## PB-5016..PB-5020 — cluster consensus and membership

Recommendation: **engine-owned, satisfied through the existing replication
plane plus `TopologyView` ownership epochs — not a port of V1 Rachis, not
a new embedded Raft, not Platform-owned.** openraft is retained only as a
named fallback if the SH-05 fault lane cannot pass, mirroring ADR-0004
D-1's Option C escalation clause.

Reasoning:

1. **The parity obligation is the observable contract, not the
   mechanism.** The five rows' V1 evidence describes Rachis internals
   (`RachisConsensus`, `AppendEntries`, `RequestVote`, ...), but their
   contract semantics are: a command applies consistently after quorum
   commit with a bounded failure when it cannot (PB-5016); leader
   transition with monotonically changing terms and step-down (PB-5017,
   PB-5020); a behind or diverged member is repaired to current state and
   reports what was restored (PB-5018); membership changes replace stale
   topology rather than resurrecting removed members, and offline members
   rejoin through recorded state (PB-5019); a lost leader stops accepting
   uncommittable commands and a survivor takes over (PB-5020). V2 already
   has an accepted engine for exactly these behaviors: ADR-0004 D-1's
   single-leader-per-range model with admission-time ownership validation
   and apply-time epoch fencing, the acknowledgement lattice (D-2) for
   quorum commit, and the R-1 catch-up/anti-entropy and rejoin machinery
   for divergence repair.
2. **The thing Rachis decides in V1 is not engine territory in V2.**
   Rachis is V1's cluster state machine: database records, shard
   topologies, orchestrator membership, task state. ADR-0004's
   consequences already assign topology administration, provisioning, and
   routing to the Platform/Orchestrator, and `topology.rs` already treats
   the `TopologyView` as caller-supplied input, never an engine decision.
   Porting a consensus service to replicate topology the engine does not
   own would re-create a control plane inside the engine and contradict
   the accepted boundary.
3. **A second consensus mechanism duplicates the accepted one.** The data
   plane already has quorum acknowledgement, fencing, and ordered
   application (W4-REPLICATION, accepted). Adding an embedded Raft for
   "cluster coordination" creates two vocabularies for leader, term, and
   commit, and a second fault surface, while the multi-region
   qualification contract (AC-10) already requires the epoch mechanism to
   survive member and region loss. The economies are all on extending the
   adjudicated model to sharded ranges (one epoch per shard).
4. **Platform-owned consensus is rejected** because fencing-critical
   facts (who owns which range at which epoch) must be enforceable by the
   engine at apply time, from validated input, without trusting an
   external call for durability-critical state; and because database
   behavior is this repository's charter scope.

Row mapping under this disposition (final proofs owed by SH-05's fault
lane; rows stay `unresolved` until then):

| Row | V1 contract | V2 vehicle under this recommendation |
| --- | --- | --- |
| PB-5016 consensus commit | consistent apply after quorum commit; bounded failure | acknowledgement lattice (D-2) with contributing-Member reporting; apply only after the named level commits |
| PB-5017 elections | leader transitions, monotone terms, step-down | control-plane ownership transitions with monotonically raised epochs; deposed-owner fencing (existing D-1 tests) |
| PB-5018 divergence and snapshots | behind/diverged member repaired; restored state observable | catch-up and anti-entropy over the publication history; rejoin with manifest/version check and progress reporting |
| PB-5019 membership and topology | stale topology fails; no resurrection; offline rejoin | `TopologyView` epoch monotonicity (views that lower an epoch are refused); member rejoin through recorded state |
| PB-5020 failover and member loss | lost leader stops commits; survivor takes over; no region inference from local success | epoch fencing plus SH-05's sharded failover lane; receipts name surviving Members and failure domains |

Fallback: if `old_owner_is_fenced_after_leader_transition`-class tests
cannot pass with epoch fencing in the SH-05 fault lane, the escalation is
openraft as an engine-internal ownership authority — reported as an
escalation by that lane, never silently switched (same shape as
ADR-0004 D-1's fallback clause).

## PB-5025 / PB-5027 — external and pull replication

Recommendation: **accepted-difference, delivered as Platform/backend
capabilities composed over engine seams** (named decisions: charter
PD-2026-08-26-10 D5, PD-2026-08-26-11 D1, and this record); a follow-up
ticket may add a typed engine-side continuous-replication source if the
Platform needs one.

- **PB-5025 (external destinations).** V1's external replication wires a
  database to remote destination URLs with its own retry, delay, and
  failure handling. In V2, cross-cluster destinations are a TerraScale
  Platform/backend concern: the Platform composes the engine's typed
  surfaces — the change-event backbone for continuous sourcing, backup/
  export/import (BK wave) for bulk movement, and the replication plane
  for member-to-member transfer — behind its own routing, credentials,
  and observability. The engine's local backend-only boundary
  (PD-2026-08-26-11) means it never dials external destinations itself.
- **PB-5027 (pull hub and sink).** Hub/sink roles, access registration,
  and certificate handling are external-authentication and routing
  policy, which the charter places exclusively at the Platform. No
  engine-side hub/sink surface is built; the disposition is
  accepted-difference, with logical export/import and backbone-mediated
  streaming as the supported equivalents.
- **Follow-up option, not a commitment:** a typed engine operation
  exposing a bounded continuous replication stream (backbone-based) for
  the Platform to terminate externally. File only if the Platform records
  the need; it is not required by these rows' contracts.

## PB-5036..PB-5041 and PB-5048 — wave mapping

The charter (PD-2026-08-26-10 D3) sequences SH-01..SH-05 as: topology and
bucket model, resharding and bucket migration, sharded query and index
execution, sharded failover and transactions with the cluster-coordination
decisions. SH-01 is this decision record. Proposed row mapping for the
implementation waves:

| Row | Name | Wave | Scope under the proposed model |
| --- | --- | --- | --- |
| PB-5036 | sharding-topology | SH-02 | shard-as-owned-range seating, bucket-range table validation, canonical range identities, routing to owner; view-refresh behavior |
| PB-5037 | sharding-buckets | SH-02 | bucket derivation, stability, bucket-scoped statistics and storage accounting |
| PB-5038 | resharding-and-bucket-migration | SH-03 | migration state machine over the replication plane; idempotent, restartable, progress-reporting; optimistic concurrency after movement |
| PB-5039 | sharding-orchestrator-coordination | SH-03 | the engine side of orchestration: typed migration/topology operations the Platform drives; no engine-internal orchestrator; no double-apply of record changes |
| PB-4036 | sharded-query-routing-and-reduction | SH-04 | query fan-out to owning shards, merge with ordering/paging/totals/includes parity, map-reduce reduction |
| PB-4060 | sharded-index-definition-and-readers | SH-04 | per-shard index lifecycle and readers behind the merged query contract |
| PB-5040 | sharding-replication-backup-and-rejoin | SH-05 | per-shard replication receipts, per-shard backup/restore, member rejoin, shard-identifying receipts |
| PB-5041 | sharding-failover-and-cluster-transactions | SH-05 | sharded failover lane, cross-shard transactions with preserved observable commit/failure results, subscriptions during resharding |
| PB-5048 | v2-multi-region-sharding-orchestration | SH-05 | multi-Region qualification of bucket ownership, resharding, rejoin; shard-local success never reported as database success |

PB-5016..PB-5020's final proofs land in SH-05 per the table above. Note
for the integrator: this mapping assumes SH-02..SH-05 follow the charter's
D3 ordering; the ticket bodies in the tracker are the authority and any
mismatch should be reconciled there, not by editing this record silently.
