//! In-crate W4-STREAM tests covering the contract's red-test list.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope`
//! has no public constructor by design; the public-construction paths are
//! covered by `tests/streaming/`. Every fixture uses a temporary directory
//! and synthetic documents only, and no test inspects pages, WAL bytes, or
//! private lock state.

use std::{
    collections::BTreeSet,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::{Duration, Instant},
};

use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::families::attachments::{AttachmentContentWrite, AttachmentSegment};
use crate::families::{
    ArchivalCommand, ArchivalWrite, AttachmentMutation, AttachmentWrite, CompareExchangeCondition,
    CompareExchangeWrite, CounterBatchWrite, CounterOperation, CounterRead, FamilyKind,
    FamilyReadContext, FamilyReadRequest, FamilyWriteRequest, IdentityCommand, IdentityWrite,
    LifecycleCommand, LifecycleWrite, RevisionCommand, RevisionWrite, SchemaDefinition, SchemaRule,
    SchemaVersion, TimeSeriesAppend, TimeSeriesBatchWrite, TimeSeriesDelete, TimeSeriesRange,
    TombstoneCommand, TombstoneWrite,
};
use crate::operations::QueryInput;
use crate::query::{
    IndexNamespace, OrderDirection, OrderingKey, QueryCondition, QueryOrdering, QueryPaging,
    QueryPredicate, QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
};
use crate::streaming::{
    self, AttachmentChangeEntry, AttachmentChangePayload, ChangeEvent, ChangeFilter, ChangeKind,
    CounterChangeEntry, CounterChangePayload, DocumentStreamSelector, FamilyChangePayload,
    FamilyStreamSelector, MAX_CHANGE_PAYLOAD_ENTRIES, PayloadShape, StreamBatch, StreamCompletion,
    StreamCursor, StreamError, StreamItem, StreamSource, StreamStore, SubscriptionAcknowledgement,
    SubscriptionSpec, SubscriptionStatus, TimeSeriesChangePayload, TimeSeriesStreamRange,
};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, ReadSnapshot, StorageSequence, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-w4-stream-{tag}-{}-{index}", std::process::id()))
}

/// Opens one durable engine and returns it with its location for cleanup.
fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(90)).expect("valid deadline")
}

fn scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), engine.identity())
}

fn text_document(collection_name: &str, id: &str, text: &str) -> Document {
    Document::new(
        document_id(id),
        collection(collection_name),
        DocumentValue::String(text.to_owned()),
    )
}

/// Commits one document as its own publication and returns its sequence.
fn commit_document(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    collection_name: &str,
    id: &str,
    text: &str,
) -> StorageSequence {
    commit_documents(engine, tenant_name, operation_name, &[(collection_name, id, text)])
}

/// Commits several documents as one publication and returns its sequence.
///
/// Tests that do not assert per-document change events batch their
/// fixtures this way: one publication serves many documents.
fn commit_documents(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    documents: &[(&str, &str, &str)],
) -> StorageSequence {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation(operation_name)).expect("begins");
    for (collection_name, id, text) in documents {
        transaction
            .put(text_document(collection_name, id, text), ExpectedVersion::Any)
            .expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => receipt.sequence(),
        other => panic!("commit did not succeed: {other:?}"),
    }
}

/// Builds `count` synthetic documents of one collection with zero-padded ids.
fn numbered_documents(
    collection_name: &str,
    count: usize,
    width: usize,
) -> Vec<(String, String, String)> {
    (0..count)
        .map(|index| (collection_name.to_owned(), format!("doc-{index:0width$}"), "v".to_owned()))
        .collect()
}

/// Borrows one owned fixture list for the batch-commit helper.
fn as_refs(documents: &[(String, String, String)]) -> Vec<(&str, &str, &str)> {
    documents
        .iter()
        .map(|(collection_name, id, text)| (collection_name.as_str(), id.as_str(), text.as_str()))
        .collect()
}

/// One uncancellable pull of a snapshot-bound stream.
fn pull(
    store: &StreamStore<'_>,
    tenant_scope: &TenantScope,
    snapshot: &ReadSnapshot,
    source: &StreamSource,
    cursor: StreamCursor,
    limit: u32,
) -> Result<StreamBatch, StreamError> {
    store.next_batch(tenant_scope, snapshot, source, cursor, limit, &Cancellation::new())
}

/// Collects every document id of one collection's stream, in order.
fn drain_document_ids(
    engine: &Engine,
    tenant_scope: &TenantScope,
    selector: DocumentStreamSelector,
    limit: u32,
) -> Vec<String> {
    let store = engine.streams();
    let snapshot = engine.read_snapshot(tenant_scope).expect("snapshot");
    let source = StreamSource::Documents(selector);
    let mut cursor = store.open(tenant_scope, &snapshot, source.clone()).expect("opens");
    let mut ids = Vec::new();
    loop {
        let batch = pull(&store, tenant_scope, &snapshot, &source, cursor, limit).expect("batch");
        for item in batch.items() {
            match item {
                StreamItem::Document(document) => ids.push(document.id().as_str().to_owned()),
                other => panic!("document stream yielded a non-document item: {other:?}"),
            }
        }
        let complete = batch.completion();
        cursor = batch.next_cursor().clone();
        if complete.is_some() {
            assert_eq!(complete, Some(StreamCompletion::EndOfStream));
            break;
        }
    }
    ids
}

fn selector(collection_name: &str) -> DocumentStreamSelector {
    DocumentStreamSelector::try_new(collection(collection_name), deadline(), Cancellation::new())
        .expect("valid selector")
}

fn subscription(name: &str, max_events: u32) -> SubscriptionSpec {
    SubscriptionSpec::try_new(name, ChangeFilter::try_new(None, None).expect("filter"), max_events)
        .expect("valid spec")
}

fn commit_family_write(
    engine: &Engine,
    tenant_scope: &TenantScope,
    operation_name: &str,
    write: FamilyWriteRequest,
) {
    let snapshot = engine.read_snapshot(tenant_scope).expect("snapshot");
    let identity = operation(operation_name);
    let mut transaction = engine.begin(tenant_scope, snapshot, identity.clone()).expect("begins");
    engine.families().stage(&mut transaction, write).expect("stages the family write");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("family write did not commit: {other:?}"),
    }
}

/// Collects the (operation, sequence) identities of one batch's events.
fn event_identities<'event>(
    events: impl IntoIterator<Item = &'event ChangeEvent>,
) -> Vec<(String, u64)> {
    events
        .into_iter()
        .map(|event| (event.operation_id().as_str().to_owned(), event.sequence().get()))
        .collect()
}

// ---------------------------------------------------------------------------
// Sources and ordering
// ---------------------------------------------------------------------------

#[test]
fn document_stream_preserves_key_order_and_result_shape() {
    let (dir, engine) = open_engine("doc-order");
    let tenant_scope = scope(&engine, "t1");
    // Insertion order is deliberately not canonical order, and one
    // publication serves all five documents.
    commit_documents(
        &engine,
        "t1",
        "op-batch",
        &[
            ("users", "delta", "d"),
            ("users", "alpha", "a"),
            ("users", "echo", "e"),
            ("users", "bravo", "b"),
            ("users", "charlie", "c"),
        ],
    );
    let ids = drain_document_ids(&engine, &tenant_scope, selector("users"), 2);
    assert_eq!(ids, ["alpha", "bravo", "charlie", "delta", "echo"]);

    // The result shape is the full logical document: id, collection, value,
    // and metadata — exactly as the snapshot observed them.
    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let source = StreamSource::Documents(selector("users"));
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let batch = pull(&store, &tenant_scope, &snapshot, &source, cursor, 3).expect("batch");
    let document = match batch.items().first() {
        Some(StreamItem::Document(document)) => document,
        other => panic!("expected a document item: {other:?}"),
    };
    assert_eq!(document.id().as_str(), "alpha");
    assert_eq!(document.collection().as_str(), "users");
    assert_eq!(document.value(), &DocumentValue::String("a".to_owned()));
    assert!(document.metadata().version() > 0, "published documents carry a version");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn document_stream_selectors_support_id_prefix_match_and_exclusion() {
    let (dir, engine) = open_engine("doc-selectors");
    let tenant_scope = scope(&engine, "t1");
    commit_documents(
        &engine,
        "t1",
        "op-users",
        &[
            ("users", "alice", "a"),
            ("users", "bob", "b"),
            ("users", "bob-2", "b2"),
            ("users", "carol", "c"),
        ],
    );
    commit_documents(&engine, "t1", "op-orders", &[("orders", "order-1", "o")]);

    let _store = engine.streams();
    let prefix = drain_document_ids(
        &engine,
        &tenant_scope,
        selector("users").with_prefix("b").expect("prefix"),
        10,
    );
    assert_eq!(prefix, ["bob", "bob-2"]);

    let matching = drain_document_ids(
        &engine,
        &tenant_scope,
        selector("users").with_match("o").expect("match"),
        10,
    );
    assert_eq!(matching, ["bob", "bob-2", "carol"]);

    let exact = drain_document_ids(
        &engine,
        &tenant_scope,
        selector("users").with_id(document_id("alice")),
        10,
    );
    assert_eq!(exact, ["alice"]);

    let excluded = drain_document_ids(
        &engine,
        &tenant_scope,
        selector("users")
            .with_prefix("b")
            .expect("prefix")
            .excluding(vec![document_id("bob-2")])
            .expect("exclusion"),
        10,
    );
    assert_eq!(excluded, ["bob"]);

    let combined = drain_document_ids(
        &engine,
        &tenant_scope,
        selector("users").with_prefix("b").expect("prefix").with_match("2").expect("match"),
        10,
    );
    assert_eq!(combined, ["bob-2"]);

    // A selector over another collection observes only that collection.
    let orders = drain_document_ids(&engine, &tenant_scope, selector("orders"), 10);
    assert_eq!(orders, ["order-1"]);

    std::fs::remove_dir_all(&dir).ok();
}

/// Ticket #87's red test, in the shape the GAP-D01 gates recorded: one
/// cross-collection document rides inside the first lookahead window, the
/// batch fills on the selected collection's documents alone, and the short
/// tail window still holds a matching item. That item must be delivered
/// before the terminal completion, not dropped by it.
#[test]
fn document_stream_delivers_the_triggering_item_at_the_exact_fill_boundary() {
    let (dir, engine) = open_engine("doc-exact-fill");
    let tenant_scope = scope(&engine, "t1");
    // Canonical order is orders/040, users/010, users/020, users/030: the
    // orders document consumes one slot of the first limit+1 window.
    commit_documents(
        &engine,
        "t1",
        "op-batch",
        &[
            ("users", "030", "c"),
            ("orders", "040", "x"),
            ("users", "010", "a"),
            ("users", "020", "b"),
        ],
    );
    let ids = drain_document_ids(&engine, &tenant_scope, selector("users"), 2);
    assert_eq!(ids, ["010", "020", "030"], "no item is lost at the boundary");

    std::fs::remove_dir_all(&dir).ok();
}

/// Property-style coverage of the exact-fill boundary (ticket #87): for
/// every limit 1..=8 and every fixture shape — a mixed orders+users tenant
/// with a varying number of cross-collection documents ahead of the users
/// run — each stream delivers exactly the selected set, no more and no
/// fewer, before its terminal completion.
#[test]
fn document_stream_delivers_exact_sets_across_limits_and_window_shapes() {
    let (dir, engine) = open_engine("doc-exact-matrix");
    // (tag, orders documents, users documents): each shape places the
    // users run at a different offset from the window boundary, so for
    // some limit in 1..=8 the batch fills exactly when the short tail
    // window arrives.
    let shapes: &[(&str, usize, usize)] = &[
        ("g0-n3", 0, 3),
        ("g1-n3", 1, 3),
        ("g2-n3", 2, 3),
        ("g1-n5", 1, 5),
        ("g3-n5", 3, 5),
        ("g0-n9", 0, 9),
        ("g1-n9", 1, 9),
        ("g2-n9", 2, 9),
        ("g3-n9", 3, 9),
    ];
    for (tag, orders_count, users_count) in shapes {
        let tenant_name = format!("t-{tag}");
        let tenant_scope = scope(&engine, &tenant_name);
        // Identifiers stay tenant-unique across collections: the resume
        // cursor names a document identifier, and identifier reuse across
        // collections is the storage port's documented at-least-once
        // shape, not this exact-delivery contract.
        let mut fixture: Vec<(String, String, String)> = (0..*orders_count)
            .map(|index| ("orders".to_owned(), format!("order-{index:03}"), "x".to_owned()))
            .collect();
        fixture.extend(numbered_documents("users", *users_count, 3));
        commit_documents(&engine, &tenant_name, "op-batch", &as_refs(&fixture));

        let expected_orders: Vec<String> =
            (0..*orders_count).map(|index| format!("order-{index:03}")).collect();
        let expected_users: Vec<String> =
            (0..*users_count).map(|index| format!("doc-{index:03}")).collect();

        // Both collections' streams deliver their exact sets.
        for limit in 1..=8_u32 {
            assert_eq!(
                drain_document_ids(&engine, &tenant_scope, selector("orders"), limit),
                expected_orders,
                "orders set at shape {tag} limit {limit}"
            );
            assert_eq!(
                drain_document_ids(&engine, &tenant_scope, selector("users"), limit),
                expected_users,
                "users set at shape {tag} limit {limit}"
            );
        }
    }

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn document_stream_missing_documents_and_invalid_selectors_return_typed_errors() {
    let (dir, engine) = open_engine("doc-errors");
    let tenant_scope = scope(&engine, "t1");
    commit_document(&engine, "t1", "op-1", "users", "present", "value");
    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");

    // An exact-identity selector addressing a document absent from the
    // snapshot view is a typed error before the first batch.
    let missing = store
        .open(
            &tenant_scope,
            &snapshot,
            StreamSource::Documents(selector("users").with_id(document_id("absent"))),
        )
        .expect_err("missing document is typed");
    assert_eq!(missing, StreamError::InvalidSource);

    // The reserved family collection is not a document-stream source.
    assert!(
        DocumentStreamSelector::try_new(
            collection("terrabase.families"),
            deadline(),
            Cancellation::new()
        )
        .is_err()
    );

    // Invalid selector text is rejected at construction.
    assert!(selector("users").with_prefix("").is_err());
    assert!(selector("users").with_match("").is_err());
    assert!(selector("users").excluding(Vec::new()).is_err());

    // A malformed continuation cursor — another source, another engine,
    // another snapshot, or a zero bound — never silently serves.
    let source = StreamSource::Documents(selector("users"));
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let wrong_source = StreamSource::Documents(selector("orders"));
    assert_eq!(
        pull(&store, &tenant_scope, &snapshot, &wrong_source, cursor.clone(), 4).unwrap_err(),
        StreamError::InvalidCursor
    );
    let (dir_b, engine_b) = open_engine("doc-errors-b");
    let scope_b = scope(&engine_b, "t1");
    let snapshot_b = engine_b.read_snapshot(&scope_b).expect("snapshot");
    let cursor_b = engine_b
        .streams()
        .open(&scope_b, &snapshot_b, StreamSource::Documents(selector("users")))
        .expect("opens");
    assert_eq!(
        pull(&store, &tenant_scope, &snapshot, &source, cursor_b, 4).unwrap_err(),
        StreamError::InvalidCursor
    );
    // A later snapshot of the same engine is a different stream boundary.
    commit_document(&engine, "t1", "op-2", "users", "later", "value");
    let fresh_snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    assert_eq!(
        pull(&store, &tenant_scope, &fresh_snapshot, &source, cursor, 4).unwrap_err(),
        StreamError::InvalidCursor
    );
    let cursor_again = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    assert_eq!(
        pull(&store, &tenant_scope, &snapshot, &source, cursor_again.clone(), 0).unwrap_err(),
        StreamError::InvalidSource
    );
    assert_eq!(
        pull(
            &store,
            &tenant_scope,
            &snapshot,
            &source,
            cursor_again,
            streaming::MAX_STREAM_BATCH_ITEMS + 1
        )
        .unwrap_err(),
        StreamError::InvalidSource
    );

    engine_b.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir_b).ok();
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn exact_document_selector_reaches_targets_beyond_window_probe_bound() {
    let (dir, engine) = open_engine("doc-exact-keyed-lookup");
    let tenant_scope = scope(&engine, "t1");

    // The old existence probe stopped after 64 windows of 64 documents. Put
    // the target after that boundary to ensure exact validation uses the
    // keyed snapshot seam rather than a bounded prefix scan.
    let prefix = numbered_documents("users", 4_096, 4);
    commit_documents(&engine, "t1", "op-prefix", &as_refs(&prefix));
    commit_document(&engine, "t1", "op-target", "users", "target", "value");

    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let source = StreamSource::Documents(selector("users").with_id(document_id("target")));
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("target exists");
    let batch = pull(&store, &tenant_scope, &snapshot, &source, cursor, 1).expect("batch");
    assert_eq!(batch.items().len(), 1);
    assert!(
        matches!(batch.items()[0], StreamItem::Document(ref document) if document.id().as_str() == "target")
    );
    assert_eq!(batch.completion(), Some(StreamCompletion::EndOfStream));

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_stream_reports_the_index_generation_it_used() {
    let (dir, engine) = open_engine("query-generation");
    let tenant_scope = scope(&engine, "t1");
    // Ranks are assigned so canonical insertion order and rank order
    // differ: d-3 ranks 3, d-1 ranks 4, d-2 ranks 5.
    let documents = [("d-3", 3_i64), ("d-1", 4), ("d-2", 5)]
        .iter()
        .map(|(id, rank)| {
            Document::new(
                document_id(id),
                collection("users"),
                DocumentValue::Object(
                    [("rank".to_owned(), DocumentValue::Integer(*rank))].into_iter().collect(),
                ),
            )
        })
        .collect::<Vec<_>>();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation("q-seed")).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    let spec = QuerySpec::try_new(
        tenant("t1"),
        QuerySource::try_new(collection("users")).expect("source"),
        QueryPredicate::all(vec![QueryCondition::Exists { field: "rank".to_owned() }])
            .expect("predicate"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("rank", OrderDirection::Ascending).expect("key"),
        ])
        .expect("ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, Some(4)).expect("paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, vec![]).expect("staleness"),
        deadline(),
        Cancellation::new(),
    )
    .expect("spec");
    let plan = engine.queries().compile(QueryInput::Typed(spec)).expect("compiles");

    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let source = StreamSource::Query(plan);
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let batch = pull(&store, &tenant_scope, &snapshot, &source, cursor, 2).expect("batch");
    // The W3-2 execution path is index-less, so the honest generation
    // report for this tracer is `None` on every batch; a plan whose
    // staleness policy requires an index the path cannot supply is a typed
    // refusal, not a silently stale stream.
    assert_eq!(batch.index_generation(), None);
    let ids: Vec<&str> = batch
        .items()
        .iter()
        .map(|item| match item {
            StreamItem::QueryRow(row) => row.document().id().as_str(),
            other => panic!("query stream yielded a non-row item: {other:?}"),
        })
        .collect();
    assert_eq!(ids, ["d-3", "d-1"]);
    let second = pull(&store, &tenant_scope, &snapshot, &source, batch.next_cursor().clone(), 2)
        .expect("batch");
    assert_eq!(second.index_generation(), None);
    assert_eq!(second.completion(), Some(StreamCompletion::EndOfStream));
    assert_eq!(second.items().len(), 1);

    // A strict-namespaced plan cannot be served index-less: typed refusal.
    let strict = QuerySpec::try_new(
        tenant("t1"),
        QuerySource::try_new(collection("users")).expect("source"),
        QueryPredicate::all(vec![]).expect("predicate"),
        QueryOrdering::try_new(vec![]).expect("ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, Some(4)).expect("paging"),
        StalenessPolicy::try_new(
            StalenessMode::Strict,
            vec![IndexNamespace::try_new(tenant("t1"), "by-rank").expect("namespace")],
        )
        .expect("staleness"),
        deadline(),
        Cancellation::new(),
    )
    .expect("spec");
    let strict_plan = engine.queries().compile(QueryInput::Typed(strict)).expect("compiles");
    let strict_source = StreamSource::Query(strict_plan);
    let strict_cursor = store.open(&tenant_scope, &snapshot, strict_source.clone()).expect("opens");
    assert_eq!(
        pull(&store, &tenant_scope, &snapshot, &strict_source, strict_cursor, 2).unwrap_err(),
        StreamError::InvalidSource
    );

    // A limit-free plan would materialize every match per batch, so it is
    // not a streamable source.
    let unbounded = QuerySpec::try_new(
        tenant("t1"),
        QuerySource::try_new(collection("users")).expect("source"),
        QueryPredicate::all(vec![]).expect("predicate"),
        QueryOrdering::try_new(vec![]).expect("ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, vec![]).expect("staleness"),
        deadline(),
        Cancellation::new(),
    )
    .expect("spec");
    let unbounded_plan = engine.queries().compile(QueryInput::Typed(unbounded)).expect("compiles");
    assert_eq!(
        store.open(&tenant_scope, &snapshot, StreamSource::Query(unbounded_plan)).unwrap_err(),
        StreamError::InvalidSource
    );

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn time_series_stream_preserves_timestamp_tag_and_value_order() {
    let (dir, engine) = open_engine("timeseries-order");
    let tenant_scope = scope(&engine, "t1");
    commit_document(&engine, "t1", "op-doc", "users", "host-1", "value");

    // Appends arrive out of timestamp order with distinct tags and values.
    let appends = vec![
        (30_i64, vec![3.0_f64], Some("b".to_owned())),
        (10_i64, vec![1.0_f64], None),
        (20_i64, vec![2.0_f64], Some("a".to_owned())),
    ]
    .into_iter()
    .map(|(timestamp, values, tag)| TimeSeriesAppend::new(timestamp, values, tag))
    .collect();
    let identity = operation("op-ts");
    let write = TimeSeriesBatchWrite::try_new(
        tenant_scope.clone(),
        document_id("host-1"),
        "cpu".to_owned(),
        appends,
        Vec::new(),
        None,
        ExpectedVersion::Any,
        identity.clone(),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid batch");
    commit_family_write(
        &engine,
        &tenant_scope,
        "op-ts",
        FamilyWriteRequest::time_series_batch(write),
    );

    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let source = StreamSource::TimeSeries(
        TimeSeriesStreamRange::try_new(
            document_id("host-1"),
            "cpu".to_owned(),
            None,
            deadline(),
            Cancellation::new(),
        )
        .expect("valid range"),
    );
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let first = pull(&store, &tenant_scope, &snapshot, &source, cursor, 2).expect("batch");
    let entries: Vec<(i64, Option<&str>, &[f64])> = first
        .items()
        .iter()
        .map(|item| match item {
            StreamItem::TimeSeries(entry) => (entry.timestamp(), entry.tag(), entry.values()),
            other => panic!("time-series stream yielded a foreign item: {other:?}"),
        })
        .collect();
    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0].0, 10, "timestamp order");
    assert_eq!(entries[0].1, None);
    assert_eq!(entries[0].2, &[1.0]);
    assert_eq!(entries[1].0, 20);
    assert_eq!(entries[1].1, Some("a"));
    assert_eq!(entries[1].2, &[2.0]);
    assert_eq!(first.completion(), None);

    let second = pull(&store, &tenant_scope, &snapshot, &source, first.next_cursor().clone(), 2)
        .expect("batch");
    let last = match second.items().first() {
        Some(StreamItem::TimeSeries(entry)) => entry,
        other => panic!("expected a time-series item: {other:?}"),
    };
    assert_eq!(last.timestamp(), 30);
    assert_eq!(last.tag(), Some("b"));
    assert_eq!(last.values(), &[3.0]);
    assert_eq!(second.completion(), Some(StreamCompletion::EndOfStream));

    // A bounded range keeps its boundary semantics.
    let ranged = StreamSource::TimeSeries(
        TimeSeriesStreamRange::try_new(
            document_id("host-1"),
            "cpu".to_owned(),
            Some(TimeSeriesRange::try_new(15, 25).expect("range")),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid range"),
    );
    let stamps = drain_series(&engine, &tenant_scope, &ranged, 10);
    assert_eq!(stamps, [20]);

    std::fs::remove_dir_all(&dir).ok();
}

/// Drains the timestamps of one time-series source, in order.
fn drain_series(
    engine: &Engine,
    tenant_scope: &TenantScope,
    source: &StreamSource,
    limit: u32,
) -> Vec<i64> {
    let store = engine.streams();
    let snapshot = engine.read_snapshot(tenant_scope).expect("snapshot");
    let mut cursor = store.open(tenant_scope, &snapshot, source.clone()).expect("opens");
    let mut stamps = Vec::new();
    loop {
        let batch = pull(&store, tenant_scope, &snapshot, source, cursor, limit).expect("batch");
        for item in batch.items() {
            match item {
                StreamItem::TimeSeries(entry) => stamps.push(entry.timestamp()),
                other => panic!("time-series stream yielded a foreign item: {other:?}"),
            }
        }
        let complete = batch.completion();
        cursor = batch.next_cursor().clone();
        if complete.is_some() {
            break;
        }
    }
    stamps
}

#[test]
fn family_stream_reuses_the_family_seams_bounded_readers() {
    let (dir, engine) = open_engine("family-readers");
    let tenant_scope = scope(&engine, "t1");
    commit_document(&engine, "t1", "op-doc", "users", "host-1", "value");
    let identity = operation("op-counters");
    let write = CounterBatchWrite::try_new(
        tenant_scope.clone(),
        document_id("host-1"),
        vec![
            CounterOperation::Increment { name: "hits".to_owned(), delta: 2 },
            CounterOperation::Increment { name: "misses".to_owned(), delta: 5 },
        ],
        ExpectedVersion::Any,
        identity.clone(),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid batch");
    commit_family_write(
        &engine,
        &tenant_scope,
        "op-counters",
        FamilyWriteRequest::counter_batch(write),
    );

    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let request = FamilyReadRequest::counter_group(
        CounterRead::try_new(document_id("host-1"), Vec::new(), deadline(), Cancellation::new())
            .expect("valid read"),
    );
    let source =
        StreamSource::Family(FamilyStreamSelector::try_new(request.clone()).expect("selector"));
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let batch = pull(&store, &tenant_scope, &snapshot, &source, cursor, 4).expect("batch");
    let streamed = match batch.items().first() {
        Some(StreamItem::Family(result)) => result.clone(),
        other => panic!("family stream yielded a foreign item: {other:?}"),
    };
    // The streamed value is exactly what the family seam's own reader
    // serves for the same snapshot: the stream wraps, never bypasses, the
    // bounded family readers.
    let direct = engine
        .families()
        .read(
            FamilyReadContext::try_new(tenant_scope.clone(), snapshot.clone()).expect("context"),
            request,
        )
        .expect("direct read");
    assert_eq!(streamed, direct);
    assert_eq!(batch.completion(), Some(StreamCompletion::EndOfStream));

    // A second poll of the completed stream is an idempotent terminal.
    let terminal = pull(&store, &tenant_scope, &snapshot, &source, batch.next_cursor().clone(), 4)
        .expect("terminal batch");
    assert!(terminal.items().is_empty());
    assert_eq!(terminal.completion(), Some(StreamCompletion::EndOfStream));

    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Cursors, bounds, memory, backpressure, cancellation
// ---------------------------------------------------------------------------

#[test]
fn stream_cursors_resume_exactly_where_the_previous_batch_ended() {
    let (dir, engine) = open_engine("cursor-resume");
    let tenant_scope = scope(&engine, "t1");
    let fixture = numbered_documents("users", 9, 2);
    commit_documents(&engine, "t1", "op-batch", &as_refs(&fixture));
    let store = engine.streams();

    // Streams are snapshot-bound: a document committed after the snapshot
    // is invisible to the stream, and continuing an old cursor against a
    // new snapshot is a typed cursor error, never a silent mix.
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    commit_document(&engine, "t1", "op-late", "users", "doc-late", "v");
    let source = StreamSource::Documents(selector("users"));
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let first = pull(&store, &tenant_scope, &snapshot, &source, cursor, 3).expect("batch");
    assert_eq!(first.items().len(), 3);
    let next_snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    assert_eq!(
        pull(&store, &tenant_scope, &next_snapshot, &source, first.next_cursor().clone(), 3)
            .unwrap_err(),
        StreamError::InvalidCursor
    );

    // Page the bound snapshot to the end: exact resume, no loss, no skip.
    let mut cursor = first.next_cursor().clone();
    let mut seen: Vec<String> = Vec::new();
    loop {
        let batch = pull(&store, &tenant_scope, &snapshot, &source, cursor, 3).expect("batch");
        for item in batch.items() {
            match item {
                StreamItem::Document(document) => seen.push(document.id().as_str().to_owned()),
                other => panic!("foreign item: {other:?}"),
            }
        }
        let complete = batch.completion();
        cursor = batch.next_cursor().clone();
        if complete.is_some() {
            break;
        }
    }
    assert_eq!(
        seen,
        ["doc-03", "doc-04", "doc-05", "doc-06", "doc-07", "doc-08"],
        "resume is exact and the late document stays invisible"
    );

    // The public observation of a cursor names the source kind and the
    // bound storage sequence, never an internal offset.
    let position = store.position(&cursor);
    assert_eq!(position.kind(), streaming::StreamSourceKind::Documents);
    assert_eq!(position.sequence(), snapshot.sequence());

    // A terminal cursor is idempotent.
    let repoll = pull(&store, &tenant_scope, &snapshot, &source, cursor, 3).expect("repoll");
    assert!(repoll.items().is_empty());
    assert_eq!(repoll.completion(), Some(StreamCompletion::EndOfStream));

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn stream_batches_honor_the_requested_bound_under_a_slow_consumer() {
    let (dir, engine) = open_engine("slow-consumer");
    let tenant_scope = scope(&engine, "t1");
    let fixture = numbered_documents("users", 20, 2);
    commit_documents(&engine, "t1", "op-batch", &as_refs(&fixture));
    let store = engine.streams();
    for limit in [1_u32, 3, 7] {
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let source = StreamSource::Documents(selector("users"));
        let mut cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
        let mut seen: Vec<String> = Vec::new();
        loop {
            // The consumer is deliberately slow: it sleeps between pulls.
            std::thread::sleep(Duration::from_millis(1));
            let batch =
                pull(&store, &tenant_scope, &snapshot, &source, cursor, limit).expect("batch");
            assert!(
                batch.items().len() <= limit as usize,
                "batch of {} exceeded the requested bound {limit}",
                batch.items().len()
            );
            for item in batch.items() {
                match item {
                    StreamItem::Document(document) => {
                        seen.push(document.id().as_str().to_owned());
                    }
                    other => panic!("foreign item: {other:?}"),
                }
            }
            let complete = batch.completion();
            cursor = batch.next_cursor().clone();
            if complete.is_some() {
                break;
            }
        }
        assert_eq!(seen.len(), 20, "every document arrives exactly once at bound {limit}");
        let mut sorted = seen.clone();
        sorted.sort();
        assert_eq!(seen, sorted, "canonical order holds at bound {limit}");
    }

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn stream_memory_stays_bounded_when_the_consumer_never_drains() {
    let (dir, engine) = open_engine("bounded-memory");
    let tenant_scope = scope(&engine, "t1");
    let total = 120_usize;
    for group in 0..12 {
        let fixture: Vec<(String, String, String)> = (0..10)
            .map(|index| {
                ("users".to_owned(), format!("doc-{:03}", group * 10 + index), "v".to_owned())
            })
            .collect();
        commit_documents(&engine, "t1", &format!("op-{group}"), &as_refs(&fixture));
    }
    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let source = StreamSource::Documents(selector("users"));

    // The consumer takes one bounded batch and never drains again. The
    // observable memory bound is the batch: at most `limit` items, served
    // by the W4-CA-7 windowed read that never materializes the tenant.
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let only = pull(&store, &tenant_scope, &snapshot, &source, cursor, 16).expect("batch");
    assert_eq!(only.items().len(), 16);
    assert_eq!(only.completion(), None);
    drop(only);

    // A fresh pass pages the whole tenant in bounded batches.
    let ids = drain_document_ids(&engine, &tenant_scope, selector("users"), 16);
    assert_eq!(ids.len(), total);

    // Nothing leaked: the engine still commits and reads.
    commit_document(&engine, "t1", "op-after", "users", "doc-after", "v");
    let ids = drain_document_ids(&engine, &tenant_scope, selector("users"), 16);
    assert_eq!(ids.len(), total + 1);

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn stream_backpressure_never_blocks_an_engine_commit() {
    let (dir, engine) = open_engine("backpressure");
    let tenant_scope = scope(&engine, "t1");
    let store = engine.streams();
    let spec = subscription("lagging", 4);
    assert_eq!(
        store.subscribe(&tenant_scope, &spec).expect("subscribes"),
        SubscriptionStatus::Active
    );

    // The consumer never drains while production runs past the
    // notification bound. Every commit must still complete: the sink is
    // non-blocking, and a slow subscriber can slow itself, never the
    // writer.
    let over = streaming::subscription::MAX_SUBSCRIPTION_NOTIFICATIONS + 8;
    for index in 0..over {
        let next = commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index:04}"),
            "v",
        );
        assert!(next.get() > 0, "commit {index} published");
    }

    // The overflow is observable, and delivery is unaffected: batches are
    // resolved from the journal, so nothing was lost.
    let batch = store
        .next_subscription_batch(&tenant_scope, "lagging", &Cancellation::new())
        .expect("batch serves");
    assert_eq!(batch.events().len(), 4, "bounded by the spec");
    assert!(
        batch.dropped_notifications() > 0,
        "the never-draining consumer's overflow is observable"
    );
    assert_eq!(
        store.subscription_status(&tenant_scope, "lagging").expect("status"),
        SubscriptionStatus::AwaitingAcknowledgement
    );
    assert_eq!(
        store
            .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
            .expect("acknowledges"),
        SubscriptionStatus::Active
    );
    store.close_subscription(&tenant_scope, "lagging").expect("closes");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn stream_cancellation_closes_promptly_without_leaking_a_lock_or_buffer() {
    let (dir, engine) = open_engine("cancellation");
    let tenant_scope = scope(&engine, "t1");
    let fixture = numbered_documents("users", 4, 0);
    commit_documents(&engine, "t1", "op-batch", &as_refs(&fixture));
    let store = engine.streams();

    // A cancelled stream reports cancellation before doing any work.
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let source = StreamSource::Documents(selector("users"));
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let signal = Cancellation::new();
    let first =
        store.next_batch(&tenant_scope, &snapshot, &source, cursor, 2, &signal).expect("batch");
    assert_eq!(first.items().len(), 2);
    signal.cancel();
    assert_eq!(
        store
            .next_batch(&tenant_scope, &snapshot, &source, first.next_cursor().clone(), 2, &signal)
            .unwrap_err(),
        StreamError::Cancelled
    );

    // A cancelled subscription releases its core and buffers.
    let spec = subscription("cancellable", 8);
    store.subscribe(&tenant_scope, &spec).expect("subscribes");
    commit_document(&engine, "t1", "op-cancel", "users", "doc-cancel", "v");
    let cancel_signal = Cancellation::new();
    let batch = store
        .next_subscription_batch(&tenant_scope, "cancellable", &cancel_signal)
        .expect("batch serves");
    assert!(!batch.events().is_empty());
    cancel_signal.cancel();
    assert_eq!(
        store.next_subscription_batch(&tenant_scope, "cancellable", &cancel_signal).unwrap_err(),
        StreamError::Cancelled
    );
    assert!(
        store.subscription_status(&tenant_scope, "cancellable").is_err(),
        "the cancelled subscription's state is released"
    );
    store.subscribe(&tenant_scope, &spec).expect("resubscribes after release");

    // No lock or buffer leaked: the engine still commits and reads.
    commit_document(&engine, "t1", "op-after", "users", "doc-after", "v");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let source = StreamSource::Documents(selector("users"));
    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    let batch = pull(&store, &tenant_scope, &snapshot, &source, cursor, 2).expect("batch");
    assert!(!batch.items().is_empty());

    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Changes and subscriptions
// ---------------------------------------------------------------------------

#[test]
fn changes_subscription_observes_only_committed_publication_events() {
    let (dir, engine) = open_engine("changes-committed");
    let tenant_scope = scope(&engine, "t1");
    let store = engine.streams();
    let spec = subscription("changes", 8);
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    // Nothing has committed since the high-water mark: the first batch is
    // empty and its boundary does not move.
    let empty = store
        .next_subscription_batch(&tenant_scope, "changes", &Cancellation::new())
        .expect("empty batch");
    assert!(empty.events().is_empty());
    assert_eq!(empty.filtered_dropped(), 0);
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(empty.checkpoint()))
        .expect("acknowledges");

    // A published commit becomes observable with its exact identity.
    let sequence = commit_document(&engine, "t1", "op-published", "users", "doc-1", "v");
    let batch = store
        .next_subscription_batch(&tenant_scope, "changes", &Cancellation::new())
        .expect("batch");
    assert_eq!(batch.events().len(), 1);
    let event = &batch.events()[0];
    assert_eq!(event.operation_id().as_str(), "op-published");
    assert_eq!(event.sequence(), sequence);
    assert_eq!(event.kind(), ChangeKind::Document);
    assert_eq!(event.collection().expect("key event collection").as_str(), "users");
    assert_eq!(event.document().expect("key event document").as_str(), "doc-1");
    assert_eq!(event.scope().tenant().as_str(), "t1");

    // Another tenant's commits stay invisible to this subscription.
    commit_document(&engine, "t2", "op-other-tenant", "users", "doc-t2", "v");
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
        .expect("acknowledges");
    let isolated = store
        .next_subscription_batch(&tenant_scope, "changes", &Cancellation::new())
        .expect("batch");
    assert!(isolated.events().is_empty(), "no cross-tenant events");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn changes_events_carry_operation_identity_and_storage_sequence_in_order() {
    let (dir, engine) = open_engine("changes-order");
    let tenant_scope = scope(&engine, "t1");
    let mut receipts = Vec::new();
    for index in 0..5 {
        let sequence = commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index}"),
            "v",
        );
        receipts.push((format!("op-{index}"), sequence.get()));
    }
    let store = engine.streams();
    let spec = subscription("ordered", 2).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    let mut observed = Vec::new();
    for _ in 0..4 {
        let batch = store
            .next_subscription_batch(&tenant_scope, "ordered", &Cancellation::new())
            .expect("batch");
        observed.extend(event_identities(batch.events()));
        store
            .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
            .expect("acknowledges");
    }
    assert_eq!(observed, receipts, "events arrive strictly by storage sequence");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn changes_never_expose_an_uncommitted_or_rejected_transaction() {
    let (dir, engine) = open_engine("changes-uncommitted");
    let tenant_scope = scope(&engine, "t1");
    let store = engine.streams();
    let spec = subscription("integrity", 8);
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    // A live transaction's staged writes are never observable.
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut staged = engine.begin(&tenant_scope, snapshot, operation("op-staged")).expect("begins");
    staged.put(text_document("users", "doc-staged", "v"), ExpectedVersion::Any).expect("stages");
    let batch = store
        .next_subscription_batch(&tenant_scope, "integrity", &Cancellation::new())
        .expect("batch");
    assert!(batch.events().is_empty(), "a live transaction is invisible");
    drop(staged);
    let batch = store
        .next_subscription_batch(&tenant_scope, "integrity", &Cancellation::new())
        .expect("batch");
    assert!(batch.events().is_empty(), "a dropped transaction is invisible");
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
        .expect("acknowledges");

    // A rejected transaction publishes nothing.
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut rejected =
        engine.begin(&tenant_scope, snapshot, operation("op-rejected")).expect("begins");
    rejected
        .put(text_document("users", "doc-rejected", "v"), ExpectedVersion::Exact(999))
        .expect("stages");
    assert!(matches!(rejected.commit(LocalDurability::Synced), CommitOutcome::Rejected(_)));
    let batch = store
        .next_subscription_batch(&tenant_scope, "integrity", &Cancellation::new())
        .expect("batch");
    assert!(batch.events().is_empty(), "a rejected transaction is invisible");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn subscription_batches_are_bounded_and_acknowledged_at_the_checkpoint_seam() {
    let (dir, engine) = open_engine("subscription-batches");
    let tenant_scope = scope(&engine, "t1");
    for index in 0..7 {
        commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index}"),
            "v",
        );
    }
    let store = engine.streams();
    let spec = subscription("bounded", 3).from_beginning();
    assert_eq!(
        store.subscribe(&tenant_scope, &spec).expect("subscribes"),
        SubscriptionStatus::Active
    );

    let mut delivered = 0_usize;
    let mut batches = 0_usize;
    loop {
        let batch = store
            .next_subscription_batch(&tenant_scope, "bounded", &Cancellation::new())
            .expect("batch");
        assert!(
            batch.events().len() <= 3,
            "batch of {} exceeded the spec bound",
            batch.events().len()
        );
        assert_eq!(
            store.subscription_status(&tenant_scope, "bounded").expect("status"),
            SubscriptionStatus::AwaitingAcknowledgement
        );
        delivered += batch.events().len();
        batches += 1;
        let status = store
            .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
            .expect("acknowledges");
        assert_eq!(status, SubscriptionStatus::Active);
        if batch.events().is_empty() {
            break;
        }
    }
    assert_eq!(delivered, 7);
    assert_eq!(batches, 4, "three full batches and one terminal empty batch");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn subscription_checkpoint_does_not_advance_before_acknowledgement() {
    let (dir, engine) = open_engine("checkpoint-advance");
    let tenant_scope = scope(&engine, "t1");
    for index in 0..6 {
        commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index}"),
            "v",
        );
    }
    let store = engine.streams();
    let spec = subscription("guarded", 3).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    let first = store
        .next_subscription_batch(&tenant_scope, "guarded", &Cancellation::new())
        .expect("batch");
    // Without an acknowledgement, the same boundary is offered again.
    let repeat = store
        .next_subscription_batch(&tenant_scope, "guarded", &Cancellation::new())
        .expect("redelivery");
    assert_eq!(repeat, first, "unacknowledged batches are redelivered verbatim");

    let stale_checkpoint = first.checkpoint();
    store
        .acknowledge(
            &tenant_scope,
            SubscriptionAcknowledgement::confirming(stale_checkpoint.clone()),
        )
        .expect("acknowledges the first boundary");
    let second = store
        .next_subscription_batch(&tenant_scope, "guarded", &Cancellation::new())
        .expect("batch");
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(second.checkpoint()))
        .expect("acknowledges the second boundary");

    // The stale acknowledgement is refused: it must not roll the durable
    // checkpoint backwards or silently advance it.
    assert_eq!(
        store
            .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(stale_checkpoint))
            .unwrap_err(),
        StreamError::InvalidCursor
    );

    // Delivery continued from the acknowledged checkpoint, not past it.
    let third = store
        .next_subscription_batch(&tenant_scope, "guarded", &Cancellation::new())
        .expect("batch");
    let identities: Vec<(String, u64)> = [first.clone(), second, third.clone()]
        .iter()
        .flat_map(|batch| event_identities(batch.events()))
        .collect();
    let unique: BTreeSet<_> = identities.iter().cloned().collect();
    assert_eq!(unique.len(), identities.len(), "no event is skipped or double-counted");
    assert!(third.events().is_empty());

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn unacknowledged_subscription_items_are_resent_after_reconnect() {
    let (dir, engine) = open_engine("resend-reconnect");
    let tenant_scope = scope(&engine, "t1");
    for index in 0..4 {
        commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index}"),
            "v",
        );
    }
    let store = engine.streams();
    let spec = subscription("resend", 3).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");
    let delivered = store
        .next_subscription_batch(&tenant_scope, "resend", &Cancellation::new())
        .expect("batch");
    assert_eq!(delivered.events().len(), 3);

    // The connection drops before the acknowledgement.
    assert_eq!(
        store.disconnect_subscription(&tenant_scope, "resend").expect("disconnects"),
        SubscriptionStatus::Reconnecting
    );
    // Reconnect reattaches and resends the unacknowledged items.
    assert_eq!(
        store.subscribe(&tenant_scope, &spec).expect("reattaches"),
        SubscriptionStatus::AwaitingAcknowledgement
    );
    let resent = store
        .next_subscription_batch(&tenant_scope, "resend", &Cancellation::new())
        .expect("resent batch");
    assert_eq!(resent, delivered, "unacknowledged items are resent");
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(resent.checkpoint()))
        .expect("acknowledges");

    // The tail continues after the acknowledged boundary.
    let tail = store
        .next_subscription_batch(&tenant_scope, "resend", &Cancellation::new())
        .expect("batch");
    assert_eq!(tail.events().len(), 1);

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn duplicate_subscription_delivery_is_identifiable_and_at_least_once() {
    let (dir, engine) = open_engine("duplicates");
    let tenant_scope = scope(&engine, "t1");
    for index in 0..3 {
        commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index}"),
            "v",
        );
    }
    let store = engine.streams();
    let spec = subscription("duplicates", 3).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    let first = store
        .next_subscription_batch(&tenant_scope, "duplicates", &Cancellation::new())
        .expect("batch");
    let duplicate = store
        .next_subscription_batch(&tenant_scope, "duplicates", &Cancellation::new())
        .expect("duplicate delivery");
    assert_eq!(first, duplicate, "redelivery is at-least-once");

    // Every item carries a stable identity, so the consumer can identify
    // the duplicate: identical operation identity and storage sequence.
    let identities = event_identities(first.events());
    let duplicate_identities = event_identities(duplicate.events());
    assert_eq!(identities, duplicate_identities);
    assert!(identities.iter().all(|(identity, sequence)| !identity.is_empty() && *sequence > 0));

    // After acknowledgement, the next batch carries new identities only.
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(first.checkpoint()))
        .expect("acknowledges");
    commit_document(&engine, "t1", "op-new", "users", "doc-new", "v");
    let next = store
        .next_subscription_batch(&tenant_scope, "duplicates", &Cancellation::new())
        .expect("batch");
    let next_identities = event_identities(next.events());
    assert!(!next_identities.is_empty());
    assert!(
        next_identities.iter().all(|identity| !identities.contains(identity)),
        "acknowledged identities are not redelivered"
    );

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn subscription_reconnect_preserves_the_durable_checkpoint() {
    let (dir, engine) = open_engine("reconnect-checkpoint");
    let tenant_scope = scope(&engine, "t1");
    for index in 0..6 {
        commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index}"),
            "v",
        );
    }
    let store = engine.streams();
    let spec = subscription("durable", 2).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    let mut acknowledged: Vec<(String, u64)> = Vec::new();
    for _ in 0..3 {
        let batch = store
            .next_subscription_batch(&tenant_scope, "durable", &Cancellation::new())
            .expect("batch");
        acknowledged.extend(event_identities(batch.events()));
        store
            .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
            .expect("acknowledges");
    }
    assert_eq!(acknowledged.len(), 6);

    // The consumer goes away and comes back: the preserved checkpoint
    // resumes delivery exactly after the acknowledged boundary.
    store.disconnect_subscription(&tenant_scope, "durable").expect("disconnects");
    assert_eq!(
        store.subscribe(&tenant_scope, &spec).expect("reattaches"),
        SubscriptionStatus::Active
    );
    commit_document(&engine, "t1", "op-after", "users", "doc-after", "v");
    let resumed = store
        .next_subscription_batch(&tenant_scope, "durable", &Cancellation::new())
        .expect("batch");
    let resumed_identities = event_identities(resumed.events());
    assert_eq!(resumed_identities.len(), 1);
    assert!(
        resumed_identities.iter().all(|identity| !acknowledged.contains(identity)),
        "reconnect neither redelivers acknowledged items nor skips ahead"
    );

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn subscription_close_reports_an_explicit_terminal_status() {
    let (dir, engine) = open_engine("close-status");
    let tenant_scope = scope(&engine, "t1");
    commit_document(&engine, "t1", "op-1", "users", "doc-1", "v");
    let store = engine.streams();
    let spec = subscription("closing", 4);
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    let status = store.close_subscription(&tenant_scope, "closing").expect("closes");
    assert_eq!(
        status,
        SubscriptionStatus::Closed(StreamCompletion::Cancelled),
        "the close reason is explicit"
    );
    assert!(store.subscription_status(&tenant_scope, "closing").is_err());
    assert!(store.next_subscription_batch(&tenant_scope, "closing", &Cancellation::new()).is_err());

    // A fresh subscription under the same name starts from its own
    // high-water mark: the closed incarnation's checkpoint is gone.
    store.subscribe(&tenant_scope, &spec).expect("resubscribes");
    let fresh = store
        .next_subscription_batch(&tenant_scope, "closing", &Cancellation::new())
        .expect("batch");
    assert!(fresh.events().is_empty(), "the fresh subscription starts from now");

    // An already open subscription refuses a second consumer.
    let second = subscription("closing", 4);
    assert_eq!(store.subscribe(&tenant_scope, &second).unwrap_err(), StreamError::InvalidSource);

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn stream_sources_reject_a_snapshot_from_another_engine_or_tenant() {
    let (dir, engine) = open_engine("identity-a");
    let (dir_b, engine_b) = open_engine("identity-b");
    let tenant_scope = scope(&engine, "t1");
    let scope_other_tenant = scope(&engine, "t2");
    let scope_b = scope(&engine_b, "t1");
    commit_document(&engine, "t1", "op-1", "users", "doc-1", "v");
    commit_document(&engine_b, "t1", "op-b", "users", "doc-b", "v");

    let store = engine.streams();
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let snapshot_other_tenant = engine.read_snapshot(&scope_other_tenant).expect("snapshot");
    let snapshot_b = engine_b.read_snapshot(&scope_b).expect("snapshot");
    let source = StreamSource::Documents(selector("users"));

    // A snapshot from another engine is refused.
    assert_eq!(
        store.open(&tenant_scope, &snapshot_b, source.clone()).unwrap_err(),
        StreamError::SnapshotEngineMismatch
    );
    // A scope from another engine is refused.
    assert_eq!(
        store.open(&scope_b, &snapshot, source.clone()).unwrap_err(),
        StreamError::SnapshotEngineMismatch
    );
    // A snapshot of another tenant under this scope is refused.
    assert_eq!(
        store.open(&tenant_scope, &snapshot_other_tenant, source.clone()).unwrap_err(),
        StreamError::TenantMismatch
    );

    let cursor = store.open(&tenant_scope, &snapshot, source.clone()).expect("opens");
    assert_eq!(
        pull(&store, &tenant_scope, &snapshot_b, &source, cursor, 4).unwrap_err(),
        StreamError::SnapshotEngineMismatch
    );

    engine_b.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir_b).ok();
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn stream_filters_report_drops_without_advancing_the_checkpoint() {
    let (dir, engine) = open_engine("filter-drops");
    let tenant_scope = scope(&engine, "t1");
    // Interleave the collections so filtering passes over real events:
    // the publications run u-0, o-0, u-1, o-1, u-2, o-2.
    for index in 0..3 {
        commit_document(
            &engine,
            "t1",
            &format!("u-{index}"),
            "users",
            &format!("user-{index}"),
            "v",
        );
        commit_document(
            &engine,
            "t1",
            &format!("o-{index}"),
            "orders",
            &format!("order-{index}"),
            "v",
        );
    }
    let store = engine.streams();
    let filter = ChangeFilter::try_new(None, Some(vec![collection("users")])).expect("filter");
    let spec = SubscriptionSpec::try_new("filtered", filter, 10).expect("spec").from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    let first = store
        .next_subscription_batch(&tenant_scope, "filtered", &Cancellation::new())
        .expect("batch");
    assert!(
        first.events().iter().all(|event| {
            event.collection().is_some_and(|collection| collection.as_str() == "users")
        }),
        "only accepted collections are delivered"
    );
    assert_eq!(first.events().len(), 3);
    // The three `orders` publications the scan examined were dropped and
    // reported; the batch's recorded boundary covers exactly the events it
    // delivered or reported, so the advance is reported, never silent.
    assert_eq!(first.filtered_dropped(), 3, "dropped events are reported");
    let acknowledged_checkpoint = first.checkpoint();
    store
        .acknowledge(
            &tenant_scope,
            SubscriptionAcknowledgement::confirming(acknowledged_checkpoint.clone()),
        )
        .expect("acknowledges");

    // A batch whose events are all filtered reports the drops as progress
    // facts, and its recorded boundary covers exactly those reported drops.
    for index in 3..5 {
        commit_document(
            &engine,
            "t1",
            &format!("o-{index}"),
            "orders",
            &format!("order-{index}"),
            "v",
        );
    }
    let dropped = store
        .next_subscription_batch(&tenant_scope, "filtered", &Cancellation::new())
        .expect("batch");
    assert!(dropped.events().is_empty());
    assert_eq!(dropped.filtered_dropped(), 2, "the drops are progress facts");
    assert_ne!(
        dropped.checkpoint(),
        acknowledged_checkpoint,
        "the recorded boundary covers the reported drops"
    );
    // The durable checkpoint still advances only at the acknowledgement
    // seam: without the acknowledgement, the recorded batch is redelivered
    // verbatim (at-least-once), never skipped past.
    let redelivered = store
        .next_subscription_batch(&tenant_scope, "filtered", &Cancellation::new())
        .expect("redelivery");
    assert_eq!(redelivered, dropped, "an unacknowledged batch is not skipped past");
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(dropped.checkpoint()))
        .expect("acknowledges");
    // After the acknowledgement, the next poll starts past the reported
    // drops: they are not re-examined and no event was lost.
    let next = store
        .next_subscription_batch(&tenant_scope, "filtered", &Cancellation::new())
        .expect("batch");
    assert!(next.events().is_empty());
    assert_eq!(next.filtered_dropped(), 0, "acknowledged drops are not re-examined");

    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Repair-loop proofs (a2)
// ---------------------------------------------------------------------------

#[test]
fn publication_sink_never_blocks_a_commit_behind_a_wedged_poll() {
    let (dir, engine) = open_engine("sink-nonblocking");
    let tenant_scope = scope(&engine, "t1");
    commit_document(&engine, "t1", "op-seed", "users", "doc-seed", "v");
    let store = engine.streams();
    let spec = subscription("wedged-poll", 8).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    // Wedge one consumer poll while it holds the subscription lock - the
    // exact contention the committing thread's sink must survive without
    // waiting - and commit from another thread while it is held. The wedge
    // session serializes this test against any other wedge test, because
    // the wedge is process-global and tests run in parallel.
    let _wedge_session = streaming::subscription::begin_wedge_session();
    streaming::subscription::arm_poll_wedge("wedged-poll");
    let (committed, commit_elapsed, commit_returned_with_wedge_held, wedged_batch) =
        std::thread::scope(|scope_thread| {
            let consumer = scope_thread.spawn(|| {
                store
                    .next_subscription_batch(&tenant_scope, "wedged-poll", &Cancellation::new())
                    .expect("the wedged poll completes after release")
            });
            // Wait for the poll to actually enter the wedge (bounded).
            let entered_by = Instant::now() + Duration::from_secs(5);
            while !streaming::subscription::wedge_entered() {
                assert!(Instant::now() < entered_by, "the consumer poll never wedged");
                std::thread::sleep(Duration::from_millis(2));
            }
            // The measured commit runs while the wedge is held: its emit must
            // not wait on the subscriber's lock.
            let started = Instant::now();
            let committed =
                commit_document(&engine, "t1", "op-during-wedge", "users", "doc-wedge", "v");
            let commit_elapsed = started.elapsed();
            // Sample the wedge BEFORE releasing it: the causal observation this
            // test exists to make. A non-blocking sink returns while the wedge
            // is still held; a blocking sink cannot return before the wedge's
            // own five-second deadline fires (the committing thread would be
            // waiting on the very lock the wedge holds, and the wedge is only
            // released below). Made deterministic-by-construction 2026-08-27
            // (ticket #101): the previous absolute wall-clock bound (<1s) was
            // load-sensitive — a synced commit's fsync legitimately took 4.1s
            // under full-suite parallel load and failed the bound without any
            // sink blocking. The wedge-held sample plus the 5s deadline bound
            // discriminate the same property independent of machine load
            // (only a commit slower than the wedge's own deadline could still
            // false-fail, which is a broken machine, not a busy one).
            let commit_returned_with_wedge_held = streaming::subscription::wedge_entered();
            streaming::subscription::release_poll_wedge();
            let batch = consumer.join().expect("the consumer thread does not panic");
            (committed, commit_elapsed, commit_returned_with_wedge_held, batch)
        });

    // The commit returned while the poll was still wedged: the sink never
    // waited on the subscriber's lock. The causal sample was taken before
    // release; the wall-clock leg only guards the degenerate case where
    // the wedge's own deadline fired first (a blocking sink waits out that
    // full deadline, so it cannot return under it).
    assert!(committed.get() > 0, "the commit published");
    assert!(
        commit_returned_with_wedge_held,
        "the commit did not return while the wedge was held (elapsed {commit_elapsed:?}): \
        the sink waited on the wedged subscriber lock or the wedge deadline fired"
    );
    assert!(
        commit_elapsed < Duration::from_secs(5),
        "the commit took {commit_elapsed:?}, at or past the wedge's own deadline — \
        inconclusive run, treat as a broken machine rather than a sink regression"
    );
    assert!(
        wedged_batch.events().iter().any(|event| event.operation_id().as_str() == "op-seed"),
        "the wedged poll delivered its own batch after release"
    );

    // The notification the sink dropped on contention is counted, and the
    // delivery itself was never lost: the next poll serves the committed
    // event from the journal.
    let batch = store
        .next_subscription_batch(&tenant_scope, "wedged-poll", &Cancellation::new())
        .expect("batch serves");
    assert!(batch.dropped_notifications() > 0, "the contention drop is counted and observable");
    assert!(
        batch.events().iter().any(|event| event.operation_id().as_str() == "op-during-wedge"),
        "delivery is journal-driven, so the dropped notification cost latency only"
    );

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn subscription_reaches_events_beyond_a_long_filtered_run() {
    let (dir, engine) = open_engine("filtered-run");
    let tenant_scope = scope(&engine, "t1");
    // A filtered run longer than the fetch window (max_events + 1 = 4
    // commits per page) precedes the accepted event.
    for index in 0..5 {
        commit_document(
            &engine,
            "t1",
            &format!("audit-{index}"),
            "audit",
            &format!("entry-{index}"),
            "v",
        );
    }
    commit_document(&engine, "t1", "op-users", "users", "user-0", "v");
    let store = engine.streams();
    let filter = ChangeFilter::try_new(None, Some(vec![collection("users")])).expect("filter");
    let spec = SubscriptionSpec::try_new("tail", filter, 3).expect("spec").from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    let batch =
        store.next_subscription_batch(&tenant_scope, "tail", &Cancellation::new()).expect("batch");
    assert_eq!(
        batch.events().len(),
        1,
        "the accepted event beyond the filtered run is delivered in one poll"
    );
    assert_eq!(batch.events()[0].collection().expect("key event").as_str(), "users");
    assert_eq!(batch.events()[0].operation_id().as_str(), "op-users");
    assert_eq!(batch.filtered_dropped(), 5, "the filtered run is reported as drops");
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
        .expect("acknowledges");

    // The boundary covers the reported drops, so a later filtered run
    // continues from there instead of re-scanning the acknowledged prefix.
    for index in 5..7 {
        commit_document(
            &engine,
            "t1",
            &format!("audit-{index}"),
            "audit",
            &format!("entry-{index}"),
            "v",
        );
    }
    let tail =
        store.next_subscription_batch(&tenant_scope, "tail", &Cancellation::new()).expect("batch");
    assert!(tail.events().is_empty());
    assert_eq!(tail.filtered_dropped(), 2, "only the new filtered run is examined");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn engine_bound_subscriptions_are_bounded_and_refused() {
    let (dir, engine) = open_engine("subscription-bound");
    let tenant_scope = scope(&engine, "t1");
    let store = engine.streams();
    for index in 0..streaming::subscription::MAX_SUBSCRIPTIONS_PER_ENGINE {
        let spec = subscription(&format!("sub-{index}"), 4);
        store.subscribe(&tenant_scope, &spec).expect("subscribes within the bound");
    }
    let beyond = subscription("sub-beyond", 4);
    assert_eq!(
        store.subscribe(&tenant_scope, &beyond).unwrap_err(),
        StreamError::InvalidSource,
        "the per-commit ingress cost is bounded by refusing beyond the bound"
    );
    // Closing one subscription frees its slot.
    store.close_subscription(&tenant_scope, "sub-0").expect("closes");
    let refill = subscription("sub-refill", 4);
    assert_eq!(
        store.subscribe(&tenant_scope, &refill).expect("resubscribes into the freed slot"),
        SubscriptionStatus::Active
    );

    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// SE-03: the public change-event backbone (selectors, replay, conflicts)
// ---------------------------------------------------------------------------

/// Builds one synthetic conflict record: two conflicting operations, two
/// members, two clock stamps, and a recorded winner.
fn conflict_record(tag: &str) -> crate::streaming::ConflictRecord {
    crate::streaming::ConflictRecord::new(
        tenant("tenant-a"),
        vec![operation(&format!("op-local-{tag}")), operation(&format!("op-remote-{tag}"))],
        vec![
            crate::types::MemberId::try_new("member-a").expect("valid member"),
            crate::types::MemberId::try_new("member-b").expect("valid member"),
        ],
        vec![
            crate::types::ReplicationClockStamp::new(10, 1),
            crate::types::ReplicationClockStamp::new(10, 2),
        ],
        crate::streaming::ConflictOutcome::Winner(
            crate::types::MemberId::try_new("member-a").expect("valid member"),
        ),
    )
}

/// Replays every journal event a selector accepts from one checkpoint,
/// paging at `limit`, and returns the delivered events in sequence order.
fn replay_all(
    engine: &Engine,
    tenant_scope: &TenantScope,
    filter: &ChangeFilter,
    after: Option<streaming::ChangeCheckpoint>,
    limit: u32,
) -> Vec<ChangeEvent> {
    let store = engine.streams();
    let mut events = Vec::new();
    let mut after = after;
    loop {
        let request = streaming::ChangeReplayRequest::try_new(filter.clone(), after, limit)
            .expect("valid replay request");
        let batch =
            store.replay_changes(tenant_scope, &request, &Cancellation::new()).expect("replay");
        assert!(batch.events().len() <= limit as usize, "replay batches stay bounded");
        events.extend(batch.events().iter().cloned());
        let drained = batch.drained();
        after = batch.next_checkpoint();
        if drained {
            assert!(after.is_some() || events.is_empty(), "a drained batch names its boundary");
            break;
        }
    }
    events
}

/// Drains one live subscription from its opening to the journal's end,
/// acknowledging each batch, and returns every delivered event.
fn drain_live(store: &StreamStore<'_>, tenant_scope: &TenantScope, name: &str) -> Vec<ChangeEvent> {
    let mut events = Vec::new();
    loop {
        let batch =
            store.next_subscription_batch(tenant_scope, name, &Cancellation::new()).expect("batch");
        events.extend(batch.events().iter().cloned());
        store
            .acknowledge(tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
            .expect("acknowledges");
        if batch.events().is_empty() {
            break;
        }
    }
    events
}

#[test]
fn change_selector_extends_the_filter_to_the_v1_parity_surface() {
    let every = ChangeFilter::try_new(None, None).expect("filter");
    let document = text_document("users", "user-1", "v");
    let document_event = ChangeEvent::new(
        ChangeKind::Document,
        TenantScope::from_parts(tenant("t1"), 7),
        collection("users"),
        document_id("user-1"),
        operation("op-1"),
        StorageSequence::new(11),
    );
    let conflict_event = ChangeEvent::conflict(
        TenantScope::from_parts(tenant("t1"), 7),
        operation("op-local-x"),
        StorageSequence::new(12),
        conflict_record("x"),
    );

    // All-documents: every document kind, no refinement.
    let all_documents =
        ChangeFilter::try_new(Some(vec![ChangeKind::Document]), None).expect("filter");
    assert!(all_documents.accepts_event(&document_event));
    assert!(!all_documents.accepts_event(&conflict_event));

    // Per-document id.
    let per_document = every.clone().with_document(document_id("user-1"));
    assert!(per_document.accepts_event(&document_event));
    assert!(
        !per_document.clone().with_document(document_id("user-2")).accepts_event(&document_event),
        "another document identity is not selected"
    );

    // Identifier prefix, validated like every selector text.
    let prefix = every.clone().with_prefix("user-").expect("prefix");
    assert!(prefix.accepts_event(&document_event));
    assert!(!prefix.accepts_event(&ChangeEvent::new(
        ChangeKind::Document,
        TenantScope::from_parts(tenant("t1"), 7),
        collection("users"),
        document_id("order-1"),
        operation("op-2"),
        StorageSequence::new(11),
    )));
    assert_eq!(
        every.clone().with_prefix("").unwrap_err(),
        crate::operations::RequestValidationError::InvalidIdentifier
    );

    // Collection selection keeps its existing dimension.
    let by_collection =
        ChangeFilter::try_new(None, Some(vec![collection("users")])).expect("filter");
    assert!(by_collection.accepts_event(&document_event));
    assert!(!by_collection.accepts_event(&ChangeEvent::new(
        ChangeKind::Document,
        TenantScope::from_parts(tenant("t1"), 7),
        collection("orders"),
        document_id("order-1"),
        operation("op-3"),
        StorageSequence::new(11),
    )));

    // Index, counter, and time-series kinds are selectable dimensions.
    for kind in [ChangeKind::Index, ChangeKind::Counter, ChangeKind::TimeSeries] {
        let selector = ChangeFilter::try_new(Some(vec![kind]), None).expect("filter");
        let event = ChangeEvent::new(
            kind,
            TenantScope::from_parts(tenant("t1"), 7),
            collection("users"),
            document_id("user-1"),
            operation("op-4"),
            StorageSequence::new(11),
        );
        assert!(selector.accepts_event(&event), "{kind:?} is selectable");
        assert!(!all_documents.accepts_event(&event));
    }

    // Operation selection: V1's per-operation observable.
    let by_operation = every
        .clone()
        .for_operations(vec![operation("op-1"), operation("op-other")])
        .expect("operations");
    assert!(by_operation.accepts_event(&document_event));
    assert!(
        !ChangeFilter::try_new(None, None)
            .expect("filter")
            .for_operations(vec![operation("op-other")])
            .expect("operations")
            .accepts_event(&document_event)
    );
    assert_eq!(
        every.clone().for_operations(Vec::new()).unwrap_err(),
        crate::operations::RequestValidationError::EmptyField
    );

    // Conflict is a first-class kind: a conflict selector accepts conflict
    // events and rejects document events, and vice versa.
    let conflicts_only =
        ChangeFilter::try_new(Some(vec![ChangeKind::Conflict]), None).expect("filter");
    assert!(conflicts_only.accepts_event(&conflict_event));
    assert!(!conflicts_only.accepts_event(&document_event));
    assert!(
        !all_documents.accepts_event(&conflict_event),
        "a document selector does not observe conflicts"
    );
    // A collection-scoped filter does not observe conflicts: they carry no
    // collection identity to match.
    assert!(!by_collection.accepts_event(&conflict_event));
    // An unrefined every-kind filter does observe them.
    assert!(every.accepts_event(&conflict_event));
    // A per-document or prefix-refined filter does not: a conflict carries
    // no document identity.
    assert!(!per_document.accepts_event(&conflict_event));
    assert!(!prefix.accepts_event(&conflict_event));
    // The operation dimension matches any conflicting operation identity.
    assert!(
        every
            .clone()
            .for_operations(vec![operation("op-remote-x")])
            .expect("operations")
            .accepts_event(&conflict_event)
    );
    let _ = document;
}

#[test]
fn conflict_events_carry_only_conflict_record_metadata() {
    let record = conflict_record("only");
    let scope = TenantScope::from_parts(tenant("t1"), 7);
    let event = ChangeEvent::conflict(
        scope.clone(),
        operation("op-local-only"),
        StorageSequence::new(30),
        record.clone(),
    );

    // The conflict payload is exactly the ConflictRecord: identities,
    // members, clocks, and the recorded outcome.
    assert_eq!(event.kind(), ChangeKind::Conflict);
    let carried = event.conflict_record().expect("the conflict payload");
    assert_eq!(carried.operations(), record.operations());
    assert_eq!(carried.members(), record.members());
    assert_eq!(carried.clocks(), record.clocks());
    assert_eq!(carried.outcome(), record.outcome());
    assert_eq!(event.conflict_record(), Some(&record));

    // No key dimensions exist on a conflict event: there is no collection,
    // no document identity, and structurally no document body — the
    // constructor accepts no `Document` at all.
    assert_eq!(event.collection(), None);
    assert_eq!(event.document(), None);

    // Journal-derived events never carry a conflict payload.
    let journal_event = ChangeEvent::new(
        ChangeKind::Document,
        scope.clone(),
        collection("users"),
        document_id("user-1"),
        operation("op-journal"),
        StorageSequence::new(31),
    );
    assert_eq!(journal_event.conflict_record(), None);
    assert!(journal_event.collection().is_some());
    assert!(journal_event.document().is_some());
}

#[test]
fn replay_from_beginning_reproduces_the_live_subscription_sequence_exactly() {
    let (dir, engine) = open_engine("replay-live-parity");
    let tenant_scope = scope(&engine, "t1");
    // Interleave single-write and multi-write publications so the expansion
    // and batch-boundary splitting are exercised on both paths.
    let mut expected_identities = Vec::new();
    for index in 0..5 {
        let sequence = commit_documents(
            &engine,
            "t1",
            &format!("op-{index}"),
            &[("users", &format!("user-{index}"), "u"), ("orders", &format!("order-{index}"), "o")],
        );
        expected_identities.push((format!("op-{index}"), sequence.get(), 2));
    }

    let store = engine.streams();
    let spec = subscription("live-parity", 3).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");
    let live = drain_live(&store, &tenant_scope, "live-parity");

    let replay = replay_all(
        &engine,
        &tenant_scope,
        &ChangeFilter::try_new(None, None).expect("filter"),
        None,
        2,
    );
    assert_eq!(live, replay, "journal replay reproduces the live event sequence exactly");
    assert_eq!(live.len(), 10);
    // Each publication expands to two ordered writes, so both sequences
    // carry each commit's operation identity and sequence twice.
    for ((operation_name, sequence, writes), group) in
        expected_identities.iter().zip(live.chunks(2))
    {
        assert_eq!(*writes, 2);
        assert_eq!(group.len(), 2);
        for event in group {
            assert_eq!(event.operation_id().as_str(), *operation_name);
            assert_eq!(event.sequence().get(), *sequence);
        }
    }

    // Replay continues from its own boundary exactly like a live
    // subscription: a later publication is the next replayed event.
    let tail = commit_document(&engine, "t1", "op-tail", "users", "user-tail", "u");
    let resumed = replay_all(
        &engine,
        &tenant_scope,
        &ChangeFilter::try_new(None, None).expect("filter"),
        replay
            .last()
            .map(|event| streaming::ChangeCheckpoint::after(event.sequence().get(), u32::MAX)),
        4,
    );
    assert_eq!(resumed.len(), 1);
    assert_eq!(resumed[0].operation_id().as_str(), "op-tail");
    assert_eq!(resumed[0].sequence(), tail);

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn replay_from_an_arbitrary_checkpoint_reproduces_the_exact_suffix() {
    let (dir, engine) = open_engine("replay-arbitrary");
    let tenant_scope = scope(&engine, "t1");
    let batched = commit_documents(
        &engine,
        "t1",
        "op-batched",
        &[("users", "user-a", "v"), ("users", "user-b", "v"), ("users", "user-c", "v")],
    );
    let later = commit_document(&engine, "t1", "op-later", "users", "user-d", "v");
    let _ = (batched, later);

    let full = replay_all(
        &engine,
        &tenant_scope,
        &ChangeFilter::try_new(None, None).expect("filter"),
        None,
        8,
    );
    assert_eq!(full.len(), 4);

    // A live subscription's acknowledged boundary is a replayable
    // checkpoint: replay from it yields exactly the suffix.
    let store = engine.streams();
    let spec = subscription("boundary", 2).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");
    let first = store
        .next_subscription_batch(&tenant_scope, "boundary", &Cancellation::new())
        .expect("batch");
    assert_eq!(first.events().len(), 2);
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(first.checkpoint()))
        .expect("acknowledges");
    let suffix = replay_all(
        &engine,
        &tenant_scope,
        &ChangeFilter::try_new(None, None).expect("filter"),
        Some(first.checkpoint().journal_position()),
        8,
    );
    assert_eq!(suffix, full[2..], "replay from a live boundary is the exact suffix");

    // A hand-forged checkpoint may split one multi-write commit: resuming
    // after the second write of `op-batched` serves only its tail.
    let split = streaming::ChangeCheckpoint::after(batched.get(), 2);
    let tail = replay_all(
        &engine,
        &tenant_scope,
        &ChangeFilter::try_new(None, None).expect("filter"),
        Some(split),
        8,
    );
    assert_eq!(tail.len(), 2);
    assert_eq!(tail[0].document().expect("key event").as_str(), "user-c");
    assert_eq!(tail[1].operation_id().as_str(), "op-later");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn replay_is_bounded_and_reports_filtered_drops() {
    let (dir, engine) = open_engine("replay-bounded");
    let tenant_scope = scope(&engine, "t1");
    for index in 0..6 {
        commit_document(
            &engine,
            "t1",
            &format!("u-{index}"),
            "users",
            &format!("user-{index}"),
            "v",
        );
        commit_document(
            &engine,
            "t1",
            &format!("o-{index}"),
            "orders",
            &format!("order-{index}"),
            "v",
        );
    }
    let store = engine.streams();

    let mut delivered = 0_usize;
    let mut dropped = 0_u64;
    let mut batches = 0_usize;
    let mut after = None;
    loop {
        let request = streaming::ChangeReplayRequest::try_new(
            ChangeFilter::try_new(None, Some(vec![collection("users")])).expect("filter"),
            after,
            2,
        )
        .expect("request");
        let batch =
            store.replay_changes(&tenant_scope, &request, &Cancellation::new()).expect("replay");
        assert!(batch.events().len() <= 2, "the replay bound holds");
        assert!(
            batch.events().iter().all(|event| {
                event.collection().is_some_and(|collection| collection.as_str() == "users")
            }),
            "only selected collections are delivered"
        );
        delivered += batch.events().len();
        dropped += batch.filtered_dropped();
        batches += 1;
        let drained = batch.drained();
        after = batch.next_checkpoint();
        if drained {
            break;
        }
    }
    assert_eq!(delivered, 6, "every selected event is delivered");
    assert_eq!(dropped, 6, "every filtered event is reported as a drop");
    // Reported drops advance the boundary, so the run completes in two
    // full batches and one final batch that reaches the journal's end.
    assert_eq!(batches, 3, "two full batches and one final draining batch");

    // A replay request validates its bound like every batch contract.
    assert_eq!(
        streaming::ChangeReplayRequest::try_new(
            ChangeFilter::try_new(None, None).expect("filter"),
            None,
            0
        )
        .unwrap_err(),
        crate::operations::RequestValidationError::InvalidRange
    );
    assert_eq!(
        streaming::ChangeReplayRequest::try_new(
            ChangeFilter::try_new(None, None).expect("filter"),
            None,
            streaming::MAX_STREAM_BATCH_ITEMS + 1
        )
        .unwrap_err(),
        crate::operations::RequestValidationError::InvalidRange
    );

    // A cancelled replay closes promptly before any work.
    let cancelled = Cancellation::new();
    cancelled.cancel();
    let request = streaming::ChangeReplayRequest::try_new(
        ChangeFilter::try_new(None, None).expect("filter"),
        None,
        2,
    )
    .expect("request");
    assert_eq!(
        store.replay_changes(&tenant_scope, &request, &cancelled).unwrap_err(),
        StreamError::Cancelled
    );

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn replay_never_crosses_a_tenant_scope() {
    let (dir, engine) = open_engine("replay-tenant-isolation");
    let scope_t1 = scope(&engine, "t1");
    let scope_t2 = scope(&engine, "t2");
    // Interleave both tenants' publications on one engine.
    for index in 0..4 {
        commit_document(
            &engine,
            "t1",
            &format!("t1-{index}"),
            "users",
            &format!("one-{index}"),
            "v",
        );
        commit_document(
            &engine,
            "t2",
            &format!("t2-{index}"),
            "users",
            &format!("two-{index}"),
            "v",
        );
    }

    let t1_events = replay_all(
        &engine,
        &scope_t1,
        &ChangeFilter::try_new(None, None).expect("filter"),
        None,
        3,
    );
    assert_eq!(t1_events.len(), 4, "only tenant t1's journal is read");
    assert!(t1_events.iter().all(|event| event.operation_id().as_str().starts_with("t1-")));
    assert!(t1_events.iter().all(|event| event.scope().tenant().as_str() == "t1"));

    let t2_events = replay_all(
        &engine,
        &scope_t2,
        &ChangeFilter::try_new(None, None).expect("filter"),
        None,
        3,
    );
    assert_eq!(t2_events.len(), 4, "only tenant t2's journal is read");
    assert!(t2_events.iter().all(|event| event.operation_id().as_str().starts_with("t2-")));

    // The isolation probe: a checkpoint forged from tenant t2's journal
    // position, replayed under tenant t1's scope, still serves only t1's
    // events after that position — the journal read is keyed by the scope's
    // validated tenant, never by the checkpoint's origin.
    let t2_last = t2_events.last().expect("t2 events").sequence();
    let probed = replay_all(
        &engine,
        &scope_t1,
        &ChangeFilter::try_new(None, None).expect("filter"),
        Some(streaming::ChangeCheckpoint::after(t2_last.get(), 0)),
        3,
    );
    assert!(
        probed.iter().all(|event| event.operation_id().as_str().starts_with("t1-")),
        "no cross-tenant event is served under a forged cross-tenant checkpoint"
    );

    // A scope from another engine is refused before any journal read.
    let (dir_b, engine_b) = open_engine("replay-tenant-isolation-b");
    let scope_b = scope(&engine_b, "t1");
    let request = streaming::ChangeReplayRequest::try_new(
        ChangeFilter::try_new(None, None).expect("filter"),
        None,
        2,
    )
    .expect("request");
    assert_eq!(
        engine.streams().replay_changes(&scope_b, &request, &Cancellation::new()).unwrap_err(),
        StreamError::SnapshotEngineMismatch
    );

    engine_b.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir_b).ok();
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn conflict_notifications_reach_only_the_owning_tenant() {
    let (dir, engine) = open_engine("conflict-tenant-isolation");
    let scope_t1 = scope(&engine, "t1");
    let scope_t2 = scope(&engine, "t2");
    let store = engine.streams();
    // Both tenants hold an unrefined subscription: the only guard between a
    // conflict notification and either consumer is the tenant check in the
    // ingress.
    store.subscribe(&scope_t1, &subscription("t1-conflicts", 8)).expect("subscribes t1");
    store.subscribe(&scope_t2, &subscription("t2-conflicts", 8)).expect("subscribes t2");

    let record = conflict_record("isolated");
    streaming::backbone::notify_conflict(&scope_t1, StorageSequence::new(9), record.clone());

    let t1_batch = store
        .next_subscription_batch(&scope_t1, "t1-conflicts", &Cancellation::new())
        .expect("t1 batch");
    assert_eq!(t1_batch.conflicts().len(), 1, "the owning tenant observes the conflict");
    let event = &t1_batch.conflicts()[0];
    assert_eq!(event.kind(), ChangeKind::Conflict);
    assert_eq!(event.conflict_record(), Some(&record));
    assert_eq!(event.scope().tenant().as_str(), "t1");
    assert_eq!(event.operation_id().as_str(), "op-local-isolated");

    // The probe: tenant t2's consumer, subscribed with the same
    // every-kind filter on the same engine, must not observe tenant t1's
    // conflict.
    let t2_batch = store
        .next_subscription_batch(&scope_t2, "t2-conflicts", &Cancellation::new())
        .expect("t2 batch");
    assert!(t2_batch.conflicts().is_empty(), "no cross-tenant conflict event");
    assert!(t2_batch.events().is_empty(), "no cross-tenant journal event");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn conflict_notification_burst_is_bounded_with_counted_drops() {
    let (dir, engine) = open_engine("conflict-burst");
    let tenant_scope = scope(&engine, "t1");
    let store = engine.streams();
    store.subscribe(&tenant_scope, &subscription("burst", 4)).expect("subscribes");

    // The consumer never drains while the burst runs past the conflict
    // queue's bound. Every notification must still be accepted: the ingress
    // is bounded and non-blocking, dropping the oldest and counting the
    // drop.
    let over = streaming::backbone::MAX_CONFLICT_NOTIFICATIONS + 20;
    for index in 0..over {
        streaming::backbone::notify_conflict(
            &tenant_scope,
            StorageSequence::new(u64::try_from(index).unwrap_or(0)),
            conflict_record(&format!("burst-{index}")),
        );
    }

    let batch = store
        .next_subscription_batch(&tenant_scope, "burst", &Cancellation::new())
        .expect("batch serves");
    assert!(
        batch.conflicts().len() <= 4,
        "delivered conflicts honor the spec bound, got {}",
        batch.conflicts().len()
    );
    assert!(
        batch.dropped_notifications() >= 20,
        "the overflow beyond the queue bound is counted and reported, got {}",
        batch.dropped_notifications()
    );
    // The newest notifications survived the drop-oldest policy.
    assert_eq!(batch.conflicts().len(), 4);
    assert_eq!(
        batch.conflicts()[0].conflict_record().expect("record").operations()[0].as_str(),
        format!("op-local-burst-{}", over - streaming::backbone::MAX_CONFLICT_NOTIFICATIONS)
    );

    // Acknowledgement consumes the delivered conflicts; the next batch
    // drains the remainder of the bounded queue.
    store
        .acknowledge(&tenant_scope, SubscriptionAcknowledgement::confirming(batch.checkpoint()))
        .expect("acknowledges");
    let rest = store
        .next_subscription_batch(&tenant_scope, "burst", &Cancellation::new())
        .expect("batch serves");
    assert_eq!(rest.conflicts().len(), 4);

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn conflict_ingress_never_blocks_behind_a_wedged_poll() {
    let (dir, engine) = open_engine("conflict-nonblocking");
    let tenant_scope = scope(&engine, "t1");
    commit_document(&engine, "t1", "op-seed", "users", "doc-seed", "v");
    let store = engine.streams();
    let spec = subscription("wedged-conflict", 8).from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");

    // Wedge one consumer poll while it holds the subscription lock, then
    // run the conflict ingress from another thread while it is held: the
    // ingress must not wait on the subscriber's lock. The wedge session
    // serializes this test against any other wedge test, because the wedge
    // is process-global and tests run in parallel.
    let _wedge_session = streaming::subscription::begin_wedge_session();
    streaming::subscription::arm_poll_wedge("wedged-conflict");
    let (notify_elapsed, entered) = std::thread::scope(|scope_thread| {
        let consumer = scope_thread.spawn(|| {
            store
                .next_subscription_batch(&tenant_scope, "wedged-conflict", &Cancellation::new())
                .expect("the wedged poll completes after release")
        });
        let entered_by = Instant::now() + Duration::from_secs(5);
        while !streaming::subscription::wedge_entered() {
            assert!(Instant::now() < entered_by, "the consumer poll never wedged");
            std::thread::sleep(Duration::from_millis(2));
        }
        let started = Instant::now();
        streaming::backbone::notify_conflict(
            &tenant_scope,
            StorageSequence::new(1),
            conflict_record("wedge"),
        );
        let notify_elapsed = started.elapsed();
        streaming::subscription::release_poll_wedge();
        let batch = consumer.join().expect("the consumer thread does not panic");
        (notify_elapsed, batch)
    });

    // The ingress returned while the poll was still wedged: it never
    // waited on the subscriber's lock. The bound is generous — a blocking
    // ingress would wait out the wedge's own five-second deadline.
    assert!(
        notify_elapsed < Duration::from_secs(1),
        "the conflict ingress blocked {notify_elapsed:?} behind a wedged subscriber poll"
    );
    let _ = entered;

    // The notification the ingress dropped on contention is counted, and
    // the delivery is observable as a counted drop in the next batch.
    let batch = store
        .next_subscription_batch(&tenant_scope, "wedged-conflict", &Cancellation::new())
        .expect("batch serves");
    assert!(batch.dropped_notifications() > 0, "the contention drop is counted and observable");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn conflict_events_honor_the_subscription_filter() {
    let (dir, engine) = open_engine("conflict-filter");
    let tenant_scope = scope(&engine, "t1");
    let store = engine.streams();
    let conflicts_only = SubscriptionSpec::try_new(
        "conflicts-only",
        ChangeFilter::try_new(Some(vec![ChangeKind::Conflict]), None).expect("filter"),
        8,
    )
    .expect("spec")
    .from_beginning();
    let documents_only = SubscriptionSpec::try_new(
        "documents-only",
        ChangeFilter::try_new(Some(vec![ChangeKind::Document]), None).expect("filter"),
        8,
    )
    .expect("spec")
    .from_beginning();
    store.subscribe(&tenant_scope, &conflicts_only).expect("subscribes");
    store.subscribe(&tenant_scope, &documents_only).expect("subscribes");

    commit_document(&engine, "t1", "op-journal", "users", "doc-1", "v");
    streaming::backbone::notify_conflict(
        &tenant_scope,
        StorageSequence::new(1),
        conflict_record("filtered"),
    );

    let conflict_batch = store
        .next_subscription_batch(&tenant_scope, "conflicts-only", &Cancellation::new())
        .expect("batch");
    assert!(conflict_batch.events().is_empty(), "the journal event is not delivered");
    assert_eq!(conflict_batch.filtered_dropped(), 1, "the filtered journal event is reported");
    assert_eq!(conflict_batch.conflicts().len(), 1, "the conflict is delivered");

    let document_batch = store
        .next_subscription_batch(&tenant_scope, "documents-only", &Cancellation::new())
        .expect("batch");
    assert_eq!(document_batch.events().len(), 1, "the journal event is delivered");
    assert!(
        document_batch.conflicts().is_empty(),
        "the conflict kind is filtered from a document subscription"
    );
    assert!(document_batch.filtered_dropped() >= 1, "the filtered conflict is reported");

    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// SE-03: public trigger notifications (PD-2026-08-26-13 D3)
// ---------------------------------------------------------------------------

#[test]
fn trigger_notifications_are_content_free_two_identifier_hints() {
    let (dir, engine) = open_engine("trigger-content-free");
    let tenant_scope = scope(&engine, "t1");
    let store = engine.streams();
    // The tap exists before the publication: a hint is emitted at commit
    // publication time and is never reconstructed after the fact.
    let tap = store.change_notifications(8).expect("tap opens");
    let sequence =
        commit_document(&engine, "t1", "op-trigger", "users", "secret-doc-id", "secret-body");

    let notification = tap.try_pop().expect("one publication, one hint");
    assert_eq!(notification.sequence(), sequence);
    assert_eq!(notification.operation_id().as_str(), "op-trigger");
    assert_eq!(
        notification,
        streaming::backbone::ChangeNotification::new(sequence, operation("op-trigger"))
    );
    // The hint is content-free: the two identifiers are its entire payload,
    // and no document identity or body ever rides it.
    let observed = std::format!("{notification:?}");
    assert!(!observed.contains("secret-doc-id"), "no document identity rides the hint: {observed}");
    assert!(!observed.contains("secret-body"), "no document body rides the hint: {observed}");
    assert!(tap.try_pop().is_none(), "one hint per publication");
    let _ = tenant_scope;

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn trigger_notification_queue_is_bounded_with_counted_drops() {
    let (dir, engine) = open_engine("trigger-bounded");
    let store = engine.streams();
    let tap = store.change_notifications(4).expect("tap opens");

    // A burst of publications with no consumer: the oldest hints are
    // dropped past the bound, the drops counted, the newest kept.
    for index in 0..10 {
        commit_document(
            &engine,
            "t1",
            &format!("op-{index}"),
            "users",
            &format!("doc-{index}"),
            "v",
        );
    }
    assert_eq!(tap.dropped(), 6, "the overflow beyond the bound is counted and reported");
    let mut seen = Vec::new();
    while let Some(notification) = tap.try_pop() {
        seen.push(notification.operation_id().as_str().to_owned());
    }
    assert_eq!(seen.len(), 4, "the queue holds exactly its bound");
    assert_eq!(seen[0], "op-6", "drop-oldest keeps the newest hints");
    assert_eq!(seen[3], "op-9");
    assert_eq!(tap.dropped(), 6, "draining does not reset the cumulative count");

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn trigger_notification_ingress_never_blocks_a_commit() {
    let (dir, engine) = open_engine("trigger-nonblocking");
    let store = engine.streams();
    let tap = store.change_notifications(8).expect("tap opens");

    // Hold the tap's queue lock - the exact contention the committing
    // thread's sink must survive without waiting - and commit from another
    // thread while it is held.
    let queue_lock = tap.hold_queue_for_test();
    let (sequence, commit_elapsed) = std::thread::scope(|scope_thread| {
        let committer = scope_thread.spawn(|| {
            let started = Instant::now();
            let sequence = commit_document(&engine, "t1", "op-wedge", "users", "doc-wedge", "v");
            (sequence, started.elapsed())
        });
        committer.join().expect("the committing thread does not panic")
    });
    drop(queue_lock);

    // The commit returned while the consumer still held the queue lock:
    // the sink never waited on it.
    assert!(
        commit_elapsed < Duration::from_secs(5),
        "the commit blocked {commit_elapsed:?} behind a locked notification queue"
    );
    assert!(tap.dropped() > 0, "the contention drop is counted and observable");
    // The contended hint was dropped, not delivered: a hint is a wake-up,
    // never data, so its loss costs one pull trigger - the commit itself
    // returned promptly and the journal holds the fact.
    assert!(tap.try_pop().is_none(), "a contended hint is dropped, not queued");
    let _ = sequence;

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn trigger_notifications_validate_capacity_and_sink_bounds() {
    let (dir, engine) = open_engine("trigger-bounds");
    let store = engine.streams();
    // The capacity bound is part of the request: zero and over-limit are
    // typed refusals, never silent truncation.
    assert_eq!(store.change_notifications(0).unwrap_err(), StreamError::InvalidSource);
    assert_eq!(
        store.change_notifications(streaming::MAX_CHANGE_NOTIFICATION_CAPACITY + 1).unwrap_err(),
        StreamError::InvalidSource
    );

    // The engine's publication registry bounds its sinks (the engine's own
    // index catalog holds one), so taps attach until that bound and the
    // next tap is refused - while every attached tap keeps serving.
    let mut taps = Vec::new();
    while let Ok(tap) = store.change_notifications(1) {
        taps.push(tap);
    }
    assert_eq!(
        taps.len(),
        crate::engine::MAX_PUBLICATION_SINKS - 1,
        "the registry bound minus the engine's own index sink"
    );
    commit_document(&engine, "t1", "op-bound", "users", "doc-bound", "v");
    assert!(taps[0].try_pop().is_some(), "an attached tap below the bound keeps serving");

    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// GAP-F02: family change payloads
// ---------------------------------------------------------------------------

/// One digest-shaped constant: sixty-four lowercase hex characters.
const PAYLOAD_TEST_DIGEST: &str =
    "0f4b1e9d2c3a5f6e8d7c9b0a1f2e3d4c5b6a79880f1e2d3c4b5a69788796a5b4";

/// Commits one owner user document so document-bound family writes stage.
fn commit_owner_document(engine: &Engine, tenant_scope: &TenantScope, id: &str) {
    let snapshot = engine.read_snapshot(tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(tenant_scope, snapshot, operation("op-owner")).expect("begins");
    transaction
        .put(text_document("sales", id, "v1"), ExpectedVersion::Any)
        .expect("stages the owner document");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("owner document did not commit: {other:?}"),
    }
}

/// Stages and commits one counter batch of named deltas on one document.
fn commit_counter_batch(
    engine: &Engine,
    tenant_scope: &TenantScope,
    operation_name: &str,
    document: &str,
    deltas: &[(&str, i64)],
) {
    let operations = deltas
        .iter()
        .map(|(name, delta)| CounterOperation::Increment {
            name: (*name).to_owned(),
            delta: *delta,
        })
        .collect();
    let write = CounterBatchWrite::try_new(
        tenant_scope.clone(),
        document_id(document),
        operations,
        ExpectedVersion::Any,
        operation(operation_name),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid counter batch");
    commit_family_write(
        engine,
        tenant_scope,
        operation_name,
        FamilyWriteRequest::counter_batch(write),
    );
}

/// Returns the events one kind filter accepts, replayed from the beginning.
fn replay_kind(engine: &Engine, tenant_scope: &TenantScope, kind: ChangeKind) -> Vec<ChangeEvent> {
    let filter = ChangeFilter::try_new(Some(vec![kind]), None).expect("filter");
    replay_all(engine, tenant_scope, &filter, None, 16)
}

/// Drains one kind-filtered live subscription from the beginning.
fn drain_live_kind(
    store: &StreamStore<'_>,
    tenant_scope: &TenantScope,
    name: &str,
    kind: ChangeKind,
) -> Vec<ChangeEvent> {
    let filter = ChangeFilter::try_new(Some(vec![kind]), None).expect("filter");
    let spec = SubscriptionSpec::try_new(name, filter, 16).expect("valid spec").from_beginning();
    store.subscribe(tenant_scope, &spec).expect("subscribes");
    drain_live(store, tenant_scope, name)
}

#[test]
fn counter_change_payloads_round_trip_totals_through_replay_and_live() {
    let (dir, engine) = open_engine("payload-counter");
    let tenant_scope = scope(&engine, "t1");
    commit_owner_document(&engine, &tenant_scope, "orders/1");
    commit_counter_batch(
        &engine,
        &tenant_scope,
        "op-counters",
        "orders/1",
        &[("views", 5), ("hits", 2)],
    );

    let replayed = replay_kind(&engine, &tenant_scope, ChangeKind::Counter);
    assert_eq!(replayed.len(), 1, "one counter record put, one event");
    let event = &replayed[0];
    let Some(FamilyChangePayload::Counters(counter)) = event.payload() else {
        panic!("a counter record event carries a counters payload: {event:?}");
    };
    assert_eq!(counter.shape(), PayloadShape::Put);
    assert_eq!(counter.record_version(), 1, "the first put of the record");
    assert!(counter.complete(), "two entries is far inside the bound");
    assert_eq!(counter.omitted_entries(), 0);
    assert_eq!(counter.entries().len(), 2);
    assert_eq!(counter.entries()[0].name(), "hits", "entries ascend by name");
    assert_eq!(counter.entries()[0].total(), 2);
    assert_eq!(counter.entries()[1].name(), "views");
    assert_eq!(counter.entries()[1].total(), 5);

    // The live subscription path and the journal replay path observe the
    // identical payload for the same journal position.
    let store = engine.streams();
    let live = drain_live_kind(&store, &tenant_scope, "payload-counter-live", ChangeKind::Counter);
    assert_eq!(live.len(), 1);
    assert_eq!(live[0].payload(), replayed[0].payload());

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn time_series_change_payloads_round_trip_segment_summaries() {
    let (dir, engine) = open_engine("payload-series");
    let tenant_scope = scope(&engine, "t1");
    commit_owner_document(&engine, &tenant_scope, "orders/1");
    let write = TimeSeriesBatchWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        "cpu".to_owned(),
        vec![
            TimeSeriesAppend::new(100, vec![1.0, 2.0], Some("a".to_owned())),
            TimeSeriesAppend::new(250, vec![3.0, 4.0], None),
            TimeSeriesAppend::new(300, vec![5.0, 6.0], Some("b".to_owned())),
        ],
        vec![],
        None,
        ExpectedVersion::Any,
        operation("op-series"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid series batch");
    commit_family_write(
        &engine,
        &tenant_scope,
        "op-series",
        FamilyWriteRequest::time_series_batch(write),
    );

    let replayed = replay_kind(&engine, &tenant_scope, ChangeKind::TimeSeries);
    assert_eq!(replayed.len(), 1);
    let Some(FamilyChangePayload::TimeSeries(series)) = replayed[0].payload() else {
        panic!("a series record event carries a series payload");
    };
    assert_eq!(series.shape(), PayloadShape::Put);
    assert_eq!(series.record_version(), 1);
    assert_eq!(series.name(), "cpu");
    assert_eq!(series.arity(), Some(2));
    assert_eq!(series.from(), Some(100), "the covered span starts at the first entry");
    assert_eq!(series.to(), Some(300), "the covered span ends at the last entry");
    assert_eq!(series.entry_count(), 3);

    let store = engine.streams();
    let live =
        drain_live_kind(&store, &tenant_scope, "payload-series-live", ChangeKind::TimeSeries);
    assert_eq!(live.len(), 1);
    assert_eq!(live[0].payload(), replayed[0].payload());

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn attachment_change_payloads_round_trip_content_addressed_metadata() {
    let (dir, engine) = open_engine("payload-attachment");
    let tenant_scope = scope(&engine, "t1");
    commit_owner_document(&engine, &tenant_scope, "orders/1");
    let mutation = AttachmentMutation::Store {
        name: "report".to_owned(),
        content_type: "application/pdf".to_owned(),
        size: 2048,
        hash: PAYLOAD_TEST_DIGEST.to_owned(),
        replace: false,
    };
    let write = AttachmentWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        mutation,
        ExpectedVersion::Any,
        operation("op-attachment"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid attachment write");
    commit_family_write(
        &engine,
        &tenant_scope,
        "op-attachment",
        FamilyWriteRequest::attachment_mutation(write),
    );

    // Attachment metadata records ride Document-kind events (the closed V1
    // change-kind set has no attachment case), so the payload is asserted on
    // the event of the metadata record itself.
    let filter = ChangeFilter::try_new(None, None).expect("filter");
    let replayed = replay_all(&engine, &tenant_scope, &filter, None, 16);
    let event = replayed
        .iter()
        .find(|event| {
            event.document().is_some_and(|id| id.as_str() == "family:attachments/orders/1")
        })
        .expect("the attachment metadata record produced an event");
    let Some(FamilyChangePayload::Attachments(attachments)) = event.payload() else {
        panic!("an attachment metadata record event carries an attachment payload");
    };
    assert_eq!(attachments.shape(), PayloadShape::Put);
    assert_eq!(attachments.record_version(), 1);
    assert!(attachments.complete());
    assert_eq!(attachments.entries().len(), 1);
    let entry = &attachments.entries()[0];
    assert_eq!(entry.name(), "report");
    assert_eq!(entry.content_type(), "application/pdf");
    assert_eq!(entry.size(), 2048);
    assert_eq!(entry.hash(), PAYLOAD_TEST_DIGEST, "the content address rides the payload");

    let store = engine.streams();
    let spec = SubscriptionSpec::try_new("payload-attachment-live", filter.clone(), 16)
        .expect("valid spec")
        .from_beginning();
    store.subscribe(&tenant_scope, &spec).expect("subscribes");
    let live = drain_live(&store, &tenant_scope, "payload-attachment-live");
    let live_event = live
        .iter()
        .find(|event| {
            event.document().is_some_and(|id| id.as_str() == "family:attachments/orders/1")
        })
        .expect("the live path delivered the metadata event");
    assert_eq!(live_event.payload(), event.payload());

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn change_payloads_refuse_document_bodies() {
    let (dir, engine) = open_engine("payload-refuses-bodies");
    let tenant_scope = scope(&engine, "t1");
    commit_documents(
        &engine,
        "t1",
        "op-users",
        &[("users", "u/1", "value-one"), ("users", "u/2", "value-two")],
    );

    let filter = ChangeFilter::try_new(None, None).expect("filter");
    let replayed = replay_all(&engine, &tenant_scope, &filter, None, 16);
    assert_eq!(replayed.len(), 2);
    for event in &replayed {
        assert_eq!(event.kind(), ChangeKind::Document);
        assert!(event.payload().is_none(), "a user-document event carries no payload: {event:?}");
    }

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn attachment_content_records_never_mint_change_payloads() {
    let (dir, engine) = open_engine("payload-content-plane");
    let tenant_scope = scope(&engine, "t1");
    commit_owner_document(&engine, &tenant_scope, "orders/1");

    // Stage the content plane first, then the metadata that addresses it:
    // the metadata hash is the digest the plane computed, so the payload's
    // content address provably names the stored bytes.
    let content = AttachmentContentWrite::try_new(
        tenant_scope.clone(),
        None,
        vec![AttachmentSegment::try_new(b"attachment-bytes").expect("bounded segment")],
        operation("op-content"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid content write");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation("op-content")).expect("begins");
    let recorded = engine
        .families()
        .stage_attachment_content(&mut transaction, content)
        .expect("stages content");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("content did not commit: {other:?}"),
    }

    let mutation = AttachmentMutation::Store {
        name: "report".to_owned(),
        content_type: "application/pdf".to_owned(),
        size: recorded.size(),
        hash: recorded.digest().to_owned(),
        replace: false,
    };
    let write = AttachmentWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        mutation,
        ExpectedVersion::Any,
        operation("op-attachment"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid attachment write");
    commit_family_write(
        &engine,
        &tenant_scope,
        "op-attachment",
        FamilyWriteRequest::attachment_mutation(write),
    );

    let filter = ChangeFilter::try_new(None, None).expect("filter");
    let replayed = replay_all(&engine, &tenant_scope, &filter, None, 16);
    let content_events: Vec<&ChangeEvent> = replayed
        .iter()
        .filter(|event| {
            event.document().is_some_and(|id| id.as_str().starts_with("family:attachment-content/"))
        })
        .collect();
    assert!(
        !content_events.is_empty(),
        "the content plane's manifest and segment records produced events"
    );
    for event in &content_events {
        assert!(
            event.payload().is_none(),
            "content-plane bytes never ride a change payload: {event:?}"
        );
    }
    let metadata_event = replayed
        .iter()
        .find(|event| {
            event.document().is_some_and(|id| id.as_str() == "family:attachments/orders/1")
        })
        .expect("the metadata record produced an event");
    let Some(FamilyChangePayload::Attachments(attachments)) = metadata_event.payload() else {
        panic!("the metadata event carries its payload");
    };
    assert_eq!(
        attachments.entries()[0].hash(),
        recorded.digest(),
        "the payload's content address is the digest the plane computed"
    );
    assert_eq!(attachments.entries()[0].size(), recorded.size());

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn change_payload_constructors_refuse_oversize_entry_lists() {
    use crate::operations::RequestValidationError;

    let oversize_counters: Vec<CounterChangeEntry> = (0..=MAX_CHANGE_PAYLOAD_ENTRIES)
        .map(|index| {
            CounterChangeEntry::try_new(&format!("n{index:03}"), i64::try_from(index).unwrap_or(0))
                .expect("a valid entry alone")
        })
        .collect();
    assert_eq!(
        CounterChangePayload::try_new(PayloadShape::Put, 1, oversize_counters.clone()).unwrap_err(),
        RequestValidationError::InvalidFamilyPayload,
        "one entry over the bound is refused"
    );
    assert!(
        CounterChangePayload::try_new(
            PayloadShape::Put,
            1,
            oversize_counters[..MAX_CHANGE_PAYLOAD_ENTRIES].to_vec()
        )
        .is_ok(),
        "exactly the bound is accepted"
    );

    let oversize_attachments: Vec<AttachmentChangeEntry> = (0..=MAX_CHANGE_PAYLOAD_ENTRIES)
        .map(|index| {
            AttachmentChangeEntry::try_new(
                &format!("n{index:03}"),
                "application/pdf",
                16,
                PAYLOAD_TEST_DIGEST,
            )
            .expect("a valid entry alone")
        })
        .collect();
    assert_eq!(
        AttachmentChangePayload::try_new(PayloadShape::Put, 1, oversize_attachments).unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
}

#[test]
fn change_payload_constructors_refuse_malformed_fields() {
    use crate::operations::RequestValidationError;

    // Malformed fields are refused at entry construction.
    let long_name = "n".repeat(256);
    assert_eq!(
        CounterChangeEntry::try_new(&long_name, 1).unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        CounterChangeEntry::try_new("", 1).unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        CounterChangeEntry::try_new("bad\nname", 1).unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        AttachmentChangeEntry::try_new("report", &long_name, 16, PAYLOAD_TEST_DIGEST).unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        AttachmentChangeEntry::try_new("report", "application/pdf", 16, "not-a-digest")
            .unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        AttachmentChangeEntry::try_new("report", "application/pdf", 16, &"a".repeat(63))
            .unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );

    // Series payload fields follow the series-name rules and the range order.
    assert_eq!(
        TimeSeriesChangePayload::try_new(
            PayloadShape::Put,
            1,
            "bad/name",
            Some(2),
            Some(100),
            Some(200),
            3
        )
        .unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        TimeSeriesChangePayload::try_new(
            PayloadShape::Put,
            1,
            &long_name,
            Some(2),
            Some(100),
            Some(200),
            3
        )
        .unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        TimeSeriesChangePayload::try_new(
            PayloadShape::Put,
            1,
            "cpu",
            Some(2),
            Some(200),
            Some(100),
            3
        )
        .unwrap_err(),
        RequestValidationError::InvalidFamilyPayload,
        "an inverted range is refused"
    );
    assert!(
        TimeSeriesChangePayload::try_new(PayloadShape::Put, 1, "cpu", Some(2), None, None, 0)
            .is_ok(),
        "an empty series carries no range"
    );
}

#[test]
fn wide_counter_records_truncate_the_payload_with_a_reported_omitted_count() {
    let (dir, engine) = open_engine("payload-truncation");
    let tenant_scope = scope(&engine, "t1");
    commit_owner_document(&engine, &tenant_scope, "orders/1");
    let names: Vec<(String, i64)> = (0..70).map(|index| (format!("n{index:03}"), 1)).collect();
    let borrowed: Vec<(&str, i64)> =
        names.iter().map(|(name, delta)| (name.as_str(), *delta)).collect();
    commit_counter_batch(&engine, &tenant_scope, "op-wide", "orders/1", &borrowed);

    let replayed = replay_kind(&engine, &tenant_scope, ChangeKind::Counter);
    assert_eq!(replayed.len(), 1);
    let Some(FamilyChangePayload::Counters(counter)) = replayed[0].payload() else {
        panic!("the wide record still carries its payload");
    };
    assert!(!counter.complete(), "a record wider than the bound reports truncation");
    assert_eq!(counter.omitted_entries(), 6);
    assert_eq!(counter.entries().len(), MAX_CHANGE_PAYLOAD_ENTRIES);
    assert_eq!(counter.entries().first().expect("bounded").name(), "n000");
    assert_eq!(
        counter.entries().last().expect("bounded").name(),
        format!("n{:03}", MAX_CHANGE_PAYLOAD_ENTRIES - 1)
    );
    for entry in counter.entries() {
        assert_eq!(entry.total(), 1);
    }

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn change_payloads_carry_strictly_increasing_record_versions_and_delete_shapes() {
    let (dir, engine) = open_engine("payload-versions");
    let tenant_scope = scope(&engine, "t1");
    commit_owner_document(&engine, &tenant_scope, "orders/1");
    commit_counter_batch(&engine, &tenant_scope, "op-one", "orders/1", &[("views", 5)]);
    commit_counter_batch(&engine, &tenant_scope, "op-two", "orders/1", &[("views", 1)]);
    // The counter family's tombstone discipline keeps a cleared name's
    // record present (delete wins and fences stale writers), so the
    // record-level Delete shape is exercised through the engine's delete
    // seam — the journal write a replication apply or restore path would
    // produce.
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation("op-record-delete")).expect("begins");
    transaction
        .delete(document_id("family:counters/orders/1"), ExpectedVersion::Any)
        .expect("stages the record delete");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("the record delete did commit: {other:?}"),
    }

    let replayed = replay_kind(&engine, &tenant_scope, ChangeKind::Counter);
    assert_eq!(replayed.len(), 3, "two puts and one record delete");
    let Some(FamilyChangePayload::Counters(first)) = replayed[0].payload() else {
        panic!("the first put carries its payload");
    };
    let Some(FamilyChangePayload::Counters(second)) = replayed[1].payload() else {
        panic!("the second put carries its payload");
    };
    let Some(FamilyChangePayload::Counters(cleared)) = replayed[2].payload() else {
        panic!("the record delete carries its payload");
    };
    assert_eq!(first.shape(), PayloadShape::Put);
    assert_eq!(second.shape(), PayloadShape::Put);
    assert!(
        second.record_version() > first.record_version(),
        "successive writes to one record observe increasing versions"
    );
    assert_eq!(first.entries()[0].total(), 5);
    assert_eq!(second.entries()[0].total(), 6);
    assert_eq!(cleared.shape(), PayloadShape::Delete);
    assert_eq!(
        cleared.record_version(),
        second.record_version(),
        "the delete carries the version that existed at the deleted key"
    );
    assert!(cleared.entries().is_empty(), "per-name entries are not recoverable from a delete");
    assert!(cleared.complete());

    // The time-series family clears its record when the last entry is
    // deleted and no policy remains, so its family-native clear is the
    // Delete shape through the family seam itself.
    let append = TimeSeriesBatchWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        "cpu".to_owned(),
        vec![TimeSeriesAppend::new(100, vec![1.0], None)],
        vec![],
        None,
        ExpectedVersion::Any,
        operation("op-series-put"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid series batch");
    commit_family_write(
        &engine,
        &tenant_scope,
        "op-series-put",
        FamilyWriteRequest::time_series_batch(append),
    );
    let clear = TimeSeriesBatchWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        "cpu".to_owned(),
        vec![],
        vec![TimeSeriesDelete::Range { start: 0, end: 1_000 }],
        None,
        ExpectedVersion::Any,
        operation("op-series-clear"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid series clear");
    commit_family_write(
        &engine,
        &tenant_scope,
        "op-series-clear",
        FamilyWriteRequest::time_series_batch(clear),
    );
    let series = replay_kind(&engine, &tenant_scope, ChangeKind::TimeSeries);
    assert_eq!(series.len(), 2, "one put and one family-native clear");
    let Some(FamilyChangePayload::TimeSeries(series_clear)) = series[1].payload() else {
        panic!("the series clear carries its payload");
    };
    assert_eq!(series_clear.shape(), PayloadShape::Delete);
    assert_eq!(series_clear.name(), "cpu");
    assert_eq!(series_clear.from(), None);
    assert_eq!(series_clear.to(), None);
    assert_eq!(series_clear.entry_count(), 0);

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn malformed_reserved_records_deliver_payload_less_events() {
    let (dir, engine) = open_engine("payload-malformed");
    let tenant_scope = scope(&engine, "t1");
    // A garbage value occupying a counter record identifier: the event still
    // delivers (fail-open to the pre-payload behavior), payload-less.
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation("op-garbage")).expect("begins");
    transaction
        .put(
            text_document("terrabase.families", "family:counters/bad", "garbage"),
            ExpectedVersion::Any,
        )
        .expect("stages the malformed record");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("the malformed record did commit: {other:?}"),
    }

    let replayed = replay_kind(&engine, &tenant_scope, ChangeKind::Counter);
    assert_eq!(replayed.len(), 1);
    assert_eq!(replayed[0].document().map(DocumentId::as_str), Some("family:counters/bad"));
    assert!(
        replayed[0].payload().is_none(),
        "an undecodable record mints no payload, but the event still delivers"
    );

    std::fs::remove_dir_all(&dir).ok();
}

/// The reserved record-key prefixes of the seven payload-less family
/// kinds (decision record disposition table).
const PAYLOAD_LESS_PREFIXES: [&str; 7] = [
    "family:revisions/",
    "family:compare-exchange/",
    "family:identities/",
    "family:lifecycle/",
    "family:archival/",
    "family:validation/",
    "family:tombstones/",
];

/// Commits one minimal real write of each payload-less family kind under
/// one tenant, against one owner document the document-bound kinds need.
fn commit_payload_less_kind_writes(engine: &Engine, tenant_scope: &TenantScope) {
    commit_revision_cmpx_identity_lifecycle_writes(engine, tenant_scope);
    commit_archival_validation_tombstone_writes(engine, tenant_scope);
}

/// The revision, compare-exchange, identity, and lifecycle quarters of the
/// payload-less kind fixture set.
fn commit_revision_cmpx_identity_lifecycle_writes(engine: &Engine, tenant_scope: &TenantScope) {
    let revision = RevisionWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        RevisionCommand::ForceCreate { expected: ExpectedVersion::Any },
        operation("op-revisions"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid revision write");
    commit_family_write(
        engine,
        tenant_scope,
        "op-revisions",
        FamilyWriteRequest::revision_command(revision),
    );

    let compare_exchange = CompareExchangeWrite::try_new(
        tenant_scope.clone(),
        "k1".to_owned(),
        CompareExchangeCondition::PutIfMissing {
            value: DocumentValue::String("v".to_owned()),
            expires_at: None,
        },
        operation("op-cmpx"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid compare-exchange write");
    commit_family_write(
        engine,
        tenant_scope,
        "op-cmpx",
        FamilyWriteRequest::compare_exchange_condition(compare_exchange),
    );

    let identity = IdentityWrite::try_new(
        tenant_scope.clone(),
        "seq".to_owned(),
        IdentityCommand::Seed { name: "seq".to_owned(), value: 100, force: false },
        operation("op-identities"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid identity write");
    commit_family_write(
        engine,
        tenant_scope,
        "op-identities",
        FamilyWriteRequest::identity_allocation(identity),
    );

    let lifecycle = LifecycleWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        LifecycleCommand::SetExpiration { at: Some(1_000) },
        ExpectedVersion::Any,
        operation("op-lifecycle"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid lifecycle write");
    commit_family_write(
        engine,
        tenant_scope,
        "op-lifecycle",
        FamilyWriteRequest::lifecycle_update(lifecycle),
    );
}

/// The archival, validation, and tombstone quarters of the payload-less
/// kind fixture set.
fn commit_archival_validation_tombstone_writes(engine: &Engine, tenant_scope: &TenantScope) {
    let archival = ArchivalWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        ArchivalCommand::SetPolicy { after_ms: 60_000 },
        operation("op-archival"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid archival write");
    commit_family_write(
        engine,
        tenant_scope,
        "op-archival",
        FamilyWriteRequest::archival_update(archival),
    );

    let schema = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![SchemaRule::Required { field: "name".to_owned() }],
    )
    .expect("valid schema");
    let validation = crate::families::ValidationWrite::try_new(
        tenant_scope.clone(),
        collection("sales"),
        Some(schema),
        operation("op-validation"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid validation write");
    commit_family_write(
        engine,
        tenant_scope,
        "op-validation",
        FamilyWriteRequest::schema_validation_update(validation),
    );

    let tombstone = TombstoneWrite::try_new(
        tenant_scope.clone(),
        document_id("orders/1"),
        TombstoneCommand::Record { family: FamilyKind::Counters, deletion_version: 1 },
        operation("op-tombstones"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid tombstone write");
    commit_family_write(
        engine,
        tenant_scope,
        "op-tombstones",
        FamilyWriteRequest::tombstone_operation(tombstone),
    );
}

#[test]
fn payload_less_family_kinds_never_mint_change_payloads() {
    let (dir, engine) = open_engine("payload-kinds");
    let tenant_scope = scope(&engine, "t1");
    commit_owner_document(&engine, &tenant_scope, "orders/1");
    commit_payload_less_kind_writes(&engine, &tenant_scope);

    let filter = ChangeFilter::try_new(None, None).expect("filter");
    let replayed = replay_all(&engine, &tenant_scope, &filter, None, 64);
    for prefix in PAYLOAD_LESS_PREFIXES {
        let events: Vec<&ChangeEvent> = replayed
            .iter()
            .filter(|event| event.document().is_some_and(|id| id.as_str().starts_with(prefix)))
            .collect();
        assert!(!events.is_empty(), "{prefix} produced at least one event");
        for event in events {
            assert!(
                event.payload().is_none(),
                "{prefix} events carry no change payload: {event:?}"
            );
        }
    }

    std::fs::remove_dir_all(&dir).ok();
}
