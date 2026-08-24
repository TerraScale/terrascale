//! Public-path query integration tests.
//!
//! These tests reach only what an external caller can construct: the typed
//! clause values, the input wrappers, and the borrowed query store acquired
//! from a durable Engine. `TenantScope` has no public constructor by design
//! (Platform creates it after authorization), so `ReadSnapshot` values and
//! execution behavior are covered by the in-crate tests instead. The
//! V1-compatible wrapper `V1QueryText` has no implemented public constructor
//! yet; that wiring is the integrator action recorded in the design record.

use std::{path::PathBuf, time::Duration};

use terrabase_v2::{
    Cancellation, Deadline, Engine, EngineConfig, QueryInput, RequestValidationError, TenantId,
    query::{
        IndexNamespace, OrderDirection, OrderingKey, QueryCondition, QueryError, QueryOrdering,
        QueryPaging, QueryPredicate, QueryProjection, QuerySource, QuerySpec, QueryStore,
        StalenessMode, StalenessPolicy,
    },
    types::{CollectionName, DocumentValue},
};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-w32-integration-query-{tag}-{}", std::process::id()))
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
        source("users"),
        QueryPredicate::all(vec![
            QueryCondition::Equals {
                field: "name".to_owned(),
                value: DocumentValue::String("ada".to_owned()),
            },
            QueryCondition::Exists { field: "age".to_owned() },
        ])
        .expect("valid conjunction"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("age", OrderDirection::Descending).expect("valid key"),
        ])
        .expect("valid ordering"),
        QueryProjection::fields(&["name", "age"]).expect("valid projection"),
        QueryPaging::try_new(0, Some(10)).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

#[test]
fn typed_clauses_validate_and_expose_read_only_parts() {
    let query = spec();
    assert_eq!(query.tenant_claim(), &tenant("tenant-a"));
    assert_eq!(query.source().collection().as_str(), "users");
    assert_eq!(query.predicate().conditions().len(), 2);
    assert_eq!(query.predicate().conditions()[0].field(), "name");
    assert_eq!(query.ordering().keys()[0].field(), "age");
    assert_eq!(query.ordering().keys()[0].direction(), OrderDirection::Descending);
    assert_eq!(query.projection().selected(), Some(&["name".to_owned(), "age".to_owned()][..]));
    assert_eq!(query.paging().offset(), 0);
    assert_eq!(query.paging().limit(), Some(10));
    assert_eq!(query.staleness().mode(), StalenessMode::Strict);
    assert!(query.staleness().indexes().is_empty());
    assert!(!query.deadline().is_expired());
    assert!(!query.cancellation().is_cancelled());

    let namespace = IndexNamespace::try_new(tenant("tenant-a"), "by-age").expect("valid namespace");
    assert_eq!(namespace.tenant(), &tenant("tenant-a"));
    assert_eq!(namespace.name(), "by-age");
    assert_eq!(
        IndexNamespace::try_new(tenant("tenant-a"), " by-age").unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}

#[test]
fn typed_clause_bounds_reject_invalid_input() {
    assert_eq!(
        QuerySource::try_new(CollectionName::try_new("terrabase.families").expect("text"))
            .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(
        QueryPredicate::all(vec![QueryCondition::Missing { field: String::new() }]).unwrap_err(),
        RequestValidationError::EmptyField
    );
    assert_eq!(
        OrderingKey::try_new("", OrderDirection::Ascending).unwrap_err(),
        RequestValidationError::EmptyField
    );
    assert_eq!(
        QueryProjection::fields(&["a", "a"]).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(QueryPaging::try_new(0, Some(0)).unwrap_err(), RequestValidationError::InvalidQuery);
    assert_eq!(
        StalenessPolicy::try_new(StalenessMode::AllowStale, Vec::new()).unwrap_err(),
        RequestValidationError::InvalidQuery,
        "an allow-stale policy must name at least one index namespace"
    );
}

#[test]
fn expired_or_cancelled_requests_are_rejected_at_construction() {
    let expired = Deadline::after(Duration::from_nanos(1)).expect("positive duration");
    while !expired.is_expired() {
        std::hint::spin_loop();
    }
    let cancelled = Cancellation::new();
    cancelled.cancel();
    let base = || {
        QuerySpec::try_new(
            tenant("tenant-a"),
            source("users"),
            QueryPredicate::all(Vec::new()).expect("valid conjunction"),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            QueryPaging::try_new(0, None).expect("valid paging"),
            StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid specification")
    };
    let query = base();
    let replaced = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        expired,
        Cancellation::new(),
    );
    assert_eq!(replaced.unwrap_err(), RequestValidationError::InvalidDeadline);
    let _ = query;
    let cancelled_spec = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        cancelled,
    );
    assert_eq!(cancelled_spec.unwrap_err(), RequestValidationError::InvalidCancellation);
}

#[test]
fn compile_produces_an_immutable_plan_from_public_input() {
    let (dir, engine) = open_engine("compile");
    let store = engine.queries();
    let plan = store.compile(QueryInput::typed(spec())).expect("compiles");
    assert_eq!(plan.tenant_claim(), Some(&tenant("tenant-a")));
    assert_eq!(plan.source().collection().as_str(), "users");
    assert_eq!(plan.ordering().keys().len(), 1);
    // A plan is a value: its clone observes the same clauses. Full structural
    // equality additionally compares the deadline instant and the
    // cancellation signal identity, so two separately built specifications
    // are not expected to compare equal.
    let copy = plan.clone();
    assert_eq!(copy.tenant_claim(), plan.tenant_claim());
    assert_eq!(copy.source().collection(), plan.source().collection());
    assert_eq!(copy.ordering().keys(), plan.ordering().keys());
    // A specification requiring another tenant's index namespace is a wrong
    // tenant scope and is rejected before a plan exists.
    let wrong_scope = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(
            StalenessMode::Strict,
            vec![IndexNamespace::try_new(tenant("tenant-b"), "by-age").expect("valid")],
        )
        .expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification");
    assert_eq!(
        store.compile(QueryInput::typed(wrong_scope)).unwrap_err(),
        QueryError::TenantMismatch
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_store_is_acquired_only_from_a_live_engine() {
    fn acquired<'engine>(engine: &'engine Engine) -> QueryStore<'engine> {
        engine.queries()
    }
    let (dir, engine) = open_engine("acquire");
    let store = acquired(&engine);
    assert!(store.compile(QueryInput::typed(spec())).is_ok());
    drop(store);
    engine.shutdown().expect("shuts down once the borrow ends");
    std::fs::remove_dir_all(&dir).ok();
}
