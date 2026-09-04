//! In-crate tests for the `SQLite` storage (D-27, TB-20).
//!
//! The engine-level suite drives the real `Engine` API so the port contract
//! is proven behind the same seam production uses. The storage-level suite
//! pins the journal-pruning floor's semantics and exercises the crash
//! windows against the database file itself.

use std::{
    path::{Path, PathBuf},
    sync::{
        Arc, Barrier,
        atomic::{AtomicU64, Ordering},
    },
};

use rusqlite::Connection;

use crate::engine::{CommitError, CommitOutcome, DurableResolution, Engine, EngineConfig};
use crate::storage::intent::{CommitIntent, CommitResult, StagedWrite};
use crate::storage::{FaultPoint, StorageOpenError, StoragePort, write_set_digest};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
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
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

/// Reopens a `SQLite` location.
fn reopen_sqlite_engine(dir: &PathBuf) -> Engine {
    Engine::open(EngineConfig::durable(dir).expect("durable location")).expect("engine reopens")
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
    let storage = reopened.storage.lock().expect("storage lock");
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
    let storage = engine.storage.lock().expect("storage lock");
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
fn sqlite_file_is_the_storage_identity_without_an_external_marker() {
    let (dir, engine) = open_sqlite_engine("file-identity");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "users",
        "stored",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert!(!dir.join("format").exists(), "the retired selector is never created");
    let bytes = std::fs::read(super::database_file(&dir)).expect("SQLite file exists");
    assert_eq!(bytes.get(..16), Some(b"SQLite format 3\0".as_slice()));
    drop(engine);
    // SQLite's own file and the durable owner are enough to reopen the
    // location without a separate backend selector.
    let reopened =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("reopens");
    assert_eq!(
        read_document(&reopened, "tenant-a", "doc-1").expect("persisted").value(),
        &DocumentValue::String("stored".to_owned())
    );
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn retention_sweep_checkpoints_the_wal() {
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
    engine.run_retention_sweep().expect("sweep runs");
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("readable").value(),
        &DocumentValue::String("checkpointed".to_owned()),
        "a swept location still reads"
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
        hlc: 0,
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
    // One key rewritten each commit: older commits are shadowed history the
    // frontiers bound (the newest version is current state and pins its
    // own commit).
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("op-{index}"),
            vec![put(
                document("doc", "users", &format!("v{index}")),
                if index == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(index - 1) },
            )],
        );
    }
    assert_eq!(storage.journal_records(), 3);
    backdate_all_rows(&storage);
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
    assert_eq!(storage.documents_at(at, &tenant()).len(), 1);
    assert_eq!(
        storage.documents_at(StorageSequence::new(2), &tenant()).len(),
        1,
        "the version history below the floor was pruned without changing answers above it"
    );
}

/// Backdates every row past the D-42 history floor so a sweep is bounded
/// only by the consumer frontiers.
#[allow(dead_code)]
fn backdate_all_rows(storage: &SqliteStorage) {
    let connection = storage.raw_writer_connection();
    connection.execute("UPDATE documents SET committed_at = 0", []).expect("rows backdate");
}

#[test]
fn fresh_rows_survive_the_sweep_regardless_of_frontiers() {
    // The D-42 history floor: within thirty days of their commit, rows are
    // reachable even when every consumer frontier has passed them.
    let (_dir, mut storage) = sqlite_store("prune-history-floor");
    for index in 1..=5 {
        commit(
            &mut storage,
            &format!("op-{index}"),
            vec![put(
                document("doc", "users", &format!("v{index}")),
                if index == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(index - 1) },
            )],
        );
    }
    let head = storage.published_state().0;
    storage.advance_consumer_frontier("solo", head.get()).expect("frontier passes everything");
    let pruned = storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(pruned, 0, "nothing inside the history floor is prunable");
    assert_eq!(storage.journal_records(), 5, "every fresh row is still reachable");
    // A mid-window backdate prunes exactly the shadowed commits older than
    // the floor. The writer guard is scoped so the sweep below can take
    // the writer.
    {
        let connection = storage.raw_writer_connection();
        connection
            .execute("UPDATE documents SET committed_at = 0 WHERE commit_seq <= 3", [])
            .expect("rows backdate");
    }
    let pruned = storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(pruned, 3, "only the backdated rows leave the journal");
    assert_eq!(storage.journal_records(), 2, "the rows inside the floor stay");
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
    // Backdate the rows past the 30-day history floor so the sweep is
    // frontier-bounded (fresh rows are inside the floor by construction).
    backdate_all_rows(&storage);
    // Whole-commit retention: only op-1 (fully shadowed by op-2) leaves.
    // The tombstone at op-3 and the fresh put at op-4 pin their commits —
    // each key's newest version must survive so above-floor snapshots
    // resolve.
    let pruned = storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(pruned, 1);
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
    // One hot key: each round's commit shadows the last, so an advancing
    // consumer fleet bounds the retained history (the newest version is
    // current state and pins its own commit).
    for round in 1..=50 {
        commit(
            &mut storage,
            &format!("op-{round}"),
            vec![put(
                document("hot", "users", &format!("v{round}")),
                if round == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(round - 1) },
            )],
        );
        let head = storage.published_state().0.get();
        storage.advance_consumer_frontier("replication", head).expect("frontier advances");
        storage.advance_consumer_frontier("sync", head).expect("frontier advances");
        backdate_all_rows(&storage);
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
        vec![put(document("hot", "users", "v51"), ExpectedVersion::Exact(50))],
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
            vec![put(
                document("doc", "users", &format!("v{index}")),
                if index == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(index - 1) },
            )],
        );
    }
    // The lifecycle sweep: backdating past the D-42 history floor and
    // advancing the frontier bounds the journal with no other call.
    backdate_all_rows(&storage);
    storage.advance_consumer_frontier("solo", 3).expect("frontier advances");
    storage.run_retention_sweep().expect("sweep runs");
    assert_eq!(storage.journal_records(), 1, "the sweep pruned below the floor");
    assert_eq!(
        storage.servable_history_start(),
        Some(StorageSequence::new(3)),
        "the floor is the servable bound"
    );
}

// ---- SQLite-focused: crash atomicity against the file --------------------

/// Asserts the folded row model's crash invariant directly against the
/// database file: every document row carries its commit binding (the
/// columns that make the row its own journal record) and no tombstone
/// carries a body.
fn assert_journal_binding(dir: &Path) {
    let connection = Connection::open(dir.join(super::DATABASE_FILE)).expect("raw connection");
    let unbonded: i64 = connection
        .query_row(
            "SELECT COUNT(*) FROM documents
             WHERE commit_ulid = '' OR revision = '' OR requested NOT BETWEEN 0 AND 2
                OR (deleted = 1 AND body != X'')",
            [],
            |row| row.get(0),
        )
        .expect("rows bind");
    assert_eq!(unbonded, 0, "a document row exists without its commit binding");
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
    assert_journal_binding(&dir);
}

// ---- SQLite-focused: detection fails closed ------------------------------

#[test]
fn a_non_sqlite_database_file_fails_closed() {
    let foreign = location("detect-non-sqlite");
    std::fs::create_dir_all(&foreign).unwrap();
    std::fs::write(super::database_file(&foreign), b"not a SQLite database").unwrap();
    assert_eq!(
        SqliteStorage::open(&foreign).err(),
        Some(StorageOpenError::Unavailable),
        "SQLite refuses foreign bytes instead of replacing them"
    );
    std::fs::remove_dir_all(&foreign).ok();
}

// ---- S-01 gates: one row, the counter cache -------------------------------

#[test]
fn write_then_read_shows_the_journal_row_is_the_document_row() {
    let (dir, mut storage) = sqlite_store("fold-one-row");
    commit(
        &mut storage,
        "op-fold",
        vec![put(document("folded", "users", "value"), ExpectedVersion::New)],
    );
    {
        let connection = storage.raw_writer_connection();
        let rows: i64 = connection
            .query_row("SELECT COUNT(*) FROM documents", [], |row| row.get(0))
            .expect("counts rows");
        assert_eq!(rows, 1, "one op wrote exactly one row");
        let bound: (i64, String, i64) = connection
            .query_row("SELECT deleted, commit_ulid, requested FROM documents", [], |row| {
                Ok((row.get(0)?, row.get(1)?, row.get(2)?))
            })
            .expect("reads the row");
        assert_eq!(bound.0, 0, "the row is a put");
        assert_eq!(bound.1, "op-fold", "the row carries its own commit binding");
        assert_eq!(bound.2, 1, "the row carries the requested durability (Written)");
        let journal: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('journal', 'ledger')",
                [],
                |row| row.get(0),
            )
            .expect("inspects the schema");
        assert_eq!(journal, 0, "no journal or ledger table exists");
    }
    drop(storage);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn usage_counters_flush_per_wave_and_reconcile_from_the_journal() {
    let (dir, mut storage) = sqlite_store("usage-counters");
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("op-{index}"),
            vec![put(
                document("doc", "users", &format!("v{index}")),
                if index == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(index - 1) },
            )],
        );
    }
    {
        let connection = storage.raw_writer_connection();
        let counted: (i64, i64) = connection
            .query_row("SELECT seq_hi, ops FROM usage_counters WHERE key = 'ops'", [], |row| {
                Ok((row.get(0)?, row.get(1)?))
            })
            .expect("reads the counter");
        assert_eq!(counted, (3, 3), "three single-op waves counted three ops");
        // A lost or torn counter is a cache miss, never lost truth.
        connection
            .execute("UPDATE usage_counters SET seq_hi = 0, ops = 0 WHERE key = 'ops'", [])
            .expect("tamper lands");
    }
    drop(storage);
    let reopened = SqliteStorage::open(&dir).expect("reopens");
    {
        let connection = reopened.raw_writer_connection();
        let reconciled: (i64, i64) = connection
            .query_row("SELECT seq_hi, ops FROM usage_counters WHERE key = 'ops'", [], |row| {
                Ok((row.get(0)?, row.get(1)?))
            })
            .expect("reads the reconciled counter");
        assert_eq!(reconciled, (3, 3), "open reconciles the counter from the journal");
    }
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

// ---- S-02: extracted index columns (D-31/D-42) ---------------------------

use crate::storage::{
    DescriptorRegistration, IndexDescriptorError, ScanCondition, ScanFilter, ScanOp,
};

/// One object document carrying the named top-level fields.
fn object_document(id: &str, collection: &str, fields: &[(&str, DocumentValue)]) -> Document {
    let map = fields
        .iter()
        .map(|(name, value)| ((*name).to_owned(), value.clone()))
        .collect::<std::collections::BTreeMap<String, DocumentValue>>();
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid collection"),
        DocumentValue::Object(map),
    )
}

/// Commits one object document through the storage port.
fn commit_object(
    storage: &mut dyn StoragePort,
    _tenant: &TenantName,
    operation: &str,
    document: Document,
) -> u64 {
    let writes = vec![StagedWrite::Put { document, expected: ExpectedVersion::Any }];
    let result = commit(storage, operation, writes);
    stored_sequence(&result)
}

/// The tenant name shared by the descriptor tests.
type TenantName = TenantId;

fn scan_tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

fn scan_users() -> CollectionName {
    CollectionName::try_new("users").expect("valid collection")
}

/// Runs one filtered scan and returns the documents, failing the test on a
/// refusal.
fn filtered(storage: &SqliteStorage, sequence: u64, filter: &ScanFilter) -> Option<Vec<Document>> {
    storage
        .collection_scan_filtered(
            StorageSequence::new(sequence),
            &scan_tenant(),
            &scan_users(),
            None,
            u32::MAX,
            filter,
        )
        .map(|result| result.expect("the scan reads"))
}

fn equals(field: &str, value: DocumentValue) -> ScanFilter {
    ScanFilter::try_new(vec![ScanCondition { field: field.to_owned(), op: ScanOp::Equals, value }])
        .expect("one condition fits the bound")
}

#[test]
fn registration_backfills_and_the_write_path_extracts() {
    let (dir, mut storage) = sqlite_store("extracted-backfill");
    let tenant = scan_tenant();
    let users = scan_users();
    // Rows that predate the descriptor: the inline backfill must cover
    // them (D-31's cost-capped inline build — this collection is small).
    let seq_a = commit_object(
        &mut storage,
        &tenant,
        "op-a",
        object_document("doc-a", "users", &[("status", DocumentValue::String("paid".into()))]),
    );
    commit_object(
        &mut storage,
        &tenant,
        "op-b",
        object_document("doc-b", "users", &[("status", DocumentValue::String("shipped".into()))]),
    );
    assert_eq!(
        storage.register_index_descriptor(&users, "status"),
        Ok(DescriptorRegistration::Active),
        "a small collection backfills inline and is active at once"
    );
    // Rows the write path serves after registration carry their values in
    // the same statement as the body (D-42).
    commit_object(
        &mut storage,
        &tenant,
        "op-c",
        object_document("doc-c", "users", &[("status", DocumentValue::String("paid".into()))]),
    );
    commit_object(
        &mut storage,
        &tenant,
        "op-d",
        object_document("doc-d", "users", &[("other", DocumentValue::Integer(7))]),
    );
    let paid =
        filtered(&storage, seq_a + 10, &equals("status", DocumentValue::String("paid".into())))
            .expect("the filter pushes");
    assert_eq!(
        paid.iter().map(|document| document.id().as_str()).collect::<Vec<_>>(),
        ["doc-a", "doc-c"],
        "both the backfilled row and the write-path row match"
    );
    // A field with no descriptor never pushes: the answer is None and the
    // caller takes the unfiltered path.
    assert!(
        filtered(&storage, seq_a + 10, &equals("other", DocumentValue::Integer(7.into())))
            .is_none(),
        "an unregistered field does not push"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn filtered_scan_resolves_the_newest_version_and_hides_tombstones() {
    let (dir, mut storage) = sqlite_store("extracted-as-of");
    let tenant = scan_tenant();
    let users = scan_users();
    let seq_one = commit_object(
        &mut storage,
        &tenant,
        "op-1",
        object_document("doc", "users", &[("status", DocumentValue::String("paid".into()))]),
    );
    assert_eq!(
        storage.register_index_descriptor(&users, "status"),
        Ok(DescriptorRegistration::Active)
    );
    let seq_two = commit_object(
        &mut storage,
        &tenant,
        "op-2",
        object_document("doc", "users", &[("status", DocumentValue::String("shipped".into()))]),
    );
    let paid = equals("status", DocumentValue::String("paid".into()));
    assert_eq!(
        filtered(&storage, seq_one, &paid).map(|documents| documents.len()),
        Some(1),
        "the as-of scan observes the version at its boundary"
    );
    assert_eq!(
        filtered(&storage, seq_two, &paid).map(|documents| documents.len()),
        Some(0),
        "the newest version wins and the pushed column follows it"
    );
    // A tombstone hides the key from the pushed scan exactly as it does
    // from the unfiltered one.
    let writes = vec![StagedWrite::Delete {
        collection: Some(users.clone()),
        id: DocumentId::try_new("doc").expect("valid identifier"),
        expected: ExpectedVersion::Any,
    }];
    let seq_three = stored_sequence(&commit(&mut storage, "op-3", writes));
    assert_eq!(
        filtered(&storage, seq_three, &paid).map(|documents| documents.len()),
        Some(0),
        "a deleted key never surfaces"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn numeric_conditions_compare_across_integer_and_float() {
    let (dir, mut storage) = sqlite_store("extracted-numeric");
    let tenant = scan_tenant();
    let users = scan_users();
    commit_object(
        &mut storage,
        &tenant,
        "op-a",
        object_document(
            "doc-a",
            "users",
            &[("age", DocumentValue::Integer(30)), ("score", DocumentValue::Number(4.5))],
        ),
    );
    commit_object(
        &mut storage,
        &tenant,
        "op-b",
        object_document("doc-b", "users", &[("age", DocumentValue::Integer(65))]),
    );
    assert_eq!(
        storage.register_index_descriptor(&users, "age"),
        Ok(DescriptorRegistration::Active)
    );
    assert_eq!(
        storage.register_index_descriptor(&users, "score"),
        Ok(DescriptorRegistration::Active)
    );
    let latest = storage.published_state().0.get();
    let over = ScanFilter::try_new(vec![ScanCondition {
        field: "age".to_owned(),
        op: ScanOp::Greater,
        value: DocumentValue::Number(29.5),
    }])
    .expect("bound");
    assert_eq!(
        filtered(&storage, latest, &over).map(|documents| documents.len()),
        Some(2),
        "a float bound matches integer-stored values numerically"
    );
    let exact = equals("score", DocumentValue::Number(4.5));
    assert_eq!(
        filtered(&storage, latest, &exact).map(|documents| documents.len()),
        Some(1),
        "a float condition matches a float-stored value"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn descriptors_survive_reopen_and_registration_is_idempotent() {
    let (dir, mut storage) = sqlite_store("extracted-reopen");
    let tenant = scan_tenant();
    let users = scan_users();
    commit_object(
        &mut storage,
        &tenant,
        "op-a",
        object_document("doc-a", "users", &[("status", DocumentValue::String("paid".into()))]),
    );
    assert_eq!(
        storage.register_index_descriptor(&users, "status"),
        Ok(DescriptorRegistration::Active)
    );
    assert_eq!(
        storage.register_index_descriptor(&users, "status"),
        Ok(DescriptorRegistration::Unchanged),
        "an identical descriptor is a no-op"
    );
    assert_eq!(
        storage.register_index_descriptor(&users, "bad\u{7}field"),
        Err(IndexDescriptorError::Storage),
        "a field name that fails the identifier discipline refuses typed"
    );
    drop(storage);
    let reopened = SqliteStorage::open(&dir).expect("reopens with the descriptors");
    let latest = reopened.published_state().0.get();
    assert_eq!(
        filtered(&reopened, latest, &equals("status", DocumentValue::String("paid".into())))
            .map(|documents| documents.len()),
        Some(1),
        "the reopened location pushes conditions over its loaded descriptors"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn the_extracted_column_budget_is_enforced() {
    let (dir, mut storage) = sqlite_store("extracted-budget");
    let users = scan_users();
    for position in 0..32 {
        let field = format!("f{position}");
        assert_eq!(
            storage.register_index_descriptor(&users, &field),
            Ok(DescriptorRegistration::Active),
            "field {field} registers inside the budget"
        );
    }
    assert_eq!(
        storage.register_index_descriptor(&users, "f32"),
        Err(IndexDescriptorError::ColumnBudgetExceeded),
        "the thirty-third distinct field refuses typed"
    );
    // An already-registered field is still fine past the budget.
    assert_eq!(
        storage.register_index_descriptor(&users, "f0"),
        Ok(DescriptorRegistration::Unchanged)
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn governed_backfill_completes_a_building_descriptor_without_stranding_a_sequence() {
    let (dir, mut storage) = sqlite_store("governed-backfill");
    let users = scan_users();
    // Phase one: a window spanning many small sequences advances one whole
    // sequence set per pass, reporting shrinking remainders until it
    // completes.
    for wave in 0..12 {
        let group = if wave % 2 == 0 { "even" } else { "odd" };
        let writes = (0..1000)
            .map(|index: u32| {
                put(
                    object_document(
                        &format!("m{wave:02}-{index:04}"),
                        "users",
                        &[("grp", DocumentValue::String(group.into()))],
                    ),
                    ExpectedVersion::Any,
                )
            })
            .collect::<Vec<_>>();
        commit(&mut storage, &format!("op-m{wave:02}"), writes);
    }
    assert_eq!(
        storage.register_index_descriptor(&users, "grp"),
        Ok(DescriptorRegistration::Building),
        "twelve thousand rows register building"
    );
    let mut previous = u64::MAX;
    let mut completed = false;
    for _ in 0..10 {
        let progress = storage.pump_index_backfills(5000).expect("the pass pumps");
        assert_eq!(progress.len(), 1, "one building descriptor owes work");
        if progress[0].active {
            assert_eq!(progress[0].remaining, 0);
            completed = true;
            break;
        }
        assert!(progress[0].remaining < previous, "each pass covers whole sequences and owes less");
        previous = progress[0].remaining;
    }
    assert!(completed, "bounded passes complete the descriptor");
    let latest = storage.published_state().0.get();
    let evens = filtered(&storage, latest, &equals("grp", DocumentValue::String("even".into())))
        .expect("conditions push once active");
    assert_eq!(evens.len(), 6000, "every wave's rows backfilled across passes");
    let writes = (0..10_001)
        .map(|index: u32| {
            let tag = if index.is_multiple_of(1000) { "needle" } else { "bulk" };
            put(
                object_document(
                    &format!("doc-{index:05}"),
                    "users",
                    &[("tag", DocumentValue::String(tag.into()))],
                ),
                ExpectedVersion::Any,
            )
        })
        .collect::<Vec<_>>();
    commit(&mut storage, "op-bulk", writes);
    assert_eq!(
        storage.register_index_descriptor(&users, "tag"),
        Ok(DescriptorRegistration::Building),
        "a collection past the inline cap registers building"
    );
    let mut completed = false;
    for _ in 0..20 {
        let progress = storage.pump_index_backfills(1000).expect("the pass pumps");
        assert_eq!(progress.len(), 1, "one building descriptor owes work");
        assert_eq!(progress[0].collection, "users");
        assert_eq!(progress[0].field, "tag");
        if progress[0].active {
            assert_eq!(progress[0].remaining, 0);
            completed = true;
            break;
        }
    }
    assert!(completed, "bounded passes complete the descriptor");
    assert!(
        storage.pump_index_backfills(1000).expect("the idle pass pumps").is_empty(),
        "a completed descriptor owes no further pass"
    );
    let latest = storage.published_state().0.get();
    let needles =
        filtered(&storage, latest, &equals("tag", DocumentValue::String("needle".into())))
            .expect("conditions push once active");
    assert_eq!(
        needles.iter().map(|document| document.id().as_str().to_owned()).collect::<Vec<_>>(),
        (0..10_001).step_by(1000).map(|index| format!("doc-{index:05}")).collect::<Vec<_>>(),
        "every same-sequence row backfilled: no strand, no miss"
    );
    std::fs::remove_dir_all(&dir).ok();
}
