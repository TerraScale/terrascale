# IX-03 recovery drill receipt: crash during auto-index replacement

Secret-safe recovery evidence for TerraBaseV2 ticket IX-03 (issue #39,
charter PD-2026-08-26-09, ADR 0010 D-4). No secret, credential, customer
data, backup content, or private host path appears here; the drill runs on
throwaway temporary directories under the system temp dir.

- Ticket: IX-03 (issue #39)
- Branch: `fleet/ix03-2`, base `develop` @ `ab64110`
- Module family: `src/query/optimizer/**` (lifecycle regions),
  `src/index/**` (catalog lifecycle regions)
- Drill test: `src/query/optimizer/lifecycle_tests.rs::
  a_crash_during_replacement_recovers_to_the_same_correct_state`
- Receipt date: 2026-08-26

## Scope of this receipt

This receipt covers exactly the ticket's second acceptance criterion —
"Replacement never interrupts in-flight queries or weakens durability;
crash during replacement recovers correctly (recovery-lane evidence)" —
for the auto-index lifecycle at the W3-2 query/index seam. It is a
focused recovery drill inside the IX-03 packet, not the exclusive
recovery lane's qualification: IX-03 changes no storage format, WAL,
checkpoint, or durability contract (the index catalog is in-memory
derived state over storage publication order, unchanged by this packet),
so the exclusive-lane gate of `AGENTS.md` is not triggered. The
exclusive recovery lane's drills remain BK-02's receipts.

## Scenario

Kill the engine process mid-replacement: the covering auto index
(`Auto/users/ByAgeAndName`) has been created and the superseded index
(`Auto/users/ByAge`) is still live, draining behind the replacement's
coverage parity and the supersede drain. In V1 terms this is the window
inside `CleanupSupersededAutoIndexes` (AX-17) between the wider index's
creation and the superseded index's deletion.

## Exact source and data identity

- Source: worktree `fleet/ix03-2` (this packet's tree; `Cargo.lock`
  unchanged from base `develop` @ `ab64110`).
- Workload: one tenant (`tenant-a`), collection `users`, three documents
  committed with `LocalDurability::Synced` before the crash (ages
  5, 5, 9; each carries a string `name`), then two dynamic queries:
  `age = 5` (creates the narrow auto index) and
  `age = 5 AND name = 3` (widens the mapping and supersedes the narrow
  index, AX-16).
- Crash point: the `Engine` value is dropped immediately after the
  widening query — before the drain can elapse, with both indexes live
  (asserted: two registered definitions at the kill point).

## What the drill asserts after reopening the same durable location

1. **Answers are unchanged.** The first post-recovery `age = 5` query
   returns the same two document identities as every pre-crash execution
   of the same query.
2. **No half-replaced index ever serves.** The reopened engine's catalog
   is empty (index state is derived, never persisted), so nothing
   stale can be selected; the deterministic `Auto/` naming re-creates
   the narrow index from the identical query shape.
3. **The lifecycle re-derives and converges.** One non-matching document
   commit after reopen gives the fresh catalog its first publication
   event; the wider query re-derives the covering identity, re-schedules
   the supersede, and after the drain the tenant owns exactly the
   covering index (`Auto/users/ByAgeAndName`, live) with the superseded
   index retired — the same converged state as before the crash, reached
   through the same deterministic rules.
4. **Durability is untouched.** Index retirement never removes or alters
   durable document state; every query answer before, during, and after
   the replacement window comes from the durable snapshot scan, and the
   replacement's build goes through the catalog's compare-and-publish
   rule (`RebuildOutcome::RejectedAsStale` on non-advancing builds),
   which this packet does not modify.

## Result

Pass (focused in-crate run, `cargo test --offline --locked --lib
optimizer::lifecycle_tests`, this tree): the drill test and the other 13
lifecycle tests pass; the full workspace suite passes (366 lib tests, 0
failures, plus all integration targets).

## Limits

- Single-process kill simulation by dropping the `Engine` (the engine's
  own durability model: a dropped engine is a closed process). No
  SIGKILL-mid-write, torn-file, or multi-member scenario is exercised —
  those belong to the exclusive recovery lane and to storage/WAL-owned
  receipts, whose contracts this packet does not touch.
- The drill pins behavior, not performance: no timing claim is made or
  implied (PD-2026-08-26-03; IX-04 owns receipts).
- Lifecycle state itself does not survive a restart by design (ADR 0010
  D-4 derives it from catalog truth and query shapes); the receipt
  records that re-derivation converges, not that state is durable.
