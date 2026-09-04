//! In-crate query tests covering the W3-2 contract list.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. SQL is the only query language (D-78):
//! text is exercised through the validated [`SqlQueryText`] front end and
//! the typed specification surface; supported SQL lowers through the same
//! planner. No test inspects raw pages, WAL bytes, or private lock state.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::{
    GenerationView, IndexNamespace, ObservedGeneration, OrderDirection, OrderingKey,
    QueryCondition, QueryError, QueryOrdering, QueryPaging, QueryPredicate, QueryProjection,
    QuerySource, QuerySpec, QueryStore, StalenessMode, StalenessPolicy, VectorSearchRequest,
    execute::execute_with_view,
    spec::{MAX_PAGE_LIMIT, validate_field_name},
};
use crate::{
    engine::{AutoIndexRecommendation, CommitOutcome, DescriptorCoverage, Engine, EngineConfig},
    operations::{QueryInput, QueryInputError, RequestValidationError, SqlQueryText},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, IndexGenerationSet, LocalDurability, OperationId, ReadSnapshot,
        StorageSequence, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-w32-query-{tag}-{}-{index}", std::process::id()))
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
    // The Engine-owned catalog (W4-CA-3) observes the whole publication
    // history through its registered sink.
    let catalog = engine.index_catalog();
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
    let store = engine.queries();
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
    assert_eq!(plan.source().collection().map(CollectionName::as_str), Some("users"));
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-1"], "only the matching user document");
    assert_eq!(result.matched(), 1);
    assert_eq!(result.ordering().keys(), QueryOrdering::default().keys());
    assert!(result.staleness().observations().is_empty());
    assert_eq!(
        result.index_generation().map(super::result::IndexGenerationRef::generation),
        Some(ObservedGeneration::new(1, 1)),
        "the exact equality query is served by its automatic index"
    );
    assert_eq!(result.statistics().scanned(), 1, "the index narrows candidates before filtering");
    let row = &result.rows()[0];
    assert_eq!(row.projected(), [("name".to_owned(), DocumentValue::String("ada".to_owned()))]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Proves the index-narrowed path structurally, without any timing claim: a
/// collection of 5,000 documents answers an equality query whose posting list
/// names two candidates by reading exactly those candidates, and the scanned
/// count reports the candidate set, never the collection size a scan would
/// have evaluated.
#[test]
fn an_indexed_equality_query_reads_only_its_named_candidates() {
    keyed_candidate_fetch("indexed-keyed-fetch");
}

/// Opens one engine and proves the keyed candidate fetch of the
/// index-narrowed path (see the test above).
fn keyed_candidate_fetch(tag: &str) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let config = EngineConfig::durable(&dir).expect("durable location");
    let engine = Engine::open(config).expect("engine opens");
    let tenant_scope = scope(&engine, "tenant-a");
    let total = 5_000_usize;
    let documents: Vec<Document> = (0..total)
        .map(|index| {
            let name = if index == 7 || index == total - 1 { "needle" } else { "hay" };
            object_document(
                &format!("doc-{index:05}"),
                "users",
                &[("name", DocumentValue::String(name.to_owned()))],
            )
        })
        .collect();
    commit_documents(&engine, &tenant_scope, "op-1", documents);
    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("needle".to_owned()),
        }]),
        QueryOrdering::default(),
        projection_fields(&["name"]),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");

    // The first execution registers and initially builds the automatic
    // index before its data access; the second selects it. Both must answer
    // identically, and both must report the two-candidate scan.
    for round in 0..2 {
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let result = store.execute(&plan, &snapshot).expect("executes");
        assert_eq!(
            row_ids(&result),
            ["doc-00007", "doc-04999"],
            "round {round} answers with exactly the needles"
        );
        assert_eq!(result.matched(), 2);
        assert_eq!(
            result.statistics().scanned(),
            2,
            "round {round} evaluated the two candidates the posting read named, \
             not the 5,000 documents a collection scan would have evaluated"
        );
        assert_eq!(result.statistics().skipped(), 0);
        assert!(
            result.index_generation().is_some(),
            "round {round} is served by the automatic index segment"
        );
    }
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn automatic_index_reads_are_collection_scoped_and_fall_back_for_an_uncovered_snapshot() {
    let (dir, engine) = open_engine("indexed-scope");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("user", "users", &[("name", DocumentValue::String("ada".into()))]),
            object_document("city", "cities", &[("name", DocumentValue::String("ada".into()))]),
            object_document("other", "users", &[("name", DocumentValue::String("ben".into()))]),
        ],
    );
    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("ada".to_owned()),
        }]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let indexed = store.execute(&plan, &snapshot).expect("indexed execution");
    assert_eq!(row_ids(&indexed), ["user"]);
    assert_eq!(
        indexed.statistics().scanned(),
        1,
        "cities and non-matching users are not candidates"
    );
    assert!(indexed.index_generation().is_some(), "the automatic segment served the lookup");

    // A later commit makes the published segment incomplete for the new cut,
    // and the optimizer pass that runs at the head of the next dynamic query
    // catches the segment up to that commit before selection: one write does
    // not strand the index at stale coverage. The healed segment serves the
    // new cut, and the new match is found.
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![object_document(
            "new-user",
            "users",
            &[("name", DocumentValue::String("ada".into()))],
        )],
    );
    let newer = engine.read_snapshot(&tenant_scope).expect("new snapshot");
    let healed = store.execute(&plan, &newer).expect("caught-up execution");
    assert_eq!(row_ids(&healed), ["new-user", "user"]);
    assert_eq!(
        healed.statistics().scanned(),
        2,
        "the caught-up segment narrows candidates at the new coverage"
    );
    assert!(
        healed.index_generation().is_some(),
        "the catch-up rebuild republished the segment for the new cut"
    );

    // A plan that names the index bypasses the optimizer pass entirely
    // (AX-26), so nothing catches its segment up: executed against a cut the
    // segment does not cover, the executor must scan deterministically
    // rather than omit the new match — the answer never depends on index
    // data.
    commit_documents(
        &engine,
        &tenant_scope,
        "op-3",
        vec![object_document(
            "late-user",
            "users",
            &[("name", DocumentValue::String("ada".into()))],
        )],
    );
    let named = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("ada".to_owned()),
        }]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::AllowStale, vec![namespace("tenant-a", "Auto/users/ByName")]),
    );
    let named_plan = store.compile(QueryInput::typed(named)).expect("compiles");
    let latest = engine.read_snapshot(&tenant_scope).expect("latest snapshot");
    let fallback = store.execute(&named_plan, &latest).expect("bounded fallback execution");
    assert_eq!(row_ids(&fallback), ["late-user", "new-user", "user"]);
    assert_eq!(
        fallback.statistics().scanned(),
        3,
        "the named segment still misses the late write, so the executor scans; \
         the durable extracted column for `name` (auto-registered by the first \
         query) narrows that scan to the three matches"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_input_normalizes_typed_and_sql_text_through_one_compile_seam() {
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
    // The same query as SQL text lowers into the same immutable plan.
    let text = SqlQueryText::try_new(
        "SELECT name, age FROM users WHERE age >= 30 ORDER BY age DESC LIMIT 2 OFFSET 0",
    )
    .expect("the front end accepts read-only SQL");
    let sql_plan = store.compile(QueryInput::sql(text)).expect("SQL lowers");
    let sql_result = store.execute(&sql_plan, &snapshot).expect("SQL executes");
    assert_eq!(row_ids(&sql_result), ["doc-2", "doc-1"]);

    let typed_result = store.execute(&typed_plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&typed_result), ["doc-2", "doc-1"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_input_rejects_unsupported_or_generic_query_text() {
    let (dir, engine) = open_engine("reject-text");
    let store = engine.queries();
    // Front-end rejections: the text is not read-only SQL, so both the
    // public constructor and the compile seam refuse it.
    let unparsable = [
        "FROM users SORT BY age",
        "SELECT * FROM users; DROP TABLE users",
        "DROP TABLE users",
        "INSERT INTO users VALUES (1)",
        "UPDATE users SET age = 2",
        "DELETE FROM users",
        "PRAGMA journal_mode=WAL",
        "ATTACH DATABASE 'x' AS y",
        "SELECT",
        "WITH",
    ];
    for text in unparsable {
        assert_eq!(
            super::validate_sql_text(text).unwrap_err(),
            QueryInputError::UnsupportedGrammar,
            "{text}"
        );
        assert_eq!(
            SqlQueryText::try_new(text).unwrap_err(),
            QueryInputError::UnsupportedGrammar,
            "the public constructor rejects the same text before the seam: {text}"
        );
        // Unvalidatable text never reaches the planner.
    }
    // Read-only SQL the front end accepts compiles into an executable plan.
    for text in [
        "SELECT * FROM users",
        "select * from users",
        "SELECT name FROM users WHERE name = 'ada'",
        "WITH recent AS (SELECT * FROM users) SELECT * FROM recent",
        "EXPLAIN SELECT * FROM users",
    ] {
        assert!(super::validate_sql_text(text).is_ok(), "the front end accepts: {text}");
        let result =
            store.compile(QueryInput::sql(SqlQueryText::try_new(text).expect("valid text")));
        assert!(result.is_ok(), "the planner accepts execute-only read shapes: {text}");
    }
    assert_eq!(super::validate_sql_text("   ").unwrap_err(), QueryInputError::EmptyText);
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
fn sql_text_validates_through_the_read_only_front_end() {
    use super::sql;
    // The front end admits one read-only statement and classifies its shape;
    // planning that tree is E-4.
    let parsed = sql::parse(
        "SELECT name, age FROM users WHERE age >= 30 ORDER BY age DESC LIMIT 10",
        16 * 1024,
    )
    .expect("parses read-only SQL");
    assert_eq!(parsed.kind(), sql::SqlStatementKind::Select);
    let with = sql::parse("WITH recent AS (SELECT * FROM users) SELECT * FROM recent", 16 * 1024)
        .expect("parses WITH");
    assert_eq!(with.kind(), sql::SqlStatementKind::With);
    let explain = sql::parse("EXPLAIN SELECT * FROM users", 16 * 1024).expect("parses EXPLAIN");
    assert_eq!(explain.kind(), sql::SqlStatementKind::Explain);
    // Parameters stay out of band: the tree carries no values.
    let parameters =
        sql::SqlParameters::try_new(&[("min_age", DocumentValue::Integer(30))]).expect("binds");
    assert!(parameters.contains("min_age"));
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
        super::execute::materialize(&model, &ordered, &std::collections::BTreeMap::new())
            .unwrap_err(),
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
    let rows = super::execute::materialize(
        live_plan.model(),
        &ordered,
        &std::collections::BTreeMap::new(),
    )
    .expect("materializes");
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

// ---------------------------------------------------------------------------
// GAP-EVAL (#62): engine query evaluator parity contract tests.
//
// V1 evidence is the pinned source identity fa99b9257776dbe625e6a2388c74c9ce
// under ../.worktrees/terrabase-v1-fa99b925-readonly (cited per test); the
// row-level gap analysis is docs/parity/ps-02-query-sweep.md (PB-4004,
// PB-4006, PB-4013 and the GAP-Q1..Q5 labels). The close-out dispositions
// live in docs/query/gap-eval-evaluator-parity.md.
// ---------------------------------------------------------------------------

/// `NotEquals` matches documents that do not carry the field. A null literal
/// is the explicit-null entry only. `Field != null` excludes present nulls
/// and nothing else, while `= null` matches the explicit null sentinel and
/// never absence.
#[test]
fn not_equals_matches_missing_and_pins_null_semantics() {
    let (dir, engine) = open_engine("gap-eval-q1");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "doc-absent",
                "users",
                &[("name", DocumentValue::String("a".to_owned()))],
            ),
            object_document(
                "doc-null",
                "users",
                &[("name", DocumentValue::String("b".to_owned())), ("tag", DocumentValue::Null)],
            ),
            object_document(
                "doc-x",
                "users",
                &[
                    ("name", DocumentValue::String("c".to_owned())),
                    ("tag", DocumentValue::String("x".to_owned())),
                ],
            ),
            object_document(
                "doc-y",
                "users",
                &[
                    ("name", DocumentValue::String("d".to_owned())),
                    ("tag", DocumentValue::String("y".to_owned())),
                ],
            ),
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
        let result = store.execute(&plan, &snapshot).expect("executes");
        row_ids(&result)
    };

    // `tag != 'x'`: absent, present null, and every other value all match.
    let mut matched = run(vec![QueryCondition::NotEquals {
        field: "tag".to_owned(),
        value: DocumentValue::String("x".to_owned()),
    }]);
    matched.sort();
    assert_eq!(matched, ["doc-absent", "doc-null", "doc-y"]);

    // `tag != null`: excludes the present null only; absence still matches.
    let mut matched = run(vec![QueryCondition::NotEquals {
        field: "tag".to_owned(),
        value: DocumentValue::Null,
    }]);
    matched.sort();
    assert_eq!(matched, ["doc-absent", "doc-x", "doc-y"]);

    // `tag = null`: the explicit null only, never absence.
    assert_eq!(
        run(vec![QueryCondition::Equals { field: "tag".to_owned(), value: DocumentValue::Null }]),
        ["doc-null"]
    );

    // Exists includes the present null; Missing is absence only.
    let mut matched = run(vec![QueryCondition::Exists { field: "tag".to_owned() }]);
    matched.sort();
    assert_eq!(matched, ["doc-null", "doc-x", "doc-y"]);
    assert_eq!(run(vec![QueryCondition::Missing { field: "tag".to_owned() }]), ["doc-absent"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Q2 (PB-4006): the predicate path compares integers and numbers by exact
/// mathematical value, the same rule the order-by path already applies
/// (`compare_integer_with_number`, and the W3-2 residual recorded at
/// docs/query/w3-query-index.md:398-406). Above 2^53 the float spacing
/// exceeds one, so a widening comparison would call `Integer(2^53 + 1)` and
/// `Number(2^53.0)` equal; V1 indexes longs and doubles as exact distinct
/// terms, so the exact comparison is the native rule.
#[test]
#[allow(
    clippy::cast_precision_loss,
    reason = "2^53 is exactly representable in f64; the boundary is the point of the test"
)]
fn predicate_comparison_is_exact_across_integer_and_number_domains() {
    let (dir, engine) = open_engine("gap-eval-q2-exact");
    let tenant_scope = scope(&engine, "tenant-a");
    let two_pow_53: i64 = 9_007_199_254_740_992;
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("big-int", "users", &[("n", DocumentValue::Integer(two_pow_53 + 1))]),
            object_document(
                "big-float",
                "users",
                &[("n", DocumentValue::Number(two_pow_53 as f64))],
            ),
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
        let mut ids = row_ids(&store.execute(&plan, &snapshot).expect("executes"));
        ids.sort();
        ids
    };

    // The exact rule orders Integer(2^53 + 1) above Number(2^53.0); the
    // widening residual called them equal, which is the red this test pins.
    assert_eq!(
        run(vec![QueryCondition::GreaterThan {
            field: "n".to_owned(),
            value: DocumentValue::Number(two_pow_53 as f64)
        }]),
        ["big-int"]
    );
    assert_eq!(
        run(vec![QueryCondition::LessThan {
            field: "n".to_owned(),
            value: DocumentValue::Integer(two_pow_53 + 1)
        }]),
        ["big-float"]
    );
    // Equal stays exact and symmetric at the boundary itself.
    assert_eq!(
        run(vec![QueryCondition::GreaterOrEqual {
            field: "n".to_owned(),
            value: DocumentValue::Number(two_pow_53 as f64)
        }]),
        ["big-float", "big-int"]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Date-valued fields use the exact tick domain. The typed value domain has
/// no separate date literal, so the mapping is:
/// dates as tick integers ride the exact integer order, and ISO-8601 date
/// strings ride the lexicographic string order, which is identical for
/// same-format timestamps. The close-out record files this as the accepted
/// V2 mapping rather than a native date domain.
#[test]
fn date_valued_fields_order_exactly_in_the_integer_and_string_domains() {
    let (dir, engine) = open_engine("gap-eval-q2-dates");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "t-1",
                "ticks",
                &[("at", DocumentValue::Integer(638_000_000_000_000_001))],
            ),
            object_document(
                "t-3",
                "ticks",
                &[("at", DocumentValue::Integer(638_000_000_000_000_003))],
            ),
            object_document(
                "t-2",
                "ticks",
                &[("at", DocumentValue::Integer(638_000_000_000_000_002))],
            ),
            object_document(
                "s-1",
                "stamps",
                &[("stamp", DocumentValue::String("2026-08-26T10:00:00Z".to_owned()))],
            ),
            object_document(
                "s-2",
                "stamps",
                &[("stamp", DocumentValue::String("2026-08-26T09:59:59Z".to_owned()))],
            ),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |collection: &str, field: &str| -> Vec<String> {
        let query = spec(
            "tenant-a",
            collection,
            predicate(Vec::new()),
            ordering(vec![(field, OrderDirection::Ascending)]),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        row_ids(&store.execute(&plan, &snapshot).expect("executes"))
    };
    assert_eq!(run("ticks", "at"), ["t-1", "t-2", "t-3"]);
    assert_eq!(run("stamps", "stamp"), ["s-2", "s-1"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// `intersect(...)` intersects the results of its argument predicates. The
/// match is the conjunction, so a two-argument intersect is exactly the
/// two-condition conjunction over the same documents.
#[test]
fn intersect_semantics_is_the_predicate_conjunction() {
    let (dir, engine) = open_engine("gap-eval-q3-intersect");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "both",
                "users",
                &[
                    ("age", DocumentValue::Integer(30)),
                    ("region", DocumentValue::String("emea".to_owned())),
                ],
            ),
            object_document(
                "age-only",
                "users",
                &[
                    ("age", DocumentValue::Integer(30)),
                    ("region", DocumentValue::String("apac".to_owned())),
                ],
            ),
            object_document(
                "region-only",
                "users",
                &[
                    ("age", DocumentValue::Integer(50)),
                    ("region", DocumentValue::String("emea".to_owned())),
                ],
            ),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![
            QueryCondition::Equals { field: "age".to_owned(), value: DocumentValue::Integer(30) },
            QueryCondition::Equals {
                field: "region".to_owned(),
                value: DocumentValue::String("emea".to_owned()),
            },
        ]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    assert_eq!(row_ids(&store.execute(&plan, &snapshot).expect("executes")), ["both"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// A null `BETWEEN` bound is unbounded. Both bounds
/// unbounded matches every document that carries the field, explicit null
/// included ("matching lucene results, nulls included"); only the upper
/// bound unbounded also admits explicit nulls (the null term is OR-ed in);
/// only the lower bound unbounded is the plain upper comparison; two bounds
/// are the inclusive range over orderable values, which explicit null and
/// absence never satisfy.
#[test]
fn between_pins_null_bound_semantics() {
    let (dir, engine) = open_engine("gap-eval-q2-between");
    let tenant_scope = scope(&engine, "tenant-a");
    let between = |low: DocumentValue, high: DocumentValue| QueryCondition::Between {
        field: "n".to_owned(),
        low,
        high,
    };
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("absent", "users", &[("name", DocumentValue::String("a".to_owned()))]),
            object_document("null-n", "users", &[("n", DocumentValue::Null)]),
            object_document("one", "users", &[("n", DocumentValue::Integer(1))]),
            object_document("five", "users", &[("n", DocumentValue::Integer(5))]),
            object_document("nine", "users", &[("n", DocumentValue::Integer(9))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |condition: QueryCondition| -> Vec<String> {
        let query = spec(
            "tenant-a",
            "users",
            predicate(vec![condition]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        let mut ids = row_ids(&store.execute(&plan, &snapshot).expect("executes"));
        ids.sort();
        ids
    };
    let null = DocumentValue::Null;
    let int = |value: i64| DocumentValue::Integer(value);

    // Both bounds unbounded: exists-or-null, absence excluded.
    assert_eq!(run(between(null.clone(), null.clone())), ["five", "nine", "null-n", "one"]);
    // Lower unbounded: the upper comparison only; null and absence excluded.
    assert_eq!(run(between(null.clone(), int(5))), ["five", "one"]);
    // Upper unbounded: the lower comparison OR the explicit null.
    assert_eq!(run(between(int(5), null.clone())), ["five", "nine", "null-n"]);
    // Two bounds: the inclusive range over orderable values.
    assert_eq!(run(between(int(1), int(5))), ["five", "one"]);
    assert_eq!(run(between(int(2), int(4))), Vec::<String>::new());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// `in` covers scalar membership and any matching element of an array.
/// `all in` requires every listed value. An empty `all in` list matches
/// everything. Absence matches neither non-empty form.
#[test]
fn in_and_all_in_pin_membership_and_array_semantics() {
    let (dir, engine) = open_engine("gap-eval-q3-in");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("absent", "users", &[("name", DocumentValue::String("a".to_owned()))]),
            object_document("scalar", "users", &[("tag", DocumentValue::String("red".to_owned()))]),
            object_document(
                "array",
                "users",
                &[(
                    "tag",
                    DocumentValue::Array(vec![
                        DocumentValue::String("red".to_owned()),
                        DocumentValue::String("green".to_owned()),
                    ]),
                )],
            ),
            object_document("other", "users", &[("tag", DocumentValue::String("blue".to_owned()))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let values = |items: &[&str]| {
        items.iter().map(|item| DocumentValue::String((*item).to_owned())).collect::<Vec<_>>()
    };
    let run = |condition: QueryCondition| -> Vec<String> {
        let query = spec(
            "tenant-a",
            "users",
            predicate(vec![condition]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        let mut ids = row_ids(&store.execute(&plan, &snapshot).expect("executes"));
        ids.sort();
        ids
    };

    // Any-match: the scalar, the array carrying one of the values, never
    // absence.
    assert_eq!(
        run(QueryCondition::In { field: "tag".to_owned(), values: values(&["red", "blue"]) }),
        ["array", "other", "scalar"]
    );
    assert_eq!(
        run(QueryCondition::In { field: "tag".to_owned(), values: values(&["green"]) }),
        ["array"]
    );
    // Every listed value carried: only the array with both.
    assert_eq!(
        run(QueryCondition::AllIn { field: "tag".to_owned(), values: values(&["red", "green"]) }),
        ["array"]
    );
    // The empty lists: any-of-nothing matches nothing; all-of-nothing is
    // V1's TrueToken and matches everything, absence included.
    assert_eq!(
        run(QueryCondition::In { field: "tag".to_owned(), values: Vec::new() }),
        Vec::<String>::new()
    );
    assert_eq!(
        run(QueryCondition::AllIn { field: "tag".to_owned(), values: Vec::new() }),
        ["absent", "array", "other", "scalar"]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Q3 (PB-4007): prefix, suffix, and substring conditions match the string
/// domain — a string field directly, or any string element of an array
/// field because the flattened term space is per-value. Exact-mode matching
/// is case-sensitive. Analyzed-mode behavior belongs to the analyzer
/// foundation.
#[test]
fn pattern_conditions_pin_prefix_suffix_and_substring_semantics() {
    let (dir, engine) = open_engine("gap-eval-q3-pattern");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "plain",
                "users",
                &[("name", DocumentValue::String("Adelaide".to_owned()))],
            ),
            object_document(
                "array",
                "users",
                &[(
                    "name",
                    DocumentValue::Array(vec![
                        DocumentValue::String("Ada".to_owned()),
                        DocumentValue::String("Grace".to_owned()),
                    ]),
                )],
            ),
            object_document(
                "other",
                "users",
                &[("name", DocumentValue::String("madam".to_owned()))],
            ),
            object_document("null-name", "users", &[("name", DocumentValue::Null)]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |condition: QueryCondition| -> Vec<String> {
        let query = spec(
            "tenant-a",
            "users",
            predicate(vec![condition]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        let mut ids = row_ids(&store.execute(&plan, &snapshot).expect("executes"));
        ids.sort();
        ids
    };

    assert_eq!(
        run(QueryCondition::StartsWith { field: "name".to_owned(), value: "Ad".to_owned() }),
        ["array", "plain"]
    );
    assert_eq!(
        run(QueryCondition::EndsWith { field: "name".to_owned(), value: "ide".to_owned() }),
        ["plain"]
    );
    assert_eq!(
        run(QueryCondition::Contains { field: "name".to_owned(), value: "aid".to_owned() }),
        ["plain"]
    );
    // Case-sensitive exact matching: 'ad' does not match 'Adelaide'.
    assert_eq!(
        run(QueryCondition::StartsWith { field: "name".to_owned(), value: "ad".to_owned() }),
        Vec::<String>::new()
    );
    // Non-string values never match a pattern.
    assert_eq!(
        run(QueryCondition::Contains { field: "name".to_owned(), value: "a".to_owned() }),
        ["array", "other", "plain"]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Explicit null placement is direction-independent. First means "nulls
/// before values" and Last means "nulls after values" under both ascending
/// and descending sorts. The missing-and-null
/// group moves as one block, because an index sort cannot distinguish an
/// absent field from an explicit null; inside the block the seam's tracer
/// order (missing before null) stays.
#[test]
fn explicit_nulls_placement_is_direction_independent() {
    use super::NullsPlacement;
    let (dir, engine) = open_engine("gap-eval-q4-nulls");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("absent", "users", &[("name", DocumentValue::String("a".to_owned()))]),
            object_document("null-a", "users", &[("a", DocumentValue::Null)]),
            object_document("one", "users", &[("a", DocumentValue::Integer(1))]),
            object_document("three", "users", &[("a", DocumentValue::Integer(3))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |direction: OrderDirection, placement: NullsPlacement| -> Vec<String> {
        let key = OrderingKey::try_new_with_nulls("a", direction, placement).expect("valid key");
        let query = spec(
            "tenant-a",
            "users",
            predicate(Vec::new()),
            QueryOrdering::try_new(vec![key]).expect("valid ordering"),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        row_ids(&store.execute(&plan, &snapshot).expect("executes"))
    };

    assert_eq!(
        run(OrderDirection::Ascending, NullsPlacement::First),
        ["absent", "null-a", "one", "three"]
    );
    assert_eq!(
        run(OrderDirection::Descending, NullsPlacement::First),
        ["absent", "null-a", "three", "one"]
    );
    assert_eq!(
        run(OrderDirection::Ascending, NullsPlacement::Last),
        ["one", "three", "absent", "null-a"]
    );
    assert_eq!(
        run(OrderDirection::Descending, NullsPlacement::Last),
        ["three", "one", "absent", "null-a"]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Seeded random ordering is deterministic per seed. The contract pins
/// determinism and seed sensitivity, not one canonical sequence.
#[test]
fn random_with_seed_orders_deterministically_per_seed() {
    let (dir, engine) = open_engine("gap-eval-q4-random");
    let tenant_scope = scope(&engine, "tenant-a");
    let documents: Vec<Document> = (0..32)
        .map(|index| {
            object_document(
                &format!("doc-{index:02}"),
                "users",
                &[("n", DocumentValue::Integer(i64::from(index)))],
            )
        })
        .collect();
    commit_documents(&engine, &tenant_scope, "op-1", documents);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |seed: u64| -> Vec<String> {
        let query = spec(
            "tenant-a",
            "users",
            predicate(Vec::new()),
            QueryOrdering::random_with_seed(seed),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        row_ids(&store.execute(&plan, &snapshot).expect("executes"))
    };

    let first = run(42);
    let again = run(42);
    assert_eq!(first, again, "one seed keeps one order across executions");
    let other = run(43);
    assert_ne!(first, other, "a different seed moves the order");
    // The shuffle is a permutation: no document is lost or duplicated.
    let mut sorted = first.clone();
    sorted.sort();
    let mut expected: Vec<String> = (0..32).map(|index| format!("doc-{index:02}")).collect();
    expected.sort();
    assert_eq!(sorted, expected);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// `load` resolves the referenced source document. Each alias binds to the
/// document whose identity the path reads, and the projected value comes
/// from that source document. Serving a projection from stored index fields is the
/// GAP-Q6-gated half and does not exist here. A reference that resolves to
/// no document binds null (the reference contract V1's id-lookup `LOAD`
/// defines), and an absent field of a resolved reference is omitted, staying distinct from a
/// present null.
#[test]
fn loads_resolve_referenced_source_documents_into_projection() {
    use super::QueryLoad;
    let (dir, engine) = open_engine("gap-eval-q5-load");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "order-1",
                "orders",
                &[
                    ("total", DocumentValue::Integer(10)),
                    ("customerId", DocumentValue::String("customer-1".to_owned())),
                ],
            ),
            object_document(
                "order-2",
                "orders",
                &[
                    ("total", DocumentValue::Integer(20)),
                    ("customerId", DocumentValue::String("customer-2".to_owned())),
                ],
            ),
            object_document(
                "order-3",
                "orders",
                &[
                    ("total", DocumentValue::Integer(30)),
                    ("customerId", DocumentValue::String("customer-x".to_owned())),
                ],
            ),
            object_document(
                "customer-1",
                "customers",
                &[("name", DocumentValue::String("Ada".to_owned()))],
            ),
            object_document(
                "customer-2",
                "customers",
                &[("region", DocumentValue::String("emea".to_owned()))],
            ),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "orders",
        predicate(Vec::new()),
        ordering(vec![("total", OrderDirection::Ascending)]),
        projection_fields(&["total", "c.name"]),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    )
    .with_loads(vec![QueryLoad::try_new("customerId", "c").expect("valid load")])
    .expect("loads bind");
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    let fields_of = |position: usize| {
        result.rows()[position]
            .projected()
            .iter()
            .map(|(name, value)| (name.as_str(), value.clone()))
            .collect::<Vec<_>>()
    };
    // The referenced source document's field, resolved by id across the
    // collection boundary.
    assert_eq!(
        fields_of(0),
        [
            ("total", DocumentValue::Integer(10)),
            ("c.name", DocumentValue::String("Ada".to_owned()))
        ]
    );
    // A resolved reference without the field omits it.
    assert_eq!(fields_of(1), [("total", DocumentValue::Integer(20))]);
    // A reference to no document binds null and navigation yields null.
    assert_eq!(
        fields_of(2),
        [("total", DocumentValue::Integer(30)), ("c.name", DocumentValue::Null)]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// GAP-EVAL repair (Q2 rider): NaN is unordered, so every ordering condition
/// against a NaN literal is false in the mixed Integer/Number arms exactly as
/// it already is in the same-domain Number arm (`ordered_f64` is
/// `partial_cmp`, and V1's numeric range terms never match NaN either).
/// Documents provably carry `Number(NaN)` (the order-by corpus above), and
/// the Q2 exact-comparison rewrite routed the predicate's mixed arms through
/// `compare_integer_with_number`, whose finite-only precondition the rank
/// gate enforces only in the order-by path: with a NaN literal both sign
/// gates are false and `NaN.trunc() as i128` saturates to 0, so the
/// predicate decided against a phantom 0 — `Integer(5)` answered
/// `> Number(NaN)` true, and a `Between` with a NaN bound admitted half the
/// range (`Integer(-3)` but not `Integer(5)`).
#[test]
fn mixed_nan_literals_make_every_ordering_condition_false() {
    let (dir, engine) = open_engine("gap-eval-q2-nan-literals");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("neg", "users", &[("n", DocumentValue::Integer(-3))]),
            object_document("zero", "users", &[("n", DocumentValue::Integer(0))]),
            object_document("five", "users", &[("n", DocumentValue::Integer(5))]),
            object_document("nan-doc", "users", &[("n", DocumentValue::Number(f64::NAN))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |condition: QueryCondition| -> Vec<String> {
        let query = spec(
            "tenant-a",
            "users",
            predicate(vec![condition]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        let mut ids = row_ids(&store.execute(&plan, &snapshot).expect("executes"));
        ids.sort();
        ids
    };
    let nan = || DocumentValue::Number(f64::NAN);
    let field = "n";

    // Integer documents against Number(NaN) literals: no value orders
    // against NaN, so no inequality holds in either direction and neither
    // inclusive form falls back to equality.
    assert_eq!(
        run(QueryCondition::GreaterThan { field: field.to_owned(), value: nan() }),
        Vec::<String>::new(),
        "Integer(5) must not answer > Number(NaN) through a phantom 0"
    );
    assert_eq!(
        run(QueryCondition::LessThan { field: field.to_owned(), value: nan() }),
        Vec::<String>::new(),
        "Integer(-3) must not answer < Number(NaN)"
    );
    assert_eq!(
        run(QueryCondition::GreaterOrEqual { field: field.to_owned(), value: nan() }),
        Vec::<String>::new(),
        "Integer(0) must not answer >= Number(NaN) as phantom equality"
    );
    assert_eq!(
        run(QueryCondition::LessOrEqual { field: field.to_owned(), value: nan() }),
        Vec::<String>::new(),
        "Integer(0) must not answer <= Number(NaN) as phantom equality"
    );

    // A Between with either NaN bound is unordered on that side, so the
    // inclusive range matches nothing — not half of it.
    assert_eq!(
        run(QueryCondition::Between {
            field: field.to_owned(),
            low: DocumentValue::Integer(-10),
            high: nan(),
        }),
        Vec::<String>::new(),
        "a NaN upper bound must not admit Integer(-3) by comparing against 0"
    );
    assert_eq!(
        run(QueryCondition::Between {
            field: field.to_owned(),
            low: nan(),
            high: DocumentValue::Integer(10),
        }),
        Vec::<String>::new(),
        "a NaN lower bound must not admit Integer(0) and Integer(5)"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// GAP-EVAL repair (Q2 rider, symmetric half): the phantom-0 defect also
/// crossed the encodings the other way — a `Number(NaN)` document compared
/// against an `Integer` literal ran the same helper with the operands
/// swapped, so the NaN document answered `>=` and `<=` an integer through a
/// phantom equality. NaN stays unordered on that side too, while the
/// infinities keep their sign-gate answers (every integer is below `+inf`
/// and above `-inf`), which pins that the fix routes NaN only.
#[test]
fn nan_documents_answer_no_integer_inequality_and_infinities_keep_their_signs() {
    let (dir, engine) = open_engine("gap-eval-q2-nan-documents");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("neg", "users", &[("n", DocumentValue::Integer(-3))]),
            object_document("zero", "users", &[("n", DocumentValue::Integer(0))]),
            object_document("five", "users", &[("n", DocumentValue::Integer(5))]),
            object_document("nan-doc", "users", &[("n", DocumentValue::Number(f64::NAN))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |condition: QueryCondition| -> Vec<String> {
        let query = spec(
            "tenant-a",
            "users",
            predicate(vec![condition]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        );
        let plan = store.compile(QueryInput::typed(query)).expect("compiles");
        let mut ids = row_ids(&store.execute(&plan, &snapshot).expect("executes"));
        ids.sort();
        ids
    };
    let field = "n";

    // A Number(NaN) document against Integer literals: the NaN document
    // answers no integer inequality (phantom equality against 0 used to
    // satisfy >= and <=).
    assert_eq!(
        run(QueryCondition::GreaterOrEqual {
            field: field.to_owned(),
            value: DocumentValue::Integer(0)
        }),
        ["five", "zero"],
        "Number(NaN) must not answer >= Integer(0)"
    );
    assert_eq!(
        run(QueryCondition::LessOrEqual {
            field: field.to_owned(),
            value: DocumentValue::Integer(0)
        }),
        ["neg", "zero"],
        "Number(NaN) must not answer <= Integer(0)"
    );

    // The infinities keep their sign-gate answers: every integer is below
    // +inf and above -inf, and the NaN document still answers nothing.
    assert_eq!(
        run(QueryCondition::LessThan {
            field: field.to_owned(),
            value: DocumentValue::Number(f64::INFINITY)
        }),
        ["five", "neg", "zero"]
    );
    assert_eq!(
        run(QueryCondition::GreaterThan {
            field: field.to_owned(),
            value: DocumentValue::Number(f64::NEG_INFINITY)
        }),
        ["five", "neg", "zero"]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// GAP-EVAL repair (Q5 rider): a load resolves a bare id by the engine's own
/// identifier rule, not by silent last-wins. Storage keys documents by
/// tenant, collection, and identifier, and nothing forbids reusing one
/// identifier across a tenant's collections, so one bare id can name two
/// stored documents; the engine's identifier-only point read
/// (`Transaction::read` → `read_document_at` → `find_by_document_id`) serves
/// the first match in canonical key order. The load map was built
/// last-wins over the same snapshot, so the same id answered two different
/// ways. The map now keeps the first match in canonical key order: the load
/// binds exactly the document the point read serves. The decision is
/// recorded in docs/query/gap-eval-evaluator-parity.md.
#[test]
fn loads_resolve_a_reused_id_the_way_the_point_read_does() {
    use super::QueryLoad;
    let (dir, engine) = open_engine("gap-eval-q5-reuse");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "order-1",
                "orders",
                &[
                    ("total", DocumentValue::Integer(1)),
                    ("refId", DocumentValue::String("shared-1".to_owned())),
                ],
            ),
            // The same document id committed in two collections of one
            // tenant: "alpha" sorts before "beta" in canonical key order, so
            // the point read serves the alpha copy.
            object_document(
                "shared-1",
                "alpha",
                &[("marker", DocumentValue::String("alpha-copy".to_owned()))],
            ),
            object_document(
                "shared-1",
                "beta",
                &[("marker", DocumentValue::String("beta-copy".to_owned()))],
            ),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");

    // The engine's identifier-only read, the rule the load must agree with.
    let operation = OperationId::try_new("op-read").expect("valid identifier");
    let transaction = engine.begin(&tenant_scope, snapshot.clone(), operation).expect("begins");
    let point_read = transaction
        .read(&DocumentId::try_new("shared-1").expect("valid identifier"))
        .expect("reads");
    let point_read = point_read.expect("the reused id resolves for the point read");
    assert_eq!(point_read.collection().as_str(), "alpha");
    drop(transaction);

    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "orders",
        predicate(Vec::new()),
        QueryOrdering::default(),
        projection_fields(&["total", "r.marker", "r"]),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    )
    .with_loads(vec![QueryLoad::try_new("refId", "r").expect("valid load")])
    .expect("loads bind");
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["order-1"]);
    let projected = result.rows()[0]
        .projected()
        .iter()
        .map(|(name, value)| (name.as_str(), value.clone()))
        .collect::<Vec<_>>();
    // The qualified field and the bare alias both read the alpha copy — the
    // same document the point read served — never the beta copy that
    // last-wins keyed.
    assert_eq!(
        projected,
        [
            ("total", DocumentValue::Integer(1)),
            ("r.marker", DocumentValue::String("alpha-copy".to_owned())),
            ("r", object(&[("marker", DocumentValue::String("alpha-copy".to_owned()))])),
        ]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// GAP-EVAL rider: the optimizer's demand derivation must classify the
/// expanded conditions — a dynamic query carrying them selects or creates
/// its index and still answers, rather than failing closed on an
/// unclassified condition (charter PD-2026-08-26-09: no query silently
/// scans without its index).
#[test]
fn dynamic_queries_with_expanded_conditions_select_through_the_optimizer() {
    let (dir, engine) = open_engine("gap-eval-optimizer");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "one",
                "users",
                &[
                    ("tag", DocumentValue::String("red".to_owned())),
                    ("age", DocumentValue::Integer(5)),
                    ("name", DocumentValue::String("anne".to_owned())),
                ],
            ),
            object_document("two", "users", &[("tag", DocumentValue::String("blue".to_owned()))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![
            QueryCondition::In {
                field: "tag".to_owned(),
                values: vec![DocumentValue::String("red".to_owned())],
            },
            QueryCondition::Between {
                field: "age".to_owned(),
                low: DocumentValue::Integer(1),
                high: DocumentValue::Integer(9),
            },
            QueryCondition::StartsWith { field: "name".to_owned(), value: "a".to_owned() },
        ]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes and selects");
    assert_eq!(row_ids(&result), ["one"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn result_statistics_report_the_scan_accounting() {
    let (dir, engine) = open_engine("stats-scan");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("match-1", "users", &[("age", DocumentValue::Integer(2))]),
            object_document("match-2", "users", &[("age", DocumentValue::Integer(3))]),
            object_document("match-3", "users", &[("age", DocumentValue::Integer(4))]),
            object_document("reject-1", "users", &[("age", DocumentValue::Integer(1))]),
            object_document("reject-2", "users", &[("age", DocumentValue::Integer(0))]),
            object_document("other-1", "cities", &[("age", DocumentValue::Integer(9))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::GreaterThan {
            field: "age".to_owned(),
            value: DocumentValue::Integer(1),
        }]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");

    let result = store.execute(&plan, &snapshot).expect("executes");
    // The scan accounting is the FILTER accounting of V1's statistics: the
    // candidates examined, and the candidates examined but rejected. Only
    // documents of the source collection are candidates; the `cities`
    // document is neither scanned nor skipped. The first query
    // auto-registers `age` inline (D-31), so the pushed scan hands the
    // executor exactly the three matches: the two rejects never surface.
    assert_eq!(result.matched(), 3);
    assert_eq!(result.rows().len(), 3);
    assert_eq!(result.statistics().scanned(), 3);
    assert_eq!(result.statistics().skipped(), 0);
    // With no limit requested, the capped count is the matched count.
    assert_eq!(result.statistics().capped_matched(), 3);
    // An index-less query has no namespace observation to be stale about.
    assert!(!result.staleness().is_stale());

    // The etag is a function of the state the query answered from: the same
    // plan over the same snapshot reports the same etag.
    let again = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(result.statistics().result_etag(), again.statistics().result_etag());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn statistics_count_caps_are_typed_exact_and_saturating() {
    let (dir, engine) = open_engine("stats-caps");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))]),
            object_document("doc-2", "users", &[("age", DocumentValue::Integer(2))]),
            object_document("doc-3", "users", &[("age", DocumentValue::Integer(3))]),
            object_document("doc-4", "users", &[("age", DocumentValue::Integer(4))]),
            object_document("doc-5", "users", &[("age", DocumentValue::Integer(5))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();

    // A paged window: the matched count stays exact, the capped count is the
    // window's size, and nothing truncates the matched count itself.
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::Exists { field: "age".to_owned() }]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(1, Some(2)),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(result.rows().len(), 2);
    assert_eq!(result.matched(), 5);
    assert_eq!(result.statistics().capped_matched(), 2);
    assert_eq!(result.statistics().skipped(), 0);

    // An offset beyond the matched set saturates the capped count at zero;
    // V1's collection path can compute a negative cap
    // (`Math.Min(limit, TotalResults - offset)` unclamped) and V2 never does.
    let query = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::Exists { field: "age".to_owned() }]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(9, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(query)).expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert!(result.rows().is_empty());
    assert_eq!(result.matched(), 5);
    assert_eq!(result.statistics().capped_matched(), 0);

    // The page-limit cap refuses typed at construction instead of truncating
    // an answer with an in-band marker: a limit at `MAX_PAGE_LIMIT` is valid
    // and one above it is refused.
    assert!(QueryPaging::try_new(0, Some(MAX_PAGE_LIMIT)).is_ok());
    assert_eq!(
        QueryPaging::try_new(0, Some(MAX_PAGE_LIMIT + 1)).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn statistics_result_etag_tracks_the_state_the_query_answered_from() {
    let (dir, engine) = open_engine("stats-etag");
    let tenant_scope = scope(&engine, "tenant-a");
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

    // One snapshot, two gate states: the etag names the state the answer was
    // computed from, so a different accepted generation is a different etag.
    let current = ForgedView::with(&required, Some((1, 2)), Some((1, 2)));
    let older = ForgedView::with(&required, Some((1, 2)), Some((1, 1)));
    let current_result = execute_with_view(&engine, &current, &plan, &snapshot).expect("executes");
    let older_result = execute_with_view(&engine, &older, &plan, &snapshot).expect("executes");
    assert_ne!(current_result.statistics().result_etag(), older_result.statistics().result_etag());

    // A new committed state is a new etag, through the live catalog path.
    let first = store.execute(&plan, &snapshot).expect("executes");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![object_document("doc-3", "users", &[("age", DocumentValue::Integer(3))])],
    );
    let next = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let second = store.execute(&plan, &next).expect("executes");
    assert_ne!(first.statistics().result_etag(), second.statistics().result_etag());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn diagnostic_duration_reports_an_execution_observation_only() {
    let (dir, engine) = open_engine("stats-duration");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let query = simple_spec("tenant-a", "users");
    let plan = engine.queries().compile(QueryInput::typed(query)).expect("compiles");
    let result = engine.queries().execute(&plan, &snapshot).expect("executes");
    // The duration is a diagnostic observation of this one execution window.
    // It is recorded for every successful execution, and nothing may be
    // inferred from its magnitude: it is not a benchmark, not comparable
    // across engines or builds, and carries no performance claim.
    assert!(result.statistics().duration().elapsed() >= Duration::ZERO);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn staleness_report_aggregates_the_is_stale_flag() {
    let (dir, engine) = open_engine("stats-stale");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![object_document("doc-1", "users", &[("age", DocumentValue::Integer(1))])],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let required = namespace("tenant-a", "by-age");
    let store = engine.queries();

    // An allow-stale query that accepted an older generation reports stale.
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
    let view = ForgedView::with(&required, Some((1, 4)), Some((1, 2)));
    let result = execute_with_view(&engine, &view, &plan, &snapshot).expect("executes");
    assert!(result.staleness().is_stale());

    // A strict query that passed the gate on an equal, current generation is
    // not stale.
    let strict = spec(
        "tenant-a",
        "users",
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, vec![required.clone()]),
    );
    let plan = store.compile(QueryInput::typed(strict)).expect("compiles");
    let view = ForgedView::with(&required, Some((1, 2)), Some((1, 2)));
    let result = execute_with_view(&engine, &view, &plan, &snapshot).expect("executes");
    assert!(!result.staleness().is_stale());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---- S-02: the pushed-down SQL scan over extracted columns (D-42) --------

#[test]
fn extracted_columns_push_the_predicate_into_the_sql_scan() {
    let (dir, engine) = open_engine("pushdown-scan");
    let tenant_scope = scope(&engine, "tenant-a");
    let users = CollectionName::try_new("users").expect("valid collection");
    // The descriptor registers before any document exists: nothing to
    // backfill, active at once, and the write path extracts from here on.
    assert_eq!(
        engine.register_index_field(&users, "status"),
        Ok(crate::engine::DescriptorRegistration::Active),
        "a fresh collection registers active"
    );
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-1", "users", &[("status", DocumentValue::String("paid".into()))]),
            object_document("doc-2", "users", &[("status", DocumentValue::String("paid".into()))]),
            object_document(
                "doc-3",
                "users",
                &[("status", DocumentValue::String("shipped".into()))],
            ),
            object_document("doc-4", "users", &[("note", DocumentValue::String("n/a".into()))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "users",
            predicate(vec![QueryCondition::Equals {
                field: "status".to_owned(),
                value: DocumentValue::String("paid".into()),
            }]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-1", "doc-2"]);
    assert_eq!(
        result.statistics().scanned(),
        2,
        "the pushed scan reads only the matching rows, not the collection"
    );
    // An order over the extracted column rides the same scan: the
    // candidate set is already exact, the ordering applies in the executor.
    let ordered = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "users",
            predicate(vec![QueryCondition::GreaterOrEqual {
                field: "status".to_owned(),
                value: DocumentValue::String("paid".into()),
            }]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, Some(2)),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    let result = store.execute(&ordered, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-1", "doc-2"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---- S-02 slice C: the first-query auto-index policy (D-31) -------------
//
// An uncovered pushable field registers on the first query that needs it: a
// small collection backfills inline, so the triggering query already pushes
// and the descriptor is active the moment registration commits.

#[test]
fn first_query_registers_pushdown_fields_inline() {
    let (dir, engine) = open_engine("pushdown-first-query");
    let tenant_scope = scope(&engine, "tenant-a");
    let orders = CollectionName::try_new("orders").expect("valid collection");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document("doc-1", "orders", &[("total", DocumentValue::Integer(10))]),
            object_document("doc-2", "orders", &[("total", DocumentValue::Integer(90))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "orders",
            predicate(vec![QueryCondition::GreaterThan {
                field: "total".to_owned(),
                value: DocumentValue::Integer(20),
            }]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-2"]);
    assert_eq!(
        result.statistics().scanned(),
        1,
        "the first query registers inline and already pushes"
    );
    assert!(
        engine.index_descriptors().expect("lists descriptors").iter().any(|listing| listing
            .collection
            == orders.as_str()
            && listing.field == "total"
            && listing.active),
        "the first query leaves an active descriptor behind"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn auto_index_audit_joins_observed_shapes_with_descriptor_coverage() {
    let (dir, engine) = open_engine("auto-index-audit");
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
                    ("status", DocumentValue::String("paid".into())),
                    ("flag", DocumentValue::Boolean(true)),
                ],
            ),
            object_document(
                "doc-2",
                "users",
                &[
                    ("status", DocumentValue::String("free".into())),
                    ("flag", DocumentValue::Boolean(false)),
                ],
            ),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    // A scalar predicate auto-registers its field inline.
    let plan = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "users",
            predicate(vec![QueryCondition::Equals {
                field: "status".to_owned(),
                value: DocumentValue::String("paid".into()),
            }]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    assert_eq!(row_ids(&store.execute(&plan, &snapshot).expect("executes")), ["doc-1"]);
    // A boolean equality is observed but never pushable: the columns do not
    // carry booleans, so no descriptor registers for it.
    let booleans = spec(
        "tenant-a",
        "users",
        predicate(vec![QueryCondition::Equals {
            field: "flag".to_owned(),
            value: DocumentValue::Boolean(true),
        }]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, None),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let boolean_plan = store.compile(QueryInput::typed(booleans)).expect("compiles");
    assert_eq!(row_ids(&store.execute(&boolean_plan, &snapshot).expect("executes")), ["doc-1"]);
    let audit = |entries: &[AutoIndexRecommendation], field: &str| {
        entries.iter().find(|entry| entry.field == field).cloned().expect("shape audited")
    };
    let entries = engine.auto_index_audit(&tenant("tenant-a")).expect("the audit reads");
    assert_eq!(
        entries
            .iter()
            .map(|entry| (entry.collection.as_str(), entry.field.as_str()))
            .collect::<Vec<_>>(),
        [("users", "flag"), ("users", "status")],
        "the audit is deterministic (collection, field) order"
    );
    let status = audit(&entries, "status");
    assert_eq!(status.observed_queries, 1);
    assert_eq!(status.coverage, DescriptorCoverage::Active);
    let flag = audit(&entries, "flag");
    assert_eq!(flag.observed_queries, 1);
    assert_eq!(flag.coverage, DescriptorCoverage::Absent);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn large_collection_first_query_is_not_ready_until_the_governed_backfill_completes() {
    let (dir, engine) = open_engine("governed-backfill-lifecycle");
    let tenant_scope = scope(&engine, "tenant-a");
    // One wave past the inline cost cap: every row shares the wave's single
    // commit sequence, which is exactly the shape that must never strand.
    // Eleven evenly spaced needles plus one the next commit retracts.
    let bulk = governed_backfill_documents();
    commit_documents(&engine, &tenant_scope, "op-bulk", bulk);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let plan = governed_backfill_plan(
        &store,
        QueryCondition::Equals {
            field: "tag".to_owned(),
            value: DocumentValue::String("needle".into()),
        },
    );
    assert_eq!(
        store.execute(&plan, &snapshot).expect("the segment serves").matched(),
        12,
        "the catalog segment serves the equality shape exactly"
    );
    // A range shape no segment covers exactly takes the scan path, and the
    // scan fails closed while the descriptor builds: a scan over
    // half-extracted columns would silently miss rows.
    let range = governed_backfill_plan(
        &store,
        QueryCondition::GreaterOrEqual {
            field: "tag".to_owned(),
            value: DocumentValue::String("n".into()),
        },
    );
    assert_eq!(
        store.execute(&range, &snapshot),
        Err(QueryError::IndexNotReady),
        "a building descriptor fails the scan closed with the retryable error"
    );
    let building = engine
        .auto_index_audit(&tenant("tenant-a"))
        .expect("the audit reads")
        .into_iter()
        .find(|entry| entry.field == "tag")
        .expect("the shape is audited");
    assert!(
        matches!(building.coverage, DescriptorCoverage::Building { backfill_remaining } if backfill_remaining > 0),
        "the audit reports the building descriptor and its owed rows"
    );
    // Writes that land while the descriptor builds carry extracted values on
    // the write path: a new match and the retraction of an old one.
    commit_documents(
        &engine,
        &tenant_scope,
        "op-during",
        vec![
            object_document(
                "late-needle",
                "events",
                &[("tag", DocumentValue::String("needle".into()))],
            ),
            object_document(
                "bulk-00500",
                "events",
                &[("tag", DocumentValue::String("bulk".into()))],
            ),
        ],
    );
    let mut active = false;
    for _ in 0..10 {
        let progress = engine.maintain_index_backfills().expect("the governed pass pumps");
        if progress.iter().any(|step| step.active) {
            active = true;
            break;
        }
    }
    assert!(active, "the bounded pass completes the descriptor");
    let fresh = engine.read_snapshot(&tenant_scope).expect("fresh snapshot");
    let result = store.execute(&plan, &fresh).expect("the query serves once active");
    assert_governed_backfill_rows(&row_ids(&result));
    let ranged = store.execute(&range, &fresh).expect("the range serves once active");
    assert_eq!(row_ids(&ranged), row_ids(&result));
    assert_eq!(
        ranged.statistics().scanned(),
        12,
        "the pushed scan reads exactly the live matches: backfilled rows, \
         write-path rows, and the retraction all resolve — zero stale reads"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

fn assert_governed_backfill_rows(ids: &[String]) {
    assert_eq!(
        ids,
        [
            "bulk-00000",
            "bulk-01000",
            "bulk-02000",
            "bulk-03000",
            "bulk-04000",
            "bulk-05000",
            "bulk-06000",
            "bulk-07000",
            "bulk-08000",
            "bulk-09000",
            "bulk-10000",
            "late-needle",
        ]
    );
}

fn governed_backfill_documents() -> Vec<Document> {
    (0..10_001u32)
        .map(|index| {
            let tag = if index.is_multiple_of(1000) || index == 500 { "needle" } else { "bulk" };
            object_document(
                &format!("bulk-{index:05}"),
                "events",
                &[("tag", DocumentValue::String(tag.into()))],
            )
        })
        .collect()
}

fn governed_backfill_plan(store: &QueryStore<'_>, condition: QueryCondition) -> super::QueryPlan {
    store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "events",
            predicate(vec![condition]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, Some(20)),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles")
}

// ---------------------------------------------------------------------------
// S-02 slice D: full-text search, TTL, explain, and the vector disposition
// ---------------------------------------------------------------------------

fn search(field: &str, query: &str) -> QueryCondition {
    QueryCondition::Search { field: field.to_owned(), query: query.to_owned(), match_any: false }
}

fn search_any(field: &str, query: &str) -> QueryCondition {
    QueryCondition::Search { field: field.to_owned(), query: query.to_owned(), match_any: true }
}

fn text_document(id: &str, collection: &str, field: &str, text: &str) -> Document {
    object_document(id, collection, &[(field, DocumentValue::String(text.to_owned()))])
}

/// Compiles one full-text search through the typed seam, with an optional
/// relevance rank travelling beside the plan (the shape the SQL planner
/// lowers `ORDER BY` relevance onto in E-4).
fn compile_search(
    store: &QueryStore,
    collection: &str,
    field: &str,
    query: &str,
    tenant_name: &str,
    rank: Option<OrderDirection>,
    limit: Option<u32>,
) -> super::QueryPlan {
    let planned = spec(
        tenant_name,
        collection,
        predicate(vec![search(field, query)]),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging(0, limit),
        staleness(StalenessMode::Strict, Vec::new()),
    );
    let plan = store.compile(QueryInput::typed(planned)).expect("the search compiles");
    match rank {
        Some(direction) => plan.with_search_rank(direction),
        None => plan,
    }
}

#[test]
fn search_matches_analyzed_terms_through_the_typed_seam() {
    let (dir, engine) = open_engine("search-typed");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            text_document("doc-term", "articles", "body", "The Quick brown fox"),
            text_document("doc-phrase", "articles", "body", "a quick brown dog naps"),
            text_document("doc-excluded", "articles", "body", "quick wolf dinner"),
            text_document("doc-number", "articles", "body", "entirely unrelated"),
            object_document("doc-nostring", "articles", &[("body", DocumentValue::Integer(7))]),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let run = |condition| {
        let plan = store
            .compile(QueryInput::typed(spec(
                "tenant-a",
                "articles",
                predicate(vec![condition]),
                QueryOrdering::default(),
                QueryProjection::whole_document(),
                paging(0, None),
                staleness(StalenessMode::Strict, Vec::new()),
            )))
            .expect("compiles");
        row_ids(&store.execute(&plan, &snapshot).expect("executes"))
    };
    assert_eq!(run(search("body", "quick")), ["doc-excluded", "doc-phrase", "doc-term"]);
    assert_eq!(run(search("body", "\"quick brown\"")), ["doc-phrase", "doc-term"]);
    assert_eq!(run(search("body", "qu*")), ["doc-excluded", "doc-phrase", "doc-term"]);
    assert_eq!(run(search("body", "quick -wolf")), ["doc-phrase", "doc-term"]);
    assert_eq!(run(search("body", "fox wolf")), Vec::<String>::new());
    assert_eq!(run(search_any("body", "fox wolf")), ["doc-excluded", "doc-term"]);
    assert_eq!(run(search("body", "!!!")), Vec::<String>::new());
    // A string array matches when any element does; a non-string never does.
    commit_documents(
        &engine,
        &tenant_scope,
        "op-2",
        vec![
            object_document(
                "doc-array",
                "articles",
                &[(
                    "body",
                    DocumentValue::Array(vec![
                        DocumentValue::String("nothing".to_owned()),
                        DocumentValue::String("quick silver".to_owned()),
                    ]),
                )],
            ),
            object_document(
                "doc-missing",
                "articles",
                &[("title", DocumentValue::String("quick".to_owned()))],
            ),
        ],
    );
    let fresh = engine.read_snapshot(&tenant_scope).expect("fresh snapshot");
    let plan = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "articles",
            predicate(vec![search("body", "quick")]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    assert_eq!(
        row_ids(&store.execute(&plan, &fresh).expect("executes")),
        ["doc-array", "doc-excluded", "doc-phrase", "doc-term"]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn first_search_registers_a_text_descriptor_and_reads_its_own_writes() {
    let (dir, engine) = open_engine("search-first-query");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            text_document("doc-1", "products", "description", "a red bicycle for city rides"),
            text_document("doc-2", "products", "description", "a blue kayak for calm lakes"),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "products",
            predicate(vec![search("description", "bicycle")]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-1"]);
    assert_eq!(
        result.statistics().scanned(),
        1,
        "the first search registers inline and already narrows"
    );
    // Index maintenance rides the commit transaction: a delete in a later
    // wave is invisible to the very next search — zero stale reads.
    let delete_snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction = engine
        .begin(
            &tenant_scope,
            delete_snapshot,
            OperationId::try_new("op-delete").expect("valid identifier"),
        )
        .expect("begins");
    transaction.delete_in_collection(
        &CollectionName::try_new("products").expect("valid collection"),
        DocumentId::try_new("doc-1").expect("valid identifier"),
        ExpectedVersion::Any,
    );
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("delete did not commit: {other:?}"),
    }
    let after = engine.read_snapshot(&tenant_scope).expect("fresh snapshot");
    assert!(
        store.execute(&plan, &after).expect("executes").rows().is_empty(),
        "the tombstone cleared the term index in its own transaction"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn search_with_score_orders_by_relevance() {
    let (dir, engine) = open_engine("search-ranked");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            text_document("doc-once", "products", "description", "apple zebra"),
            text_document(
                "doc-often",
                "products",
                "description",
                "apple apple apple apple apple zebra",
            ),
            text_document("doc-never", "products", "description", "a zebra with no fruit"),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let descending = compile_search(
        &store,
        "products",
        "description",
        "apple",
        "tenant-a",
        Some(OrderDirection::Descending),
        Some(10),
    );
    assert_eq!(
        row_ids(&store.execute(&descending, &snapshot).expect("executes")),
        ["doc-often", "doc-once"]
    );
    let ascending = compile_search(
        &store,
        "products",
        "description",
        "apple",
        "tenant-a",
        Some(OrderDirection::Ascending),
        Some(10),
    );
    assert_eq!(
        row_ids(&store.execute(&ascending, &snapshot).expect("executes")),
        ["doc-once", "doc-often"]
    );
    // The unranked form serves the same match set in source order.
    let unranked =
        compile_search(&store, "products", "description", "apple", "tenant-a", None, None);
    assert_eq!(
        row_ids(&store.execute(&unranked, &snapshot).expect("executes")),
        ["doc-often", "doc-once"]
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn large_collection_first_search_is_not_ready_until_the_text_backfill_completes() {
    let (dir, engine) = open_engine("search-governed-backfill");
    let tenant_scope = scope(&engine, "tenant-a");
    // One wave past the inline cost cap, so the first search registers a
    // building descriptor instead of an active one.
    let bulk = (0..10_001u32)
        .map(|index| {
            let body = if index.is_multiple_of(1000) {
                "a rare needle in the hay"
            } else {
                "plain bulk filler"
            };
            text_document(&format!("bulk-{index:05}"), "articles", "body", body)
        })
        .collect::<Vec<_>>();
    commit_documents(&engine, &tenant_scope, "op-bulk", bulk);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "articles",
            predicate(vec![search("body", "needle")]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, Some(20)),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    assert_eq!(
        store.execute(&plan, &snapshot),
        Err(QueryError::IndexNotReady),
        "a building text descriptor fails the search closed — a scan over a \
         half-indexed term stream would silently miss rows"
    );
    let explained = store.explain(&plan, &snapshot).expect("explains while building");
    assert_eq!(explained.access(), super::ExplainAccess::FullScan);
    assert_eq!(explained.building(), &["articles.body".to_owned()]);
    let text =
        explained.indexes().iter().find(|index| index.field == "body").expect("body indexed");
    assert_eq!(text.kind, super::ExplainIndexKind::Text);
    assert_eq!(text.state, super::ExplainIndexState::Building);
    let mut active = false;
    for _ in 0..10 {
        let progress = engine.maintain_index_backfills().expect("the governed pass pumps");
        if progress.iter().any(|step| step.active) {
            active = true;
            break;
        }
    }
    assert!(active, "the bounded pass completes the text descriptor");
    let fresh = engine.read_snapshot(&tenant_scope).expect("fresh snapshot");
    // A client retries with a fresh query after `IndexBuilding`: recompile
    // so the plan carries a live deadline past the backfill window.
    let retry = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "articles",
            predicate(vec![search("body", "needle")]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, Some(20)),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    let result = store.execute(&retry, &fresh).expect("the search serves once active");
    assert_eq!(result.matched(), 11);
    assert_eq!(
        result.statistics().scanned(),
        11,
        "the narrowed read touches exactly the live matches — zero stale reads"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
#[allow(clippy::too_many_lines)] // the fixture builds, backfills, and pins both top-k paths together
fn scale_100k_fts_and_vector_top_k_are_exact() {
    // S-02 done-when: FTS + vector top-k over a 100k-document fixture. One
    // collection carries both the searched text and the searched embedding,
    // so both top-k paths prove exactness over the same corpus: the FTS path
    // through the governed text backfill (the first search fails
    // `IndexNotReady`, the bounded pump completes the descriptor, the retry
    // narrows to exactly the planted needles), the vector path as the exact
    // cosine k-NN over the whole snapshot (every document scored, the
    // planted best direction first, scores descending). Both executes carry
    // a latency smoke bound beside their correctness pins.
    fn filler_embedding(index: u32) -> Vec<DocumentValue> {
        // Every component is positive, so the cosine with `[1,0,0,0]` is
        // strictly below 1 — the planted best direction is the unique top-1.
        [
            f64::from(index % 7 + 1),
            f64::from((index / 7) % 7 + 1),
            f64::from((index / 13) % 5 + 1),
            f64::from((index / 29) % 3 + 1),
        ]
        .into_iter()
        .map(DocumentValue::Number)
        .collect()
    }

    const DOCUMENTS: u32 = 100_000;
    const WAVE: u32 = 10_000;
    const NEEDLE_EVERY: u32 = 10_000;
    const NEEDLE_AT: u32 = 7;
    const NEEDLES: u64 = (DOCUMENTS / NEEDLE_EVERY) as u64;
    const BEST_INDEX: u32 = 42_424;
    const TOP_K: u32 = 10;

    let (dir, engine) = open_engine("scale-100k-topk");
    let tenant_scope = scope(&engine, "tenant-a");
    for wave in 0..DOCUMENTS / WAVE {
        let documents = (0..WAVE)
            .map(|offset| {
                let index = wave * WAVE + offset;
                let id = format!("doc-{index:06}");
                let body = if index % NEEDLE_EVERY == NEEDLE_AT {
                    format!("zephyrneedle brass telescope observation {index}")
                } else {
                    format!("bulk filler words {index}")
                };
                let embedding = if index == BEST_INDEX {
                    vec![
                        DocumentValue::Number(1.0),
                        DocumentValue::Number(0.0),
                        DocumentValue::Number(0.0),
                        DocumentValue::Number(0.0),
                    ]
                } else {
                    filler_embedding(index)
                };
                object_document(
                    &id,
                    "corpus",
                    &[
                        ("body", DocumentValue::String(body)),
                        ("embedding", DocumentValue::Array(embedding)),
                    ],
                )
            })
            .collect::<Vec<_>>();
        commit_documents(&engine, &tenant_scope, &format!("op-scale-{wave:02}"), documents);
    }
    let store = engine.queries();
    let first = compile_search(
        &store,
        "corpus",
        "body",
        "zephyrneedle",
        "tenant-a",
        Some(OrderDirection::Descending),
        Some(10),
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    assert_eq!(
        store.execute(&first, &snapshot),
        Err(QueryError::IndexNotReady),
        "the first search over 100k unindexed rows fails closed until the governed text backfill completes"
    );
    let mut active = false;
    for _ in 0..100 {
        let progress = engine.maintain_index_backfills().expect("the governed pass pumps");
        if progress.iter().any(|step| step.active) {
            active = true;
            break;
        }
    }
    assert!(active, "the bounded pass completes the 100k text descriptor");
    // A client retries with a fresh query after `IndexBuilding`: recompile
    // so the plan carries a live deadline past the backfill window.
    let fresh = engine.read_snapshot(&tenant_scope).expect("fresh snapshot");
    let ranked = compile_search(
        &store,
        "corpus",
        "body",
        "zephyrneedle",
        "tenant-a",
        Some(OrderDirection::Descending),
        Some(10),
    );
    let started = std::time::Instant::now();
    let result = store.execute(&ranked, &fresh).expect("the FTS top-10 serves once active");
    let fts_elapsed = started.elapsed();
    assert_eq!(
        result.matched(),
        NEEDLES,
        "the 100k FTS top-k resolves exactly the planted needles — no missing rows, no phantoms"
    );
    let mut actual = row_ids(&result);
    actual.sort();
    let mut expected = (0..NEEDLES)
        .map(|needle| {
            let index =
                u32::try_from(needle).expect("ten needles fit in u32") * NEEDLE_EVERY + NEEDLE_AT;
            format!("doc-{index:06}")
        })
        .collect::<Vec<_>>();
    expected.sort();
    assert_eq!(actual, expected, "the FTS top-10 returns exactly the needle set");
    assert!(
        !expected.contains(&format!("doc-{BEST_INDEX:06}")),
        "the best vector document carries filler text, so the needle set excludes it"
    );
    assert_eq!(
        result.statistics().scanned(),
        NEEDLES,
        "the narrowed read touches exactly the live matches — zero stale reads"
    );
    assert!(
        fts_elapsed < Duration::from_secs(30),
        "FTS top-10 latency smoke over 100k documents: {fts_elapsed:?}"
    );
    let request = VectorSearchRequest::try_new(
        source("corpus"),
        "embedding",
        vec![1.0, 0.0, 0.0, 0.0],
        TOP_K,
    )
    .expect("valid request");
    let started = std::time::Instant::now();
    let knn = store.vector_search(&request, &fresh).expect("the exact k-NN serves over 100k");
    let vector_elapsed = started.elapsed();
    assert!(knn.exact(), "the 100k vector top-k is the exact k-NN, never an approximation");
    assert_eq!(knn.statistics().scanned(), u64::from(DOCUMENTS));
    assert_eq!(
        knn.statistics().scored(),
        u64::from(DOCUMENTS),
        "every document of the 100k corpus is scored — the top-k is exact, not sampled"
    );
    assert_eq!(knn.statistics().returned(), u64::from(TOP_K));
    assert_eq!(knn.matches().len(), usize::try_from(TOP_K).expect("small top-k"));
    assert_eq!(
        knn.matches()[0].id().as_str(),
        format!("doc-{BEST_INDEX:06}"),
        "the planted best direction is the unique top-1"
    );
    assert!((knn.matches()[0].score() - 1.0).abs() < 1e-12);
    for pair in knn.matches().windows(2) {
        assert!(
            pair[0].score() >= pair[1].score(),
            "vector top-k scores descend: {} before {}",
            pair[0].score(),
            pair[1].score()
        );
    }
    assert!(
        vector_elapsed < Duration::from_secs(30),
        "vector top-10 latency smoke over 100k documents: {vector_elapsed:?}"
    );
    eprintln!(
        "scale-100k top-k: FTS top-10 in {fts_elapsed:?}, vector top-10 in {vector_elapsed:?}"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn ttl_expiry_hides_documents_and_the_sweep_tombstones_them() {
    let (dir, engine) = open_engine("ttl-sweep");
    let tenant_scope = scope(&engine, "tenant-a");
    let now_millis = i64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("wall clock")
            .as_millis(),
    )
    .unwrap_or(i64::MAX);
    let live =
        object_document("doc-live", "sessions", &[("user", DocumentValue::String("a".to_owned()))]);
    let future = Document::new(
        DocumentId::try_new("doc-future").expect("valid identifier"),
        CollectionName::try_new("sessions").expect("valid identifier"),
        DocumentValue::Object(
            [
                ("user".to_owned(), DocumentValue::String("b".to_owned())),
                ("expires_at".to_owned(), DocumentValue::Integer(now_millis + 3_600_000)),
            ]
            .into_iter()
            .collect(),
        ),
    );
    let past = Document::new(
        DocumentId::try_new("doc-past").expect("valid identifier"),
        CollectionName::try_new("sessions").expect("valid identifier"),
        DocumentValue::Object(
            [
                ("user".to_owned(), DocumentValue::String("c".to_owned())),
                ("expires_at".to_owned(), DocumentValue::Integer(now_millis - 1)),
            ]
            .into_iter()
            .collect(),
        ),
    );
    commit_documents(&engine, &tenant_scope, "op-1", vec![live, future, past]);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let plan =
        store.compile(QueryInput::typed(simple_spec("tenant-a", "sessions"))).expect("compiles");
    // Read-time filtering hides the elapsed key before any sweep runs.
    assert_eq!(
        row_ids(&store.execute(&plan, &snapshot).expect("executes")),
        ["doc-future", "doc-live"]
    );
    // Point reads agree: the past is already gone.
    let past_id = DocumentId::try_new("doc-past").expect("valid identifier");
    let sessions = CollectionName::try_new("sessions").expect("valid identifier");
    let transaction = engine
        .begin(&tenant_scope, snapshot, OperationId::try_new("op-probe").expect("valid identifier"))
        .expect("begins");
    assert!(transaction.read_in_collection(&sessions, &past_id).expect("reads").is_none());
    drop(transaction);
    // The sweep writes the tombstone; the key stays hidden and gains history.
    assert_eq!(engine.sweep_expired_ttl(256).expect("sweeps"), 1);
    assert_eq!(engine.sweep_expired_ttl(256).expect("the second sweep owes nothing"), 0);
    let fresh = engine.read_snapshot(&tenant_scope).expect("fresh snapshot");
    assert_eq!(
        row_ids(&store.execute(&plan, &fresh).expect("executes")),
        ["doc-future", "doc-live"]
    );
    let history = engine
        .document_history_in_collection(&tenant_scope, &sessions, &past_id, 10)
        .expect("history reads");
    assert!(history.iter().any(|entry| entry.deleted), "the sweep left a tombstone revision");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn search_never_serves_an_expired_match() {
    let (dir, engine) = open_engine("search-ttl");
    let tenant_scope = scope(&engine, "tenant-a");
    let now_millis = i64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("wall clock")
            .as_millis(),
    )
    .unwrap_or(i64::MAX);
    let stale = Document::new(
        DocumentId::try_new("doc-stale").expect("valid identifier"),
        CollectionName::try_new("products").expect("valid identifier"),
        DocumentValue::Object(
            [
                ("description".to_owned(), DocumentValue::String("red bicycle".to_owned())),
                ("expires_at".to_owned(), DocumentValue::Integer(now_millis - 1)),
            ]
            .into_iter()
            .collect(),
        ),
    );
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![stale, text_document("doc-fresh", "products", "description", "red bicycle")],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "products",
            predicate(vec![search("description", "bicycle")]),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            paging(0, None),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    assert_eq!(row_ids(&store.execute(&plan, &snapshot).expect("executes")), ["doc-fresh"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn explain_reports_access_indexes_order_and_building() {
    let (dir, engine) = open_engine("explain");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            object_document(
                "doc-1",
                "orders",
                &[
                    ("status", DocumentValue::String("paid".to_owned())),
                    ("note", DocumentValue::String("a quick note".to_owned())),
                ],
            ),
            object_document(
                "doc-2",
                "orders",
                &[
                    ("status", DocumentValue::String("open".to_owned())),
                    ("note", DocumentValue::String("another note".to_owned())),
                ],
            ),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    // A bare scan explains as a full input-order scan.
    let scan =
        store.compile(QueryInput::typed(simple_spec("tenant-a", "orders"))).expect("compiles");
    let explained = store.explain(&scan, &snapshot).expect("explains");
    assert_eq!(explained.collection(), "orders");
    assert_eq!(explained.access(), super::ExplainAccess::FullScan);
    assert_eq!(explained.order(), super::ExplainOrder::Input);
    assert!(explained.indexes().is_empty());
    assert!(explained.building().is_empty());
    assert!(explained.ttl_filtered());
    // Running the value query first activates its descriptor, so the same
    // query explains as a filtered, sorted scan over one active index.
    let filtered = store
        .compile(QueryInput::typed(spec(
            "tenant-a",
            "orders",
            predicate(vec![QueryCondition::Equals {
                field: "status".to_owned(),
                value: DocumentValue::String("paid".into()),
            }]),
            ordering(vec![("status", OrderDirection::Ascending)]),
            QueryProjection::whole_document(),
            paging(0, Some(5)),
            staleness(StalenessMode::Strict, Vec::new()),
        )))
        .expect("compiles");
    store.execute(&filtered, &snapshot).expect("the first query registers inline");
    let explained = store.explain(&filtered, &snapshot).expect("explains");
    assert_eq!(explained.access(), super::ExplainAccess::FilteredScan);
    assert_eq!(explained.order(), super::ExplainOrder::Sorted);
    assert_eq!(explained.limit(), Some(5));
    assert_eq!(explained.offset(), 0);
    assert!(explained.building().is_empty());
    let index =
        explained.indexes().iter().find(|index| index.field == "status").expect("status indexed");
    assert_eq!(index.kind, super::ExplainIndexKind::Value);
    assert_eq!(index.state, super::ExplainIndexState::Active);
    // The ranked search explains as a text search with score order. The
    // first execution registers the text descriptor inline, so explaining
    // afterwards reports the active index the query already narrowed
    // through.
    let ranked = compile_search(
        &store,
        "orders",
        "note",
        "quick",
        "tenant-a",
        Some(OrderDirection::Descending),
        Some(5),
    );
    assert_eq!(
        row_ids(&store.execute(&ranked, &snapshot).expect("the first search registers")),
        ["doc-1"]
    );
    let explained = store.explain(&ranked, &snapshot).expect("explains");
    assert_eq!(explained.access(), super::ExplainAccess::TextSearch);
    assert_eq!(explained.order(), super::ExplainOrder::ScoreRanked);
    let text =
        explained.indexes().iter().find(|index| index.field == "note").expect("note indexed");
    assert_eq!(text.kind, super::ExplainIndexKind::Text);
    assert_eq!(text.state, super::ExplainIndexState::Active);
    // Nothing names a storage internal.
    let rendered = format!("{explained:?}");
    assert!(!rendered.contains("fts_docs") && !rendered.contains("sqlite"), "explain: {rendered}");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn sql_front_end_gates_statements_relations_and_functions() {
    // The read-only boundary fails closed: statements outside
    // SELECT/WITH/EXPLAIN, SQLite-internal relations, and volatile or
    // host-sensitive functions never reach authorization or planning.
    // Full-text search rides the typed seam through the SQL `MATCH`
    // lowering (`Search` conditions); vector search rides exact k-NN
    // requests, not SQL operators.
    for text in [
        "SELECT * FROM orders",
        "SELECT _id, body ->> '$.customer' AS customer FROM orders WHERE total >= ?1",
        "WITH recent AS (SELECT * FROM orders) SELECT * FROM recent",
        "EXPLAIN SELECT * FROM orders",
    ] {
        assert!(super::validate_sql_text(text).is_ok(), "the front end accepts: {text}");
    }
    for text in [
        "DELETE FROM orders",
        "PRAGMA table_info(orders)",
        "SELECT * FROM sqlite_master",
        "SELECT random() FROM orders",
        "SELECT load_extension('x')",
    ] {
        assert_eq!(
            super::validate_sql_text(text).unwrap_err(),
            QueryInputError::UnsupportedGrammar,
            "the front end refuses: {text}"
        );
    }
}

#[test]
fn sql_live_subset_admits_the_launch_operators() {
    use super::sql::{LiveSubset, classify_sql_text};
    const MAX: usize = 16 * 1024;
    // The D-78 live subset: one collection, scalar predicates over columns
    // or JSON paths, projection, FTS5 `MATCH`, `ORDER BY`, `LIMIT`.
    // `EXPLAIN` classifies through its inner query.
    for text in [
        "SELECT * FROM orders",
        "SELECT _id, total FROM orders WHERE status = 'paid' ORDER BY total DESC LIMIT 25",
        "SELECT body ->> '$.customer' FROM orders WHERE body ->> '$.status' = :status",
        "SELECT * FROM articles WHERE body MATCH :query",
        "SELECT * FROM articles WHERE body MATCH 'quick'",
        "EXPLAIN SELECT * FROM orders WHERE total > 10",
    ] {
        assert_eq!(classify_sql_text(text, MAX), Ok(LiveSubset::Live), "{text}");
    }
}

#[test]
fn sql_live_subset_names_the_first_non_live_operator() {
    use super::sql::{LiveSubset, check_subscribable, classify_sql_text, parse};
    const MAX: usize = 16 * 1024;
    // ADR 0044 §3.2 operator table, in priority order: a statement carrying
    // several non-live operators names the first.
    for (text, operator) in [
        ("SELECT * FROM orders JOIN archive ON orders._id = archive._id", "join"),
        ("SELECT status, count(*) FROM orders GROUP BY status HAVING count(*) > 1", "aggregate"),
        ("SELECT count(*) FROM orders", "aggregate"),
        ("SELECT row_number() OVER (ORDER BY _id) FROM orders", "window"),
        ("SELECT * FROM orders WHERE _id IN (SELECT _id FROM archive)", "subquery"),
        ("SELECT * FROM orders WHERE EXISTS (SELECT 1 FROM archive)", "subquery"),
        ("SELECT * FROM (SELECT * FROM orders) AS recent", "subquery"),
        ("WITH recent AS (SELECT * FROM orders) SELECT * FROM recent", "subquery"),
        ("SELECT * FROM orders UNION ALL SELECT * FROM archive", "subquery"),
        ("SELECT count(*) FROM orders JOIN archive ON orders._id = archive._id", "join"),
    ] {
        assert_eq!(
            classify_sql_text(text, MAX),
            Ok(LiveSubset::ExecuteOnly { operator }),
            "{text}"
        );
        let parsed = parse(text, MAX).expect("the front end parses execute-only shapes");
        assert_eq!(check_subscribable(&parsed).unwrap_err().operator(), operator, "{text}");
    }
    // The refusal carries the fix, and a live statement subscribes cleanly.
    let parsed = parse("SELECT count(*) FROM orders", MAX).expect("parses");
    let refusal = check_subscribable(&parsed).expect_err("aggregates do not subscribe");
    assert!(refusal.fix().contains("execute()"), "the fix names execute()");
    let live = parse("SELECT * FROM orders WHERE total > 10", MAX).expect("parses");
    assert!(check_subscribable(&live).is_ok());
}

#[test]
fn sql_match_predicate_lowers_to_search_and_executes() {
    use super::sql::SqlParameters;
    let (dir, engine) = open_engine("sql-match-lowers");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &tenant_scope,
        "op-1",
        vec![
            text_document("doc-1", "articles", "body", "the quick brown fox"),
            text_document("doc-2", "articles", "body", "a slow green turtle"),
        ],
    );
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.queries();
    // Bound-parameter form: the query string travels as data, never as FTS syntax.
    let text = SqlQueryText::try_new("SELECT * FROM articles WHERE body MATCH :query")
        .expect("valid text");
    let parameters =
        SqlParameters::try_new(&[("query", DocumentValue::String("quick".to_owned()))])
            .expect("binds");
    let plan = store
        .compile(QueryInput::sql_bounded_with_parameters(
            text,
            deadline(),
            Cancellation::new(),
            parameters,
        ))
        .expect("MATCH lowers to the typed search condition");
    assert_eq!(row_ids(&store.execute(&plan, &snapshot).expect("MATCH executes")), ["doc-1"]);
    // Literal form serves the same match set.
    let literal = SqlQueryText::try_new("SELECT * FROM articles WHERE body MATCH 'turtle'")
        .expect("valid text");
    let plan = store.compile(QueryInput::sql(literal)).expect("literal MATCH lowers");
    assert_eq!(row_ids(&store.execute(&plan, &snapshot).expect("executes")), ["doc-2"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
