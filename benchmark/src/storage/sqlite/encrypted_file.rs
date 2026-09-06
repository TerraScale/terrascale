//! Authenticated logical SQLite files. Native SQLite locks a stable stub inode;
//! this companion contains only authenticated records and is atomically compacted.
//!
//! Every complete record authenticates its file identity, sequence, logical size,
//! block address and bytes. A partial final record is an interrupted append and
//! is discarded on recovery; a complete record with invalid authentication is
//! never skipped. SQLite's WAL ordering remains authoritative for transactions.

use std::{collections::BTreeMap, fs::{File, OpenOptions}, io::{self, Read, Seek, SeekFrom, Write}, os::unix::fs::OpenOptionsExt, path::{Path, PathBuf}, sync::Arc};

use crate::encryption::{ArtifactCipher, ArtifactKind, Compression, DatabaseKeySource, DatabaseKeyVersion, EncryptionContext};

const MAGIC: &[u8; 8] = b"TBSQL001";
const HEADER: u64 = 40;
const BLOCK: usize = 4096;
const RECORD: usize = 8192;
const MAX_FILE: u64 = 1 << 40;
const PAYLOAD_HEADER: usize = 57;
const ENVELOPE_OFFSET: usize = 66;

fn invalid() -> io::Error { io::Error::new(io::ErrorKind::InvalidData, "encrypted SQLite storage authentication failed") }
fn crypt(error: crate::encryption::EncryptionError) -> io::Error { io::Error::other(error) }

pub(crate) fn companion(path: &Path) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(".tbe");
    PathBuf::from(name)
}
fn suffix(path: &Path, tail: &str) -> PathBuf {
    let mut name = path.as_os_str().to_owned(); name.push(tail); PathBuf::from(name)
}
fn secure_options() -> OpenOptions {
    let mut options = OpenOptions::new();
    options.read(true).write(true).mode(0o600).custom_flags(rustix::fs::OFlags::NOFOLLOW.bits() as i32);
    options
}
fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes { text.push(char::from(DIGITS[(byte >> 4) as usize])); text.push(char::from(DIGITS[(byte & 15) as usize])); }
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
}

impl EncryptedFile {
    pub(crate) fn open(path: &Path, keys: Arc<dyn DatabaseKeySource>, kind: ArtifactKind) -> io::Result<Self> {
        use std::os::unix::fs::PermissionsExt;
        keys.current_lease().map_err(crypt)?;
        let backing = companion(path);
        let lock = secure_options().create(true).truncate(false).open(suffix(path, ".tbe.lock"))?;
        lock.try_lock().map_err(io::Error::other)?;
        let (mut file, fresh) = match secure_options().create_new(true).open(&backing) {
            Ok(file) => (file, true),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => (secure_options().open(&backing)?, false),
            Err(error) => return Err(error),
        };
        let metadata = file.metadata()?;
        if !metadata.is_file() || metadata.permissions().mode() & 0o077 != 0 { return Err(invalid()); }
        let mut identity = [0; 32];
        if fresh {
            aws_lc_rs::rand::fill(&mut identity).map_err(|_| invalid())?;
            file.write_all(MAGIC)?; file.write_all(&identity)?;
        } else {
            let mut magic = [0; 8]; file.read_exact(&mut magic)?;
            if &magic != MAGIC { return Err(invalid()); }
            file.read_exact(&mut identity)?;
        }
        let role = if path.as_os_str().as_encoded_bytes().ends_with(b"-journal") { "rollback" } else if path.as_os_str().as_encoded_bytes().ends_with(b"-wal") { "wal" } else { "main" };
        let mut result = Self { path: backing, file, _lock: lock, keys, kind, role, identity, blocks: BTreeMap::new(), length: 0, records: 0, poisoned: false };
        if fresh {
            result.append(0, 0, &[])?;
            result.sync()?;
        } else {
            let bytes = result.file.metadata()?.len();
            if bytes < HEADER + RECORD as u64 { return Err(invalid()); }
            let count = (bytes - HEADER) / RECORD as u64;
            for sequence in 0..count {
                let payload = result.decode(sequence)?;
                result.apply(&payload, sequence)?;
                result.records += 1;
            }
            // Only a physically incomplete final fixed-size record is a tear.
            // Malformed complete records already failed authentication above.
            let end = HEADER + count * RECORD as u64;
            if bytes != end { result.file.set_len(end)?; result.file.sync_all()?; }
        }
        Ok(result)
    }

    pub(crate) fn size(&self) -> u64 { self.length }

    fn check(&self) -> io::Result<()> {
        if self.poisoned { return Err(invalid()); }
        self.keys.current_lease().map_err(crypt)?;
        Ok(())
    }

    fn context(&self, lease: &crate::encryption::DatabaseKeyLease, id: &[u8]) -> io::Result<EncryptionContext> {
        let metadata = lease.metadata(); let scope = metadata.scope();
        let artifact_id = format!("{}-{}", self.role, hex(id));
        let context = if let Some(project) = scope.project() {
            EncryptionContext::new_with_project(scope.environment(), scope.tenant().to_text(), project.to_text(), scope.database().to_text(), self.kind.as_str(), artifact_id, metadata.version().to_text(), 1, Compression::None)
        } else {
            EncryptionContext::new(scope.environment(), scope.tenant().to_text(), scope.database().to_text(), self.kind.as_str(), artifact_id, metadata.version().to_text(), 1, Compression::None)
        };
        context.map_err(crypt)
    }

    fn encode(&self, sequence: u64, block: u64, length: u64, bytes: &[u8]) -> io::Result<[u8; RECORD]> {
        let mut output = [0; RECORD];
        let lease = self.keys.current_lease().map_err(crypt)?;
        let version = lease.metadata().version().to_text();
        if version.len() != 26 { return Err(invalid()); }
        output[4..30].copy_from_slice(version.as_bytes());
        // A fresh 256-bit artifact domain for every record means ordinal zero
        // is never reused under a derived key, including recovery/compaction.
        aws_lc_rs::rand::fill(&mut output[30..62]).map_err(|_| invalid())?;
        let context = self.context(&lease, &output[30..62])?;
        let mut cipher = ArtifactCipher::from_database_key_lease(&lease, context, 0).map_err(crypt)?;
        let mut payload = Vec::with_capacity(PAYLOAD_HEADER + bytes.len());
        payload.push(u8::from(!bytes.is_empty()));
        payload.extend_from_slice(&self.identity);
        payload.extend_from_slice(&sequence.to_le_bytes());
        payload.extend_from_slice(&length.to_le_bytes());
        payload.extend_from_slice(&block.to_le_bytes());
        payload.extend_from_slice(bytes);
        let sealed = cipher.seal(&payload).map_err(crypt)?;
        if sealed.len() > RECORD - ENVELOPE_OFFSET { return Err(invalid()); }
        output[..4].copy_from_slice(&(sealed.len() as u32).to_le_bytes());
        output[ENVELOPE_OFFSET..ENVELOPE_OFFSET + sealed.len()].copy_from_slice(&sealed);
        Ok(output)
    }

    fn decode(&mut self, sequence: u64) -> io::Result<Vec<u8>> {
        let mut record = [0; RECORD];
        self.file.seek(SeekFrom::Start(HEADER + sequence * RECORD as u64))?;
        self.file.read_exact(&mut record)?;
        let length = u32::from_le_bytes(record[..4].try_into().map_err(|_| invalid())?) as usize;
        if length == 0 || length > RECORD - ENVELOPE_OFFSET || record[62..66] != [0; 4] || record[ENVELOPE_OFFSET + length..].iter().any(|byte| *byte != 0) { return Err(invalid()); }
        let version = std::str::from_utf8(&record[4..30]).ok().and_then(|text| DatabaseKeyVersion::from_text(text).ok()).ok_or_else(invalid)?;
        let lease = self.keys.lease(version).map_err(crypt)?;
        let context = self.context(&lease, &record[30..62])?;
        let cipher = ArtifactCipher::from_database_key_lease(&lease, context, 1).map_err(crypt)?;
        let payload = cipher.open(&record[ENVELOPE_OFFSET..ENVELOPE_OFFSET + length]).map_err(crypt)?;
        if payload.len() < PAYLOAD_HEADER || payload[1..33] != self.identity || read_u64(&payload[33..41])? != sequence { return Err(invalid()); }
        Ok(payload)
    }

    fn apply(&mut self, payload: &[u8], sequence: u64) -> io::Result<()> {
        let length = read_u64(&payload[41..49])?;
        let block = read_u64(&payload[49..57])?;
        if length > MAX_FILE { return Err(invalid()); }
        match payload[0] {
            0 if payload.len() == PAYLOAD_HEADER && block == 0 => {},
            1 if payload.len() == PAYLOAD_HEADER + BLOCK && block < MAX_FILE / BLOCK as u64 => { self.blocks.insert(block, sequence); },
            _ => return Err(invalid()),
        }
        self.length = length;
        self.blocks.retain(|block, _| *block * (BLOCK as u64) < length);
        Ok(())
    }

    fn append(&mut self, block: u64, length: u64, bytes: &[u8]) -> io::Result<()> {
        let record = self.encode(self.records, block, length, bytes)?;
        if let Err(error) = self.file.seek(SeekFrom::Start(HEADER + self.records * RECORD as u64)) { self.poisoned = true; return Err(error); }
        if let Err(error) = self.file.write_all(&record) { self.poisoned = true; return Err(error); }
        self.length = length;
        if !bytes.is_empty() { self.blocks.insert(block, self.records); }
        self.records += 1;
        Ok(())
    }

    fn block(&mut self, block: u64) -> io::Result<[u8; BLOCK]> {
        let mut output = [0; BLOCK];
        if let Some(sequence) = self.blocks.get(&block).copied() {
            let payload = self.decode(sequence)?;
            if payload.len() != PAYLOAD_HEADER + BLOCK || payload[0] != 1 || read_u64(&payload[49..57])? != block { return Err(invalid()); }
            output.copy_from_slice(&payload[PAYLOAD_HEADER..]);
        }
        let start = block * BLOCK as u64;
        if self.length < start + BLOCK as u64 {
            output[self.length.saturating_sub(start) as usize..].fill(0);
        }
        Ok(output)
    }

    pub(crate) fn read_exact_at(&mut self, output: &mut [u8], offset: u64) -> io::Result<()> {
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
            output[destination..destination + count].copy_from_slice(&bytes[within..within + count]);
            cursor += count as u64;
        }
        if end > self.length { return Err(io::Error::from(io::ErrorKind::UnexpectedEof)); }
        Ok(())
    }

    pub(crate) fn write_all_at(&mut self, bytes: &[u8], offset: u64) -> io::Result<()> {
        self.check()?;
        let end = offset.checked_add(bytes.len() as u64).filter(|end| *end <= MAX_FILE).ok_or_else(invalid)?;
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
        if length > MAX_FILE { return Err(invalid()); }
        // Capture a shortened partial block so later extension cannot expose
        // bytes from before a truncate, even across restart and compaction.
        if length < self.length && length % BLOCK as u64 != 0 {
            let block = length / BLOCK as u64;
            let mut bytes = self.block(block)?;
            bytes[(length % BLOCK as u64) as usize..].fill(0);
            self.append(block, length, &bytes)?;
        } else { self.append(0, length, &[])?; }
        self.blocks.retain(|block, _| *block * (BLOCK as u64) < length);
        self.compact_if_needed()
    }

    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.check()?;
        self.file.sync_all()?;
        File::open(self.path.parent().ok_or_else(invalid)?)?.sync_all()
    }

    fn compact_if_needed(&mut self) -> io::Result<()> {
        // Bounds stale ciphertext to two live images plus 512 KiB, even for
        // WAL resets. No plaintext staging file is ever created.
        if self.records <= (self.blocks.len() as u64 + 1) * 2 + 64 { return Ok(()); }
        self.compact()
    }

    fn compact(&mut self) -> io::Result<()> {
        let temporary = suffix(&self.path, ".compact");
        // A previous interrupted compaction has no authority: only the main
        // name, atomically replaced after fsync, is recoverable.
        match std::fs::remove_file(&temporary) { Ok(()) => {}, Err(error) if error.kind() == io::ErrorKind::NotFound => {}, Err(error) => return Err(error) }
        let mut target = secure_options().create_new(true).open(&temporary)?;
        target.write_all(MAGIC)?; target.write_all(&self.identity)?;
        target.write_all(&self.encode(0, 0, self.length, &[])?)?;
        let blocks: Vec<_> = self.blocks.keys().copied().collect();
        let mut index = BTreeMap::new();
        for (position, block) in blocks.into_iter().enumerate() {
            let bytes = self.block(block)?;
            let sequence = position as u64 + 1;
            target.write_all(&self.encode(sequence, block, self.length, &bytes)?)?;
            index.insert(block, sequence);
        }
        target.sync_all()?;
        std::fs::rename(&temporary, &self.path)?;
        self.file = target;
        self.records = index.len() as u64 + 1;
        self.blocks = index;
        File::open(self.path.parent().ok_or_else(invalid)?)?.sync_all()
    }
}

fn read_u64(bytes: &[u8]) -> io::Result<u64> { Ok(u64::from_le_bytes(bytes.try_into().map_err(|_| invalid())?)) }
