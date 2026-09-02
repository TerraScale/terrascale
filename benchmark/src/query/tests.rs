//! In-crate query tests covering the W3-2 contract list.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. The V1-compatible text is exercised
//! through the crate-private raw-text compile entry, because the public
//! `TqlQueryText::try_new` constructor is still the declaration-only W3-0
//! placeholder; that wiring is the integrator action recorded in the design
//! record. No test inspects raw pages, WAL bytes, or private lock state.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::{
    GenerationView, GrammarVersion, IndexNamespace, ObservedGeneration, OrderDirection,
    OrderingKey, QueryCondition, QueryError, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QuerySource, QuerySpec, QueryStore, StalenessMode, StalenessPolicy,
    TqlParameters,
    execute::execute_with_view,
    spec::{MAX_PAGE_LIMIT, validate_field_name},
    tql,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    operations::{QueryInput, QueryInputError, RequestValidationError, TqlQueryText},
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
        4,
        "an uncovered segment falls back to the full collection scan"
    );
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
    // The same query as TQL text, through the same seam. The public
    // constructor validates the text before it reaches the seam.
    let text_plan = store
        .compile(QueryInput::tql(
            TqlQueryText::try_new(
                "select name, age from users where age >= 30 order by age desc limit 2 offset 0",
            )
            .expect("public constructor accepts the grammar"),
        ))
        .expect("parses and lowers through the one full parser");
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
    // Grammar-level rejections: the text does not parse under `tql/1.0`,
    // so both the public constructor and the compile seam refuse it.
    let unparsable = [
        "from users sort by age",
        "from users order by age select name limit 2",
        "from users limit 0",
        "from users offset -1",
        "from users where name = 'ada' extra",
        "from",
        "where name = 'ada'",
    ];
    for text in unparsable {
        assert_eq!(
            super::validate_tql_text(text).unwrap_err(),
            QueryInputError::UnsupportedGrammar,
            "{text}"
        );
        assert_eq!(
            TqlQueryText::try_new(text).unwrap_err(),
            QueryInputError::UnsupportedGrammar,
            "the public constructor rejects the same text before the seam: {text}"
        );
        assert_eq!(
            store.compile_tql_text(text).unwrap_err(),
            QueryError::InvalidSpec,
            "unsupported grammar must be rejected: {text}"
        );
    }
    // Grammar-valid but seam-unlowerable: the constructor accepts the text
    // (the grammar defines it) and the compile seam fails closed instead of
    // degrading the plan.
    let unlodable = [
        "from users where name = 'ada' or age = 2",
        "from users where name = \"ada\"",
        "from orders include customer",
        "select distinct status from orders",
    ];
    for text in unlodable {
        assert!(super::validate_tql_text(text).is_ok(), "the full grammar defines: {text}");
        assert_eq!(
            store.compile_tql_text(text).unwrap_err(),
            QueryError::InvalidSpec,
            "the seam fails closed on: {text}"
        );
    }
    assert_eq!(super::validate_tql_text("   ").unwrap_err(), QueryInputError::EmptyText);
    // The full parser enforces the shared identifier discipline itself: an
    // over-long collection name is a typed invalid identifier at validation
    // and an invalid specification at compile.
    let long_collection = format!("from {} where a = 1", "x".repeat(300));
    assert_eq!(
        super::validate_tql_text(&long_collection).unwrap_err(),
        QueryInputError::InvalidIdentifier
    );
    assert_eq!(store.compile_tql_text(&long_collection).unwrap_err(), QueryError::InvalidSpec);
    // The full grammar accepts strictly more than the deleted subset did:
    // case-insensitive keywords, `select *` as the whole-document
    // projection, and parenthesized single comparisons all compile.
    for text in [
        "SELECT * FROM users",
        "select * from users",
        "from users where (name = 'ada')",
        "from users where name = 'o''brien'",
    ] {
        assert!(store.compile_tql_text(text).is_ok(), "the full grammar accepts: {text}");
    }
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
fn tql_text_lowers_into_typed_clauses_through_the_one_parser() {
    let query = tql::parse(
        GrammarVersion::CURRENT,
        "select name, age from users where exists(flag) and age >= 30 and missing(note) order by age desc, name limit 10 offset 5",
        &TqlParameters::new(),
    )
    .expect("parses the grammar");
    let clauses = tql::lower_clauses(&query, &TqlParameters::new()).expect("lowers");
    assert_eq!(clauses.source.collection().map(CollectionName::as_str), Some("users"));
    assert_eq!(clauses.predicate.conditions().len(), 3);
    assert_eq!(clauses.ordering.keys().len(), 2);
    assert_eq!(clauses.ordering.keys()[0].field(), "age");
    assert_eq!(clauses.ordering.keys()[0].direction(), OrderDirection::Descending);
    assert_eq!(clauses.ordering.keys()[1].direction(), OrderDirection::Ascending);
    assert_eq!(clauses.paging.offset(), 5);
    assert_eq!(clauses.paging.limit(), Some(10));
    assert_eq!(clauses.projection.selected(), Some(&["name".to_owned(), "age".to_owned()][..]));
    // A `from`-only query is the minimal valid form, and the doubled-quote
    // escape is a literal quote inside the string.
    assert!(tql::parse(GrammarVersion::CURRENT, "from users", &TqlParameters::new()).is_ok());
    assert!(
        tql::parse(
            GrammarVersion::CURRENT,
            "from users where name = 'o''brien'",
            &TqlParameters::new()
        )
        .is_ok()
    );
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
/// no document binds null (docs/tql/spec.md Section 4.2), and an absent
/// field of a resolved reference is omitted, staying distinct from a
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
    // document is neither scanned nor skipped.
    assert_eq!(result.matched(), 3);
    assert_eq!(result.rows().len(), 3);
    assert_eq!(result.statistics().scanned(), 5);
    assert_eq!(result.statistics().skipped(), 2);
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
        .compile(QueryInput::tql(
            TqlQueryText::try_new("from users where status = 'paid'").expect("valid text"),
        ))
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
        .compile(QueryInput::tql(
            TqlQueryText::try_new("from users where status >= 'paid' limit 2").expect("valid text"),
        ))
        .expect("compiles");
    let result = store.execute(&ordered, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-1", "doc-2"]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_field_without_an_active_descriptor_never_pushes() {
    let (dir, engine) = open_engine("pushdown-uncovered");
    let tenant_scope = scope(&engine, "tenant-a");
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
        .compile(QueryInput::tql(
            TqlQueryText::try_new("from orders where total > 20").expect("valid text"),
        ))
        .expect("compiles");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(row_ids(&result), ["doc-2"]);
    assert_eq!(
        result.statistics().scanned(),
        2,
        "no descriptor means the unfiltered page path scans the collection"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
