//! Encrypted, bounded input staging for import operations.

use blake3::Hasher;
use std::sync::Arc;
use std::{
    fs::{self, File},
    io::{self, Read, Seek, Write},
    path::{Path, PathBuf},
};
use terrabase::encryption::{ArtifactCipher, Compression, DatabaseKeyLease, EncryptionContext};

const MAX_INPUT_BYTES: u64 = 1 << 30;

/// Import-purpose encryption lease. Implementations must bind ciphertext to
/// the operation and reject a different database/tenant key context.
pub(crate) trait ImportCipher: Send + Sync {
    fn seal_chunk(&self, operation_id: &str, chunk: u64, plaintext: &[u8]) -> io::Result<Vec<u8>>;
    fn open_chunk(&self, operation_id: &str, chunk: u64, ciphertext: &[u8]) -> io::Result<Vec<u8>>;
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
        let tag = chunk_tag(operation_id, chunk, plaintext);
        let context = EncryptionContext::new(
            self.lease.metadata().scope().environment(),
            &self.tenant,
            &self.database,
            "import",
            blake3::Hash::from(tag).to_hex().to_string(),
            self.lease.metadata().version().to_text(),
            1,
            Compression::None,
        )
        .map_err(|_| {
            io::Error::new(io::ErrorKind::PermissionDenied, "import encryption context unavailable")
        })?;
        let mut cipher =
            ArtifactCipher::from_database_key_lease(&self.lease, context, 0).map_err(|_| {
                io::Error::new(io::ErrorKind::PermissionDenied, "import key lease unavailable")
            })?;
        let mut output = tag.to_vec();
        output.extend(
            cipher
                .seal(plaintext)
                .map_err(|_| io::Error::new(io::ErrorKind::Other, "import encryption failed"))?,
        );
        Ok(output)
    }

    fn open_chunk(&self, operation_id: &str, chunk: u64, ciphertext: &[u8]) -> io::Result<Vec<u8>> {
        let tag: [u8; 32] = ciphertext
            .get(..32)
            .ok_or_else(invalid_input)?
            .try_into()
            .map_err(|_| invalid_input())?;
        let context = EncryptionContext::new(
            self.lease.metadata().scope().environment(),
            &self.tenant,
            &self.database,
            "import",
            blake3::Hash::from(tag).to_hex().to_string(),
            self.lease.metadata().version().to_text(),
            1,
            Compression::None,
        )
        .map_err(|_| permission_error())?;
        let cipher = ArtifactCipher::from_database_key_lease(&self.lease, context, 0)
            .map_err(|_| permission_error())?;
        let plaintext = cipher.open(&ciphertext[32..]).map_err(|_| invalid_input())?;
        if chunk_tag(operation_id, chunk, &plaintext) != tag {
            return Err(invalid_input());
        }
        Ok(plaintext)
    }
}

fn invalid_input() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "invalid encrypted import chunk")
}
fn permission_error() -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, "import encryption context unavailable")
}
fn chunk_tag(operation_id: &str, chunk: u64, plaintext: &[u8]) -> [u8; 32] {
    let mut hasher = Hasher::new();
    for value in [operation_id.as_bytes(), &chunk.to_be_bytes(), plaintext] {
        hasher.update(&(value.len() as u64).to_be_bytes());
        hasher.update(value);
    }
    *hasher.finalize().as_bytes()
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
    pub(crate) fn create(
        root: impl AsRef<Path>,
        operation_id: &str,
        cipher: C,
    ) -> io::Result<Self> {
        if operation_id.is_empty() || operation_id.contains('/') || operation_id.contains('\\') {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid operation identity"));
        }
        fs::create_dir_all(root.as_ref())?;
        let path = root.as_ref().join(format!("{operation_id}.upload.part"));
        if path.exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "import input already staged",
            ));
        }
        let file = File::create(&path)?;
        Ok(Self {
            file,
            path,
            cipher,
            operation_id: operation_id.into(),
            digest: Hasher::new(),
            length: 0,
            chunk: 0,
        })
    }

    /// Reopens a durable partial upload after a process crash, validating every
    /// existing record before allowing more chunks to be appended.
    pub(crate) fn resume(
        root: impl AsRef<Path>,
        operation_id: &str,
        cipher: C,
    ) -> io::Result<Self> {
        if operation_id.is_empty() || operation_id.contains('/') || operation_id.contains('\\') {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid operation identity"));
        }
        let path = root.as_ref().join(format!("{operation_id}.upload.part"));
        let mut file = File::options().read(true).write(true).open(&path)?;
        let mut digest = Hasher::new();
        let mut length = 0u64;
        let mut chunk = 0u64;
        loop {
            let mut size = [0u8; 8];
            let read = file.read(&mut size)?;
            if read == 0 {
                break;
            }
            if read != size.len() {
                return Err(invalid_input());
            }
            let encrypted_len = u64::from_be_bytes(size);
            if encrypted_len < 32 || encrypted_len > MAX_INPUT_BYTES {
                return Err(invalid_input());
            }
            let mut encrypted =
                vec![0u8; usize::try_from(encrypted_len).map_err(|_| invalid_input())?];
            file.read_exact(&mut encrypted)?;
            let plaintext = cipher.open_chunk(operation_id, chunk, &encrypted)?;
            let next = length.checked_add(plaintext.len() as u64).ok_or_else(invalid_input)?;
            if next > MAX_INPUT_BYTES {
                return Err(io::Error::new(io::ErrorKind::InvalidInput, "input exceeds 1 GiB"));
            }
            digest.update(&plaintext);
            length = next;
            chunk = chunk.checked_add(1).ok_or_else(invalid_input)?;
        }
        file.seek(std::io::SeekFrom::End(0))?;
        Ok(Self { file, path, cipher, operation_id: operation_id.into(), digest, length, chunk })
    }

    pub(crate) fn write_chunk(&mut self, plaintext: &[u8]) -> io::Result<()> {
        let next = self
            .length
            .checked_add(plaintext.len() as u64)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "input too large"))?;
        if next > MAX_INPUT_BYTES {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "input exceeds 1 GiB"));
        }
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
        if complete.exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "import input already committed",
            ));
        }
        fs::rename(&self.path, &complete)?;
        if let Some(parent) = complete.parent() {
            File::open(parent)?.sync_all()?;
        }
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

pub(crate) struct ImportInputReader<C> {
    file: File,
    cipher: C,
    operation_id: String,
}

impl<C: ImportCipher> ImportInputReader<C> {
    pub(crate) fn open(path: impl AsRef<Path>, operation_id: &str, cipher: C) -> io::Result<Self> {
        let metadata = fs::symlink_metadata(path.as_ref())?;
        if !metadata.is_file() {
            return Err(invalid_input());
        }
        let file = File::open(path)?;
        Ok(Self { file, cipher, operation_id: operation_id.into() })
    }

    pub(crate) fn read_all(
        mut self,
        expected_digest: &str,
        expected_length: u64,
    ) -> io::Result<Vec<u8>> {
        if expected_length > MAX_INPUT_BYTES
            || expected_digest.len() != 64
            || !expected_digest.bytes().all(|b| b.is_ascii_hexdigit())
        {
            return Err(invalid_input());
        }
        let mut digest = Hasher::new();
        let mut output = Vec::new();
        let mut chunk = 0u64;
        loop {
            let mut size = [0u8; 8];
            let read = self.file.read(&mut size)?;
            if read == 0 {
                break;
            }
            if read != size.len() {
                return Err(invalid_input());
            }
            let encrypted_len = u64::from_be_bytes(size);
            if encrypted_len < 32 || encrypted_len > MAX_INPUT_BYTES {
                return Err(invalid_input());
            }
            let mut encrypted =
                vec![0u8; usize::try_from(encrypted_len).map_err(|_| invalid_input())?];
            self.file.read_exact(&mut encrypted)?;
            let plaintext = self.cipher.open_chunk(&self.operation_id, chunk, &encrypted)?;
            let next = u64::try_from(output.len())
                .unwrap()
                .checked_add(plaintext.len() as u64)
                .ok_or_else(invalid_input)?;
            if next > expected_length || next > MAX_INPUT_BYTES {
                return Err(invalid_input());
            }
            digest.update(&plaintext);
            output.extend_from_slice(&plaintext);
            chunk = chunk.checked_add(1).ok_or_else(invalid_input)?;
        }
        if output.len() as u64 != expected_length
            || digest.finalize().to_hex().as_str() != expected_digest
        {
            return Err(invalid_input());
        }
        Ok(output)
    }

    /// Streams authenticated plaintext to `consume` without retaining the
    /// upload in memory. The callback receives one encrypted-chunk's
    /// plaintext at a time; callers must perform record framing themselves
    /// and must not retain the supplied slice after returning.
    pub(crate) fn for_each_chunk<F>(
        mut self,
        expected_digest: &str,
        expected_length: u64,
        mut consume: F,
    ) -> io::Result<()>
    where
        F: FnMut(&[u8]) -> io::Result<()>,
    {
        if expected_length > MAX_INPUT_BYTES
            || expected_digest.len() != 64
            || !expected_digest.bytes().all(|b| b.is_ascii_hexdigit())
        {
            return Err(invalid_input());
        }
        let mut digest = Hasher::new();
        let mut length = 0u64;
        let mut chunk = 0u64;
        loop {
            let mut size = [0u8; 8];
            let read = self.file.read(&mut size)?;
            if read == 0 {
                break;
            }
            if read != size.len() {
                return Err(invalid_input());
            }
            let encrypted_len = u64::from_be_bytes(size);
            if !(32..=MAX_INPUT_BYTES).contains(&encrypted_len) {
                return Err(invalid_input());
            }
            let mut encrypted =
                vec![0u8; usize::try_from(encrypted_len).map_err(|_| invalid_input())?];
            self.file.read_exact(&mut encrypted)?;
            let plaintext = self.cipher.open_chunk(&self.operation_id, chunk, &encrypted)?;
            let next = length.checked_add(plaintext.len() as u64).ok_or_else(invalid_input)?;
            if next > expected_length || next > MAX_INPUT_BYTES {
                return Err(invalid_input());
            }
            digest.update(&plaintext);
            consume(&plaintext)?;
            length = next;
            chunk = chunk.checked_add(1).ok_or_else(invalid_input)?;
        }
        if length != expected_length || digest.finalize().to_hex().as_str() != expected_digest {
            return Err(invalid_input());
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_keys;
    use terrabase::{encryption::DatabaseKeySource, member::DatabaseScope, regions::RegionCode};

    fn leased_cipher() -> ImportLeaseCipher {
        let scope = DatabaseScope::new(
            "production",
            terrabase::identity::TenantUlid::generate(),
            terrabase::identity::ProjectUlid::generate(),
            terrabase::identity::DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .expect("scope");
        let database = scope.database().to_string();
        let tenant = scope.tenant().to_string();
        let lease = test_keys::storage_keys(&scope)
            .into_values()
            .next()
            .expect("database key")
            .current_lease()
            .expect("key lease");
        ImportLeaseCipher::new(lease, tenant, database)
    }

    #[test]
    fn leased_input_roundtrip_and_scope_binding() {
        let root = tempfile::tempdir().expect("root");
        let mut writer =
            ImportInputWriter::create(root.path(), "operation", leased_cipher()).expect("writer");
        writer.write_chunk(b"first").expect("chunk");
        writer.write_chunk(b"second").expect("chunk");
        let (path, digest, length) = writer.finish().expect("finish");
        let bytes = ImportInputReader::open(&path, "operation", leased_cipher())
            .expect("reader")
            .read_all(&digest, length)
            .expect("readback");
        assert_eq!(bytes, b"firstsecond");
        assert!(
            ImportInputReader::open(&path, "other-operation", leased_cipher())
                .expect("reader")
                .read_all(&digest, length)
                .is_err()
        );
    }

    #[test]
    fn leased_input_rejects_corrupt_and_truncated_chunks() {
        let root = tempfile::tempdir().expect("root");
        let mut writer =
            ImportInputWriter::create(root.path(), "operation", leased_cipher()).expect("writer");
        writer.write_chunk(b"payload").expect("chunk");
        let (path, digest, length) = writer.finish().expect("finish");
        let mut bytes = std::fs::read(&path).expect("artifact");
        bytes[bytes.len() - 1] ^= 1;
        std::fs::write(&path, &bytes).expect("corrupt");
        assert!(
            ImportInputReader::open(&path, "operation", leased_cipher())
                .expect("reader")
                .read_all(&digest, length)
                .is_err()
        );
        bytes.pop();
        std::fs::write(&path, &bytes).expect("truncate");
        assert!(
            ImportInputReader::open(&path, "operation", leased_cipher())
                .expect("reader")
                .read_all(&digest, length)
                .is_err()
        );
    }
}
