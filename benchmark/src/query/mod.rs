//! The typed query and index seam over immutable snapshots.
//!
//! This module implements the W3-2 query contract: one compile/execute path
//! over [`QueryInput`], execution against an immutable [`ReadSnapshot`], and
//! generation-aware index selection. Typed clauses and bounded TQL text
//! normalize into one private model, the plan is
//! immutable after compilation, and execution consumes only the caller's
//! snapshot.
//!
//! GAP-Q19 adds the family query sources: counters and time-series are
//! typed sources beside user collections, answering through typed family
//! rows (`FamilyQueryRow`) read by the family seam against the query's own
//! snapshot — with cursor-stable time-series paging through the GAP-F04
//! window — while the documents path, its rows, and its includes stay
//! exactly as they were. The decision record is
//! `docs/query/gapq19-family-sources.md`.
//!
//! W3-2 proves the seam; it does not claim the V1 query matrix. Execution uses
//! bounded exact-key postings for the safely supported scalar equality shape
//! and deterministically falls back to a snapshot scan for other predicates;
//! the private index catalog keeps in-memory segments. Every query parity row
//! stays unresolved until its parity test and Proof ID exist. The design
//! record with the supported grammar, per-row coverage notes, limits, and
//! non-claims is `docs/query/w3-query-index.md`.

mod analyzer;
mod execute;
pub mod includes;
mod model;
pub mod optimizer;
mod result;
mod spatial;
mod spec;
mod statistics;
mod text;
mod vector;

pub mod bulk;
mod family_sources;
pub mod tql;

#[cfg(test)]
mod bulk_tests;
#[cfg(test)]
mod family_sources_tests;
#[cfg(test)]
mod includes_tests;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod vector_tests;

use std::sync::Arc;

pub use bulk::{
    BulkAuditEntry, BulkAuditStage, BulkByQueryOptions, BulkByQueryReceipt, BulkDetail,
    BulkDetailOutcome, BulkOperationKind, BulkPageReceipt, BulkRefusal, BulkTermination,
    DeleteByQueryRequest, DeletionConfirmation, MAX_BULK_AUDIT_ENTRIES, MAX_BULK_DETAILS,
    MAX_BULK_PAGE_DOCUMENTS, PatchByQueryRequest,
};
// GAP-Q13 (TerraBaseV2#67) collision action: the analyzer foundation was
// wired in as a declaration-only private root; without this re-export its
// typed contract is unreachable and rustc treats its contents as dead
// code. The export shape is the parent's to ratify or reshape.
pub use analyzer::{
    ANALYZER_LANGUAGE_PREFIX, ANALYZER_LANGUAGE_VERSION, Analyzer, AnalyzerError, AnalyzerKind,
    MAX_ANALYZED_VALUE_BYTES, MAX_ANALYZER_NAME_BYTES, MAX_TERM_BYTES, MAX_TOKEN_FILTERS,
    MAX_TOKENS_PER_FIELD, TermOffset, TermVector, TermVectorEntry, TermVectorMode, TokenFilterKind,
    TokenizerKind,
};
pub use includes::{
    CounterInclude, DocumentInclude, INCLUDE_READ_WINDOW, IncludeClause, IncludePath, IncludeSet,
    IncludedCounters, IncludedTimeSeries, MAX_INCLUDE_COUNTER_NAMES, MAX_INCLUDE_DEPTH,
    MAX_INCLUDE_DOCUMENTS, MAX_INCLUDE_PATH_SEGMENTS, MAX_INCLUDES, TimeSeriesInclude,
};
pub use result::{
    CounterQueryRow, FamilyQueryRow, IndexGenerationRef, ObservedGeneration, QueryResult, QueryRow,
    SeriesPageCursor, StaleIndexObservation, StalenessReport, TimeSeriesQueryRow,
};
pub use spec::{
    IndexNamespace, MAX_CONDITIONS, MAX_FIELD_BYTES, MAX_IN_VALUES, MAX_LOADS, MAX_ORDERING_KEYS,
    MAX_PAGE_LIMIT, MAX_PROJECTION_FIELDS, MAX_STALENESS_INDEXES, NullsPlacement, OrderDirection,
    OrderingKey, QueryCondition, QueryLoad, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QuerySource, QuerySourceKind, QuerySpec, StalenessMode, StalenessPolicy,
};
// GAP-Q14 collision action: the spatial foundation types are defined in
// the private `spatial` module the integrator wired; this re-export is the
// minimal module-root edit that makes them the query seam's public
// vocabulary. Everything else in that module stays private.
pub use spatial::{
    DEFAULT_DISTANCE_ERROR_PCT, DEFAULT_PRECISION_DECIMALS, EARTH_MEAN_RADIUS_KM, Latitude,
    Longitude, MAX_DISTANCE_ERROR_PCT, MAX_POLYGON_RINGS, MAX_PRECISION_DECIMALS, MAX_SHAPE_POINTS,
    MAX_WKT_BYTES, MILES_TO_KM, QuantizedCell, SpatialFieldEncoding, SpatialPolygon,
    SpatialPrecision, SpatialPredicate, SpatialRelation, SpatialShape, SpatialShapeError,
    SpatialUnits, Wgs84Point,
};
// The GAP-F04 window values a time-series source query binds (GAP-Q19):
// re-exported beside the seam that consumes them so callers reach the
// window and its source through one vocabulary.
pub use crate::families::{TimeSeriesPaging, TimeSeriesRange};
pub use statistics::{DiagnosticDuration, QueryStatistics, ResultEtag};
#[allow(unused_imports, reason = "the public TqlQueryText constructor delegates here")]
pub(crate) use text::validate as validate_tql_text;
pub use vector::{
    DEFAULT_VECTOR_SEARCH_LIMIT, MAX_VECTOR_DIMENSIONS, MAX_VECTOR_SEARCH_LIMIT,
    MIN_VECTOR_DIMENSIONS, TqlVectorPredicate, VECTOR_PREDICATE_VERSION, VECTOR_SEARCH_WINDOW,
    VectorEmbeddingType, VectorIndexDeclaration, VectorIndexDrift, VectorIndexError, VectorMatch,
    VectorOptions, VectorOptionsError, VectorSearchError, VectorSearchMode, VectorSearchRequest,
    VectorSearchResult, VectorSearchStatistics, VectorSimilarity,
};

use crate::{
    engine::Engine,
    index::IndexCatalog,
    operations::{QueryInput, QueryInputError},
    types::{Document, ReadSnapshot, StorageSequence, TenantId},
};
use model::NormalizedQuery;

/// Compares two documents under one typed ordering, by the seam's total
/// document order over the ordering's key fields.
///
/// Crate-only: the sharded fan-out (`crate::sharding`) merges per-shard
/// pages with exactly the ordering comparison the single-shard execution
/// applies, so a fanned-out result and a single-shard result over the same
/// documents are one order. This is SH-04's one additive hook into this
/// seam; it delegates to the existing comparison and changes no behavior.
pub(crate) fn compare_documents_under_ordering(
    ordering: &QueryOrdering,
    left: &Document,
    right: &Document,
) -> std::cmp::Ordering {
    model::compare_documents(ordering, left, right)
}

/// Query planning or execution failures.
///
/// Every case is typed and carries no document value, storage detail, path,
/// or private implementation type.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QueryError {
    /// The Engine has closed.
    EngineClosed,
    /// The snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// The snapshot names another tenant than the query's tenant claim.
    SnapshotTenantMismatch,
    /// A required index namespace or data-access scope belongs to another
    /// tenant than the query's tenant binding.
    TenantMismatch,
    /// The typed query specification is invalid.
    InvalidSpec,
    /// A required index generation is missing, too old, or from a different
    /// epoch, and the policy is strict. The optimizer's fail-closed refusals
    /// surface here too: an unmatched dynamic query when automatic creation
    /// is disabled, or a creation refused by the identity guard — the query
    /// fails rather than silently scanning without its index. A bounded
    /// posting-list row or byte refusal also uses this fail-closed error so a
    /// partial index result is never returned.
    IndexNotReady,
    /// The request deadline expired.
    Deadline,
    /// The request was cancelled.
    Cancelled,
    /// A chained document include re-entered a document the execution had
    /// already gathered from. The include graph carries a reference cycle
    /// reachable within the declared depth, and the query refuses instead
    /// of following it.
    IncludeCycle,
    /// The include set would materialize more than
    /// [`MAX_INCLUDE_DOCUMENTS`] distinct
    /// documents. Depth bounds the levels, not the width, so this is the
    /// bound that keeps one array field from materializing without limit.
    IncludeBoundExceeded,
    /// The Engine's snapshot view returned a typed failure.
    StorageFailure,
}

/// An immutable, generation-aware query plan.
///
/// A plan is produced only by [`QueryStore::compile`] and never changes
/// afterwards. It owns no index, no lock, and no storage handle, so it can be
/// reused with any compatible snapshot; execution reads the snapshot and
/// never writes one back.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryPlan {
    query: NormalizedQuery,
}

impl QueryPlan {
    /// Creates a plan from one normalized model. Crate-only: compile is the
    /// only plan constructor.
    pub(crate) fn new(query: NormalizedQuery) -> Self {
        Self { query }
    }

    /// Returns the plan's model. Crate-only planner surface.
    pub(crate) fn model(&self) -> &NormalizedQuery {
        &self.query
    }

    /// Returns the tenant claim the plan carries, or `None` for the
    /// V1-compatible form, whose tenant binding is the snapshot's tenant.
    #[must_use]
    pub fn tenant_claim(&self) -> Option<&TenantId> {
        self.query.tenant_claim()
    }

    /// Returns the typed source of the plan.
    #[must_use]
    pub fn source(&self) -> &QuerySource {
        self.query.source()
    }

    /// Returns the ordering the plan will apply.
    #[must_use]
    pub fn ordering(&self) -> &QueryOrdering {
        self.query.ordering()
    }

    /// Returns the include clauses the plan will materialize, in
    /// declaration order.
    ///
    /// An empty list materializes nothing: the includes contract is
    /// additive, exactly like the loads clause, and a query without
    /// includes pays nothing for it.
    #[must_use]
    pub fn includes(&self) -> &[IncludeClause] {
        self.query.includes()
    }
}

/// The index-generation view one query evaluates staleness against.
///
/// The production implementation is the private index catalog. The seam is
/// crate-private so the selection rule stays provable with independent
/// observed and supplied generations.
pub(crate) trait GenerationView {
    /// Returns the generation a snapshot at the given sequence observed for
    /// one namespace, with its epoch.
    fn observed_generation(
        &self,
        namespace: &IndexNamespace,
        at: StorageSequence,
    ) -> Option<ObservedGeneration>;

    /// Returns the generation the view can currently supply for one
    /// namespace, with its epoch.
    fn available_generation(&self, namespace: &IndexNamespace) -> Option<ObservedGeneration>;
}

/// A query store borrowed from one Engine.
///
/// The store is bound to its Engine's lifetime and durable open state. It can
/// neither outlive the Engine, open a storage location, nor switch to an
/// in-memory implementation, and
/// [`Engine::queries`](crate::engine::Engine::queries) is its only
/// production acquisition path.
///
/// ```compile_fail
/// // No store may outlive its Engine. This must not compile: `shutdown`
/// // consumes the Engine while the borrowed store is still live. The example
/// // fails on exactly that borrow rule — every other line is valid.
/// use terrabase_v2::{Engine, EngineConfig};
/// let engine = Engine::open(EngineConfig::durable("/tmp/tbv2-docstore").unwrap()).unwrap();
/// let store = engine.queries();
/// engine.shutdown().unwrap();
/// drop(store);
/// ```
///
/// ```compile_fail
/// // There is no independent durable or in-memory query constructor. This
/// // must not compile.
/// let store = terrabase_v2::query::QueryStore::open("/tmp/other");
/// ```
pub struct QueryStore<'engine> {
    engine: &'engine Engine,
    indexes: Arc<IndexCatalog>,
}

impl<'engine> QueryStore<'engine> {
    /// Binds one store to one Engine and its engine-owned index catalog
    /// (integrator action W4-CA-3). Crate-only: the durable Engine is the
    /// only production acquisition path.
    pub(crate) fn from_engine(engine: &'engine Engine) -> Self {
        Self { engine, indexes: engine.index_catalog() }
    }

    /// Returns one tenant's recent index selection records, oldest first.
    ///
    /// The record store is bounded engine state: the newest
    /// [`MAX_SELECTION_RECORDS`](optimizer::MAX_SELECTION_RECORDS)
    /// selections of the engine are retained, and only this tenant's records
    /// are ever returned.
    #[must_use]
    pub fn recent_index_selections(
        &self,
        tenant: &crate::types::TenantId,
    ) -> Vec<optimizer::IndexSelectionRecord> {
        self.indexes.selection_records(tenant)
    }

    /// Returns one tenant's registered indexes, explicit and automatic, in
    /// catalog order.
    #[must_use]
    pub fn registered_indexes(
        &self,
        tenant: &crate::types::TenantId,
    ) -> Vec<optimizer::RegisteredIndex> {
        optimizer::registered_indexes_of(&self.indexes, tenant)
    }

    /// Compiles typed or V1-compatible input into an immutable plan.
    ///
    /// Both input forms normalize through this one seam: the typed form is
    /// re-validated as it is normalized, and the V1-compatible text is parsed
    /// against the supported grammar subset and rejected with
    /// [`QueryError::InvalidSpec`] for anything the subset does not define.
    ///
    /// # Errors
    ///
    /// Returns [`QueryError::InvalidSpec`] for an invalid typed specification
    /// or unsupported query text, and [`QueryError::TenantMismatch`] when a
    /// required index namespace belongs to another tenant than the query's
    /// tenant claim.
    #[allow(clippy::needless_pass_by_value)] // the frozen contract passes the input by value
    pub fn compile(&self, input: QueryInput) -> Result<QueryPlan, QueryError> {
        match input {
            QueryInput::Typed(spec) => self.compile_normalized(NormalizedQuery::from_spec(
                spec.source().clone(),
                spec.predicate().clone(),
                spec.ordering().clone(),
                spec.projection().clone(),
                spec.paging(),
                spec.staleness().clone(),
                spec.loads().to_vec(),
                spec.tenant_claim().clone(),
                spec.deadline(),
                spec.cancellation(),
                spec.includes().to_vec(),
                spec.time_series_window(),
            )),
            QueryInput::Tql(text) => {
                let (source, predicate, ordering, paging, projection) =
                    text::parse_clauses(text.as_str()).map_err(input_error)?;
                self.compile_normalized(NormalizedQuery::unbound(
                    source,
                    predicate,
                    ordering,
                    projection,
                    paging,
                    text::default_staleness(),
                ))
            }
            QueryInput::TqlBounded { text, deadline, cancellation } => {
                let (source, predicate, ordering, paging, projection) =
                    text::parse_clauses(text.as_str()).map_err(input_error)?;
                self.compile_normalized(
                    NormalizedQuery::unbound(
                        source,
                        predicate,
                        ordering,
                        projection,
                        paging,
                        text::default_staleness(),
                    )
                    .with_control(deadline, cancellation),
                )
            }
        }
    }

    /// Compiles raw TQL text through the same seam.
    ///
    /// Test-only: the public path wraps the text in
    /// [`TqlQueryText`](crate::operations::TqlQueryText) first, and that
    /// constructor validates through the same parser.
    #[cfg(test)]
    pub(crate) fn compile_tql_text(&self, text: &str) -> Result<QueryPlan, QueryError> {
        let (source, predicate, ordering, paging, projection) =
            text::parse_clauses(text).map_err(input_error)?;
        self.compile_normalized(NormalizedQuery::unbound(
            source,
            predicate,
            ordering,
            projection,
            paging,
            text::default_staleness(),
        ))
    }

    /// Validates one normalized model and freezes it into a plan.
    #[allow(clippy::unused_self, reason = "compile is one store-bound seam")]
    fn compile_normalized(&self, query: NormalizedQuery) -> Result<QueryPlan, QueryError> {
        if let Some(claim) = query.tenant_claim() {
            for namespace in query.staleness().indexes() {
                if namespace.tenant() != claim {
                    return Err(QueryError::TenantMismatch);
                }
            }
        }
        // The family-source fail-closed rules (GAP-Q19): a family source is
        // served index-less over the family read seam, its rows are typed
        // family values rather than projected documents, and its clauses
        // are the ones the documents path defines for documents. Anything
        // the family contract does not define refuses here instead of
        // silently degrading at execution.
        if query.source().kind() != QuerySourceKind::Collection {
            if !query.staleness().indexes().is_empty() {
                return Err(QueryError::InvalidSpec);
            }
            if !query.loads().is_empty() || !query.includes().is_empty() {
                return Err(QueryError::InvalidSpec);
            }
            if query.projection().selected().is_some() {
                return Err(QueryError::InvalidSpec);
            }
        }
        Ok(QueryPlan::new(query))
    }

    /// Executes an immutable plan against an immutable snapshot.
    ///
    /// The snapshot's Engine identity, its tenant, and the plan's tenant
    /// claim are validated before any data access, the execution observes
    /// only the supplied snapshot, and the result reports the ordering, the
    /// per-namespace staleness observations, and the index generation used.
    ///
    /// # Errors
    ///
    /// Returns [`QueryError::SnapshotEngineMismatch`] for a snapshot from
    /// another Engine, [`QueryError::SnapshotTenantMismatch`] when the
    /// snapshot names another tenant than the plan's claim,
    /// [`QueryError::TenantMismatch`] when a required index namespace belongs
    /// to another tenant, [`QueryError::IndexNotReady`] when a strict policy
    /// is not met, [`QueryError::Deadline`] or [`QueryError::Cancelled`] at a
    /// bounded checkpoint, and [`QueryError::StorageFailure`] when the
    /// snapshot view fails.
    pub fn execute(
        &self,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryResult, QueryError> {
        execute::execute(self.engine, &self.indexes, plan, snapshot)
    }
}

/// Maps a grammar rejection onto the compile error. Both input forms report
/// unsupported text through the same typed path.
fn input_error(error: QueryInputError) -> QueryError {
    match error {
        QueryInputError::EmptyText
        | QueryInputError::UnsupportedGrammar
        | QueryInputError::InvalidIdentifier => QueryError::InvalidSpec,
    }
}
