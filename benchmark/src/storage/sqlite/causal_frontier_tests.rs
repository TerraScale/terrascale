//! Live cache parity, atomicity, and retained-history work bounds.
use super::*;
use crate::storage::intent::PrivateMetadataWrite;

fn database() -> Connection {
    let db = Connection::open_in_memory().unwrap();
    db.execute_batch(SCHEMA).unwrap();
    db
}

fn evidence(db: &Connection, id: i64, sequence: u64, hlc: u64) {
    db.execute(
        "INSERT INTO documents(tenant,collection,id,commit_seq,commit_ulid,revision,requested,digest,version,deleted,body,origin,origin_seq,hlc)
         VALUES('t','docs',?1,?1,'op','r',0,0,1,0,X'00','remote',?2,?3)",
        rusqlite::params![id, sequence as i64, hlc as i64],
    ).unwrap();
    advance_causal_frontier(db, "t", "remote", sequence, hlc).unwrap();
}

fn parity(db: &Connection, sequence: u64, hlc: u64) {
    let live = live_origin_prefixes_on(db, "t").unwrap();
    assert_eq!(live, origin_prefixes_on(db, "t").unwrap());
    if sequence == 0 {
        assert!(live.is_empty());
    } else {
        assert_eq!(live.len(), 1);
        assert_eq!((live[0].origin_sequence, live[0].hlc), (sequence, hlc));
    }
}

#[test]
fn gaps_out_of_order_and_multirow_unsigned_hlc_match_reconstruction() {
    let db = database();
    evidence(&db, 1, 3, 90);
    parity(&db, 0, 0);
    evidence(&db, 2, 1, 20);
    parity(&db, 1, 20);
    evidence(&db, 3, 3, u64::MAX);
    parity(&db, 1, 20);
    evidence(&db, 4, 2, 40);
    parity(&db, 3, u64::MAX);
    initialize_causal_frontiers(&db).unwrap();
    parity(&db, 3, u64::MAX);
    // A later row in an already contiguous source transaction raises HLC.
    let db = database();
    evidence(&db, 1, 1, 20);
    evidence(&db, 2, 1, 1_u64 << 63);
    evidence(&db, 3, 1, u64::MAX);
    parity(&db, 1, u64::MAX);
    initialize_causal_frontiers(&db).unwrap();
    parity(&db, 1, u64::MAX);
}

#[test]
fn late_evidence_below_pruning_checkpoint_and_max_sequence_match_rebuild() {
    let db = database();
    evidence(&db, 1, 1, 20);
    db.execute("INSERT INTO origin_prefixes SELECT * FROM causal_frontiers", []).unwrap();
    evidence(&db, 2, 1, u64::MAX);
    parity(&db, 1, 20);
    initialize_causal_frontiers(&db).unwrap();
    parity(&db, 1, 20);
    evidence(&db, 3, 2, 30);
    parity(&db, 2, 30);
    let maximum = i64::MAX as u64;
    db.execute("UPDATE origin_prefixes SET seq=?1,hlc=30", [i64::MAX - 1]).unwrap();
    initialize_causal_frontiers(&db).unwrap();
    evidence(&db, 4, maximum, 50);
    parity(&db, maximum, 50);
    initialize_causal_frontiers(&db).unwrap();
    parity(&db, maximum, 50);
    assert!(advance_causal_frontier(&db, "t", "remote", maximum + 1, 50).is_err());
}

#[test]
fn savepoint_and_transaction_rollback_remove_frontier_advancement() {
    let mut db = database();
    evidence(&db, 1, 1, 20);
    {
        let tx = db.transaction().unwrap();
        tx.execute_batch("SAVEPOINT admission").unwrap();
        evidence(&tx, 2, 2, 40);
        parity(&tx, 2, 40);
        tx.execute_batch("ROLLBACK TO admission; RELEASE admission").unwrap();
        parity(&tx, 1, 20);
        evidence(&tx, 3, 2, 50);
        tx.rollback().unwrap();
    }
    parity(&db, 1, 20);
}

#[test]
fn frontier_queries_have_bounded_logical_work_with_retained_history() {
    let db = database();
    db.execute_batch(
        "WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<10000)
         INSERT INTO documents(tenant,collection,id,commit_seq,commit_ulid,revision,requested,digest,version,deleted,body,origin,origin_seq,hlc)
         SELECT 't','docs',i,i,'op','r',0,0,1,0,X'00','remote',i,i FROM n;"
    ).unwrap();
    initialize_causal_frontiers(&db).unwrap();
    for sql in [
        LIVE_ORIGIN_PREFIXES_SQL,
        "SELECT origin,seq FROM causal_frontiers WHERE tenant=?1 ORDER BY origin",
        "SELECT hlc FROM documents WHERE tenant=?1 AND origin='remote' AND origin_seq=10001",
        "SELECT hlc FROM replication_publications WHERE tenant=?1 AND origin='remote' AND origin_seq=10001",
    ] {
        let mut statement = db.prepare(sql).unwrap();
        let mut rows = statement.query(["t"]).unwrap();
        while rows.next().unwrap().is_some() {}
        drop(rows);
        assert_eq!(statement.get_status(rusqlite::StatementStatus::FullscanStep), 0, "{sql}");
        assert!(statement.get_status(rusqlite::StatementStatus::VmStep) < 100, "{sql}");
    }
    evidence(&db, 10001, 10001, 10001);
    parity(&db, 10001, 10001);
}

fn location(tag: &str) -> PathBuf {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "terrabase-causal-{tag}-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ))
}

fn intent(operation: &str, writes: Vec<StagedWrite>) -> CommitIntent {
    CommitIntent {
        tenant: TenantId::try_new("t").unwrap(),
        operation: OperationId::try_new(operation).unwrap(),
        digest: 1,
        snapshot_sequence: StorageSequence::new(0),
        durability: LocalDurability::Synced,
        usage_admission: None,
        budget_allocation: None,
        internal_maintenance: false,
        shard_copy: false,
        expected_origin_epoch: Some(0),
        expected_uniform_context: None,
        merge_conflict: None,
        replication_fingerprint: None,
        replication_publication: None,
        hlc: 30,
        metadata: if writes.is_empty() {
            vec![PrivateMetadataWrite {
                key: operation.into(),
                value: Some(DocumentValue::String("private".into())),
            }]
        } else {
            Vec::new()
        },
        schema_envelopes: Vec::new(),
        writes,
    }
}

fn put(id: &str) -> StagedWrite {
    StagedWrite::Put {
        document: Document::new(
            DocumentId::try_new(id).unwrap(),
            CollectionName::try_new("docs").unwrap(),
            DocumentValue::String("value".into()),
        ),
        expected: ExpectedVersion::New,
    }
}

#[test]
fn real_commit_wave_private_writes_replay_and_reopen_preserve_frontier() {
    let dir = location("wave");
    let tenant = TenantId::try_new("t").unwrap();
    let mut store = SqliteStorage::open(&dir).unwrap();
    let first = intent("one", vec![put("one"), put("two")]);
    let results = store.commit_batch(
        vec![first.clone(), first, intent("private", vec![]), intent("three", vec![put("three")])],
        false,
    );
    assert!(results.iter().all(|result| matches!(result, CommitResult::Committed(_))));
    let live = store.live_origin_prefixes(&tenant).unwrap();
    assert_eq!(live, store.durable_origin_prefixes(&tenant).unwrap());
    assert_eq!(live[0].origin_sequence, 3);
    let context: Vec<u8> = store
        .raw_writer_connection()
        .query_row(
            "SELECT context FROM replication_publications WHERE tenant='t' AND origin_seq=3",
            [],
            |row| row.get(0),
        )
        .unwrap();
    let mut expected = vec![1];
    codec::push_u32(&mut expected, 1);
    codec::push_str(&mut expected, &live[0].origin);
    codec::push_u64(&mut expected, 2);
    assert_eq!(context, expected);
    drop(store);
    let store = SqliteStorage::open(&dir).unwrap();
    assert_eq!(store.live_origin_prefixes(&tenant).unwrap(), live);
    drop(store);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn failed_real_commit_rolls_back_publication_and_frontier() {
    let dir = location("failure");
    let mut store = SqliteStorage::open(&dir).unwrap();
    store.raw_writer_connection().execute_batch("CREATE TRIGGER refuse_doc BEFORE INSERT ON documents BEGIN SELECT RAISE(ABORT,'failure'); END;").unwrap();
    assert!(matches!(store.commit(intent("fail", vec![put("doc")])), CommitResult::Rejected(_)));
    let tenant = TenantId::try_new("t").unwrap();
    assert!(store.live_origin_prefixes(&tenant).unwrap().is_empty());
    assert!(store.durable_origin_prefixes(&tenant).unwrap().is_empty());
    drop(store);
    let store = SqliteStorage::open(&dir).unwrap();
    assert!(store.live_origin_prefixes(&tenant).unwrap().is_empty());
    drop(store);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn engine_per_record_replication_updates_session_frontier_across_gaps_and_reopen() {
    use crate::engine::{Engine, EngineConfig, ReplicatedRecord};
    use crate::{origin_lane::OriginLaneId, types::{RegionId, TenantScope}};
    let dir = location("engine");
    let engine = Engine::open(EngineConfig::durable(&dir).unwrap()).unwrap();
    let tenant = TenantId::try_new("t").unwrap();
    let scope = TenantScope::from_parts(tenant.clone(), engine.identity());
    let origin = OriginLaneId::try_new(RegionId::try_new("remote").unwrap(), 1).unwrap();
    for (sequence, expected) in [(3, 0), (1, 1), (2, 3)] {
        let record = ReplicatedRecord {
            collection: CollectionName::try_new("docs").unwrap(),
            id: DocumentId::try_new(&format!("doc-{sequence}")).unwrap(),
            body: DocumentValue::String("value".into()),
            deleted: false,
            commit_ulid: format!("remote-{sequence}"),
            hlc: sequence,
            origin: origin.as_str(),
            origin_sequence: sequence,
            context: vec![1, 0, 0, 0, 0],
        };
        engine.apply_replicated(&scope, &record).unwrap();
        engine.apply_replicated(&scope, &record).unwrap();
        assert_eq!(engine.session_frontier(&scope).unwrap().sequence(&origin), expected);
        assert_eq!(
            engine.durable_origin_prefixes(&scope).unwrap().get("remote").copied().unwrap_or(0),
            expected
        );
    }
    drop(engine);
    let engine = Engine::open(EngineConfig::durable(&dir).unwrap()).unwrap();
    let scope = TenantScope::from_parts(tenant, engine.identity());
    assert_eq!(engine.session_frontier(&scope).unwrap().sequence(&origin), 3);
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
}
