# TerraBase server clippy next-batch packet

Captured after the post-worker server clippy run in
`/tmp/terrabase-server-clippy-after-batches-20260911.log`.

## Cohort

- HEAD: `7f576d8a8af3967e639ed7325a06c39c15ef87a2`.
- The run reported 293 diagnostics after the released export/recovery/small-
  module batches.
- Active ownership exclusions: `api.rs`, `native_backup_runtime.rs`,
  `native_recovery.rs`, new backup catalog files, and all previously released
  export/admission/small-module files. No source edits are included here.

## Slice A: import runtime safe mechanics

File: `crates/terrabase-server/src/api_imports.rs`.

Safe sub-batch:

- Lines 42 and 860: replace the bounded `u128` millisecond casts with the same
  explicit checked/saturating helper used by export runtime. Preserve zero for
  pre-epoch time and `u64::MAX` saturation.
- Line 59: use `std::sync::Mutex::default()`; no state change.
- Lines 154, 211, and 600: use explicit `()` error patterns, preserving the
  existing redacted error mapping.
- Line 487: use `map_or_else` while retaining fresh operation IDs and cloned
  replay IDs exactly.
- Line 515: merge identical commit-outcome arms only; preserve `Rejected` vs
  `Unknown` state transitions.
- Lines 729 and 733: use `let ... else` with the exact existing HTTP refusal
  responses; preserve fail-closed key lease/upload admission behavior.
- Lines 836 and 863: collapse nested conditions only after checking that
  cancellation, cleanup, and durable cursor ordering are unchanged.

Do not change in this slice: unread error payload fields (271-275),
`execute_import`/`commit_batch` argument or line-count lints, or large-error
layout. Those require an error-contract/algorithm decision.

Focused selectors: inspect the import tests with the binary `--list`; run
only exact names covering upload bounds, cancellation, replay, and durable
cursor behavior. A compile-only check is required before selector execution.

## Slice B: operation discovery mechanics

File: `crates/terrabase-server/src/api_operations.rs`.

- Line 32: merge only identical wildcard match arms; preserve the fallback
  operation state mapping.
- Lines 280, 297, 309, and 326: apply the suggested conditional/unit-pattern
  rewrites without changing authorization or operation visibility.
- Lines 392, 398, 511, and 516: remove needless borrows only; preserve the
  borrowed state lifetime.
- Lines 432, 466, and 501: use `clone_from` only where the destination already
  owns the value; preserve returned operation/database identifiers.

Leave line 367 (`too_many_lines`) for a cohesive operation-query refactor;
do not suppress it. Focused tests must cover operation list/get and ownership
filtering, discovered from `--list` and run by exact name.

## Slice C: production lifecycle documentation/unit mechanics

File: `crates/terrabase-server/src/production_lifecycle_control.rs`.

- Add `# Errors` sections at lines 172, 946, 967, 985, 1033, 1046, 1059,
  1072, 1085, and 1098, describing redacted lifecycle/control failures only.
- Move local `use`/type declarations currently at lines 192-193 above the
  first executable statements. This is scope organization only; preserve
  all type names and control flow.
- Replace unit `_|` patterns at lines 282, 302, and 874 with `()` patterns.

Leave line 529 (`too_many_lines`) for a lifecycle state-machine refactor.
Focused tests should cover admission, pause/resume, and cancellation paths;
do not run the broad server suite.

## Slice D: replication peer ownership mechanics

File: `crates/terrabase-server/src/replication_peers.rs`.

- Line 182 (`retention` unread): determine whether the field is intended to
  participate in the active frontier owner. Do not delete or allow it without
  tracing construction and retention advancement; this is a design decision.
- Lines 583, 596, and 686: reference the `DatabaseOwner`/`BTreeSet`/name values
  as suggested only if ownership and route-scope semantics remain unchanged.
- Lines 719, 886, and 1340 are line-count diagnostics and require separate
  lifecycle/peer algorithm review; do not split speculatively.

This slice is not mechanical until line 182 is resolved. Focused replication
peer suspension/frontier tests are the minimum evidence.

## Slice E: usage admission documentation

File: `crates/terrabase-server/src/usage_admission.rs`.

- Add `# Errors` sections at lines 66, 215, 231, 344, and 555 based on the
  existing error variants and fail-closed admission behavior.
- Put backticks around the protocol term at line 77.

This is documentation-only and must not alter billing registration,
allocation signature validation, or durable journal behavior. Run the exact
usage admission tests listed by the matching test executable.

## Deferred/unsafe without decisions

- `api_imports.rs:271-275`: error payloads are currently carried for debugging
  but not read; changing variants can alter diagnostics and error redaction.
- `api_imports.rs:289,473,563,581`: line-count, argument-count, and large-error
  lints require cohesive API/error design, not mechanical edits.
- `api_operations.rs:367`, `production_lifecycle_control.rs:529`, and
  `replication_peers.rs:719,886,1340`: structural refactors require algorithm
  review.
- All coordinated API, native recovery, backup catalog, and runtime modules
  remain excluded.
