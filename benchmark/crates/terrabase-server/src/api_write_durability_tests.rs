use super::*;
use crate::replication_runtime::ReplicationProgress;
use std::collections::{BTreeMap, BTreeSet};
use std::time::Duration;
use terrabase_cluster::production::DatabaseRouteScope;

const TENANT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
const DATABASE: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

fn fixture() -> (Arc<ApiState>, Arc<terrabase_tbep::member_host::MemberHost>, DatabaseRouteScope) {
    let mut state = super::tests::state_with_keys_and_control();
    let state_mut = Arc::get_mut(&mut state).unwrap();
    state_mut.scope = (TENANT.into(), PROJECT.into());
    let location = state_mut.catalog.as_ref().unwrap().location_for(TENANT, PROJECT, DATABASE);
    // Production pins each origin to its Region. Seed that same durable
    // configuration before the local catalog reopens the test database.
    terrabase::Engine::open(
        terrabase::engine::EngineConfig::durable(&location)
            .unwrap()
            .with_replication_origin(terrabase::types::RegionId::try_new("gru").unwrap()),
    )
    .unwrap()
    .shutdown()
    .unwrap();
    let host = state_mut.catalog.as_ref().unwrap().host_for(TENANT, PROJECT, DATABASE).unwrap();
    let owner = host.engine().database_owner();
    let scope = DatabaseRouteScope::new(owner.tenant(), owner.project(), owner.database());
    let progress = Arc::new(ReplicationProgress::default());
    progress.admit(scope, BTreeSet::from(["gru".into(), "sfo".into()])).unwrap();
    state_mut.replication_progress = Some(progress);
    (state, host, scope)
}

fn commit(
    engine: &terrabase::Engine,
    scope: &terrabase::types::TenantScope,
    key: &str,
) -> terrabase::CommitReceipt {
    let snapshot = engine.read_snapshot(scope).unwrap();
    let mut transaction =
        engine.begin(scope, snapshot, OperationId::try_new(key).unwrap()).unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("doc").unwrap(),
                CollectionName::try_new("items").unwrap(),
                DocumentValue::Null,
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => receipt,
        outcome => panic!("unexpected outcome: {outcome:?}"),
    }
}

#[tokio::test]
async fn postcommit_timeout_is_unknown_and_identical_operation_replays() {
    let (state, host, route_scope) = fixture();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let durability = WriteDurability {
        class: Durability::Regional,
        uniform_context: None,
        progress: state.replication_progress.clone().unwrap(),
        scope: route_scope,
        timeout: Duration::from_millis(1),
        standalone: false,
    };
    let receipt = commit(engine, &scope, "retry-key");
    let error = durability.confirm(engine, &scope, &receipt).await.unwrap_err();
    assert_eq!(error.code, "commit_unknown");
    assert_eq!(error.status, StatusCode::SERVICE_UNAVAILABLE);
    let replay = commit(engine, &scope, "retry-key");
    assert_eq!(receipt.sequence(), replay.sequence());
    durability.progress.advance_regional(route_scope, receipt.sequence().get());
    durability.confirm(engine, &scope, &replay).await.unwrap();
}

#[tokio::test]
async fn another_database_receipt_cannot_confirm_this_write() {
    let (state, host, route_scope) = fixture();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let progress = state.replication_progress.clone().unwrap();
    let other = DatabaseRouteScope::try_new(TENANT, PROJECT, "01ARZ3NDEKTSV4RRFFQ69G5FAY").unwrap();
    progress.admit(other, BTreeSet::from(["gru".into()])).unwrap();
    let receipt = commit(engine, &scope, "scope-key");
    progress.advance_regional(other, receipt.sequence().get());
    let durability = WriteDurability {
        class: Durability::Regional,
        uniform_context: None,
        progress,
        scope: route_scope,
        timeout: Duration::from_millis(1),
        standalone: false,
    };
    assert_eq!(
        durability.confirm(engine, &scope, &receipt).await.unwrap_err().code,
        "commit_unknown"
    );
}

#[tokio::test]
async fn global_preflight_refuses_nonuniform_context_before_next_commit() {
    let (mut state, host, route_scope) = fixture();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let receipt = commit(engine, &scope, "dependency");
    let context = engine.durable_origin_prefixes(&scope).unwrap();
    let progress = Arc::new(ReplicationProgress::default());
    progress.admit(route_scope, BTreeSet::from(["gru".into(), "sfo".into()])).unwrap();
    progress.advance_regional(route_scope, receipt.sequence().get());
    progress.advance_region(route_scope, "gru", &context).unwrap();
    progress.advance_region(route_scope, "sfo", &BTreeMap::new()).unwrap();
    Arc::get_mut(&mut state).unwrap().replication_progress = Some(progress);
    let mut headers = axum::http::HeaderMap::new();
    headers.insert("x-terrascale-durability", HeaderValue::from_static("global"));
    headers.insert("x-terrascale-timeout-ms", HeaderValue::from_static("1"));
    let before = engine.read_snapshot(&scope).unwrap().sequence();
    let error = WriteDurability::prepare(&state, engine, &scope, &headers).await.err().unwrap();
    assert_eq!(error.code, "global_durability_unavailable");
    assert_eq!(engine.read_snapshot(&scope).unwrap().sequence(), before);
}

#[tokio::test]
async fn global_receipt_requires_every_selected_region_for_exact_origin_commit() {
    let (state, host, route_scope) = fixture();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let receipt = commit(engine, &scope, "global-receipt");
    let row = engine.journal_rows(&scope, receipt.sequence().get() - 1, 1).unwrap().remove(0);
    let progress = Arc::new(ReplicationProgress::default());
    progress.admit(route_scope, BTreeSet::from(["gru".into(), "sfo".into()])).unwrap();
    progress.advance_regional(route_scope, receipt.sequence().get());
    let frontier = BTreeMap::from([(row.origin.clone(), row.origin_sequence)]);
    progress.advance_region(route_scope, "gru", &frontier).unwrap();
    let durability = WriteDurability {
        class: Durability::Global,
        uniform_context: Some(BTreeMap::new()),
        progress: progress.clone(),
        scope: route_scope,
        timeout: Duration::from_millis(1),
        standalone: false,
    };
    assert_eq!(
        durability.confirm(engine, &scope, &receipt).await.unwrap_err().code,
        "commit_unknown"
    );
    progress.advance_region(route_scope, "sfo", &frontier).unwrap();
    durability.confirm(engine, &scope, &receipt).await.unwrap();
    drop(state);
}
