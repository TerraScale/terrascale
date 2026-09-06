//! Shared bounded key-file validation for encrypted export artifact owners.

use crate::export_api::{
    ArtifactFact, ExportError, MAX_METADATA_BYTES, MAX_PAGE_BYTES, ensure_no_symlink_components,
};
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::Path;
use terrabase::encryption::{
    ArtifactCipher, Compression, DatabaseKey, EncryptionContext, ProviderMode,
};

/// Lease-scoped encryption operation used by production artifact writers.
/// Implementations must perform key use inside the active authorization
/// lease; this trait never exposes or stores raw key material.
pub(crate) trait ArtifactCrypto: Send + Sync {
    fn seal(&self, job: &str, member: &str, plaintext: &[u8]) -> Result<Vec<u8>, ExportError>;
    fn open(&self, job: &str, member: &str, ciphertext: &[u8]) -> Result<Vec<u8>, ExportError>;
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
