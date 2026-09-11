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

/// Customer KMS adapter bound to one admitted provider configuration.
#[derive(Clone)]
pub struct CustomerKmsKeyDelivery {
    socket: PathBuf,
    orchestrator_uid: u32,
    socket_gid: u32,
    provider: String,
    key_id: String,
    key_region: String,
    key_endpoint: String,
}

impl fmt::Debug for CustomerKmsKeyDelivery {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("CustomerKmsKeyDelivery(REDACTED)")
    }
}

impl CustomerKmsKeyDelivery {
    /// Binds an admitted customer KMS instance without accepting credentials.
    ///
    /// # Errors
    ///
    /// Returns an error when configuration fields or the authenticated socket
    /// path are invalid or unavailable.
    pub fn new(
        socket: PathBuf,
        orchestrator_uid: u32,
        socket_gid: u32,
        provider: impl Into<String>,
        key_id: impl Into<String>,
        key_region: impl Into<String>,
        key_endpoint: impl Into<String>,
    ) -> Result<Self, EncryptionError> {
        let source = Self {
            socket,
            orchestrator_uid,
            socket_gid,
            provider: provider.into(),
            key_id: key_id.into(),
            key_region: key_region.into(),
            key_endpoint: key_endpoint.into(),
        };
        if !valid_region(&source.key_region)
            || !valid_field(&source.provider)
            || !valid_field(&source.key_id)
            || (!source.key_endpoint.is_empty() && !valid_field(&source.key_endpoint))
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
        let key_region = self.key_region.clone();
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
        for (index, field) in fields.into_iter().enumerate() {
            if !(valid_field(field) || index == 3 && field.is_empty()) {
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

fn valid_region(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 64
        && value.bytes().all(|byte| byte.is_ascii_alphanumeric() || b"-_.".contains(&byte))
}

fn unavailable<T>(_: T) -> EncryptionError {
    EncryptionError::ProductionProviderUnavailable
}

#[cfg(test)]
mod tests {
    use super::valid_region;

    #[test]
    fn runtime_producer_matches_broker_golden_with_omitted_endpoint() {
        use super::*;
        use crate::encryption::{DatabaseKeyVersion, KeyPurpose, KeyScope};
        use crate::identity::{DatabaseUlid, ProjectUlid, TenantUlid};
        use std::os::unix::fs::PermissionsExt;
        use std::os::unix::net::UnixListener;
        let root = std::env::current_dir()
            .unwrap()
            .join(format!(".customer-frame-{}", std::process::id()));
        fs::create_dir(&root).unwrap();
        fs::set_permissions(&root, fs::Permissions::from_mode(0o710)).unwrap();
        let socket = root.join("broker.sock");
        let listener = UnixListener::bind(&socket).unwrap();
        fs::set_permissions(&socket, fs::Permissions::from_mode(0o660)).unwrap();
        let stat = fs::metadata(&socket).unwrap();
        let adapter = CustomerKmsKeyDelivery::new(
            socket,
            stat.uid(),
            stat.gid(),
            "aws-kms",
            "arn:aws:kms:sa-east-1:123456789012:key/12345678-1234-1234-1234-123456789012",
            "sa-east-1",
            String::default(),
        )
        .unwrap();
        let capture = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut prefix = [0; 4];
            stream.read_exact(&mut prefix).unwrap();
            let mut body = vec![0; u32::from_be_bytes(prefix) as usize];
            stream.read_exact(&mut body).unwrap();
            stream.write_all(&0_u32.to_be_bytes()).unwrap();
            [prefix.as_slice(), body.as_slice()].concat()
        });
        let metadata = KeyVersionMetadata::new(
            KeyScope::new_with_project(
                "production",
                TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap(),
                ProjectUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAW").unwrap(),
                DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAX").unwrap(),
                KeyPurpose::Data,
            )
            .unwrap(),
            DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAY").unwrap(),
        );
        assert!(adapter.lease(&metadata, RegionCode::from_text("GRU").unwrap()).is_err());
        let frame = capture.join().unwrap();
        fs::remove_dir_all(root).unwrap();
        assert_eq!(hex::encode(frame), include_str!("testdata/rust_customer_key_frame.hex").trim());
    }

    #[test]
    fn accepts_cloud_provider_regions() {
        assert!(valid_region("us-east-1"));
        assert!(valid_region("eu-west-1"));
        assert!(!valid_region("us east 1"));
        assert!(!valid_region(""));
    }
}
