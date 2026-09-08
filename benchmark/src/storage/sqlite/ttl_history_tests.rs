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
    let (path, engine) = open("ttl-usage-restart");
    let tenants = [TenantId::try_new("expired").unwrap(), TenantId::try_new("live").unwrap()];
    for tenant in &tenants {
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
    }
    let expired = engine.scope_for(&tenants[0]);
    let live = engine.scope_for(&tenants[1]);
    put(&engine, &expired, "sessions", "seed", 1);
    put(&engine, &live, "sessions", "seed", i64::MAX);
    assert_eq!(engine.sweep_expired_ttl(16).unwrap(), 1);
    assert_eq!(usage(&engine, &expired), 2);
    assert_eq!(usage(&engine, &live), 2);
    let collection = CollectionName::try_new("sessions").unwrap();
    let id = DocumentId::try_new("same-id").unwrap();
    let history = engine.document_history_in_collection(&expired, &collection, &id, 16).unwrap();
    assert_eq!(history.len(), 2);
    assert_eq!(history.iter().filter(|entry| entry.deleted).count(), 1);
    assert_eq!(
        engine
            .snapshot_documents_in_collection(
                &live,
                &engine.read_snapshot(&live).unwrap(),
                &collection
            )
            .unwrap()
            .len(),
        1
    );
    engine.shutdown().unwrap();

    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let expired = engine.scope_for(&tenants[0]);
    let live = engine.scope_for(&tenants[1]);
    assert_eq!(engine.sweep_expired_ttl(16).unwrap(), 0);
    assert_eq!(usage(&engine, &expired), 2);
    let mut transaction = engine
        .begin(
            &live,
            engine.read_snapshot(&live).unwrap(),
            OperationId::try_new("customer-delete").unwrap(),
        )
        .unwrap();
    transaction.delete_in_collection(&collection, id, ExpectedVersion::Any);
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    assert_eq!(usage(&engine, &live), 4);
    assert_eq!(usage(&engine, &expired), 2);
    engine.shutdown().unwrap();
    let storage = SqliteStorage::open(&path).unwrap();
    let writer = storage.raw_writer_connection();
    for tenant in &tenants {
        let (stored, billed): (i64, i64) = writer
            .query_row(
                "SELECT (SELECT SUM(LENGTH(body)) FROM documents WHERE tenant = ?1 AND deleted = 0),
                        bytes FROM storage_accounting_gauges WHERE tenant = ?1",
                [tenant.as_str()],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .unwrap();
        assert!(stored > 0);
        assert_eq!(stored, billed, "expiry and deletion retain billable history");
    }
    drop(writer);
    drop(storage);
    std::fs::remove_dir_all(path).unwrap();
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
