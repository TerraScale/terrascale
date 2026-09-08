use super::*;
use crate::regions::RegionCode;
use crate::usage::{MAX_USAGE_PAGE_SIZE, UsagePageRequest, UsagePlacement};

fn setup() -> (std::path::PathBuf, Engine, TenantScope) {
    let path =
        std::env::temp_dir().join(format!("terrabase-write-usage-{}", crate::identity::generate()));
    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let tenant = TenantId::try_new("usage-tenant").unwrap();
    let scope = engine.scope_for(&tenant);
    engine
        .configure_usage_placement(
            UsagePlacement::try_new(
                tenant,
                "project",
                "database",
                "partition",
                RegionCode::GRU,
                vec![RegionCode::GRU, RegionCode::FRA],
                "revision-one",
            )
            .unwrap(),
        )
        .unwrap();
    (path, engine, scope)
}

fn write(engine: &Engine, scope: &TenantScope, operation: &str) -> CommitOutcome {
    let snapshot = engine.read_snapshot(scope).unwrap();
    let mut transaction =
        engine.begin(scope, snapshot, OperationId::try_new(operation).unwrap()).unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("one").unwrap(),
                CollectionName::try_new("users").unwrap(),
                DocumentValue::Object(std::collections::BTreeMap::from([(
                    "value".into(),
                    DocumentValue::String("small".into()),
                )])),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    transaction.commit(LocalDurability::Synced)
}

#[test]
fn automatic_write_usage_is_atomic_region_adjusted_idempotent_and_durable() {
    let (path, engine, scope) = setup();
    assert!(matches!(write(&engine, &scope, "write-one"), CommitOutcome::Committed(_)));
    assert!(matches!(write(&engine, &scope, "write-one"), CommitOutcome::Committed(_)));
    let page = engine
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    assert_eq!(page.facts().len(), 1);
    assert_eq!(page.facts()[0].fact().delta(), 2);
    assert_eq!(page.facts()[0].sequence(), engine.published_sequence().unwrap().get());
    assert_eq!(page.facts()[0].fact().scope().database_id().as_str(), "database");
    engine.shutdown().unwrap();
    let reopened = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let scope = reopened.scope_for(&TenantId::try_new("usage-tenant").unwrap());
    assert!(matches!(write(&reopened, &scope, "write-one"), CommitOutcome::Committed(_)));
    let page = reopened
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    assert_eq!(page.facts().len(), 1);
    assert_eq!(page.facts()[0].fact().delta(), 2);
    reopened.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn placement_cannot_be_rebound_or_claim_prebinding_writes() {
    let (path, engine, scope) = setup();
    let mut changed = engine.usage_placement(&scope).unwrap().unwrap();
    changed.selected_regions = vec![RegionCode::GRU];
    assert!(engine.configure_usage_placement(changed).is_err());
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();

    let path = std::env::temp_dir()
        .join(format!("terrabase-unbound-usage-{}", crate::identity::generate()));
    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let tenant = TenantId::try_new("usage-tenant").unwrap();
    let scope = engine.scope_for(&tenant);
    assert!(matches!(write(&engine, &scope, "unbound"), CommitOutcome::Committed(_)));
    assert!(
        engine
            .configure_usage_placement(
                UsagePlacement::try_new(
                    tenant,
                    "project",
                    "database",
                    "partition",
                    RegionCode::GRU,
                    vec![RegionCode::GRU],
                    "rev"
                )
                .unwrap()
            )
            .is_err()
    );
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn rejected_customer_mutation_mints_no_usage_fact() {
    let (path, engine, scope) = setup();
    assert!(matches!(write(&engine, &scope, "seed"), CommitOutcome::Committed(_)));
    let before = engine
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction =
        engine.begin(&scope, snapshot, OperationId::try_new("rejected").unwrap()).unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("one").unwrap(),
                CollectionName::try_new("users").unwrap(),
                DocumentValue::Null,
            ),
            ExpectedVersion::New,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Rejected(_)));
    let after = engine
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    assert_eq!(after.facts(), before.facts());
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn write_rollback_does_not_publish_document_or_usage() {
    let (path, engine, scope) = setup();
    engine.arm_storage_fault(crate::storage::FaultPoint::WalAppend);
    assert!(matches!(write(&engine, &scope, "aborted"), CommitOutcome::Rejected(_)));
    let page = engine
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    assert!(page.facts().is_empty());
    let snapshot = engine.read_snapshot(&scope).unwrap();
    assert!(
        engine
            .snapshot_documents_in_collection(
                &scope,
                &snapshot,
                &CollectionName::try_new("users").unwrap()
            )
            .unwrap()
            .is_empty()
    );
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn accepted_deletion_refuses_new_customer_usage_but_replays_existing_receipt() {
    let (path, engine, scope) = setup();
    assert!(matches!(write(&engine, &scope, "before-stop"), CommitOutcome::Committed(_)));
    let now = u64::try_from(
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis(),
    )
    .unwrap();
    // Accepted deletion publishes lifecycle authority and its accounting fence
    // together; a standalone accounting stop intentionally fails closed.
    engine
        .lifecycle_accept_deletion(
            &scope,
            lifecycle_state::DeletionIntent {
                operation_id: "delete-database".into(),
                usage_stopped_at: now,
                retain_until: None,
            },
        )
        .unwrap();
    assert!(matches!(write(&engine, &scope, "after-stop"), CommitOutcome::Rejected(_)));
    assert!(matches!(write(&engine, &scope, "before-stop"), CommitOutcome::Committed(_)));
    let mut changed = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("before-stop").unwrap(),
        )
        .unwrap();
    changed
        .put(
            Document::new(
                DocumentId::try_new("one").unwrap(),
                CollectionName::try_new("users").unwrap(),
                DocumentValue::String("different payload".into()),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(
        changed.commit(LocalDurability::Synced),
        CommitOutcome::Rejected(CommitError::OperationIdReuse)
    ));
    let page = engine
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    assert_eq!(page.facts().len(), 1);
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn internal_maintenance_mutation_does_not_mint_customer_write_usage() {
    let (path, engine, scope) = setup();
    assert!(matches!(write(&engine, &scope, "customer"), CommitOutcome::Committed(_)));
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction =
        engine.begin(&scope, snapshot, OperationId::try_new("ttl-cleanup").unwrap()).unwrap();
    transaction.internal_maintenance = true;
    transaction.delete_in_collection(
        &CollectionName::try_new("users").unwrap(),
        DocumentId::try_new("one").unwrap(),
        ExpectedVersion::Any,
    );
    assert!(matches!(transaction.commit(LocalDurability::Written), CommitOutcome::Committed(_)));
    let page = engine
        .usage_page(&scope, UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE, None).unwrap())
        .unwrap();
    assert_eq!(page.facts().len(), 1);
    assert_eq!(page.facts()[0].fact().delta(), 2);
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}
