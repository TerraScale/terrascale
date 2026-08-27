# GAP-R02 tombstone retention receipts (ticket #79)

Lane: focused in-crate and public-construction runs in this worktree
(`fleet/gapr02-2`). No exclusive recovery-lane run: GAP-R02 holds no
exclusive-lane flag and this packet changes no recovery, durability, or
storage-format path — the declared history bound is a seam the storage
compaction path (GAP-S1, #81) will drive in production, and no engine
component gives up journal history today. Recovery-lane restart/restore
receipts for the watermark remain owed to that lane, as they were before
this packet.

Scope of the claims below: **implemented, focused-test-proved, lane-scoped
only.** No production, multi-region qualification, durability, or
performance claim is made. Row dispositions stay with WS-PARITY; the
proposals are recorded in `docs/parity/gapr02-ws-parity-proposals.md`.

## Decision record

`docs/replication/tombstone-retention-watermark.md` (proposed for parent
ratification): the retention invariant and its bounds (D1), the composition
with `servable_history_start` including extraction-before-discard,
durability-before-discard, and window-must-fully-exist (D2.1–D2.4), the
three consumer classes and who records their floors (D3), and the
below-bound serving rule for members, backup chains, and smuggler exports
(D4), with four rejected alternatives.

## Contract receipts (`src/replication/retention_tests.rs`, 20 tests)

| Test | Fact proved |
| --- | --- |
| `the_floor_starts_at_zero_and_only_rises` | retention starts at zero, the floor is monotone, a refused candidate does not move it, refusals are observable |
| `the_floor_never_advances_past_the_slowest_consumer` | `ConsumerBelowFloor` names the binding consumer; the slowest floor is the ceiling |
| `a_consumer_that_has_consumed_nothing_needs_all_of_the_evidence` | an unknown (`None`) consumer floor blocks every advance above zero |
| `the_floor_cap_retains_evidence_unconditionally_above_it` | `FloorExceededCap`: the cap is a lower bound on retention |
| `forgetting_a_consumer_releases_its_floor_but_never_lowers_a_need` | dropping a consumer releases only its own constraint |
| `a_consumer_floor_never_regresses` | a regressing observation is refused and the recorded floor stands |
| `advancing_the_floor_discards_only_the_evidence_below_it` | advance discards exactly the below-floor facts, disclosed by count; serving is per tenant |
| `one_key_keeps_only_its_newest_evidence` | a subsumed delete of the same key carries no additional fact |
| `a_history_declaration_is_monotone_and_requires_durable_evidence` | `EvidenceNotDurable` and `BoundRegression`; a refused declaration does not move the bound |
| `a_history_declaration_names_the_consumers_below_the_bound` | the declaration discloses which consumers sit below the bound |
| `a_declared_bound_may_not_exceed_the_journals_durable_head` | decision D2.4 at the contract level: `BoundBeyondWindow` for a bound past the handed journal head, recorded in the refusal log; a window ending at the head declares cleanly |
| `holding_discloses_the_facts_dropped_below_the_floor` | `hold` returns a `RetentionHold` disclosing both counts — below-floor drops are counted, never silent (the disclosure discipline of `advance_floor`/declare) |
| `a_history_declaration_whose_window_cannot_be_extracted_fails_closed` | a member-driven declaration whose journal window cannot be read is refused (`DeclareBoundError::Engine`), the bound does not move, and previously extracted evidence stays held — an unreadable window is never treated as an empty one |
| `a_member_refuses_a_declared_bound_past_its_journals_durable_head` | the member's extraction refuses `head+1` and `head+5` with the recorded `BoundBeyondWindow` refusal instead of silently truncating the window at the journal's end; the bound does not move |
| `an_over_range_bound_refuses_instead_of_black_holing_the_window` | the warden's black-hole repro as a regression test: the `head+5` declaration is refused, so a publication landing inside `(head, bound)` is still served by the journal and the follower converges instead of silently diverging behind a caught-up report |
| `an_offline_member_converges_across_a_declared_history_boundary` | the acceptance case: a follower below the declared bound receives the retained evidence, does not resurrect the deleted document, and resumes incremental service at the bound |
| `a_put_below_the_declared_bound_still_fails_closed` | a missed put below the bound is the typed `GapUnservable`, never a silent skip, and no evidence is served |
| `the_watermark_holds_history_for_a_seated_but_silent_member` | topology seeding holds the watermark for a member that has never requested; a caught-up member releases it |
| `a_backup_chain_anchor_holds_the_watermark_until_the_chain_moves` | a chain's base boundary is a consumer floor the watermark respects |
| `delivered_evidence_applies_once_per_operation_identity` | a duplicated evidence frame resolves through the durable operation ledger: one apply, one duplicate, nothing deleted twice |

## Backup-chain receipts (`src/backup/tests.rs`, 5 tests)

| Test | Fact proved |
| --- | --- |
| `an_incremental_below_the_history_bound_restores_without_resurrecting` | a chain whose base falls below the declared bound archives the retained delete, verifies, and restores with the deleted document absent (`chain_depth` 2, one bound delete, two surviving documents) |
| `an_incremental_below_the_bound_fails_closed_when_the_window_holds_a_missed_put` | `BackupError::SourceUnavailable` when the discarded window holds a put the chain missed |
| `an_incremental_below_the_bound_fails_closed_when_the_floor_is_above_the_base` | `BackupError::SourceUnavailable` when the watermark already discarded the evidence below the chain's base |
| `an_incremental_run_records_its_chain_anchor_on_the_watermark` | the run records `RetentionConsumer::BackupChain` at the base recovery point's boundary, and that anchor refuses a floor advance past it |
| `an_incremental_refuses_a_declared_bound_past_the_journals_durable_head` | decision D2.4 at the run gate: a plan whose watermark declares a bound (at `head+1` and `head+5`) this source's journal cannot cover — declared honestly against another engine's journal — fails closed with `SourceUnavailable` instead of serving from a phantom bound, with the refusal recorded on the watermark's log |

## Migration/export receipts

- `src/backup/smuggler/tests.rs` ::
  `a_tombstone_export_carries_the_retained_evidence_below_the_bound` — a
  tombstone-selected export with the watermark emits the retained fact in
  place of the journal record (one tombstone, identical artifact identity,
  because the retained fact encodes the same logical delete), and after the
  floor advances past the fact the artifact carries zero tombstones rather
  than silently re-reading the journal below the bound.
- The `RetentionConsumer::Migration` class and its floor semantics are
  proved by the contract tests above; an incremental migration driver that
  registers one does not exist yet (recorded in the decision record's
  limits).

## Public-seam receipts (`tests/recovery/tombstone_retention_public.rs`, 2 tests)

- `the_watermark_contract_is_public_and_fail_closed` — the watermark types
  are reachable at `terrabase_v2::replication::*`; consumer observation,
  the durability gate, the shared-handle identity, and the floor cap are
  observable by an external caller.
- `a_backup_plan_carries_the_shared_watermark_on_the_public_seam` —
  `BackupPlan::with_retention` composes with `run_backup`,
  `verify_latest_chain`, and `RestorePlan::restore_latest` end to end on a
  real engine and destination, and the rendered receipt carries no private
  host path.

`StorageSequence` and `TenantScope` have no public constructor by design, so
the data-bearing below-bound cases with nonzero sequences live in the
in-crate suites listed above.

## Red verification (mutation checks)

The two fail-closed gates and the serving path were verified red by
mutation, then restored and re-run green:

1. `src/backup/mod.rs` — the below-bound provability check forced to
   `true`: both
   `an_incremental_below_the_bound_fails_closed_when_the_window_holds_a_missed_put`
   and
   `an_incremental_below_the_bound_fails_closed_when_the_floor_is_above_the_base`
   fail (2 failed / 0 passed). Restored; both pass.
2. `src/replication/member.rs` — the evidence-serving branch disabled
   (`if false && covered`):
   `an_offline_member_converges_across_a_declared_history_boundary` fails.
   Restored; passes.
3. `src/replication/member.rs` — the history-bound declaration's window
   extraction reverted to collapsing an engine failure to an empty window
   (`.unwrap_or_default()`): the declaration proceeded and moved the bound,
   and `a_history_declaration_whose_window_cannot_be_extracted_fails_closed`
   failed on exactly that. Restored; passes.

All three mutations were re-applied and re-observed red by the
continuation pass (2026-08-27), then restored and re-run green.

## Repair red verification (warden loop 1, 2026-08-27)

The warden's MAJOR (an over-range declared bound was accepted and silently
black-holed every publication in `(journal head, bound)`) was repaired
red-then-green — the behavioral reds below were observed on the pre-repair
tree before any fix landed:

1. `a_member_refuses_a_declared_bound_past_its_journals_durable_head` —
   the `head+1` declaration was accepted
   (`RetentionCompaction { bound: 3 }` against a head of 2); the refusal
   assertions failed. Fixed: refused with the recorded
   `BoundBeyondWindow`; green.
2. `an_over_range_bound_refuses_instead_of_black_holing_the_window` — the
   warden's scenario: the `head+5` declaration was accepted
   (`RetentionCompaction { bound: 7 }`), which on the pre-repair tree
   jumped the follower's cursor past the head and silently diverged it.
   Fixed: refused, and the in-window publication is served by the journal;
   green.
3. `an_incremental_refuses_a_declared_bound_past_the_journals_durable_head`
   — both runs (bounds `head+1` and `head+5` over this source, declared
   honestly against a foreign journal) succeeded and appended links to the
   chain. Fixed: `BackupError::SourceUnavailable`, refusal recorded; green.
4. The D2.4 contract test and the `hold` disclosure test were red at the
   compile level (no `journal_head` parameter, no `BoundBeyondWindow`
   variant, `hold` returning a bare `usize`), then green after the fix.

The compile-level red of the whole packet is the pre-packet tree, where
none of the watermark surface, the `DeletionEvidenceBatch` frame, or the
retention-aware plan exists.

## Runs

- `cargo +1.97.1 test --offline --locked --lib` — 553 passed, 0 failed
  (26 of them this packet's: 20 retention, 5 backup, 1 smuggler).
- `cargo +1.97.1 test --offline --locked --test recovery` — 19 passed,
  0 failed (2 of them this packet's).
- Focused filters used during development: `retention_tests`,
  `backup::tests`, `a_tombstone_export_carries`,
  `an_incremental_below_the_bound`, `an_offline_member_converges`,
  `delivered_evidence_applies`.
- Counts re-derived by the continuation pass (2026-08-27), which also
  repaired the member history-bound declaration to refuse — not proceed
  with an empty extraction — when the journal window cannot be read, and
  added the contract test above for that refusal; re-derived again by the
  warden repair pass (2026-08-27), which added the four D2.4 refusals and
  the hold-disclosure test.
- Full-suite and `scripts/check.sh` outcomes are recorded in the packet's
  finish protocol, not here.

## Limits carried

- The watermark state is process-local; its inputs (member cursors, sealed
  chain anchors) are durable, and the retained evidence is the journal's
  while the journal covers it. A durable retained-evidence store is a
  recorded limit owned by GAP-S1, which is why `declare_history_bound`
  fails closed without the durability assertion.
- A member below the bound that missed a put still requires a full state
  transfer (PB-5024); this packet does not build one.
- No production acquisition seam drives the watermark yet; the member's
  transitions are typed operations with no host surface (GAP-R3's typed
  observation operations are the natural exposure point).
- Topology seeding (D3) has no production caller — it is an explicit,
  tested operation the declaring seam must drive; and `BackupChain`
  consumers are never forgotten when a chain is pruned, because the prune
  path (BK-04's `enforce_retention`) holds no watermark handle. Both
  residuals are recorded with their owners in the decision record's
  Limits.
