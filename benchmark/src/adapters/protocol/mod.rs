//! Typed Protocol adapter declarations with no generic transport escape hatch.

#![allow(dead_code)]
#![allow(
    clippy::missing_errors_doc,
    clippy::missing_fields_in_debug,
    clippy::must_use_candidate,
    clippy::needless_pass_by_value,
    clippy::unimplemented
)]

use std::{fmt, marker::PhantomData};

use crate::{
    adapters::platform::{PlatformAdapter, PlatformPort},
    operations::{PublicOperation, PublicResponse},
    types::TenantId,
};

/// A validated non-secret principal identifier from caller evidence.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct PrincipalId(String);

impl PrincipalId {
    /// Creates a validated principal identifier.
    pub fn try_new(value: &str) -> Result<Self, CallerContextError> {
        let _ = value;
        unimplemented!("caller validation belongs to a later behavior packet")
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

/// A private, non-cloneable credential input.
pub struct CredentialInput {
    kind: CredentialKind,
    secret: String,
}

impl CredentialInput {
    /// Creates a credential input without exposing its secret.
    pub fn try_new(kind: CredentialKind, secret: &str) -> Result<Self, CallerContextError> {
        let _ = (kind, secret);
        unimplemented!("credential validation belongs to a later behavior packet")
    }

    /// Returns the credential kind without returning the secret.
    pub fn kind(&self) -> CredentialKind {
        self.kind
    }
}

impl fmt::Debug for CredentialInput {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("CredentialInput")
            .field("kind", &self.kind)
            .field("secret", &"<redacted>")
            .finish()
    }
}

/// Untrusted caller evidence before Platform authorization.
pub struct CallerEvidence {
    principal: PrincipalId,
    tenant: TenantId,
    credential: CredentialInput,
}

impl CallerEvidence {
    /// Creates structurally valid but unauthorised caller evidence.
    pub fn try_new(
        principal: PrincipalId,
        tenant: TenantId,
        credential: CredentialInput,
    ) -> Result<Self, CallerContextError> {
        let _ = (principal, tenant, credential);
        unimplemented!("caller evidence validation belongs to a later behavior packet")
    }

    /// Returns the non-secret principal identifier.
    pub fn principal(&self) -> &PrincipalId {
        &self.principal
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the credential kind without exposing the credential.
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
            .field("credential_kind", &self.credential_kind())
            .finish()
    }
}

/// Untrusted caller context accepted by Protocol and Platform seams.
pub struct CallerContext {
    evidence: CallerEvidence,
}

impl CallerContext {
    /// Creates a caller context from untrusted evidence.
    pub fn try_new(evidence: CallerEvidence) -> Result<Self, CallerContextError> {
        let _ = evidence;
        unimplemented!("caller context validation belongs to a later behavior packet")
    }

    /// Returns the non-secret principal identifier.
    pub fn principal(&self) -> &PrincipalId {
        self.evidence.principal()
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        self.evidence.tenant()
    }

    /// Returns the credential kind without exposing the credential.
    pub fn credential_kind(&self) -> CredentialKind {
        self.evidence.credential_kind()
    }
}

impl fmt::Debug for CallerContext {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("CallerContext")
            .field("principal", &self.principal())
            .field("tenant_claim", &self.tenant_claim())
            .field("credential_kind", &self.credential_kind())
            .finish()
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
    InvalidOperation,
    /// The operation is private to the control plane.
    PrivateOperation,
    /// Generic route or body access is rejected.
    GenericAccessRejected,
    /// Platform denied the operation.
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

/// A synchronous typed Protocol adapter borrowing Platform wiring.
pub struct ProtocolAdapter<'platform> {
    platform: PhantomData<&'platform dyn PlatformPort>,
}

impl<'platform> ProtocolAdapter<'platform> {
    pub(crate) fn new(platform: &'platform dyn PlatformPort) -> Self {
        let _ = platform;
        unimplemented!("Protocol forwarding belongs to W3-3")
    }

    /// Acquires a Protocol adapter from approved Platform wiring.
    pub fn from_platform(platform: &'platform PlatformAdapter<'_>) -> Self {
        let _ = platform;
        unimplemented!("Protocol forwarding belongs to W3-3")
    }

    #[cfg(test)]
    pub(crate) fn new_with_platform_port(platform: &'platform dyn PlatformPort) -> Self {
        Self::new(platform)
    }

    /// Forwards one closed typed operation.
    pub fn call(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, ProtocolError> {
        let _ = (self, caller, operation);
        unimplemented!("Protocol forwarding belongs to W3-3")
    }
}
