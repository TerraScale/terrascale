# Terrabase lifecycle-control clippy handoff

Captured 2026-09-11 from the approved Slice C packet
(`terrabase-server-clippy-next-current.md`).

## Bounded source changes

Changed only `crates/terrabase-server/src/production_lifecycle_control.rs`:

- Added redacted `# Errors` sections to the requested lifecycle/control
  `Result` APIs and the `LifecycleAuthority::verify` contract.
- Moved the two local `use` declarations in `TbepLifecycleControl::restore_to_target`
  before executable statements, preserving names and control flow.
- Replaced unit `|_|` closures with `|()|` in lifecycle receipt mappings,
  preserving all receipt and fencing transitions.
- Left `too_many_lines` at `invoke` unchanged; no structural refactor or lint
  suppression was made.

## Source identity

Approved cohort HEAD: `7f576d8a8af3967e639ed7325a06c39c15ef87a2`.

Post-edit SHA-256:

```text
7272366abfaede82596e5342d39277fcb811b3e458559e783ef5d2d5ddbb7909  crates/terrabase-server/src/production_lifecycle_control.rs
```

## Validation and build-owner handoff

`rustfmt --edition 2024 --check crates/terrabase-server/src/production_lifecycle_control.rs`
passed. `git diff --check` passed. No Cargo build, clippy, or tests were run
in this worker lane.

Submit to the sole `terrabase_clippy_batch` owner for compile/clippy and these
focused lifecycle selectors (discover exact binary names before execution):

- lifecycle admission paths
- pause/resume and reactivation paths
- cancellation/retry paths
- `restore_stats_preserves_verified_three_region_placement_and_identity`
- `provider_purge_success_is_applied_and_replay_remains_applied`

No public API, concrete error type, receipt, or fencing semantics were changed.
