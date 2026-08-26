//! In-crate Logical Migration tests (BK-03).
//!
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the data-bearing migration behavior tests live
//! in-crate with the `TenantScope::from_parts` fixture, mirroring the backup
//! and families suites. Every assertion goes through the public
//! `crate::migration` seam plus the engine's own read paths; the V1 export
//! inputs are synthetic representations produced by the crate fixture
//! builder, which is honest about the fact that no live V1 store is opened.
//!
//! These tests prove observable behavior only. They make no recovery,
//! durability, or performance claim: formal Recovery Receipts belong to the
//! exclusive recovery lane.

use std::fmt::Write as _;
use std::path::PathBuf;
use std::sync::Arc;

use crate::engine::{Engine, EngineConfig};
use crate::families::{
    CounterRead, FamilyReadContext, FamilyReadRequest, FamilyReadResult, IdentityEntry,
    IdentityRead,
};
use crate::migration::{
    MigrationCategory, MigrationError, MigrationKind, MigrationOptions, MigrationRecord,
    MigrationRevisionPolicy, MigrationTombstonePolicy, fixture, run_migration,
};
use crate::types::{CollectionName, DocumentId, DocumentValue, TenantId, TenantScope};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-bk03-migration-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid collection")
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn text(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

/// Reads one tenant's live documents through the engine read path — the
/// proof that migration wrote through the Engine transaction seam.
fn live_documents(engine: &Engine, tenant_name: &str) -> Vec<crate::types::Document> {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    engine.snapshot_documents(&tenant_scope, &snapshot).expect("documents")
}

fn document_record(
    tenant_name: &str,
    collection_name: &str,
    id: &str,
    value: DocumentValue,
    version: u64,
) -> MigrationRecord {
    MigrationRecord::Document {
        tenant: tenant(tenant_name),
        collection: collection(collection_name),
        id: document_id(id),
        value,
        version,
    }
}

/// One export fixture exercising every record kind the loss matrix names.
fn full_fixture_records() -> Vec<MigrationRecord> {
    vec![
        document_record("alpha", "Orders", "order-1", text("first"), 1),
        document_record("alpha", "Orders", "order-1", text("first-amended"), 2),
        document_record("alpha", "Orders", "order-2", DocumentValue::Integer(7), 1),
        MigrationRecord::Attachment {
            tenant: tenant("alpha"),
            document: document_id("order-2"),
            name: "invoice.pdf".to_owned(),
            content_type: "application/pdf".to_owned(),
            size: 2048,
            hash: "b17b4a".to_owned(),
        },
        MigrationRecord::Tombstone {
            tenant: tenant("alpha"),
            collection: collection("Orders"),
            id: document_id("order-3"),
            version_at_delete: 4,
        },
        MigrationRecord::Counter {
            tenant: tenant("alpha"),
            document: document_id("order-2"),
            name: "downloads".to_owned(),
            value: 9,
        },
        MigrationRecord::TimeSeries {
            tenant: tenant("alpha"),
            document: document_id("order-2"),
            name: "orders-per-sec".to_owned(),
            timestamp: 1_000,
            values: vec![1.5, 2.5],
            tag: Some("batch-a".to_owned()),
        },
        MigrationRecord::Identity {
            tenant: tenant("alpha"),
            name: "order-seq".to_owned(),
            value: 41,
        },
        MigrationRecord::CompareExchange {
            tenant: tenant("alpha"),
            key: "reservation/1".to_owned(),
            value: text("owner-a"),
        },
        MigrationRecord::StorageInternal { kind: "journal-header".to_owned() },
        MigrationRecord::OrphanBlob { hash: "dead00".to_owned() },
        MigrationRecord::PlatformConfig { kind: "connection-string".to_owned() },
        MigrationRecord::IndexDefinition { name: "Orders/ByTotal".to_owned() },
    ]
}

fn write_fixture(tag: &str, records: &[MigrationRecord]) -> PathBuf {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    fixture::write_export(&dir, records).expect("fixture writes");
    dir
}

fn default_options() -> MigrationOptions {
    MigrationOptions::default()
}

#[test]
fn migration_validates_the_v1_export_manifest_before_any_write() {
    let (_dir, engine) = open_engine("manifest-first");
    let source = write_fixture("manifest-first-src", &full_fixture_records());
    let manifest = source.join("manifest.tbv1m");
    let body = std::fs::read_to_string(&manifest).expect("reads the manifest");
    // Corrupt one bound fact: the identity no longer binds the content.
    let tampered = body.replace("count document 3", "count document 9");
    assert_ne!(&tampered, &body, "the fixture must carry the fact being tampered");
    std::fs::write(&manifest, tampered).expect("writes the tampered manifest");

    let report = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options());

    assert_eq!(report.err(), Some(MigrationError::ManifestInvalid));
    assert!(
        live_documents(&engine, "alpha").is_empty(),
        "no write may happen when the manifest fails validation"
    );
}

#[test]
fn migration_rejects_an_unsupported_manifest_version() {
    let (_dir, engine) = open_engine("version");
    let source = write_fixture("version-src", &full_fixture_records());
    let manifest = source.join("manifest.tbv1m");
    let body = std::fs::read_to_string(&manifest).expect("reads the manifest");
    let bumped = body.replace("terrabase-v1-export 1", "terrabase-v1-export 2");
    std::fs::write(&manifest, bumped).expect("writes the future-version manifest");

    let report = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options());

    assert_eq!(report.err(), Some(MigrationError::UnsupportedVersion));
    assert!(live_documents(&engine, "alpha").is_empty(), "nothing was written");
}

#[test]
fn migration_writes_only_through_the_engine_transaction_seam() {
    let (_dir, engine) = open_engine("engine-seam");
    let source = write_fixture("engine-seam-src", &full_fixture_records());
    let report = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options())
        .expect("migrates");

    let documents = live_documents(&engine, "alpha");
    let order1 = documents
        .iter()
        .find(|document| document.id().as_str() == "order-1")
        .expect("the converted document is readable through the engine seam");
    assert_eq!(order1.value(), &text("first-amended"), "the newest version converted");
    assert_eq!(order1.collection().as_str(), "Orders");

    // The counter converted through the family seam and reads back typed.
    let tenant_scope = scope(&engine, "alpha");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.families();
    let deadline = crate::types::Deadline::after(std::time::Duration::from_mins(1))
        .expect("a positive deadline");
    let read = store
        .read(
            FamilyReadContext::new(tenant_scope.clone(), snapshot.clone()),
            FamilyReadRequest::counter_group(
                CounterRead::try_new(
                    document_id("order-2"),
                    vec!["downloads".to_owned()],
                    deadline,
                    crate::types::Cancellation::new(),
                )
                .expect("valid read"),
            ),
        )
        .expect("counter reads");
    let FamilyReadResult::CounterGroup(group) = read else {
        panic!("a counter read returns a counter group");
    };
    assert_eq!(group.find("downloads"), Some(Some(9)), "the V1 counter value arrived");

    let identity_read = store
        .read(
            FamilyReadContext::new(tenant_scope, snapshot),
            FamilyReadRequest::identity_range(
                IdentityRead::try_new(
                    vec!["order-seq".to_owned()],
                    deadline,
                    crate::types::Cancellation::new(),
                )
                .expect("valid read"),
            ),
        )
        .expect("identity reads");
    let FamilyReadResult::IdentityRange(ranges) = identity_read else {
        panic!("an identity read returns identity ranges");
    };
    assert_eq!(
        ranges.entries().iter().find(|entry| entry.name() == "order-seq").map(IdentityEntry::next),
        Some(41),
        "the V1 identity seed arrived"
    );
    assert_eq!(report.converted_documents(), 3);
}

#[test]
fn migration_is_idempotent_for_the_same_input_and_target_state() {
    let (_dir, engine) = open_engine("idempotent");
    let source = write_fixture("idempotent-src", &full_fixture_records());

    let first = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options())
        .expect("first run");
    let second = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options())
        .expect("second run");

    assert_eq!(first.duplicates(), 0, "a fresh target has no duplicates");
    assert!(second.duplicates() > 0, "the second run resolves its deterministic operations");
    assert_eq!(second.converted_documents(), 0, "nothing converts twice");
    let documents = live_documents(&engine, "alpha");
    let user_documents = documents
        .iter()
        .filter(|document| document.collection().as_str() != "terrabase.families")
        .count();
    assert_eq!(
        user_documents, 2,
        "order-1 and order-2; the tombstone removed order-3; family records are separate"
    );
    assert_eq!(first.identity(), second.identity(), "both runs bind the same input identity");
}

#[test]
fn interrupted_migration_resumes_from_its_last_durable_record() {
    let (_dir, engine) = open_engine("resume");
    let source = write_fixture("resume-src", &full_fixture_records());

    let token = Arc::new(crate::types::Cancellation::new());
    let cancel_after_two = {
        let token = Arc::clone(&token);
        Arc::new(move |applied: u64| {
            if applied >= 2 {
                token.cancel();
            }
        })
    };
    let interrupted = run_migration(
        &engine,
        &scope(&engine, "alpha"),
        &source,
        &MigrationOptions::default()
            .with_cancellation(Arc::clone(&token))
            .with_progress_observer(cancel_after_two),
    );
    assert_eq!(interrupted.err(), Some(MigrationError::Cancelled));

    let resumed = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options())
        .expect("resumes");
    assert!(resumed.duplicates() >= 2, "the durable records of the first run are resolved");
    assert!(resumed.verification().all_verdicts(), "the resumed run still verifies");

    // The final state matches a single uninterrupted run.
    let (_dir2, engine2) = open_engine("resume-clean");
    let clean = run_migration(&engine2, &scope(&engine2, "alpha"), &source, &default_options())
        .expect("clean run");
    let resumed_documents = live_documents(&engine, "alpha");
    let clean_documents = live_documents(&engine2, "alpha");
    assert_eq!(resumed_documents.len(), clean_documents.len());
    assert_eq!(
        resumed.converted_documents() + resumed.duplicates(),
        clean.converted_documents() + clean.duplicates()
    );
}

#[test]
fn migration_operation_identities_are_stable_across_selections() {
    // An unselected-first fixture: the identity record sorts before the
    // documents in decode order, so a documents-only run and an all-kinds
    // run disagree about which record each ordinal names unless identity
    // binds to decode position alone.
    let records = vec![
        MigrationRecord::Identity {
            tenant: tenant("alpha"),
            name: "order-seq".to_owned(),
            value: 41,
        },
        document_record("alpha", "Orders", "order-1", text("v1"), 1),
        document_record("alpha", "Orders", "order-1", text("v2"), 2),
    ];
    let source = write_fixture("stable-selection-src", &records);
    let (_dir, engine) = open_engine("stable-selection");

    // A partial run under selection A (documents only) is interrupted
    // after its first durable record.
    let token = Arc::new(crate::types::Cancellation::new());
    let cancel_after_one = {
        let token = Arc::clone(&token);
        Arc::new(move |applied: u64| {
            if applied >= 1 {
                token.cancel();
            }
        })
    };
    let interrupted = run_migration(
        &engine,
        &scope(&engine, "alpha"),
        &source,
        &MigrationOptions::default()
            .only_documents()
            .with_cancellation(Arc::clone(&token))
            .with_progress_observer(cancel_after_one),
    );
    assert_eq!(interrupted.err(), Some(MigrationError::Cancelled));

    // The resumed run selects every kind. A record's operation identity is
    // a property of its decode position in the representation, never of
    // the run's selection, so the record the partial run skipped must
    // convert — not resolve as a duplicate it never applied.
    let resumed = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options())
        .expect("resumes under a wider selection");
    assert_eq!(
        resumed.converted(MigrationKind::Identity),
        1,
        "the record unselected by the partial run converts on the wider run"
    );
    assert_eq!(resumed.converted_documents(), 1, "the partial run already converted v1");
    assert_eq!(resumed.duplicates(), 1, "exactly the partial run's durable record");
    assert_eq!(
        resumed.converted_documents()
            + resumed.converted(MigrationKind::Identity)
            + resumed.duplicates(),
        3,
        "every decoded record is accounted exactly once"
    );

    // The skipped record's data actually arrived through the family seam.
    let tenant_scope = scope(&engine, "alpha");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let deadline = crate::types::Deadline::after(std::time::Duration::from_mins(1))
        .expect("a positive deadline");
    let read = engine
        .families()
        .read(
            FamilyReadContext::new(tenant_scope, snapshot),
            FamilyReadRequest::identity_range(
                IdentityRead::try_new(
                    vec!["order-seq".to_owned()],
                    deadline,
                    crate::types::Cancellation::new(),
                )
                .expect("valid read"),
            ),
        )
        .expect("identity reads");
    let FamilyReadResult::IdentityRange(ranges) = read else {
        panic!("an identity read returns identity ranges");
    };
    assert_eq!(
        ranges.entries().iter().find(|entry| entry.name() == "order-seq").map(IdentityEntry::next),
        Some(41),
        "the record the partial run skipped is present, not silently dropped"
    );
    let documents = live_documents(&engine, "alpha");
    let user_documents: Vec<_> = documents
        .iter()
        .filter(|document| document.collection().as_str() != "terrabase.families")
        .collect();
    assert_eq!(user_documents.len(), 1);
    assert_eq!(user_documents[0].value(), &text("v2"));
}

#[test]
fn migration_reports_an_explicit_loss_matrix_with_reasons_and_counts() {
    let (_dir, engine) = open_engine("loss");
    let source = write_fixture("loss-src", &full_fixture_records());
    let report = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options())
        .expect("migrates");

    let matrix = report.loss_matrix();
    // Every loss-matrix category appears exactly once.
    assert_eq!(matrix.rows().len(), 9, "the nine contract categories");
    for category in [
        MigrationCategory::DocumentsAndRevisions,
        MigrationCategory::ReferencedAttachments,
        MigrationCategory::TombstonesAndDeletedRanges,
        MigrationCategory::CountersAndTimeSeries,
        MigrationCategory::IdentitiesAndCompareExchange,
        MigrationCategory::V1StorageInternals,
        MigrationCategory::UnreferencedBlobs,
        MigrationCategory::PlatformOwnedConfiguration,
        MigrationCategory::IndexDefinitions,
    ] {
        assert!(matrix.row(category).is_some(), "{category:?} is present");
    }
    // Dropped categories carry counts and reasons; nothing is silent.
    let internals = matrix.row(MigrationCategory::V1StorageInternals).expect("present");
    assert_eq!(internals.converted(), 0);
    assert_eq!(internals.dropped(), 1);
    assert!(internals.reason().is_some(), "a dropped category names its reason");
    let platform = matrix.row(MigrationCategory::PlatformOwnedConfiguration).expect("present");
    assert_eq!(platform.dropped(), 1);
    assert!(platform.reason().is_some());
    let indexes = matrix.row(MigrationCategory::IndexDefinitions).expect("present");
    assert_eq!(indexes.dropped(), 1);
    assert!(indexes.reason().is_some());
    let blobs = matrix.row(MigrationCategory::UnreferencedBlobs).expect("present");
    assert_eq!(blobs.dropped(), 1);
    assert!(blobs.reason().is_some());
    let documents = matrix.row(MigrationCategory::DocumentsAndRevisions).expect("present");
    assert_eq!(documents.converted(), 3);
    assert_eq!(documents.dropped(), 0);
}

#[test]
fn migration_verifies_counts_and_checksums_before_reporting_success() {
    let (_dir, engine) = open_engine("verify");
    let source = write_fixture("verify-src", &full_fixture_records());
    let report = run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options())
        .expect("migrates");
    assert!(report.verification().all_verdicts(), "counts and digests verified");

    // A manifest whose counts lie about its records fails closed.
    let lying = write_fixture("verify-lying-src", &full_fixture_records());
    let manifest = lying.join("manifest.tbv1m");
    let body = std::fs::read_to_string(&manifest).expect("reads");
    // Recompute the identity over the lying body so only the count lies.
    let identity = recompute_identity(&body.replace("count document 3", "count document 4"));
    let forged = replace_identity(&body.replace("count document 3", "count document 4"), &identity);
    std::fs::write(&manifest, forged).expect("writes");
    let (_dir2, engine2) = open_engine("verify-lying");
    assert_eq!(
        run_migration(&engine2, &scope(&engine2, "alpha"), &lying, &default_options()).err(),
        Some(MigrationError::ManifestInvalid),
        "decoded counts must reconcile with the manifest before success"
    );
}

#[test]
fn migration_rejects_a_truncated_or_corrupt_input_record() {
    let (_dir, engine) = open_engine("corrupt");
    let source = write_fixture("corrupt-src", &full_fixture_records());
    let segment = first_segment(&source);
    let segment_name = segment.file_name().expect("a segment name").to_owned();
    let body = std::fs::read(&segment).expect("reads the segment");

    // Truncated: a producer that sealed its manifest over a segment cut
    // inside the last frame — the manifest binds the damaged bytes, so the
    // record decoder is what fails closed.
    let truncated = location("corrupt-truncated");
    std::fs::remove_dir_all(&truncated).ok();
    fixture::copy_export(&source, &truncated).expect("copies");
    std::fs::write(truncated.join(&segment_name), &body[..body.len() - 3]).expect("truncates");
    rebind_manifest(&truncated);
    assert_eq!(
        run_migration(&engine, &scope(&engine, "alpha"), &truncated, &default_options()).err(),
        Some(MigrationError::RecordTruncated)
    );

    // Corrupt: an unknown record-kind tag inside an intact frame envelope.
    let corrupt = location("corrupt-tag");
    std::fs::remove_dir_all(&corrupt).ok();
    fixture::copy_export(&source, &corrupt).expect("copies");
    let mut bytes = body.clone();
    // The first byte of a frame payload is the kind tag; frame 0's payload
    // starts after the four length bytes.
    bytes[4] = 0xEF;
    std::fs::write(corrupt.join(&segment_name), bytes).expect("corrupts");
    rebind_manifest(&corrupt);
    assert_eq!(
        run_migration(&engine, &scope(&engine, "alpha"), &corrupt, &default_options()).err(),
        Some(MigrationError::RecordCorrupt)
    );
    assert!(live_documents(&engine, "alpha").is_empty(), "nothing was written");
}

#[test]
fn migration_rejects_a_document_order_violation_while_decoding() {
    let (_dir, engine) = open_engine("order-contract");

    // Descending key: a revision of order-1 arrives after the record stream
    // already moved past that key to order-2. Under `LatestOnly` this shape
    // would silently convert a non-newest version as the key's latest, so
    // the documented order contract must be enforced by the production
    // decoder, not only by the fixture producer's gate.
    let descending_key = write_reordered_export(
        "order-key-src",
        &[
            document_record("alpha", "Orders", "order-1", text("v1"), 1),
            document_record("alpha", "Orders", "order-1", text("v2"), 2),
            document_record("alpha", "Orders", "order-2", DocumentValue::Integer(7), 1),
        ],
        &[
            document_record("alpha", "Orders", "order-1", text("v1"), 1),
            document_record("alpha", "Orders", "order-2", DocumentValue::Integer(7), 1),
            document_record("alpha", "Orders", "order-1", text("v2"), 2),
        ],
    );
    assert_eq!(
        run_migration(&engine, &scope(&engine, "alpha"), &descending_key, &default_options()).err(),
        Some(MigrationError::RecordCorrupt),
        "a key that sorts below its predecessor is corrupt, never convertible"
    );
    assert!(live_documents(&engine, "alpha").is_empty(), "nothing was written");

    // Non-ascending version of one key: v1 arrives after v2 of order-1.
    let descending_version = write_reordered_export(
        "order-version-src",
        &[
            document_record("alpha", "Orders", "order-1", text("v1"), 1),
            document_record("alpha", "Orders", "order-1", text("v2"), 2),
        ],
        &[
            document_record("alpha", "Orders", "order-1", text("v2"), 2),
            document_record("alpha", "Orders", "order-1", text("v1"), 1),
        ],
    );
    assert_eq!(
        run_migration(&engine, &scope(&engine, "alpha"), &descending_version, &default_options())
            .err(),
        Some(MigrationError::RecordCorrupt),
        "a version that does not ascend within one key is corrupt, never convertible"
    );
    assert!(live_documents(&engine, "alpha").is_empty(), "nothing was written");
}

/// Writes one representation whose manifest was bound for
/// `manifest_records` but whose segment carries `on_disk`: the fixture
/// producer gates document order at write time, so the violating bytes are
/// encoded by hand and the manifest rebound over them (the
/// corrupt-producer shape — internally consistent, lying about order).
fn write_reordered_export(
    tag: &str,
    manifest_records: &[MigrationRecord],
    on_disk: &[MigrationRecord],
) -> PathBuf {
    let source = write_fixture(tag, manifest_records);
    let segment = first_segment(&source);
    let mut buffer = Vec::new();
    for record in on_disk {
        super::codec::encode_into(&mut buffer, record);
    }
    std::fs::write(&segment, &buffer).expect("rewrites the segment");
    rebind_manifest(&source);
    source
}

/// Rebinds a fixture's manifest over the segment bytes as they are now,
/// keeping the original counts — the corrupt-producer shape, where the
/// manifest is internally consistent but lies about its records.
fn rebind_manifest(source: &std::path::Path) {
    let manifest = source.join("manifest.tbv1m");
    let body = std::fs::read_to_string(&manifest).expect("reads the manifest");
    let counts: Vec<&str> = body.lines().filter(|line| line.starts_with("count ")).collect();
    let mut members = Vec::new();
    let mut names: Vec<String> = std::fs::read_dir(source)
        .expect("reads the directory")
        .filter_map(std::result::Result::ok)
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.starts_with("segment-"))
        .collect();
    names.sort();
    for name in names {
        let bytes = std::fs::read(source.join(&name)).expect("reads the segment");
        members.push(format!("member {} {} {}", name, bytes.len(), fixture::identity_of(&bytes)));
    }
    let mut body = String::from("terrabase-v1-export 1\n");
    for count in counts {
        let _ = writeln!(body, "{count}");
    }
    for member in members {
        let _ = writeln!(body, "{member}");
    }
    // The identity binds every line except the identity line itself.
    let identity = fixture::identity_of(body.as_bytes());
    let mut bound = String::from("terrabase-v1-export 1\n");
    let _ = writeln!(bound, "identity {identity}");
    bound.push_str(body.strip_prefix("terrabase-v1-export 1\n").expect("the header"));
    std::fs::write(&manifest, bound).expect("writes the rebound manifest");
}

#[test]
fn migration_rejects_a_tampered_segment_digest() {
    let (_dir, engine) = open_engine("digest");
    let source = write_fixture("digest-src", &full_fixture_records());
    let segment = first_segment(&source);
    let mut bytes = std::fs::read(&segment).expect("reads");
    let last = bytes.len() - 1;
    bytes[last] = bytes[last].wrapping_add(1);
    std::fs::write(&segment, bytes).expect("tampers with the content, same length");

    assert_eq!(
        run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options()).err(),
        Some(MigrationError::ManifestInvalid),
        "segment digest verification is part of manifest validation"
    );
    assert!(live_documents(&engine, "alpha").is_empty());
}

#[test]
fn migration_tombstone_policy_controls_convert_or_drop() {
    let records = full_fixture_records();
    let source = write_fixture("tombstones-src", &records);

    let (_dir, converting) = open_engine("tombstones-convert");
    let converted =
        run_migration(&converting, &scope(&converting, "alpha"), &source, &default_options())
            .expect("converts tombstones");
    let row = converted
        .loss_matrix()
        .row(MigrationCategory::TombstonesAndDeletedRanges)
        .expect("present");
    assert_eq!(row.converted(), 1);
    assert_eq!(row.dropped(), 0);

    let (_dir2, dropping) = open_engine("tombstones-drop");
    let dropped = run_migration(
        &dropping,
        &scope(&dropping, "alpha"),
        &source,
        &MigrationOptions::default().with_tombstone_policy(MigrationTombstonePolicy::DropRecorded),
    )
    .expect("drops tombstones with a recorded count");
    let row =
        dropped.loss_matrix().row(MigrationCategory::TombstonesAndDeletedRanges).expect("present");
    assert_eq!(row.converted(), 0);
    assert_eq!(row.dropped(), 1);
    assert!(row.reason().is_some(), "the drop is disclosed, never silent");
}

#[test]
fn migration_revision_policy_latest_only_collapses_to_the_newest_version() {
    let records = vec![
        document_record("alpha", "Orders", "order-1", text("v1"), 1),
        document_record("alpha", "Orders", "order-1", text("v2"), 2),
        document_record("alpha", "Orders", "order-1", text("v3"), 3),
    ];
    let source = write_fixture("revisions-src", &records);

    let (_dir, all) = open_engine("revisions-all");
    let report = run_migration(&all, &scope(&all, "alpha"), &source, &default_options())
        .expect("all versions");
    assert_eq!(report.converted_documents(), 3);
    let documents = live_documents(&all, "alpha");
    assert_eq!(documents.len(), 1);
    assert_eq!(documents[0].value(), &text("v3"));
    assert_eq!(documents[0].metadata().version(), 3, "each record became one V2 version");

    let (_dir2, latest) = open_engine("revisions-latest");
    let report = run_migration(
        &latest,
        &scope(&latest, "alpha"),
        &source,
        &MigrationOptions::default().with_revision_policy(MigrationRevisionPolicy::LatestOnly),
    )
    .expect("latest only");
    assert_eq!(report.converted_documents(), 1);
    let documents = live_documents(&latest, "alpha");
    assert_eq!(documents.len(), 1);
    assert_eq!(documents[0].value(), &text("v3"));
    assert_eq!(documents[0].metadata().version(), 1, "one put of the newest V1 version");
}

#[test]
fn migration_selection_skips_unselected_kinds_with_recorded_drops() {
    let (_dir, engine) = open_engine("selection");
    let source = write_fixture("selection-src", &full_fixture_records());
    let report = run_migration(
        &engine,
        &scope(&engine, "alpha"),
        &source,
        &MigrationOptions::default().only_documents(),
    )
    .expect("migrates documents only");

    assert_eq!(report.converted_documents(), 3);
    let counters =
        report.loss_matrix().row(MigrationCategory::CountersAndTimeSeries).expect("present");
    assert_eq!(counters.converted(), 0);
    assert_eq!(counters.dropped(), 2, "counter and time-series records skipped by selection");
    assert!(counters.reason().is_some(), "unselected is a disclosed drop, never silent");
}

#[test]
fn logical_migration_makes_no_direct_v1_file_open_claim() {
    let (_dir, engine) = open_engine("no-v1-open");
    // A directory holding a raw V1 database file and no representation
    // manifest: the migration seam must refuse it — V2 never opens V1
    // storage files directly.
    let source = location("no-v1-open-src");
    std::fs::remove_dir_all(&source).ok();
    std::fs::create_dir_all(&source).expect("creates");
    std::fs::write(source.join("TerraBase.exe.db"), b"\x00\x01v1-page-bytes").expect("writes");

    assert_eq!(
        run_migration(&engine, &scope(&engine, "alpha"), &source, &default_options()).err(),
        Some(MigrationError::ManifestInvalid)
    );
    assert!(live_documents(&engine, "alpha").is_empty());
}

/// Recomputes the manifest identity over the non-identity lines.
fn recompute_identity(manifest_text: &str) -> String {
    let mut body = String::new();
    for line in manifest_text.lines().filter(|line| !line.starts_with("identity ")) {
        let _ = writeln!(body, "{line}");
    }
    fixture::identity_of(body.as_bytes())
}

fn replace_identity(manifest_text: &str, identity: &str) -> String {
    manifest_text
        .lines()
        .map(|line| {
            if line.starts_with("identity ") {
                format!("identity {identity}")
            } else {
                line.to_owned()
            }
        })
        .collect::<Vec<_>>()
        .join("\n")
        + "\n"
}

fn first_segment(source: &std::path::Path) -> PathBuf {
    let mut names: Vec<_> = std::fs::read_dir(source)
        .expect("reads the directory")
        .filter_map(std::result::Result::ok)
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.starts_with("segment-"))
        .collect();
    names.sort();
    source.join(names.first().expect("a segment exists"))
}
