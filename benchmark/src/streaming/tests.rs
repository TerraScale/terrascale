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
    time::Duration,
};

use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::families::{
    CounterBatchWrite, CounterOperation, CounterRead, FamilyReadContext, FamilyReadRequest,
    FamilyWriteRequest, TimeSeriesAppend, TimeSeriesBatchWrite, TimeSeriesRange,
};
use crate::operations::QueryInput;
use crate::query::{
    IndexNamespace, OrderDirection, OrderingKey, QueryCondition, QueryOrdering, QueryPaging,
    QueryPredicate, QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
};
use crate::streaming::{
    self, ChangeEvent, ChangeFilter, ChangeKind, DocumentStreamSelector, FamilyStreamSelector,
    StreamBatch, StreamCompletion, StreamCursor, StreamError, StreamItem, StreamSource,
    StreamStore, SubscriptionAcknowledgement, SubscriptionSpec, SubscriptionStatus,
    TimeSeriesStreamRange,
};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, ReadSnapshot, StorageSequence, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-w4-stream-{tag}-{}-{index}", std::process::id()))
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
    assert_eq!(event.collection().as_str(), "users");
    assert_eq!(event.document().as_str(), "doc-1");
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
        first.events().iter().all(|event| event.collection().as_str() == "users"),
        "only accepted collections are delivered"
    );
    assert_eq!(first.events().len(), 3);
    // The three `orders` publications the scan examined were dropped and
    // reported; the trailing one sits after the boundary, so it will be
    // re-examined on the next poll — the no-advance rule's cost.
    assert_eq!(first.filtered_dropped(), 3, "dropped events are reported");
    let acknowledged_checkpoint = first.checkpoint();
    store
        .acknowledge(
            &tenant_scope,
            SubscriptionAcknowledgement::confirming(acknowledged_checkpoint.clone()),
        )
        .expect("acknowledges");

    // A batch whose events are all filtered reports the drops and keeps
    // the checkpoint exactly where the acknowledgement left it.
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
    assert_eq!(dropped.filtered_dropped(), 3, "the drops are progress facts");
    assert_eq!(
        dropped.checkpoint(),
        acknowledged_checkpoint,
        "a filter drop never advances the checkpoint"
    );

    std::fs::remove_dir_all(&dir).ok();
}
