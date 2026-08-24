//! Platform-owned authorization seam kept separate from the Engine.

#![allow(dead_code)]
#![allow(clippy::missing_errors_doc, clippy::must_use_candidate, clippy::unimplemented)]

use std::marker::PhantomData;

use crate::{
    adapters::protocol::CallerContext,
    engine::EnginePort,
    operations::{PublicOperation, PublicResponse},
};

/// The typed Platform adapter used by the public Protocol adapter.
pub struct PlatformAdapter<'engine> {
    engine: PhantomData<&'engine dyn EnginePort>,
}

impl<'engine> PlatformAdapter<'engine> {
    pub(crate) fn new(authorizer: TrustedAuthorizer, engine: &'engine dyn EnginePort) -> Self {
        let _ = (authorizer, engine);
        unimplemented!("Platform authorization belongs to W5")
    }

    /// Dispatches one typed public operation after Platform validation.
    pub fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError> {
        let _ = (self, caller, operation);
        unimplemented!("Platform authorization belongs to W5")
    }
}

/// Platform validation and Engine forwarding failures.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PlatformError {
    /// Caller identity was denied.
    CallerDenied,
    /// The caller's tenant claim was denied.
    TenantDenied,
    /// The requested public operation was not permitted.
    PermissionDenied,
    /// Caller evidence or authorization expired.
    Expired,
    /// The operation requires explicit confirmation.
    ConfirmationRequired,
    /// Audit recording failed.
    AuditFailure,
    /// Secret handling policy rejected the request.
    SecretPolicyViolation,
    /// The public operation was malformed.
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
struct PlatformGrant(PhantomData<()>);

/// The trusted authorizer supplied by Platform wiring.
pub(crate) struct TrustedAuthorizer(PhantomData<()>);

/// The private forwarding seam from Protocol to Platform.
pub(crate) trait PlatformPort {
    fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError>;
}
