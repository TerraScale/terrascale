use crate::{
    engine::{Engine, EngineConfig},
    regions::RegionCode,
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    },
    usage::{MAX_USAGE_PAGE_SIZE, UsagePageRequest, UsagePlacement},
};

#[test]
fn per_database_rollup_applies_accepted_region_multiplier_once() {
    let path =
        std::env::temp_dir().join(format!("terrabase-metering-{}", crate::identity::generate()));
    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let tenant = TenantId::try_new("metering-tenant").unwrap();
    let scope = engine.scope_for(&tenant);
    engine
        .configure_usage_placement(
            UsagePlacement::try_new(
                tenant.clone(),
                "project",
                "database",
                "partition",
                RegionCode::GRU,
                vec![RegionCode::GRU, RegionCode::SFO, RegionCode::FRA],
                "metering-placement",
            )
            .unwrap(),
        )
        .unwrap();
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction =
        engine.begin(&scope, snapshot, OperationId::try_new("metering-put").unwrap()).unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("doc").unwrap(),
                CollectionName::try_new("items").unwrap(),
                DocumentValue::String("x".repeat(4097).into()),
            ),
            ExpectedVersion::New,
        )
        .unwrap();
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let usage = engine
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    assert_eq!(usage.facts()[0].fact().delta(), 6);
    assert_eq!(engine.metering_fact(&scope, tenant.as_str()).unwrap().writes(), 6);
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}
