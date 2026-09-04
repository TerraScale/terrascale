//! Platform-owned tenant scoping and dispatch seam kept separate from the Engine.
//!
//! PD-2026-08-26-11 keeps this boundary local and trusted: the TerraScale
//! backend is the only caller, it is trusted, and there is deliberately no
//! authentication, authorization, or credential surface here — customer
//! authentication, authorization (PB-3042), and tenant onboarding stay
//! exclusively on the TerraScale Platform, and the `CallerDenied`,
//! `PermissionDenied`, and `Expired` refusal vocabulary remains unused for
//! that future Platform side. What this adapter does own:
//!
//! - the engine-enforced [`TenantScope`] is created from backend-supplied
//!   context only, and every tenant claim a request carries must match it:
//!   cross-tenant probes are denied and audited, and the Engine repeats the
//!   isolation on every snapshot, transaction, and query it serves;
//! - the closed [`PublicOperation`] registry is dispatched end-to-end
//!   through the trusted in-process `EnginePort` with typed errors only;
//! - a typed, content-free lifecycle audit skeleton records every dispatch
//!   without document bodies or secrets.

#![allow(dead_code)]
#![allow(clippy::doc_markdown, clippy::missing_errors_doc, clippy::must_use_candidate)]

mod audit;
mod engine_port;
#[cfg(test)]
mod schema_surface_tests;
#[cfg(test)]
mod tests;

pub use audit::{AuditEvent, AuditOperation, AuditRefusal, AuditStage, MAX_AUDIT_EVENTS};

use crate::{
    adapters::protocol::CallerContext,
    engine::{EngineError, EnginePort},
    operations::{
        EngineRequest, EngineResponse, FamilyOperation, PublicOperation, PublicResponse,
        QueryInput, SchemaOperation,
    },
    types::{Cancellation, Deadline, OperationId, TenantScope},
};

use audit::AuditJournal;

/// The typed Platform adapter used by the public Protocol adapter.
pub struct PlatformAdapter<'engine> {
    authorizer: TrustedAuthorizer,
    engine: &'engine dyn EnginePort,
    audit: AuditJournal,
}

impl<'engine> PlatformAdapter<'engine> {
    /// Creates the adapter from trusted Platform wiring over one Engine.
    ///
    /// Crate-private by design: the authorizer and the Engine port are
    /// crate-private types, so only in-crate Platform wiring — the engine
    /// process serving its local backend — constructs the adapter.
    pub(crate) fn new(authorizer: TrustedAuthorizer, engine: &'engine dyn EnginePort) -> Self {
        Self { authorizer, engine, audit: AuditJournal::new() }
    }

    /// Dispatches one typed public operation after Platform validation.
    ///
    /// There is deliberately no authentication here (PD-2026-08-26-11): the
    /// backend is trusted and local, so no credential is read and no caller
    /// is verified. The dispatch instead enforces, in order:
    ///
    /// 1. the tenant scope is created from the backend-supplied caller
    ///    context only, and every tenant claim the request carries must
    ///    match it — a mismatch is [`PlatformError::TenantDenied`] and is
    ///    audited before any Engine access;
    /// 2. the operation's own bounds — an elapsed deadline or a cancelled
    ///    signal is refused with the mutating identity preserved (the
    ///    Protocol adapter repeats the same check before dispatch);
    /// 3. the trusted in-process Engine port executes the request, and the
    ///    Engine's own snapshot, transaction, and query validation repeats
    ///    the tenant and engine binding on every path.
    ///
    /// Every dispatch records typed lifecycle audit events — refused, or
    /// dispatched plus completed — that never carry document bodies or
    /// secrets.
    ///
    /// # Errors
    ///
    /// Returns [`PlatformError::TenantDenied`] for a cross-tenant request,
    /// [`PlatformError::Deadline`] or [`PlatformError::Cancelled`] with the
    /// mutating identity for elapsed bounds, [`PlatformError::Conflict`],
    /// [`PlatformError::InvalidRequest`], or
    /// [`PlatformError::EngineFailure`] as the Engine's typed failures map,
    /// and never an untyped or generic refusal.
    pub fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError> {
        // The scope is created from backend-supplied context only: the
        // tenant claim the backend put in the caller context, paired with
        // the engine identity Platform wiring observed. No credential,
        // principal role, or permission input exists on this path.
        let scope = self.authorizer.tenant_scope_for(caller);
        let kind = audit_operation(&operation);
        let principal = caller.principal().clone();
        let tenant = caller.tenant_claim().clone();

        // Tenant isolation first: a request claiming another tenant is
        // denied and audited before anything else, so a cross-tenant probe
        // learns nothing about operation state.
        if let Err(error) = enforce_tenant_isolation(&scope, &operation) {
            let stage = AuditStage::Refused(audit_refusal(&error));
            self.audit.record(stage, kind, principal, tenant, mutating_identity(&operation));
            return Err(error);
        }

        // The operation's own bounds, with the mutating identity preserved.
        let identity = mutating_identity(&operation);
        let (deadline, cancellation) = bounds(&operation);
        if deadline.is_expired() {
            let error = PlatformError::Deadline { operation_id: identity.clone() };
            let stage = AuditStage::Refused(audit_refusal(&error));
            self.audit.record(stage, kind, principal, tenant, identity);
            return Err(error);
        }
        if cancellation.is_cancelled() {
            let error = PlatformError::Cancelled { operation_id: identity.clone() };
            let stage = AuditStage::Refused(audit_refusal(&error));
            self.audit.record(stage, kind, principal, tenant, identity);
            return Err(error);
        }

        // Accepted: audit the dispatch, mint the short-lived grant, and
        // forward through the trusted port. The grant is consumed within
        // this call and never crosses a seam.
        self.audit.record(
            AuditStage::Dispatched,
            kind,
            principal.clone(),
            tenant.clone(),
            identity.clone(),
        );
        let grant = self.authorizer.mint_grant();
        match self.forward(&grant, scope, operation) {
            Ok(response) => {
                self.audit.record(AuditStage::Completed, kind, principal, tenant, identity);
                Ok(response)
            }
            Err(error) => {
                let stage = AuditStage::Refused(audit_refusal(&error));
                self.audit.record(stage, kind, principal, tenant, identity);
                Err(error)
            }
        }
    }

    /// Returns a snapshot of the typed lifecycle audit events recorded so far.
    ///
    /// The events are the bounded, in-process audit skeleton: they carry only
    /// structural identity, never a document body or a secret, and they live
    /// only as long as this adapter. Durable audit shipping belongs to the
    /// later Platform side.
    #[must_use]
    pub fn audit_events(&self) -> Vec<AuditEvent> {
        self.audit.events()
    }

    /// Forwards one tenant-scoped request through the trusted Engine port.
    ///
    /// The private [`PlatformGrant`] is the only way to reach this method,
    /// and only [`TrustedAuthorizer::mint_grant`] creates one, so nothing
    /// outside a dispatch can forward an engine request.
    fn forward(
        &self,
        grant: &PlatformGrant,
        scope: TenantScope,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError> {
        debug_assert_eq!(
            grant.engine_identity, self.authorizer.engine_identity,
            "the grant binds the same engine as the authorizer that minted it"
        );
        let identity = mutating_identity(&operation);
        let request = match operation {
            PublicOperation::DocumentRead(request) => {
                EngineRequest::DocumentRead { scope, request }
            }
            PublicOperation::DocumentWrite(request) => {
                EngineRequest::DocumentWrite { scope, request }
            }
            PublicOperation::Query(request) => EngineRequest::Query { scope, request },
            PublicOperation::Family(request) => EngineRequest::Family { scope, request },
            PublicOperation::Schema(request) => EngineRequest::Schema { scope, request },
        };
        self.engine
            .execute(request)
            .map(public_response)
            .map_err(|error| map_engine_error(error, identity))
    }
}

/// Platform validation and Engine forwarding failures.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PlatformError {
    /// Caller identity was denied.
    ///
    /// Unused at this seam: PD-2026-08-26-11 keeps this local boundary
    /// trusted with no authentication surface; the future Platform side
    /// owns caller denial.
    CallerDenied,
    /// The caller's tenant claim was denied.
    ///
    /// Produced when a request's tenant claim does not match the
    /// backend-supplied tenant scope, or when the Engine's own tenant
    /// isolation refuses the scope.
    TenantDenied,
    /// The requested public operation was not permitted.
    ///
    /// Unused at this seam: there is no authorization surface here; the
    /// future Platform side owns operation permission.
    PermissionDenied,
    /// Caller evidence or authorization expired.
    ///
    /// Unused at this seam: no caller evidence is evaluated here; the
    /// future Platform side owns evidence expiry.
    Expired,
    /// The operation requires explicit confirmation.
    ///
    /// Unused at this seam: no confirmation policy runs here; the future
    /// Platform side owns confirmation.
    ConfirmationRequired,
    /// Audit recording failed.
    ///
    /// Unused at this seam: the audit skeleton records in-process and never
    /// fails a dispatch; durable audit shipping may own this failure later.
    AuditFailure,
    /// Secret handling policy rejected the request.
    ///
    /// Unused at this seam: no credential is read, accepted, or stored here,
    /// so no secret policy exists to violate; the future Platform side owns
    /// secret handling.
    SecretPolicyViolation,
    /// The public operation was malformed.
    ///
    /// Produced when the Engine rejects the operation's logical content —
    /// for example a document or family payload that fails its rules.
    InvalidRequest,
    /// The operation conflicted.
    Conflict {
        /// The optional operation identity associated with the conflict.
        operation_id: Option<crate::types::OperationId>,
    },
    /// The operation deadline expired.
    Deadline {
        /// The optional operation identity associated with the deadline.
        operation_id: Option<crate::types::OperationId>,
    },
    /// The operation was cancelled.
    Cancelled {
        /// The optional operation identity associated with cancellation.
        operation_id: Option<crate::types::OperationId>,
    },
    /// Engine returned a typed failure.
    EngineFailure {
        /// The optional operation identity associated with the Engine error.
        operation_id: Option<crate::types::OperationId>,
    },
}

/// A short-lived private authorization result.
///
/// PD-2026-08-26-11 keeps this boundary local and trusted, so the grant
/// records no caller authentication: it binds one dispatch to the engine
/// identity of the tenant scope it was minted from and is consumed by the
/// forwarding step within the same call. It never crosses Protocol or
/// Engine, is never public or re-exported, and only
/// [`TrustedAuthorizer::mint_grant`] creates one.
struct PlatformGrant {
    engine_identity: u128,
}

/// The trusted local Platform wiring binding backend-supplied context to one Engine.
///
/// Under PD-2026-08-26-11 there is no authentication or authorization
/// surface here, so the authorizer holds no credential and decides nothing
/// about callers. It records only the Engine identity Platform wiring
/// observed, which — combined with the backend-supplied tenant claim — is
/// exactly the pair the engine-enforced tenant scope is created from.
pub(crate) struct TrustedAuthorizer {
    engine_identity: u128,
}

impl TrustedAuthorizer {
    /// Binds the authorizer to the Engine identity Platform wiring supplies.
    ///
    /// The identity is the engine-side half of every tenant scope this
    /// adapter creates; a wrong binding fails closed on the Engine's own
    /// scope validation at dispatch.
    pub(crate) fn new(engine_identity: u128) -> Self {
        Self { engine_identity }
    }

    /// Creates the engine-enforced tenant scope from backend-supplied context only.
    ///
    /// The tenant comes from the caller context the backend supplied; no
    /// credential, principal role, or permission input exists here, and the
    /// scope is never created from any other source.
    fn tenant_scope_for(&self, caller: &CallerContext) -> TenantScope {
        TenantScope::from_parts(caller.tenant_claim().clone(), self.engine_identity)
    }

    /// Mints the short-lived private grant for one dispatch.
    ///
    /// The grant carries the authorizer's own engine binding — the same
    /// identity every scope it creates pairs with the backend tenant claim.
    fn mint_grant(&self) -> PlatformGrant {
        PlatformGrant { engine_identity: self.engine_identity }
    }
}

/// The private forwarding seam from Protocol to Platform.
pub(crate) trait PlatformPort {
    fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError>;
}

/// Enforces that every tenant claim a request carries matches the derived scope.
///
/// The match is exhaustive over the closed registry with no wildcard arm, so
/// a new variant that carries a tenant claim fails compilation until it is
/// checked here. The SQL text form and family reads carry no
/// tenant claim: the derived backend scope is their only tenant input, so
/// there is nothing else to compare for them.
fn enforce_tenant_isolation(
    scope: &TenantScope,
    operation: &PublicOperation,
) -> Result<(), PlatformError> {
    let tenant = scope.tenant();
    let mismatch = match operation {
        PublicOperation::DocumentRead(request) => request.tenant_claim() != tenant,
        PublicOperation::DocumentWrite(request) => request.tenant_claim() != tenant,
        PublicOperation::Query(request) => match request.input() {
            QueryInput::Typed(spec) => spec.tenant_claim() != tenant,
            // SQL text binds the tenant at execution, not in
            // the text.
            QueryInput::Sql(_) | QueryInput::SqlBounded { .. } => false,
        },
        PublicOperation::Family(FamilyOperation::Read(_)) => false,
        PublicOperation::Family(FamilyOperation::Write(request)) => {
            request.tenant_scope().tenant() != tenant
        }
        // The schema surface carries a tenant claim on every operation,
        // reads included: the claim names the collection's owner and the
        // derived scope is the authorized truth, so a mismatch is refused
        // before any schema state is read.
        PublicOperation::Schema(SchemaOperation::Declare(request)) => {
            request.tenant_claim() != tenant
        }
        PublicOperation::Schema(SchemaOperation::Status(request)) => {
            request.tenant_claim() != tenant
        }
        PublicOperation::Schema(SchemaOperation::Validate(request)) => {
            request.tenant_claim() != tenant
        }
        PublicOperation::Schema(SchemaOperation::MigrationStatus(request)) => {
            request.tenant_claim() != tenant
        }
        PublicOperation::Schema(SchemaOperation::MigrationDryRun(request)) => {
            request.tenant_claim() != tenant
        }
        PublicOperation::Schema(SchemaOperation::MigrationApply(request)) => {
            request.tenant_claim() != tenant
        }
    };
    if mismatch {
        return Err(PlatformError::TenantDenied);
    }
    Ok(())
}

/// Returns the deadline and cancellation signal carried by one operation.
///
/// The match is exhaustive with no wildcard arm, so adding a registry
/// variant without a bounded deadline and cancellation breaks compilation.
/// The Protocol adapter keeps the same private shape at its own seam.
fn bounds(operation: &PublicOperation) -> (Deadline, Cancellation) {
    match operation {
        PublicOperation::DocumentRead(request) => (request.deadline(), request.cancellation()),
        PublicOperation::DocumentWrite(request) => (request.deadline(), request.cancellation()),
        PublicOperation::Query(request) => (request.deadline(), request.cancellation()),
        PublicOperation::Family(FamilyOperation::Read(request)) => {
            (request.deadline(), request.cancellation())
        }
        PublicOperation::Family(FamilyOperation::Write(request)) => {
            (request.deadline(), request.cancellation())
        }
        PublicOperation::Schema(SchemaOperation::Declare(request)) => {
            (request.deadline(), request.cancellation())
        }
        PublicOperation::Schema(SchemaOperation::Status(request)) => {
            (request.deadline(), request.cancellation())
        }
        PublicOperation::Schema(SchemaOperation::Validate(request)) => {
            (request.deadline(), request.cancellation())
        }
        PublicOperation::Schema(SchemaOperation::MigrationStatus(request)) => {
            (request.deadline(), request.cancellation())
        }
        PublicOperation::Schema(SchemaOperation::MigrationDryRun(request)) => {
            (request.deadline(), request.cancellation())
        }
        PublicOperation::Schema(SchemaOperation::MigrationApply(request)) => {
            (request.deadline(), request.cancellation())
        }
    }
}

/// Returns the mutating operation identity carried by one operation.
///
/// Every mutating request carries its stable [`OperationId`]; read-only
/// requests carry none. The identity is what deadline, cancellation, and
/// engine-failure refusals preserve so a caller can resolve or retry.
fn mutating_identity(operation: &PublicOperation) -> Option<OperationId> {
    match operation {
        PublicOperation::DocumentRead(_) | PublicOperation::Family(FamilyOperation::Read(_)) => {
            None
        }
        PublicOperation::DocumentWrite(request) => Some(request.operation_id().clone()),
        PublicOperation::Query(request) => request.operation_id().cloned(),
        PublicOperation::Family(FamilyOperation::Write(request)) => {
            Some(request.operation_id().clone())
        }
        // The schema surface's mutations are the schema declaration and the
        // migration apply; its reads carry no retry identity.
        PublicOperation::Schema(SchemaOperation::Declare(request)) => {
            Some(request.operation_id().clone())
        }
        PublicOperation::Schema(SchemaOperation::MigrationApply(request)) => {
            Some(request.operation_id().clone())
        }
        PublicOperation::Schema(
            SchemaOperation::Status(_)
            | SchemaOperation::Validate(_)
            | SchemaOperation::MigrationStatus(_)
            | SchemaOperation::MigrationDryRun(_),
        ) => None,
    }
}

/// Returns the audited operation kind of one operation, exhaustively.
fn audit_operation(operation: &PublicOperation) -> AuditOperation {
    match operation {
        PublicOperation::DocumentRead(_) => AuditOperation::DocumentRead,
        PublicOperation::DocumentWrite(_) => AuditOperation::DocumentWrite,
        PublicOperation::Query(_) => AuditOperation::Query,
        PublicOperation::Family(FamilyOperation::Read(_)) => AuditOperation::FamilyRead,
        PublicOperation::Family(FamilyOperation::Write(_)) => AuditOperation::FamilyWrite,
        PublicOperation::Schema(SchemaOperation::Declare(_)) => AuditOperation::SchemaDeclare,
        PublicOperation::Schema(SchemaOperation::Status(_)) => AuditOperation::SchemaStatus,
        PublicOperation::Schema(SchemaOperation::Validate(_)) => AuditOperation::SchemaValidate,
        PublicOperation::Schema(SchemaOperation::MigrationStatus(_)) => {
            AuditOperation::SchemaMigrationStatus
        }
        PublicOperation::Schema(SchemaOperation::MigrationDryRun(_)) => {
            AuditOperation::SchemaMigrationDryRun
        }
        PublicOperation::Schema(SchemaOperation::MigrationApply(_)) => {
            AuditOperation::SchemaMigrationApply
        }
    }
}

/// Maps one Platform refusal to its audit refusal category.
///
/// Exhaustive over the Platform vocabulary: the denial categories this seam
/// never produces today — caller, permission, expiry, confirmation, audit
/// sink, and secret policy, the future Platform authorization surface —
/// collapse to the reserved [`AuditRefusal::Denied`] row.
fn audit_refusal(error: &PlatformError) -> AuditRefusal {
    match error {
        PlatformError::CallerDenied
        | PlatformError::PermissionDenied
        | PlatformError::Expired
        | PlatformError::ConfirmationRequired
        | PlatformError::AuditFailure
        | PlatformError::SecretPolicyViolation => AuditRefusal::Denied,
        PlatformError::TenantDenied => AuditRefusal::TenantDenied,
        PlatformError::InvalidRequest => AuditRefusal::InvalidRequest,
        PlatformError::Conflict { .. } => AuditRefusal::Conflict,
        PlatformError::Deadline { .. } => AuditRefusal::Deadline,
        PlatformError::Cancelled { .. } => AuditRefusal::Cancelled,
        PlatformError::EngineFailure { .. } => AuditRefusal::EngineFailure,
    }
}

/// Maps one Engine failure onto the Platform refusal vocabulary.
///
/// The match is exhaustive over the frozen Engine error set. The mutating
/// identity travels with the bounds and engine-failure rows so a caller can
/// resolve or retry; identity reuse carries the identity the Engine
/// reported, which is the one actually bound.
#[allow(clippy::match_same_arms)] // the frozen rows stay readable per variant
fn map_engine_error(error: EngineError, identity: Option<OperationId>) -> PlatformError {
    match error {
        EngineError::Closed(_) => PlatformError::EngineFailure { operation_id: identity },
        // A scope or snapshot naming another Engine is a binding refusal,
        // not a tenant denial: the caller's request was scoped elsewhere.
        EngineError::SnapshotEngineMismatch | EngineError::ScopeMismatch => {
            PlatformError::EngineFailure { operation_id: identity }
        }
        // The Engine-enforced tenant isolation refused the scope; this is
        // the same rule the adapter checks before dispatch, surfacing from
        // the engine's own snapshot and transaction validation.
        EngineError::SnapshotTenantMismatch => PlatformError::TenantDenied,
        EngineError::OperationIdReuse { operation_id } => {
            PlatformError::Conflict { operation_id: Some(operation_id) }
        }
        EngineError::Conflict => PlatformError::Conflict { operation_id: identity },
        EngineError::InvalidDocument => PlatformError::InvalidRequest,
        EngineError::Deadline => PlatformError::Deadline { operation_id: identity },
        EngineError::Cancelled => PlatformError::Cancelled { operation_id: identity },
        EngineError::StorageFailure
        | EngineError::SnapshotPageLimitExceeded
        | EngineError::SnapshotBoundaryUnavailable => {
            PlatformError::EngineFailure { operation_id: identity }
        }
    }
}

/// Maps one trusted Engine response onto the public response registry.
///
/// Exhaustive with no wildcard arm: a new Engine response variant breaks
/// compilation until it has a public row.
fn public_response(response: EngineResponse) -> PublicResponse {
    match response {
        EngineResponse::Document(document) => PublicResponse::Document(document),
        EngineResponse::Query(result) => PublicResponse::Query(result),
        EngineResponse::Family(result) => PublicResponse::Family(result),
        EngineResponse::Commit(outcome) => PublicResponse::Commit(outcome),
        EngineResponse::Schema(answer) => PublicResponse::Schema(answer),
    }
}
