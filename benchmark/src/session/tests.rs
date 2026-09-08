//! In-crate session tests covering the GAP-D01 contract list.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design (the same evidence pattern as
//! `src/engine/tests.rs`). Every assertion observes the public session
//! surface or the engine's own public read path; no private session state
//! is inspected. The four acceptance-named behaviors (deferred ordering,
//! identity map, `WhatChanged`, lazy batching) each have a dedicated test.

use std::{
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
    time::Duration,
};

use crate::engine::{CommitError, CommitOutcome, Engine, EngineConfig};
use crate::families::{
    CounterBatchWrite, CounterOperation, CounterRead, FamilyReadContext, FamilyReadRequest,
    FamilyWriteRequest, IdentityEntry,
};
use crate::session::{
    BeforeDeleteEvent, BeforeStoreEvent, FieldChange, FieldChangeKind, MAX_SESSION_TOKEN_BYTES,
    Session, SessionCommand, SessionEpoch, SessionError, SessionHookVeto, SessionOptions,
    SessionToken, SessionTokenError, SessionTracking, SessionTransactionMode,
};
use crate::streaming::{DocumentStreamSelector, StreamCompletion, StreamItem};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, RegionId, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-gapd01-session-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(
        TenantId::try_new(tenant_name).expect("valid tenant"),
        engine.identity(),
    )
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid collection")
}

fn doc_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn op_id(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields.iter().map(|(name, value)| ((*name).to_owned(), value.clone())).collect(),
    )
}

fn text(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn array(values: &[&str]) -> DocumentValue {
    DocumentValue::Array(values.iter().map(|value| text(value)).collect())
}

fn commit_document(
    engine: &Engine,
    tenant_name: &str,
    collection_name: &str,
    id: &str,
    value: DocumentValue,
) {
    static SEED: AtomicU64 = AtomicU64::new(0);
    let seed = SEED.fetch_add(1, Ordering::Relaxed);
    let scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine
        .begin(&scope, snapshot, op_id(&format!("seed-{seed}-{collection_name}-{id}")))
        .expect("begins");
    transaction
        .put(Document::new(doc_id(id), collection(collection_name), value), ExpectedVersion::Any)
        .expect("stages");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
}

fn read_engine_document(
    engine: &Engine,
    tenant_name: &str,
    collection_name: &str,
    id: &str,
) -> Option<Document> {
    let scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let transaction = engine.begin(&scope, snapshot, op_id("read-probe")).expect("begins");
    transaction.read_in_collection(&collection(collection_name), &doc_id(id)).expect("reads")
}

fn counter_total(engine: &Engine, tenant_name: &str, id: &str) -> Option<i64> {
    let scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let context = FamilyReadContext::try_new(scope, snapshot).expect("context");
    let request = FamilyReadRequest::counter_group(
        CounterRead::try_new(
            doc_id(id),
            vec!["hits".to_owned()],
            Deadline::after(Duration::from_secs(30)).expect("deadline"),
            Cancellation::new(),
        )
        .expect("counter read"),
    );
    match engine.families().read(context, request).expect("counter read") {
        crate::families::FamilyReadResult::CounterGroup(result) => {
            result.find("hits").expect("counter entry")
        }
        other => panic!("expected a counter group result, got {other:?}"),
    }
}

fn open_session<'e>(engine: &'e Engine, tenant_name: &str) -> Session<'e> {
    Session::open(engine, &scope(engine, tenant_name), SessionOptions::new())
        .expect("session opens")
}

fn committed(outcome: Option<CommitOutcome>) -> crate::engine::CommitReceipt {
    match outcome {
        Some(CommitOutcome::Committed(receipt)) => receipt,
        other => panic!("expected a committed receipt, got {other:?}"),
    }
}

// ---------------------------------------------------------------------------
// R7.5 / GAP-D08 — deferred ordering: deletes, then puts, then deferred
// commands, all inside one atomic commit.
// ---------------------------------------------------------------------------

#[test]
fn save_stages_deletes_then_puts_then_deferred_commands_in_v1_batch_order() {
    let (dir, engine) = open_engine("batch-order");
    let scope = scope(&engine, "tenant-a");
    commit_document(&engine, "tenant-a", "users", "old", text("v1"));
    commit_document(&engine, "tenant-a", "users", "counter-doc", object(&[]));
    let recorded: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));

    let mut session = Session::open(&engine, &scope, SessionOptions::new()).expect("session");
    let delete_sink = Arc::clone(&recorded);
    session.on_before_delete(Box::new(move |event: &BeforeDeleteEvent| {
        delete_sink
            .lock()
            .expect("recorder")
            .push(format!("before-delete:{}", event.id().as_str()));
        Ok(())
    }));
    let store_sink = Arc::clone(&recorded);
    session.on_before_store(Box::new(move |event: &mut BeforeStoreEvent| {
        store_sink.lock().expect("recorder").push(format!("before-store:{}", event.id().as_str()));
        Ok(())
    }));

    // Track and delete `old`, stage a put for `fresh`, and defer one typed
    // family command — the V1 single-batch shape.
    session.load(&collection("users"), &doc_id("old")).expect("loads");
    session.delete(collection("users"), doc_id("old")).expect("stages delete");
    session.store(collection("users"), doc_id("fresh"), text("v2")).expect("stages put");
    session
        .defer(SessionCommand::family(FamilyWriteRequest::counter_batch(
            CounterBatchWrite::try_new(
                scope.clone(),
                doc_id("counter-doc"),
                vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 7 }],
                ExpectedVersion::Any,
                op_id("deferred-counter"),
                Deadline::after(Duration::from_secs(30)).expect("deadline"),
                Cancellation::new(),
            )
            .expect("counter write"),
        )))
        .expect("defers");

    committed(session.save_changes().expect("save"));
    let order = recorded.lock().expect("recorder");
    assert_eq!(
        *order,
        vec!["before-delete:old".to_owned(), "before-store:fresh".to_owned(),],
        "V1 batches deletes before puts; hooks observe that order"
    );
    drop(order);

    // The deferred family command committed atomically with the documents.
    assert_eq!(counter_total(&engine, "tenant-a", "counter-doc"), Some(7));
    assert!(
        read_engine_document(&engine, "tenant-a", "users", "old").is_none(),
        "the session delete committed"
    );
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "fresh").map(|d| d.value().clone()),
        Some(text("v2")),
        "the session put committed"
    );
    drop(dir);
}

// ---------------------------------------------------------------------------
// R4 / PB-2010 — the identity map pins one instance per document.
// ---------------------------------------------------------------------------

#[test]
fn identity_map_pins_the_loaded_instance_until_refresh_evict_or_save() {
    let (dir, engine) = open_engine("identity-map");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));

    let mut session = open_session(&engine, "tenant-a");
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("u1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("v1"))
    );
    assert!(session.is_loaded(&collection("users"), &doc_id("u1")));

    // An external commit is invisible to the pinned instance.
    commit_document(&engine, "tenant-a", "users", "u1", text("v2"));
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("u1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("v1")),
        "the identity map answers the pinned instance, not the newer engine state"
    );

    // Refresh replaces the tracked instance with the engine's current value.
    session.refresh(&collection("users"), &doc_id("u1")).expect("refreshes");
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("u1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("v2"))
    );

    // Evict unpins entirely: the next load sees engine state directly.
    commit_document(&engine, "tenant-a", "users", "u1", text("v3"));
    session.evict(&collection("users"), &doc_id("u1"));
    assert!(!session.is_loaded(&collection("users"), &doc_id("u1")));
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("u1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("v3"))
    );
    drop(dir);
}

#[test]
fn absent_loads_register_known_missing_and_stay_loaded() {
    let (dir, engine) = open_engine("known-missing");
    let mut session = open_session(&engine, "tenant-a");
    assert!(
        session.load(&collection("users"), &doc_id("ghost")).expect("loads").is_none(),
        "the document does not exist"
    );
    assert!(
        session.is_loaded(&collection("users"), &doc_id("ghost")),
        "a known-missing id is loaded, like V1's IsLoaded"
    );
    assert!(session.is_known_missing(&collection("users"), &doc_id("ghost")));

    session.evict(&collection("users"), &doc_id("ghost"));
    assert!(!session.is_loaded(&collection("users"), &doc_id("ghost")));
    assert!(!session.is_known_missing(&collection("users"), &doc_id("ghost")));
    drop(dir);
}

#[test]
fn loads_resolve_by_collection_key_and_batches_preserve_request_order() {
    let (dir, engine) = open_engine("collection-keys");
    commit_document(&engine, "tenant-a", "users", "shared", text("user-copy"));
    commit_document(&engine, "tenant-a", "orders", "shared", text("order-copy"));

    let mut session = open_session(&engine, "tenant-a");
    let batch = session
        .load_many(&collection("users"), &[doc_id("shared"), doc_id("missing"), doc_id("shared")])
        .expect("batch loads");
    assert_eq!(batch.len(), 3);
    assert_eq!(batch[0].as_ref().map(|d| d.value().clone()), Some(text("user-copy")));
    assert!(batch[1].is_none());
    assert_eq!(batch[2].as_ref().map(|d| d.value().clone()), Some(text("user-copy")));

    // The same identifier in another collection is a different tracked key.
    assert_eq!(
        session
            .load(&collection("orders"), &doc_id("shared"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("order-copy"))
    );
    assert!(session.is_known_missing(&collection("users"), &doc_id("missing")));
    drop(dir);
}

// ---------------------------------------------------------------------------
// R6 / PB-2012 / GAP-D06 — WhatChanged field-level reporting.
// ---------------------------------------------------------------------------

#[test]
fn what_changed_reports_field_level_old_new_and_change_types() {
    let (dir, engine) = open_engine("what-changed");
    commit_document(
        &engine,
        "tenant-a",
        "users",
        "u1",
        object(&[
            ("name", text("ada")),
            ("city", text("london")),
            ("tags", array(&["a", "b", "c"])),
        ]),
    );
    commit_document(&engine, "tenant-a", "users", "u2", text("unchanged"));

    let mut session = open_session(&engine, "tenant-a");
    session.load(&collection("users"), &doc_id("u1")).expect("loads");
    session.load(&collection("users"), &doc_id("u2")).expect("loads");

    // Modify u1: field change, removal, addition, array value change and
    // removal; delete u2; store u3 new.
    session
        .store(
            collection("users"),
            doc_id("u1"),
            object(&[
                ("name", text("grace")),
                ("tags", array(&["a", "x"])),
                ("nick", text("lovelace")),
            ]),
        )
        .expect("stages");
    session.delete(collection("users"), doc_id("u2")).expect("stages delete");
    session.store(collection("users"), doc_id("u3"), text("new")).expect("stages");
    assert!(session.has_changes());

    let report = session.what_changed();
    let changed: Vec<_> =
        report.iter().map(|entry| (entry.collection().as_str(), entry.id().as_str())).collect();
    assert_eq!(
        changed,
        vec![("users", "u1"), ("users", "u2"), ("users", "u3")],
        "one entry per changed document in canonical key order"
    );

    let u1 = &report[0];
    let u1_changes: Vec<(String, &str, FieldChangeKind)> = u1
        .changes()
        .iter()
        .map(|change| (change.field_full_name(), change.field_name(), change.change()))
        .collect();
    assert_eq!(
        u1_changes,
        vec![
            ("city".to_owned(), "city", FieldChangeKind::RemovedField),
            ("name".to_owned(), "name", FieldChangeKind::FieldChanged),
            ("nick".to_owned(), "nick", FieldChangeKind::NewField),
            ("tags.1".to_owned(), "1", FieldChangeKind::ArrayValueChanged),
            ("tags.2".to_owned(), "2", FieldChangeKind::ArrayValueRemoved),
        ],
        "V1's change-type vocabulary with old/new values and dotted paths"
    );
    let name_change = &u1.changes()[1];
    assert_eq!(name_change.old_value(), Some(&text("ada")));
    assert_eq!(name_change.new_value(), Some(&text("grace")));
    let tags_change = &u1.changes()[3];
    assert_eq!(tags_change.old_value(), Some(&text("b")));
    assert_eq!(tags_change.new_value(), Some(&text("x")));

    let u2 = &report[1];
    assert_eq!(u2.changes().len(), 1);
    assert_eq!(u2.changes()[0].change(), FieldChangeKind::DocumentDeleted);

    let u3 = &report[2];
    assert_eq!(u3.changes().len(), 1);
    assert_eq!(u3.changes()[0].change(), FieldChangeKind::DocumentAdded);

    // Idempotent re-inspection: a second call reports the same thing.
    assert_eq!(session.what_changed(), report);
    assert!(session.has_changed(&collection("users"), &doc_id("u1")));
    assert!(!session.has_changed(&collection("users"), &doc_id("absent")));

    committed(session.save_changes().expect("save"));
    assert!(
        session.what_changed().is_empty(),
        "a successful save clears the committed session state"
    );
    assert!(!session.has_changes());
    drop(dir);
}

#[test]
fn nested_type_change_reports_the_parent_path_and_leaf_name() {
    // A nested field whose value changed kind (object to scalar) diffed
    // below the root reports the containing path and the leaf name, like
    // every other field change — never the full dotted name in `name`.
    let mut changes = Vec::new();
    super::diff_values(
        "profile.address",
        &object(&[("city", text("london"))]),
        &text("plain"),
        &mut changes,
    );
    assert_eq!(changes.len(), 1);
    assert_eq!(changes[0].field_path(), "profile");
    assert_eq!(changes[0].field_name(), "address");
    assert_eq!(changes[0].field_full_name(), "profile.address");
    assert_eq!(changes[0].old_value(), Some(&object(&[("city", text("london"))])));
    assert_eq!(changes[0].new_value(), Some(&text("plain")));
    assert_eq!(changes[0].change(), FieldChangeKind::FieldChanged);
}

#[test]
fn root_type_change_reports_the_root_value_name() {
    let (dir, engine) = open_engine("root-type-change");
    commit_document(&engine, "tenant-a", "users", "u1", text("scalar"));
    let mut session = open_session(&engine, "tenant-a");
    session.load(&collection("users"), &doc_id("u1")).expect("loads");
    session.store(collection("users"), doc_id("u1"), object(&[("a", text("b"))])).expect("stages");
    let report = session.what_changed();
    assert_eq!(report.len(), 1);
    let change = &report[0].changes()[0];
    assert_eq!(change.field_path(), "");
    assert_eq!(change.field_name(), "value");
    assert_eq!(change.field_full_name(), "value");
    assert_eq!(change.change(), FieldChangeKind::FieldChanged);
    drop(dir);
}

#[test]
fn ignore_changes_for_keeps_a_document_tracked_but_outside_the_save() {
    let (dir, engine) = open_engine("ignore-changes");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    session.load(&collection("users"), &doc_id("u1")).expect("loads");
    session.store(collection("users"), doc_id("u1"), text("v2")).expect("stages");
    session.ignore_changes_for(&collection("users"), &doc_id("u1")).expect("ignores");

    assert!(!session.has_changed(&collection("users"), &doc_id("u1")));
    assert!(session.what_changed().is_empty());
    assert!(
        session.save_changes().expect("save").is_none(),
        "nothing to send: the only change is ignored, like V1's no-op SaveChanges"
    );
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1").map(|d| d.value().clone()),
        Some(text("v1")),
        "the ignored modification never reached the engine"
    );
    drop(dir);
}

// ---------------------------------------------------------------------------
// R9 / PB-2019 / GAP-D14 — lazy reads batch at execution and at save.
// ---------------------------------------------------------------------------

#[test]
fn pending_lazy_reads_execute_as_one_batch_and_batch_again_at_save() {
    let (dir, engine) = open_engine("lazy-batching");
    commit_document(&engine, "tenant-a", "users", "l1", text("one"));
    commit_document(&engine, "tenant-a", "users", "l2", text("two"));
    commit_document(&engine, "tenant-a", "users", "l3", text("three"));

    let mut session = open_session(&engine, "tenant-a");
    session.lazy_load(&collection("users"), &doc_id("l1")).expect("registers");
    session.lazy_load(&collection("users"), &doc_id("l2")).expect("registers");
    assert!(!session.is_loaded(&collection("users"), &doc_id("l1")), "nothing executed yet");
    assert_eq!(session.lazy_batches_executed(), 0);

    session.execute_all_pending_lazy_operations().expect("executes");
    assert!(session.is_loaded(&collection("users"), &doc_id("l1")));
    assert!(session.is_loaded(&collection("users"), &doc_id("l2")));
    assert_eq!(session.lazy_batches_executed(), 1, "one execution resolved the whole pending set");
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("l1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("one"))
    );

    // A new pending read executes as part of the save, again as one batch.
    session.lazy_load(&collection("users"), &doc_id("l3")).expect("registers");
    session.store(collection("users"), doc_id("l4"), text("four")).expect("stages");
    committed(session.save_changes().expect("save"));
    assert!(session.is_loaded(&collection("users"), &doc_id("l3")));
    assert_eq!(session.lazy_batches_executed(), 2);
    drop(dir);
}

#[test]
fn evict_and_clear_remove_pending_lazy_reads() {
    let (dir, engine) = open_engine("lazy-evict");
    commit_document(&engine, "tenant-a", "users", "l1", text("one"));
    let mut session = open_session(&engine, "tenant-a");
    session.lazy_load(&collection("users"), &doc_id("l1")).expect("registers");
    session.evict(&collection("users"), &doc_id("l1"));
    session.execute_all_pending_lazy_operations().expect("executes");
    assert!(!session.is_loaded(&collection("users"), &doc_id("l1")), "evicted before execution");

    session.lazy_load(&collection("users"), &doc_id("l1")).expect("registers");
    session.clear();
    session.execute_all_pending_lazy_operations().expect("executes");
    assert!(!session.is_loaded(&collection("users"), &doc_id("l1")), "cleared before execution");
    drop(dir);
}

// ---------------------------------------------------------------------------
// R1 — transaction-mode selection and no-tracking rules.
// ---------------------------------------------------------------------------

#[test]
fn cluster_wide_mode_fails_closed_at_open() {
    let (dir, engine) = open_engine("cluster-wide");
    let scope = scope(&engine, "tenant-a");
    let error = Session::open(&engine, &scope, SessionOptions::new().cluster_wide())
        .expect_err("no TerraBase cluster transaction exists");
    assert_eq!(error, SessionError::ClusterWideUnavailable);

    let single = Session::open(
        &engine,
        &scope,
        SessionOptions {
            transaction_mode: SessionTransactionMode::SingleNode,
            ..SessionOptions::new()
        },
    );
    assert!(single.is_ok(), "single-node mode opens and saves");
    drop(dir);
}

#[test]
fn no_tracking_reads_answer_but_refuse_every_write_path() {
    let (dir, engine) = open_engine("no-tracking");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let scope = scope(&engine, "tenant-a");
    let mut session =
        Session::open(&engine, &scope, SessionOptions::new().no_tracking()).expect("opens");

    assert_eq!(
        session
            .load(&collection("users"), &doc_id("u1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("v1")),
        "reads answer without tracking"
    );
    assert!(!session.is_loaded(&collection("users"), &doc_id("u1")));
    assert!(
        session.load(&collection("users"), &doc_id("ghost")).expect("loads").is_none(),
        "absent reads answer"
    );
    assert!(!session.is_known_missing(&collection("users"), &doc_id("ghost")));

    assert_eq!(
        session.store(collection("users"), doc_id("n1"), text("v")).unwrap_err(),
        SessionError::NoTrackingRefused,
        "V1: Store throws on a NoTracking session"
    );
    assert_eq!(
        session.delete(collection("users"), doc_id("u1")).unwrap_err(),
        SessionError::NoTrackingRefused
    );
    assert_eq!(
        session.lazy_load(&collection("users"), &doc_id("u1")).unwrap_err(),
        SessionError::NoTrackingRefused
    );
    assert_eq!(
        session.save_changes().unwrap_err(),
        SessionError::NoTrackingRefused,
        "V1: SaveChanges throws on a NoTracking session"
    );
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1").map(|d| d.value().clone()),
        Some(text("v1")),
        "nothing was written or deleted"
    );
    drop(dir);
}

// ---------------------------------------------------------------------------
// R2/R3/R7 — store/delete/save semantics at the unit-of-work boundary.
// ---------------------------------------------------------------------------

#[test]
fn read_your_writes_and_pending_puts_report_document_added() {
    let (dir, engine) = open_engine("read-your-writes");
    let mut session = open_session(&engine, "tenant-a");
    session.store(collection("users"), doc_id("p1"), text("staged")).expect("stages");
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("p1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("staged")),
        "a pending put answers loads before the save"
    );
    let report = session.what_changed();
    assert_eq!(report.len(), 1);
    assert_eq!(report[0].changes()[0].change(), FieldChangeKind::DocumentAdded);
    drop(dir);
}

#[test]
fn session_reads_update_the_token_and_enforce_supplied_frontiers() {
    let (dir, engine) = open_engine("causal-session-read");
    let scope = scope(&engine, "tenant-a");
    let mut writer = Session::open(&engine, &scope, SessionOptions::new()).expect("opens");
    writer.store(collection("users"), doc_id("u1"), text("v1")).expect("stages");
    committed(writer.save_changes().expect("saves"));
    let token = writer.session_token().clone();
    assert!(
        token.frontier().entries().any(|(_, sequence)| sequence > 0),
        "a committed write is reflected in the session frontier"
    );

    let mut continued =
        Session::open(&engine, &scope, SessionOptions::new().with_session_token(token.clone()))
            .expect("continues the token");
    assert_eq!(
        continued
            .load(&collection("users"), &doc_id("u1"))
            .expect("loads")
            .map(|document| document.value().clone()),
        Some(text("v1"))
    );
    assert!(continued.session_token().frontier().covers(token.frontier()));

    let mut impossible = SessionToken::new(token.epoch());
    impossible
        .advance(
            crate::origin_lane::OriginLaneId::try_new(
                crate::types::RegionId::try_new("unavailable").expect("region"),
                1,
            )
            .expect("lane"),
            1,
        )
        .expect("frontier entry");
    let mut behind = Session::open(
        &engine,
        &scope,
        SessionOptions::new().with_session_token(impossible).wait_timeout(Duration::ZERO),
    )
    .expect("opens with a valid token");
    assert!(matches!(
        behind.load(&collection("users"), &doc_id("other")),
        Err(SessionError::StaleReplica(_))
    ));
    drop(dir);
}

#[test]
fn session_frontier_rejects_an_unbounded_multi_origin_token() {
    let epoch = SessionEpoch::new(1).expect("non-zero epoch");
    let mut token = SessionToken::new(epoch);
    let mut rejected = None;
    for index in 0..100 {
        let origin = format!("{}-{index:03}", "region".repeat(35));
        let lane = crate::origin_lane::OriginLaneId::try_new(
            crate::types::RegionId::try_new(&origin).expect("valid region"),
            index as u128 + 1,
        )
        .expect("lane");
        if let Err(error) = token.advance(lane, 1) {
            rejected = Some(error);
            break;
        }
    }
    assert_eq!(rejected, Some(SessionTokenError::OriginLimitExceeded));
    assert!(token.encode().len() <= MAX_SESSION_TOKEN_BYTES);
    assert!(SessionToken::decode(&token.encode()).is_ok());
}

#[test]
fn session_token_survives_engine_restart_and_preserves_read_your_writes() {
    let dir = location("restart-token");
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("opens");
    let initial_scope = scope(&engine, "tenant-a");
    let mut writer = Session::open(&engine, &initial_scope, SessionOptions::new()).expect("opens");
    writer.store(collection("users"), doc_id("restart"), text("durable")).expect("stages");
    committed(writer.save_changes().expect("saves"));
    let token = writer.session_token().clone();
    drop(writer);
    drop(engine);

    let reopened =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("reopens");
    let reopened_scope = scope(&reopened, "tenant-a");
    let mut continued = Session::open(
        &reopened,
        &reopened_scope,
        SessionOptions::new().with_session_token(token.clone()),
    )
    .expect("token remains valid for the same database incarnation");
    assert_eq!(
        continued
            .load(&collection("users"), &doc_id("restart"))
            .expect("loads")
            .map(|document| document.value().clone()),
        Some(text("durable"))
    );
    assert!(continued.session_token().frontier().covers(token.frontier()));
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn concurrent_sessions_capture_distinct_write_frontier_progress() {
    let (dir, engine) = open_engine("concurrent-frontier");
    let engine = Arc::new(engine);
    let mut workers = Vec::new();
    for index in 0..2 {
        let worker_engine = Arc::clone(&engine);
        workers.push(std::thread::spawn(move || {
            let scope = worker_engine.scope_for(&TenantId::try_new("tenant-a").expect("tenant"));
            let mut session =
                Session::open(&worker_engine, &scope, SessionOptions::new()).expect("opens");
            session
                .store(collection("users"), doc_id(&format!("concurrent-{index}")), text("value"))
                .expect("stages");
            matches!(session.save_changes().expect("saves"), Some(CommitOutcome::Committed(_)))
        }));
    }
    assert!(workers.into_iter().all(|worker| worker.join().expect("worker")));
    let scope = engine.scope_for(&TenantId::try_new("tenant-a").expect("tenant"));
    let mut reader = Session::open(&engine, &scope, SessionOptions::new()).expect("opens");
    assert!(reader.load(&collection("users"), &doc_id("concurrent-0")).expect("loads").is_some());
    assert!(reader.load(&collection("users"), &doc_id("concurrent-1")).expect("loads").is_some());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn stale_multi_origin_reads_return_a_safe_retry_region_without_spinning() {
    let (dir, engine) = open_engine("stale-multi-origin");
    let epoch = engine.session_epoch();
    let mut token = SessionToken::new(epoch);
    token
        .advance(
            crate::origin_lane::OriginLaneId::try_new(
                RegionId::try_new("region-a").expect("region"),
                1,
            )
            .expect("lane"),
            1,
        )
        .expect("frontier");
    token
        .advance(
            crate::origin_lane::OriginLaneId::try_new(
                RegionId::try_new("region-b").expect("region"),
                1,
            )
            .expect("lane"),
            1,
        )
        .expect("frontier");
    let scope = scope(&engine, "tenant-a");
    let mut session = Session::open(
        &engine,
        &scope,
        SessionOptions::new().with_session_token(token).wait_timeout(Duration::from_millis(5)),
    )
    .expect("opens");
    let started = std::time::Instant::now();
    let error = session.load(&collection("users"), &doc_id("missing")).expect_err("stale");
    assert!(
        matches!(error, SessionError::StaleReplica(stale) if stale.region() == &RegionId::try_new("region-a").expect("region"))
    );
    assert!(started.elapsed() >= Duration::from_millis(4), "bounded wait must yield, not spin");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// R2/R3 interaction — a delete and a pending put for the same key.
// ---------------------------------------------------------------------------

#[test]
fn store_then_delete_in_one_session_does_not_resurrect_on_a_later_save() {
    let (dir, engine) = open_engine("store-delete-resurrect");
    let mut session = open_session(&engine, "tenant-a");
    session.store(collection("users"), doc_id("x"), text("v1")).expect("stages");
    session.delete(collection("users"), doc_id("x")).expect("stages delete");
    committed(session.save_changes().expect("save"));
    assert!(
        read_engine_document(&engine, "tenant-a", "users", "x").is_none(),
        "the engine document is absent after the save"
    );

    // V1's Delete replaces an earlier Store: the pending put never reaches
    // the batch, so nothing survives the save to report or resend.
    assert!(
        session.what_changed().is_empty(),
        "no post-save report: the delete cancelled the pending put"
    );
    assert!(!session.has_changes(), "a second save has nothing queued");
    assert!(
        session.save_changes().expect("second save").is_none(),
        "the no-op save sends nothing, so the document stays deleted"
    );
    assert!(
        read_engine_document(&engine, "tenant-a", "users", "x").is_none(),
        "still absent after the second save"
    );
    drop(dir);
}

#[test]
fn delete_then_store_commits_the_put_with_last_command_wins() {
    // The session uses last-command-wins for a delete followed by a store.
    let (dir, engine) = open_engine("delete-store-batch");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    session.delete(collection("users"), doc_id("u1")).expect("stages delete");
    session.store(collection("users"), doc_id("u1"), text("v2")).expect("stages put");
    committed(session.save_changes().expect("save"));
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1").map(|d| d.value().clone()),
        Some(text("v2")),
        "V1's delete+put batch leaves the stored value in place; the engine's \
         one-write-per-target transaction receives the subsuming put, whose \
         committed outcome is the same"
    );
    assert!(!session.has_changes(), "the save cleared both staged commands");
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("u1"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("v2")),
        "the re-based tracked instance answers the surviving value"
    );
    drop(dir);
}

#[test]
fn load_after_a_pending_delete_answers_absent() {
    let (dir, engine) = open_engine("load-after-delete");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    assert!(
        session.load(&collection("users"), &doc_id("u1")).expect("loads").is_some(),
        "the document exists before the delete"
    );
    session.delete(collection("users"), doc_id("u1")).expect("stages delete");
    assert!(
        session.load(&collection("users"), &doc_id("u1")).expect("loads").is_none(),
        "V1's Delete registers known-missing, so Load answers null for a session-deleted id"
    );
    assert!(
        session.is_loaded(&collection("users"), &doc_id("u1")),
        "the deleted id stays loaded like V1's IsLoadedOrDeleted"
    );
    drop(dir);
}

#[test]
fn store_new_refuses_an_existing_document_at_commit_and_keeps_session_state() {
    let (dir, engine) = open_engine("store-new");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    session.store_new(collection("users"), doc_id("u1"), text("v2")).expect("stages");
    let outcome = session.save_changes().expect("save surfaces the engine outcome");
    match &outcome {
        Some(CommitOutcome::Rejected(CommitError::Conflict)) => {}
        other => panic!("expected a conflict rejection, got {other:?}"),
    }
    assert!(session.has_changes(), "a rejected save keeps the session state for retry");
    assert!(!session.what_changed().is_empty());
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1").map(|d| d.value().clone()),
        Some(text("v1")),
        "the engine document is untouched by the rejection"
    );
    drop(dir);
}

#[test]
fn post_commit_frontier_retry_reuses_one_any_operation() {
    let (dir, engine) = open_engine("post-commit-frontier-retry");
    let mut session = open_session(&engine, "tenant-a");
    session.store(collection("users"), doc_id("u1"), text("v1")).expect("stages");
    session.arm_post_commit_frontier_fault();
    assert!(matches!(session.save_changes(), Err(SessionError::WriteFrontierUnavailable)));
    assert!(matches!(
        session.store(collection("users"), doc_id("u1"), text("v2")),
        Err(SessionError::WriteFrontierUnavailable)
    ));

    committed(session.save_changes().expect("resolves the published operation"));
    let scope = scope(&engine, "tenant-a");
    assert_eq!(engine.journal_rows(&scope, 0, 10).expect("journal rows").len(), 1);
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1")
            .expect("published document")
            .metadata()
            .version(),
        1,
        "retry did not publish a second ExpectedVersion::Any operation"
    );
    drop(dir);
}

#[test]
fn checked_store_sends_the_expected_version() {
    let (dir, engine) = open_engine("checked-store");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    session
        .store_checked(collection("users"), doc_id("u1"), text("v2"), ExpectedVersion::Exact(1))
        .expect("stages");
    committed(session.save_changes().expect("save"));
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1").map(|d| d.value().clone()),
        Some(text("v2"))
    );

    // The same Exact check against the bumped version now conflicts.
    let mut second = open_session(&engine, "tenant-a");
    second
        .store_checked(collection("users"), doc_id("u1"), text("v3"), ExpectedVersion::Exact(1))
        .expect("stages");
    assert!(matches!(
        second.save_changes().expect("save"),
        Some(CommitOutcome::Rejected(CommitError::Conflict))
    ));
    drop(dir);
}

#[test]
fn checked_store_on_a_tracked_document_carries_its_expected_version() {
    let (dir, engine) = open_engine("checked-tracked");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    session.load(&collection("users"), &doc_id("u1")).expect("loads");
    session
        .store_checked(collection("users"), doc_id("u1"), text("v2"), ExpectedVersion::Exact(9999))
        .expect("stages");
    let outcome = session.save_changes().expect("save surfaces the engine outcome");
    assert!(
        matches!(outcome, Some(CommitOutcome::Rejected(CommitError::Conflict))),
        "the explicitly requested check must reach the commit, got {outcome:?}"
    );
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1").map(|d| d.value().clone()),
        Some(text("v1")),
        "the engine document is untouched by the rejection"
    );
    assert!(session.has_changes(), "a rejected save keeps the session state for retry");
    drop(dir);
}

#[test]
fn a_later_store_adopts_the_last_expectation() {
    let (dir, engine) = open_engine("checked-lww");
    let mut session = open_session(&engine, "tenant-a");
    session
        .store_checked(collection("users"), doc_id("u2"), text("v1"), ExpectedVersion::Exact(9999))
        .expect("stages");
    session.store(collection("users"), doc_id("u2"), text("v2")).expect("stages");
    committed(session.save_changes().expect("save"));
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u2").map(|d| d.value().clone()),
        Some(text("v2")),
        "the staged expectation is last-write-wins: the later store replaced the check"
    );
    drop(dir);
}

#[test]
fn a_successful_save_consumes_the_checked_expectation() {
    let (dir, engine) = open_engine("checked-consumed");
    commit_document(&engine, "tenant-a", "users", "u1", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    session
        .store_checked(collection("users"), doc_id("u1"), text("v2"), ExpectedVersion::Exact(1))
        .expect("stages");
    committed(session.save_changes().expect("save"));
    session.store(collection("users"), doc_id("u1"), text("v3")).expect("stages");
    committed(session.save_changes().expect("save"));
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "u1").map(|d| d.value().clone()),
        Some(text("v3")),
        "the re-based document saves unchecked again until a new checked store"
    );
    drop(dir);
}

#[test]
fn successful_save_clears_committed_state_and_evicts_deleted_documents() {
    let (dir, engine) = open_engine("committed-clearing");
    commit_document(&engine, "tenant-a", "users", "keep", text("v1"));
    commit_document(&engine, "tenant-a", "users", "drop", text("v1"));
    let mut session = open_session(&engine, "tenant-a");
    session.load(&collection("users"), &doc_id("keep")).expect("loads");
    session.load(&collection("users"), &doc_id("drop")).expect("loads");
    session.store(collection("users"), doc_id("keep"), text("v2")).expect("stages");
    session.delete(collection("users"), doc_id("drop")).expect("stages delete");

    committed(session.save_changes().expect("save"));
    assert!(!session.has_changes());
    assert!(
        session.is_loaded(&collection("users"), &doc_id("keep")),
        "a stored document stays tracked after the save"
    );
    assert!(!session.is_loaded(&collection("users"), &doc_id("drop")));
    assert!(
        read_engine_document(&engine, "tenant-a", "users", "drop").is_none(),
        "the delete committed"
    );
    assert_eq!(
        read_engine_document(&engine, "tenant-a", "users", "keep").map(|d| d.value().clone()),
        Some(text("v2"))
    );
    // The re-based tracked instance answers the committed value.
    assert_eq!(
        session
            .load(&collection("users"), &doc_id("keep"))
            .expect("loads")
            .map(|d| d.value().clone()),
        Some(text("v2"))
    );
    drop(dir);
}

// ---------------------------------------------------------------------------
// R8 / GAP-D08 — deferred-command save-time validation.
// ---------------------------------------------------------------------------

#[test]
fn deferred_command_on_a_session_deleted_document_refuses_the_whole_save() {
    let (dir, engine) = open_engine("defer-delete-conflict");
    let scope = scope(&engine, "tenant-a");
    commit_document(&engine, "tenant-a", "users", "doomed", object(&[]));
    let mut session = Session::open(&engine, &scope, SessionOptions::new()).expect("session");

    session
        .defer(SessionCommand::family(FamilyWriteRequest::counter_batch(
            CounterBatchWrite::try_new(
                scope.clone(),
                doc_id("doomed"),
                vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
                ExpectedVersion::Any,
                op_id("deferred-on-doomed"),
                Deadline::after(Duration::from_secs(30)).expect("deadline"),
                Cancellation::new(),
            )
            .expect("counter write"),
        )))
        .expect("defers");
    session.delete(collection("users"), doc_id("doomed")).expect("stages delete");

    let error = session.save_changes().expect_err("the save must refuse");
    assert_eq!(
        error,
        SessionError::DeferredCommandOnDeletedDocument { document: doc_id("doomed") },
        "V1 refuses a deferred command on a session-deleted document"
    );
    assert!(
        read_engine_document(&engine, "tenant-a", "users", "doomed").is_some(),
        "nothing committed: the refusal happened before staging"
    );
    assert_eq!(counter_total(&engine, "tenant-a", "doomed"), None, "no family write committed");
    assert!(session.has_changes(), "the session keeps its queued work");
    drop(dir);
}

#[test]
fn deferred_identity_commands_are_not_document_guarded() {
    let (dir, engine) = open_engine("defer-identity");
    let scope = scope(&engine, "tenant-a");
    let mut session = Session::open(&engine, &scope, SessionOptions::new()).expect("session");
    // An identity allocation guards no document, so the delete-conflict rule
    // does not apply even to an unrelated deletion.
    session.delete(collection("users"), doc_id("unrelated")).expect("stages delete");
    session
        .defer(SessionCommand::family(FamilyWriteRequest::identity_allocation(
            crate::families::IdentityWrite::try_new(
                scope.clone(),
                "manual-range".to_owned(),
                crate::families::IdentityCommand::Allocate {
                    name: "manual-range".to_owned(),
                    count: 5,
                },
                op_id("deferred-identity"),
                Deadline::after(Duration::from_secs(30)).expect("deadline"),
                Cancellation::new(),
            )
            .expect("identity write"),
        )))
        .expect("defers");
    committed(session.save_changes().expect("save"));
    drop(dir);
}

// ---------------------------------------------------------------------------
// R12 / GAP-D05 engine half — generated identity for no-explicit-ID stores.
// ---------------------------------------------------------------------------

#[test]
fn generated_stores_assign_sequential_ids_from_the_identity_family() {
    let (dir, engine) = open_engine("generated-ids");
    let scope = scope(&engine, "tenant-a");
    let mut session = Session::open(&engine, &scope, SessionOptions::new()).expect("session");
    session.store_generated(collection("users"), text("first")).expect("reserves");
    session.store_generated(collection("users"), text("second")).expect("reserves");
    session.store(collection("users"), doc_id("explicit"), text("explicit")).expect("stages");

    committed(session.save_changes().expect("save"));
    let generated = session.generated_ids().to_vec();
    assert_eq!(generated.len(), 2);
    let first = generated[0].1.as_str().parse::<u64>().expect("decimal id");
    let second = generated[1].1.as_str().parse::<u64>().expect("decimal id");
    assert_eq!(second, first + 1, "ids are sequential from the family range");
    assert!(
        read_engine_document(&engine, "tenant-a", "users", generated[0].1.as_str()).is_some(),
        "the first generated document committed under its assigned id"
    );
    assert!(
        read_engine_document(&engine, "tenant-a", "users", generated[1].1.as_str()).is_some(),
        "the second generated document committed under its assigned id"
    );

    // A later save in a new session continues the same range.
    let mut next_session = open_session(&engine, "tenant-a");
    next_session.store_generated(collection("users"), text("third")).expect("reserves");
    committed(next_session.save_changes().expect("save"));
    let third = next_session.generated_ids()[0].1.as_str().parse::<u64>().expect("decimal id");
    assert!(third > second, "the range persists across sessions ({third} > {second})");
    drop(dir);
}

#[test]
fn a_vetoed_save_leaks_no_generated_identity() {
    let (dir, engine) = open_engine("generated-leak");
    let scope = scope(&engine, "tenant-a");
    let mut session = Session::open(&engine, &scope, SessionOptions::new()).expect("session");
    session.on_before_store(Box::new(|_event: &mut BeforeStoreEvent| {
        Err(SessionHookVeto::new("veto the generated put"))
    }));
    session.store_generated(collection("users"), text("one")).expect("reserves");
    session.store_generated(collection("users"), text("two")).expect("reserves");
    assert!(session.save_changes().is_err(), "the veto aborts the save");

    // The identity range never advanced: the allocation rode the aborted
    // transaction, so a later save starts from the same next value.
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let context = FamilyReadContext::try_new(scope.clone(), snapshot).expect("context");
    let request = FamilyReadRequest::identity_range(
        crate::families::IdentityRead::try_new(
            vec!["documents/users".to_owned()],
            Deadline::after(Duration::from_secs(30)).expect("deadline"),
            Cancellation::new(),
        )
        .expect("identity read"),
    );
    match engine.families().read(context, request).expect("identity read") {
        crate::families::FamilyReadResult::IdentityRange(result) => {
            assert_eq!(
                result.entries().first().map(IdentityEntry::next),
                Some(0),
                "the aborted save allocated nothing from the family range"
            );
        }
        other => panic!("expected an identity range result, got {other:?}"),
    }
    drop(dir);
}

// ---------------------------------------------------------------------------
// R11 / GAP-D16 — lifecycle hooks with a cannot-partially-commit contract.
// ---------------------------------------------------------------------------

#[test]
fn before_store_mutates_the_staged_value_and_before_delete_vetoes_atomically() {
    let (dir, engine) = open_engine("hooks");
    commit_document(&engine, "tenant-a", "users", "doomed", text("v1"));

    // Mutation: the committed document carries the hook's edit.
    {
        let scope = scope(&engine, "tenant-a");
        let mut session = Session::open(&engine, &scope, SessionOptions::new()).expect("session");
        session.on_before_store(Box::new(|event: &mut BeforeStoreEvent| {
            if let DocumentValue::String(value) = event.value_mut() {
                value.push_str("-touched");
            }
            Ok(())
        }));
        session.store(collection("users"), doc_id("h1"), text("raw")).expect("stages");
        committed(session.save_changes().expect("save"));
        assert_eq!(
            read_engine_document(&engine, "tenant-a", "users", "h1").map(|d| d.value().clone()),
            Some(text("raw-touched"))
        );
    }

    // Veto: the save aborts with nothing staged or committed.
    {
        let scope = scope(&engine, "tenant-a");
        let mut session = Session::open(&engine, &scope, SessionOptions::new()).expect("session");
        session.load(&collection("users"), &doc_id("doomed")).expect("loads");
        session.delete(collection("users"), doc_id("doomed")).expect("stages delete");
        session.on_before_delete(Box::new(|_event: &BeforeDeleteEvent| {
            Err(SessionHookVeto::new("deletion refused"))
        }));
        let error = session.save_changes().expect_err("the veto aborts the save");
        assert!(
            matches!(error, SessionError::HookVetoed { .. }),
            "expected a hook veto, got {error:?}"
        );
        assert!(
            read_engine_document(&engine, "tenant-a", "users", "doomed").is_some(),
            "nothing committed"
        );
        assert!(session.has_changes(), "the delete stays queued for a retry");
    }
    drop(dir);
}

#[test]
fn after_save_observes_each_commit_once_with_operation_and_sequence() {
    let (dir, engine) = open_engine("after-save");
    let observed: Arc<Mutex<Vec<(String, u64)>>> = Arc::new(Mutex::new(Vec::new()));
    let mut session = open_session(&engine, "tenant-a");
    let sink = Arc::clone(&observed);
    session.on_after_save(Box::new(move |event: &crate::session::AfterSaveEvent| {
        sink.lock()
            .expect("sink")
            .push((event.operation_id().as_str().to_owned(), event.sequence().get()));
    }));

    session.store(collection("users"), doc_id("s1"), text("v1")).expect("stages");
    let first = committed(session.save_changes().expect("save"));
    session.store(collection("users"), doc_id("s2"), text("v2")).expect("stages");
    let second = committed(session.save_changes().expect("save"));

    let events = observed.lock().expect("sink").clone();
    assert_eq!(events.len(), 2, "one observation per successful save");
    assert_eq!(events[0], (first.operation_id().as_str().to_owned(), first.sequence().get()));
    assert_eq!(events[1], (second.operation_id().as_str().to_owned(), second.sequence().get()));
    assert_ne!(events[0].0, events[1].0, "each save carries its own operation identity");
    assert!(
        events.iter().all(|(operation, _)| operation.starts_with("terrabase-session-")),
        "new session identities use the canonical product prefix"
    );
    drop(dir);
}

// ---------------------------------------------------------------------------
// R10 / GAP-D15 — session stream surface over W4-STREAM.
// ---------------------------------------------------------------------------

#[test]
fn stream_documents_serves_canonical_batches_with_terminal_completion() {
    let (dir, engine) = open_engine("stream");
    commit_document(&engine, "tenant-a", "users", "010", text("a"));
    commit_document(&engine, "tenant-a", "users", "020", text("b"));
    commit_document(&engine, "tenant-a", "users", "030", text("c"));

    let session = open_session(&engine, "tenant-a");
    let selector = DocumentStreamSelector::try_new(
        collection("users"),
        Deadline::after(Duration::from_secs(30)).expect("deadline"),
        Cancellation::new(),
    )
    .expect("selector");
    let mut stream = session.stream_documents(selector).expect("opens");

    let mut ids = Vec::new();
    let mut completion = None;
    while completion.is_none() {
        let batch = stream.next_batch(2).expect("batch");
        for item in batch.items() {
            match item {
                StreamItem::Document(document) => ids.push(document.id().as_str().to_owned()),
                other => panic!("expected documents, got {other:?}"),
            }
        }
        if let Some(done) = batch.completion() {
            completion = Some(done);
        }
    }
    assert_eq!(ids, vec!["010", "020", "030"], "canonical key order across batches");
    assert_eq!(completion, Some(StreamCompletion::EndOfStream));
    assert!(
        stream.session_token().frontier().entries().any(|(_, sequence)| sequence > 0),
        "consuming a stream advances its causal token"
    );

    // A second collection in the same tenant stays outside the selector,
    // and a batch limit that covers the page serves it in one batch.
    commit_document(&engine, "tenant-a", "orders", "040", text("other-collection"));
    let second = open_session(&engine, "tenant-a");
    let selector = DocumentStreamSelector::try_new(
        collection("users"),
        Deadline::after(Duration::from_secs(30)).expect("deadline"),
        Cancellation::new(),
    )
    .expect("selector");
    let mut stream = second.stream_documents(selector).expect("opens");
    let batch = stream.next_batch(3).expect("batch");
    let ids: Vec<_> = batch
        .items()
        .iter()
        .map(|item| match item {
            StreamItem::Document(document) => document.id().as_str().to_owned(),
            other => panic!("expected documents, got {other:?}"),
        })
        .collect();
    assert_eq!(ids, vec!["010", "020", "030"], "the selector scopes to its collection");
    assert_eq!(batch.completion(), Some(StreamCompletion::EndOfStream));
    drop(dir);
}

#[test]
fn load_starting_with_pages_by_prefix_and_match() {
    let (dir, engine) = open_engine("load-starting-with");
    commit_document(&engine, "tenant-a", "users", "order/1", text("a"));
    commit_document(&engine, "tenant-a", "users", "order/2", text("b"));
    commit_document(&engine, "tenant-a", "users", "order/3", text("c"));
    commit_document(&engine, "tenant-a", "users", "user/9", text("d"));

    let session = open_session(&engine, "tenant-a");
    let page =
        session.load_starting_with(collection("users"), "order/", None, 1, 2).expect("pages");
    assert_eq!(
        page.iter().map(|document| document.id().as_str()).collect::<Vec<_>>(),
        vec!["order/2", "order/3"],
        "skip one, take two, canonical order"
    );
    let matched =
        session.load_starting_with(collection("users"), "order/", Some("2"), 0, 10).expect("pages");
    assert_eq!(
        matched.iter().map(|document| document.id().as_str()).collect::<Vec<_>>(),
        vec!["order/2"],
        "the match selector restricts by substring"
    );
    drop(dir);
}

// ---------------------------------------------------------------------------
// R1 — scope binding.
// ---------------------------------------------------------------------------

#[test]
fn a_scope_from_another_engine_is_refused_at_open() {
    let (_first_dir, first) = open_engine("scope-first");
    let (_second_dir, second) = open_engine("scope-second");
    let foreign_scope = TenantScope::from_parts(
        TenantId::try_new("tenant-a").expect("valid tenant"),
        first.identity(),
    );
    let error = Session::open(&second, &foreign_scope, SessionOptions::new())
        .expect_err("the scope names another engine");
    assert_eq!(error, SessionError::ScopeMismatch);
}

// ---------------------------------------------------------------------------
// Options vocabulary (public construction).
// ---------------------------------------------------------------------------

#[test]
fn options_default_to_tracked_single_node_synced_and_build_fluently() {
    let defaults = SessionOptions::default();
    assert_eq!(defaults.tracking(), SessionTracking::Tracked);
    assert_eq!(defaults.transaction_mode(), SessionTransactionMode::SingleNode);
    assert_eq!(defaults.save_durability(), LocalDurability::Synced);

    let built =
        SessionOptions::new().no_tracking().cluster_wide().durability(LocalDurability::Written);
    assert_eq!(built.tracking(), SessionTracking::NoTracking);
    assert_eq!(built.transaction_mode(), SessionTransactionMode::ClusterWide);
    assert_eq!(built.save_durability(), LocalDurability::Written);

    let change = FieldChange::new(
        "tags".to_owned(),
        "1".to_owned(),
        Some(text("b")),
        Some(text("x")),
        FieldChangeKind::ArrayValueChanged,
    );
    assert_eq!(change.field_path(), "tags");
    assert_eq!(change.field_name(), "1");
    assert_eq!(change.field_full_name(), "tags.1");
}
