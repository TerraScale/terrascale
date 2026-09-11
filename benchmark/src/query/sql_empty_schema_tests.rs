//! Focused regression tests for direct single-base execute-only SQL.
//!
//! Register this file as `sql_empty_schema_tests` in `query/mod.rs` during
//! integration. It intentionally has not been compiled in staging.

use super::*;
use crate::{
    CommitOutcome, Engine, EngineConfig,
    operations::{QueryInput, SqlQueryText},
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    },
};
use std::sync::Arc;

fn fixture(label: &str) -> (Arc<Engine>, TenantId, CollectionName, std::path::PathBuf) {
    let root =
        std::env::temp_dir().join(format!("sql-empty-{label}-{}", crate::identity::generate()));
    let engine = Arc::new(Engine::open(EngineConfig::durable(&root).unwrap()).unwrap());
    (
        Arc::clone(&engine),
        TenantId::try_new("tenant").unwrap(),
        CollectionName::try_new("records").unwrap(),
        root,
    )
}

fn execute(engine: &Engine, tenant: &TenantId, text: &str) -> Result<QueryResult, QueryError> {
    let plan = engine.queries().compile(QueryInput::sql(SqlQueryText::try_new(text).unwrap()))?;
    let scope = engine.scope_for(tenant);
    let snapshot = engine.read_snapshot(&scope).map_err(|_| QueryError::StorageFailure)?;
    engine.queries().execute(&plan, &snapshot)
}

fn put(
    engine: &Engine,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &str,
    value: DocumentValue,
) {
    let scope = engine.scope_for(tenant);
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction = engine
        .begin(&scope, snapshot, OperationId::try_new(&format!("put-{id}")).unwrap())
        .unwrap();
    transaction
        .put(
            Document::new(DocumentId::try_new(id).unwrap(), collection.clone(), value),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
}

fn object(fields: impl IntoIterator<Item = (&'static str, DocumentValue)>) -> DocumentValue {
    DocumentValue::Object(
        fields.into_iter().map(|(name, value)| (name.to_owned(), value)).collect(),
    )
}

#[test]
fn direct_field_collector_excludes_aliases_and_non_fields() {
    let parsed = sql::parse(
        "SELECT SUM(r.amount) AS total FROM records AS r WHERE r.status = :status ORDER BY total",
        16 * 1024,
    )
    .unwrap();
    assert_eq!(sql::direct_single_base_referenced_fields(&parsed), ["amount", "status"]);
}

#[test]
fn live_absent_field_is_null_and_unknown_alias_is_invalid_spec() {
    let (engine, tenant, collection, root) = fixture("live");
    put(&engine, &tenant, &collection, "one", object([("present", DocumentValue::Integer(1))]));
    let result = execute(&engine, &tenant, "SELECT r.missing_field FROM records AS r").unwrap();
    assert_eq!(result.rows().len(), 1);
    assert_eq!(result.rows()[0].projected()[0].1, DocumentValue::Null);
    assert!(matches!(
        execute(&engine, &tenant, "SELECT x.missing_field FROM records AS r"),
        Err(QueryError::InvalidSpec)
    ));
    drop(engine);
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn empty_aggregates_preserve_sqlite_cardinality_and_types() {
    let (engine, tenant, _collection, root) = fixture("aggregates");
    for (query, expected) in [
        ("SELECT COUNT(*) AS total FROM records", DocumentValue::Integer(0)),
        ("SELECT COUNT(missing_field) AS total FROM records", DocumentValue::Integer(0)),
        ("SELECT COALESCE(SUM(missing_field), 0) AS total FROM records", DocumentValue::Integer(0)),
    ] {
        let result = execute(&engine, &tenant, query).unwrap();
        assert_eq!(result.rows().len(), 1, "{query}");
        assert_eq!(result.rows()[0].projected()[0].1, expected, "{query}");
    }
    for function in ["SUM", "AVG", "MIN", "MAX"] {
        let query = format!("SELECT {function}(missing_field) AS total FROM records");
        let result = execute(&engine, &tenant, &query).unwrap();
        assert_eq!(result.rows().len(), 1, "{query}");
        assert_eq!(result.rows()[0].projected()[0].1, DocumentValue::Null, "{query}");
    }
    drop(engine);
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn all_null_aggregate_and_empty_projection_keep_cardinality() {
    let (engine, tenant, collection, root) = fixture("all-null");
    put(&engine, &tenant, &collection, "one", object([("other", DocumentValue::Integer(1))]));
    let result =
        execute(&engine, &tenant, "SELECT SUM(missing_field) AS total FROM records").unwrap();
    assert_eq!(result.rows().len(), 1);
    assert_eq!(result.rows()[0].projected()[0].1, DocumentValue::Null);
    let plan = engine
        .queries()
        .compile(QueryInput::sql(
            SqlQueryText::try_new("SELECT r.missing_field FROM records AS r").unwrap(),
        ))
        .unwrap();
    assert!(plan.sql_execute_only_operator().is_some());
    assert_eq!(
        execute(&engine, &tenant, "SELECT r.missing_field FROM records AS r").unwrap().rows().len(),
        1
    );
    let scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction =
        engine.begin(&scope, snapshot, OperationId::try_new("delete-all").unwrap()).unwrap();
    transaction.delete_in_collection(
        &collection,
        DocumentId::try_new("one").unwrap(),
        ExpectedVersion::Any,
    );
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    assert!(
        execute(&engine, &tenant, "SELECT r.missing_field FROM records AS r")
            .unwrap()
            .rows()
            .is_empty()
    );
    drop(engine);
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn pinned_snapshot_survives_delete_while_fresh_snapshot_is_empty() {
    let (engine, tenant, collection, root) = fixture("pinned");
    put(&engine, &tenant, &collection, "one", object([("value", DocumentValue::Integer(1))]));
    let scope = engine.scope_for(&tenant);
    let pinned = engine.read_snapshot(&scope).unwrap();
    let plan = engine
        .queries()
        .compile(QueryInput::sql(
            SqlQueryText::try_new("SELECT r.missing_field FROM records AS r").unwrap(),
        ))
        .unwrap();
    let mut transaction = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("delete").unwrap(),
        )
        .unwrap();
    transaction.delete_in_collection(
        &collection,
        DocumentId::try_new("one").unwrap(),
        ExpectedVersion::Any,
    );
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let fresh = engine.read_snapshot(&scope).unwrap();
    assert_eq!(engine.queries().execute(&plan, &pinned).unwrap().rows().len(), 1);
    assert!(engine.queries().execute(&plan, &fresh).unwrap().rows().is_empty());
    drop(engine);
    std::fs::remove_dir_all(root).unwrap();
}
