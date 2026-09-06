//! Validation and typed capability boundary for private database lifecycle.
//!
//! The native member owns storage lifecycle state, while the orchestrator
//! owns provider operations such as CDN purge. Until the production runtime
//! supplies that owner, unsupported mutations fail with a typed refusal.

use crate::proto::terrabase::v1 as pb;

/// Native lifecycle port composed by the serving process.
///
/// Implementations own durable idempotency and native side effects. The TBEP
/// adapter only authenticates the channel and validates the signed assignment
/// envelope before calling this port.
pub trait LifecycleControl: Send + Sync {
    fn ensure_snapshot(&self, context: &pb::LifecycleControlContext, operation: &str) -> Result<(), String>;
    fn verify_snapshot_placement(&self, context: &pb::LifecycleControlContext, operation: &str) -> Result<(), String>;
    fn close_database(&self, context: &pb::LifecycleControlContext, operation: &str) -> Result<(), String>;
    fn stop_native_backup(&self, context: &pb::LifecycleControlContext, operation: &str) -> Result<(), String>;
    fn reactivate(&self, context: &pb::LifecycleControlContext, operation: &str) -> Result<(), String>;
    fn delete_owned_data(&self, context: &pb::LifecycleControlContext, operation: &str) -> Result<(), String>;
}

/// Bounded lifecycle refusal categories.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LifecycleError {
    /// The request is incomplete or exceeds a contract bound.
    Invalid,
    /// The signed accepted placement does not authorize the request.
    AuthorityMismatch,
    /// The native owner has not been composed into this server.
    Unavailable,
}

/// Validates the common lifecycle identity before any side effect.
///
/// Signature verification is deliberately owned by the placement journal;
/// this function only rejects absent proof and malformed identity. Callers
/// must compare the decoded proof with the durable accepted assignment.
pub fn validate_context(
    context: Option<&pb::LifecycleControlContext>,
) -> Result<(), LifecycleError> {
    let Some(context) = context else { return Err(LifecycleError::Invalid) };
    let values = [
        &context.environment,
        &context.tenant,
        &context.project,
        &context.database,
        &context.region,
        &context.member,
        &context.authority_lease,
    ];
    if values.iter().any(|value| value.is_empty() || value.len() > 255)
        || context.placement_revision == 0
        || context.authority_epoch == 0
        || context.authority_signature.is_empty()
        || context.authority_signature.len() > 256 * 1024
    {
        return Err(LifecycleError::Invalid);
    }
    Ok(())
}

/// Validates a lifecycle mutation and returns its stable operation identity.
pub fn validate_request(request: &pb::LifecycleControlRequest) -> Result<&str, LifecycleError> {
    validate_context(request.context.as_ref())?;
    if request.operation_id.is_empty() || request.operation_id.len() > 128 {
        return Err(LifecycleError::Invalid);
    }
    Ok(&request.operation_id)
}

/// Reports a capability which is not yet connected to the native runtime.
pub fn unavailable(operation_id: &str) -> pb::LifecycleControlResponse {
    pb::LifecycleControlResponse {
        status: pb::LifecycleStatus::Unavailable as i32,
        operation_id: operation_id.to_owned(),
        detail: "native lifecycle owner is not composed".into(),
    }
}

pub fn applied(operation_id: &str) -> pb::LifecycleControlResponse {
    pb::LifecycleControlResponse { status: pb::LifecycleStatus::Applied as i32, operation_id: operation_id.to_owned(), detail: String::new() }
}

/// Validates a same-database reactivation request before admission.
pub fn validate_reactivate(
    request: &pb::LifecycleReactivateRequest,
) -> Result<&str, LifecycleError> {
    let Some(inner) = request.request.as_ref() else { return Err(LifecycleError::Invalid) };
    validate_request(inner)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request() -> pb::LifecycleControlRequest {
        pb::LifecycleControlRequest {
            context: Some(pb::LifecycleControlContext {
                environment: "production".into(),
                tenant: "tenant".into(),
                project: "project".into(),
                database: "database".into(),
                region: "GRU".into(),
                member: "member".into(),
                placement_revision: 7,
                authority_epoch: 3,
                authority_lease: "lease".into(),
                authority_signature: vec![1],
            }),
            operation_id: "operation".into(),
        }
    }

    #[test]
    fn accepts_complete_identity_and_preserves_retry_key() {
        let request = request();
        assert_eq!(validate_request(&request), Ok("operation"));
    }

    #[test]
    fn refuses_missing_authority_proof() {
        let mut request = request();
        request.context.as_mut().unwrap().authority_signature.clear();
        assert_eq!(validate_request(&request), Err(LifecycleError::Invalid));
    }

    #[test]
    fn unavailable_response_is_typed_and_echoes_operation() {
        let response = unavailable("op");
        assert_eq!(response.status, pb::LifecycleStatus::Unavailable as i32);
        assert_eq!(response.operation_id, "op");
    }
}
