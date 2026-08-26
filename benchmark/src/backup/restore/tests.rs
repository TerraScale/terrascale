//! In-crate restore and recovery-receipt tests (BK-02).
//!
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the data-bearing restore drills live in-crate
//! with the `TenantScope::from_parts` fixture, mirroring the backup suite at
//! `src/backup/tests.rs`. Every assertion goes through the public restore
//! entries on [`crate::backup::RestorePlan`] and the real durable `Engine`
//! seam: restore selection, verified execution into a fresh engine location,
//! fail-closed corruption handling, restart-after-restore, and the
//! secret-safe Recovery Receipt facts.
//!
//! These drills run in the exclusive recovery lane (BK-02 owns the lane for
//! this wave). They make no performance claim and no claim beyond the
//! observed scenarios; the tracked receipts under `evidence/recovery/`
//! record what ran.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use crate::backup::{
    BackupError, BackupKind, BackupPlan, RestorePlan, run_backup, verify_latest_chain,
};
use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    let path = std::env::temp_dir()
        .join(format!("tbv2-bk02-restore-{tag}-{}-{index}", std::process::id()));
    // A recycled pid could collide with a crashed earlier run's leftover;
    // every location is test-owned, so start from a clean path.
    let _ = std::fs::remove_dir_all(&path);
    path
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
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

/// One `(collection, identifier, value)` fact of a restored document.
type KeyedValue = (String, String, DocumentValue);

/// Reads one tenant's live documents through a real durable `Engine` opened
/// at `location`, as the restart-after-restore read-back path does.
fn read_back(location: &Path, tenant_name: &str) -> Vec<KeyedValue> {
    let engine =
        Engine::open(EngineConfig::durable(location).expect("durable location")).expect("reopens");
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

fn sorted(mut values: Vec<KeyedValue>) -> Vec<KeyedValue> {
    values.sort_by(|left, right| (&left.0, &left.1).cmp(&(&right.0, &right.1)));
    values
}

fn member_path(destination: &Path, identity: &str, name: &str) -> PathBuf {
    destination.join(format!("rp-{identity}")).join(name)
}

fn manifest_path(destination: &Path, identity: &str) -> PathBuf {
    destination.join(format!("rp-{identity}")).join(crate::backup::manifest::MANIFEST_FILE)
}

/// Rewrites the identity line of serialized manifest bytes so the manifest
/// stays internally consistent — the exact state a forger with full write
/// access to the destination produces. Validation of the body still applies;
/// this helper never weakens it.
fn rebind_identity(original: &[u8]) -> Vec<u8> {
    let content = String::from_utf8_lossy(original).into_owned();
    let mut lines = content.split('\n').map(str::to_owned).collect::<Vec<_>>();
    // The body is every line after the identity line except the trailing
    // empty element the final newline produces; the digest covers exactly
    // the body lines joined by newlines, with no trailing newline.
    let body = lines[2..lines.len() - 1].join("\n");
    let mut hasher = crate::backup::sha256::Sha256::new();
    hasher.update(body.as_bytes());
    lines[1] = format!("identity {}", crate::backup::sha256::to_hex(&hasher.finish()));
    lines.join("\n").into_bytes()
}

/// Replaces the digest of the first bound member in serialized manifest
/// bytes, leaving the name and length untouched.
fn rebind_first_member_digest(original: &[u8], digest: &str) -> Vec<u8> {
    let content = String::from_utf8_lossy(original).into_owned();
    let mut lines = content.split('\n').map(str::to_owned).collect::<Vec<_>>();
    for line in &mut lines {
        if let Some(rest) = line.strip_prefix("member ") {
            let mut parts = rest.splitn(3, ' ');
            let name = parts.next().unwrap_or_default();
            let length = parts.next().unwrap_or_default();
            *line = format!("member {name} {length} {digest}");
            break;
        }
    }
    lines.join("\n").into_bytes()
}

fn digest_of(path: &Path) -> String {
    crate::backup::sha256::digest_file(path).expect("digests the member")
}

/// Rebinds one point's manifest over the member file's current bytes and
/// renames the point directory to the forged identity — the complete state
/// a forger with full write access to the destination produces. Returns the
/// forged identity the directory now carries.
fn reseat_point(
    destination: &Path,
    identity: &str,
    original_manifest: &[u8],
    member_file: &Path,
) -> String {
    let rebound =
        rebind_identity(&rebind_first_member_digest(original_manifest, &digest_of(member_file)));
    let text = String::from_utf8_lossy(&rebound).into_owned();
    let forged_identity = text
        .split('\n')
        .find_map(|line| line.strip_prefix("identity "))
        .expect("identity line")
        .to_owned();
    let point = destination.join(format!("rp-{identity}"));
    std::fs::write(point.join(crate::backup::manifest::MANIFEST_FILE), &rebound)
        .expect("writes the rebound manifest");
    let reseat = destination.join(format!("rp-{forged_identity}"));
    std::fs::rename(&point, &reseat).expect("renames the point directory");
    forged_identity
}

/// Stages one document per named collection into a single transaction, so a
/// drill can build a wide collection fan-out without one commit per document.
fn put_one_per_collection(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    collections: &[String],
    value: &str,
) -> CommitOutcome {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation(operation_name)).expect("begins");
    for collection in collections {
        transaction
            .put(
                Document::new(
                    document_id(id),
                    CollectionName::try_new(collection).expect("valid identifier"),
                    text(value),
                ),
                ExpectedVersion::Any,
            )
            .expect("stages");
    }
    transaction.commit(LocalDurability::Synced)
}

// ---------------------------------------------------------------------------
// Restore from each backup kind reproduces exact counts and values
// ---------------------------------------------------------------------------

#[test]
fn full_backup_restores_exact_values_counts_and_tenant_scopes() {
    let (_source, engine) = open_engine("full-values");
    let destination = location("full-values-dest");
    let target = location("full-values-target");

    committed(put(
        &engine,
        "alpha",
        "op-a1",
        "doc-1",
        "users",
        text("alpha-one"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-a2",
        "doc-2",
        "orders",
        DocumentValue::Integer(42),
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
    committed(put_family_record(&engine, "beta", "op-b2", "counters", "owner-b"));

    let backup = run_backup(&engine, BackupPlan::full(), &destination).expect("full backup");
    let receipt = RestorePlan::restore_latest(&destination, &target).expect("full restore runs");

    let manifest = backup.manifest();
    assert_eq!(receipt.selected_identity(), manifest.identity());
    assert_eq!(receipt.selected_kind(), BackupKind::Full);
    assert_eq!(receipt.selected_boundary(), manifest.boundary_sequence());
    assert_eq!(receipt.chain_depth(), 1, "a full backup is its own chain root");

    // Exact counts: the manifest's bound facts and the restored live state
    // agree, per tenant and in total.
    assert_eq!(receipt.expected_documents(), manifest.documents());
    assert_eq!(receipt.expected_family_records(), manifest.family_records());
    assert_eq!(receipt.decoded_documents(), manifest.documents());
    assert_eq!(receipt.decoded_family_records(), manifest.family_records());
    assert_eq!(receipt.restored_documents(), 3, "alpha's two and beta's one user documents");
    assert_eq!(receipt.restored_family_records(), 1);
    let alpha = receipt
        .restored_tenants()
        .iter()
        .find(|t| t.tenant().as_str() == "alpha")
        .expect("alpha restored");
    assert_eq!(alpha.documents(), 2);
    assert_eq!(alpha.family_records(), 0);
    let beta = receipt
        .restored_tenants()
        .iter()
        .find(|t| t.tenant().as_str() == "beta")
        .expect("beta restored");
    assert_eq!(beta.documents(), 1);
    assert_eq!(beta.family_records(), 1);

    // Exact values through a real durable Engine at the restored location.
    assert_eq!(
        sorted(read_back(&target, "alpha")),
        sorted(vec![
            ("orders".to_owned(), "doc-2".to_owned(), DocumentValue::Integer(42)),
            ("users".to_owned(), "doc-1".to_owned(), text("alpha-one")),
        ])
    );
    assert_eq!(
        sorted(read_back(&target, "beta")),
        sorted(vec![
            (
                "terrabase.families".to_owned(),
                "family:counters/owner-b".to_owned(),
                DocumentValue::Object([("count".to_owned(), DocumentValue::Integer(7))].into())
            ),
            ("users".to_owned(), "doc-1".to_owned(), text("beta-one")),
        ])
    );

    // The clean-restore verification really read the reopened target back.
    assert_eq!(receipt.tenants_verified(), 2);
    assert_eq!(receipt.documents_read_back(), 4, "every live key, family records included");
    assert_eq!(receipt.representative_values_matched(), 2, "one byte-exact value per tenant");
    assert!(receipt.members_verified() >= 1);
    assert!(receipt.largest_tenant_archive_bytes() > 0, "the bound is sized from this run");
}

#[test]
fn incremental_chain_restore_replays_updates_and_deletes_in_publication_order() {
    let (_source, engine) = open_engine("chain-replay");
    let destination = location("chain-replay-dest");
    let target = location("chain-replay-target");

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
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("base full");
    let base = full.manifest().boundary_sequence();

    committed(put(
        &engine,
        "alpha",
        "op-3",
        "doc-2",
        "users",
        text("two-updated"),
        LocalDurability::Synced,
    ));
    committed(delete(&engine, "alpha", "op-4", "doc-1"));
    committed(put(
        &engine,
        "alpha",
        "op-5",
        "doc-3",
        "users",
        text("three"),
        LocalDurability::Synced,
    ));
    committed(delete(&engine, "alpha", "op-6", "never-existed"));
    let incremental =
        run_backup(&engine, BackupPlan::incremental_since(base), &destination).expect("link");

    let receipt = RestorePlan::restore_latest(&destination, &target).expect("chain restore runs");

    assert_eq!(receipt.chain_depth(), 2, "full root plus one incremental link");
    assert_eq!(
        receipt.chain_identities(),
        vec![full.manifest().identity().to_owned(), incremental.manifest().identity().to_owned()]
    );
    assert_eq!(receipt.selected_identity(), incremental.manifest().identity());
    assert_eq!(receipt.expected_deletes(), 2, "both journal deletes are bound facts");
    assert_eq!(receipt.decoded_deletes(), 2);
    assert_eq!(receipt.duplicate_put_replays(), 1, "doc-2 was written twice across the chain");
    assert_eq!(receipt.missing_key_deletes(), 1, "the no-op delete replayed against nothing");
    assert_eq!(receipt.restored_documents(), 2, "doc-2 updated and doc-3 new survive");

    // Last-write-wins by publication order: the update replays after the
    // base's original put, and the delete removes the base's document.
    assert_eq!(
        sorted(read_back(&target, "alpha")),
        sorted(vec![
            ("users".to_owned(), "doc-2".to_owned(), text("two-updated")),
            ("users".to_owned(), "doc-3".to_owned(), text("three")),
        ])
    );
}

#[test]
fn a_later_chain_delete_of_the_sample_document_still_verifies_green() {
    let (_source, engine) = open_engine("dead-sample");
    let destination = location("dead-sample-dest");
    let target = location("dead-sample-target");

    // The full root decodes the tenant's documents in canonical key order,
    // so `doc-2` is the last decoded sample the replay retains for `alpha`.
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
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("root");
    let base = full.manifest().boundary_sequence();

    // The newest decoded sample is deleted by the chain's next link: the
    // archive is legitimate, so the restore must verify green against the
    // final live state, not against a sample the chain removed.
    committed(delete(&engine, "alpha", "op-3", "doc-2"));
    run_backup(&engine, BackupPlan::incremental_since(base), &destination).expect("link");

    let receipt = RestorePlan::restore_latest(&destination, &target).expect("restore verifies");
    assert_eq!(receipt.restored_documents(), 1, "doc-1 alone survives the chain");
    assert_eq!(receipt.tenants_verified(), 1);
    assert_eq!(receipt.documents_read_back(), 1, "the live key set read back exactly");
    assert_eq!(
        sorted(read_back(&target, "alpha")),
        sorted(vec![("users".to_owned(), "doc-1".to_owned(), text("one"))]),
        "the surviving document keeps its source value"
    );
    // The retained sample retired with the deleted document, so this run has
    // no live representative value to compare for `alpha`: the count is the
    // honest disclosure of that, never a mismatch verdict on a good archive.
    assert_eq!(receipt.representative_values_matched(), 0);
}

#[test]
fn one_identifier_reused_across_many_collections_restores_and_verifies() {
    let (_source, engine) = open_engine("reused-identifier");
    let destination = location("reused-identifier-dest");
    let target = location("reused-identifier-target");

    // One identifier across 300 collections: the read-back window pages by a
    // document identifier alone, and 300 canonical keys share one identifier,
    // so an identifier-only cursor cannot walk the tenant. Restore must still
    // terminate and verify the complete key set.
    let collections: Vec<String> = (0..300).map(|index| format!("coll-{index:03}")).collect();
    committed(put_one_per_collection(
        &engine,
        "alpha",
        "op-1",
        "shared",
        &collections,
        "reused-value",
    ));
    run_backup(&engine, BackupPlan::full(), &destination).expect("full backup");

    let receipt = RestorePlan::restore_latest(&destination, &target).expect("restore verifies");
    assert_eq!(receipt.restored_documents(), 300, "every collection's copy is live");
    assert_eq!(receipt.tenants_verified(), 1);
    assert_eq!(receipt.documents_read_back(), 300, "the whole key set read back exactly");
    assert_eq!(receipt.representative_values_matched(), 1);

    // The restored location really holds one live copy per collection.
    let restored = sorted(read_back(&target, "alpha"));
    assert_eq!(restored.len(), 300);
    for collection in &collections {
        assert!(
            restored.iter().any(|(name, id, value)| name == collection
                && id == "shared"
                && *value == text("reused-value")),
            "collection {collection} holds its own copy of the identifier"
        );
    }
}

#[test]
fn snapshot_backup_restores_its_pinned_boundary_not_the_latest_state() {
    let (_source, engine) = open_engine("snapshot-pin");
    let destination = location("snapshot-pin-dest");
    let target = location("snapshot-pin-target");

    let pinned = committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let snapshot = run_backup(&engine, BackupPlan::snapshot_at(pinned), &destination)
        .expect("snapshot backup");
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));

    let receipt =
        RestorePlan::restore_recovery_point(snapshot.manifest().identity(), &destination, &target)
            .expect("snapshot restore runs");

    assert_eq!(receipt.selected_kind(), BackupKind::Snapshot);
    assert_eq!(receipt.selected_boundary(), pinned);
    assert_eq!(receipt.chain_depth(), 1, "a snapshot is self-contained");
    assert_eq!(receipt.restored_documents(), 1, "only the pinned boundary's state");
    assert_eq!(
        sorted(read_back(&target, "alpha")),
        sorted(vec![("users".to_owned(), "doc-1".to_owned(), text("one"))]),
        "the later write never enters the pinned restore"
    );
}

#[test]
fn restore_selection_by_identity_restores_an_older_chain_depth_than_latest() {
    let (_source, engine) = open_engine("selection");
    let destination = location("selection-dest");
    let latest_target = location("selection-latest-target");
    let root_target = location("selection-root-target");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("root");
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("two"),
        LocalDurability::Synced,
    ));
    run_backup(
        &engine,
        BackupPlan::incremental_since(full.manifest().boundary_sequence()),
        &destination,
    )
    .expect("link");

    let latest =
        RestorePlan::restore_latest(&destination, &latest_target).expect("latest restores");
    let root =
        RestorePlan::restore_recovery_point(full.manifest().identity(), &destination, &root_target)
            .expect("the named root restores");

    assert_eq!(latest.chain_depth(), 2);
    assert_eq!(latest.restored_documents(), 2);
    assert_eq!(root.chain_depth(), 1, "selection stops at the named point");
    assert_eq!(root.selected_identity(), full.manifest().identity());
    assert_eq!(root.restored_documents(), 1, "the link's document stays out");
    assert_eq!(
        sorted(read_back(&root_target, "alpha")),
        sorted(vec![("users".to_owned(), "doc-1".to_owned(), text("one"))])
    );
}

#[test]
fn empty_source_full_backup_restores_an_empty_engine_location() {
    let (_source, engine) = open_engine("empty-restore");
    let destination = location("empty-restore-dest");
    let target = location("empty-restore-target");
    run_backup(&engine, BackupPlan::full(), &destination).expect("empty backup");

    let receipt = RestorePlan::restore_latest(&destination, &target).expect("empty restore runs");

    assert_eq!(receipt.restored_documents(), 0);
    assert!(receipt.restored_tenants().is_empty());
    assert_eq!(receipt.documents_read_back(), 0);
    assert!(target.is_dir(), "the fresh engine location exists");
    let reopened = Engine::open(EngineConfig::durable(&target).expect("durable location"))
        .expect("the restored location reopens as a real Engine");
    reopened.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Fail-closed: corruption, truncation, missing links, bad targets
// ---------------------------------------------------------------------------

#[test]
fn restore_refuses_a_non_empty_or_unrelated_target() {
    let (_source, engine) = open_engine("target-refusal");
    let destination = location("target-refusal-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    run_backup(&engine, BackupPlan::full(), &destination).expect("backup");

    // A target holding an unrelated file is refused before any work.
    let file_target = location("target-refusal-file");
    std::fs::create_dir_all(&file_target).expect("creates the target");
    std::fs::write(file_target.join("unrelated.txt"), b"not a restore").expect("seeds a file");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &file_target).err(),
        Some(BackupError::TargetNotEmpty)
    );

    // A target holding an unrelated engine is refused the same way, and the
    // existing engine's bytes are untouched.
    let (engine_dir, engine_target) = open_engine("target-refusal-engine");
    let before = sorted(read_back(&engine_dir, "alpha"));
    assert_eq!(
        RestorePlan::restore_latest(&destination, &engine_dir).err(),
        Some(BackupError::TargetNotEmpty)
    );
    assert_eq!(sorted(read_back(&engine_dir, "alpha")), before, "nothing was written");
    drop(engine_target);

    // A path occupied by a file is refused, not replaced.
    let file_path = location("target-refusal-path");
    std::fs::write(&file_path, b"file").expect("seeds a file path");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &file_path).err(),
        Some(BackupError::TargetNotEmpty)
    );

    // A successful restore's own target is non-empty afterwards: a second
    // restore into it is refused rather than merged.
    let target = location("target-refusal-target");
    RestorePlan::restore_latest(&destination, &target).expect("first restore");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::TargetNotEmpty),
        "restore never merges into an existing engine location"
    );
}

#[test]
fn corrupted_and_truncated_members_fail_closed_and_leave_no_target() {
    let (_source, engine) = open_engine("corrupt-members");
    let destination = location("corrupt-members-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one-value-one"),
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
    let backup = run_backup(&engine, BackupPlan::full(), &destination).expect("backup");
    let identity = backup.manifest().identity().to_owned();
    let member = backup.manifest().members()[0].name().to_owned();
    let member_file = member_path(&destination, &identity, &member);
    let original = std::fs::read(&member_file).expect("reads the member");

    // Tampered bytes: the digest check fails before anything is materialized.
    let mut tampered = original.clone();
    tampered[0] ^= 0xff;
    std::fs::write(&member_file, &tampered).expect("writes tampered bytes");
    let target = location("corrupt-members-target-tampered");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::ChecksumMismatch)
    );
    assert!(!target.exists(), "no target was created");

    // Truncated bytes: the length check fails the same way.
    std::fs::write(&member_file, &original[..original.len() - 1]).expect("truncates");
    let target = location("corrupt-members-target-truncated");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::ChecksumMismatch)
    );
    assert!(!target.exists(), "no target was created");

    // A missing member file is an incomplete point.
    std::fs::remove_file(&member_file).expect("removes the member");
    let target = location("corrupt-members-target-missing");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::ChainIncomplete)
    );
    assert!(!target.exists(), "no target was created");
}

#[test]
fn a_missing_chain_link_fails_the_restore_before_any_target_exists() {
    let (_source, engine) = open_engine("missing-link");
    let destination = location("missing-link-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("root");
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
    let link =
        run_backup(&engine, BackupPlan::incremental_since(base), &destination).expect("link");
    assert!(verify_latest_chain(&destination).is_ok());

    std::fs::remove_dir_all(destination.join(format!("rp-{}", full.manifest().identity())))
        .expect("removes the root link");
    let target = location("missing-link-target");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::ChainIncomplete),
        "a chain missing its root is not restorable"
    );
    assert!(!target.exists(), "no target was created");
    drop(link);
}

#[test]
fn a_digest_consistent_forged_archive_fails_the_content_checks() {
    let (_source, engine) = open_engine("forged");
    let destination = location("forged-dest");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one-value-one"),
        LocalDurability::Synced,
    ));
    let backup = run_backup(&engine, BackupPlan::full(), &destination).expect("backup");
    let identity = backup.manifest().identity().to_owned();
    let manifest_file = manifest_path(&destination, &identity);
    let original_manifest = std::fs::read(&manifest_file).expect("reads the manifest");
    let member = backup.manifest().members()[0].name().to_owned();
    let member_file = member_path(&destination, &identity, &member);
    let original_member = std::fs::read(&member_file).expect("reads the member");

    // Same length, garbage bytes, and a re-derived honest manifest identity:
    // the digest verifies, so the record decoder is the next line of
    // defense, and an undecodable frame can never become a success. The
    // forger renames the point directory to the forged identity too, so the
    // named-identity path is the one that runs.
    let garbage = vec![0x07_u8; original_member.len()];
    std::fs::write(&member_file, &garbage).expect("writes garbage");
    let garbage_identity = reseat_point(&destination, &identity, &original_manifest, &member_file);
    let target = location("forged-target-garbage");
    assert_eq!(
        RestorePlan::restore_recovery_point(&garbage_identity, &destination, &target).err(),
        Some(BackupError::ChecksumMismatch),
        "undecodable frames in a digest-consistent member fail closed"
    );
    assert!(!target.exists());

    // Return the point to its honest state, then forge again: valid
    // canonical frames that disagree with the manifest's bound counts are a
    // typed failure too — one extra document record appended, digest and
    // identity re-derived, counts left as the honest run bound them.
    std::fs::rename(
        destination.join(format!("rp-{garbage_identity}")),
        destination.join(format!("rp-{identity}")),
    )
    .expect("renames the point back");
    std::fs::write(&manifest_file, &original_manifest).expect("restores the manifest");
    let mut extended = original_member.clone();
    let extra = crate::backup::records::Record::Document {
        tenant: tenant("alpha"),
        document: Document::new(
            document_id("forged-doc"),
            CollectionName::try_new("users").expect("valid identifier"),
            text("forged"),
        ),
        durability: LocalDurability::Synced,
    };
    crate::backup::records::encode_into(&mut extended, &extra);
    std::fs::write(&member_file, &extended).expect("writes the extended member");
    let extended_identity = reseat_point(&destination, &identity, &original_manifest, &member_file);
    let target = location("forged-target-counts");
    assert_eq!(
        RestorePlan::restore_recovery_point(&extended_identity, &destination, &target).err(),
        Some(BackupError::ChecksumMismatch),
        "decoded counts must reproduce the manifest's bound counts exactly"
    );
    assert!(!target.exists(), "no partial-success target remains");
}

#[test]
fn a_failed_restore_leaves_no_target_and_the_retry_succeeds() {
    let (_source, engine) = open_engine("retry");
    let destination = location("retry-dest");
    let target = location("retry-target");

    // A pre-existing empty target directory is a fresh location: the atomic
    // publish renames the staged engine over it.
    std::fs::create_dir_all(&target).expect("pre-creates an empty target");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("root");
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
    let link =
        run_backup(&engine, BackupPlan::incremental_since(base), &destination).expect("link");

    // Corrupt the newest link: the attempt fails, and no target directory
    // exists — an interrupted or failed restore never leaves a usable
    // partial engine location behind.
    let member = link.manifest().members()[0].name().to_owned();
    let member_file = member_path(&destination, link.manifest().identity(), &member);
    let original = std::fs::read(&member_file).expect("reads the member");
    let mut tampered = original.clone();
    tampered[0] ^= 0xff;
    std::fs::write(&member_file, &tampered).expect("corrupts the head link");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::ChecksumMismatch)
    );
    assert!(
        target.read_dir().expect("reads the target").next().is_none(),
        "the pre-created target is still exactly as empty as before the failed attempt"
    );

    // A stale staging sibling — what a crashed restore process leaves — does
    // not block the next attempt at the same target.
    let stale = target.with_file_name(format!(".restore-staging-{}-stale", std::process::id()));
    std::fs::create_dir_all(&stale).expect("creates a stale staging sibling");
    std::fs::write(stale.join("wal"), b"torn").expect("seeds torn bytes");

    // Restore the intact root instead: the retry succeeds into the same
    // target the failed attempt never built into.
    let receipt =
        RestorePlan::restore_recovery_point(full.manifest().identity(), &destination, &target)
            .expect("the retry restores");
    assert_eq!(receipt.restored_documents(), 1);
    assert_eq!(
        sorted(read_back(&target, "alpha")),
        sorted(vec![("users".to_owned(), "doc-1".to_owned(), text("one"))])
    );
}

// ---------------------------------------------------------------------------
// Restart-after-restore and the Recovery Receipt
// ---------------------------------------------------------------------------

#[test]
fn restart_after_restore_reopens_the_target_and_reads_the_same_values() {
    let (_source, engine) = open_engine("restart");
    let destination = location("restart-dest");
    let target = location("restart-target");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("restart-value"),
        LocalDurability::Synced,
    ));
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("second-value"),
        LocalDurability::Synced,
    ));
    run_backup(&engine, BackupPlan::full(), &destination).expect("backup");
    RestorePlan::restore_latest(&destination, &target).expect("restore runs");

    let first = sorted(read_back(&target, "alpha"));
    let second = sorted(read_back(&target, "alpha"));
    assert_eq!(first, second, "a reopen reads the same values");
    assert_eq!(first.len(), 2);
}

#[test]
fn the_receipt_discloses_counts_duplicates_and_the_memory_bound() {
    let (_source, engine) = open_engine("receipt");
    let destination = location("receipt-dest");
    let target = location("receipt-target");

    committed(put(
        &engine,
        "alpha",
        "op-1",
        "doc-1",
        "users",
        text("receipt-value"),
        LocalDurability::Synced,
    ));
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("root");
    let base = full.manifest().boundary_sequence();

    // A below-Synced commit is archived, disclosed, and restored as a
    // disclosed record — never dropped, never silently fully durable.
    committed(put(
        &engine,
        "alpha",
        "op-2",
        "doc-2",
        "users",
        text("buffered-value"),
        LocalDurability::Buffered,
    ));
    let link =
        run_backup(&engine, BackupPlan::incremental_since(base), &destination).expect("link");
    assert_eq!(link.manifest().disclosures(), 1);

    let receipt = RestorePlan::restore_latest(&destination, &target).expect("restore runs");
    assert_eq!(receipt.expected_disclosures(), 1);
    assert_eq!(receipt.decoded_disclosures(), 1, "the frame-level disclosure recount agrees");
    assert_eq!(receipt.disclosed_records_restored(), 1);
    assert_eq!(receipt.storage_format(), "TBV2FORMAT 1");
    assert!(receipt.restore_commits() >= 1, "the replay published at least one commit");
    assert!(receipt.target_sequence().get() > 0);
    assert!(!receipt.target_root_identity().is_empty());
    assert_eq!(
        receipt.member_bytes_verified(),
        link.manifest().total_bytes() + full.manifest().total_bytes()
    );

    // The rendered receipt is secret-safe: it carries the identities, counts,
    // and the memory-bound disclosure, and never a path or a document value.
    let rendered = receipt.render();
    let target_text = target.to_string_lossy().into_owned();
    assert!(!rendered.contains(&target_text), "no private host path crosses the seam");
    assert!(!rendered.contains("receipt-value"), "no document value crosses the seam");
    assert!(!rendered.contains("buffered-value"), "no document value crosses the seam");
    for needle in ["memory bound", "largest tenant", "re-minted", "verification"] {
        assert!(rendered.contains(needle), "the receipt states its limits ({needle} missing)");
    }
    assert!(rendered.contains("duplicate"), "duplicate handling is disclosed");
    assert!(rendered.contains("missing"), "missing-key handling is disclosed");
}
