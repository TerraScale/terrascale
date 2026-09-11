# TerraBase server clippy readiness packet

Captured 2026-09-11 after the fresh server-library run in
`/tmp/terrabase-server-clippy-current-20260911.log`.

## Cohort and gate

- Source HEAD: `412896c8e5c861e0146f0b567f84548e51b07949`.
- Command: `nice -n 19 CARGO_BUILD_JOBS=2 CARGO_TARGET_DIR=/tmp/terrabase-native-g1-target.lNjLFz cargo clippy -p terrabase-server --lib --locked --message-format=short -- -D warnings`.
- Result: status 101, 317 server diagnostics.
- No source files were changed while the gate ran. The only worktree changes
  are the external native-shutdown evidence/source edits.

## Bounded implementation briefs

### Production export mechanics (six diagnostics)

File: `crates/terrabase-server/src/production_export.rs`.

- Line 35: introduce a private type alias for the resolver function type. The
  alias must retain `Fn(&str) -> Result<NativeBackupInput, String> + Send +
  Sync`; this is a readability-only change and must not alter the admission
  or error boundary.
- Line 84: rewrite the single-pattern `match` as `if let`, preserving the
  existing `remove_dir_all` result/error behavior.
- Line 131: elide the explicit `'a` lifetimes in `existing_facts`; retain the
  returned `ExportFuture` lifetime and mutable writer borrow.
- Line 184: use captured variables in `format!`; preserve the exact remote
  object key (`prefix/database/job/path`).
- Line 326: add an `# Errors` section describing cursor-key/runtime rejection
  without exposing provider or key material.
- Line 333: use an explicit `()` pattern for the unit match. Do not change
  filesystem cleanup semantics.

Cheap verification: `cargo fmt --all --check`, then
`cargo test -p terrabase-server production_export --lib --locked`.

### Export runtime/store/API mechanics (eight low-risk diagnostics)

Files: `export_runtime.rs`, `export_store.rs`, `export_api.rs`, and one
separate lifetime-only item in `export_artifact_crypto.rs`.

- `export_runtime.rs:179` and `export_store.rs:20`: replace saturating
  `as_millis()` casts with checked conversion after an explicit upper bound;
  preserve the current behavior of returning the maximum representable wire
  timestamp on overflow and zero when system time is before the epoch.
- `export_store.rs:295`: retain the `u64` saturating subtraction and perform an
  explicit signed conversion compatible with the SQLite `i64` column. Confirm
  the chosen behavior for values above `i64::MAX`; do not use an unchecked
  cast that can wrap retention cutoffs.
- `export_api.rs:374` and `export_artifact_crypto.rs:277`: elide explicit
  lifetimes only; preserve trait-object future lifetimes and mutable borrows.
- `export_api.rs:838`: remove the unnecessary `&` passed to
  `status_from_state`; no status behavior changes.
- `export_api.rs:986`: document `download_with_format` errors, including
  unknown/incomplete jobs, invalid members, dependency failures, and
  verification failures.
- `export_api.rs:1219`: defer `too_many_lines` to a structural refactor; do
  not suppress it or make speculative extraction in this batch.

Cheap verification: `cargo fmt --all --check`, then
`cargo test -p terrabase-server export_api --lib --locked` and the existing
export runtime/store tests.

### Recovery admission numeric correctness (six diagnostics)

File: `crates/terrabase-server/src/native_recovery/admission.rs`.

- Lines 21, 69, 80, 86, 92: the SQLite schema and `MAX_RECORD_BYTES` are
  signed `i64`, while Rust lengths/limits are `usize`. Replace each cast with
  checked conversions and fail closed with `NativeRecoveryError::InvalidRequest`
  or `Unavailable` according to the surrounding operation. Never truncate a
  length or allow a negative database value to become a large `usize`.
- Line 52: collapse the nested conditional only after the conversion/error
  behavior is unchanged.

Required discriminating tests: existing `malformed_and_oversized_records_are_rejected`,
`pending_full_rejects_without_mutation`, `total_bound_prunes_oldest_terminal_only`,
and a targeted boundary test covering `i64::MAX`, negative lengths, and
`usize` values that cannot fit in `i64`. This batch needs correctness review;
do not apply clippy's cast suggestions blindly.

### Independent docs/style-only candidates

These are disjoint from the coordinated API/auth/runtime modules:

- `replication_frontier.rs:31,57`: add precise `# Errors` sections describing
  poisoned mutex, missing assignment, unrequired peer, and engine frontier
  failure. Preserve the minimum-acknowledgement invariant. Tests:
  `replication_frontier` module tests.
- `webhook_api/journal.rs:73`: replace `map(...).unwrap_or_else(...)` with
  `map_or_else(...)`; preserve uppercase parsed region and raw-lane fallback.
  Test the existing webhook journal module tests.
- `usage_closure_http.rs:3`: put backticks around `storage_intervals` in the
  module documentation. Leave the 127-line function for a separate
  structural review. Tests:
  `usage_closure_http::tests::timelines_require_complete_nonoverlapping_coverage_and_preserve_large_quantities`
  and `rejects_caller_totals_and_noncanonical_checksums`.
- `replication_peers/suspension.rs:2`: replace the wildcard import with the
  exact names suggested by clippy, verifying no symbol resolution or public
  API changes. Run its focused module tests.

## Explicit exclusions

Do not modify `api.rs`, `native_backup_runtime.rs`, `production_data_runtime.rs`,
`delegated_identity.rs`, `admitted_databases.rs`, or `runtime.rs` in these
batches. They are under active restore/auth/runtime coordination. Do not add
blanket lint allowances or refactor solely to satisfy argument/line-count
lints.

## Consolidated verification after worker batches

The source-matched test build passed:

```text
cargo test -p terrabase-server --lib --locked --no-run
```

Executable: `/tmp/terrabase-native-g1-target.lNjLFz/debug/deps/terrabase_server-b6b1a255b810086b`.
Twenty-seven exact selectors were executed against that executable. Twenty-five
passed. Two existing assertions failed:

- `export_api::tests::placement_and_encryption_are_fail_closed`: the expected
  `PlacementMismatch` assertion failed at `export_api.rs:2879`.
- `usage_closure_http::tests::duplicate_billing_keys_and_foreign_regions_are_rejected`:
  observed HTTP 503, expected 404, at `usage_closure_http.rs:368`.

The post-batch clippy command was run once with `-D warnings` and finished
status 101. Diagnostics fell from 317 to 293. All released worker diagnostics
in `production_export.rs`, `export_runtime.rs`, `export_store.rs`,
`export_artifact_crypto.rs`, `replication_frontier.rs`,
`replication_peers/suspension.rs`, and `webhook_api/journal.rs` are clear.
The only remaining diagnostics in the released cohort are intentionally
deferred structural items:

- `export_api.rs:1224`: `too_many_lines` (101/100).
- `native_recovery/admission.rs:68`: collapsible `if`.
- `usage_closure_http.rs:114`: `too_many_lines` (127/100).

The remaining 290 diagnostics are in modules excluded by the packet or in
other pre-existing server modules. Logs:
`/tmp/terrabase-server-test-build-current-2-20260911.log`,
`/tmp/terrabase-server-exact-*.log`, and
`/tmp/terrabase-server-clippy-after-batches-20260911.log`.
