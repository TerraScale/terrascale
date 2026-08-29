# Model-based state-machine checking lane: membership + authorization generations

Status: implemented (bounded model checking within the bounds recorded
below). Not qualified beyond those bounds; performance rows are
untouched by this lane.

Last revised: 2026-08-29.

## What this lane is

GOAL.md "Correctness and simulation" (line 2402) requires that
"membership, coordinated operations, causal compaction, and
authorization generation transitions require model-based or formal
state-machine checking in addition to ordinary tests." The
qualification gap sweep (`docs/evidence/qualification-gap-sweep.md`)
recorded that no model checker existed anywhere in the repository
(rows 15 and 19 capped at `partial` for exactly that reason, standing
constraint 4). This lane supplies the first one, for two of the four
named areas:

- membership / replica-set epoch transitions (row 15),
- authorization generation transitions (row 19, engine policy layer).

Coordinated operations and causal compaction remain WITHOUT a model
checker; nothing in this lane claims them.

## Artifacts

| File | What it is |
| --- | --- |
| `crates/terrabase-cluster/tests/model_checker.rs` | A small dependency-free bounded-exhaustive checker (DFS, canonical-key memoization, state + transition invariants, exact state cap with an honest truncation flag), with self-tests including a negative control proving it catches a broken invariant with a reproduction trace. Compiled both as its own test target and as a module of the membership model. |
| `crates/terrabase-cluster/tests/membership_model.rs` | The membership transition model over the REAL `terrabase-cluster` types (`Membership`, `RegionMembers`, `DurabilityCoordinator::advance_membership`/`submit`/`record_ack`/`set_member_active`/`set_region_reachable`), the three invariants below, two exhaustive sweeps, two negative controls, and one focused fencing drill. |
| `src/authorization/generation_model_tests.rs` | The authorization-generation model over the REAL engine types (`Policy::try_new` publications, `PrincipalContext::from_verified` pins — the crate-private verified seam — and `evaluate`), with its own minimal local DFS (a `src/` unit test cannot import another package's integration-test checker). Sibling of `src/authorization/tests.rs`, which is untouched. Wired in by one `#[cfg(test)] mod generation_model_tests;` line in `src/authorization/mod.rs`. |
| `docs/evidence/model-checking-lane.md` | This file. |

Reproduce (focused checks only; the full CI gate owns everything
wider):

```sh
cargo test -p terrabase-cluster --test model_checker -- --nocapture
cargo test -p terrabase-cluster --test membership_model -- --nocapture
cargo test -p terrabase-v2 --lib authorization -- --nocapture
```

## Membership model (row 15)

### Modeled system

Two real `DurabilityCoordinator`s are two members of one replica set
that observe a configuration change at different times: the window
where one side still operates the OLD configuration and the other
already operates the NEW one — the overlapping-quorum transition row
15 names. Both sides walk one shared configuration chain
(serialized installation, the protocol assumption): the side at the
chain tail mints the next configuration through its own real
`advance_membership` — which refuses stale versions and
non-overlapping shapes — and a side behind the tail can only catch
up. Steps offered from every state:

- `Submit` (fresh or known operation, both durability levels),
- `Advance` (catch-up, or mint any shape at any of the four version
  candidates `(+1,+1)`, `(+1,+0)`, `(+0,+1)`, `(+0,+0)` — only the
  first can be accepted; the others exist to be refused typed),
- `ToggleMember` / `ToggleRegion` (fault interleaving, fault sweep
  only),
- `ProbeStaleAck` (an acknowledgment pinned at the side's previous
  version),
- `InstallFresh` (negative controls only: direct construction that
  bypasses `advance_membership`).

Shapes are RF=2 seats per region; the sweeps cover single-region
pools and two-region shapes with region addition/erasure.

### Invariants checked

- **INV-A — no split replica set (quorum intersection).** Whenever
  the two live sides hold different effective configurations, the
  configurations share a member; every consecutive chain pair also
  overlaps. Enforced in the explored space by the real
  `advance_membership` overlap refusal.
- **INV-B — identity/counter never reused.** One `(generation,
  epoch)` pair is never bound to two different member sets in a run;
  both counters strictly increase along the chain.
- **INV-C — stale-epoch operations are typed refusals.** Stale
  acknowledgment -> `StaleMembership`; an operation pinned at a
  superseded version -> `StaleOperation` at the fencing boundary;
  non-increasing or disjoint advances -> `StaleVersion` /
  `NoOverlap` exactly when expected; and every receipt names exactly
  the issuing configuration with contributors seated in it.

### Coverage bounds (measured, not estimated)

| Sweep | Universe | Depth | Distinct states | Transitions | Complete? |
| --- | --- | --- | --- | --- | --- |
| single-region | 3-member pool, 3 shapes, all 3 initial shapes, 1 op/side, 4 version candidates | 6 | 18,720 | 103,596 | exhaustive to depth (state cap unhit) |
| two-region with faults | r1 pairs over {a,b,c} x optional r2 {d,e} (6 shapes), faults on, 1 op/side | 5 | 128,623 | 632,210 | exhaustive to depth (state cap unhit) |

"Exhaustive to depth" is asserted in the tests (`!hit_state_cap`):
every trace of transitions up to the depth bound was explored. Longer
traces always exist (version counters are unbounded), which the
reports record as `truncated: true` — the expected, honest shape.

Negative controls (proving the checker is not vacuous):

- A disjoint configuration installed by direct construction
  (`DurabilityCoordinator::new`, bypassing `advance_membership`) is
  detected as INV-A "split replica set" with a trace ending in the
  rogue install, which the control first proves CAN commit.
- Binding a version pair already in the chain to a different member
  set is detected as an INV-B counter-reuse violation.

Focused straight-line drill
(`focused_stale_epoch_refusals_are_typed_across_an_overlapping_transition`):
across one overlapping transition (old seats {m0,m1} -> new {m1,m2}),
every non-increasing advance shape refuses `StaleVersion`, a disjoint
shape refuses `NoOverlap`, the in-flight operation refuses
`StaleOperation` typed, stale and duplicate and wrong-seat
acknowledgments refuse typed, and the new configuration commits with
the overlapping member.

### Seams, stated honestly

1. `DurabilityCoordinator` is not `Clone` and its acknowledgment
   ledger cannot be re-seeded through the public API after a version
   advance, so the model rebuilds each side from a canonical summary
   before every step. Operations pinned at a superseded version are
   probed at the fencing boundary (inside the advance step, against
   the live coordinator that still holds them) and then retired from
   the summary.
2. An operation whose ENTIRE acknowledgment set sits on deactivated
   members cannot be re-seeded either; such operations retire without
   a live fencing probe inside the sweep, and the real refusal for
   that shape is proven straight-line in the focused drill. (The
   sweep's own invariant checker caught the divergence that motivated
   this seam during development — the model initially expected the
   rebuilt coordinator to refuse `StaleOperation` for an operation it
   could not hold.)
3. The serialized configuration chain is a protocol assumption. The
   crate's API enforces version monotonicity and overlap per
   coordinator, but nothing prevents two coordinators that both
   bypass `advance_membership` from binding the same or disjoint
   configurations — which is exactly what the negative controls
   demonstrate being detected. Config-installation agreement across
   acceptors (the control plane's job in the real system) is outside
   this crate's API surface.
4. Membership advances reset member-activity and region-reachability
   flags (real `advance_membership` behavior); the model mirrors that
   reset rather than modeling fault persistence across transitions.

## Authorization generation model (row 19)

### Modeled system

One policy with two grants (`read documents`, `read secrets`) is
republished across generations (bounded at generation 4); two
principals each hold a PINNED generation — the generation their
verified context was minted at, moved only by an explicit
re-verification (`Refresh`, the reconnect-and-refresh path). The
clock ticks from 100 toward the 200 expiry. Every `Read` step runs
the REAL `evaluate()` against the REAL `Policy` and
`PrincipalContext` built for that state.

### Invariants checked

- **INV-1 — a revoked principal never reads through any
  interleaving.** An allow requires a current pin AND a present rule
  AND an unexpired window; after a revocation every interleaving
  denies (stale pins by `PolicyGenerationMismatch`, refreshed pins by
  `NoMatchingRule`).
- **INV-2 — removals precede generation advance.** Any publication
  that changes the rule set strictly increases the generation (state
  and transition forms).
- **INV-3 — stale-generation checkpoint refusal is typed.** A stale
  pin with otherwise-valid unexpired context refuses with EXACTLY
  `PolicyGenerationMismatch`; an expired stale context refuses
  `Expired` (evaluate orders expiry first) — still a denial.

### Coverage bounds (measured, not estimated)

| Sweep | Universe | Depth | Distinct states | Transitions | Complete? |
| --- | --- | --- | --- | --- | --- |
| generation interleavings | generations 1..=4, 2 rules, 2 principals with pins, clock {100,125,150,175,200} | 8 | 3,325 | 21,198 | exhaustive to depth (state cap unhit) |

Negative control: an un-gated publisher that removes a rule WITHOUT
advancing the generation is detected as an INV-2 violation with a
trace ending in the unversioned publication. This matters because
`Policy::try_new` itself does NOT enforce monotonic publication — the
engine's fail-closed guarantee for stale pins (the generation check
in `evaluate`) only protects principals whose pin differs from the
current policy; same-generation rule swaps are invisible to it.
Publication discipline is the gateway's obligation; the model states
it as the checked protocol rule.

Straight-line drill
(`a_revoked_principal_never_reads_again_through_any_observed_path`):
grant at generation 1, revoke `secrets` at generation 2, then the
stale checkpoint answers `PolicyGenerationMismatch`, the refreshed
principal answers `NoMatchingRule`, a checkpoint from before the
revocation still answers the mismatch after two further publications
(including a re-grant for other principals), expiry answers
`Expired`, and a freshly verified current principal reads again (the
positive control).

### Honest scope against row 19

Row 19's remaining half is NOT claimed by this lane: the row also
wants an end-to-end lane that reconnects a revoked principal with a
stale checkpoint at the SYNC SERVING surface
(`crates/terrabase-sync-core` view generations) and observes the
typed refusal there. This lane proves the engine policy layer's state
machine; the sync-core serving seam (including its
`transition_authorization` removal path and checkpoint resume) is a
separate lane's scope.

## Effect on the gap sweep verdicts

- Row 15 (replica-set epoch change): the "no model checker exists"
  clause is now false. A bounded model checker exists and holds the
  three invariants within the recorded bounds, with negative
  controls. The row's full closure still requires the fault matrix
  and scenario lanes the row names, so the row remains `partial`
  overall — but for a different, smaller reason than before.
- Row 19 (permission revocation): the "removals precede generation
  advance" ordering is now model-checked at the engine policy layer,
  and the stale-checkpoint refusal is exhaustively interleaved. The
  end-to-end sync-serving reconnect lane remains open; the row stays
  `partial`.
- Standing constraint 4 of the gap sweep ("no model checker exists in
  this repository") is now false for membership and authorization
  generations; coordinated operations and causal compaction still
  have none and need their own lanes.

## Runs recorded

Focused checks executed on this host (2026-08-29, develop):

- `cargo test -p terrabase-cluster --test model_checker` — 3 passed.
- `cargo test -p terrabase-cluster --test membership_model` — 8
  passed (includes the checker self-tests compiled as a module).
- `cargo test -p terrabase-v2 --lib authorization` — 8 passed (4
  pre-existing `authorization::tests` + 3 model tests + unchanged
  others in scope).
- `cargo test -p terrabase-cluster` (full crate, the packet's focused
  gate) — recorded in the landing commit message.

No performance claim of any kind is made or implied by any number in
this file; all figures are correctness-exploration counts.
