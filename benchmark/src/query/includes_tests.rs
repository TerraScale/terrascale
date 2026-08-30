//! In-crate tests for the includes contract (GAP-Q07, PB-4016).
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. Counter and time-series fixtures are
//! staged through the family seam's own write path and read back only
//! through the include contract, so the tests never inspect family state
//! directly. No V1 parity claim is made beyond the pinned citations in
//! `docs/query/gapq07-includes-contract.md`, and no Recovery,
//! Replication, or Benchmark receipt is created.

#![allow(clippy::too_many_lines)]

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use super::{
    CounterInclude, DocumentInclude, IncludeClause, IncludePath, IncludeSet, QueryError,
    QueryOrdering, QueryPaging, QueryPredicate, QueryProjection, QuerySource, QuerySpec,
    StalenessMode, StalenessPolicy, TimeSeriesInclude,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    families::{
        CounterOperation, FamilyReadContext, FamilyReadRequest, FamilyWriteRequest,
        TimeSeriesAppend, TimeSeriesBatchWrite,
    },
    operations::RequestValidationError,
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
    std::env::temp_dir()
        .join(format!("terrabase-q07-includes-{tag}-{}-{index}", std::process::id()))
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

fn strings(values: &[&str]) -> DocumentValue {
    DocumentValue::Array(
        values.iter().map(|value| DocumentValue::String((*value).to_owned())).collect(),
    )
}

fn string_values(values: &[String]) -> DocumentValue {
    DocumentValue::Array(values.iter().map(|value| DocumentValue::String(value.clone())).collect())
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
/// same staging shape the family seam's own tests use.
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
    let identity = unique_operation("q07");
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

fn stage_series<'engine>(
    store: &crate::families::FamilyStore<'engine>,
    transaction: &mut crate::engine::Transaction<'engine>,
    tenant_scope: &TenantScope,
    identity: &OperationId,
    document: &str,
    name: &str,
    timestamps: &[i64],
) {
    let appends = timestamps
        .iter()
        .map(|timestamp| {
            // The fixture timestamps are small; the remainder-derived value
            // keeps the entries distinct without a truncating cast.
            let value = i32::try_from(*timestamp % 97).expect("fixture timestamp fits an i32");
            TimeSeriesAppend::new(*timestamp, vec![f64::from(value)], None)
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

fn simple_spec(claim: &str, collection: &str) -> QuerySpec {
    QuerySpec::try_new(
        tenant(claim),
        QuerySource::try_new(CollectionName::try_new(collection).expect("valid identifier"))
            .expect("valid source"),
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

fn paged_spec(claim: &str, collection: &str, offset: u64, limit: Option<u32>) -> QuerySpec {
    QuerySpec::try_new(
        tenant(claim),
        QuerySource::try_new(CollectionName::try_new(collection).expect("valid identifier"))
            .expect("valid source"),
        QueryPredicate::all(Vec::new()).expect("valid predicate"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(offset, limit).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn execute_with_includes(
    engine: &Engine,
    tenant_name: &str,
    collection: &str,
    clauses: Vec<IncludeClause>,
) -> Result<super::QueryResult, QueryError> {
    let spec = simple_spec(tenant_name, collection).with_includes(clauses).expect("valid includes");
    let store = engine.queries();
    let plan = store.compile(crate::operations::QueryInput::Typed(spec)).expect("compiles");
    let snapshot = engine.read_snapshot(&scope(engine, tenant_name)).expect("snapshot");
    store.execute(&plan, &snapshot)
}

fn document_include(path: &str, depth: u8) -> DocumentInclude {
    DocumentInclude::try_new(IncludePath::try_new(path).expect("valid path"), depth)
        .expect("valid document include")
}

fn counter_include(path: &str, names: &[&str]) -> CounterInclude {
    CounterInclude::try_new(
        IncludePath::try_new(path).expect("valid path"),
        names.iter().map(|name| (*name).to_owned()).collect(),
    )
    .expect("valid counter include")
}

fn include_document_ids(includes: &IncludeSet) -> Vec<String> {
    includes.documents().iter().map(|document| document.id().as_str().to_owned()).collect()
}

// ---------------------------------------------------------------------------
// Document includes
// ---------------------------------------------------------------------------

#[test]
fn document_includes_materialize_referenced_documents_from_result_rows() {
    let (dir, engine) = open_engine("doc-rows");
    put_documents(
        &engine,
        "t",
        vec![
            object_document(
                "order/1",
                "orders",
                &[("owner", DocumentValue::String("user/a".into()))],
            ),
            object_document("user/a", "users", &[("name", DocumentValue::String("Ada".into()))]),
            object_document("user/b", "users", &[("name", DocumentValue::String("Bo".into()))]),
        ],
    );
    let result = execute_with_includes(
        &engine,
        "t",
        "orders",
        vec![IncludeClause::documents(document_include("owner", 1))],
    )
    .expect("executes");
    assert_eq!(include_document_ids(result.includes()), vec!["user/a".to_owned()]);
    assert!(result.includes().find_document("user/a").is_some());
    // The include set is carried beside the rows, never merged into them.
    assert_eq!(result.rows().len(), 1);
    assert_eq!(result.rows()[0].document().id().as_str(), "order/1");

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_walk_nested_paths_and_flatten_arrays() {
    let (dir, engine) = open_engine("doc-walk");
    put_documents(
        &engine,
        "t",
        vec![
            object_document(
                "post/1",
                "posts",
                &[
                    ("author", object(&[("ref", DocumentValue::String("user/a".into()))])),
                    ("tags", strings(&["user/b", "user/c"])),
                    ("count", DocumentValue::Integer(7)),
                ],
            ),
            object_document("user/a", "users", &[]),
            object_document("user/b", "users", &[]),
            object_document("user/c", "users", &[]),
        ],
    );
    let result = execute_with_includes(
        &engine,
        "t",
        "posts",
        vec![
            IncludeClause::documents(document_include("author.ref", 1)),
            IncludeClause::documents(document_include("tags", 1)),
        ],
    )
    .expect("executes");
    assert_eq!(
        include_document_ids(result.includes()),
        vec!["user/a".to_owned(), "user/b".to_owned(), "user/c".to_owned()]
    );

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_accept_an_integer_leaf_as_a_reference() {
    let (dir, engine) = open_engine("doc-int");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("ref", DocumentValue::Integer(42))]),
            // The invariant decimal spelling of the integer leaf.
            object_document("42", "users", &[]),
        ],
    );
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("ref", 1))],
    )
    .expect("executes");
    assert_eq!(include_document_ids(result.includes()), vec!["42".to_owned()]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_prefix_form_gathers_the_bare_reference_alongside() {
    let (dir, engine) = open_engine("doc-prefix");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("ref", DocumentValue::String("a".into()))]),
            object_document("users/a", "users", &[]),
            object_document("a", "users", &[]),
        ],
    );
    let prefixed = document_include("ref", 1).with_id_prefix("users/").expect("valid prefix");
    let result =
        execute_with_includes(&engine, "t", "rows", vec![IncludeClause::documents(prefixed)])
            .expect("executes");
    // V1 loads the transformed reference and the original string reference,
    // so a caller relying on either keeps its answer.
    assert_eq!(include_document_ids(result.includes()), vec!["a".to_owned(), "users/a".to_owned()]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_omit_unresolved_references_and_the_rows_own_document() {
    let (dir, engine) = open_engine("doc-missing");
    put_documents(
        &engine,
        "t",
        vec![
            object_document(
                "row/1",
                "rows",
                &[
                    ("absent", DocumentValue::String("nope".into())),
                    ("self", DocumentValue::String("row/1".into())),
                    ("ignored", DocumentValue::Boolean(true)),
                ],
            ),
            object_document("row/2", "rows", &[("self", DocumentValue::String("row/1".into()))]),
        ],
    );
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("absent", 1))],
    )
    .expect("executes");
    assert!(result.includes().documents().is_empty());

    // A row's own document is never re-materialized as an include of
    // itself, and a reference from another row to it is not either: the
    // ignore set is every row of this result.
    let self_reference = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("self", 1))],
    )
    .expect("executes");
    assert!(self_reference.includes().documents().is_empty());

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_are_deterministic_and_ordered_by_identifier() {
    let (dir, engine) = open_engine("doc-order");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("refs", strings(&["c", "a", "b"]))]),
            object_document("a", "users", &[]),
            object_document("b", "users", &[]),
            object_document("c", "users", &[]),
        ],
    );
    let clauses = vec![IncludeClause::documents(document_include("refs", 1))];
    let first = execute_with_includes(&engine, "t", "rows", clauses.clone()).expect("executes");
    let second = execute_with_includes(&engine, "t", "rows", clauses).expect("executes");
    assert_eq!(include_document_ids(first.includes()), vec!["a", "b", "c"]);
    assert_eq!(first.includes(), second.includes());

    let _ = std::fs::remove_dir_all(dir);
}

// ---------------------------------------------------------------------------
// Chaining, cycles, bounds
// ---------------------------------------------------------------------------

#[test]
fn document_includes_chain_to_the_declared_depth() {
    let (dir, engine) = open_engine("chain");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("next", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[("next", DocumentValue::String("b".into()))]),
            object_document("b", "users", &[("next", DocumentValue::String("c".into()))]),
            object_document("c", "users", &[("next", DocumentValue::String("gone".into()))]),
        ],
    );
    // Depth 1 is V1 parity: only the rows' own references.
    let one = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 1))],
    )
    .expect("executes");
    assert_eq!(include_document_ids(one.includes()), vec!["a".to_owned()]);

    let two = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 2))],
    )
    .expect("executes");
    assert_eq!(include_document_ids(two.includes()), vec!["a".to_owned(), "b".to_owned()]);

    let three = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 3))],
    )
    .expect("executes");
    // The fourth reference names no document, so depth 3 still stops there.
    assert_eq!(include_document_ids(three.includes()), vec!["a", "b", "c"]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_refuse_a_reference_cycle_as_a_typed_error() {
    let (dir, engine) = open_engine("cycle");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("next", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[("next", DocumentValue::String("b".into()))]),
            object_document("b", "users", &[("next", DocumentValue::String("a".into()))]),
        ],
    );
    // A cycle two steps long needs the walk to leave `a` and come back,
    // which depth 2 cannot do: `a` is gathered, then expanded once.
    let shallow = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 2))],
    )
    .expect("executes");
    assert_eq!(include_document_ids(shallow.includes()), vec!["a".to_owned(), "b".to_owned()]);

    // At depth 3 the closing edge is traversed and the graph carries the
    // cycle, so the query refuses instead of following it.
    let refused = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 3))],
    )
    .expect_err("a cycle must refuse");
    assert_eq!(refused, QueryError::IncludeCycle);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_refuse_a_self_reference_cycle() {
    // A self-reference is a one-step cycle and refuses at depth 2, the
    // first depth that expands the referenced document.
    let (dir, engine) = open_engine("self-cycle");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("next", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[("next", DocumentValue::String("a".into()))]),
        ],
    );
    let refused = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 2))],
    )
    .expect_err("a self reference must refuse");
    assert_eq!(refused, QueryError::IncludeCycle);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_do_not_refuse_a_diamond() {
    let (dir, engine) = open_engine("diamond");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("refs", strings(&["b", "c"]))]),
            object_document("b", "mids", &[("next", DocumentValue::String("d".into()))]),
            object_document("c", "mids", &[("next", DocumentValue::String("d".into()))]),
            object_document("d", "leafs", &[]),
        ],
    );
    // `d` is reached from both `b` and `c`, but nothing reaches back: a
    // diamond is a shared target, not a cycle, and deduplication already
    // handles it.
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![
            IncludeClause::documents(document_include("refs", 1)),
            IncludeClause::documents(document_include("next", 2)),
        ],
    )
    .expect("no cycle here");
    assert_eq!(include_document_ids(result.includes()), vec!["b", "c", "d"]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_refuse_past_the_include_document_bound() {
    let (dir, engine) = open_engine("bound");
    // One more distinct referenced document than the bound allows, so the
    // include set cannot be materialized without refusing.
    let bound = super::includes::MAX_INCLUDE_DOCUMENTS;
    let references: Vec<String> = (0..=bound).map(|index| format!("x{index:04}")).collect();
    let mut documents =
        vec![object_document("row/1", "rows", &[("refs", string_values(&references))])];
    for reference in &references {
        documents.push(object_document(reference, "users", &[]));
    }
    put_documents(&engine, "t", documents);
    let refused = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("refs", 1))],
    )
    .expect_err("the include set cannot grow without bound");
    assert_eq!(refused, QueryError::IncludeBoundExceeded);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_accept_the_include_document_bound_exactly() {
    let (dir, engine) = open_engine("bound-at");
    // Exactly the bound's worth of distinct referenced documents: the set
    // materializes — the refusal fires only past the bound, never at it.
    let bound = super::includes::MAX_INCLUDE_DOCUMENTS;
    let references: Vec<String> = (0..bound).map(|index| format!("y{index:04}")).collect();
    let mut documents =
        vec![object_document("row/1", "rows", &[("refs", string_values(&references))])];
    for reference in &references {
        documents.push(object_document(reference, "users", &[]));
    }
    put_documents(&engine, "t", documents);
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("refs", 1))],
    )
    .expect("the include set materializes at the bound");
    assert_eq!(result.includes().documents().len(), bound);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_truncate_a_cycle_that_closes_through_a_result_row() {
    let (dir, engine) = open_engine("row-cycle");
    // The walk leaves the row, reaches `a`, and `a` points back at the
    // row itself. A row is a level-0 gather source, not a gather target —
    // the V1 `AddToIgnore` truncation this contract carries — so the
    // back edge is not walked and the query answers instead of refusing.
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("next", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[("next", DocumentValue::String("row/1".into()))]),
        ],
    );
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 2))],
    )
    .expect("a cycle closing through a row truncates, it does not refuse");
    assert_eq!(include_document_ids(result.includes()), vec!["a".to_owned()]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn document_includes_materialize_only_the_querying_tenants_documents() {
    let (dir, engine) = open_engine("tenant-split");
    // The same document identifiers exist in two tenants with
    // distinguishing content. Tenant `t` queries; its include set must
    // carry tenant `t`'s material only, at every depth of the walk — the
    // chain resolves through this tenant's `b`, never the other tenant's.
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("next", DocumentValue::String("a".into()))]),
            object_document(
                "a",
                "users",
                &[
                    ("mark", DocumentValue::String("tenant-t".into())),
                    ("next", DocumentValue::String("b".into())),
                ],
            ),
            object_document(
                "b",
                "users",
                &[("mark", DocumentValue::String("tenant-t-deep".into()))],
            ),
        ],
    );
    put_documents(
        &engine,
        "u",
        vec![
            object_document("row/1", "rows", &[("next", DocumentValue::String("a".into()))]),
            object_document(
                "a",
                "users",
                &[
                    ("mark", DocumentValue::String("tenant-u".into())),
                    ("next", DocumentValue::String("b".into())),
                ],
            ),
            object_document(
                "b",
                "users",
                &[("mark", DocumentValue::String("tenant-u-deep".into()))],
            ),
        ],
    );
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::documents(document_include("next", 2))],
    )
    .expect("executes");
    assert_eq!(include_document_ids(result.includes()), vec!["a".to_owned(), "b".to_owned()]);
    let marks: Vec<String> = result
        .includes()
        .documents()
        .iter()
        .map(|document| match document.value() {
            DocumentValue::Object(fields) => match fields.get("mark") {
                Some(DocumentValue::String(mark)) => mark.as_str().to_owned(),
                _ => String::new(),
            },
            _ => String::new(),
        })
        .collect();
    assert_eq!(marks, vec!["tenant-t".to_owned(), "tenant-t-deep".to_owned()]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn counter_includes_read_named_counters_through_the_family_seam() {
    let (dir, engine) = open_engine("counters");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("row/2", "rows", &[("owner", DocumentValue::String("b".into()))]),
            object_document("a", "users", &[]),
            object_document("b", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "a",
            vec![
                CounterOperation::Increment { name: "views".into(), delta: 3 },
                CounterOperation::Increment { name: "clicks".into(), delta: 5 },
                CounterOperation::Increment { name: "hidden".into(), delta: 9 },
            ],
        );
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "b",
            vec![CounterOperation::Increment { name: "views".into(), delta: 11 }],
        );
    });

    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::counters(counter_include("owner", &["views", "clicks"]))],
    )
    .expect("executes");
    let groups = result.includes().counters();
    assert_eq!(groups.len(), 2);
    assert_eq!(groups[0].document().as_str(), "a");
    assert_eq!(groups[1].document().as_str(), "b");
    let names: Vec<(String, Option<i64>)> =
        groups[0].entries().iter().map(|entry| (entry.name().to_owned(), entry.value())).collect();
    // Only the requested names, ordered by name.
    assert_eq!(names, vec![("clicks".to_owned(), Some(5)), ("views".to_owned(), Some(3))]);
    // The include reports the family reader's own answer for the requested
    // names: a name the target does not carry stays a present entry with an
    // absent value, observable rather than silently dropped.
    let b_names: Vec<(String, Option<i64>)> =
        groups[1].entries().iter().map(|entry| (entry.name().to_owned(), entry.value())).collect();
    assert_eq!(b_names, vec![("clicks".to_owned(), None), ("views".to_owned(), Some(11))]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn counter_includes_read_the_whole_group_for_an_empty_name_list() {
    let (dir, engine) = open_engine("counters-all");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "a",
            vec![
                CounterOperation::Increment { name: "alpha".into(), delta: 1 },
                CounterOperation::Increment { name: "beta".into(), delta: 2 },
            ],
        );
    });
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::counters(counter_include("owner", &[]))],
    )
    .expect("executes");
    let groups = result.includes().counters();
    assert_eq!(groups.len(), 1);
    let names: Vec<&str> =
        groups[0].entries().iter().map(crate::families::CounterEntry::name).collect();
    assert_eq!(names, vec!["alpha", "beta"]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn counter_includes_skip_a_target_with_no_document_and_dedup_targets() {
    let (dir, engine) = open_engine("counters-skip");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owners", strings(&["a", "a", "gone", "b"]))]),
            object_document("a", "users", &[]),
            object_document("b", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "a",
            vec![CounterOperation::Increment { name: "views".into(), delta: 4 }],
        );
    });
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::counters(counter_include("owners", &["views"]))],
    )
    .expect("executes");
    let groups = result.includes().counters();
    // `a` twice gathers once, `gone` resolves to nothing and contributes no
    // group, and `b` has no counters staged so it reports an absent value.
    assert_eq!(groups.len(), 2);
    assert_eq!(groups[0].document().as_str(), "a");
    assert_eq!(groups[0].entries()[0].value(), Some(4));
    assert_eq!(groups[1].document().as_str(), "b");
    assert_eq!(groups[1].entries()[0].value(), None);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn time_series_includes_read_series_ranges_through_the_family_seam() {
    let (dir, engine) = open_engine("series");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_series(store, transaction, tenant_scope, identity, "a", "cpu", &[10, 20, 30, 40]);
        stage_series(store, transaction, tenant_scope, identity, "a", "mem", &[15, 25]);
    });
    let whole = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::time_series(
            TimeSeriesInclude::try_new(
                IncludePath::try_new("owner").expect("valid path"),
                "cpu",
                None,
            )
            .expect("valid series include"),
        )],
    )
    .expect("executes");
    let series = whole.includes().time_series();
    assert_eq!(series.len(), 1);
    assert_eq!(series[0].document().as_str(), "a");
    assert_eq!(series[0].name(), "cpu");
    assert_eq!(series[0].page().entries().len(), 4);
    assert_eq!(series[0].page().entries()[0].timestamp(), 10);

    let ranged = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![IncludeClause::time_series(
            TimeSeriesInclude::try_new(
                IncludePath::try_new("owner").expect("valid path"),
                "cpu",
                Some(crate::families::TimeSeriesRange::try_new(20, 30).expect("valid range")),
            )
            .expect("valid series include"),
        )],
    )
    .expect("executes");
    let ranged_series = ranged.includes().time_series();
    assert_eq!(ranged_series.len(), 1);
    let timestamps: Vec<i64> = ranged_series[0]
        .page()
        .entries()
        .iter()
        .map(crate::families::TimeSeriesEntry::timestamp)
        .collect();
    assert_eq!(timestamps, vec![20, 30]);
    assert_eq!(ranged_series[0].range().map(|range| (range.start(), range.end())), Some((20, 30)));

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn time_series_includes_keep_distinct_ranges_of_one_series_separate() {
    let (dir, engine) = open_engine("series-ranges");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_series(store, transaction, tenant_scope, identity, "a", "cpu", &[10, 20, 30, 40]);
    });
    let result = execute_with_includes(
        &engine,
        "t",
        "rows",
        vec![
            IncludeClause::time_series(
                TimeSeriesInclude::try_new(
                    IncludePath::try_new("owner").expect("valid path"),
                    "cpu",
                    Some(crate::families::TimeSeriesRange::try_new(10, 20).expect("valid range")),
                )
                .expect("valid series include"),
            ),
            IncludeClause::time_series(
                TimeSeriesInclude::try_new(
                    IncludePath::try_new("owner").expect("valid path"),
                    "cpu",
                    Some(crate::families::TimeSeriesRange::try_new(30, 40).expect("valid range")),
                )
                .expect("valid series include"),
            ),
        ],
    )
    .expect("executes");
    let series = result.includes().time_series();
    assert_eq!(series.len(), 2);
    let timestamps: Vec<Vec<i64>> = series
        .iter()
        .map(|one| {
            one.page().entries().iter().map(crate::families::TimeSeriesEntry::timestamp).collect()
        })
        .collect();
    assert_eq!(timestamps, vec![vec![10, 20], vec![30, 40]]);

    let _ = std::fs::remove_dir_all(dir);
}

// ---------------------------------------------------------------------------
// Seam placement and validation
// ---------------------------------------------------------------------------

#[test]
fn includes_are_typed_on_the_query_document_seam() {
    let (dir, engine) = open_engine("seam");
    let spec = simple_spec("t", "rows")
        .with_includes(vec![IncludeClause::documents(document_include("owner", 1))])
        .expect("valid includes");
    assert_eq!(spec.includes().len(), 1);
    assert_eq!(spec.includes()[0].path().segments(), &["owner".to_owned()]);

    // The plan is the frozen carrier: it exposes exactly what the
    // specification bound, in declaration order.
    let store = engine.queries();
    let plan = store.compile(crate::operations::QueryInput::Typed(spec)).expect("compiles");
    assert_eq!(plan.includes().len(), 1);
    assert_eq!(plan.includes()[0].path().segments(), &["owner".to_owned()]);
    assert_eq!(plan.includes()[0].path().segments().len(), 1);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn a_query_without_includes_carries_an_empty_set() {
    let (dir, engine) = open_engine("empty");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[]),
        ],
    );
    let result = execute_with_includes(&engine, "t", "rows", Vec::new()).expect("executes");
    assert!(result.includes().is_empty());
    assert_eq!(result.rows().len(), 1);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn includes_materialize_only_the_rows_the_page_carries() {
    let (dir, engine) = open_engine("paged");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("row/2", "rows", &[("owner", DocumentValue::String("b".into()))]),
            object_document("a", "users", &[]),
            object_document("b", "users", &[]),
        ],
    );
    let clauses = || vec![IncludeClause::documents(document_include("owner", 1))];
    let store = engine.queries();
    let snapshot = engine.read_snapshot(&scope(&engine, "t")).expect("snapshot");

    // Page one of one row materializes only that row's include, exactly as
    // V1 gathers per result document of the response being built.
    let page_one = store
        .compile(crate::operations::QueryInput::Typed(
            paged_spec("t", "rows", 0, Some(1)).with_includes(clauses()).expect("valid includes"),
        ))
        .and_then(|plan| store.execute(&plan, &snapshot))
        .expect("executes");
    assert_eq!(page_one.rows().len(), 1);
    assert_eq!(include_document_ids(page_one.includes()), vec!["a".to_owned()]);

    // The second page materializes the second row's include.
    let page_two = store
        .compile(crate::operations::QueryInput::Typed(
            paged_spec("t", "rows", 1, Some(1)).with_includes(clauses()).expect("valid includes"),
        ))
        .and_then(|plan| store.execute(&plan, &snapshot))
        .expect("executes");
    assert_eq!(page_two.rows().len(), 1);
    assert_eq!(include_document_ids(page_two.includes()), vec!["b".to_owned()]);

    // An unbounded plan still covers every row's include.
    let all = store
        .compile(crate::operations::QueryInput::Typed(
            simple_spec("t", "rows").with_includes(clauses()).expect("valid includes"),
        ))
        .and_then(|plan| store.execute(&plan, &snapshot))
        .expect("executes");
    assert_eq!(include_document_ids(all.includes()), vec!["a".to_owned(), "b".to_owned()]);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn include_clause_validation_is_typed_and_bounded() {
    // Path bounds.
    assert_eq!(IncludePath::try_new("").unwrap_err(), RequestValidationError::EmptyField);
    assert_eq!(IncludePath::try_new("a..b").unwrap_err(), RequestValidationError::EmptyField);
    assert_eq!(
        IncludePath::try_new("a.b.c.d.e.f.g.h.i").unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert!(IncludePath::try_new("a.b.c.d.e.f.g.h").is_ok());

    // Depth bounds.
    assert_eq!(
        DocumentInclude::try_new(IncludePath::field("owner").expect("valid"), 0).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    let over_depth = super::includes::MAX_INCLUDE_DEPTH + 1;
    assert_eq!(
        DocumentInclude::try_new(IncludePath::field("owner").expect("valid"), over_depth)
            .unwrap_err(),
        RequestValidationError::InvalidQuery
    );

    // Counter name bounds.
    let too_many: Vec<String> =
        (0..=super::includes::MAX_INCLUDE_COUNTER_NAMES).map(|index| format!("c{index}")).collect();
    assert_eq!(
        CounterInclude::try_new(IncludePath::field("owner").expect("valid"), too_many.clone())
            .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(
        CounterInclude::try_new(
            IncludePath::field("owner").expect("valid"),
            vec!["a".to_owned(), "a".to_owned()],
        )
        .unwrap_err(),
        RequestValidationError::InvalidQuery
    );

    // Clause list bound.
    let many: Vec<IncludeClause> = (0..=super::includes::MAX_INCLUDES)
        .map(|_| IncludeClause::documents(document_include("owner", 1)))
        .collect();
    assert_eq!(
        simple_spec("t", "rows").with_includes(many).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}

#[test]
fn the_family_seam_still_reads_counters_directly() {
    // The include contract coordinates the family seam; it does not replace
    // it. This pins that the underlying read path is untouched.
    let (dir, engine) = open_engine("family-direct");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "a",
            vec![CounterOperation::Increment { name: "views".into(), delta: 6 }],
        );
    });
    let tenant_scope = scope(&engine, "t");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let context = FamilyReadContext::new(tenant_scope, snapshot);
    let request = FamilyReadRequest::counter_group(
        crate::families::CounterRead::try_new(
            DocumentId::try_new("a").expect("valid identifier"),
            vec!["views".to_owned()],
            deadline(),
            Cancellation::new(),
        )
        .expect("valid counter read"),
    );
    match engine.families().read(context, request) {
        Ok(crate::families::FamilyReadResult::CounterGroup(group)) => {
            assert_eq!(group.find("views"), Some(Some(6)));
        }
        other => panic!("counter read did not succeed: {other:?}"),
    }

    let _ = std::fs::remove_dir_all(dir);
}

// ---------------------------------------------------------------------------
// Stream includes and page equivalence
// ---------------------------------------------------------------------------

/// Drains one query stream over the event backbone's query source,
/// returning every batch.
fn drain_query_stream(
    engine: &Engine,
    tenant_name: &str,
    plan: &crate::query::QueryPlan,
    batch_limit: u32,
) -> Vec<crate::streaming::StreamBatch> {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let streams = engine.streams();
    let mut cursor = streams
        .open(&tenant_scope, &snapshot, crate::streaming::StreamSource::Query(plan.clone()))
        .expect("opens");
    let signal = Cancellation::new();
    let mut batches = Vec::new();
    loop {
        let batch = streams
            .next_batch(
                &tenant_scope,
                &snapshot,
                &crate::streaming::StreamSource::Query(plan.clone()),
                cursor,
                batch_limit,
                &signal,
            )
            .expect("serves a batch");
        let finished = batch.completion().is_some();
        cursor = batch.next_cursor().clone();
        batches.push(batch);
        if finished {
            return batches;
        }
    }
}

/// The counter part of the comparison shape: one document and its
/// (name, value) pairs.
type CounterUnion = Vec<(String, Vec<(String, Option<i64>)>)>;

/// The series part of the comparison shape: document, name, inclusive
/// bounds, and the read page's entry timestamps.
type SeriesUnion = Vec<(String, String, Option<(i64, i64)>, Vec<i64>)>;

/// The union of one include set, as the stream/page equivalence proof
/// compares it: documents by identifier, counters by document and name,
/// series by document, name, and range.
///
/// The union is a set union, not a concatenation: two rows referencing one
/// document put that document's counters and series in two different
/// batches, and both are the same read of the same immutable snapshot, so
/// one occurrence is every occurrence.
#[derive(Debug, PartialEq)]
struct IncludeUnion {
    documents: Vec<String>,
    counters: CounterUnion,
    time_series: SeriesUnion,
}

fn union_of(includes: &IncludeSet) -> IncludeUnion {
    let mut union = IncludeUnion {
        documents: includes.documents().iter().map(|d| d.id().as_str().to_owned()).collect(),
        counters: Vec::new(),
        time_series: Vec::new(),
    };
    for group in includes.counters() {
        union.counters.push((
            group.document().as_str().to_owned(),
            group.entries().iter().map(|entry| (entry.name().to_owned(), entry.value())).collect(),
        ));
    }
    for series in includes.time_series() {
        union.time_series.push((
            series.document().as_str().to_owned(),
            series.name().to_owned(),
            series.range().map(|range| (range.start(), range.end())),
            series
                .page()
                .entries()
                .iter()
                .map(crate::families::TimeSeriesEntry::timestamp)
                .collect(),
        ));
    }
    union.documents.sort();
    union.counters.sort();
    union.time_series.sort();
    union
}

fn union_all(batches: &[crate::streaming::StreamBatch]) -> IncludeUnion {
    // The union is the merge of each batch's own set: documents and targets
    // deduplicate by their key, counter values are the same read of the
    // same snapshot so any occurrence is every occurrence, and series
    // entries concatenate then sort by timestamp.
    let mut documents: Vec<String> = Vec::new();
    let mut counters: CounterUnion = Vec::new();
    let mut time_series: SeriesUnion = Vec::new();
    for batch in batches {
        let one = union_of(batch.includes());
        for id in one.documents {
            if !documents.contains(&id) {
                documents.push(id);
            }
        }
        for group in one.counters {
            if !counters.iter().any(|(document, _)| *document == group.0) {
                counters.push(group);
            }
        }
        for series in one.time_series {
            let seen = time_series.iter().any(|(document, name, range, _)| {
                *document == series.0 && *name == series.1 && *range == series.2
            });
            if !seen {
                time_series.push(series);
            }
        }
    }
    documents.sort();
    counters.sort();
    time_series.sort();
    IncludeUnion { documents, counters, time_series }
}

#[test]
fn stream_includes_and_paged_includes_materialize_the_same_set() {
    let (dir, engine) = open_engine("equivalence");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("row/2", "rows", &[("owner", DocumentValue::String("b".into()))]),
            object_document("row/3", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("row/4", "rows", &[("owner", DocumentValue::String("gone".into()))]),
            object_document("a", "users", &[("peer", DocumentValue::String("b".into()))]),
            object_document("b", "users", &[]),
        ],
    );
    commit_transaction(&engine, "t", |store, transaction, tenant_scope, identity| {
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "a",
            vec![
                CounterOperation::Increment { name: "views".into(), delta: 2 },
                CounterOperation::Increment { name: "clicks".into(), delta: 4 },
            ],
        );
        stage_counters(
            store,
            transaction,
            tenant_scope,
            identity,
            "b",
            vec![CounterOperation::Increment { name: "views".into(), delta: 6 }],
        );
        stage_series(store, transaction, tenant_scope, identity, "a", "cpu", &[10, 20, 30]);
        stage_series(store, transaction, tenant_scope, identity, "b", "cpu", &[40, 50]);
    });

    let clauses = vec![
        IncludeClause::documents(document_include("owner", 1)),
        IncludeClause::documents(document_include("peer", 2)),
        IncludeClause::counters(counter_include("owner", &["views", "clicks"])),
        IncludeClause::time_series(
            TimeSeriesInclude::try_new(
                IncludePath::try_new("owner").expect("valid path"),
                "cpu",
                None,
            )
            .expect("valid series include"),
        ),
    ];
    // A bounded page: the stream source requires one.
    let spec = paged_spec("t", "rows", 0, Some(4)).with_includes(clauses).expect("valid includes");
    let store = engine.queries();
    let plan = store.compile(crate::operations::QueryInput::Typed(spec)).expect("compiles");

    // The paged execution materializes the includes of its whole page once.
    let tenant_scope = scope(&engine, "t");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let paged = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(paged.rows().len(), 4);
    assert!(!paged.includes().is_empty());
    let paged_union = union_of(paged.includes());

    // The stream slices the same window into batches of two, so the union
    // of every batch's include set must equal the paged set.
    let batches = drain_query_stream(&engine, "t", &plan, 2);
    assert_eq!(batches.len(), 2);
    for batch in &batches {
        assert_eq!(batch.items().len(), 2);
        assert!(!batch.includes().is_empty());
    }
    assert_eq!(union_all(&batches), paged_union);

    // A batch limit of one slices the same window four ways and still
    // unions to the same set: the equivalence does not depend on the
    // batching.
    let singles = drain_query_stream(&engine, "t", &plan, 1);
    assert_eq!(singles.len(), 4);
    assert_eq!(union_all(&singles), paged_union);

    // And a single batch covering the whole window is exactly the paged set.
    let whole = drain_query_stream(&engine, "t", &plan, 4);
    assert_eq!(whole.len(), 1);
    assert_eq!(union_of(whole[0].includes()), paged_union);

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn a_query_stream_without_includes_carries_empty_batches() {
    let (dir, engine) = open_engine("equivalence-empty");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("owner", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[]),
        ],
    );
    let spec = paged_spec("t", "rows", 0, Some(1));
    let store = engine.queries();
    let plan = store.compile(crate::operations::QueryInput::Typed(spec)).expect("compiles");
    let batches = drain_query_stream(&engine, "t", &plan, 1);
    assert_eq!(batches.len(), 1);
    assert!(batches[0].includes().is_empty());

    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn a_query_stream_reports_an_include_refusal_as_a_typed_source_error() {
    let (dir, engine) = open_engine("equivalence-refusal");
    put_documents(
        &engine,
        "t",
        vec![
            object_document("row/1", "rows", &[("next", DocumentValue::String("a".into()))]),
            object_document("a", "users", &[("next", DocumentValue::String("a".into()))]),
        ],
    );
    let spec = paged_spec("t", "rows", 0, Some(1))
        .with_includes(vec![IncludeClause::documents(document_include("next", 2))])
        .expect("valid includes");
    let store = engine.queries();
    let plan = store.compile(crate::operations::QueryInput::Typed(spec)).expect("compiles");
    let tenant_scope = scope(&engine, "t");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let streams = engine.streams();
    let cursor = streams
        .open(&tenant_scope, &snapshot, crate::streaming::StreamSource::Query(plan.clone()))
        .expect("opens");
    let signal = Cancellation::new();
    let refused = streams
        .next_batch(
            &tenant_scope,
            &snapshot,
            &crate::streaming::StreamSource::Query(plan),
            cursor,
            1,
            &signal,
        )
        .expect_err("the cycle must refuse");
    assert_eq!(refused, crate::streaming::StreamError::InvalidSource);

    let _ = std::fs::remove_dir_all(dir);
}
