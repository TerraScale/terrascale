//! In-crate tests for the `SQLite` storage backend (D-27, TB-20).
//!
//! The engine-level suite drives the real `Engine` API so the port contract
//! is proven behind the same seam production uses. The storage-level suite
//! differentials the backend against the `FileStorage` reference (and the
//! usage surface against the LSM reference), pins the journal-pruning
//! floor's semantics, and exercises the crash windows against the database
//! file itself.

use std::{
    path::{Path, PathBuf},
    sync::{
        Arc, Barrier,
        atomic::{AtomicU64, Ordering},
    },
};

use rusqlite::Connection;

use crate::RegionCode;
use crate::engine::{
    CommitError, CommitOutcome, DurableResolution, Engine, EngineConfig, StorageBackend,
};
use crate::storage::intent::{CommitIntent, CommitResult, StagedWrite};
use crate::storage::lsm::LsmStorage;
use crate::storage::{CompactionPhase, CompactionPolicy, CompactionRequest, FaultPoint};
use crate::storage::{FileStorage, StoragePort, write_set_digest};
use crate::storage::{StorageOpenError, UsageFactPosition};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
};
use crate::usage::{
    DurableUsageFact, MeasurementWindow, MeterCatalog, MeterKind, MintingIdentity, UsageFact,
    UsageFactId,
};

use super::SqliteStorage;

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-sqlite-{tag}-{}-{index}", std::process::id()))
}

/// Opens a location on the `SQLite` backend explicitly.
fn open_sqlite_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine = Engine::open(
        EngineConfig::durable(&dir)
            .expect("durable location")
            .with_storage_backend(StorageBackend::Sqlite),
    )
    .expect("sqlite engine opens");
    (dir, engine)
}

/// Reopens a `SQLite` location through the explicit selector.
fn reopen_sqlite_engine(dir: &PathBuf) -> Engine {
    Engine::open(
        EngineConfig::durable(dir)
            .expect("durable location")
            .with_storage_backend(StorageBackend::Sqlite),
    )
    .expect("sqlite engine reopens")
}

fn tenant_scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(
        TenantId::try_new(tenant_name).expect("valid tenant"),
        engine.identity(),
    )
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid operation")
}

fn document(id: &str, collection: &str, text: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid collection"),
        DocumentValue::String(text.to_owned()),
    )
}

#[allow(clippy::too_many_arguments, reason = "the engine seam's own commit inputs")]
fn commit_put(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    collection: &str,
    text: &str,
    expected: ExpectedVersion,
    durability: LocalDurability,
) -> CommitOutcome {
    let scope = tenant_scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction =
        engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
    transaction.put(document(id, collection, text), expected).expect("stages");
    transaction.commit(durability)
}

fn commit_delete(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    expected: ExpectedVersion,
    durability: LocalDurability,
) -> CommitOutcome {
    let scope = tenant_scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction =
        engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
    transaction
        .delete(DocumentId::try_new(id).expect("valid identifier"), expected)
        .expect("stages");
    transaction.commit(durability)
}

fn read_document(engine: &Engine, tenant_name: &str, id: &str) -> Option<Document> {
    let scope = tenant_scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let transaction = engine.begin(&scope, snapshot, operation("read-probe")).expect("begins");
    transaction.read(&DocumentId::try_new(id).expect("valid identifier")).expect("reads")
}

fn committed_sequence(outcome: CommitOutcome) -> u64 {
    match outcome {
        CommitOutcome::Committed(receipt) => receipt.sequence().get(),
        other => panic!("expected a committed receipt, got {other:?}"),
    }
}

fn rejected_error(outcome: CommitOutcome) -> CommitError {
    match outcome {
        CommitOutcome::Rejected(error) => error,
        other => panic!("expected a rejected outcome, got {other:?}"),
    }
}
#[test]
fn engine_round_trips_and_persists_across_reopen() {
    let (dir, engine) = open_sqlite_engine("round-trip");
    let scope = tenant_scope(&engine, "tenant-a");

    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-put-1",
        "doc-1",
        "users",
        "value-1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-put-2",
        "doc-2",
        "users",
        "value-2",
        ExpectedVersion::New,
        LocalDurability::Written,
    ));
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-put-3",
        "doc-1",
        "orders",
        "reused-id",
        ExpectedVersion::New,
        LocalDurability::Written,
    ));
    // A collection-aware read resolves the reused identifier in its own
    // collection; the absent identifier contributes nothing.
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let by_ids = engine
        .snapshot_documents_by_ids(
            &scope,
            &snapshot,
            &CollectionName::try_new("orders").expect("collection"),
            &[
                DocumentId::try_new("doc-1").expect("identifier"),
                DocumentId::try_new("doc-9").expect("identifier"),
            ],
        )
        .expect("keyed read");
    assert_eq!(by_ids.len(), 1, "the absent identifier contributes nothing");
    assert_eq!(by_ids[0].value(), &DocumentValue::String("reused-id".to_owned()));

    committed_sequence(commit_delete(
        &engine,
        "tenant-a",
        "op-delete",
        "doc-2",
        ExpectedVersion::Exact(1),
        LocalDurability::Synced,
    ));
    assert!(read_document(&engine, "tenant-a", "doc-2").is_none(), "deleted document is absent");
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("survivor").value(),
        &DocumentValue::String("reused-id".to_owned()),
        "the orders copy wins canonical identifier order: orders sorts first"
    );

    // The scan runs at the pre-delete snapshot above, so it still observes
    // both users documents, in canonical identifier order.
    let collection = CollectionName::try_new("users").expect("collection");
    let scan =
        engine.snapshot_documents_in_collection(&scope, &snapshot, &collection).expect("scan");
    assert_eq!(scan.len(), 2, "the as-of scan observes the pre-delete state");
    let page = engine
        .snapshot_documents_in_collection_from(&scope, &snapshot, &collection, None, 1)
        .expect("page");
    assert_eq!(page.len(), 1, "the page honors its limit");
    assert_eq!(page[0].id().as_str(), "doc-1", "the page starts in canonical order");
    let next_page = engine
        .snapshot_documents_in_collection_from(
            &scope,
            &snapshot,
            &collection,
            Some(DocumentId::try_new("doc-1").expect("identifier")),
            10,
        )
        .expect("next page");
    assert_eq!(next_page.len(), 1, "the cursor page resumes after doc-1");
    assert_eq!(next_page[0].id().as_str(), "doc-2");

    drop(engine);
    let reopened = reopen_sqlite_engine(&dir);
    assert_eq!(
        read_document(&reopened, "tenant-a", "doc-1").expect("persisted").value(),
        &DocumentValue::String("reused-id".to_owned()),
        "committed documents survive a reopen"
    );
    assert!(read_document(&reopened, "tenant-a", "doc-2").is_none(), "deletes survive a reopen");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn faulted_batch_leaves_zero_divergence() {
    let (dir, engine) = open_sqlite_engine("crash-atomicity");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-seed",
        "doc-1",
        "users",
        "seeded",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    // The fault aborts the batch after its rows are staged but before the
    // transaction commits; the rollback is the in-process stand-in for a
    // crash between any two durable steps.
    engine.arm_storage_fault(FaultPoint::WalAppend);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-lost",
        "doc-2",
        "users",
        "lost",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(rejected_error(outcome), CommitError::StorageFailure);
    drop(engine);

    let reopened = reopen_sqlite_engine(&dir);
    assert!(
        read_document(&reopened, "tenant-a", "doc-2").is_none(),
        "the faulted batch's documents are absent"
    );
    assert_eq!(
        read_document(&reopened, "tenant-a", "doc-1").expect("seed survived").value(),
        &DocumentValue::String("seeded".to_owned()),
        "the earlier synced commit survived"
    );
    let storage = reopened.storage.write().expect("storage lock");
    assert_eq!(storage.port.journal_divergence(), 0, "documents and journal agree");
    assert_eq!(
        storage.port.journaled_high_water().map(StorageSequence::get),
        Some(1),
        "the faulted commit left no journal record"
    );
    assert_eq!(storage.port.published_state().0.get(), 1, "the sequence watermark did not move");
    drop(storage);
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn idempotent_replay_and_digest_conflict() {
    let (dir, engine) = open_sqlite_engine("idempotency");
    let first = commit_put(
        &engine,
        "tenant-a",
        "op-retry",
        "doc-1",
        "users",
        "same-write-set",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let first_sequence = committed_sequence(first);
    // A retry of the same operation identity with the same write set
    // replays the original receipt instead of publishing again.
    let retry = commit_put(
        &engine,
        "tenant-a",
        "op-retry",
        "doc-1",
        "users",
        "same-write-set",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(
        committed_sequence(retry),
        first_sequence,
        "the replay returns the original sequence"
    );
    let scope = tenant_scope(&engine, "tenant-a");
    match engine.resolve_commit(&scope, operation("op-retry")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.sequence().get(), first_sequence);
            assert_eq!(receipt.durability(), LocalDurability::Synced);
        }
        other => panic!("expected a committed resolution, got {other:?}"),
    }
    // The same identity bound to a different write set is a durable
    // conflict, never a second publication.
    let conflicting = commit_put(
        &engine,
        "tenant-a",
        "op-retry",
        "doc-1",
        "users",
        "different-write-set",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(rejected_error(conflicting), CommitError::OperationIdReuse);
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("readable").value(),
        &DocumentValue::String("same-write-set".to_owned()),
        "the conflicting write never landed"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn snapshots_isolate_later_writes() {
    let (dir, engine) = open_sqlite_engine("mvcc");
    let scope = tenant_scope(&engine, "tenant-a");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-v1",
        "doc-1",
        "users",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let old = engine.read_snapshot(&scope).expect("snapshot");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-v2",
        "doc-1",
        "users",
        "v2",
        ExpectedVersion::Exact(1),
        LocalDurability::Synced,
    ));
    // A transaction pinned to the older snapshot reads the older version;
    // the boundary below the new head stays exactly reconstructable.
    let pinned = engine.begin(&scope, old.clone(), operation("pinned-read")).expect("begins");
    assert_eq!(
        pinned
            .read(&DocumentId::try_new("doc-1").expect("identifier"))
            .expect("reads")
            .expect("present at the old boundary")
            .value(),
        &DocumentValue::String("v1".to_owned()),
        "a read at the old sequence never sees the newer write"
    );
    drop(pinned);
    let documents = engine
        .snapshot_documents_in_collection(
            &scope,
            &old,
            &CollectionName::try_new("users").expect("collection"),
        )
        .expect("scan");
    assert_eq!(
        documents[0].value(),
        &DocumentValue::String("v1".to_owned()),
        "the as-of scan agrees with the as-of point read"
    );
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("current read").value(),
        &DocumentValue::String("v2".to_owned())
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn concurrent_writers_produce_one_continuous_journal() {
    const WRITERS: usize = 8;
    let (dir, engine) = open_sqlite_engine("group-commit");
    let engine = Arc::new(engine);
    let barrier = Arc::new(Barrier::new(WRITERS));
    let mut handles = Vec::with_capacity(WRITERS);
    for writer in 0..WRITERS {
        let engine = Arc::clone(&engine);
        let barrier = Arc::clone(&barrier);
        handles.push(std::thread::spawn(move || {
            barrier.wait();
            committed_sequence(commit_put(
                &engine,
                "tenant-a",
                &format!("op-{writer}"),
                &format!("doc-{writer}"),
                "users",
                "grouped",
                ExpectedVersion::New,
                LocalDurability::Written,
            ))
        }));
    }
    let mut sequences: Vec<u64> =
        handles.into_iter().map(|handle| handle.join().expect("writer finishes")).collect();
    sequences.sort_unstable();
    sequences.dedup();
    assert_eq!(sequences.len(), WRITERS, "every writer committed a distinct sequence");
    assert_eq!(sequences[0], 1, "sequences are continuous from one");
    assert_eq!(
        sequences.last().copied(),
        Some(WRITERS as u64),
        "the group produced one continuous sequence run"
    );
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let documents = engine
        .snapshot_documents_in_collection(
            &scope,
            &snapshot,
            &CollectionName::try_new("users").expect("collection"),
        )
        .expect("scan");
    assert_eq!(documents.len(), WRITERS, "every grouped write is visible after");
    let storage = engine.storage.write().expect("storage lock");
    assert_eq!(storage.port.journal_divergence(), 0);
    assert_eq!(
        storage
            .port
            .tenant_commits(
                &TenantId::try_new("tenant-a").expect("tenant"),
                StorageSequence::new(0),
                WRITERS as u32
            )
            .len(),
        WRITERS,
        "the journal serves every published commit"
    );
    drop(storage);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn sqlite_marker_is_sniffed_without_the_selector() {
    let (dir, engine) = open_sqlite_engine("marker-sniff");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "users",
        "marked",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert!(
        std::fs::read(dir.join("format")).expect("format marker exists").starts_with(b"TBV3SQLITE")
    );
    drop(engine);
    // No crate-private selector and no environment override: the location's
    // own marker must select the backend that created it. An LSM open of
    // this marker would refuse the location, so reading the document back
    // proves the sniff chose `SQLite`.
    let reopened = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("marker selects the sqlite backend");
    assert_eq!(
        read_document(&reopened, "tenant-a", "doc-1").expect("persisted").value(),
        &DocumentValue::String("marked".to_owned())
    );
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn compact_checkpoints_the_wal() {
    let (dir, engine) = open_sqlite_engine("compact");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "users",
        "checkpointed",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let receipt =
        engine.compact(CompactionRequest::new(CompactionPolicy::Reclaim)).expect("compaction pass");
    assert!(
        receipt.phases().contains(&CompactionPhase::Checkpoint),
        "the pass reports its checkpoint phase"
    );
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("readable").value(),
        &DocumentValue::String("checkpointed".to_owned()),
        "a checkpointed location still reads"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---- Storage-level helpers ----------------------------------------------

fn tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

fn put(document: Document, expected: ExpectedVersion) -> StagedWrite {
    StagedWrite::Put { document, expected }
}

fn delete(collection: Option<&str>, id: &str, expected: ExpectedVersion) -> StagedWrite {
    StagedWrite::Delete {
        collection: collection.map(|name| CollectionName::try_new(name).expect("collection")),
        id: DocumentId::try_new(id).expect("identifier"),
        expected,
    }
}

fn intent(operation: &str, writes: Vec<StagedWrite>, durability: LocalDurability) -> CommitIntent {
    let tenant = tenant();
    CommitIntent {
        digest: write_set_digest(&tenant, &writes),
        tenant,
        operation: OperationId::try_new(operation).expect("valid identifier"),
        snapshot_sequence: StorageSequence::new(0),
        durability,
        usage_admission: None,
        writes,
    }
}

fn fresh_intent(
    storage: &mut dyn StoragePort,
    operation: &str,
    writes: Vec<StagedWrite>,
    durability: LocalDurability,
) -> CommitIntent {
    let mut intent = intent(operation, writes, durability);
    intent.snapshot_sequence = storage.published_state().0;
    intent
}

fn commit(
    storage: &mut dyn StoragePort,
    operation: &str,
    writes: Vec<StagedWrite>,
) -> CommitResult {
    let intent = fresh_intent(storage, operation, writes, LocalDurability::Written);
    storage.commit(intent)
}

fn stored_sequence(result: &CommitResult) -> u64 {
    match result {
        CommitResult::Committed(receipt) => receipt.sequence.get(),
        other => panic!("expected a commit, got {other:?}"),
    }
}

/// Runs one scenario against the `FileStorage` reference and the `SQLite`
/// backend and asserts the observable results are identical.
fn differential<T: PartialEq + std::fmt::Debug>(
    tag: &str,
    scenario: impl Fn(&mut dyn StoragePort) -> T,
) {
    let file_dir = location(&format!("{tag}-file"));
    let sqlite_dir = location(&format!("{tag}-sqlite"));
    let mut file = FileStorage::open(&file_dir).expect("file reference opens");
    let mut sqlite = SqliteStorage::open(&sqlite_dir).expect("sqlite backend opens");
    let file_result = scenario(&mut file);
    let sqlite_result = scenario(&mut sqlite);
    assert_eq!(file_result, sqlite_result, "differential mismatch in {tag}");
    drop(file);
    drop(sqlite);
    std::fs::remove_dir_all(&file_dir).ok();
    std::fs::remove_dir_all(&sqlite_dir).ok();
}

// ---- Differential against the FileStorage reference ----------------------

#[test]
fn differential_write_read_delete_and_current_state() {
    differential("write-read-delete", |storage| {
        let first = commit(
            storage,
            "op-1",
            vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)],
        );
        let second = commit(
            storage,
            "op-2",
            vec![put(document("doc-2", "users", "two"), ExpectedVersion::New)],
        );
        let at = storage.published_state().0;
        let ids = storage
            .documents_at(at, &tenant())
            .into_iter()
            .map(|document| document.id().as_str().to_owned())
            .collect::<Vec<_>>();
        let point = storage
            .read_document_in_collection_at(
                at,
                &tenant(),
                &CollectionName::try_new("users").unwrap(),
                &DocumentId::try_new("doc-1").unwrap(),
            )
            .map(|document| document.metadata().version());
        let replace = commit(
            storage,
            "op-3",
            vec![put(document("doc-1", "users", "replaced"), ExpectedVersion::Exact(1))],
        );
        let at_after = storage.published_state().0;
        let version_after = storage
            .read_document_at(at_after, &tenant(), &DocumentId::try_new("doc-1").unwrap())
            .map(|document| document.metadata().version());
        let gone = commit(
            storage,
            "op-4",
            vec![delete(Some("users"), "doc-2", ExpectedVersion::Exact(1))],
        );
        let at_deleted = storage.published_state().0;
        let absent = storage
            .read_document_in_collection_at(
                at_deleted,
                &tenant(),
                &CollectionName::try_new("users").unwrap(),
                &DocumentId::try_new("doc-2").unwrap(),
            )
            .is_none();
        // A delete hides the key, so the next put restarts at version 1.
        let restart = commit(
            storage,
            "op-5",
            vec![put(document("doc-2", "users", "again"), ExpectedVersion::New)],
        );
        let at_restart = storage.published_state().0;
        let restarted_version = storage
            .read_document_at(at_restart, &tenant(), &DocumentId::try_new("doc-2").unwrap())
            .map(|document| document.metadata().version());
        (
            stored_sequence(&first),
            stored_sequence(&second),
            ids,
            point,
            stored_sequence(&replace),
            version_after,
            stored_sequence(&gone),
            absent,
            stored_sequence(&restart),
            restarted_version,
        )
    });
}

#[test]
fn differential_reopen_serves_the_same_state() {
    let file_dir = location("reopen-file");
    let sqlite_dir = location("reopen-sqlite");
    let mut file = FileStorage::open(&file_dir).expect("file reference opens");
    let mut sqlite = SqliteStorage::open(&sqlite_dir).expect("sqlite backend opens");
    for storage in [&mut file as &mut dyn StoragePort, &mut sqlite as &mut dyn StoragePort] {
        commit(storage, "op-1", vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)]);
        commit(storage, "op-2", vec![put(document("doc-2", "users", "two"), ExpectedVersion::New)]);
        commit(storage, "op-3", vec![delete(Some("users"), "doc-1", ExpectedVersion::Exact(1))]);
    }
    drop(file);
    drop(sqlite);
    let mut file = FileStorage::open(&file_dir).expect("file reference reopens");
    let mut sqlite = SqliteStorage::open(&sqlite_dir).expect("sqlite backend reopens");
    let observe = |storage: &mut dyn StoragePort| {
        let at = storage.published_state().0;
        (
            at.get(),
            storage.documents_at(at, &tenant()).len(),
            storage.journaled_high_water().map(StorageSequence::get),
            storage.journal_divergence(),
        )
    };
    assert_eq!(observe(&mut file), observe(&mut sqlite));
    drop(file);
    drop(sqlite);
    std::fs::remove_dir_all(&file_dir).ok();
    std::fs::remove_dir_all(&sqlite_dir).ok();
}

#[test]
fn differential_snapshot_reads_on_and_across_boundaries() {
    differential("snapshot-boundaries", |storage| {
        commit(storage, "op-1", vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)]);
        let first = storage.published_state().0;
        commit(storage, "op-2", vec![put(document("doc-2", "users", "two"), ExpectedVersion::New)]);
        let second = storage.published_state().0;
        commit(
            storage,
            "op-3",
            vec![put(document("doc-1", "users", "replaced"), ExpectedVersion::Exact(1))],
        );
        let third = storage.published_state().0;
        let ids_at = |sequence| {
            storage
                .documents_at(sequence, &tenant())
                .into_iter()
                .map(|document| (document.id().as_str().to_owned(), document.metadata().version()))
                .collect::<Vec<_>>()
        };
        (
            ids_at(first),
            ids_at(second),
            ids_at(third),
            storage
                .read_document_at(first, &tenant(), &DocumentId::try_new("doc-1").unwrap())
                .map(|document| document.metadata().version()),
            storage
                .read_document_at(second, &tenant(), &DocumentId::try_new("doc-1").unwrap())
                .map(|document| document.metadata().version()),
            storage.snapshot_boundary_available(first),
            storage.snapshot_boundary_available(second),
        )
    });
}

#[test]
fn differential_snapshot_below_first_commit_serves_empty_state() {
    differential("snapshot-empty", |storage| {
        commit(storage, "op-1", vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)]);
        let zero = StorageSequence::new(0);
        (
            storage.documents_at(zero, &tenant()).len(),
            storage
                .read_document_at(zero, &tenant(), &DocumentId::try_new("doc-1").unwrap())
                .is_none(),
        )
    });
}

#[test]
fn differential_pagination_edges() {
    differential("pagination", |storage| {
        let users = CollectionName::try_new("users").unwrap();
        commit(storage, "op-1", vec![put(document("doc-c", "users", "c"), ExpectedVersion::New)]);
        commit(storage, "op-2", vec![put(document("doc-a", "users", "a"), ExpectedVersion::New)]);
        commit(storage, "op-3", vec![put(document("doc-b", "orders", "b"), ExpectedVersion::New)]);
        commit(storage, "op-4", vec![put(document("doc-d", "users", "d"), ExpectedVersion::New)]);
        let at = storage.published_state().0;
        let ids = |documents: Vec<Document>| {
            documents
                .into_iter()
                .map(|document| document.id().as_str().to_owned())
                .collect::<Vec<_>>()
        };
        let tenant_from = |after: Option<&str>, limit: u32| {
            storage.documents_at_from(
                at,
                &tenant(),
                after.map(|cursor| DocumentId::try_new(cursor).unwrap()).as_ref(),
                limit,
            )
        };
        let tenant_from_key = |after: Option<(&str, &str)>, limit: u32| {
            let owned = after.map(|(collection, id)| {
                (CollectionName::try_new(collection).unwrap(), DocumentId::try_new(id).unwrap())
            });
            storage
                .documents_at_from_key(
                    at,
                    &tenant(),
                    owned.as_ref().map(|(collection, id)| (collection, id)),
                    limit,
                )
                .expect("keyed page serves")
        };
        let collection_from = |after: Option<&str>, limit: u32| {
            storage
                .documents_in_collection_at_from(
                    at,
                    &tenant(),
                    &users,
                    after.map(|cursor| DocumentId::try_new(cursor).unwrap()).as_ref(),
                    limit,
                )
                .expect("collection page serves")
        };
        vec![
            ids(tenant_from(None, 10)),
            ids(tenant_from(None, 2)),
            ids(tenant_from(None, 0)),
            ids(tenant_from(Some("doc-a"), 10)),
            ids(tenant_from(Some("doc-missing"), 10)),
            ids(tenant_from(Some("doc-d"), 10)),
            ids(tenant_from_key(None, 10)),
            ids(tenant_from_key(Some(("users", "doc-a")), 10)),
            ids(tenant_from_key(Some(("users", "doc-d")), 10)),
            ids(tenant_from_key(Some(("orders", "doc-b")), 10)),
            ids(tenant_from_key(Some(("users", "doc-missing")), 10)),
            ids(storage.documents_in_collection_at(at, &tenant(), &users)),
            ids(collection_from(None, 10)),
            ids(collection_from(Some("doc-a"), 1)),
            ids(collection_from(Some("doc-missing"), 10)),
        ]
    });
}

#[test]
fn differential_identifier_cursor_across_reused_identifiers() {
    differential("id-cursor-reuse", |storage| {
        commit(storage, "op-1", vec![put(document("shared", "alpha", "a"), ExpectedVersion::New)]);
        commit(storage, "op-2", vec![put(document("shared", "zeta", "z"), ExpectedVersion::New)]);
        commit(storage, "op-3", vec![put(document("tail", "zeta", "t"), ExpectedVersion::New)]);
        let at = storage.published_state().0;
        let page = storage.documents_at_from(
            at,
            &tenant(),
            Some(&DocumentId::try_new("shared").unwrap()),
            10,
        );
        let ids = page
            .into_iter()
            .map(|document| {
                (document.collection().as_str().to_owned(), document.id().as_str().to_owned())
            })
            .collect::<Vec<_>>();
        let by_identifier = storage
            .read_document_at(at, &tenant(), &DocumentId::try_new("shared").unwrap())
            .map(|document| document.collection().as_str().to_owned());
        (
            ids,
            by_identifier,
            storage
                .read_documents_in_collection_at(
                    at,
                    &tenant(),
                    &CollectionName::try_new("zeta").unwrap(),
                    &[
                        DocumentId::try_new("tail").unwrap(),
                        DocumentId::try_new("shared").unwrap(),
                        DocumentId::try_new("missing").unwrap(),
                    ],
                )
                .into_iter()
                .map(|document| document.id().as_str().to_owned())
                .collect::<Vec<_>>(),
        )
    });
}

#[test]
fn differential_collection_isolation_of_one_identifier() {
    differential("collection-isolation", |storage| {
        commit(
            storage,
            "op-1",
            vec![put(document("doc-1", "users", "user"), ExpectedVersion::New)],
        );
        commit(
            storage,
            "op-2",
            vec![put(document("doc-1", "archive", "archived"), ExpectedVersion::New)],
        );
        let at = storage.published_state().0;
        let users = CollectionName::try_new("users").unwrap();
        let archive = CollectionName::try_new("archive").unwrap();
        let id = DocumentId::try_new("doc-1").unwrap();
        (
            storage
                .read_document_in_collection_at(at, &tenant(), &users, &id)
                .map(|document| document.value().clone()),
            storage
                .read_document_in_collection_at(at, &tenant(), &archive, &id)
                .map(|document| document.value().clone()),
            // A delete of one collection's copy leaves the other intact.
            {
                let outcome = commit(
                    storage,
                    "op-3",
                    vec![delete(Some("users"), "doc-1", ExpectedVersion::Exact(1))],
                );
                let after = storage.published_state().0;
                (
                    stored_sequence(&outcome),
                    storage.read_document_in_collection_at(after, &tenant(), &users, &id).is_none(),
                    storage
                        .read_document_in_collection_at(after, &tenant(), &archive, &id)
                        .is_some(),
                )
            },
        )
    });
}

#[test]
fn differential_replay_conflict_and_operation_reuse() {
    differential("replay-conflict-reuse", |storage| {
        let first = commit(
            storage,
            "op-1",
            vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)],
        );
        // A retried identity with the same write set replays the receipt.
        let replay = commit(
            storage,
            "op-1",
            vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)],
        );
        // The same identity bound to a different write set is reuse.
        let reuse = commit(
            storage,
            "op-1",
            vec![put(document("doc-1", "users", "other"), ExpectedVersion::New)],
        );
        // A stale snapshot conflicts.
        let stale = storage.commit(intent(
            "op-2",
            vec![put(document("doc-1", "users", "stale"), ExpectedVersion::New)],
            LocalDurability::Written,
        ));
        // A retried conflict keeps its durable rejection.
        let stale_retry = storage.commit(intent(
            "op-2",
            vec![put(document("doc-1", "users", "stale"), ExpectedVersion::New)],
            LocalDurability::Written,
        ));
        // An expectation mismatch conflicts.
        let mismatch = commit(
            storage,
            "op-3",
            vec![put(document("doc-1", "users", "wrong"), ExpectedVersion::Exact(7))],
        );
        // A collection-less delete of an absent identifier is the provable
        // no-op commit.
        let noop = commit(storage, "op-4", vec![delete(None, "ghost", ExpectedVersion::Any)]);
        let after = storage.published_state().0;
        // Root identities encode backend-specific physical state. Compare
        // the portable result contract here: outcome, sequence, and reason.
        let normalize = |result: CommitResult| match result {
            CommitResult::Committed(receipt) => ("committed", Some(receipt.sequence.get()), None),
            CommitResult::Rejected(reason) => ("rejected", None, Some(reason)),
            CommitResult::Unknown(_) => ("unknown", None, None),
        };
        (
            stored_sequence(&first),
            normalize(replay),
            normalize(reuse),
            normalize(stale),
            normalize(stale_retry),
            normalize(mismatch),
            stored_sequence(&noop),
            storage
                .read_document_at(after, &tenant(), &DocumentId::try_new("doc-1").unwrap())
                .map(|document| document.metadata().version()),
        )
    });
}

#[test]
fn differential_tenant_history_high_water_and_divergence() {
    differential("tenant-history", |storage| {
        let other = TenantId::try_new("tenant-b").expect("tenant");
        let commit_as = |storage: &mut dyn StoragePort, tenant: &TenantId, operation: &str| {
            let mut request = intent(operation, vec![], LocalDurability::Written);
            request.tenant = tenant.clone();
            request.snapshot_sequence = storage.published_state().0;
            request.writes = vec![put(
                Document::new(
                    DocumentId::try_new(operation).unwrap(),
                    CollectionName::try_new("users").unwrap(),
                    DocumentValue::String(operation.to_owned()),
                ),
                ExpectedVersion::New,
            )];
            request.digest = write_set_digest(tenant, &request.writes);
            storage.commit(request)
        };
        commit_as(storage, &tenant(), "op-a1");
        commit_as(storage, &other, "op-b1");
        commit_as(storage, &tenant(), "op-a2");
        let published = storage.published_state().0;
        let commits = |tenant: &TenantId, after: u64| {
            storage
                .tenant_commits(tenant, StorageSequence::new(after), 10)
                .into_iter()
                .map(|commit| (commit.sequence().get(), commit.operation_id().as_str().to_owned()))
                .collect::<Vec<_>>()
        };
        let tenants = storage
            .committed_tenants()
            .into_iter()
            .map(|tenant| tenant.as_str().to_owned())
            .collect::<Vec<_>>();
        let first_tenant_commit = storage.tenant_commits(&tenant(), StorageSequence::new(0), 1);
        (
            published.get(),
            commits(&tenant(), 0),
            commits(&tenant(), 1),
            commits(&other, 0),
            tenants,
            first_tenant_commit.iter().map(|commit| commit.writes().len()).collect::<Vec<_>>(),
            storage.tenant_sequence(&tenant(), published).map(StorageSequence::get),
            storage.tenant_sequence(&tenant(), StorageSequence::new(1)).map(StorageSequence::get),
            storage.journaled_high_water().map(StorageSequence::get),
            storage.journal_divergence(),
        )
    });
}

#[test]
fn differential_winning_durabilities() {
    differential("winning-durabilities", |storage| {
        commit(storage, "op-1", vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)]);
        let durable = {
            let request = fresh_intent(
                storage,
                "op-2",
                vec![put(document("doc-2", "users", "two"), ExpectedVersion::New)],
                LocalDurability::Synced,
            );
            storage.commit(request)
        };
        let at = storage.published_state().0;
        let keys = [("users", "doc-1"), ("users", "doc-2")];
        (
            matches!(durable, CommitResult::Committed(_)),
            storage.winning_durabilities_at(&tenant(), at, &keys),
            storage.winning_durabilities_at(&tenant(), StorageSequence::new(1), &keys),
            // A key no retained write names makes the answer unprovable.
            storage.winning_durabilities_at(
                &tenant(),
                at,
                &[("users", "doc-1"), ("users", "never-written")],
            ),
        )
    });
}

// ---- Usage differential against the LSM reference ------------------------

fn standalone_fact(tenant: &TenantId, fact_id: &str, at_millis: u64, delta: i128) -> UsageFact {
    let definition =
        *MeterCatalog::initial().get(MeterKind::PublicEgressBytes, 1).expect("public egress meter");
    let scope = crate::usage::UsageScope::try_new(
        tenant.clone(),
        "project-a",
        "database-a",
        "partition-a",
        RegionCode::GRU,
    )
    .expect("scope");
    UsageFact::try_new(
        definition,
        scope,
        MintingIdentity::PublicResponse(UsageFactId::try_new(fact_id).expect("fact id")),
        MeasurementWindow::instant(at_millis),
        delta,
    )
    .expect("fact")
}

#[test]
fn usage_pages_match_the_lsm_reference() {
    let lsm_dir = location("usage-lsm");
    let sqlite_dir = location("usage-sqlite");
    let mut lsm = LsmStorage::open(&lsm_dir).expect("lsm reference opens");
    let mut sqlite = SqliteStorage::open(&sqlite_dir).expect("sqlite backend opens");
    let scenario = |storage: &mut dyn StoragePort| {
        let first =
            storage.record_usage(&tenant(), standalone_fact(&tenant(), "fact-1", 1_000, 128));
        let replay =
            storage.record_usage(&tenant(), standalone_fact(&tenant(), "fact-1", 1_000, 128));
        let rebind =
            storage.record_usage(&tenant(), standalone_fact(&tenant(), "fact-1", 1_000, 256));
        let _ = storage.record_usage(&tenant(), standalone_fact(&tenant(), "fact-2", 2_000, 64));
        let _ = storage.record_usage(&tenant(), standalone_fact(&tenant(), "fact-3", 3_000, 32));
        let snapshot = storage.published_state().0;
        let summarize = |facts: &[DurableUsageFact]| {
            facts
                .iter()
                .map(|fact| {
                    (
                        fact.sequence(),
                        fact.fact().identity().id().as_str().to_owned(),
                        fact.fact().delta(),
                    )
                })
                .collect::<Vec<_>>()
        };
        let page = storage.usage_facts_page(&tenant(), snapshot, None, None, 10);
        let limited = storage.usage_facts_page(&tenant(), snapshot, None, None, 2);
        let after_page = storage.usage_facts_page(
            &tenant(),
            snapshot,
            limited
                .as_ref()
                .ok()
                .and_then(|facts| facts.last())
                .map(UsageFactPosition::of)
                .as_ref(),
            None,
            10,
        );
        let windowed = storage.usage_facts_page(
            &tenant(),
            snapshot,
            None,
            Some(MeasurementWindow::interval(1_500, 2_500).expect("window")),
            10,
        );
        let below_snapshot =
            storage.usage_facts_page(&tenant(), StorageSequence::new(1), None, None, 10);
        let foreign = storage.usage_facts_page(
            &TenantId::try_new("tenant-b").unwrap(),
            snapshot,
            None,
            None,
            10,
        );
        (
            first.map(|(fact, replayed)| (fact.sequence(), replayed)),
            replay.map(|(fact, replayed)| (fact.sequence(), replayed)),
            rebind.err(),
            summarize(&page.clone().expect("page")),
            summarize(&limited.expect("limited page")),
            summarize(&after_page.expect("continuation page")),
            summarize(&windowed.expect("windowed page")),
            summarize(&below_snapshot.expect("snapshot-bounded page")),
            summarize(&foreign.expect("foreign tenant page")),
            page.expect("page").iter().map(UsageFactPosition::of).collect::<Vec<_>>(),
        )
    };
    let lsm_result = scenario(&mut lsm);
    let sqlite_result = scenario(&mut sqlite);
    assert_eq!(lsm_result, sqlite_result);
    drop(lsm);
    drop(sqlite);
    std::fs::remove_dir_all(&lsm_dir).ok();
    std::fs::remove_dir_all(&sqlite_dir).ok();
}

// ---- SQLite-focused: the journal-pruning floor ---------------------------

fn sqlite_store(tag: &str) -> (PathBuf, SqliteStorage) {
    let dir = location(tag);
    let storage = SqliteStorage::open(&dir).expect("sqlite backend opens");
    (dir, storage)
}

#[test]
fn pruning_requires_every_consumer_frontier() {
    let (_dir, mut storage) = sqlite_store("prune-frontiers");
    storage.advance_consumer_frontier("fast", 0).expect("fast frontier registers");
    storage.advance_consumer_frontier("slow", 0).expect("slow frontier registers");
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
        );
    }
    assert_eq!(storage.journal_records(), 3);
    // Both consumers exist, pinned at zero. One advancing prunes nothing:
    // the other still needs every row.
    storage.advance_consumer_frontier("fast", 3).expect("frontier advances");
    storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(storage.journal_records(), 3);
    // Both consumers past sequence 2: exactly the rows below the floor go.
    storage.advance_consumer_frontier("slow", 2).expect("frontier advances");
    let pruned = storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(pruned, 1);
    assert_eq!(storage.journal_records(), 2);
    // The pruning floor is the honest history bound now.
    assert_eq!(storage.servable_history_start().map(StorageSequence::get), Some(2));
    assert!(!storage.snapshot_boundary_available(StorageSequence::new(1)));
    assert!(storage.snapshot_boundary_available(StorageSequence::new(2)));
    // Above-floor snapshots still answer exactly.
    let at = storage.published_state().0;
    assert_eq!(storage.documents_at(at, &tenant()).len(), 3);
    assert_eq!(
        storage.documents_at(StorageSequence::new(2), &tenant()).len(),
        2,
        "the version history below the floor was pruned without changing answers above it"
    );
}

#[test]
fn pruning_keeps_each_keys_newest_version() {
    let (_dir, mut storage) = sqlite_store("prune-versions");
    commit(&mut storage, "op-1", vec![put(document("kept", "users", "one"), ExpectedVersion::New)]);
    commit(
        &mut storage,
        "op-2",
        vec![put(document("kept", "users", "two"), ExpectedVersion::Exact(1))],
    );
    commit(&mut storage, "op-3", vec![delete(Some("users"), "gone", ExpectedVersion::Any)]);
    commit(
        &mut storage,
        "op-4",
        vec![put(document("fresh", "users", "new"), ExpectedVersion::New)],
    );
    storage.advance_consumer_frontier("solo", 4).expect("frontier advances");
    let pruned = storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(pruned, 3);
    // Each key's newest version survives so above-floor snapshots resolve;
    // the shadowed older version is gone, and the tombstone still hides
    // its key.
    let at = storage.published_state().0;
    let kept = storage.read_document_at(at, &tenant(), &DocumentId::try_new("kept").unwrap());
    assert_eq!(kept.map(|document| document.metadata().version()), Some(2));
    assert!(
        storage.read_document_at(at, &tenant(), &DocumentId::try_new("gone").unwrap()).is_none()
    );
    assert!(
        storage.read_document_at(at, &tenant(), &DocumentId::try_new("fresh").unwrap()).is_some()
    );
}

#[test]
fn sustained_writes_with_advancing_consumers_keep_the_journal_bounded() {
    let (_dir, mut storage) = sqlite_store("prune-bounded");
    storage.advance_consumer_frontier("replication", 0).expect("frontier registers");
    storage.advance_consumer_frontier("sync", 0).expect("frontier registers");
    for round in 1..=50 {
        commit(
            &mut storage,
            &format!("op-{round}"),
            vec![put(document(&format!("doc-{round}"), "users", "value"), ExpectedVersion::New)],
        );
        let head = storage.published_state().0.get();
        storage.advance_consumer_frontier("replication", head).expect("frontier advances");
        storage.advance_consumer_frontier("sync", head).expect("frontier advances");
        storage.prune_below_consumer_frontiers().expect("prune runs");
    }
    assert!(
        storage.journal_records() <= 2,
        "an advancing consumer fleet bounds the journal, found {}",
        storage.journal_records()
    );
    assert_eq!(storage.servable_history_start().map(StorageSequence::get), Some(50));

    // A consumer registered behind a new head pins every row it still needs.
    commit(
        &mut storage,
        "op-51",
        vec![put(document("doc-51", "users", "value"), ExpectedVersion::New)],
    );
    storage.advance_consumer_frontier("metering", 50).expect("frontier registers at the head");
    storage.advance_consumer_frontier("replication", 51).expect("frontier advances");
    storage.advance_consumer_frontier("sync", 51).expect("frontier advances");
    storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(storage.servable_history_start().map(StorageSequence::get), Some(50));
    assert_eq!(storage.journal_records(), 2);
}

#[test]
fn compact_sweeps_the_pruning_floor() {
    let (_dir, mut storage) = sqlite_store("prune-compact");
    storage.advance_consumer_frontier("solo", 0).expect("frontier registers");
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
        );
    }
    // `compact` is the lifecycle sweep: advancing the frontier and
    // compacting bounds the journal with no other call.
    storage.advance_consumer_frontier("solo", 3).expect("frontier advances");
    let request = crate::storage::compaction::StorageCompaction {
        policy: CompactionPolicy::Reclaim,
        floor: None,
        progress: None,
    };
    let (receipt, _facts) = storage.compact(request).expect("compaction sweeps");
    assert_eq!(storage.journal_records(), 1, "the sweep pruned below the floor");
    assert_eq!(
        receipt.servable_start_after().map(StorageSequence::get),
        Some(3),
        "the receipt reports the floor as the servable bound"
    );
}

// ---- SQLite-focused: crash atomicity against the file --------------------

/// Asserts the crash invariant directly against the database file: no
/// document row without its journal row, and no journal row without its
/// committed ledger row.
fn assert_journal_binding(dir: &Path) {
    let connection = Connection::open(dir.join(super::DATABASE_FILE)).expect("raw connection");
    let unbonded: i64 = connection
        .query_row(
            "SELECT COUNT(*) FROM documents WHERE commit_seq NOT IN (SELECT sequence FROM journal)",
            [],
            |row| row.get(0),
        )
        .expect("documents bind");
    assert_eq!(unbonded, 0, "a document row exists without its journal row");
    let orphan: i64 = connection
        .query_row(
            "SELECT COUNT(*) FROM journal WHERE sequence NOT IN (
               SELECT sequence FROM ledger WHERE committed = 1)",
            [],
            |row| row.get(0),
        )
        .expect("journal binds");
    assert_eq!(orphan, 0, "a journal row exists without its committed ledger row");
}

#[test]
fn crash_window_process_death_between_transactions_replays_committed_state() {
    let (dir, storage) = sqlite_store("crash-between");
    let mut storage = storage;
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("op-{index}"),
            vec![put(document(&format!("doc-{index}"), "users", "value"), ExpectedVersion::New)],
        );
    }
    let expected = storage.documents_at(storage.published_state().0, &tenant());
    // Simulated process death: the connections leak without close, so no
    // checkpoint runs and the WAL carries exactly the committed frames.
    std::mem::forget(storage);
    let reopened = SqliteStorage::open(&dir).expect("wal recovery reopens");
    assert_eq!(reopened.documents_at(reopened.published_state().0, &tenant()), expected);
    assert_eq!(reopened.journal_divergence(), 0);
    assert_journal_binding(&dir);
}

#[test]
fn crash_window_torn_wal_tail_after_last_commit_loses_nothing_committed() {
    let (dir, storage) = sqlite_store("crash-torn-tail");
    let mut storage = storage;
    commit(
        &mut storage,
        "op-1",
        vec![put(document("doc-1", "users", "one"), ExpectedVersion::New)],
    );
    commit(
        &mut storage,
        "op-2",
        vec![put(document("doc-2", "users", "two"), ExpectedVersion::New)],
    );
    let expected = storage.documents_at(storage.published_state().0, &tenant());
    std::mem::forget(storage);
    // Torn tail: bytes after the last valid commit frame, the shape a
    // crash mid-append leaves behind.
    let wal = dir.join(format!("{}-wal", super::DATABASE_FILE));
    if let Ok(mut file) = std::fs::OpenOptions::new().append(true).create(true).open(&wal) {
        let _ = std::io::Write::write_all(&mut file, &[0xa5; 8192]);
    }
    let reopened = SqliteStorage::open(&dir).expect("recovery stops at the last valid frame");
    assert_eq!(reopened.documents_at(reopened.published_state().0, &tenant()), expected);
    assert_eq!(reopened.journal_divergence(), 0);
    assert_journal_binding(&dir);
}

#[test]
fn group_commit_batches_are_atomic() {
    let (dir, mut storage) = sqlite_store("group-commit");
    let published = storage.published_state().0;
    let intents: Vec<CommitIntent> = ["a", "b", "c"]
        .iter()
        .map(|suffix| {
            let mut request = intent(
                &format!("op-{suffix}"),
                vec![put(
                    document(&format!("doc-{suffix}"), "users", "value"),
                    ExpectedVersion::New,
                )],
                LocalDurability::Written,
            );
            request.snapshot_sequence = published;
            request
        })
        .collect();
    let results = storage.commit_batch(intents, false);
    let sequences: Vec<u64> = results.iter().map(stored_sequence).collect();
    assert_eq!(sequences, vec![1, 2, 3]);
    drop(storage);
    let reopened = SqliteStorage::open(&dir).expect("batch reopens");
    assert_eq!(reopened.documents_at(reopened.published_state().0, &tenant()).len(), 3);
    assert_eq!(reopened.journal_divergence(), 0);
    assert_journal_binding(&dir);
}

// ---- SQLite-focused: detection fails closed ------------------------------

#[test]
fn foreign_and_stranded_locations_fail_closed() {
    // A foreign format marker refuses the location.
    let foreign = location("detect-foreign");
    std::fs::create_dir_all(&foreign).unwrap();
    std::fs::write(foreign.join("format"), b"TBV2LSM1").unwrap();
    assert_eq!(
        SqliteStorage::open(&foreign).err(),
        Some(StorageOpenError::FormatMismatch),
        "a location naming another format never converts"
    );
    // A marker without its database is loss, not a fresh location.
    let stranded = location("detect-stranded");
    std::fs::create_dir_all(&stranded).unwrap();
    let mut marker = Vec::new();
    marker.extend_from_slice(super::SQLITE_FORMAT_MAGIC);
    marker.extend_from_slice(&1_u64.to_le_bytes());
    std::fs::write(stranded.join("format"), marker).unwrap();
    assert_eq!(
        SqliteStorage::open(&stranded).err(),
        Some(StorageOpenError::FormatMismatch),
        "a marker without its database never mints a fresh database over it"
    );
    // Foreign artifacts beside a would-be fresh location refuse it.
    let occupied = location("detect-occupied");
    std::fs::create_dir_all(&occupied).unwrap();
    std::fs::write(occupied.join("stranger"), b"not ours").unwrap();
    assert_eq!(
        SqliteStorage::open(&occupied).err(),
        Some(StorageOpenError::FormatMismatch),
        "a fresh location may not sit beside foreign artifacts"
    );
}
