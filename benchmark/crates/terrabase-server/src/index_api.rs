//! Typed service boundary for the D-31 automatic-index coordinator.
//!
//! This module deliberately does not know about SQLite, descriptor tables, or
//! the historical query-optimizer registry.  A production adapter must expose
//! the durable D-31 coordinator through [`IndexCoordinatorAdapter`]; no default
//! or in-memory implementation is provided.  Consequently every operation
//! fails closed until the adapter proves that the requested tenant/database is
//! backed by durable lifecycle state.

use std::fmt;

use serde::{Deserialize, Serialize};

use crate::api_keys::{Principal, Verbs};

/// Maximum number of records returned by one index listing.
pub const MAX_INDEX_LIST: usize = 256;
/// Maximum number of recommendations returned by one request.
pub const MAX_RECOMMENDATIONS: usize = 64;
/// Maximum number of audit entries returned by one request.
pub const MAX_AUDIT_ENTRIES: usize = 256;
/// Maximum number of evidence samples retained in one response.
pub const MAX_EVIDENCE_SAMPLES: usize = 128;
/// Maximum bytes in one identifier, candidate shape, or evidence message.
pub const MAX_INDEX_TEXT: usize = 256;
/// Maximum extracted fields carried by one candidate DTO.
pub const MAX_INDEX_FIELDS: usize = 64;

/// The only coordinator kind accepted by this surface.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CoordinatorKind {
    /// Durable coordinator defined by D-31 / ADR 0045.
    D31,
    /// Any old registry or descriptor path. It is rejected explicitly.
    Legacy,
}

/// Whether lifecycle state is backed by a replayable durable journal.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Durability {
    /// Journal, catalog, and physical index mutations are durable.
    Durable,
    /// The adapter is a process-local or otherwise non-authoritative view.
    Ephemeral,
}

/// A tenant/database/collection authorization scope.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct IndexScope {
    /// Tenant (database owner) identity.
    pub tenant: String,
    /// Database identity.
    pub database: String,
    /// Optional collection narrowing. `None` means all collections in the DB.
    pub collection: Option<String>,
}

impl IndexScope {
    /// Creates a validated scope. Empty, oversized, or control-containing
    /// identifiers are rejected before an adapter is touched.
    pub fn try_new(
        tenant: impl Into<String>,
        database: impl Into<String>,
        collection: Option<String>,
    ) -> Result<Self, IndexServiceError> {
        let scope = Self {
            tenant: tenant.into(),
            database: database.into(),
            collection: collection.map(Into::into),
        };
        for (kind, value) in [
            (ScopePart::Tenant, scope.tenant.as_str()),
            (ScopePart::Database, scope.database.as_str()),
        ] {
            validate_text(kind, value)?;
        }
        if let Some(collection) = scope.collection.as_deref() {
            validate_text(ScopePart::Collection, collection)?;
        }
        Ok(scope)
    }

    fn validate(&self) -> Result<(), IndexServiceError> {
        validate_text(ScopePart::Tenant, &self.tenant)?;
        validate_text(ScopePart::Database, &self.database)?;
        if let Some(collection) = self.collection.as_deref() {
            validate_text(ScopePart::Collection, collection)?;
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ScopePart {
    Tenant,
    Database,
    Collection,
    Index,
    Shape,
}

impl ScopePart {
    const fn label(self) -> &'static str {
        match self {
            Self::Tenant => "tenant",
            Self::Database => "database",
            Self::Collection => "collection",
            Self::Index => "index",
            Self::Shape => "candidate shape",
        }
    }
}

fn validate_text(part: ScopePart, value: &str) -> Result<(), IndexServiceError> {
    if value.is_empty()
        || value.len() > MAX_INDEX_TEXT
        || !value.is_ascii()
        || value.chars().any(char::is_control)
    {
        return Err(match part {
            ScopePart::Tenant | ScopePart::Database | ScopePart::Collection => {
                IndexServiceError::InvalidScope { part: part.label() }
            }
            ScopePart::Index | ScopePart::Shape => {
                IndexServiceError::InvalidIdentifier { part: part.label() }
            }
        });
    }
    Ok(())
}

/// Operation class used for scope admission.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexOperation {
    /// Read-only listing, recommendation, audit, or evidence.
    Read,
    /// Lifecycle mutation.
    Admin,
}

impl IndexOperation {
    const fn verb(self) -> Verbs {
        match self {
            Self::Read => Verbs::READ,
            Self::Admin => Verbs::ADMIN,
        }
    }
}

/// Lifecycle value returned by the D-31 adapter.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum IndexPhase {
    Observing,
    Selected,
    Building,
    Shadowing,
    Active,
    Invisible,
    Abandoned,
}

/// Typed index state supplied by the durable coordinator.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct CoordinatorIndex {
    /// Stable physical index identity.
    pub id: String,
    /// Collection containing the index.
    pub collection: String,
    /// Ordered extracted fields.
    pub fields: Vec<String>,
    /// Current lifecycle phase.
    pub phase: IndexPhase,
    /// Current lifecycle generation. Pins and writes are generation-bound.
    pub generation: u64,
    /// Last journal sequence atomically covered by the index.
    pub covered_watermark: u64,
    /// Build target sequence, if this is or was a build.
    pub target_watermark: Option<u64>,
    /// Whether SQLite may choose this index.
    pub visible: bool,
    /// Whether customer control pinned the index.
    pub pinned: bool,
    /// Whether this candidate shape is customer-forbidden.
    pub forbidden: bool,
    /// Estimated write-maintenance cost.
    pub maintenance_cost: u64,
    /// Last planner use, if observed durably.
    pub last_used_millis: Option<u64>,
    /// Adapter proof that writes and index maintenance share a transaction.
    pub zero_stale_reads: bool,
    /// Adapter proof that no verified regression is currently exposed.
    pub regression_free: bool,
}

/// A recommendation produced by the D-31 coordinator.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct CoordinatorRecommendation {
    /// Candidate identity (stable across retries).
    pub candidate_id: String,
    /// Collection for the candidate.
    pub collection: String,
    /// Ordered fields proposed for the index.
    pub fields: Vec<String>,
    /// Bandit score used for selection.
    pub score: f64,
    /// Estimated maintenance cost per write.
    pub maintenance_cost: u64,
    /// Current generation at which this recommendation was read.
    pub generation: u64,
    /// Whether a descriptor must be created by the D-31 transaction.
    pub needs_descriptor: bool,
}

/// A bounded logical-cost sample in evidence.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct EvidenceSample {
    /// Logical pages read.
    pub pages_read: u64,
    /// Logical rows examined.
    pub rows_examined: u64,
}

/// One durable audit event.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct AuditEntry {
    /// Journal sequence of the event.
    pub sequence: u64,
    /// Index identity, if the event concerns one index.
    pub index_id: Option<String>,
    /// Action or lifecycle transition name.
    pub action: String,
    /// Generation recorded by the coordinator.
    pub generation: u64,
    /// Evidence digest, if the transition carried evidence.
    pub evidence_digest: Option<[u8; 32]>,
    /// Durable event time.
    pub at_millis: u64,
}

/// Before/after logical-cost evidence for a candidate.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct CoordinatorEvidence {
    /// Candidate/index identity.
    pub index_id: String,
    /// Generation to which this evidence applies.
    pub generation: u64,
    /// Baseline samples, capped by the adapter.
    pub baseline: Vec<EvidenceSample>,
    /// Candidate samples, capped by the adapter.
    pub candidate: Vec<EvidenceSample>,
    /// Fractional logical-work improvement.
    pub improvement: f64,
    /// Whether any hard regression guard was crossed.
    pub regression: bool,
    /// Digest committed in the lifecycle journal.
    pub digest: [u8; 32],
}

/// Read response for the index list operation.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct IndexList {
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether more records exist beyond this bounded response.
    pub truncated: bool,
    /// Index records in the requested collection.
    pub indexes: Vec<CoordinatorIndex>,
}

/// Read response for recommendations.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct RecommendationList {
    /// Current lifecycle generation. Pass this to [`ApplyRequest`].
    pub generation: u64,
    /// Whether more recommendations exist beyond this bounded response.
    pub truncated: bool,
    /// Ordered recommendations.
    pub recommendations: Vec<CoordinatorRecommendation>,
}

/// Read response for an audit query.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct AuditLog {
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether more events exist beyond this bounded response.
    pub truncated: bool,
    /// Durable lifecycle events, oldest or newest according to the adapter.
    pub entries: Vec<AuditEntry>,
}

/// Candidate application command.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct ApplyRequest {
    /// Stable recommendation/candidate identity.
    pub candidate_id: String,
    /// Generation returned by list/recommend; stale generations are refused.
    pub expected_generation: u64,
    /// Current time used by the coordinator's audit event.
    pub at_millis: u64,
}

/// Index pin command.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct PinRequest {
    /// Stable index identity.
    pub index_id: String,
    /// Exact normalized template to pin.
    pub template: String,
    /// Generation returned by list/recommend.
    pub expected_generation: u64,
}

/// Candidate-shape forbid command.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct ForbidRequest {
    /// Stable candidate identity or canonical shape.
    pub candidate_id: String,
    /// Exact normalized shape, never a broad collection switch.
    pub shape: String,
    /// Generation returned by list/recommend.
    pub expected_generation: u64,
}

/// Index retirement command.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct RetireRequest {
    /// Stable index identity.
    pub index_id: String,
    /// Generation returned by list/recommend.
    pub expected_generation: u64,
    /// Current time; the coordinator enforces the invisible grace period.
    pub at_millis: u64,
}

/// Command forwarded to the durable D-31 coordinator after service checks.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum CoordinatorCommand {
    /// Apply a recommendation.
    Apply(ApplyRequest),
    /// Pin one exact template/index pair.
    Pin(PinRequest),
    /// Forbid one exact candidate shape.
    Forbid(ForbidRequest),
    /// Retire one invisible index after grace.
    Retire(RetireRequest),
}

/// Errors an adapter may return. These are deliberately actionable and do
/// not include SQL text or document data.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IndexAdapterError {
    /// The adapter has no durable implementation for this scope.
    NotDurable,
    /// The adapter selected an old descriptor/registry path.
    LegacyCoordinator,
    /// The request's tenant binding is not admitted.
    UnauthorizedTenant,
    /// The database is absent.
    DatabaseNotFound,
    /// Durable generation changed while the command was being applied.
    GenerationConflict { current: u64 },
    /// The coordinator rejected the lifecycle transition.
    InvalidTransition,
    /// Write maintenance budget would be exceeded.
    WriteBudgetExceeded,
    /// A visible index could not prove atomic maintenance with writes.
    StaleReadRisk,
    /// A candidate would expose a verified regression.
    VerifiedRegression,
    /// Adapter storage/journal failure.
    StorageFailure,
}

/// Service-level errors returned before or after adapter execution.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IndexServiceError {
    /// A scope identifier is malformed.
    InvalidScope { part: &'static str },
    /// Principal lacks the operation's database/collection scope.
    Forbidden { operation: IndexOperation },
    /// Tenant/database binding failed in the durable authority.
    UnauthorizedTenant,
    /// The requested database is not present in the durable member catalog.
    DatabaseNotFound,
    /// No durable D-31 coordinator is installed.
    NotReady,
    /// An old descriptor path was offered and was rejected.
    LegacyCoordinator,
    /// The request's generation is no longer current.
    GenerationConflict { expected: u64, current: u64 },
    /// The caller requested more than the bounded DTO can carry.
    BoundsExceeded { requested: usize, maximum: usize },
    /// Candidate or index identity is invalid.
    InvalidIdentifier { part: &'static str },
    /// Candidate does not satisfy the zero-stale-read invariant.
    StaleReadRisk,
    /// Candidate has a verified regression and cannot be exposed.
    VerifiedRegression,
    /// Candidate would exceed the collection write budget.
    WriteBudgetExceeded,
    /// Coordinator state does not permit the command.
    InvalidTransition,
    /// Durable storage/journal failed.
    StorageFailure,
}

impl fmt::Display for IndexServiceError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidScope { part } => write!(formatter, "invalid {part} scope"),
            Self::Forbidden { operation } => {
                write!(formatter, "index {operation:?} is not admitted by the credential")
            }
            Self::UnauthorizedTenant => {
                formatter.write_str("credential is not admitted for this tenant")
            }
            Self::DatabaseNotFound => formatter.write_str("the requested database does not exist"),
            Self::NotReady => {
                formatter.write_str("durable D-31 index coordinator is not installed")
            }
            Self::LegacyCoordinator => {
                formatter.write_str("legacy index descriptor path is not accepted")
            }
            Self::GenerationConflict { expected, current } => write!(
                formatter,
                "index lifecycle generation is stale (expected {expected}, current {current})"
            ),
            Self::BoundsExceeded { requested, maximum } => {
                write!(formatter, "request bound exceeded ({requested}, maximum {maximum})")
            }
            Self::InvalidIdentifier { part } => write!(formatter, "invalid {part} identifier"),
            Self::StaleReadRisk => {
                formatter.write_str("index cannot prove zero-stale-read maintenance")
            }
            Self::VerifiedRegression => {
                formatter.write_str("verified query regression prevents activation")
            }
            Self::WriteBudgetExceeded => {
                formatter.write_str("index write-maintenance budget would be exceeded")
            }
            Self::InvalidTransition => {
                formatter.write_str("index lifecycle transition is not valid")
            }
            Self::StorageFailure => formatter.write_str("durable index journal or catalog failed"),
        }
    }
}

impl std::error::Error for IndexServiceError {}

impl IndexServiceError {
    /// Stable wire code for HTTP and CLI adapters.
    #[must_use]
    pub const fn code(&self) -> &'static str {
        match self {
            Self::InvalidScope { .. } => "invalid_scope",
            Self::Forbidden { .. } => "forbidden",
            Self::UnauthorizedTenant => "unauthorized_tenant",
            Self::DatabaseNotFound => "database_not_found",
            Self::NotReady => "index_coordinator_unavailable",
            Self::LegacyCoordinator => "legacy_coordinator_rejected",
            Self::GenerationConflict { .. } => "generation_conflict",
            Self::BoundsExceeded { .. } => "bounds_exceeded",
            Self::InvalidIdentifier { .. } => "invalid_identifier",
            Self::StaleReadRisk => "stale_read_risk",
            Self::VerifiedRegression => "verified_regression",
            Self::WriteBudgetExceeded => "write_budget_exceeded",
            Self::InvalidTransition => "invalid_transition",
            Self::StorageFailure => "storage_failure",
        }
    }

    /// One-line remedy suitable for the public error envelope's `fix` field.
    #[must_use]
    pub const fn fix(&self) -> &'static str {
        match self {
            Self::InvalidScope { .. } | Self::InvalidIdentifier { .. } => {
                "use bounded ASCII tenant, database, collection, and index identifiers"
            }
            Self::Forbidden { .. } | Self::UnauthorizedTenant => {
                "use a credential admitted for this tenant/database/collection and index administration"
            }
            Self::DatabaseNotFound => {
                "select an existing database or create it before managing indexes"
            }
            Self::NotReady => "install the durable D-31 coordinator before serving index state",
            Self::LegacyCoordinator => "route index state through the durable D-31 coordinator",
            Self::GenerationConflict { .. } => {
                "re-list recommendations and retry with the current lifecycle generation"
            }
            Self::BoundsExceeded { .. } => "request a smaller bounded page",
            Self::StaleReadRisk => {
                "keep the index invisible until same-transaction maintenance is available"
            }
            Self::VerifiedRegression => {
                "keep the candidate invisible or pin the regressed template to its baseline plan"
            }
            Self::WriteBudgetExceeded => {
                "retire an eligible index or wait for the collection write budget to allow this candidate"
            }
            Self::InvalidTransition => {
                "retry only after the D-31 lifecycle reaches the required phase"
            }
            Self::StorageFailure => "retry after the durable index journal/catalog is healthy",
        }
    }
}

impl From<IndexAdapterError> for IndexServiceError {
    fn from(error: IndexAdapterError) -> Self {
        match error {
            IndexAdapterError::NotDurable => Self::NotReady,
            IndexAdapterError::LegacyCoordinator => Self::LegacyCoordinator,
            IndexAdapterError::UnauthorizedTenant => Self::UnauthorizedTenant,
            IndexAdapterError::DatabaseNotFound => Self::DatabaseNotFound,
            IndexAdapterError::StorageFailure => Self::StorageFailure,
            IndexAdapterError::GenerationConflict { current } => {
                Self::GenerationConflict { expected: 0, current }
            }
            IndexAdapterError::InvalidTransition => Self::InvalidTransition,
            IndexAdapterError::WriteBudgetExceeded => Self::WriteBudgetExceeded,
            IndexAdapterError::StaleReadRisk => Self::StaleReadRisk,
            IndexAdapterError::VerifiedRegression => Self::VerifiedRegression,
        }
    }
}

/// Explicit adapter contract for the durable D-31 coordinator.
pub trait IndexCoordinatorAdapter {
    /// Returns the coordinator identity and durability for this scope.
    fn readiness(
        &self,
        scope: &IndexScope,
    ) -> Result<(CoordinatorKind, Durability), IndexAdapterError>;
    /// Performs tenant binding and any member-side authorization check.
    fn authorize(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        operation: IndexOperation,
    ) -> Result<(), IndexAdapterError>;
    /// Reads the current lifecycle generation.
    fn generation(&self, scope: &IndexScope) -> Result<u64, IndexAdapterError>;
    /// Lists bounded durable state. The adapter must not consult a legacy registry.
    fn list(
        &self,
        scope: &IndexScope,
        limit: usize,
    ) -> Result<Vec<CoordinatorIndex>, IndexAdapterError>;
    /// Computes bounded recommendations from D-31 observations.
    fn recommend(
        &self,
        scope: &IndexScope,
        limit: usize,
    ) -> Result<Vec<CoordinatorRecommendation>, IndexAdapterError>;
    /// Atomically applies a command in the durable coordinator.
    /// The adapter must reject a budget overflow, stale generation, stale-read
    /// risk, or verified regression before committing any catalog mutation.
    fn apply(
        &mut self,
        scope: &IndexScope,
        command: CoordinatorCommand,
    ) -> Result<CoordinatorIndex, IndexAdapterError>;
    /// Reads bounded lifecycle audit entries.
    fn audit(
        &self,
        scope: &IndexScope,
        index_id: Option<&str>,
        limit: usize,
    ) -> Result<Vec<AuditEntry>, IndexAdapterError>;
    /// Reads bounded before/after evidence for one index.
    fn evidence(
        &self,
        scope: &IndexScope,
        index_id: &str,
        limit: usize,
    ) -> Result<CoordinatorEvidence, IndexAdapterError>;
}

/// Typed D-31 service used by HTTP/CLI wiring.
pub struct IndexService<A> {
    adapter: A,
}

impl<A: IndexCoordinatorAdapter> IndexService<A> {
    /// Creates a service around an explicit adapter. Readiness is checked on
    /// every operation so a later adapter downgrade cannot become silently
    /// authoritative.
    pub const fn new(adapter: A) -> Self {
        Self { adapter }
    }

    /// Returns the underlying adapter for composition and diagnostics.
    pub fn adapter(&self) -> &A {
        &self.adapter
    }

    /// Returns the underlying mutable adapter for maintenance wiring.
    pub fn adapter_mut(&mut self) -> &mut A {
        &mut self.adapter
    }

    fn authorize(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        operation: IndexOperation,
    ) -> Result<(), IndexServiceError> {
        scope.validate()?;
        let admitted = match scope.collection.as_deref() {
            Some(collection) => {
                principal.scope().admits_collection(operation.verb(), &scope.database, collection)
            }
            None => principal.scope().admits(operation.verb(), &scope.database),
        };
        if !admitted {
            return Err(IndexServiceError::Forbidden { operation });
        }
        self.adapter.authorize(scope, principal, operation).map_err(map_adapter)
    }

    fn ready(&self, scope: &IndexScope) -> Result<(), IndexServiceError> {
        let (kind, durability) = self.adapter.readiness(scope).map_err(map_adapter)?;
        if kind != CoordinatorKind::D31 {
            return Err(IndexServiceError::LegacyCoordinator);
        }
        if durability != Durability::Durable {
            return Err(IndexServiceError::NotReady);
        }
        Ok(())
    }

    fn bounded(limit: usize, maximum: usize) -> Result<usize, IndexServiceError> {
        if limit == 0 {
            return Err(IndexServiceError::BoundsExceeded { requested: limit, maximum });
        }
        if limit > maximum {
            return Err(IndexServiceError::BoundsExceeded { requested: limit, maximum });
        }
        Ok(limit)
    }

    fn check_generation(&self, scope: &IndexScope, expected: u64) -> Result<(), IndexServiceError> {
        let current =
            self.adapter.generation(scope).map_err(|error| map_action_error(error, expected))?;
        if current != expected {
            return Err(IndexServiceError::GenerationConflict { expected, current });
        }
        Ok(())
    }

    fn check_index(
        index: &CoordinatorIndex,
        expected_generation: u64,
    ) -> Result<(), IndexServiceError> {
        if index.generation != expected_generation {
            return Err(IndexServiceError::GenerationConflict {
                expected: expected_generation,
                current: index.generation,
            });
        }
        if index.visible && !index.zero_stale_reads {
            return Err(IndexServiceError::StaleReadRisk);
        }
        if index.visible && !index.regression_free {
            return Err(IndexServiceError::VerifiedRegression);
        }
        Ok(())
    }

    fn check_index_shape(index: &CoordinatorIndex) -> Result<(), IndexServiceError> {
        validate_text(ScopePart::Index, &index.id)?;
        validate_text(ScopePart::Collection, &index.collection)?;
        if index.fields.len() > MAX_INDEX_FIELDS {
            return Err(IndexServiceError::BoundsExceeded {
                requested: index.fields.len(),
                maximum: MAX_INDEX_FIELDS,
            });
        }
        for field in &index.fields {
            validate_text(ScopePart::Shape, field)?;
        }
        Ok(())
    }

    fn check_recommendation_shape(
        recommendation: &CoordinatorRecommendation,
    ) -> Result<(), IndexServiceError> {
        validate_text(ScopePart::Index, &recommendation.candidate_id)?;
        validate_text(ScopePart::Collection, &recommendation.collection)?;
        if recommendation.fields.len() > MAX_INDEX_FIELDS {
            return Err(IndexServiceError::BoundsExceeded {
                requested: recommendation.fields.len(),
                maximum: MAX_INDEX_FIELDS,
            });
        }
        for field in &recommendation.fields {
            validate_text(ScopePart::Shape, field)?;
        }
        if !recommendation.score.is_finite() {
            return Err(IndexServiceError::InvalidTransition);
        }
        Ok(())
    }

    /// Lists the durable D-31 lifecycle state for a scope.
    pub fn list(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        limit: usize,
    ) -> Result<IndexList, IndexServiceError> {
        let limit = Self::bounded(limit, MAX_INDEX_LIST)?;
        self.authorize(scope, principal, IndexOperation::Read)?;
        self.ready(scope)?;
        let generation = self.adapter.generation(scope).map_err(map_adapter)?;
        let adapter_limit = limit.saturating_add(1);
        let mut indexes = self.adapter.list(scope, adapter_limit).map_err(map_adapter)?;
        if indexes.len() > adapter_limit {
            return Err(IndexServiceError::BoundsExceeded {
                requested: indexes.len(),
                maximum: adapter_limit,
            });
        }
        let truncated = indexes.len() > limit;
        indexes.truncate(limit);
        for index in &indexes {
            Self::check_index_shape(index)?;
            if scope.collection.as_deref().is_some_and(|collection| collection != index.collection)
            {
                return Err(IndexServiceError::InvalidTransition);
            }
            Self::check_index(index, generation)?;
        }
        Ok(IndexList { generation, truncated, indexes })
    }

    /// Lists bounded D-31 recommendations for a scope.
    pub fn recommend(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        limit: usize,
    ) -> Result<RecommendationList, IndexServiceError> {
        let limit = Self::bounded(limit, MAX_RECOMMENDATIONS)?;
        self.authorize(scope, principal, IndexOperation::Read)?;
        self.ready(scope)?;
        let generation = self.adapter.generation(scope).map_err(map_adapter)?;
        let adapter_limit = limit.saturating_add(1);
        let mut recommendations =
            self.adapter.recommend(scope, adapter_limit).map_err(map_adapter)?;
        if recommendations.len() > adapter_limit {
            return Err(IndexServiceError::BoundsExceeded {
                requested: recommendations.len(),
                maximum: adapter_limit,
            });
        }
        let truncated = recommendations.len() > limit;
        recommendations.truncate(limit);
        for recommendation in &recommendations {
            Self::check_recommendation_shape(recommendation)?;
            if scope
                .collection
                .as_deref()
                .is_some_and(|collection| collection != recommendation.collection)
            {
                return Err(IndexServiceError::InvalidTransition);
            }
            if recommendation.generation != generation {
                return Err(IndexServiceError::GenerationConflict {
                    expected: generation,
                    current: recommendation.generation,
                });
            }
        }
        Ok(RecommendationList { generation, truncated, recommendations })
    }

    /// Applies one recommendation directly into the D-31 build/verify state machine.
    pub fn apply(
        &mut self,
        scope: &IndexScope,
        principal: &Principal,
        request: ApplyRequest,
    ) -> Result<CoordinatorIndex, IndexServiceError> {
        validate_text(ScopePart::Index, &request.candidate_id)?;
        self.authorize(scope, principal, IndexOperation::Admin)?;
        self.ready(scope)?;
        self.check_generation(scope, request.expected_generation)?;
        let expected = request.expected_generation;
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Apply(request))
            .map_err(|error| map_action_error(error, expected))?;
        Self::check_index(&result, expected)?;
        Ok(result)
    }

    /// Pins one exact normalized template to one active index.
    pub fn pin(
        &mut self,
        scope: &IndexScope,
        principal: &Principal,
        request: PinRequest,
    ) -> Result<CoordinatorIndex, IndexServiceError> {
        validate_text(ScopePart::Index, &request.index_id)?;
        validate_text(ScopePart::Shape, &request.template)?;
        self.authorize(scope, principal, IndexOperation::Admin)?;
        self.ready(scope)?;
        self.check_generation(scope, request.expected_generation)?;
        let expected = request.expected_generation;
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Pin(request))
            .map_err(|error| map_action_error(error, expected))?;
        Self::check_index(&result, expected)?;
        Ok(result)
    }

    /// Forbids one exact candidate shape; it never disables a whole collection.
    pub fn forbid(
        &mut self,
        scope: &IndexScope,
        principal: &Principal,
        request: ForbidRequest,
    ) -> Result<CoordinatorIndex, IndexServiceError> {
        validate_text(ScopePart::Index, &request.candidate_id)?;
        validate_text(ScopePart::Shape, &request.shape)?;
        self.authorize(scope, principal, IndexOperation::Admin)?;
        self.ready(scope)?;
        self.check_generation(scope, request.expected_generation)?;
        let expected = request.expected_generation;
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Forbid(request))
            .map_err(|error| map_action_error(error, expected))?;
        Self::check_index(&result, expected)?;
        Ok(result)
    }

    /// Retires an index through the durable invisible-then-drop lifecycle.
    pub fn retire(
        &mut self,
        scope: &IndexScope,
        principal: &Principal,
        request: RetireRequest,
    ) -> Result<CoordinatorIndex, IndexServiceError> {
        validate_text(ScopePart::Index, &request.index_id)?;
        self.authorize(scope, principal, IndexOperation::Admin)?;
        self.ready(scope)?;
        self.check_generation(scope, request.expected_generation)?;
        let expected = request.expected_generation;
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Retire(request))
            .map_err(|error| map_action_error(error, expected))?;
        Self::check_index(&result, expected)?;
        Ok(result)
    }

    /// Reads bounded durable audit history for the scope.
    pub fn audit(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        index_id: Option<&str>,
        limit: usize,
    ) -> Result<AuditLog, IndexServiceError> {
        let limit = Self::bounded(limit, MAX_AUDIT_ENTRIES)?;
        if let Some(index_id) = index_id {
            validate_text(ScopePart::Index, index_id)?;
        }
        self.authorize(scope, principal, IndexOperation::Read)?;
        self.ready(scope)?;
        let generation = self.adapter.generation(scope).map_err(map_adapter)?;
        let mut entries =
            self.adapter.audit(scope, index_id, limit.saturating_add(1)).map_err(map_adapter)?;
        if entries.len() > limit.saturating_add(1) {
            return Err(IndexServiceError::BoundsExceeded {
                requested: entries.len(),
                maximum: limit.saturating_add(1),
            });
        }
        for entry in &entries {
            if let Some(index_id) = entry.index_id.as_deref() {
                validate_text(ScopePart::Index, index_id)?;
            }
            validate_text(ScopePart::Shape, &entry.action)?;
        }
        let truncated = entries.len() > limit;
        entries.truncate(limit);
        Ok(AuditLog { generation, truncated, entries })
    }

    /// Reads bounded evidence and rejects evidence that is not current and safe.
    pub fn evidence(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        index_id: &str,
        limit: usize,
    ) -> Result<CoordinatorEvidence, IndexServiceError> {
        let limit = Self::bounded(limit, MAX_EVIDENCE_SAMPLES)?;
        validate_text(ScopePart::Index, index_id)?;
        self.authorize(scope, principal, IndexOperation::Read)?;
        self.ready(scope)?;
        let generation = self.adapter.generation(scope).map_err(map_adapter)?;
        let evidence = self.adapter.evidence(scope, index_id, limit).map_err(map_adapter)?;
        validate_text(ScopePart::Index, &evidence.index_id)?;
        if evidence.baseline.len() > limit || evidence.candidate.len() > limit {
            return Err(IndexServiceError::BoundsExceeded {
                requested: evidence.baseline.len().max(evidence.candidate.len()),
                maximum: limit,
            });
        }
        if evidence.generation != generation {
            return Err(IndexServiceError::GenerationConflict {
                expected: generation,
                current: evidence.generation,
            });
        }
        if !evidence.improvement.is_finite() || evidence.regression {
            return Err(IndexServiceError::VerifiedRegression);
        }
        Ok(evidence)
    }
}

fn map_adapter(error: IndexAdapterError) -> IndexServiceError {
    match error {
        IndexAdapterError::GenerationConflict { current } => {
            IndexServiceError::GenerationConflict { expected: 0, current }
        }
        other => other.into(),
    }
}

fn map_action_error(error: IndexAdapterError, expected: u64) -> IndexServiceError {
    match error {
        IndexAdapterError::GenerationConflict { current } => {
            IndexServiceError::GenerationConflict { expected, current }
        }
        other => other.into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::api_keys::Scope;

    #[derive(Default)]
    struct Adapter {
        generation: u64,
        kind: CoordinatorKind,
        durability: Durability,
        indexes: Vec<CoordinatorIndex>,
        commands: Vec<CoordinatorCommand>,
    }

    impl Default for CoordinatorKind {
        fn default() -> Self {
            Self::D31
        }
    }
    impl Default for Durability {
        fn default() -> Self {
            Self::Durable
        }
    }

    impl IndexCoordinatorAdapter for Adapter {
        fn readiness(
            &self,
            _scope: &IndexScope,
        ) -> Result<(CoordinatorKind, Durability), IndexAdapterError> {
            Ok((self.kind, self.durability))
        }
        fn authorize(
            &self,
            _scope: &IndexScope,
            _principal: &Principal,
            _operation: IndexOperation,
        ) -> Result<(), IndexAdapterError> {
            Ok(())
        }
        fn generation(&self, _scope: &IndexScope) -> Result<u64, IndexAdapterError> {
            Ok(self.generation)
        }
        fn list(
            &self,
            _scope: &IndexScope,
            limit: usize,
        ) -> Result<Vec<CoordinatorIndex>, IndexAdapterError> {
            Ok(self.indexes.iter().take(limit).cloned().collect())
        }
        fn recommend(
            &self,
            _scope: &IndexScope,
            _limit: usize,
        ) -> Result<Vec<CoordinatorRecommendation>, IndexAdapterError> {
            Ok(Vec::new())
        }
        fn apply(
            &mut self,
            _scope: &IndexScope,
            command: CoordinatorCommand,
        ) -> Result<CoordinatorIndex, IndexAdapterError> {
            let expected = match &command {
                CoordinatorCommand::Apply(request) => request.expected_generation,
                CoordinatorCommand::Pin(request) => request.expected_generation,
                CoordinatorCommand::Forbid(request) => request.expected_generation,
                CoordinatorCommand::Retire(request) => request.expected_generation,
            };
            if expected != self.generation {
                return Err(IndexAdapterError::GenerationConflict { current: self.generation });
            }
            self.commands.push(command);
            self.indexes.first().cloned().ok_or(IndexAdapterError::InvalidTransition)
        }
        fn audit(
            &self,
            _scope: &IndexScope,
            _index_id: Option<&str>,
            _limit: usize,
        ) -> Result<Vec<AuditEntry>, IndexAdapterError> {
            Ok(Vec::new())
        }
        fn evidence(
            &self,
            _scope: &IndexScope,
            _index_id: &str,
            _limit: usize,
        ) -> Result<CoordinatorEvidence, IndexAdapterError> {
            Err(IndexAdapterError::InvalidTransition)
        }
    }

    fn principal() -> Principal {
        let store = crate::api_keys::KeyStore::with_keys(&[(
            "tskey_index_test_abcdefghijkl",
            "test",
            Scope::database("db", Verbs::ALL),
        )]);
        store.admit("tskey_index_test_abcdefghijkl").expect("test principal")
    }

    fn scope() -> IndexScope {
        IndexScope::try_new("tenant", "db", Some("users".into())).expect("scope")
    }

    fn active() -> CoordinatorIndex {
        CoordinatorIndex {
            id: "idx-users".into(),
            collection: "users".into(),
            fields: vec!["email".into()],
            phase: IndexPhase::Active,
            generation: 7,
            covered_watermark: 9,
            target_watermark: Some(9),
            visible: true,
            pinned: false,
            forbidden: false,
            maintenance_cost: 1,
            last_used_millis: Some(10),
            zero_stale_reads: true,
            regression_free: true,
        }
    }

    #[test]
    fn list_checks_database_and_collection_scope() {
        let adapter = Adapter { generation: 7, indexes: vec![active()], ..Adapter::default() };
        let service = IndexService::new(adapter);
        let denied_store = crate::api_keys::KeyStore::with_keys(&[(
            "tskey_index_denied_abcdefghijkl",
            "x",
            Scope::database("other", Verbs::READ),
        )]);
        let denied = denied_store.admit("tskey_index_denied_abcdefghijkl").expect("denied key");
        assert!(matches!(
            service.list(&scope(), &denied, 10),
            Err(IndexServiceError::Forbidden { .. })
        ));
    }

    #[test]
    fn list_fails_closed_for_ephemeral_or_legacy_adapters() {
        let mut adapter = Adapter { generation: 7, indexes: vec![active()], ..Adapter::default() };
        adapter.durability = Durability::Ephemeral;
        let service = IndexService::new(adapter);
        assert_eq!(service.list(&scope(), &principal(), 10), Err(IndexServiceError::NotReady));
        let mut adapter = Adapter { generation: 7, indexes: vec![active()], ..Adapter::default() };
        adapter.kind = CoordinatorKind::Legacy;
        let service = IndexService::new(adapter);
        assert_eq!(
            service.list(&scope(), &principal(), 10),
            Err(IndexServiceError::LegacyCoordinator)
        );
    }

    #[test]
    fn list_bounds_and_generation_are_typed() {
        let adapter = Adapter { generation: 7, indexes: vec![active()], ..Adapter::default() };
        let service = IndexService::new(adapter);
        assert!(matches!(
            service.list(&scope(), &principal(), MAX_INDEX_LIST + 1),
            Err(IndexServiceError::BoundsExceeded { .. })
        ));
        let listed = service.list(&scope(), &principal(), 1).expect("list");
        assert_eq!(listed.generation, 7);
        assert!(!listed.truncated);
    }

    #[test]
    fn mutations_forward_only_current_generation() {
        let adapter = Adapter { generation: 7, indexes: vec![active()], ..Adapter::default() };
        let mut service = IndexService::new(adapter);
        let request = PinRequest {
            index_id: "idx-users".into(),
            template: "users where email = ?".into(),
            expected_generation: 6,
        };
        assert_eq!(
            service.pin(&scope(), &principal(), request),
            Err(IndexServiceError::GenerationConflict { expected: 6, current: 7 })
        );
        let request = PinRequest {
            index_id: "idx-users".into(),
            template: "users where email = ?".into(),
            expected_generation: 7,
        };
        assert!(service.pin(&scope(), &principal(), request).is_ok());
    }

    #[test]
    fn visible_state_rejects_stale_or_regressed_indexes() {
        for (stale, regression, error) in [
            (false, true, IndexServiceError::StaleReadRisk),
            (true, false, IndexServiceError::VerifiedRegression),
        ] {
            let mut index = active();
            index.zero_stale_reads = stale;
            index.regression_free = regression;
            let adapter = Adapter { generation: 7, indexes: vec![index], ..Adapter::default() };
            let service = IndexService::new(adapter);
            assert_eq!(service.list(&scope(), &principal(), 1), Err(error));
        }
    }

    #[test]
    fn audit_and_evidence_are_bounded() {
        let adapter = Adapter { generation: 7, indexes: vec![active()], ..Adapter::default() };
        let service = IndexService::new(adapter);
        assert!(matches!(
            service.audit(&scope(), &principal(), None, MAX_AUDIT_ENTRIES + 1),
            Err(IndexServiceError::BoundsExceeded { .. })
        ));
    }

    #[test]
    fn errors_have_stable_codes_and_remedies() {
        let error = IndexServiceError::GenerationConflict { expected: 4, current: 5 };
        assert_eq!(error.code(), "generation_conflict");
        assert!(error.fix().contains("re-list"));
    }
}
