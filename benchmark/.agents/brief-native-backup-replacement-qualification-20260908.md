# Native backup replacement qualification brief (2026-09-08)

Planning/evidence note for `/root`. Product source is read-only for this lane. This note does not establish acceptance and does not authorize deletion, provider changes, or scope expansion.

## Outcome and current gap

Qualify the native Rust backup replacement against ADR 0046's required recovery and integration gates, then return an evidence-backed decision packet to Astra. The observable qualified state is: the real production member composes one native fleet from admitted database/key/provider inputs; native capture, provider readback and restore work through their production callers; recovery remains bounded and fail-closed across restart/failure cases; and the retired Litestream adapter has no remaining production reachability before deletion is proposed.

Current native implementation and evidence:

- `src/backup/worker.rs` owns the bounded fleet, encrypted point capture, per-provider upload/readback, retention, restart inventory and restore paths. `capture_participants_at` pins each stream to a caller-supplied `StorageSequence`; `publish_envelope` writes an atomically renamed local envelope after every participant and provider succeeds.
- `crates/terrabase-server/src/native_backup_runtime.rs` owns admitted runtime composition, operation validation, capture/restore adapters and the multi-participant `capture_shard_envelope` seam. `production_backup.rs` sorts and admits production inputs all-or-nothing.
- `native_snapshot_backup.rs` covers encrypted physical WAL snapshot upload/install through a follower sink; installation is only a bootstrap cut and does not grant serving authority.
- Existing logical recovery coverage includes `src/backup/tests.rs`, `src/backup/worker.rs` tests, `tests/recovery/backup_public_construction.rs`, and native runtime/recovery tests. Existing focused run `qualification/native-backup-runtime-focused-20260908.log` passed 4 tests but failed `native_backup_runtime::restore_tests::one_native_fleet_captures_two_encrypted_databases_and_restores_content` at `native_backup_runtime.rs:854` with `Fleet(Capture)`. `qualification/replication-backup-dbe30d49.log` also records supervisor/drain failures, but that run used a different source cohort and is historical evidence only.
- The old adapter remains reachable: `crates/terrabase-server/src/backup.rs` shells out to Litestream; `api.rs:610,662,3403,5516,5571`, `runtime.rs:1542`, `production_data_runtime/public_server.rs:233`, and `usage_closure_http.rs:263` construct or consume `BackupStreams`. ADR 0046 requires deleting this path in the replacement series, but native qualification must first prove replacement callers and coverage.

## Ready task: `TB-BACKUP-QUALIFY-NATIVE-REPLACEMENT`

### Owned outcome and write scope

Produce a qualification/evidence packet and focused regression changes only where the coordinator assigns the corresponding source owner. The default qualification owner writes only under `terrabase/.agents/qualification/` and this brief's sibling evidence files. Product source, tests, manifests, generated contracts, benchmark artifacts, and the old adapter remain read-only unless a separate implementation brief explicitly hands them over. Do not edit `AGENTS.md`, human-owned goals, or files already modified by another worker.

Record source identity, changed paths, command lines, exit status, test selectors, provider availability, and exact limits. Do not claim a worker report or green unit test establishes acceptance.

### Dependencies and ownership

| Dependency | Authoritative source/owner | State and handoff |
|---|---|---|
| Native logical worker and envelope implementation | `src/backup/{mod.rs,manifest.rs,envelope.rs,worker.rs}`; current worker owner | Read-only while current dirty changes in `worker.rs`/`inventory.rs` settle. Requalify only a coherent source hash. |
| Native runtime/production composition | `crates/terrabase-server/src/native_backup_runtime.rs`, `production_backup.rs`; runtime owner | Read-only. The failing two-database test is a blocker to capture/restore qualification; preserve its log and isolate cause before rerun. |
| Restore/serving authority | `native_recovery.rs`, `production_data_runtime.rs`, lifecycle/host admission | Read-only and potentially overlapping `/root/serving_fixer`; no serving publication edits from this task. |
| Physical snapshot bridge | `native_snapshot_backup.rs` plus replication follower sink | Read-only; qualify independently as bootstrap/reconcile evidence, never as proof of logical PITR. |
| Public production reachability and retired adapter | `api.rs`, `runtime.rs`, `production_data_runtime/public_server.rs`, `usage_closure_http.rs`, `backup.rs` | Conflict with integration/deletion owners. First map all callers and required contracts; deletion needs Astra's explicit release after native callers and tests are proven. |
| Shared Rust build/test resource | `/root/tb_check` (one Cargo/build owner) | Submit selectors/source cohort; do not run competing Cargo/Make builds. Rust commands, if later authorized by owner, use `nice -n 19`. |
| External provider qualification | OCI/Tigris credentials, buckets, residency and outage controls | Unavailable locally unless root supplies an intended provider lane. Memory object-store tests are useful regression evidence only, never provider qualification. |

### Accepted design and qualification method

Use ADR 0046 and `docs/design/native-backup-implementation.md` as the design authority. Do not invent a new format or compatibility shim. For logical points, the source of truth is the engine's durable publication journal and `BackupPlan::snapshot_at`; never infer a boundary from SQLite WAL file length. Every selected provider must independently hold and read back a complete encrypted chain. Upload acknowledgement alone is insufficient; restart/explicit capture requires authenticated readback. A provider outage retains staged data and journal protection and reports degraded health.

For shard envelopes, the production caller must retain an admitted `AdmittedShardReadCut`, capture every participant at its supplied sequence, verify each manifest/archive, then publish one canonical envelope only after all participants and configured providers succeed. A participant `rp-*` directory is not a complete logical point. Restore must select one provider's independently listed envelope, verify identity/incarnation/route generation/ordered participants and every archive before target publication; a partial participant restore never resumes serving.

Qualification should first reproduce the existing `Fleet(Capture)` failure through the real runtime caller and inspect the exact returned boundary (`run_backup`, key lease, lifecycle, staging or provider). Classify the result as source defect, fixture/admission defect, or shared-build/source-cohort drift. Fixes belong to the implementation owner; this lane records the reproduction and updates the brief, rather than patching around it. The historical supervisor/drain failures must not be conflated with this capture failure until reproduced on the same source hash.

### Executable acceptance cases

Run through `/root/tb_check`, using one coherent source cohort and the narrowest selector first:

1. `cargo test -p terrabase_server native_backup_runtime::restore_tests::one_native_fleet_captures_two_encrypted_databases_and_restores_content --lib --locked` must pass on the current native source. The test must exercise real admitted engines, encrypted staging, provider upload/readback, restore into a fresh target, and a query proving restored content.
2. The native worker selectors covering `envelope_catalog_is_not_published_when_a_provider_fails`, `outage_during_rotation_retries_without_exceeding_disk_bound`, `acknowledged_provider_loss_is_detected_after_restart_and_repaired`, `native_fleet_restores_both_providers_and_reopens_encrypted_chain`, and corrupt-provider fallback must pass. Expected evidence: no discoverable complete envelope/point on incomplete provider sets; staged data and retention floor remain; healthy provider can restore independently.
3. `cargo test -p terrabase --test recovery backup_public_construction --locked` (or repository-equivalent selector) must retain coverage for pinned boundaries, path traversal, missing/truncated members, torn publish orphan repair, idempotent retry, encrypted chain verification, timestamp PITR and retention gaps.
4. Native physical snapshot checks must demonstrate wrong-region rejection before provider use, bounded chunk/manifest reads, ciphertext authentication, and installation only through the follower sink. A passing physical bootstrap test is recorded separately from logical PITR and serving authority.
5. Read-only reachability audit must enumerate every `BackupStreams`, `BackupAttachConfig`, `RestoreError`, `open_member`, and `litestream` production reference. Acceptance of the replacement requires native production entrypoints for each still-required behavior plus removal of all active external process/config/package references in a subsequent owned change. A search that merely finds the old code cannot qualify replacement.
6. Failure/restart cases must cover capture cancellation/crash before envelope commit, corrupt or missing participant, provider outage in one-of-two policy, key version recovery, retention root preservation, and restore failure after one participant has staged. Expected outcomes are redacted failure, no serving resume, no false protected frontier, and retry/reconciliation from durable state.
7. Actual OCI-only, Tigris-only, and dual-provider qualification remains pending until intended credentials/buckets and residency controls are available. Record `blocked: external provider access` with no synthetic success; local object-store fixtures must be labelled regression evidence.

The command/resource allocation is one shared Cargo lane under `/root/tb_check`, low CPU priority, with no duplicate build. The owner records exact source SHA (or dirty-tree manifest), target directory, selector and terminal result. Broad `make check` is only scheduled after the source cohort is frozen and focused failures are resolved.

### Forbidden shortcuts

Do not accept in-memory envelope codec tests as proof of real capture/restore; do not treat one participant's point as a shard backup; do not use a local provider fixture as OCI/Tigris qualification; do not allow a write acknowledgement or configured 10-second timer to imply remote recoverability; do not swallow provider/key/checksum errors, weaken assertions, retry unknown writes, or retain Litestream as a fallback. Do not delete the old adapter while its real callers are still unresolved, and do not run a second build against a different dirty source overlay.

## Decisions needed from Astra/coordinator

1. Release the old adapter deletion only after the reachability map identifies native replacements for API, startup, restore, time-travel and usage paths, and focused native evidence passes.
2. Decide whether the multi-participant envelope remote key/catalog remains the current first-participant-owned prefix or must move to a logical-database catalog; either choice must preserve independent provider listing/readback and restart discovery.
3. Provide or explicitly defer intended OCI/Tigris provider credentials, residency evidence and restore-drill scheduling. No local qualification can close those ADR 0046 gates.
4. Decide whether the failing two-database fixture should be repaired as a production runtime defect or replaced with a true admitted shard-cut fixture; the current `Fleet(Capture)` result alone does not establish which.

## Queue state and recurring lessons

| Work item | State | Owner/dependency |
|---|---|---|
| Reproduce/classify `Fleet(Capture)` on coherent source | ready, unassigned | runtime owner + `/root/tb_check` |
| Focused native logical recovery qualification | ready after source freeze | worker owner + shared Cargo lane |
| Physical snapshot bootstrap qualification | ready, independent | replication/backup owner; no serving claim |
| Production reachability map and deletion prerequisites | ready, unassigned | integration owner; overlaps old adapter files |
| OCI/Tigris qualification and restore drills | blocked on external access | Astra/coordinator decision |
| Final acceptance/release decision | pending | Astra; worker evidence is advisory |

Confirmed recurring lesson: existing native backup tests can be green while the real production caller still uses `BackupStreams`/Litestream, and an in-memory envelope seam can pass while participant capture fails. Prevention is to require actual admitted runtime callers, restart-discoverable staged/provider artifacts, and a source-backed reachability audit before calling the replacement qualified. The current `Fleet(Capture)` failure is confirmed evidence; its root cause remains a hypothesis until reproduced with the same source cohort.

