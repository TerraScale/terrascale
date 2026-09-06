use super::*;
use crate::regions::RegionCode;
use crate::usage::{UsagePlacement, UsagePageRequest, MAX_USAGE_PAGE_SIZE};

fn setup() -> (std::path::PathBuf, Engine, TenantScope) {
    let path = std::env::temp_dir().join(format!("terrabase-write-usage-{}", crate::identity::process_generator().generate().to_text()));
    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let tenant = TenantId::try_new("usage-tenant").unwrap();
    let scope = engine.scope_for(&tenant);
    engine.configure_usage_placement(UsagePlacement::try_new(
        tenant,"project","database","partition",RegionCode::GRU,vec![RegionCode::GRU,RegionCode::FRA],"revision-one",
    ).unwrap()).unwrap();
    (path,engine,scope)
}

fn write(engine: &Engine, scope: &TenantScope, operation: &str) -> CommitOutcome {
    let snapshot = engine.read_snapshot(scope).unwrap();
    let mut transaction = engine.begin(scope,snapshot,OperationId::try_new(operation).unwrap()).unwrap();
    transaction.put(Document::new(
        DocumentId::try_new("one").unwrap(),CollectionName::try_new("users").unwrap(),
        DocumentValue::Object(std::collections::BTreeMap::from([("value".into(),DocumentValue::String("small".into()))])),
        ExpectedVersion::Any,
    ).unwrap();
    transaction.commit(LocalDurability::Synced)
}

#[test]
fn automatic_write_usage_is_atomic_region_adjusted_idempotent_and_durable() {
    let (path,engine,scope) = setup();
    assert!(matches!(write(&engine,&scope,"write-one"),CommitOutcome::Committed(_)));
    assert!(matches!(write(&engine,&scope,"write-one"),CommitOutcome::Committed(_)));
    let page = engine.usage_page(&scope,UsagePageRequest::try_new(None,MAX_USAGE_PAGE_SIZE,None).unwrap()).unwrap();
    assert_eq!(page.facts().len(),1);
    assert_eq!(page.facts()[0].fact().delta(),2);
    assert_eq!(page.facts()[0].sequence(),engine.published_sequence().unwrap().get());
    assert_eq!(page.facts()[0].fact().scope().database_id().as_str(),"database");
    engine.shutdown().unwrap();
    drop(engine);
    let reopened = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let scope = reopened.scope_for(&TenantId::try_new("usage-tenant").unwrap());
    assert!(matches!(write(&reopened,&scope,"write-one"),CommitOutcome::Committed(_)));
    let page = reopened.usage_page(&scope,UsagePageRequest::try_new(None,MAX_USAGE_PAGE_SIZE,None).unwrap()).unwrap();
    assert_eq!(page.facts().len(),1);
    assert_eq!(page.facts()[0].fact().delta(),2);
    reopened.shutdown().unwrap();
    drop(reopened);
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn placement_cannot_be_rebound_or_claim_prebinding_writes() {
    let (path,engine,scope) = setup();
    let mut changed = engine.usage_placement(&scope).unwrap().unwrap();
    changed.selected_regions = vec![RegionCode::GRU];
    assert!(engine.configure_usage_placement(changed).is_err());
    engine.shutdown().unwrap();
    drop(engine);
    std::fs::remove_dir_all(path).unwrap();

    let path = std::env::temp_dir().join(format!("terrabase-unbound-usage-{}",crate::identity::process_generator().generate().to_text()));
    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let tenant = TenantId::try_new("usage-tenant").unwrap();
    let scope = engine.scope_for(&tenant);
    assert!(matches!(write(&engine,&scope,"unbound"),CommitOutcome::Committed(_)));
    assert!(engine.configure_usage_placement(UsagePlacement::try_new(tenant,"project","database","partition",RegionCode::GRU,vec![RegionCode::GRU],"rev").unwrap()).is_err());
    engine.shutdown().unwrap();
    drop(engine);
    std::fs::remove_dir_all(path).unwrap();
}
