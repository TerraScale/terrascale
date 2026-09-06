//! Authenticated local delivery of OCI Vault leases by the host orchestrator.

use std::collections::BTreeMap;
use std::fmt;
use std::fs;
use std::io::{Read, Write};
use std::os::unix::fs::{FileTypeExt, MetadataExt};
use std::os::unix::net::UnixStream;
use std::path::{Component, Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use super::{
    DatabaseKey, DatabaseKeyLease, DatabaseKeySource, DatabaseKeyVersion, EncryptionError,
    KeyVersionMetadata, MAX_DATABASE_KEY_LEASE,
};
use crate::regions::{RegionCatalog, RegionCode};

const MAGIC: &[u8; 5] = b"TBKL\x01";
const TIMEOUT: Duration = Duration::from_secs(25);
const SUCCESS_BYTES: u32 = 68;

/// A production key source backed by the host's authenticated Vault adapter.
///
/// Every request rechecks the peer and current scope/version authorization.
/// Missing or rejected leases never generate replacement key material.
#[derive(Clone)]
pub struct OciVaultKeyDelivery {
    socket: PathBuf,
    orchestrator_uid: u32,
    socket_gid: u32,
}

impl fmt::Debug for OciVaultKeyDelivery {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("OciVaultKeyDelivery(REDACTED)")
    }
}

impl OciVaultKeyDelivery {
    /// Validates a protected socket owned by the adapter UID and dedicated GID.
    ///
    /// # Errors
    /// Returns a redacted refusal for absent, unsafe, or inaccessible paths.
    pub fn new(
        socket: PathBuf,
        orchestrator_uid: u32,
        socket_gid: u32,
    ) -> Result<Self, EncryptionError> {
        let provider = Self { socket, orchestrator_uid, socket_gid };
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
    pub fn lease(
        &self,
        metadata: &KeyVersionMetadata,
        region: RegionCode,
    ) -> Result<DatabaseKeyLease, EncryptionError> {
        self.validate_path()?;
        if !RegionCatalog::production().is_registered(&region) {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        let scope = metadata.scope();
        let project = scope.project().ok_or(EncryptionError::InvalidKeyMetadata)?;
        let fields = [
            scope.environment().to_owned(),
            scope.tenant().to_string(),
            project.to_string(),
            scope.database().to_string(),
            metadata.version().to_text(),
            region.to_string(),
            scope.purpose().as_str().to_owned(),
        ];
        let mut request = MAGIC.to_vec();
        for field in fields {
            if field.is_empty() || field.len() > 128 {
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
        let mut length = [0u8; 4];
        stream.read_exact(&mut length).map_err(unavailable)?;
        if u32::from_be_bytes(length) != SUCCESS_BYTES {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        let mut header = [0u8; 36];
        stream.read_exact(&mut header).map_err(unavailable)?;
        if &header[..5] != MAGIC
            || header[5] != 0
            || header[10..] != *metadata.version().to_text().as_bytes()
        {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        let seconds = u32::from_be_bytes(header[6..10].try_into().map_err(unavailable)?);
        let duration = Duration::from_secs(u64::from(seconds));
        if duration.is_zero() || duration > MAX_DATABASE_KEY_LEASE {
            return Err(EncryptionError::InvalidKeyLease);
        }
        // Read directly into locked memory: no plaintext response buffer.
        let mut key = DatabaseKey::locked()?;
        stream.read_exact(key.material_mut()).map_err(unavailable)?;
        let expires_at = started.checked_add(duration).ok_or(EncryptionError::InvalidKeyLease)?;
        DatabaseKeyLease::from_adapter_until(metadata.clone(), key, expires_at)
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
            validate_component(&path, &self.socket, self.orchestrator_uid, self.socket_gid)?;
        }
        Ok(())
    }
}

/// Bounded, renewable OCI leases for one immutable database scope and version.
///
/// Replace this source only after a new active version is durably admitted.
/// Historical versions are requested explicitly and remain usable only while
/// the orchestrator authorizes their regional wrappers.
#[derive(Debug)]
pub struct OciVaultDatabaseKeys {
    provider: OciVaultKeyDelivery,
    metadata: KeyVersionMetadata,
    region: RegionCode,
    cache: Mutex<BTreeMap<DatabaseKeyVersion, CachedLease>>,
}

#[derive(Debug)]
struct CachedLease {
    lease: Arc<DatabaseKeyLease>,
    renew_at: Instant,
}

impl OciVaultDatabaseKeys {
    /// Authorizes the active key before returning a production storage source.
    ///
    /// # Errors
    /// Fails closed when the bound scope/version/Region cannot obtain a lease.
    pub fn new(
        provider: OciVaultKeyDelivery,
        metadata: KeyVersionMetadata,
        region: RegionCode,
    ) -> Result<Self, EncryptionError> {
        let source = Self { provider, metadata, region, cache: Mutex::new(BTreeMap::new()) };
        source.current_lease()?;
        Ok(source)
    }
}

impl DatabaseKeySource for OciVaultDatabaseKeys {
    fn current_lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
        self.lease(self.metadata.version())
    }

    fn lease(&self, version: DatabaseKeyVersion) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
        // Serialize a renewal per database so concurrent page reads cannot
        // create a Vault request storm. Healthy cached reads do no socket I/O.
        let mut cache = self.cache.lock().map_err(unavailable)?;
        let now = Instant::now();
        cache.retain(|_, cached| !cached.lease.is_expired());
        if let Some(cached) = cache.get(&version)
            && now < cached.renew_at
            && !cached.lease.is_expired()
        {
            return Ok(Arc::clone(&cached.lease));
        }
        let metadata = KeyVersionMetadata::new(self.metadata.scope().clone(), version);
        let lease = Arc::new(self.provider.lease(&metadata, self.region)?);
        let now = Instant::now();
        let remaining = lease
            .expires_at()
            .checked_duration_since(now)
            .filter(|duration| !duration.is_zero())
            .ok_or(EncryptionError::KeyLeaseExpired)?;
        let renew_at =
            now.checked_add(remaining * 4 / 5).ok_or(EncryptionError::InvalidKeyLease)?;
        if !cache.contains_key(&version) && cache.len() >= 32 {
            cache.pop_first();
        }
        cache.insert(version, CachedLease { lease: Arc::clone(&lease), renew_at });
        Ok(lease)
    }
}

fn validate_component(
    path: &Path,
    socket: &Path,
    uid: u32,
    gid: u32,
) -> Result<(), EncryptionError> {
    let metadata = fs::symlink_metadata(path).map_err(unavailable)?;
    if metadata.file_type().is_symlink() {
        return Err(EncryptionError::ProductionProviderUnavailable);
    }
    if path == socket {
        if !metadata.file_type().is_socket()
            || metadata.uid() != uid
            || metadata.gid() != gid
            || metadata.mode() & 0o777 != 0o660
        {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
    } else if !metadata.is_dir()
        || (socket.parent() == Some(path)
            && (metadata.uid() != uid || metadata.gid() != gid || metadata.mode() & 0o777 != 0o710))
        || (metadata.uid() != 0 && metadata.uid() != uid)
        || metadata.mode() & 0o022 != 0
    {
        return Err(EncryptionError::ProductionProviderUnavailable);
    }
    Ok(())
}

fn unavailable<T>(_: T) -> EncryptionError {
    EncryptionError::ProductionProviderUnavailable
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::encryption::{DatabaseKeyVersion, KeyProviderIdentity, KeyPurpose, KeyScope};
    use crate::identity::{DatabaseUlid, ProjectUlid, TenantUlid};
    use std::os::unix::fs::PermissionsExt;
    use std::os::unix::net::UnixListener;
    use std::sync::atomic::{AtomicU64, Ordering};

    static NEXT: AtomicU64 = AtomicU64::new(0);

    struct SocketFixture {
        directory: PathBuf,
        socket: PathBuf,
    }

    impl SocketFixture {
        fn new() -> Self {
            let directory = std::env::current_dir().unwrap().join(format!(
                "target/key-delivery-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&directory).unwrap();
            fs::set_permissions(&directory, fs::Permissions::from_mode(0o710)).unwrap();
            Self { socket: directory.join("lease.sock"), directory }
        }

        fn listen(&self) -> UnixListener {
            let listener = UnixListener::bind(&self.socket).unwrap();
            fs::set_permissions(&self.socket, fs::Permissions::from_mode(0o660)).unwrap();
            listener
        }

        fn provider(&self) -> OciVaultKeyDelivery {
            OciVaultKeyDelivery::new(
                self.socket.clone(),
                rustix::process::geteuid().as_raw(),
                rustix::process::getegid().as_raw(),
            )
            .unwrap()
        }
    }

    impl Drop for SocketFixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.directory);
        }
    }

    fn metadata(version: &str) -> KeyVersionMetadata {
        KeyVersionMetadata::new(
            KeyScope::new_with_project(
                "production",
                TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap(),
                ProjectUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAW").unwrap(),
                DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAX").unwrap(),
                KeyPurpose::Data,
            )
            .unwrap(),
            DatabaseKeyVersion::from_text(version).unwrap(),
        )
    }

    fn respond(
        listener: UnixListener,
        version: &str,
        seconds: u32,
        key: u8,
    ) -> std::thread::JoinHandle<()> {
        let version = version.to_owned();
        std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut length = [0u8; 4];
            stream.read_exact(&mut length).unwrap();
            let mut request = vec![0u8; usize::try_from(u32::from_be_bytes(length)).unwrap()];
            assert!(request.len() < 2048);
            stream.read_exact(&mut request).unwrap();
            assert_eq!(&request[..5], MAGIC);
            assert!(request.windows(26).any(|field| field == version.as_bytes()));
            let mut response = SUCCESS_BYTES.to_be_bytes().to_vec();
            response.extend_from_slice(MAGIC);
            response.push(0);
            response.extend_from_slice(&seconds.to_be_bytes());
            response.extend_from_slice(version.as_bytes());
            response.extend_from_slice(&[key; 32]);
            let _ = stream.write_all(&response);
        })
    }

    #[test]
    fn lease_restart_and_rotation_preserve_explicit_version_and_locked_material() {
        let fixture = SocketFixture::new();
        let version = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
        for (version, key) in [(version, 11), (version, 11), ("01ARZ3NDEKTSV4RRFFQ69G5FB1", 22)] {
            let listener = fixture.listen();
            let provider = fixture.provider();
            let server = respond(listener, version, 60, key);
            let lease = provider
                .lease(&metadata(version), RegionCode::from_wire_bytes(*b"GRU").unwrap())
                .unwrap();
            assert_eq!(lease.provider(), KeyProviderIdentity::OciVault);
            assert_eq!(lease.metadata().version().to_text(), version);
            lease
                .with_database_key(|material| {
                    assert!(matches!(material.bytes, super::super::KeyMemory::Locked(_)));
                    assert_eq!(material.material(), &[key; 32]);
                })
                .unwrap();
            assert!(!format!("{provider:?} {lease:?}").contains("lease.sock"));
            server.join().unwrap();
            fs::remove_file(&fixture.socket).unwrap();
        }
    }

    #[test]
    fn missing_unsafe_and_wrong_uid_sockets_fail_without_key_generation() {
        let fixture = SocketFixture::new();
        assert!(
            OciVaultKeyDelivery::new(
                fixture.socket.clone(),
                0,
                rustix::process::getegid().as_raw()
            )
            .is_err()
        );
        let _listener = fixture.listen();
        assert!(
            OciVaultKeyDelivery::new(
                fixture.socket.clone(),
                rustix::process::geteuid().as_raw(),
                rustix::process::getegid().as_raw().wrapping_add(1)
            )
            .is_err()
        );
        assert!(
            OciVaultKeyDelivery::new(
                fixture.socket.clone(),
                rustix::process::geteuid().as_raw().wrapping_add(1),
                rustix::process::getegid().as_raw()
            )
            .is_err()
        );
        fs::set_permissions(&fixture.socket, fs::Permissions::from_mode(0o666)).unwrap();
        assert!(
            OciVaultKeyDelivery::new(
                fixture.socket.clone(),
                rustix::process::geteuid().as_raw(),
                rustix::process::getegid().as_raw()
            )
            .is_err()
        );
    }

    #[test]
    fn excessive_authorization_duration_is_rejected() {
        let fixture = SocketFixture::new();
        let listener = fixture.listen();
        let version = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
        let server = respond(listener, version, 1801, 17);
        assert!(matches!(
            fixture
                .provider()
                .lease(&metadata(version), RegionCode::from_wire_bytes(*b"GRU").unwrap()),
            Err(EncryptionError::InvalidKeyLease)
        ));
        server.join().unwrap();
    }
    #[test]
    fn denied_truncated_and_wrong_version_replies_never_expose_secret_material() {
        let version = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
        let mut wrong_version = SUCCESS_BYTES.to_be_bytes().to_vec();
        wrong_version.extend_from_slice(MAGIC);
        wrong_version.push(0);
        wrong_version.extend_from_slice(&60u32.to_be_bytes());
        wrong_version.extend_from_slice(b"01ARZ3NDEKTSV4RRFFQ69G5FB1");
        wrong_version.extend_from_slice(b"SECRET_MUST_NOT_APPEAR_IN_OUTPUT!");
        let denied = [0, 0, 0, 6, b'T', b'B', b'K', b'L', 1, 1].to_vec();
        for response in [denied, wrong_version, vec![0, 0, 0, 68, b'T']] {
            let fixture = SocketFixture::new();
            let listener = fixture.listen();
            let provider = fixture.provider();
            let server = std::thread::spawn(move || {
                let (mut stream, _) = listener.accept().unwrap();
                let mut length = [0u8; 4];
                stream.read_exact(&mut length).unwrap();
                let mut request = vec![0; usize::try_from(u32::from_be_bytes(length)).unwrap()];
                stream.read_exact(&mut request).unwrap();
                let _ = stream.write_all(&response);
            });
            let error = provider
                .lease(&metadata(version), RegionCode::from_text("GRU").unwrap())
                .unwrap_err();
            assert_eq!(error.to_string(), "production provider unavailable");
            server.join().unwrap();
        }
    }
    #[test]
    fn persisted_artifact_reopens_with_fresh_lease_and_rejects_wrong_scope_or_tampering() {
        use crate::encryption::{ArtifactCipher, ArtifactKind, Compression, EncryptionContext};
        let metadata = metadata("01ARZ3NDEKTSV4RRFFQ69G5FB0");
        let make_lease = || {
            DatabaseKeyLease::from_adapter(
                metadata.clone(),
                DatabaseKey::from_bytes([91; 32]),
                Duration::from_secs(60),
                KeyProviderIdentity::OciVault,
            )
            .unwrap()
        };
        let original = make_lease();
        let context = EncryptionContext::for_artifact(
            metadata.scope(),
            ArtifactKind::Database,
            crate::identity::process_generator().generate(),
            metadata.version(),
            1,
            Compression::None,
        )
        .unwrap();
        let mut cipher = ArtifactCipher::from_database_key_lease(&original, context, 0).unwrap();
        let envelope = cipher.seal(b"restart-safe encrypted artifact").unwrap();
        drop(cipher);
        drop(original);
        assert_eq!(
            ArtifactCipher::envelope_key_version(
                metadata.scope(),
                ArtifactKind::Database,
                1,
                &envelope
            )
            .unwrap(),
            metadata.version()
        );
        assert!(
            ArtifactCipher::envelope_key_version(
                metadata.scope(),
                ArtifactKind::Backup,
                1,
                &envelope
            )
            .is_err()
        );
        let restored = make_lease();
        assert_eq!(
            ArtifactCipher::open_envelope_from_database_key_lease(
                &restored,
                ArtifactKind::Database,
                1,
                &envelope
            )
            .unwrap(),
            b"restart-safe encrypted artifact"
        );
        assert!(
            ArtifactCipher::open_envelope_from_database_key_lease(
                &restored,
                ArtifactKind::Backup,
                1,
                &envelope
            )
            .is_err()
        );
        assert!(
            ArtifactCipher::open_envelope_from_database_key_lease(
                &restored,
                ArtifactKind::Database,
                2,
                &envelope
            )
            .is_err()
        );
        let mut altered = envelope;
        *altered.last_mut().unwrap() ^= 1;
        assert!(
            ArtifactCipher::open_envelope_from_database_key_lease(
                &restored,
                ArtifactKind::Database,
                1,
                &altered
            )
            .is_err()
        );
    }
    #[test]
    fn bound_source_caches_io_and_renews_the_same_explicit_version() {
        let fixture = SocketFixture::new();
        let listener = fixture.listen();
        let version = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
        let server = respond(listener, version, 60, 41);
        let source = OciVaultDatabaseKeys::new(
            fixture.provider(),
            metadata(version),
            RegionCode::from_text("GRU").unwrap(),
        )
        .unwrap();
        server.join().unwrap();
        fs::remove_file(&fixture.socket).unwrap();
        let first = source.current_lease().unwrap();
        let second = source.current_lease().unwrap();
        assert!(Arc::ptr_eq(&first, &second));
        // Expire the renewal deadline, not the key's actual authorization.
        source.cache.lock().unwrap().get_mut(&metadata(version).version()).unwrap().renew_at =
            Instant::now();
        let server = respond(fixture.listen(), version, 60, 41);
        let renewed = source.current_lease().unwrap();
        assert!(!Arc::ptr_eq(&first, &renewed));
        assert_eq!(renewed.metadata(), first.metadata());
        server.join().unwrap();
        fs::remove_file(&fixture.socket).unwrap();
        assert!(
            source
                .lease(DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB1").unwrap())
                .is_err()
        );
        assert!(source.current_lease().is_ok());
    }
}
