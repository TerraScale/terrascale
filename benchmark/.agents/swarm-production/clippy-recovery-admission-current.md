# Recovery admission numeric correctness handoff

Source basis: `412896c8e5c861e0146f0b567f84548e51b07949` plus this bounded
worktree change. Updated source SHA-256:
`9b719ec45829417908f9269f482122ff0be0700becadc66330013fc9afd5ba4c`

## Before / after

- Proposed JSON size now converts `MAX_RECORD_BYTES` to `usize` with
  `usize::try_from`; an unrepresentable bound returns `Unavailable` before
  opening a transaction.
- Pending and retained `usize` limits now convert to signed SQLite-compatible
  `i64` with `i64::try_from`; an unrepresentable request returns
  `InvalidRequest` before mutation.
- Persisted `length(record)` values are checked with a signed-to-`usize`
  conversion and the checked maximum. Negative or oversized durable values
  return `VerificationFailed`, preserving the durable-state error contract.
- SQLite count results reject negative values as `Unavailable`; pruning and
  insert/update mutation still occur only after all relevant bound checks.

## Focused evidence

- Existing `malformed_and_oversized_records_are_rejected`,
  `pending_full_rejects_without_mutation`, and
  `total_bound_prunes_oldest_terminal_only` remain in place.
- Added `persisted_size_and_limits_reject_signed_or_unrepresentable_values`
  covering `-1`, `i64::MAX`, the exact maximum, and the target's
  `usize::MAX -> i64` representability boundary.
- `rustfmt --check crates/terrabase-server/src/native_recovery/admission.rs`
  passed; `git diff --check` passed.
- Cargo builds/tests were intentionally not run; batch owner
  `terrabase_clippy_batch` owns those checks.

