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
    FileStorage, PublishedCommit, PublishedWrite, StorageOpenError, StoragePort,
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

/// Builds one intent whose snapshot is the store's current published
/// sequence, so replaces and deletes pass the conflict check.
fn fresh_intent(
    storage: &FileStorage,
    operation: &str,
    writes: Vec<StagedWrite>,
    durability: LocalDurability,
) -> CommitIntent {
    let mut intent = intent(operation, writes, durability);
    intent.snapshot_sequence = storage.published_state().0;
    intent
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
fn failed_torn_tail_truncation_fails_the_open() {
    let (dir, mut storage) = open_storage("truncate-fails");
    assert!(matches!(
        storage.commit(intent(
            "op-1",
            vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
            LocalDurability::Written,
        )),
        CommitResult::Committed(_)
    ));
    drop(storage);

    // Tear the trailing frame, then poison the truncation of exactly this
    // log. The open must fail loudly rather than hand back a writer whose
    // append offset still spans the torn bytes.
    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    let last = bytes.len() - 1;
    bytes[last] = bytes[last].wrapping_add(1);
    std::fs::write(&wal, bytes).expect("wal rewrites");
    super::wal::fail_truncate_for(wal.clone());
    assert_eq!(
        FileStorage::open(&dir).err(),
        Some(StorageOpenError::Unavailable),
        "a truncation that cannot complete must fail the open instead of masking the damage"
    );

    // The poison is one-shot and consumed: the same location opens once the
    // truncation can run, and the verified prefix survives.
    let reopened = FileStorage::open(&dir).expect("opens once the truncation succeeds");
    let tenant = tenant();
    assert!(
        reopened
            .read_document_at(
                StorageSequence::new(1),
                &tenant,
                &DocumentId::try_new("doc-1").expect("valid identifier")
            )
            .is_some()
    );
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn roll_forward_keeps_the_operation_digest_for_a_same_identity_retry() {
    // A commit that fails after its WAL group is durable but before its
    // Ready ledger record leaves no prior entry, so recovery's roll-forward
    // Published record must itself carry the digest from the WAL commit.
    let (dir, mut storage) = open_storage("roll-forward-digest");
    storage.arm_test_fault(FaultPoint::ReadyLedgerSync);
    assert!(matches!(
        storage.commit(intent(
            "op-a",
            vec![put(document("doc-a", "users", "a"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Rejected(_)
    ));
    drop(storage);

    let mut reopened = FileStorage::open(&dir).expect("rolls the durable group forward");
    let tenant = tenant();
    let operation = OperationId::try_new("op-a").expect("valid identifier");
    match reopened.resolve_operation(&tenant, &operation).expect("resolves") {
        super::intent::OperationResolution::Committed { sequence, durability } => {
            assert_eq!(sequence.get(), 1);
            assert_ne!(durability, LocalDurability::Synced);
        }
        other => panic!("the rolled-forward commit must resolve as committed, got {other:?}"),
    }
    // Same identity, same write set: the recorded resolution replays instead
    // of colliding as identity reuse.
    assert!(matches!(
        reopened.commit(intent(
            "op-a",
            vec![put(document("doc-a", "users", "a"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    // A different write set under the same identity is still reuse.
    assert!(matches!(
        reopened.commit(intent(
            "op-a",
            vec![put(document("doc-a", "users", "mutated"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Rejected(super::intent::CommitRejection::OperationIdReuse)
    ));
    drop(reopened);
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

// ---------------------------------------------------------------------------
// W4-CA-7: the bounded windowed snapshot read
// ---------------------------------------------------------------------------

/// Commits one document per call so the tenant's canonical key order mixes
/// collections and identifier orders.
fn commit_document(storage: &mut FileStorage, operation: &str, id: &str, collection: &str) {
    let outcome = storage.commit(intent(
        operation,
        vec![put(document(id, collection, "value"), ExpectedVersion::New)],
        LocalDurability::Written,
    ));
    assert!(matches!(outcome, CommitResult::Committed(_)));
}

#[test]
fn documents_at_from_pages_canonical_key_order_exactly() {
    // The windowed read is the streaming production path (W4-CA-7): paging
    // by the last document of each window, at several limits, must reproduce
    // exactly the canonical-order tenant view that `documents_at` returns —
    // with collections and identifier orders interleaved.
    let (dir, mut storage) = open_storage("window-pages");
    // Canonical (collection, identifier) order for these writes:
    // (alpha, doc-2), (alpha, doc-4), (beta, doc-1), (beta, doc-3),
    // (gamma, doc-5) — identifier order is deliberately not monotonic.
    for (operation, id, collection) in [
        ("op-1", "doc-1", "beta"),
        ("op-2", "doc-2", "alpha"),
        ("op-3", "doc-3", "beta"),
        ("op-4", "doc-4", "alpha"),
        ("op-5", "doc-5", "gamma"),
    ] {
        commit_document(&mut storage, operation, id, collection);
    }
    let tenant = tenant();
    let sequence = StorageSequence::new(5);
    let expected = storage.documents_at(sequence, &tenant);
    let expected_ids: Vec<&str> = expected.iter().map(|document| document.id().as_str()).collect();
    assert_eq!(expected_ids, ["doc-2", "doc-4", "doc-1", "doc-3", "doc-5"]);

    for limit in [1_u32, 2, 3, 5, 9] {
        let mut paged: Vec<Document> = Vec::new();
        let mut after: Option<DocumentId> = None;
        loop {
            let window = storage.documents_at_from(sequence, &tenant, after.as_ref(), limit);
            let Some(last) = window.last() else {
                break;
            };
            let last_id = last.id().clone();
            paged.extend(window);
            after = Some(last_id);
        }
        let paged_ids: Vec<&str> = paged.iter().map(|document| document.id().as_str()).collect();
        assert_eq!(&paged_ids, &expected_ids, "limit {limit} must page the exact view");
    }

    // A zero bound serves an empty window without touching the view.
    assert!(
        storage.documents_at_from(sequence, &tenant, None, 0).is_empty(),
        "a zero limit serves nothing"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn documents_at_from_resumes_strictly_after_the_cursor_identifier() {
    // The resume rule: the window resumes immediately after the FIRST
    // canonical key whose document identifier equals the cursor. With a
    // unique identifier this pages the canonical order exactly; when one
    // identifier is reused across collections, a window that ended past a
    // later occurrence may re-serve keys between the first occurrence and
    // the window end — duplicates only, never losses, which is the
    // streaming contract's at-least-once shape. A foreign tenant is never
    // served by another tenant's window.
    let (dir, mut storage) = open_storage("window-resume");
    for (operation, id, collection) in
        [("op-1", "doc-1", "alpha"), ("op-2", "doc-1", "beta"), ("op-3", "doc-2", "alpha")]
    {
        commit_document(&mut storage, operation, id, collection);
    }
    let tenant = tenant();
    let sequence = StorageSequence::new(3);
    let cursor = DocumentId::try_new("doc-1").expect("valid identifier");
    let window = storage.documents_at_from(sequence, &tenant, Some(&cursor), 10);
    let ids: Vec<&str> = window.iter().map(|document| document.id().as_str()).collect();
    assert_eq!(
        ids,
        ["doc-2", "doc-1"],
        "the window resumes immediately after the first key bearing the cursor identifier"
    );

    // Paging a tenant whose identifier repeats across collections never
    // loses a document: every document appears in at least one window. (The
    // iteration count is bounded because such a paging sequence can cycle —
    // duplicates only — which is the documented id-cursor limit.)
    let mut seen: Vec<(String, String)> = Vec::new();
    let mut after: Option<DocumentId> = None;
    for _ in 0..8 {
        let window = storage.documents_at_from(sequence, &tenant, after.as_ref(), 1);
        let Some(last) = window.last() else {
            break;
        };
        let last_id = last.id().clone();
        seen.extend(window.iter().map(|document| {
            (document.collection().as_str().to_owned(), document.id().as_str().to_owned())
        }));
        after = Some(last_id);
    }
    seen.sort();
    seen.dedup();
    assert_eq!(seen.len(), 3, "every document is served at least once: {seen:?}");

    let other = TenantId::try_new("tenant-b").expect("valid tenant");
    assert!(
        storage.documents_at_from(sequence, &other, None, 10).is_empty(),
        "another tenant's window is empty"
    );
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// W4-CA-4: the durable publication-history journal
// ---------------------------------------------------------------------------

/// Stages one delete of a document the snapshot locates.
fn delete(collection: &str, id: &str, expected: ExpectedVersion) -> StagedWrite {
    StagedWrite::Delete {
        collection: Some(CollectionName::try_new(collection).expect("valid identifier")),
        id: DocumentId::try_new(id).expect("valid identifier"),
        expected,
    }
}

/// Builds one intent for a named tenant.
fn intent_for(
    tenant_name: &str,
    operation: &str,
    writes: Vec<StagedWrite>,
    durability: LocalDurability,
) -> CommitIntent {
    let tenant = TenantId::try_new(tenant_name).expect("valid tenant");
    CommitIntent {
        digest: write_set_digest(&tenant, &writes),
        tenant,
        operation: OperationId::try_new(operation).expect("valid identifier"),
        snapshot_sequence: StorageSequence::new(0),
        durability,
        writes,
    }
}

#[test]
fn tenant_commits_reconstruct_puts_and_deletes_with_versions() {
    // Obligation 1: the ledger binds identities to digests and records no
    // document identities, so the publication history must carry the write
    // set itself. Puts travel as the published document — the version the
    // commit assigned — and deletes travel as identifier plus the version
    // that existed at the delete, including `0` for an absent identifier.
    let (dir, mut storage) = open_storage("history-reconstruct");
    let outcomes = [
        storage.commit(fresh_intent(
            &storage,
            "op-1",
            vec![put(document("doc-1", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Written,
        )),
        storage.commit(fresh_intent(
            &storage,
            "op-2",
            vec![put(document("doc-1", "users", "v2"), ExpectedVersion::Any)],
            LocalDurability::Written,
        )),
        storage.commit(fresh_intent(
            &storage,
            "op-3",
            vec![delete("users", "doc-1", ExpectedVersion::Exact(2))],
            LocalDurability::Written,
        )),
        storage.commit(fresh_intent(
            &storage,
            "op-4",
            vec![
                delete("users", "doc-9", ExpectedVersion::Any),
                put(document("doc-2", "users", "v1"), ExpectedVersion::New),
            ],
            LocalDurability::Synced,
        )),
    ];
    for outcome in outcomes {
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    let tenant = tenant();
    let commits = storage.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(commits.len(), 4, "every published commit is recorded");
    assert_eq!(commits[0].operation_id().as_str(), "op-1");
    assert_eq!(commits[0].sequence().get(), 1);
    assert_eq!(commits[0].tenant(), &tenant);
    match &commits[0].writes()[0] {
        PublishedWrite::Put { document } => {
            assert_eq!(document.id().as_str(), "doc-1");
            assert_eq!(document.metadata().version(), 1, "the put carries the published version");
            assert_eq!(document.value(), &DocumentValue::String("v1".to_owned()));
        }
        other @ PublishedWrite::Delete { .. } => panic!("expected a put, got {other:?}"),
    }
    match &commits[1].writes()[0] {
        PublishedWrite::Put { document } => {
            assert_eq!(document.metadata().version(), 2, "a replacement carries its new version");
        }
        other @ PublishedWrite::Delete { .. } => panic!("expected a put, got {other:?}"),
    }
    match &commits[2].writes()[0] {
        PublishedWrite::Delete { collection, id, version_at_delete } => {
            assert_eq!(
                collection.as_ref().map(|name| name.as_str().to_owned()),
                Some("users".to_owned()),
                "the delete carries its exact key's collection"
            );
            assert_eq!(id.as_str(), "doc-1");
            assert_eq!(*version_at_delete, 2, "the delete carries the version it removed");
        }
        other @ PublishedWrite::Put { .. } => panic!("expected a delete, got {other:?}"),
    }
    assert_eq!(commits[3].writes().len(), 2);
    match &commits[3].writes()[0] {
        PublishedWrite::Delete { collection, id, version_at_delete } => {
            assert_eq!(id.as_str(), "doc-9");
            assert_eq!(
                collection.as_ref().map(|name| name.as_str().to_owned()),
                Some("users".to_owned()),
                "an absent key keeps the requested collection"
            );
            assert_eq!(*version_at_delete, 0, "an absent key records version zero");
        }
        other @ PublishedWrite::Put { .. } => panic!("expected a delete, got {other:?}"),
    }
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn tenant_commits_page_in_global_sequence_order_across_the_limit() {
    // Obligation 2: the operation-ledger index is keyed (tenant, operation)
    // in canonical key order; the journal's own index pages by global
    // storage sequence, so `after` plus `limit` walks publication order.
    let (dir, mut storage) = open_storage("history-paging");
    for index in 0..5 {
        let outcome = storage.commit(intent(
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
            LocalDurability::Written,
        ));
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    let tenant = tenant();
    let sequences = |after: u64, limit: u32| {
        storage
            .tenant_commits(&tenant, StorageSequence::new(after), limit)
            .iter()
            .map(|commit| commit.sequence().get())
            .collect::<Vec<u64>>()
    };
    assert_eq!(sequences(0, 10), [1, 2, 3, 4, 5]);
    assert_eq!(sequences(0, 2), [1, 2]);
    assert_eq!(sequences(2, 2), [3, 4]);
    assert_eq!(sequences(4, 2), [5]);
    assert!(sequences(5, 2).is_empty(), "nothing is served past the published sequence");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn committed_tenants_and_tenant_sequence_map_a_global_cursor_per_tenant() {
    // The seam's tenant mappings: enumeration in canonical order, and the
    // global-cursor entry that names one tenant's own progress.
    let (dir, mut storage) = open_storage("history-tenants");
    let first = intent_for(
        "tenant-a",
        "op-1",
        vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Written,
    );
    let second = intent_for(
        "tenant-b",
        "op-1",
        vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Written,
    );
    let third = intent_for(
        "tenant-a",
        "op-2",
        vec![put(document("doc-2", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Written,
    );
    for intent in [first, second, third] {
        assert!(matches!(storage.commit(intent), CommitResult::Committed(_)));
    }
    let tenant_a = TenantId::try_new("tenant-a").expect("valid tenant");
    let tenant_b = TenantId::try_new("tenant-b").expect("valid tenant");
    let tenant_c = TenantId::try_new("tenant-c").expect("valid tenant");
    assert_eq!(storage.committed_tenants(), vec![tenant_a.clone(), tenant_b.clone()]);
    assert_eq!(
        storage.tenant_sequence(&tenant_a, StorageSequence::new(2)),
        Some(StorageSequence::new(1)),
        "a global cursor below the tenant's second commit maps to the first"
    );
    assert_eq!(
        storage.tenant_sequence(&tenant_a, StorageSequence::new(3)),
        Some(StorageSequence::new(3))
    );
    assert_eq!(
        storage.tenant_sequence(&tenant_b, StorageSequence::new(1)),
        None,
        "tenant-b committed nothing at or below sequence one"
    );
    assert_eq!(storage.tenant_sequence(&tenant_c, StorageSequence::new(9)), None);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn publication_history_survives_reopen_and_serves_the_same_commits() {
    let (dir, mut storage) = open_storage("history-reopen");
    for index in 0..3 {
        let outcome = storage.commit(intent(
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        ));
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    let tenant = tenant();
    let before = storage.tenant_commits(&tenant, StorageSequence::new(0), 10);
    drop(storage);

    let reopened = FileStorage::open(&dir).expect("reopens with its journal");
    let after = reopened.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(before, after, "the journal serves the identical records after a reopen");
    assert_eq!(reopened.committed_tenants().len(), 1);
    assert_eq!(reopened.servable_history_start(), Some(StorageSequence::new(1)));
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_torn_publication_history_tail_bounds_the_servable_window() {
    // Obligation 3: torn-tail truncation bounds the servable window. The
    // scan keeps only the verified prefix, reports the window start, and a
    // consumer detects the truncated end by comparing the last served
    // sequence with the published sequence instead of assuming completeness.
    let (dir, mut storage) = open_storage("history-torn-tail");
    for index in 0..3 {
        let outcome = storage.commit(intent(
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        ));
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    drop(storage);

    let history = dir.join("history");
    // Tear inside the third commit record specifically. Since repair F2 the
    // journal interleaves a durable high-water-mark record after every commit
    // record, so a fixed-length tear from the end would only clip the
    // trailing mark; walking the frames keeps the fixture aimed at the
    // record whose loss must bound the window.
    let bytes = std::fs::read(&history).expect("journal reads");
    let mut offset = 0_usize;
    let mut commits_seen = 0_usize;
    let mut cut = None;
    while offset + 12 <= bytes.len() {
        let length =
            u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("length")) as usize;
        let payload_start = offset + 12;
        let kind = bytes[payload_start];
        if kind == 1 {
            commits_seen += 1;
            if commits_seen == 3 {
                cut = Some((payload_start + 2).min(bytes.len()));
                break;
            }
        }
        let frame_end = payload_start + length;
        if frame_end > bytes.len() {
            break;
        }
        offset = frame_end;
    }
    let cut = cut.expect("the third commit record exists in the journal");
    let mut torn = bytes;
    torn.truncate(cut);
    std::fs::write(&history, torn).expect("journal rewrites");

    let reopened = FileStorage::open(&dir).expect("a torn journal tail still opens");
    let tenant = tenant();
    let served = reopened.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(served.len(), 2, "the verified prefix is served");
    assert_eq!(
        reopened.servable_history_start(),
        Some(StorageSequence::new(1)),
        "the window start is reported, never silently skipped"
    );
    let last_served = served.last().expect("prefix served").sequence().get();
    let (published, _) = reopened.published_state();
    assert!(
        last_served < published.get(),
        "the consumer sees the truncated end: {last_served} served, {} published",
        published.get()
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn an_unservable_publication_history_reports_no_window_instead_of_a_gap() {
    // Obligation 3's fallback: when the oldest records are gone or the whole
    // journal is absent, the seam reports no servable window rather than
    // serving from a fabricated start. Data remains readable through the
    // ordinary snapshot path either way.
    let (dir, mut storage) = open_storage("history-absent");
    let outcome = storage.commit(intent(
        "op-1",
        vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    ));
    assert!(matches!(outcome, CommitResult::Committed(_)));
    drop(storage);

    let history = dir.join("history");
    let mut bytes = std::fs::read(&history).expect("journal reads");
    bytes[13] = bytes[13].wrapping_add(1);
    std::fs::write(&history, bytes).expect("journal rewrites");
    let damaged = FileStorage::open(&dir).expect("opens with an unservable journal");
    let tenant = tenant();
    assert_eq!(
        damaged.servable_history_start(),
        None,
        "a damaged first record makes nothing servable; no silent gap is served"
    );
    assert!(damaged.tenant_commits(&tenant, StorageSequence::new(0), 10).is_empty());
    drop(damaged);

    std::fs::remove_file(&history).expect("journal removes");
    let absent = FileStorage::open(&dir).expect("opens without a journal");
    assert_eq!(absent.servable_history_start(), None);
    assert!(absent.committed_tenants().is_empty());
    let id = DocumentId::try_new("doc-1").expect("valid identifier");
    assert!(
        absent.read_document_at(StorageSequence::new(1), &tenant, &id).is_some(),
        "snapshot reads do not depend on the journal"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn publication_history_lets_a_follower_replay_puts_and_deletes_idempotently() {
    // The reconstructability contract: a follower that applies every served
    // commit through the storage seam, by the source's operation identity,
    // reproduces the source's view — and replaying the same delivery again
    // changes nothing.
    let (dir, mut source) = open_storage("history-replay-source");
    // Each intent takes a fresh snapshot of the moment it commits, so the
    // delete sees the state it conflict-checks against.
    for (operation, writes) in [
        (
            "op-1",
            vec![put(document("doc-1", "users", "v1"), ExpectedVersion::New)] as Vec<StagedWrite>,
        ),
        ("op-2", vec![put(document("doc-2", "users", "v2"), ExpectedVersion::New)]),
        ("op-3", vec![delete("users", "doc-1", ExpectedVersion::Exact(1))]),
    ] {
        let intent = fresh_intent(&source, operation, writes, LocalDurability::Written);
        assert!(matches!(source.commit(intent), CommitResult::Committed(_)), "{operation} commits");
    }
    let tenant = tenant();
    let delivery = source.tenant_commits(&tenant, StorageSequence::new(0), 10);

    let (follower_dir, mut follower) = open_storage("history-replay-follower");
    let replay = |follower: &mut FileStorage, commit: &PublishedCommit| {
        // Repair F3: a delete replays against the exact recorded key — the
        // journal's own collection — with the recorded version as its
        // expected version, so an identifier reused across collections can
        // never make the follower remove a different occurrence than the
        // source removed. A zero version means nothing existed at any key
        // for the identifier, so the replay mirrors the source's no-op.
        let writes: Vec<StagedWrite> = commit
            .writes()
            .iter()
            .map(|write| match write {
                PublishedWrite::Put { document } => {
                    StagedWrite::Put { document: document.clone(), expected: ExpectedVersion::Any }
                }
                PublishedWrite::Delete { collection, id, version_at_delete } => {
                    StagedWrite::Delete {
                        collection: collection.clone(),
                        id: id.clone(),
                        expected: if *version_at_delete == 0 {
                            ExpectedVersion::Any
                        } else {
                            ExpectedVersion::Exact(*version_at_delete)
                        },
                    }
                }
            })
            .collect();
        let snapshot_sequence = follower.published_state().0;
        follower.commit(CommitIntent {
            digest: write_set_digest(commit.tenant(), &writes),
            tenant: commit.tenant().clone(),
            operation: commit.operation_id().clone(),
            snapshot_sequence,
            durability: LocalDurability::Written,
            writes,
        })
    };
    for commit in &delivery {
        assert!(
            matches!(replay(&mut follower, commit), CommitResult::Committed(_)),
            "the follower applies the delivery"
        );
    }
    let source_documents = source.documents_at(source.published_state().0, &tenant);
    let follower_documents = follower.documents_at(follower.published_state().0, &tenant);
    assert_eq!(
        follower_documents, source_documents,
        "replaying the served commits reproduces the source view, deletes included"
    );

    // At-least-once redelivery: the same operation identities replay through
    // the follower's ledger without changing its state.
    for commit in &delivery {
        assert!(
            matches!(replay(&mut follower, commit), CommitResult::Committed(_)),
            "a redelivered commit resolves idempotently"
        );
    }
    assert_eq!(
        follower.documents_at(follower.published_state().0, &tenant),
        source_documents,
        "redelivery changes nothing"
    );
    std::fs::remove_dir_all(&dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
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

// ---------------------------------------------------------------------------
// W4-CA-4 repair loop a2 (quality-gate findings F1..F4)
// ---------------------------------------------------------------------------

#[test]
fn a_torn_journal_tail_is_truncated_so_later_appends_stay_servable() {
    // F1: a torn journal tail must be truncated at open exactly as the WAL
    // is. Without truncation, every post-tear append lands behind the torn
    // bytes where no scan can see it, and the servable window freezes
    // forever. With truncation, commits made after the tear on the reopened
    // store stay servable.
    let (dir, mut storage) = open_storage("history-torn-truncate");
    for index in 0..2 {
        let outcome = storage.commit(fresh_intent(
            &storage,
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        ));
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
    drop(storage);

    // Crash mid-journal-append: the trailing record is incomplete.
    let history = dir.join("history");
    let mut bytes = std::fs::read(&history).expect("journal reads");
    bytes.truncate(bytes.len() - 4);
    std::fs::write(&history, bytes).expect("journal rewrites");

    let tenant = tenant();
    let mut reopened = FileStorage::open(&dir).expect("opens and truncates the torn tail");
    let served = reopened.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(served.len(), 2, "the verified prefix is served");

    // New commits land after the truncation point and must stay servable —
    // this is the assertion the untruncated tail used to fail permanently.
    let outcome = reopened.commit(fresh_intent(
        &reopened,
        "op-after-tear",
        vec![put(document("doc-9", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    ));
    assert!(matches!(outcome, CommitResult::Committed(_)));
    let after_tear = reopened.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(after_tear.len(), 3, "the post-tear commit is servable immediately");
    assert_eq!(after_tear.last().expect("served").operation_id().as_str(), "op-after-tear");
    drop(reopened);

    // And it survives the next reopen: the truncation persisted.
    let reopened_again = FileStorage::open(&dir).expect("reopens");
    let served_again = reopened_again.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(served_again.len(), 3, "the post-tear commit stays servable after a reopen");
    assert_eq!(
        reopened_again.servable_history_start(),
        Some(StorageSequence::new(1)),
        "the window keeps reporting its start"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_failed_journal_append_leaves_the_divergence_observable() {
    // F2: sequences have legitimate holes, so a consumer cannot detect a
    // failed journal append by discontinuity. The durable journal
    // high-water mark is the real signal: after a failed append it stays at
    // the last successfully journaled sequence while the published sequence
    // is ahead, and the divergence survives a reopen.
    let (dir, mut storage) = open_storage("history-high-water");
    let outcome = storage.commit(fresh_intent(
        &storage,
        "op-1",
        vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    ));
    assert!(matches!(outcome, CommitResult::Committed(_)));
    assert_eq!(storage.journaled_high_water(), Some(StorageSequence::new(1)));

    // Inject a transient journal-append failure. The commit itself must
    // still publish: journaling is best-effort and never delays or fails
    // the publication path.
    storage.arm_test_fault(FaultPoint::JournalAppend);
    let outcome = storage.commit(fresh_intent(
        &storage,
        "op-2",
        vec![put(document("doc-2", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    ));
    assert!(matches!(outcome, CommitResult::Committed(_)), "the commit still publishes");
    let (published, _) = storage.published_state();
    assert_eq!(published.get(), 2);
    assert_eq!(
        storage.journaled_high_water(),
        Some(StorageSequence::new(1)),
        "the high-water mark names the last successful append"
    );
    assert!(
        storage.journaled_high_water().is_none_or(|mark| mark < published),
        "the divergence is observable: the journaled mark is behind the published sequence"
    );

    // The mark is durable: the divergence survives a reopen.
    drop(storage);
    let reopened = FileStorage::open(&dir).expect("reopens");
    assert_eq!(
        reopened.journaled_high_water(),
        Some(StorageSequence::new(1)),
        "the durable mark still names the last journaled commit"
    );
    let (republished, _) = reopened.published_state();
    assert_eq!(republished.get(), 2, "the published sequence is still ahead");
    // A later healthy commit advances the mark again past the skipped one.
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn journal_deletes_resolve_the_exact_key_across_duplicate_identifiers() {
    // F3: one identifier reused across collections. The delete names the
    // beta occurrence while the alpha occurrence sorts first canonically and
    // carries a higher version. The journal record must carry the deleted
    // key's own collection and version — not the first canonical
    // occurrence's — and a follower replaying by that exact key removes
    // exactly the occurrence the source removed.
    let (dir, mut source) = open_storage("history-exact-key");
    for (operation, writes) in [
        (
            "op-1",
            vec![put(document("doc-1", "alpha", "v1"), ExpectedVersion::New)] as Vec<StagedWrite>,
        ),
        ("op-2", vec![put(document("doc-1", "alpha", "v2"), ExpectedVersion::Any)]),
        ("op-3", vec![put(document("doc-1", "beta", "v1"), ExpectedVersion::New)]),
        ("op-4", vec![delete("beta", "doc-1", ExpectedVersion::Exact(1))]),
    ] {
        let intent = fresh_intent(&source, operation, writes, LocalDurability::Written);
        assert!(matches!(source.commit(intent), CommitResult::Committed(_)), "{operation} commits");
    }
    let tenant = tenant();
    let delivery = source.tenant_commits(&tenant, StorageSequence::new(0), 10);
    let delete = match delivery.last().expect("four commits served").writes() {
        [PublishedWrite::Delete { collection, id, version_at_delete }] => {
            (collection.clone(), id.clone(), *version_at_delete)
        }
        other => panic!("expected one delete, got {other:?}"),
    };
    // The deleted key is (beta, doc-1) at version one; the alpha occurrence
    // is first canonically and at version two, and neither fact may leak
    // into the record.
    assert_eq!(
        delete,
        (
            Some(CollectionName::try_new("beta").expect("valid identifier")),
            DocumentId::try_new("doc-1").expect("valid identifier"),
            1
        ),
        "the delete carries its exact key's collection and version"
    );

    // Exact-key replay: the follower applies the delete against the recorded
    // key with the recorded version, so only the beta occurrence disappears.
    let (follower_dir, mut follower) = open_storage("history-exact-key-follower");
    for commit in &delivery {
        let writes: Vec<StagedWrite> = commit
            .writes()
            .iter()
            .map(|write| match write {
                PublishedWrite::Put { document } => {
                    StagedWrite::Put { document: document.clone(), expected: ExpectedVersion::Any }
                }
                PublishedWrite::Delete { collection, id, version_at_delete } => {
                    StagedWrite::Delete {
                        collection: collection.clone(),
                        id: id.clone(),
                        // A recorded version means the key existed at it; a
                        // zero version means nothing existed to remove.
                        expected: if *version_at_delete == 0 {
                            ExpectedVersion::Any
                        } else {
                            ExpectedVersion::Exact(*version_at_delete)
                        },
                    }
                }
            })
            .collect();
        let snapshot_sequence = follower.published_state().0;
        let outcome = follower.commit(CommitIntent {
            digest: write_set_digest(commit.tenant(), &writes),
            tenant: commit.tenant().clone(),
            operation: commit.operation_id().clone(),
            snapshot_sequence,
            durability: LocalDurability::Written,
            writes,
        });
        assert!(matches!(outcome, CommitResult::Committed(_)), "the follower applies the commit");
    }
    let follower_documents = follower.documents_at(follower.published_state().0, &tenant);
    let source_documents = source.documents_at(source.published_state().0, &tenant);
    assert_eq!(follower_documents, source_documents, "the views match");
    let alpha = CollectionName::try_new("alpha").expect("valid identifier");
    let beta = CollectionName::try_new("beta").expect("valid identifier");
    let id = DocumentId::try_new("doc-1").expect("valid identifier");
    let key_of = |documents: &[Document]| {
        documents
            .iter()
            .map(|document| (document.collection().clone(), document.id().clone()))
            .collect::<Vec<_>>()
    };
    let keys = key_of(&follower_documents);
    assert_eq!(keys, vec![(alpha, id)], "exactly the alpha occurrence survives");
    assert!(
        follower_documents.iter().all(|document| document.collection() != &beta),
        "the beta occurrence is gone and only it"
    );
    assert_eq!(
        follower_documents[0].metadata().version(),
        2,
        "the surviving alpha occurrence keeps its own version"
    );
    std::fs::remove_dir_all(&dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn journal_records_the_achieved_local_durability_of_each_commit() {
    // F4: each journal entry is annotated with its commit's achieved local
    // durability so a consumer can reconcile the phantom-history disclosure
    // (a Buffered or Written entry may describe data the source lost after
    // a restart); W4-REPLICATION owns the filtering policy.
    let (dir, mut storage) = open_storage("history-durability");
    let buffered = storage.commit(fresh_intent(
        &storage,
        "op-buffered",
        vec![put(document("doc-1", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Buffered,
    ));
    assert!(matches!(buffered, CommitResult::Committed(_)));
    let synced = storage.commit(fresh_intent(
        &storage,
        "op-synced",
        vec![put(document("doc-2", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    ));
    assert!(matches!(synced, CommitResult::Committed(_)));
    let written = storage.commit(fresh_intent(
        &storage,
        "op-written",
        vec![put(document("doc-3", "users", "value"), ExpectedVersion::New)],
        LocalDurability::Written,
    ));
    assert!(matches!(written, CommitResult::Committed(_)));

    let tenant = tenant();
    let commits = storage.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(commits.len(), 3);
    assert_eq!(commits[0].durability(), LocalDurability::Buffered);
    assert_eq!(commits[1].durability(), LocalDurability::Synced);
    assert_eq!(commits[2].durability(), LocalDurability::Written);

    // The annotation is durable: it survives a reopen.
    drop(storage);
    let reopened = FileStorage::open(&dir).expect("reopens");
    let reopened_commits = reopened.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert_eq!(
        reopened_commits.iter().map(PublishedCommit::durability).collect::<Vec<_>>(),
        vec![LocalDurability::Buffered, LocalDurability::Synced, LocalDurability::Written],
        "the durability annotation survives a reopen"
    );
    std::fs::remove_dir_all(&dir).ok();
}
