# TerraBase backup/recovery ready briefs (2026-09-09)

Planning/evidence note for `/root/task_map`. Product source is read-only here; this note does not establish acceptance or authorize scope changes. The note was included in HEAD commit `64800112` (`Backup recovery map brief`); that commit also contains concurrent product-source changes in `src/backup/manifest.rs` and `src/backup/restore/decode.rs`, so its source snapshot is not a clean evidence baseline.

## Current coordination/provenance

The requested base `85889473` is present (`Workspace sync: native snapshot backup and recovery, database branch source, host destruction`), and `git log 85889473..HEAD` contains 139 commits. The immediately relevant backup changes include `ac8da8ee`, `20b6c553`, `c81a71a1`, `820ce7a1`, `5a3eaf0f`, `5d31cd10`, `0b2121d8`, followed by the map commit `64800112`. Treat all earlier qualification logs and inherited owner names as historical until a current worker reruns them on a declared source cohort.

| Current lane | Source scope | State/finding | Resource or conflict |
|---|---|---|---|
| Backup map/preparation (`/root/task_map`) | `.agents/backup-recovery-map-20260909.md` only | active; source map and briefs maintained | no product edits, no build |
| Shared Rust build/test (`/root/build_test_owner`) | shared Cargo outputs and test execution | current build owner; submit selectors/source cohort | one owner; Rust commands `nice -n 19`; no competing build |
| Native backup implementation | `src/backup/*`, server native backup runtime | source is changing across backup commits; no current failure inferred from old logs | confirm worker owner/source hash before assignment |
| Runtime/native integration | `crates/terrabase-server/src/native_backup_runtime.rs`, `production_backup.rs`, runtime/API callers | ready to inspect; prior `Fleet(Capture)` is historical until reproduced | coordinate with current runtime owner; no stale `/root/serving_fixer` handle |
| Restore/serving integration | `production_data_runtime.rs`, native recovery/authority seams | overlap owner unknown from repository evidence | requires current owner handoff or Astra decision |
| External provider qualification | OCI/Tigris credentials, buckets and residency controls | blocked absent intended provider access | local object-store fixtures remain regression evidence |

`/root/tb_check` and `/root/serving_fixer` appearing in inherited notes are historical references, not current handles. Current shared build ownership is `/root/build_test_owner`; no serving-fixer assignment is established by the repository. Do not report a lane as active, blocked or accepted solely because an inherited note says so.

## Source map and current evidence

The logical native backup owner is `src/backup/{mod.rs,manifest.rs,restore/,worker.rs,object_store.rs}`. `BackupPlan::snapshot_at`/`run_backup` publish encrypted, manifest-bound points; `NativeBackupFleet::sync_once`, `capture_participants_at`, `restore_from_provider`, retention, restart inventory, and provider readback are implemented in `src/backup/worker.rs` (capture helpers at 1763+, upload at 1837+, retention at 1890+, restore entry at 1295+). Focused core tests in `src/backup/tests.rs` cover corruption, cancellation, chain gaps, idempotent retry, staging limits and timestamp PITR (390–2031); worker tests cover provider outage, restart, retention, policy history, dual-provider restore and capture while writes continue (2151–3600).

The production native owner is `crates/terrabase-server/src/native_backup_runtime.rs` and `production_backup.rs`. `NativeBackupRuntime::capture` at 271–280 still captures one stream at its own high-water; `capture_shard_envelope` at 148–169 does common-cut participant capture and local envelope publication, but its caller/catalog integration is incomplete. The real two-database native test is `native_backup_runtime::restore_tests::one_native_fleet_captures_two_encrypted_databases_and_restores_content` at 734–906 and is recorded as failing with `Fleet(Capture)` in `.agents/qualification/` evidence. Physical encrypted snapshot bridge is `native_snapshot_backup.rs:43–211`, with only focused residency coverage at 244+; this is bootstrap evidence, not logical PITR evidence.

The retired external path remains reachable in `crates/terrabase-server/src/backup.rs` (`BackupStreams`, Litestream process/config/tests), `runtime.rs:1656`, `production_data_runtime/public_server.rs:233`, `usage_closure_http.rs:263`, and `api.rs:610,662,3424–3444,5568–5624`. Production `ApiState` already accepts `native_backups` at `api.rs:643–664`, so replacement wiring can be traced without changing product contracts prematurely.

## Ready brief A — native capture failure reproduction and caller repair

**Outcome/caller:** Make `NativeBackupRuntime::capture` succeed through the real admitted two-database test, with encrypted staged point, provider readback and restart-discoverable manifest. Preserve one stream per database and operation validation.

**Write scope/ownership:** `crates/terrabase-server/src/native_backup_runtime.rs` and its focused tests only, unless the worker owner accepts a handoff into `src/backup/worker.rs`. `src/backup/{mod.rs,manifest.rs,object_store.rs}`, admitted database/engine/key code, and shared Cargo outputs owned by `/root/build_test_owner` are read-only dependencies. Do not change the envelope catalog or public API in this task.

**Design/acceptance:** Reproduce the exact `Fleet(Capture)` return on the current source cohort and inspect staging, key lease, engine publication boundary and destination setup before changing code. The test must call `runtime.capture` with real admitted engines, reopen the published encrypted point, and restore content. A capture/provider/key/staging failure must retain no falsely complete point and must leave the source journal floor protected. Run the narrow selector through `/root/build_test_owner`; no competing Cargo build. Acceptance is the real selector passing on one coherent current source cohort plus evidence identifying the fixed cause. Do not treat the old qualification failure as current, and do not mask the error with plaintext keys, shared engine/staging paths, synthetic manifests, or a test-only helper.

**Risk/dependency:** Existing qualification notes identify the failure but do not establish its cause; classify source defect versus fixture/admission drift. Any change to common-cut envelope behavior is a separate task and requires coordinator/Astra review.

## Ready brief B — production native reachability map and replacement wiring

**Outcome/caller:** Every production backup, readiness, restore/time-travel and usage path uses the native owner; development-only Litestream fixtures remain isolated until deletion is explicitly released.

**Write scope/ownership:** Map first, then the current integration owner may edit `runtime.rs`, `api.rs`, `production_data_runtime/public_server.rs`, `usage_closure_http.rs`, and adjacent native integration tests. `backup.rs` deletion is a later Astra release decision. Native runtime and serving/restore boundaries have an owner conflict that is not resolved by inherited notes; obtain a current handoff before editing overlapping serving files.

**Design/acceptance:** For each `BackupStreams`, `BackupAttachConfig`, `TimeTravelCache`, Litestream command/config, and restore caller, document the native replacement symbol (`NativeBackupRuntime::{tick,capture,restore_from_provider,restore_at_*}`, native recovery/target authority, or an explicit unavailable response). Wire production construction through the existing `ApiState::production` native parameters and preserve placement, authorization, operation identity, provider health and metrics. Add/adjust one real API/runtime test per still-supported behavior; prove no production construction reaches `BackupStreams` before proposing deletion. Acceptance requires a source reachability search, changed caller tests, and a clean native focused build. Do not retain compatibility shims or delete the adapter while native callers are unproven.

**Risk/dependency:** Deletion is consequential and needs Astra approval after qualification. Public time-travel semantics, provider selection and restore authority must follow accepted ADR 0046/ADR 0047 decisions; unresolved product/security choices are not worker assumptions.

## Ready brief C — shard envelope capture/restore integration

**Outcome/caller:** `AdmittedDatabaseRegistry`/member read-cut caller produces one common-cut envelope for all admitted participants; restore selects and verifies the complete envelope before serving resumes.

**Write scope/ownership:** Capture adapter/tests: `native_backup_runtime.rs` plus its tests. Restore serving seam: `production_data_runtime.rs` or a handed-off native restore adapter, with the current serving owner resolved first. Envelope primitives `src/backup/envelope.rs`, `manifest.rs`, and `worker.rs` are read-only unless explicitly handed off.

**Design/acceptance:** Retain `AdmittedShardReadCut`, reject empty/stale route/incarnation or participant mismatch, call `capture_participants_at` at each supplied sequence, verify each archive, then publish one canonical envelope only after all participants/providers succeed. Restore independently lists one provider, authenticates envelope identity/incarnation/route/ordered participants and every archive, stages every target participant under one fenced reservation, and resumes only after all readback and receipt authority checks pass. Acceptance: two-participant routed read/write round trip; one missing/corrupt participant, provider outage, cancellation or post-participant crash leaves no complete envelope and no serving/receipt; retry preserves operation/grant identity. No individual `rp-*` point counts as logical success.

**Decision/dependency:** Astra must choose whether the durable envelope catalog is a provider inventory sidecar or separate committed object, and serving owner must hand off overlapping restore publication. Filesystem-only publication cannot qualify remote restore discovery.

## Ready brief D — physical snapshot/bootstrap qualification

**Outcome/caller:** `build_peer_databases` → `WalSnapshotSource`/follower sink proves encrypted physical snapshot transfer and frontier validation for bootstrap/reconcile.

**Write scope:** `crates/terrabase-server/src/native_snapshot_backup.rs` and focused integration tests, with replication follower owner for sink changes. `src/backup` logical archive and serving authority are read-only.

**Acceptance:** Verify wrong region/provider/key, corrupt/truncated chunk, incomplete transfer, restart and frontier mismatch fail before serving; successful restore opens the target, validates digest/sequence and still requires peer tail/reconciliation before readiness. Label this separately from logical encrypted PITR. External OCI/Tigris placement and credentials remain blocked without intended provider access; local object-store tests are regression evidence only.

## Queue/evidence state and recurring lesson

Capture repair is ready/unassigned pending current-source reproduction by the runtime owner and `/root/build_test_owner`; envelope integration is ready but blocked on the catalog decision and unresolved serving ownership; reachability/deletion is ready for mapping and blocked for destructive removal on Astra release; physical qualification is ready for focused fixture work and blocked on external provider access. Shared Cargo/build owner is `/root/build_test_owner`; do not start another build. Current evidence has no throughput/rework/usage sample for this lane, so no concurrency/batch change is proposed.

Confirmed recurring error: green core/unit tests can coexist with an unused native seam and a live Litestream production caller; the real runtime test currently returns `Fleet(Capture)`. Correction is to exercise admitted runtime callers, encrypted staged/provider state and restart discovery. Prevention is to require exact caller selectors and source reachability before claiming qualification. This is confirmed by `native_backup_runtime.rs:734–906`, `docs/development/backup-qualification-2026-09-08.md`, and `docs/design/native-backup-implementation.md`; the underlying capture cause remains a hypothesis until reproduced on the same source cohort.
