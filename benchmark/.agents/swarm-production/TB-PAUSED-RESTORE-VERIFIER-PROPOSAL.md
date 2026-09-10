# Paused restore marker verification: source-backed narrow proposal

Approved implementation now present; Cargo qualification pending engine_gate.

## Actual caller proof

- native_recovery.rs process_restore_pending loads the durable saved request and, for Restoring/Verifying, calls read_published_restore before RestoreExecutor::reconcile and registry.resume.
- read_published_restore calls read_published_marker.
- read_published_marker resolves only the retained admitted MemberHost, checks target owner/database/placement/residency and exact selection, and calls backup::read_restore_marker with the exact RestoreIdentity.
- Once a marker exists it calls EngineHost::admitted_shard_read_cut, then backup::verify_envelope_restore.
- Member's routed registry read_cut calls Engine::routed_read_snapshots (engine/shard_split.rs). Under the origin commit barrier that rejects paused or revoked lifecycle state. Thus an installed marker with a still-paused retained target produces VerificationFailed before reconcile/resume. This is a deterministic ordering refusal, not a mutex deadlock.
- ProductionRestoreExecutor::reconcile delegates to resume_restore_workers; it cannot run on this path until the public-cut read succeeds.
- The same read_published_marker function is used for verified_restore_source_facts on a Complete operation, so a replacement must also handle already-resumed complete readback without changing public admission.

## Proposed bounded correction for source owners

Add an administrative, operation-specific verification seam on the retained MemberHost/Member lease. Return verified RestoreMarkerFacts (or success), not an AdmittedShardReadCut available to a client.

Keep all existing request/target/marker identity checks. The durable admitted restore request and exact authenticated marker authorize only verification of that operation. Inside Member, retain the existing registered physical participants and route generation, hold the same exclusive publication barrier, reject revoked access and active cross-file state, validate physical identities/authority and the exact restored-route binding, and capture private snapshots for verification while lifecycle remains paused. The paused exception belongs only to this marker-bound verifier; ordinary read/capture cuts remain denied.

Reuse verify_envelope_restore's exact envelope/tree/checksum/row-count validation internally. The internal verifier may assemble a private cut for that function but must not return it to the caller. Only after success should existing worker reconcile/resume execute, then durable operation Complete publication.

Do not:
- unpause before identity/tree verification;
- open another root Engine by a caller-supplied path;
- add an unrestricted allow_paused flag to the public read-cut API;
- omit cross-file or revoked-access fences;
- infer verification from marker presence alone.

## Discriminating regression

Using existing real routed lifecycle fixture: publish restore tree/marker, retain target paused, persist operation Verifying (or simulate lost executor result), restart the recovery worker, and require exact operation completion with all participant checks before resume. Assert public admitted_shard_read_cut still denies while verification is paused. Foreign operation/digest/route binding and revoked lifecycle must remain denied, with registry still paused. Run Complete source-facts readback afterward to cover the shared caller.

Source-only followthrough also adds managed/customer real admission selectors and two provider/declaration mismatch cases in routed_restore_lifecycle_tests.rs. test_keys.rs records exact TBKL/TBCK request magic; mismatches require no key-delivery request. These checks are not yet Cargo-verified; engine_gate owns the lane.

## Implemented checkpoint

- Exact additions transferred by cross_file_fixer: engine/shard_split.rs private helper, Member lease method, retained registry verification, EngineHost forwarding.
- Engine helper holds origin commit WRITE then cross-file snapshot READ barriers through marker and content verification. Existing read_cross_file_active includes finishing state. All registered participant ownership, authority, revocation and route-generation checks remain. Revocation is rechecked after validation. No public cut escapes.
- NativeRecoveryRuntime now uses EngineHost::verify_published_restore after unchanged saved-request/owner/placement/selection checks. Reconcile/resume/Complete ordering unchanged. Standalone HostEngine::Local is refused because it has no retained physical Member ownership.
- Lower real native test now asserts paused public cut denied, exact private marker verification succeeds, public cut still denied, then explicitly resumes before data readback.
- New routed_paused_marker_reconciles_after_lost_result_and_restart uses real ProductionRestoreExecutor, injects lost result after success while re-pausing target, observes durable Uncertain, shuts down/reopens worker and uses public retry readback. Requires Complete, exactly one executor call, Complete source-facts readback, foreign digest/operation denial, and revoked target denial. This injects the relevant paused+published+unknown state; it is not an OS process-kill at the exact pre-resume instruction.
- Existing provider tests remain parameterized; startup routed-support guard remains unchanged pending actual positive-candidate execution.
- Both non-cross-file restore CommitIntent initializers explicitly set cross_file_control, cross_file_billing and cross_file_dependencies to None.

Formatting and focused diff check passed. No Cargo run by fixer. Engine gate and coordinator notified.

Hashes at checkpoint (dirty shared source, not an acceptance cohort):

- src/engine/shard_split.rs 1bee91eb971a66e7859c406de89b69aeaa3a6e877b91dfc2f60245c4b1727b12
- src/member/shard_registry.rs 0e7c02e5faeefdac7909f34edd3758d2b609c6ea21e07cf03539f321f43f600f
- src/member/mod.rs b5c103d4390803e3aa52d26c9847a628a6f65421ddaa496220cd4055c3bbd0d9
- crates/terrabase-tbep/src/engine_host.rs 93165bb747b32186533be59fdadc0776e186ea450f7efde6b9076eb6bdfd5153
- crates/terrabase-server/src/native_recovery.rs ac8f8e7d5f46e4a1a6aedab7dc1c11a4577c5fbb44d471660b80914d241a6cf1
- crates/terrabase-server/src/routed_restore_lifecycle_tests.rs ecc3c8617241062844eec27d5b9349b259849c21b662aa173011d8f7091efbe2
- src/backup/restore/mod.rs f8a2add63e2e12377ecd3fc281c12dc8f603945d6d5f63318a3eb29bdb22486c
