//! Shared bounded key-file validation for encrypted export artifact owners.

use crate::export_api::ExportError;
use std::fs::{self, File};
use std::io::Read;
use std::path::Path;
use terrabase::encryption::{
    ArtifactCipher, Compression, DatabaseKey, EncryptionContext, ProviderMode,
};

/// Lease-scoped encryption operation used by production artifact writers.
/// Implementations must perform key use inside the active authorization
/// lease; this trait never exposes or stores raw key material.
pub(crate) trait ArtifactCrypto: Send + Sync {
    fn seal(&self, domain: &str, plaintext: &[u8]) -> Result<Vec<u8>, ExportError>;
    fn open(&self, domain: &str, ciphertext: &[u8]) -> Result<Vec<u8>, ExportError>;
}

pub(crate) use crate::export_local::{LocalManifest, LocalWriter};

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
