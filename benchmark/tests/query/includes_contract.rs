//! Public-path includes contract tests (GAP-Q07, PB-4016).
//!
//! These tests reach only what an external caller can construct: the typed
//! include clause values, the `with_includes` binding on `QuerySpec`, the
//! frozen clause list on `QueryPlan`, and the empty set a result carries
//! when no clause was bound. `TenantScope` has no public constructor by
//! design (Platform creates it after authorization), so snapshot-bound
//! materialization, the cycle and bound refusals, and the stream/page
//! equivalence proof are covered by the in-crate tests in
//! `src/query/includes_tests.rs`.

use std::{path::PathBuf, time::Duration};

use terrabase::{
    Cancellation, Deadline, Engine, EngineConfig, QueryInput, RequestValidationError, TenantId,
    query::{
        CounterInclude, DocumentInclude, IncludeClause, IncludePath, MAX_INCLUDE_COUNTER_NAMES,
        MAX_INCLUDE_DEPTH, MAX_INCLUDE_PATH_SEGMENTS, MAX_INCLUDES, QueryOrdering, QueryPaging,
        QueryPredicate, QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
        TimeSeriesInclude,
    },
    types::CollectionName,
};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir()
        .join(format!("terrabase-q07-includes-integration-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn source(collection: &str) -> QuerySource {
    QuerySource::try_new(CollectionName::try_new(collection).expect("valid identifier"))
        .expect("user collection")
}

fn spec() -> QuerySpec {
    QuerySpec::try_new(
        tenant("tenant-a"),
        source("orders"),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn path(text: &str) -> IncludePath {
    IncludePath::try_new(text).expect("valid path")
}

#[test]
fn include_paths_validate_their_segments_and_length() {
    let single = IncludePath::field("owner").expect("valid single segment");
    assert_eq!(single.segments(), &["owner".to_owned()]);

    let dotted = path("shipping.carrier.ref");
    assert_eq!(dotted.segments().len(), 3);

    assert_eq!(IncludePath::try_new("").unwrap_err(), RequestValidationError::EmptyField);
    assert_eq!(IncludePath::try_new("owner..ref").unwrap_err(), RequestValidationError::EmptyField);
    assert_eq!(IncludePath::try_new(" owner").unwrap_err(), RequestValidationError::InvalidQuery);

    // One segment more than the bound refuses.
    let mut over = String::new();
    for index in 0..=MAX_INCLUDE_PATH_SEGMENTS {
        if index > 0 {
            over.push('.');
        }
        over.push('s');
    }
    assert_eq!(IncludePath::try_new(&over).unwrap_err(), RequestValidationError::InvalidQuery);
}

#[test]
fn document_includes_validate_depth_and_prefix() {
    let base = DocumentInclude::try_new(path("owner"), 1).expect("valid include");
    assert_eq!(base.depth(), 1);
    assert_eq!(base.id_prefix(), None);
    assert_eq!(base.path().segments(), &["owner".to_owned()]);

    assert_eq!(
        DocumentInclude::try_new(path("owner"), 0).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(
        DocumentInclude::try_new(path("owner"), MAX_INCLUDE_DEPTH + 1).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    // The bound itself is accepted: the limit is inclusive, not one below it.
    assert!(DocumentInclude::try_new(path("owner"), MAX_INCLUDE_DEPTH).is_ok());

    let prefixed = base.clone().with_id_prefix("users/").expect("valid prefix");
    assert_eq!(prefixed.id_prefix(), Some("users/"));
    assert_eq!(base.with_id_prefix(" users/").unwrap_err(), RequestValidationError::InvalidQuery);
}

#[test]
fn counter_includes_validate_their_name_list() {
    let named =
        CounterInclude::try_new(path("owner"), vec!["views".to_owned(), "clicks".to_owned()])
            .expect("valid counter include");
    assert_eq!(named.names().len(), 2);
    let whole = CounterInclude::try_new(path("owner"), Vec::new()).expect("whole group");
    assert!(whole.names().is_empty());

    assert_eq!(
        CounterInclude::try_new(path("owner"), vec!["".to_owned()]).unwrap_err(),
        RequestValidationError::EmptyField
    );
    assert_eq!(
        CounterInclude::try_new(path("owner"), vec!["views".to_owned(), "views".to_owned()])
            .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    let too_many: Vec<String> =
        (0..=MAX_INCLUDE_COUNTER_NAMES).map(|index| format!("c{index}")).collect();
    assert_eq!(
        CounterInclude::try_new(path("owner"), too_many).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}

#[test]
fn time_series_includes_validate_their_series_name() {
    let series = TimeSeriesInclude::try_new(path("owner"), "cpu", None).expect("valid series");
    assert_eq!(series.name(), "cpu");
    assert_eq!(series.range(), None);
    let ranged = TimeSeriesInclude::try_new(
        path("owner"),
        "cpu",
        Some(terrabase::families::TimeSeriesRange::try_new(10, 20).expect("valid range")),
    )
    .expect("valid ranged series");
    assert_eq!(ranged.range().map(|range| (range.start(), range.end())), Some((10, 20)));

    assert_eq!(
        TimeSeriesInclude::try_new(path("owner"), "", None).unwrap_err(),
        RequestValidationError::EmptyField
    );
}

#[test]
fn a_specification_binds_and_exposes_its_include_clauses() {
    let bound = spec()
        .with_includes(vec![
            IncludeClause::documents(DocumentInclude::try_new(path("owner"), 1).expect("valid")),
            IncludeClause::counters(
                CounterInclude::try_new(path("owner"), vec!["views".to_owned()]).expect("valid"),
            ),
            IncludeClause::time_series(
                TimeSeriesInclude::try_new(path("owner"), "cpu", None).expect("valid"),
            ),
        ])
        .expect("valid includes");
    assert_eq!(bound.includes().len(), 3);
    // Declaration order is preserved, and every clause exposes its path.
    assert_eq!(bound.includes()[0].path().segments(), &["owner".to_owned()]);
    assert_eq!(bound.includes()[1].path().segments(), &["owner".to_owned()]);
    assert_eq!(bound.includes()[2].path().segments(), &["owner".to_owned()]);

    // A specification without includes binds an empty list.
    assert!(spec().includes().is_empty());
}

#[test]
fn a_specification_refuses_an_over_long_include_list() {
    let clauses: Vec<IncludeClause> = (0..=MAX_INCLUDES)
        .map(|_| IncludeClause::documents(DocumentInclude::try_new(path("owner"), 1).expect("ok")))
        .collect();
    assert_eq!(spec().with_includes(clauses).unwrap_err(), RequestValidationError::InvalidQuery);
    // The bound itself is accepted.
    let at_bound: Vec<IncludeClause> = (0..MAX_INCLUDES)
        .map(|_| IncludeClause::documents(DocumentInclude::try_new(path("owner"), 1).expect("ok")))
        .collect();
    assert!(spec().with_includes(at_bound).is_ok());
}

#[test]
fn a_compiled_plan_carries_the_frozen_include_clause_list() {
    let (dir, engine) = open_engine("plan-clauses");
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::Typed(
            spec()
                .with_includes(vec![
                    IncludeClause::documents(
                        DocumentInclude::try_new(path("owner"), 2).expect("valid"),
                    ),
                    IncludeClause::time_series(
                        TimeSeriesInclude::try_new(path("owner"), "cpu", None).expect("valid"),
                    ),
                ])
                .expect("valid includes"),
        ))
        .expect("compiles");
    assert_eq!(plan.includes().len(), 2);
    // The depth and the series name survive compilation untouched.
    match &plan.includes()[0] {
        IncludeClause::Documents(document) => assert_eq!(document.depth(), 2),
        other => panic!("first clause is not a document include: {other:?}"),
    }
    match &plan.includes()[1] {
        IncludeClause::TimeSeries(series) => assert_eq!(series.name(), "cpu"),
        other => panic!("second clause is not a series include: {other:?}"),
    }

    // A plan without includes carries an empty list, and the empty set a
    // result would report is constructible and empty.
    let plain = store.compile(QueryInput::Typed(spec())).expect("compiles");
    assert!(plain.includes().is_empty());
    assert!(terrabase::query::IncludeSet::default().is_empty());

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn a_query_store_borrow_keeps_the_engine_alive_while_compiling() {
    let (dir, engine) = open_engine("borrow");
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::Typed(
            spec()
                .with_includes(vec![IncludeClause::documents(
                    DocumentInclude::try_new(path("owner"), 1).expect("valid"),
                )])
                .expect("valid includes"),
        ))
        .expect("compiles");
    assert_eq!(plan.includes().len(), 1);
    drop(store);
    engine.shutdown().expect("shuts down");

    let _ = std::fs::remove_dir_all(dir);
}
