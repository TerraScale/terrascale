---
status: accepted
---

# ADR 0004: W4 replication, acknowledgement, and conflict decisions

Parent adjudication of the W4 design contract's D-1..D-4 records,
2026-08-24.

Owner: the parent fleet orchestrator, on the evidence of
`docs/design/w4-transfer-replication-contract.md` at commit `93f04ee`, the
W3 contracts it builds on, and the pinned TerraBase V1 source identity.

## Context

The charter listed the replication algorithm, consistency and
acknowledgement defaults, conflict policy, and production topology limits
as parent-gated decisions no builder could make silently. The W4 design
contract recorded them as proposals with options, V1/W3 evidence, and
recommendations. Both independent gates reviewed the contract; their
adjudication-blocking amendments (fencing parameters, contributing
durability threshold, loss-matrix completeness) were applied before this
decision.

## Decisions

### D-1 — replication algorithm: single leader per data range with cross-Region quorum

Option A is accepted. One Member owns each data range's write path;
stronger acknowledgement levels commit once the required set of Regions
has durably recorded the operation; followers catch up asynchronously
through the publication history (W4-CA-4). This is the only option that
preserves W3's accepted single-writer publication order unchanged and can
support the full Acknowledgement Level lattice and the charter's
region-loss scenarios honestly.

Fencing: admission-time topology-ownership validation plus apply-time
epoch checks. Admission fencing stops a deposed leader's new writes
immediately and locally; epoch checks catch writes that race past
admission. Conflict-policy-only fencing is rejected — it lets a deposed
leader keep minting writes that all become conflicts.

Old-leader writes: a deposed leader's post-deposition local commits are
retained as observable ConflictRecord history, never silently discarded.
They keep their local receipts (a local durable fact is real locally) and
converge under D-3.

Fallback: Option C (full consensus data plane) escalates at the
W4-REPLICATION gate if `old_owner_is_fenced_after_leader_transition` and
its fault variants cannot pass with this mechanism in the fault lane.
That gate reports the escalation; it does not silently switch.

### D-2 — acknowledgement defaults: Local by default, stronger levels explicit

The lattice is `Local`, `RegionQuorum`, `MajorityRegions`, `AllRegions`.
The default is `Local` (V1 parity: local save, replication asynchronous).
Stronger levels are explicit per operation. A Member contributes to an
acknowledgement only after its local publication reaches
`LocalDurability::Synced` — the only level with a durable-recoverability
claim in W3's loss-window table. `ReplicationOutcome::Timeout` is
distinct from any local commit failure and carries the partial-ack
observation (which Members had durably recorded at timeout). No level
implies cross-Region read-your-writes unless the Region is named in the
report; Convergence is its own observation, never inferred from an
acknowledgement.

### D-3 — conflict policy: last-writer-wins on a bounded hybrid logical clock

Documents converge by deterministic last-writer-wins on a bounded hybrid
logical clock; losers are retained as observable, content-free
ConflictRecord history. Per-family deterministic merge applies where the
family defines one (counters, time-series — V1 merges these by
construction). A peer whose clock exceeds the configured skew bound has
its timestamps treated as untrusted: affected concurrent writes converge
as conflicts rather than being rejected or clamped. The skew bound value
derives from the D-4 timing bounds and is recorded with the
W4-REPLICATION implementation evidence. Application-resolved conflicts
remain a later addition, not a W4 default.

### D-4 — topology limits: three synthetic Regions in W4, no production claims

W4 qualifies a three-synthetic-Region lane only. No W4 artifact may state
a production topology limit, and RPO/RTO/convergence/lag numbers are
lane-scoped facts. Lattice degeneracy (with one Region,
RegionQuorum = MajorityRegions = AllRegions; with two Regions,
MajorityRegions = AllRegions) is part of the contract; the lane exercises
the non-degenerate three-Region case. Production limits, placement, and
cross-Region timing bounds remain parent-gated.

## Consequences

- PB-5000, PB-5016..PB-5020, PB-5022, PB-5026, PB-5034, PB-5042,
  PB-5043, and PB-5045 may progress from `unresolved` only through the
  normal parity proof path; this decision unblocks their owners, it does
  not resolve them.
- Data-plane replication is engine-owned; control-plane replication
  (topology administration, provisioning, routing) remains
  Platform/Orchestrator-owned and outside this repository.
- The W4-REPLICATION fault lane's fencing tests are the accepted
  escalation trigger for D-1's fallback.
