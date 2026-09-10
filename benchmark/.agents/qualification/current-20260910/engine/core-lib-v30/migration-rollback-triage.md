# Migration rollback qualification triage

## Finding: fixture contract drift (confirmed)

Severity: qualification-blocking test failure; no production rollback defect established.

Artifact: `engine/core-lib-v30/test.log`, failure at
`src/families/migrations/tests.rs:1794` in
`families::migrations::tests::stopped_partial_migration_rolls_back_across_two_reopens`.

Source path and schedule:

1. The test seeds 1025 documents, starts a reversible rename migration, applies
   1024 documents, and leaves the last document target-invalid. The first
   reopen calls `rollback`, which commits a `RollingBack` record with the page
   cursor (`migrations::rollback`, `mod.rs:837-896`). The test observes
   `PageRestored { restored: 1024 }` and `pages: 2`, then drops the engine.
2. On the second reopen, the test helper creates a *new* operation identity
   (`migrations::tests::commit`, `tests.rs:107-118`) and calls
   `stage_rollback` (`migrations::stage_rollback`, `mod.rs:765-788`).
3. The durable record is already `RecordState::RollingBack`; that branch is
   explicitly `return Ok(())` (`mod.rs:780-783`). Therefore no document,
   private metadata, schema envelope, or other intent is staged.
4. The helper nevertheless requires `transaction.commit(Synced)` to return
   `Committed` (`tests.rs:1794-1799`). Empty intents are rejected as
   `CommitError::Validation`: the engine has a direct regression assertion for
   an empty write set (`engine/tests.rs:918-922`), and the storage admission
   guard rejects an intent with empty writes/metadata/schema envelopes
   (`storage/sqlite/mod.rs:2586-2596`, `stage_intent`).

This is a deterministic counterexample schedule, not a race: after the first
rollback page commits and the process restarts, an idempotent staging call is a
no-op, while the fixture asserts that a no-op transaction produces a receipt.
The helper's “Admission replay” comment is also imprecise: `unique_operation`
creates a fresh operation identity; this is not durable operation replay.

## Production rollback assessment

No confirmed production defect in the rollback owner/caller path. The first
rollback page durably updates the cursor before its document puts
(`migrations::rollback`, `mod.rs:884-905`), and the final page clears the
record atomically with restoring puts. On the second reopen, calling
`rollback` directly reads the existing `RollingBack` cursor and should scan
only the remaining document; the expected `Complete { restored: 0 }` assertion
is consistent with the already-restored first page. `stage_rollback` is
documented and implemented as idempotent for this state, so changing it to
manufacture a receipt would alter the no-op commit contract rather than repair
rollback ownership.

## Discriminating regression

Keep both reopens and all original-schema/data assertions. After the second
reopen, either invoke `stage_rollback` without requiring a commit receipt, or
explicitly accept `CommitOutcome::Rejected(CommitError::Validation)` for the
empty idempotent transaction, then call `rollback` and retain the
`Complete { restored: 0 }` assertion. A production rollback regression would
instead be indicated by a reset cursor, `PageRestored { restored: 1024 }` on
the second rollback, loss of the `a` values, or failure to return to schema v1;
none is implied by this failure log or the cited source.

## Unresolved hypotheses

The qualification log does not include the rejected value because the fixture
uses `matches!(..., CommitOutcome::Committed(_))`. If a targeted diagnostic
prints the outcome and it is not `Rejected(CommitError::Validation)`, revisit
the engine/storage path. Based on the frozen source, any other outcome would
be unexpected and is not a confirmed finding here.
