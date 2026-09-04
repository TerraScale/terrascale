//! Query-side observations for the durable automatic-index coordinator.
//!
//! This module is deliberately an adapter, not an index authority.  Query
//! execution can derive a bounded, literal-free template and measured row
//! work, while lifecycle state and physical visibility remain owned by the
//! coordinator and its storage adapter.  In particular, the fail-closed
//! adapter below never claims that an index is durable or current.

use std::time::{SystemTime, UNIX_EPOCH};

use crate::auto_index::{
    Candidate, Coordinator, CoordinatorError, HookError, LogicalCost, Observation, Predicate,
    PredicateKind, StorageAdapter, Template,
};

use super::{NormalizedQuery, QueryCondition, QueryResult, QueryStatistics};

/// Maximum bytes retained by one normalized workload template.
pub(crate) const MAX_TEMPLATE_BYTES: usize = 16 * 1024;

/// Builds the literal-free template for a document collection query.
///
/// Family sources and predicates which the coordinator cannot classify are
/// excluded.  Omitting an unsupported shape is safer than collapsing it into
/// an equality shape and proposing an index for a semantically different
/// query.  All names have already passed the query validator; the explicit
/// bound remains here so this adapter stays safe if its caller changes.
pub(crate) fn normalized_template(query: &NormalizedQuery) -> Option<Template> {
    let collection = query.source().collection()?;
    let mut predicates = Vec::with_capacity(query.predicate().conditions().len());
    for condition in query.predicate().conditions() {
        let (field, kind) = match condition {
            QueryCondition::Equals { field, .. }
            | QueryCondition::In { field, .. }
            | QueryCondition::AllIn { field, .. }
            | QueryCondition::Exists { field } => (field.as_str(), PredicateKind::Equality),
            QueryCondition::GreaterThan { field, .. }
            | QueryCondition::GreaterOrEqual { field, .. }
            | QueryCondition::LessThan { field, .. }
            | QueryCondition::LessOrEqual { field, .. }
            | QueryCondition::Between { field, .. } => (field.as_str(), PredicateKind::Range),
            QueryCondition::Search { field, .. } => (field.as_str(), PredicateKind::FullText),
            QueryCondition::NotEquals { .. }
            | QueryCondition::Missing { .. }
            | QueryCondition::StartsWith { .. }
            | QueryCondition::EndsWith { .. }
            | QueryCondition::Contains { .. } => return None,
        };
        predicates.push(Predicate { field: field.to_owned(), kind });
    }

    let ordering =
        query.ordering().keys().iter().map(|key| key.field().to_owned()).collect::<Vec<_>>();
    let projection = query.projection().selected().map(|fields| {
        let mut fields = fields.to_vec();
        fields.sort();
        fields.dedup();
        fields
    });
    let template =
        Template { collection: collection.as_str().to_owned(), predicates, ordering, projection };
    bounded_template(&template).then_some(template)
}

fn bounded_template(template: &Template) -> bool {
    let mut bytes = template.collection.len();
    for predicate in &template.predicates {
        bytes = bytes.saturating_add(predicate.field.len()).saturating_add(1);
    }
    for field in &template.ordering {
        bytes = bytes.saturating_add(field.len()).saturating_add(1);
    }
    if let Some(fields) = &template.projection {
        for field in fields {
            bytes = bytes.saturating_add(field.len()).saturating_add(1);
        }
    }
    bytes <= MAX_TEMPLATE_BYTES
}

/// Converts query result accounting into measured logical work.
///
/// `scanned` is the executor's exact candidate count.  The query seam does
/// not expose a SQLite page counter, so pages are explicitly zero rather than
/// fabricated; a storage-backed statement-counter integration can supply the
/// page component through [`logical_cost`].
pub(crate) const fn measured_cost(statistics: &QueryStatistics) -> LogicalCost {
    LogicalCost::new(0, statistics.scanned())
}

/// Converts one successful execution into the coordinator's bounded
/// observation value.  The timestamp is injectable for deterministic tests.
pub(crate) fn observation_at(
    query: &NormalizedQuery,
    result: &QueryResult,
    at_millis: u64,
) -> Option<Observation> {
    let template = normalized_template(query)?;
    let index =
        result.index_generation().map(|generation| generation.namespace().name().to_owned());
    Some(Observation {
        template,
        cost: measured_cost(result.statistics()),
        rows_returned: u64::try_from(result.rows().len()).unwrap_or(u64::MAX),
        index,
        at_millis,
    })
}

/// Converts one successful execution using the production wall clock.
pub(crate) fn observation(query: &NormalizedQuery, result: &QueryResult) -> Option<Observation> {
    let at_millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| u64::try_from(duration.as_millis()).ok())
        .unwrap_or(u64::MAX);
    observation_at(query, result, at_millis)
}

/// Supplies the page component when the SQLite statement-counter seam is
/// available.  This keeps the two logical dimensions explicit and avoids
/// treating wall-clock duration as query work.
pub(crate) const fn logical_cost(pages_read: u64, rows_examined: u64) -> LogicalCost {
    LogicalCost::new(pages_read, rows_examined)
}

/// Delivers an observation through the coordinator's explicit logical-cost
/// hook.  A failed hook leaves coordinator state untouched and is returned to
/// the caller; no stale-read or lifecycle success is inferred here.
pub(crate) fn record_observation<A: StorageAdapter>(
    coordinator: &mut Coordinator<A>,
    observation: &Observation,
) -> Result<(), CoordinatorError> {
    coordinator
        .adapter_mut()
        .record_logical_cost(&observation.template, observation.cost)
        .map_err(CoordinatorError::Hook)
}

/// An intentionally unavailable production adapter until the storage lane
/// installs durable journal, publication, planner, and backfill hooks.
///
/// Every method fails closed.  It cannot be used to recover a coordinator,
/// publish an index, or make a stale index appear current by accident.
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct UnavailableStorageAdapter;

impl StorageAdapter for UnavailableStorageAdapter {
    fn load_lifecycle(&mut self) -> Result<Vec<crate::auto_index::LifecycleEvent>, HookError> {
        Err(HookError::Unavailable)
    }

    fn append_lifecycle(
        &mut self,
        _event: &crate::auto_index::LifecycleEvent,
    ) -> Result<(), HookError> {
        Err(HookError::Unavailable)
    }

    fn backfill_batch(&mut self, _index: &str, _first: u64, _last: u64) -> Result<(), HookError> {
        Err(HookError::Unavailable)
    }

    fn catch_up_and_verify(
        &mut self,
        _index: &str,
        _covered: u64,
        _target: u64,
    ) -> Result<u64, HookError> {
        Err(HookError::Unavailable)
    }

    fn set_visible(&mut self, _index: &str, _visible: bool) -> Result<(), HookError> {
        Err(HookError::Unavailable)
    }

    fn drop_physical(&mut self, _index: &str) -> Result<(), HookError> {
        Err(HookError::Unavailable)
    }

    fn set_plan_pin(
        &mut self,
        _template: &str,
        _index: &str,
        _pinned: bool,
    ) -> Result<(), HookError> {
        Err(HookError::Unavailable)
    }

    fn record_logical_cost(
        &mut self,
        _template: &Template,
        _cost: LogicalCost,
    ) -> Result<(), HookError> {
        Err(HookError::Unavailable)
    }

    fn compile_hypothetical(
        &mut self,
        _template: &Template,
        _candidate: &Candidate,
    ) -> Result<bool, HookError> {
        Err(HookError::Unavailable)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::query::spec::{QueryOrdering, QueryPredicate, QueryProjection, QuerySource};
    use crate::types::{CollectionName, DocumentValue};

    #[test]
    fn unsupported_predicates_are_not_collapsed_into_an_index_shape() {
        let source = QuerySource::try_new(CollectionName::try_new("users").unwrap()).unwrap();
        let query = NormalizedQuery::from_sql(
            source,
            QueryPredicate::all(vec![QueryCondition::NotEquals {
                field: "state".into(),
                value: DocumentValue::String("deleted".into()),
            }])
            .unwrap(),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            None,
            crate::query::spec::QueryPaging::try_new(0, None).unwrap(),
            None,
            None,
        );
        assert!(normalized_template(&query).is_none());
    }

    #[test]
    fn normalized_template_keeps_shape_but_discards_bound_values_and_aliases() {
        let source = QuerySource::try_new(CollectionName::try_new("users").unwrap()).unwrap();
        let query = NormalizedQuery::from_sql(
            source,
            QueryPredicate::all(vec![QueryCondition::Equals {
                field: "age".into(),
                value: DocumentValue::Integer(42),
            }])
            .unwrap(),
            QueryOrdering::default(),
            QueryProjection::fields(&["age"]).unwrap(),
            Some(vec!["years".into()]),
            crate::query::spec::QueryPaging::try_new(0, Some(10)).unwrap(),
            None,
            None,
        );
        let template = normalized_template(&query).expect("collection shape");
        assert_eq!(template.collection, "users");
        assert_eq!(template.predicates[0].field, "age");
        assert_eq!(template.projection, Some(vec!["age".into()]));
    }

    #[test]
    fn coordinator_delivery_fails_closed_without_storage_hooks() {
        let mut coordinator = Coordinator::recover_with_hooks(
            UnavailableStorageAdapter,
            crate::auto_index::HookAvailability::default(),
        );
        assert!(matches!(
            coordinator,
            Err(CoordinatorError::MissingHook(crate::auto_index::HookKind::Journal))
        ));
    }
}
