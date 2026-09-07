//! Encrypted, bounded input staging for import operations.

use blake3::Hasher;
use std::{fs::{self, File}, io::{self, Write}, path::{Path, PathBuf}};
use std::sync::Arc;
use terrabase::encryption::{ArtifactCipher, Compression, DatabaseKeyLease, EncryptionContext};

const MAX_INPUT_BYTES: u64 = 1 << 30;

/// Import-purpose encryption lease. Implementations must bind ciphertext to
/// the operation and reject a different database/tenant key context.
pub(crate) trait ImportCipher: Send + Sync {
    fn seal_chunk(&self, operation_id: &str, chunk: u64, plaintext: &[u8]) -> io::Result<Vec<u8>>;
}

/// Production import cipher. The purpose and upload identity are part of the
/// authenticated encryption context and cannot be reused by export members.
pub(crate) struct ImportLeaseCipher {
    lease: Arc<DatabaseKeyLease>,
    tenant: String,
    database: String,
}

impl ImportLeaseCipher {
    pub(crate) fn new(lease: Arc<DatabaseKeyLease>, tenant: String, database: String) -> Self {
        Self { lease, tenant, database }
    }
}

impl ImportCipher for ImportLeaseCipher {
    fn seal_chunk(&self, operation_id: &str, chunk: u64, plaintext: &[u8]) -> io::Result<Vec<u8>> {
        let mut tag_input = Vec::new();
        tag_input.extend_from_slice(operation_id.as_bytes());
        tag_input.extend_from_slice(&chunk.to_be_bytes());
        tag_input.extend_from_slice(plaintext);
        let tag = *blake3::hash(&tag_input).as_bytes();
        let context = EncryptionContext::new(
            self.lease.metadata().scope().environment(),
            &self.tenant,
            &self.database,
            "import",
            blake3::Hash::from(tag).to_hex().to_string(),
            self.lease.metadata().version().to_text(),
            1,
            Compression::None,
        ).map_err(|_| io::Error::new(io::ErrorKind::PermissionDenied, "import encryption context unavailable"))?;
        let mut cipher = ArtifactCipher::from_database_key_lease(&self.lease, context, 0)
            .map_err(|_| io::Error::new(io::ErrorKind::PermissionDenied, "import key lease unavailable"))?;
        let mut output = tag.to_vec();
        output.extend(cipher.seal(plaintext).map_err(|_| io::Error::new(io::ErrorKind::Other, "import encryption failed"))?);
        Ok(output)
    }
}

pub(crate) struct ImportInputWriter<C> {
    file: File,
    path: PathBuf,
    cipher: C,
    operation_id: String,
    digest: Hasher,
    length: u64,
    chunk: u64,
}

impl<C: ImportCipher> ImportInputWriter<C> {
    pub(crate) fn create(root: impl AsRef<Path>, operation_id: &str, cipher: C) -> io::Result<Self> {
        if operation_id.is_empty() || operation_id.contains('/') || operation_id.contains('\\') {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid operation identity"));
        }
        fs::create_dir_all(root.as_ref())?;
        let path = root.as_ref().join(format!("{operation_id}.upload.part"));
        let file = File::create(&path)?;
        Ok(Self { file, path, cipher, operation_id: operation_id.into(), digest: Hasher::new(), length: 0, chunk: 0 })
    }

    pub(crate) fn write_chunk(&mut self, plaintext: &[u8]) -> io::Result<()> {
        let next = self.length.checked_add(plaintext.len() as u64).ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "input too large"))?;
        if next > MAX_INPUT_BYTES { return Err(io::Error::new(io::ErrorKind::InvalidInput, "input exceeds 1 GiB")); }
        let encrypted = self.cipher.seal_chunk(&self.operation_id, self.chunk, plaintext)?;
        self.file.write_all(&(encrypted.len() as u64).to_be_bytes())?;
        self.file.write_all(&encrypted)?;
        self.digest.update(plaintext);
        self.length = next;
        self.chunk += 1;
        Ok(())
    }

    pub(crate) fn finish(mut self) -> io::Result<(PathBuf, String, u64)> {
        self.file.sync_all()?;
        let digest = self.digest.finalize().to_hex().to_string();
        let complete = self.path.with_extension("upload");
        fs::rename(&self.path, &complete)?;
        Ok((complete, digest, self.length))
    }

    pub(crate) fn finish_and_commit(
        self,
        store: &crate::import_store::ImportStore,
    ) -> io::Result<(PathBuf, String, u64)> {
        let operation_id = self.operation_id.clone();
        let result = self.finish()?;
        store
            .commit_input(&operation_id, &result.1, result.2, &result.0)
            .map_err(|_| io::Error::other("import metadata commit failed"))?;
        Ok(result)
    }
}
