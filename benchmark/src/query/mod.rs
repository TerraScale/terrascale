//! Declaration-only typed query seam over immutable snapshots.

#![allow(dead_code)]
#![allow(
    clippy::missing_errors_doc,
    clippy::must_use_candidate,
    clippy::needless_pass_by_value,
    clippy::unimplemented
)]

use std::marker::PhantomData;

use crate::{
    engine::Engine,
    operations::{QueryInput, RequestValidationError},
    types::{Cancellation, Deadline, ReadSnapshot, TenantId},
};

macro_rules! query_value {
    ($name:ident, $description:literal) => {
        #[doc = $description]
        #[derive(Clone, Debug, Eq, PartialEq)]
        pub struct $name {
            marker: PhantomData<()>,
        }
    };
}

query_value!(QuerySource, "A typed query source.");
query_value!(QueryPredicate, "A typed query predicate.");
query_value!(QueryOrdering, "A typed query ordering.");
query_value!(QueryProjection, "A typed query projection.");
query_value!(QueryPaging, "A typed query page request.");
query_value!(StalenessPolicy, "A typed index staleness policy.");

/// A validated typed query specification.
#[derive(Clone, Debug, PartialEq)]
pub struct QuerySpec {
    tenant_claim: TenantId,
    source: QuerySource,
    predicate: QueryPredicate,
    ordering: QueryOrdering,
    projection: QueryProjection,
    paging: QueryPaging,
    staleness: StalenessPolicy,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl QuerySpec {
    /// Creates a typed query specification after request validation.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        tenant_claim: TenantId,
        source: QuerySource,
        predicate: QueryPredicate,
        ordering: QueryOrdering,
        projection: QueryProjection,
        paging: QueryPaging,
        staleness: StalenessPolicy,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        let _ = (
            tenant_claim,
            source,
            predicate,
            ordering,
            projection,
            paging,
            staleness,
            deadline,
            cancellation,
        );
        unimplemented!("query specification validation belongs to a later behavior packet")
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the typed source.
    pub fn source(&self) -> &QuerySource {
        &self.source
    }

    /// Returns the typed predicate.
    pub fn predicate(&self) -> &QueryPredicate {
        &self.predicate
    }

    /// Returns the typed ordering.
    pub fn ordering(&self) -> &QueryOrdering {
        &self.ordering
    }

    /// Returns the typed projection.
    pub fn projection(&self) -> &QueryProjection {
        &self.projection
    }

    /// Returns the typed page request.
    pub fn paging(&self) -> &QueryPaging {
        &self.paging
    }

    /// Returns the typed index staleness policy.
    pub fn staleness(&self) -> &StalenessPolicy {
        &self.staleness
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// An immutable generation-aware query plan.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QueryPlan {
    marker: PhantomData<()>,
}

/// A typed query result.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QueryResult {
    marker: PhantomData<()>,
}

/// Query planning or execution failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QueryError {
    /// The Engine has closed.
    EngineClosed,
    /// The snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// The query tenant does not match the snapshot tenant.
    TenantMismatch,
    /// The typed query specification is invalid.
    InvalidSpec,
    /// The requested index generation is not ready.
    IndexNotReady,
    /// The request deadline expired.
    Deadline,
    /// The request was cancelled.
    Cancelled,
}

/// A query store borrowed from one Engine.
pub struct QueryStore<'engine> {
    engine: PhantomData<&'engine Engine>,
}

impl<'engine> QueryStore<'engine> {
    pub(crate) fn from_engine(_: &'engine Engine) -> Self {
        Self { engine: PhantomData }
    }

    /// Compiles typed or V1-compatible input into an immutable plan.
    pub fn compile(&self, input: QueryInput) -> Result<QueryPlan, QueryError> {
        let _ = (self, input);
        unimplemented!("query behavior belongs to W3-2")
    }

    /// Executes an immutable plan against an immutable snapshot.
    pub fn execute(
        &self,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryResult, QueryError> {
        let _ = (self, plan, snapshot);
        unimplemented!("query behavior belongs to W3-2")
    }
}
