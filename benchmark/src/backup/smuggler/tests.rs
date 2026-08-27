//! In-crate smuggler export/import tests (BK-03).
//!
//! The smuggler seam is private to `src/backup` and `TenantScope` has no
//! public constructor, so these focused tests live in-crate with the
//! `TenantScope::from_parts` fixture, mirroring the backup-core suite.
//! Every assertion goes through the crate-internal smuggler seam and the
//! engine's own read paths; no test inspects archive bytes directly.

use std::fmt::Write as _;
use std::path::PathBuf;
use std::sync::Arc;

use super::{
    ExportFamily, ExportSelection, SmugglerError, SmugglerImportPlan, SmugglerTransform,
    run_smuggler_export, run_smuggler_import,
};
use crate::engine::{Engine, EngineConfig};
use crate::types::{
    Cancellation, CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-bk03-smuggler-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(TenantId::try_new(name).expect("valid tenant"), engine.identity())
}

fn put(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    collection: &str,
    value: DocumentValue,
) {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction = engine
        .begin(&tenant_scope, snapshot, OperationId::try_new(operation_name).expect("valid"))
        .expect("begins");
    transaction
        .put(
            Document::new(
                DocumentId::try_new(id).expect("valid identifier"),
                CollectionName::try_new(collection).expect("valid identifier"),
                value,
            ),
            ExpectedVersion::Any,
        )
        .expect("stages");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
}

/// Seeds one source engine with a two-tenant, single-version state: user
/// documents and one family record per tenant, plus one deleted document
/// whose tombstone lives in the journal.
fn seed_source(engine: &Engine) {
    put(engine, "alpha", "a1", "order-1", "Orders", DocumentValue::String("first".to_owned()));
    put(engine, "alpha", "a2", "order-2", "Orders", DocumentValue::Integer(7));
    put(
        engine,
        "alpha",
        "a3",
        "family:counters/order-2",
        "terrabase.families",
        DocumentValue::Object([("downloads".to_owned(), DocumentValue::Integer(9))].into()),
    );
    put(engine, "alpha", "a4", "order-3", "Orders", DocumentValue::String("doomed".to_owned()));
    let tenant_scope = scope(engine, "alpha");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction = engine
        .begin(&tenant_scope, snapshot, OperationId::try_new("a5").expect("valid"))
        .expect("begins");
    transaction
        .delete(DocumentId::try_new("order-3").expect("valid"), ExpectedVersion::Any)
        .expect("stages");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
    put(engine, "beta", "b1", "invoice-1", "Invoices", DocumentValue::Boolean(true));
}

fn export_destination(tag: &str) -> PathBuf {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    dir
}

/// The artifact directory of one sealed export identity.
fn artifact_of(destination: &std::path::Path, identity: &str) -> PathBuf {
    destination.join(format!("sm-{identity}"))
}

fn documents_and_families() -> ExportSelection {
    ExportSelection::of([ExportFamily::Documents, ExportFamily::FamilyRecords])
}

#[test]
fn export_writes_a_manifest_with_family_selection_counts_and_digests() {
    let (_dir, engine) = open_engine("export-manifest");
    seed_source(&engine);
    let destination = export_destination("export-manifest-dest");

    let result = run_smuggler_export(
        &engine,
        &super::SmugglerExportPlan::for_selection(ExportSelection::all()),
        &destination,
    )
    .expect("exports");

    assert_eq!(result.count(ExportFamily::Documents), 3, "two alpha orders plus one beta invoice");
    assert_eq!(result.count(ExportFamily::FamilyRecords), 1);
    assert_eq!(result.count(ExportFamily::Tombstones), 1, "the seeded delete");
    assert_eq!(result.identity().len(), 64, "identity is a SHA-256 hex digest");
    assert_eq!(result.members().len(), 1, "the fixture fits one member");
    assert!(result.verify(&destination).is_ok(), "every bound digest verifies");

    // An artifact directory is published under the identity and holds a
    // sealed manifest.
    let artifact = destination.join(format!("sm-{}", result.identity()));
    assert!(artifact.join("manifest.tbv2s").is_file());
}

#[test]
fn export_selection_excludes_unselected_families() {
    let (_dir, engine) = open_engine("export-selection");
    seed_source(&engine);
    let destination = export_destination("export-selection-dest");

    let result = run_smuggler_export(
        &engine,
        &super::SmugglerExportPlan::for_selection(ExportSelection::of([ExportFamily::Documents])),
        &destination,
    )
    .expect("exports");

    assert_eq!(result.count(ExportFamily::Documents), 3);
    assert_eq!(result.count(ExportFamily::FamilyRecords), 0, "unselected families never enter");
    assert_eq!(result.count(ExportFamily::Tombstones), 0);
    assert!(result.verify(&destination).is_ok());
}

#[test]
fn round_trip_export_import_preserves_families_with_checksums() {
    let (_source, engine_a) = open_engine("round-trip-a");
    seed_source(&engine_a);
    let destination = export_destination("round-trip-dest");
    let selection = documents_and_families();
    let first = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(selection.clone()),
        &destination,
    )
    .expect("exports");

    let artifact = artifact_of(&destination, first.identity());
    let (_target, engine_b) = open_engine("round-trip-b");
    let import = SmugglerImportPlan::importing(selection.clone());
    run_smuggler_import(&engine_b, &scope(&engine_b, "alpha"), &import, &artifact)
        .expect("imports alpha");
    run_smuggler_import(&engine_b, &scope(&engine_b, "beta"), &import, &artifact)
        .expect("imports beta");

    let re_export_destination = export_destination("round-trip-redest");
    let second = run_smuggler_export(
        &engine_b,
        &super::SmugglerExportPlan::for_selection(selection),
        &re_export_destination,
    )
    .expect("re-exports");

    assert_eq!(
        first.identity(),
        second.identity(),
        "the same logical state seals the same checksum-bound identity"
    );
    assert_eq!(first.members().len(), second.members().len());
    for (left, right) in first.members().iter().zip(second.members().iter()) {
        assert_eq!(left.digest(), right.digest(), "member digests survive the round trip");
        assert_eq!(left.length(), right.length());
    }
    assert_eq!(first.count(ExportFamily::Documents), second.count(ExportFamily::Documents));
    assert_eq!(first.count(ExportFamily::FamilyRecords), second.count(ExportFamily::FamilyRecords));
    assert!(second.verify(&re_export_destination).is_ok());
}

#[test]
fn import_operation_identities_are_stable_across_selections() {
    let (_source, engine_a) = open_engine("stable-selection-a");
    seed_source(&engine_a);
    let destination = export_destination("stable-selection-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(ExportSelection::all()),
        &destination,
    )
    .expect("exports every family")
    .identity()
    .to_owned();
    let artifact = artifact_of(&destination, &identity);

    let (_target, engine_b) = open_engine("stable-selection-b");
    // A partial import under selection A (family records only) is
    // interrupted after its first durable record. The artifact's decode
    // order is order-1, order-2, the family record, the tombstone, and
    // beta's invoice — the family record sits at position two.
    let token = Arc::new(Cancellation::new());
    let cancel_after_one = {
        let token = Arc::clone(&token);
        Arc::new(move |applied: u64| {
            if applied >= 1 {
                token.cancel();
            }
        })
    };
    let interrupted = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(ExportSelection::of([ExportFamily::FamilyRecords]))
            .with_cancellation(Arc::clone(&token))
            .with_progress_observer(cancel_after_one),
        &artifact,
    );
    assert_eq!(interrupted.err(), Some(SmugglerError::Cancelled));

    // The resumed import selects every family. A record's operation
    // identity is a property of its decode position in the artifact, never
    // of the run's selection, so the documents the partial run skipped
    // must convert — not resolve as duplicates they never applied.
    let resumed = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(ExportSelection::all()),
        &artifact,
    )
    .expect("resumes under a wider selection");

    assert_eq!(resumed.applied(ExportFamily::Documents), 2, "both documents convert");
    assert_eq!(
        resumed.duplicates(ExportFamily::Documents),
        0,
        "no document may be skipped as a duplicate it never applied"
    );
    assert_eq!(
        resumed.duplicates(ExportFamily::FamilyRecords),
        1,
        "exactly the partial run's durable record"
    );
    assert_eq!(resumed.applied(ExportFamily::Tombstones), 1);

    let tenant_scope = scope(&engine_b, "alpha");
    let snapshot = engine_b.read_snapshot(&tenant_scope).expect("snapshot");
    let documents = engine_b.snapshot_documents(&tenant_scope, &snapshot).expect("documents");
    assert!(
        documents.iter().any(|document| document.id().as_str() == "order-1"),
        "the document the partial run skipped is present in the target"
    );
    assert_eq!(
        documents.len(),
        3,
        "order-1, order-2, and the family record; the tombstone stays deleted"
    );
}

#[test]
fn import_applies_collection_rename_transforms() {
    let (_source, engine_a) = open_engine("transform-a");
    seed_source(&engine_a);
    let destination = export_destination("transform-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports")
    .identity()
    .to_owned();

    let (_target, engine_b) = open_engine("transform-b");
    let plan = SmugglerImportPlan::importing(documents_and_families())
        .with_transform(SmugglerTransform::none().rename_collection("Orders", "orders-v2"));
    let result = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &plan,
        &artifact_of(&destination, &identity),
    )
    .expect("imports with a transform");

    assert_eq!(result.renames_applied(), 2, "both alpha Orders documents were renamed");
    let tenant_scope = scope(&engine_b, "alpha");
    let snapshot = engine_b.read_snapshot(&tenant_scope).expect("snapshot");
    let documents = engine_b.snapshot_documents(&tenant_scope, &snapshot).expect("documents");
    let renamed: Vec<_> =
        documents.iter().filter(|document| document.collection().as_str() == "orders-v2").collect();
    assert_eq!(renamed.len(), 2, "the documents live in the renamed collection");
    assert!(documents.iter().all(|document| document.collection().as_str() != "Orders"));
}

#[test]
fn import_rejects_a_transform_targeting_the_reserved_collection() {
    let (_source, engine_a) = open_engine("transform-invalid");
    seed_source(&engine_a);
    let destination = export_destination("transform-invalid-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports")
    .identity()
    .to_owned();

    let (_target, engine_b) = open_engine("transform-invalid-b");
    let plan = SmugglerImportPlan::importing(documents_and_families()).with_transform(
        SmugglerTransform::none().rename_collection("Orders", "terrabase.families"),
    );
    let failure = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &plan,
        &artifact_of(&destination, &identity),
    );
    assert_eq!(failure.err(), Some(SmugglerError::TransformInvalid));
}

#[test]
fn import_is_idempotent_on_reimport() {
    let (_source, engine_a) = open_engine("idempotent-a");
    seed_source(&engine_a);
    let destination = export_destination("idempotent-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports")
    .identity()
    .to_owned();
    let artifact = artifact_of(&destination, &identity);

    let (_target, engine_b) = open_engine("idempotent-b");
    let plan = SmugglerImportPlan::importing(documents_and_families());
    let first = run_smuggler_import(&engine_b, &scope(&engine_b, "alpha"), &plan, &artifact)
        .expect("first import");
    let second = run_smuggler_import(&engine_b, &scope(&engine_b, "alpha"), &plan, &artifact)
        .expect("second import");

    assert!(first.duplicates(ExportFamily::Documents) == 0, "a fresh target has no duplicates");
    assert_eq!(
        second.applied(ExportFamily::Documents) + second.applied(ExportFamily::FamilyRecords),
        0,
        "nothing applies twice"
    );
    assert_eq!(second.duplicates(ExportFamily::Documents), first.applied(ExportFamily::Documents));
    assert_eq!(
        second.duplicates(ExportFamily::FamilyRecords),
        first.applied(ExportFamily::FamilyRecords)
    );
    assert!(second.verified(), "the re-import still verifies its counts");

    let tenant_scope = scope(&engine_b, "alpha");
    let snapshot = engine_b.read_snapshot(&tenant_scope).expect("snapshot");
    let documents = engine_b.snapshot_documents(&tenant_scope, &snapshot).expect("documents");
    assert_eq!(documents.len(), 3, "two orders plus the family record, exactly once each");
}

#[test]
fn interrupted_import_resumes_idempotently() {
    let (_source, engine_a) = open_engine("resume-a");
    seed_source(&engine_a);
    let destination = export_destination("resume-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports")
    .identity()
    .to_owned();
    let artifact = artifact_of(&destination, &identity);

    let (_target, engine_b) = open_engine("resume-b");
    let token = Arc::new(Cancellation::new());
    let cancel_after_one = {
        let token = Arc::clone(&token);
        Arc::new(move |applied: u64| {
            if applied >= 1 {
                token.cancel();
            }
        })
    };
    let interrupted = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families())
            .with_cancellation(Arc::clone(&token))
            .with_progress_observer(cancel_after_one),
        &artifact,
    );
    assert_eq!(interrupted.err(), Some(SmugglerError::Cancelled));

    let resumed = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact,
    )
    .expect("resumes");
    assert!(resumed.duplicates(ExportFamily::Documents) >= 1, "the durable prefix resolved");
    assert!(resumed.verified());

    // The final state equals a single uninterrupted import.
    let (_target2, engine_c) = open_engine("resume-clean");
    let clean = run_smuggler_import(
        &engine_c,
        &scope(&engine_c, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact,
    )
    .expect("clean import");
    let read_back = |engine: &Engine| {
        let tenant_scope = scope(engine, "alpha");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        engine.snapshot_documents(&tenant_scope, &snapshot).expect("documents")
    };
    assert_eq!(read_back(&engine_b).len(), read_back(&engine_c).len());
    assert_eq!(
        resumed.applied(ExportFamily::Documents) + resumed.duplicates(ExportFamily::Documents),
        clean.applied(ExportFamily::Documents) + clean.duplicates(ExportFamily::Documents)
    );
}

#[test]
fn import_rejects_a_tampered_manifest_before_any_write() {
    let (_source, engine_a) = open_engine("tamper-manifest");
    seed_source(&engine_a);
    let destination = export_destination("tamper-manifest-dest");
    let result = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports");
    let manifest = destination.join(format!("sm-{}", result.identity())).join("manifest.tbv2s");
    let body = std::fs::read_to_string(&manifest).expect("reads");
    let tampered = body.replace("count Documents 3", "count Documents 9");
    assert_ne!(&tampered, &body);
    std::fs::write(&manifest, tampered).expect("writes");

    let (_target, engine_b) = open_engine("tamper-manifest-b");
    let failure = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact_of(&destination, result.identity()),
    );
    assert_eq!(failure.err(), Some(SmugglerError::ManifestInvalid));
    let tenant_scope = scope(&engine_b, "alpha");
    let snapshot = engine_b.read_snapshot(&tenant_scope).expect("snapshot");
    assert!(
        engine_b.snapshot_documents(&tenant_scope, &snapshot).expect("documents").is_empty(),
        "nothing was written"
    );
}

#[test]
fn import_rejects_a_digest_mismatch_before_any_write() {
    let (_source, engine_a) = open_engine("tamper-digest");
    seed_source(&engine_a);
    let destination = export_destination("tamper-digest-dest");
    let result = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports");
    let member =
        destination.join(format!("sm-{}", result.identity())).join(result.members()[0].name());
    let bytes = std::fs::read(&member).expect("reads the member");
    let mut tampered = bytes.clone();
    let last = tampered.len() - 1;
    tampered[last] = tampered[last].wrapping_add(1);
    std::fs::write(&member, tampered).expect("tampers with the content, same length");

    let (_target, engine_b) = open_engine("tamper-digest-b");
    let failure = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact_of(&destination, result.identity()),
    );
    assert_eq!(failure.err(), Some(SmugglerError::ChecksumMismatch));
    let tenant_scope = scope(&engine_b, "alpha");
    let snapshot = engine_b.read_snapshot(&tenant_scope).expect("snapshot");
    assert!(engine_b.snapshot_documents(&tenant_scope, &snapshot).expect("documents").is_empty());
}

#[test]
fn import_rejects_a_truncated_or_corrupt_record() {
    let (_source, engine_a) = open_engine("corrupt-member");
    seed_source(&engine_a);
    let destination = export_destination("corrupt-member-dest");
    let result = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports");
    let member =
        destination.join(format!("sm-{}", result.identity())).join(result.members()[0].name());
    let bytes = std::fs::read(&member).expect("reads the member");

    // Truncated: the member ends inside a record.
    let artifact = artifact_of(&destination, result.identity());
    std::fs::write(&member, &bytes[..bytes.len() - 2]).expect("truncates");
    rebind_member(&artifact, &member, &bytes[..bytes.len() - 2]);
    let (_target, engine_b) = open_engine("corrupt-member-b");
    let failure = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact_of(&destination, result.identity()),
    );
    assert_eq!(failure.err(), Some(SmugglerError::RecordTruncated));

    // Corrupt: an unknown record tag at the first byte.
    let mut corrupt = bytes.clone();
    corrupt[0] = 0xEF;
    std::fs::write(&member, &corrupt).expect("corrupts");
    rebind_member(&artifact, &member, &corrupt);
    let failure = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact_of(&destination, result.identity()),
    );
    assert_eq!(failure.err(), Some(SmugglerError::RecordCorrupt));
}

/// Rebinds the manifest over one member's current bytes: the
/// corrupt-producer shape, where the manifest is internally consistent but
/// the member holds damaged records.
fn rebind_member(artifact: &std::path::Path, _member: &std::path::Path, bytes: &[u8]) {
    use super::codec::{identity_of_body, with_identity};
    let manifest_path = artifact.join("manifest.tbv2s");
    let body = std::fs::read_to_string(&manifest_path).expect("reads the manifest");
    let mut rebound = String::new();
    for line in body.lines() {
        if line.starts_with("identity ") {
            continue;
        }
        if let Some(rest) = line.strip_prefix("member ") {
            let name = rest.split(' ').next().expect("the member name");
            let digest = {
                use crate::backup::sha256::{Sha256, to_hex};
                let mut hasher = Sha256::new();
                hasher.update(bytes);
                to_hex(&hasher.finish())
            };
            let _ = writeln!(rebound, "member {name} {} {digest}", bytes.len());
        } else {
            let _ = writeln!(rebound, "{line}");
        }
    }
    let identity = identity_of_body(&rebound);
    std::fs::write(&manifest_path, with_identity(&rebound, &identity)).expect("writes");
}

#[test]
fn import_rejects_an_unsupported_manifest_version() {
    let (_source, engine_a) = open_engine("version");
    seed_source(&engine_a);
    let destination = export_destination("version-dest");
    let result = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports");
    let manifest = destination.join(format!("sm-{}", result.identity())).join("manifest.tbv2s");
    let body = std::fs::read_to_string(&manifest).expect("reads");
    let bumped = body.replace("terrabase-smuggler-export 1", "terrabase-smuggler-export 2");
    std::fs::write(&manifest, bumped).expect("writes");

    let (_target, engine_b) = open_engine("version-b");
    let failure = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact_of(&destination, result.identity()),
    );
    assert_eq!(failure.err(), Some(SmugglerError::UnsupportedVersion));
}

#[test]
fn import_rejects_a_selection_the_artifact_does_not_carry() {
    let (_source, engine_a) = open_engine("selection-missing");
    seed_source(&engine_a);
    let destination = export_destination("selection-missing-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports")
    .identity()
    .to_owned();

    let (_target, engine_b) = open_engine("selection-missing-b");
    let failure = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &SmugglerImportPlan::importing(ExportSelection::all()),
        &artifact_of(&destination, &identity),
    );
    assert_eq!(
        failure.err(),
        Some(SmugglerError::ManifestInvalid),
        "an artifact without tombstones cannot import them"
    );
}

#[test]
fn import_isolates_tenants_from_the_supplied_scope() {
    let (_source, engine_a) = open_engine("isolation");
    seed_source(&engine_a);
    let destination = export_destination("isolation-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(documents_and_families()),
        &destination,
    )
    .expect("exports")
    .identity()
    .to_owned();

    // A single-scope import under one identity still succeeds for its own
    // tenant only; the other tenant's records are out of scope here.
    let (_target, engine_b) = open_engine("isolation-b");
    let result = run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "beta"),
        &SmugglerImportPlan::importing(documents_and_families()),
        &artifact_of(&destination, &identity),
    )
    .expect("imports beta only");
    assert_eq!(result.applied(ExportFamily::Documents), 1);

    let tenant_scope = scope(&engine_b, "alpha");
    let snapshot = engine_b.read_snapshot(&tenant_scope).expect("snapshot");
    assert!(
        engine_b.snapshot_documents(&tenant_scope, &snapshot).expect("documents").is_empty(),
        "alpha's records never entered through beta's scope"
    );
}

#[test]
fn import_applies_tombstones_as_engine_deletes() {
    let (_source, engine_a) = open_engine("tombstones");
    seed_source(&engine_a);
    let destination = export_destination("tombstones-dest");
    let identity = run_smuggler_export(
        &engine_a,
        &super::SmugglerExportPlan::for_selection(ExportSelection::all()),
        &destination,
    )
    .expect("exports")
    .identity()
    .to_owned();

    let (_target, engine_b) = open_engine("tombstones-b");
    let plan = SmugglerImportPlan::importing(ExportSelection::all());
    run_smuggler_import(
        &engine_b,
        &scope(&engine_b, "alpha"),
        &plan,
        &artifact_of(&destination, &identity),
    )
    .expect("imports with tombstones");

    let tenant_scope = scope(&engine_b, "alpha");
    let snapshot = engine_b.read_snapshot(&tenant_scope).expect("snapshot");
    let documents = engine_b.snapshot_documents(&tenant_scope, &snapshot).expect("documents");
    assert_eq!(documents.len(), 3, "order-3 stays deleted by its imported tombstone");
    assert!(documents.iter().all(|document| document.id().as_str() != "order-3"));
}

// ---------------------------------------------------------------------------
// Tombstone retention watermark (GAP-R02)
// ---------------------------------------------------------------------------

/// Builds one shared watermark whose history bound sits above the seeded
/// delete, with the window's deletion evidence extracted from the journal.
/// The bound is the journal's own durable head, so the window fully exists
/// (decision D2.4).
fn watermark_above_seeded_delete(engine: &Engine) -> crate::replication::SharedTombstoneRetention {
    use crate::replication::{ReplicationClockStamp, SharedTombstoneRetention, StampedCommit};

    let handle = SharedTombstoneRetention::new();
    let bound = engine.journaled_high_water().expect("high water").unwrap_or_default();
    let mut window = Vec::new();
    let mut after = crate::types::StorageSequence::default();
    loop {
        let page = engine
            .tenant_commits(&TenantId::try_new("alpha").expect("valid"), after, 1024)
            .expect("journal page");
        if page.is_empty() {
            break;
        }
        let reached = page.last().is_none_or(|commit| commit.sequence().get() >= bound.get());
        for commit in &page {
            if commit.sequence().get() < bound.get() {
                window.push(StampedCommit::new(ReplicationClockStamp::new(0, 0), commit.clone()));
            }
        }
        after = page.last().map_or(after, crate::storage::PublishedCommit::sequence);
        if reached {
            break;
        }
    }
    handle.with_lock(|watermark| {
        watermark
            .declare_history_bound_from_commits(bound, bound, &window, true)
            .expect("the window declares cleanly");
    });
    handle
}

#[test]
fn a_tombstone_export_carries_the_retained_evidence_below_the_bound() {
    let (_dir, engine) = open_engine("gapr02-smuggler");
    seed_source(&engine);
    let destination = export_destination("gapr02-smuggler-dest");
    let retention = watermark_above_seeded_delete(&engine);
    retention.with_lock(|watermark| {
        assert_eq!(watermark.held_evidence_count(), 1, "the seeded delete is retained");
    });

    // Without the watermark the export emits the journal's own delete
    // record; with it, the retained fact below the declared bound stands in
    // — one tombstone either way, never two.
    let plain = run_smuggler_export(
        &engine,
        &super::SmugglerExportPlan::for_selection(ExportSelection::all()),
        &destination,
    )
    .expect("exports without a watermark");
    assert_eq!(plain.count(ExportFamily::Tombstones), 1);

    let destination_two = export_destination("gapr02-smuggler-dest-two");
    let retained = run_smuggler_export(
        &engine,
        &super::SmugglerExportPlan::for_selection(ExportSelection::all())
            .with_retention(retention.clone()),
        &destination_two,
    )
    .expect("exports with the watermark");
    assert_eq!(
        retained.count(ExportFamily::Tombstones),
        1,
        "the retained fact replaces the journal record, it does not duplicate it"
    );
    assert_eq!(
        plain.identity(),
        retained.identity(),
        "the retained fact encodes the same logical delete, so the artifact is identical"
    );

    // A floor advanced past the retained fact removes it from the artifact:
    // the export discloses exactly what the watermark holds.
    retention.with_lock(|watermark| {
        watermark.advance_floor(crate::types::StorageSequence::new(u64::MAX)).expect("no consumer");
    });
    let destination_three = export_destination("gapr02-smuggler-dest-three");
    let discarded = run_smuggler_export(
        &engine,
        &super::SmugglerExportPlan::for_selection(ExportSelection::all()).with_retention(retention),
        &destination_three,
    )
    .expect("exports after the floor advanced");
    assert_eq!(
        discarded.count(ExportFamily::Tombstones),
        0,
        "a discarded tombstone is not silently invented from the journal below the bound"
    );
}
