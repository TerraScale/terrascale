//! In-crate storage tests: verified recovery, codec round trips, and the
//! durable ledger's tolerance for a torn trailing record.
//!
//! These tests construct storage state directly because the faults they prove
//! live below the public Engine seam. They never inspect raw WAL or page
//! bytes; corruption is injected by writing files, and every assertion checks
//! a typed outcome only.

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
};

use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId,
};

use super::{
    FileStorage, StorageOpenError, StoragePort,
    faults::FaultPoint,
    intent::{CommitIntent, CommitResult, StagedWrite},
    write_set_digest,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-w31-storage-{tag}-{}-{index}", std::process::id()))
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

fn document(id: &str, collection: &str, text: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::String(text.to_owned()),
    )
}

fn put(document: Document, expected: ExpectedVersion) -> StagedWrite {
    StagedWrite::Put { document, expected }
}

fn intent(operation: &str, writes: Vec<StagedWrite>, durability: LocalDurability) -> CommitIntent {
    let tenant = tenant();
    CommitIntent {
        digest: write_set_digest(&tenant, &writes),
        tenant,
        operation: OperationId::try_new(operation).expect("valid identifier"),
        snapshot_sequence: StorageSequence::new(0),
        durability,
        writes,
    }
}

fn open_storage(tag: &str) -> (PathBuf, FileStorage) {
    let dir = location(tag);
    let storage = FileStorage::open(&dir).expect("opens a fresh location");
    (dir, storage)
}

#[test]
fn recovery_discards_prepare_frames_without_a_valid_commit_marker() {
    let (dir, mut storage) = open_storage("prepare-without-marker");
    storage.arm_test_fault(FaultPoint::CommitMarkerApplication);
    let outcome = storage.commit(intent(
        "op-prepare",
        vec![put(document("doc-1", "users", "prepared"), ExpectedVersion::New)],
        LocalDurability::Written,
    ));
    assert!(matches!(outcome, CommitResult::Rejected(_)));
    drop(storage);

    let reopened = FileStorage::open(&dir).expect("recovery ignores the unfinished prepare");
    let tenant = tenant();
    let id = DocumentId::try_new("doc-1").expect("valid identifier");
    assert!(
        reopened.read_document_at(StorageSequence::new(1), &tenant, &id).is_none(),
        "a prepare frame without a commit marker must never publish a document"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn recovery_rejects_bad_checksum_and_truncated_frame() {
    // Mid-file damage must fail closed. Commit twice so the damaged frame is
    // followed by an intact one, then corrupt a byte inside the first commit's
    // frames.
    let (dir, mut storage) = open_storage("mid-file-checksum");
    for index in 0..2 {
        let outcome = storage.commit(intent(
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        ));
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    drop(storage);

    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    bytes[5] = bytes[5].wrapping_add(1);
    std::fs::write(&wal, bytes).expect("wal rewrites");
    assert_eq!(
        FileStorage::open(&dir).err(),
        Some(StorageOpenError::FormatMismatch),
        "a checksum failure in a non-trailing frame is mid-file corruption and fails closed"
    );
    std::fs::remove_dir_all(&dir).ok();

    // A truncated frame that is not the trailing frame is the same mid-file
    // corruption: a valid frame is cut through and bytes still follow it.
    let (dir, mut storage) = open_storage("mid-file-truncated");
    for index in 0..2 {
        let outcome = storage.commit(intent(
            &format!("op-{index}"),
            vec![put(
                document(&format!("doc-{index}"), "users", &format!("value-{index}")),
                ExpectedVersion::New,
            )],
            LocalDurability::Synced,
        ));
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    drop(storage);
    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    // Keep the first commit's group whole, then overwrite the rest with a
    // single valid-looking prefix of a frame header so the damaged region is
    // followed by remaining bytes: the parser sees a frame it cannot verify
    // with data behind it.
    let cut = bytes.len() / 2;
    bytes.truncate(cut);
    bytes.extend_from_slice(&[3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF]);
    std::fs::write(&wal, bytes).expect("wal rewrites");
    assert_eq!(
        FileStorage::open(&dir).err(),
        Some(StorageOpenError::FormatMismatch),
        "a truncated frame with bytes behind it fails closed"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn torn_trailing_wal_frame_is_truncated_at_recovery_and_data_survives() {
    let (dir, mut storage) = open_storage("torn-tail");
    for index in 0..2 {
        let outcome = storage.commit(intent(
            &format!("op-{index}"),
            vec![put(
                document(&format!("doc-{index}"), "users", &format!("value-{index}")),
                ExpectedVersion::New,
            )],
            LocalDurability::Written,
        ));
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    drop(storage);

    // Flip one byte of the final frame: an interrupted or partially lost
    // append. Recovery must treat that trailing frame as absent, truncate it,
    // and open on the verified prefix.
    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    let last = bytes.len() - 1;
    bytes[last] = bytes[last].wrapping_add(1);
    std::fs::write(&wal, bytes).expect("wal rewrites");
    let length_after_damage = std::fs::metadata(&wal).expect("wal exists").len();

    let reopened = FileStorage::open(&dir).expect("a torn trailing frame opens on its prefix");
    let tenant = tenant();
    assert!(
        reopened
            .read_document_at(StorageSequence::new(1), &tenant, document("doc-0", "users", "").id())
            .is_some(),
        "the commit before the torn tail must survive"
    );
    let truncated = std::fs::metadata(&wal).expect("wal exists").len();
    assert!(
        truncated < length_after_damage,
        "recovery truncates the torn trailing frame instead of leaving it behind"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn failed_wal_append_rolls_back_to_the_last_good_offset() {
    let (dir, mut storage) = open_storage("append-rollback");
    assert!(matches!(
        storage.commit(intent(
            "op-seed",
            vec![put(document("doc-0", "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    let before = std::fs::metadata(dir.join("wal")).expect("wal exists").len();

    storage.arm_test_fault(FaultPoint::WalAppend);
    assert!(matches!(
        storage.commit(intent(
            "op-torn",
            vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Rejected(_)
    ));
    assert_eq!(
        std::fs::metadata(dir.join("wal")).expect("wal exists").len(),
        before,
        "a failed append must roll back to the last-good offset"
    );

    // The store still opens, and a later commit appends onto the clean log.
    drop(storage);
    let mut reopened = FileStorage::open(&dir).expect("opens after the rolled-back append");
    assert!(matches!(
        reopened.commit(intent(
            "op-after",
            vec![put(document("doc-2", "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    drop(reopened);
    let again = FileStorage::open(&dir).expect("reopens with the later commit");
    let tenant = tenant();
    assert!(
        again
            .read_document_at(StorageSequence::new(2), &tenant, document("doc-2", "users", "").id())
            .is_some()
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn open_rejects_a_foreign_storage_format() {
    let (dir, storage) = open_storage("foreign-format");
    drop(storage);
    std::fs::write(dir.join("format"), b"NOT-TBV2FMT\x00\x00\x00\x00\x00\x00\x00\x00")
        .expect("format rewrites");
    assert_eq!(FileStorage::open(&dir).err(), Some(StorageOpenError::FormatMismatch));
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn recovery_replays_a_verified_commit_that_was_never_published() {
    // A written commit marker that never reached pointer publication is rolled
    // forward at reopen, without claiming the post-publication syncs.
    let (dir, mut storage) = open_storage("roll-forward");
    let writes = vec![put(document("doc-1", "users", "rolled"), ExpectedVersion::New)];
    let mut intent = intent("op-roll", writes.clone(), LocalDurability::Synced);
    intent.snapshot_sequence = StorageSequence::new(0);
    storage.arm_test_fault(FaultPoint::PointerPublish);
    let outcome = storage.commit(intent);
    assert!(matches!(outcome, CommitResult::Rejected(_)));
    drop(storage);

    let reopened = FileStorage::open(&dir).expect("reopens after the interrupted publication");
    let tenant = tenant();
    let id = DocumentId::try_new("doc-1").expect("valid identifier");
    assert!(
        reopened.read_document_at(StorageSequence::new(1), &tenant, &id).is_some(),
        "a verified commit marker must survive the interrupted pointer publication"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn codec_round_trips_documents_and_digest_is_order_independent() {
    let document = Document::new(
        DocumentId::try_new("doc-9").expect("valid identifier"),
        CollectionName::try_new("users").expect("valid identifier"),
        DocumentValue::Object(
            [
                ("name".to_owned(), DocumentValue::String("ada".to_owned())),
                ("count".to_owned(), DocumentValue::Integer(-7)),
                ("ratio".to_owned(), DocumentValue::Number(1.5)),
                ("flags".to_owned(), DocumentValue::Array(vec![DocumentValue::Boolean(true)])),
            ]
            .into_iter()
            .collect(),
        ),
    );
    assert!(super::document_within_limits(&document));
    let first = vec![
        put(document.clone(), ExpectedVersion::New),
        StagedWrite::Delete {
            collection: Some(CollectionName::try_new("users").expect("valid identifier")),
            id: DocumentId::try_new("doc-10").expect("valid identifier"),
            expected: ExpectedVersion::Any,
        },
    ];
    let mut reversed = first.clone();
    reversed.reverse();
    let tenant = tenant();
    assert_eq!(
        write_set_digest(&tenant, &first),
        write_set_digest(&tenant, &reversed),
        "the same logical write set must digest identically in any staging order"
    );
    let other_tenant = TenantId::try_new("tenant-b").expect("valid tenant");
    assert_ne!(
        write_set_digest(&tenant, &first),
        write_set_digest(&other_tenant, &first),
        "the digest binds the tenant as well as the writes"
    );
}

#[test]
fn ledger_scan_ignores_a_torn_trailing_record() {
    use super::ledger::LedgerIndex;
    let (dir, storage) = open_storage("torn-ledger");
    drop(storage);
    let path = dir.join("ledger");
    let mut bytes = std::fs::read(&path).expect("ledger reads");
    bytes.extend_from_slice(&[0x01, 0x00]);
    std::fs::write(&path, bytes).expect("ledger rewrites");
    let index = LedgerIndex::scan(&path);
    let tenant = tenant();
    let operation = OperationId::try_new("op-none").expect("valid identifier");
    assert!(
        index.entry(&tenant, &operation).is_none(),
        "an unsynced trailing record must not resolve anything"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn root_identities_depend_on_content() {
    use super::root::RootTable;
    let table = RootTable::empty()
        .apply(&tenant(), &[put(document("doc-1", "users", "value"), ExpectedVersion::New)]);
    let first = super::root::root_identity_of(1, &table);
    let changed = RootTable::empty()
        .apply(&tenant(), &[put(document("doc-1", "users", "other"), ExpectedVersion::New)]);
    let second = super::root::root_identity_of(1, &changed);
    assert_ne!(first.as_str(), second.as_str());
    assert_eq!(first.as_str(), super::root::root_identity_of(1, &table).as_str());
}

#[test]
fn manifests_that_do_not_verify_are_ignored_during_recovery() {
    let (dir, mut storage) = open_storage("bad-manifest");
    let outcome = storage.commit(intent(
        "op-synced",
        vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    ));
    assert!(matches!(outcome, CommitResult::Committed(_)));
    drop(storage);
    // Corrupt the published manifest so it cannot verify; recovery must fall
    // back to the verified write-ahead commit instead of failing or exposing
    // an unverified root.
    let manifest = dir.join("manifest");
    let mut bytes = std::fs::read(&manifest).expect("manifest reads");
    let last = bytes.len().saturating_sub(1);
    bytes[last] = bytes[last].wrapping_add(1);
    std::fs::write(&manifest, bytes).expect("manifest rewrites");

    let reopened = FileStorage::open(&dir).expect("reopens by rebuilding from the verified log");
    let tenant = tenant();
    let id = DocumentId::try_new("doc-1").expect("valid identifier");
    assert!(reopened.read_document_at(StorageSequence::new(1), &tenant, &id).is_some());
    std::fs::remove_dir_all(&dir).ok();
}
