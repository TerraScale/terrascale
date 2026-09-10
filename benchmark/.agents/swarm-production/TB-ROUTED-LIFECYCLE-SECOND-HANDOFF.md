# Routed lifecycle second takeover: concrete findings

## v17 namespace-hierarchy correction (supersedes single-directory fix)

Root identified the omitted detail in the prior correction: participant_namespace is not a single component. It returns participants/{32-hex incarnation}/{16-hex shard}/{32-hex lane}. Therefore create_dir(workspace/namespace) still fails on absent intermediate directories, mapping to Capture before RestorePlan. The temporary diagnostic at RestorePlan's final error mapping could not run on this earlier failure; lack of diagnostic output did not imply a key/restore failure.

Temporary diagnostic removed first; worker baseline SHA256 verified exactly 8f7b39305d47d0934ee91fdc70c6571460bf0b718c93fe41383ca01a0b1f0b93. No diagnostic environment check or print remains.

Approved correction now creates the unique operation workspace via nonrecursive DirBuilder, with Unix mode0700 set at creation. Existing workspace paths are not reused. Within that fresh owner-private directory, create_dir_all builds the generated namespace hierarchy and reuses shared participants/incarnation parents between entries. The components come solely from a fixed literal and numeric hex identity formatting; no archive-supplied slash, parent traversal or arbitrary path is accepted. This confines recursive creation to the owned fresh workspace and prevents other Unix users from placing child symlinks there under the existing trusted staging-owner assumption. Same-UID/privileged interference is not newly claimed to be defended against.

Identity/digest/placement checks, bounded transfer checks, final RestorePlan error mapping and RestoreStagingCleanup remain unchanged. Startup guard untouched.

Correction worker hash: 5e8cf988e060b3bcad3b82e416d2af9d43983387956d1d70933a0fab275d6eb2.
Scoped rustfmt --check and git diff --check pass; no Cargo from fixer. Engine gate should reuse routed_native_capture_restores_all_admitted_files, then independent review; no qualification result claimed.

Recurring mistake for future briefs: inspect the actual namespace formatter before prescribing a filesystem creation primitive. The previous brief correctly located the missing parent but incorrectly called a nested namespace one child; the one-level fix was insufficient. Establish which stage executes before adding diagnostics to a later collapsed error mapping.

## Public startup integration execution brief (read-only, v13 preparation)

No product edits/Cargo/guard retirement authorized or performed in this brief. Current source test hash differs from v11 because the key-fixture owner is preparing v13; this is a source trace, not a new tested cohort.

### What the guard actually protects

runtime.rs:440 calls admission.compose_native_backup before serve_admitted_production. compose_native_backup enumerates activated retained hosts and resolves each through resolve_native_backup_input. That resolver first obtains an actual Member-owned public read cut, then unconditionally refuses every installed route generation through ensure_native_backup_shard_capture_supported. One routed serving database therefore fails whole fleet composition before production recovery composition.

After fleet creation, serve_admitted_production constructs NativeRecoveryRuntime with ProductionRestoreExecutor and the same fleet gate, starts native supervision, and binds public lifecycle controls/receipt signing. Export's ProductionArtifactFactory also uses native_backup_input_resolver; the gate affects that caller too.

The guard's comment explicitly says it remains until real production lifecycle capture/restore is proven. It does not demonstrate absence of the routed algorithms: those hooks already exist. It prevents enabling an incompletely qualified composition. Lower private-verifier success alone does not qualify this startup path.

Existing hooks:

- AdmittedBackupEngine requests a fresh retained physical cut from MemberHost.
- NativeBackupFleet::sync_once dispatches routed cuts to capture_shard_envelope; NativeRecoveryRuntime::run_capture does the same for lifecycle captures.
- Envelopes bind physical identities/routes/pins and publish after participant uploads; restore_logical_from_provider selects envelope restore, bounded namespace downloads and whole-tree encrypted RestorePlan publication.
- ProductionRestoreExecutor validates accepted authority/grant/request, suspends target peer/backup owners, reserves/closes/reacquires through Member and completes reservation. Resume uses current assignment/peer databases and native host rebinding.
- NativeRecoveryRuntime marker readback now calls the exact marker-bound private verifier before reconcile/resume/Complete. Public cut remains paused-denied.

### Concrete uncovered composition gaps

1. **Fresh-daemon paused-target backup policy reconstruction.** AdmittedDatabaseRegistry::pause removes the database from serving. retained_snapshot derives activated from that serving set. compose_native_backup filters activated, so a fresh fleet omits a retained paused restore target. NativeBackupFleet::resume_after_restore returns Ok when no suspended policy exists and no live stream exists; it does not create a stream. No production caller of NativeBackupRuntime::attach_async was found. Therefore worker reconciliation can complete without restoring the target's configured backup stream after a fresh daemon composition. This is broader than routed capture and must be explicitly assigned, not silently bundled into guard removal.

   Existing suitable lower hook: NativeBackupFleet::attach reads durable lifecycle, rejects revoked access, and stores paused: lifecycle.paused. It can retain a paused stream without capturing or unpausing. A narrowly scoped internal policy resolver/composer could authenticate retained owner/config/key/placement and reconstruct paused policy without asking for a public read cut. Keep the public active-source resolver's public cut requirement unchanged. A resumed marker must still pass private verification before any public activation.

2. **Current test does not reconstruct those owners.** Its fleet contains only the source input; absent target suspension is intentionally a no-op. Lost-result regression reopens NativeRecoveryRuntime but retains Member/admission/registry/fleet and does not traverse runtime.rs startup or public TbepLifecycleControl request/receipt composition. It proves only the explicitly labeled paused+published+unknown state injection once reachable. It cannot qualify full daemon restart or target-policy restoration.

3. **Customer fixture policy mismatch behind the guard.** Fixture production_owner currently builds ProductionBackupDestination provider Oci for both modes. resolve_native_backup_input requires CustomerS3 when BYOK is configured and uses real UnixWorkloadIdentityProvider plus customer_destination_from_config. Correct any future customer positive test's declaration and selected restore provider to CustomerS3; do not weaken this requirement or pretend an Oci controlled destination proves customer S3. TBCK data/backup key delivery and workload identity credentials are distinct protocols; key delivery observations alone do not qualify workload identity or object transport. Managed OCI is the smallest first positive composition scope.

### Minimal bounded execution order requiring root approval

1. Engine gate runs the already-fixed lower native restore/private-verifier selector on frozen v13; retain exact fail/pass source binding. No guard change for this checkpoint.
2. Assign production_data_runtime.rs internal paused-policy reconstruction and its compose_native_backup caller, with native_backup_runtime/worker changes only if existing attach semantics prove insufficient. Distinguish absent legitimate stream from failed restore rebind; reject revoked/key/config/placement mismatches. No public allow_paused read flag or pre-verification unpause.
3. Extend the existing fixture to compose both source and target backup policies and rebuild Member/admission/registry/fleet/recovery for restart, using persisted accepted placement and exact durable operation/grant. Reuse signed authority fixture; do not fabricate an admitted cut. Controlled external object transport may remain, but its provider identity must match declaration.
4. Root explicitly approves a separate positive startup candidate that retires only the unconditional guard call/helper/guard-only test, keeping public cut inventory and all owner/config/key/provider/placement validation. This is deliberate enablement under test, not speculative removal. Reject or revise candidate on any unresolved composition failure.
5. Exercise actual public lifecycle requests through TbepLifecycleControl: signed EnsureSnapshot/capture, signed RestoreToTarget, exact uncertain retry after full owner rebuild, signed completion receipt and source facts. Require all physical docs/routes, public paused denial before verification, no duplicate provider execution for published marker, target stream present/rebound after Complete, capture resumes only after lifecycle resume. Preserve foreign operation/digest/owner/placement, revoked-key/access, active/finishing-crossfile and provider failure negatives.
6. Separate Astra integration review before claiming startup support. Real OCI/customer external-provider qualification and literal process-crash timing remain separate from controlled transport/state-injection tests.

Exact proposed ownership: server production_data_runtime.rs resolver/composer + existing routed_restore_lifecycle_tests.rs and narrow test transport seams; runtime.rs only if a genuine caller integration change is needed. Existing Member/private verifier and backup algorithms need no speculative rewrite. Guard remains untouched until step4 is authorized. Target policy reconstruction is a real runtime contract decision for root, not a fixture-only workaround.

Read source hashes:
- runtime.rs f990cdc0358bff57ce7e2939bd8f0823eeed9a864a3944a13404d63aac6c8f27
- production_data_runtime.rs 96872dbd1f515b2fbdf6a21e017b7efec797696271e238bc0dfc835f8ed0c524
- native_backup_runtime.rs 31e8a90528375bc597390bde7a60959721e9411b7af06f93f63b7b7742ee2f07
- native_recovery.rs ac8f8e7d5f46e4a1a6aedab7dc1c11a4577c5fbb44d471660b80914d241a6cf1
- production_lifecycle_control.rs 03528b4431a262700842f8f71dc343526ad64f2858f5f477af20c35e03d53738
- worker.rs 8f7b39305d47d0934ee91fdc70c6571460bf0b718c93fe41383ca01a0b1f0b93
- routed_restore_lifecycle_tests.rs 374b1056e2ae2116652a859db9db6672d9f28d47d17b9dadd22eed66b1791936

## Approved missing-parent correction, frozen handback

Root transferred only restore_envelope_from_provider's namespace-parent creation hunk. Applied exactly one production line after root = workspace.join(&namespace):

```rust
std::fs::create_dir(&root).map_err(|_| FleetError::Capture)?;
```

This creates one fresh child under the already-created private operation workspace. Existing/symlink child paths are rejected by create_dir rather than followed/reused. No recursive directory creation, identity/digest/residency checks, byte limits, download verification, or RestoreStagingCleanup behavior changed. No tests changed and startup guard remains intact.

Source: src/backup/worker.rs SHA256 8f7b39305d47d0934ee91fdc70c6571460bf0b718c93fe41383ca01a0b1f0b93 (prior v11 30bb23be4ff3e640863a35b6df2ee8736ddb545f475c8e1fa998df7490a0aece).

Checks: rustfmt --edition 2024 --check src/backup/worker.rs and git diff --check -- src/backup/worker.rs both exit0. No Cargo run; engine_gate retains sole lane. Freeze this one-line delta into the next cohort; no other source overlays required for this correction.

### Exact guard-preserving private-verifier exercise

No further test/source change is needed for the next verifier checkpoint. Run the existing routed_native_capture_restores_all_admitted_files selector after applying this download-parent correction. Immediately after real encrypted provider restore and reservation.reacquire, its unchanged assertions already:

1. Require lease.admitted_shard_read_cut() to fail on the still-paused target.
2. Call lease.verify_published_restore with the exact envelope/target/operation identity and require verified marker facts.
3. Require the public cut to remain denied, proving verification did not unpause.
4. Explicitly resume only after those checks and verify all admitted participants, routed SQL IDs, and per-participant values.

This exercises the production private Member verifier with real retained participant ownership and encrypted restored state. It uses the existing explicitly constructed NativeBackupRuntime with controlled in-memory transport; it does not cross or alter ProductionAdmissionRuntime's intentional startup guard. Label any pass as lower native/provider-transport-fixture plus private-verifier coverage, not production startup enablement, crash/restart qualification, or external-provider qualification.

The separate routed_paused_marker_reconciles_after_lost_result_and_restart assertion remains unchanged and expected to stop at the startup guard until a separately authorized startup candidate exists. Do not reinterpret its current failure as success or remove its assertion to obtain a green selector.

## v11 read-only diagnosis, 2026-09-10

Coordinator requested diagnosis only; no product edits, Cargo, guard changes, or provider activity performed.

Evidence log:
/home/mariogk/Projects/TerraScale/.agents/qualification/current-20260909/engine/server-routed-v11.log

The two assigned failures are distinct:

### Routed native restore: missing private download parent

The lower test reaches line759, the result unwrap of NativeBackupRuntime::restore_logical_from_provider, and reports Fleet(Upload). Earlier assertions succeeded: real scheduled tick returned no capture errors, retained envelope had two participants/two documents, and target restore reservation/close completed.

Actual path:

1. native_backup_runtime.rs:348 selects retained envelope and calls NativeBackupFleet::restore_envelope_from_provider.
2. worker.rs:1542 creates only stream-staging/restore-ULID.
3. For each physical participant, worker.rs:1548 sets root = workspace/participant_namespace; no operation creates this root.
4. worker.rs:1557 passes root/rp-manifest-id to ObjectDestination::download_point_with_byte_limit.
5. object_store.rs:1145 obtains target.parent() (the nonexistent root) and calls StagingDir::create(parent).
6. backup/mod.rs:2221 uses nonrecursive std::fs::create_dir(parent/.staging-...). Missing parent necessarily fails; object store maps this to TransferError::LocalIo, and worker maps every download error to FleetError::Upload.

The log exposes only collapsed Upload; it does not print the transfer suberror. The missing-parent failure is established directly by the production call path, not inferred from credentials or a guessed fixture adjustment. Capture uploads and envelope publication precede restore; a fresh uniquely named restore workspace cannot already contain that participant child. Unsharded restore_selected correctly creates its staging directory and downloads directly beneath it (worker.rs:1663 onward).

Effect: restore aborts during download preparation, before RestorePlan::restore_envelope_encrypted_with_config can publish a target tree. This is not evidence of an external provider outage or encryption refusal.

Minimal correction brief (NOT APPLIED): obtain narrow ownership of src/backup/worker.rs::restore_envelope_from_provider and create each exact private participant root with std::fs::create_dir before invoking bounded download. Map creation failure to existing Capture/local staging failure; do not change transfer integrity/size checks, object-store helper contract, or global error taxonomy. The parent is beneath the already-created operation workspace and derives from validated physical identity; no recursive arbitrary-path creation is needed. Existing routed_native_capture_restores_all_admitted_files already discriminates this production caller defect; reuse it rather than masking the failure by precreating a test-only path.

### Paused lifecycle test: deliberate startup guard

routed_paused_marker_reconciles_after_lost_result_and_restart fails line869 at actual ProductionAdmissionRuntime::resolve_native_backup_input. production_data_runtime.rs:1084 passes the real admitted cut's installed route generation to ensure_native_backup_shard_capture_supported. The helper (1922) intentionally rejects any Some generation with the exact logged message.

This fixture has a real restored route generation (3). The failure means that the declared fail-closed feature gate is working. It occurs before the test replaces external object transport, before NativeRecoveryRuntime construction/capture, and before ProductionRestoreExecutor or administrative paused-marker verification.

No fix is authorized/applied in this diagnosis. Keep guard and public paused-read denial. After lower restore and other blocking source corrections are verified, coordinator must explicitly schedule the already-proposed narrow positive candidate (guard call/function/guard-only test retirement) with actual lifecycle selectors. A passing admission or provider mock alone cannot justify guard retirement. The paused/lost-result regression remains a state-injection test after executor success/re-pause, not an OS process-crash test.

### Source binding and ownership request

Read source hashes exactly equal the supplied v11 before-manifest:

- worker.rs 30bb23be4ff3e640863a35b6df2ee8736ddb545f475c8e1fa998df7490a0aece
- object_store.rs 2807b02ea35bd16c5fbbfe1cc35241857f19143a4494724c00d029b2161b574e
- backup/mod.rs 45bd77e40599da8a79d89960745627badc7dc4f872f463b9b7ccfc64eb3a764a
- production_data_runtime.rs 96872dbd1f515b2fbdf6a21e017b7efec797696271e238bc0dfc835f8ed0c524
- routed_restore_lifecycle_tests.rs ecc3c8617241062844eec27d5b9349b259849c21b662aa173011d8f7091efbe2

Only evidence file changed in this diagnosis. Request root transfer the single worker download-parent hunk from its current owner; no object-store/server/encryption edits needed for this cause. Engine gate retains sole Cargo ownership. Separate admission EncryptionUnavailable failures remain assigned to sharded_recovery.

Prevention for worker briefs: distinguish transfer errors from their collapsed fleet error label and verify the caller establishes the downloader's parent-directory precondition when introducing an additional physical namespace.

Base HEAD afe177c7aaa34caed76a5d4afea57a028d558936; dirty shared source preserved.

## Implemented

- New real routed server tests in crates/terrabase-server/src/routed_restore_lifecycle_tests.rs and narrow lib.rs declaration. Scheduled native capture/provider restore/Member reacquire and real ProductionAdmissionRuntime + NativeRecoveryRuntime + ProductionRestoreExecutor lifecycle with signed placement/grant authorities, exact retry, foreign-envelope retry rejection, recovery-ledger reopen, routed SQL readback.
- Narrow transferred src/member/mod.rs open_database_engine fix removes redundant explicit plain-region replication origin; retains owner binding. Engine already infers lanes for new files and respects persisted lanes for existing files. Diagnostic showed failure before epoch activation; both tests moved beyond Member acquisition after fix.
- Test fixture root mode0700 satisfies PlacementJournal path security. No security guard weakened.
- Explicit BYOK fixture now uses CustomerKmsDatabaseKeys and matching ProductionByokConfig instead of inherited auth-runtime OCI-only template.
- Narrow test_keys.rs retained Unix responder supports TBCK as well as TBKL and echoes protocol magic. Production constructors still verify lease metadata/version/peer/path.

## Two newly exposed production dependencies

1. Customer key contradiction: ProductionAdmissionRuntime customer_source requires CustomerKms/AWS BYOK, but EngineConfig::validate_storage_keys rejects any provider except OciVault (engine/mod.rs:1301). ArtifactCipher::from_database_key_lease also rejects CustomerKms (encryption/mod.rs:548). Live test correctly fails Engine::open InvalidConfig with real customer source. Root assigned separate audit; do not fake OCI identity for TBCK. Wrapped-key hierarchy gates may be intentionally OCI-specific; do not broadly replace every provider check.
2. Independent OCI capture failure: genuine admitted read-cut pinned sequence3, journaled_high_water1, servable floor0. SQLite journaled_high_water reads MAX(documents.commit_seq), omitting metadata-only source publications from restored file identity/catalog setup. run_backup correctly refuses snapshot pin greater than highwater. Fix complete publication-history frontier, not pin lowering. Existing sequence_only_delete_is_durable_across_reopen expectation currently documents old document-only highwater; failed-documents-table read regression must remain meaningful.

Startup routed-support guard remains intact: full lifecycle has not reached it. Root transferred its narrow removal, but removal is conditional on meaningful real positive-path qualification.

## Exact execution evidence

All cargo: CARGO_TARGET_DIR=/tmp/terrabase-restore-check.v06vvE/target nice -n 19 cargo test --locked -j 2 -p terrabase-server --lib SELECTOR -- --nocapture.
Protected frozen source: /home/mariogk/.cache/trc.yR76BC. /tmp source is unsuitable for compile-time key fixture socket path because all ancestor permissions are validated.
Logs under /tmp/terrabase-restore-check.v06vvE:

- routed-admission-diagnostic.log: session99759 completed exit101; open_database_engine diagnostic before epoch.
- routed-origin-fix.log: session20916 exit101; both moved beyond original failure.
- routed-post-origin.log: session73652 exit101; Capture plus missing BYOK fixture.
- routed-capture-diagnostic.log: session81689 exit101; run_backup SourceUnavailable.
- routed-customer-diagnostic.log: session42601 exit101 compile-only ambiguous .into calls; corrected.
- routed-customer-diagnostic-2.log: session14816 exit101; real CustomerKms sources, Engine::open InvalidConfig.
- routed-oci-boundary-diagnostic.log: session10330 exit101; exact pinned3/highwater1/floor0 cause.

No live Cargo process remains from this takeover. No full check, provider, or production acceptance claimed.

## Source state and snapshot differences

Live SHA256 at handoff:
- routed_restore_lifecycle_tests.rs ee18a51fc8fd9b980cc9b7e71fff59939e53f649ffc9b9d7462dcf0bfeed6838
- test_keys.rs 26bfe284accced79fb8f79ec40dc865c2246e0cca968e6d42aa1c60b5cd45eb8
- src/member/mod.rs a4eebda188a5d71402a5c048585a9178c86bd61acf097f150e0de3a2914f7e1a (includes other owner's concurrent work)

Focused git diff --check passes. Owned tests formatted; no whole-member formatting.
Frozen worker has explicitly diagnostic-only prints at capture_participants/run_backup/envelope/publish plus pinned/highwater/floor. Never copy it wholesale live. Frozen test temporarily uses prior OCI key calls at four fixture sites solely to isolate lower capture failure; live test remains coherent BYOK. Restore those four sites from live before customer-path qualification. Frozen cohort excludes current cross-file/retention integration; do not refresh entire files.

## Prevention / bounded resumption

- Do not restart live builds due elapsed time; all sessions above were reused to completion.
- Do not manufacture stale root copies with direct writes after route installation: Engine correctly rejects them without Member admission. Existing core routed_snapshot_filters_stale_copies_at_the_pinned_boundary covers filtering separately.
- Do not place compile-time Unix key fixture source under world-writable ancestors or weaken path validation.
- Do not reuse unrelated authentication production templates as a full admission declaration without checking exact key provider/BYOK fields.
- Resolve provider consumer contract and complete highwater under their source owners; reuse these two regressions, remove snapshot diagnostics, deliberately remove guard only in qualified candidate, then run same selectors. Separate Astra review required; this fixer does not self-approve.
