use super::*;
use std::collections::BTreeMap;
use crate::schema_api::{
    CollectionSchema, DatabaseIdentity, DurableSchemaRecord, SchemaDocument, SchemaTarget,
};

fn record(previous_schema: Option<CollectionSchema>, schema_version: u64) -> DurableSchemaRecord {
    let schema: CollectionSchema = serde_json::from_value(serde_json::json!({
        "name": "orders",
        "fields": [{"name": "headline", "type": "string"}],
        "renames": [{"from": "title", "to": "headline"}],
        "version": schema_version
    }))
    .expect("test schema is valid");
    DurableSchemaRecord {
        document: SchemaDocument {
            target: SchemaTarget {
                database: "demo".into(),
                branch: "main".into(),
                collection: "orders".into(),
            },
            identity: DatabaseIdentity {
                database_id: "database-1".into(),
                placement: vec!["GRU".into()],
            },
            schema,
            generation: 2,
            schema_version,
            migration: None,
        },
        previous_schema,
        migration_plan: None,
        rollback: Some(SchemaRollback { idempotency_key: "rollback-1".into(), generation: 2 }),
        idempotency: BTreeMap::default(),
    }
}

fn engine() -> terrabase::Engine {
    let root =
        std::env::temp_dir().join(format!("terrabase-schema-recovery-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    terrabase::Engine::open(terrabase::EngineConfig::durable(root).expect("durable config"))
        .expect("engine opens")
}

#[test]
fn advance_rejects_rollback_without_previous_schema() {
    let engine = engine();
    let mut record = record(None, 2);

    let error = advance(&engine, &mut record)
        .expect_err("rollback without a source schema must fail closed");
    assert_eq!(error, "rename migration has no previous schema");
}

#[test]
fn advance_rejects_invalid_previous_schema_version() {
    let engine = engine();
    let previous = serde_json::from_value(serde_json::json!({
        "name": "orders",
        "fields": [{"name": "title", "type": "string"}],
        "version": 0
    }))
    .expect("test schema is valid");
    let mut record = record(Some(previous), 2);

    let error =
        advance(&engine, &mut record).expect_err("invalid source versions must fail closed");
    assert_eq!(error, "previous schema version is invalid");
}
