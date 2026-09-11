# Native backup catalog historical-test handoff

Captured 2026-09-11 for the approved adversarial server-test slice.

## Added regressions

- `routed_restore_lifecycle_tests.rs` now drives the real routed fixture and
  provider-backed `NativeBackupRuntime` through a whole-database capture before
  the existing routed envelope capture. It asserts the public catalog lists
  both authenticated points, preserves newest-first ordering, resolves the old
  point through `NativeRecoveryRuntime::resolve_recovery_point`, and selects
  the routed envelope through the `latest` envelope resolver.
- `native_backup_runtime.rs` now populates three real standalone captures and
  checks bounded pagination, cursor continuation without duplicates, and expiry
  filtering at `u64::MAX`.

No catalog DTOs or fake manifests were constructed; the tests use real capture,
envelope, provider, and native recovery seams. No production shutdown code was
changed.

## Source identity

Post-edit SHA-256:

```text
db8e5bc84e1a72da4d1821598315a20b334645a5a92c9a1fce0269e746829e94  crates/terrabase-server/src/native_backup_runtime.rs
dd0dd712a33e37337fc32d279176afb67e26f99cad9b8dfa4ac40422abee666b  crates/terrabase-server/src/routed_restore_lifecycle_tests.rs
```

## Validation and handoff

`rustfmt --edition 2024 --check` over both changed files passed, as did
`git diff --check`. No Cargo commands were run. Submit the exact selectors to
the sole `terrabase_clippy_batch` owner:

- `native_backup_runtime::tests::catalog_lists_an_authenticated_standalone_capture`
- `routed_restore_lifecycle_tests::routed_managed_lifecycle_capture_restore_and_reopen`
- `routed_restore_lifecycle_tests::routed_production_lifecycle_capture_restore_and_reopen`

The owner should verify whether the routed engine permits the preceding direct
whole-database capture; if it rejects a routed serving cut, the source-backed
fixture requires a standalone engine state transition before capture rather
than weakening catalog or resolver assertions.

The explicit participant relocation test remains pending: the assigned server
fixture currently does not expose a provider-object copy seam for the captured
participant archive, and this worker did not fabricate one or weaken resolver
checks. This is a concrete test-fixture prerequisite, not a production
behavior change.
