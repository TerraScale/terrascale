//! Private member identity credential delivery. Mount only on the UID-authenticated admission socket.
use crate::delegated_identity::IdentityVerifier;
use axum::{
    Json, Router,
    extract::{DefaultBodyLimit, State},
    http::StatusCode,
    routing::post,
};
use serde::{Deserialize, Serialize};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt, PermissionsExt};
use std::{
    io::{self, Write},
    path::{Path, PathBuf},
    sync::Arc,
};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Delivery {
    version: String,
    generation: u64,
    secret: String,
}
impl Drop for Delivery {
    fn drop(&mut self) {
        use zeroize::Zeroize;
        self.secret.zeroize();
    }
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Receipt {
    version: String,
    generation: u64,
    state: String,
}

/// Fixed-destination credential installer using the running API's verifier.
pub struct IdentitySecretDelivery {
    verifier: Arc<IdentityVerifier>,
    path: PathBuf,
    journal: PathBuf,
    serial: tokio::sync::Mutex<()>,
}
fn private(path: &Path, directory: bool) -> io::Result<()> {
    let m = std::fs::symlink_metadata(path)?;
    if m.uid() != rustix::process::geteuid().as_raw()
        || m.permissions().mode() & 0o077 != 0
        || m.file_type().is_symlink()
        || (directory && !m.is_dir())
        || (!directory && !m.is_file())
    {
        return Err(io::Error::other("identity material must be owner-only"));
    }
    Ok(())
}
fn replace(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let parent = path.parent().ok_or_else(|| io::Error::other("missing identity directory"))?;
    private(parent, true)?;
    if path.try_exists()? {
        private(path, false)?;
    }
    let temp = path.with_extension("identity-pending");
    match std::fs::remove_file(&temp) {
        Ok(()) => (),
        Err(e) if e.kind() == io::ErrorKind::NotFound => (),
        Err(e) => return Err(e),
    }
    let mut f = std::fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&temp)?;
    f.write_all(bytes)?;
    f.sync_all()?;
    std::fs::rename(&temp, path)?;
    std::fs::File::open(parent)?.sync_all()
}
impl IdentitySecretDelivery {
    /// Builds a receiver for the exact file used by live introspection.
    ///
    /// # Errors
    /// Refuses an unsafe destination or a process outside the engine UID.
    pub fn new(verifier: Arc<IdentityVerifier>) -> io::Result<Self> {
        if rustix::process::geteuid().as_raw() != 62011 {
            return Err(io::Error::other("identity delivery requires engine UID"));
        }
        let path = verifier
            .service_key_file()
            .ok_or_else(|| io::Error::other("identity file is required"))?
            .to_path_buf();
        if !path.is_absolute() {
            return Err(io::Error::other("identity file must be absolute"));
        }
        private(
            path.parent().ok_or_else(|| io::Error::other("identity directory required"))?,
            true,
        )?;
        let journal = path.with_extension("identity-receipt.json");
        Ok(Self { verifier, path, journal, serial: tokio::sync::Mutex::new(()) })
    }
    /// Routes for the admission listener, which must authenticate UID 62010.
    pub fn router(self: Arc<Self>) -> Router {
        Router::new()
            .route("/internal/v1/identity/service-key", post(deliver))
            .layer(DefaultBodyLimit::max(2048))
            .with_state(self)
    }
    async fn install(&self, request: Delivery) -> Result<Receipt, StatusCode> {
        use base64::Engine as _;
        if request.generation == 0
            || request.version.is_empty()
            || request.version.len() > 128
            || !request.version.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
            || request.secret.len() != 43
            || base64::engine::general_purpose::URL_SAFE_NO_PAD
                .decode(&request.secret)
                .map(zeroize::Zeroizing::new)
                .map_or(true, |b| b.len() != 32)
        {
            return Err(StatusCode::BAD_REQUEST);
        }
        let _guard = self.serial.lock().await;
        if self.journal.try_exists().map_err(|_| StatusCode::SERVICE_UNAVAILABLE)? {
            private(&self.journal, false).map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
            let bytes =
                std::fs::read(&self.journal).map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
            let previous: Receipt =
                serde_json::from_slice(&bytes).map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
            if request.generation < previous.generation
                || (request.generation == previous.generation
                    && request.version != previous.version)
            {
                return Err(StatusCode::CONFLICT);
            }
            if request.generation == previous.generation && previous.state == "adopted" {
                let current = zeroize::Zeroizing::new(
                    crate::runtime::read_secret_key(&self.path)
                        .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?,
                );
                if current.as_slice() != request.secret.as_bytes() {
                    return Err(StatusCode::CONFLICT);
                }
                self.verifier
                    .probe_service_key()
                    .await
                    .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
                return Ok(previous);
            }
        }
        let mut receipt = Receipt {
            version: request.version.clone(),
            generation: request.generation,
            state: "pending".into(),
        };
        replace(
            &self.journal,
            &serde_json::to_vec(&receipt).map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?,
        )
        .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
        replace(&self.path, request.secret.as_bytes())
            .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
        self.verifier.probe_service_key().await.map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
        receipt.state = "adopted".into();
        replace(
            &self.journal,
            &serde_json::to_vec(&receipt).map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?,
        )
        .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
        Ok(receipt)
    }
}
async fn deliver(
    State(service): State<Arc<IdentitySecretDelivery>>,
    Json(request): Json<Delivery>,
) -> Result<Json<Receipt>, StatusCode> {
    service.install(request).await.map(Json)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replacement_is_private_and_refuses_symlink_destinations() {
        let root = std::env::temp_dir()
            .join(format!("identity-delivery-{}", terrabase::identity::DatabaseUlid::generate()));
        std::fs::create_dir(&root).unwrap();
        std::fs::set_permissions(&root, std::fs::Permissions::from_mode(0o700)).unwrap();
        let path = root.join("service-key");
        replace(&path, b"first").unwrap();
        replace(&path, b"second").unwrap();
        assert_eq!(std::fs::read(&path).unwrap(), b"second");
        assert_eq!(std::fs::metadata(&path).unwrap().permissions().mode() & 0o777, 0o600);
        let link = root.join("link");
        std::os::unix::fs::symlink(&path, &link).unwrap();
        assert!(replace(&link, b"unexpected").is_err());
        assert_eq!(std::fs::read(&path).unwrap(), b"second");
        std::fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn failed_adoption_persists_revision_and_allows_later_rollback() {
        use base64::Engine as _;
        let root = std::env::temp_dir()
            .join(format!("identity-revision-{}", terrabase::identity::DatabaseUlid::generate()));
        std::fs::create_dir(&root).unwrap();
        std::fs::set_permissions(&root, std::fs::Permissions::from_mode(0o700)).unwrap();
        let path = root.join("key");
        let verifier = IdentityVerifier::new(crate::delegated_identity::IdentityConfig {
            endpoint: "https://127.0.0.1:1/api/internal/identity/introspect".into(),
            issuer: "issuer".into(),
            audience: "audience".into(),
            service_key: "s".repeat(32),
        })
        .unwrap()
        .with_service_key_file(path.clone());
        let service = IdentitySecretDelivery {
            verifier: Arc::new(verifier),
            path: path.clone(),
            journal: root.join("receipt.json"),
            serial: tokio::sync::Mutex::new(()),
        };
        let secret = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode([7u8; 32]);
        let result = service
            .install(Delivery { version: "oci-2".into(), generation: 4, secret: secret.clone() })
            .await;
        assert!(matches!(result, Err(StatusCode::SERVICE_UNAVAILABLE)));
        assert_eq!(std::fs::read(&path).unwrap(), secret.as_bytes());
        let receipt: Receipt =
            serde_json::from_slice(&std::fs::read(&service.journal).unwrap()).unwrap();
        assert_eq!(receipt.state, "pending");
        assert_eq!(receipt.generation, 4);
        assert!(matches!(
            service
                .install(Delivery {
                    version: "oci-1".into(),
                    generation: 3,
                    secret: secret.clone()
                })
                .await,
            Err(StatusCode::CONFLICT)
        ));
        assert!(matches!(
            service
                .install(Delivery {
                    version: "oci-1".into(),
                    generation: 4,
                    secret: secret.clone()
                })
                .await,
            Err(StatusCode::CONFLICT)
        ));
        assert!(matches!(
            service.install(Delivery { version: "oci-1".into(), generation: 5, secret }).await,
            Err(StatusCode::SERVICE_UNAVAILABLE)
        ));
        let receipt: Receipt =
            serde_json::from_slice(&std::fs::read(&service.journal).unwrap()).unwrap();
        assert_eq!(receipt.generation, 5);
        assert_eq!(receipt.version, "oci-1");
        std::fs::remove_dir_all(root).unwrap();
    }
}
