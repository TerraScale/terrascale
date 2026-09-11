//! Bounded local broker for customer S3 workload identity credentials.

use std::fmt;
#[cfg(unix)]
use std::fs;
#[cfg(unix)]
use std::os::unix::fs::{FileTypeExt, MetadataExt};
#[cfg(unix)]
use std::path::Component;
use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use crate::backup::object_store::{Credentials, WorkloadIdentityError, WorkloadIdentityProvider};

const MAX_REQUEST_BYTES: usize = 8 * 1024;
const MAX_RESPONSE_BYTES: usize = 8 * 1024;
const MIN_TTL_SECONDS: u64 = 5;
const MAX_TTL_SECONDS: u64 = 3600;

/// Exact destination facts bound into a broker credential request.
#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct WorkloadIdentityScope {
    /// Deployment environment bound to the admitted database.
    pub environment: String,
    /// Tenant owning the admitted database.
    pub tenant_id: String,
    /// Project owning the admitted database.
    pub project_id: String,
    /// Database receiving the admitted backup.
    pub database_id: String,
    /// Customer role or workload identity being assumed.
    pub role: String,
    /// Canonical HTTPS destination endpoint.
    pub endpoint: String,
    /// Destination bucket.
    pub bucket: String,
    /// `SigV4` signing region.
    pub region: String,
    /// Control-plane placement revision.
    pub placement_revision: String,
}

#[derive(serde::Serialize)]
struct CredentialRequest<'a> {
    version: u8,
    scope: &'a WorkloadIdentityScope,
}

fn encode_request(scope: &WorkloadIdentityScope) -> Result<Vec<u8>, WorkloadIdentityError> {
    let request = serde_json::to_vec(&CredentialRequest { version: 1, scope })
        .map_err(|_| WorkloadIdentityError::Invalid)?;
    if request.len() > MAX_REQUEST_BYTES {
        return Err(WorkloadIdentityError::Invalid);
    }
    Ok(request)
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
    ///
    /// # Errors
    ///
    /// Returns [`WorkloadIdentityError::Invalid`] for an invalid socket or scope.
    pub fn new(
        socket: PathBuf,
        scope: WorkloadIdentityScope,
    ) -> Result<Self, WorkloadIdentityError> {
        if !socket.is_absolute()
            || [
                scope.environment.as_str(),
                scope.tenant_id.as_str(),
                scope.project_id.as_str(),
                scope.database_id.as_str(),
                scope.role.as_str(),
                scope.endpoint.as_str(),
                scope.bucket.as_str(),
                scope.region.as_str(),
                scope.placement_revision.as_str(),
            ]
            .into_iter()
            .any(|value| !valid_scope_field(value))
        {
            return Err(WorkloadIdentityError::Invalid);
        }
        Ok(Self { socket, scope })
    }

    #[cfg(unix)]
    fn validate_path(&self) -> Result<u32, WorkloadIdentityError> {
        if !self.socket.is_absolute() {
            return Err(WorkloadIdentityError::Unavailable);
        }
        let mut path = PathBuf::new();
        let mut parent: Option<fs::Metadata> = None;
        for component in self.socket.components() {
            match component {
                Component::RootDir | Component::Normal(_) => path.push(component),
                _ => return Err(WorkloadIdentityError::Unavailable),
            }
            let metadata =
                fs::symlink_metadata(&path).map_err(|_| WorkloadIdentityError::Unavailable)?;
            if metadata.file_type().is_symlink() {
                return Err(WorkloadIdentityError::Unavailable);
            }
            if path == self.socket {
                if !metadata.file_type().is_socket() || metadata.mode() & 0o777 != 0o660 {
                    return Err(WorkloadIdentityError::Unavailable);
                }
                let parent_metadata = parent.as_ref().ok_or(WorkloadIdentityError::Unavailable)?;
                if parent_metadata.uid() != metadata.uid()
                    || parent_metadata.gid() != metadata.gid()
                    || parent_metadata.mode() & 0o777 != 0o710
                {
                    return Err(WorkloadIdentityError::Unavailable);
                }
                return Ok(metadata.uid());
            }
            if !metadata.is_dir() || metadata.mode() & 0o022 != 0 {
                return Err(WorkloadIdentityError::Unavailable);
            }
            parent = Some(metadata);
        }
        Err(WorkloadIdentityError::Unavailable)
    }
}

fn valid_scope_field(value: &str) -> bool {
    !value.is_empty() && value.len() <= 128 && !value.chars().any(char::is_control)
}

#[cfg(unix)]
#[async_trait::async_trait]
impl WorkloadIdentityProvider for UnixWorkloadIdentityProvider {
    async fn credentials(&self) -> Result<Credentials, WorkloadIdentityError> {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        use tokio::net::UnixStream;

        let socket_uid = self.validate_path()?;
        let mut stream = UnixStream::connect(&self.socket)
            .await
            .map_err(|_| WorkloadIdentityError::Unavailable)?;
        let credentials = rustix::net::sockopt::socket_peercred(&stream)
            .map_err(|_| WorkloadIdentityError::Unavailable)?;
        if credentials.uid.as_raw() != socket_uid {
            return Err(WorkloadIdentityError::Unavailable);
        }
        let request = encode_request(&self.scope)?;
        stream
            .write_u32(u32::try_from(request.len()).map_err(|_| WorkloadIdentityError::Invalid)?)
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

#[cfg(all(test, unix))]
mod tests {
    use super::*;
    use std::fs;
    use std::os::unix::fs::PermissionsExt;
    use std::os::unix::net::UnixListener;

    fn scope() -> WorkloadIdentityScope {
        WorkloadIdentityScope {
            environment: "production".into(),
            tenant_id: "01ARZ3NDEKTSV4RRFFQ69G5FAV".into(),
            project_id: "01ARZ3NDEKTSV4RRFFQ69G5FAW".into(),
            database_id: "01ARZ3NDEKTSV4RRFFQ69G5FAX".into(),
            role: "role".into(),
            endpoint: "https://example.invalid".into(),
            bucket: "bucket".into(),
            region: "region".into(),
            placement_revision: "revision".into(),
        }
    }

    fn fixture_path(name: &str) -> (PathBuf, PathBuf) {
        let root =
            std::env::temp_dir().join(format!("terrabase-workload-{}-{name}", std::process::id()));
        let parent = root.join("protected");
        fs::create_dir_all(&parent).unwrap();
        fs::set_permissions(&parent, fs::Permissions::from_mode(0o710)).unwrap();
        (root, parent.join("broker.sock"))
    }

    #[test]
    fn path_validation_rejects_symlink_components() {
        let (root, socket) = fixture_path("symlink");
        let link = root.join("link");
        std::os::unix::fs::symlink(socket.parent().unwrap(), &link).unwrap();
        let provider =
            UnixWorkloadIdentityProvider::new(link.join("broker.sock"), scope()).unwrap();
        assert_eq!(provider.validate_path(), Err(WorkloadIdentityError::Unavailable));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn path_validation_rejects_unprotected_socket_mode() {
        let (root, socket) = fixture_path("mode");
        let _listener = UnixListener::bind(&socket).unwrap();
        fs::set_permissions(&socket, fs::Permissions::from_mode(0o666)).unwrap();
        let provider = UnixWorkloadIdentityProvider::new(socket, scope()).unwrap();
        assert_eq!(provider.validate_path(), Err(WorkloadIdentityError::Unavailable));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn wire_rejects_retired_external_id() {
        let mut value = serde_json::to_value(scope()).unwrap();
        assert!(value.get("external_id").is_none());
        value["external_id"] = serde_json::json!("external-id");
        assert!(serde_json::from_value::<WorkloadIdentityScope>(value).is_err());
    }

    #[test]
    fn constructor_rejects_unbounded_or_controlled_role() {
        for role in ["bad\nrole".into(), "x".repeat(129)] {
            let mut candidate = scope();
            candidate.role = role;
            assert!(matches!(
                UnixWorkloadIdentityProvider::new(PathBuf::from("/run/broker.sock"), candidate),
                Err(WorkloadIdentityError::Invalid)
            ));
        }
    }
}
