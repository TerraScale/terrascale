//! Bounded local broker for customer S3 workload identity credentials.

use std::fmt;
use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use crate::backup::object_store::{Credentials, WorkloadIdentityError, WorkloadIdentityProvider};

const MAX_REQUEST_BYTES: usize = 8 * 1024;
const MAX_RESPONSE_BYTES: usize = 8 * 1024;
const MIN_TTL_SECONDS: u64 = 5;
const MAX_TTL_SECONDS: u64 = 3600;

/// Exact destination facts bound into a broker credential request.
#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct WorkloadIdentityScope {
    /// Customer role or workload identity being assumed.
    pub role: String,
    /// Optional external identifier required by the role trust policy.
    pub external_id: Option<String>,
    /// Canonical HTTPS destination endpoint.
    pub endpoint: String,
    /// Destination bucket.
    pub bucket: String,
    /// SigV4 signing region.
    pub region: String,
    /// Control-plane placement revision.
    pub placement_revision: String,
}

#[derive(serde::Serialize)]
struct CredentialRequest<'a> {
    version: u8,
    scope: &'a WorkloadIdentityScope,
}

#[derive(serde::Deserialize)]
struct CredentialResponse {
    access_key: String,
    secret_key: String,
    session_token: String,
    expires_at_unix_seconds: u64,
}

/// Workload identity provider backed by an authenticated local Unix socket.
#[derive(Clone)]
pub struct UnixWorkloadIdentityProvider {
    socket: PathBuf,
    scope: WorkloadIdentityScope,
}

impl fmt::Debug for UnixWorkloadIdentityProvider {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("UnixWorkloadIdentityProvider")
            .field("socket", &self.socket)
            .field("scope", &self.scope)
            .finish()
    }
}

impl UnixWorkloadIdentityProvider {
    /// Creates a broker provider. The socket path must be absolute and the
    /// scope must contain no control characters or empty required fields.
    pub fn new(
        socket: PathBuf,
        scope: WorkloadIdentityScope,
    ) -> Result<Self, WorkloadIdentityError> {
        if !socket.is_absolute()
            || scope.role.is_empty()
            || scope.endpoint.is_empty()
            || scope.bucket.is_empty()
            || scope.region.is_empty()
            || scope.placement_revision.is_empty()
            || [
                scope.role.as_str(),
                scope.endpoint.as_str(),
                scope.bucket.as_str(),
                scope.region.as_str(),
                scope.placement_revision.as_str(),
            ]
            .into_iter()
            .any(|value| value.chars().any(char::is_control))
        {
            return Err(WorkloadIdentityError::Invalid);
        }
        Ok(Self { socket, scope })
    }
}

#[cfg(unix)]
#[async_trait::async_trait]
impl WorkloadIdentityProvider for UnixWorkloadIdentityProvider {
    async fn credentials(&self) -> Result<Credentials, WorkloadIdentityError> {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        use tokio::net::UnixStream;

        let mut stream = UnixStream::connect(&self.socket)
            .await
            .map_err(|_| WorkloadIdentityError::Unavailable)?;
        let request = serde_json::to_vec(&CredentialRequest { version: 1, scope: &self.scope })
            .map_err(|_| WorkloadIdentityError::Invalid)?;
        if request.len() > MAX_REQUEST_BYTES {
            return Err(WorkloadIdentityError::Invalid);
        }
        stream
            .write_u32(request.len() as u32)
            .await
            .map_err(|_| WorkloadIdentityError::Unavailable)?;
        stream.write_all(&request).await.map_err(|_| WorkloadIdentityError::Unavailable)?;
        let length =
            stream.read_u32().await.map_err(|_| WorkloadIdentityError::Unavailable)? as usize;
        if length == 0 || length > MAX_RESPONSE_BYTES {
            return Err(WorkloadIdentityError::Invalid);
        }
        let mut response = vec![0_u8; length];
        stream.read_exact(&mut response).await.map_err(|_| WorkloadIdentityError::Unavailable)?;
        let response: CredentialResponse =
            serde_json::from_slice(&response).map_err(|_| WorkloadIdentityError::Invalid)?;
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| WorkloadIdentityError::Invalid)?
            .as_secs();
        let ttl = response
            .expires_at_unix_seconds
            .checked_sub(now)
            .ok_or(WorkloadIdentityError::Invalid)?;
        if !(MIN_TTL_SECONDS..=MAX_TTL_SECONDS).contains(&ttl)
            || response.access_key.is_empty()
            || response.secret_key.is_empty()
            || response.session_token.is_empty()
        {
            return Err(WorkloadIdentityError::Invalid);
        }
        Ok(Credentials::temporary(
            response.access_key,
            response.secret_key,
            response.session_token,
            SystemTime::now() + Duration::from_secs(ttl),
        ))
    }
}

#[cfg(not(unix))]
#[async_trait::async_trait]
impl WorkloadIdentityProvider for UnixWorkloadIdentityProvider {
    async fn credentials(&self) -> Result<Credentials, WorkloadIdentityError> {
        Err(WorkloadIdentityError::Unavailable)
    }
}
