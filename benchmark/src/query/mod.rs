//! The typed query and index seam over immutable snapshots.
//!
//! This module implements the W3-2 query contract: one compile/execute path
//! over [`QueryInput`], execution against an immutable [`ReadSnapshot`], and
//! generation-aware index selection. Typed clauses normalize into one private
//! model, the plan is immutable after compilation, and execution consumes
//! only the caller's snapshot. SQL is the only query language (D-78); its
//! supported read-only subset lowers through the same model and executor.
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
mod spec;
mod sql_execute;
mod statistics;
mod vector;

pub mod bulk;
mod family_sources;
pub mod sql;

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
// The full-text search term space (S-02 slice D): the storage write path,
// the FTS5 narrowing, and the exact in-memory check share one vocabulary,
// so the three can never disagree on what a term is.
pub(crate) use analyzer::{
    parse_search_query, sanitize_to_fts5_match, satisfied_positive_clauses, standard_terms,
};
pub use includes::{
    CounterInclude, DocumentInclude, INCLUDE_READ_WINDOW, IncludeClause, IncludePath, IncludeSet,
    IncludedCounters, IncludedTimeSeries, MAX_INCLUDE_COUNTER_NAMES, MAX_INCLUDE_DEPTH,
    MAX_INCLUDE_DOCUMENTS, MAX_INCLUDE_PATH_SEGMENTS, MAX_INCLUDES, TimeSeriesInclude,
};
pub use result::{
    CounterQueryRow, ExplainAccess, ExplainIndex, ExplainIndexKind, ExplainIndexState,
    ExplainOrder, FamilyQueryRow, IndexGenerationRef, ObservedGeneration, QueryExplanation,
    QueryResult, QueryRow, SeriesPageCursor, StaleIndexObservation, StalenessReport,
    TimeSeriesQueryRow,
};
pub use spec::{
    IndexNamespace, MAX_CONDITIONS, MAX_FIELD_BYTES, MAX_IN_VALUES, MAX_LOADS, MAX_ORDERING_KEYS,
    MAX_PAGE_LIMIT, MAX_PROJECTION_FIELDS, MAX_SEARCH_QUERY_BYTES, MAX_STALENESS_INDEXES,
    NullsPlacement, OrderDirection, OrderingKey, QueryCondition, QueryLoad, QueryOrdering,
    QueryPaging, QueryPredicate, QueryProjection, QuerySource, QuerySourceKind, QuerySpec,
    StalenessMode, StalenessPolicy,
};
// The GAP-F04 window values a time-series source query binds (GAP-Q19):
// re-exported beside the seam that consumes them so callers reach the
// window and its source through one vocabulary.
pub use crate::families::{TimeSeriesPaging, TimeSeriesRange};
pub use statistics::{DiagnosticDuration, QueryStatistics, ResultEtag};
// The public SqlQueryText constructor validates through the read-only SQL
// front end (`sql`); the full SQL planner is track item E-4.
pub(crate) use sql::SqlErrorClass;
/// The launch live-subset tag and the typed subscribe refusal (E-4, D-78):
/// the whole `NotSubscribable` decision, pinned for SDK conformance.
pub use sql::{LiveSubset, NotSubscribable};
pub use vector::{
    DEFAULT_VECTOR_SEARCH_LIMIT, MAX_VECTOR_DIMENSIONS, MAX_VECTOR_SEARCH_LIMIT,
    MIN_VECTOR_DIMENSIONS, VECTOR_PREDICATE_VERSION, VECTOR_SEARCH_WINDOW, VectorEmbeddingType,
    VectorIndexDeclaration, VectorIndexDrift, VectorIndexError, VectorMatch, VectorOptions,
    VectorOptionsError, VectorPredicate, VectorSearchError, VectorSearchMode, VectorSearchRequest,
    VectorSearchResult, VectorSearchStatistics, VectorSimilarity,
};

use crate::{
    engine::Engine,
    index::IndexCatalog,
    operations::{QueryInput, QueryInputError},
    types::{ReadSnapshot, StorageSequence, TenantId},
};
use model::NormalizedQuery;

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
    /// partial index result is never returned. A durable extracted-column
    /// descriptor that is still building refuses here too, until its
    /// governed backfill completes it.
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
    /// The ordered candidate window cannot retain the requested offset/page,
    /// or the query matched more candidates than its hard memory ceiling.
    CandidateBoundExceeded,
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
    sql_runtime: Option<Box<sql::SqlRuntime>>,
}

impl QueryPlan {
    /// Creates a plan from one normalized model. Crate-only: compile is the
    /// only plan constructor.
    pub(crate) fn new(query: NormalizedQuery) -> Self {
        Self { query, sql_runtime: None }
    }

    pub(crate) fn new_sql(query: NormalizedQuery, runtime: sql::SqlRuntime) -> Self {
        Self { query, sql_runtime: Some(Box::new(runtime)) }
    }

    /// Binds the score-rank request of a relevance-ordered search.
    /// Test-only: in-crate search tests bind the rank the SQL planner
    /// lowers from relevance ordering.
    #[cfg(test)]
    pub(crate) fn with_search_rank(self, direction: OrderDirection) -> Self {
        Self { query: self.query.with_search_rank(direction), ..self }
    }

    /// Returns the plan's model. Crate-only planner surface.
    pub(crate) fn model(&self) -> &NormalizedQuery {
        &self.query
    }

    /// Returns the tenant claim the plan carries, or `None` for the
    /// SQL text form, whose tenant binding is the snapshot's tenant.
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

    /// Returns the projected field selection, or `None` for whole documents.
    #[must_use]
    pub fn projection(&self) -> &QueryProjection {
        self.query.projection()
    }

    /// Returns the execute-only SQL operator, when this plan is not safe for
    /// incremental subscription. `None` identifies the live single-
    /// collection subset.
    #[must_use]
    pub fn sql_execute_only_operator(&self) -> Option<&'static str> {
        self.sql_runtime.as_ref().and_then(|runtime| runtime.operator)
    }

    /// Returns the typed subscription refusal for an execute-only SQL plan.
    #[must_use]
    pub fn sql_execute_only_refusal(&self) -> Option<NotSubscribable> {
        self.sql_execute_only_operator().map(NotSubscribable::new)
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
/// use terrabase::{Engine, EngineConfig};
/// let engine = Engine::open(EngineConfig::durable("/tmp/terrabase-docstore").unwrap()).unwrap();
/// let store = engine.queries();
/// engine.shutdown().unwrap();
/// drop(store);
/// ```
///
/// ```compile_fail
/// // There is no independent durable or in-memory query constructor. This
/// // must not compile.
/// let store = terrabase::query::QueryStore::open("/tmp/other");
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

    /// Compiles typed or SQL text input into an immutable plan.
    ///
    /// The typed form (the SQL AST surface) is re-validated as it is
    /// normalized. SQL text is parsed by the read-only front end and lowered
    /// onto the same typed clauses, so both forms share one plan shape.
    /// Anything the front end accepts but the seam cannot express exactly
    /// fails closed with [`QueryError::InvalidSpec`], never a degraded plan.
    /// Authorization compiles into the plan and index derivation reads the
    /// plan's predicates and order keys — both on the SQL AST (D-78).
    ///
    /// # Errors
    ///
    /// Returns [`QueryError::InvalidSpec`] for an invalid typed specification
    /// or SQL text, and [`QueryError::TenantMismatch`] when a
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
            QueryInput::Sql(text) => self.compile_sql(text, None, None, sql::SqlParameters::new()),
            QueryInput::SqlBounded { text, deadline, cancellation, parameters } => {
                self.compile_sql(text, Some(deadline), Some(cancellation), parameters)
            }
        }
    }

    #[allow(clippy::needless_pass_by_value)]
    #[allow(clippy::unused_self, reason = "compile is one store-bound seam")]
    fn compile_sql(
        &self,
        text: crate::operations::SqlQueryText,
        deadline: Option<crate::types::Deadline>,
        cancellation: Option<crate::types::Cancellation>,
        parameters: sql::SqlParameters,
    ) -> Result<QueryPlan, QueryError> {
        let parsed = sql::parse(text.as_str(), crate::operations::MAX_SQL_TEXT_BYTES)
            .map_err(|_| QueryError::InvalidSpec)?;
        sql::validate_parameters(text.as_str(), &parameters)
            .map_err(|_| QueryError::InvalidSpec)?;
        let lowered = match sql::lower(&parsed, &parameters) {
            Ok(lowered) => lowered,
            Err(error) if error.class() == sql::SqlErrorClass::UnsupportedStatement => {
                let operator = match sql::classify_live_subset(&parsed) {
                    sql::LiveSubset::ExecuteOnly { operator } => Some(operator),
                    sql::LiveSubset::Live => None,
                };
                let source = QuerySource::try_new(
                    crate::types::CollectionName::try_new("__sql_execute__")
                        .map_err(|_| QueryError::InvalidSpec)?,
                )
                .map_err(|_| QueryError::InvalidSpec)?;
                let query = NormalizedQuery::from_sql(
                    source,
                    QueryPredicate::all(Vec::new()).map_err(|_| QueryError::InvalidSpec)?,
                    QueryOrdering::default(),
                    QueryProjection::whole_document(),
                    // The SQL text owns paging. This model copy is only for
                    // live dependency planning and must not invent a limit
                    // for joins, CTEs, EXPLAIN, or other execute-only forms.
                    QueryPaging::try_new(0, None).map_err(|_| QueryError::InvalidSpec)?,
                    deadline,
                    cancellation.clone(),
                );
                return Ok(QueryPlan::new_sql(
                    query,
                    sql::SqlRuntime {
                        text: text.as_str().to_owned(),
                        parameters,
                        tables: sql::table_names(&parsed),
                        deadline,
                        cancellation,
                        operator,
                    },
                ));
            }
            Err(_) => return Err(QueryError::InvalidSpec),
        };
        let collection = crate::types::CollectionName::try_new(&lowered.collection)
            .map_err(|_| QueryError::InvalidSpec)?;
        let source = QuerySource::try_new(collection).map_err(|_| QueryError::InvalidSpec)?;
        let predicate =
            QueryPredicate::all(lowered.conditions).map_err(|_| QueryError::InvalidSpec)?;
        let ordering =
            QueryOrdering::try_new(lowered.ordering).map_err(|_| QueryError::InvalidSpec)?;
        let projection = match lowered.projection {
            Some(fields) => {
                let refs: Vec<&str> = fields.iter().map(String::as_str).collect();
                QueryProjection::fields(&refs).map_err(|_| QueryError::InvalidSpec)?
            }
            None => QueryProjection::whole_document(),
        };
        let paging = QueryPaging::try_new(lowered.offset, lowered.limit)
            .map_err(|_| QueryError::InvalidSpec)?;
        let query = NormalizedQuery::from_sql(
            source,
            predicate,
            ordering,
            projection,
            paging,
            deadline,
            cancellation.clone(),
        );
        let operator = match parsed.kind() {
            sql::SqlStatementKind::Explain => Some("explain"),
            _ => None,
        };
        Ok(QueryPlan::new_sql(
            query,
            sql::SqlRuntime {
                text: text.as_str().to_owned(),
                parameters,
                tables: sql::table_names(&parsed),
                deadline,
                cancellation,
                operator,
            },
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
    /// bounded checkpoint, [`QueryError::CandidateBoundExceeded`] when the
    /// requested page cannot fit the bounded execution window, and
    /// [`QueryError::StorageFailure`] when the
    /// snapshot view fails.
    pub fn execute(
        &self,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryResult, QueryError> {
        if let Some(runtime) = &plan.sql_runtime {
            return sql_execute::execute(self.engine, plan, runtime, snapshot);
        }
        execute::execute(self.engine, &self.indexes, plan, snapshot)
    }

    /// Explains the plan the optimizer would use for the same query: the
    /// serving access, the logical indexes with their states, the ordering,
    /// and the shapes whose building descriptors would refuse execution
    /// with the retryable not-ready error. Explaining never executes and
    /// never names a storage internal.
    ///
    /// # Errors
    ///
    /// Returns the binding errors of [`QueryStore::execute`] without
    /// touching any document.
    pub fn explain(
        &self,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryExplanation, QueryError> {
        execute::explain(self.engine, plan, snapshot)
    }
}

/// Validates one public SQL text through the read-only front end.
///
/// The check the public
/// [`SqlQueryText`](crate::operations::SqlQueryText) constructor delegates
/// to: the text must be non-empty and shaped as read-only SQL (`SELECT`,
/// `WITH`, or `EXPLAIN` first keyword; no DDL, DML, pragma, or attachment).
///
/// # Errors
///
/// Returns [`QueryInputError::EmptyText`] for empty or whitespace-only text,
/// [`QueryInputError::InvalidIdentifier`] for a name the identifier
/// discipline rejects, and [`QueryInputError::UnsupportedGrammar`] for every
/// other front-end refusal.
pub(crate) fn validate_sql_text(text: &str) -> Result<(), QueryInputError> {
    use crate::operations::MAX_SQL_TEXT_BYTES;
    if text.trim().is_empty() {
        return Err(QueryInputError::EmptyText);
    }
    let Err(error) = sql::validate_text(text, MAX_SQL_TEXT_BYTES) else {
        return Ok(());
    };
    Err(match error.class() {
        SqlErrorClass::InvalidIdentifier => QueryInputError::InvalidIdentifier,
        _ => QueryInputError::UnsupportedGrammar,
    })
}
