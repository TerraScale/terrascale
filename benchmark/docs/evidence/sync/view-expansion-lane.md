# View-generation expansion path — native result-view state machine (gap row 18)

Qualification gap sweep row 18 (`docs/evidence/qualification-gap-sweep.md`):
"subscription expansion — missing. `crates/terrabase-sync-core/src/native/mod.rs`
implements `transition_authorization` (removal path only). No code path creates
a new view generation for a widened subscription, and no test asserts newly
visible current data whose mutations precede the old checkpoint."

Charter anchor: `GOAL.md` "View checkpoints" — *"A subscription expansion,
policy change, role change, or newly authorized relationship creates a new view
generation. TerraBase computes an authorized diff when bounded and safe;
otherwise it sends a new snapshot. It never skips newly visible records because
an older cursor scanned past their mutations. Permission loss emits removals
for every item no longer visible before the new generation advances."*

Status after this lane: **implemented (state machine, focused tests)** — see
"Honest limits" for the boundary of that claim.

## What was implemented

All in `crates/terrabase-sync-core/src/native/mod.rs` (additive; no existing
signature changed):

| Item | Line | Role |
| --- | --- | --- |
| `ScopedItem` | 263 | One record newly in scope: current authorized projection, insert position, and the causal position of its latest mutation. |
| `ExpansionRequest` | 283 | Expansion inputs: next view generation, authorization generation, newly visible records, concurrently revoked identities, the old checkpoint's scan frontier, retention/expiry bounds. |
| `GenerationAdvance` | 316 | `Diff(ResultDelta)` (bounded) or `Reset(Snapshot)` (unbounded fallback). |
| `ExpansionDelivery` | 330 | Wire order: removals delta in the **old** generation first, then the advance. |
| `ViewState::expand_subscription` | 504 | Producer side: computes the authorized diff from **current values**, decides bounded vs reset, routes delivery through the coalescing queue, advances the authoritative state. |
| `ViewState::apply_expansion` | 652 | Consumer side: applies removals via the ordinary delta path, then advances the generation as diff or snapshot, validating digest/revision/generation continuity. |
| `ViewState::apply_generation_advance` | 694 | Applies a diff that strictly advances the view generation, continuing from the current revision. |
| `DeliveryQueue::replace_with_snapshot` | 1115 | The terminal coalesced form (no delta to attempt first) for unbounded expansions. |

Design decisions, each tied to the charter sentence it serves:

- **Never skips.** The diff is built from the records' current authorized
  values (`ScopedItem::item`), never from a suffix of the mutation log behind
  the old checkpoint. `expand_subscription` additionally guards, unconditionally
  for every scoped record — not only post-frontier ones — that the record is
  present in the final result, so a future position filter cannot reintroduce
  the skip class as a silent behavior. The skip-prone class (mutations at or
  before `ExpansionRequest::scan_frontier`) is supplied by tests and asserted
  visible with its current value.
- **Diff when bounded, snapshot otherwise.** Bounded means both the removal
  delta and the advance delta fit `Limits::max_operations`. The advance delta
  is pushed through the existing `DeliveryQueue` coalescing machinery
  (`push(delta, snapshot)`), so a full backlog also flips the delivered shape
  to the queue's reset snapshot — the same `CoalescedReset` path slow-consumer
  shedding uses. An unbounded diff is never truncated into a guessed partial
  patch.
- **Removals before the generation advances.** Revoked identities are emitted
  as a `ResultDelta` **in the old view generation** that precedes the advance;
  a consumer still in the old generation applies it through the ordinary
  `apply_delta` path and observes the removal before any generation change.
  When the diff is unbounded, the replacement snapshot carries the removals
  atomically (honest limit noted below).
- **Producer/consumer agreement.** The authoritative state advances to the
  final authorized result regardless of which delivery shape the queue chose;
  every test asserts producer and consumer converge on identical items,
  revision, digest, and generation, and that the pre-expansion checkpoint is
  refused (`ResetRequired(CheckpointNotCurrent)`), never resumed.

## Commands run and results

- `cargo test -p terrabase-sync-core` — all targets pass
  (lib 10, contract 3, core 15, v2 protocol 12, vectors 1+1 ignored by
  design, view_expansion 5, doc-tests 0).
- `cargo clippy -p terrabase-sync-core --all-targets --locked -- -D warnings`
  — clean (matches the CI gate's flag set, scoped to this crate).
- `rustfmt --edition 2024 --check` on the two touched Rust files — clean.

## Test inventory

`crates/terrabase-sync-core/tests/view_expansion.rs` (integration, public API
only):

| Test | Charter sentence exercised |
| --- | --- |
| `expansion_makes_pre_checkpoint_mutations_visible` | A record whose latest mutation is at causal position 5, far before the frontier-100 checkpoint, becomes visible with its **current** value in the new generation; stale checkpoint refused. |
| `unbounded_expansion_resets_and_never_skips` | Five newly visible records against a three-operation budget: the advance is a `Reset` snapshot carrying all seven records; the coalescing queue holds that snapshot as its sole delivery; the consumer loses nothing. |
| `coalescing_queue_replaces_a_bounded_diff_with_a_snapshot` | A bounded diff whose queue backlog is full is delivered as the queue's reset snapshot instead — the reused coalescing machinery — and the record is still not skipped. |
| `expansion_with_concurrent_revocation_removes_before_advancing` | Widening plus revocation: removals delta names the **old** generation and is observed by a consumer still in it before the advance; the revoked record does not return; a replayed expansion is a typed refusal. |
| `expansion_property_scenarios` | 800 deterministic xorshift64\* scenarios over random result shapes, scopes, revocations (including revoke-then-reinclude), positions, mutation positions, and operation bounds: producer/consumer exact convergence, delivery shape matches bounded-ness, removals ride the old generation, every scoped record present with its current value, revocations stick. Fails if the pre-frontier skip class was never exercised. |

Inline unit test `expansion_input_validation`
(`crates/terrabase-sync-core/src/native/mod.rs:1566`): unbootstrapped view,
non-advancing view/authorization generations, duplicate scoped identities,
and malformed insert positions are typed errors that leave the view unchanged.

## Honest limits

- **State machine, not the serving surface.** Everything above lives in the
  transport-independent `native` module. No host, TBEP, SSE, or SDK path
  delivers a subscription expansion end to end; the serving-side receipt the
  gap row ultimately wants (a live client observing a widened scope) remains
  open, as does the row-19 end-to-end revocation reconnect lane.
- **The scan frontier is declared metadata.** The model has no real causal
  cursor over a mutation log; `ExpansionRequest::scan_frontier` and
  `ScopedItem::last_mutation_position` let callers and tests name the skip
  class precisely. The never-skip property is enforced structurally
  (current-value inputs plus the unconditional coverage guard) and by the
  tests above — it is not derived from replaying a real log.
- **Unbounded path and removals.** When the authorized diff is unbounded, the
  concurrently revoked identities are carried by the replacement snapshot
  atomically; there is no observable pre-advance removal step in that shape.
  The "removals before the generation advances" ordering is observable and
  tested on the bounded path, which is the only path where a stream of
  removals exists to order.
- **Revision accounting choice.** A bounded expansion with removals advances
  the revision twice (removals r→r+1 in the old generation, advance r+1→r+2 in
  the new one); without removals it advances once. Revisions are delivery
  facts, not causal positions, and never move backwards.
- **Property harness.** The scenario loop is deterministic
  (xorshift64\*, seeded per iteration), not a fuzzing framework; no
  property-testing dependency could be added because the crate manifest is
  outside this packet's owned paths.
- **No performance claim.** Correctness evidence only; no timing, throughput,
  or byte-count measurement was taken or is implied.
