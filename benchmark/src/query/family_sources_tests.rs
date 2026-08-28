//! In-crate tests for family query sources (GAP-Q19, folds F3).
//!
//! Counters and time-series serve as typed query sources: the counter
//! source returns live counter entries `(document, name, value)`, the
//! time-series source returns retained entries `(document, name,
//! timestamp, values, tag)` with paging through the GAP-F04 seam. Both
//! read through the family seam's own readers against the query's
//! snapshot, so a query row, an include entry, and a direct family read
//! never disagree. Fixtures are staged through the family seam's write
//! path; no test inspects reserved family state directly, and no V1
//! parity claim is made beyond the pinned citations in
//! `docs/query/gapq19-family-sources.md`.

#![allow(clippy::too_many_lines)]

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use super::{
    CounterInclude, CounterQueryRow, FamilyQueryRow, IncludeClause, IncludePath, OrderDirection,
    OrderingKey, QueryCondition, QueryError, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QueryResult, QuerySource, QuerySourceKind, QuerySpec, StalenessMode,
    StalenessPolicy, TimeSeriesPaging, TimeSeriesQueryRow, TimeSeriesRange,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    families::{
        CounterOperation, FamilyReadContext, FamilyReadRequest, FamilyWriteRequest,
        TimeSeriesAppend, TimeSeriesBatchWrite, TimeSeriesRead,
    },
    operations::QueryInput,
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    },
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-q19-sources-{tag}-{}-{index}", std::process::id()))
}

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

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(45)).expect("valid deadline")
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn object_document(id: &str, collection: &str, fields: &[(&str, DocumentValue)]) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        object(fields),
    )
}

fn unique_operation(prefix: &str) -> OperationId {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("{prefix}-{index}")).expect("valid identifier")
}

/// Commits one transaction over documents and family writes together, the
/// same staging shape the includes tests use.
fn commit_transaction<'engine>(
    engine: &'engine Engine,
    tenant_name: &str,
    stage: impl FnOnce(
        &crate::families::FamilyStore<'engine>,
        &mut crate::engine::Transaction<'engine>,
        &TenantScope,
        &OperationId,
    ),
) -> u64 {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("q19");
    let store = engine.families();
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    stage(&store, &mut transaction, &tenant_scope, &identity);
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => receipt.sequence().get(),
        other => panic!("commit did not succeed: {other:?}"),
    }
}

fn put_documents(engine: &Engine, tenant_name: &str, documents: Vec<Document>) -> u64 {
    commit_transaction(engine, tenant_name, |_store, transaction, _scope, _identity| {
        for document in documents {
            transaction.put(document, ExpectedVersion::Any).expect("stages document");
        }
    })
}

fn stage_counters<'engine>(
    store: &crate::families::FamilyStore<'engine>,
    transaction: &mut crate::engine::Transaction<'engine>,
    tenant_scope: &TenantScope,
    identity: &OperationId,
    document: &str,
    operations: Vec<CounterOperation>,
) {
    store
        .stage(
            transaction,
            FamilyWriteRequest::counter_batch(
                crate::families::CounterBatchWrite::try_new(
                    tenant_scope.clone(),
                    DocumentId::try_new(document).expect("valid identifier"),
                    operations,
                    ExpectedVersion::Any,
                    identity.clone(),
                    deadline(),
                    Cancellation::new(),
                )
                .expect("valid counter batch"),
            ),
        )
        .expect("stages counters");
}

fn counter_increment(name: &str, delta: i64) -> CounterOperation {
    CounterOperation::Increment { name: name.to_owned(), delta }
}

/// Stages one single-valued series with distinct derived values per entry.
fn stage_series<'engine>(
    store: &crate::families::FamilyStore<'engine>,
    transaction: &mut crate::engine::Transaction<'engine>,
    tenant_scope: &TenantScope,
    identity: &OperationId,
    document: &str,
    name: &str,
    points: &[(i64, f64, Option<&str>)],
) {
    let appends = points
        .iter()
        .map(|(timestamp, value, tag)| {
            TimeSeriesAppend::new(*timestamp, vec![*value], tag.map(str::to_owned))
        })
        .collect();
    store
        .stage(
            transaction,
            FamilyWriteRequest::time_series_batch(
                TimeSeriesBatchWrite::try_new(
                    tenant_scope.clone(),
                    DocumentId::try_new(document).expect("valid identifier"),
                    name.to_owned(),
                    appends,
                    Vec::new(),
                    None,
                    ExpectedVersion::Any,
                    identity.clone(),
                    deadline(),
                    Cancellation::new(),
                )
                .expect("valid series batch"),
            ),
        )
        .expect("stages series");
}

fn family_spec(claim: &str, source: QuerySource) -> QuerySpec {
    QuerySpec::try_new(
        tenant(claim),
        source,
        QueryPredicate::all(Vec::new()).expect("valid predicate"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn execute_family(
    engine: &Engine,
    tenant_name: &str,
    spec: QuerySpec,
) -> Result<QueryResult, QueryError> {
    let store = engine.queries();
    let plan = store.compile(QueryInput::Typed(spec)).expect("compiles");
    let snapshot = engine.read_snapshot(&scope(engine, tenant_name)).expect("snapshot");
    store.execute(&plan, &snapshot)
}

/// Reads one counter group directly through the family seam, the read the
/// parity assertions compare the query rows against.
fn direct_counter_read(
    engine: &Engine,
    tenant_name: &str,
    document: &str,
    names: Vec<String>,
) -> crate::families::CounterGroupResult {
    let snapshot = engine.read_snapshot(&scope(engine, tenant_name)).expect("snapshot");
    let context = FamilyReadContext::new(scope(engine, tenant_name), snapshot);
    let read = crate::families::CounterRead::try_new(
        DocumentId::try_new(document).expect("valid identifier"),
        names,
        deadline(),
        Cancellation::new(),
    )
    .expect("valid counter read");
    match engine
        .families()
        .read(context, FamilyReadRequest::counter_group(read))
        .expect("counter read")
    {
        crate::families::FamilyReadResult::CounterGroup(group) => group,
        other => panic!("counter read returned {other:?}"),
    }
}

/// Reads one series page directly through the family seam.
fn direct_series_read(
    engine: &Engine,
    tenant_name: &str,
    document: &str,
    name: &str,
    range: Option<TimeSeriesRange>,
) -> crate::families::TimeSeriesPage {
    let snapshot = engine.read_snapshot(&scope(engine, tenant_name)).expect("snapshot");
    let context = FamilyReadContext::new(scope(engine, tenant_name), snapshot);
    let read = TimeSeriesRead::try_new(
        DocumentId::try_new(document).expect("valid identifier"),
        name.to_owned(),
        range,
        deadline(),
        Cancellation::new(),
    )
    .expect("valid series read");
    match engine
        .families()
        .read(context, FamilyReadRequest::time_series_range(read))
        .expect("series read")
    {
        crate::families::FamilyReadResult::TimeSeriesPage(page) => page,
        other => panic!("series read returned {other:?}"),
    }
}

fn counter_rows(result: &QueryResult) -> Vec<&CounterQueryRow> {
    result
        .family_rows()
        .iter()
        .filter_map(|row| match row {
            FamilyQueryRow::Counter(row) => Some(row),
            FamilyQueryRow::TimeSeries(_) => None,
        })
        .collect()
}

fn series_rows(result: &QueryResult) -> Vec<&TimeSeriesQueryRow> {
    result
        .family_rows()
        .iter()
        .filter_map(|row| match row {
            FamilyQueryRow::TimeSeries(row) => Some(row),
            FamilyQueryRow::Counter(_) => None,
        })
        .collect()
}

// ---------------------------------------------------------------------------
// Counter source
// ---------------------------------------------------------------------------

#[test]
fn counter_source_returns_entry_rows_across_documents() {
    let (_dir, engine) = open_engine("counter-rows");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("users/1", "users", &[("name", DocumentValue::String("a".into()))]),
            object_document("users/2", "users", &[("name", DocumentValue::String("b".into()))]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            vec![counter_increment("likes", 7), counter_increment("views", -2)],
        );
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/2",
            vec![counter_increment("likes", 40)],
        );
    });

    let result = execute_family(&engine, "t", family_spec("t", QuerySource::counters()))
        .expect("counter source executes");
    assert!(
        result.rows().is_empty(),
        "family state must not leak through the document row channel"
    );
    let rows = counter_rows(&result);
    // One row per live entry: (users/1, likes), (users/1, views), (users/2, likes).
    assert_eq!(rows.len(), 3, "one row per live counter entry");
    assert_eq!(rows[0].document().as_str(), "users/1");
    assert_eq!(rows[0].name(), "likes");
    assert_eq!(rows[0].value(), 7);
    assert_eq!(rows[1].name(), "views");
    assert_eq!(rows[1].value(), -2);
    assert_eq!(rows[2].document().as_str(), "users/2");
    assert_eq!(rows[2].name(), "likes");
    assert_eq!(rows[2].value(), 40);
    assert_eq!(result.matched(), 3, "matched counts entries before paging");
    assert_eq!(result.statistics().scanned(), 3);
    assert_eq!(result.statistics().skipped(), 0);
}

#[test]
fn counter_source_named_restricts_to_one_name() {
    let (_dir, engine) = open_engine("counter-named");
    put_documents(
        &engine,
        "t",
        vec![object_document("users/1", "users", &[]), object_document("users/2", "users", &[])],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            vec![counter_increment("likes", 1), counter_increment("views", 2)],
        );
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/2",
            vec![counter_increment("views", 3)],
        );
    });

    let result = execute_family(
        &engine,
        "t",
        family_spec("t", QuerySource::counters_named("views").expect("valid name")),
    )
    .expect("named counter source executes");
    let rows = counter_rows(&result);
    assert_eq!(rows.len(), 2, "one row per document carrying the named counter");
    assert!(rows.iter().all(|row| row.name() == "views"));
    assert_eq!(rows[0].document().as_str(), "users/1");
    assert_eq!(rows[0].value(), 2);
    assert_eq!(rows[1].document().as_str(), "users/2");
    assert_eq!(rows[1].value(), 3);
}

#[test]
fn counter_source_predicate_and_ordering_evaluate_over_entry_fields() {
    let (_dir, engine) = open_engine("counter-predicate");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("users/1", "users", &[]),
            object_document("users/2", "users", &[]),
            object_document("users/3", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            vec![counter_increment("score", 10)],
        );
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/2",
            vec![counter_increment("score", 30)],
        );
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/3",
            vec![counter_increment("score", 20)],
        );
    });

    let spec = QuerySpec::try_new(
        tenant("t"),
        QuerySource::counters_named("score").expect("valid name"),
        QueryPredicate::all(vec![QueryCondition::GreaterOrEqual {
            field: "value".to_owned(),
            value: DocumentValue::Integer(20),
        }])
        .expect("valid predicate"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("value", OrderDirection::Descending).expect("valid key"),
        ])
        .expect("valid ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification");
    let result = execute_family(&engine, "t", spec).expect("predicated counter source executes");
    let rows = counter_rows(&result);
    assert_eq!(rows.len(), 2, "the predicate filters entry rows");
    assert_eq!(rows[0].value(), 30, "ordering descends over the value field");
    assert_eq!(rows[1].value(), 20);
    assert_eq!(result.matched(), 2);
    assert_eq!(result.statistics().scanned(), 3, "scanned counts every entry");
    assert_eq!(result.statistics().skipped(), 1, "skipped counts filtered entries");
}

#[test]
fn counter_source_pages_entry_rows() {
    let (_dir, engine) = open_engine("counter-paging");
    put_documents(
        &engine,
        "t",
        vec![object_document("users/1", "users", &[]), object_document("users/2", "users", &[])],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            vec![counter_increment("a", 1), counter_increment("b", 2), counter_increment("c", 3)],
        );
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/2",
            vec![counter_increment("d", 4)],
        );
    });

    let spec = QuerySpec::try_new(
        tenant("t"),
        QuerySource::counters(),
        QueryPredicate::all(Vec::new()).expect("valid predicate"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(1, Some(2)).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid paged specification");
    let result = execute_family(&engine, "t", spec).expect("paged counter source executes");
    let rows = counter_rows(&result);
    assert_eq!(rows.len(), 2, "the page serves the offset-window rows");
    assert_eq!(rows[0].name(), "b");
    assert_eq!(rows[1].name(), "c");
    assert_eq!(result.matched(), 4, "matched stays the whole entry set");
}

#[test]
fn counter_source_values_match_include_and_direct_read() {
    let (_dir, engine) = open_engine("counter-parity");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("users/1", "users", &[("name", DocumentValue::String("a".into()))]),
            object_document(
                "users/2",
                "users",
                &[
                    ("name", DocumentValue::String("b".into())),
                    ("owner", DocumentValue::String("users/1".into())),
                ],
            ),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            vec![counter_increment("likes", 5)],
        );
    });

    // The query leg: the counter source row value.
    let result = execute_family(&engine, "t", family_spec("t", QuerySource::counters()))
        .expect("counter source executes");
    let rows = counter_rows(&result);
    assert_eq!(rows.len(), 1);
    let queried = rows[0].value();

    // The direct leg: the family seam's own group read.
    let direct = direct_counter_read(&engine, "t", "users/1", Vec::new());
    assert_eq!(direct.find("likes"), Some(Some(queried)), "the direct read agrees");

    // The include leg: a document-source query materializing the same
    // counter through the include contract, over the same reader.
    let include_spec = QuerySpec::try_new(
        tenant("t"),
        QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
            .expect("user collection"),
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("b".into()),
        }])
        .expect("valid predicate"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
    .with_includes(vec![IncludeClause::counters(
        CounterInclude::try_new(IncludePath::field("owner").expect("valid path"), Vec::new())
            .expect("valid counter include"),
    )])
    .expect("valid includes");
    let included = execute_family(&engine, "t", include_spec).expect("include query executes");
    let group = included
        .includes()
        .counters_of(&DocumentId::try_new("users/1").expect("valid identifier"))
        .expect("the include materialized the owner's counters");
    let entry = group.entries().iter().find(|entry| entry.name() == "likes").expect("likes");
    assert_eq!(entry.value(), Some(queried), "query, include, and read agree");
}

// ---------------------------------------------------------------------------
// Time-series source
// ---------------------------------------------------------------------------

#[test]
fn time_series_source_returns_entry_rows_with_series_cursors() {
    let (_dir, engine) = open_engine("series-rows");
    put_documents(
        &engine,
        "t",
        vec![object_document("users/1", "users", &[]), object_document("users/2", "users", &[])],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_series(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            "hr",
            &[(100, 60.0, Some("rest")), (200, 70.0, None), (300, 80.0, None)],
        );
        stage_series(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/2",
            "hr",
            &[(150, 65.0, None)],
        );
        stage_series(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/2",
            "temp",
            &[(120, 36.5, None)],
        );
    });

    let result = execute_family(
        &engine,
        "t",
        family_spec("t", QuerySource::time_series_named("hr").expect("valid name")),
    )
    .expect("series source executes");
    assert!(result.rows().is_empty(), "no document rows for a family source");
    let rows = series_rows(&result);
    assert_eq!(rows.len(), 4, "entries of both hr series, no temp entries");
    let (first, second) = (rows[0], rows[1]);
    assert_eq!(first.document().as_str(), "users/1");
    assert_eq!(first.name(), "hr");
    assert_eq!(first.timestamp(), 100);
    assert_eq!(first.values(), &[60.0]);
    assert_eq!(first.tag(), Some("rest"));
    assert_eq!(second.timestamp(), 200, "entries ascend by timestamp per series");
    assert_eq!(rows[3].document().as_str(), "users/2");
    assert_eq!(rows[3].timestamp(), 150);
    assert_eq!(result.matched(), 4);
    assert_eq!(result.statistics().scanned(), 4);

    // One fully-served cursor per read series, exhausted by the page.
    let cursors = result.series_cursors();
    assert_eq!(cursors.len(), 2, "one cursor per (document, series) read");
    assert!(
        cursors.iter().all(|cursor| cursor.next().is_none()),
        "a fully served series has no next cursor"
    );
    assert_eq!(cursors[0].document().as_str(), "users/1");
    assert_eq!(cursors[0].name(), "hr");
}

#[test]
fn time_series_source_enumerates_every_series_when_unnamed() {
    let (_dir, engine) = open_engine("series-all");
    put_documents(&engine, "t", vec![object_document("users/1", "users", &[])]);
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_series(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            "hr",
            &[(100, 60.0, None)],
        );
        stage_series(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            "temp",
            &[(110, 36.5, None)],
        );
    });

    let result = execute_family(&engine, "t", family_spec("t", QuerySource::time_series()))
        .expect("unnamed series source executes");
    let rows = series_rows(&result);
    assert_eq!(rows.len(), 2);
    assert_eq!(rows.iter().filter(|row| row.name() == "hr").count(), 1);
    assert_eq!(rows.iter().filter(|row| row.name() == "temp").count(), 1);
    assert_eq!(result.series_cursors().len(), 2);
}

#[test]
fn time_series_source_pages_through_the_f04_seam() {
    let (_dir, engine) = open_engine("series-paging");
    put_documents(&engine, "t", vec![object_document("users/1", "users", &[])]);
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_series(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            "hr",
            &[(100, 60.0, None), (200, 70.0, None), (300, 80.0, None), (400, 90.0, None)],
        );
    });

    // First page: two entries per the F04 bound.
    let first = family_spec("t", QuerySource::time_series_named("hr").expect("valid name"))
        .with_time_series_window(
            None,
            Some(TimeSeriesPaging::try_new(None, Some(2)).expect("valid paging")),
        )
        .expect("valid window");
    let page_one = execute_family(&engine, "t", first).expect("first page executes");
    let rows_one = series_rows(&page_one);
    assert_eq!(rows_one.len(), 2, "the F04 limit bounds the series page");
    assert_eq!(rows_one[0].timestamp(), 100);
    assert_eq!(rows_one[1].timestamp(), 200);
    let cursor = page_one.series_cursors()[0].next().expect("more entries follow");
    assert_eq!(cursor, 200, "the cursor is the last served timestamp");
    // matched counts the served rows of this execution; the series' own
    // pre-paging total is the F04 page's total, observed through the
    // direct read below.

    // Second page: resume exactly after the cursor.
    let second = family_spec("t", QuerySource::time_series_named("hr").expect("valid name"))
        .with_time_series_window(
            None,
            Some(TimeSeriesPaging::try_new(Some(cursor), Some(2)).expect("valid paging")),
        )
        .expect("valid window");
    let page_two = execute_family(&engine, "t", second).expect("second page executes");
    let rows_two = series_rows(&page_two);
    assert_eq!(rows_two.len(), 2);
    assert_eq!(rows_two[0].timestamp(), 300, "the cursor serves strictly-after entries");
    assert_eq!(rows_two[1].timestamp(), 400);
    assert!(page_two.series_cursors()[0].next().is_none(), "the addressed view is exhausted");

    // A third page resumes to nothing — pages chain exactly once.
    let third = family_spec("t", QuerySource::time_series_named("hr").expect("valid name"))
        .with_time_series_window(
            None,
            Some(TimeSeriesPaging::try_new(Some(400), None).expect("valid paging")),
        )
        .expect("valid window");
    let page_three = execute_family(&engine, "t", third).expect("third page executes");
    assert_eq!(series_rows(&page_three).len(), 0);

    // The direct F04 read agrees page for page (read parity).
    let direct = direct_series_read(
        &engine,
        "t",
        "users/1",
        "hr",
        Some(TimeSeriesRange::try_new(100, 400).expect("valid range")),
    );
    assert_eq!(direct.total(), 4, "the range matches every entry");
}

#[test]
fn time_series_source_range_window_bounds_the_read() {
    let (_dir, engine) = open_engine("series-range");
    put_documents(&engine, "t", vec![object_document("users/1", "users", &[])]);
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_series(
            store,
            transaction,
            tenant_scope,
            identity,
            "users/1",
            "hr",
            &[(100, 60.0, None), (200, 70.0, None), (300, 80.0, None)],
        );
    });

    let spec = family_spec("t", QuerySource::time_series_named("hr").expect("valid name"))
        .with_time_series_window(
            Some(TimeSeriesRange::try_new(150, 250).expect("valid range")),
            None,
        )
        .expect("valid window");
    let result = execute_family(&engine, "t", spec).expect("windowed source executes");
    let rows = series_rows(&result);
    assert_eq!(rows.len(), 1, "only the in-range entry serves");
    assert_eq!(rows[0].timestamp(), 200);
    assert_eq!(result.statistics().scanned(), 1);
}

// ---------------------------------------------------------------------------
// Fail-closed rules and channel separation
// ---------------------------------------------------------------------------

#[test]
fn family_source_refuses_index_staleness_policy() {
    let (_dir, engine) = open_engine("family-staleness");
    let namespace = super::IndexNamespace::try_new(tenant("t"), "idx/likes").expect("valid");
    let spec = QuerySpec::try_new(
        tenant("t"),
        QuerySource::counters(),
        QueryPredicate::all(Vec::new()).expect("valid predicate"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::AllowStale, vec![namespace]).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification");
    let plan = engine.queries().compile(QueryInput::Typed(spec));
    assert_eq!(plan.err(), Some(QueryError::InvalidSpec), "family sources are index-less");
}

#[test]
fn family_source_refuses_loads_includes_window_and_projection() {
    let (_dir, engine) = open_engine("family-refusals");
    let store = engine.queries();

    // Loads.
    let loads = family_spec("t", QuerySource::counters())
        .with_loads(vec![super::QueryLoad::try_new("owner", "owner").expect("valid load")]);
    assert_eq!(loads.err(), Some(crate::operations::RequestValidationError::InvalidQuery));

    // Includes.
    let includes = family_spec("t", QuerySource::counters()).with_includes(Vec::new());
    assert!(includes.is_ok(), "an empty include list is vacuous");
    let includes =
        family_spec("t", QuerySource::counters()).with_includes(vec![IncludeClause::counters(
            CounterInclude::try_new(IncludePath::field("owner").expect("valid path"), Vec::new())
                .expect("valid counter include"),
        )]);
    assert_eq!(includes.err(), Some(crate::operations::RequestValidationError::InvalidQuery));

    // A time-series window on a counter source.
    let window = family_spec("t", QuerySource::counters())
        .with_time_series_window(Some(TimeSeriesRange::try_new(0, 1).expect("valid range")), None);
    assert!(window.is_err(), "the window belongs to the time-series source");

    // A field projection on a family source.
    let projected = QuerySpec::try_new(
        tenant("t"),
        QuerySource::time_series(),
        QueryPredicate::all(Vec::new()).expect("valid predicate"),
        QueryOrdering::default(),
        QueryProjection::fields(&["value"]).expect("valid projection"),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification");
    assert_eq!(
        store.compile(QueryInput::Typed(projected)).err(),
        Some(QueryError::InvalidSpec),
        "the typed row is the family projection vocabulary"
    );
}

#[test]
fn document_queries_keep_document_rows_and_empty_family_channel() {
    let (_dir, engine) = open_engine("channel-separation");
    put_documents(
        &engine,
        "t",
        vec![object_document("users/1", "users", &[("k", DocumentValue::Integer(1))])],
    );
    let result = execute_family(
        &engine,
        "t",
        family_spec(
            "t",
            QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
                .expect("user collection"),
        ),
    )
    .expect("document query executes");
    assert_eq!(result.rows().len(), 1);
    assert!(result.family_rows().is_empty());
    assert!(result.series_cursors().is_empty());
}

// ---------------------------------------------------------------------------
// Source vocabulary
// ---------------------------------------------------------------------------

#[test]
fn source_constructors_validate_names() {
    assert_eq!(QuerySource::counters().kind(), QuerySourceKind::Counters);
    assert_eq!(QuerySource::time_series().kind(), QuerySourceKind::TimeSeries);
    assert_eq!(
        QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
            .expect("user collection")
            .kind(),
        QuerySourceKind::Collection
    );
    assert!(QuerySource::counters_named("likes").is_ok());
    // The shared name rule: no padding, no control characters, non-empty.
    assert!(QuerySource::counters_named(" padded ").is_err());
    assert!(QuerySource::counters_named("").is_err());
    assert!(QuerySource::time_series_named("hr").is_ok());
    assert!(
        QuerySource::time_series_named("bad/name").is_err(),
        "series names reject the reserved record separator"
    );
    assert_eq!(
        QuerySource::counters_named("likes").expect("valid name").counter_name(),
        Some("likes")
    );
    assert_eq!(QuerySource::time_series_named("hr").expect("valid name").series_name(), Some("hr"));
    assert!(QuerySource::counters().collection().is_none());
    assert_eq!(
        QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
            .expect("user collection")
            .collection()
            .map(CollectionName::as_str),
        Some("users")
    );
}
