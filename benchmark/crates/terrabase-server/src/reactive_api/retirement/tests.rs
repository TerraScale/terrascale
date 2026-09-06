use super::*;
use crate::reactive_api::{
    AttachRequest, CapabilityKeyRing, Materializer, ReadResult, incremental,
};
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};
use terrabase_tbep::member_host::{MemberHost, MemberHostOptions};

fn put(host: &MemberHost, tenant: &TenantId, value: i64) {
    let scope = host.engine().scope_for(tenant);
    let mut transaction = host
        .engine()
        .begin(
            &scope,
            host.engine().read_snapshot(&scope).unwrap(),
            OperationId::try_new(&format!("write-{value}")).unwrap(),
        )
        .unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("row").unwrap(),
                CollectionName::try_new("orders").unwrap(),
                DocumentValue::Object([("value".into(), DocumentValue::Integer(value))].into()),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        terrabase::engine::CommitOutcome::Committed(_)
    ));
}

fn age(store: &ViewStore, id: &str) {
    store.views.lock().unwrap().get_mut(id).unwrap().last_access =
        tokio::time::Instant::now() - IDLE;
}

#[tokio::test(start_paused = true)]
async fn periodic_retirement_runs_without_another_request() {
    let store = Arc::new(ViewStore::new());
    let request: AttachRequest =
        serde_json::from_str(r#"{"databaseId":"db","statement":"SELECT * FROM orders"}"#).unwrap();
    let (_, id) = store.attach(&request, "tenant").unwrap();
    store.start_maintenance();
    tokio::task::yield_now().await;
    tokio::time::advance(IDLE + SWEEP).await;
    let deadline = std::time::Instant::now() + Duration::from_secs(2);
    loop {
        if store.views.lock().unwrap().get(&id).unwrap().retired_at.is_some() {
            break;
        }
        assert!(
            std::time::Instant::now() < deadline,
            "the periodic worker did not retire the idle view"
        );
        tokio::task::yield_now().await;
    }
    let weak = Arc::downgrade(&store);
    drop(store);
    while weak.upgrade().is_some() {
        assert!(std::time::Instant::now() < deadline, "the timer owns the registry");
        tokio::task::yield_now().await;
    }
}

fn prepare(host: &Arc<MemberHost>, tenant: &TenantId, request: &AttachRequest) -> Materialization {
    let plan = host
        .engine()
        .queries()
        .compile(terrabase::QueryInput::sql(
            terrabase::SqlQueryText::try_new(&request.statement).unwrap(),
        ))
        .unwrap();
    Materialization {
        revision: 0,
        items: Vec::new(),
        digest: String::new(),
        identity: b"test-incarnation".to_vec(),
        materializer: Some(Materializer {
            host: Arc::clone(host),
            incremental: incremental::SqlMaterializer::for_plan(&plan).unwrap(),
            plan,
            tenant: tenant.clone(),
            source: None,
            dependency: None,
        }),
    }
}

#[test]
fn retirement_releases_source_and_quota_and_reattachment_preserves_history() {
    let directory =
        std::env::temp_dir().join(format!("terrabase-retirement-{}", std::process::id()));
    let host = Arc::new(
        MemberHost::open(&directory, 1, "tenant", "project", "db", MemberHostOptions::tb1a())
            .unwrap(),
    );
    let tenant = TenantId::try_new("tenant").unwrap();
    put(&host, &tenant, 1);
    let request: AttachRequest =
        serde_json::from_str(r#"{"databaseId":"db","statement":"SELECT value FROM orders"}"#)
            .unwrap();
    let prepared = prepare(&host, &tenant, &request);
    let guards = Arc::new(crate::guards::AbuseGuards::new());
    let mut limits = crate::guards::Plan::Free.limits();
    limits.distinct_views = 1;
    guards.set_plan("db", crate::guards::Plan::Custom(limits));
    let store = ViewStore::new();
    let keys = CapabilityKeyRing::conformance();
    let attach = || {
        store.attach_materialized(
            &request,
            "tenant",
            terrabase::regions::RegionCode::GRU,
            Some(&prepared),
            &keys,
            Some(&guards),
        )
    };
    let (original, id) = attach().unwrap();
    let frozen = store.read_snapshot(&id, original.generation, original.offset).unwrap();
    let source = Arc::downgrade(
        store
            .views
            .lock()
            .unwrap()
            .get(&id)
            .unwrap()
            .materializer
            .as_ref()
            .unwrap()
            .source
            .as_ref()
            .unwrap(),
    );
    let refreshing = store.view_refresh_lock(&id).unwrap();
    age(&store, &id);
    store.retire_idle(tokio::time::Instant::now());
    assert_eq!(guards.view_count("db"), 1, "in-flight refresh protects admission");
    assert!(source.upgrade().is_some());
    drop(refreshing);
    store.retire_idle(tokio::time::Instant::now());
    assert_eq!(guards.view_count("db"), 0);
    assert!(source.upgrade().is_none(), "retirement releases the journal source and watch");
    assert_eq!(
        store.read_snapshot(&id, original.generation, original.offset).unwrap().bytes,
        frozen.bytes
    );
    assert!(
        matches!(store.read_log(&id, original.generation, original.offset), Err(error) if error.code == "view_retired")
    );
    put(&host, &tenant, 2);
    guards.attach_view("db", "other").unwrap();
    assert_eq!(attach().unwrap_err().code, "abuse_guard_exceeded");
    assert!(store.views.lock().unwrap().get(&id).unwrap().materializer.is_none());
    guards.detach_view("db", "other");
    let (resumed, same_id) = attach().unwrap();
    assert_eq!(same_id, id);
    assert_eq!(resumed.generation, original.generation);
    assert!(resumed.offset > original.offset);
    assert_eq!(guards.view_count("db"), 1);
    assert_eq!(
        store.read_snapshot(&id, original.generation, original.offset).unwrap().bytes,
        frozen.bytes
    );
    let ReadResult::Data { frozen: delta, .. } =
        store.read_log(&id, original.generation, original.offset).unwrap()
    else {
        panic!("reattachment must append the missed change");
    };
    assert!(matches!(terrabase_sync_core::wire::decode_envelope(&delta.bytes).unwrap().message,
        terrabase_sync_core::wire::Message::Delta { from_revision, to_revision, .. }
        if from_revision == original.offset && to_revision == resumed.offset));
    age(&store, &id);
    let now = tokio::time::Instant::now();
    store.retire_idle(now);
    let ReadResult::Data { frozen: archived, .. } =
        store.read_log(&id, original.generation, original.offset).unwrap()
    else {
        panic!("retirement keeps retained delta bytes");
    };
    assert_eq!(archived.bytes, delta.bytes);
    store.retire_idle(now + super::super::RETENTION + Duration::from_secs(1));
    assert!(store.read_snapshot(&id, original.generation, original.offset).is_err());
    drop(store);
    drop(prepared);
    drop(host);
    std::fs::remove_dir_all(directory).unwrap();
}

#[test]
fn deletion_removes_active_and_retired_origin_bytes_only_for_owner_scope() {
    let store = ViewStore::new();
    let request: AttachRequest =
        serde_json::from_str(r#"{"databaseId":"db","statement":"SELECT * FROM orders"}"#).unwrap();
    let (active, active_id) = store.attach(&request, "org:project:db").unwrap();
    let (_, other_id) = store.attach(&request, "other:project:db").unwrap();
    store.revoke_scope("org:project:db").unwrap();
    assert!(store.capability_claims(&active_id, active.generation).is_err());
    assert!(store.views.lock().unwrap().contains_key(&other_id));
    store.revoke_scope("org:project:db").unwrap();
    age(&store, &other_id);
    store.retire_idle(tokio::time::Instant::now());
    assert!(store.views.lock().unwrap().get(&other_id).unwrap().retired_at.is_some());
    store.revoke_scope("other:project:db").unwrap();
    assert!(!store.views.lock().unwrap().contains_key(&other_id));
}
