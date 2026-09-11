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

impl<T: ImportCipher + ?Sized> ImportCipher for Arc<T> {
    fn seal_chunk(&self, operation_id: &str, chunk: u64, plaintext: &[u8]) -> io::Result<Vec<u8>> {
        (**self).seal_chunk(operation_id, chunk, plaintext)
    }
    fn open_chunk(&self, operation_id: &str, chunk: u64, ciphertext: &[u8]) -> io::Result<Vec<u8>> {
        (**self).open_chunk(operation_id, chunk, ciphertext)
    }
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
        use aws_lc_rs::rand::{SecureRandom as _, SystemRandom};
        let mut nonce = [0u8; 32];
        SystemRandom::new()
            .fill(&mut nonce)
            .map_err(|_| io::Error::other("import randomness unavailable"))?;
        let tag = chunk_artifact_id(operation_id, chunk, &nonce)?;
        let context = EncryptionContext::new_with_project(
            self.lease.metadata().scope().environment(),
            &self.tenant,
            self.lease.metadata().scope().project().ok_or_else(permission_error)?.to_text(),
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
        let mut output = nonce.to_vec();
        output.extend(
            cipher
                .seal(plaintext)
                .map_err(|_| io::Error::other("import encryption failed"))?,
        );
        Ok(output)
    }

    fn open_chunk(&self, operation_id: &str, chunk: u64, ciphertext: &[u8]) -> io::Result<Vec<u8>> {
        let nonce: [u8; 32] = ciphertext
            .get(..32)
            .ok_or_else(invalid_input)?
            .try_into()
            .map_err(|_| invalid_input())?;
        let tag = chunk_artifact_id(operation_id, chunk, &nonce)?;
        let context = EncryptionContext::new_with_project(
            self.lease.metadata().scope().environment(),
            &self.tenant,
            self.lease.metadata().scope().project().ok_or_else(permission_error)?.to_text(),
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
        Ok(plaintext)
    }
}

fn invalid_input() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "invalid encrypted import chunk")
}
fn permission_error() -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, "import encryption context unavailable")
}
fn chunk_artifact_id(operation_id: &str, chunk: u64, nonce: &[u8; 32]) -> io::Result<[u8; 32]> {
    let mut hasher = Hasher::new();
    for value in [operation_id.as_bytes(), &chunk.to_be_bytes(), nonce] {
        let length = u64::try_from(value.len()).map_err(|_| invalid_input())?;
        hasher.update(&length.to_be_bytes());
        hasher.update(value);
    }
    Ok(*hasher.finalize().as_bytes())
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
        let file = File::options().write(true).create_new(true).open(&path)?;
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

    pub(crate) fn write_chunk(&mut self, plaintext: &[u8]) -> io::Result<()> {
        let next = self
            .length
            .checked_add(u64::try_from(plaintext.len()).map_err(|_| {
                io::Error::new(io::ErrorKind::InvalidInput, "input length is not representable")
            })?)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "input too large"))?;
        if next > MAX_INPUT_BYTES {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "input exceeds 1 GiB"));
        }
        let encrypted = self.cipher.seal_chunk(&self.operation_id, self.chunk, plaintext)?;
        let encrypted_length = u64::try_from(encrypted.len()).map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidInput, "encrypted chunk is not representable")
        })?;
        self.file.write_all(&encrypted_length.to_be_bytes())?;
        self.file.write_all(&encrypted)?;
        self.digest.update(plaintext);
        self.length = next;
        self.chunk += 1;
        Ok(())
    }

    pub(crate) fn finish(self) -> io::Result<(PathBuf, String, u64)> {
        self.file.sync_all()?;
        let digest = self.digest.finalize().to_hex().to_string();
        let complete = self.path.with_extension("");
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

impl<C> Drop for ImportInputWriter<C> {
    fn drop(&mut self) {
        // Only the uncommitted name is removed. A completed immutable upload
        // survives process/request teardown and is owned by ImportStore.
        let _ = fs::remove_file(&self.path);
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

    #[cfg(test)]
    pub(crate) fn read_all(
        self,
        expected_digest: &str,
        expected_length: u64,
    ) -> io::Result<Vec<u8>> {
        let mut reader = self.verified_reader(expected_digest, expected_length)?;
        let mut output = Vec::new();
        reader.read_to_end(&mut output)?;
        Ok(output)
    }
}

/// Bounded plaintext reader; validates the complete immutable upload before
/// any records can reach a write transaction.
pub(crate) struct ImportPlaintextReader<C> {
    input: ImportInputReader<C>,
    chunk: u64,
    pending: io::Cursor<Vec<u8>>,
}
impl<C: ImportCipher> ImportInputReader<C> {
    pub(crate) fn verified_reader(
        self,
        digest: &str,
        length: u64,
    ) -> io::Result<ImportPlaintextReader<C>> {
        let mut reader =
            ImportPlaintextReader { input: self, chunk: 0, pending: io::Cursor::new(Vec::new()) };
        let mut actual = Hasher::new();
        let mut total = 0u64;
        // Keep the validation buffer bounded without consuming a large stack
        // frame on every request.
        let mut buffer = vec![0u8; 65536];
        loop {
            let read = reader.read(&mut buffer)?;
            if read == 0 {
                break;
            }
            total = total
                .checked_add(u64::try_from(read).map_err(|_| invalid_input())?)
                .ok_or_else(invalid_input)?;
            if total > length || total > MAX_INPUT_BYTES {
                return Err(invalid_input());
            }
            actual.update(&buffer[..read]);
        }
        if total != length || actual.finalize().to_hex().as_str() != digest {
            return Err(invalid_input());
        }
        reader.input.file.seek(io::SeekFrom::Start(0))?;
        reader.chunk = 0;
        reader.pending = io::Cursor::new(Vec::new());
        Ok(reader)
    }
}
impl<C: ImportCipher> Read for ImportPlaintextReader<C> {
    fn read(&mut self, output: &mut [u8]) -> io::Result<usize> {
        if output.is_empty() {
            return Ok(0);
        }
        loop {
            let count = self.pending.read(output)?;
            if count != 0 {
                return Ok(count);
            }
            let mut size = [0u8; 8];
            if self.input.file.read(&mut size[..1])? == 0 {
                return Ok(0);
            }
            self.input.file.read_exact(&mut size[1..])?;
            let size = u64::from_be_bytes(size);
            // Upload routes produce chunks of at most 64 KiB. Retain a
            // generous bounded ceiling for existing admitted staged inputs.
            if !(32..=2 * 1024 * 1024).contains(&size) {
                return Err(invalid_input());
            }
            let size = usize::try_from(size).map_err(|_| invalid_input())?;
            let mut encrypted = vec![0u8; size];
            self.input.file.read_exact(&mut encrypted)?;
            let plaintext =
                self.input.cipher.open_chunk(&self.input.operation_id, self.chunk, &encrypted)?;
            self.chunk = self.chunk.checked_add(1).ok_or_else(invalid_input)?;
            self.pending = io::Cursor::new(plaintext);
        }
    }
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::*;
    use crate::test_keys;
    use terrabase::{member::DatabaseScope, regions::RegionCode};

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

    fn test_root() -> std::path::PathBuf {
        let root = std::env::temp_dir().join(format!(
            "terrabase-import-{}-{}",
            std::process::id(),
            terrabase::identity::generate()
        ));
        std::fs::create_dir_all(&root).expect("root");
        root
    }

    #[test]
    fn randomized_chunks_bind_operation_ordinal_and_database() {
        let cipher = leased_cipher();
        let first = cipher.seal_chunk("operation", 0, b"same low-entropy secret").unwrap();
        let second = cipher.seal_chunk("operation", 0, b"same low-entropy secret").unwrap();
        assert_ne!(first, second, "fresh artifact IDs must not reveal plaintext equality");
        assert_eq!(cipher.open_chunk("operation", 0, &first).unwrap(), b"same low-entropy secret");
        assert_eq!(cipher.open_chunk("operation", 0, &second).unwrap(), b"same low-entropy secret");
        assert!(cipher.open_chunk("other-operation", 0, &first).is_err());
        assert!(cipher.open_chunk("operation", 1, &first).is_err());
        assert!(leased_cipher().open_chunk("operation", 0, &first).is_err());
        let mut tampered = first;
        tampered[0] ^= 1;
        assert!(cipher.open_chunk("operation", 0, &tampered).is_err());
    }

    #[test]
    fn leased_input_roundtrip_and_scope_binding() {
        let root = test_root();
        let cipher = Arc::new(leased_cipher());
        let mut writer =
            ImportInputWriter::create(&root, "operation", Arc::clone(&cipher)).expect("writer");
        writer.write_chunk(b"first").expect("chunk");
        writer.write_chunk(b"second").expect("chunk");
        let (path, digest, length) = writer.finish().expect("finish");
        let bytes = ImportInputReader::open(&path, "operation", Arc::clone(&cipher))
            .expect("reader")
            .read_all(&digest, length)
            .expect("readback");
        assert_eq!(bytes, b"firstsecond");
        assert!(
            ImportInputReader::open(&path, "other-operation", Arc::clone(&cipher))
                .expect("reader")
                .read_all(&digest, length)
                .is_err()
        );
        std::fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn leased_input_rejects_corrupt_and_truncated_chunks() {
        let root = test_root();
        let cipher = Arc::new(leased_cipher());
        let mut writer =
            ImportInputWriter::create(&root, "operation", Arc::clone(&cipher)).expect("writer");
        writer.write_chunk(b"payload").expect("chunk");
        let (path, digest, length) = writer.finish().expect("finish");
        let mut bytes = std::fs::read(&path).expect("artifact");
        let last = bytes.len() - 1;
        bytes[last] ^= 1;
        std::fs::write(&path, &bytes).expect("corrupt");
        assert!(
            ImportInputReader::open(&path, "operation", Arc::clone(&cipher))
                .expect("reader")
                .read_all(&digest, length)
                .is_err()
        );
        bytes.pop();
        std::fs::write(&path, &bytes).expect("truncate");
        assert!(
            ImportInputReader::open(&path, "operation", Arc::clone(&cipher))
                .expect("reader")
                .read_all(&digest, length)
                .is_err()
        );
        std::fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn reader_rejects_oversized_declared_chunk_before_allocating() {
        let root = test_root();
        let path = root.join("operation.upload");
        std::fs::write(&path, u64::MAX.to_be_bytes()).expect("header");
        let reader =
            ImportInputReader::open(&path, "operation", Arc::new(leased_cipher())).expect("reader");
        assert!(reader.read_all("", 0).is_err());
        std::fs::remove_dir_all(root).expect("cleanup");
    }
}
