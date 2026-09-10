use super::*;
use crate::{
    CommitOutcome, Engine, EngineConfig,
    operations::{QueryInput, SqlQueryText},
    query::routed::{RoutedReadCut, RoutedReadParticipant},
    regions::RegionCode,
    shard_split::{FileShardIdentity, ShardRoute},
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    },
};
use std::{collections::BTreeMap, sync::Arc};

#[test]
fn lowered_routed_sql_uses_typed_empty_cut_after_delete() {
    let root = std::env::temp_dir().join(format!("routed-lowered-{}", crate::identity::generate()));
    let engine = Arc::new(Engine::open(EngineConfig::durable(&root).unwrap()).unwrap());
    let tenant = TenantId::try_new("tenant").unwrap();
    let scope = engine.scope_for(&tenant);
    let collection = CollectionName::try_new("records").unwrap();
    let id = DocumentId::try_new("record").unwrap();
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction =
        engine.begin(&scope, snapshot, OperationId::try_new("put").unwrap()).unwrap();
    transaction
        .put(
            Document::new(
                id.clone(),
                collection.clone(),
                DocumentValue::Object(BTreeMap::from([(
                    "value".into(),
                    DocumentValue::Integer(1),
                )])),
            ),
            ExpectedVersion::New,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction =
        engine.begin(&scope, snapshot, OperationId::try_new("delete").unwrap()).unwrap();
    transaction.delete_in_collection(&collection, id, ExpectedVersion::Any);
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let participant = RoutedReadParticipant::new(
        FileShardIdentity {
            database_incarnation: 1,
            shard_id: 1,
            region: RegionCode::GRU,
            origin_lane_incarnation: 1,
        },
        Arc::clone(&engine),
        snapshot,
        vec![ShardRoute::new(1, None, None).unwrap()],
    )
    .unwrap();
    let cut = RoutedReadCut::new(1, 1, vec![participant]).unwrap();
    let plan = engine
        .queries()
        .compile(QueryInput::sql(
            SqlQueryText::try_new("SELECT _id, value FROM records ORDER BY _id").unwrap(),
        ))
        .unwrap();
    let runtime = plan.sql_runtime.as_deref().unwrap();
    let result = execute_routed(&cut, &plan, runtime).unwrap();
    assert!(result.rows().is_empty());
    drop(engine);
    std::fs::remove_dir_all(root).unwrap();
}
