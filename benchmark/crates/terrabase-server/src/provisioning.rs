//! Authenticated local ingress to the durable database provisioning authority.

use bytes::Bytes;
use http_body_util::{BodyExt as _, Full, Limited};
use hyper_util::rt::TokioIo;
use serde::{Deserialize, Serialize};
use std::{path::PathBuf, time::Duration};

/// Pinned local authority and server-owned tenant identity.
pub struct ProvisioningClient {
    socket: PathBuf,
    authority_uid: u32,
    environment: String,
    tenant_id: String,
    project_id: String,
    permits: tokio::sync::Semaphore,
}

/// An unavailable authority or an invalid request/response.
#[derive(Debug)]
pub struct ProvisioningError;

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
    /// Pins the local authority UID and deployment-owned identity.
    ///
    /// # Errors
    /// Rejects relative sockets and missing identity.
    pub fn new(
        socket: PathBuf,
        authority_uid: u32,
        environment: String,
        tenant_id: String,
        project_id: String,
    ) -> Result<Self, ProvisioningError> {
        if !socket.is_absolute()
            || [&environment, &tenant_id, &project_id].iter().any(|s| s.is_empty())
        {
            return Err(ProvisioningError);
        }
        Ok(Self {
            socket,
            authority_uid,
            environment,
            tenant_id,
            project_id,
            permits: tokio::sync::Semaphore::new(32),
        })
    }

    /// Submits an idempotent create to the durable GRU authority pipeline.
    ///
    /// # Errors
    /// Fails closed on peer mismatch, timeout, refusal, or inconsistent identity.
    pub async fn create(
        &self,
        name: &str,
        regions: &[String],
        residency: &str,
        operation_id: &str,
    ) -> Result<ProvisioningOperation, ProvisioningError> {
        if name.is_empty() || !valid_operation_id(operation_id) || regions.is_empty() {
            return Err(ProvisioningError);
        }
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError)?;
        let body = serde_json::to_vec(&serde_json::json!({"environment":self.environment,
            "tenant_id":self.tenant_id,"project_id":self.project_id,"name":name,
            "regions":regions,"residency":residency,"operation_id":operation_id}))
        .map_err(|_| ProvisioningError)?;
        let result = tokio::time::timeout(
            Duration::from_secs(10),
            self.exchange("/internal/v1/databases/create", body, hyper::StatusCode::ACCEPTED),
        )
        .await
        .map_err(|_| ProvisioningError)??;
        if result.environment != self.environment
            || result.tenant_id != self.tenant_id
            || result.project_id != self.project_id
            || result.name != name
            || result.operation_id != operation_id
            || result.regions != regions
            || result.residency != residency
            || !canonical_ulid(&result.key_version)
            || !canonical_ulid(&result.database_id)
            || !matches!(result.state.as_str(), "accepted" | "pending")
        {
            return Err(ProvisioningError);
        }
        Ok(result)
    }

    /// Looks up a durable operation within the pinned tenant/project scope.
    ///
    /// # Errors
    /// Rejects unavailable authorities and inconsistent or malformed responses.
    pub async fn status(
        &self,
        operation_id: &str,
    ) -> Result<ProvisioningOperation, ProvisioningError> {
        if !valid_operation_id(operation_id) {
            return Err(ProvisioningError);
        }
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError)?;
        let body = serde_json::to_vec(&serde_json::json!({"environment":self.environment,
            "tenant_id":self.tenant_id,"project_id":self.project_id,"operation_id":operation_id}))
        .map_err(|_| ProvisioningError)?;
        let result = tokio::time::timeout(
            Duration::from_secs(10),
            self.exchange("/internal/v1/databases/status", body, hyper::StatusCode::OK),
        )
        .await
        .map_err(|_| ProvisioningError)??;
        if result.environment != self.environment
            || result.tenant_id != self.tenant_id
            || result.project_id != self.project_id
            || result.operation_id != operation_id
            || !canonical_ulid(&result.database_id)
            || !canonical_ulid(&result.key_version)
            || !matches!(result.state.as_str(), "accepted" | "pending" | "ready" | "failed")
        {
            return Err(ProvisioningError);
        }
        Ok(result)
    }

    async fn exchange(
        &self,
        path: &str,
        body: Vec<u8>,
        status: hyper::StatusCode,
    ) -> Result<ProvisioningOperation, ProvisioningError> {
        let stream =
            tokio::net::UnixStream::connect(&self.socket).await.map_err(|_| ProvisioningError)?;
        if stream.peer_cred().map_err(|_| ProvisioningError)?.uid() != self.authority_uid {
            return Err(ProvisioningError);
        }
        let (mut sender, connection) = hyper::client::conn::http1::Builder::new()
            .max_headers(32)
            .max_buf_size(16 * 1024)
            .handshake(TokioIo::new(stream))
            .await
            .map_err(|_| ProvisioningError)?;
        let request = hyper::Request::builder()
            .method("POST")
            .uri(path)
            .header("host", "localhost")
            .header("content-type", "application/json")
            .body(Full::new(Bytes::from(body)))
            .map_err(|_| ProvisioningError)?;
        let response = async {
            let response = sender.send_request(request).await.map_err(|_| ProvisioningError)?;
            if response.status() != status {
                return Err(ProvisioningError);
            }
            let bytes = Limited::new(response.into_body(), 16 * 1024)
                .collect()
                .await
                .map_err(|_| ProvisioningError)?
                .to_bytes();
            serde_json::from_slice(&bytes).map_err(|_| ProvisioningError)
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
    async fn accepts_durable_pending_but_rejects_ready() {
        for state in ["pending", "ready"] {
            let path =
                std::env::temp_dir().join(format!("tb-create-{}-{state}.sock", std::process::id()));
            let listener = tokio::net::UnixListener::bind(&path).expect("bind");
            let probe = tokio::net::UnixStream::connect(&path).await.expect("probe");
            let uid = probe.peer_cred().expect("credentials").uid();
            let _ = listener.accept().await.expect("probe accept");
            drop(probe);
            let server = tokio::spawn(async move {
                let (stream, _) = listener.accept().await.expect("accept");
                hyper::server::conn::http1::Builder::new().serve_connection(TokioIo::new(stream),
                    hyper::service::service_fn(move |request: hyper::Request<hyper::body::Incoming>| async move {
                        assert_eq!(request.uri().path(), "/internal/v1/databases/create");
                        let bytes = request.into_body().collect().await.expect("body").to_bytes();
                        let request: serde_json::Value = serde_json::from_slice(&bytes).expect("json");
                        assert_eq!(request["tenant_id"], "tenant");
                        assert_eq!(request["environment"], "production");
                        Ok::<_, std::convert::Infallible>(hyper::Response::builder().status(202).body(Full::new(Bytes::from(
                            serde_json::json!({"environment":"production", "tenant_id":"tenant", "project_id":"project", "operation_id":"op", "database_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV", "key_version":"01ARZ3NDEKTSV4RRFFQ69G5FAW",
                            "name":"db","regions":["GRU"],"residency":"BR","state":state}).to_string()))).expect("response"))
                    })).await.expect("serve");
            });
            let client = ProvisioningClient::new(
                path.clone(),
                uid,
                "production".into(),
                "tenant".into(),
                "project".into(),
            )
            .expect("client");
            let result = client.create("db", &["GRU".into()], "BR", "op").await;
            assert_eq!(result.is_ok(), state == "pending");
            server.await.expect("server");
            std::fs::remove_file(path).expect("cleanup");
        }
    }
}
