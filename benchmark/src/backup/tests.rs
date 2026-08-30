//! In-crate backup-core tests (BK-01).
//!
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the data-bearing behavior tests live in-crate
//! with the `TenantScope::from_parts` fixture, mirroring the engine,
//! families, streaming, and replication test suites. Every assertion goes
//! through the public `crate::backup` seam; no test inspects archive bytes,
//! member internals, or private writer state directly.
//!
//! These tests prove observable behavior only. They make no recovery,
//! durability, RPO/RTO, or performance claim: formal Recovery Receipts come
//! exclusively from the exclusive recovery lane, which this suite does not
//! run.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc;
use std::thread;

use crate::backup::{
    BackupError, BackupKind, BackupPlan, BackupSkipReason, BackupStep, RestorePlan,
    load_recovery_point, run_backup, verify_latest_chain,
};
use crate::encryption::{
    ArtifactCipher, Compression, DatabaseKey, EncryptionContext, ProviderMode,
};
use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::types::{
    Cancellation, CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, StorageSequence, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    let path = std::env::temp_dir()
        .join(format!("terrabase-bk01-backup-{tag}-{}-{index}", std::process::id()));
    // A recycled pid could collide with a crashed earlier run's leftover;
    // every location is test-owned, so start from a clean path.
    let _ = std::fs::remove_dir_all(&path);
    path
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine = Engine::open(
        EngineConfig::durable(&dir)
            .expect("durable location")
            .with_storage_backend(crate::engine::StorageBackend::RootFiles),
    )
    .expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn text(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn put(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    collection: &str,
    value: DocumentValue,
    durability: LocalDurability,
) -> CommitOutcome {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation(operation_name)).expect("begins");
    transaction
        .put(
            Document::new(
                document_id(id),
                CollectionName::try_new(collection).expect("valid identifier"),
                value,
            ),
            ExpectedVersion::Any,
        )
        .expect("stages");
    transaction.commit(durability)
}

fn delete(engine: &Engine, tenant_name: &str, operation_name: &str, id: &str) -> CommitOutcome {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation(operation_name)).expect("begins");
    transaction.delete(document_id(id), ExpectedVersion::Any).expect("stages");
    transaction.commit(LocalDurability::Synced)
}

/// Writes one family record into the reserved collection through the Engine
/// transaction seam, exactly as a typed family write does.
fn put_family_record(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    kind_key: &str,
    owner: &str,
) -> CommitOutcome {
    let id = DocumentId::try_new(&format!("family:{kind_key}/{owner}")).expect("valid identifier");
    put(
        engine,
        tenant_name,
        operation_name,
        id.as_str(),
        "terrabase.families",
        DocumentValue::Object([("count".to_owned(), DocumentValue::Integer(7))].into()),
        LocalDurability::Synced,
    )
}

fn committed(outcome: CommitOutcome) -> StorageSequence {
    match outcome {
        CommitOutcome::Committed(receipt) => receipt.sequence(),
        other => panic!("expected a committed receipt, got {other:?}"),
    }
}

fn test_backup_cipher() -> ArtifactCipher {
    test_backup_cipher_with_key(7)
}

fn test_backup_cipher_with_key(key_byte: u8) -> ArtifactCipher {
    let context = EncryptionContext::new(
        "test",
        "tenant-a",
        "database-a",
        "backup",
        "recovery-point-a",
        "key-a",
        3,
        Compression::None,
    )
    .expect("encryption context");
    ArtifactCipher::new(ProviderMode::LocalFile, DatabaseKey::from_bytes([key_byte; 32]), context)
        .expect("backup cipher")
}

#[test]
fn encrypted_backup_seals_members_and_manifest_and_round_trips() {
    let (_dir, engine) = open_engine("encrypted");
    let destination = location("encrypted-dest");
    let target = location("encrypted-target");
    committed(put(
        &engine,
        "alpha",
        "encrypted-put",
        "doc-1",
        "users",
        text("secret-value"),
        LocalDurability::Synced,
    ));

    let result =
        run_backup(&engine, BackupPlan::full().with_encryption(test_backup_cipher()), &destination)
            .expect("encrypted backup");
    let point = destination.join(format!("rp-{}", result.manifest().identity()));
    assert!(!point.join("manifest.tbv2m").exists());
    let encrypted_manifest = std::fs::read(point.join("manifest.tbv3e")).expect("manifest");
    assert!(!String::from_utf8_lossy(&encrypted_manifest).contains("documents"));
    for member in result.manifest().members() {
        let bytes = std::fs::read(point.join(member.name())).expect("member");
        assert!(!String::from_utf8_lossy(&bytes).contains("secret-value"));
    }
    RestorePlan::restore_latest_encrypted(&destination, &target, &test_backup_cipher())
        .expect("encrypted restore");
}

#[test]
fn encrypted_restore_rejects_wrong_key_and_member_tampering() {
    let (_dir, engine) = open_engine("encrypted-tamper");
    let destination = location("encrypted-tamper-dest");
    let target = location("encrypted-tamper-target");
    committed(put(
        &engine,
        "alpha",
        "encrypted-tamper-put",
        "doc-1",
        "users",
        text("secret-value"),
        LocalDurability::Synced,
    ));
    let result =
        run_backup(&engine, BackupPlan::full().with_encryption(test_backup_cipher()), &destination)
            .expect("encrypted backup");
    let point = destination.join(format!("rp-{}", result.manifest().identity()));
    assert_eq!(
        RestorePlan::restore_latest_encrypted(
            &destination,
            &target,
            &test_backup_cipher_with_key(8)
        ),
        Err(BackupError::ChecksumMismatch)
    );
    let member = result.manifest().members().first().expect("member");
    let member_path = point.join(member.name());
    let mut bytes = std::fs::read(&member_path).expect("member bytes");
    let last = bytes.last_mut().expect("ciphertext");
    *last ^= 1;
    std::fs::write(&member_path, bytes).expect("tamper member");
    assert_eq!(
        RestorePlan::restore_latest_encrypted(&destination, &target, &test_backup_cipher()),
        Err(BackupError::ChecksumMismatch)
    );
    assert!(!target.exists(), "failed restore must not publish target");
}

#[test]
fn full_backup_writes_a_manifest_with_ordered_digests_and_counts() {
    let (_dir, engine) = open_engine("full-manifest");
    let destination = location("full-manifest-dest");

    committed(put(
        &engine,
        "alpha",
        "op-a1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-a2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "beta",
        "op-b1",
        "doc-1",
        "users",
        text("beta-one"),
        LocalDurability::Synced,
    ));

    let result = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup runs");

    let manifest = result.manifest();
    assert_eq!(manifest.kind(), BackupKind::Full);
    assert!(manifest.base_sequence().is_none());
    assert!(manifest.boundary_sequence().get() > 0, "boundary is the published sequence");
    assert_eq!(manifest.documents(), 3);
    assert_eq!(manifest.family_records(), 0);
    assert_eq!(manifest.deletes(), 0);
    assert_eq!(manifest.disclosures(), 0);
    assert_eq!(manifest.tenants().len(), 2, "both committed tenants are bound");

    let members = manifest.members();
    assert!(!members.is_empty(), "at least one archive member exists");
    for window in members.windows(2) {
        assert!(window[0].name() < window[1].name(), "member names are ordered");
    }
    for member in members {
        assert_eq!(member.digest().len(), 64, "SHA-256 digests are 64 hex characters");
        assert!(member.digest().chars().all(|c| c.is_ascii_hexdigit()), "digests are hex");
    }
    assert_eq!(
        manifest.total_bytes(),
        members.iter().map(crate::backup::BackupMember::length).sum(),
        "total bytes bind the ordered member lengths"
    );

    // The manifest identity is content-derived, stable, and reloadable.
    let identity = manifest.identity();
    assert_eq!(identity.len(), 64);
    let reloaded = load_recovery_point(&destination, identity).expect("manifest reloads");
    assert_eq!(reloaded, *manifest);

    result.archive().verify(&destination).expect("the written archive verifies");
}

#[test]
fn backup_includes_family_records_from_the_reserved_collection() {
    let (_dir, engine) = open_engine("family-records");
    let destination = location("family-records-dest");

    committed(put(
        &engine,
        "alpha",
        "op-doc",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put_family_record(&engine, "alpha", "op-family-1", "counters", "owner-a"));
    committed(put_family_record(&engine, "beta", "op-family-2", "attachments", "owner-b"));

    let result = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup runs");
    let manifest = result.manifest();
    assert_eq!(manifest.documents(), 1, "user documents count separately");
    assert_eq!(manifest.family_records(), 2, "reserved-collection records count as family records");

    let alpha = manifest.tenants().iter().find(|t| t.tenant().as_str() == "alpha").expect("alpha");
    assert_eq!(alpha.documents(), 1);
    assert_eq!(alpha.family_records(), 1);
    let beta = manifest.tenants().iter().find(|t| t.tenant().as_str() == "beta").expect("beta");
    assert_eq!(beta.documents(), 0);
    assert_eq!(beta.family_records(), 1);

    result.archive().verify(&destination).expect("archive verifies");
}

#[test]
fn backup_progress_reports_skipped_items_without_hiding_loss() {
    let (_dir, engine) = open_engine("skipped-disclosure");
    let destination = location("skipped-dest");

    committed(put(
        &engine,
        "alpha",
        "op-seq",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let base = {
        let result = run_backup(&engine, BackupPlan::full(), &destination).expect("base full");
        result.manifest().boundary_sequence()
    };

    // A Buffered commit is real published history whose durability is
    // disclosed: the record must stay in the archive AND appear as a skip
    // fact, never silently either dropped or presented as fully durable.
    committed(put(
        &engine,
        "alpha",
        "op-buffered",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Buffered,
    ));

    let observed: Arc<std::sync::Mutex<Vec<crate::backup::BackupProgress>>> =
        Arc::new(std::sync::Mutex::new(Vec::new()));
    let sink = Arc::clone(&observed);
    let plan = BackupPlan::incremental_since(base).with_progress(Arc::new(move |progress| {
        sink.lock().expect("progress sink lock").push(progress);
    }));
    let result = run_backup(&engine, plan, &destination).expect("incremental runs");

    assert_eq!(result.manifest().disclosures(), 1, "the disclosure is bound into the manifest");
    assert_eq!(result.manifest().documents(), 1, "the disclosed record is still in the archive");
    assert_eq!(result.skipped_total(), 1);
    let skip = result.skipped_items().first().expect("the skip fact is listed");
    assert_eq!(skip.tenant().as_str(), "alpha");
    assert_eq!(skip.document().as_str(), "doc-2");
    assert_eq!(skip.reason(), BackupSkipReason::DurabilityBelowSynced);

    // Progress carried the same fact before the terminal result existed,
    // and every observed snapshot is a real phase of the run.
    let observed = observed.lock().expect("progress sink lock");
    assert!(observed.iter().any(|p| p.skipped_total() == 1), "progress never hid the disclosure");
    assert!(
        observed.iter().any(|p| p.step() == BackupStep::ReadingJournal),
        "the journal phase is observable"
    );
    assert!(observed.iter().all(|p| p.skipped_items().len() as u64 <= p.skipped_total()));

    // A run with only Synced history reports no skips at all.
    let clean = run_backup(&engine, BackupPlan::full(), &destination).expect("clean full");
    assert_eq!(clean.skipped_total(), 0);
    assert!(clean.skipped_items().is_empty());
    assert_eq!(clean.manifest().disclosures(), 0);
}

#[test]
fn backup_cancellation_leaves_no_usable_partial_recovery_point() {
    let (_dir, engine) = open_engine("cancel");
    let destination = location("cancel-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let token = Arc::new(Cancellation::new());
    let fired = Arc::new(AtomicU64::new(0));
    let fired_probe = Arc::clone(&fired);
    let cancel_token = Arc::clone(&token);
    let plan = BackupPlan::full()
        .with_member_bound_bytes(4096)
        .with_cancellation(Arc::clone(&token))
        .with_progress(Arc::new(move |progress| {
            fired_probe.fetch_add(1, Ordering::SeqCst);
            if progress.documents_read() > 0 {
                cancel_token.cancel();
            }
        }));
    let outcome = run_backup(&engine, plan, &destination);
    assert_eq!(outcome.err(), Some(BackupError::Cancelled));
    assert!(fired.load(Ordering::SeqCst) > 0, "the observer ran before cancellation");

    // No recovery point exists and none can be discovered: the destination
    // holds no manifest that claims completeness.
    assert_eq!(
        verify_latest_chain(&destination).err(),
        Some(BackupError::ChainIncomplete),
        "no usable partial recovery point remains"
    );
    assert!(
        !destination.read_dir().expect("destination readable").any(|entry| entry
            .expect("entry")
            .file_name()
            .to_string_lossy()
            .starts_with("rp-")),
        "no recovery-point directory was sealed"
    );
}

#[test]
fn backup_admits_one_job_and_rejects_a_concurrent_attempt_with_a_typed_error() {
    let (_dir, engine) = open_engine("admission");
    let engine = Arc::new(engine);
    let destination = location("admission-dest");
    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));

    // The first job's observer holds the run open at an observed phase until
    // the test releases it, so the second attempt overlaps deterministically.
    let (entered_tx, entered_rx) = mpsc::channel::<()>();
    let release = Arc::new((std::sync::Mutex::new(false), std::sync::Condvar::new()));
    let release_waiter = Arc::clone(&release);
    let observer: Arc<dyn Fn(crate::backup::BackupProgress) + Send + Sync> =
        Arc::new(move |_progress: crate::backup::BackupProgress| {
            if entered_tx.send(()).is_ok() {
                let (flag, condvar) = &*release_waiter;
                let mut released = flag.lock().expect("release lock");
                while !*released {
                    released = condvar.wait(released).expect("release wait");
                }
            }
        });
    let held_plan = BackupPlan::full().with_progress(Arc::clone(&observer));
    let engine_for_job = Arc::clone(&engine);
    let job_destination = destination.clone();
    let job = thread::spawn(move || {
        run_backup(&engine_for_job, held_plan, &job_destination).expect("held job completes")
    });

    entered_rx.recv().expect("the first job is running");
    let second = run_backup(&engine, BackupPlan::full(), &destination);
    assert_eq!(
        second.err(),
        Some(BackupError::Throttled),
        "the concurrent attempt is rejected with the admission error"
    );

    let (flag, condvar) = &*release;
    *flag.lock().expect("release lock") = true;
    condvar.notify_all();
    let result = job.join().expect("the first job completes");
    assert!(result.archive().verify(&destination).is_ok());

    // Admission is per source: the slot frees after the first job finished.
    let third = run_backup(&engine, BackupPlan::full(), &destination);
    assert!(third.is_ok(), "the admission slot is released after completion");
}

#[test]
fn backup_throttling_limits_reads_without_reordering_or_dropping_records() {
    let (_dir, engine) = open_engine("throttle");
    let destination = location("throttle-dest");

    for index in 0..6 {
        committed(put(
            &engine,
            "alpha",
            &format!("op-{index}"),
            &format!("doc-{index}"),
            "users",
            text(&format!("value-{index}")),
            LocalDurability::Synced,
        ));
    }
    for index in 0..4 {
        committed(put(
            &engine,
            "beta",
            &format!("op-b-{index}"),
            &format!("doc-{index}"),
            "users",
            text(&format!("beta-{index}")),
            LocalDurability::Synced,
        ));
    }

    // A bounded read rate keeps every record and the canonical member order;
    // the bound must never reorder or drop. The run also exercises the delay
    // path itself, but no wall-clock assertion is made here: timing facts
    // belong to the benchmark and recovery lanes, not focused tests.
    let bounded = run_backup(
        &engine,
        BackupPlan::full().with_read_ops_per_second(500).with_member_bound_bytes(4096),
        &destination,
    )
    .expect("throttled backup runs");

    assert_eq!(bounded.manifest().documents(), 10, "no record is dropped");
    assert!(bounded.source_reads() >= 2, "read operations are counted");
    bounded.archive().verify(&destination).expect("the throttled archive verifies");
    let members = bounded.manifest().members();
    for window in members.windows(2) {
        assert!(window[0].name() < window[1].name(), "member order is canonical");
    }

    let unbounded =
        run_backup(&engine, BackupPlan::full().with_member_bound_bytes(4096), &destination)
            .expect("unbounded comparison run");
    assert_eq!(unbounded.manifest().documents(), 10);
    assert_eq!(
        unbounded.manifest().identity(),
        bounded.manifest().identity(),
        "the throttle changes no archive content"
    );

    // A zero bound throttles everything and fails closed before any read.
    let zero = run_backup(&engine, BackupPlan::full().with_read_ops_per_second(0), &destination);
    assert_eq!(zero.err(), Some(BackupError::Throttled));
}

#[test]
fn manifest_validation_rejects_path_traversal_and_in_progress_manifests() {
    let (_dir, engine) = open_engine("manifest-validation");
    let destination = location("manifest-validation-dest");
    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let result = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup runs");
    let identity = result.manifest().identity().to_owned();

    // A traversal member name is rejected by validation, whatever the
    // identity line claims.
    let point_dir = destination.join(format!("rp-{identity}"));
    let manifest_path = point_dir.join("manifest.tbv2m");
    let original = std::fs::read(&manifest_path).expect("reads the manifest");
    let mut traversal = original.clone();
    patch_member_name(&mut traversal, "../escape");
    std::fs::write(&manifest_path, &traversal).expect("writes the traversal manifest");
    assert_eq!(
        load_recovery_point(&destination, &identity).err(),
        Some(BackupError::ManifestInvalid),
        "path traversal in a member name fails validation"
    );
    assert_eq!(
        verify_latest_chain(&destination).err(),
        Some(BackupError::ManifestInvalid),
        "discovery fails closed on an invalid manifest"
    );

    // An in-progress manifest never validates as a recovery point.
    let partial_dir = destination.join(format!("rp-{}", "f".repeat(64)));
    std::fs::create_dir_all(&partial_dir).expect("creates a point directory");
    std::fs::write(
        partial_dir.join("manifest.tbv2m.inprogress"),
        format!("{}\nin_progress true\n", crate::backup::manifest::HEADER),
    )
    .expect("writes an in-progress manifest");
    assert_eq!(
        load_recovery_point(&destination, &"f".repeat(64)).err(),
        Some(BackupError::ManifestInvalid),
        "an in-progress manifest is rejected"
    );

    // The sealed original still validates once restored byte for byte.
    std::fs::write(&manifest_path, &original).expect("restores the manifest");
    assert!(load_recovery_point(&destination, &identity).is_ok());
}

/// Rewrites the first member name in serialized manifest bytes. The test
/// only needs a traversal name to reach validation; it does not fix the
/// identity line, because name validation must reject the member before
/// identity comparison regardless.
fn patch_member_name(bytes: &mut Vec<u8>, replacement: &str) {
    let content = String::from_utf8_lossy(bytes).into_owned();
    let mut lines = content.split('\n').map(str::to_owned).collect::<Vec<_>>();
    for line in &mut lines {
        if let Some(rest) = line.strip_prefix("member ") {
            let mut parts = rest.splitn(2, ' ');
            let _name = parts.next();
            let tail = parts.next().unwrap_or("");
            *line = format!("member {replacement} {tail}");
            break;
        }
    }
    let joined = lines.join("\n");
    bytes.clear();
    bytes.extend_from_slice(joined.as_bytes());
}

#[test]
fn incremental_backup_appends_to_its_base_chain_with_the_journal_range_boundary() {
    let (_dir, engine) = open_engine("incremental-append");
    let destination = location("incremental-append-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("base full backup");
    let base = full.manifest().boundary_sequence();

    let put_sequence = committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    committed(delete(&engine, "alpha", "op-3", "doc-1"));
    let first_incremental = run_backup(&engine, BackupPlan::incremental_since(base), &destination)
        .expect("first incremental");
    let first = first_incremental.manifest();
    assert_eq!(first.kind(), BackupKind::Incremental { base });
    assert_eq!(first.base_sequence(), Some(base));
    assert_eq!(
        first.boundary_sequence().get(),
        put_sequence.get() + 1,
        "the boundary covers the journal range through the delete"
    );
    assert_eq!(first.documents(), 1, "the put is journaled");
    assert_eq!(first.deletes(), 1, "the delete is journaled");

    committed(put(
        &engine,
        "alpha",
        "op-4",
        "doc-3",
        "users",
        text("three"),
        LocalDurability::Synced,
    ));
    let second =
        run_backup(&engine, BackupPlan::incremental_since(first.boundary_sequence()), &destination)
            .expect("second incremental appends to the chain");

    let chain = verify_latest_chain(&destination).expect("the chain verifies");
    assert_eq!(chain.len(), 3, "full -> incremental -> incremental");
    assert_eq!(chain[0].kind(), BackupKind::Full);
    assert_eq!(chain[1].base_sequence(), Some(base));
    assert_eq!(chain[2].base_sequence(), Some(first.boundary_sequence()));
    assert_eq!(chain[2].identity(), second.manifest().identity());
    assert!(
        chain.windows(2).all(|w| w[0].boundary_sequence() <= w[1].base_sequence().expect("link"))
    );
    assert!(second.archive().verify(&destination).is_ok());
}

#[test]
fn incremental_chain_verification_rejects_a_missing_or_reordered_link() {
    let (_dir, engine) = open_engine("chain-gaps");
    let destination = location("chain-gaps-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("full");
    let base = full.manifest().boundary_sequence();
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    let first =
        run_backup(&engine, BackupPlan::incremental_since(base), &destination).expect("inc 1");
    committed(put(
        &engine,
        "alpha",
        "op-3",
        "doc-3",
        "users",
        text("three"),
        LocalDurability::Synced,
    ));
    let second = run_backup(
        &engine,
        BackupPlan::incremental_since(first.manifest().boundary_sequence()),
        &destination,
    )
    .expect("inc 2");
    assert!(verify_latest_chain(&destination).is_ok());

    // Remove the middle link: the newest incremental names a base boundary
    // no recovery point provides, and verification must fail closed.
    std::fs::remove_dir_all(destination.join(format!("rp-{}", first.manifest().identity())))
        .expect("removes the middle link");
    assert_eq!(
        verify_latest_chain(&destination).err(),
        Some(BackupError::ChainIncomplete),
        "a missing chain link is rejected"
    );
    let _ = second;
}

#[test]
fn tampered_member_bytes_fail_verification_with_checksum_mismatch() {
    let (_dir, engine) = open_engine("tamper");
    let destination = location("tamper-dest");
    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    let result = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup");
    assert!(result.archive().verify(&destination).is_ok());

    let point_dir = destination.join(format!("rp-{}", result.manifest().identity()));
    let member_name = result.manifest().members()[0].name().to_owned();
    let member_path = point_dir.join(&member_name);
    let original = std::fs::read(&member_path).expect("reads the member");
    let mut tampered = original.clone();
    tampered[0] ^= 0xff;
    std::fs::write(&member_path, &tampered).expect("writes tampered bytes");

    assert_eq!(
        result.archive().verify(&destination).err(),
        Some(BackupError::ChecksumMismatch),
        "tampered member bytes fail the digest check"
    );
    assert_eq!(
        verify_latest_chain(&destination).err(),
        Some(BackupError::ChecksumMismatch),
        "chain verification fails closed on tampering"
    );
}

#[test]
fn truncated_and_missing_members_fail_verification_with_typed_errors() {
    let (_dir, engine) = open_engine("truncate");
    let destination = location("truncate-dest");
    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two-value-two"),
        LocalDurability::Synced,
    ));
    let result = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup");

    let point_dir = destination.join(format!("rp-{}", result.manifest().identity()));
    let member_name = result.manifest().members()[0].name().to_owned();
    let member_path = point_dir.join(&member_name);

    // Truncation: fewer bytes than the bound length.
    let original = std::fs::read(&member_path).expect("reads the member");
    assert!(original.len() > 1);
    std::fs::write(&member_path, &original[..original.len() - 1]).expect("truncates");
    assert_eq!(
        result.archive().verify(&destination).err(),
        Some(BackupError::ChecksumMismatch),
        "a truncated member fails the length and digest check"
    );

    // Missing: the member file is gone entirely.
    std::fs::remove_file(&member_path).expect("removes the member");
    assert_eq!(
        result.archive().verify(&destination).err(),
        Some(BackupError::ChainIncomplete),
        "a missing member is an incomplete point"
    );
    assert_eq!(
        verify_latest_chain(&destination).err(),
        Some(BackupError::ChainIncomplete),
        "chain verification fails closed on the missing member"
    );
}

#[test]
fn incremental_with_an_unservable_base_fails_closed() {
    let (_dir, engine) = open_engine("unservable");
    let destination = location("unservable-dest");
    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));

    let beyond = run_backup(
        &engine,
        BackupPlan::incremental_since(StorageSequence::new(u64::MAX / 2)),
        &destination,
    );
    assert_eq!(
        beyond.err(),
        Some(BackupError::ChainIncomplete),
        "a base beyond the source boundary is an unusable chain request"
    );

    // A base in the future leaves no recovery point behind.
    assert_eq!(verify_latest_chain(&destination).err(), Some(BackupError::ChainIncomplete));
}

#[test]
fn snapshot_backup_pins_its_boundary_and_stays_self_contained() {
    let (_dir, engine) = open_engine("snapshot");
    let destination = location("snapshot-dest");

    let pinned = committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));

    let snapshot = run_backup(&engine, BackupPlan::snapshot_at(pinned), &destination)
        .expect("snapshot backup runs");
    let manifest = snapshot.manifest();
    assert_eq!(manifest.kind(), BackupKind::Snapshot);
    assert_eq!(manifest.boundary_sequence(), pinned, "the pinned boundary is honored");
    assert_eq!(manifest.documents(), 1, "only the pinned boundary's documents are archived");
    assert!(manifest.base_sequence().is_none(), "a snapshot is not incremental");

    let chain = verify_latest_chain(&destination).expect("the snapshot is its own complete chain");
    assert_eq!(chain.len(), 1, "a snapshot verifies standalone with no chain context");
    assert_eq!(chain[0].kind(), BackupKind::Snapshot);

    // The same source at the latest boundary is a different kind with the
    // later boundary.
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup");
    assert_eq!(full.manifest().kind(), BackupKind::Full);
    assert!(full.manifest().boundary_sequence().get() > manifest.boundary_sequence().get());
    assert_eq!(full.manifest().documents(), 2);
}

#[test]
fn snapshot_backup_refuses_a_boundary_below_the_retained_history_window() {
    let (_dir, engine) = open_engine("snapshot-below-floor");
    let destination = location("snapshot-below-floor-dest");
    let first = committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-3",
        "doc-3",
        "users",
        text("three"),
        LocalDurability::Synced,
    ));
    engine
        .compact(crate::storage::CompactionRequest::new(
            crate::storage::CompactionPolicy::ReclaimHistory,
        ))
        .expect("history compaction runs");

    let result = run_backup(&engine, BackupPlan::snapshot_at(first), &destination);
    assert_eq!(
        result.err(),
        Some(BackupError::SourceUnavailable),
        "a cut below the retained history floor cannot be reconstructed"
    );
}

#[test]
fn backup_preserves_tenant_scopes_in_per_tenant_counts() {
    let (_dir, engine) = open_engine("tenants");
    let destination = location("tenants-dest");

    committed(put(
        &engine,
        "alpha",
        "op-a",
        "doc-1",
        "users",
        text("alpha-one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "beta",
        "op-b",
        "doc-1",
        "users",
        text("beta-one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "beta",
        "op-b2",
        "doc-2",
        "users",
        text("beta-two"),
        LocalDurability::Synced,
    ));

    let all = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup");
    assert_eq!(all.manifest().tenants().len(), 2);
    let alpha =
        all.manifest().tenants().iter().find(|t| t.tenant().as_str() == "alpha").expect("alpha");
    let beta =
        all.manifest().tenants().iter().find(|t| t.tenant().as_str() == "beta").expect("beta");
    assert_eq!(alpha.documents(), 1);
    assert_eq!(beta.documents(), 2);

    // Item selection limits the backup to the selected tenant's scope only.
    let selected =
        run_backup(&engine, BackupPlan::full().with_tenants([tenant("beta")]), &destination)
            .expect("selected backup");
    let tenants = selected.manifest().tenants();
    assert_eq!(tenants.len(), 1);
    assert_eq!(tenants[0].tenant().as_str(), "beta");
    assert_eq!(tenants[0].documents(), 2);
    assert_eq!(selected.manifest().documents(), 2, "no cross-tenant document leaks in");
    assert!(selected.archive().verify(&destination).is_ok());
}

#[test]
fn repeated_backup_at_the_same_boundary_is_idempotent() {
    let (_dir, engine) = open_engine("idempotent");
    let first_destination = location("idempotent-dest-a");
    let second_destination = location("idempotent-dest-b");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));

    let first = run_backup(&engine, BackupPlan::full(), &first_destination).expect("first run");
    let second =
        run_backup(&engine, BackupPlan::full(), &first_destination).expect("same destination");
    let elsewhere =
        run_backup(&engine, BackupPlan::full(), &second_destination).expect("other destination");

    assert_eq!(
        first.manifest().identity(),
        second.manifest().identity(),
        "an uncertain result cannot mint two recovery-point identities"
    );
    assert_eq!(
        first.manifest().identity(),
        elsewhere.manifest().identity(),
        "identity is content-derived, not location-derived"
    );
    assert!(load_recovery_point(&first_destination, first.manifest().identity()).is_ok());

    // The repeated run sealed exactly one point directory, not a conflict.
    let points = first_destination
        .read_dir()
        .expect("reads the destination")
        .filter(|entry| {
            entry.as_ref().is_ok_and(|e| e.file_name().to_string_lossy().starts_with("rp-"))
        })
        .count();
    assert_eq!(points, 1);
}

/// Counts the `rp-` directories one destination holds.
fn point_directories(destination: &std::path::Path) -> Vec<String> {
    destination
        .read_dir()
        .expect("reads the destination")
        .filter_map(|entry| {
            let name = entry.expect("entry").file_name();
            let name = name.to_string_lossy().into_owned();
            name.strip_prefix("rp-").map(str::to_owned)
        })
        .collect()
}

#[test]
fn a_torn_publish_orphan_is_repaired_by_the_identical_retry() {
    let (_dir, engine) = open_engine("torn-publish");
    let rehearsal = location("torn-publish-rehearsal");
    let destination = location("torn-publish-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));

    // Identity is content-derived, so a rehearsal run names the recovery
    // point the interrupted run was sealing.
    let reference = run_backup(&engine, BackupPlan::full(), &rehearsal).expect("rehearsal run");
    let identity = reference.manifest().identity().to_owned();
    let sealed_point = rehearsal.join(format!("rp-{identity}"));

    // Simulate the crash window a non-atomic publish leaves behind: the
    // destination's `rp-<identity>` directory exists, holds members, and has
    // no sealed manifest.
    let orphan = destination.join(format!("rp-{identity}"));
    std::fs::create_dir_all(&orphan).expect("creates the orphan directory");
    for member in reference.manifest().members() {
        std::fs::copy(sealed_point.join(member.name()), orphan.join(member.name()))
            .expect("copies a staged member into the orphan");
    }
    assert!(
        !orphan.join("manifest.tbv2m").exists(),
        "the orphan is exactly the unsealed crash state"
    );

    // The identical retry must succeed and seal that same point: the crash
    // window must not brick the destination against its own recovery point.
    let retry = run_backup(&engine, BackupPlan::full(), &destination).expect("retry seals");
    assert_eq!(retry.manifest().identity(), identity);
    assert!(retry.archive().verify(&destination).is_ok(), "the retry's archive verifies");
    assert!(load_recovery_point(&destination, &identity).is_ok(), "the point reloads");
    assert_eq!(point_directories(&destination), vec![identity], "exactly one point remains");
}

#[test]
fn discovery_ignores_unsealed_orphan_directories() {
    let (_dir, engine) = open_engine("orphan-discovery");
    let destination = location("orphan-discovery-dest");
    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let sealed = run_backup(&engine, BackupPlan::full(), &destination).expect("seals a point");
    let identity = sealed.manifest().identity().to_owned();
    let boundary = sealed.manifest().boundary_sequence();

    // An unsealed orphan beside the sealed point: a valid identity directory
    // holding member bytes and no manifest.
    let orphan_identity = "ab".repeat(32);
    let orphan = destination.join(format!("rp-{orphan_identity}"));
    std::fs::create_dir_all(&orphan).expect("creates the orphan directory");
    std::fs::write(orphan.join("member-000001"), b"staged bytes").expect("stages a member");

    // Discovery skips the orphan rather than failing the whole destination;
    // a sealed manifest that does not validate still fails closed.
    let chain = verify_latest_chain(&destination).expect("the sealed chain still verifies");
    assert_eq!(chain.len(), 1);
    assert_eq!(chain[0].identity(), identity);
    assert_eq!(
        load_recovery_point(&destination, &orphan_identity).err(),
        Some(BackupError::ManifestInvalid),
        "an unsealed orphan is still not a loadable recovery point"
    );

    // A destination holding only an orphan has no recovery point at all —
    // not an invalid destination.
    let only_orphan = location("orphan-only-dest");
    std::fs::create_dir_all(only_orphan.join(format!("rp-{orphan_identity}")))
        .expect("creates the orphan directory");
    std::fs::write(only_orphan.join(format!("rp-{orphan_identity}")).join("member-000001"), b"x")
        .expect("stages a member");
    assert_eq!(
        verify_latest_chain(&only_orphan).err(),
        Some(BackupError::ChainIncomplete),
        "an orphan alone is no recovery point"
    );

    // An incremental still anchors on the sealed base beside the orphan.
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    let incremental = run_backup(&engine, BackupPlan::incremental_since(boundary), &destination)
        .expect("anchors beside the orphan");
    assert_eq!(incremental.manifest().base_sequence(), Some(boundary));
    assert_eq!(verify_latest_chain(&destination).expect("chain verifies").len(), 2);
}

#[test]
fn tenants_with_interior_whitespace_round_trip_through_the_manifest() {
    let (_dir, engine) = open_engine("whitespace-tenant");
    let destination = location("whitespace-tenant-dest");

    // `TenantId` admits interior whitespace, colons, and digits, so the
    // manifest's tenant line must encode the name unambiguously.
    committed(put(
        &engine,
        "team a",
        "op-a",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "tenant:2 99 x",
        "op-b",
        "doc-1",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "gamma",
        "op-c",
        "doc-1",
        "users",
        text("three"),
        LocalDurability::Synced,
    ));

    let result = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup runs");
    let manifest = result.manifest();
    assert_eq!(
        crate::backup::BackupManifest::decode(&manifest.encode()).expect("canonical round trip"),
        *manifest,
    );
    let names: Vec<&str> = manifest.tenants().iter().map(|t| t.tenant().as_str()).collect();
    assert_eq!(names.len(), 3);
    assert!(names.contains(&"team a"), "the whitespace tenant is bound: {names:?}");
    assert!(names.contains(&"tenant:2 99 x"), "the colon tenant is bound: {names:?}");
    for tenant in manifest.tenants() {
        assert_eq!(tenant.documents(), 1, "every tenant keeps its own count");
    }
    result.archive().verify(&destination).expect("the archive verifies");

    // The run's success must not be a lie about what can be recovered: the
    // sealed manifest reloads with every name intact.
    let reloaded =
        load_recovery_point(&destination, manifest.identity()).expect("the manifest reloads");
    assert_eq!(reloaded, *manifest);
    assert_eq!(
        reloaded.tenants().iter().map(|t| t.tenant().as_str()).collect::<Vec<_>>(),
        names,
        "the encoded names parse back exactly"
    );

    // A chain over the same source reloads and verifies too.
    committed(put(
        &engine,
        "team a",
        "op-d",
        "doc-2",
        "users",
        text("four"),
        LocalDurability::Synced,
    ));
    let incremental = run_backup(
        &engine,
        BackupPlan::incremental_since(manifest.boundary_sequence()),
        &destination,
    )
    .expect("incremental runs");
    assert!(load_recovery_point(&destination, incremental.manifest().identity()).is_ok());
    assert_eq!(verify_latest_chain(&destination).expect("chain verifies").len(), 2);
}

#[test]
fn record_frames_bind_the_commit_durability_into_the_archive_digest() {
    let (_dir_a, engine_a) = open_engine("durability-synced");
    let (_dir_b, engine_b) = open_engine("durability-buffered");
    let synced_destination = location("durability-synced-dest");
    let buffered_destination = location("durability-buffered-dest");

    // The same logical document committed at two local durabilities, with
    // nothing else differing: the archive bytes are the only place the
    // durability fact can live, so the member digest must bind it.
    committed(put(
        &engine_a,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine_b,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Buffered,
    ));

    let synced =
        run_backup(&engine_a, BackupPlan::full(), &synced_destination).expect("synced run");
    let buffered =
        run_backup(&engine_b, BackupPlan::full(), &buffered_destination).expect("buffered run");

    assert_eq!(synced.manifest().documents(), 1);
    assert_eq!(buffered.manifest().documents(), 1);
    assert_eq!(
        synced.manifest().members().len(),
        buffered.manifest().members().len(),
        "the frame layout is unchanged in member count"
    );
    for (synced_member, buffered_member) in
        synced.manifest().members().iter().zip(buffered.manifest().members().iter())
    {
        assert_eq!(
            synced_member.length(),
            buffered_member.length(),
            "one durability tag adds no variable length"
        );
        assert_ne!(
            synced_member.digest(),
            buffered_member.digest(),
            "the durability fact is bound into the digest-covered archive bytes"
        );
    }
    assert!(synced.archive().verify(&synced_destination).is_ok());
    assert!(buffered.archive().verify(&buffered_destination).is_ok());
}

#[test]
fn progress_disclosures_are_bounded_while_the_total_stays_exact() {
    let (_dir, engine) = open_engine("progress-window");
    let destination = location("progress-window-dest");
    committed(put(
        &engine,
        "alpha",
        "op-seed",
        "doc-0",
        "users",
        text("seed"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("base full");
    let boundary = full.manifest().boundary_sequence();

    // More disclosed records than the progress window, in one tenant and one
    // journal page, so the snapshot after the page observes every fact.
    let total = 72usize;
    for index in 1..=total {
        committed(put(
            &engine,
            "alpha",
            &format!("op-buffered-{index}"),
            &format!("doc-{index}"),
            "users",
            text(&format!("value-{index}")),
            LocalDurability::Buffered,
        ));
    }

    let observed: Arc<std::sync::Mutex<Vec<crate::backup::BackupProgress>>> =
        Arc::new(std::sync::Mutex::new(Vec::new()));
    let sink = Arc::clone(&observed);
    let plan = BackupPlan::incremental_since(boundary).with_progress(Arc::new(move |progress| {
        sink.lock().expect("progress sink lock").push(progress);
    }));
    let result = run_backup(&engine, plan, &destination).expect("incremental runs");

    let observed = observed.lock().expect("progress sink lock");
    let widest = observed.iter().map(|p| p.skipped_items().len()).max().unwrap_or(0);
    assert!(
        widest < total,
        "no snapshot clones the whole disclosure list (widest window {widest} of {total})"
    );
    let after_all = observed
        .iter()
        .rev()
        .find(|p| p.skipped_total() == total as u64)
        .expect("a snapshot after the last disclosure");
    assert_eq!(
        after_all.skipped_items().last().expect("the newest disclosure").document().as_str(),
        format!("doc-{total}"),
        "the window carries the newest facts"
    );
    assert!(
        after_all.skipped_items().iter().all(|skip| skip.document().as_str() != "doc-1"),
        "the window is a suffix, so the oldest fact has rotated out"
    );

    // The exact total is never truncated anywhere, and the terminal result
    // still carries the complete list.
    assert!(observed.iter().any(|p| p.skipped_total() == total as u64));
    assert_eq!(result.skipped_total(), total as u64);
    assert_eq!(result.skipped_items().len(), total);
    assert!(result.skipped_items().iter().any(|s| s.document().as_str() == "doc-1"));
    assert_eq!(result.manifest().disclosures(), total as u64);
}

// ---------------------------------------------------------------------------
// Tombstone retention watermark (GAP-R02)
// ---------------------------------------------------------------------------

/// Builds one shared watermark whose history bound sits at `bound`, with
/// the window's deletion evidence extracted from the engine's journal —
/// exactly what the storage compaction path will declare in production,
/// and what no engine component declares today.
///
/// The declaration carries this engine's own durable head (decision D2.4:
/// a declared bound may not exceed it), so the handle is honest for the
/// engine it was declared against — a caller reusing it against another
/// engine is the run gate's to refuse.
fn watermark_at(
    engine: &Engine,
    bound: StorageSequence,
) -> crate::replication::SharedTombstoneRetention {
    use crate::replication::StampedCommit;
    use crate::replication::{ReplicationClockStamp, SharedTombstoneRetention};

    let handle = SharedTombstoneRetention::new();
    let mut window = Vec::new();
    for tenant in engine.committed_tenants().expect("committed tenants") {
        let mut after = StorageSequence::default();
        loop {
            let page = engine.tenant_commits(&tenant, after, 1024).expect("journal page");
            if page.is_empty() {
                break;
            }
            let reached = page.last().is_none_or(|commit| commit.sequence().get() >= bound.get());
            for commit in &page {
                if commit.sequence().get() < bound.get() {
                    window
                        .push(StampedCommit::new(ReplicationClockStamp::new(0, 0), commit.clone()));
                }
            }
            after = page.last().map_or(after, crate::storage::PublishedCommit::sequence);
            if reached {
                break;
            }
        }
    }
    handle.with_lock(|watermark| {
        let head = engine.journaled_high_water().expect("high water").unwrap_or_default();
        watermark
            .declare_history_bound_from_commits(bound, head, &window, true)
            .expect("the window declares cleanly");
    });
    handle
}

/// One `(collection, identifier, value)` fact of a restored document.
type KeyedValue = (String, String, DocumentValue);

/// Reads one tenant's live documents back from a restored location.
fn read_back(location: &PathBuf, tenant_name: &str) -> Vec<KeyedValue> {
    // Restored locations are root-format (FileStorage staging); the LSM
    // restore path is recorded follow-up work.
    let engine = Engine::open(
        EngineConfig::durable(location)
            .expect("durable location")
            .with_storage_backend(crate::engine::StorageBackend::RootFiles),
    )
    .expect("reopens");
    let tenant_scope = scope(&engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let documents = engine.snapshot_documents(&tenant_scope, &snapshot).expect("reads back");
    engine.shutdown().expect("shuts down");
    documents
        .into_iter()
        .map(|document| {
            (
                document.collection().as_str().to_owned(),
                document.id().as_str().to_owned(),
                document.value().clone(),
            )
        })
        .collect()
}

fn sorted(values: Vec<KeyedValue>) -> Vec<KeyedValue> {
    let mut values = values;
    values.sort_by(|left, right| {
        (left.0.as_str(), left.1.as_str()).cmp(&(right.0.as_str(), right.1.as_str()))
    });
    values
}

#[test]
fn an_incremental_below_the_history_bound_restores_without_resurrecting() {
    let (_dir, engine) = open_engine("gapr02-chain");
    let destination = location("gapr02-chain-dest");
    let target = location("gapr02-chain-target");

    // The chain's root covers the first two publications.
    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("the chain root");
    let base = full.manifest().boundary_sequence();

    // While the chain rests, the source deletes a document and writes a new
    // one, then gives up the history below the newer publications.
    let delete_at = committed(delete(&engine, "alpha", "op-3", "doc-1"));
    let put_at = committed(put(
        &engine,
        "alpha",
        "op-4",
        "doc-3",
        "users",
        text("three"),
        LocalDurability::Synced,
    ));
    assert!(delete_at > base, "the delete is above the chain's base");
    let retention = watermark_at(&engine, put_at);
    retention.with_lock(|watermark| {
        assert_eq!(watermark.held_evidence_count(), 1, "the delete is retained");
        assert_eq!(
            watermark.non_deletion_through(&tenant("alpha")),
            Some(base),
            "no put below the bound is above the chain's base"
        );
    });

    // The incremental below the declared bound archives the retained delete
    // and the journal range above the bound.
    let incremental = run_backup(
        &engine,
        BackupPlan::incremental_since(base).with_retention(retention.clone()),
        &destination,
    )
    .expect("the below-bound link archives the retained evidence");
    assert_eq!(incremental.manifest().deletes(), 1, "the retained delete is a bound fact");
    assert_eq!(incremental.manifest().documents(), 1, "doc-3 is the only archived document");
    assert_eq!(incremental.manifest().base_sequence(), Some(base));
    assert!(verify_latest_chain(&destination).is_ok(), "the chain verifies");

    // The chain restores without resurrecting the deleted document.
    let receipt = crate::backup::RestorePlan::restore_latest(&destination, &target)
        .expect("chain restore runs");
    assert_eq!(receipt.chain_depth(), 2);
    assert_eq!(receipt.expected_deletes(), 1);
    assert_eq!(receipt.decoded_deletes(), 1);
    assert_eq!(receipt.restored_documents(), 2, "doc-2 and doc-3 survive");
    assert_eq!(
        sorted(read_back(&target, "alpha")),
        sorted(vec![
            ("users".to_owned(), "doc-2".to_owned(), text("two")),
            ("users".to_owned(), "doc-3".to_owned(), text("three")),
        ]),
        "doc-1 stays deleted: the retained evidence survived the bound"
    );
}

#[test]
fn an_incremental_below_the_bound_fails_closed_when_the_window_holds_a_missed_put() {
    let (_dir, engine) = open_engine("gapr02-missed-put");
    let destination = location("gapr02-missed-put-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("the chain root");
    let base = full.manifest().boundary_sequence();

    // A put the chain has not seen lands below the declared bound, so the
    // window is not provable from deletion evidence alone.
    let missed = committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    let after = committed(delete(&engine, "alpha", "op-3", "doc-2"));
    let retention = watermark_at(&engine, after);
    retention.with_lock(|watermark| {
        assert_eq!(watermark.non_deletion_through(&tenant("alpha")), Some(missed));
    });

    let error = run_backup(
        &engine,
        BackupPlan::incremental_since(base).with_retention(retention),
        &destination,
    )
    .expect_err("a put the chain missed below the bound is not provable");
    assert_eq!(error, BackupError::SourceUnavailable, "the failure is typed and fail-closed");
}

#[test]
fn an_incremental_below_the_bound_fails_closed_when_the_floor_is_above_the_base() {
    let (_dir, engine) = open_engine("gapr02-floor");
    let destination = location("gapr02-floor-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("the chain root");
    let base = full.manifest().boundary_sequence();

    committed(delete(&engine, "alpha", "op-3", "doc-1"));
    let boundary = engine.journaled_high_water().expect("high water").unwrap_or_default();
    let retention = watermark_at(&engine, boundary);
    // The watermark already discarded the evidence below the chain's base:
    // the chain is not servable from this source anymore.
    retention
        .with_lock(|watermark| watermark.advance_floor(boundary).expect("no consumer blocks it"));

    let error = run_backup(
        &engine,
        BackupPlan::incremental_since(base).with_retention(retention),
        &destination,
    )
    .expect_err("a base below the retention floor is not provable");
    assert_eq!(error, BackupError::SourceUnavailable);
}

#[test]
fn an_incremental_run_records_its_chain_anchor_on_the_watermark() {
    let (_dir, engine) = open_engine("gapr02-anchor");
    let destination = location("gapr02-anchor-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("the chain root");
    let base = full.manifest().boundary_sequence();

    committed(delete(&engine, "alpha", "op-2", "doc-1"));
    let boundary = engine.journaled_high_water().expect("high water").unwrap_or_default();
    let retention = watermark_at(&engine, boundary);

    run_backup(
        &engine,
        BackupPlan::incremental_since(base).with_retention(retention.clone()),
        &destination,
    )
    .expect("the link archives");

    let consumers = retention.with_lock(|watermark| watermark.consumers());
    let anchor = consumers
        .iter()
        .find_map(|(consumer, floor)| match consumer {
            crate::replication::RetentionConsumer::BackupChain { identity } => {
                Some((identity.clone(), *floor))
            }
            _ => None,
        })
        .expect("the run recorded its chain's anchor");
    assert_eq!(
        anchor,
        (full.manifest().identity().to_owned(), Some(base)),
        "the anchor is the base recovery point at its boundary"
    );

    // The chain's anchor holds the watermark: its deletion evidence cannot
    // be discarded below the boundary while the chain is anchored there.
    let refusal = retention
        .with_lock(|watermark| watermark.advance_floor(boundary))
        .expect_err("the chain still needs the evidence above its base");
    assert!(matches!(refusal, crate::replication::RetentionRefusal::ConsumerBelowFloor { .. }));
}

#[test]
fn an_incremental_refuses_a_declared_bound_past_the_journals_durable_head() {
    // Decision D2.4 (GAP-R02): a declared bound may not exceed the journal's
    // durable head — the window must fully exist. The watermark here was
    // declared honestly against another engine's journal (the one way an
    // over-range bound reaches a plan): this run's source cannot cover it,
    // and the run must fail closed instead of serving from a phantom bound
    // whose (head, bound) range is in neither the journal nor the retained
    // evidence — the exact range a chain would silently miss.
    let (_dir_a, engine_a) = open_engine("gapr02-foreign-window");
    committed(put(
        &engine_a,
        "alpha",
        "f-op-1",
        "f-doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine_a,
        "alpha",
        "f-op-2",
        "f-doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    // Only deletes follow, so a chain based at sequence two stays provable
    // from the deletion evidence alone: the over-range bound is the one
    // fact that must refuse this run.
    for index in 3..=7 {
        committed(delete(&engine_a, "alpha", &format!("f-op-{index}"), "f-doc-1"));
    }
    let head_a = engine_a.journaled_high_water().expect("high water").unwrap_or_default();
    assert_eq!(head_a.get(), 7, "the foreign journal covers both bounds");

    let (_dir_b, engine_b) = open_engine("gapr02-short-journal");
    committed(put(
        &engine_b,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine_b,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    let destination = location("gapr02-over-range-dest");
    let full = run_backup(&engine_b, BackupPlan::full(), &destination).expect("the chain root");
    let base = full.manifest().boundary_sequence();
    let head_b = engine_b.journaled_high_water().expect("high water").unwrap_or_default();
    assert_eq!(base, head_b, "the chain is anchored at this journal's head");

    for over in [1, 5] {
        let bound = StorageSequence::new(head_b.get() + over);
        assert!(
            bound.get() <= head_a.get(),
            "the foreign declaration was honest where it was made"
        );
        let retention = watermark_at(&engine_a, bound);
        let error = run_backup(
            &engine_b,
            BackupPlan::incremental_since(base).with_retention(retention),
            &destination,
        )
        .expect_err("a declared bound past this journal's head is not provable");
        assert_eq!(
            error,
            BackupError::SourceUnavailable,
            "head+{over} does not fully exist in this source"
        );
    }
}
