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
///
/// A dynamic plan — one whose staleness policy names no index — also runs
/// one optimizer pass against the engine-owned catalog on its valid, served
/// execution: the pass selects or creates the serving index, records the
/// selection, and fails closed on its guards, in which case the scanned
/// answer is discarded and the query refuses rather than silently scanning
/// without its index. The pass never changes the plan's rows, ordering, or
/// paging, which the bounded snapshot scan below produces either way. A plan
/// that names an index bypasses the optimizer entirely (AX-26).
pub(crate) fn execute(
    engine: &Engine,
    indexes: &Arc<IndexCatalog>,
    plan: &super::QueryPlan,
    snapshot: &ReadSnapshot,
) -> Result<QueryResult, QueryError> {
    if plan.model().staleness().indexes().is_empty() {
        // Execution validates the plan's bindings — tenant claim, namespace
        // tenants, the snapshot's Engine identity inside the snapshot view —
        // and the bounded scan only completes for a valid, served query, so
        // the optimizer pass below never sees an unvalidated snapshot.
        let result = execute_with_view(engine, indexes.as_ref(), plan, snapshot)?;
        super::optimizer::run(engine, indexes.as_ref(), plan, snapshot)?;
        Ok(result)
    } else {
        execute_with_view(engine, indexes.as_ref(), plan, snapshot)
    }
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
    // Reference resolution for the loads clause: one bounded id lookup per
    // load, served from the same materialized snapshot view. The map spans
    // every collection of the tenant, because a reference is an id, not a
    // collection-qualified pointer. Storage keys documents by tenant,
    // collection, and identifier, and nothing forbids reusing one id across a
    // tenant's collections, so the first document in canonical key order
    // wins: the same first-match rule the engine's identifier-only point read
    // applies (`find_by_document_id` through `read_document_at`), so a load
    // and a point read never disagree about which document one bare id names.
    let mut by_id: std::collections::BTreeMap<&str, &Document> = std::collections::BTreeMap::new();
    if !query.loads().is_empty() {
        for document in &documents {
            by_id.entry(document.id().as_str()).or_insert(document);
        }
    }
    // Bounded checkpoint at the scan/sort boundary: a bound that expired
    // during the scan stops the query before the sort starts.
    check_bounds(query.deadline(), query.cancellation_signal())?;
    let mut ordered: Vec<Document> = matched.into_iter().cloned().collect();
    ordered.sort_by(|left, right| model::compare_documents(query.ordering(), left, right));
    let matched_count = u64::try_from(ordered.len()).unwrap_or(u64::MAX);
    let rows = materialize(query, &ordered, &by_id)?;

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
/// binds null — the TQL reference contract (Section 4.2 of
/// `docs/tql/spec.md`) and V1's id-lookup `LOAD` agree on that shape. An id
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
        .filter_map(|name| {
            for (shape, referenced) in loads {
                if name == &shape.alias {
                    let value =
                        referenced.map_or(DocumentValue::Null, |document| document.value().clone());
                    return Some((name.clone(), value));
                }
                if let Some(field) = name.strip_prefix(shape.dotted.as_str()) {
                    return match referenced {
                        // A null binding navigates to null; the projected
                        // field is present with a null value.
                        None => Some((name.clone(), DocumentValue::Null)),
                        Some(document) => match document.value() {
                            DocumentValue::Object(referenced_fields) => referenced_fields
                                .get(field)
                                .map(|value| (name.clone(), value.clone())),
                            _ => None,
                        },
                    };
                }
            }
            fields.get(name).map(|value| (name.clone(), value.clone()))
        })
        .collect()
}
