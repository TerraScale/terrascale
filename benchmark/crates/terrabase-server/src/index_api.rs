//! Typed service boundary for the D-31 automatic-index coordinator.
//!
//! [`EngineIndexAdapter`] is the production composition point.  It binds the
//! authenticated route scope to a real [`terrabase::Engine`] and uses the
//! Engine's SQLite-backed lifecycle generation and snapshot, and tenant-bound
//! query recommendations and scoped lifecycle audit. Verified lifecycle mutations
//! and durable shadow evidence still require their engine integration hooks.
//! Those operations report [`IndexAdapterError::CoreApiUnavailable`].

use std::fmt;

use serde::{Deserialize, Serialize};
use terrabase::Engine;
use terrabase::types::TenantId;

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
#[derive(Clone, Copy, Debug, Eq, PartialEq, Default)]
pub enum CoordinatorKind {
    /// Durable coordinator defined by D-31 / ADR 0045.
    #[default]
    D31,
    /// Any old registry or descriptor path. It is rejected explicitly.
    Legacy,
}

/// Whether lifecycle state is backed by a replayable durable journal.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Default)]
pub enum Durability {
    /// Journal, catalog, and physical index mutations are durable.
    #[default]
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
    ///
    /// # Errors
    ///
    /// Returns `InvalidScope` for an invalid tenant, database, or collection.
    pub fn try_new(
        tenant: impl Into<String>,
        database: impl Into<String>,
        collection: Option<String>,
    ) -> Result<Self, IndexServiceError> {
        let scope = Self { tenant: tenant.into(), database: database.into(), collection };
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
    /// The coordinator is collecting query observations.
    Observing,
    /// The coordinator selected this candidate for evaluation.
    Selected,
    /// `SQLite` is building the physical index.
    Building,
    /// The candidate is being compared against its baseline.
    Shadowing,
    /// The index is visible to the query planner.
    Active,
    /// The index remains durable but is hidden from planning.
    Invisible,
    /// The candidate was abandoned and cannot serve queries.
    Abandoned,
}

/// Typed index state supplied by the durable coordinator.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "wire record carries independent policy and safety facts; lifecycle is IndexPhase"
)]
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
    /// Whether `SQLite` may choose this index.
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
    GenerationConflict {
        /// Generation currently held by the durable coordinator.
        current: u64,
    },
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
    /// The Engine has no method that can prove the requested D-31 fact.
    ///
    /// `required_method` is an exact core API name, not a generic TODO.  The
    /// adapter must remain fail-closed until that method is implemented by the
    /// Engine and backed by the same durable publication transaction.
    CoreApiUnavailable {
        /// Adapter operation that could not be implemented.
        operation: &'static str,
        /// Exact Engine method required to implement the operation.
        required_method: &'static str,
    },
}

impl IndexAdapterError {
    /// Returns the exact core method needed to clear an unavailable seam.
    ///
    /// This deliberately exposes no SQL, document data, or filesystem
    /// details, while allowing startup diagnostics and router health probes to
    /// report an actionable integration gap.
    #[must_use]
    pub const fn required_core_method(&self) -> Option<&'static str> {
        match self {
            Self::CoreApiUnavailable { required_method, .. } => Some(*required_method),
            _ => None,
        }
    }
}

impl fmt::Display for IndexAdapterError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotDurable => formatter.write_str("index coordinator is not durable"),
            Self::LegacyCoordinator => formatter.write_str("legacy index coordinator rejected"),
            Self::UnauthorizedTenant => formatter.write_str("tenant is not owned by the Engine"),
            Self::DatabaseNotFound => formatter.write_str("database is not owned by the Engine"),
            Self::GenerationConflict { current } => {
                write!(formatter, "index lifecycle generation conflict (current {current})")
            }
            Self::InvalidTransition => formatter.write_str("invalid index lifecycle transition"),
            Self::WriteBudgetExceeded => formatter.write_str("index write budget exceeded"),
            Self::StaleReadRisk => formatter.write_str("index has stale-read risk"),
            Self::VerifiedRegression => formatter.write_str("index has a verified regression"),
            Self::StorageFailure => formatter.write_str("index durable storage failed"),
            Self::CoreApiUnavailable { operation, required_method } => write!(
                formatter,
                "D-31 {operation} requires the durable implementation of {required_method}"
            ),
        }
    }
}

impl std::error::Error for IndexAdapterError {}

/// Service-level errors returned before or after adapter execution.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IndexServiceError {
    /// A scope identifier is malformed.
    InvalidScope {
        /// Scope component that failed validation.
        part: &'static str,
    },
    /// Principal lacks the operation's database/collection scope.
    Forbidden {
        /// Operation the principal was not allowed to perform.
        operation: IndexOperation,
    },
    /// Tenant/database binding failed in the durable authority.
    UnauthorizedTenant,
    /// The requested database is not present in the durable member catalog.
    DatabaseNotFound,
    /// No durable D-31 coordinator is installed.
    NotReady,
    /// An old descriptor path was offered and was rejected.
    LegacyCoordinator,
    /// The request's generation is no longer current.
    GenerationConflict {
        /// Generation supplied by the caller.
        expected: u64,
        /// Generation currently held by the durable coordinator.
        current: u64,
    },
    /// The caller requested more than the bounded DTO can carry.
    BoundsExceeded {
        /// Number of records the adapter attempted to return.
        requested: usize,
        /// Maximum records admitted by this API.
        maximum: usize,
    },
    /// Candidate or index identity is invalid.
    InvalidIdentifier {
        /// Identifier component that failed validation.
        part: &'static str,
    },
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
            IndexAdapterError::NotDurable | IndexAdapterError::CoreApiUnavailable { .. } => {
                Self::NotReady
            }
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
    ///
    /// # Errors
    ///
    /// Returns an ownership, database, or storage error when readiness cannot be read.
    fn readiness(
        &self,
        scope: &IndexScope,
    ) -> Result<(CoordinatorKind, Durability), IndexAdapterError>;
    /// Performs tenant binding and any member-side authorization check.
    ///
    /// # Errors
    ///
    /// Returns an authorization, database, or storage error when scope admission fails.
    fn authorize(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        operation: IndexOperation,
    ) -> Result<(), IndexAdapterError>;
    /// Reads the current lifecycle generation.
    ///
    /// # Errors
    ///
    /// Returns a scope, storage, or unavailable-core error when generation is unavailable.
    fn generation(&self, scope: &IndexScope) -> Result<u64, IndexAdapterError>;
    /// Lists bounded durable state. The adapter must not consult a legacy registry.
    ///
    /// # Errors
    ///
    /// Returns a scope, storage, or unavailable-core error when durable state cannot be read.
    fn list(
        &self,
        scope: &IndexScope,
        limit: usize,
    ) -> Result<Vec<CoordinatorIndex>, IndexAdapterError>;
    /// Computes bounded recommendations from D-31 observations.
    ///
    /// # Errors
    ///
    /// Returns a scope, storage, or unavailable-core error when observations cannot be read.
    fn recommend(
        &self,
        scope: &IndexScope,
        limit: usize,
    ) -> Result<Vec<CoordinatorRecommendation>, IndexAdapterError>;
    /// Atomically applies a command in the durable coordinator.
    /// A successful receipt carries the requested identity and exactly the
    /// successor of the command's expected generation.
    /// The adapter must reject a budget overflow, stale generation, stale-read
    /// risk, or verified regression before committing any catalog mutation.
    ///
    /// # Errors
    ///
    /// Returns a scope, generation, transition, budget, safety, storage, or
    /// unavailable-core error before applying an inadmissible command.
    fn apply(
        &mut self,
        scope: &IndexScope,
        command: CoordinatorCommand,
    ) -> Result<CoordinatorIndex, IndexAdapterError>;
    /// Reads bounded lifecycle audit entries.
    ///
    /// # Errors
    ///
    /// Returns a scope, storage, or unavailable-core error when the journal cannot be read.
    fn audit(
        &self,
        scope: &IndexScope,
        index_id: Option<&str>,
        limit: usize,
    ) -> Result<Vec<AuditEntry>, IndexAdapterError>;
    /// Reads bounded before/after evidence for one index.
    ///
    /// # Errors
    ///
    /// Returns a scope, storage, or unavailable-core error when evidence cannot be read.
    fn evidence(
        &self,
        scope: &IndexScope,
        index_id: &str,
        limit: usize,
    ) -> Result<CoordinatorEvidence, IndexAdapterError>;
}

/// Adapter from one resident Engine to the D-31 service boundary.
///
/// The borrow ties the adapter to the resident Engine lifetime, matching the
/// Engine's [`Engine::queries`] and [`Engine::indexes`] stores.  A Member
/// router should create this value from its admitted database lease and keep
/// it alongside the lease while serving requests.
pub struct EngineIndexAdapter<'engine> {
    engine: &'engine Engine,
}

impl<'engine> EngineIndexAdapter<'engine> {
    /// Binds the adapter to one already-open durable Engine.
    #[must_use]
    pub const fn new(engine: &'engine Engine) -> Self {
        Self { engine }
    }

    /// Returns the Engine used by this adapter.
    #[must_use]
    pub const fn engine(&self) -> &'engine Engine {
        self.engine
    }

    fn tenant_for(&self, scope: &IndexScope) -> Result<TenantId, IndexAdapterError> {
        let tenant =
            TenantId::try_new(&scope.tenant).map_err(|_| IndexAdapterError::UnauthorizedTenant)?;
        let owner = self.engine.database_owner();
        if scope.database != owner.database().to_text() {
            return Err(IndexAdapterError::DatabaseNotFound);
        }
        // Local-development Engines intentionally support several logical
        // tenants. Production ownership is database-per-tenant and must be
        // checked against the durable owner tuple before any Engine read.
        if owner.environment().as_str() != "local-development"
            && scope.tenant != owner.tenant().to_text()
        {
            return Err(IndexAdapterError::UnauthorizedTenant);
        }
        Ok(tenant)
    }

    fn check_engine_tenant(&self, scope: &IndexScope) -> Result<TenantId, IndexAdapterError> {
        let tenant = self.tenant_for(scope)?;
        self.engine
            .read_snapshot(&self.engine.scope_for(&tenant))
            .map_err(|error| map_engine_error(&error))?;
        Ok(tenant)
    }

    fn unavailable(operation: &'static str, required_method: &'static str) -> IndexAdapterError {
        IndexAdapterError::CoreApiUnavailable { operation, required_method }
    }
}

impl IndexCoordinatorAdapter for EngineIndexAdapter<'_> {
    fn readiness(
        &self,
        scope: &IndexScope,
    ) -> Result<(CoordinatorKind, Durability), IndexAdapterError> {
        self.generation(scope)?;
        Ok((CoordinatorKind::D31, Durability::Durable))
    }

    fn authorize(
        &self,
        scope: &IndexScope,
        principal: &Principal,
        operation: IndexOperation,
    ) -> Result<(), IndexAdapterError> {
        self.check_engine_tenant(scope)?;
        let admitted = match scope.collection.as_deref() {
            Some(collection) => {
                principal.scope().admits_collection(operation.verb(), &scope.database, collection)
            }
            None => principal.scope().admits(operation.verb(), &scope.database),
        };
        admitted.then_some(()).ok_or(IndexAdapterError::UnauthorizedTenant)
    }

    fn generation(&self, scope: &IndexScope) -> Result<u64, IndexAdapterError> {
        let tenant = self.check_engine_tenant(scope)?;
        self.engine
            .auto_index_lifecycle_generation(&tenant)
            .map_err(|error| map_engine_error(&error))
    }

    fn list(
        &self,
        scope: &IndexScope,
        limit: usize,
    ) -> Result<Vec<CoordinatorIndex>, IndexAdapterError> {
        let tenant = self.check_engine_tenant(scope)?;
        let snapshot = self
            .engine
            .auto_index_lifecycle_snapshot(
                &tenant,
                scope.collection.as_deref(),
                limit.min(MAX_INDEX_LIST + 1),
            )
            .map_err(|error| map_engine_error(&error))?;
        snapshot
            .indexes
            .into_iter()
            .map(|index| {
                // An active flag alone does not prove the D-31 shadow gate. The
                // engine's evidence store still lacks durable publication hooks.
                if index.visible {
                    return Err(Self::unavailable(
                        "list visible index",
                        "Engine::auto_index_shadow_evidence",
                    ));
                }
                Ok(coordinator_index(index, snapshot.generation))
            })
            .collect()
    }

    fn recommend(
        &self,
        scope: &IndexScope,
        limit: usize,
    ) -> Result<Vec<CoordinatorRecommendation>, IndexAdapterError> {
        let tenant = self.check_engine_tenant(scope)?;
        let snapshot = self
            .engine
            .auto_index_recommendations(
                &tenant,
                scope.collection.as_deref(),
                limit.min(MAX_RECOMMENDATIONS + 1),
            )
            .map_err(|error| map_engine_error(&error))?;
        Ok(snapshot
            .recommendations
            .into_iter()
            .map(|value| CoordinatorRecommendation {
                candidate_id: value.candidate_id,
                collection: value.collection,
                fields: value.fields,
                score: value.score,
                maintenance_cost: value.maintenance_cost,
                generation: snapshot.generation,
                needs_descriptor: value.needs_descriptor,
            })
            .collect())
    }

    fn apply(
        &mut self,
        scope: &IndexScope,
        command: CoordinatorCommand,
    ) -> Result<CoordinatorIndex, IndexAdapterError> {
        let _ = self.check_engine_tenant(scope)?;
        let (operation, method) = match command {
            CoordinatorCommand::Apply(_) => ("apply", "Engine::auto_index_apply"),
            CoordinatorCommand::Pin(_) => ("pin", "Engine::auto_index_pin_template"),
            CoordinatorCommand::Forbid(_) => ("forbid", "Engine::auto_index_forbid_shape"),
            CoordinatorCommand::Retire(_) => ("retire", "Engine::auto_index_retire"),
        };
        Err(Self::unavailable(operation, method))
    }

    fn audit(
        &self,
        scope: &IndexScope,
        index_id: Option<&str>,
        limit: usize,
    ) -> Result<Vec<AuditEntry>, IndexAdapterError> {
        let tenant = self.check_engine_tenant(scope)?;
        self.engine
            .auto_index_lifecycle_audit(
                &tenant,
                scope.collection.as_deref(),
                index_id,
                limit.min(MAX_AUDIT_ENTRIES + 1),
            )
            .map(|entries| {
                entries
                    .into_iter()
                    .map(|entry| AuditEntry {
                        sequence: entry.sequence,
                        index_id: entry.index_id,
                        action: entry.action,
                        generation: entry.generation,
                        evidence_digest: entry.evidence_digest,
                        at_millis: entry.at_millis,
                    })
                    .collect()
            })
            .map_err(|error| map_engine_error(&error))
    }

    fn evidence(
        &self,
        scope: &IndexScope,
        _index_id: &str,
        _limit: usize,
    ) -> Result<CoordinatorEvidence, IndexAdapterError> {
        let _ = self.check_engine_tenant(scope)?;
        Err(Self::unavailable("evidence", "Engine::auto_index_shadow_evidence"))
    }
}

fn coordinator_index(
    value: terrabase::auto_index::LifecycleIndexView,
    generation: u64,
) -> CoordinatorIndex {
    use terrabase::auto_index::LifecyclePhase;
    let phase = match value.phase {
        LifecyclePhase::Observing => IndexPhase::Observing,
        LifecyclePhase::Selected => IndexPhase::Selected,
        LifecyclePhase::Building => IndexPhase::Building,
        LifecyclePhase::Shadowing => IndexPhase::Shadowing,
        LifecyclePhase::Active => IndexPhase::Active,
        LifecyclePhase::Invisible => IndexPhase::Invisible,
        LifecyclePhase::Abandoned => IndexPhase::Abandoned,
    };
    CoordinatorIndex {
        id: value.id,
        collection: value.collection,
        fields: value.fields,
        phase,
        generation,
        covered_watermark: value.covered_watermark,
        target_watermark: value.target_watermark,
        visible: value.visible,
        pinned: value.pinned,
        forbidden: value.forbidden,
        maintenance_cost: value.maintenance_cost,
        last_used_millis: value.last_used_millis,
        zero_stale_reads: value.zero_stale_reads,
        regression_free: value.regression_free,
    }
}

fn map_engine_error(error: &terrabase::EngineError) -> IndexAdapterError {
    match error {
        terrabase::EngineError::ScopeMismatch | terrabase::EngineError::SnapshotTenantMismatch => {
            IndexAdapterError::UnauthorizedTenant
        }
        terrabase::EngineError::StorageFailure | terrabase::EngineError::Closed(_) => {
            IndexAdapterError::StorageFailure
        }
        terrabase::EngineError::SnapshotEngineMismatch
        | terrabase::EngineError::OperationIdReuse { .. }
        | terrabase::EngineError::Conflict
        | terrabase::EngineError::InvalidDocument
        | terrabase::EngineError::Deadline
        | terrabase::EngineError::Cancelled
        | terrabase::EngineError::SnapshotPageLimitExceeded
        | terrabase::EngineError::SnapshotBoundaryUnavailable => IndexAdapterError::StorageFailure,
    }
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
        if index.visible && index.phase != IndexPhase::Active {
            return Err(IndexServiceError::InvalidTransition);
        }
        if index.visible
            && index.target_watermark.is_some_and(|target| index.covered_watermark < target)
        {
            return Err(IndexServiceError::StaleReadRisk);
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

    fn check_index_for_scope(
        index: &CoordinatorIndex,
        scope: &IndexScope,
        generation: u64,
    ) -> Result<(), IndexServiceError> {
        Self::check_index_shape(index)?;
        if scope.collection.as_deref().is_some_and(|collection| collection != index.collection) {
            return Err(IndexServiceError::InvalidTransition);
        }
        Self::check_index(index, generation)
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
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, readiness, bounds, or adapter failures.
    /// Invalid returned records and inconsistent generation or safety facts are refused.
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
        for index in &indexes {
            Self::check_index_for_scope(index, scope, generation)?;
        }
        indexes.truncate(limit);
        Ok(IndexList { generation, truncated, indexes })
    }

    /// Lists bounded D-31 recommendations for a scope.
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, readiness, bounds, or adapter failures.
    /// Invalid returned records and inconsistent generation or safety facts are refused.
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
        recommendations.truncate(limit);
        Ok(RecommendationList { generation, truncated, recommendations })
    }

    /// Applies one recommendation directly into the D-31 build/verify state machine.
    ///
    /// # Errors
    ///
    /// Returns identifier, scope, authorization, readiness, generation, or adapter
    /// failures. Unsafe visible state returned by the adapter is also refused.
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
        let committed_generation =
            expected.checked_add(1).ok_or(IndexServiceError::InvalidTransition)?;
        let candidate_id = request.candidate_id.clone();
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Apply(request))
            .map_err(|error| map_action_error(error, expected))?;
        if result.id != candidate_id {
            return Err(IndexServiceError::InvalidTransition);
        }
        Self::check_index_for_scope(&result, scope, committed_generation)?;
        Ok(result)
    }

    /// Pins one exact normalized template to one active index.
    ///
    /// # Errors
    ///
    /// Returns identifier, scope, authorization, readiness, generation, or adapter
    /// failures. Unsafe visible state returned by the adapter is also refused.
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
        let committed_generation =
            expected.checked_add(1).ok_or(IndexServiceError::InvalidTransition)?;
        let index_id = request.index_id.clone();
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Pin(request))
            .map_err(|error| map_action_error(error, expected))?;
        if result.id != index_id {
            return Err(IndexServiceError::InvalidTransition);
        }
        Self::check_index_for_scope(&result, scope, committed_generation)?;
        Ok(result)
    }

    /// Forbids one exact candidate shape; it never disables a whole collection.
    ///
    /// # Errors
    ///
    /// Returns identifier, scope, authorization, readiness, generation, or adapter
    /// failures. Unsafe visible state returned by the adapter is also refused.
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
        let committed_generation =
            expected.checked_add(1).ok_or(IndexServiceError::InvalidTransition)?;
        let candidate_id = request.candidate_id.clone();
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Forbid(request))
            .map_err(|error| map_action_error(error, expected))?;
        if result.id != candidate_id {
            return Err(IndexServiceError::InvalidTransition);
        }
        Self::check_index_for_scope(&result, scope, committed_generation)?;
        Ok(result)
    }

    /// Retires an index through the durable invisible-then-drop lifecycle.
    ///
    /// # Errors
    ///
    /// Returns identifier, scope, authorization, readiness, generation, or adapter
    /// failures. Unsafe visible state returned by the adapter is also refused.
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
        let committed_generation =
            expected.checked_add(1).ok_or(IndexServiceError::InvalidTransition)?;
        let index_id = request.index_id.clone();
        let result = self
            .adapter
            .apply(scope, CoordinatorCommand::Retire(request))
            .map_err(|error| map_action_error(error, expected))?;
        if result.id != index_id {
            return Err(IndexServiceError::InvalidTransition);
        }
        Self::check_index_for_scope(&result, scope, committed_generation)?;
        Ok(result)
    }

    /// Reads bounded durable audit history for the scope.
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, readiness, bounds, or adapter failures.
    /// Invalid returned index identifiers and action text are refused.
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
            if entry.generation > generation {
                return Err(IndexServiceError::GenerationConflict {
                    expected: generation,
                    current: entry.generation,
                });
            }
            if index_id.is_some() && entry.index_id.as_deref() != index_id {
                return Err(IndexServiceError::InvalidTransition);
            }
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
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, readiness, bounds, or adapter failures.
    /// Invalid returned records and inconsistent generation or safety facts are refused.
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
        if evidence.index_id != index_id {
            return Err(IndexServiceError::InvalidTransition);
        }
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
        evidence: Option<CoordinatorEvidence>,
        audit_entries: Vec<AuditEntry>,
        recommendations: Vec<CoordinatorRecommendation>,
        reported_generation: Option<u64>,
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
            limit: usize,
        ) -> Result<Vec<CoordinatorRecommendation>, IndexAdapterError> {
            Ok(self.recommendations.iter().take(limit).cloned().collect())
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
            let generation =
                self.generation.checked_add(1).ok_or(IndexAdapterError::InvalidTransition)?;
            let index = self.indexes.first_mut().ok_or(IndexAdapterError::InvalidTransition)?;
            index.generation = generation;
            self.generation = generation;
            self.commands.push(command);
            let mut receipt = index.clone();
            receipt.generation = self.reported_generation.unwrap_or(generation);
            Ok(receipt)
        }
        fn audit(
            &self,
            _scope: &IndexScope,
            _index_id: Option<&str>,
            limit: usize,
        ) -> Result<Vec<AuditEntry>, IndexAdapterError> {
            Ok(self.audit_entries.iter().take(limit).cloned().collect())
        }
        fn evidence(
            &self,
            _scope: &IndexScope,
            _index_id: &str,
            _limit: usize,
        ) -> Result<CoordinatorEvidence, IndexAdapterError> {
            self.evidence.clone().ok_or(IndexAdapterError::InvalidTransition)
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
    fn mutation_receipts_use_the_committed_generation_and_overflow_never_dispatches() {
        for operation in 0..4 {
            let invoke = |service: &mut IndexService<Adapter>, expected_generation| match operation
            {
                0 => service.apply(
                    &scope(),
                    &principal(),
                    ApplyRequest {
                        candidate_id: "idx-users".into(),
                        expected_generation,
                        at_millis: 10,
                    },
                ),
                1 => service.pin(
                    &scope(),
                    &principal(),
                    PinRequest {
                        index_id: "idx-users".into(),
                        template: "users where email = ?".into(),
                        expected_generation,
                    },
                ),
                2 => service.forbid(
                    &scope(),
                    &principal(),
                    ForbidRequest {
                        candidate_id: "idx-users".into(),
                        shape: "email".into(),
                        expected_generation,
                    },
                ),
                _ => service.retire(
                    &scope(),
                    &principal(),
                    RetireRequest {
                        index_id: "idx-users".into(),
                        expected_generation,
                        at_millis: 10,
                    },
                ),
            };
            let mut service = IndexService::new(Adapter {
                generation: 7,
                indexes: vec![active()],
                ..Adapter::default()
            });
            assert_eq!(invoke(&mut service, 7).unwrap().generation, 8);
            assert_eq!(
                invoke(&mut service, 7),
                Err(IndexServiceError::GenerationConflict { expected: 7, current: 8 })
            );
            assert_eq!(service.adapter().commands.len(), 1);
            assert_eq!(invoke(&mut service, 8).unwrap().generation, 9);
            let mut foreign = IndexService::new(Adapter {
                generation: 7,
                indexes: vec![CoordinatorIndex { id: "other-index".into(), ..active() }],
                ..Adapter::default()
            });
            assert_eq!(invoke(&mut foreign, 7), Err(IndexServiceError::InvalidTransition));
            for reported_generation in [7, 9] {
                let mut service = IndexService::new(Adapter {
                    generation: 7,
                    indexes: vec![active()],
                    reported_generation: Some(reported_generation),
                    ..Adapter::default()
                });
                assert_eq!(
                    invoke(&mut service, 7),
                    Err(IndexServiceError::GenerationConflict {
                        expected: 8,
                        current: reported_generation,
                    })
                );
            }
            let mut exhausted = IndexService::new(Adapter {
                generation: u64::MAX,
                indexes: vec![active()],
                ..Adapter::default()
            });
            assert_eq!(invoke(&mut exhausted, u64::MAX), Err(IndexServiceError::InvalidTransition));
            assert!(exhausted.adapter().commands.is_empty());
        }
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
    fn visible_indexes_require_active_phase_and_completed_coverage() {
        for phase in [
            IndexPhase::Observing,
            IndexPhase::Selected,
            IndexPhase::Building,
            IndexPhase::Shadowing,
            IndexPhase::Invisible,
            IndexPhase::Abandoned,
        ] {
            let mut index = active();
            index.phase = phase;
            let service = IndexService::new(Adapter {
                generation: 7,
                indexes: vec![index],
                ..Adapter::default()
            });
            assert_eq!(
                service.list(&scope(), &principal(), 1),
                Err(IndexServiceError::InvalidTransition)
            );
        }
        let mut index = active();
        index.target_watermark = Some(index.covered_watermark + 1);
        let service = IndexService::new(Adapter {
            generation: 7,
            indexes: vec![index],
            ..Adapter::default()
        });
        assert_eq!(service.list(&scope(), &principal(), 1), Err(IndexServiceError::StaleReadRisk));
    }

    #[test]
    fn evidence_must_belong_to_the_requested_index() {
        let mut service = IndexService::new(Adapter {
            generation: 7,
            evidence: Some(CoordinatorEvidence {
                index_id: "other-index".into(),
                generation: 7,
                baseline: Vec::new(),
                candidate: Vec::new(),
                improvement: 0.5,
                regression: false,
                digest: [1; 32],
            }),
            ..Adapter::default()
        });
        assert_eq!(
            service.evidence(&scope(), &principal(), "idx-users", 1),
            Err(IndexServiceError::InvalidTransition)
        );
        service.adapter_mut().evidence.as_mut().expect("evidence").index_id = "idx-users".into();
        assert!(service.evidence(&scope(), &principal(), "idx-users", 1).is_ok());
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

    #[test]
    fn engine_adapter_reads_durable_generation_and_empty_catalog() {
        let location = std::env::temp_dir().join(format!(
            "terrabase-index-adapter-{}-{}",
            std::process::id(),
            terrabase::identity::process_generator().generate().to_text()
        ));
        let config = terrabase::EngineConfig::durable(&location).expect("durable config");
        let engine = terrabase::Engine::open(config).expect("durable engine");
        let database = engine.database_owner().database().to_text();
        let scope = IndexScope::try_new("tenant", database, None).expect("scope");
        let adapter = EngineIndexAdapter::new(&engine);

        assert_eq!(
            adapter.readiness(&scope).expect("owner binding"),
            (CoordinatorKind::D31, Durability::Durable)
        );
        assert_eq!(adapter.generation(&scope).unwrap(), 0);
        assert!(adapter.list(&scope, 1).unwrap().is_empty());
        assert!(adapter.recommend(&scope, 1).unwrap().is_empty());
        let tenant = TenantId::try_new("tenant").unwrap();
        engine.auto_index_forbid_shape(&tenant, "candidate", "email", 0).unwrap();
        assert_eq!(adapter.generation(&scope).unwrap(), 1);

        engine.shutdown().expect("flush engine");
        let reopened = Engine::open(terrabase::EngineConfig::durable(&location).unwrap()).unwrap();
        let adapter = EngineIndexAdapter::new(&reopened);
        assert_eq!(adapter.generation(&scope).unwrap(), 1);
        assert_eq!(adapter.audit(&scope, None, 10).unwrap().len(), 1);
        let collection_scope =
            IndexScope::try_new("tenant", scope.database.clone(), Some("users".into())).unwrap();
        assert!(adapter.audit(&collection_scope, None, 10).unwrap().is_empty());
        let other = IndexScope::try_new("other-tenant", scope.database.clone(), None).unwrap();
        assert_eq!(adapter.generation(&other).unwrap(), 0);
        assert!(adapter.audit(&other, None, 10).unwrap().is_empty());
        reopened.shutdown().unwrap();
        std::fs::remove_dir_all(location).expect("remove test database");
    }

    #[test]
    fn engine_adapter_recommends_from_queries_and_reports_selected_candidates() {
        use terrabase::operations::{QueryInput, SqlQueryText};
        use terrabase::types::{
            CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
            OperationId,
        };
        let location = std::env::temp_dir().join(format!(
            "terrabase-index-reads-{}",
            terrabase::identity::process_generator().generate().to_text()
        ));
        let engine = Engine::open(terrabase::EngineConfig::durable(&location).unwrap()).unwrap();
        let tenant = TenantId::try_new("tenant").unwrap();
        let tenant_scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&tenant_scope).unwrap();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, OperationId::try_new("seed").unwrap()).unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("one").unwrap(),
                    CollectionName::try_new("users").unwrap(),
                    DocumentValue::Object(std::collections::BTreeMap::from([(
                        "email".into(),
                        DocumentValue::String("a@example.test".into()),
                    )])),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            terrabase::engine::CommitOutcome::Committed(_)
        ));
        let plan = engine
            .queries()
            .compile(QueryInput::sql(
                SqlQueryText::try_new("SELECT email FROM users WHERE email = 'a@example.test'")
                    .unwrap(),
            ))
            .unwrap();
        engine.queries().execute(&plan, &engine.read_snapshot(&tenant_scope).unwrap()).unwrap();
        let database = engine.database_owner().database().to_text();
        let scope = IndexScope::try_new("tenant", &database, Some("users".into())).unwrap();
        let keys = crate::api_keys::KeyStore::with_keys(&[(
            "tskey_index_reads_abcdefghijkl",
            "test",
            Scope::database(&database, Verbs::ALL),
        )]);
        let principal = keys.admit("tskey_index_reads_abcdefghijkl").unwrap();
        let mut service = IndexService::new(EngineIndexAdapter::new(&engine));
        assert!(service.list(&scope, &principal, 10).unwrap().indexes.is_empty());
        let recommended = service.recommend(&scope, &principal, 10).unwrap();
        assert_eq!(recommended.generation, 0);
        assert_eq!(recommended.recommendations.len(), 1);
        let candidate = &recommended.recommendations[0].candidate_id;
        let other = IndexScope::try_new("other", &database, Some("users".into())).unwrap();
        assert!(service.recommend(&other, &principal, 10).unwrap().recommendations.is_empty());
        assert_eq!(
            service.apply(
                &scope,
                &principal,
                ApplyRequest {
                    candidate_id: candidate.clone(),
                    expected_generation: 0,
                    at_millis: 1,
                }
            ),
            Err(IndexServiceError::NotReady)
        );
        assert_eq!(engine.auto_index_lifecycle_generation(&tenant).unwrap(), 0);
        // Selection is durable and visible in the control catalog, but it
        // cannot claim planner activation or verified safety.
        engine.auto_index_apply(&tenant, candidate, 0, 1).unwrap();
        let selected = service.list(&scope, &principal, 10).unwrap();
        assert_eq!(selected.indexes.len(), 1);
        assert_eq!(selected.indexes[0].phase, IndexPhase::Selected);
        assert!(!selected.indexes[0].visible);
        assert!(!selected.indexes[0].zero_stale_reads);
        assert!(!selected.indexes[0].regression_free);
        let elsewhere = IndexScope::try_new("tenant", &database, Some("orders".into())).unwrap();
        assert!(service.list(&elsewhere, &principal, 10).unwrap().indexes.is_empty());
        let audit = service.audit(&scope, &principal, Some(candidate), 1).unwrap();
        assert_eq!(audit.entries.len(), 1);
        assert_eq!(audit.entries[0].index_id.as_deref(), Some(candidate.as_str()));
        assert!(!audit.truncated);
        engine.auto_index_forbid_shape(&tenant, candidate, "email", 1).unwrap();
        let page = service.audit(&scope, &principal, Some(candidate), 1).unwrap();
        assert!(page.truncated);
        assert_eq!(page.entries.len(), 1);
        assert_eq!(page.entries[0].action, "forbid");
        assert_eq!(page.generation, 2);
        assert!(service.audit(&elsewhere, &principal, None, 1).unwrap().entries.is_empty());
        assert!(service.audit(&other, &principal, None, 1).unwrap().entries.is_empty());
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(location).unwrap();
    }

    #[test]
    fn engine_adapter_refuses_a_database_not_owned_by_the_engine() {
        let location = std::env::temp_dir().join(format!(
            "terrabase-index-adapter-{}-{}",
            std::process::id(),
            terrabase::identity::process_generator().generate().to_text()
        ));
        let config = terrabase::EngineConfig::durable(&location).expect("durable config");
        let engine = terrabase::Engine::open(config).expect("durable engine");
        let scope = IndexScope::try_new("tenant", "other-database", None).expect("scope");
        let adapter = EngineIndexAdapter::new(&engine);
        assert_eq!(adapter.readiness(&scope), Err(IndexAdapterError::DatabaseNotFound));
        engine.shutdown().expect("flush engine");
        std::fs::remove_dir_all(location).expect("remove test database");
    }
    #[test]
    fn mutation_responses_must_match_collection_and_have_valid_shape() {
        for invalid in [
            CoordinatorIndex { collection: "foreign".into(), ..active() },
            CoordinatorIndex { fields: vec!["field".into(); MAX_INDEX_FIELDS + 1], ..active() },
            CoordinatorIndex { id: String::new(), ..active() },
        ] {
            let mut service = IndexService::new(Adapter {
                generation: 7,
                indexes: vec![invalid],
                ..Adapter::default()
            });
            assert!(
                service
                    .apply(
                        &scope(),
                        &principal(),
                        ApplyRequest {
                            candidate_id: "idx-users".into(),
                            expected_generation: 7,
                            at_millis: 10
                        }
                    )
                    .is_err()
            );
            assert!(
                service
                    .pin(
                        &scope(),
                        &principal(),
                        PinRequest {
                            index_id: "idx-users".into(),
                            template: "users-email".into(),
                            expected_generation: 8,
                        }
                    )
                    .is_err()
            );
            assert!(
                service
                    .forbid(
                        &scope(),
                        &principal(),
                        ForbidRequest {
                            candidate_id: "idx-users".into(),
                            shape: "email".into(),
                            expected_generation: 9,
                        }
                    )
                    .is_err()
            );
            assert!(
                service
                    .retire(
                        &scope(),
                        &principal(),
                        RetireRequest {
                            index_id: "idx-users".into(),
                            expected_generation: 10,
                            at_millis: 10
                        }
                    )
                    .is_err()
            );
        }
    }

    #[test]
    fn pin_and_retire_refuse_a_different_index_in_the_same_collection() {
        let mut service = IndexService::new(Adapter {
            generation: 7,
            indexes: vec![active()],
            ..Adapter::default()
        });
        assert_eq!(
            service.pin(
                &scope(),
                &principal(),
                PinRequest {
                    index_id: "another-index".into(),
                    template: "users-email".into(),
                    expected_generation: 7,
                }
            ),
            Err(IndexServiceError::InvalidTransition)
        );
        assert_eq!(
            service.retire(
                &scope(),
                &principal(),
                RetireRequest {
                    index_id: "another-index".into(),
                    expected_generation: 8,
                    at_millis: 10
                }
            ),
            Err(IndexServiceError::InvalidTransition)
        );
    }
    #[test]
    fn audit_rejects_events_newer_than_its_snapshot_generation() {
        let event = AuditEntry {
            sequence: 1,
            index_id: Some("idx-users".into()),
            action: "transition".into(),
            generation: 7,
            evidence_digest: None,
            at_millis: 10,
        };
        let service = IndexService::new(Adapter {
            generation: 7,
            audit_entries: vec![event.clone(), AuditEntry { sequence: 2, generation: 8, ..event }],
            ..Adapter::default()
        });
        for limit in [1, 2] {
            assert_eq!(
                service.audit(&scope(), &principal(), None, limit),
                Err(IndexServiceError::GenerationConflict { expected: 7, current: 8 })
            );
        }
    }

    #[test]
    fn filtered_audit_rejects_foreign_and_unscoped_events_including_lookahead() {
        let event = |index_id: Option<&str>, sequence| AuditEntry {
            sequence,
            index_id: index_id.map(str::to_owned),
            action: "activated".into(),
            generation: 7,
            evidence_digest: None,
            at_millis: 10,
        };
        for wrong in [Some("other-index"), None] {
            let service = IndexService::new(Adapter {
                generation: 7,
                audit_entries: vec![event(Some("idx-users"), 1), event(wrong, 2)],
                ..Adapter::default()
            });
            assert_eq!(
                service.audit(&scope(), &principal(), Some("idx-users"), 1),
                Err(IndexServiceError::InvalidTransition),
                "a foreign lookahead row cannot establish pagination for this index"
            );
            assert!(service.audit(&scope(), &principal(), None, 2).is_ok());
        }
        let service = IndexService::new(Adapter {
            generation: 7,
            audit_entries: vec![event(Some("idx-users"), 1), event(Some("idx-users"), 2)],
            ..Adapter::default()
        });
        let page =
            service.audit(&scope(), &principal(), Some("idx-users"), 1).expect("matching events");
        assert_eq!(page.entries.len(), 1);
        assert!(page.truncated);
    }
    #[test]
    fn index_list_validates_lookahead_before_claiming_another_page() {
        for invalid in [
            CoordinatorIndex { collection: "other".into(), ..active() },
            CoordinatorIndex { generation: 8, ..active() },
            CoordinatorIndex { id: String::new(), ..active() },
        ] {
            let service = IndexService::new(Adapter {
                generation: 7,
                indexes: vec![active(), invalid],
                ..Adapter::default()
            });
            assert!(service.list(&scope(), &principal(), 1).is_err());
        }
        let service = IndexService::new(Adapter {
            generation: 7,
            indexes: vec![active(), CoordinatorIndex { id: "next-index".into(), ..active() }],
            ..Adapter::default()
        });
        let page = service.list(&scope(), &principal(), 1).expect("valid lookahead");
        assert!(page.truncated);
        assert_eq!(page.indexes.len(), 1);
    }

    #[test]
    fn recommendation_list_validates_lookahead_scope_generation_and_score() {
        let valid = CoordinatorRecommendation {
            candidate_id: "candidate".into(),
            collection: "users".into(),
            fields: vec!["email".into()],
            score: 1.0,
            maintenance_cost: 1,
            generation: 7,
            needs_descriptor: true,
        };
        for invalid in [
            CoordinatorRecommendation { collection: "other".into(), ..valid.clone() },
            CoordinatorRecommendation { generation: 8, ..valid.clone() },
            CoordinatorRecommendation { score: f64::NAN, ..valid.clone() },
        ] {
            let service = IndexService::new(Adapter {
                generation: 7,
                recommendations: vec![valid.clone(), invalid],
                ..Adapter::default()
            });
            assert!(service.recommend(&scope(), &principal(), 1).is_err());
        }
        let service = IndexService::new(Adapter {
            generation: 7,
            recommendations: vec![
                valid.clone(),
                CoordinatorRecommendation { candidate_id: "next".into(), ..valid },
            ],
            ..Adapter::default()
        });
        let page = service.recommend(&scope(), &principal(), 1).expect("valid lookahead");
        assert!(page.truncated);
        assert_eq!(page.recommendations.len(), 1);
    }
}
