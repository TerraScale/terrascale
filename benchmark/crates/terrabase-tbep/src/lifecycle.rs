//! Validation and typed capability boundary for private database lifecycle.
//!
//! The native member owns storage lifecycle state, while the orchestrator
//! owns provider operations such as CDN purge. Until the production runtime
//! supplies that owner, unsupported mutations fail with a typed refusal.

use crate::proto::terrabase::v1 as pb;
use serde::{Deserialize, Serialize};
use std::future::Future;
use std::pin::Pin;

/// Closed lifecycle effect action issued by the current authority lease.
#[allow(missing_docs)]
#[derive(Clone, Copy, Debug, Deserialize, Serialize, Eq, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum LifecycleGrantAction {
    /// Restore an exact recovery point into an admitted target.
    RestoreToTarget,
    EnsureSnapshot,
    VerifySnapshotPlacement,
    CloseDatabase,
    StopNativeBackup,
    Reactivate,
    DeleteOwnedData,
}

/// Strict, scoped lifecycle effect grant payload.
#[allow(missing_docs)]
#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct LifecycleOperationGrant {
    pub version: u64,
    pub domain: String,
    pub environment: String,
    pub tenant: String,
    pub project: String,
    pub database: String,
    pub region: String,
    pub member: String,
    pub action: LifecycleGrantAction,
    pub operation_id: String,
    pub accepted_proof_digest: String,
    pub accepted_placement_revision: u64,
    pub authority_epoch: u64,
    pub authority_lease: String,
    pub issued_at_unix_millis: i64,
    pub expires_at_unix_millis: i64,
}

fn is_lower_hex_digest(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

impl LifecycleOperationGrant {
    /// Validates bounded identity, digest, domain/version, and time ordering.
    pub fn validate(&self, now_unix_millis: i64) -> Result<(), LifecycleError> {
        if self.version != 1
            || self.domain != "terrascale-lifecycle-operation-v1"
            || self.environment.is_empty()
            || self.environment.len() > 255
            || self.tenant.is_empty()
            || self.project.is_empty()
            || self.database.is_empty()
            || self.region.is_empty()
            || self.member.is_empty()
            || self.operation_id.is_empty()
            || self.operation_id.len() > 128
            || !is_lower_hex_digest(&self.accepted_proof_digest)
            || self.accepted_placement_revision == 0
            || self.authority_epoch == 0
            || self.authority_lease.is_empty()
            || self.issued_at_unix_millis > self.expires_at_unix_millis
            || self.expires_at_unix_millis.saturating_sub(self.issued_at_unix_millis) > 300_000
            || now_unix_millis < self.issued_at_unix_millis
            || now_unix_millis >= self.expires_at_unix_millis
        {
            return Err(LifecycleError::Invalid);
        }
        Ok(())
    }
}

/// Native lifecycle port composed by the serving process.
///
/// Implementations own durable idempotency and native side effects. The TBEP
/// adapter only authenticates the channel and validates the signed assignment
/// envelope before calling this port.
pub type LifecycleFuture<'a> =
    Pin<Box<dyn Future<Output = Result<pb::LifecycleControlResponse, String>> + Send + 'a>>;

/// Async native lifecycle owner boundary.
pub trait LifecycleControl: Send + Sync {
    /// Reads verified facts from an admitted restored target.
    fn restore_stats(&self, request: &pb::RestoreStatsRequest) -> RestoreStatsFuture<'_> {
        let _ = request;
        Box::pin(async { Err("native restore stats owner is not composed".into()) })
    }
    /// Restores an exact authenticated recovery point into an admitted target.
    fn restore_to_target(&self, request: &pb::RestoreToTargetRequest) -> LifecycleFuture<'_> {
        let _ = request;
        Box::pin(async { Err("native restore owner is not composed".into()) })
    }
    /// Captures and verifies the native snapshot for this assignment.
    fn ensure_snapshot(
        &self,
        context: &pb::LifecycleControlContext,
        operation: &str,
    ) -> LifecycleFuture<'_>;
    /// Verifies snapshot placement against the admitted assignment.
    fn verify_snapshot_placement(
        &self,
        context: &pb::LifecycleControlContext,
        operation: &str,
    ) -> LifecycleFuture<'_>;
    /// Closes serving while retaining the database.
    fn close_database(
        &self,
        context: &pb::LifecycleControlContext,
        operation: &str,
    ) -> LifecycleFuture<'_>;
    /// Stops this database's native backup stream.
    fn stop_native_backup(
        &self,
        context: &pb::LifecycleControlContext,
        operation: &str,
    ) -> LifecycleFuture<'_>;
    /// Resumes the same retained database.
    fn reactivate(
        &self,
        context: &pb::LifecycleControlContext,
        operation: &str,
    ) -> LifecycleFuture<'_>;
    /// Deletes data owned by this member.
    fn delete_owned_data(
        &self,
        context: &pb::LifecycleControlContext,
        operation: &str,
    ) -> LifecycleFuture<'_>;
}
/// Async result returned by the native restore facts owner.
pub type RestoreStatsFuture<'a> = Pin<Box<dyn Future<Output = Result<pb::RestoreStatsResponse, String>> + Send + 'a>>;

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
        recovery_point_id: String::new(),
        recovery_point_digest: String::new(),
        placement: Vec::new(),
    }
}

/// Reports a successfully applied native lifecycle operation.
pub fn applied(operation_id: &str) -> pb::LifecycleControlResponse {
    pb::LifecycleControlResponse {
        status: pb::LifecycleStatus::Applied as i32,
        operation_id: operation_id.to_owned(),
        detail: String::new(),
        recovery_point_id: String::new(),
        recovery_point_digest: String::new(),
        placement: Vec::new(),
    }
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
