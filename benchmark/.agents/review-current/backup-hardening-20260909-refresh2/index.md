# Astra backup hardening and SDK supplement — 2026-09-09

This is a bounded correction supplement to
[backup-hardening-20260909-refresh](../backup-hardening-20260909-refresh/index.md).
It adds the missing Rust SDK restore contract and refreshes the native
runtime/envelope/snapshot/restore source identities. Review input only; no
correctness approval is asserted.

## Frozen source and complete diffs

At capture, TerraBase HEAD was `962436c6038173a1bb036338dcd33255d0ddbefe`
(`Backup worker and lifecycle updates`), descended from the requested
`1ada89f4`. The previously reported dirty TerraBase files had been committed;
the only remaining TerraBase changes are review-artifact files. `ts-rust` HEAD
was `8477b9f261320983a73ed23148ba32b10356cc43` and had only an ignored
`scripts/__pycache__` output; `src/management.rs` and `tests/management.rs`
were clean at capture. This observed state supersedes the earlier
“1ada + dirty” description.

The complete SDK delta from prior reviewed `9aaed1cb` is
[ts-rust-management-9aaed1cb-8477b9f.diff](./diffs/ts-rust-management-9aaed1cb-8477b9f.diff);
the current SDK dirty overlay is empty at capture. SDK source snapshots are
`sdk-rust/src_management.rs` and `sdk-rust/tests_management.rs`.
TerraBase authored and current overlays are
[terrabase-authored-1ada89f4-HEAD.diff](./diffs/terrabase-authored-1ada89f4-HEAD.diff)
and [terrabase-dirty-overlay-1ada89f4.diff](./diffs/terrabase-dirty-overlay-1ada89f4.diff).
HEAD and current source copies are under `source-head/` and `source-current/`.
[SHA256SUMS](./SHA256SUMS) covers all diffs and source copies; captured commit,
status, and per-file hashes are in [source-identities.txt](./source-identities.txt).
Both manifest checks passed twice one second apart.

The SDK change is bounded to `src/management.rs:790,848,1118-1151` and
`tests/management.rs:133-314`: `RestoreRequest` now requires bounded
`operation_id`; backup restore requires HTTP 202, decodes
`ProvisioningOperation`, verifies returned operation identity, and observes
cancellation. Tests cover missing create provisioning fields without a
transport call, the accepted 202 operation response, rejected 201 status, and
mismatched operation identity. The actual management request body assertion is
at `tests/management.rs:265`.

## Native source identity and interaction map

The refreshed TerraBase source copies cover current `api.rs`, `config.rs`,
`native_backup_runtime.rs`, `native_recovery.rs`, `native_snapshot_backup.rs`
and its tests, `production_backup.rs`, `production_data_runtime.rs`,
`production_lifecycle_control.rs`, `runtime.rs`, `src/backup/{envelope,
object_store,restore/mod,worker}.rs`, encryption key/provider files, and
`tests/encryption/main.rs`. The authored delta from `1ada89f4` includes the
three committed files `production_data_runtime.rs`,
`production_lifecycle_control.rs`, and `src/backup/worker.rs`; the full current
source identity is nevertheless captured for all affected callers.

Relevant callers are `api.rs` restore/list handlers; production construction
in `runtime.rs`, `production_data_runtime.rs`, and `production_backup.rs`;
`NativeBackupRuntime`/`NativeRecoveryRuntime`; `NativeBackupFleet`; and
`ObjectDestination` plus the SDK `Backups::restore` method. Review the SDK's
202 response and operation identity against the server `restore_backup` route
and operation schema. Review native gate acquisition and cancellation through
fleet worker, recovery worker, lifecycle control, staged restore publication,
and snapshot chunk validation. Customer KMS and workload identity remain
bound to their config validation and object-store credential providers. The
old Litestream/local fallback reachability remains relevant context only where
the current API explicitly reports unavailable authority.

Do not carry forward the earlier `common_cut` concern as a current finding:
the source state has since changed and this supplement records no assertion
about its present correctness. Any envelope issue must be established against
the captured `962436c6` source and a concrete caller.

## Evidence state and limits

| Evidence | State | Interpretation |
|---|---|---|
| Current capture identity | `962436c6` TerraBase; `8477b9f` ts-rust | Source copies and hashes are available; no build was run by preparation. |
| SDK management focused evidence | Not supplied in this supplement | Source/test changes are inspectable, but no pass claim is made without an exact command, exit status, and matching source hash. |
| Broader server suite | 523 passed, 60 failed | Latest owner-reported aggregate; failed evidence, not acceptance. Exact terminal log/source binding is still missing. |
| Prior make check/clippy | 107 clippy errors | Historical owner report; not rerun and not current-source proof. |
| Native runtime | Historical log: 4 passed, 1 failed (`Fleet(Capture)`) | Existing log is retained in the prior refresh and remains stale relative to `962436c6`. |
| Snapshot/restore publication and cancellation | Source-review only | Current code is captured, but exact current focused terminal evidence is missing. |

The ignored SDK `__pycache__` is unrelated generated output and is excluded
from the SDK source/diff scope. External provider behavior, restart discovery,
full multi-participant restore, and production end-to-end reachability remain
unproven. Keep historical failures, pending checks, and any future current
capture result separate; a current hash is not a passing test.

## Readiness and next action

This supplement is ready for Astra source review of the SDK contract and the
current native interaction delta. Routine acceptance readiness remains **no**.
Owners must provide exact source-bound SDK, server, native runtime/recovery,
snapshot, restore cancellation/publication, KMS, and workload-identity test
commands and terminal exits, then repair the known broad mechanical failures.
Any subsequent source change requires a new correction supplement containing
only its delta and matching evidence.
