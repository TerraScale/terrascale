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
    time::Duration,
};

use rusqlite::Connection;

use crate::engine::{CommitError, CommitOutcome, DurableResolution, Engine, EngineConfig};
use crate::storage::intent::{CommitIntent, CommitResult, StagedWrite, TtlGuard};
use crate::storage::{DbUsageFact, FaultPoint, StorageOpenError, StoragePort, write_set_digest};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
};

use super::SqliteStorage;
#[path = "cross_file_tests.rs"]
mod cross_file_tests;

#[test]
fn default_local_origin_uses_canonical_lane_identity() {
    let origin = super::local_origin_for(Path::new("/tmp/terrabase-session-origin"));
    let parsed = crate::origin_lane::OriginLaneId::parse(&origin).expect("canonical lane origin");
    assert_eq!(parsed.region().as_str(), "local");
    assert_ne!(parsed.incarnation(), 0);
}

#[test]
fn sequence_watermarks_and_billing_fold_do_not_scan_retained_facts() {
    let connection = Connection::open_in_memory().expect("database");
    connection.execute_batch(super::SCHEMA).expect("schema");
    connection.execute_batch(
        "WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<10000)
         INSERT INTO operation_facts (tenant,client_request_ulid,digest,committed,sequence,writes,bytes)
         SELECT 'tenant',printf('op-%d',i),0,1,i,1,256 FROM n;
         INSERT INTO usage_facts
         SELECT tenant,'project','database','partition','GRU',1,client_request_ulid,sequence,0,1,X'00'
         FROM operation_facts;"
    ).expect("retained history");
    for (sql, expected) in [
        ("SELECT MAX(sequence) FROM operation_facts", 10000_i64),
        ("SELECT MAX(seq) FROM usage_facts", 10000),
        ("SELECT SUM(writes) FROM operation_facts WHERE committed=1 AND sequence>9990", 10),
    ] {
        let mut statement = connection.prepare(sql).expect("prepare");
        let value: i64 = statement.query_row([], |row| row.get(0)).expect("query");
        assert_eq!(value, expected, "{sql}");
        assert_eq!(statement.get_status(rusqlite::StatementStatus::FullscanStep), 0, "{sql}");
        assert!(statement.get_status(rusqlite::StatementStatus::VmStep) < 200, "{sql}");
    }
}

#[test]
fn fts_key_migration_preserves_rows_and_transactional_deletes() {
    let mut connection = Connection::open_in_memory().expect("database");
    connection.execute_batch(super::SCHEMA).expect("legacy schema");
    connection
        .execute_batch(
            "INSERT INTO fts_docs (rowid, tenant, collection, field, doc_id, text)
             VALUES (42, 'a', 'docs', 'body', 'same', 'needle'),
                    (99, 'b', 'docs', 'body', 'same', 'needle');",
        )
        .expect("legacy search rows");
    super::migrate_fts_keys(&mut connection).expect("migrate");
    super::migrate_fts_keys(&mut connection).expect("idempotent reopen");
    let row_id: i64 = connection
        .query_row("SELECT row_id FROM fts_keys WHERE tenant = 'a'", [], |row| row.get(0))
        .expect("preserved key");
    assert_eq!(row_id, 42);
    {
        let transaction = connection.transaction().expect("transaction");
        transaction.execute("DELETE FROM fts_keys WHERE tenant = 'a'", []).expect("delete");
        super::insert_fts_row(&transaction, "a", "docs", "body", "same", "replacement")
            .expect("replace");
        transaction.rollback().expect("rollback");
    }
    let count = |connection: &Connection| -> i64 {
        connection
            .query_row("SELECT count(*) FROM fts_docs WHERE fts_docs MATCH 'needle'", [], |row| {
                row.get(0)
            })
            .expect("search")
    };
    assert_eq!(count(&connection), 2, "rollback restores both terms and keys");
    connection.execute("DELETE FROM fts_keys WHERE tenant = 'a'", []).expect("delete");
    assert_eq!(count(&connection), 1, "delete is tenant scoped");
    let row_id: i64 = connection
        .query_row("SELECT rowid FROM fts_docs", [], |row| row.get(0))
        .expect("other tenant survives");
    assert_eq!(row_id, 99);
}

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

#[test]
fn identifier_lookup_index_is_installed_on_existing_databases() {
    let (dir, engine) = open_sqlite_engine("identifier-index");
    drop(engine);
    let path = dir.join("terrabase.sqlite3");
    let connection = Connection::open(&path).expect("database");
    connection.execute_batch("DROP INDEX documents_id").expect("simulate old schema");
    drop(connection);

    let engine = reopen_sqlite_engine(&dir);
    let connection = Connection::open(&path).expect("database");
    let mut statement = connection
        .prepare(
            "EXPLAIN QUERY PLAN
             SELECT collection, deleted, expires_at, body, MAX(commit_seq)
                 FROM documents INDEXED BY documents_id
             WHERE tenant = ?1 AND id = ?2 AND commit_seq <= ?3
             GROUP BY collection ORDER BY collection LIMIT 1",
        )
        .expect("lookup plan");
    let details = statement
        .query_map(rusqlite::params!["tenant-a", "absent-schema", 100], |row| {
            row.get::<_, String>(3)
        })
        .expect("explain")
        .collect::<Result<Vec<_>, _>>()
        .expect("plan rows");
    assert!(
        details.iter().any(|detail| detail.contains("tenant=? AND id=?")),
        "identifier lookups must constrain both tenant and id: {details:?}"
    );
    drop(statement);
    drop(connection);
    drop(engine);
    std::fs::remove_dir_all(dir).expect("cleanup");
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
fn paused_lifecycle_rejects_writes_until_resume() {
    let (dir, engine) = open_sqlite_engine("lifecycle-pause");
    let scope = tenant_scope(&engine, "tenant-a");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "before-pause",
        "doc-1",
        "users",
        "value-1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));

    engine.lifecycle_pause(&scope, "pause-1").expect("pause");
    assert_eq!(
        rejected_error(commit_put(
            &engine,
            "tenant-a",
            "while-paused",
            "doc-2",
            "users",
            "value-2",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::StorageFailure
    );
    assert!(read_document(&engine, "tenant-a", "doc-2").is_none());

    engine.lifecycle_resume(&scope, "resume-1").expect("resume");
    committed_sequence(commit_put(
        &engine,
        "tenant-a",
        "after-resume",
        "doc-2",
        "users",
        "value-2",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-2").unwrap().value(),
        &DocumentValue::String("value-2".into())
    );
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(dir).expect("cleanup");
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
fn journal_rows_keep_origin_counters_and_tombstones_across_reopen() {
    let (dir, engine) = open_sqlite_engine("anti-entropy-origin");
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "origin-put",
            "doc-1",
            "orders",
            "one",
            ExpectedVersion::Any,
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));
    assert!(matches!(
        commit_delete(
            &engine,
            "tenant-a",
            "origin-delete",
            "doc-1",
            ExpectedVersion::Any,
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));
    drop(engine);

    let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
    let reopened = SqliteStorage::open(&dir).expect("storage reopens");
    let first = reopened.journal_rows(&tenant, StorageSequence::new(0), 16);
    assert_eq!(first.len(), 2);
    assert!(first.iter().all(|row| !row.origin.is_empty() && row.origin_sequence > 0));
    assert!(first.iter().all(|row| !row.canonical.is_empty()));
    assert_ne!(first[0].origin_sequence, first[1].origin_sequence);
    let second = reopened.journal_rows(&tenant, StorageSequence::new(0), 16);
    assert_eq!(first, second, "reopen preserves journal identities and bytes");
}

#[test]
fn origin_allocation_uses_each_durable_source_max_without_cross_scope_rows() {
    let (dir, mut storage) = sqlite_store("origin-allocation-max-sources");
    stored_sequence(&commit(
        &mut storage,
        "seed-origin",
        vec![put(document("seed", "users", "seed"), ExpectedVersion::New)],
    ));
    let local_origin: String = storage
        .raw_writer_connection()
        .query_row("SELECT value FROM meta WHERE key = 'local_origin'", [], |row| row.get(0))
        .expect("local origin");
    {
        let writer = storage.raw_writer_connection();
        writer
            .execute(
                "INSERT INTO replication_publications
                 (tenant, origin, origin_seq, hlc, operation, local_seq, committed_at, context, private_payload)
                 VALUES ('tenant-a', 'other-origin', 100, 100, 'other', 701, 0, X'', X'')",
                [],
            )
            .expect("other origin frontier");
        writer
            .execute(
                "INSERT INTO replication_publications
                 (tenant, origin, origin_seq, hlc, operation, local_seq, committed_at, context, private_payload)
                 VALUES ('other-tenant', ?1, 100, 100, 'other-tenant', 702, 0, X'', X'')",
                 [&local_origin],
            )
            .expect("other tenant frontier");
        writer
            .execute(
                "UPDATE documents SET origin_seq = 10 WHERE tenant = 'tenant-a' AND origin = ?1",
                [&local_origin],
            )
            .expect("document frontier");
    }
    let document_sequence = stored_sequence(&commit(
        &mut storage,
        "after-document-max",
        vec![put(document("after", "users", "after"), ExpectedVersion::New)],
    ));
    let row = storage
        .journal_rows(&tenant(), StorageSequence::new(0), 16)
        .into_iter()
        .find(|row| row.commit_sequence == document_sequence)
        .expect("post-frontier journal row");
    assert_eq!(row.origin, local_origin);
    assert_eq!(row.origin_sequence, 11, "document maximum allocates the next sequence");
    storage
        .raw_writer_connection()
        .execute(
            "INSERT INTO origin_prefixes (tenant, origin, seq, hlc) VALUES ('tenant-a', ?1, 20, 20)",
            [&local_origin],
        )
        .expect("prefix frontier");
    let prefix_sequence = stored_sequence(&commit(
        &mut storage,
        "after-prefix-max",
        vec![put(document("prefix", "users", "prefix"), ExpectedVersion::New)],
    ));
    let row = storage
        .journal_rows(&tenant(), StorageSequence::new(0), 16)
        .into_iter()
        .find(|row| row.commit_sequence == prefix_sequence)
        .expect("prefix journal row");
    assert_eq!(row.origin_sequence, 21, "prefix maximum allocates the next sequence");
    storage
        .raw_writer_connection()
        .execute(
            "INSERT INTO replication_publications
             (tenant, origin, origin_seq, hlc, operation, local_seq, committed_at, context, private_payload)
             VALUES ('tenant-a', ?1, 30, 30, 'replicated', 700, 0, X'', X'')",
            [&local_origin],
        )
        .expect("replication frontier");
    let publication_sequence = stored_sequence(&commit(
        &mut storage,
        "after-publication-max",
        vec![put(document("publication", "users", "publication"), ExpectedVersion::New)],
    ));
    let row = storage
        .journal_rows(&tenant(), StorageSequence::new(0), 16)
        .into_iter()
        .find(|row| row.commit_sequence == publication_sequence)
        .expect("publication journal row");
    assert_eq!(row.origin_sequence, 31, "publication maximum allocates the next sequence");
    drop(storage);
    std::fs::remove_dir_all(dir).ok();

    let (empty_dir, mut empty_storage) = sqlite_store("origin-allocation-max-empty");
    let empty_sequence = stored_sequence(&commit(
        &mut empty_storage,
        "empty-origin",
        vec![put(document("empty", "users", "empty"), ExpectedVersion::New)],
    ));
    let row = empty_storage
        .journal_rows(&tenant(), StorageSequence::new(0), 16)
        .into_iter()
        .find(|row| row.commit_sequence == empty_sequence)
        .expect("empty journal row");
    assert_eq!(row.origin_sequence, 1, "empty sources start at sequence one");
    drop(empty_storage);
    std::fs::remove_dir_all(empty_dir).ok();
}

#[test]
fn migration_backfills_local_origin_counter_without_global_identity() {
    let (dir, engine) = open_sqlite_engine("anti-entropy-migration");
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "migration-put",
            "doc-1",
            "orders",
            "one",
            ExpectedVersion::Any,
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));
    drop(engine);
    let storage = SqliteStorage::open(&dir).expect("storage opens");
    storage
        .raw_writer_connection()
        .execute("UPDATE documents SET origin = '', origin_seq = 0", [])
        .expect("legacy row shape is writable");
    drop(storage);

    let reopened = SqliteStorage::open(&dir).expect("migration reopens");
    let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
    let rows = reopened.journal_rows(&tenant, StorageSequence::new(0), 16);
    assert_eq!(rows.len(), 1);
    assert!(!rows[0].origin.is_empty());
    assert_eq!(rows[0].origin_sequence, 1);
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
        storage.port.journaled_high_water().unwrap().map(StorageSequence::get),
        Some(1),
        "the faulted commit left no journal record"
    );
    assert_eq!(storage.port.published_state().0.get(), 1, "the sequence watermark did not move");
    drop(storage);
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn post_commit_sync_failure_is_unknown_and_replays_committed_receipt() {
    let (dir, engine) = open_sqlite_engine("post-commit-sync-failure");
    let scope = tenant_scope(&engine, "tenant-a");
    engine.arm_storage_fault(FaultPoint::PostCommitSync);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-post-sync",
        "doc-1",
        "users",
        "committed",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    assert!(matches!(
        outcome,
        CommitOutcome::Unknown(ref unknown)
            if unknown.reason() == crate::UnknownReason::PostCommitSync
    ));
    let resolved = engine.resolve_commit(&scope, operation("op-post-sync")).expect("resolves");
    let sequence = match resolved {
        DurableResolution::Committed(receipt) => receipt.sequence().get(),
        other => panic!("expected committed resolution, got {other:?}"),
    };
    let retry = commit_put(
        &engine,
        "tenant-a",
        "op-post-sync",
        "doc-1",
        "users",
        "committed",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    assert_eq!(committed_sequence(retry), sequence, "retry replays exactly once");
    drop(engine);
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
fn no_row_operation_replays_exactly_after_restart_and_later_writes() {
    let (dir, mut storage) = sqlite_store("operation-facts-restart");
    let request = intent(
        "delete-missing",
        vec![delete(None, "never-present", ExpectedVersion::Any)],
        LocalDurability::Synced,
    );
    let first = storage.commit(request.clone());
    assert_eq!(stored_sequence(&first), 1);
    {
        let connection = storage.raw_writer_connection();
        let fact: (i64, i64, i64, String) = connection
            .query_row(
                "SELECT sequence, writes, bytes, client_request_ulid
                 FROM operation_facts WHERE tenant = 'tenant-a' AND client_request_ulid = 'delete-missing'",
                [],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
            )
            .expect("operation fact persists");
        assert_eq!(fact.0, 1);
        assert_eq!(fact.1, 1, "a no-row delete is one billed write");
        assert_eq!(fact.2, 0, "a no-row delete has no body bytes");
        assert_eq!(fact.3, "delete-missing");
    }
    drop(storage);
    let mut reopened = SqliteStorage::open(&dir).expect("reopens");
    let next = commit(
        &mut reopened,
        "after-delete",
        vec![put(document("present", "users", "value"), ExpectedVersion::New)],
    );
    assert_eq!(stored_sequence(&next), 2);
    let replay = reopened.commit(request);
    assert_eq!(replay, first, "restart and later writes do not alter the receipt");
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn sequence_only_delete_is_durable_across_reopen() {
    let (dir, engine) = open_sqlite_engine("sequence-only-delete");
    let first = committed_sequence(commit_delete(
        &engine,
        "tenant-a",
        "op-missing-delete",
        "missing",
        ExpectedVersion::Any,
        LocalDurability::Synced,
    ));
    assert_eq!(first, 1, "the acknowledged no-row delete publishes sequence one");
    {
        let storage = engine.storage.lock().expect("storage lock");
        assert_eq!(storage.port.published_state().0.get(), first);
        assert_eq!(storage.port.metering_fact("tenant-a").expect("metering fact").seq_hi(), first);
        assert_eq!(
            storage.port.journaled_high_water().unwrap(),
            Some(StorageSequence::new(first)),
            "the durable no-row publication still covers the acknowledged backup cut"
        );
    }
    drop(engine);

    let reopened = reopen_sqlite_engine(&dir);
    let second = committed_sequence(commit_put(
        &reopened,
        "tenant-a",
        "op-after-missing-delete",
        "present",
        "users",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(second, 2, "the next commit cannot reuse the sequence-only publication");
    let storage = reopened.storage.lock().expect("storage lock");
    assert_eq!(storage.port.published_state().0.get(), second);
    assert_eq!(storage.port.journaled_high_water().unwrap(), Some(StorageSequence::new(second)));
    drop(storage);
    drop(reopened);
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
            .expect("journal read")
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

#[test]
fn row_model_pragmas_and_idle_hook_are_explicit() {
    let (dir, storage) = sqlite_store("row-model-pragmas");
    let connection = storage.raw_writer_connection();
    let columns: Vec<String> = connection
        .prepare("PRAGMA table_xinfo(documents)")
        .expect("document schema")
        .query_map([], |row| row.get(1))
        .expect("document columns")
        .collect::<Result<_, _>>()
        .expect("column names");
    assert!(columns.iter().any(|column| column == "seq"), "D-42 exposes the canonical sequence");
    assert!(columns.iter().any(|column| column == "dot"), "D-42 exposes the immutable dot");
    let autocheckpoint: i64 = connection
        .query_row("PRAGMA wal_autocheckpoint", [], |row| row.get(0))
        .expect("autocheckpoint pragma");
    assert_eq!(autocheckpoint, 0, "checkpoints stay off the commit path");
    let synchronous: i64 = connection
        .query_row("PRAGMA synchronous", [], |row| row.get(0))
        .expect("synchronous pragma");
    assert_eq!(synchronous, 1, "connections use NORMAL as the write baseline");
    drop(connection);
    assert!(!storage.close_idle(Duration::from_mins(1)), "a newly opened file is not idle");
    std::thread::sleep(Duration::from_millis(2));
    assert!(storage.close_idle(Duration::from_millis(1)), "idle hook reports a quiet file");
    drop(storage);
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
        ttl_guard: None,
    }
}

fn intent(operation: &str, writes: Vec<StagedWrite>, durability: LocalDurability) -> CommitIntent {
    let tenant = tenant();
    CommitIntent {
        cross_file_control: None,
        cross_file_dependencies: None,
        cross_file_billing: None,
        generated_primary_file_identity: None,
        digest: write_set_digest(&tenant, &writes),
        tenant,
        operation: OperationId::try_new(operation).expect("valid identifier"),
        snapshot_sequence: StorageSequence::new(0),
        durability,
        usage_admission: None,
        budget_allocation: None,
        internal_maintenance: false,
        shard_copy: false,
        expected_origin_epoch: Some(0),
        expected_uniform_context: None,
        merge_conflict: None,
        replication_fingerprint: None,
        replication_publication: None,
        hlc: 0,
        metadata: Vec::new(),
        schema_envelopes: Vec::new(),
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

fn generated_identity_intent(storage: &SqliteStorage, operation: &str, id: &str) -> CommitIntent {
    let lane = crate::origin_lane::OriginLaneId::parse(storage.local_replication_origin()).unwrap();
    let identity = crate::shard_split::FileShardIdentity {
        database_incarnation: 1,
        shard_id: 1,
        region: crate::regions::RegionCode::GRU,
        origin_lane_incarnation: lane.incarnation(),
    };
    let key = crate::engine::shard_split::file_shard_key(identity.database_incarnation);
    let mut value = intent(
        operation,
        vec![put(document(id, "users", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    );
    value.metadata.push(crate::storage::PrivateMetadataWrite {
        key: key.clone(),
        value: Some(DocumentValue::String(hex::encode(
            crate::engine::shard_split::encode_file_identity(identity),
        ))),
    });
    value.generated_primary_file_identity = Some(key);
    value
}

fn admitted_identity_store(tag: &str) -> (PathBuf, SqliteStorage) {
    let (dir, mut storage) = sqlite_store(tag);
    let identity = generated_identity_intent(&storage, "admission-template", "template")
        .validated_generated_identity()
        .unwrap()
        .unwrap();
    storage.bind_generated_primary_identity(identity).unwrap();
    (dir, storage)
}

#[test]
fn generated_identity_same_snapshot_preserves_customer_and_metadata_occ() {
    let (dir, mut storage) = admitted_identity_store("generated-identity-occ");
    let first = generated_identity_intent(&storage, "first", "first");
    let second = generated_identity_intent(&storage, "second", "second");
    let same_document = generated_identity_intent(&storage, "same-document", "first");
    let mut explicit = generated_identity_intent(&storage, "explicit", "explicit");
    explicit.generated_primary_file_identity = None;
    let key = first.generated_primary_file_identity.clone().unwrap();
    assert_eq!(stored_sequence(&storage.commit(first)), 1);
    assert_eq!(stored_sequence(&storage.commit(second)), 2);
    assert!(matches!(
        storage.commit(same_document),
        CommitResult::Rejected(crate::storage::CommitRejection::Conflict)
    ));
    assert!(matches!(
        storage.commit(explicit),
        CommitResult::Rejected(crate::storage::CommitRejection::Conflict)
    ));
    let rows: i64 = storage
        .raw_writer_connection()
        .query_row(
            "SELECT count(*) FROM family_metadata WHERE tenant=?1 AND record_key=?2",
            rusqlite::params![tenant().as_str(), key],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(rows, 1, "only the first customer commit installs the physical identity");
    let publications = storage.replication_publications(&tenant(), 0, 10).unwrap();
    assert_eq!(publications.len(), 2, "no separate initialization publication");
    let (metadata, _) =
        crate::storage::decode_private_changes(&publications[1].1.private_payload).unwrap();
    assert!(metadata.is_empty(), "equal generated initialization is a no-op");
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn generated_identity_marker_is_validated_and_codec_bound() {
    let (dir, mut storage) = sqlite_store("generated-identity-invalid");
    let value = generated_identity_intent(&storage, "generated", "document");
    assert!(
        matches!(
            storage.commit(value.clone()),
            CommitResult::Rejected(crate::storage::CommitRejection::Conflict)
        ),
        "raw storage has no admitted identity authority"
    );
    storage
        .bind_generated_primary_identity(value.validated_generated_identity().unwrap().unwrap())
        .unwrap();
    let encoded = crate::storage::prepared_codec::encode(&value).unwrap();
    let decoded = crate::storage::prepared_codec::decode(&encoded).unwrap();
    assert_eq!(decoded.generated_primary_file_identity, value.generated_primary_file_identity);
    assert_eq!(crate::storage::prepared_codec::encode(&decoded).unwrap(), encoded);
    let mut legacy: serde_json::Value = serde_json::from_slice(&encoded).unwrap();
    legacy["version"] = 2.into();
    assert!(crate::storage::prepared_codec::decode(&serde_json::to_vec(&legacy).unwrap()).is_err());
    let mut wrong_key = value.clone();
    wrong_key.generated_primary_file_identity = Some("unrelated-key".into());
    assert!(crate::storage::prepared_codec::encode(&wrong_key).is_err());
    assert!(matches!(
        storage.commit(wrong_key),
        CommitResult::Rejected(crate::storage::CommitRejection::Validation)
    ));
    let mut duplicate = value.clone();
    duplicate.metadata.push(duplicate.metadata[0].clone());
    assert!(crate::storage::prepared_codec::encode(&duplicate).is_err());
    assert!(matches!(
        storage.commit(duplicate),
        CommitResult::Rejected(crate::storage::CommitRejection::Validation)
    ));
    let mut foreign = value;
    let mut identity = foreign.validated_generated_identity().unwrap().unwrap();
    identity.origin_lane_incarnation ^= 1;
    foreign.metadata[0].value = Some(DocumentValue::String(hex::encode(
        crate::engine::shard_split::encode_file_identity(identity),
    )));
    assert!(matches!(
        storage.commit(foreign),
        CommitResult::Rejected(crate::storage::CommitRejection::Conflict)
    ));
    assert_eq!(storage.published_state().0.get(), 0);
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn generated_identity_rolls_back_and_rejects_replacement_or_tombstone() {
    let (dir, mut storage) = admitted_identity_store("generated-identity-rollback");
    let value = generated_identity_intent(&storage, "first", "first");
    let key = value.generated_primary_file_identity.clone().unwrap();
    storage.arm_test_fault(FaultPoint::WalAppend);
    assert!(matches!(storage.commit(value.clone()), CommitResult::Rejected(_)));
    assert_eq!(storage.published_state().0.get(), 0);
    assert!(
        storage
            .private_metadata_at(&tenant(), &key, StorageSequence::new(u64::MAX >> 1))
            .unwrap()
            .is_none()
    );
    assert_eq!(stored_sequence(&storage.commit(value)), 1);
    let mut different = generated_identity_intent(&storage, "different", "different");
    let mut identity = different.validated_generated_identity().unwrap().unwrap();
    identity.region = crate::regions::RegionCode::SFO;
    different.metadata[0].value = Some(DocumentValue::String(hex::encode(
        crate::engine::shard_split::encode_file_identity(identity),
    )));
    different.generated_primary_file_identity = None;
    different.snapshot_sequence = storage.published_state().0;
    assert_eq!(stored_sequence(&storage.commit(different)), 2);
    let replacement = generated_identity_intent(&storage, "after-replacement", "after-replacement");
    assert!(matches!(
        storage.commit(replacement),
        CommitResult::Rejected(crate::storage::CommitRejection::Conflict)
    ));
    let mut tombstone = intent("tombstone", Vec::new(), LocalDurability::Synced);
    tombstone.snapshot_sequence = storage.published_state().0;
    tombstone.metadata.push(crate::storage::PrivateMetadataWrite { key: key.clone(), value: None });
    assert_eq!(stored_sequence(&storage.commit(tombstone)), 3);
    let retry = generated_identity_intent(&storage, "after-tombstone", "after-tombstone");
    assert!(matches!(
        storage.commit(retry),
        CommitResult::Rejected(crate::storage::CommitRejection::Conflict)
    ));
    assert_eq!(storage.published_state().0.get(), 3);
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
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

#[test]
fn stale_ttl_expiry_work_item_cannot_delete_a_renewed_document() {
    let (dir, mut storage) = sqlite_store("ttl-renewal-guard");
    let expiry_now = super::unix_millis();
    let expired_at = expiry_now - 1;
    let renewed_until = expiry_now + 3_600_000;
    let expired = Document::new(
        DocumentId::try_new("session").expect("identifier"),
        CollectionName::try_new("sessions").expect("collection"),
        DocumentValue::Object(
            [
                ("value".to_owned(), DocumentValue::String("v1".to_owned())),
                ("expires_at".to_owned(), DocumentValue::Integer(expired_at)),
            ]
            .into_iter()
            .collect(),
        ),
    );
    stored_sequence(&commit(
        &mut storage,
        "ttl-v1",
        vec![StagedWrite::Put { document: expired, expected: ExpectedVersion::New }],
    ));

    // Capture the exact work item before a concurrent writer renews it.
    let key = StoragePort::expired_ttl_keys(&storage, expiry_now, 16)
        .expect("expired work list")
        .into_iter()
        .next()
        .expect("v1 is expired");
    assert_eq!(key.version, 1);
    assert_eq!(key.expires_at, expired_at);

    let renewed = Document::new(
        DocumentId::try_new("session").expect("identifier"),
        CollectionName::try_new("sessions").expect("collection"),
        DocumentValue::Object(
            [
                ("value".to_owned(), DocumentValue::String("v2".to_owned())),
                ("expires_at".to_owned(), DocumentValue::Integer(renewed_until)),
            ]
            .into_iter()
            .collect(),
        ),
    );
    let second = stored_sequence(&commit(
        &mut storage,
        "ttl-v2",
        vec![StagedWrite::Put { document: renewed, expected: ExpectedVersion::New }],
    ));

    // Expiry hides the old row, so renewal is a new put and reuses version 1.
    // Discovery predates renewal, but the sweep snapshot already includes it:
    // neither the public version nor ordinary snapshot conflict detects this race.
    let mut stale = intent(
        "ttl-stale-delete",
        vec![StagedWrite::Delete {
            collection: Some(key.collection.clone()),
            id: key.id.clone(),
            expected: ExpectedVersion::Exact(key.version),
            ttl_guard: Some(TtlGuard { revision: key.revision, expires_at: key.expires_at }),
        }],
        LocalDurability::Written,
    );
    stale.snapshot_sequence = StorageSequence::new(second);
    stale.internal_maintenance = true;
    assert!(matches!(
        storage.commit(stale),
        CommitResult::Rejected(crate::storage::CommitRejection::Conflict)
    ));

    let current = storage
        .read_document_in_collection_at(
            StorageSequence::new(second),
            &tenant(),
            &key.collection,
            &key.id,
        )
        .expect("renewed document survives");
    assert_eq!(current.metadata().version(), key.version, "renewal reuses the public version");
    assert_eq!(
        current.value(),
        &DocumentValue::Object(
            [
                ("value".to_owned(), DocumentValue::String("v2".to_owned())),
                ("expires_at".to_owned(), DocumentValue::Integer(renewed_until)),
            ]
            .into_iter()
            .collect(),
        )
    );
    assert!(
        StoragePort::expired_ttl_keys(&storage, expiry_now, 16)
            .expect("retry work list")
            .is_empty()
    );
    assert_eq!(
        storage
            .document_history_in_collection(&tenant(), &key.collection, &key.id, 16)
            .iter()
            .filter(|entry| entry.deleted)
            .count(),
        0,
        "no TTL tombstone"
    );
    assert_eq!(
        storage.metering_fact("tenant-a").expect("write usage").writes(),
        2,
        "the rejected maintenance retry creates no billed TTL units"
    );

    drop(storage);
    std::fs::remove_dir_all(dir).ok();
}

// ---- SQLite-focused: the journal-pruning floor ---------------------------

fn sqlite_store(tag: &str) -> (PathBuf, SqliteStorage) {
    let dir = location(tag);
    let storage = SqliteStorage::open(&dir).expect("sqlite backend opens");
    (dir, storage)
}

/// A fixture must acknowledge the production-required consumers before its
/// synthetic frontier can exercise the pruning policy.
fn acknowledge_required_frontiers(storage: &mut SqliteStorage) {
    let head = storage.published_state().0.get();
    for name in ["replication", "metering"] {
        storage.advance_consumer_frontier(name, head).expect("required frontier advances");
    }
}

#[test]
fn journal_replay_rejects_corrupt_rows_instead_of_serving_a_partial_commit() {
    for mutation in [
        "body = X'00'",
        "body = 'not a blob'",
        "requested = 99",
        "kind = 'unknown'",
        "origin_seq = -1",
        "origin_seq = origin_seq + 1",
        "commit_ulid = 'different-operation'",
        "context = X'00'",
        "collection = 'mismatched'",
        "id = 'mismatched'",
        "deleted = 2",
        "version = -1",
    ] {
        let (dir, mut storage) = sqlite_store("corrupt-journal-page");
        commit(
            &mut storage,
            "op-page",
            vec![
                put(document("a", "users", "first"), ExpectedVersion::New),
                put(document("z", "users", "last"), ExpectedVersion::New),
            ],
        );
        let tenant = scan_tenant();
        let page = storage.tenant_commits(&tenant, StorageSequence::new(0), 10).unwrap();
        assert_eq!(page.len(), 1);
        assert_eq!(page[0].writes().len(), 2);
        storage.writer.lock().unwrap().execute_batch(&format!(
            "PRAGMA ignore_check_constraints = ON; UPDATE documents SET {mutation} WHERE id = 'z';"
        )).unwrap();
        assert!(
            storage.tenant_commits(&tenant, StorageSequence::new(0), 10).is_err(),
            "corruption must reject the entire page: {mutation}"
        );
        drop(storage);
        std::fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn journal_replay_distinguishes_storage_failure_from_a_drained_journal() {
    let (dir, storage) = sqlite_store("unavailable-journal-page");
    let tenant = scan_tenant();
    assert!(storage.tenant_commits(&tenant, StorageSequence::new(0), 10).unwrap().is_empty());
    assert!(storage.tenant_commits(&tenant, StorageSequence::new(u64::MAX), 10).is_err());
    storage.writer.lock().unwrap().execute_batch("DROP TABLE documents").unwrap();
    assert!(storage.tenant_commits(&tenant, StorageSequence::new(0), 10).is_err());
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn journal_metadata_distinguishes_empty_storage_from_failed_reads() {
    let (dir, engine) = open_sqlite_engine("journal-metadata-errors");
    let tenant = tenant();
    let cursor = StorageSequence::new(0);
    assert_eq!(engine.committed_tenants().unwrap(), Vec::<TenantId>::new());
    assert_eq!(engine.tenant_sequence(&tenant, cursor).unwrap(), None);
    assert_eq!(engine.servable_history_start().unwrap(), Some(StorageSequence::new(0)));
    assert_eq!(engine.journaled_high_water().unwrap(), None);
    assert_eq!(
        engine.tenant_sequence(&tenant, StorageSequence::new(u64::MAX)),
        Err(crate::engine::EngineError::StorageFailure)
    );
    let connection = Connection::open(dir.join("terrabase.sqlite3")).unwrap();
    connection.execute_batch("DROP TABLE documents; DROP TABLE consumer_frontiers").unwrap();
    assert_eq!(engine.committed_tenants(), Err(crate::engine::EngineError::StorageFailure));
    assert_eq!(
        engine.tenant_sequence(&tenant, cursor),
        Err(crate::engine::EngineError::StorageFailure)
    );
    assert_eq!(engine.servable_history_start(), Err(crate::engine::EngineError::StorageFailure));
    assert_eq!(engine.journaled_high_water(), Err(crate::engine::EngineError::StorageFailure));
    drop(connection);
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn journal_high_water_covers_metadata_only_publication_after_reopen() {
    let dir = location("metadata-publication-high-water");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let mut request = intent("metadata-only", vec![], LocalDurability::Synced);
    request.metadata.push(crate::storage::PrivateMetadataWrite {
        key: "catalog:identity".into(),
        value: Some(DocumentValue::String("bound".into())),
    });
    let CommitResult::Committed(receipt) = storage.commit(request) else {
        panic!("metadata publication");
    };
    assert_eq!(storage.journaled_high_water().unwrap(), Some(receipt.sequence));
    drop(storage);
    let storage = SqliteStorage::open(&dir).unwrap();
    assert_eq!(storage.journaled_high_water().unwrap(), Some(receipt.sequence));
}

#[test]
fn journal_metadata_rejects_invalid_tenants_and_negative_frontiers() {
    let (dir, engine) = open_sqlite_engine("invalid-journal-metadata");
    drop(engine);
    let storage = SqliteStorage::open(&dir).unwrap();
    {
        let writer = storage.raw_writer_connection();
        writer.execute_batch(
            "INSERT INTO origin_prefixes (tenant, origin, seq, hlc) VALUES ('', 'region-a', 1, 0);
             INSERT INTO consumer_frontiers (name, seq) VALUES ('broken', -1);"
        ).unwrap();
    }
    assert!(storage.committed_tenants().is_err());
    assert!(storage.servable_history_start().is_err());
    drop(storage);
    assert!(matches!(
        Engine::open(EngineConfig::durable(&dir).unwrap()),
        Err(crate::engine::EngineOpenError::StorageUnavailable)
    ));
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn journal_metadata_failure_reaches_replay_and_subscription_callers() {
    use crate::streaming::{ChangeFilter, ChangeReplayRequest, StreamError, SubscriptionSpec};
    use crate::types::Cancellation;

    let (dir, engine) = open_sqlite_engine("journal-start-errors");
    let scope = tenant_scope(&engine, "tenant-a");
    let filter = ChangeFilter::try_new(None, None).unwrap();
    let request = ChangeReplayRequest::try_new(filter.clone(), None, 10).unwrap();
    let spec = SubscriptionSpec::try_new("history-reader", filter, 10).unwrap().from_beginning();
    // The document journal remains readable. Only its history-bound query
    // fails, so falling back to zero would incorrectly report success.
    let connection = Connection::open(dir.join("terrabase.sqlite3")).unwrap();
    connection.execute_batch("DROP TABLE consumer_frontiers").unwrap();
    assert!(engine.tenant_commits(scope.tenant(), StorageSequence::new(0), 10).unwrap().is_empty());
    assert_eq!(
        engine.streams().replay_changes(&scope, &request, &Cancellation::new()).unwrap_err(),
        StreamError::EngineClosed
    );
    assert_eq!(
        engine.streams().replay_sync_journal(&scope, None, 10, &Cancellation::new()).unwrap_err(),
        StreamError::EngineClosed
    );
    assert_eq!(engine.streams().subscribe(&scope, &spec).unwrap_err(), StreamError::EngineClosed);
    drop(connection);
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
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
    acknowledge_required_frontiers(&mut storage);
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
    assert_eq!(storage.servable_history_start().unwrap().map(StorageSequence::get), Some(2));
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
    acknowledge_required_frontiers(&mut storage);
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
fn collection_history_windows_are_evaluated_independently() {
    let (_dir, mut storage) = sqlite_store("collection-history-windows");
    commit(
        &mut storage,
        "short-window-op",
        vec![put(document("short", "short-lived", "one"), ExpectedVersion::New)],
    );
    commit(
        &mut storage,
        "long-window-op",
        vec![put(document("long", "long-lived", "two"), ExpectedVersion::New)],
    );
    commit(
        &mut storage,
        "replacement-op",
        vec![
            put(document("short", "short-lived", "new"), ExpectedVersion::Exact(1)),
            put(document("long", "long-lived", "new"), ExpectedVersion::Exact(1)),
        ],
    );
    acknowledge_required_frontiers(&mut storage);
    storage.advance_consumer_frontier("solo", 3).expect("frontier advances");
    {
        let connection = storage.raw_writer_connection();
        connection
            .execute(
                "INSERT INTO collection_history (collection, days) VALUES ('short-lived', 1)",
                [],
            )
            .expect("short history declaration");
        connection
            .execute(
                "UPDATE documents SET committed_at = ?1",
                [super::unix_millis() - 2 * 24 * 60 * 60 * 1000],
            )
            .expect("rows backdate");
    }
    assert_eq!(storage.prune_below_consumer_frontiers().expect("prune runs"), 1);
    assert_eq!(storage.journal_records(), 2, "the long-window history and current rows remain");
}

#[test]
fn pruning_preserves_complete_commits_across_history_windows_and_reopen() {
    let (dir, mut storage) = sqlite_store("prune-mixed-windows");
    for version in 1..=2 {
        let expected = if version == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(1) };
        let result = commit(
            &mut storage,
            &format!("mixed-{version}"),
            vec![
                put(document("short", "short-lived", "value"), expected),
                put(document("long", "long-lived", "value"), expected),
            ],
        );
        assert_eq!(stored_sequence(&result), version);
    }
    acknowledge_required_frontiers(&mut storage);
    storage.advance_consumer_frontier("solo", 3).unwrap();
    {
        let writer = storage.raw_writer_connection();
        writer
            .execute(
                "INSERT INTO collection_history (collection, days) VALUES ('short-lived', 1)",
                [],
            )
            .unwrap();
        writer
            .execute(
                "UPDATE documents SET committed_at = ?1",
                [super::unix_millis() - 2 * 24 * 60 * 60 * 1000],
            )
            .unwrap();
    }
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 0);
    let page = storage.tenant_commits(&tenant(), StorageSequence::new(0), 10).unwrap();
    assert_eq!(page.len(), 2);
    assert!(page.iter().all(|commit| commit.writes().len() == 2));
    backdate_all_rows(&storage);
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 2);
    drop(storage);
    let storage = SqliteStorage::open(&dir).unwrap();
    assert_eq!(
        storage.tenant_commits(&tenant(), StorageSequence::new(0), 10),
        Err(crate::storage::JournalReadError::HistoryExpired)
    );
    let page = storage.tenant_commits(&tenant(), StorageSequence::new(1), 10).unwrap();
    assert_eq!(page.len(), 1);
    assert_eq!(page[0].sequence().get(), 2);
    assert_eq!(page[0].writes().len(), 2);
    for id in ["short", "long"] {
        let current = storage
            .read_document_at(
                storage.published_state().0,
                &tenant(),
                &DocumentId::try_new(id).unwrap(),
            )
            .unwrap();
        assert_eq!(current.metadata().version(), 2);
    }
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn journal_pruning_watermark_is_atomic_durable_and_tenant_scoped() {
    use crate::storage::JournalReadError;
    use crate::streaming::{ChangeCheckpoint, StreamError};
    use crate::types::Cancellation;

    let (dir, engine) = open_sqlite_engine("journal-pruning-watermark");
    drop(engine);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    assert_eq!(
        stored_sequence(&commit(
            &mut storage,
            "first",
            vec![put(document("a", "users", "old"), ExpectedVersion::New)]
        )),
        1
    );
    let mut other = fresh_intent(
        &mut storage,
        "other",
        vec![put(document("b", "users", "kept"), ExpectedVersion::New)],
        LocalDurability::Written,
    );
    other.tenant = TenantId::try_new("tenant-b").unwrap();
    other.digest = write_set_digest(&other.tenant, &other.writes);
    assert_eq!(stored_sequence(&storage.commit(other)), 2);
    assert_eq!(
        stored_sequence(&commit(
            &mut storage,
            "replacement",
            vec![put(document("a", "users", "new"), ExpectedVersion::Exact(1))]
        )),
        3
    );
    acknowledge_required_frontiers(&mut storage);
    storage.advance_consumer_frontier("consumer", 3).unwrap();
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 0);
    assert_eq!(
        storage.tenant_commits(&tenant(), StorageSequence::new(0), 10).unwrap().len(),
        2,
        "advancing consumers alone does not expire replay"
    );
    backdate_all_rows(&storage);
    storage.raw_writer_connection().execute_batch(
        "CREATE TRIGGER refuse_watermark BEFORE INSERT ON journal_pruning BEGIN SELECT RAISE(ABORT, 'fault'); END;"
    ).unwrap();
    assert!(storage.prune_below_consumer_frontiers().is_err());
    assert_eq!(
        storage.tenant_commits(&tenant(), StorageSequence::new(0), 10).unwrap().len(),
        2,
        "failed watermark persistence rolls back deletion"
    );
    storage.raw_writer_connection().execute_batch("DROP TRIGGER refuse_watermark").unwrap();
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 1);
    assert_eq!(
        storage.tenant_commits(&tenant(), StorageSequence::new(0), 10),
        Err(JournalReadError::HistoryExpired)
    );
    assert_eq!(
        storage
            .tenant_commits(&TenantId::try_new("tenant-b").unwrap(), StorageSequence::new(0), 10)
            .unwrap()
            .len(),
        1
    );
    assert_eq!(
        storage.tenant_commits(&tenant(), StorageSequence::new(1), 10).unwrap()[0].sequence().get(),
        3,
        "legitimate sequence holes do not expire replay"
    );
    drop(storage);
    let engine = reopen_sqlite_engine(&dir);
    let scope = tenant_scope(&engine, "tenant-a");
    assert_eq!(
        engine.tenant_commits(scope.tenant(), StorageSequence::new(0), 10),
        Err(crate::engine::EngineError::HistoryExpired)
    );
    let checkpoint = ChangeCheckpoint::after(0, 0);
    assert_eq!(
        engine
            .streams()
            .replay_sync_journal(&scope, Some(checkpoint), 10, &Cancellation::new())
            .unwrap_err(),
        StreamError::HistoryExpired
    );
    drop(engine);
    // Simulate a file last opened before deletion watermarks existed.
    let connection = Connection::open(dir.join("terrabase.sqlite3")).unwrap();
    connection.execute_batch("DROP TABLE journal_pruning").unwrap();
    drop(connection);
    let engine = reopen_sqlite_engine(&dir);
    assert_eq!(
        engine.tenant_commits(&tenant(), StorageSequence::new(1), 10),
        Err(crate::engine::EngineError::HistoryExpired),
        "legacy recovery refuses the potentially pruned prefix"
    );
    assert_eq!(
        engine.tenant_commits(&tenant(), StorageSequence::new(2), 10).unwrap()[0].sequence().get(),
        3
    );
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn retained_publication_proofs_survive_later_journal_gaps() {
    let (dir, engine) = open_sqlite_engine("retained-publication-proof");
    drop(engine);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    commit(
        &mut storage,
        "pinned",
        vec![put(document("pinned", "users", "value"), ExpectedVersion::New)],
    );
    commit(&mut storage, "old", vec![put(document("hot", "users", "old"), ExpectedVersion::New)]);
    commit(
        &mut storage,
        "new",
        vec![put(document("hot", "users", "new"), ExpectedVersion::Exact(1))],
    );
    acknowledge_required_frontiers(&mut storage);
    storage.advance_consumer_frontier("consumer", 3).unwrap();
    backdate_all_rows(&storage);
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 1);
    assert!(storage.tenant_commits(&tenant(), StorageSequence::new(0), 10).is_err());
    assert!(storage.retained_commit(&tenant(), StorageSequence::new(1)).unwrap().is_some());
    assert!(storage.retained_commit(&tenant(), StorageSequence::new(2)).unwrap().is_none());
    drop(storage);
    let engine = reopen_sqlite_engine(&dir);
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let transaction =
        engine.begin(&scope, snapshot, OperationId::try_new("proof-read").unwrap()).unwrap();
    let head = transaction
        .head(
            &CollectionName::try_new("users").unwrap(),
            &DocumentId::try_new("pinned").unwrap(),
            crate::engine::HeadCondition::Any,
        )
        .unwrap();
    assert!(
        matches!(head, crate::engine::DocumentHead::Current(contract) if contract.sequence().get() == 1)
    );
    drop(transaction);
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
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
    acknowledge_required_frontiers(&mut storage);
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
    storage.advance_consumer_frontier("fast", 0).expect("frontier registers");
    storage.advance_consumer_frontier("slow", 0).expect("frontier registers");
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
        acknowledge_required_frontiers(&mut storage);
        storage.advance_consumer_frontier("fast", head).expect("frontier advances");
        storage.advance_consumer_frontier("slow", head).expect("frontier advances");
        backdate_all_rows(&storage);
        storage.prune_below_consumer_frontiers().expect("prune runs");
    }
    assert!(
        storage.journal_records() <= 2,
        "an advancing consumer fleet bounds the journal, found {}",
        storage.journal_records()
    );
    assert_eq!(storage.servable_history_start().unwrap().map(StorageSequence::get), Some(50));

    // A consumer registered behind a new head pins every row it still needs.
    commit(
        &mut storage,
        "op-51",
        vec![put(document("hot", "users", "v51"), ExpectedVersion::Exact(50))],
    );
    storage.advance_consumer_frontier("lagging", 50).expect("frontier registers at the head");
    acknowledge_required_frontiers(&mut storage);
    storage.advance_consumer_frontier("fast", 51).expect("frontier advances");
    storage.advance_consumer_frontier("slow", 51).expect("frontier advances");
    storage.prune_below_consumer_frontiers().expect("prune runs");
    assert_eq!(storage.servable_history_start().unwrap().map(StorageSequence::get), Some(50));
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
    acknowledge_required_frontiers(&mut storage);
    backdate_all_rows(&storage);
    storage.advance_consumer_frontier("solo", 3).expect("frontier advances");
    storage.run_retention_sweep().expect("sweep runs");
    assert_eq!(storage.journal_records(), 1, "the sweep pruned below the floor");
    assert_eq!(
        storage.servable_history_start().unwrap(),
        Some(StorageSequence::new(3)),
        "the floor is the servable bound"
    );
}

#[test]
fn required_frontiers_are_registered_before_retention_can_prune() {
    let (_dir, mut storage) = sqlite_store("required-frontiers");
    commit(&mut storage, "op-1", vec![put(document("doc", "users", "v1"), ExpectedVersion::New)]);
    commit(
        &mut storage,
        "op-2",
        vec![put(document("doc", "users", "v2"), ExpectedVersion::Exact(1))],
    );
    backdate_all_rows(&storage);

    // The production-required consumers are admitted at sequence zero. A
    // backup acknowledgement alone cannot move the minimum floor past zero.
    storage.advance_backup_frontier(2).expect("backup frontier advances");
    assert_eq!(storage.prune_below_consumer_frontiers().expect("prune runs"), 0);

    for name in ["replication", "sync", "metering"] {
        storage.advance_consumer_frontier(name, 2).expect("required frontier advances");
    }
    assert_eq!(storage.prune_below_consumer_frontiers().expect("prune runs"), 1);
    assert_eq!(storage.journal_records(), 1, "the newest row remains the live-state pin");
}

#[test]
fn retention_frontier_rejects_sequences_sqlite_cannot_represent() {
    let (_dir, mut storage) = sqlite_store("frontier-sequence-range");
    let maximum = i64::MAX as u64;
    storage
        .advance_consumer_frontier("replication", maximum)
        .expect("the SQLite maximum is representable");
    assert!(storage.advance_consumer_frontier("replication", maximum + 1).is_err());
    let stored: i64 = storage
        .raw_writer_connection()
        .query_row("SELECT seq FROM consumer_frontiers WHERE name = 'replication'", [], |row| {
            row.get(0)
        })
        .expect("frontier remains durable");
    assert_eq!(stored, i64::MAX);
}

#[test]
fn raw_reopened_location_retains_process_owned_sync_lease_for_engine_admission() {
    let (dir, mut storage) = sqlite_store("sync-lease-restart");
    storage.register_consumer_frontier("sync", 7).expect("registers sync lease");
    drop(storage);

    let reopened = SqliteStorage::open(&dir).expect("reopens location");
    let sync_rows: i64 = reopened
        .raw_writer_connection()
        .query_row("SELECT COUNT(*) FROM consumer_frontiers WHERE name = 'sync'", [], |row| {
            row.get(0)
        })
        .expect("reads consumer leases");
    assert_eq!(sync_rows, 1, "raw storage does not own Engine sync lease retirement");
}

#[test]
fn native_backup_lease_is_absent_until_a_stream_is_admitted() {
    let (dir, mut storage) = sqlite_store("native-backup-lease");
    let initial_rows: i64 = storage
        .raw_writer_connection()
        .query_row(
            "SELECT COUNT(*) FROM consumer_frontiers WHERE name = 'native-backup'",
            [],
            |row| row.get(0),
        )
        .expect("reads native backup lease");
    assert_eq!(initial_rows, 0, "an unadmitted backup must not pin at sequence zero");

    // Simulate a process that admitted a stream and died before retiring it.
    storage.register_consumer_frontier("native-backup", 0).expect("registers backup lease");
    drop(storage);

    let reopened = SqliteStorage::open(&dir).expect("reopens location");
    let rows: i64 = reopened
        .raw_writer_connection()
        .query_row(
            "SELECT COUNT(*) FROM consumer_frontiers WHERE name = 'native-backup'",
            [],
            |row| row.get(0),
        )
        .expect("reads native backup lease after restart");
    assert_eq!(rows, 0, "stale process-owned backup lease cannot pin pruning");
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
fn every_new_row_persists_a_versioned_causal_context_across_restart() {
    let (dir, mut storage) = sqlite_store("causal-context-restart");
    commit(
        &mut storage,
        "context-op-1",
        vec![put(document("context-1", "users", "one"), ExpectedVersion::New)],
    );
    commit(
        &mut storage,
        "context-op-2",
        vec![put(document("context-2", "users", "two"), ExpectedVersion::New)],
    );
    let before: Vec<Vec<u8>> = {
        let connection = storage.raw_writer_connection();
        let mut statement = connection
            .prepare("SELECT context FROM documents ORDER BY commit_seq")
            .expect("context column");
        statement
            .query_map([], |row| row.get::<_, Vec<u8>>(0))
            .expect("context rows")
            .collect::<Result<Vec<_>, _>>()
            .expect("context bytes")
    };
    assert_eq!(before.len(), 2);
    assert!(before.iter().all(|context| context.first() == Some(&1)));
    assert!(before.iter().all(|context| context.len() >= 5));
    assert_ne!(before[0], before[1], "the second commit observes the first dot");
    drop(storage);

    let reopened = SqliteStorage::open(&dir).expect("context restart");
    let after: Vec<Vec<u8>> = {
        let connection = reopened.raw_writer_connection();
        let mut statement = connection
            .prepare("SELECT context FROM documents ORDER BY commit_seq")
            .expect("context column after restart");
        statement
            .query_map([], |row| row.get::<_, Vec<u8>>(0))
            .expect("context rows after restart")
            .collect::<Result<Vec<_>, _>>()
            .expect("context bytes after restart")
    };
    assert_eq!(after, before, "restart must not reconstruct a different causal cut");
    std::fs::remove_dir_all(&dir).ok();
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

#[test]
fn replacing_an_owned_sqlite_file_is_rejected_without_mutation() {
    let (dir, engine) = open_sqlite_engine("replacement-binding");
    drop(engine);
    let database = super::database_file(&dir);
    std::fs::remove_file(&database).expect("replace the database file");
    let foreign = Connection::open(&database).expect("foreign sqlite file");
    foreign.execute("CREATE TABLE foreign_data (value TEXT)", []).expect("foreign schema");
    drop(foreign);
    let before = std::fs::read(&database).expect("foreign bytes");

    assert_eq!(SqliteStorage::open(&dir).err(), Some(StorageOpenError::Unavailable));
    assert_eq!(std::fs::read(&database).expect("foreign bytes remain"), before);
    assert!(!database.with_extension("sqlite3-wal").exists(), "rejection did not create a WAL");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn authentic_legacy_sqlite_core_schema_is_bound_during_upgrade() {
    let (dir, engine) = open_sqlite_engine("legacy-binding");
    drop(engine);
    let database = super::database_file(&dir);
    let connection = Connection::open(&database).expect("legacy sqlite file");
    connection
        .execute("DELETE FROM meta WHERE key = ?1", [super::SCHEMA_BINDING_KEY])
        .expect("remove the pre-binding marker");
    drop(connection);

    let storage = SqliteStorage::open(&dir).expect("legacy schema migrates");
    let bound: String = storage
        .raw_writer_connection()
        .query_row("SELECT value FROM meta WHERE key = ?1", [super::SCHEMA_BINDING_KEY], |row| {
            row.get(0)
        })
        .expect("migration writes the binding");
    assert!(bound.starts_with("owner-"), "the upgraded file is owner-bound");
    drop(storage);
    std::fs::remove_dir_all(&dir).ok();
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

// ---- E-9: metering facts, cursor export, chaos reconcile (D-16/D-42) -----

use crate::storage::billed_writes_for_bytes;

/// Commits one write set as the named tenant through the storage port.
fn commit_as(
    storage: &mut dyn StoragePort,
    tenant_name: &str,
    operation: &str,
    writes: Vec<StagedWrite>,
) -> CommitResult {
    let tenant = TenantId::try_new(tenant_name).expect("valid tenant");
    let intent = CommitIntent {
        cross_file_control: None,
        cross_file_dependencies: None,
        cross_file_billing: None,
        generated_primary_file_identity: None,
        digest: write_set_digest(&tenant, &writes),
        tenant,
        operation: OperationId::try_new(operation).expect("valid identifier"),
        snapshot_sequence: storage.published_state().0,
        durability: LocalDurability::Written,
        usage_admission: None,
        budget_allocation: None,
        internal_maintenance: false,
        shard_copy: false,
        expected_origin_epoch: Some(0),
        expected_uniform_context: None,
        merge_conflict: None,
        replication_fingerprint: None,
        replication_publication: None,
        hlc: 0,
        metadata: Vec::new(),
        schema_envelopes: Vec::new(),
        writes,
    };
    storage.commit(intent)
}

fn assert_metering_unit_rule() {
    // The D-16 unit rule first, as a pure function with no storage.
    assert_eq!(billed_writes_for_bytes(0), 1, "a tombstone bills its one delete");
    assert_eq!(billed_writes_for_bytes(1), 1, "a small body bills one write");
    assert_eq!(billed_writes_for_bytes(4096), 1, "exactly 4 KiB bills one write");
    assert_eq!(billed_writes_for_bytes(4097), 2, "one byte over bills two writes");
}

fn assert_metering_retries_and_large_write(
    storage: &mut dyn StoragePort,
) -> (DbUsageFact, DbUsageFact) {
    // One small put flushes one (db, seq_hi, writes, bytes) fact per wave.
    let first = stored_sequence(&commit(
        storage,
        "op-meter-1",
        vec![put(document("doc", "users", "value"), ExpectedVersion::New)],
    ));
    let fact = storage.metering_fact("tenant-a").expect("the wave flushed the fact");
    assert_eq!(fact.db(), "tenant-a", "the fact names its database");
    assert_eq!(fact.seq_hi(), first, "the fact covers the wave's sequence");
    assert_eq!(fact.writes(), 1, "one small put bills one write");
    assert!(fact.bytes() > 0, "bytes carry the stored body");

    // RETRIES: the same operation identity replays its receipt and bills
    // nothing twice — exactly-once billing under client retries.
    let retry = commit(
        storage,
        "op-meter-1",
        vec![put(document("doc", "users", "value"), ExpectedVersion::New)],
    );
    assert_eq!(stored_sequence(&retry), first, "the retry replays the original receipt");
    assert_eq!(
        storage.metering_fact("tenant-a").expect("fact"),
        fact,
        "the retry bills nothing twice"
    );

    // A body over 4 KiB bills two write units through the same wave flush.
    let big = "x".repeat(5000);
    commit(
        storage,
        "op-meter-big",
        vec![put(document("big", "users", &big), ExpectedVersion::New)],
    );
    let grown = storage.metering_fact("tenant-a").expect("fact");
    assert_eq!(grown.writes(), fact.writes() + 2, "5 KiB bills two write units");
    (fact, grown)
}

fn assert_metering_split_and_cursor(storage: &mut dyn StoragePort, grown: &DbUsageFact) {
    // SPLITS: a second database's rows bill their own fact (E-11 moves key
    // ranges with their rows; per-database facts stay isolated either way).
    commit_as(
        storage,
        "tenant-b",
        "op-b-1",
        vec![put(document("doc", "users", "other"), ExpectedVersion::New)],
    );
    commit_as(
        storage,
        "tenant-b",
        "op-b-2",
        vec![delete(Some("users"), "doc", ExpectedVersion::Exact(1))],
    );
    let fact_b = storage.metering_fact("tenant-b").expect("tenant-b bills its own fact");
    assert_eq!(fact_b.writes(), 2, "put plus tombstone bill two writes");
    assert!(fact_b.bytes() > 0, "the tombstone adds no bytes to the put's");
    assert_eq!(
        storage.metering_fact("tenant-a").expect("fact").writes(),
        grown.writes(),
        "tenant-b rows never move tenant-a"
    );

    // CURSOR: the export walks per-database facts in db order.
    let (first_page, cursor) = storage.metering_facts_page(None, 1);
    assert_eq!(first_page.len(), 1, "the page honors its limit");
    assert_eq!(first_page[0].db(), "tenant-a", "pages run in db order");
    let cursor = cursor.expect("one more database follows");
    let (second_page, end) = storage.metering_facts_page(Some(&cursor), 1);
    assert_eq!(second_page.len(), 1, "the cursor resumes after tenant-a");
    assert_eq!(second_page[0].db(), "tenant-b", "the second page carries tenant-b");
    assert_eq!(end, None, "the cursor ends after the last database");
}

fn assert_metering_reads_and_live_bytes(storage: &mut dyn StoragePort, grown: &DbUsageFact) {
    // READ COUNTERS: served reads meter for guards and attribution only.
    let reads_before = storage.read_count("tenant-a");
    let snapshot = storage.published_state().0;
    let _ = storage.read_document_at(
        snapshot,
        &tenant(),
        &DocumentId::try_new("doc").expect("identifier"),
    );
    let _ = storage.documents_at(snapshot, &tenant());
    assert_eq!(
        storage.read_count("tenant-a"),
        reads_before + 2,
        "two served reads counted for guards"
    );
    assert_eq!(
        storage.metering_fact("tenant-a").expect("fact").writes(),
        grown.writes(),
        "reads never bill"
    );

    // LIVE BYTES: the D-16 storage snapshot counts live documents only —
    // retained history bills through the fact's bytes, never here.
    let live_a = storage.metering_live_bytes("tenant-a");
    assert!(live_a > 0, "live documents carry storage bytes");
    assert!(
        live_a <= storage.metering_fact("tenant-a").expect("fact").bytes(),
        "live bytes never exceed retained bytes"
    );
    assert_eq!(
        storage.metering_live_bytes("tenant-b"),
        0,
        "the tombstoned key carries no live bytes"
    );
}

fn assert_metering_restart_reconciles(dir: &Path, storage: SqliteStorage) {
    // RESTARTS: a torn counter reconciles from the journal on open.
    let expected_a = storage.metering_fact("tenant-a").expect("fact");
    let expected_b = storage.metering_fact("tenant-b").expect("fact");
    {
        let connection = storage.raw_writer_connection();
        connection
            .execute("UPDATE metering_db_facts SET seq_hi = 0, writes = 0, bytes = 0", [])
            .expect("tamper lands");
    }
    drop(storage);
    let reopened = SqliteStorage::open(dir).expect("reopens");
    assert_eq!(
        reopened.metering_fact("tenant-a").expect("reconciled"),
        expected_a,
        "open reconciles tenant-a from the journal"
    );
    assert_eq!(
        reopened.metering_fact("tenant-b").expect("reconciled"),
        expected_b,
        "open reconciles tenant-b from the journal"
    );
    drop(reopened);
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn metering_chaos_retries_splits_restarts_reconcile() {
    assert_metering_unit_rule();
    let (dir, mut storage) = sqlite_store("metering-chaos");
    let (_, grown) = assert_metering_retries_and_large_write(&mut storage);
    assert_metering_split_and_cursor(&mut storage, &grown);
    assert_metering_reads_and_live_bytes(&mut storage, &grown);
    assert_metering_restart_reconciles(&dir, storage);
}

#[test]
fn metering_engine_export_seam_is_tenant_isolated() {
    let (dir, engine) = open_sqlite_engine("metering-engine");
    commit_put(
        &engine,
        "tenant-a",
        "op-eng-1",
        "doc-1",
        "users",
        "value",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    let scope_a = tenant_scope(&engine, "tenant-a");
    let scope_b = tenant_scope(&engine, "tenant-b");
    let fact = engine.metering_fact(&scope_a, "tenant-a").expect("the caller reads its own fact");
    assert_eq!(fact.writes(), 1, "one small put bills one write");
    assert_eq!(engine.metering_fact(&scope_a, "tenant-b"), None, "cross-tenant fact reads refuse");
    assert_eq!(
        engine.metering_fact(&scope_b, "tenant-b"),
        None,
        "a database with no rows has no fact"
    );
    let (page, next) = engine.metering_facts_page(&scope_a, None, 10);
    assert_eq!(page.len(), 1, "the isolated page carries the own fact");
    assert_eq!(page[0].db(), "tenant-a", "isolation holds through the cursor");
    assert_eq!(next, None, "nothing follows the single isolated fact");
    let (empty, _) = engine.metering_facts_page(&scope_b, None, 10);
    assert!(empty.is_empty(), "the empty database pages empty");
    assert!(engine.metering_live_bytes(&scope_a) > 0, "live bytes serve through the seam");
    let _ = read_document(&engine, "tenant-a", "doc-1");
    assert!(
        engine.metering_read_count(&scope_a) >= 1,
        "engine reads count through the storage seam"
    );
    drop(engine);
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
        ttl_guard: None,
    }];
    let seq_three = stored_sequence(&commit(&mut storage, "op-3", writes));
    assert_eq!(
        filtered(&storage, seq_three, &paid).map(|documents| documents.len()),
        Some(0),
        "a deleted key never surfaces"
    );
    let shipped = equals("status", DocumentValue::String("shipped".into()));
    assert_eq!(filtered(&storage, seq_two, &shipped).unwrap().len(), 1);
    assert!(filtered(&storage, seq_three, &shipped).unwrap().is_empty());
    let seq_four = commit_object(
        &mut storage,
        &tenant,
        "op-4",
        object_document("doc", "users", &[("status", DocumentValue::String("paid".into()))]),
    );
    assert_eq!(filtered(&storage, seq_four, &paid).unwrap().len(), 1);
    assert!(filtered(&storage, seq_three, &paid).unwrap().is_empty());
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn filtered_query_allows_a_candidate_index_seek_without_changing_snapshot_answers() {
    use super::{filtered_page_sql, quote_identifier, text_column};
    let (dir, mut storage) = sqlite_store("filtered-index-seek");
    let users = scan_users();
    let tenant = scan_tenant();
    storage.register_index_descriptor(&users, "status").unwrap();
    let mut sequence = 0;
    for (operation, id, status) in [
        ("a1", "a", "paid"),
        ("b1", "b", "paid"),
        ("a2", "a", "shipped"),
        ("c1", "c", "paid"),
        ("c2", "c", "paid"),
    ] {
        sequence = commit_object(
            &mut storage,
            &tenant,
            operation,
            object_document(id, "users", &[("status", DocumentValue::String(status.into()))]),
        );
    }
    let sql = filtered_page_sql(&[format!("{} = ?", quote_identifier(&text_column("status")))]);
    // A shadow execution must be able to force this candidate. This is a
    // read-only planner hint, not a serving-plan activation.
    let candidate = sql.replace(
        "FROM documents AS current",
        "FROM documents AS current INDEXED BY \"i:status#t\"",
    );
    let connection = storage.writer.lock().unwrap();
    let sql_sequence = i64::try_from(sequence).unwrap();
    let parameters =
        rusqlite::params![tenant.as_str(), users.as_str(), sql_sequence, "", "paid", 1];
    let details = connection
        .prepare(&format!("EXPLAIN QUERY PLAN {candidate}"))
        .unwrap()
        .query_map(parameters, |row| row.get::<_, String>(3))
        .unwrap()
        .collect::<Result<Vec<_>, _>>()
        .unwrap();
    assert!(
        details.iter().any(|detail| detail.contains("SEARCH current USING INDEX i:status#t")),
        "candidate must seek rather than scan: {details:?}"
    );
    for after in ["", "b", "c"] {
        let parameters =
            rusqlite::params![tenant.as_str(), users.as_str(), sql_sequence, after, "paid", 1];
        let read = |query: &str| {
            connection
                .prepare(query)
                .unwrap()
                .query_map(parameters, |row| row.get::<_, String>(0))
                .unwrap()
                .collect::<Result<Vec<_>, _>>()
                .unwrap()
        };
        assert_eq!(read(&sql), read(&candidate));
    }
    drop(connection);
    let filter = equals("status", DocumentValue::String("paid".into()));
    let request = crate::storage::ShadowReadRequest {
        collection: &users,
        filter: &filter,
        candidate_field: "status",
        hidden_candidate: None,
        after: None,
        limit: 1000,
        max_vm_steps: crate::storage::MAX_SHADOW_VM_STEPS,
    };
    let comparison =
        storage.shadow_filtered_page(StorageSequence::new(sequence), &tenant, &request).unwrap();
    assert_eq!(comparison.ordinary.rows, 2);
    assert_eq!(comparison.ordinary.result_digest, comparison.candidate.result_digest);
    assert!(comparison.ordinary.vm_steps > 0 && comparison.candidate.vm_steps > 0);
    let foreign = TenantId::try_new("foreign").unwrap();
    assert_eq!(
        storage
            .shadow_filtered_page(StorageSequence::new(sequence), &foreign, &request)
            .unwrap()
            .ordinary
            .rows,
        0
    );
    let over_budget = crate::storage::ShadowReadRequest { max_vm_steps: 1, ..request };
    assert_eq!(
        storage.shadow_filtered_page(StorageSequence::new(sequence), &tenant, &over_budget),
        Err(crate::storage::ShadowReadError::BudgetExceeded)
    );
    let documents =
        filtered(&storage, sequence, &equals("status", DocumentValue::String("paid".into())))
            .unwrap();
    assert_eq!(
        documents.iter().map(|document| document.id().as_str()).collect::<Vec<_>>(),
        ["b", "c"]
    );
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
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

#[test]
fn origin_progress_survives_pruning_and_reopen_atomically() {
    let (dir, mut storage) = sqlite_store("origin-prefix-pruning");
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("prefix-{index}"),
            vec![put(
                document("doc", "users", &format!("v{index}")),
                if index == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(index - 1) },
            )],
        );
    }
    acknowledge_required_frontiers(&mut storage);
    backdate_all_rows(&storage);
    storage.advance_consumer_frontier("origin-reader", 3).unwrap();
    {
        let writer = storage.raw_writer_connection();
        writer
            .execute_batch(
                "CREATE TRIGGER refuse_pruning BEFORE DELETE ON documents BEGIN
             SELECT RAISE(ABORT, 'injected pruning failure'); END;",
            )
            .unwrap();
    }
    assert!(storage.prune_below_consumer_frontiers().is_err());
    {
        let writer = storage.raw_writer_connection();
        let saved: i64 =
            writer.query_row("SELECT COUNT(*) FROM origin_prefixes", [], |row| row.get(0)).unwrap();
        assert_eq!(saved, 0, "failed pruning rolls back its frontier checkpoint");
        writer.execute_batch("DROP TRIGGER refuse_pruning").unwrap();
    }
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 2);
    assert_eq!(storage.journal_records(), 1);
    let before = storage.durable_origin_prefixes(&tenant()).unwrap();
    assert_eq!(before.len(), 1);
    assert_eq!(before[0].origin_sequence, 3);
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let restored = storage.durable_origin_prefixes(&tenant()).unwrap();
    assert_eq!(restored[0].origin_sequence, 3);
    commit(
        &mut storage,
        "prefix-4",
        vec![put(document("doc", "users", "v4"), ExpectedVersion::Exact(3))],
    );
    acknowledge_required_frontiers(&mut storage);
    assert_eq!(storage.durable_origin_prefixes(&tenant()).unwrap()[0].origin_sequence, 4);
    backdate_all_rows(&storage);
    storage.advance_consumer_frontier("origin-reader", 4).unwrap();
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 1);
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    assert_eq!(storage.durable_origin_prefixes(&tenant()).unwrap()[0].origin_sequence, 4);
    {
        let writer = storage.raw_writer_connection();
        writer
            .execute("INSERT INTO collection_history (collection, days) VALUES ('users', 1)", [])
            .unwrap();
    }
    storage.advance_consumer_frontier("origin-reader", 5).unwrap();
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 0);
    assert_eq!(storage.journal_records(), 1, "history expiry must preserve current state");
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    commit(
        &mut storage,
        "prefix-after-history-sweep",
        vec![put(document("fresh", "users", "v5"), ExpectedVersion::New)],
    );
    assert_eq!(storage.durable_origin_prefixes(&tenant()).unwrap()[0].origin_sequence, 5);
}

#[test]
fn pruning_preserves_a_gap_instead_of_promoting_the_maximum_sequence() {
    let (dir, mut storage) = sqlite_store("origin-prefix-gap-pruning");
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("gap-{index}"),
            vec![put(
                document("doc", "users", &format!("v{index}")),
                if index == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(index - 1) },
            )],
        );
    }
    {
        let writer = storage.raw_writer_connection();
        writer
            .execute("UPDATE documents SET origin_seq = origin_seq + 1 WHERE origin_seq >= 2", [])
            .unwrap();
        // The durable prefix is the proven causal state; keep it aligned with
        // the intentionally introduced gap instead of leaving the old cached
        // prefix to authorize the synthetic rows after it.
        writer.execute("UPDATE origin_prefixes SET seq = 1", []).unwrap();
        // The publication journal is another durable proof of causal progress.
        // Remove the missing publication too, so neither source fills the gap.
        writer
            .execute("DELETE FROM replication_publication_records WHERE local_seq = 2", [])
            .unwrap();
        writer.execute("DELETE FROM replication_publications WHERE origin_seq = 2", []).unwrap();
    }
    acknowledge_required_frontiers(&mut storage);
    backdate_all_rows(&storage);
    storage.advance_consumer_frontier("gap-reader", 3).unwrap();
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 2);
    drop(storage);
    let storage = SqliteStorage::open(&dir).unwrap();
    assert_eq!(storage.durable_origin_prefixes(&tenant()).unwrap()[0].origin_sequence, 1);
}

#[test]
fn json_storage_preserves_finite_float_variants_and_bits() {
    for number in
        [0.0_f64, -0.0, 42.0, -42.0, 0.125, f64::MIN_POSITIVE, f64::MAX, 9_007_199_254_740_992.0]
    {
        let original = Document::new(
            DocumentId::try_new("number").unwrap(),
            CollectionName::try_new("numbers").unwrap(),
            DocumentValue::Number(number),
        );
        let bytes = super::encode_document_body(&original);
        let restored = super::decode_document_body(&bytes).expect("stored JSON decodes");
        let DocumentValue::Number(actual) = restored.value() else {
            panic!("floating-point {number:?} changed its numeric variant");
        };
        assert_eq!(actual.to_bits(), number.to_bits());
    }
    let integer = Document::new(
        DocumentId::try_new("integer").unwrap(),
        CollectionName::try_new("numbers").unwrap(),
        DocumentValue::Integer(42),
    );
    let restored = super::decode_document_body(&super::encode_document_body(&integer)).unwrap();
    assert_eq!(restored.value(), &DocumentValue::Integer(42));
}

#[test]
fn json_storage_preserves_envelope_unicode_nonfinite_and_rejects_invalid_input() {
    let value = DocumentValue::Object(
        [("text".to_owned(), DocumentValue::String("line\n café".to_owned()))].into(),
    );
    let document = Document::new(
        DocumentId::try_new("json-doc").unwrap(),
        CollectionName::try_new("json").unwrap(),
        value.clone(),
    );
    let bytes = super::encode_document_body(&document);
    assert_eq!(
        bytes,
        "{\"$collection\":\"json\",\"$id\":\"json-doc\",\"$value\":{\"text\":\"line\\u000a café\"},\"$version\":0}"
            .as_bytes()
    );
    assert_eq!(super::decode_document_body(&bytes).unwrap().value(), &value);

    for number in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
        let document = Document::new(
            DocumentId::try_new("json-doc").unwrap(),
            CollectionName::try_new("json").unwrap(),
            DocumentValue::Number(number),
        );
        let bytes = super::encode_document_body(&document);
        let restored = super::decode_document_body(&bytes).unwrap();
        let DocumentValue::Number(restored) = restored.value() else {
            panic!("the floating-point value changed type");
        };
        assert_eq!(restored.to_bits(), number.to_bits());
    }

    assert!(
        super::decode_document_body(
            br#"{"$collection":"json","$id":"json-doc","$value":null,"$version":0} trailing"#
        )
        .is_none()
    );
    assert!(
        super::decode_document_body(
            br#"{"$collection":"json","$id":"json-doc","$value":[1,],"$version":0}"#
        )
        .is_none()
    );
}

#[test]
fn origin_prefix_preserves_unsigned_hlc_through_pruning() {
    let (dir, mut storage) = sqlite_store("prefix-unsigned-hlc");
    for index in 1..=3 {
        commit(
            &mut storage,
            &format!("clock-{index}"),
            vec![put(
                document("doc", "users", &format!("v{index}")),
                if index == 1 { ExpectedVersion::New } else { ExpectedVersion::Exact(index - 1) },
            )],
        );
    }
    acknowledge_required_frontiers(&mut storage);
    {
        let writer = storage.raw_writer_connection();
        writer.execute("UPDATE documents SET hlc = CASE origin_seq WHEN 1 THEN 100 WHEN 2 THEN -1 ELSE -9223372036854775808 END", []).unwrap();
    }
    assert_eq!(storage.durable_origin_prefixes(&tenant()).unwrap()[0].hlc, u64::MAX);
    backdate_all_rows(&storage);
    storage.advance_consumer_frontier("hlc-reader", 3).unwrap();
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 2);
    drop(storage);
    let storage = SqliteStorage::open(&dir).unwrap();
    let prefix = storage.durable_origin_prefixes(&tenant()).unwrap();
    assert_eq!(prefix[0].origin_sequence, 3);
    assert_eq!(prefix[0].hlc, u64::MAX);
    storage.prune_below_consumer_frontiers().unwrap();
    assert_eq!(storage.durable_origin_prefixes(&tenant()).unwrap()[0].hlc, u64::MAX);
}

#[test]
fn merge_metadata_commits_rolls_back_and_reopens_with_its_document() {
    let (dir, engine) = open_sqlite_engine("merge-metadata-atomicity");
    let collection = CollectionName::try_new("users").expect("collection");
    let id = DocumentId::try_new("typed").expect("id");
    let schema = crate::storage::MergeSchema::try_new(std::collections::BTreeMap::from([(
        "count".to_owned(),
        crate::storage::MergeFieldKind::PnCounter,
    )]))
    .expect("schema");
    engine
        .declare_merge_schema(&tenant_scope(&engine, "tenant-a"), &collection, &schema)
        .expect("declare");
    let write = |engine: &Engine, name: &str, body: DocumentValue| {
        let scope = tenant_scope(engine, "tenant-a");
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction = engine.begin(&scope, snapshot, operation(name)).expect("transaction");
        transaction
            .put(Document::new(id.clone(), collection.clone(), body), ExpectedVersion::Any)
            .expect("put");
        transaction.commit(LocalDurability::Synced)
    };
    let body = DocumentValue::Object(std::collections::BTreeMap::from([
        ("count".to_owned(), DocumentValue::Integer(7)),
        ("plain".to_owned(), DocumentValue::Integer(9)),
    ]));
    committed_sequence(write(&engine, "initial", body.clone()));
    let kernels = engine
        .merge_kernels_for(&tenant_scope(&engine, "tenant-a"), &collection, &id)
        .expect("kernels");
    assert_eq!(kernels.len(), 1, "only the declared field stores merge metadata");
    assert_eq!(kernels[0].field, "count");
    engine.arm_storage_fault(FaultPoint::WalAppend);
    assert!(matches!(
        write(&engine, "faulted", DocumentValue::String("replace".into())),
        CommitOutcome::Rejected(CommitError::StorageFailure)
    ));
    engine.shutdown().expect("shutdown");
    let engine = Engine::open(EngineConfig::durable(&dir).expect("config")).expect("reopen");
    let scope = tenant_scope(&engine, "tenant-a");
    assert_eq!(
        engine.merge_kernels_for(&scope, &collection, &id).expect("reopened kernels"),
        kernels
    );
    assert_eq!(read_document(&engine, "tenant-a", "typed").expect("document").value(), &body);
    committed_sequence(write(&engine, "replace", DocumentValue::String("scalar".into())));
    assert!(
        engine.merge_kernels_for(&scope, &collection, &id).expect("cleared kernels").is_empty()
    );
    committed_sequence(write(&engine, "restore-field", body));
    assert_eq!(
        engine.merge_kernels_for(&scope, &collection, &id).expect("restored kernels").len(),
        1
    );
    committed_sequence(commit_delete(
        &engine,
        "tenant-a",
        "delete",
        "typed",
        ExpectedVersion::Any,
        LocalDurability::Synced,
    ));
    assert!(
        engine.merge_kernels_for(&scope, &collection, &id).expect("deleted kernels").is_empty()
    );
    engine.shutdown().expect("close");
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn waves_restore_normal_baseline_after_durable_mutations() {
    let (dir, mut storage) = sqlite_store("synced-wave-pragma");
    storage
        .raw_writer_connection()
        .execute_batch(
            "CREATE TEMP TABLE observed_sync (level INTEGER);
         CREATE TEMP TRIGGER observe_document_sync AFTER INSERT ON main.documents BEGIN
           INSERT INTO observed_sync SELECT synchronous FROM pragma_synchronous;
         END;",
        )
        .unwrap();
    for (index, durability, expected) in [
        (1, LocalDurability::Written, 1),
        (2, LocalDurability::Synced, 2),
        (3, LocalDurability::Buffered, 1),
    ] {
        storage.raw_writer_connection().execute("DELETE FROM observed_sync", []).unwrap();
        let intent = fresh_intent(
            &mut storage,
            &format!("sync-{index}"),
            vec![put(document(&format!("doc-{index}"), "docs", "value"), ExpectedVersion::New)],
            durability,
        );
        assert!(matches!(storage.commit(intent), CommitResult::Committed(_)));
        let writer = storage.raw_writer_connection();
        let observed: Vec<i64> = writer
            .prepare("SELECT DISTINCT level FROM observed_sync")
            .unwrap()
            .query_map([], |row| row.get(0))
            .unwrap()
            .collect::<Result<_, _>>()
            .unwrap();
        assert_eq!(observed, vec![expected], "durability must hold during the write transaction");
        assert_eq!(
            writer.query_row("PRAGMA synchronous", [], |row| row.get::<_, i64>(0)).unwrap(),
            1
        );
    }
    storage.raw_writer_connection().execute("DELETE FROM observed_sync", []).unwrap();
    let mixed = [LocalDurability::Written, LocalDurability::Synced]
        .into_iter()
        .enumerate()
        .map(|(index, durability)| {
            fresh_intent(
                &mut storage,
                &format!("mixed-{index}"),
                vec![put(
                    document(&format!("mixed-{index}"), "docs", "value"),
                    ExpectedVersion::New,
                )],
                durability,
            )
        })
        .collect();
    assert!(
        storage
            .commit_batch(mixed, false)
            .iter()
            .all(|result| matches!(result, CommitResult::Committed(_)))
    );
    let levels: Vec<i64> = storage
        .raw_writer_connection()
        .prepare("SELECT level FROM observed_sync")
        .unwrap()
        .query_map([], |row| row.get(0))
        .unwrap()
        .collect::<Result<_, _>>()
        .unwrap();
    assert_eq!(levels, vec![2, 2], "a mixed wave must honor its strongest durability request");
    storage.arm_test_fault(FaultPoint::WalAppend);
    let intent = fresh_intent(
        &mut storage,
        "failed-written",
        vec![put(document("failed", "docs", "value"), ExpectedVersion::New)],
        LocalDurability::Written,
    );
    assert!(matches!(storage.commit(intent), CommitResult::Rejected(_)));
    assert_eq!(
        storage
            .raw_writer_connection()
            .query_row("PRAGMA synchronous", [], |row| row.get::<_, i64>(0))
            .unwrap(),
        1
    );
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn losing_replication_decisions_commit_with_full_sync() {
    let (dir, mut storage) = sqlite_store("superseded-sync");
    storage.raw_writer_connection().execute_batch(
        "CREATE TEMP TABLE observed_sync (level INTEGER);
         CREATE TEMP TRIGGER observe_superseded_sync AFTER INSERT ON main.replication_superseded BEGIN
           INSERT INTO observed_sync SELECT synchronous FROM pragma_synchronous;
         END;"
    ).unwrap();
    let intent = || crate::storage::SupersededReplication {
        tenant: tenant(),
        operation: OperationId::try_new("loser").unwrap(),
        fingerprint: [7; 32],
        snapshot_sequence: StorageSequence::new(0),
        collection: CollectionName::try_new("docs").unwrap(),
        id: DocumentId::try_new("doc").unwrap(),
        conflict: None,
    };
    assert!(!storage.record_superseded_replication(intent()).unwrap());
    assert!(storage.record_superseded_replication(intent()).unwrap());
    let levels: Vec<i64> = storage
        .raw_writer_connection()
        .prepare("SELECT level FROM observed_sync")
        .unwrap()
        .query_map([], |row| row.get(0))
        .unwrap()
        .collect::<Result<_, _>>()
        .unwrap();
    assert_eq!(levels, vec![2], "only the first durable decision writes, under FULL");
    drop(storage);
    let reopened = SqliteStorage::open(&dir).unwrap();
    assert!(
        reopened
            .replication_was_superseded(&tenant(), &OperationId::try_new("loser").unwrap())
            .unwrap()
    );
    drop(reopened);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn checkpoint_notifications_coalesce_without_blocking_commits() {
    let (dir, mut storage) = sqlite_store("checkpoint-notifications");
    let gate = Arc::clone(&storage.checkpoint_gate);
    let held = gate.lock().unwrap();
    let mut coalesced = 0;
    for _ in 0..1000 {
        match storage.checkpointer.wake.try_send(()) {
            Ok(()) => {}
            Err(std::sync::mpsc::TrySendError::Full(())) => coalesced += 1,
            Err(std::sync::mpsc::TrySendError::Disconnected(())) => panic!("checkpointer stopped"),
        }
    }
    assert!(coalesced >= 998, "at most one wake is executing and one is queued");
    // The checkpoint worker cannot acquire its gate until this test lets go.
    // The committer must still finish while its notification slot is full.
    assert!(matches!(
        commit(
            &mut storage,
            "while-checkpoint-blocked",
            vec![put(document("doc", "docs", "value"), ExpectedVersion::New)]
        ),
        CommitResult::Committed(_)
    ));
    drop(held);
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn checkpoint_startup_refuses_missing_or_corrupt_database_without_creating_it() {
    let dir = location("checkpoint-startup-failure");
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("missing.sqlite3");
    let readers = Arc::new(std::sync::Mutex::new(super::ReaderPool::new(1024)));
    let gate = Arc::new(std::sync::Mutex::new(()));
    assert!(matches!(
        super::spawn_checkpointer(path.clone(), "expected", readers.clone(), gate.clone(), None),
        Err(StorageOpenError::Unavailable)
    ));
    assert!(!path.exists(), "checkpoint startup must never create an empty replacement");
    let corrupt = b"not a SQLite database";
    std::fs::write(&path, corrupt).unwrap();
    assert!(matches!(
        super::spawn_checkpointer(path.clone(), "expected", readers, gate, None),
        Err(StorageOpenError::Unavailable)
    ));
    assert_eq!(std::fs::read(&path).unwrap(), corrupt);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn reader_open_failure_uses_bound_writer_without_creating_a_database() {
    let (dir, mut storage) = sqlite_store("reader-open-failure");
    let committed = commit(
        &mut storage,
        "original",
        vec![put(document("doc", "docs", "original"), ExpectedVersion::New)],
    );
    let sequence = StorageSequence::new(stored_sequence(&committed));
    {
        let mut readers = storage.readers.lock().unwrap();
        while readers.pop().is_some() {}
    }
    let original_path = storage.database_path.clone();
    let missing_path = dir.join("must-not-be-created.sqlite3");
    storage.database_path.clone_from(&missing_path);
    let reader = storage.reader();
    assert!(matches!(reader, super::ReaderGuard::Writer(_)));
    drop(reader);
    let read = storage
        .read_document_in_collection_at(
            sequence,
            &tenant(),
            &CollectionName::try_new("docs").unwrap(),
            &DocumentId::try_new("doc").unwrap(),
        )
        .unwrap();
    assert_eq!(read.value(), &DocumentValue::String("original".into()));
    assert!(
        storage.resolve_operation(&tenant(), &OperationId::try_new("unknown").unwrap()).is_none(),
        "the rejection fallback must reuse its guard rather than acquire the writer twice"
    );
    assert!(storage.open_sqlite_read_connection().is_err());
    assert!(!missing_path.exists());
    storage.database_path = original_path;
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn reader_refill_rejects_foreign_database_binding() {
    let (dir, mut storage) = sqlite_store("reader-identity");
    let (foreign_dir, mut foreign) = sqlite_store("reader-foreign");
    let result = commit(
        &mut storage,
        "original",
        vec![put(document("doc", "docs", "original"), ExpectedVersion::New)],
    );
    commit(
        &mut foreign,
        "foreign",
        vec![put(document("doc", "docs", "foreign"), ExpectedVersion::New)],
    );
    let sequence = StorageSequence::new(stored_sequence(&result));
    let foreign_path = foreign.database_path.clone();
    drop(foreign);
    let before = std::fs::read(&foreign_path).unwrap();
    {
        let mut pool = storage.readers.lock().unwrap();
        while pool.pop().is_some() {}
    }
    let original_path = storage.database_path.clone();
    storage.database_path = foreign_path.clone();
    assert!(storage.open_sqlite_read_connection().is_err());
    assert!(matches!(storage.reader(), super::ReaderGuard::Writer(_)));
    let read = storage
        .read_document_in_collection_at(
            sequence,
            &tenant(),
            &CollectionName::try_new("docs").unwrap(),
            &DocumentId::try_new("doc").unwrap(),
        )
        .unwrap();
    assert_eq!(read.value(), &DocumentValue::String("original".into()));
    assert!(matches!(
        super::spawn_checkpointer(
            foreign_path.clone(),
            &storage.expected_binding,
            storage.readers.clone(),
            storage.checkpoint_gate.clone(),
            None,
        ),
        Err(StorageOpenError::Unavailable)
    ));
    assert_eq!(std::fs::read(foreign_path).unwrap(), before);
    storage.database_path = original_path;
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
    std::fs::remove_dir_all(foreign_dir).unwrap();
}

#[test]
fn invisible_index_build_limit_bounds_all_tenant_triggers() {
    use crate::storage::{InvisibleColumnKind, InvisibleIndexColumn};
    let (dir, mut storage) = sqlite_store("invisible-global-limit");
    let collection = scan_users();
    storage.register_index_descriptor(&collection, "score").unwrap();
    let columns =
        [InvisibleIndexColumn { field: "score".into(), kind: InvisibleColumnKind::Numeric }];
    for index in 0..32 {
        let tenant = TenantId::try_new(&format!("tenant-{index}")).unwrap();
        assert!(
            storage
                .begin_invisible_index(&tenant, &collection, "candidate", &columns)
                .unwrap()
                .complete
        );
    }
    let overflow = TenantId::try_new("overflow").unwrap();
    assert!(storage.begin_invisible_index(&overflow, &collection, "candidate", &columns).is_err());
    let original = TenantId::try_new("tenant-0").unwrap();
    assert!(
        storage
            .begin_invisible_index(&original, &collection, "candidate", &columns)
            .unwrap()
            .complete
    );
    let count: i64 = storage
        .writer
        .lock()
        .unwrap()
        .query_row(
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger' AND name LIKE 'hidden_%'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(count, 32 * 3);
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn invisible_index_build_resumes_and_tracks_transactional_writes() {
    use crate::storage::{InvisibleColumnKind, InvisibleIndexColumn};
    let (dir, mut storage) = sqlite_store("invisible-build");
    let tenant = scan_tenant();
    let users = scan_users();
    for id in ["a", "b", "c", "d"] {
        commit_object(
            &mut storage,
            &tenant,
            id,
            object_document(id, "users", &[("status", DocumentValue::String("paid".into()))]),
        );
    }
    storage.register_index_descriptor(&users, "status").unwrap();
    let columns =
        vec![InvisibleIndexColumn { field: "status".into(), kind: InvisibleColumnKind::Text }];
    let index_count = |storage: &SqliteStorage| {
        storage.writer.lock().unwrap().query_row(
        "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'documents'", [],
        |row| row.get::<_, i64>(0)).unwrap()
    };
    let serving_indexes = index_count(&storage);
    let initial = storage.begin_invisible_index(&tenant, &users, "candidate", &columns).unwrap();
    assert!(!initial.complete);
    assert_eq!(
        storage.begin_invisible_index(&tenant, &users, "candidate", &columns).unwrap(),
        initial
    );
    assert_eq!(index_count(&storage), serving_indexes);
    let table =
        super::quote_identifier(&super::invisible::table_name(tenant.as_str(), "candidate"));
    let count = |storage: &SqliteStorage| {
        storage
            .writer
            .lock()
            .unwrap()
            .query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |row| row.get::<_, i64>(0))
            .unwrap()
    };
    assert_eq!(count(&storage), 0);
    assert!(!storage.advance_invisible_index(&tenant, "candidate", 1).unwrap().complete);
    assert_eq!(count(&storage), 1);
    storage
        .writer
        .lock()
        .unwrap()
        .execute_batch(
            "CREATE TEMP TRIGGER reject_cursor BEFORE UPDATE ON invisible_index_builds
         BEGIN SELECT RAISE(ABORT, 'cursor failure'); END;",
        )
        .unwrap();
    assert!(storage.advance_invisible_index(&tenant, "candidate", 1).is_err());
    assert_eq!(count(&storage), 1, "copied rows and cursor must roll back together");
    storage.writer.lock().unwrap().execute_batch("DROP TRIGGER reject_cursor").unwrap();
    let other = TenantId::try_new("tenant-b").unwrap();
    assert!(storage.begin_invisible_index(&other, &users, "candidate", &columns).unwrap().complete);
    for (operation, id) in
        [("new-before", "0"), ("new-between", "aa"), ("new-after", "z"), ("update-a", "a")]
    {
        commit_object(
            &mut storage,
            &tenant,
            operation,
            object_document(id, "users", &[("status", DocumentValue::String("shipped".into()))]),
        );
    }
    assert_eq!(count(&storage), 5, "new versions are copied inside their write transaction");
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let mut complete = false;
    for _ in 0..16 {
        let progress = storage.advance_invisible_index(&tenant, "candidate", 1).unwrap();
        assert_eq!(progress.target_sequence, initial.target_sequence);
        if progress.complete {
            complete = true;
            break;
        }
    }
    assert!(complete);
    assert_eq!(count(&storage), 8);
    let writer = storage.writer.lock().unwrap();
    let different: i64 = writer
        .query_row(
            &format!(
                "SELECT COUNT(*) FROM (SELECT id, commit_seq, \"f:status#t\" FROM documents
         WHERE tenant = ?1 AND collection = ?2 EXCEPT SELECT id, commit_seq, c0 FROM {table})"
            ),
            rusqlite::params![tenant.as_str(), users.as_str()],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(different, 0);
    let other_table =
        super::quote_identifier(&super::invisible::table_name(other.as_str(), "candidate"));
    assert_eq!(
        writer
            .query_row(&format!("SELECT COUNT(*) FROM {other_table}"), [], |row| row
                .get::<_, i64>(0))
            .unwrap(),
        0
    );
    writer
        .execute_batch(&format!(
            "CREATE TEMP TRIGGER reject_hidden BEFORE INSERT ON {table}
        BEGIN SELECT RAISE(ABORT, 'maintenance failure'); END;"
        ))
        .unwrap();
    drop(writer);
    let before = storage.published_state();
    let failed = commit(
        &mut storage,
        "rejected-hidden-write",
        vec![StagedWrite::Put {
            document: object_document(
                "rejected",
                "users",
                &[("status", DocumentValue::String("paid".into()))],
            ),
            expected: ExpectedVersion::Any,
        }],
    );
    assert!(matches!(failed, CommitResult::Rejected(_)));
    assert_eq!(storage.published_state(), before);
    assert_eq!(count(&storage), 8);
    storage.writer.lock().unwrap().execute_batch("DROP TRIGGER reject_hidden").unwrap();
    assert!(matches!(
        commit(
            &mut storage,
            "delete-hidden-row",
            vec![StagedWrite::Delete {
                collection: Some(users.clone()),
                id: DocumentId::try_new("b").unwrap(),
                expected: ExpectedVersion::Any,
                ttl_guard: None,
            }]
        ),
        CommitResult::Committed(_)
    ));
    assert_eq!(count(&storage), 9, "tombstone versions are maintained too");
    storage.writer.lock().unwrap().execute(
        "DELETE FROM documents WHERE tenant = ?1 AND collection = ?2 AND id = 'a' AND commit_seq = 1",
        rusqlite::params![tenant.as_str(), users.as_str()],
    ).unwrap();
    assert_eq!(count(&storage), 8, "pruned source versions release their hidden entries");
    verify_hidden_shadow_results(&mut storage, &tenant, &users, &table);
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

fn verify_hidden_shadow_results(
    storage: &mut SqliteStorage,
    tenant: &TenantId,
    collection: &CollectionName,
    table: &str,
) {
    let filter = equals("status", DocumentValue::String("paid".into()));
    let request = crate::storage::ShadowReadRequest {
        collection,
        filter: &filter,
        candidate_field: "status",
        hidden_candidate: Some("candidate"),
        after: None,
        limit: 1000,
        max_vm_steps: crate::storage::MAX_SHADOW_VM_STEPS,
    };
    let sequence = storage.published_state().0;
    let comparison = storage.shadow_filtered_page(sequence, tenant, &request).unwrap();
    assert_eq!(comparison.ordinary.rows, 2);
    assert_eq!(comparison.ordinary.result_digest, comparison.candidate.result_digest);
    storage.writer.lock().unwrap().execute(&format!("DELETE FROM {table}"), []).unwrap();
    assert_eq!(
        storage.shadow_filtered_page(sequence, tenant, &request),
        Err(crate::storage::ShadowReadError::ResultMismatch)
    );
}

#[test]
fn shadow_evidence_retains_each_template_and_refuses_capacity_without_eviction() {
    use crate::auto_index::{
        LifecycleIndexView, LifecyclePhase, LifecycleStore, LogicalCost, ShadowEvidence,
        ShadowOutcome,
    };
    let (dir, mut storage) = sqlite_store("shadow-template-set");
    let mut store = LifecycleStore::open(&storage).unwrap();
    let view = LifecycleIndexView {
        id: "candidate".into(),
        tenant: "tenant".into(),
        collection: "items".into(),
        fields: vec!["score".into()],
        phase: LifecyclePhase::Shadowing,
        generation: 0,
        covered_watermark: 10,
        target_watermark: Some(10),
        visible: false,
        pinned: false,
        forbidden: false,
        maintenance_cost: 1,
        last_used_millis: None,
        zero_stale_reads: false,
        regression_free: false,
    };
    store.install(&mut storage, view.clone(), 1, [1; 32]).unwrap();
    let template = |index: u64| *blake3::hash(&index.to_le_bytes()).as_bytes();
    let mut sample = ShadowEvidence {
        index_id: "candidate".into(),
        generation: 1,
        template_digest: template(0),
        baseline: vec![LogicalCost::new(0, 100); 20],
        candidate: vec![LogicalCost::new(0, 150); 20],
        improvement: 0.0,
        regression: false,
        outcome: ShadowOutcome::Abandon,
        digest: [0; 32],
    };
    let regressed = store.record_evidence(&mut storage, "tenant", sample.clone(), 2).unwrap();
    assert!(regressed.regression);
    sample.candidate = vec![LogicalCost::new(0, 50); 20];
    for index in 1..256 {
        sample.generation = store.generation("tenant");
        sample.template_digest = template(index);
        store.record_evidence(&mut storage, "tenant", sample.clone(), index + 2).unwrap();
    }
    assert_eq!(store.evidence("tenant", "candidate", template(0), 20), Some(regressed.clone()));
    assert!(!store.evidence("tenant", "candidate", template(255), 20).unwrap().regression);
    let generation = store.generation("tenant");
    let audits = store.audit("tenant", None, None, 1000);
    sample.generation = generation;
    sample.template_digest = template(256);
    assert!(store.record_evidence(&mut storage, "tenant", sample.clone(), 300).is_err());
    assert_eq!(store.generation("tenant"), generation);
    assert_eq!(store.audit("tenant", None, None, 1000), audits);
    // The bound applies to every tenant together, and cannot evict regression evidence.
    let mut foreign = view;
    foreign.tenant = "foreign".into();
    store.install(&mut storage, foreign, 301, [1; 32]).unwrap();
    sample.generation = 1;
    assert!(store.record_evidence(&mut storage, "foreign", sample.clone(), 302).is_err());
    // Refreshing an already retained template remains possible at capacity.
    sample.generation = generation;
    sample.template_digest = template(255);
    let refreshed = store.record_evidence(&mut storage, "tenant", sample, 303).unwrap();
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let mut restored = LifecycleStore::open(&storage).unwrap();
    assert_eq!(restored.evidence("tenant", "candidate", template(0), 20), Some(regressed));
    assert_eq!(restored.evidence("tenant", "candidate", template(255), 20), Some(refreshed));
    assert!(restored.evidence("foreign", "candidate", template(0), 20).is_none());
    restored.control(&mut storage, ("tenant", "candidate"), true, 304, [3; 32]).unwrap();
    let reopened = LifecycleStore::open(&storage).unwrap();
    for index in 0..256 {
        assert!(restored.evidence("tenant", "candidate", template(index), 20).is_none());
        assert!(reopened.evidence("tenant", "candidate", template(index), 20).is_none());
    }
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn shadow_evidence_is_durable_scoped_and_atomic() {
    use crate::auto_index::{
        LifecycleIndexView, LifecyclePhase, LifecycleStore, LogicalCost, ShadowEvidence,
    };
    let dir = location("shadow-evidence");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let mut lifecycle = LifecycleStore::open(&storage).unwrap();
    let view = LifecycleIndexView {
        id: "candidate".into(),
        tenant: "tenant".into(),
        collection: "items".into(),
        fields: vec!["score".into()],
        phase: LifecyclePhase::Shadowing,
        generation: 0,
        covered_watermark: 10,
        target_watermark: Some(10),
        visible: false,
        pinned: false,
        forbidden: false,
        maintenance_cost: 9,
        last_used_millis: None,
        zero_stale_reads: false,
        regression_free: false,
    };
    lifecycle.install(&mut storage, view, 1, [1; 32]).unwrap();
    let samples = ShadowEvidence {
        index_id: "candidate".into(),
        generation: 1,
        template_digest: [7; 32],
        baseline: vec![LogicalCost::new(10, 90); 20],
        candidate: vec![LogicalCost::new(5, 45); 20],
        // Supplied derived fields cannot authorize a decision.
        improvement: f64::NAN,
        regression: true,
        outcome: crate::auto_index::ShadowOutcome::Activate,
        digest: [0; 32],
    };
    let before = lifecycle.snapshot("tenant", None, 10);
    assert!(lifecycle.record_evidence(&mut storage, "foreign", samples.clone(), 2).is_err());
    for variant in 0..5 {
        let mut invalid = samples.clone();
        match variant {
            0 => invalid.generation = 0,
            1 => invalid.baseline.truncate(19),
            2 => invalid.template_digest = [0; 32],
            3 => invalid.baseline[0] = LogicalCost::new(u64::MAX, 1),
            _ => {
                invalid.baseline = vec![LogicalCost::new(10, 90); 4097];
                invalid.candidate = invalid.baseline.clone();
            }
        }
        assert!(lifecycle.record_evidence(&mut storage, "tenant", invalid, 2).is_err());
    }
    storage
        .writer
        .lock()
        .unwrap()
        .execute_batch(
            "CREATE TEMP TRIGGER reject_evidence BEFORE INSERT ON index_lifecycle_events
         BEGIN SELECT RAISE(ABORT, 'injected evidence failure'); END;",
        )
        .unwrap();
    assert!(lifecycle.record_evidence(&mut storage, "tenant", samples.clone(), 2).is_err());
    assert_eq!(lifecycle.snapshot("tenant", None, 10), before);
    assert!(lifecycle.evidence("tenant", "candidate", [7; 32], 20).is_none());
    assert_eq!(lifecycle.audit("tenant", None, None, 10).len(), 1);
    storage.writer.lock().unwrap().execute_batch("DROP TRIGGER reject_evidence").unwrap();
    let committed = lifecycle.record_evidence(&mut storage, "tenant", samples, 2).unwrap();
    assert_eq!(committed.generation, 2);
    assert!((committed.improvement - 0.5).abs() < f64::EPSILON);
    assert!(!committed.regression);
    assert_ne!(committed.digest, [0; 32]);
    assert!(!lifecycle.index("tenant", "candidate").unwrap().visible);
    assert_eq!(lifecycle.evidence("tenant", "candidate", [7; 32], 1).unwrap().baseline.len(), 1);
    assert!(lifecycle.evidence("foreign", "candidate", [7; 32], 20).is_none());
    let audit = lifecycle.audit("tenant", Some("items"), None, 1);
    assert_eq!(audit[0].action, "evidence");
    assert_eq!(audit[0].evidence_digest, Some(committed.digest));
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let mut restored = LifecycleStore::open(&storage).unwrap();
    assert_eq!(restored.evidence("tenant", "candidate", [7; 32], 20), Some(committed.clone()));
    assert_eq!(restored.snapshot("tenant", None, 10), lifecycle.snapshot("tenant", None, 10));
    assert_eq!(restored.audit("tenant", Some("items"), None, 1), audit);
    restored.control(&mut storage, ("tenant", "candidate"), true, 3, [3; 32]).unwrap();
    assert!(restored.evidence("tenant", "candidate", [7; 32], 20).is_none());
    assert!(
        LifecycleStore::open(&storage)
            .unwrap()
            .evidence("tenant", "candidate", [7; 32], 20)
            .is_none()
    );
    let mut forbidden = committed;
    forbidden.generation = 3;
    assert!(restored.record_evidence(&mut storage, "tenant", forbidden, 4).is_err());
    // Even a payload with a valid storage checksum must match the evidence
    // digest committed for the tenant, template, generation, and samples.
    let writer = storage.writer.lock().unwrap();
    let mut payload: Vec<u8> = writer
        .query_row("SELECT payload FROM index_lifecycle_events WHERE sequence = 2", [], |row| {
            row.get(0)
        })
        .unwrap();
    *payload.last_mut().unwrap() ^= 1;
    let checksum = blake3::hash(&payload);
    writer
        .execute(
            "UPDATE index_lifecycle_events SET payload = ?1, digest = ?2 WHERE sequence = 2",
            rusqlite::params![payload, checksum.as_bytes().as_slice()],
        )
        .unwrap();
    drop(writer);
    assert!(LifecycleStore::open(&storage).is_err());
    drop(storage);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn index_lifecycle_sqlite_replay_and_failed_mutations_are_atomic() {
    use crate::auto_index::{LifecycleIndexView, LifecyclePhase, LifecycleStore};
    let dir = location("index-lifecycle");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let mut lifecycle = LifecycleStore::open(&storage).unwrap();
    let view = LifecycleIndexView {
        id: "candidate".into(),
        tenant: "tenant".into(),
        collection: "items".into(),
        fields: vec!["score".into(), "nested\u{1f}name".into()],
        phase: LifecyclePhase::Shadowing,
        generation: 0,
        covered_watermark: 10,
        target_watermark: Some(10),
        visible: false,
        pinned: false,
        forbidden: false,
        maintenance_cost: 9,
        last_used_millis: None,
        zero_stale_reads: false,
        regression_free: false,
    };
    lifecycle.install(&mut storage, view, 1, [1; 32]).unwrap();
    // Adjacent tenant keys and identical candidate IDs cannot cross scopes,
    // either through a catalog page or a direct command lookup.
    for tenant in ["tenan", "tenant", "tenant-z"] {
        let mut scoped = lifecycle.index("tenant", "candidate").unwrap().clone();
        scoped.id = "second".into();
        scoped.tenant = tenant.into();
        scoped.collection = "secondary".into();
        lifecycle.install(&mut storage, scoped, 1, [1; 32]).unwrap();
    }
    assert!(lifecycle.index("tenant-z", "candidate").is_none());
    assert_eq!(lifecycle.index("tenant-z", "second").unwrap().tenant, "tenant-z");
    assert!(lifecycle.snapshot("ten", None, 10).indexes.is_empty());
    let empty_page = lifecycle.snapshot("tenant", None, 0);
    assert!(empty_page.indexes.is_empty());
    assert!(empty_page.truncated);
    let first_page = lifecycle.snapshot("tenant", None, 1);
    assert_eq!(first_page.indexes[0].id, "candidate");
    assert!(first_page.truncated);
    let scoped_page = lifecycle.snapshot("tenant", Some("secondary"), 1);
    assert_eq!(scoped_page.indexes[0].id, "second");
    assert!(!scoped_page.truncated);
    let tenant_page = lifecycle.snapshot("tenant", None, 10);
    assert_eq!(tenant_page.indexes.len(), 2);
    assert!(tenant_page.indexes.iter().all(|index| index.tenant == "tenant"));
    let before = lifecycle.snapshot("tenant", None, 10);
    storage
        .writer
        .lock()
        .unwrap()
        .execute_batch(
            "CREATE TEMP TRIGGER reject_lifecycle BEFORE INSERT ON index_lifecycle_events
         BEGIN SELECT RAISE(ABORT, 'injected lifecycle failure'); END;",
        )
        .unwrap();
    let mut rejected = before.indexes[0].clone();
    rejected.id = "rejected".into();
    assert!(lifecycle.install(&mut storage, rejected, 2, [2; 32]).is_err());
    assert!(lifecycle.control(&mut storage, ("tenant", "candidate"), false, 2, [2; 32]).is_err());
    assert!(lifecycle.control(&mut storage, ("tenant", "candidate"), true, 2, [2; 32]).is_err());
    assert!(lifecycle.retire_index(&mut storage, ("tenant", "candidate"), 2, [2; 32]).is_err());
    assert_eq!(lifecycle.snapshot("tenant", None, 10), before);
    assert!(!lifecycle.is_forbidden("tenant", "candidate"));
    assert_eq!(lifecycle.audit("tenant", None, None, 10).len(), 2);
    storage.writer.lock().unwrap().execute_batch("DROP TRIGGER reject_lifecycle").unwrap();
    lifecycle.control(&mut storage, ("tenant", "candidate"), false, 2, [2; 32]).unwrap();
    assert!(lifecycle.snapshot("tenant", None, 10).indexes[0].pinned);
    assert_eq!(
        LifecycleStore::open(&storage).unwrap().snapshot("tenant", None, 10),
        lifecycle.snapshot("tenant", None, 10)
    );
    lifecycle.control(&mut storage, ("tenant", "candidate"), true, 3, [3; 32]).unwrap();
    lifecycle.retire_index(&mut storage, ("tenant", "candidate"), 4, [4; 32]).unwrap();
    // More than one replay page, including forbidden shapes without an index.
    for id in 0..70 {
        lifecycle
            .control(&mut storage, ("tenant", &format!("shape-{id}")), true, 5, [5; 32])
            .unwrap();
    }
    let collection_audit = lifecycle.audit("tenant", Some("items"), None, 10);
    assert!(lifecycle.audit("tenant", Some(""), None, 100).is_empty());
    assert_eq!(collection_audit.len(), 4);
    assert_eq!(lifecycle.audit("tenant", Some("items"), None, 1), collection_audit[..1]);
    // Historical events keep their original collection even if the current
    // record is subsequently replaced with a different collection binding.
    let mut replacement = lifecycle.snapshot("tenant", None, 1).indexes.remove(0);
    replacement.collection = "other".into();
    lifecycle.install(&mut storage, replacement, 6, [6; 32]).unwrap();
    assert_eq!(lifecycle.audit("tenant", Some("items"), None, 10), collection_audit);
    assert_eq!(lifecycle.audit("tenant", Some("other"), None, 10).len(), 1);
    let expected = lifecycle.snapshot("tenant", None, 10);
    let audit = lifecycle.audit("tenant", None, None, 100);
    drop(storage);
    let reopened = SqliteStorage::open(&dir).unwrap();
    let restored = LifecycleStore::open(&reopened).unwrap();
    assert_eq!(restored.snapshot("tenant", None, 10), expected);
    assert_eq!(restored.audit("tenant", None, None, 100), audit);
    assert_eq!(restored.audit("tenant", Some("items"), None, 10), collection_audit);
    assert!(restored.audit("tenant", Some(""), None, 100).is_empty());
    assert!(restored.is_forbidden("tenant", "shape-69"));
    assert!(!dir.join("auto-index.lifecycle").exists());
    reopened
        .writer
        .lock()
        .unwrap()
        .execute("UPDATE index_lifecycle_events SET digest = zeroblob(32) WHERE sequence = 1", [])
        .unwrap();
    assert!(LifecycleStore::open(&reopened).is_err());
    drop(reopened);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn descriptor_removal_preserves_shared_users_and_survives_restart() {
    let (dir, mut storage) = sqlite_store("descriptor-remove-shared");
    let tenant = scan_tenant();
    let users = scan_users();
    let orders = CollectionName::try_new("orders").expect("collection");
    commit_object(
        &mut storage,
        &tenant,
        "before-removal",
        object_document("doc-a", "users", &[("score", DocumentValue::Integer(7))]),
    );
    storage.register_index_descriptor(&users, "score").expect("users descriptor");
    storage.register_index_descriptor(&orders, "score").expect("shared descriptor");
    let before = storage.published_state();
    assert_eq!(storage.remove_index_descriptor(&users, "score"), Ok(true));
    assert_eq!(storage.remove_index_descriptor(&users, "score"), Ok(false));
    assert_eq!(storage.index_descriptor_state(&users, "score"), None);
    assert_eq!(storage.index_descriptor_state(&orders, "score"), Some(true));
    assert_eq!(storage.published_state(), before, "index cleanup creates no customer write");
    assert_eq!(storage.remove_index_descriptor(&orders, "score"), Ok(true));
    let writer = storage.writer.lock().expect("writer");
    let columns: i64 = writer.query_row(
        "SELECT count(*) FROM pragma_table_info('documents') WHERE name IN ('f:score#t','f:score#n')", [], |row| row.get(0)
    ).expect("physical columns");
    assert_eq!(columns, 0, "last deletion removes stored columns");
    drop(writer);
    commit_object(
        &mut storage,
        &tenant,
        "after-removal",
        object_document("doc-b", "users", &[("score", DocumentValue::Integer(7))]),
    );
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).expect("restart after retirement");
    assert!(storage.index_descriptors().expect("descriptors").is_empty());
    assert_eq!(
        storage.register_index_descriptor(&users, "score"),
        Ok(DescriptorRegistration::Active)
    );
    let latest = storage.published_state().0.get();
    assert_eq!(
        filtered(&storage, latest, &equals("score", DocumentValue::Integer(7)))
            .expect("recreated index")
            .len(),
        2
    );
    drop(storage);
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn descriptor_removal_refuses_hidden_dependencies_atomically() {
    let (dir, mut storage) = sqlite_store("descriptor-remove-hidden");
    let users = scan_users();
    storage.register_index_descriptor(&users, "score").expect("descriptor");
    storage
        .begin_invisible_index(
            &scan_tenant(),
            &users,
            "candidate",
            &[crate::storage::InvisibleIndexColumn {
                field: "score".into(),
                kind: crate::storage::InvisibleColumnKind::Numeric,
            }],
        )
        .expect("hidden candidate");
    assert_eq!(
        storage.remove_index_descriptor(&users, "score"),
        Err(crate::storage::IndexDescriptorRemovalError::InUse)
    );
    assert_eq!(storage.index_descriptor_state(&users, "score"), Some(true));
    drop(storage);
    let storage = SqliteStorage::open(&dir).expect("restart after refusal");
    assert_eq!(storage.index_descriptor_state(&users, "score"), Some(true));
    drop(storage);
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn descriptor_removal_releases_the_column_budget() {
    let (dir, mut storage) = sqlite_store("descriptor-remove-budget");
    let users = scan_users();
    for position in 0..32 {
        storage.register_index_descriptor(&users, &format!("field{position}")).expect("descriptor");
    }
    assert_eq!(
        storage.register_index_descriptor(&users, "replacement"),
        Err(IndexDescriptorError::ColumnBudgetExceeded)
    );
    assert_eq!(storage.remove_index_descriptor(&users, "field0"), Ok(true));
    assert_eq!(
        storage.register_index_descriptor(&users, "replacement"),
        Ok(DescriptorRegistration::Active)
    );
    drop(storage);
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn descriptor_removal_rolls_back_both_columns_and_cache_after_second_ddl_failure() {
    let (dir, mut storage) = sqlite_store("descriptor-remove-rollback");
    let users = scan_users();
    let tenant = scan_tenant();
    storage.register_index_descriptor(&users, "score").expect("descriptor");
    storage
        .writer
        .lock()
        .expect("writer")
        .execute_batch("CREATE INDEX test_extra_numeric_dependency ON documents (\"f:score#n\")")
        .expect("independent numeric dependency");
    assert_eq!(
        storage.remove_index_descriptor(&users, "score"),
        Err(crate::storage::IndexDescriptorRemovalError::Storage)
    );
    assert_eq!(storage.index_descriptor_state(&users, "score"), Some(true));
    commit_object(
        &mut storage,
        &tenant,
        "after-rollback",
        object_document("doc-a", "users", &[("score", DocumentValue::Integer(7))]),
    );
    let latest = storage.published_state().0.get();
    assert_eq!(
        filtered(&storage, latest, &equals("score", DocumentValue::Integer(7)))
            .expect("restored index")
            .len(),
        1
    );
    drop(storage);
    let storage = SqliteStorage::open(&dir).expect("restart after rolled-back DDL");
    assert_eq!(storage.index_descriptor_state(&users, "score"), Some(true));
    let connection = storage.writer.lock().expect("writer");
    let count: i64 = connection.query_row(
        "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name IN ('i:score#t', 'i:score#n')", [], |row| row.get(0),
    ).expect("restored indexes");
    assert_eq!(count, 2);
    drop(connection);
    drop(storage);
    std::fs::remove_dir_all(dir).expect("cleanup");
}
