use super::*;
use crate::{
    CommitOutcome, EngineConfig,
    operations::{QueryInput, SqlQueryText},
    query::routed::{RoutedReadCut, RoutedReadParticipant},
    shard_split::{FileShardIdentity, ShardRoute},
    types::{CollectionName, DocumentId, ExpectedVersion, LocalDurability, OperationId, TenantId},
};

fn put(engine: &Engine, tenant: &TenantId, id: &str, body: &str) {
    let scope = engine.scope_for(tenant);
    let mut transaction = engine
        .begin(&scope, engine.read_snapshot(&scope).unwrap(), OperationId::try_new(id).unwrap())
        .unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new(id).unwrap(),
                CollectionName::try_new("records").unwrap(),
                DocumentValue::Object(BTreeMap::from([(
                    "body".to_owned(),
                    DocumentValue::String(body.to_owned()),
                )])),
            ),
            ExpectedVersion::New,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
}

#[test]
fn routed_match_uses_canonical_terms_and_global_page_over_owned_tenant_rows() {
    let root = std::env::temp_dir().join(format!("routed-match-{}", crate::identity::generate()));
    let source =
        Arc::new(Engine::open(EngineConfig::durable(root.join("source")).unwrap()).unwrap());
    let target =
        Arc::new(Engine::open(EngineConfig::durable(root.join("target")).unwrap()).unwrap());
    let tenant = TenantId::try_new("tenant").unwrap();
    put(&source, &tenant, "a-record", "quick brown");
    put(&source, &tenant, "b-record", "quick only");
    put(&source, &tenant, "z-stale", "quick brown");
    put(&target, &tenant, "y-record", "Quick BROWN");
    put(&target, &tenant, "z-record", "quick brown");
    put(&target, &TenantId::try_new("other").unwrap(), "x-private", "quick brown");
    let split = crate::shard_split::canonical_shard_key(
        &CollectionName::try_new("records").unwrap(),
        &DocumentId::try_new("m-record").unwrap(),
    );
    let participant = |engine: &Arc<Engine>, shard_id, route| {
        RoutedReadParticipant::new(
            FileShardIdentity {
                database_incarnation: 1,
                shard_id,
                region: crate::regions::RegionCode::GRU,
                origin_lane_incarnation: 1,
            },
            Arc::clone(engine),
            engine.read_snapshot(&engine.scope_for(&tenant)).unwrap(),
            vec![route],
        )
        .unwrap()
    };
    let mut cut = RoutedReadCut::new(
        1,
        1,
        vec![
            participant(&source, 1, ShardRoute::new(1, None, Some(split.clone())).unwrap()),
            participant(&target, 2, ShardRoute::new(2, Some(split), None).unwrap()),
        ],
    )
    .unwrap();
    let plan = source.queries().compile(QueryInput::sql(SqlQueryText::try_new(
        "SELECT _id AS id FROM records WHERE body MATCH 'quick brown' ORDER BY _id DESC LIMIT 1 OFFSET 1"
    ).unwrap())).unwrap();
    let runtime = plan.sql_runtime.as_deref().unwrap();
    let result = execute_routed(&cut, &plan, runtime).unwrap();
    assert_eq!(result.matched(), 3);
    assert_eq!(result.rows().len(), 1);
    assert_eq!(result.rows()[0].document().id().as_str(), "y-record");
    assert_eq!(
        result.rows()[0].projected(),
        &[("id".to_owned(), DocumentValue::String("y-record".to_owned()))]
    );
    cut.participants[1].snapshot =
        target.read_snapshot(&target.scope_for(&TenantId::try_new("other").unwrap())).unwrap();
    assert!(matches!(
        execute_routed(&cut, &plan, runtime),
        Err(QueryError::SnapshotTenantMismatch)
    ));
}
