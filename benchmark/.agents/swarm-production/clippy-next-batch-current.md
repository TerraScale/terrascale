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
