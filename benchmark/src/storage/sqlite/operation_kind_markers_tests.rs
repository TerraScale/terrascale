use super::*;
use crate::storage::{
    StoragePort,
    intent::{CommitIntent, CommitResult, PrivateMetadataWrite, StagedWrite},
};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};
use rusqlite::Connection;

fn causal_context(origin: &str, sequence: u64) -> Vec<u8> {
    let mut bytes = vec![1];
    bytes.extend_from_slice(&1_u32.to_le_bytes());
    bytes.extend_from_slice(&(origin.len() as u32).to_le_bytes());
    bytes.extend_from_slice(origin.as_bytes());
    bytes.extend_from_slice(&sequence.to_le_bytes());
    bytes
}

fn metadata_intent(operation: &str, maintenance: bool, writes: Vec<StagedWrite>) -> CommitIntent {
    CommitIntent {
        tenant: TenantId::try_new("marker-tenant").unwrap(),
        operation: OperationId::try_new(operation).unwrap(),
        digest: 1,
        snapshot_sequence: StorageSequence::new(0),
        durability: LocalDurability::Written,
        usage_admission: None,
        budget_allocation: None,
        internal_maintenance: maintenance,
        shard_copy: false,
        expected_origin_epoch: Some(0),
        expected_uniform_context: None,
        merge_conflict: None,
        replication_fingerprint: None,
        replication_publication: None,
        hlc: 0,
        metadata: if !maintenance && writes.is_empty() {
            vec![PrivateMetadataWrite {
                key: "marker-key".into(),
                value: Some(DocumentValue::String("marker-value".into())),
            }]
        } else {
            Vec::new()
        },
        schema_envelopes: Vec::new(),
        writes,
    }
}

#[test]
fn metadata_and_maintenance_commits_record_distinct_markers_and_replay_after_reopen() {
    let dir =
        std::env::temp_dir().join(format!("terrabase-marker-generation-{}", std::process::id()));
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let metadata_receipt =
        match storage.commit(metadata_intent("metadata-marker", false, Vec::new())) {
            CommitResult::Committed(receipt) => receipt,
            other => panic!("metadata commit failed: {other:?}"),
        };
    let write = StagedWrite::Put {
        document: Document::new(
            DocumentId::try_new("maintenance-doc").unwrap(),
            CollectionName::try_new("docs").unwrap(),
            DocumentValue::String("x".into()),
        ),
        expected: ExpectedVersion::New,
    };
    let maintenance_receipt =
        match storage.commit(metadata_intent("maintenance-marker", true, vec![write.clone()])) {
            CommitResult::Committed(receipt) => receipt,
            other => panic!("maintenance commit failed: {other:?}"),
        };
    let rows: Vec<(String, String)> = storage
        .raw_writer_connection()
        .prepare("SELECT operation,kind FROM operation_kind_markers ORDER BY operation")
        .unwrap()
        .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
        .unwrap()
        .collect::<Result<_, _>>()
        .unwrap();
    assert_eq!(
        rows,
        vec![
            ("maintenance-marker".into(), "internal_maintenance".into()),
            ("metadata-marker".into(), "metadata_only".into())
        ]
    );
    drop(storage);
    let mut reopened = SqliteStorage::open(&dir).unwrap();
    assert!(
        matches!(reopened.commit(metadata_intent("metadata-marker", false, Vec::new())), CommitResult::Committed(receipt) if receipt.sequence == metadata_receipt.sequence)
    );
    assert!(
        matches!(reopened.commit(metadata_intent("maintenance-marker", true, vec![write])), CommitResult::Committed(receipt) if receipt.sequence == maintenance_receipt.sequence)
    );
    let count: i64 = reopened
        .raw_writer_connection()
        .query_row("SELECT count(*) FROM operation_kind_markers", [], |row| row.get(0))
        .unwrap();
    assert_eq!(count, 2);
    drop(reopened);
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn replicated_commit_marker_uses_binding_identity_and_replays_same_counter() {
    use crate::engine::{Engine, EngineConfig, ReplicatedPublication, ReplicatedRecord};
    use crate::types::{TenantId, TenantScope};
    let dir =
        std::env::temp_dir().join(format!("terrabase-marker-replication-{}", std::process::id()));
    let engine = Engine::open(EngineConfig::durable(&dir).unwrap()).unwrap();
    let scope = TenantScope::from_parts(
        TenantId::try_new("marker-repl-tenant").unwrap(),
        engine.identity(),
    );
    let publication = ReplicatedPublication {
        origin: "group-peer".into(),
        origin_sequence: 1,
        hlc: 9,
        commit_ulid: "source-operation-7".into(),
        context: Vec::new(),
        records: vec![ReplicatedRecord {
            collection: CollectionName::try_new("docs").unwrap(),
            id: DocumentId::try_new("replicated-doc").unwrap(),
            body: DocumentValue::String("value".into()),
            deleted: false,
            commit_ulid: "source-operation-7".into(),
            hlc: 9,
            origin: "group-peer".into(),
            origin_sequence: 1,
            context: Vec::new(),
        }],
        private_payload: Vec::new(),
    };
    let first = engine.apply_replicated_publication(&scope, &publication).unwrap();
    let second = engine.apply_replicated_publication(&scope, &publication).unwrap();
    assert_eq!(first.receipt().unwrap().sequence(), second.receipt().unwrap().sequence());
    engine.shutdown().unwrap();
    let reopened = Engine::open(EngineConfig::durable(&dir).unwrap()).unwrap();
    let reopened_scope = reopened.scope_for(scope.tenant());
    let replayed = reopened.apply_replicated_publication(&reopened_scope, &publication).unwrap();
    assert_eq!(replayed.receipt().unwrap().sequence(), first.receipt().unwrap().sequence());
    reopened.shutdown().unwrap();
    let raw = Connection::open(dir.join("terrabase.sqlite3")).unwrap();
    let count: i64 = raw
        .query_row(
            "SELECT count(*) FROM operation_kind_markers WHERE kind='replication'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(count, 1);
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn branch_transform_moves_marked_fact_to_import_receipt_and_rebranch_has_no_old_marker() {
    let mut db = Connection::open_in_memory().unwrap();
    db.execute_batch(SCHEMA).unwrap();
    repair_summary::initialize(&db).unwrap();
    storage_closure::initialize(&db).unwrap();
    accounting::initialize_at(&db, 1).unwrap();
    db.execute_batch("INSERT INTO meta VALUES('root_head','source-root');
        INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,root,writes,bytes)
        VALUES('tenant','metadata-marker',1,1,1,'source-root',0,0);
        INSERT INTO operation_kind_markers VALUES('tenant','metadata-marker','metadata_only');").unwrap();
    validate_operation_facts(&db).unwrap();
    let owner = crate::storage::DatabaseOwner::local_development();
    let transaction = db.transaction().unwrap();
    assert_eq!(branch_provenance::transform_provenance(&transaction, &owner), Ok(1));
    transaction.commit().unwrap();
    validate_operation_facts(&db).unwrap();
    assert_eq!(
        db.query_row("SELECT COUNT(*) FROM operation_kind_markers", [], |row| row.get::<_, i64>(0))
            .unwrap(),
        0
    );
    assert_eq!(
        db.query_row("SELECT COUNT(*) FROM branch_snapshot_receipts", [], |row| row
            .get::<_, i64>(0))
            .unwrap(),
        2
    );
    let owner = crate::storage::DatabaseOwner::local_development();
    let transaction = db.transaction().unwrap();
    assert_eq!(branch_provenance::transform_provenance(&transaction, &owner), Ok(1));
    transaction.commit().unwrap();
    validate_operation_facts(&db).unwrap();
    assert_eq!(
        db.query_row("SELECT COUNT(*) FROM operation_kind_markers", [], |row| row.get::<_, i64>(0))
            .unwrap(),
        0
    );
}

fn invalid_marker(fact: &str, marker: &str, extra: &str) -> bool {
    let connection = Connection::open_in_memory().unwrap();
    connection.execute_batch(SCHEMA).unwrap();
    connection.execute_batch(fact).unwrap();
    connection.execute_batch(marker).unwrap();
    connection.execute_batch(extra).unwrap();
    validate_operation_facts(&connection).is_err()
}

#[test]
fn marker_corruption_fails_closed_for_orphan_rejected_unknown_positive_and_imported_rows() {
    let committed = "INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,root,writes,bytes) VALUES('t','op',1,1,1,'root',0,0)";
    assert!(!invalid_marker(
        committed,
        "INSERT INTO operation_kind_markers VALUES('t','op','metadata_only')",
        ""
    ));
    assert!(invalid_marker(
        committed,
        "INSERT INTO operation_kind_markers VALUES('t','missing','metadata_only')",
        ""
    ));
    let rejected = "INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,root,writes,bytes) VALUES('t','op',1,0,0,'',0,0)";
    assert!(invalid_marker(
        rejected,
        "INSERT INTO operation_kind_markers VALUES('t','op','metadata_only')",
        ""
    ));
    assert!(invalid_marker(
        committed,
        "",
        "DROP TABLE operation_kind_markers; CREATE TABLE operation_kind_markers(tenant TEXT, operation TEXT, kind TEXT); INSERT INTO operation_kind_markers VALUES('t','op','unknown')"
    ));
    assert!(invalid_marker(
        committed,
        "INSERT INTO operation_kind_markers VALUES('t','op','metadata_only')",
        "UPDATE operation_facts SET writes=1 WHERE client_request_ulid='op'"
    ));
    assert!(invalid_marker(
        committed,
        "INSERT INTO operation_kind_markers VALUES('t','op','metadata_only')",
        "INSERT INTO branch_snapshot_receipts VALUES('t','op')"
    ));
}

#[test]
fn ordinary_billable_fact_mutated_to_zero_writes_fails_validation() {
    let connection = Connection::open_in_memory().unwrap();
    connection.execute_batch(SCHEMA).unwrap();
    connection.execute("INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,root,writes,bytes) VALUES('t','billable',1,1,1,'root',2,10)", []).unwrap();
    connection
        .execute("UPDATE operation_facts SET writes=0 WHERE client_request_ulid='billable'", [])
        .unwrap();
    assert!(validate_operation_facts(&connection).is_err());
}
