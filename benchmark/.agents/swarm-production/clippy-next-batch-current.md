# Terrabase server next-batch mechanics handoff

Captured 2026-09-11 for approved slices A and D from
`clippy-next-batch-ready-current.md`.

## Changed files

- `crates/terrabase-server/src/schema_migration_runtime.rs`: removed the
  immediately dereferenced byte borrow, used `Vec::clone`, and converted the
  checkpoint serialization match to `let ... else`, preserving fallback and
  CAS behavior.
- `crates/terrabase-server/src/replication_peers.rs`: borrowed the Tokio handle
  at the spawn helper boundary, used `BTreeSet::default`, and removed the
  immediately dereferenced peer-name borrow. Retention ownership and worker
  lifecycle are unchanged.

## Source hashes

```text
0ee3904d049b91507cc607fdec2a15cbcfe1cf7bff73b29d9bfabfc7fa22c29b  crates/terrabase-server/src/schema_migration_runtime.rs
da82abf8c2cf3e619903e68e7b358b9c3ead167b235d41622a40f5f9a952bade  crates/terrabase-server/src/replication_peers.rs
```

## Validation and selectors

`rustfmt --edition 2024 --check` on both files passed; `git diff --check`
passed. No Cargo command was run. Submit to `terrabase_clippy_batch` with
these focused existing selectors:

- schema migration checkpoint persistence
- schema migration CAS conflict
- schema migration rollback
- malformed checkpoint serialization
- replication frontier module tests
- `real_worker_suspension_releases_engine_and_rebinds_same_assignment`
- begin-drain admission/join and cancellation retry tests

The `too_many_lines` diagnostics remain intentionally deferred. The outer
retention owner and engine-frontier acknowledgement paths were not modified.

## Slice B — configuration and identity mechanics

Changed only the approved mechanical items in `config.rs`,
`delegated_identity.rs`, `auth_runtime/byo_config.rs`,
`authorization_leases.rs`, `production_tbep_host.rs`, and
`reactive_api/socket.rs`: explicit unit error patterns, default construction,
checked DER key length conversions (oversized lengths fail closed as `None`),
`String::clone`, `clone_from`, and direct `Result::is_ok_and`.

No BYOK/auth architecture or runtime semantics changed. `rustfmt` and
`git diff --check` passed; Cargo verification is assigned to the consolidated
`project_capacity_acceptance_fixer`/`terrabase_clippy_batch` owner.

Source hashes:

```text
e61e9709afe6a4e40610de43b9c84cddc2a2481eac5cd8a7e782cca2f08ce47a  crates/terrabase-server/src/config.rs
ea31d60c27e740cecfc41f5d5fb3420d20bc29c9b2e28f15a7bd04e11ffca236  crates/terrabase-server/src/delegated_identity.rs
1dfdca3dcd7387de595d8c9279e2c69ce5541a9f55d0b212aa30906fa1dad156  crates/terrabase-server/src/auth_runtime/byo_config.rs
3d4009b65ceacc037cc6f8b3c18d3326a266488eb07d329a890143843a6d8bb3  crates/terrabase-server/src/authorization_leases.rs
9cc470ee5ee25eeab334cf5ddbbdb21dc6b3c12e086f46144bdc16bb349fbd81  crates/terrabase-server/src/production_tbep_host.rs
5ae95891567ad64532cf248a82e7c795c22765b7d43b6b21310f0d4eae25c2f4  crates/terrabase-server/src/reactive_api/socket.rs
```
