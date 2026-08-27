# SH-05 coordination record: sharded failover, cluster transactions, recovery

Status: proposed record for SH-05 (`TerraScale/TerraBaseV2#51`), 2026-08-26.
Authority: charter PD-2026-08-26-10 D3, ADR 0004 (D-1/D-2), ADR 0011
(D1/D3/D4), and `docs/sharding/dispositions.md` (SH-01's row mapping put
PB-5040/5041/5048 and the PB-5016..PB-5020 final proofs in SH-05). The
implementation lives in `src/sharding/coordination/`; the qualification
scenarios in its crate-private lane; the receipts under
`evidence/sharding/`.

## What SH-05 builds (and does not)

Under ADR 0011 D4, cluster coordination runs through the existing
replication plane and `TopologyView` ownership epochs — no Rachis port, no
second consensus service, no engine-internal orchestrator. SH-05 therefore
adds three engine-owned coordination seams and qualifies them; it does not
add a consensus mechanism, and the openraft fallback in
`docs/sharding/dispositions.md` was not needed by the fault lane (the
`old_owner_is_fenced`-class behavior passed on epoch fencing; recorded
below with its scope).

### 1. Shard loss assessment and failover decisions (`failover.rs`)

- `ShardLoss::assess` names, per shard, whether its owner survived the
  declared Member loss — with the lost owner's Region, failure domain, and
  bucket ranges — plus the surviving Members. It is availability truth per
  shard only: no database-wide verdict exists on this seam.
- `FailoverDecision::decide` validates the Platform's replacement
  table/view pair under a declared loss. Failover shape or refusal:
  - a shard whose owner survived keeps its owner and never shrinks
    (owner swaps and range moves on healthy shards are migration
    decisions — `HealthyShardOwnerReplaced`, `HealthyShardShrunk`);
  - a lost shard is transferred to a surviving successor at a raised
    epoch (`ShardTransfer`) or dispersed into other shards
    (`DispersedShard` with per-receiving-shard bucket counts);
  - the declared-lost owner is never re-seated (`LostOwnerRetained`) and
    a lost successor is refused (`SuccessorLost`);
  - a shard identity the seated table does not name — a new or returning
    identity entering the table — is judged as a claim on the buckets it
    takes, never as a fact of a brand-new identity: its owner must
    survive the declared loss (`EnteringShardLostOwner`) and its epoch
    must exceed the fencing bar of those buckets
    (`EnteringShardEpochNotRaised`), so a deposed owner cannot re-enter
    through a re-identified shard at the epoch it was deposed from;
  - the sharding seam's own seating and epoch-monotonicity rules
    (`ShardTopology::transition`) apply unchanged, including the seam's
    entering-identity fencing bar
    (`ShardingError::EnteringShardEpochNotRaised`), which every
    transition enforces — not only failover.
- Deposed-owner fencing is not re-implemented: the plan's seated topology
  refuses the deposed owner's placements through the existing
  `authorize_placement` checks (`PlacementOwnerSuperseded`,
  `PlacementEpochSuperseded`), and execution-time re-authorization inside
  cluster transactions fences a failover that lands between routing and
  execution.

### 2. Cluster-scoped transactions (`transactions.rs`)

PB-5041's observable contract — writes that span shard-local transactions
with their observable commit or failure results preserved — is delivered
with an explicitly recorded atomicity boundary:

- A **single-shard** cluster transaction commits atomically as one
  shard-local engine transaction (`ClusterOutcome::Atomic`), with the
  engine receipt's observable result preserved across idempotent
  re-drives (resolve-first by operation identity).
- A **cross-shard** cluster transaction is *explicitly limited, never
  silently atomic*: each shard commits its own shard-local transaction
  (`ClusterOutcome::PerShard`), and the outcome records the limits
  (`CROSS_SHARD_NOT_ATOMIC`, `NO_CROSS_SHARD_ROLLBACK`,
  `SHARD_LOCAL_ACKNOWLEDGEMENT`). A refused shard makes the outcome
  `Partial`: committed shards stay committed, the refusal names the shard
  and reason, the shards execution never reached are recorded as
  unattempted (committed, refused, and unattempted partition the routed
  shards exactly), and a re-drive replays committed shards idempotently
  while retrying only what refused.
- A shard-local receipt is a `LocalDurability::Synced` fact about one
  shard's engine; database-wide acknowledgement is the replication
  plane's own observation and is never inferred here (ADR 0011 D1's
  "shard-local acknowledgement is never database success").
- Tenant context is backend-supplied (PD-2026-08-26-11): the executor's
  caller seats one Platform-minted `TenantScope` per Member; an owner
  with no seated scope refuses its shard (`ScopeMissing`), a seated scope
  of another tenant refuses it too (`ScopeTenantMismatch` — execution
  never writes through a foreign tenant's context, whatever the caller
  seated), and the engine seam validates every scope.

This is a decision, not a gap in progress: cross-shard atomic commit
would require a distributed-commit protocol that the accepted model does
not carry (ADR 0011 D4's rejected-alternatives reasoning), so the limit is
the contract until a new decision record accepts one.

### 3. Per-shard recovery reporting (`recovery.rs`)

PB-5040's receipt-shape obligation: a `ShardRecoveryReport` names every
seated shard and every Member that contributed to the observation, with
the owner's verdict per shard. Only seated shards and Members enter it;
every seated shard must be reported (a receipt may not skip a shard); an
owner-absent shard is recorded unrecovered, never guessed recovered.

## Qualification: the multi-region matrix extended with sharded topologies

The charter's multi-region qualification matrix (AC-10) gains sharded
rows. Every scenario runs in the deterministic in-process lane
(`src/sharding/coordination/lane.rs`): three Regions, two Members per
Region, one failure domain per Region, two shards owned across two
Regions, every Member a real durable Engine. No clock, thread, async
runtime, or wall time; the determinism test asserts byte-equal receipts
across runs. Each receipt opens with its fault line in the accepted W4
fault vocabulary (the replication lane's composite receipt vocabulary —
`leader-loss`, `healing`, `region-loss-after-acknowledged-write`,
`member-restart`, `member-rejoin`); the sharded lane names exactly the
faults its scenarios inject and invents no fault names.

| Scenario (matrix row) | Required result | Producing test | Receipt |
| --- | --- | --- | --- |
| sharded topology, healthy | per-shard writes commit shard-locally; a shard-local receipt never reads as database success | `lane_shard_owner_loss_failover_is_truthful_and_deterministic` (healthy prefix) | `evidence/sharding/sh05-failover-lane.md` |
| shard-owner loss before acknowledgement | write to the lost shard refuses before commit and lands nowhere; surviving shard keeps serving | `lane_shard_owner_loss_failover_is_truthful_and_deterministic` | same |
| failover after shard-owner loss | transfer at a raised epoch; deposed owner's placements fenced; the refused write re-drives and commits after the transfer | same | same |
| Region loss after acknowledged sharded write | per-shard RPO measured as acknowledged-then-absent operations; double transfer to the surviving Region | `lane_region_loss_after_acknowledged_sharded_writes_measures_per_shard_rpo` | `evidence/sharding/sh05-region-loss-lane.md` |
| cross-shard cluster transaction under shard loss | partial commit recorded, no rollback claimed, idempotent completion after failover | `lane_cross_shard_transaction_refuses_partially_and_completes_after_failover` | `evidence/sharding/sh05-failover-lane.md` |
| member lost beyond backup boundary, restored, rejoined | restored Member holds exactly the backup; the window above it is reported per shard | `lane_backup_restore_rejoin_reports_the_recovery_window_per_shard` | `evidence/sharding/sh05-backup-rejoin-lane.md` |

### PB-5016..PB-5020 proof status under this lane

Per `docs/sharding/dispositions.md`, those rows' final proofs were owed
by the SH-05 fault lane. What this lane proves directly:

- **PB-5017 (elections/leader transition)** — control-plane ownership
  transitions with monotonically raised epochs; deposed-owner fencing at
  the placement seam; no epoch regression accepted (the sharding seam's
  transition rules, re-validated under loss).
- **PB-5019 (membership/topology)** — stale topology fails closed
  (`LostOwnerRetained`, `SuccessorLost`, `PlacementEpochSuperseded`);
  failover never resurrects a declared-lost owner, directly or through a
  re-identified shard entering the table
  (`EnteringShardLostOwner`, `EnteringShardEpochNotRaised`).
- **PB-5020 (failover/member loss)** — lost-leader shards stop committing
  (refused writes land nowhere); survivors take over at raised epochs;
  receipts name surviving Members and failure domains.

What this lane composes rather than re-proves (the W4 lane's own
qualified machinery, unchanged by SH-05):

- **PB-5016 (consensus commit)** — quorum acknowledgement is the
  replication plane's acknowledgement lattice; the sharded lane drives
  lane-scoped delivery over the same engine primitives and reports
  holding facts per Member, and does not re-qualify the lattice.
- **PB-5018 (divergence/snapshots)** — catch-up and rejoin repair are the
  replication plane's R-1 machinery; the lane's delivery replay mirrors
  its replay rules (resolve-first, `ExpectedVersion::Any`, `Synced`) but
  is lane machinery, not the qualified transport.

Row dispositions remain the parent's to flip under WS-PARITY rules; this
record proposes, it does not write overlays.

## Limits

- Everything the lane reports is a lane-scoped fact of one deterministic
  run at the recorded commit: no production topology, timing,
  performance, or durability claim.
- The coordination seam observes no clock: no detection, RTO, or
  convergence-interval fact is claimed; those belong to the replication
  lane's tick-based receipts.
- Cross-shard atomicity is not provided (recorded in every cross-shard
  outcome); a cross-Region orchestrator quorum, placement policy, and
  resharding policy remain Platform decisions (PB-5048's "no default is
  selected" stands).
- Subscriptions during resharding (PB-5041's V1 test surface) are not
  claimed by this record; the SE wave owns the delivery surface this
  composes with.
- No sharded performance claim is made or implied; any such claim needs a
  Benchmark Receipt under PD-2026-08-26-03.
