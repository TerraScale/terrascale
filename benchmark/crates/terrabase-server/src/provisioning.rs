//! Authenticated local ingress to the durable database provisioning authority.

use bytes::Bytes;
use http_body_util::{BodyExt as _, Full, Limited};
use hyper_util::rt::TokioIo;
use serde::{Deserialize, Serialize};
use std::{path::PathBuf, time::Duration};

/// Pinned local authority and deployment environment; tenant scope is verified per request.
pub struct ProvisioningClient {
    socket: PathBuf,
    authority_uid: u32,
    environment: String,
    permits: tokio::sync::Semaphore,
}

/// An unavailable authority or an invalid request/response.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProvisioningError {
    /// The request does not satisfy the authority policy.
    InvalidRequest,
    /// An immutable database or operation identity conflicts with durable state.
    Conflict,
    /// The authority denied admission.
    Forbidden,
    /// A trustworthy authority result could not be obtained.
    Unavailable,
}

/// The durable authority result. Creation only acknowledges pending work.
#[derive(Deserialize, Serialize, utoipa::ToSchema)]
pub struct ProvisioningOperation {
    /// Deployment environment owning the operation.
    pub environment: String,
    /// Authoritatively bound tenant identity.
    pub tenant_id: String,
    /// Authoritatively bound project identity.
    pub project_id: String,
    /// Stable operation identifier supplied by the caller.
    pub operation_id: String,
    /// Canonical database ULID allocated durably by the authority.
    pub database_id: String,
    /// Parent identity of a provisioned branch.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_database_id: Option<String>,
    /// Persisted encryption key version.
    pub key_version: String,
    /// Immutable database name.
    pub name: String,
    /// Immutable requested serving regions.
    pub regions: Vec<String>,
    /// Immutable residency constraint.
    pub residency: String,
    /// Authority progress; create never implies serving readiness.
    pub state: String,
}

impl ProvisioningClient {
    /// Pins the local authority UID and deployment-owned environment.
    ///
    /// # Errors
    /// Rejects relative sockets and missing identity.
    pub fn new(
        socket: PathBuf,
        authority_uid: u32,
        environment: String,
    ) -> Result<Self, ProvisioningError> {
        if !socket.is_absolute() || environment.is_empty() {
            return Err(ProvisioningError::Unavailable);
        }
        Ok(Self { socket, authority_uid, environment, permits: tokio::sync::Semaphore::new(32) })
    }

    /// Submits an idempotent create to the durable GRU authority pipeline.
    ///
    /// # Errors
    /// Fails closed on peer mismatch, timeout, refusal, or inconsistent identity.
    pub async fn create(
        &self,
        scope: &crate::delegated_identity::VerifiedProjectScope,
        name: &str,
        regions: &[String],
        residency: &str,
        operation_id: &str,
    ) -> Result<ProvisioningOperation, ProvisioningError> {
        if name.is_empty() || !valid_operation_id(operation_id) || regions.is_empty() {
            return Err(ProvisioningError::InvalidRequest);
        }
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError::Unavailable)?;
        let body = serde_json::to_vec(&serde_json::json!({"environment":self.environment,
            "tenant_id":scope.tenant_id,"project_id":scope.project_id,"name":name,
            "regions":regions,"residency":residency,"operation_id":operation_id}))
        .map_err(|_| ProvisioningError::Unavailable)?;
        let result: ProvisioningOperation = tokio::time::timeout(
            Duration::from_secs(10),
            self.exchange("/internal/v1/databases/create", body, hyper::StatusCode::ACCEPTED),
        )
        .await
        .map_err(|_| ProvisioningError::Unavailable)??;
        if result.environment != self.environment
            || result.tenant_id != scope.tenant_id
            || result.project_id != scope.project_id
            || result.name != name
            || result.parent_database_id.is_some()
            || result.operation_id != operation_id
            || result.regions != regions
            || result.residency != residency
            || !canonical_ulid(&result.key_version)
            || !canonical_ulid(&result.database_id)
            || !matches!(result.state.as_str(), "accepted" | "pending")
        {
            return Err(ProvisioningError::Unavailable);
        }
        Ok(result)
    }

    /// Submits a branch allocation bound to its admitted parent's immutable policy.
    ///
    /// # Errors
    /// Rejects a foreign parent, invalid request, unavailable authority, or mismatched response.
    pub async fn branch(
        &self,
        scope: &crate::delegated_identity::VerifiedProjectScope,
        parent: &crate::admission::AdmissionRecord,
        name: &str,
        operation_id: &str,
    ) -> Result<ProvisioningOperation, ProvisioningError> {
        if parent.tenant_id != scope.tenant_id
            || parent.project_id != scope.project_id
            || !valid_operation_id(operation_id)
            || name.is_empty()
        {
            return Err(ProvisioningError::Forbidden);
        }
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError::Unavailable)?;
        let body = serde_json::to_vec(&serde_json::json!({"environment":self.environment,"tenant_id":scope.tenant_id,"project_id":scope.project_id,"name":name,"regions":parent.regions,"residency":parent.residency,"operation_id":operation_id,"parent_database_id":parent.database_id})).map_err(|_| ProvisioningError::Unavailable)?;
        let result: ProvisioningOperation = tokio::time::timeout(
            Duration::from_secs(10),
            self.exchange("/internal/v1/databases/create", body, hyper::StatusCode::ACCEPTED),
        )
        .await
        .map_err(|_| ProvisioningError::Unavailable)??;
        if result.environment != self.environment
            || result.tenant_id != scope.tenant_id
            || result.project_id != scope.project_id
            || result.operation_id != operation_id
            || result.name != name
            || result.parent_database_id.as_deref() != Some(parent.database_id.as_str())
            || result.regions != parent.regions
            || result.residency != parent.residency
            || result.database_id == parent.database_id
            || !canonical_ulid(&result.database_id)
            || !canonical_ulid(&result.key_version)
            || !matches!(result.state.as_str(), "accepted" | "pending")
        {
            return Err(ProvisioningError::Unavailable);
        }
        Ok(result)
    }

    /// Begins or polls authoritative deletion work in the verified database scope.
    ///
    /// # Errors
    /// Rejects invalid identifiers, unavailable control service access, or a response
    /// that does not match the verified scope and requested deletion operation.
    pub async fn deletion(
        &self,
        scope: &crate::delegated_identity::VerifiedProjectScope,
        database_id: &str,
        operation_id: &str,
        begin: bool,
        authorization: Option<&str>,
    ) -> Result<serde_json::Value, ProvisioningError> {
        if !canonical_ulid(database_id)
            || !valid_operation_id(operation_id)
            || !operation_id
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
        {
            return Err(ProvisioningError::InvalidRequest);
        }
        let expected = serde_json::json!({"environment":self.environment,"tenant_id":scope.tenant_id,"project_id":scope.project_id,"database_id":database_id,"operation_id":operation_id});
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError::Unavailable)?;
        let body = serde_json::to_vec(&expected).map_err(|_| ProvisioningError::Unavailable)?;
        let path = if begin {
            "/internal/v1/databases/delete/begin"
        } else {
            "/internal/v1/databases/delete/status"
        };
        let result: serde_json::Value = tokio::time::timeout(
            Duration::from_secs(10),
            self.exchange_authorized(path, body, hyper::StatusCode::OK, authorization),
        )
        .await
        .map_err(|_| ProvisioningError::Unavailable)??;
        if result.get("request") != Some(&expected)
            || !matches!(
                result.get("state").and_then(serde_json::Value::as_str),
                Some("preparing" | "stopping" | "cleanup_pending")
            )
            || result.get("cutoff").and_then(serde_json::Value::as_str).is_none()
            || !result.get("targets").is_some_and(serde_json::Value::is_array)
        {
            return Err(ProvisioningError::Unavailable);
        }
        let targets = result
            .get("targets")
            .and_then(serde_json::Value::as_array)
            .ok_or(ProvisioningError::Unavailable)?;
        if targets.is_empty()
            || targets.len() > 3000
            || targets.iter().any(|target| {
                let Some(stop) = target.get("stop") else {
                    return true;
                };
                stop.get("environment").and_then(serde_json::Value::as_str)
                    != Some(self.environment.as_str())
                    || stop.get("organization_id").and_then(serde_json::Value::as_str)
                        != Some(scope.tenant_id.as_str())
                    || stop.get("project_id").and_then(serde_json::Value::as_str)
                        != Some(scope.project_id.as_str())
                    || stop.get("operation_id").and_then(serde_json::Value::as_str)
                        != Some(operation_id)
                    || stop
                        .get("database_id")
                        .and_then(serde_json::Value::as_str)
                        .is_none_or(|id| !canonical_ulid(id))
                    || (stop.get("database_id").and_then(serde_json::Value::as_str)
                        != Some(database_id)
                        && stop.get("parent_database_id").and_then(serde_json::Value::as_str)
                            != Some(database_id))
            })
        {
            return Err(ProvisioningError::Unavailable);
        }
        Ok(result)
    }

    /// Looks up a durable operation within the verified request tenant/project scope.
    ///
    /// # Errors
    /// Rejects unavailable authorities and inconsistent or malformed responses.
    pub async fn status(
        &self,
        scope: &crate::delegated_identity::VerifiedProjectScope,
        operation_id: &str,
    ) -> Result<ProvisioningOperation, ProvisioningError> {
        if !valid_operation_id(operation_id) {
            return Err(ProvisioningError::InvalidRequest);
        }
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError::Unavailable)?;
        let body = serde_json::to_vec(&serde_json::json!({"environment":self.environment,
            "tenant_id":scope.tenant_id,"project_id":scope.project_id,"operation_id":operation_id}))
        .map_err(|_| ProvisioningError::Unavailable)?;
        let result: ProvisioningOperation = tokio::time::timeout(
            Duration::from_secs(10),
            self.exchange("/internal/v1/databases/status", body, hyper::StatusCode::OK),
        )
        .await
        .map_err(|_| ProvisioningError::Unavailable)??;
        if result.environment != self.environment
            || result.tenant_id != scope.tenant_id
            || result.project_id != scope.project_id
            || result.operation_id != operation_id
            || !canonical_ulid(&result.database_id)
            || !canonical_ulid(&result.key_version)
            || !matches!(result.state.as_str(), "accepted" | "pending" | "ready" | "failed")
        {
            return Err(ProvisioningError::Unavailable);
        }
        Ok(result)
    }

    /// Discovers committed operations without requiring the original submission response.
    pub(crate) async fn list_operations(
        &self,
        scope: &crate::delegated_identity::VerifiedProjectScope,
        after: Option<&str>,
        limit: usize,
    ) -> Result<crate::api::operations::OperationPage, ProvisioningError> {
        if !(1..=50).contains(&limit) || after.is_some_and(|value| value.len() > 256) {
            return Err(ProvisioningError::InvalidRequest);
        }
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError::Unavailable)?;
        let body = serde_json::to_vec(&serde_json::json!({
            "environment":self.environment,"tenant_id":scope.tenant_id,
            "project_id":scope.project_id,"after":after.unwrap_or(""),"limit":limit,
        }))
        .map_err(|_| ProvisioningError::Unavailable)?;
        #[derive(Deserialize)]
        struct ScopedPage {
            environment: String,
            tenant_id: String,
            project_id: String,
            #[serde(flatten)]
            page: crate::api::operations::OperationPage,
        }
        let result: ScopedPage = tokio::time::timeout(
            Duration::from_secs(10),
            self.exchange("/internal/v1/operations/list", body, hyper::StatusCode::OK),
        )
        .await
        .map_err(|_| ProvisioningError::Unavailable)??;
        if result.environment != self.environment
            || result.tenant_id != scope.tenant_id
            || result.project_id != scope.project_id
        {
            return Err(ProvisioningError::Unavailable);
        }
        let page = result.page;
        if page.operations.len() > limit
            || page.covered_kinds != ["database_create", "branch_create"]
            || page
                .next_cursor
                .as_ref()
                .is_some_and(|cursor| cursor.len() > 256 || page.operations.len() != limit)
            || page.operations.iter().any(|operation| {
                !valid_operation_id(&operation.operation_id)
                    || !canonical_ulid(&operation.database_id)
                    || !matches!(operation.kind.as_str(), "database_create" | "branch_create")
                    || !matches!(
                        (operation.state.as_str(), operation.stage.as_str()),
                        ("queued", "accepted")
                            | ("running", "members_admitted" | "placement_pending")
                            | ("succeeded", "ready")
                    )
                    || operation.cancellable
                    || operation.revision == 0
            })
        {
            return Err(ProvisioningError::Unavailable);
        }
        Ok(page)
    }

    async fn exchange<T: serde::de::DeserializeOwned>(
        &self,
        path: &str,
        body: Vec<u8>,
        status: hyper::StatusCode,
    ) -> Result<T, ProvisioningError> {
        self.exchange_authorized(path, body, status, None).await
    }

    async fn exchange_authorized<T: serde::de::DeserializeOwned>(
        &self,
        path: &str,
        body: Vec<u8>,
        status: hyper::StatusCode,
        authorization: Option<&str>,
    ) -> Result<T, ProvisioningError> {
        let stream = tokio::net::UnixStream::connect(&self.socket)
            .await
            .map_err(|_| ProvisioningError::Unavailable)?;
        if stream.peer_cred().map_err(|_| ProvisioningError::Unavailable)?.uid()
            != self.authority_uid
        {
            return Err(ProvisioningError::Unavailable);
        }
        let (mut sender, connection) = hyper::client::conn::http1::Builder::new()
            .max_headers(32)
            .max_buf_size(16 * 1024)
            .handshake(TokioIo::new(stream))
            .await
            .map_err(|_| ProvisioningError::Unavailable)?;
        let mut request = hyper::Request::builder()
            .method("POST")
            .uri(path)
            .header("host", "localhost")
            .header("content-type", "application/json");
        if let Some(authorization) = authorization {
            request = request.header("authorization", authorization);
        }
        let request = request
            .body(Full::new(Bytes::from(body)))
            .map_err(|_| ProvisioningError::Unavailable)?;
        let response = async {
            let response =
                sender.send_request(request).await.map_err(|_| ProvisioningError::Unavailable)?;
            if response.status() != status {
                return Err(match response.status() {
                    hyper::StatusCode::BAD_REQUEST => ProvisioningError::InvalidRequest,
                    hyper::StatusCode::CONFLICT => ProvisioningError::Conflict,
                    hyper::StatusCode::FORBIDDEN => ProvisioningError::Forbidden,
                    _ => ProvisioningError::Unavailable,
                });
            }
            let bytes = Limited::new(response.into_body(), 16 * 1024)
                .collect()
                .await
                .map_err(|_| ProvisioningError::Unavailable)?
                .to_bytes();
            serde_json::from_slice(&bytes).map_err(|_| ProvisioningError::Unavailable)
        };
        tokio::pin!(response, connection);
        tokio::select! { biased; result = &mut response => result, _ = &mut connection => response.await }
    }
}

fn valid_operation_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.bytes().all(|byte| (0x21..=0x7e).contains(&byte))
}

fn canonical_ulid(value: &str) -> bool {
    value.len() == 26
        && value.as_bytes()[0] <= b'7'
        && value.bytes().all(|c| b"0123456789ABCDEFGHJKMNPQRSTVWXYZ".contains(&c))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn preserves_authority_failure_class_without_reading_error_details() {
        for (status, expected) in [
            (400, ProvisioningError::InvalidRequest),
            (409, ProvisioningError::Conflict),
            (403, ProvisioningError::Forbidden),
            (500, ProvisioningError::Unavailable),
        ] {
            let path = std::env::temp_dir()
                .join(format!("tb-create-error-{}-{status}.sock", std::process::id()));
            let listener = tokio::net::UnixListener::bind(&path).expect("bind");
            let probe = tokio::net::UnixStream::connect(&path).await.expect("probe");
            let uid = probe.peer_cred().expect("credentials").uid();
            let _ = listener.accept().await.expect("probe accept");
            drop(probe);
            let server = tokio::spawn(async move {
                let (stream, _) = listener.accept().await.expect("accept");
                let _ = hyper::server::conn::http1::Builder::new()
                    .serve_connection(
                        TokioIo::new(stream),
                        hyper::service::service_fn(
                            move |_request: hyper::Request<hyper::body::Incoming>| async move {
                                Ok::<_, std::convert::Infallible>(
                                    hyper::Response::builder()
                                        .status(status)
                                        .body(Full::new(Bytes::from_static(
                                            b"private authority diagnostics",
                                        )))
                                        .expect("response"),
                                )
                            },
                        ),
                    )
                    .await;
            });
            let client =
                ProvisioningClient::new(path.clone(), uid, "production".into()).expect("client");
            let scope = crate::delegated_identity::VerifiedProjectScope {
                tenant_id: "tenant".into(),
                project_id: "project".into(),
            };
            assert_eq!(client.status(&scope, "op").await.err(), Some(expected));
            server.await.expect("server");
            std::fs::remove_file(path).expect("cleanup");
        }
    }

    #[tokio::test]
    async fn accepts_durable_pending_but_rejects_ready() {
        for (state, polling, response_tenant) in [
            ("pending", false, "tenant"),
            ("ready", false, "tenant"),
            ("ready", true, "tenant"),
            ("pending", true, "other-tenant"),
        ] {
            let path = std::env::temp_dir().join(format!(
                "tb-create-{}-{state}-{polling}-{response_tenant}.sock",
                std::process::id()
            ));
            let listener = tokio::net::UnixListener::bind(&path).expect("bind");
            let probe = tokio::net::UnixStream::connect(&path).await.expect("probe");
            let uid = probe.peer_cred().expect("credentials").uid();
            let _ = listener.accept().await.expect("probe accept");
            drop(probe);
            let server = tokio::spawn(async move {
                let (stream, _) = listener.accept().await.expect("accept");
                hyper::server::conn::http1::Builder::new().serve_connection(TokioIo::new(stream),
                    hyper::service::service_fn(move |request: hyper::Request<hyper::body::Incoming>| async move {
                        assert_eq!(request.uri().path(), if polling { "/internal/v1/databases/status" } else { "/internal/v1/databases/create" });
                        let bytes = request.into_body().collect().await.expect("body").to_bytes();
                        let request: serde_json::Value = serde_json::from_slice(&bytes).expect("json");
                        assert_eq!(request["tenant_id"], "tenant");
                        assert_eq!(request["environment"], "production");
                        Ok::<_, std::convert::Infallible>(hyper::Response::builder().status(if polling { 200 } else { 202 }).body(Full::new(Bytes::from(
                            serde_json::json!({"environment":"production", "tenant_id":response_tenant, "project_id":"project", "operation_id":"op", "database_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV", "key_version":"01ARZ3NDEKTSV4RRFFQ69G5FAW",
                            "name":"db","regions":["GRU"],"residency":"BR","state":state}).to_string()))).expect("response"))
                    })).await.expect("serve");
            });
            let client =
                ProvisioningClient::new(path.clone(), uid, "production".into()).expect("client");
            let scope = crate::delegated_identity::VerifiedProjectScope {
                tenant_id: "tenant".into(),
                project_id: "project".into(),
            };
            let result = if polling {
                client.status(&scope, "op").await
            } else {
                client.create(&scope, "db", &["GRU".into()], "BR", "op").await
            };
            assert_eq!(
                result.is_ok(),
                response_tenant == "tenant" && (polling || state == "pending")
            );
            server.await.expect("server");
            std::fs::remove_file(path).expect("cleanup");
        }
    }
}
