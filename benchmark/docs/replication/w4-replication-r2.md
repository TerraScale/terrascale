# W4-REPLICATION R-2 implementation record

Status: implementation record for phase R-2 of the W4-REPLICATION packet,
2026-08-24. Authority: `docs/design/w4-transfer-replication-contract.md`
("Replication and multi-region contract", "Decision D-1/D-2/D-3", "Public
type ownership") and `docs/adr/0004` (accepted D-1..D-4). Worktree
`terrabasev2-w4-replication`, branch `fleet/w4-replication`, based on the
R-1 handoff `a9e56e0`.

Phase scope: R-2 implements everything ADR-0004 D-1/D-2/D-3 names that R-1
reserved — the acknowledgement lattice above `Local`, the convergence
observation, the conflict policy on a bounded hybrid logical clock, and
leader fencing. The three-Region fault lane, `ReplicationReceipt`
artifacts, and RPO/RTO/convergence/lag measurement remain R-3. This record
resolves no parity row and makes no capability claim beyond the tested
behavior; every timing fact below is a lane-scoped fact.

## What was built

R-2 extends `src/replication/` without removing or renaming any R-1
surface:

- `mod.rs` — the contract content of `AcknowledgementReport`,
  `Convergence`, and `ConflictRecord`; the new lane-scoped vocabulary
  `ReplicationInstant` and `ReplicationClockStamp`; `ConflictOutcome`.
- `topology.rs` — ownership epochs (`with_owner_at_epoch`,
  `owner_epoch_of`); the plain `with_owner` builder assigns epoch 1.
- `transport.rs` — the lane clock on the network (`now()`, one tick per
  `pump`), multi-pump delay hold-backs (`delay_pumps`), the `Push` frame
  carrying range + ownership epoch, and `StampedCommit` pairing each
  commit with its writer's clock stamp.
- `member.rs` — the D-2 wait, the D-3 clock and conflict resolution, D-1
  view transitions and apply-time fencing, `observe_convergence`, and
  `observe_region_loss`.
- `tests.rs` / `tests/replication/` — the eleven listed R-2 red tests,
  one extra in-crate skew-bound test, and two external extras; the R-1
  suites stay green.

## The lane clock (no wall time anywhere)

`ReplicationNetwork` owns a tick counter that advances exactly one tick
per `pump()`. Deadlines, observation instants, and delay lengths are
values of that clock: `network.now()`, `ReplicationInstant::plus_ticks`,
`link.delay_pumps(n)`. Nothing in `src/replication/` reads `Instant`,
`SystemTime`, sleeps, threads, or async — the same determinism contract
R-1 held, now extended to the acknowledgement, convergence, and conflict
seams. `wait_for_acknowledgement` reaches its `Timeout` outcome only when
the lane clock passes the injected deadline instant; a test proves it
with a held-back link plus a deadline shorter than the hold.

## D-2: the acknowledgement lattice

`commit_write` completes the local publication first and on its own,
whatever the named level; `wait_for_acknowledgement` is the separate,
explicitly driven wait — the exact shape of V1's post-commit waits
(PB-5026). It never blocks or rewrites the local commit path, and the
local `CommitReceipt` stays local-only (the R-1 red test 1 still passes
unchanged).

Each round: push the operation to every live peer, pump once, let each
peer apply. Then:

- **Contributing threshold**: a Member contributes only when its own
  publication of the operation resolves durably at
  `LocalDurability::Synced` — the only local level with a
  durable-recoverability claim (ADR-0004 D-2).
- **Level evaluation** is per topology: `RegionQuorum` needs a quorum of
  the write's home Region's Members; `MajorityRegions` needs a Region
  quorum in a majority of Regions; `AllRegions` needs one in every
  Region. Quorum is `floor(n/2) + 1`.
- **Degenerate normalization** (D-4 canonical reading): with one Region,
  `RegionQuorum` = `MajorityRegions` = `AllRegions`, so those requests
  normalize to `RegionQuorum`; with two, `MajorityRegions` =
  `AllRegions`. The report records `(requested, canonical)` so reports
  stay comparable across topologies; `requested()` always names what the
  caller asked for.
- **Outcomes**: `Acknowledged(report)` with the contributing Regions and
  Members in canonical order and the observation instant;
  `Timeout { operation_id, requested, observed }` where the partial
  observation names exactly who had durably recorded at the deadline and
  the strongest level that partial set actually achieved;
  `Failed(MemberUnavailable)` when an `AllRegions` request loses a Member
  outright, `Failed(QuorumUnreachable)` for a quorum request. A lost
  Member is detected by reachability (engine absent), and recovery is
  immediate once it reopens — the listed member-loss test proves both the
  failure and the recovery.
- **Per-level visibility**: a read observes a replicated operation only
  after the observing Member's storage published it; the cross-Region
  test asserts the write is invisible at the second Region before its
  Member publishes it and readable after the acknowledgement names that
  Region. No level implies read-your-writes at an unnamed Region.

## Convergence

`observe_convergence(operation, observers, at)` is its own seam: a caller
taking an observation at an injected lane instant. It names every
observed Member that durably holds the operation — each holding reported
at `AcknowledgementLevel::Local`, that Member's own publication fact —
plus the operation identity and the observation instant. Members that do
not hold it are simply not named. There is no path from an
`AcknowledgementReport`, a `ReplicationStatus`, or anything else to a
`Convergence` value: convergence is never inferred from an
acknowledgement.

## D-3: the conflict policy

Each member carries a bounded hybrid logical clock
(`ReplicationClockStamp` = physical ticks + logical counter). Locally
admitted writes are stamped at commit; stamps travel with every served
commit (`StampedCommit`); a trusted incoming stamp merges into the local
clock (physical max, counter tick).

Convergence is deterministic last-writer-wins on the total order
`(stamp, MemberId)` — the Member identity is the tie-break past a stamp
tie, and because the comparison is a total order both sides of a pair
decide identically. An operation publishes atomically, so one disposition
covers all of its keys: it applies only when its stamp beats every
competing recorded key state of the applying member — regardless of
whether that state came from the member's own write or from an applied
remote write; otherwise the whole operation is skipped. Either way a
`ConflictRecord` is retained: the conflicting
operation identities (canonical order), the writer identities, the clock
stamps, and the policy outcome (`Winner`, `DeposedOwnerRetained`, or
`UntrustedClock`, each naming the Member converged to). The record is
content-free by construction and asserted so in the tests.

**Skew bound B.** `MAX_CLOCK_SKEW_TICKS = 4` lane ticks. Derivation: the
lane clock is the shared transport clock and advances one tick per pump;
a member refreshes its physical component at every network-touching call
and at an explicitly injected instant, so the maximum skew a correct lane
member can show against the member observing it is one tick — the tick
the peer observed before the observer's last refresh. Four ticks is a
stated four-times margin over that one-tick maximum, chosen to err toward
classifying genuine reorderings as conflicts rather than trusting a clock
that cannot be trusted. A peer stamp beyond local + B is untrusted: the
pair converges as a conflict decided by the deterministic tie-break —
never rejected, never clamped (the ADR-0004 D-3 disposition). This is a
lane-scoped fact: the production B must be derived from the deployment's
provisioned clock discipline plus a stated margin, which is a parent
decision (D-3 parameter), and nothing here claims it.

Conflict history is bounded (`MAX_CONFLICTS`, evicting the oldest) and
deduplicated by operation set + outcome.

## D-1: leader fencing

- **Admission fencing** (from R-1, now exercised): a member commits only
  while its current view names it owner of the range; a deposed owner's
  new writes fail with the typed `NotOwner` error locally, with no
  cross-Member round trip.
- **Epochs**: `with_owner_at_epoch` records the transition number;
  `apply_view` refuses a view that lowers a held epoch or flips an owner
  at a non-higher epoch (`StaleView`), and records the deposition
  observably.
- **Apply-time fencing**: a `Push` batch stamped below the highest epoch
  seen for its range is rejected with the typed `FencedBatch` error, the
  event is recorded observably (`fenced()`), and the batch's commits are
  retained as `DeposedOwnerRetained` conflict history — a local durable
  fact is never silently discarded (ADR-0004 D-1's old-leader rule).
- **Leader loss**: while no surviving member owns the range, every commit
  fails admission; applying the transitioned view (the confirmed new
  leader) resumes commits. There is no automatic failover: the transition
  is the control plane's act, delivered as a new `TopologyView`.

## Region loss

`observe_region_loss` states durable visibility — the Regions and
Members that durably hold the acknowledged write at the loss instant —
plus the recovery limit: the lost Regions' latest state is
`UnknownUntilRejoin`, and nothing about their content may be inferred
before one of their Members rejoins and validates its cursor. It supports
no claim about what the lost Regions held (ADR-0004 D-4).

## The R-1 seam contract after R-2

- `commit_write` still admits and commits locally; the stronger-level
  refusal (`AcknowledgementLevelNotImplemented`) is gone because the
  lattice exists, and the R-1 focused extra that asserted the refusal now
  asserts the R-2 contract: the local commit completes and the wait is
  the separate step.
- The local-receipt test, tenant isolation, idempotence, cursor,
  rejoin, and anti-entropy suites all pass unchanged.
- The R-1 external test that asserted `Convergence` was a zero-sized
  reserved type now asserts it carries content while remaining a
  separate type from status (the compile_fail doc proof that no status
  value converts to `Convergence` still holds).

## Limits and non-claims

- Everything here is exercised through the lane (in-crate tests and
  `tests/replication/` public-construction tests). No production
  topology, timing, or performance claim is made or implied.
- The acknowledgement wait drives replication synchronously round by
  round; it is deterministic and bounded by the injected deadline, and it
  is not a claim about real network behavior.
- The D-3 key-state and stamp tracking is in-memory per member and does
  not survive a restart (see the R-1 record's cross-restart note): after
  a restart, a member serves its journal at its current clock and
  compares incoming writes with a fresh key state. The isolated-writers
  determinism and both listed conflict tests do not cross a restart;
  a restart-crossing ordering guarantee would need the stamps persisted
  with the journal (proposed collision action below).
- The remote-apply tie-break is delivery-forwarder-keyed: a key state
  recorded from a remote write carries the DELIVERY SOURCE's identity as
  its writer, because the originator identity does not travel in the
  frozen `PublishedCommit`. The `(stamp, Member)` order stays total per
  member, and every directly delivered path (all tested paths) breaks
  ties exactly. But two replicas that received the same write through
  different forwarders can record different writers for it, so an exact
  stamp tie across such divergent forwarding paths can resolve
  differently per replica. Closing that edge needs the originator
  identity persisted with the stamps (see W4-CA-10 below).
- The D-1 fencing state (`highest_epoch_seen`) is likewise in-memory:
  `reset_memory_state` deliberately KEEPS it across an engine reopen, so
  the lane's "restart" models a process that retains its fencing
  memory (close and reopen of the same process image), not a cold
  process restart. A member that lost that memory — a true process
  restart — would accept a deposed leader's below-epoch pushes again
  until it re-learned the epoch from a view or a peer. Persisting the
  fencing state with the journal is a separate proposed collision action
  (W4-CA-13), distinct from the stamp proposal below.
- `wait_for_acknowledgement` pushes the single operation it is waiting
  for; a production fan-out batching many in-flight operations is an R-3
  optimization, not a correctness gap.
- The lane supports the fault classes R-3's matrix needs (drop,
  duplicate, reorder, multi-pump delay, partition, member restart), but
  the three-Region lane itself and its receipt are R-3.

## Proposed collision actions (in addition to R-1's W4-CA-8/W4-CA-9)

- W4-CA-10 (proposed): persist per-operation HLC stamps (and per-key last
  stamps, including the WRITER identity for each stamp — the originator,
  not the delivery forwarder) alongside the publication-history journal,
  so a restarted member preserves its D-3 ordering facts and every
  replica records the same writer for one write even when it arrived
  through different forwarders. Touches frozen
  `src/storage/**`; a builder cannot do it. Until then the cross-restart
  ordering behavior and the forwarder-keyed tie-break edge are the
  documented limits above.
- W4-CA-11 (proposed): an engine-side durable write-set digest comparison
  seam would let `wait_for_acknowledgement` verify a peer's held
  operation is the same write set (not just the same identity) before
  counting it as contributing. This overlaps W4-CA-8 and should be
  executed as one action.
- W4-CA-13 (proposed, added in repair loop 1): persist the per-range
  fencing state (`highest_epoch_seen`) alongside the publication-history
  journal, so a cold process restart preserves apply-time fencing instead
  of re-learning it from a view or a peer. The lane's restart keeps the
  memory today, which is the documented limit above; this is distinct from
  W4-CA-10 (ordering stamps) and could be executed with it in one storage
  schema change.

## Verification

Run from the worktree at the tip of this phase:

- `cargo fmt --all -- --check`
- `cargo check --workspace --locked --offline`
- `cargo clippy --workspace --locked --offline --all-targets -- -D warnings`
- `cargo test --workspace --locked --offline`
- `python3 scripts/validate_parity.py --check`

The eleven listed R-2 test names and their home (all in-crate, in
`src/replication/tests.rs`):

- `acknowledgement_report_names_the_regions_and_members_that_contributed`
- `acknowledgement_timeout_is_distinct_from_local_commit_failure`
- `one_region_multiple_members_reach_region_quorum_under_member_delay`
- `two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility`
- `member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success`
- `region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit`
- `convergence_report_names_members_operation_identity_and_observation_time`
- `isolated_concurrent_writes_converge_deterministically_after_healing`
- `conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record`
- `leader_loss_stops_new_commits_until_a_new_leader_is_confirmed`
- `old_owner_is_fenced_after_leader_transition`

Focused extras: `a_peer_stamp_beyond_the_skew_bound_converges_as_a_conflict`
(in-crate); `topology_views_carry_monotonic_ownership_epochs` and
`lane_instants_and_clock_stamps_order_deterministically`
(`tests/replication/`).

## R-3 entry points left ready

- The transport doubles, member driver, lattice evaluation, conflict
  resolution, and fencing are topology-agnostic: R-3's three-Region lane
  (`r-alpha`/`r-beta`/`r-gamma`, two Members per Region) can use them as
  they stand, and `three_regions_one_member_each` in the tests already
  exercises the non-degenerate three-Region lattice shape.
- `ReplicationReceipt` remains declaration-only with its R-3 reservation;
  the lane needs only to assemble the secret-safe scenario record from
  the observations this phase produced.
- The measured definitions (RPO as a sequence window, RTO and convergence
  in lane ticks, lag as a sequence distance) map directly onto
  `ReplicationCursor`, `ReplicationInstant`, and the per-tenant high
  waters this phase already reports.
