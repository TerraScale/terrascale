//! In-crate query tests covering the W3-2 contract list.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. The V1-compatible text is exercised
//! through the crate-private raw-text compile entry, because the public
//! `V1QueryText::try_new` constructor is still the declaration-only W3-0
//! placeholder; that wiring is the integrator action recorded in the design
//! record. No test inspects raw pages, WAL bytes, or private lock state.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
    },
    time::Duration,
};

use super::{
    GenerationView, IndexNamespace, ObservedGeneration, OrderDirection, OrderingKey,
    QueryCondition, QueryError, QueryOrdering, QueryPaging, QueryPredicate, QueryProjection,
    QuerySource, QuerySpec, QueryStore, StalenessMode, StalenessPolicy,
    execute::execute_with_view,
    spec::{MAX_PAGE_LIMIT, validate_field_name},
    v1,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    operations::{QueryInput, QueryInputError, RequestValidationError},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, IndexGenerationSet, LocalDurability, OperationId, ReadSnapshot,
        StorageSequence, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-w32-query-{tag}-{}-{index}", std::process::id()))
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

fn commit_documents(
    engine: &Engine,
    scope: &TenantScope,
    tag: &str,
    documents: Vec<Document>,
) -> u64 {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => receipt.sequence().get(),
        other => panic!("commit did not succeed: {other:?}"),
    }
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn source(collection: &str) -> QuerySource {
    QuerySource::try_new(CollectionName::try_new(collection).expect("valid identifier"))
        .expect("user collection")
}

fn predicate(conditions: Vec<QueryCondition>) -> QueryPredicate {
    QueryPredicate::all(conditions).expect("valid conjunction")
}

fn ordering(keys: Vec<(&str, OrderDirection)>) -> QueryOrdering {
    QueryOrdering::try_new(
        keys.into_iter()
            .map(|(field, direction)| OrderingKey::try_new(field, direction).expect("valid key"))
            .collect(),
    )
    .expect("valid ordering")
}

fn projection_fields(names: &[&str]) -> QueryProjection {
    QueryProjection::fields(names).expect("valid projection")
}

fn paging(offset: u64, limit: Option<u32>) -> QueryPaging {
    QueryPaging::try_new(offset, limit).expect("valid paging")
}

fn namespace(tenant_name: &str, name: &str) -> IndexNamespace {
    IndexNamespace::try_new(tenant(tenant_name), name).expect("valid namespace")
}

fn staleness(mode: StalenessMode, namespaces: Vec<IndexNamespace>) -> StalenessPolicy {
    StalenessPolicy::try_new(mode, namespaces).expect("valid policy")
}

fn spec(
    claim: &str,
    collection: &str,
    predicate: QueryPredicate,
    ordering: QueryOrdering,
    projection: QueryProjection,
    paging: QueryPaging,
    staleness: StalenessPolicy,
) -> QuerySpec {
    QuerySpec::try_new(
        tenant(claim),
        source(collection),
        predicate,
        ordering,
        projection,
        paging,
        staleness,
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn simple_spec(claim: &str, collection: &str) -> QuerySpec {
    spec(
        claim,
        collection,
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    )
}

fn row_ids(result: &super::QueryResult) -> Vec<String> {
    result.rows().iter().map(|row| row.document().id().as_str().to_owned()).collect()
}

/// A forged generation view: independent observed and supplied generations
/// per namespace, exactly the situation one monotonic catalog cannot show.
struct ForgedView {
    entries: BTreeMap<String, (Option<ObservedGeneration>, Option<ObservedGeneration>)>,
}

impl ForgedView {
    fn with(
        namespace: &IndexNamespace,
        observed: Option<(u64, u64)>,
        available: Option<(u64, u64)>,
    ) -> Self {
        let mut entries = BTreeMap::new();
        entries.insert(
            namespace.catalog_key(),
            (
                observed.map(|(epoch, generation)| ObservedGeneration::new(epoch, generation)),
                available.map(|(epoch, generation)| ObservedGeneration::new(epoch, generation)),
            ),
        );
        Self { entries }
    }
}

impl GenerationView for ForgedView {
    fn observed_generation(
        &self,
        namespace: &IndexNamespace,
        _at: StorageSequence,
    ) -> Option<ObservedGeneration> {
        self.entries.get(&namespace.catalog_key()).and_then(|(observed, _)| *observed)
    }

    fn available_generation(&self, namespace: &IndexNamespace) -> Option<ObservedGeneration> {
        self.entries.get(&namespace.catalog_key()).and_then(|(_, available)| *available)
    }
}

fn forged_snapshot(engine: &Engine, tenant_name: &str, sequence: u64) -> ReadSnapshot {
    ReadSnapshot::new(
        engine.identity(),
        tenant(tenant_name),
        StorageSequence::new(sequence),
        IndexGenerationSet::default(),
    )
}

// ---------------------------------------------------------------------------
// The W3-2 red-test list
// ---------------------------------------------------------------------------

#[test]
fn query_plan_preserves_result_ordering_on_an_immutable_snapshot() {
    let (dir, engine) = open_engine("ordering");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-b", "users", &[("age", DocumentValue::Integer(2))]),
            object_document("doc-a", "users", &[("age", DocumentValue::Integer(1))]),
            object_document("doc-c", "users", &[("age", DocumentValue::Integer(2))]),
            object_document("doc-d", "users", &[("age", DocumentValue::Integer(1))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        ordering(vec![("age", OrderDirection::Descending)]),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");
    let result = engine.queries().execute(&plan, &snapshot).expect("executes");
    assert_eq!(
        row_ids(&result),
        // Age 2 first (doc-b before doc-c by source order), then age 1.
        ["doc-b", "doc-c", "doc-a", "doc-d"],
        "descending order must keep equal keys in the stable source order"
    );
    // A later commit does not move the immutable snapshot.
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![object_document("doc-e", "users", &[("age", DocumentValue::Integer(9))])],
    );
    let replay = engine.queries().execute(&plan, &snapshot).expect("re-executes");
    assert_eq!(row_ids(&replay), row_ids(&result), "the snapshot stays immutable");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn null_missing_and_exists_have_distinct_results() {
    let (dir, engine) = open_engine("null-missing");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-null", "users", &[("x", DocumentValue::Null)]),
            object_document("doc-seven", "users", &[("x", DocumentValue::Integer(7))]),
            object_document("doc-absent", "users", &[("y", DocumentValue::Integer(1))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();

    let run = |conditions: Vec<QueryCondition>| -> Vec<String> {
        let query = spec(
            "tenant-a",
            "users",
            predicate(conditions),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        row_ids(&store.execute(&plan, &snapshot).expect("executes"))
    };

    assert_eq!(
        run(vec![QueryCondition::Equals { field: "x".to_owned(), value: DocumentValue::Null }]),
        ["doc-null",]
    );
    assert_eq!(
        run(vec![QueryCondition::Exists { field: "x".to_owned() }]),
        ["doc-null", "doc-seven"]
    );
    assert_eq!(run(vec![QueryCondition::Missing { field: "x".to_owned() }]), ["doc-absent"]);
    assert_eq!(
        run(vec![QueryCondition::NotEquals { field: "x".to_owned(), value: DocumentValue::Null }]),
        ["doc-absent", "doc-seven"],
        "absent is not equal to a present null, and a present null is not not-equal to null"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn strict_generation_rejects_an_index_that_is_too_old() {
    let (dir, engine) = open_engine("strict-too-old");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let required = namespace("tenant-a", "by-age");
    let query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, vec![required.clone()]),
    );
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");

    // The supplied index generation is older than the one the snapshot
    // observed, so a strict query must reject it.
    let view = ForgedView::with(&required, Some((1, 3)), Some((1, 2)));
    let error = execute_with_view(&engine, &view, &plan, &snapshot).expect_err("strict rejects");
    assert_eq!(error, QueryError::IndexNotReady);
    // The rejection happens before any data access: even a snapshot from
    // another Engine would not be reached by the failing gate.
    let foreign = ReadSnapshot::new(
        engine.identity() ^ 1,
        tenant("tenant-a"),
        StorageSequence::new(1),
        IndexGenerationSet::default(),
    );
    let error = execute_with_view(&engine, &view, &plan, &foreign).expect_err("gate first");
    assert_eq!(
        error,
        QueryError::IndexNotReady,
        "the staleness gate must reject before the snapshot view is touched"
    );
    // The boundary cases the same rule admits.
    let equal = ForgedView::with(&required, Some((1, 2)), Some((1, 2)));
    assert!(execute_with_view(&engine, &equal, &plan, &snapshot).is_ok());
    let newer = ForgedView::with(&required, Some((1, 2)), Some((1, 5)));
    assert!(execute_with_view(&engine, &newer, &plan, &snapshot).is_ok());
    let missing = ForgedView::with(&required, Some((1, 2)), None);
    assert_eq!(
        execute_with_view(&engine, &missing, &plan, &snapshot).unwrap_err(),
        QueryError::IndexNotReady
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn allowed_stale_query_reports_the_generation_it_used() {
    let (dir, engine) = open_engine("allow-stale");
    let tenant_scope = scope(&engine, "tenant-a");
    // The catalog sink is installed before the first commit so the catalog
    // observes the whole publication history.
    let catalog = Arc::new(crate::index::IndexCatalog::new());
    engine.set_publication_sink(catalog.sink());
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))]),
            object_document("doc-2", "users", &[("age", DocumentValue::Integer(2))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let required = namespace("tenant-a", "by-age");
    let store = engine.queries();

    // A forged older supplied generation: the query still executes and the
    // result names the generation it accepted, per namespace.
    let view = ForgedView::with(&required, Some((1, 4)), Some((1, 2)));
    let query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::AllowStale, vec![required.clone()]),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let result = execute_with_view(&engine, &view, &plan, &snapshot).expect("executes");
    let observation = result.staleness().observation_for(&required).expect("reported");
    assert_eq!(observation.observed(), Some(ObservedGeneration::new(1, 4)));
    assert_eq!(observation.used(), Some(ObservedGeneration::new(1, 2)));
    assert!(observation.stale());
    let used = result.index_generation().expect("primary generation");
    assert_eq!(used.namespace(), &required);
    assert_eq!(used.generation(), ObservedGeneration::new(1, 2));
    assert_eq!(row_ids(&result), ["doc-1", "doc-2"]);

    // The live catalog reports a used generation from another epoch as stale.
    // The publication history is: generation one of epoch one covers sequence
    // one, then the namespace is recreated and generation one of epoch two
    // covers sequence two. A snapshot at sequence one observed the first
    // epoch only, and the two epochs are never ordered against each other.
    assert_eq!(catalog.ensure_namespace(&required, "age"), 1);
    let builder = crate::index::IndexBuilder::new(&catalog, &engine);
    assert!(matches!(
        builder.rebuild(&required, engine.identity()),
        Ok(crate::index::RebuildOutcome::Published {
            epoch: 1,
            generation: 1,
            covered_sequence: 1
        })
    ));
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![object_document("doc-3", "users", &[("age", DocumentValue::Integer(3))])],
    );
    assert_eq!(catalog.recreate_namespace(&required, 2, "age"), Ok(()));
    assert!(matches!(
        builder.rebuild(&required, engine.identity()),
        Ok(crate::index::RebuildOutcome::Published {
            epoch: 2,
            generation: 1,
            covered_sequence: 2
        })
    ));
    let observed_second_epoch = forged_snapshot(&engine, "tenant-a", 1);
    let store = engine.queries().with_index_catalog(Arc::clone(&catalog));
    let result = store.execute(&plan, &observed_second_epoch).expect("executes");
    let observation = result.staleness().observation_for(&required).expect("reported");
    assert_eq!(
        observation.observed(),
        Some(ObservedGeneration::new(1, 1)),
        "the snapshot observed epoch one's first generation"
    );
    assert_eq!(
        observation.used(),
        Some(ObservedGeneration::new(2, 1)),
        "the catalog supplies epoch two's first generation"
    );
    assert!(observation.stale(), "a generation of another epoch is never comparable");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_store_compiles_and_executes_against_supplied_snapshot() {
    let (dir, engine) = open_engine("compile-execute");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-1", "users", &[("name", DocumentValue::String("ada".into()))]),
            object_document("doc-2", "users", &[("name", DocumentValue::String("ben".into()))]),
            object_document("doc-3", "other", &[("name", DocumentValue::String("ada".into()))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("ada".to_owned()),
        }]),
        QueryOrdering::default(),
        projection_fields(&["name"]),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    assert_eq!(plan.tenant_claim(), Some(&tenant("tenant-a")));
    assert_eq!(plan.source().collection().as_str(), "users");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-1"], "only the matching user document");
    assert_eq!(result.matched(), 1);
    assert_eq!(result.ordering().keys(), QueryOrdering::default().keys());
    assert!(result.staleness().observations().is_empty());
    assert_eq!(result.index_generation(), None, "an index-less query reports no generation");
    let row = &result.rows()[0];
    assert_eq!(row.projected(), [("name".to_owned(), DocumentValue::String("ada".to_owned()))]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_input_normalizes_typed_and_v1_text_through_one_compile_seam() {
    let (dir, engine) = open_engine("one-seam");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "doc-1",
                "users",
                &[
                    ("name", DocumentValue::String("ada".into())),
                    ("age", DocumentValue::Integer(30)),
                ],
            ),
            object_document(
                "doc-2",
                "users",
                &[
                    ("name", DocumentValue::String("ben".into())),
                    ("age", DocumentValue::Integer(40)),
                ],
            ),
            object_document("doc-3", "users", &[("name", DocumentValue::String("cyd".into()))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();

    let typed = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::GreaterOrEqual {
            field: "age".to_owned(),
            value: DocumentValue::Integer(30),
        }]),
        ordering(vec![("age", OrderDirection::Descending)]),
        projection_fields(&["name", "age"]),
        paging(0, Some(2)),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let typed_plan = store.compile(QueryInput::typed(typed)).expect("compiles");
    // The same query as supported V1-compatible text, through the same seam.
    let text_plan = store
        .compile_v1_text(
            "from users where age >= 30 order by age desc limit 2 offset 0 select name, age",
        )
        .expect("parses the supported subset");
    assert_eq!(text_plan.tenant_claim(), None, "the grammar carries no tenant claim");

    let typed_result = store.execute(&typed_plan, &snapshot).expect("executes");
    let text_result = store.execute(&text_plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&typed_result), row_ids(&text_result));
    assert_eq!(typed_result.matched(), text_result.matched());
    assert_eq!(
        typed_result.rows()[0].projected(),
        text_result.rows()[0].projected(),
        "both forms must project identically"
    );
    assert_eq!(row_ids(&typed_result), ["doc-2", "doc-1"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_input_rejects_unsupported_or_generic_query_text() {
    let (dir, engine) = open_engine("reject-text");
    let store = engine.queries();
    let unsupported = [
        "SELECT * FROM users",
        "select * from users",
        "from users where name = \"ada\"",
        "from users sort by age",
        "from users where (name = 'ada')",
        "from users where name = 'ada' or age = 2",
        "from users order by age select name limit 2",
        "from users limit 0",
        "from users offset -1",
        "from users where name = 'ada' extra",
        "from",
        "where name = 'ada'",
    ];
    for text in unsupported {
        assert_eq!(
            store.compile_v1_text(text).unwrap_err(),
            QueryError::InvalidSpec,
            "unsupported grammar must be rejected: {text}"
        );
        assert_eq!(v1::validate(text).unwrap_err(), QueryInputError::UnsupportedGrammar, "{text}");
    }
    assert_eq!(v1::validate("   ").unwrap_err(), QueryInputError::EmptyText);
    assert_eq!(
        v1::validate(&format!("from {} where a = 1", "x".repeat(300))).unwrap_err(),
        QueryInputError::InvalidIdentifier
    );
    // Generic query text cannot enter through the typed form: it has no
    // free-text channel at all. Identifiers still reject control characters,
    // and the reserved family collection is not a query source.
    assert_eq!(
        CollectionName::try_new("users\u{7}").unwrap_err(),
        RequestValidationError::InvalidIdentifier
    );
    assert_eq!(
        QuerySource::try_new(
            CollectionName::try_new("terrabase.families").expect("valid identifier text")
        )
        .unwrap_err(),
        RequestValidationError::InvalidQuery,
        "the reserved family collection is not a query source"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_store_borrow_prevents_engine_shutdown_while_live() {
    // Static proof: a store cannot outlive the borrow that produced it.
    #[allow(clippy::needless_pass_by_value)] // the store moves in and drops here
    fn borrow_outlives_store<'engine>(
        engine: &'engine Engine,
        store: QueryStore<'engine>,
    ) -> &'engine Engine {
        let _ = store;
        engine
    }
    let (dir, engine) = open_engine("borrow");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    {
        let store = engine.queries();
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let plan =
            store.compile(QueryInput::typed(simple_spec("tenant-a", "users"))).expect("compiles");
        let result = store.execute(&plan, &snapshot).expect("executes while live");
        assert_eq!(result.matched(), 1);
        let _ = borrow_outlives_store(&engine, store);
    }
    // With the borrow dropped, the Engine can shut down.
    engine.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_rejects_snapshot_from_another_engine() {
    let (dir_a, engine_a) = open_engine("engine-a");
    let (dir_b, engine_b) = open_engine("engine-b");
    let scope_b = scope(&engine_b, "tenant-a");
    commit_documents(
        &engine_b,
        &scope_b,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    let foreign_snapshot = engine_b.read_snapshot(&scope_b).expect("snapshot");
    let plan = engine_a
        .queries()
        .compile(QueryInput::typed(simple_spec("tenant-a", "users")))
        .expect("compiles");
    assert_eq!(
        engine_a.queries().execute(&plan, &foreign_snapshot).unwrap_err(),
        QueryError::SnapshotEngineMismatch
    );
    drop(engine_a);
    drop(engine_b);
    std::fs::remove_dir_all(&dir_a).ok();
    std::fs::remove_dir_all(&dir_b).ok();
}

#[test]
fn query_rejects_snapshot_for_another_tenant() {
    let (dir, engine) = open_engine("tenant-mismatch");
    let tenant_scope = scope(&engine, "tenant-b");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    let snapshot_of_b = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let plan = engine
        .queries()
        .compile(QueryInput::typed(simple_spec("tenant-a", "users")))
        .expect("compiles");
    assert_eq!(
        engine.queries().execute(&plan, &snapshot_of_b).unwrap_err(),
        QueryError::SnapshotTenantMismatch,
        "the claim and the snapshot tenant are compared before any data access"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_rejects_generation_from_another_namespace_or_epoch() {
    let (dir, engine) = open_engine("namespace-epoch");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let required = namespace("tenant-a", "by-age");
    let other = namespace("tenant-a", "by-name");
    let store = engine.queries();

    // A newer generation of another namespace never satisfies this one.
    let mut view = ForgedView::with(&required, Some((1, 1)), None);
    view.entries.insert(other.catalog_key(), (None, Some(ObservedGeneration::new(1, 99))));
    let query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, vec![required.clone()]),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    assert_eq!(
        execute_with_view(&engine, &view, &plan, &snapshot).unwrap_err(),
        QueryError::IndexNotReady,
        "another namespace's generation is never used for this namespace"
    );
    // A generation of another epoch is never compared with this epoch's.
    let epoch_view = ForgedView::with(&required, Some((1, 5)), Some((2, 1)));
    assert_eq!(
        execute_with_view(&engine, &epoch_view, &plan, &snapshot).unwrap_err(),
        QueryError::IndexNotReady
    );
    // Allow-stale still records both sides without ordering them.
    let allow_query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::AllowStale, vec![required]),
    );
    let allow_plan = store.compile(QueryInput::typed(allow_query)).expect("compiles");
    let result = execute_with_view(&engine, &epoch_view, &allow_plan, &snapshot).expect("executes");
    let observation = result.staleness().observations().first().expect("reported");
    assert_eq!(observation.observed().map(ObservedGeneration::epoch), Some(1));
    assert_eq!(observation.used().map(ObservedGeneration::epoch), Some(2));
    assert!(observation.stale());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn plan_reuse_does_not_mutate_the_snapshot() {
    let (dir, engine) = open_engine("plan-reuse");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-1", "users", &[("age", DocumentValue::Integer(2))]),
            object_document("doc-2", "users", &[("age", DocumentValue::Integer(1))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let before = snapshot.clone();
    let store = engine.queries();
    let plan =
        store.compile(QueryInput::typed(simple_spec("tenant-a", "users"))).expect("compiles");
    let first = store.execute(&plan, &snapshot).expect("first execution");
    let second = store.execute(&plan, &snapshot).expect("second execution");
    assert_eq!(row_ids(&first), row_ids(&second));
    assert_eq!(snapshot, before, "execution never writes the snapshot back");
    // The same reused plan also serves a later, different snapshot.
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![object_document("doc-3", "users", &[("age", DocumentValue::Integer(3))])],
    );
    let later = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let third = store.execute(&plan, &later).expect("reused with a new snapshot");
    assert_eq!(third.matched(), 3);
    assert_eq!(snapshot, before);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn projection_and_paging_have_typed_errors_and_boundaries() {
    let (dir, engine) = open_engine("projection-paging");
    let tenant_scope = scope(&engine, "tenant-a");
    let documents = (1..=5)
        .map(|index| {
            object_document(
                &format!("doc-{index}"),
                "users",
                &[("age", DocumentValue::Integer(index))],
            )
        })
        .collect();
    commit_documents(&engine, &tenant_scope, "op-1", documents);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();

    // Typed construction errors.
    assert_eq!(QueryProjection::fields(&[""]).unwrap_err(), RequestValidationError::EmptyField);
    assert_eq!(
        QueryProjection::fields(&["age", "age"]).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(
        QueryProjection::fields(&[" age"]).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(QueryPaging::try_new(0, Some(0)).unwrap_err(), RequestValidationError::InvalidQuery);
    assert_eq!(
        QueryPaging::try_new(0, Some(MAX_PAGE_LIMIT + 1)).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert!(QueryPaging::try_new(0, Some(MAX_PAGE_LIMIT)).is_ok());
    assert!(QueryPaging::try_new(9, None).is_ok());

    // Typed boundaries in execution.
    let query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        ordering(vec![("age", OrderDirection::Ascending)]),
        projection_fields(&["age", "absent"]),
        paging(1, Some(2)),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(result.matched(), 5, "matched counts before paging");
    assert_eq!(row_ids(&result), ["doc-2", "doc-3"], "offset skips and limit caps");
    let projected = result.rows()[0].projected();
    assert_eq!(
        projected,
        [("age".to_owned(), DocumentValue::Integer(2))],
        "an absent projected field is omitted, not null"
    );
    // A whole-document projection lists every top-level field.
    let whole = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, Some(1)),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let result = store
        .execute(&store.compile(QueryInput::typed(whole)).expect("compiles"), &snapshot)
        .expect("executes");
    assert_eq!(result.rows()[0].projected(), [("age".to_owned(), DocumentValue::Integer(1))]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_cancellation_is_bounded_and_does_not_leak_a_lock() {
    let (dir, engine) = open_engine("cancellation");
    let tenant_scope = scope(&engine, "tenant-a");
    let documents = (1..=150)
        .map(|index| {
            object_document(
                &format!("doc-{index:03}"),
                "users",
                &[("age", DocumentValue::Integer(index))],
            )
        })
        .collect();
    commit_documents(&engine, &tenant_scope, "op-1", documents);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");

    // The checkpoint itself is pure and typed.
    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(super::execute::check_bounds(None, Some(&cancelled)), Err(QueryError::Cancelled));
    assert_eq!(super::execute::check_bounds(None, None), Ok(()));
    let short_deadline = Deadline::after(Duration::from_nanos(1)).expect("positive duration");
    while !short_deadline.is_expired() {
        std::hint::spin_loop();
    }
    assert_eq!(super::execute::check_bounds(Some(short_deadline), None), Err(QueryError::Deadline));
    let cancelled_spec = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
        deadline(),
        cancelled.clone(),
    );
    assert_eq!(
        cancelled_spec.unwrap_err(),
        RequestValidationError::InvalidCancellation,
        "an already cancelled signal is rejected at construction"
    );

    // A signal cancelled after compilation stops the query at the first
    // bounded checkpoint, before any scan work. The signal is read through
    // the crate-private model reference because the public construction path
    // hands the signal over by value: a caller cannot cancel a compiled plan
    // mid-flight, which the design record lists as a W3-2 limit.
    let signal = Cancellation::new();
    let query = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
        deadline(),
        signal,
    )
    .expect("valid specification");
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");
    plan.model().cancellation_signal().expect("bound signal").cancel();
    assert_eq!(engine.queries().execute(&plan, &snapshot).unwrap_err(), QueryError::Cancelled);

    // A deadline that expires between compilation and execution is bounded
    // the same way.
    let short = Deadline::after(Duration::from_millis(20)).expect("valid deadline");
    let query = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
        short,
        Cancellation::new(),
    )
    .expect("valid specification");
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");
    while !plan.model().deadline().expect("bound").is_expired() {
        std::hint::spin_loop();
    }
    assert_eq!(engine.queries().execute(&plan, &snapshot).unwrap_err(), QueryError::Deadline);

    // No lock leaks: storage reads and commits still work afterwards.
    engine.read_snapshot(&tenant_scope).expect("storage lock is free");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![object_document("doc-new", "users", &[("age", DocumentValue::Integer(1))])],
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_rejects_a_wrong_tenant_scope() {
    let (dir, engine) = open_engine("wrong-scope");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    let store = engine.queries();

    // A required index namespace of another tenant is a wrong tenant scope:
    // the namespace identifies the tenant and the logical definition.
    let query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, vec![namespace("tenant-b", "by-age")]),
    );
    assert_eq!(
        store.compile(QueryInput::typed(query)).unwrap_err(),
        QueryError::TenantMismatch,
        "a namespace of another tenant is rejected before a plan exists"
    );
    // The same rule holds at execution for the snapshot's tenant binding.
    let own = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, vec![namespace("tenant-a", "by-age")]),
    );
    let plan = store.compile(QueryInput::typed(own)).expect("compiles");
    let other_tenant_snapshot = ReadSnapshot::new(
        engine.identity(),
        tenant("tenant-b"),
        StorageSequence::new(1),
        IndexGenerationSet::default(),
    );
    assert_eq!(
        store.execute(&plan, &other_tenant_snapshot).unwrap_err(),
        QueryError::SnapshotTenantMismatch,
        "the claim mismatch is caught before the namespace scope check"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Focused extras
// ---------------------------------------------------------------------------

#[test]
fn v1_grammar_parses_the_supported_subset_into_typed_clauses() {
    let (source, predicate, ordering, paging, projection) = v1::parse_clauses(
        "from users where exists(flag) and age >= 30 and missing(note) order by age desc, name limit 10 offset 5 select name, age",
    )
    .expect("parses the documented subset");
    assert_eq!(source.collection().as_str(), "users");
    assert_eq!(predicate.conditions().len(), 3);
    assert_eq!(ordering.keys().len(), 2);
    assert_eq!(ordering.keys()[0].field(), "age");
    assert_eq!(ordering.keys()[0].direction(), OrderDirection::Descending);
    assert_eq!(ordering.keys()[1].direction(), OrderDirection::Ascending);
    assert_eq!(paging.offset(), 5);
    assert_eq!(paging.limit(), Some(10));
    assert_eq!(projection.selected(), Some(&["name".to_owned(), "age".to_owned()][..]));
    // A `from`-only query is the minimal valid form.
    assert!(v1::parse_clauses("from users").is_ok());
    assert!(v1::parse_clauses("from users where name = 'o''brien'").is_err());
}

#[test]
fn typed_clause_validations_are_typed_and_bounded() {
    assert_eq!(validate_field_name(""), Err(RequestValidationError::EmptyField));
    assert_eq!(validate_field_name(" age"), Err(RequestValidationError::InvalidQuery));
    assert_eq!(validate_field_name(&"a".repeat(256)), Err(RequestValidationError::InvalidQuery));
    assert!(validate_field_name("age").is_ok());
    assert_eq!(
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("age", OrderDirection::Ascending).expect("valid key"),
            OrderingKey::try_new("age", OrderDirection::Descending).expect("valid key"),
        ])
        .unwrap_err(),
        RequestValidationError::InvalidQuery,
        "one field may not be ordered twice"
    );
    let long_predicate =
        (0..33).map(|index| QueryCondition::Exists { field: format!("f{index}") }).collect();
    assert_eq!(
        QueryPredicate::all(long_predicate).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(
        StalenessPolicy::try_new(
            StalenessMode::Strict,
            vec![namespace("tenant-a", "by-age"), namespace("tenant-a", "by-age")]
        )
        .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}

#[test]
fn staleness_selection_reports_every_required_namespace() {
    let dir = location("selection");
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    let first = namespace("tenant-a", "by-age");
    let second = namespace("tenant-a", "by-name");
    let mut view = ForgedView::with(&first, Some((1, 2)), Some((1, 2)));
    view.entries.insert(
        second.catalog_key(),
        (Some(ObservedGeneration::new(1, 4)), Some(ObservedGeneration::new(1, 2))),
    );
    let policy = staleness(StalenessMode::AllowStale, vec![first.clone(), second.clone()]);
    let snapshot = forged_snapshot(&engine, "tenant-a", 4);
    let report = super::execute::select_indexes(&view, &policy, &snapshot).expect("selects");
    assert_eq!(report.observations().len(), 2);
    let first_observation = report.observation_for(&first).expect("reported");
    assert!(!first_observation.stale());
    let second_observation = report.observation_for(&second).expect("reported");
    assert_eq!(second_observation.used(), Some(ObservedGeneration::new(1, 2)));
    assert!(second_observation.stale());
    // Strict fails on the second namespace only.
    let strict = staleness(StalenessMode::Strict, vec![first, second]);
    assert_eq!(
        super::execute::select_indexes(&view, &strict, &snapshot).unwrap_err(),
        QueryError::IndexNotReady
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn materialization_checkpoint_stops_a_query_whose_bound_expired_after_the_scan() {
    // Deterministic proof of the post-scan checkpoint: the cancellation
    // signal becomes cancelled after the scan and sort have produced their
    // input, which is simulated exactly by calling the bounded
    // materialization step directly with an already-cancelled model. The
    // query must stop with Cancelled before it produces any row, and the
    // signal is checked with no lock held.
    let (dir, engine) = open_engine("post-sort-cancel");
    let signal = Cancellation::new();
    let query = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
        deadline(),
        signal,
    )
    .expect("valid specification");
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");
    let model = plan.model().clone();
    // The scan and sort have finished by the time this fires.
    model.cancellation_signal().expect("bound signal").cancel();
    let ordered: Vec<Document> = (0..300)
        .map(|index| {
            object_document(
                &format!("doc-{index:03}"),
                "users",
                &[("age", DocumentValue::Integer(index))],
            )
        })
        .collect();
    assert_eq!(
        super::execute::materialize(&model, &ordered).unwrap_err(),
        QueryError::Cancelled,
        "the first materialization checkpoint must stop the query before any row"
    );

    // A live signal still materializes the bounded page through the same
    // checkpoints, proving the step did not simply stop unconditionally.
    let live = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        predicate(Vec::new()),
        ordering(vec![("age", OrderDirection::Ascending)]),
        QueryProjection::whole_document(),
        paging(10, Some(55)),
        staleness(StalenessMode::Strict, Vec::new()),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification");
    let live_plan = engine.queries().compile(QueryInput::typed(live)).expect("compiles");
    let ordered: Vec<Document> = (0..300)
        .map(|index| {
            object_document(
                &format!("doc-{index:03}"),
                "users",
                &[("age", DocumentValue::Integer(index))],
            )
        })
        .collect();
    let rows = super::execute::materialize(live_plan.model(), &ordered).expect("materializes");
    assert_eq!(rows.len(), 55, "the offset skips and the limit caps");
    assert_eq!(rows[0].document().id().as_str(), "doc-010");
    assert_eq!(rows[54].document().id().as_str(), "doc-064");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_large_corpus_stays_bounded_and_leaks_no_lock_after_cancellation() {
    // End-to-end at scale: a signal cancelled after compilation on a corpus
    // far larger than the checkpoint interval still returns the typed
    // Cancelled error, and the storage lock is free afterwards. Any one of
    // the bounded checkpoints catching it satisfies the contract; the test
    // above pins the post-scan checkpoint specifically.
    let (dir, engine) = open_engine("cancel-at-scale");
    let tenant_scope = scope(&engine, "tenant-a");
    let documents: Vec<Document> = (0..2500)
        .map(|index| {
            object_document(
                &format!("doc-{index:04}"),
                "users",
                &[("age", DocumentValue::Integer(index))],
            )
        })
        .collect();
    commit_documents(&engine, &tenant_scope, "op-1", documents);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let signal = Cancellation::new();
    let query = QuerySpec::try_new(
        tenant("tenant-a"),
        source("users"),
        predicate(Vec::new()),
        ordering(vec![("age", OrderDirection::Ascending)]),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
        deadline(),
        signal,
    )
    .expect("valid specification");
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");
    plan.model().cancellation_signal().expect("bound signal").cancel();
    assert_eq!(engine.queries().execute(&plan, &snapshot).unwrap_err(), QueryError::Cancelled);
    engine.read_snapshot(&tenant_scope).expect("storage lock is free");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![object_document("doc-new", "users", &[("age", DocumentValue::Integer(1))])],
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn total_document_order_ranks_missing_and_types_deterministically() {
    use crate::query::model::compare_values;
    let values = [
        DocumentValue::Null,
        DocumentValue::Boolean(false),
        DocumentValue::Boolean(true),
        DocumentValue::Integer(2),
        DocumentValue::Number(2.5),
        DocumentValue::String("a".to_owned()),
        DocumentValue::Array(vec![]),
        DocumentValue::Object(BTreeMap::new()),
    ];
    for (index, left) in values.iter().enumerate() {
        for right in values.iter().skip(index + 1) {
            assert_eq!(
                compare_values(Some(left), Some(right)),
                std::cmp::Ordering::Less,
                "{left:?} must order before {right:?}"
            );
        }
    }
    assert_eq!(compare_values(None, Some(&DocumentValue::Null)), std::cmp::Ordering::Less);
    assert_eq!(
        compare_values(Some(&DocumentValue::Integer(2)), Some(&DocumentValue::Number(2.0))),
        std::cmp::Ordering::Equal,
        "integer and float compare numerically"
    );
}

#[test]
#[allow(clippy::too_many_lines)] // one property block plus one end-to-end block, split would blur the proof
fn nan_and_non_finite_numbers_keep_a_total_deterministic_order() {
    use crate::query::model::{compare_documents, compare_values};

    // The comparator is a true total order over the full value domain,
    // including NaN and the infinities: antisymmetric, transitive, and with
    // NaN consistently equal to NaN. An inconsistent comparator can make the
    // standard library's sort panic, so this is checked as a property.
    let owned = [
        None,
        Some(DocumentValue::Null),
        Some(DocumentValue::Boolean(false)),
        Some(DocumentValue::Boolean(true)),
        Some(DocumentValue::Integer(-7)),
        Some(DocumentValue::Integer(3)),
        Some(DocumentValue::Number(-1.5)),
        Some(DocumentValue::Number(f64::NEG_INFINITY)),
        Some(DocumentValue::Number(f64::NAN)),
        Some(DocumentValue::Number(f64::INFINITY)),
        Some(DocumentValue::Number(1.0e308)),
        Some(DocumentValue::String("a".to_owned())),
        // The exact-integer boundary of the float domain: above 2^53 the
        // float spacing exceeds 1, so a widened integer rounds and distinct
        // integers can collapse onto one float. This is the range of
        // snowflake identifiers and nanosecond timestamps.
        Some(DocumentValue::Integer(9_007_199_254_740_992)), // 2^53
        Some(DocumentValue::Integer(9_007_199_254_740_993)), // 2^53 + 1
        Some(DocumentValue::Integer(-9_007_199_254_740_993)), // -(2^53) - 1
        Some(DocumentValue::Number(9_007_199_254_740_992.0)), // 2^53 exactly
        Some(DocumentValue::Number(9_007_199_254_740_993.0)), // rounds to 2^53
        // The whole i64 range against the sign gates of the exact comparison.
        Some(DocumentValue::Integer(i64::MIN)),
        Some(DocumentValue::Integer(i64::MAX)),
        Some(DocumentValue::Number(-9_223_372_036_854_775_808.0)), // -2^63 exactly
        Some(DocumentValue::Number(f64::MAX)),
        Some(DocumentValue::Number(f64::MIN)),
        // Smallest magnitudes and the two zeroes, which probe the fractional
        // tie-break and the zero-equality of the unified numeric order.
        Some(DocumentValue::Number(f64::MIN_POSITIVE)),
        Some(DocumentValue::Number(-f64::MIN_POSITIVE)),
        Some(DocumentValue::Number(-0.0)),
        // Nested values exercise the array and object comparisons, which
        // delegate element-wise: a NaN inside a container, a plain element,
        // and a mixed container with the exact-integer boundary.
        Some(DocumentValue::Array(vec![DocumentValue::Number(f64::NAN)])),
        Some(DocumentValue::Array(vec![DocumentValue::Integer(1)])),
        Some(DocumentValue::Array(vec![
            DocumentValue::Integer(9_007_199_254_740_992),
            DocumentValue::Number(f64::NAN),
        ])),
        Some(DocumentValue::Object(
            [("a".to_owned(), DocumentValue::Number(f64::NAN))].into_iter().collect(),
        )),
        Some(DocumentValue::Object(
            [("a".to_owned(), DocumentValue::Integer(1))].into_iter().collect(),
        )),
        Some(DocumentValue::Object(
            [
                ("a".to_owned(), DocumentValue::Integer(9_007_199_254_740_993)),
                ("b".to_owned(), DocumentValue::Number(f64::NAN)),
            ]
            .into_iter()
            .collect(),
        )),
    ];
    let values: Vec<Option<&DocumentValue>> = owned.iter().map(|value| value.as_ref()).collect();
    for (index, left) in values.iter().enumerate() {
        for (other_index, right) in values.iter().enumerate() {
            let forward = compare_values(*left, *right);
            let backward = compare_values(*right, *left).reverse();
            assert_eq!(forward, backward, "{left:?} vs {right:?} must be antisymmetric");
            if index == other_index {
                assert_eq!(forward, std::cmp::Ordering::Equal);
            }
        }
    }
    assert_eq!(
        compare_values(
            Some(&DocumentValue::Number(f64::NAN)),
            Some(&DocumentValue::Number(f64::NAN))
        ),
        std::cmp::Ordering::Equal,
        "NaN must equal NaN"
    );
    // Substitution: two values that compare Equal must be interchangeable —
    // against every third value, both must answer the same way. This is the
    // total-order axiom a widening comparison breaks: above 2^53 two distinct
    // integers can round onto one float, compare Equal against it, and yet
    // compare unequal to each other.
    for equal_left in &values {
        for equal_right in &values {
            if compare_values(*equal_left, *equal_right) != std::cmp::Ordering::Equal {
                continue;
            }
            for third in &values {
                assert_eq!(
                    compare_values(*equal_left, *third),
                    compare_values(*equal_right, *third),
                    "{equal_left:?} and {equal_right:?} compare Equal, so they must agree against {third:?}"
                );
            }
        }
    }
    for left in &values {
        for middle in &values {
            for right in &values {
                let first = compare_values(*left, *middle);
                let second = compare_values(*middle, *right);
                if first == std::cmp::Ordering::Less && second == std::cmp::Ordering::Less {
                    assert_eq!(
                        compare_values(*left, *right),
                        std::cmp::Ordering::Less,
                        "order must be transitive over {left:?} < {middle:?} < {right:?}"
                    );
                }
            }
        }
    }
    // Non-finite numbers rank after every finite number and before strings.
    let finite = Some(&DocumentValue::Number(1.0e308));
    let nan = Some(&DocumentValue::Number(f64::NAN));
    let text = Some(&DocumentValue::String("a".to_owned()));
    assert_eq!(compare_values(finite, nan), std::cmp::Ordering::Less);
    // The widening defect's exact counterexample and the cross-type
    // boundaries, pinned directly so the regression reads without the loops:
    // one-apart integers at 2^53, the exact -2^63, the sign gates at the
    // finite extremes, the fractional tie-break, and the two zeroes.
    assert_eq!(
        compare_values(
            Some(&DocumentValue::Integer(9_007_199_254_740_992)),
            Some(&DocumentValue::Number(9_007_199_254_740_992.0))
        ),
        std::cmp::Ordering::Equal
    );
    assert_eq!(
        compare_values(
            Some(&DocumentValue::Integer(9_007_199_254_740_993)),
            Some(&DocumentValue::Number(9_007_199_254_740_992.0))
        ),
        std::cmp::Ordering::Greater,
        "2^53 + 1 is strictly greater than the float 2^53, not equal to it"
    );
    assert_eq!(
        compare_values(
            Some(&DocumentValue::Integer(i64::MIN)),
            Some(&DocumentValue::Number(-9_223_372_036_854_775_808.0))
        ),
        std::cmp::Ordering::Equal,
        "i64::MIN and the exact float -2^63 are the same mathematical value"
    );
    assert_eq!(
        compare_values(
            Some(&DocumentValue::Integer(i64::MAX)),
            Some(&DocumentValue::Number(f64::MAX))
        ),
        std::cmp::Ordering::Less
    );
    assert_eq!(
        compare_values(
            Some(&DocumentValue::Integer(i64::MIN)),
            Some(&DocumentValue::Number(f64::MIN))
        ),
        std::cmp::Ordering::Greater
    );
    assert_eq!(
        compare_values(Some(&DocumentValue::Integer(2)), Some(&DocumentValue::Number(2.5))),
        std::cmp::Ordering::Less
    );
    assert_eq!(
        compare_values(Some(&DocumentValue::Integer(0)), Some(&DocumentValue::Number(-0.0))),
        std::cmp::Ordering::Equal
    );
    assert_eq!(compare_values(nan, text), std::cmp::Ordering::Less);

    // End to end: a corpus with several NaN documents and a mixed
    // Integer/Number field at the 2^53 boundary sorts deterministically
    // through the public path, and every NaN document lands after every
    // finite number. NaN survives the storage codec bit-exactly and integers
    // and floats keep their encodings, so the ordering is exercised over real
    // committed documents in the exact domain where widening used to collapse
    // distinct values.
    let (dir, engine) = open_engine("nan-order");
    let tenant_scope = scope(&engine, "tenant-a");
    let scores = [
        DocumentValue::Number(f64::NAN),
        DocumentValue::Integer(4),
        DocumentValue::Number(f64::NAN),
        DocumentValue::Number(f64::INFINITY),
        DocumentValue::Number(-2.5),
        DocumentValue::Number(f64::NAN),
        DocumentValue::Integer(10),
        DocumentValue::String("text".to_owned()),
        DocumentValue::Number(f64::NEG_INFINITY),
        // Mixed Integer/Number values around the 2^53 boundary and the
        // extremes of the cross-type range: the widening defect's range.
        DocumentValue::Integer(9_007_199_254_740_992),
        DocumentValue::Integer(9_007_199_254_740_993),
        DocumentValue::Number(9_007_199_254_740_992.0),
        DocumentValue::Number(9_007_199_254_740_992.5),
        DocumentValue::Integer(i64::MIN),
        DocumentValue::Integer(i64::MAX),
        DocumentValue::Number(f64::MAX),
        DocumentValue::Number(-0.0),
    ];
    let documents = scores
        .iter()
        .enumerate()
        .map(|(index, score)| {
            object_document(&format!("doc-{index:02}"), "users", &[("score", score.clone())])
        })
        .collect();
    commit_documents(&engine, &tenant_scope, "op-1", documents);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let query = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        ordering(vec![("score", OrderDirection::Ascending)]),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");
    let first = engine.queries().execute(&plan, &snapshot).expect("sorts");
    let second = engine.queries().execute(&plan, &snapshot).expect("sorts again");
    assert_eq!(row_ids(&first), row_ids(&second), "repeated sorts must be identical");
    let ordered_scores: Vec<&DocumentValue> =
        first.rows().iter().map(|row| &row.projected()[0].1).collect();
    // Ascending: finite numbers first, then the non-finite slot, then text.
    let nan_count = ordered_scores
        .iter()
        .take_while(|value| !matches!(value, DocumentValue::Number(number) if !number.is_finite()))
        .count();
    let finite_count = scores
        .iter()
        .filter(|value| match value {
            DocumentValue::Integer(_) => true,
            DocumentValue::Number(number) => number.is_finite(),
            _ => false,
        })
        .count();
    assert_eq!(nan_count, finite_count, "every NaN document must land after every finite number");
    assert!(ordered_scores.iter().any(|value| matches!(
        value,
        DocumentValue::Number(number) if number.is_nan()
    )));

    // Sort stress: the same matched documents under many pseudo-random input
    // permutations must all sort without panicking, and every output must be
    // consistent with the comparator (no later element orders before an
    // earlier one). Equal-key documents — here the three NaN documents,
    // which are equal by design — may keep their shuffled input order under
    // a stable sort, so convergence is asserted on the comparator's order,
    // not on the equal-key arrangement.
    let key = OrderingKey::try_new("score", OrderDirection::Ascending).expect("valid key");
    let ordering = QueryOrdering::try_new(vec![key]).expect("valid ordering");
    let mut source: Vec<Document> = first.rows().iter().map(|row| row.document().clone()).collect();
    let mut members: Vec<String> = source.iter().map(|doc| doc.id().as_str().to_owned()).collect();
    members.sort();
    let mut state: u64 = 0x2545_f491_4f6c_dd1d;
    for _ in 0..32 {
        // Deterministic Fisher-Yates driven by a splitmix step; no external
        // dependency is allowed in this crate.
        for position in (1..source.len()).rev() {
            state = state.wrapping_add(0x9e37_79b9_7f4a_7c15);
            let mut mixed = state;
            mixed = (mixed ^ (mixed >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
            mixed = (mixed ^ (mixed >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
            mixed ^= mixed >> 31;
            let swap = usize::try_from(mixed % u64::try_from(position + 1).expect("small"))
                .expect("bounded by the vector length");
            source.swap(position, swap);
        }
        source.sort_by(|left, right| compare_documents(&ordering, left, right));
        let mut ids: Vec<String> = source.iter().map(|doc| doc.id().as_str().to_owned()).collect();
        ids.sort();
        assert_eq!(ids, members, "sorting never loses or duplicates a document");
        for (position, left) in source.iter().enumerate() {
            for right in source.iter().skip(position + 1) {
                assert_ne!(
                    compare_documents(&ordering, left, right),
                    std::cmp::Ordering::Greater,
                    "{:?} orders after {:?} in a sorted output",
                    left.id(),
                    right.id()
                );
            }
        }
    }
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
