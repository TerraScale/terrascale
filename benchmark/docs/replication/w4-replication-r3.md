# W4-REPLICATION R-3 implementation record

Status: implementation record for phase R-3 (final) of the W4-REPLICATION
packet, 2026-08-24. Authority: `docs/design/w4-transfer-replication-contract.md`
("Three-Region synthetic fault lane", "Public type ownership", "Checks and
evidence") and `docs/adr/0004` (accepted D-1..D-4). Worktree
`terrabasev2-w4-replication`, branch `fleet/w4-replication`, based on the
R-2 handoff `9fbf64d`; original implementation commit `f6082d7`. Repair
loop 1 later re-emitted all five receipts at `f70a5d8` (values unchanged
except as its provenance notes state); the receipts name that revision.

Phase scope: the three-Region synthetic fault lane and the
`ReplicationReceipt` artifacts. This record completes the packet's
23-name red-test disposition. It resolves no parity row and makes no
capability claim: every measurement quoted below is a lane-scoped fact.

## What was built

- `mod.rs` — the contract content of `ReplicationReceipt`: scenario
  identity; topology (Regions, Member seats, failure domains); fault
  classes; the acknowledgement facts per operation with their contributing
  Members; the convergence observation and its condition; RPO as a count
  plus a storage-sequence window; RTO and the convergence interval in lane
  ticks; replication lag as sequence distances; explicit limits. The four
  measurement definitions are contract-verbatim public constants
  (`RPO_DEFINITION`, `RTO_DEFINITION`, `CONVERGENCE_DEFINITION`,
  `LAG_DEFINITION`). `summary()` renders the secret-safe artifact.
  `FaultClass` is the fault vocabulary — class names only, so a receipt
  naming its faults stays secret-safe by construction.
- `lane.rs` (`#[cfg(test)]`) — the lane harness: `r-alpha`, `r-beta`,
  `r-gamma`; two Members per Region; each Region one failure domain; every
  Member a real durable Engine in a unique temporary directory; controlled
  in-process links with drop, delay, duplicate, reorder, and partition per
  direction. Helpers drive writes, acknowledgement waits, replication,
  Region partitions, healing, restart and rejoin, convergence observation,
  lag, and RPO accounting.
- `lane_tests.rs` — the two listed R-3 tests plus four lane scenario tests.
- `member.rs` — one correctness fix the lane surfaced: clock stamps now
  propagate on apply (a forwarded commit keeps its originator's stamp), and
  a self round-trip — the member's own write returning through another
  member's journal with the stamp it minted — resolves by operation
  identity instead of staging. The stamp equality is what keeps a hostile
  identity reuse under a different stamp failing the typed
  `OperationIdReuse` check, which the R-2 test still proves. Without this,
  a write that crossed a member and returned to its originator hit the W3
  digest binding (the ledger binds the staged document; the journal carries
  the published one) — the same frozen-path fact the R-1 record's W4-CA-8
  names.

## The lane clock, again

One tick per network pump; deadlines, observation instants, and delay
lengths are values of that clock. RTO and the convergence interval are
therefore honest lane-time measurements with their clock stated, never wall
time. A not-observed measurement is reported as not-measured with its
reason through a receipt limit — never guessed.

## Complete 23-name red-test disposition

Every name from the contract's "W4-REPLICATION red tests" list, its phase,
its home, and its state at this tip:

| # | Test name | Phase | Home | State |
| --- | --- | --- | --- | --- |
| 1 | `local_commit_receipt_contains_no_region_or_member_facts` | R-1 | in-crate | green |
| 2 | `replication_request_names_an_explicit_acknowledgement_level` | R-1 | `tests/replication/` | green |
| 3 | `acknowledgement_report_names_the_regions_and_members_that_contributed` | R-2 | in-crate | green |
| 4 | `acknowledgement_timeout_is_distinct_from_local_commit_failure` | R-2 | in-crate | green |
| 5 | `single_region_single_member_write_reads_back_and_survives_restart` | R-1 | in-crate | green |
| 6 | `one_region_multiple_members_reach_region_quorum_under_member_delay` | R-2 | in-crate | green |
| 7 | `two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility` | R-2 | in-crate | green |
| 8 | `member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success` | R-2 | in-crate | green |
| 9 | `region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit` | R-2 | in-crate | green |
| 10 | `delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome` | R-1 | in-crate | green |
| 11 | `duplicate_delivery_applies_each_operation_once` | R-1 | in-crate | green |
| 12 | `member_rejoin_checks_identity_and_catches_up_without_duplicates` | R-1 | in-crate | green |
| 13 | `restarted_member_resumes_from_its_durable_replication_cursor` | R-1 | in-crate | green |
| 14 | `anti_entropy_repairs_a_lagging_member_after_a_gap` | R-1 | in-crate | green |
| 15 | `convergence_report_names_members_operation_identity_and_observation_time` | R-2 | in-crate | green |
| 16 | `replication_status_reports_health_separately_from_convergence` | R-1 | `tests/replication/` | green |
| 17 | `isolated_concurrent_writes_converge_deterministically_after_healing` | R-2 | in-crate | green |
| 18 | `conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record` | R-2 | in-crate | green |
| 19 | `leader_loss_stops_new_commits_until_a_new_leader_is_confirmed` | R-2 | in-crate | green |
| 20 | `old_owner_is_fenced_after_leader_transition` | R-2 | in-crate | green |
| 21 | `tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable` | R-1 | in-crate | green |
| 22 | `replication_receipt_is_secret_safe_and_names_scenario_faults_and_limits` | R-3 | in-crate | green |
| 23 | `three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag` | R-3 | in-crate | green |

No listed name was weakened, renamed, or dropped. Focused extras beyond the
list: R-1 — the identifier, topology, and config unit tests plus seven
correctness extras; R-2 — the skew-bound, epoch, and clock-ordering extras;
R-3 — `lane_composite_scenario_is_deterministic`,
`lane_duplicate_replay_applies_each_operation_once`,
`lane_tenant_isolation_probe_denies_cross_tenant_and_is_observable`,
`lane_region_partition_with_isolated_concurrent_writes_converges`.

## Charter matrix mapping

| Charter row | Where it is exercised |
| --- | --- |
| one Region, one Member | R-1 `single_region_single_member_write_reads_back_and_survives_restart` (and the one-Region normalization case in R-2's quorum test) |
| one Region, multiple Members | R-2 `one_region_multiple_members_reach_region_quorum_under_member_delay` (three Members, one delayed past the quorum window) |
| two Regions, healthy links | R-2 `two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility` |
| member loss before acknowledgement | R-2 `member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success` (with recovery) |
| Region loss after acknowledged write | R-2 `region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit` and the composite lane's act F (the RPO measurement) |
| delayed and reordered replication | R-1 `delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome` and the composite lane's act C |
| member rejoin | R-1 `member_rejoin_checks_identity_and_catches_up_without_duplicates`, `restarted_member_resumes_from_its_durable_replication_cursor`, and the composite lane's act D |
| tenant-isolation probe | R-1 `tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable` and `lane_tenant_isolation_probe_denies_cross_tenant_and_is_observable` |

## Lane scenario list and receipt inventory

Five receipt artifacts under `evidence/replication/` (see the deviation note
below on their git status):

1. `w4-lane-three-region-composite.md` — the composite scenario covering
   the contract's full fault-class list and producing all four
   measurements.
2. `w4-lane-duplicate-replay.md` — duplicated delivery, idempotent apply.
3. `w4-lane-tenant-isolation-probe.md` — the cross-tenant probe in lane
   shape.
4. `w4-lane-member-restart-rejoin.md` — restart, rejoin, duplicate-free
   catch-up.
5. `w4-lane-region-partition-isolated-writes.md` — the full-Region
   partition with isolated concurrent writes and their D-3 convergence.

Each names the producing commit (originally `f6082d7`; re-emitted at
`f70a5d8` after repair loop 1), the run command, the asserted
facts, the measurements with their definitions or an explicit not-measured
reason, and limits, and states it makes no production claim. The composite
receipt's verbatim artifact is mirrored below so it is tracked in the
repository regardless of the evidence directory's git status.

## Deviation: the evidence directory is gitignored

The charter's WS-REPLICATION lane-receipt scope names
`evidence/replication/**`, and this phase wrote the artifacts there — but
the repository's `.gitignore` (integrator-owned, frozen to this packet)
ignores `evidence/`. The artifacts therefore exist in the worktree as
ignored, untracked files: they were written within the lease, and they were
not force-added, because overriding an integrator-set ignore rule from a
builder packet would be worse than leaving them untracked. Proposed
collision action W4-CA-12 records the integrator decision needed (un-ignore
`evidence/replication/`, or designate a tracked evidence path). Until then,
the tracked mirror below is the durable copy of the composite receipt, and
the other four artifacts are reproducible from their named tests at the
named commit.

## The composite receipt (tracked mirror, verbatim)

```text
scenario: three-region-composite
regions: r-alpha, r-beta, r-gamma
members: m-a1 r-alpha; m-a2 r-alpha; m-b1 r-beta; m-b2 r-beta; m-g1 r-gamma; m-g2 r-gamma
failure domains: fd-r-alpha [m-a1, m-a2]; fd-r-beta [m-b1, m-b2]; fd-r-gamma [m-g1, m-g2]
faults: link-delay, link-reorder, link-drop, link-duplicate, duplicate-replay, healing, member-restart, member-rejoin, leader-loss, region-partition, region-loss-after-acknowledged-write
acknowledged: op-1 at AllRegions by m-a1, m-a2, m-b1, m-b2, m-g1, m-g2
acknowledged: op-3 at RegionQuorum by m-a1, m-a2
acknowledged: op-4 at Local by m-b1
convergence: op-4 held by m-b1, m-b2, m-g1, m-g2 at lane tick 22
convergence condition: every surviving Member durably holds the operation
rpo: 1 lost, storage-sequence window 3..=3
rpo definition: The set of operations acknowledged at the requested level but absent from every surviving Member after the recorded fault, reported as a count and a storage-sequence window; never a time guess.
rto definition: The interval from fault injection to the first post-fault successful operation satisfying the availability condition for the surviving topology, in lane ticks of the lane clock (one tick per network pump); lane time only.
rto: 2 lane ticks
convergence interval: 8 lane ticks
convergence definition: The interval from the last healing event to the first Convergence observation naming every surviving Member, in lane ticks of the lane clock, together with the condition checked.
replication lag: m-a2 1, m-b1 1, m-b2 1, m-g1 1, m-g2 1, m-b2 2, m-g1 2, m-g2 3
lag definition: The storage-sequence distance between a Member's durable replication cursor and the source's published sequence at observation time; a sequence distance, not a latency claim.
limit: lane-scoped facts only: no production topology, timing, or performance claim
limit: RPO counts operations acknowledged then absent from every survivor
limit: RTO availability condition: first post-fault local write by the surviving owner
limit: convergence condition names the surviving Members, not the lost Region
limit: replication lag is a storage-sequence distance, not a latency
claim: none beyond the lane; lane-scoped facts only
```

## What the lane proves and what it does not

Proves, at the seam, in the lane's clock discipline: the acknowledgement
lattice with its Synced contributing threshold, degenerate normalization,
and timeout partial observations; convergence as its own observation;
deterministic D-3 convergence of isolated concurrent writes with
content-free conflict history; D-1 admission and apply-time fencing with
deposed-owner writes retained; region-loss observations that state durable
visibility and the unknown-until-rejoin limit; restart, rejoin, anti-entropy,
and idempotent replay; tenant isolation at both seams; and the receipt
shape with secret safety asserted against canary documents.

Does not prove: anything about real networks, disks, deployments, or
topologies; any production timing or performance; any behavior of a
cross-Region control plane (which is Platform/Orchestrator-owned); and the
cross-restart D-3 ordering guarantee (in-memory stamps; W4-CA-10). The
lane's RTO is a scripted detection window, and its convergence interval
includes the script's repair rounds — both stated with their clock and
neither citable outside the lane.

## Proposed collision actions (cumulative)

- W4-CA-8 (R-1): digest-bearing durable resolution (cheap replay
  classification). Partly mitigated in-scope by the stamp-propagation and
  self-round-trip resolution this phase added; the frozen-path action
  stands for the general form.
- W4-CA-9 (R-1): durable source-sequence progress for incremental
  post-restart catch-up.
- W4-CA-10 (R-2): persisted HLC stamps for cross-restart D-3 ordering.
- W4-CA-11 (R-2): write-set digest verification for acknowledgement
  contribution (merge with W4-CA-8).
- W4-CA-12 (R-3, new): the `.gitignore` treatment of `evidence/` — the
  lane receipts are written within the lease but untracked until the
  integrator un-ignores `evidence/replication/` or designates a tracked
  evidence path.
- W4-CA-13 (repair loop 1): persist the per-range fencing state
  (`highest_epoch_seen`) so a cold process restart preserves apply-time
  fencing; the lane's restart retains the memory today (see the R-2
  record's limits).

## Verification

Run from the worktree at the tip of this phase:

- `cargo fmt --all -- --check`
- `cargo check --workspace --locked --offline`
- `cargo clippy --workspace --locked --offline --all-targets -- -D warnings`
- `cargo test --workspace --locked --offline`
- `python3 scripts/validate_parity.py --check`

The lane runs under `cargo test --offline --locked --lib lane -- --nocapture`
and is deterministic: `lane_composite_scenario_is_deterministic` asserts two
runs of the composite script produce equal receipts.
