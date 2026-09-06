//! Authenticated local ingress to the durable database provisioning authority.

use std::{path::PathBuf, time::Duration};
use bytes::Bytes;
use http_body_util::{BodyExt as _, Full, Limited};
use hyper_util::rt::TokioIo;
use serde::{Deserialize, Serialize};

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
#[derive(Deserialize, Serialize)]
pub struct ProvisioningOperation {
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
    pub fn new(socket: PathBuf, authority_uid: u32, environment: String, tenant_id: String, project_id: String) -> Result<Self, ProvisioningError> {
        if !socket.is_absolute() || [&environment, &tenant_id, &project_id].iter().any(|s| s.is_empty()) {
            return Err(ProvisioningError);
        }
        Ok(Self { socket, authority_uid, environment, tenant_id, project_id, permits: tokio::sync::Semaphore::new(32) })
    }

    /// Submits an idempotent create to the durable GRU authority pipeline.
    ///
    /// # Errors
    /// Fails closed on peer mismatch, timeout, refusal, or inconsistent identity.
    pub async fn create(&self, name: &str, regions: &[String], residency: &str, operation_id: &str) -> Result<ProvisioningOperation, ProvisioningError> {
        if name.is_empty() || operation_id.is_empty() || operation_id.len() > 128 || regions.is_empty() {
            return Err(ProvisioningError);
        }
        let _permit = self.permits.try_acquire().map_err(|_| ProvisioningError)?;
        let body = serde_json::to_vec(&serde_json::json!({"environment":self.environment,
            "tenant_id":self.tenant_id,"project_id":self.project_id,"name":name,
            "regions":regions,"residency":residency,"operation_id":operation_id})).map_err(|_| ProvisioningError)?;
        let result = tokio::time::timeout(Duration::from_secs(10), self.exchange(body)).await.map_err(|_| ProvisioningError)??;
        if result.name != name || result.operation_id != operation_id || result.regions != regions
            || result.residency != residency || !canonical_ulid(&result.key_version)
            || !canonical_ulid(&result.database_id)
            || !matches!(result.state.as_str(), "accepted" | "pending") {
            return Err(ProvisioningError);
        }
        Ok(result)
    }

    async fn exchange(&self, body: Vec<u8>) -> Result<ProvisioningOperation, ProvisioningError> {
        let stream = tokio::net::UnixStream::connect(&self.socket).await.map_err(|_| ProvisioningError)?;
        if stream.peer_cred().map_err(|_| ProvisioningError)?.uid() != self.authority_uid { return Err(ProvisioningError); }
        let (mut sender, connection) = hyper::client::conn::http1::Builder::new().max_headers(32).max_buf_size(16 * 1024)
            .handshake(TokioIo::new(stream)).await.map_err(|_| ProvisioningError)?;
        let request = hyper::Request::builder().method("POST").uri("/internal/v1/databases/create")
            .header("host", "localhost").header("content-type", "application/json")
            .body(Full::new(Bytes::from(body))).map_err(|_| ProvisioningError)?;
        let response = async {
            let response = sender.send_request(request).await.map_err(|_| ProvisioningError)?;
            if response.status() != hyper::StatusCode::ACCEPTED { return Err(ProvisioningError); }
            let bytes = Limited::new(response.into_body(), 16 * 1024).collect().await.map_err(|_| ProvisioningError)?.to_bytes();
            serde_json::from_slice(&bytes).map_err(|_| ProvisioningError)
        };
        tokio::pin!(response, connection);
        tokio::select! { biased; result = &mut response => result, _ = &mut connection => response.await }
    }
}

fn canonical_ulid(value: &str) -> bool {
    value.len() == 26 && value.as_bytes()[0] <= b'7'
        && value.bytes().all(|c| b"0123456789ABCDEFGHJKMNPQRSTVWXYZ".contains(&c))
}
