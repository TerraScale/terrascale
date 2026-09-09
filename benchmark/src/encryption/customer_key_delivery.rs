//! Authenticated host delivery for a customer-selected KMS adapter.

use std::fmt;
use std::fs;
use std::io::{Read, Write};
use std::os::unix::fs::{FileTypeExt, MetadataExt};
use std::os::unix::net::UnixStream;
use std::path::{Component, PathBuf};
use std::time::{Duration, Instant};

use super::{
    CustomerKmsAdapter, DatabaseKey, DatabaseKeyLease, EncryptionError, KeyProviderIdentity,
    KeyVersionMetadata, MAX_DATABASE_KEY_LEASE,
};
use crate::regions::{RegionCatalog, RegionCode};

const MAGIC: &[u8; 5] = b"TBCK\x01";
const TIMEOUT: Duration = Duration::from_secs(25);
const MAX_FRAME: usize = 4096;
const KEY_BYTES: usize = 32;

/// Customer KMS adapter bound to one admitted provider configuration.
#[derive(Clone)]
pub struct CustomerKmsKeyDelivery {
    socket: PathBuf,
    orchestrator_uid: u32,
    socket_gid: u32,
    provider: String,
    key_id: String,
    key_region: RegionCode,
    key_endpoint: String,
}

impl fmt::Debug for CustomerKmsKeyDelivery {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("CustomerKmsKeyDelivery(REDACTED)")
    }
}

impl CustomerKmsKeyDelivery {
    /// Binds an admitted customer KMS instance without accepting credentials.
    pub fn new(
        socket: PathBuf,
        orchestrator_uid: u32,
        socket_gid: u32,
        provider: impl Into<String>,
        key_id: impl Into<String>,
        key_region: RegionCode,
        key_endpoint: impl Into<String>,
    ) -> Result<Self, EncryptionError> {
        let source = Self {
            socket,
            orchestrator_uid,
            socket_gid,
            provider: provider.into(),
            key_id: key_id.into(),
            key_region,
            key_endpoint: key_endpoint.into(),
        };
        if !RegionCatalog::production().is_registered(&source.key_region)
            || !valid_field(&source.provider)
            || !valid_field(&source.key_id)
            || !valid_field(&source.key_endpoint)
        {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        source.validate_path()?;
        Ok(source)
    }

    fn validate_path(&self) -> Result<(), EncryptionError> {
        if !self.socket.is_absolute() {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        let mut path = PathBuf::new();
        for component in self.socket.components() {
            match component {
                Component::RootDir | Component::Normal(_) => path.push(component),
                _ => return Err(EncryptionError::ProductionProviderUnavailable),
            }
            let metadata = fs::symlink_metadata(&path).map_err(unavailable)?;
            if metadata.file_type().is_symlink() {
                return Err(EncryptionError::ProductionProviderUnavailable);
            }
            if path == self.socket {
                if !metadata.file_type().is_socket()
                    || metadata.uid() != self.orchestrator_uid
                    || metadata.gid() != self.socket_gid
                    || metadata.mode() & 0o777 != 0o660
                {
                    return Err(EncryptionError::ProductionProviderUnavailable);
                }
            } else if !metadata.is_dir() || metadata.mode() & 0o022 != 0 {
                return Err(EncryptionError::ProductionProviderUnavailable);
            }
        }
        Ok(())
    }
}

impl CustomerKmsAdapter for CustomerKmsKeyDelivery {
    fn identity(&self) -> KeyProviderIdentity {
        KeyProviderIdentity::CustomerKms
    }

    fn lease(
        &self,
        metadata: &KeyVersionMetadata,
        local_region: RegionCode,
    ) -> Result<DatabaseKeyLease, EncryptionError> {
        self.validate_path()?;
        if !RegionCatalog::production().is_registered(&local_region) {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        let scope = metadata.scope();
        let project = scope.project().ok_or(EncryptionError::InvalidKeyMetadata)?;
        let key_region = self.key_region.to_string();
        let tenant = scope.tenant().to_string();
        let project_id = project.to_string();
        let database = scope.database().to_string();
        let version = metadata.version().to_text();
        let local_region = local_region.to_string();
        let fields = [
            self.provider.as_str(),
            self.key_id.as_str(),
            key_region.as_str(),
            self.key_endpoint.as_str(),
            scope.environment(),
            tenant.as_str(),
            project_id.as_str(),
            database.as_str(),
            version.as_str(),
            local_region.as_str(),
            scope.purpose().as_str(),
        ];
        let mut request = MAGIC.to_vec();
        for field in fields {
            if !valid_field(field) {
                return Err(EncryptionError::InvalidKeyMetadata);
            }
            let length = u16::try_from(field.len()).map_err(unavailable)?;
            request.extend_from_slice(&length.to_be_bytes());
            request.extend_from_slice(field.as_bytes());
        }
        let started = Instant::now();
        let mut stream = UnixStream::connect(&self.socket).map_err(unavailable)?;
        let credentials = rustix::net::sockopt::socket_peercred(&stream).map_err(unavailable)?;
        if credentials.uid.as_raw() != self.orchestrator_uid {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        stream.set_read_timeout(Some(TIMEOUT)).map_err(unavailable)?;
        stream.set_write_timeout(Some(TIMEOUT)).map_err(unavailable)?;
        let length = u32::try_from(request.len()).map_err(unavailable)?;
        stream.write_all(&length.to_be_bytes()).map_err(unavailable)?;
        stream.write_all(&request).map_err(unavailable)?;
        let mut length = [0; 4];
        stream.read_exact(&mut length).map_err(unavailable)?;
        let length = usize::try_from(u32::from_be_bytes(length)).map_err(unavailable)?;
        if !(5 + 1 + 4 + 26 + 32..=MAX_FRAME).contains(&length) {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        let mut response = vec![0; length];
        stream.read_exact(&mut response).map_err(unavailable)?;
        if response[..5] != *MAGIC || response[5] != 0 {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        let seconds = u32::from_be_bytes(response[6..10].try_into().map_err(unavailable)?);
        if response[10..36] != *metadata.version().to_text().as_bytes()
            || seconds == 0
            || Duration::from_secs(u64::from(seconds)) > MAX_DATABASE_KEY_LEASE
            || response.len() != 68
        {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        let mut key = DatabaseKey::locked()?;
        key.material_mut().copy_from_slice(&response[36..68]);
        let expires_at = started
            .checked_add(Duration::from_secs(u64::from(seconds)))
            .ok_or(EncryptionError::InvalidKeyLease)?;
        DatabaseKeyLease::from_adapter_until(
            metadata.clone(),
            key,
            expires_at,
            KeyProviderIdentity::CustomerKms,
        )
    }
}

fn valid_field(value: &str) -> bool {
    !value.is_empty() && value.len() <= 128 && !value.as_bytes().contains(&0)
}

fn unavailable<T>(_: T) -> EncryptionError {
    EncryptionError::ProductionProviderUnavailable
}
