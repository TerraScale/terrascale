# Astra backup hardening refresh — 2026-09-09

Correction supplement to [backup-hardening-20260909](../backup-hardening-20260909/index.md).
Review input only; no correctness approval is asserted. This refresh covers the
post-`a5aac458` authored commits through `b60963d8` plus the current dirty
overlay. The prior packet remains immutable; complete deltas, current source
copies, and hashes are in this directory.

## Frozen inputs and scope

`b60963d8` is the tracked HEAD. The checkout is dirty in 13 product paths;
their complete current diff is [dirty-overlay-current.diff](./diffs/dirty-overlay-current.diff).
The authored correction from `a5aac458..b60963d8` is
[authored-a5aac458-b60963d8.diff](./diffs/authored-a5aac458-b60963d8.diff).
Full HEAD source copies are under `source/`; current dirty copies are under
`source-current/`. [SHA256SUMS](./SHA256SUMS) covers every diff, source copy,
and retained log (the index and manifest-check files are excluded). Both
manifest checks passed twice one second apart. No source, test, contract,
AGENTS file, commit, or build was changed by preparation.

| Worker outcome | Changed paths / review input |
|---|---|
| API local fallback and restore contract | `crates/terrabase-server/src/api.rs:4895-4990`; `runtime.rs:612-646,2295`; `production_data_runtime.rs:889-899,989-1381`; native restore authority now rejects unavailable provisioning rather than selecting a local fallback |
| Customer KMS policy and caller migration | `Cargo.toml`, `Cargo.lock`; `config.rs:92-116,763-780,1124-1295`; ADRs `0030`, new `0049`, `docs/adr/README.md`; `src/encryption/{key_delivery.rs,mod.rs,provider.rs}`; `worker.rs` customer key construction and key delivery callers; `tests/encryption/main.rs:213-313,509-598` |
| Workload identity construction/compile repairs | `production_backup.rs:15-120`; `src/backup/object_store.rs:37-130,454-501,1206-1290`; `tests/encryption/main.rs:509+`; provider-specific destination validation and identity credential path |
| Native runtime/recovery gate and cancellation | `native_backup_runtime.rs:105-524`; `native_recovery.rs:321-580,1228-1250,1989`; `production_lifecycle_control.rs:492-887`; async fleet gate adapters, restore lifecycle fencing and cancellation paths |
| Snapshot and restore publication | `native_snapshot_backup.rs:281-372`; `native_snapshot_backup_tests.rs:39-278`; `src/backup/restore/mod.rs:1557-1800`; chunk identity/order checks, staged publication and cancellation cleanup |
| Object store durability | `src/backup/object_store.rs:11-82,454-501,1206-1290,1355-1423,1611-1618,1870+`; immutable credential zeroization, workload identity, remote publication and validation |
| Existing envelope/worker deltas | `src/backup/envelope.rs`, `src/backup/worker.rs:1055-1070,2173+,2874+,3165+`; operation/fleet and envelope identity interactions remain coupled to prior packet |

The source cohort also changes `auth_runtime/tests.rs`; it is retained in the
complete diff because it is a compile/contract repair dependency. The actual
unmodified callers Astra should inspect with these changes are
`ApiState::production` (`api.rs:643-664`), production startup composition in
`runtime.rs`/`production_data_runtime.rs`, `NativeBackupRuntime` and
`NativeRecoveryRuntime`, `NativeBackupFleet`, `ObjectDestination`, and the
`BackupReader`/restore APIs. The old Litestream `BackupStreams` reachability
remains a separate deletion/replacement concern.

## Decisions and interaction points

ADR 0049 and the config validation establish customer-controlled backup
destination plus KMS identity fields; key delivery must bind the customer
scope/identity and fail closed on unsupported providers. Workload identity
destinations must use the injected provider and valid region/geography
configuration. Native async callers share the runtime fleet gate with recovery
operations. Restore must hold the reservation through cancellation/error and
publish only after every staged artifact is verified. Snapshot chunk checks bind
scope, epoch, identity, file and offset. The API must return an unavailable
authority error when native restore provisioning is absent instead of using a
filesystem/local fallback.

Review the interactions between the API fallback removal and startup
construction; customer KMS configuration, `CustomerKmsBackupKeys` construction,
and key-reader use during retry/restore; workload identity validation and object
store provider construction; async gate locking and synchronous recovery
bridges; snapshot cancellation cleanup and restore publication; and remote
create-if-absent semantics versus worker restart inventory. Similar symbols do
not establish that production callers are connected.

## Latest evidence and limits

| Check | Result / status | Evidence interpretation |
|---|---|---|
| Current broader server suite | **523 passed, 60 failed** | Latest owner-reported aggregate; exact command/source-bound terminal log was not present in this artifact directory. It is failed evidence, not a gate. |
| Prior `make check`/clippy | **107 clippy errors** | Latest owner-reported prior result; not rerun here and not proof against `b60963d8` plus dirty files. |
| Existing native runtime log | 4 passed, 1 failed (`Fleet(Capture)`) | [native-backup-runtime-focused-20260908.log](./logs/native-backup-runtime-focused-20260908.log), historical source cohort; cannot qualify current fixes. |
| Existing worker envelope log | blocked on Cargo build lock | [native-backup-worker-envelope-20260908.log](./logs/native-backup-worker-envelope-20260908.log); pending/stale, not a pass. |
| Existing clippy log | failed with 34 test-build errors at terminal | [full-clippy-current.log](./logs/full-clippy-current.log); source identity predates this refresh. |
| Packet `git diff --check` | exit 0 | Applied to tracked authored and current dirty scoped paths. |
| Cargo/build/make checks by preparer | not run | Explicitly prohibited for this refresh. |

The 523/60 and 107 figures are retained exactly as latest evidence but need
owner command, exit status and source hash before routine Astra submission.
Pending, failed, and stale records must not be combined into a passing claim.
The snapshot/restore publication and cancellation work is reviewable as source,
while any formerly failing snapshot helper must be tied to this frozen dirty
source and a new terminal result. External OCI/Tigris provider behavior,
restart discovery, full multi-participant common-cut restore, and end-to-end
production reachability remain unproven.

## Readiness and next batch

This supplement is source-review ready with explicit failed/pending evidence;
routine acceptance readiness is **no**. Owners must first repair obvious
mechanical compile/clippy failures and publish exact source-bound logs for the
server suite, customer KMS/workload identity selectors, native runtime/recovery,
snapshot roundtrip, restore cancellation/publication, and relevant encryption
tests. The next review batch should contain only the correction delta from this
source state (or a new frozen snapshot if any listed path changes), plus the
matching evidence update. Do not append new generated output or amend either
this supplement or the prior packet after submission.
