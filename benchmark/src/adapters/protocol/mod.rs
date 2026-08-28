//! Typed Protocol adapter with no generic transport escape hatch.
//!
//! The adapter forwards one closed [`PublicOperation`] at a time to the
//! Platform seam and returns only typed [`PublicResponse`] values and typed
//! [`ProtocolError`] refusals. It accepts an opaque [`CallerContext`] built
//! from untrusted caller evidence, never mints a tenant scope or grant, and
//! performs no authorization: Platform (the W5 adapter packet) alone
//! validates the caller, tenant, permission, expiry, confirmation, audit,
//! and secret-handling policy.
//!
//! The registry is closed by construction: no variant of
//! [`PublicOperation`] carries a route, raw body, generic bytes, arbitrary
//! SQL, a storage or session handle, a permission claim, or a private
//! control-plane operation, so nothing of that shape can be forwarded or
//! rejected at runtime — the type system is the refusal.

use std::fmt;

use crate::{
    adapters::platform::{PlatformAdapter, PlatformError, PlatformPort},
    operations::{FamilyOperation, PublicOperation, PublicResponse, SchemaOperation},
    types::{Cancellation, Deadline, OperationId, TenantId},
};

/// A validated non-secret principal identifier from caller evidence.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct PrincipalId(String);

impl PrincipalId {
    /// Creates a validated principal identifier.
    ///
    /// The identifier is structural evidence, not an authentication result:
    /// only a non-empty, non-whitespace-only text is accepted, and Platform
    /// decides whether the principal is authentic. Finer principal policy
    /// belongs to that later seam.
    ///
    /// # Errors
    ///
    /// Returns [`CallerContextError::EmptyPrincipal`] when the text is empty
    /// or whitespace-only.
    pub fn try_new(value: &str) -> Result<Self, CallerContextError> {
        if value.trim().is_empty() {
            return Err(CallerContextError::EmptyPrincipal);
        }
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical principal text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for PrincipalId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// The closed credential kind accepted as untrusted caller evidence.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CredentialKind {
    /// A bearer credential.
    Bearer,
    /// An API key credential.
    ApiKey,
}

impl fmt::Display for CredentialKind {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::Bearer => "bearer",
            Self::ApiKey => "api-key",
        })
    }
}

/// The fixed prefix marking a redacted credential fingerprint.
const FINGERPRINT_PREFIX: &str = "redacted:";

/// Returns the redacted fingerprint of one credential secret.
///
/// The fingerprint is a fixed-width 64-bit FNV-1a digest of the secret bytes
/// rendered in hexadecimal behind [`FINGERPRINT_PREFIX`]. It exists only so
/// redacted `Debug` and `Display` output can be correlated without exposing
/// the secret: it is not an authentication token, not reversible, and never
/// reconstructs the credential.
fn redacted_fingerprint(secret: &str) -> String {
    const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;
    const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;
    let mut hash = FNV_OFFSET_BASIS;
    for byte in secret.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    format!("{FINGERPRINT_PREFIX}{hash:016x}")
}

/// A private, non-cloneable credential input.
///
/// The secret is readable nowhere: the type has no accessor for it, is not
/// `Clone`, and its `Debug` and `Display` forms show only the credential
/// kind and the redacted fingerprint.
pub struct CredentialInput {
    kind: CredentialKind,
    secret: String,
}

impl CredentialInput {
    /// Creates a structurally validated credential input without exposing
    /// its secret.
    ///
    /// Structural validation only: a non-empty, non-whitespace-only secret
    /// is accepted for either closed credential kind. Verifying the secret
    /// against Platform policy is the W5 Platform Adapter's job.
    ///
    /// # Errors
    ///
    /// Returns [`CallerContextError::EmptyCredential`] when the secret is
    /// empty or whitespace-only.
    pub fn try_new(kind: CredentialKind, secret: &str) -> Result<Self, CallerContextError> {
        if secret.trim().is_empty() {
            return Err(CallerContextError::EmptyCredential);
        }
        Ok(Self { kind, secret: secret.to_owned() })
    }

    /// Returns the credential kind without returning the secret.
    #[must_use]
    pub fn kind(&self) -> CredentialKind {
        self.kind
    }

    /// Returns the redacted non-secret fingerprint used in redacted output.
    fn fingerprint(&self) -> String {
        redacted_fingerprint(&self.secret)
    }
}

impl fmt::Debug for CredentialInput {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("CredentialInput")
            .field("kind", &self.kind)
            .field("secret", &"<redacted>")
            .field("fingerprint", &self.fingerprint())
            .finish()
    }
}

impl fmt::Display for CredentialInput {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "credential {} {}", self.kind, self.fingerprint())
    }
}

/// Untrusted caller evidence before Platform authorization.
///
/// The evidence can state only a principal, a tenant claim, and one closed
/// credential kind. It cannot state permissions, a tenant scope, a grant,
/// or an authorization decision, and it is never authenticated here.
pub struct CallerEvidence {
    principal: PrincipalId,
    tenant: TenantId,
    credential: CredentialInput,
}

impl CallerEvidence {
    /// Creates structurally valid but unauthorized caller evidence.
    ///
    /// The parts arrive through their own validated constructors, so this
    /// constructor repeats only the cheap structural checks; the defensive
    /// tenant check exists because [`CallerContextError::EmptyTenant`] is
    /// part of the closed error vocabulary even though a validated
    /// [`TenantId`] can never be empty today.
    ///
    /// # Errors
    ///
    /// Returns [`CallerContextError::EmptyPrincipal`] or
    /// [`CallerContextError::EmptyTenant`] if a part is structurally empty;
    /// the credential kind is closed and the credential secret was checked
    /// by [`CredentialInput::try_new`].
    pub fn try_new(
        principal: PrincipalId,
        tenant: TenantId,
        credential: CredentialInput,
    ) -> Result<Self, CallerContextError> {
        if principal.as_str().trim().is_empty() {
            return Err(CallerContextError::EmptyPrincipal);
        }
        if tenant.as_str().trim().is_empty() {
            return Err(CallerContextError::EmptyTenant);
        }
        Ok(Self { principal, tenant, credential })
    }

    /// Returns the non-secret principal identifier.
    #[must_use]
    pub fn principal(&self) -> &PrincipalId {
        &self.principal
    }

    /// Returns the untrusted tenant claim.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the credential kind without exposing the credential.
    #[must_use]
    pub fn credential_kind(&self) -> CredentialKind {
        self.credential.kind()
    }
}

impl fmt::Debug for CallerEvidence {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("CallerEvidence")
            .field("principal", &self.principal)
            .field("tenant", &self.tenant)
            .field("credential", &self.credential)
            .finish()
    }
}

impl fmt::Display for CallerEvidence {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "principal={} tenant={} {}", self.principal, self.tenant, self.credential)
    }
}

/// Untrusted caller context accepted by Protocol and Platform seams.
///
/// The context is opaque to callers: it is not `Clone`, exposes only the
/// principal, the tenant claim, and the credential kind, and its `Debug`
/// and `Display` forms are redacted. It authenticates and authorizes
/// nothing, and it never carries a tenant scope or grant.
pub struct CallerContext {
    evidence: CallerEvidence,
}

impl CallerContext {
    /// Creates a caller context from structurally valid untrusted evidence.
    ///
    /// This is structural validation only. Platform authenticates and
    /// authorizes the context, compares its tenant claim against the
    /// authorized scope, and mints any private grant; this constructor
    /// performs none of that.
    ///
    /// # Errors
    ///
    /// Returns [`CallerContextError`] when the evidence is structurally
    /// empty; the evidence constructors make every case unreachable today,
    /// and the result keeps the closed error contract.
    pub fn try_new(evidence: CallerEvidence) -> Result<Self, CallerContextError> {
        if evidence.principal.as_str().trim().is_empty() {
            return Err(CallerContextError::EmptyPrincipal);
        }
        if evidence.tenant.as_str().trim().is_empty() {
            return Err(CallerContextError::EmptyTenant);
        }
        Ok(Self { evidence })
    }

    /// Returns the non-secret principal identifier.
    #[must_use]
    pub fn principal(&self) -> &PrincipalId {
        self.evidence.principal()
    }

    /// Returns the untrusted tenant claim.
    #[must_use]
    pub fn tenant_claim(&self) -> &TenantId {
        self.evidence.tenant()
    }

    /// Returns the credential kind without exposing the credential.
    #[must_use]
    pub fn credential_kind(&self) -> CredentialKind {
        self.evidence.credential_kind()
    }
}

impl fmt::Debug for CallerContext {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.debug_struct("CallerContext").field("evidence", &self.evidence).finish()
    }
}

impl fmt::Display for CallerContext {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}", self.evidence)
    }
}

/// Structural caller-context validation failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CallerContextError {
    /// The principal was empty.
    EmptyPrincipal,
    /// The tenant claim was empty.
    EmptyTenant,
    /// The credential was empty.
    EmptyCredential,
    /// The credential kind is unsupported.
    UnsupportedCredential,
}

/// Typed Protocol forwarding failures.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ProtocolError {
    /// The operation failed structural validation.
    ///
    /// Unreachable from this seam's own checks today: operations arrive
    /// through validated constructors, and the closed registry leaves no
    /// malformed shape to build. The variant carries Platform's typed
    /// malformed-request category.
    InvalidOperation,
    /// The operation is private to the control plane.
    ///
    /// Unreachable at this seam today: the closed registry contains no
    /// private or control-plane variant, so no such operation can be
    /// expressed or forwarded. The variant keeps the refusal vocabulary for
    /// the adapters that accept wider input.
    PrivateOperation,
    /// Generic route or body access is rejected.
    ///
    /// Unreachable at this seam today: no registry variant carries a route,
    /// body, bytes, or storage handle. The variant keeps the refusal
    /// vocabulary for the adapters that accept wider input.
    GenericAccessRejected,
    /// Platform denied the operation.
    ///
    /// This adapter never produces the denial itself: Platform alone
    /// decides caller, tenant, permission, expiry, confirmation, audit, and
    /// secret policy.
    PlatformDenied,
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

/// Returns the deadline and cancellation signal carried by one operation.
///
/// The match is exhaustive with no wildcard arm, so adding a registry
/// variant without a bounded deadline and cancellation breaks compilation.
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

/// Decides the bounded refusal for one operation before any dispatch.
///
/// An already-elapsed deadline or an already-set cancellation signal is
/// refused without contacting Platform, preserving the mutating identity
/// when present. The deadline is checked first, so a doubly-bound operation
/// reports the deadline. The check is bounded and takes no lock.
fn bound_refusal(
    deadline_expired: bool,
    signal_cancelled: bool,
    operation_id: Option<OperationId>,
) -> Option<ProtocolError> {
    if deadline_expired {
        return Some(ProtocolError::Deadline { operation_id });
    }
    if signal_cancelled {
        return Some(ProtocolError::Cancelled { operation_id });
    }
    None
}

/// Provisionally maps one Platform refusal to its Protocol form.
///
/// W5 owns the final pure `map_platform_error` function and its exhaustive
/// red tests. This private mapping exists only so [`ProtocolAdapter::call`]
/// can return the typed Protocol error today; every row follows the
/// contract's Platform-to-Protocol table exactly: all denial detail
/// collapses to [`ProtocolError::PlatformDenied`], a malformed request
/// becomes [`ProtocolError::InvalidOperation`], a conflict surfaces as
/// [`ProtocolError::EngineFailure`] carrying the safe identity, and
/// deadline, cancellation, and engine failures preserve their optional
/// identity. The match is exhaustive with no wildcard arm.
fn forward_platform_error(error: PlatformError) -> ProtocolError {
    match error {
        PlatformError::CallerDenied
        | PlatformError::TenantDenied
        | PlatformError::PermissionDenied
        | PlatformError::Expired
        | PlatformError::ConfirmationRequired
        | PlatformError::AuditFailure
        | PlatformError::SecretPolicyViolation => ProtocolError::PlatformDenied,
        PlatformError::InvalidRequest => ProtocolError::InvalidOperation,
        PlatformError::Conflict { operation_id } => ProtocolError::EngineFailure { operation_id },
        PlatformError::Deadline { operation_id } => ProtocolError::Deadline { operation_id },
        PlatformError::Cancelled { operation_id } => ProtocolError::Cancelled { operation_id },
        PlatformError::EngineFailure { operation_id } => {
            ProtocolError::EngineFailure { operation_id }
        }
    }
}

/// Bridges the W5 Platform adapter onto the private forwarding port.
///
/// [`PlatformAdapter::dispatch`] already has exactly the
/// [`PlatformPort::dispatch`] shape, so the adapter is a port. The bridge
/// lives in this module because the platform files belong to the W5 packet;
/// W5 may move it into the platform wiring without changing behavior.
impl PlatformPort for PlatformAdapter<'_> {
    fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError> {
        PlatformAdapter::dispatch(self, caller, operation)
    }
}

/// A synchronous typed Protocol adapter borrowing Platform wiring.
///
/// The adapter is the only public call seam. It borrows the private
/// Platform port and cannot outlive it; no constructor exposes the port.
pub struct ProtocolAdapter<'platform> {
    platform: &'platform dyn PlatformPort,
}

impl<'platform> ProtocolAdapter<'platform> {
    /// Creates an adapter over one private Platform port.
    ///
    /// Crate-private because [`PlatformPort`] is private to the crate: this
    /// is the primitive constructor behind the acquisition paths.
    #[allow(
        dead_code,
        reason = "exercised through the cfg(test) port double; W5 platform wiring may construct ports directly"
    )]
    #[must_use]
    pub(crate) fn new(platform: &'platform dyn PlatformPort) -> Self {
        Self { platform }
    }

    /// Acquires a Protocol adapter from approved Platform wiring.
    ///
    /// This is the production acquisition path: it borrows the W5-owned
    /// [`PlatformAdapter`], which alone performs authorization. Constructing
    /// that adapter is W5 wiring, so this base proves only that the path
    /// compiles and hides the private port; executing operations through it
    /// lands with the W5 packet.
    #[must_use]
    pub fn from_platform(platform: &'platform PlatformAdapter<'_>) -> Self {
        Self { platform }
    }

    /// Creates an adapter over an in-process Platform port double.
    ///
    /// Test-only by design: it lets the tracer tests observe typed
    /// forwarding without exposing an injectable production authority.
    #[cfg(test)]
    #[must_use]
    pub(crate) fn new_with_platform_port(platform: &'platform dyn PlatformPort) -> Self {
        Self::new(platform)
    }

    /// Forwards one closed typed operation.
    ///
    /// The caller reference is already valid by being borrowed, so no
    /// caller validation happens here: the opaque [`CallerContext`] is
    /// forwarded unchanged and Platform alone decides whether it may act.
    /// Before dispatch, the operation's own bounds are checked once without
    /// a lock: an already-elapsed deadline or an already-set cancellation
    /// signal is refused as [`ProtocolError::Deadline`] or
    /// [`ProtocolError::Cancelled`] preserving the mutating identity. The
    /// operation is then dispatched to the Platform port and any Platform
    /// refusal is mapped to its Protocol row.
    ///
    /// Generic routes, raw bodies, storage handles, and private
    /// control-plane operations have no typed form and so are refused by
    /// the closed registry itself — they can neither reach this method nor
    /// be forwarded through it.
    ///
    /// # Errors
    ///
    /// Returns [`ProtocolError::Deadline`] or [`ProtocolError::Cancelled`]
    /// for an already-expired deadline or already-set cancellation before
    /// dispatch, and otherwise the mapped Platform refusal:
    /// [`ProtocolError::PlatformDenied`] for every denial,
    /// [`ProtocolError::InvalidOperation`] for a malformed request, and
    /// [`ProtocolError::EngineFailure`], [`ProtocolError::Deadline`], or
    /// [`ProtocolError::Cancelled`] with the preserved identity for the
    /// engine-side failures.
    pub fn call(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, ProtocolError> {
        let identity = mutating_identity(&operation);
        let (deadline, cancellation) = bounds(&operation);
        if let Some(error) =
            bound_refusal(deadline.is_expired(), cancellation.is_cancelled(), identity)
        {
            return Err(error);
        }
        self.platform.dispatch(caller, operation).map_err(forward_platform_error)
    }
}

#[cfg(test)]
mod tests;
