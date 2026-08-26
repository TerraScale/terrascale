//! The typed query and index seam over immutable snapshots.
//!
//! This module implements the W3-2 query contract: one compile/execute path
//! over [`QueryInput`], execution against an immutable [`ReadSnapshot`], and
//! generation-aware index selection. The typed clause values and the
//! V1-compatible grammar normalize into one private model, the plan is
//! immutable after compilation, and execution consumes only the caller's
//! snapshot.
//!
//! W3-2 proves the seam; it does not claim the V1 query matrix. Execution is
//! an index-less full scan of the snapshot view, the private index catalog
//! keeps in-memory segments, and every query parity row stays unresolved
//! until its parity test and Proof ID exist. The design record with the
//! supported grammar, per-row coverage notes, limits, and non-claims is
//! `docs/query/w3-query-index.md`.

mod execute;
mod model;
pub mod optimizer;
mod result;
mod spec;
mod v1;

#[cfg(test)]
mod tests;

use std::sync::Arc;

pub use result::{
    IndexGenerationRef, ObservedGeneration, QueryResult, QueryRow, StaleIndexObservation,
    StalenessReport,
};
pub use spec::{
    IndexNamespace, MAX_CONDITIONS, MAX_FIELD_BYTES, MAX_ORDERING_KEYS, MAX_PAGE_LIMIT,
    MAX_PROJECTION_FIELDS, MAX_STALENESS_INDEXES, OrderDirection, OrderingKey, QueryCondition,
    QueryOrdering, QueryPaging, QueryPredicate, QueryProjection, QuerySource, QuerySpec,
    StalenessMode, StalenessPolicy,
};
#[allow(
    unused_imports,
    reason = "the public V1QueryText constructor delegates here once the integrator action lands"
)]
pub(crate) use v1::validate as validate_v1_text;

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
    /// fails rather than silently scanning without its index.
    IndexNotReady,
    /// The request deadline expired.
    Deadline,
    /// The request was cancelled.
    Cancelled,
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
                spec.tenant_claim().clone(),
                spec.deadline(),
                spec.cancellation(),
            )),
            QueryInput::V1Compatible(text) => {
                let (source, predicate, ordering, paging, projection) =
                    v1::parse_clauses(text.as_str()).map_err(input_error)?;
                self.compile_normalized(NormalizedQuery::unbound(
                    source,
                    predicate,
                    ordering,
                    projection,
                    paging,
                    v1::default_staleness(),
                ))
            }
        }
    }

    /// Compiles raw V1-compatible text through the same seam.
    ///
    /// Test-only: the public path wraps the text in
    /// [`V1QueryText`](crate::operations::V1QueryText) first, and that
    /// constructor's validation is the integrator action recorded in the
    /// design record. Both entries parse with the same function.
    #[cfg(test)]
    pub(crate) fn compile_v1_text(&self, text: &str) -> Result<QueryPlan, QueryError> {
        let (source, predicate, ordering, paging, projection) =
            v1::parse_clauses(text).map_err(input_error)?;
        self.compile_normalized(NormalizedQuery::unbound(
            source,
            predicate,
            ordering,
            projection,
            paging,
            v1::default_staleness(),
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
