# Obsolete cleanup audit, 2026-09-08

The current task table (`.agents/benchmark-20260907/task-table.md`) contains
benchmark tasks only; it does not authorize another product cleanup series.
The previously confirmed dead served benchmark path was removed in the prior
series (`src/bin/terrabase-bench-served.rs`, its Cargo target, the
`run_served_boundary_cli` entrypoint, and `bench-tools` cfg wiring).

## Remaining candidates traced

The external backup path is not independently removable. The public API still
stores `BackupStreams` and `TimeTravelCache` in
`crates/terrabase-server/src/api.rs:606-614`; local runtime composition creates
them at `crates/terrabase-server/src/runtime.rs:1502-1507`; PITR reads call the
time-travel cache at `crates/terrabase-server/src/api.rs:3720-3724`. The native
backup runtime is a replacement dependency, but ADR 0046 and
`docs/development/cleanup-verification-2026-09-05.md:15-19` require restore-chain,
encryption, residency, multi-database resource, and comparison gates before
deleting this active adapter.

`src/query/optimizer/auto_index/legacy.rs` is active code despite its filename:
`src/query/optimizer/auto_index/mod.rs:11-13` reexports it, query observation
construction uses its `PredicateKind`, `Predicate`, `Observation`, and
`Template` in `src/query/mod.rs:134-179`, and engine lifecycle state stores its
workload sketch and lifecycle types. No deletion is justified.

The duplicate MessagePack vectors are intentional generated compatibility
copies. `scripts/validate-release-contracts.sh:264-267` requires
`contracts/msgpack/{message,hostile}-vectors.json` to byte-match their
`contracts/conformance/msgpack/` producer outputs. They cannot be deleted
without changing the contract validator and generated release inputs.

Ignored `target/`, `rust_out`, and local benchmark/evidence outputs are not
tracked product paths. Current worktree changes and qualification evidence were
left untouched.

Audit commands: `rg -n 'BackupStreams|TimeTravelCache|legacy.rs|contracts/msgpack'`,
`rg -n 'terrabase-bench-served|run_served_boundary_cli|bench-tools'`,
`git ls-files`, and source inspection of the cited callers. No Cargo build or
test was run.
