# Focused Rust verification

- Start revision: `c9d56faa7727f0d050822923fffd294a36fc512b`
- Command: sequential `nice -n 19 cargo test --locked` selectors for core `storage_closure`, `shard_split`, and server `credential_rotation`, `production_lifecycle_control`, `origin_proof`, `native_recovery`.
- Log: [focused-c9d56faa.log](focused-c9d56faa.log)

Results:

- Core storage closure: **PASS**, 4 passed.
- Core shard split: **PASS**, 23 passed.
- All server selectors were blocked at compilation by concurrent `native_backup_runtime.rs` edits. The first error is `E0277` at line 144: `(String, FleetError)` is not `Copy`, but `errors.iter().copied()` is used. Later repeated server attempts also reported `E0382` use of moved `native_task`.
- No server tests executed in this lane; server source changed during the run (`credential_rotation.rs`, `native_backup_runtime.rs`, `replication_peers/admission_tests.rs`, `runtime.rs`).

The requested clippy pass was deferred because focused server tests did not compile and the source cohort was unstable. No source files were modified by this lane.
