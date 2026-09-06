//! Authenticated local delivery of OCI Vault leases by the host orchestrator.

use std::fmt;
use std::fs;
use std::io::{Read, Write};
use std::os::unix::fs::{FileTypeExt, MetadataExt};
use std::os::unix::net::UnixStream;
use std::path::{Component, Path, PathBuf};
use std::time::{Duration, Instant};

use crate::regions::{RegionCatalog, RegionCode};
use super::{DatabaseKey, DatabaseKeyLease, EncryptionError, KeyProviderIdentity, KeyVersionMetadata, MAX_DATABASE_KEY_LEASE};

const MAGIC: &[u8; 5] = b"TBKL\x01";
const TIMEOUT: Duration = Duration::from_secs(5);
const SUCCESS_BYTES: u32 = 68;

/// A production key source backed by the host's authenticated Vault adapter.
///
/// Every request rechecks the peer and current scope/version authorization.
/// Missing or rejected leases never generate replacement key material.
#[derive(Clone)]
pub struct OciVaultKeyDelivery {
    socket: PathBuf,
    orchestrator_uid: u32,
}

impl fmt::Debug for OciVaultKeyDelivery {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("OciVaultKeyDelivery(REDACTED)")
    }
}

impl OciVaultKeyDelivery {
    /// Validates an absolute, protected socket belonging to the adapter UID.
    ///
    /// # Errors
    /// Returns a redacted refusal for absent, unsafe, or inaccessible paths.
    pub fn new(socket: PathBuf, orchestrator_uid: u32) -> Result<Self, EncryptionError> {
        let provider = Self { socket, orchestrator_uid };
        provider.validate_path()?;
        Ok(provider)
    }

    /// Obtains a short-lived key for exactly the requested durable identity.
    ///
    /// Keys occupy locked, non-dumpable anonymous memory and are zeroed on drop.
    /// Only the orchestrator resolves Vault resource identities and ciphertext.
    ///
    /// # Errors
    /// Fails closed on invalid scope, revoked/missing versions, unauthorized
    /// peers, malformed responses, timeout, or unavailable locked memory.
    pub fn lease(&self, metadata: &KeyVersionMetadata, region: RegionCode) -> Result<DatabaseKeyLease, EncryptionError> {
        self.validate_path()?;
        if !RegionCatalog::production().is_registered(&region) {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        let scope = metadata.scope();
        let project = scope.project().ok_or(EncryptionError::InvalidKeyMetadata)?;
        let fields = [scope.environment().to_owned(), scope.tenant().to_string(), project.to_string(),
            scope.database().to_string(), metadata.version().to_text(), region.to_string(), scope.purpose().as_str().to_owned()];
        let mut request = MAGIC.to_vec();
        for field in fields {
            if field.is_empty() || field.len() > 128 { return Err(EncryptionError::InvalidKeyMetadata); }
            let length = u16::try_from(field.len()).map_err(unavailable)?;
            request.extend_from_slice(&length.to_be_bytes());
            request.extend_from_slice(field.as_bytes());
        }
        let started = Instant::now();
        let mut stream = UnixStream::connect(&self.socket).map_err(unavailable)?;
        let credentials = rustix::net::sockopt::socket_peercred(&stream).map_err(unavailable)?;
        if credentials.uid.as_raw() != self.orchestrator_uid { return Err(EncryptionError::ProductionProviderUnavailable); }
        stream.set_read_timeout(Some(TIMEOUT)).map_err(unavailable)?;
        stream.set_write_timeout(Some(TIMEOUT)).map_err(unavailable)?;
        let length = u32::try_from(request.len()).map_err(unavailable)?;
        stream.write_all(&length.to_be_bytes()).map_err(unavailable)?;
        stream.write_all(&request).map_err(unavailable)?;
        let mut length = [0u8; 4];
        stream.read_exact(&mut length).map_err(unavailable)?;
        if u32::from_be_bytes(length) != SUCCESS_BYTES { return Err(EncryptionError::ProductionProviderUnavailable); }
        let mut header = [0u8; 36];
        stream.read_exact(&mut header).map_err(unavailable)?;
        if &header[..5] != MAGIC || header[5] != 0 || header[10..] != *metadata.version().to_text().as_bytes() {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        let seconds = u32::from_be_bytes(header[6..10].try_into().map_err(unavailable)?);
        let duration = Duration::from_secs(u64::from(seconds));
        if duration.is_zero() || duration > MAX_DATABASE_KEY_LEASE { return Err(EncryptionError::InvalidKeyLease); }
        // Read directly into locked memory: no plaintext response buffer.
        let mut key = DatabaseKey::locked()?;
        stream.read_exact(key.material_mut()).map_err(unavailable)?;
        let remaining = duration.checked_sub(started.elapsed()).filter(|duration| !duration.is_zero()).ok_or(EncryptionError::KeyLeaseExpired)?;
        DatabaseKeyLease::from_adapter(metadata.clone(), key, remaining, KeyProviderIdentity::OciVault)
    }

    fn validate_path(&self) -> Result<(), EncryptionError> {
        if !self.socket.is_absolute() { return Err(EncryptionError::ProductionProviderUnavailable); }
        let mut path = PathBuf::new();
        for component in self.socket.components() {
            match component {
                Component::RootDir | Component::Normal(_) => path.push(component),
                _ => return Err(EncryptionError::ProductionProviderUnavailable),
            }
            validate_component(&path, &self.socket, self.orchestrator_uid)?;
        }
        Ok(())
    }
}

fn validate_component(path: &Path, socket: &Path, uid: u32) -> Result<(), EncryptionError> {
    let metadata = fs::symlink_metadata(path).map_err(unavailable)?;
    if metadata.file_type().is_symlink() { return Err(EncryptionError::ProductionProviderUnavailable); }
    if path == socket {
        if !metadata.file_type().is_socket() || metadata.uid() != uid || metadata.mode() & 0o077 != 0 {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
    } else if !metadata.is_dir() || (metadata.uid() != 0 && metadata.uid() != uid) || metadata.mode() & 0o022 != 0 {
        return Err(EncryptionError::ProductionProviderUnavailable);
    }
    Ok(())
}

fn unavailable<T>(_: T) -> EncryptionError { EncryptionError::ProductionProviderUnavailable }
