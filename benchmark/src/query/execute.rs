//! The private query execution path.
//!
//! Execution validates tenant, snapshot, and namespace bindings before any
//! data access, evaluates the staleness policy against the index catalog,
//! then scans the immutable snapshot view, orders, projects, and pages the
//! matches with bounded deadline and cancellation checkpoints. It never
//! mutates a document, a snapshot, or an index, and it publishes nothing.

use std::sync::Arc;

use super::spec::StalenessPolicy;
use super::{
    GenerationView, NormalizedQuery, QueryError, QueryResult, QueryRow, StalenessMode, model,
    result::{IndexGenerationRef, ObservedGeneration, StaleIndexObservation, StalenessReport},
};
use crate::{
    engine::Engine,
    index::IndexCatalog,
    types::{Cancellation, Deadline, Document, DocumentValue, ReadSnapshot, TenantScope},
};

/// How many scanned documents pass between two bounded checkpoints.
const CHECKPOINT_INTERVAL: usize = 64;

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
fn map_engine_error(error: &crate::engine::EngineError) -> QueryError {
    match error {
        // A scope mismatch can only mean the snapshot claims another Engine.
        crate::engine::EngineError::SnapshotEngineMismatch
        | crate::engine::EngineError::ScopeMismatch => QueryError::SnapshotEngineMismatch,
        crate::engine::EngineError::SnapshotTenantMismatch => QueryError::SnapshotTenantMismatch,
        crate::engine::EngineError::Deadline => QueryError::Deadline,
        crate::engine::EngineError::Cancelled => QueryError::Cancelled,
        crate::engine::EngineError::Closed(_) => QueryError::EngineClosed,
        crate::engine::EngineError::StorageFailure
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
pub(crate) fn execute(
    engine: &Engine,
    indexes: &Arc<IndexCatalog>,
    plan: &super::QueryPlan,
    snapshot: &ReadSnapshot,
) -> Result<QueryResult, QueryError> {
    execute_with_view(engine, indexes.as_ref(), plan, snapshot)
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
    let query: &NormalizedQuery = plan.model();
    // Tenant, snapshot, and namespace validation before any data access.
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
    check_bounds(query.deadline(), query.cancellation_signal())?;
    // The snapshot supplies the tenant binding and its own Engine identity;
    // the Engine validates both against its real identity inside the view.
    let scope = TenantScope::from_parts(snapshot.tenant().clone(), snapshot.engine_identity());
    let documents =
        engine.snapshot_documents(&scope, snapshot).map_err(|error| map_engine_error(&error))?;

    // Index-less scan: the W3-2 tracer base path. The document list is
    // materialized, so no Engine or storage lock is held while scanning,
    // ordering, projecting, or paging.
    let mut matched: Vec<&Document> = Vec::new();
    for (position, document) in documents.iter().enumerate() {
        if position % CHECKPOINT_INTERVAL == 0 {
            check_bounds(query.deadline(), query.cancellation_signal())?;
        }
        if document.collection() != query.source().collection() {
            continue;
        }
        if model::matches(query.predicate(), document) {
            matched.push(document);
        }
    }
    // Bounded checkpoint at the scan/sort boundary: a bound that expired
    // during the scan stops the query before the sort starts.
    check_bounds(query.deadline(), query.cancellation_signal())?;
    let mut ordered: Vec<Document> = matched.into_iter().cloned().collect();
    ordered.sort_by(|left, right| model::compare_documents(query.ordering(), left, right));
    let matched_count = u64::try_from(ordered.len()).unwrap_or(u64::MAX);
    let rows = materialize(query, &ordered)?;

    let index_generation = staleness
        .observations()
        .first()
        .filter(|observation| observation.used().is_some())
        .and_then(|observation| {
            observation
                .used()
                .map(|used| IndexGenerationRef::new(observation.namespace().clone(), used))
        });
    Ok(QueryResult::new(rows, matched_count, query.ordering().clone(), staleness, index_generation))
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
) -> Result<Vec<QueryRow>, QueryError> {
    let offset = usize::try_from(query.paging().offset()).unwrap_or(usize::MAX);
    let limit = query
        .paging()
        .limit()
        .map_or(usize::MAX, |limit| usize::try_from(limit).unwrap_or(usize::MAX));
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
        rows.push(QueryRow::new(document.clone(), project(query, document)));
    }
    Ok(rows)
}

/// Projects one matched document into its row fields.
fn project(query: &NormalizedQuery, document: &Document) -> Vec<(String, DocumentValue)> {
    let DocumentValue::Object(fields) = document.value() else {
        return Vec::new();
    };
    match query.projection().selected() {
        None => fields.iter().map(|(name, value)| (name.clone(), value.clone())).collect(),
        Some(selected) => selected
            .iter()
            .filter_map(|name| fields.get(name).map(|value| (name.clone(), value.clone())))
            .collect(),
    }
}
