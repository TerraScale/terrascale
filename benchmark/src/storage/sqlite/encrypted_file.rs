//! Authenticated logical SQLite files. Native SQLite locks a stable stub inode;
//! this companion contains only authenticated records and is atomically compacted.
//!
//! An atomically published authenticated manifest selects a generation and its
//! durable record frontier. Recovery validates that entire prefix, retains valid
//! unsynced records, and discards interrupted tails, including full-sized tears. Sector-aligned appends never
//! rewrite a durable sector. SQLite WAL ordering remains authoritative.

use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    os::unix::fs::OpenOptionsExt,
    path::{Path, PathBuf},
    sync::Arc,
};

use crate::encryption::{
    ArtifactCipher, ArtifactKind, Compression, DatabaseKeySource, DatabaseKeyVersion,
    EncryptionContext,
};

const MAGIC: &[u8; 8] = b"TBSQL001";
const HEADER: u64 = 4096;
const MANIFEST_HEADER: usize = 40;
const BLOCK: usize = 4096;
const RECORD: usize = 8192;
const MAX_FILE: u64 = 1 << 40;
const PAYLOAD_HEADER: usize = 57;
const ENVELOPE_OFFSET: usize = 66;

fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "encrypted SQLite storage authentication failed")
}
fn crypt(error: crate::encryption::EncryptionError) -> io::Error {
    io::Error::other(error)
}

pub(crate) fn companion(path: &Path) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(".tbe");
    PathBuf::from(name)
}
fn suffix(path: &Path, tail: &str) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(tail);
    PathBuf::from(name)
}
fn secure_options() -> OpenOptions {
    let mut options = OpenOptions::new();
    options
        .read(true)
        .write(true)
        .mode(0o600)
        .custom_flags(rustix::fs::OFlags::NOFOLLOW.bits() as i32);
    options
}
fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        text.push(char::from(DIGITS[(byte >> 4) as usize]));
        text.push(char::from(DIGITS[(byte & 15) as usize]));
    }
    text
}

/// One process exclusively owns each encrypted logical file. All connections in
/// that process share this handle behind a mutex; no key or plaintext is stored
/// in the locking stub or in temporary compaction files.
pub(crate) struct EncryptedFile {
    path: PathBuf,
    file: File,
    _lock: File,
    keys: Arc<dyn DatabaseKeySource>,
    kind: ArtifactKind,
    role: &'static str,
    identity: [u8; 32],
    blocks: BTreeMap<u64, u64>,
    length: u64,
    records: u64,
    poisoned: bool,
    root: [u8; 32],
}

struct FileTiming {
    stage: &'static str,
    role: &'static str,
    records: u64,
    blocks: usize,
    started: Option<std::time::Instant>,
}

impl FileTiming {
    fn start(stage: &'static str, role: &'static str, records: u64, blocks: usize) -> Self {
        Self {
            stage,
            role,
            records,
            blocks,
            started: std::env::var_os("TERRASCALE_AUTH_TIMING").map(|_| std::time::Instant::now()),
        }
    }
}

impl Drop for FileTiming {
    fn drop(&mut self) {
        if let Some(started) = self.started {
            let micros = started.elapsed().as_micros();
            if micros >= 10_000 {
                eprintln!(
                    "[encrypted timing] stage={} role={} micros={micros} records={} blocks={}",
                    self.stage, self.role, self.records, self.blocks
                );
            }
        }
    }
}

impl EncryptedFile {
    pub(crate) fn open(
        path: &Path,
        keys: Arc<dyn DatabaseKeySource>,
        kind: ArtifactKind,
    ) -> io::Result<Self> {
        Self::open_with_create(path, keys, kind, true)
    }

    pub(crate) fn open_existing(
        path: &Path,
        keys: Arc<dyn DatabaseKeySource>,
        kind: ArtifactKind,
    ) -> io::Result<Self> {
        Self::open_with_create(path, keys, kind, false)
    }

    fn open_with_create(
        path: &Path,
        keys: Arc<dyn DatabaseKeySource>,
        kind: ArtifactKind,
        allow_create: bool,
    ) -> io::Result<Self> {
        use std::os::unix::fs::PermissionsExt;
        keys.current_lease().map_err(crypt)?;
        let backing = companion(path);
        let lock = secure_options()
            .create(allow_create)
            .truncate(false)
            .open(suffix(path, ".tbe.lock"))?;
        lock.try_lock().map_err(io::Error::other)?;
        let role = if path.as_os_str().as_encoded_bytes().ends_with(b"-journal") {
            "rollback"
        } else if path.as_os_str().as_encoded_bytes().ends_with(b"-wal") {
            "wal"
        } else {
            "main"
        };
        let _timing = FileTiming::start("open", role, 0, 0);
        let mut manifest = [0; MANIFEST_HEADER + RECORD];
        let fresh = match secure_options().open(&backing) {
            Ok(mut file) => {
                let metadata = file.metadata()?;
                if !metadata.is_file()
                    || metadata.permissions().mode() & 0o077 != 0
                    || metadata.len() != manifest.len() as u64
                {
                    return Err(invalid());
                }
                file.read_exact(&mut manifest)?;
                if &manifest[..8] != MAGIC {
                    return Err(invalid());
                }
                false
            }
            Err(error) if allow_create && error.kind() == io::ErrorKind::NotFound => true,
            Err(error) => return Err(error),
        };
        let mut identity = [0; 32];
        let file = if fresh {
            aws_lc_rs::rand::fill(&mut identity).map_err(|_| invalid())?;
            new_generation(&backing, &identity)?
        } else {
            identity.copy_from_slice(&manifest[8..40]);
            secure_options().open(generation(&backing, &identity))?
        };
        let metadata = file.metadata()?;
        if !metadata.is_file() || metadata.permissions().mode() & 0o077 != 0 {
            return Err(invalid());
        }
        let mut result = Self {
            path: backing,
            file,
            _lock: lock,
            keys,
            kind,
            role,
            identity,
            blocks: BTreeMap::new(),
            length: 0,
            records: 0,
            poisoned: false,
            root: [0; 32],
        };
        if fresh {
            result.append(0, 0, &[])?;
            result.sync()?;
        } else {
            let frontier = result.unseal(&manifest[MANIFEST_HEADER..])?;
            if frontier.len() != PAYLOAD_HEADER + 32
                || frontier[0] != 2
                || frontier[1..33] != identity
                || read_u64(&frontier[49..57])? != 0
            {
                return Err(invalid());
            }
            let count = read_u64(&frontier[33..41])?;
            let end = count
                .checked_mul(RECORD as u64)
                .and_then(|n| n.checked_add(HEADER))
                .ok_or_else(invalid)?;
            if count == 0 || metadata.len() < end {
                return Err(invalid());
            }
            let mut header = [0; HEADER as usize];
            result.file.read_exact(&mut header)?;
            if &header[..8] != MAGIC
                || header[8..40] != identity
                || header[40..].iter().any(|b| *b != 0)
            {
                return Err(invalid());
            }
            for sequence in 0..count {
                let mut record = [0; RECORD];
                result.file.seek(SeekFrom::Start(HEADER + sequence * RECORD as u64))?;
                result.file.read_exact(&mut record)?;
                result.root = chain(&result.root, &record);
                let payload = result.decode(sequence)?;
                result.apply(&payload, sequence)?;
                result.records += 1;
            }
            if result.root != frontier[PAYLOAD_HEADER..]
                || result.length != read_u64(&frontier[41..49])?
            {
                return Err(invalid());
            }
            // Preserve complete authenticated unsynced writes after process
            // death (LocalDurability::Written). A malformed/torn suffix is
            // discardable only beyond the verified durable frontier. Key
            // authorization errors are never treated as interrupted writes.
            let complete = (metadata.len() - HEADER) / RECORD as u64;
            for sequence in count..complete {
                let payload = match result.decode(sequence) {
                    Ok(payload) => payload,
                    Err(error) if error.kind() == io::ErrorKind::InvalidData => break,
                    Err(error) => return Err(error),
                };
                if result.apply(&payload, sequence).is_err() {
                    break;
                }
                let mut record = [0; RECORD];
                result.file.seek(SeekFrom::Start(HEADER + sequence * RECORD as u64))?;
                result.file.read_exact(&mut record)?;
                result.root = chain(&result.root, &record);
                result.records += 1;
            }
            let end = HEADER + result.records * RECORD as u64;
            if metadata.len() != end {
                result.file.set_len(end)?;
                result.file.sync_all()?;
            }
        }
        result.cleanup_generations()?;
        Ok(result)
    }

    pub(crate) fn size(&self) -> u64 {
        self.length
    }

    fn check(&self) -> io::Result<()> {
        if self.poisoned {
            return Err(invalid());
        }
        self.keys.current_lease().map_err(crypt)?;
        Ok(())
    }

    fn context(
        &self,
        lease: &crate::encryption::DatabaseKeyLease,
        id: &[u8],
    ) -> io::Result<EncryptionContext> {
        let metadata = lease.metadata();
        let scope = metadata.scope();
        let artifact_id = format!("{}-{}", self.role, hex(id));
        let context = if let Some(project) = scope.project() {
            EncryptionContext::new_with_project(
                scope.environment(),
                scope.tenant().to_text(),
                project.to_text(),
                scope.database().to_text(),
                self.kind.as_str(),
                artifact_id,
                metadata.version().to_text(),
                1,
                Compression::None,
            )
        } else {
            EncryptionContext::new(
                scope.environment(),
                scope.tenant().to_text(),
                scope.database().to_text(),
                self.kind.as_str(),
                artifact_id,
                metadata.version().to_text(),
                1,
                Compression::None,
            )
        };
        context.map_err(crypt)
    }

    fn encode(
        &self,
        sequence: u64,
        block: u64,
        length: u64,
        bytes: &[u8],
    ) -> io::Result<[u8; RECORD]> {
        self.encode_for(&self.identity, u8::from(!bytes.is_empty()), sequence, block, length, bytes)
    }

    fn encode_for(
        &self,
        identity: &[u8; 32],
        tag: u8,
        sequence: u64,
        block: u64,
        length: u64,
        bytes: &[u8],
    ) -> io::Result<[u8; RECORD]> {
        let mut output = [0; RECORD];
        let lease = self.keys.current_lease().map_err(crypt)?;
        let version = lease.metadata().version().to_text();
        if version.len() != 26 {
            return Err(invalid());
        }
        output[4..30].copy_from_slice(version.as_bytes());
        // A fresh 256-bit artifact domain for every record means ordinal zero
        // is never reused under a derived key, including recovery/compaction.
        aws_lc_rs::rand::fill(&mut output[30..62]).map_err(|_| invalid())?;
        let context = self.context(&lease, &output[30..62])?;
        let mut cipher =
            ArtifactCipher::from_database_key_lease(&lease, context, 0).map_err(crypt)?;
        let mut payload = Vec::with_capacity(PAYLOAD_HEADER + bytes.len());
        payload.push(tag);
        payload.extend_from_slice(identity);
        payload.extend_from_slice(&sequence.to_le_bytes());
        payload.extend_from_slice(&length.to_le_bytes());
        payload.extend_from_slice(&block.to_le_bytes());
        payload.extend_from_slice(bytes);
        let sealed = cipher.seal(&payload).map_err(crypt)?;
        if sealed.len() > RECORD - ENVELOPE_OFFSET {
            return Err(invalid());
        }
        output[..4].copy_from_slice(&(sealed.len() as u32).to_le_bytes());
        output[ENVELOPE_OFFSET..ENVELOPE_OFFSET + sealed.len()].copy_from_slice(&sealed);
        Ok(output)
    }

    fn decode(&mut self, sequence: u64) -> io::Result<Vec<u8>> {
        let mut record = [0; RECORD];
        self.file.seek(SeekFrom::Start(HEADER + sequence * RECORD as u64))?;
        self.file.read_exact(&mut record)?;
        let payload = self.unseal(&record)?;
        if payload.len() < PAYLOAD_HEADER
            || payload[1..33] != self.identity
            || read_u64(&payload[33..41])? != sequence
        {
            return Err(invalid());
        }
        Ok(payload)
    }

    fn unseal(&self, record: &[u8]) -> io::Result<Vec<u8>> {
        if record.len() != RECORD {
            return Err(invalid());
        }
        let length = u32::from_le_bytes(record[..4].try_into().map_err(|_| invalid())?) as usize;
        if length == 0
            || length > RECORD - ENVELOPE_OFFSET
            || record[62..66] != [0; 4]
            || record[ENVELOPE_OFFSET + length..].iter().any(|byte| *byte != 0)
        {
            return Err(invalid());
        }
        let version = std::str::from_utf8(&record[4..30])
            .ok()
            .and_then(|text| DatabaseKeyVersion::from_text(text).ok())
            .ok_or_else(invalid)?;
        let lease = self.keys.lease(version).map_err(crypt)?;
        let context = self.context(&lease, &record[30..62])?;
        let cipher = ArtifactCipher::from_database_key_lease(&lease, context, 1).map_err(crypt)?;
        let payload =
            cipher.open(&record[ENVELOPE_OFFSET..ENVELOPE_OFFSET + length]).map_err(|error| {
                if matches!(
                    error,
                    crate::encryption::EncryptionError::AuthenticationFailed
                        | crate::encryption::EncryptionError::MalformedEnvelope
                        | crate::encryption::EncryptionError::DigestMismatch
                        | crate::encryption::EncryptionError::SizeLimit
                ) {
                    invalid()
                } else {
                    crypt(error)
                }
            })?;
        Ok(payload)
    }

    fn apply(&mut self, payload: &[u8], sequence: u64) -> io::Result<()> {
        let length = read_u64(&payload[41..49])?;
        let block = read_u64(&payload[49..57])?;
        if length > MAX_FILE {
            return Err(invalid());
        }
        match payload[0] {
            0 if payload.len() == PAYLOAD_HEADER && block == 0 => {}
            1 if payload.len() == PAYLOAD_HEADER + BLOCK && block < MAX_FILE / BLOCK as u64 => {
                self.blocks.insert(block, sequence);
            }
            _ => return Err(invalid()),
        }
        self.length = length;
        self.blocks.retain(|block, _| *block * (BLOCK as u64) < length);
        Ok(())
    }

    fn append(&mut self, block: u64, length: u64, bytes: &[u8]) -> io::Result<()> {
        let record = self.encode(self.records, block, length, bytes)?;
        if let Err(error) = self.file.seek(SeekFrom::Start(HEADER + self.records * RECORD as u64)) {
            self.poisoned = true;
            return Err(error);
        }
        if let Err(error) = self.file.write_all(&record) {
            self.poisoned = true;
            return Err(error);
        }
        self.root = chain(&self.root, &record);
        self.length = length;
        if !bytes.is_empty() {
            self.blocks.insert(block, self.records);
        }
        self.records += 1;
        Ok(())
    }

    fn block(&mut self, block: u64) -> io::Result<[u8; BLOCK]> {
        let mut output = [0; BLOCK];
        if let Some(sequence) = self.blocks.get(&block).copied() {
            let payload = self.decode(sequence)?;
            if payload.len() != PAYLOAD_HEADER + BLOCK
                || payload[0] != 1
                || read_u64(&payload[49..57])? != block
            {
                return Err(invalid());
            }
            output.copy_from_slice(&payload[PAYLOAD_HEADER..]);
        }
        let start = block * BLOCK as u64;
        if self.length < start + BLOCK as u64 {
            output[self.length.saturating_sub(start) as usize..].fill(0);
        }
        Ok(output)
    }

    pub(crate) fn read_exact_at(&mut self, output: &mut [u8], offset: u64) -> io::Result<()> {
        let _timing = FileTiming::start("read", self.role, self.records, self.blocks.len());
        self.check()?;
        output.fill(0);
        let end = offset.checked_add(output.len() as u64).ok_or_else(invalid)?;
        let mut cursor = offset;
        while cursor < end.min(self.length) {
            let block = cursor / BLOCK as u64;
            let bytes = self.block(block)?;
            let within = (cursor % BLOCK as u64) as usize;
            let count = (BLOCK - within).min((end.min(self.length) - cursor) as usize);
            let destination = (cursor - offset) as usize;
            output[destination..destination + count]
                .copy_from_slice(&bytes[within..within + count]);
            cursor += count as u64;
        }
        if end > self.length {
            return Err(io::Error::from(io::ErrorKind::UnexpectedEof));
        }
        Ok(())
    }

    pub(crate) fn write_all_at(&mut self, bytes: &[u8], offset: u64) -> io::Result<()> {
        let _timing = FileTiming::start("write", self.role, self.records, self.blocks.len());
        self.check()?;
        let end = offset
            .checked_add(bytes.len() as u64)
            .filter(|end| *end <= MAX_FILE)
            .ok_or_else(invalid)?;
        let mut cursor = offset;
        while cursor < end {
            let block = cursor / BLOCK as u64;
            let mut content = self.block(block)?;
            let within = (cursor % BLOCK as u64) as usize;
            let count = (BLOCK - within).min((end - cursor) as usize);
            let source = (cursor - offset) as usize;
            content[within..within + count].copy_from_slice(&bytes[source..source + count]);
            self.append(block, self.length.max(cursor + count as u64), &content)?;
            cursor += count as u64;
        }
        self.compact_if_needed()
    }

    pub(crate) fn set_len(&mut self, length: u64) -> io::Result<()> {
        self.check()?;
        if length > MAX_FILE {
            return Err(invalid());
        }
        // Capture a shortened partial block so later extension cannot expose
        // bytes from before a truncate, even across restart and compaction.
        if length < self.length && length % BLOCK as u64 != 0 {
            let block = length / BLOCK as u64;
            let mut bytes = self.block(block)?;
            bytes[(length % BLOCK as u64) as usize..].fill(0);
            self.append(block, length, &bytes)?;
        } else {
            self.append(0, length, &[])?;
        }
        self.blocks.retain(|block, _| *block * (BLOCK as u64) < length);
        self.compact_if_needed()
    }

    pub(crate) fn sync(&mut self) -> io::Result<()> {
        let _timing = FileTiming::start("sync", self.role, self.records, self.blocks.len());
        self.check()?;
        let result =
            self.publish(&self.identity, self.records, self.length, &self.root, &self.file);
        if result.is_err() {
            self.poisoned = true;
        }
        result
    }

    fn publish(
        &self,
        identity: &[u8; 32],
        count: u64,
        length: u64,
        root: &[u8; 32],
        file: &File,
    ) -> io::Result<()> {
        file.sync_all()?;
        fault("generation_synced")?;
        let directory = File::open(self.path.parent().ok_or_else(invalid)?)?;
        // The generation name must survive before its manifest can select it.
        directory.sync_all()?;
        fault("generation_directory_synced")?;
        let temporary = suffix(&self.path, ".manifest-next");
        remove_if_present(&temporary)?;
        let mut manifest = secure_options().create_new(true).open(&temporary)?;
        manifest.write_all(MAGIC)?;
        manifest.write_all(identity)?;
        manifest.write_all(&self.encode_for(identity, 2, count, 0, length, root)?)?;
        fault("manifest_written")?;
        manifest.sync_all()?;
        fault("manifest_synced")?;
        std::fs::rename(&temporary, &self.path)?;
        fault("manifest_renamed")?;
        directory.sync_all()?;
        fault("manifest_directory_synced")
    }

    pub(crate) fn reencrypt(&mut self) -> io::Result<()> {
        self.check()?;
        let result = self.compact();
        if result.is_err() {
            self.poisoned = true;
        }
        result
    }

    fn compact_if_needed(&mut self) -> io::Result<()> {
        if self.records <= (self.blocks.len() as u64 + 1) * 2 + 64 {
            return Ok(());
        }
        let result = self.compact();
        if result.is_err() {
            self.poisoned = true;
        }
        result
    }

    fn compact(&mut self) -> io::Result<()> {
        let _timing = FileTiming::start("compact", self.role, self.records, self.blocks.len());
        let mut identity = [0; 32];
        aws_lc_rs::rand::fill(&mut identity).map_err(|_| invalid())?;
        let mut target = new_generation(&self.path, &identity)?;
        let record = self.encode_for(&identity, 0, 0, 0, self.length, &[])?;
        target.write_all(&record)?;
        let mut root = chain(&[0; 32], &record);
        let blocks: Vec<_> = self.blocks.keys().copied().collect();
        let mut index = BTreeMap::new();
        for (position, block) in blocks.into_iter().enumerate() {
            let bytes = self.block(block)?;
            let sequence = position as u64 + 1;
            let record = self.encode_for(&identity, 1, sequence, block, self.length, &bytes)?;
            target.write_all(&record)?;
            root = chain(&root, &record);
            index.insert(block, sequence);
        }
        let count = index.len() as u64 + 1;
        // Atomic manifest publication selects the entire fresh generation.
        // Until its directory fsync succeeds the old generation is retained.
        self.publish(&identity, count, self.length, &root, &target)?;
        self.file = target;
        self.identity = identity;
        self.records = count;
        self.blocks = index;
        self.root = root;
        self.cleanup_generations()
    }

    fn cleanup_generations(&self) -> io::Result<()> {
        let selected = generation(&self.path, &self.identity);
        for path in generations(&self.path)? {
            if path != selected {
                remove_if_present(&path)?;
            }
        }
        remove_if_present(&suffix(&self.path, ".manifest-next"))?;
        File::open(self.path.parent().ok_or_else(invalid)?)?.sync_all()
    }
}

fn read_u64(bytes: &[u8]) -> io::Result<u64> {
    Ok(u64::from_le_bytes(bytes.try_into().map_err(|_| invalid())?))
}

fn chain(previous: &[u8; 32], record: &[u8]) -> [u8; 32] {
    let mut digest = aws_lc_rs::digest::Context::new(&aws_lc_rs::digest::SHA256);
    digest.update(previous);
    digest.update(record);
    digest.finish().as_ref().try_into().expect("SHA256 length")
}

fn generation(path: &Path, identity: &[u8; 32]) -> PathBuf {
    suffix(path, &format!(".gen-{}", hex(identity)))
}

fn new_generation(path: &Path, identity: &[u8; 32]) -> io::Result<File> {
    let mut file = secure_options().create_new(true).open(generation(path, identity))?;
    let mut header = [0; HEADER as usize];
    header[..8].copy_from_slice(MAGIC);
    header[8..40].copy_from_slice(identity);
    file.write_all(&header)?;
    Ok(file)
}

fn remove_if_present(path: &Path) -> io::Result<()> {
    match std::fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error),
    }
}

fn generations(path: &Path) -> io::Result<Vec<PathBuf>> {
    use std::os::unix::ffi::OsStrExt;
    let mut prefix = path.file_name().ok_or_else(invalid)?.as_bytes().to_vec();
    prefix.extend_from_slice(b".gen-");
    let mut paths = Vec::new();
    for entry in std::fs::read_dir(path.parent().ok_or_else(invalid)?)? {
        let entry = entry?;
        let name = entry.file_name();
        if let Some(id) = name.as_bytes().strip_prefix(prefix.as_slice()) {
            if id.len() == 64 && id.iter().all(u8::is_ascii_hexdigit) {
                paths.push(entry.path());
            }
        }
    }
    Ok(paths)
}

/// Remove authority durably before retiring any generation it could select.
/// The caller owns SQLite's deletion locks and excludes local file handles.
pub(crate) fn delete(path: &Path) -> io::Result<()> {
    // Physical replication handles use this lock without SQLite's native
    // locks. Exclude them across processes before removing durable authority.
    let lock = secure_options().create(true).truncate(false).open(suffix(path, ".tbe.lock"))?;
    lock.try_lock().map_err(io::Error::other)?;
    let backing = companion(path);
    remove_if_present(&backing)?;
    File::open(backing.parent().ok_or_else(invalid)?)?.sync_all()?;
    for path in generations(&backing)? {
        remove_if_present(&path)?;
    }
    remove_if_present(&suffix(&backing, ".manifest-next"))?;
    File::open(backing.parent().ok_or_else(invalid)?)?.sync_all()
}

#[cfg(test)]
thread_local! {
    static FAILURE: std::cell::Cell<Option<&'static str>> = const { std::cell::Cell::new(None) };
}

#[cfg(test)]
pub(crate) fn fail_next_publication_at(stage: &'static str) {
    FAILURE.set(Some(stage));
}

fn fault(stage: &str) -> io::Result<()> {
    #[cfg(test)]
    if FAILURE.with(|failure| {
        if failure.get() == Some(stage) {
            failure.set(None);
            true
        } else {
            false
        }
    }) {
        return Err(io::Error::other("injected encrypted frontier publication failure"));
    }
    let _ = stage;
    Ok(())
}
