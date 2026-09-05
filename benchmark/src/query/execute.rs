//! The private query execution path.
//!
//! Execution validates tenant, snapshot, and namespace bindings before any
//! data access, evaluates the staleness policy against the index catalog, then
//! uses a bounded exact-key posting list when one safely covers the snapshot
//! — fetching a small candidate set by keyed reads, and scanning only the
//! immutable snapshot view of the source collection otherwise — orders,
//! projects, and pages the matches with bounded deadline and cancellation
//! checkpoints. It never mutates a document, a snapshot, or an index, and it
//! publishes nothing.

use std::time::Instant;
use std::{collections::BTreeMap, collections::BTreeSet, sync::Arc};

use super::spec::{QueryCondition, StalenessPolicy};
use super::{
    GenerationView, NormalizedQuery, QueryError, QueryExplanation, QueryPlan, QueryResult,
    QueryRow, StalenessMode, model,
    result::{
        ExplainAccess, ExplainIndex, ExplainIndexKind, ExplainIndexState, ExplainOrder,
        IndexGenerationRef, ObservedGeneration, StaleIndexObservation, StalenessReport,
    },
    statistics::{DiagnosticDuration, QueryStatistics, ResultEtag},
};
use crate::{
    engine::Engine,
    index::{IndexCatalog, IndexLookupBounds, IndexLookupError},
    storage::{ScanCondition, ScanFilter, ScanOp},
    types::{
        Cancellation, Deadline, Document, DocumentId, DocumentValue, ReadSnapshot, TenantScope,
    },
};

/// How many scanned documents pass between two bounded checkpoints.
const CHECKPOINT_INTERVAL: usize = 64;

/// The maximum number of documents one query scan asks the snapshot seam for.
/// Keeping this below the engine's page ceiling makes the query bound
/// independent of the backend's implementation details.
pub(crate) const QUERY_SCAN_PAGE: u32 = 256;

/// The maximum number of ordered candidates retained while a query scans.
/// Requests whose offset and page cannot fit this window are refused before
/// data access, and an unbounded request is refused once it exceeds it.
pub(crate) const MAX_RETAINED_CANDIDATES: usize = 4096;

/// The maximum estimated bytes retained by the ordered candidate window.
pub(crate) const MAX_RETAINED_CANDIDATE_BYTES: usize = 16 * 1024 * 1024;

/// Checks the execution bounds at one bounded checkpoint.
///
/// The check is pure: it holds no lock, and callers reach it only between
/// scan batches, never while an Engine lock is held.
pub(crate) fn check_bounds(
    deadline: Option<Deadline>,
    cancellation: Option<&Cancellation>,
) -> Result<(), QueryError> {
    if deadline.is_some_and(|deadline| deadline.is_expired()) {
        return Err(QueryError::Deadline);
    }
    if cancellation.is_some_and(Cancellation::is_cancelled) {
        return Err(QueryError::Cancelled);
    }
    Ok(())
}

/// Maps an Engine error from the snapshot view onto the typed query errors.
///
/// Crate-only: the family-source execution path (GAP-Q19) maps the snapshot
/// view through the same vocabulary.
pub(crate) fn map_engine_error(error: &crate::engine::EngineError) -> QueryError {
    match error {
        // A scope mismatch can only mean the snapshot claims another Engine.
        crate::engine::EngineError::SnapshotEngineMismatch
        | crate::engine::EngineError::ScopeMismatch => QueryError::SnapshotEngineMismatch,
        crate::engine::EngineError::SnapshotTenantMismatch => QueryError::SnapshotTenantMismatch,
        crate::engine::EngineError::Deadline => QueryError::Deadline,
        crate::engine::EngineError::Cancelled => QueryError::Cancelled,
        crate::engine::EngineError::Closed(_) => QueryError::EngineClosed,
        crate::engine::EngineError::StorageFailure
        | crate::engine::EngineError::SnapshotPageLimitExceeded
        | crate::engine::EngineError::HistoryExpired
        | crate::engine::EngineError::SnapshotBoundaryUnavailable
        | crate::engine::EngineError::Conflict
        | crate::engine::EngineError::InvalidDocument
        | crate::engine::EngineError::OperationIdReuse { .. } => QueryError::StorageFailure,
    }
}

/// Selects the indexes one query may use under its staleness policy.
///
/// For every required namespace the selection reads two independent facts:
/// the generation the snapshot observed for that namespace, and the
/// generation the catalog can currently supply. A strict policy requires the
/// supplied generation to be present and at least the observed generation
/// within the same epoch; a namespace from another epoch is never treated as
/// comparable, and a missing or older generation fails with
/// [`QueryError::IndexNotReady`] instead of silently returning stale results.
/// An allow-stale policy accepts any supplied generation and records it.
pub(crate) fn select_indexes(
    view: &dyn GenerationView,
    policy: &StalenessPolicy,
    snapshot: &ReadSnapshot,
) -> Result<StalenessReport, QueryError> {
    let mut observations = Vec::new();
    for namespace in policy.indexes() {
        let observed = view.observed_generation(namespace, snapshot.sequence());
        let used = view.available_generation(namespace);
        let comparable = match (observed, used) {
            (Some(observed), Some(used)) => observed.epoch() == used.epoch(),
            _ => false,
        };
        let required = observed.map_or(0, ObservedGeneration::generation);
        let meets = comparable && used.is_some_and(|used| used.generation() >= required);
        if policy.mode() == StalenessMode::Strict && !meets {
            return Err(QueryError::IndexNotReady);
        }
        let stale = match (observed, used) {
            (Some(observed), Some(used)) => {
                used.epoch() != observed.epoch() || used.generation() < observed.generation()
            }
            // An absent observation or an absent supplied generation can
            // never be confirmed current, so both count as stale.
            (Some(_), None) | (None, _) => true,
        };
        observations.push(StaleIndexObservation::new(namespace.clone(), observed, used, stale));
    }
    Ok(StalenessReport::new(observations))
}

/// Executes one compiled plan against one immutable snapshot.
///
/// A dynamic plan — one whose staleness policy names no index — runs one
/// optimizer pass before data execution: the pass selects or creates the
/// serving index, records the selection, and fails closed on its guards. The
/// selected segment is used only for the exact-key shape it can prove safe;
/// all other shapes use the canonical bounded scan. A plan that names an index
/// bypasses the optimizer entirely (AX-26).
pub(crate) fn execute(
    engine: &Engine,
    indexes: &Arc<IndexCatalog>,
    plan: &super::QueryPlan,
    snapshot: &ReadSnapshot,
) -> Result<QueryResult, QueryError> {
    // A family source runs the family-source bridge and never the optimizer
    // pass (GAP-Q19): auto indexes are keyed by collection demand, and V1's
    // auto-index surface is document-only, so there is no selection to
    // record and no index to create for the family entry space.
    if plan.model().source().kind() != super::spec::QuerySourceKind::Collection {
        return execute_with_view(engine, indexes.as_ref(), plan, snapshot);
    }
    let selected = if plan.model().staleness().indexes().is_empty() {
        // The optimizer may build an index by reading this snapshot, so reject
        // forged engine/tenant bindings before it can perform any catalog work.
        validate_bindings(engine, plan.model(), snapshot)?;
        // Derive durable descriptors from the query shape up front (D-31):
        // registration rides every dynamic query no matter which serving
        // path answers it, so the extracted-column coverage converges even
        // when the catalog segment serves. Refusal stays with the scan path,
        // the only one a building descriptor could starve. Text descriptors
        // ride along the same way (S-02 slice D).
        if let (Some(collection), Some(filter)) =
            (plan.model().source().collection(), pushdown_filter(plan.model().predicate()))
        {
            register_pushdown_fields(engine, collection, &filter);
        }
        if let Some(collection) = plan.model().source().collection() {
            register_fts_fields(engine, collection, &search_conditions(plan.model().predicate()));
        }
        super::optimizer::run(engine, indexes.as_ref(), plan, snapshot)?
    } else {
        // Explicitly named plans bypass optimizer selection (AX-26), but may
        // still use their named segment when the execution safety checks below
        // prove that it exactly covers this snapshot.
        plan.model().staleness().indexes().first().cloned()
    };
    execute_with_index(
        engine,
        indexes.as_ref(),
        plan,
        snapshot,
        Some(indexes.as_ref()),
        selected.as_ref(),
    )
}

/// Executes one plan against one supplied index-generation view.
///
/// Crate-only: the production view is the Engine's index catalog; the test
/// seam passes an independent view so the selection rule stays provable with
/// observed and supplied generations that one monotonic catalog could never
/// produce at the same time.
pub(crate) fn execute_with_view(
    engine: &Engine,
    view: &dyn GenerationView,
    plan: &super::QueryPlan,
    snapshot: &ReadSnapshot,
) -> Result<QueryResult, QueryError> {
    execute_with_index(engine, view, plan, snapshot, None, None)
}

/// Executes one plan, optionally narrowing the document candidates through a
/// catalog segment selected by the optimizer. The narrowing is decided before
/// any document is materialized: an admitted bounded posting read with a
/// small candidate set is fetched by keyed reads, every other shape scans the
/// immutable snapshot view of the source collection. The generic
/// `GenerationView` remains the test seam; only the production catalog can
/// provide segment contents, so forged generation views continue to exercise
/// the original scan path.
#[allow(clippy::too_many_lines)] // execution keeps the gate, scan, and result accounting together
fn execute_with_index(
    engine: &Engine,
    view: &dyn GenerationView,
    plan: &super::QueryPlan,
    snapshot: &ReadSnapshot,
    catalog: Option<&IndexCatalog>,
    selected: Option<&super::IndexNamespace>,
) -> Result<QueryResult, QueryError> {
    // The diagnostic window: entry of the execution path to the construction
    // of the result. It is an observation of this one call, never a
    // benchmark or a speed claim (see `DiagnosticDuration`).
    let started = Instant::now();
    let query: &NormalizedQuery = plan.model();
    // Tenant and namespace validation before catalog access.
    if let Some(claim) = query.tenant_claim()
        && claim != snapshot.tenant()
    {
        return Err(QueryError::SnapshotTenantMismatch);
    }
    for namespace in query.staleness().indexes() {
        if namespace.tenant() != snapshot.tenant() {
            return Err(QueryError::TenantMismatch);
        }
    }
    let staleness = select_indexes(view, query.staleness(), snapshot)?;
    // Snapshot identity validation before document access.
    if snapshot.engine_identity() != engine.identity() {
        return Err(QueryError::SnapshotEngineMismatch);
    }
    check_bounds(query.deadline(), query.cancellation_signal())?;
    // A family source runs the family-source bridge from here (GAP-Q19):
    // its row space is the family entry space, read through the family
    // seam against this same snapshot, and it never touches the document
    // scan below.
    if query.source().kind() != super::spec::QuerySourceKind::Collection {
        return super::family_sources::execute_family(engine, query, snapshot, staleness);
    }
    // The snapshot supplies the tenant binding and its own Engine identity;
    // the Engine validates both against its real identity inside the view.
    let scope = TenantScope::from_parts(snapshot.tenant().clone(), snapshot.engine_identity());

    // The index gate runs BEFORE any document is materialized. The gate is
    // exactly the one the materialized path applied — the available
    // generation above, and inside `indexed_candidates` the tenant binding,
    // the segment's collection, and the snapshot-isolation guard that the
    // segment covers exactly this snapshot's sequence — but reading it first
    // lets a narrowed query fetch its candidates by key instead of scanning.
    let retained_capacity = retained_capacity(query)?;
    let indexed_read = match (selected, catalog) {
        (Some(namespace), Some(catalog)) if view.available_generation(namespace).is_some() => {
            indexed_candidates(catalog, namespace, query, snapshot)?
        }
        _ => None,
    };
    let indexed_generation = indexed_read.as_ref().map(|read| read.generation);
    let indexed_ids = indexed_read.map(|read| read.ids.into_iter().collect::<BTreeSet<_>>());

    // Loads and includes resolve identifiers across every collection of the
    // tenant — a reference is an id, not a collection-qualified pointer —
    // through their own bounded resolution pass below. A ranked
    // (`order by score()`) query never takes the keyed path: its order
    // comes from the relevance scores of the scan path's ranked branches,
    // and a model-ordered window could evict the most relevant matches
    // before the scores are even consulted.
    let ranked = query.search_rank().is_some();
    let (ordered, matched_count, scanned, skipped) = if let (Some(collection), Some(ids)) =
        (query.source().collection(), indexed_ids.as_ref())
        && ids.len() <= KEYED_CANDIDATE_READS
        && !ranked
    {
        // The keyed candidate fetch: the posting read above named a small,
        // bounded identifier set, and each candidate resolves by its exact
        // storage key — one seek and one decode per candidate — so nothing
        // outside the candidate set is ever read.
        let candidates: Vec<DocumentId> = ids.iter().cloned().collect();
        let documents = engine
            .snapshot_documents_by_ids(&scope, snapshot, collection, &candidates)
            .map_err(|error| map_engine_error(&error))?;
        collect_matches(query, &documents, indexed_ids.as_ref(), retained_capacity)?
    } else if let Some(collection) = query.source().collection() {
        // The bounded source-collection scan: the fallback for an index read
        // that was not admitted (no segment, an unsupported predicate shape,
        // or a snapshot the segment does not exactly cover) and for candidate
        // sets past the keyed bound, and the plain scan of a collection query
        // that selected no index at all. It stops at the collection's key
        // range instead of decoding the tenant's other collections.
        scan_collection(
            engine,
            &scope,
            snapshot,
            query,
            collection,
            indexed_ids.as_ref(),
            retained_capacity,
        )?
    } else {
        (Vec::new(), 0, 0, 0)
    };

    // The scan retained only the ordered candidate window. Loads and includes
    // resolve their page's references through a separate bounded tenant pass,
    // preserving canonical first-match semantics for reused IDs.
    // Bounded checkpoint at the scan/sort boundary: a bound that expired
    // during the scan stops the query before the sort starts.
    check_bounds(query.deadline(), query.cancellation_signal())?;
    let mut ordered = ordered;
    if query.search_rank().is_none() {
        ordered.sort_by(|left, right| model::compare_documents(query.ordering(), left, right));
    }
    let page_documents = page_documents(query, &ordered);
    let resolved_documents = resolve_references(engine, snapshot, query, &page_documents)?;
    let mut by_id: BTreeMap<&str, &Document> = BTreeMap::new();
    for document in &resolved_documents {
        by_id.entry(document.id().as_str()).or_insert(document);
    }
    let rows = materialize(query, &ordered, &by_id)?;
    // The include set is materialized from exactly the rows the page
    // carries, after ordering and paging, so a paged include-bearing query
    // materializes the includes of its own page and nothing else.
    let includes = super::includes::materialize(
        engine,
        snapshot,
        &resolved_documents,
        &rows,
        query.includes(),
        query.deadline(),
        query.cancellation_signal(),
    )?;

    let index_generation = selected
        .zip(indexed_generation)
        .map(|(namespace, generation)| IndexGenerationRef::new(namespace.clone(), generation))
        .or_else(|| {
            staleness
                .observations()
                .first()
                .filter(|observation| observation.used().is_some())
                .and_then(|observation| {
                    observation
                        .used()
                        .map(|used| IndexGenerationRef::new(observation.namespace().clone(), used))
                })
        });
    let statistics = QueryStatistics::new(
        matched_count,
        query.paging(),
        scanned,
        skipped,
        ResultEtag::of(snapshot.sequence(), &staleness),
        DiagnosticDuration::new(started.elapsed()),
    );
    Ok(QueryResult::new(
        rows,
        matched_count,
        query.ordering().clone(),
        staleness,
        index_generation,
        includes,
        statistics,
    ))
}

/// Explains the plan one query would use against one snapshot: the serving
/// access, the logical indexes with their states, the ordering, and the
/// shapes whose building descriptors would refuse execution.
///
/// The explanation reads only descriptor state (lock-light, in-memory) and
/// the plan itself: it validates the tenant and snapshot bindings like
/// execution does, then reports without touching any document.
#[allow(clippy::too_many_lines, reason = "the explanation's fixed dimensions read together")]
pub(crate) fn explain(
    engine: &Engine,
    plan: &QueryPlan,
    snapshot: &ReadSnapshot,
) -> Result<QueryExplanation, QueryError> {
    let query: &NormalizedQuery = plan.model();
    if let Some(claim) = query.tenant_claim()
        && claim != snapshot.tenant()
    {
        return Err(QueryError::SnapshotTenantMismatch);
    }
    for namespace in query.staleness().indexes() {
        if namespace.tenant() != snapshot.tenant() {
            return Err(QueryError::TenantMismatch);
        }
    }
    if snapshot.engine_identity() != engine.identity() {
        return Err(QueryError::SnapshotEngineMismatch);
    }
    let collection = query
        .source()
        .collection()
        .map(|collection| collection.as_str().to_owned())
        .unwrap_or_default();
    let mut indexes: Vec<ExplainIndex> = Vec::new();
    let mut building: Vec<String> = Vec::new();
    let mut pushed = false;
    if let (Some(source), Some(filter)) =
        (query.source().collection(), pushdown_filter(query.predicate()))
    {
        for condition in filter.conditions() {
            let field = condition.field.as_str();
            if indexes.iter().any(|index: &ExplainIndex| index.field == field) {
                continue;
            }
            if let Some(active) = engine.pushdown_field_state(source, field) {
                pushed = true;
                if !active {
                    building.push(format!("{}.{field}", source.as_str()));
                }
                indexes.push(ExplainIndex {
                    field: field.to_owned(),
                    kind: ExplainIndexKind::Value,
                    state: if active {
                        ExplainIndexState::Active
                    } else {
                        ExplainIndexState::Building
                    },
                });
            }
        }
    }
    let mut search_shapes: Vec<(&str, &str, bool)> = Vec::new();
    for condition in query.predicate().conditions() {
        if let QueryCondition::Search { field, query, match_any } = condition {
            search_shapes.push((field.as_str(), query.as_str(), *match_any));
        }
    }
    // A single searched field over an active text descriptor with an FTS5
    // spelling narrows the read exactly the way execution narrows it.
    let mut text_narrows = false;
    if search_shapes.len() == 1
        && let Some(source) = query.source().collection()
    {
        let (field, search_query, match_any) = search_shapes[0];
        if engine.fts_field_state(source, field) == Some(true) {
            let parsed = super::parse_search_query(search_query, match_any);
            text_narrows = super::sanitize_to_fts5_match(&parsed).is_some();
        }
    }
    if let Some(source) = query.source().collection() {
        for (field, _, _) in &search_shapes {
            if indexes.iter().any(|index: &ExplainIndex| index.field == *field) {
                continue;
            }
            if let Some(active) = engine.fts_field_state(source, field) {
                if !active {
                    building.push(format!("{}.{field}", source.as_str()));
                }
                indexes.push(ExplainIndex {
                    field: (*field).to_owned(),
                    kind: ExplainIndexKind::Text,
                    state: if active {
                        ExplainIndexState::Active
                    } else {
                        ExplainIndexState::Building
                    },
                });
            }
        }
    }
    indexes.sort_by(|left, right| left.field.cmp(&right.field));
    building.sort();
    building.dedup();
    let order = if query.search_rank().is_some() {
        ExplainOrder::ScoreRanked
    } else if query.ordering().keys().is_empty() && query.ordering().random_seed().is_none() {
        ExplainOrder::Input
    } else {
        ExplainOrder::Sorted
    };
    let access = if search_shapes.len() == 1 && text_narrows {
        ExplainAccess::TextSearch
    } else if pushed {
        ExplainAccess::FilteredScan
    } else {
        ExplainAccess::FullScan
    };
    Ok(QueryExplanation::new(
        collection,
        access,
        indexes,
        order,
        query.paging().limit(),
        query.paging().offset(),
        building,
        true,
    ))
}

/// Performs the binding checks needed before the optimizer can build or
/// select an index. The full snapshot view repeats these checks, but doing the
/// identity checks here keeps a forged request from mutating the catalog.
fn validate_bindings(
    engine: &Engine,
    query: &NormalizedQuery,
    snapshot: &ReadSnapshot,
) -> Result<(), QueryError> {
    if snapshot.engine_identity() != engine.identity() {
        return Err(QueryError::SnapshotEngineMismatch);
    }
    if query.tenant_claim().is_some_and(|claim| claim != snapshot.tenant()) {
        return Err(QueryError::SnapshotTenantMismatch);
    }
    if query.staleness().indexes().iter().any(|namespace| namespace.tenant() != snapshot.tenant()) {
        return Err(QueryError::TenantMismatch);
    }
    Ok(())
}

/// Reads an index's complete exact-key posting list when the segment can
/// safely answer this query. `None` is the deterministic fallback for a
/// predicate the segment cannot prove complete (unsupported operators,
/// missing equality values, another collection, or a generation that does
/// not cover exactly this snapshot). A bounded refusal is an error: silently
/// turning an admitted index read into an unbounded scan would violate the
/// resource contract.
fn indexed_candidates(
    catalog: &IndexCatalog,
    namespace: &super::IndexNamespace,
    query: &NormalizedQuery,
    snapshot: &ReadSnapshot,
) -> Result<Option<IndexedCandidates>, QueryError> {
    if namespace.tenant() != snapshot.tenant() {
        return Err(QueryError::TenantMismatch);
    }
    let Some(source_collection) = query.source().collection() else {
        return Ok(None);
    };
    let Some(segment) = catalog.segment(namespace) else {
        return Ok(None);
    };
    // A segment covering an older or newer MVCC cut cannot be used: either
    // it misses documents visible to the snapshot or it includes future ones.
    if segment.collection() != Some(source_collection)
        || segment.covered_sequence() != snapshot.sequence().get()
    {
        return Ok(None);
    }
    let Some(key) = exact_index_key(query, segment.fields()) else {
        return Ok(None);
    };
    let bounds = IndexLookupBounds::new(MAX_INDEX_READ_ROWS, MAX_INDEX_READ_BYTES);
    let result = segment.lookup(&key, bounds).map_err(map_lookup_error)?;
    Ok(Some(IndexedCandidates {
        ids: result.rows().iter().map(|row| row.document_id().clone()).collect(),
        generation: ObservedGeneration::new(
            result.identity().epoch(),
            result.identity().generation(),
        ),
    }))
}

/// The complete bounded posting read and the generation that produced it.
/// Keeping the identity beside the IDs means a concurrent catalog replacement
/// cannot make result metadata claim a different segment than the one read.
struct IndexedCandidates {
    ids: Vec<DocumentId>,
    generation: ObservedGeneration,
}

/// Bounded resource limits for one complete index posting list. These limits
/// are deliberately independent of the result page limit: matched counts and
/// offsets require the complete candidate set, not only the returned page.
const MAX_INDEX_READ_ROWS: usize = 65_536;
const MAX_INDEX_READ_BYTES: usize = 4 * 1024 * 1024;

/// The candidate bound below which an admitted index read fetches its
/// candidates by keyed reads instead of scanning the source collection.
///
/// The decision runs before anything is materialized, so the tenant's size is
/// not observable without paying the very scan the bound exists to avoid; the
/// bound is therefore fixed. The trade it names: a keyed fetch costs one
/// bounded seek plus one decode per candidate (`O(k)` keyed lookups), while a
/// collection scan costs one merge walk and one decode per stored document
/// (`O(n)`). At or below this bound the keyed path's total work stays within
/// one bounded pass for any collection the auto-index policy considers worth
/// serving (a selective predicate on a large collection — the shape the
/// automatic index exists for), and above it the scan amortizes better, so
/// the existing bounded scan with the candidate filter applies unchanged.
///
/// The bound sits far below [`MAX_INDEX_READ_ROWS`]: a posting list that
/// would pass the posting read's own row bound but exceed this bound simply
/// takes the scan, and no unbounded identifier set can ever reach the keyed
/// path.
const KEYED_CANDIDATE_READS: usize = 1024;

fn map_lookup_error(error: IndexLookupError) -> QueryError {
    match error {
        // The index seam keeps the precise row/byte refusal typed. The query
        // public error vocabulary intentionally remains stable, so both
        // bounded refusals surface as the existing fail-closed readiness
        // error rather than permitting a partial or unbounded fallback.
        IndexLookupError::RowLimitExceeded { .. } | IndexLookupError::ByteLimitExceeded { .. } => {
            QueryError::IndexNotReady
        }
    }
}

/// Builds the exact key shape emitted by the index builder. Only a conjunction
/// of one scalar equality per indexed field is admitted; every other query
/// falls back to the predicate evaluator, preserving its richer semantics.
fn exact_index_key(query: &NormalizedQuery, fields: &[String]) -> Option<String> {
    let mut equals: BTreeMap<&str, &DocumentValue> = BTreeMap::new();
    for condition in query.predicate().conditions() {
        let super::QueryCondition::Equals { field, value } = condition else {
            return None;
        };
        if equals.insert(field.as_str(), value).is_some() {
            return None;
        }
    }
    if equals.len() != fields.len()
        || fields.iter().any(|field| !equals.contains_key(field.as_str()))
    {
        return None;
    }
    fields
        .iter()
        .map(|field| index_value_key(equals[field.as_str()]))
        .collect::<Option<Vec<_>>>()
        .map(|keys| keys.join("\u{1f}"))
}

/// Mirrors the index builder's scalar key encoding. Arrays and objects are
/// intentionally excluded because the current index segment does not flatten
/// or recursively index them.
fn index_value_key(value: &DocumentValue) -> Option<String> {
    match value {
        DocumentValue::Null => Some("null".to_owned()),
        DocumentValue::Boolean(value) => Some(format!("boolean:{value}")),
        DocumentValue::Integer(value) => Some(format!("integer:{value}")),
        DocumentValue::Number(value) if value.is_finite() => Some(format!("number:{value}")),
        DocumentValue::String(value) => Some(format!("string:{value}")),
        DocumentValue::Number(_) | DocumentValue::Array(_) | DocumentValue::Object(_) => None,
    }
}

/// Computes the candidate window needed to serve one request. A page whose
/// offset cannot fit the retained window is rejected before any data access.
fn retained_capacity(query: &NormalizedQuery) -> Result<usize, QueryError> {
    let offset =
        usize::try_from(query.paging().offset()).map_err(|_| QueryError::CandidateBoundExceeded)?;
    let needed = match query.paging().limit() {
        Some(limit) => offset
            .checked_add(usize::try_from(limit).unwrap_or(usize::MAX))
            .ok_or(QueryError::CandidateBoundExceeded)?,
        None => MAX_RETAINED_CANDIDATES,
    };
    if needed > MAX_RETAINED_CANDIDATES {
        return Err(QueryError::CandidateBoundExceeded);
    }
    Ok(needed)
}

/// Evaluates the predicate against a bounded candidate batch. Candidates are
/// re-checked by the canonical evaluator so an index can safely provide a
/// superset without changing query semantics.
fn collect_matches(
    query: &NormalizedQuery,
    documents: &[Document],
    indexed_ids: Option<&BTreeSet<DocumentId>>,
    retained_capacity: usize,
) -> Result<(Vec<Document>, u64, u64, u64), QueryError> {
    let Some(source_collection) = query.source().collection() else {
        return Err(QueryError::InvalidSpec);
    };
    let mut matched = Vec::new();
    let mut retained_bytes = 0_usize;
    let mut matched_count = 0_u64;
    let mut scanned: u64 = 0;
    let mut skipped: u64 = 0;
    for (position, document) in documents.iter().enumerate() {
        if position % CHECKPOINT_INTERVAL == 0 {
            check_bounds(query.deadline(), query.cancellation_signal())?;
        }
        if document.collection() != source_collection
            || indexed_ids.is_some_and(|ids| !ids.contains(document.id()))
        {
            continue;
        }
        scanned = scanned.saturating_add(1);
        if model::matches(query.predicate(), document) {
            matched_count = matched_count.saturating_add(1);
            if query.paging().limit().is_none() && matched_count > retained_capacity as u64 {
                return Err(QueryError::CandidateBoundExceeded);
            }
            retain_candidate(
                query,
                &mut matched,
                &mut retained_bytes,
                document,
                retained_capacity,
            )?;
        } else {
            skipped = skipped.saturating_add(1);
        }
    }
    Ok((matched, matched_count, scanned, skipped))
}

/// Scans one source collection in fixed-size immutable snapshot pages.
///
/// When the predicate lowers onto extracted index columns with active
/// descriptors (D-42), the pages come from the pushed-down SQL scan — a
/// sound narrowing the executor's own predicate still refines — and the
/// scan falls back to the unfiltered page path when the backend pushes
/// nothing. Before paging, the first-query auto-index policy
/// ([`ensure_pushdown_fields`]) covers every pushable field: small
/// collections register inline and the same scan already pushes, while a
/// field whose descriptor is still building fails the query closed with
/// [`QueryError::IndexNotReady`] until the governed backfill completes it.
///
/// A single `search` condition takes the FTS5 candidate path instead: the
/// index names matching ids with relevance scores, the executor loads
/// them at the snapshot and re-checks the exact predicate, and a ranked
/// query retains them in score order. Several `search` conjuncts, a
/// vacuous or pure-exclusion query, or a snapshot behind the tip take the
/// exact scan (with fallback scores when ranked): the match set stays
/// exact everywhere, only the accelerator changes.
#[allow(
    clippy::too_many_lines,
    reason = "the scan keeps its paging loop and result accounting together"
)]
fn scan_collection(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    query: &NormalizedQuery,
    collection: &crate::types::CollectionName,
    indexed_ids: Option<&BTreeSet<DocumentId>>,
    retained_capacity: usize,
) -> Result<(Vec<Document>, u64, u64, u64), QueryError> {
    let filter = pushdown_filter(query.predicate());
    if let Some(filter) = filter.as_ref() {
        ensure_pushdown_fields(engine, collection, filter)?;
    }
    let searches = search_conditions(query.predicate());
    if !searches.is_empty() {
        ensure_fts_fields(engine, collection, &searches)?;
        if searches.len() == 1 {
            let (field, fts_query, match_any) = searches[0];
            if let Some(candidates) = engine
                .snapshot_fts_match(scope, snapshot, collection, field, fts_query, match_any)
                .map_err(|error| map_engine_error(&error))?
            {
                return scan_fts_candidates(
                    engine,
                    scope,
                    snapshot,
                    query,
                    collection,
                    &candidates,
                    indexed_ids,
                    retained_capacity,
                );
            }
        }
    }
    let mut pushdown = filter.is_some();
    let mut after = None;
    let ranked = query.search_rank().is_some();
    let rank_direction = query
        .search_rank()
        .map_or(super::spec::OrderDirection::Descending, model::SearchRank::direction);
    let mut matched = Vec::new();
    let mut scored: Vec<(f64, Document)> = Vec::new();
    let mut retained_bytes = 0_usize;
    let mut matched_count = 0_u64;
    let mut scanned = 0_u64;
    let mut skipped = 0_u64;
    loop {
        check_bounds(query.deadline(), query.cancellation_signal())?;
        let page = if pushdown {
            match engine.snapshot_documents_in_collection_filtered(
                scope,
                snapshot,
                collection,
                after.clone(),
                QUERY_SCAN_PAGE,
                filter.as_ref().expect("pushdown implies a filter"),
            ) {
                Ok(Some(page)) => page,
                // The backend pushed nothing for this filter; take the
                // unfiltered page path for the rest of the scan.
                Ok(None) => {
                    pushdown = false;
                    continue;
                }
                Err(error) => return Err(map_engine_error(&error)),
            }
        } else {
            engine
                .snapshot_documents_in_collection_from(
                    scope,
                    snapshot,
                    collection,
                    after,
                    QUERY_SCAN_PAGE,
                )
                .map_err(|error| map_engine_error(&error))?
        };
        if page.is_empty() {
            break;
        }
        for (position, document) in page.iter().enumerate() {
            if position % CHECKPOINT_INTERVAL == 0 {
                check_bounds(query.deadline(), query.cancellation_signal())?;
            }
            if indexed_ids.is_some_and(|ids| !ids.contains(document.id())) {
                continue;
            }
            scanned = scanned.saturating_add(1);
            if model::matches(query.predicate(), document) {
                matched_count = matched_count.saturating_add(1);
                if query.paging().limit().is_none() && matched_count > retained_capacity as u64 {
                    return Err(QueryError::CandidateBoundExceeded);
                }
                if ranked {
                    // The FTS narrowing missed (an older snapshot, several
                    // searched fields, a pure-exclusion query): the match
                    // set stays exact and the relevance order degrades to
                    // the deterministic clause-count proxy.
                    let relevance = fallback_score(document, query.predicate());
                    retain_scored(
                        query,
                        &mut scored,
                        &mut retained_bytes,
                        document,
                        relevance,
                        rank_direction,
                        retained_capacity,
                    )?;
                } else {
                    retain_candidate(
                        query,
                        &mut matched,
                        &mut retained_bytes,
                        document,
                        retained_capacity,
                    )?;
                }
            } else {
                skipped = skipped.saturating_add(1);
            }
        }
        if page.len() < QUERY_SCAN_PAGE as usize {
            break;
        }
        after = page.last().map(|document| document.id().clone());
    }
    if ranked {
        matched = scored.into_iter().map(|(_, document)| document).collect();
    }
    Ok((matched, matched_count, scanned, skipped))
}

/// Applies the first-query auto-index policy (D-31, ADR 0040): every
/// pushable field of the filter is covered by an active descriptor before
/// the scan pages. An absent field registers on the spot — a collection
/// inside the inline cost cap goes active at once and the same scan already
/// pushes; a larger collection goes building and the query fails closed with
/// [`QueryError::IndexNotReady`] (the API renders the retry fix) until the
/// governed backfill completes it. A building field refuses the same way: a
/// scan over half-extracted columns would silently miss rows, so the SQL
/// layer never pushes over one. A registration that leaves coverage
/// unchanged (column budget, storage, close) degrades to the executor's
/// exact scan instead: reads stay available and the audit keeps reporting
/// the shape uncovered.
///
/// Registration also rides [`execute`] up front for every dynamic collection
/// query, so shapes the catalog segment serves still derive their durable
/// descriptors; this seam adds the refusal the scan path needs.
fn ensure_pushdown_fields(
    engine: &Engine,
    collection: &crate::types::CollectionName,
    filter: &ScanFilter,
) -> Result<(), QueryError> {
    register_pushdown_fields(engine, collection, filter);
    for field in pushdown_fields(filter) {
        if engine.pushdown_field_state(collection, field) == Some(false) {
            return Err(QueryError::IndexNotReady);
        }
    }
    Ok(())
}

/// Registers the filter's pushable fields without refusing anything:
/// absent fields derive their durable descriptors (inline or building),
/// every other outcome leaves serving to the caller. The scan seam
/// ([`ensure_pushdown_fields`]) layers the building refusal on top.
fn register_pushdown_fields(
    engine: &Engine,
    collection: &crate::types::CollectionName,
    filter: &ScanFilter,
) {
    for field in pushdown_fields(filter) {
        if engine.pushdown_field_state(collection, field).is_none() {
            let _ = engine.register_index_field(collection, field);
        }
    }
}

/// The filter's pushable fields in first-use order.
fn pushdown_fields(filter: &ScanFilter) -> Vec<&str> {
    let mut fields: Vec<&str> = Vec::new();
    for condition in filter.conditions() {
        let field: &str = &condition.field;
        if !fields.contains(&field) {
            fields.push(field);
        }
    }
    fields
}

/// Lowers the predicate's pushable conjuncts onto extracted index columns
/// (D-42): the scalar comparison family over string and number values.
/// Everything else — presence tests, patterns, membership, booleans, nulls
/// — stays with the executor's exact predicate, so the pushed filter only
/// ever narrows.
fn pushdown_filter(predicate: &super::spec::QueryPredicate) -> Option<ScanFilter> {
    fn push(conditions: &mut Vec<ScanCondition>, field: &str, op: ScanOp, value: &DocumentValue) {
        if matches!(
            value,
            DocumentValue::String(_) | DocumentValue::Integer(_) | DocumentValue::Number(_)
        ) {
            conditions.push(ScanCondition { field: field.to_owned(), op, value: value.clone() });
        }
    }
    let mut conditions = Vec::new();
    for condition in predicate.conditions() {
        match condition {
            QueryCondition::Equals { field, value } => {
                push(&mut conditions, field, ScanOp::Equals, value);
            }
            QueryCondition::NotEquals { field, value } => {
                push(&mut conditions, field, ScanOp::NotEquals, value);
            }
            QueryCondition::LessThan { field, value } => {
                push(&mut conditions, field, ScanOp::Less, value);
            }
            QueryCondition::LessOrEqual { field, value } => {
                push(&mut conditions, field, ScanOp::LessOrEqual, value);
            }
            QueryCondition::GreaterThan { field, value } => {
                push(&mut conditions, field, ScanOp::Greater, value);
            }
            QueryCondition::GreaterOrEqual { field, value } => {
                push(&mut conditions, field, ScanOp::GreaterOrEqual, value);
            }
            QueryCondition::Between { field, low, high }
                if !matches!(high, DocumentValue::Null) =>
            {
                // A null upper bound is unbounded AND admits explicit nulls,
                // which a pushed lower comparison would silently drop under
                // SQL's three-valued logic: the whole range stays with the
                // executor's exact predicate. Every other shape lowers per
                // side — a null lower bound is unbounded and contributes no
                // conjunct — because the exact rule excludes nulls and
                // absence exactly where the pushed comparison drops NULL
                // columns.
                push(&mut conditions, field, ScanOp::GreaterOrEqual, low);
                push(&mut conditions, field, ScanOp::LessOrEqual, high);
            }
            // Presence, patterns, membership, full-text search, and a range
            // with a null upper bound have no extracted-column shape; the
            // executor's predicate is their exact filter.
            _ => {}
        }
    }
    ScanFilter::try_new(conditions)
}

/// Collects the predicate's full-text conjuncts in declaration order: one
/// `(field, query, match_any)` triple per `search` condition.
fn search_conditions(predicate: &super::spec::QueryPredicate) -> Vec<(&str, &str, bool)> {
    let mut searches = Vec::new();
    for condition in predicate.conditions() {
        if let QueryCondition::Search { field, query, match_any } = condition {
            searches.push((field.as_str(), query.as_str(), *match_any));
        }
    }
    searches
}

/// Applies the first-query text-index policy (S-02 slice D, D-31): every
/// searched field is covered by an active text descriptor before the scan
/// narrows. An absent field registers on the spot — a collection inside
/// the inline cost cap goes active at once and the same query already
/// narrows — while a field whose descriptor is still building fails the
/// query closed with [`QueryError::IndexNotReady`] until the governed
/// text pass completes it. A registration that leaves coverage unchanged
/// degrades to the executor's exact scan instead.
fn ensure_fts_fields(
    engine: &Engine,
    collection: &crate::types::CollectionName,
    searches: &[(&str, &str, bool)],
) -> Result<(), QueryError> {
    register_fts_fields(engine, collection, searches);
    for (field, _, _) in searches {
        if engine.fts_field_state(collection, field) == Some(false) {
            return Err(QueryError::IndexNotReady);
        }
    }
    Ok(())
}

/// Registers the searched fields without refusing anything: absent fields
/// derive their durable text descriptors (inline or building), every other
/// outcome leaves serving to the caller.
fn register_fts_fields(
    engine: &Engine,
    collection: &crate::types::CollectionName,
    searches: &[(&str, &str, bool)],
) {
    for (field, _, _) in searches {
        if engine.fts_field_state(collection, field).is_none() {
            let _ = engine.register_fts_field(collection, field);
        }
    }
}

/// Serves one FTS5 candidate set: loads the named ids at the snapshot —
/// tombstoned, expired, or concurrently vanished keys simply contribute
/// nothing — re-checks the executor's exact predicate over each survivor,
/// and retains the matches in score order when the query ranks, in model
/// order otherwise. The index narrows; the predicate filters.
#[allow(clippy::too_many_arguments, reason = "the scan path's context")]
fn scan_fts_candidates(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    query: &NormalizedQuery,
    collection: &crate::types::CollectionName,
    candidates: &[(String, f64)],
    indexed_ids: Option<&BTreeSet<DocumentId>>,
    retained_capacity: usize,
) -> Result<(Vec<Document>, u64, u64, u64), QueryError> {
    let ranked = query.search_rank().is_some();
    let direction = query
        .search_rank()
        .map_or(super::spec::OrderDirection::Descending, model::SearchRank::direction);
    let relevance_by_id: std::collections::BTreeMap<&str, f64> =
        candidates.iter().map(|(id, score)| (id.as_str(), *score)).collect();
    let mut matched: Vec<Document> = Vec::new();
    let mut ranked_retained: Vec<(f64, Document)> = Vec::new();
    let mut retained_bytes = 0_usize;
    let mut matched_count = 0_u64;
    let mut scanned = 0_u64;
    let mut skipped = 0_u64;
    for chunk in candidates.chunks(QUERY_SCAN_PAGE as usize) {
        check_bounds(query.deadline(), query.cancellation_signal())?;
        let ids: Vec<DocumentId> =
            chunk.iter().filter_map(|(id, _)| DocumentId::try_new(id).ok()).collect();
        let documents = engine
            .snapshot_documents_by_ids(scope, snapshot, collection, &ids)
            .map_err(|error| map_engine_error(&error))?;
        for (position, document) in documents.iter().enumerate() {
            if position % CHECKPOINT_INTERVAL == 0 {
                check_bounds(query.deadline(), query.cancellation_signal())?;
            }
            if indexed_ids.is_some_and(|ids| !ids.contains(document.id())) {
                continue;
            }
            scanned = scanned.saturating_add(1);
            if model::matches(query.predicate(), document) {
                matched_count = matched_count.saturating_add(1);
                if query.paging().limit().is_none() && matched_count > retained_capacity as u64 {
                    return Err(QueryError::CandidateBoundExceeded);
                }
                if ranked {
                    let relevance =
                        relevance_by_id.get(document.id().as_str()).copied().unwrap_or(0.0);
                    retain_scored(
                        query,
                        &mut ranked_retained,
                        &mut retained_bytes,
                        document,
                        relevance,
                        direction,
                        retained_capacity,
                    )?;
                } else {
                    retain_candidate(
                        query,
                        &mut matched,
                        &mut retained_bytes,
                        document,
                        retained_capacity,
                    )?;
                }
            } else {
                skipped = skipped.saturating_add(1);
            }
        }
    }
    if ranked {
        matched = ranked_retained.into_iter().map(|(_, document)| document).collect();
    }
    Ok((matched, matched_count, scanned, skipped))
}

/// Retains one scored candidate in rank order: the rank direction first,
/// document identity as the total tie-breaker. The window and byte bounds
/// match [`retain_candidate`]'s, so a ranked query is bounded exactly like
/// a model-ordered one.
fn retain_scored(
    query: &NormalizedQuery,
    retained: &mut Vec<(f64, Document)>,
    retained_bytes: &mut usize,
    document: &Document,
    score: f64,
    direction: super::spec::OrderDirection,
    capacity: usize,
) -> Result<(), QueryError> {
    check_bounds(query.deadline(), query.cancellation_signal())?;
    let position = retained
        .iter()
        .position(|(other, id)| {
            compare_scores(direction, score, document.id().as_str(), *other, id.id().as_str())
                == std::cmp::Ordering::Less
        })
        .unwrap_or(retained.len());
    *retained_bytes = (*retained_bytes).saturating_add(document_bytes(document));
    retained.insert(position, (score, document.clone()));
    if retained.len() > capacity
        && let Some((_, removed)) = retained.pop()
    {
        *retained_bytes = (*retained_bytes).saturating_sub(document_bytes(&removed));
    }
    if *retained_bytes > MAX_RETAINED_CANDIDATE_BYTES {
        return Err(QueryError::CandidateBoundExceeded);
    }
    Ok(())
}

/// Orders two scored candidates: the rank direction over the scores, then
/// document identity ascending as the total tie-breaker so paging is
/// stable. NaN scores sort last in either direction; stored scores are
/// finite `-bm25` values, so the arm is unreachable defense.
fn compare_scores(
    direction: super::spec::OrderDirection,
    left_score: f64,
    left_id: &str,
    right_score: f64,
    right_id: &str,
) -> std::cmp::Ordering {
    let ordering = left_score
        .partial_cmp(&right_score)
        .unwrap_or_else(|| {
            if left_score.is_nan() && right_score.is_nan() {
                std::cmp::Ordering::Equal
            } else if left_score.is_nan() {
                std::cmp::Ordering::Less
            } else {
                std::cmp::Ordering::Greater
            }
        })
        .then_with(|| left_id.cmp(right_id));
    match direction {
        super::spec::OrderDirection::Ascending => ordering,
        super::spec::OrderDirection::Descending => ordering.reverse(),
    }
}

/// Scores one document for the scan fallback's relevance order: the total
/// count of satisfied positive search clauses over the field values the
/// search conditions name. The exact predicate already admitted the
/// document; this only orders survivors deterministically when FTS5
/// scores are unavailable.
fn fallback_score(document: &Document, predicate: &super::spec::QueryPredicate) -> f64 {
    let mut hits = 0_usize;
    for condition in predicate.conditions() {
        let QueryCondition::Search { field, query, match_any } = condition else { continue };
        let parsed = super::parse_search_query(query, *match_any);
        let mut terms = Vec::new();
        match model::field_of(document, field) {
            Some(DocumentValue::String(text)) => {
                terms.extend(super::standard_terms(text));
            }
            Some(DocumentValue::Array(elements)) => {
                for element in elements {
                    if let DocumentValue::String(text) = element {
                        terms.extend(super::standard_terms(text));
                    }
                }
            }
            _ => {}
        }
        hits = hits.saturating_add(super::satisfied_positive_clauses(&terms, &parsed));
    }
    #[allow(
        clippy::cast_precision_loss,
        reason = "the clause count is bounded by the query's condition cap, far below 2^52"
    )]
    let relevance = hits as f64;
    relevance
}

/// Retains only the best candidates for the requested order. Stable sorting
/// preserves canonical source order for equal ordering keys.
fn retain_candidate(
    query: &NormalizedQuery,
    retained: &mut Vec<Document>,
    retained_bytes: &mut usize,
    document: &Document,
    capacity: usize,
) -> Result<(), QueryError> {
    check_bounds(query.deadline(), query.cancellation_signal())?;
    let position = {
        let mut low = 0;
        let mut high = retained.len();
        while low < high {
            let middle = low + (high - low) / 2;
            if model::compare_documents(query.ordering(), &retained[middle], document)
                == std::cmp::Ordering::Greater
            {
                high = middle;
            } else {
                // Insert after equal keys to preserve stable canonical order.
                low = middle + 1;
            }
        }
        low
    };
    *retained_bytes = (*retained_bytes).saturating_add(document_bytes(document));
    retained.insert(position, document.clone());
    if retained.len() > capacity
        && let Some(removed) = retained.pop()
    {
        *retained_bytes = (*retained_bytes).saturating_sub(document_bytes(&removed));
    }
    if *retained_bytes > MAX_RETAINED_CANDIDATE_BYTES {
        return Err(QueryError::CandidateBoundExceeded);
    }
    Ok(())
}

pub(crate) fn document_bytes(document: &Document) -> usize {
    document
        .id()
        .as_str()
        .len()
        .saturating_add(document.collection().as_str().len())
        .saturating_add(document_value_bytes(document.value()))
}

fn document_value_bytes(value: &DocumentValue) -> usize {
    match value {
        DocumentValue::Null
        | DocumentValue::Boolean(_)
        | DocumentValue::Integer(_)
        | DocumentValue::Number(_) => 8,
        DocumentValue::String(value) => value.len().saturating_add(8),
        DocumentValue::Array(values) => {
            values.iter().fold(8, |bytes, value| bytes.saturating_add(document_value_bytes(value)))
        }
        DocumentValue::Object(values) => values.iter().fold(8, |bytes, (key, value)| {
            bytes.saturating_add(key.len()).saturating_add(document_value_bytes(value))
        }),
    }
}

fn page_documents<'a>(query: &NormalizedQuery, ordered: &'a [Document]) -> Vec<&'a Document> {
    let offset = usize::try_from(query.paging().offset()).unwrap_or(usize::MAX);
    let start = offset.min(ordered.len());
    let end = query
        .paging()
        .limit()
        .map_or(ordered.len(), |limit| start.saturating_add(limit as usize).min(ordered.len()));
    ordered[start..end].iter().collect()
}

/// Resolves only IDs reachable from the result page. Tenant pages are read in
/// canonical full-key order, so the first occurrence wins when collections
/// reuse an identifier; no complete tenant vector is retained.
fn resolve_references(
    engine: &Engine,
    snapshot: &ReadSnapshot,
    query: &NormalizedQuery,
    rows: &[&Document],
) -> Result<Vec<Document>, QueryError> {
    if query.loads().is_empty() && query.includes().is_empty() {
        return Ok(Vec::new());
    }
    let max_references = super::includes::MAX_INCLUDE_DOCUMENTS + super::spec::MAX_LOADS;
    let mut pending = BTreeSet::new();
    for document in rows {
        for load in query.loads() {
            if let Some(DocumentValue::String(reference)) = model::field_of(document, load.field())
                && !reference.is_empty()
            {
                pending.insert(reference.clone());
            }
        }
        pending.extend(super::includes::referenced_ids(document, query.includes()));
    }
    if pending.len() > max_references {
        return Err(reference_bound_error(query));
    }

    let mut resolved = BTreeMap::<String, Document>::new();
    for depth in 0..super::includes::MAX_INCLUDE_DEPTH {
        if pending.is_empty() {
            break;
        }
        let mut after = None;
        let mut discovered = Vec::new();
        loop {
            check_bounds(query.deadline(), query.cancellation_signal())?;
            let page = engine
                .snapshot_records_from(
                    snapshot.tenant().clone(),
                    snapshot.sequence(),
                    after,
                    QUERY_SCAN_PAGE,
                )
                .map_err(|error| map_engine_error(&error))?;
            if page.is_empty() {
                break;
            }
            for (position, record) in page.iter().enumerate() {
                if position % CHECKPOINT_INTERVAL == 0 {
                    check_bounds(query.deadline(), query.cancellation_signal())?;
                }
                let document = record.document();
                if pending.contains(document.id().as_str())
                    && !resolved.contains_key(document.id().as_str())
                {
                    resolved.insert(document.id().as_str().to_owned(), document.clone());
                    discovered.push(document.clone());
                    if resolved.len() > max_references {
                        return Err(reference_bound_error(query));
                    }
                }
            }
            if pending.iter().all(|id| resolved.contains_key(id)) {
                break;
            }
            if page.len() < QUERY_SCAN_PAGE as usize {
                break;
            }
            after = page.last().map(|record| {
                (record.document().collection().clone(), record.document().id().clone())
            });
        }
        pending.retain(|id| !resolved.contains_key(id));
        if discovered.is_empty() {
            break;
        }
        if depth + 1 >= super::includes::MAX_INCLUDE_DEPTH {
            break;
        }
        for document in &discovered {
            pending.extend(super::includes::document_referenced_ids(
                document,
                query.includes(),
                depth + 1,
            ));
        }
        if pending.len() > max_references {
            return Err(reference_bound_error(query));
        }
    }
    Ok(resolved.into_values().collect())
}

fn reference_bound_error(query: &NormalizedQuery) -> QueryError {
    if query.includes().is_empty() {
        QueryError::CandidateBoundExceeded
    } else {
        QueryError::IncludeBoundExceeded
    }
}

/// Sorts nothing and reads no data: this is the bounded materialization step
/// after the stable sort.
///
/// The first checkpoint fires before any row is produced, so a deadline that
/// expired during the sort still stops the query, and one further checkpoint
/// runs every [`CHECKPOINT_INTERVAL`] materialized rows so a large page
/// cannot outlive its bounds either. Materializing clones documents and
/// projected fields only; it holds no lock.
pub(crate) fn materialize(
    query: &NormalizedQuery,
    ordered: &[Document],
    by_id: &std::collections::BTreeMap<&str, &Document>,
) -> Result<Vec<QueryRow>, QueryError> {
    let offset = usize::try_from(query.paging().offset()).unwrap_or(usize::MAX);
    let limit = query
        .paging()
        .limit()
        .map_or(usize::MAX, |limit| usize::try_from(limit).unwrap_or(usize::MAX));
    // One shape per load clause, built once for the whole page: per-row
    // resolution and projection only read the shapes, never rebuild them.
    let load_shapes: Vec<LoadShape> = query
        .loads()
        .iter()
        .map(|load| LoadShape {
            field: load.field().to_owned(),
            alias: load.alias().to_owned(),
            dotted: format!("{}.", load.alias()),
        })
        .collect();
    let mut rows = Vec::new();
    for (position, document) in ordered.iter().enumerate() {
        if position % CHECKPOINT_INTERVAL == 0 {
            check_bounds(query.deadline(), query.cancellation_signal())?;
        }
        if position < offset {
            continue;
        }
        if rows.len() >= limit {
            break;
        }
        let loads = resolve_loads(&load_shapes, document, by_id);
        rows.push(QueryRow::new(document.clone(), project(query, document, &loads)));
    }
    Ok(rows)
}

/// One load clause precomputed once per query: the source field, the alias,
/// and the alias's dotted prefix. Projection runs per matched row and per
/// projection name, so formatting the prefix (or re-cloning the alias) inside
/// that loop would allocate per row per name; the shapes are built once in
/// [`materialize`] instead.
struct LoadShape {
    field: String,
    alias: String,
    dotted: String,
}

/// One resolved load binding: the clause's shape, and the referenced
/// document or `None` when the reference resolved to no document (which
/// binds null).
type ResolvedLoad<'a> = (&'a LoadShape, Option<&'a Document>);

/// Resolves the loads clause of one matched document.
///
/// A load reads its field's string value as a document id; a missing field,
/// a non-string value, or an unknown id resolves to nothing and the alias
/// binds null — the reference contract V1's id-lookup `LOAD` defines. An id
/// reused across the tenant's collections resolves to the first document in
/// canonical key order, the rule of the engine's identifier-only point read.
fn resolve_loads<'a>(
    shapes: &'a [LoadShape],
    document: &Document,
    by_id: &std::collections::BTreeMap<&str, &'a Document>,
) -> Vec<ResolvedLoad<'a>> {
    shapes
        .iter()
        .map(|shape| {
            let referenced = match model::field_of(document, shape.field.as_str()) {
                Some(DocumentValue::String(id)) => by_id.get(id.as_str()).copied(),
                _ => None,
            };
            (shape, referenced)
        })
        .collect()
}

/// Projects one matched document into its row fields.
///
/// A projection name that names a load alias (`alias`) or a field of one
/// (`alias.field`) reads the resolved referenced document: the alias itself
/// projects the referenced document object, its qualified field projects
/// that document's field, a null binding projects null, and an absent field
/// of a resolved reference is omitted exactly as an absent source field is.
/// Every other name reads the matched source document.
pub(crate) fn project_live(
    query: &NormalizedQuery,
    document: &Document,
) -> Vec<(String, DocumentValue)> {
    project(query, document, &[])
}

fn project(
    query: &NormalizedQuery,
    document: &Document,
    loads: &[ResolvedLoad<'_>],
) -> Vec<(String, DocumentValue)> {
    let DocumentValue::Object(fields) = document.value() else {
        return Vec::new();
    };
    let Some(selected) = query.projection().selected() else {
        return fields.iter().map(|(name, value)| (name.clone(), value.clone())).collect();
    };
    selected
        .iter()
        .enumerate()
        .filter_map(|(index, name)| {
            let output = query
                .sql_projection_outputs()
                .and_then(|outputs| outputs.get(index))
                .map_or(name.as_str(), String::as_str);
            let metadata = match name.as_str() {
                "_id" => Some(DocumentValue::String(document.id().as_str().to_owned())),
                "_rev" => Some(DocumentValue::Integer(
                    i64::try_from(document.metadata().version()).unwrap_or(i64::MAX),
                )),
                _ => None,
            };
            for (shape, referenced) in loads {
                if name == &shape.alias {
                    let value =
                        referenced.map_or(DocumentValue::Null, |document| document.value().clone());
                    return Some((output.to_owned(), value));
                }
                if let Some(field) = name.strip_prefix(shape.dotted.as_str()) {
                    return match referenced {
                        // A null binding navigates to null; the projected
                        // field is present with a null value.
                        None => Some((output.to_owned(), DocumentValue::Null)),
                        Some(document) => match document.value() {
                            DocumentValue::Object(referenced_fields) => referenced_fields
                                .get(field)
                                .map(|value| (output.to_owned(), value.clone())),
                            _ => None,
                        },
                    };
                }
            }
            metadata.or_else(|| fields.get(name).cloned()).map(|value| (output.to_owned(), value))
        })
        .collect()
}
