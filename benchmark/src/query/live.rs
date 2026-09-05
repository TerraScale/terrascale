//! Row evaluation shared by snapshot execution and incremental consumers.

use std::cmp::Ordering;

use super::{
    NotSubscribable, QueryError, QueryPaging, QueryPlan, QueryRow, QuerySourceKind, execute, model,
};
use crate::types::{CollectionName, Document};

/// A compiled single-collection plan whose rows can be maintained from the
/// publication journal. Filtering, ordering and projection use the snapshot
/// executor's implementations, including exact numeric comparisons and SQL
/// output aliases. Paging applies to the ordered result, not individual rows.
#[derive(Clone, Debug)]
pub struct LiveQueryPlan {
    plan: QueryPlan,
    collection: CollectionName,
}

impl QueryPlan {
    /// Binds the compiled plan to the engine's incremental row evaluator.
    ///
    /// # Errors
    /// Returns a typed refusal for operators requiring other rows, referenced
    /// documents, or collection-wide relevance statistics.
    pub fn live_plan(&self) -> Result<LiveQueryPlan, NotSubscribable> {
        if let Some(refusal) = self.sql_execute_only_refusal() {
            return Err(refusal);
        }
        let query = self.model();
        let unsupported = if query.source().kind() != QuerySourceKind::Collection {
            Some("source")
        } else if !query.loads().is_empty() || !query.includes().is_empty() {
            Some("referenced_documents")
        } else if query.search_rank().is_some() {
            Some("relevance_statistics")
        } else {
            None
        };
        if let Some(operator) = unsupported {
            return Err(NotSubscribable::new(operator));
        }
        let collection =
            query.source().collection().ok_or_else(|| NotSubscribable::new("source"))?.clone();
        Ok(LiveQueryPlan { plan: self.clone(), collection })
    }
}

impl LiveQueryPlan {
    /// Returns the source collection bound by the compiled plan.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the offset and limit to apply after ordering eligible rows.
    #[must_use]
    pub fn paging(&self) -> QueryPaging {
        self.plan.model().paging()
    }

    /// Evaluates one published document, preserving its identity and version.
    /// A row from another collection is never eligible.
    ///
    /// # Errors
    /// Returns deadline or cancellation errors from the compiled plan.
    pub fn evaluate(&self, document: &Document) -> Result<Option<QueryRow>, QueryError> {
        let query = self.plan.model();
        execute::check_bounds(query.deadline(), query.cancellation_signal())?;
        if document.collection() != self.collection()
            || !model::matches(query.predicate(), document)
        {
            return Ok(None);
        }
        Ok(Some(QueryRow::new(document.clone(), execute::project_live(query, document))))
    }

    /// Orders eligible documents exactly as the snapshot executor, with the
    /// canonical document identity breaking equal sort keys.
    #[must_use]
    pub fn compare(&self, left: &Document, right: &Document) -> Ordering {
        model::compare_documents(self.plan.ordering(), left, right)
            .then_with(|| left.id().cmp(right.id()))
    }

    /// Returns the executor's estimated retained-document byte cost.
    #[must_use]
    pub fn estimated_document_bytes(document: &Document) -> usize {
        execute::document_bytes(document)
    }

    /// Builds a storage refill plan for the first `limit` eligible rows.
    /// Execute it against the same immutable snapshot as the journal cut being
    /// materialized. The original offset and result limit remain on this plan.
    ///
    /// # Errors
    /// Returns [`QueryError::InvalidSpec`] for a limit outside the query paging bounds.
    pub fn window_plan(&self, limit: u32) -> Result<QueryPlan, QueryError> {
        let paging = QueryPaging::try_new(0, Some(limit)).map_err(|_| QueryError::InvalidSpec)?;
        let mut plan = self.plan.clone();
        plan.query = plan.query.with_paging(paging);
        Ok(plan)
    }
}
