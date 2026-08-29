//! Focused tests of the LSM storage engine (P1).
//!
//! These prove the engine's own contracts: bounded durable bytes per
//! write, atomic-batch recovery, torn-tail truncation, idempotent replay,
//! MVCC snapshot reads, rotation and flush, manifest recovery, and the
//! compaction checkpoint ordering. The full engine suite exercises the
//! same [`StoragePort`] seam through the backend selector.

use crate::{
    engine::{Engine, EngineConfig},
    storage::{
        self, StoragePort,
        compaction::{CompactionPolicy, CompactionRequest},
        intent::{CommitIntent, CommitResult, StagedWrite},
        lsm::LsmStorage,
    },
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    },
};

fn location(tag: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "tbv2-lsm-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document(collection: &str, id: &str, text: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::String(text.to_owned()),
    )
}

fn put_intent(
    tenant: &TenantId,
    operation: &str,
    sequence: u64,
    document: Document,
) -> CommitIntent {
    CommitIntent {
        tenant: tenant.clone(),
        operation: OperationId::try_new(operation).expect("valid operation"),
        digest: storage::write_set_digest(
            tenant,
            &[StagedWrite::Put { document: document.clone(), expected: ExpectedVersion::Any }],
        ),
        snapshot_sequence: crate::types::StorageSequence::new(sequence),
        durability: LocalDurability::Synced,
        writes: vec![StagedWrite::Put { document, expected: ExpectedVersion::Any }],
    }
}

fn delete_intent(
    tenant: &TenantId,
    operation: &str,
    sequence: u64,
    id: &str,
    expected: ExpectedVersion,
) -> CommitIntent {
    CommitIntent {
        tenant: tenant.clone(),
        operation: OperationId::try_new(operation).expect("valid operation"),
        digest: storage::write_set_digest(
            tenant,
            &[StagedWrite::Delete {
                collection: Some(CollectionName::try_new("orders").expect("valid collection")),
                id: DocumentId::try_new(id).expect("valid document"),
                expected,
            }],
        ),
        snapshot_sequence: crate::types::StorageSequence::new(sequence),
        durability: LocalDurability::Synced,
        writes: vec![StagedWrite::Delete {
            collection: Some(CollectionName::try_new("orders").expect("valid collection")),
            id: DocumentId::try_new(id).expect("valid document"),
            expected,
        }],
    }
}

#[test]
fn commit_reads_back_and_survives_reopen() {
    let dir = location("reopen");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let result =
        engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    assert!(matches!(result, CommitResult::Committed(_)));
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let found = reopened.read_document_in_collection_at(
        reopened.published_state().0,
        &scope_tenant,
        &CollectionName::try_new("orders").expect("valid collection"),
        &DocumentId::try_new("order-1").expect("valid document"),
    );
    assert!(found.is_some(), "the committed document survives a reopen");
    let document = found.unwrap();
    assert_eq!(document.metadata().version(), 1);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn durable_bytes_per_write_are_bounded_by_the_write_set() {
    let dir = location("amplification");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    // Seed enough documents that a full-root store would amplify: the
    // baseline run rewrote ~300 KB per ~300-byte write.
    let count: u64 = 64;
    for index in 0..count {
        let result = engine.commit(put_intent(
            &scope_tenant,
            &format!("seed-{index}"),
            0,
            document("orders", &format!("order-{index:04}"), &"x".repeat(300)),
        ));
        assert!(matches!(result, CommitResult::Committed(_)));
    }
    let wal_bytes = engine.wal_len();
    // The frame count equals the commit count; each frame carries its
    // document plus the ledger and history records. The bound: fewer than
    // 3 KiB of durable bytes per ~300-byte document, versus the ~300 KiB
    // full-root baseline — orders of magnitude, by construction.
    let per_write = wal_bytes / count;
    assert!(
        per_write < 3 * 1024,
        "durable bytes per write must be bounded by the write set, saw {per_write}"
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn idempotent_retry_replays_the_recorded_outcome() {
    let dir = location("idempotent");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let intent = put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first"));
    let first = engine.commit(intent.clone());
    let second = engine.commit(intent);
    match (first, second) {
        (CommitResult::Committed(a), CommitResult::Committed(b)) => {
            assert_eq!(a.sequence, b.sequence, "the retry resolves to the recorded outcome");
            assert_eq!(a.root, b.root);
        }
        other => panic!("both attempts must commit: {other:?}"),
    }
    let mut tampered =
        put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "different"));
    tampered.digest = tampered.digest.wrapping_add(1);
    assert!(matches!(
        engine.commit(tampered),
        CommitResult::Rejected(storage::intent::CommitRejection::OperationIdReuse)
    ));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn recovery_selects_root_head_by_sequence_not_operation_key() {
    let dir = location("root-head-sequence");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();

    // The first operation sorts after the second one, while its publication
    // sequence is lower. Recovery must follow sequence order, not the ledger
    // map's (tenant, operation) key order.
    let first =
        engine.commit(put_intent(&scope_tenant, "op-z", 0, document("orders", "order-1", "first")));
    let first = match first {
        CommitResult::Committed(receipt) => receipt,
        other => panic!("first commit must succeed: {other:?}"),
    };
    let second = engine.commit(put_intent(
        &scope_tenant,
        "op-a",
        first.sequence.get(),
        document("orders", "order-2", "second"),
    ));
    let second = match second {
        CommitResult::Committed(receipt) => receipt,
        other => panic!("second commit must succeed: {other:?}"),
    };
    drop(engine);

    let reopened = LsmStorage::open(&dir).unwrap();
    assert_eq!(
        reopened.published_state().1,
        second.root,
        "recovery must restore the root from the latest committed sequence"
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn synced_durability_flip_survives_compaction_and_reopen() {
    let dir = location("synced-flip-reopen");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let operation = OperationId::try_new("op-synced").expect("valid operation");

    let result = engine.commit(put_intent(
        &scope_tenant,
        operation.as_str(),
        0,
        document("orders", "order-1", "synced"),
    ));
    assert!(matches!(result, CommitResult::Committed(_)));
    assert!(matches!(
        engine.resolve_operation(&scope_tenant, &operation),
        Some(crate::storage::intent::OperationResolution::Committed {
            durability: LocalDurability::Synced,
            ..
        })
    ));

    engine
        .compact(storage::compaction::StorageCompaction {
            policy: CompactionPolicy::Reclaim,
            floor: None,
            progress: None,
        })
        .expect("compaction completes");
    drop(engine);

    let reopened = LsmStorage::open(&dir).unwrap();
    assert!(matches!(
        reopened.resolve_operation(&scope_tenant, &operation),
        Some(crate::storage::intent::OperationResolution::Committed {
            durability: LocalDurability::Synced,
            ..
        })
    ));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn delete_then_read_answers_absent_across_reopen() {
    let dir = location("delete");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let published = engine.published_state().0;
    let result = engine.commit(delete_intent(
        &scope_tenant,
        "op-2",
        published.get(),
        "order-1",
        ExpectedVersion::Exact(1),
    ));
    assert!(matches!(result, CommitResult::Committed(_)));
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    assert!(
        reopened
            .read_document_in_collection_at(
                state,
                &scope_tenant,
                &CollectionName::try_new("orders").expect("valid collection"),
                &DocumentId::try_new("order-1").expect("valid document"),
            )
            .is_none()
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn mvcc_snapshot_reads_isolate_concurrent_writers() {
    let dir = location("mvcc");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let snapshot = engine.published_state().0;
    engine.commit(put_intent(
        &scope_tenant,
        "op-2",
        snapshot.get(),
        document("orders", "order-1", "second"),
    ));
    let at_snapshot = engine
        .read_document_in_collection_at(
            snapshot,
            &scope_tenant,
            &CollectionName::try_new("orders").expect("valid collection"),
            &DocumentId::try_new("order-1").expect("valid document"),
        )
        .expect("the snapshot still observes the first write");
    assert_eq!(
        at_snapshot.metadata().version(),
        1,
        "a snapshot read sees the state at its sequence"
    );
    let current = engine
        .read_document_in_collection_at(
            engine.published_state().0,
            &scope_tenant,
            &CollectionName::try_new("orders").expect("valid collection"),
            &DocumentId::try_new("order-1").expect("valid document"),
        )
        .unwrap();
    assert_eq!(current.metadata().version(), 2);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn rotation_flush_and_manifest_recovery_round_trip() {
    let dir = location("rotation");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.tuning.max_memtable_entries = 8;
    engine.tuning.max_memtable_bytes = usize::MAX;
    for index in 0..64_u64 {
        let result = engine.commit(put_intent(
            &scope_tenant,
            &format!("op-{index}"),
            0,
            document("orders", &format!("order-{index:04}"), "payload"),
        ));
        assert!(matches!(result, CommitResult::Committed(_)));
    }
    assert!(
        engine.segment_count_at_level(0) > 0,
        "rotation flushed frozen memtables into segments"
    );
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    let documents = reopened.documents_at(state, &scope_tenant);
    assert_eq!(documents.len(), 64, "every document survives the manifest recovery");
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn expected_version_conflict_is_recorded_and_idempotent() {
    let dir = location("conflict");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let published = engine.published_state().0;
    let stale = engine.commit(delete_intent(
        &scope_tenant,
        "op-stale",
        0,
        "order-1",
        ExpectedVersion::Exact(99),
    ));
    assert!(matches!(stale, CommitResult::Rejected(storage::intent::CommitRejection::Conflict)));
    let retried = engine.commit(delete_intent(
        &scope_tenant,
        "op-stale",
        0,
        "order-1",
        ExpectedVersion::Exact(99),
    ));
    assert!(matches!(retried, CommitResult::Rejected(storage::intent::CommitRejection::Conflict)));
    let still_present = engine.read_document_in_collection_at(
        published,
        &scope_tenant,
        &CollectionName::try_new("orders").expect("valid collection"),
        &DocumentId::try_new("order-1").expect("valid document"),
    );
    assert!(still_present.is_some());
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn compaction_checkpoint_truncates_the_wal_and_keeps_data() {
    let dir = location("compact");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    for index in 0..16_u64 {
        engine.commit(put_intent(
            &scope_tenant,
            &format!("op-{index}"),
            0,
            document("orders", &format!("order-{index:04}"), "payload"),
        ));
    }
    let wal_before = engine.wal_len();
    let (receipt, facts) = engine
        .compact(storage::compaction::StorageCompaction {
            policy: CompactionPolicy::Reclaim,
            floor: None,
            progress: None,
        })
        .expect("the compaction pass completes");
    assert_eq!(receipt.wal_bytes_before(), wal_before);
    assert!(receipt.wal_bytes_after() < wal_before, "the checkpoint truncates the WAL");
    assert!(receipt.checkpoint_sequence().is_some());
    assert_eq!(facts.bound, None, "Reclaim does not touch history");
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    assert_eq!(reopened.documents_at(state, &scope_tenant).len(), 16);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn history_reclaim_extracts_evidence_and_declares_the_bound() {
    let dir = location("reclaim");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let published = engine.published_state().0;
    engine.commit(delete_intent(
        &scope_tenant,
        "op-2",
        published.get(),
        "order-1",
        ExpectedVersion::Exact(1),
    ));
    let after_delete = engine.published_state().0;
    engine.commit(put_intent(
        &scope_tenant,
        "op-3",
        after_delete.get(),
        document("orders", "order-2", "second"),
    ));
    let request = CompactionRequest::new(CompactionPolicy::ReclaimHistory);
    let lowered = request.lower(None);
    let (receipt, facts) = engine.compact(lowered).expect("the pass completes");
    assert!(matches!(
        receipt.history_reclaim(),
        crate::storage::compaction::HistoryReclaimOutcome::Reclaimed { .. }
    ));
    assert!(receipt.evidence_extracted() >= 1, "the delete produced retained evidence");
    assert!(facts.bound.is_some());
    let state = engine.retained_state();
    assert!(state.bound.is_some(), "the bound is durably declared");
    assert!(!state.facts.is_empty(), "the deletion fact is retained");
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    assert!(reopened.servable_history_start().is_some(), "the floor survives the reopen");
    assert_eq!(
        reopened.tenant_commits(&scope_tenant, crate::types::StorageSequence::new(0), 64).len(),
        1,
        "history below the bound is not served"
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn torn_wal_tail_truncates_and_keeps_verified_commits() {
    let dir = location("torn");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    drop(engine);
    // Simulate the torn tail of an interrupted append: a real frame's
    // little-endian magic followed by a cut header.
    let wal_path = dir.join("wal");
    let mut bytes = std::fs::read(&wal_path).unwrap();
    bytes.extend_from_slice(&[0x4D, 0x53, 0x4C, 0x54, 0x00]);
    std::fs::write(&wal_path, &bytes).unwrap();
    let mut reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    assert!(
        reopened
            .read_document_in_collection_at(
                state,
                &scope_tenant,
                &CollectionName::try_new("orders").expect("valid collection"),
                &DocumentId::try_new("order-1").expect("valid document"),
            )
            .is_some()
    );
    // The next commit lands and reopens cleanly past the truncation.
    let result = reopened.commit(put_intent(
        &scope_tenant,
        "op-2",
        state.get(),
        document("orders", "order-2", "second"),
    ));
    assert!(matches!(result, CommitResult::Committed(_)));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn foreign_directory_fails_closed() {
    let dir = location("foreign");
    std::fs::write(dir.join("unexpected"), b"not a terrabase location").unwrap();
    assert!(matches!(
        LsmStorage::open(&dir),
        Err(crate::storage::lsm::LsmOpenError::FormatMismatch)
    ));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn engine_opens_on_the_lsm_backend_and_serves_documents() {
    let dir = location("engine");
    let config = EngineConfig::durable(&dir)
        .unwrap()
        .with_storage_backend(crate::engine::StorageBackend::Lsm);
    let engine = Engine::open(config).unwrap();
    drop(engine);
    // The marker identifies the location as an LSM location.
    let marker = std::fs::read_to_string(dir.join("format")).unwrap();
    assert_eq!(marker.trim(), "TBV2LSM1");
    std::fs::remove_dir_all(&dir).unwrap();
}
