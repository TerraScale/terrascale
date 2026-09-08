//! Retention and billing through the production document and sweep callers.
use super::SqliteStorage;
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    regions::RegionCode,
    storage::StoragePort,
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId, TenantScope,
    },
    usage::{MAX_USAGE_PAGE_SIZE, UsagePageRequest, UsagePlacement},
};

fn open(tag: &str) -> (std::path::PathBuf, Engine) {
    let path =
        std::env::temp_dir().join(format!("terrabase-{tag}-{}", crate::identity::generate()));
    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    (path, engine)
}

fn put(engine: &Engine, scope: &TenantScope, collection: &str, operation: &str, expiry: i64) {
    let mut transaction = engine
        .begin(
            scope,
            engine.read_snapshot(scope).unwrap(),
            OperationId::try_new(operation).unwrap(),
        )
        .unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("same-id").unwrap(),
                CollectionName::try_new(collection).unwrap(),
                DocumentValue::Object(
                    [("expires_at".to_owned(), DocumentValue::Integer(expiry))]
                        .into_iter()
                        .collect(),
                ),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
}

fn usage(engine: &Engine, scope: &TenantScope) -> i128 {
    engine
        .usage_page(scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap()
        .facts()
        .iter()
        .map(|fact| fact.fact().delta())
        .sum()
}

#[test]
fn real_ttl_sweep_is_unbilled_and_customer_delete_is_billed_after_restart() {
    for (tenant_name, expires_at, expected_sweep) in [("expired", 1, 1), ("live", i64::MAX, 0)] {
        // Placement belongs to one physical database. Separate files exercise
        // equal document IDs without attempting to rebind a database owner.
        let (path, engine) = open("ttl-usage-restart");
        let tenant = TenantId::try_new(tenant_name).unwrap();
        engine
            .configure_usage_placement(
                UsagePlacement::try_new(
                    tenant.clone(),
                    "project",
                    tenant.as_str(),
                    "partition",
                    RegionCode::GRU,
                    vec![RegionCode::GRU, RegionCode::FRA],
                    "placement",
                )
                .unwrap(),
            )
            .unwrap();
        let scope = engine.scope_for(&tenant);
        put(&engine, &scope, "sessions", "seed", expires_at);
        assert_eq!(engine.sweep_expired_ttl(16).unwrap(), expected_sweep);
        assert_eq!(usage(&engine, &scope), 2);
        let collection = CollectionName::try_new("sessions").unwrap();
        let id = DocumentId::try_new("same-id").unwrap();
        let history = engine.document_history_in_collection(&scope, &collection, &id, 16).unwrap();
        assert_eq!(history.iter().filter(|entry| entry.deleted).count(), expected_sweep as usize);
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&tenant);
        assert_eq!(engine.sweep_expired_ttl(16).unwrap(), 0);
        assert_eq!(usage(&engine, &scope), 2);
        if tenant_name == "live" {
            let mut transaction = engine
                .begin(
                    &scope,
                    engine.read_snapshot(&scope).unwrap(),
                    OperationId::try_new("customer-delete").unwrap(),
                )
                .unwrap();
            transaction.delete_in_collection(&collection, id, ExpectedVersion::Any);
            assert!(matches!(
                transaction.commit(LocalDurability::Synced),
                CommitOutcome::Committed(_)
            ));
            assert_eq!(usage(&engine, &scope), 4);
        }
        engine.shutdown().unwrap();
        let storage = SqliteStorage::open(&path).unwrap();
        let writer = storage.raw_writer_connection();
        let (stored, billed): (i64, i64) = writer.query_row(
            "SELECT (SELECT SUM(LENGTH(body)) FROM documents WHERE tenant = ?1 AND deleted = 0),
                    bytes FROM storage_accounting_gauges WHERE tenant = ?1",
            [tenant.as_str()], |row| Ok((row.get(0)?, row.get(1)?)),
        ).unwrap();
        assert!(stored > 0);
        assert_eq!(stored, billed, "expiry and deletion retain billable history");
        drop(writer);
        drop(storage);
        std::fs::remove_dir_all(path).unwrap();
    }
}

#[test]
fn undeclared_history_expires_after_seven_days_and_declared_history_survives_restart() {
    let (path, engine) = open("default-history-window");
    let tenant = TenantId::try_new("history").unwrap();
    let scope = engine.scope_for(&tenant);
    for collection in ["default-window", "long-window"] {
        put(&engine, &scope, collection, &format!("{collection}-one"), i64::MAX);
        put(&engine, &scope, collection, &format!("{collection}-two"), i64::MAX);
    }
    engine
        .set_collection_history_retention(&CollectionName::try_new("long-window").unwrap(), 30)
        .unwrap();
    engine.shutdown().unwrap();
    let mut storage = SqliteStorage::open(&path).unwrap();
    storage.advance_consumer_frontier("qualified-consumer", 5).unwrap();
    {
        let writer = storage.raw_writer_connection();
        writer
            .execute(
                "UPDATE documents SET committed_at = ?1",
                [super::unix_millis() - 8 * 24 * 60 * 60 * 1000],
            )
            .unwrap();
    }
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 1);
    for (collection, retained) in [("default-window", 1), ("long-window", 2)] {
        assert_eq!(
            storage
                .document_history_in_collection(
                    &tenant,
                    &CollectionName::try_new(collection).unwrap(),
                    &DocumentId::try_new("same-id").unwrap(),
                    16
                )
                .len(),
            retained
        );
    }
    drop(storage);
    let storage = SqliteStorage::open(&path).unwrap();
    assert_eq!(storage.prune_below_consumer_frontiers().unwrap(), 0);
    let writer = storage.raw_writer_connection();
    let stored_bytes: i64 = writer
        .query_row(
            "SELECT SUM(LENGTH(body)) FROM documents WHERE tenant = 'history' AND deleted = 0",
            [],
            |row| row.get(0),
        )
        .unwrap();
    let billed_bytes: i64 = writer
        .query_row(
            "SELECT bytes FROM storage_accounting_gauges WHERE tenant = 'history'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(
        billed_bytes, stored_bytes,
        "retained history remains in the logical storage gauge after restart"
    );
    assert!(billed_bytes > 0);
    drop(writer);
    drop(storage);
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn periodic_retention_limits_complete_publications_and_resumes_after_reopen() {
    let (path, engine) = open("bounded-retention");
    let tenant = TenantId::try_new("history").unwrap();
    let scope = engine.scope_for(&tenant);
    for generation in 0..3 {
        let mut transaction = engine
            .begin(
                &scope,
                engine.read_snapshot(&scope).unwrap(),
                OperationId::try_new(&format!("generation-{generation}")).unwrap(),
            )
            .unwrap();
        for id in ["first", "second"] {
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(id).unwrap(),
                        CollectionName::try_new("docs").unwrap(),
                        DocumentValue::Integer(generation),
                    ),
                    ExpectedVersion::Any,
                )
                .unwrap();
        }
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    }
    engine.shutdown().unwrap();
    let mut storage = SqliteStorage::open(&path).unwrap();
    storage.advance_consumer_frontier("consumer", 4).unwrap();
    storage.raw_writer_connection().execute("UPDATE documents SET committed_at = 0", []).unwrap();
    StoragePort::maintain_retention(&mut storage, 1).unwrap();
    let commits =
        storage.tenant_commits(&tenant, crate::types::StorageSequence::new(1), 16).unwrap();
    assert_eq!(commits.len(), 2);
    assert!(commits.iter().all(|commit| commit.writes().len() == 2));
    drop(storage);
    let mut storage = SqliteStorage::open(&path).unwrap();
    StoragePort::maintain_retention(&mut storage, 1).unwrap();
    let commits =
        storage.tenant_commits(&tenant, crate::types::StorageSequence::new(2), 16).unwrap();
    assert_eq!(commits.len(), 1);
    assert_eq!(commits[0].writes().len(), 2);
    StoragePort::maintain_retention(&mut storage, 1).unwrap();
    assert_eq!(storage.documents_at(storage.published_state().0, &tenant).len(), 2);
    drop(storage);
    std::fs::remove_dir_all(path).unwrap();
}
