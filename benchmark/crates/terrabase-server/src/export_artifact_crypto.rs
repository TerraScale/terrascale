//! Shared bounded key-file validation for encrypted export artifact owners.

use crate::export_api::{
    ArtifactFact, ArtifactWriter, ExportError, ExportFuture, MAX_METADATA_BYTES, MAX_PAGE_BYTES,
    MAX_PAGE_RECORDS, ensure_no_symlink_components, fact, publish_artifact, safe_member_path,
    sync_directory_ancestors,
};
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::Path;
use terrabase::encryption::{ArtifactCipher, Compression, DatabaseKeyLease, EncryptionContext};

/// Lease-scoped encryption operation used by production artifact writers.
/// Implementations must perform key use inside the active authorization
/// lease; this trait never exposes or stores raw key material.
pub(crate) trait ArtifactCrypto: Send + Sync {
    fn seal(&self, job: &str, member: &str, plaintext: &[u8]) -> Result<Vec<u8>, ExportError>;
    fn open(&self, job: &str, member: &str, ciphertext: &[u8]) -> Result<Vec<u8>, ExportError>;
}

/// Production crypto handle retaining the provider lease for the writer's
/// lifetime; raw key bytes never leave `DatabaseKeyLease`.
pub(crate) struct LeaseCrypto {
    lease: Arc<DatabaseKeyLease>,
    database: String,
    tenant: String,
}

impl LeaseCrypto {
    pub(crate) fn new(lease: Arc<DatabaseKeyLease>, database: String, tenant: String) -> Self {
        Self { lease, database, tenant }
    }

    fn cipher(&self, tag: &[u8; 32]) -> Result<ArtifactCipher, ExportError> {
        let context = EncryptionContext::new(
            self.lease.metadata().scope().environment(),
            &self.tenant,
            &self.database,
            "export",
            blake3::Hash::from(*tag).to_hex().to_string(),
            self.lease.metadata().version().to_text(),
            1,
            Compression::None,
        )
        .map_err(|_| ExportError::EncryptionUnavailable)?;
        ArtifactCipher::from_database_key_lease(&self.lease, context, 0)
            .map_err(|_| ExportError::EncryptionUnavailable)
    }
}

impl ArtifactCrypto for LeaseCrypto {
    fn seal(&self, job: &str, member: &str, plaintext: &[u8]) -> Result<Vec<u8>, ExportError> {
        let mut hasher = blake3::Hasher::new();
        for value in [job.as_bytes(), member.as_bytes(), plaintext] {
            hasher.update(&(value.len() as u64).to_be_bytes());
            hasher.update(value);
        }
        let tag = *hasher.finalize().as_bytes();
        let mut output = tag.to_vec();
        output.extend(
            self.cipher(&tag)?.seal(plaintext).map_err(|_| ExportError::EncryptionUnavailable)?,
        );
        Ok(output)
    }

    fn open(&self, job: &str, member: &str, ciphertext: &[u8]) -> Result<Vec<u8>, ExportError> {
        let tag: [u8; 32] = ciphertext
            .get(..32)
            .ok_or(ExportError::IdempotencyConflict)?
            .try_into()
            .map_err(|_| ExportError::IdempotencyConflict)?;
        let plaintext = self
            .cipher(&tag)?
            .open(&ciphertext[32..])
            .map_err(|_| ExportError::IdempotencyConflict)?;
        let mut hasher = blake3::Hasher::new();
        for value in [job.as_bytes(), member.as_bytes(), plaintext.as_slice()] {
            hasher.update(&(value.len() as u64).to_be_bytes());
            hasher.update(value);
        }
        if *hasher.finalize().as_bytes() != tag {
            return Err(ExportError::IdempotencyConflict);
        }
        Ok(plaintext)
    }
}

use std::path::PathBuf;
use std::sync::Arc;

#[derive(serde::Deserialize)]
pub(crate) struct LocalManifest {
    pub(crate) members: Vec<ArtifactFact>,
}

pub(crate) struct LocalWriter {
    pub(crate) directory: PathBuf,
    pub(crate) job_id: String,
    pub(crate) crypto: Arc<dyn ArtifactCrypto>,
}

pub(crate) fn io_error(error: &io::Error) -> ExportError {
    ExportError::Io(error.to_string())
}

pub(crate) fn bounded_read(path: &Path, limit: usize) -> Result<Vec<u8>, ExportError> {
    ensure_no_symlink_components(path)?;
    let file = File::open(path).map_err(|error| io_error(&error))?;
    let metadata = file.metadata().map_err(|error| io_error(&error))?;
    if !metadata.is_file() || metadata.len() > limit as u64 {
        return Err(ExportError::DestinationUnavailable);
    }
    let mut bytes = Vec::new();
    file.take(limit as u64 + 1).read_to_end(&mut bytes).map_err(|error| io_error(&error))?;
    if bytes.len() > limit {
        return Err(ExportError::DestinationUnavailable);
    }
    Ok(bytes)
}

pub(crate) fn encrypted_member(path: &Path) -> Result<Vec<u8>, ExportError> {
    bounded_read(path, MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) + 4096)
}

impl LocalWriter {
    pub(crate) fn verify_member(
        &self,
        path: &str,
        plaintext: &[u8],
    ) -> Result<ArtifactFact, ExportError> {
        let target = self.directory.join(path);
        ensure_no_symlink_components(&target)?;
        let encrypted = encrypted_member(&target)?;
        if self
            .crypto
            .open(&self.job_id, path, &encrypted)
            .map_err(|_| ExportError::IdempotencyConflict)?
            != plaintext
        {
            return Err(ExportError::IdempotencyConflict);
        }
        sync_directory_ancestors(target.parent().ok_or(ExportError::DestinationUnavailable)?)
            .map_err(|error| ExportError::Io(error.to_string()))?;
        Ok(fact(path, &encrypted))
    }
}

/// Reads an owner-private 32-byte export cursor key without exposing it in
/// diagnostics or retaining unbounded file contents.
pub(crate) fn read_cursor_key(path: &Path) -> Result<[u8; 32], ()> {
    let metadata = fs::symlink_metadata(path).map_err(|_| ())?;
    if !metadata.is_file() || metadata.len() != 32 {
        return Err(());
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(());
        }
    }
    let mut bytes = Vec::with_capacity(32);
    File::open(path).map_err(|_| ())?.take(33).read_to_end(&mut bytes).map_err(|_| ())?;
    if bytes.len() != 32 || bytes.iter().all(|byte| *byte == 0) {
        return Err(());
    }
    bytes.try_into().map_err(|_| ())
}

pub(crate) struct LocalCrypto {
    pub(crate) key: [u8; 32],
    pub(crate) database: String,
    pub(crate) tenant: String,
}

impl LocalCrypto {
    pub(crate) fn tag(&self, job: &str, member: &str, plaintext: &[u8]) -> [u8; 32] {
        let mut hasher = blake3::Hasher::new_keyed(&self.key);
        hasher.update(b"terrabase-local-export-member-v1");
        for bytes in [
            self.database.as_bytes(),
            self.tenant.as_bytes(),
            job.as_bytes(),
            member.as_bytes(),
            plaintext,
        ] {
            hasher.update(&(bytes.len() as u64).to_be_bytes());
            hasher.update(bytes);
        }
        *hasher.finalize().as_bytes()
    }

    pub(crate) fn cipher(&self, tag: &[u8; 32]) -> Result<ArtifactCipher, ExportError> {
        let context = EncryptionContext::new(
            "local",
            &self.tenant,
            &self.database,
            "export",
            blake3::Hash::from(*tag).to_hex().to_string(),
            "local-export-v1",
            1,
            Compression::None,
        )
        .map_err(|_| ExportError::EncryptionUnavailable)?;
        ArtifactCipher::new(ProviderMode::LocalFile, DatabaseKey::from_bytes(self.key), context)
            .map_err(|_| ExportError::EncryptionUnavailable)
    }

    pub(crate) fn seal(
        &self,
        job: &str,
        member: &str,
        plaintext: &[u8],
    ) -> Result<Vec<u8>, ExportError> {
        let tag = self.tag(job, member, plaintext);
        let mut bytes = tag.to_vec();
        bytes.extend(
            self.cipher(&tag)?.seal(plaintext).map_err(|_| ExportError::EncryptionUnavailable)?,
        );
        Ok(bytes)
    }

    pub(crate) fn open(
        &self,
        job: &str,
        member: &str,
        bytes: &[u8],
    ) -> Result<Vec<u8>, ExportError> {
        let tag: [u8; 32] = bytes
            .get(..32)
            .ok_or(ExportError::IdempotencyConflict)?
            .try_into()
            .map_err(|_| ExportError::IdempotencyConflict)?;
        let plaintext =
            self.cipher(&tag)?.open(&bytes[32..]).map_err(|_| ExportError::IdempotencyConflict)?;
        if self.tag(job, member, &plaintext) != tag {
            return Err(ExportError::IdempotencyConflict);
        }
        Ok(plaintext)
    }
}

impl ArtifactCrypto for LocalCrypto {
    fn seal(&self, job: &str, member: &str, plaintext: &[u8]) -> Result<Vec<u8>, ExportError> {
        LocalCrypto::seal(self, job, member, plaintext)
    }

    fn open(&self, job: &str, member: &str, ciphertext: &[u8]) -> Result<Vec<u8>, ExportError> {
        LocalCrypto::open(self, job, member, ciphertext)
    }
}

impl ArtifactWriter for LocalWriter {
    fn put<'a>(&'a mut self, path: &'a str, plaintext: &'a [u8]) -> ExportFuture<'a, ArtifactFact> {
        Box::pin(async move {
            if !safe_member_path(path) || plaintext.len() > MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) {
                return Err(ExportError::InvalidRequest("artifact member is unsafe or too large"));
            }
            let target = self.directory.join(path);
            ensure_no_symlink_components(&target)?;
            match fs::symlink_metadata(&target) {
                Ok(_) => return self.verify_member(path, plaintext),
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(ExportError::Io(error.to_string())),
            }
            let encrypted = self.crypto.seal(&self.job_id, path, plaintext)?;
            if publish_artifact(&target, &encrypted)? {
                Ok(fact(path, &encrypted))
            } else {
                self.verify_member(path, plaintext)
            }
        })
    }

    fn existing_facts<'a>(&'a mut self) -> ExportFuture<'a, Vec<ArtifactFact>> {
        Box::pin(async move {
            let mut pending = vec![self.directory.clone()];
            let mut facts = Vec::new();

            while let Some(directory) = pending.pop() {
                let entries =
                    fs::read_dir(&directory).map_err(|error| ExportError::Io(error.to_string()))?;
                for entry in entries {
                    let entry = entry.map_err(|error| ExportError::Io(error.to_string()))?;
                    let file_type =
                        entry.file_type().map_err(|error| ExportError::Io(error.to_string()))?;
                    if file_type.is_symlink() {
                        return Err(ExportError::DestinationUnavailable);
                    }
                    let path = entry.path();
                    if file_type.is_dir() {
                        pending.push(path);
                        continue;
                    }
                    if !file_type.is_file()
                        || path.file_name().is_some_and(|name| name == "COMMITTED")
                    {
                        continue;
                    }
                    let relative = path
                        .strip_prefix(&self.directory)
                        .map_err(|_| ExportError::DestinationUnavailable)?;
                    let member = relative.to_str().ok_or(ExportError::DestinationUnavailable)?;
                    // Publication temporaries use this exact, case-sensitive suffix.
                    #[allow(clippy::case_sensitive_file_extension_comparisons)]
                    if member.ends_with(".part") || !safe_member_path(member) {
                        continue;
                    }
                    let encrypted = encrypted_member(&path)?;
                    if encrypted.len() > MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) + 4096 {
                        return Err(ExportError::DestinationUnavailable);
                    }
                    self.crypto
                        .open(&self.job_id, member, &encrypted)
                        .map_err(|_| ExportError::IdempotencyConflict)?;
                    facts.push(fact(member, &encrypted));
                    if facts.len() > MAX_PAGE_RECORDS.saturating_mul(4) {
                        return Err(ExportError::DestinationUnavailable);
                    }
                }
            }
            facts.sort_by(|left, right| left.path.cmp(&right.path));
            Ok(facts)
        })
    }

    fn commit(self: Box<Self>, manifest: &ArtifactFact) -> ExportFuture<'static, ()> {
        let manifest = manifest.clone();
        Box::pin(async move {
            if manifest.path != "manifest.json" {
                return Err(ExportError::IdempotencyConflict);
            }
            let manifest_path = self.directory.join(&manifest.path);
            ensure_no_symlink_components(&manifest_path)?;
            let encrypted = encrypted_member(&manifest_path)?;
            if fact(&manifest.path, &encrypted) != *manifest {
                return Err(ExportError::IdempotencyConflict);
            }
            self.crypto.open(&self.job_id, &manifest.path, &encrypted)?;
            let marker = self.directory.join("COMMITTED");
            ensure_no_symlink_components(&marker)?;
            let bytes = format!("{}\t{}\t{}\n", manifest.path, manifest.length, manifest.checksum);
            if !publish_artifact(&marker, bytes.as_bytes())?
                && bounded_read(&marker, 1024)? != bytes.as_bytes()
            {
                return Err(ExportError::IdempotencyConflict);
            }
            Ok(())
        })
    }
}
