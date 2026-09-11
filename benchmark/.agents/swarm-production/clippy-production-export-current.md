# Production export clippy gate

The bounded production-export cohort was applied to
`crates/terrabase-server/src/production_export.rs` from the readiness brief in
`terrabase-server-clippy-ready-current.md`.

Changed diagnostics:

- introduced `NativeBackupResolver` for the complex resolver type;
- replaced the single-pattern remote-put `match` with `if let` while keeping
  exact-byte idempotency reconciliation;
- elided the `existing_facts` method lifetime while retaining its returned
  future borrow;
- preserved the remote key as `{prefix}/{database}/{job}/COMMITTED` using
  captured `format!` variables;
- documented cursor-key and runtime `Result` errors without secrets;
- used an explicit `()` pattern only for the unit-valued cursor-key error;
  `put_member` and `ExportRuntime::new` retain wildcard mapping because their
  concrete errors are `TransferError` and `ExportError`, respectively.

The first handoff used `Err(())` and `map_err(|()| ...)` mechanically at two
non-unit error boundaries. The consolidated compile caught this; the fix
confirms error types before applying pattern elision.

Validation performed:

```text
cargo fmt --all
cargo fmt --all --check
git diff --check
```

Build and test execution was intentionally left to the consolidated Rust
clippy/test owner, per the execution brief.
