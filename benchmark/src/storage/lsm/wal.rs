//! The write-ahead log of the LSM engine (P1).
//!
//! One frame carries one whole ordered commit batch — document entries,
//! ledger record, and history record together — so a commit is durable or
//! absent as a unit. Frames are checksummed; a torn trailing frame is the
//! interrupted append of a commit that never acknowledged, so recovery
//! truncates to the last verified frame. This is the manifest-anchored WAL
//! repair the W3-1 adjudication (PD-2026-08-24-03 R6) anticipated for the
//! segmented engine: the segment manifest is the anchor, the WAL holds only
//! the unflushed tail, and truncation loses nothing that was ever
//! acknowledged.
//!
//! Acknowledged `Synced` commits fsync per commit batch (the batch is the
//! group). `Written` commits rely on the appended bytes without a sync.
//! `Buffered` commits write no frame at all and report their weaker
//! durability honestly.

use std::{
    fs::{File, OpenOptions},
    io::{self, Read, Seek, Write},
    path::Path,
};

use super::super::codec;
use super::internal::{Entry, InternalKey};
use crate::encryption::{
    ArtifactCipher, ArtifactKind, Compression, DatabaseKey, DatabaseKeyVersion, EncryptionContext,
    KeyPurpose, KeyScope, envelope_length_prefix,
};
use crate::storage::DatabaseOwner;

/// The fixed frame magic every verified frame starts with.
const FRAME_MAGIC: u32 = 0x544C_534D;
const ENCRYPTED_MAGIC: &[u8; 4] = b"TBE1";
const ORDINAL_MAGIC: &[u8; 8] = b"TBWALOR1";
const ORDINAL_BYTES: usize = 8 + 16 + 32;

/// The result of scanning a WAL file: the verified entries, the byte
/// length of the verified prefix, and whether damage sits mid-file.
pub(super) struct WalScan {
    /// Every entry of every verified frame, in append order.
    pub(super) entries: Vec<Entry>,
    /// The byte length of the verified prefix. A file longer than this holds
    /// a torn or unverified trailing frame.
    pub(super) good_bytes: u64,
    /// The total byte length observed by the scan.
    pub(super) total_bytes: u64,
    /// A complete frame failed its checksum or structure, or a frame boundary
    /// was damaged before later bytes. The open must fail closed instead of
    /// silently truncating acknowledged history.
    pub(super) mid_file_damage: bool,
    /// Every verified frame end, in append order.
    frame_ends: Vec<u64>,
}

impl WalScan {
    /// Returns the raw frame boundaries used by the independent BLAKE3
    /// frontier. The frontier chains the complete 16-byte header and payload.
    pub(super) fn frame_ends(&self) -> &[u64] {
        &self.frame_ends
    }
}

/// The exact bytes appended for one WAL batch.
#[derive(Clone, Debug)]
pub(super) struct WalAppend {
    pub(super) start: u64,
    pub(super) frame: Vec<u8>,
}

/// Encryption inputs for a WAL.  The database key is moved into the cipher
/// and never retained by the storage owner after construction.
pub(crate) struct WalEncryptionConfig {
    pub(crate) owner: DatabaseOwner,
    pub(crate) key: DatabaseKey,
    pub(crate) key_version: DatabaseKeyVersion,
}

struct WalCipher {
    cipher: ArtifactCipher,
    ordinal_path: std::path::PathBuf,
}

/// Encodes one commit batch into a WAL frame payload.
///
/// The payload is `[entry_count u32]` followed by each entry as
/// `[key_len u32][key][sequence u64][kind u8][value_len u32][value]`.
pub(super) fn encode_frame(entries: &[Entry]) -> Vec<u8> {
    let mut payload = Vec::new();
    #[allow(clippy::cast_possible_truncation)]
    let count = entries.len() as u32;
    codec::push_u32(&mut payload, count);
    for entry in entries {
        encode_entry(&mut payload, entry);
    }
    payload
}

/// Appends one entry's encoding to `out`.
fn encode_entry(out: &mut Vec<u8>, entry: &Entry) {
    #[allow(clippy::cast_possible_truncation)]
    let key_len = entry.key.user_key().len() as u32;
    codec::push_u32(out, key_len);
    out.extend_from_slice(entry.key.user_key());
    codec::push_u64(out, entry.key.sequence());
    codec::push_u8(out, u8::from(entry.key.is_deletion()));
    #[allow(clippy::cast_possible_truncation)]
    let value_len = entry.value.len() as u32;
    codec::push_u32(out, value_len);
    out.extend_from_slice(&entry.value);
}

/// Decodes every entry of one frame payload, failing closed on any
/// structural error or trailing byte.
fn decode_frame(payload: &[u8]) -> Option<Vec<Entry>> {
    let mut reader = codec::Reader::new(payload);
    let count = reader.read_u32()? as usize;
    let mut entries = Vec::with_capacity(count.min(4096));
    for _ in 0..count {
        let key_len = reader.read_u32()? as usize;
        if key_len > 4096 {
            return None;
        }
        let key = reader.take(key_len)?.to_owned();
        let sequence = reader.read_u64()?;
        let deletion = match reader.read_u8()? {
            0 => false,
            1 => true,
            _ => return None,
        };
        let value_len = reader.read_u32()? as usize;
        let value = reader.take(value_len)?.to_owned();
        let entry = Entry { key: InternalKey::decode(&key, sequence, deletion)?, value };
        if !super::persisted_entry_is_semantically_valid(&entry) {
            return None;
        }
        entries.push(entry);
    }
    if reader.remaining() != 0 {
        return None;
    }
    Some(entries)
}

/// The append-only WAL writer of one LSM location.
pub(super) struct WalLog {
    file: File,
    path_len: u64,
    encrypted: bool,
    cipher: Option<WalCipher>,
}

impl WalLog {
    /// Opens or creates the WAL at `path`, truncating a verified torn tail.
    ///
    /// Legacy recovery uses this helper. Activated recovery calls
    /// [`Self::open_unrepaired`] and verifies the independent frontier before
    /// it permits any truncation.
    #[cfg(test)]
    pub(super) fn open(path: &Path) -> io::Result<Self> {
        let mut wal = Self::open_unrepaired(path)?;
        let scan = wal.scan()?;
        if scan.mid_file_damage {
            // Live verified history sits behind the damage: fail closed
            // rather than truncate it away.
            return Err(io::Error::new(io::ErrorKind::InvalidData, "wal mid-file damage"));
        }
        if scan.total_bytes > scan.good_bytes {
            // The trailing frame is structurally incomplete, which is the
            // only form this append path can prove was never acknowledged.
            // Complete frames that fail verification were refused above.
            wal.truncate(scan.good_bytes)?;
        }
        Ok(wal)
    }

    /// Opens or creates the WAL without changing its bytes.
    pub(super) fn open_unrepaired(path: &Path) -> io::Result<Self> {
        let file = OpenOptions::new().create(true).read(true).append(true).open(path)?;
        let path_len = file.metadata()?.len();
        Ok(Self { file, path_len, encrypted: false, cipher: None })
    }

    /// Opens an encrypted WAL for a newly created or already encrypted
    /// location. A legacy plaintext WAL remains readable for local
    /// compatibility, but it is never selected for a fresh file.
    pub(super) fn open_unrepaired_with_encryption(
        path: &Path,
        config: WalEncryptionConfig,
    ) -> io::Result<Self> {
        let file = OpenOptions::new().create(true).read(true).append(true).open(path)?;
        let path_len = file.metadata()?.len();
        let mut prefix = [0_u8; 4];
        let mut probe = file.try_clone()?;
        use std::io::Read as _;
        let prefix_len = probe.read(&mut prefix)?;
        let legacy = path_len != 0 && prefix_len >= 4 && prefix == FRAME_MAGIC.to_le_bytes();
        if legacy {
            return Ok(Self { file, path_len, encrypted: false, cipher: None });
        }
        if path_len != 0 && (prefix_len < 4 || prefix != *ENCRYPTED_MAGIC) {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "unknown WAL format"));
        }
        let scope = KeyScope::new_with_project(
            config.owner.environment().as_str(),
            config.owner.tenant(),
            config.owner.project(),
            config.owner.database(),
            KeyPurpose::Data,
        )
        .map_err(encryption_io)?;
        let context = EncryptionContext::for_artifact(
            &scope,
            ArtifactKind::Wal,
            config.owner.database().value(),
            config.key_version,
            1,
            Compression::None,
        )
        .map_err(encryption_io)?;
        let ordinal_path = path.with_file_name("wal.ordinal");
        let next_ordinal = load_or_create_ordinal(&ordinal_path, path_len != 0)?;
        let cipher = ArtifactCipher::with_next_ordinal(
            crate::encryption::ProviderMode::LocalFile,
            config.key,
            context,
            next_ordinal,
        )
        .map_err(encryption_io)?;
        Ok(Self {
            file,
            path_len,
            encrypted: true,
            cipher: Some(WalCipher { cipher, ordinal_path }),
        })
    }

    /// Appends one commit batch as a single checksummed frame.
    ///
    /// The write is one `write_all` of the fully built frame, so a partial
    /// frame is only ever the tail.
    pub(super) fn append(&mut self, entries: &[Entry]) -> io::Result<WalAppend> {
        let payload = encode_frame(entries);
        let mut frame = Vec::with_capacity(payload.len() + 16);
        codec::push_u32(&mut frame, FRAME_MAGIC);
        let checksum = codec::fnv1a(&payload);
        let checksum = (checksum & 0xFFFF_FFFF) | (u64::from(FRAME_MAGIC) << 32);
        codec::push_u64(&mut frame, checksum);
        #[allow(clippy::cast_possible_truncation)]
        let payload_len = payload.len() as u32;
        codec::push_u32(&mut frame, payload_len);
        frame.extend_from_slice(&payload);
        let frame = if self.encrypted {
            let wal_cipher = self.cipher.as_mut().ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidData, "encrypted WAL cipher missing")
            })?;
            let reservation = wal_cipher.cipher.reserve_ordinal().map_err(encryption_io)?;
            persist_ordinal(&wal_cipher.ordinal_path, reservation.next_ordinal())?;
            wal_cipher.cipher.seal_reserved(reservation, &frame).map_err(encryption_io)?
        } else {
            frame
        };
        let start = self.path_len;
        self.file.write_all(&frame)?;
        let frame_len = u64::try_from(frame.len())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "wal frame is too large"))?;
        self.path_len = self
            .path_len
            .checked_add(frame_len)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "wal file is too large"))?;
        Ok(WalAppend { start, frame })
    }

    /// Replays every verified entry. The caller applies them idempotently.
    pub(super) fn scan(&self) -> io::Result<WalScan> {
        if self.encrypted {
            scan_encrypted_file(
                &self.file,
                self.cipher.as_ref().ok_or_else(|| {
                    io::Error::new(io::ErrorKind::InvalidData, "encrypted WAL cipher missing")
                })?,
            )
        } else {
            scan_file(&self.file)
        }
    }

    /// Fsncs the appended bytes to stable storage.
    pub(super) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }

    /// Truncates the WAL to zero after a flush checkpoint published every
    /// entry it held into segments.
    #[cfg(test)]
    pub(super) fn truncate_after_checkpoint(&mut self) -> io::Result<()> {
        self.truncate(0)
    }

    /// Truncates the file only after the caller has independently verified
    /// that every removed byte is either checkpointed or an unacknowledged
    /// torn suffix.
    pub(super) fn truncate(&mut self, bytes: u64) -> io::Result<()> {
        self.set_len_unsynced(bytes)?;
        self.sync_all()
    }

    /// Changes the WAL length without claiming the metadata is durable yet.
    pub(super) fn set_len_unsynced(&mut self, bytes: u64) -> io::Result<()> {
        self.file.set_len(bytes)?;
        self.path_len = bytes;
        Ok(())
    }

    /// Syncs WAL data and metadata, including a preceding truncation.
    pub(super) fn sync_all(&mut self) -> io::Result<()> {
        self.file.sync_all()
    }

    /// The current WAL byte length.
    pub(super) fn len(&self) -> u64 {
        self.path_len
    }
}

/// A shareable sync handle to one open WAL file, held outside the storage
/// lock so a group-commit leader can witness its batch's bytes without
/// blocking the next batch's append (the pipelined group-commit shape).
///
/// `fdatasync` operates on the file's inode, not on the descriptor, so a
/// sync through this handle covers every byte any writer appended to the
/// same WAL before the sync began — including bytes written through
/// [`WalLog`]'s own descriptor. That is the entire contract the engine's
/// witness epoch needs: a sync that starts after a batch's append returns
/// proves that batch durable.
pub(crate) struct WalSyncHandle {
    file: File,
}

impl WalSyncHandle {
    /// Opens the WAL of the LSM location at `dir`, if it exists.
    ///
    /// `None` (absent or unreadable WAL) makes the caller fall back to the
    /// fully synchronous commit shape, which needs no handle.
    pub(crate) fn try_open(dir: &Path) -> Option<Self> {
        let file = OpenOptions::new().read(true).write(true).open(dir.join("wal")).ok()?;
        Some(Self { file })
    }

    /// Fdatasyncs the WAL: every byte appended before this call returns
    /// durable, on the next batch's appends as well as this one's.
    pub(crate) fn sync(&self) -> io::Result<()> {
        self.file.sync_data()
    }
}

/// Scans a WAL file from its start, returning verified entries and prefix.
fn scan_file(file: &File) -> io::Result<WalScan> {
    let mut file = file;
    file.rewind()?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    let mut entries = Vec::new();
    let mut offset = 0_usize;
    let mut mid_file_damage = false;
    let mut frame_ends = Vec::new();
    while offset < bytes.len() {
        if offset + 16 > bytes.len() {
            // A partial header can only be the trailing torn frame of an
            // interrupted append — appends write the frame magic first, so
            // a trailing region that does not even start with the magic is
            // damage, not an interrupted append.
            let region = &bytes[offset..];
            if region.len() >= 4 && region[..4] != FRAME_MAGIC.to_le_bytes() {
                mid_file_damage = true;
            }
            break;
        }
        let mut header = [0_u8; 16];
        header.copy_from_slice(&bytes[offset..offset + 16]);
        let magic = u32::from_le_bytes(header[0..4].try_into().expect("four bytes"));
        if magic != FRAME_MAGIC {
            // A full header's bytes exist but do not carry the frame magic:
            // bytes sit behind this position, so it is not a trailing
            // append.
            mid_file_damage = true;
            break;
        }
        let checksum = u64::from_le_bytes(header[4..12].try_into().expect("eight bytes"));
        let payload_len =
            u32::from_le_bytes(header[12..16].try_into().expect("four bytes")) as usize;
        let start = offset + 16;
        let Some(end) = start.checked_add(payload_len) else {
            break;
        };
        if end > bytes.len() {
            // A real interrupted append ends here. A corrupted in-range
            // length can also claim to run past EOF while swallowing later
            // valid frames, so a verified successor makes this integrity
            // damage instead.
            mid_file_damage = has_verified_frame_after(&bytes, offset);
            break;
        }
        let payload = &bytes[start..end];
        let actual = (codec::fnv1a(payload) & 0xFFFF_FFFF) | (u64::from(FRAME_MAGIC) << 32);
        let verified = actual == checksum;
        let decoded = if verified { decode_frame(payload) } else { None };
        let Some(mut decoded) = decoded else {
            // A complete frame may have been synced and acknowledged. No
            // payload byte pattern proves otherwise, so checksum or
            // structural failure refuses the location even at EOF.
            mid_file_damage = true;
            break;
        };
        entries.append(&mut decoded);
        offset = end;
        frame_ends.push(u64::try_from(offset).expect("a file offset fits u64"));
    }
    Ok(WalScan {
        entries,
        good_bytes: u64::try_from(offset).expect("a file offset fits u64"),
        total_bytes: u64::try_from(bytes.len()).expect("a file length fits u64"),
        mid_file_damage,
        frame_ends,
    })
}

fn scan_encrypted_file(file: &File, wal_cipher: &WalCipher) -> io::Result<WalScan> {
    let mut file = file;
    file.rewind()?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    let mut entries = Vec::new();
    let mut offset = 0_usize;
    let mut mid_file_damage = false;
    let mut frame_ends = Vec::new();
    let mut required_next = 0_u128;
    while offset < bytes.len() {
        let remaining = &bytes[offset..];
        if remaining.len() < 29 {
            mid_file_damage = remaining.len() < 4 || remaining[..4] != *ENCRYPTED_MAGIC;
            break;
        }
        if remaining[..4] != *ENCRYPTED_MAGIC {
            mid_file_damage = true;
            break;
        }
        let envelope_len = match envelope_length_prefix(remaining) {
            Ok(length) => length,
            Err(_) => {
                mid_file_damage = true;
                break;
            }
        };
        let Some(envelope) = remaining.get(..envelope_len) else {
            // The append was interrupted after writing a strict envelope
            // prefix; the authenticated unit is a torn trailing frame.
            break;
        };
        let plaintext = match wal_cipher.cipher.open(envelope) {
            Ok(plaintext) => plaintext,
            Err(_) => {
                mid_file_damage = true;
                break;
            }
        };
        if plaintext.len() < 16
            || u32::from_le_bytes(plaintext[..4].try_into().expect("frame magic")) != FRAME_MAGIC
        {
            mid_file_damage = true;
            break;
        }
        let checksum = u64::from_le_bytes(plaintext[4..12].try_into().expect("frame checksum"));
        let payload_len = usize::try_from(u32::from_le_bytes(
            plaintext[12..16].try_into().expect("frame length"),
        ))
        .unwrap_or(usize::MAX);
        let Some(payload_end) = 16_usize.checked_add(payload_len) else {
            mid_file_damage = true;
            break;
        };
        if payload_end != plaintext.len() {
            mid_file_damage = true;
            break;
        }
        let payload = &plaintext[16..];
        let actual = (codec::fnv1a(payload) & 0xFFFF_FFFF) | (u64::from(FRAME_MAGIC) << 32);
        let Some(mut decoded) = (actual == checksum).then(|| decode_frame(payload)).flatten()
        else {
            mid_file_damage = true;
            break;
        };
        let mut ordinal_bytes = [0_u8; 16];
        ordinal_bytes[4..].copy_from_slice(&envelope[5..17]);
        let ordinal = u128::from_be_bytes(ordinal_bytes);
        required_next = required_next.max(ordinal.saturating_add(1));
        entries.append(&mut decoded);
        offset = offset.saturating_add(envelope_len);
        frame_ends.push(u64::try_from(offset).expect("a file offset fits u64"));
    }
    if !mid_file_damage && wal_cipher.cipher.next_ordinal() < required_next {
        mid_file_damage = true;
    }
    Ok(WalScan {
        entries,
        good_bytes: u64::try_from(offset).expect("a file offset fits u64"),
        total_bytes: u64::try_from(bytes.len()).expect("a file length fits u64"),
        mid_file_damage,
        frame_ends,
    })
}

fn encryption_io(error: crate::encryption::EncryptionError) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}

fn ordinal_checksum(body: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"terrabase.lsm.wal.ordinal.v1\0");
    hasher.update(body);
    *hasher.finalize().as_bytes()
}

fn load_or_create_ordinal(path: &Path, required: bool) -> io::Result<u128> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink()
                || !metadata.is_file()
                || metadata.len() != ORDINAL_BYTES as u64
            {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "unsafe WAL ordinal state"));
            }
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt as _;
                if metadata.permissions().mode() & 0o077 != 0 {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidData,
                        "unsafe WAL ordinal permissions",
                    ));
                }
            }
            let bytes = std::fs::read(path)?;
            if bytes[..8] != *ORDINAL_MAGIC || bytes[24..] != ordinal_checksum(&bytes[..24]) {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "corrupt WAL ordinal state",
                ));
            }
            Ok(u128::from_be_bytes(bytes[8..24].try_into().expect("ordinal bytes")))
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound && !required => {
            persist_ordinal(path, 0)?;
            Ok(0)
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            Err(io::Error::new(io::ErrorKind::InvalidData, "missing WAL ordinal state"))
        }
        Err(error) => Err(error),
    }
}

fn persist_ordinal(path: &Path, ordinal: u128) -> io::Result<()> {
    let mut body = Vec::with_capacity(ORDINAL_BYTES);
    body.extend_from_slice(ORDINAL_MAGIC);
    body.extend_from_slice(&ordinal.to_be_bytes());
    body.extend_from_slice(&ordinal_checksum(&body));
    let temp = path.with_file_name(format!(
        "{}.tmp-{}",
        path.file_name().and_then(|n| n.to_str()).unwrap_or("wal.ordinal"),
        std::process::id()
    ));
    let mut options = OpenOptions::new();
    options.create(true).truncate(true).write(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.mode(0o600);
    }
    let mut file = options.open(&temp)?;
    file.write_all(&body)?;
    file.sync_all()?;
    std::fs::rename(&temp, path)?;
    if let Some(parent) = path.parent() {
        std::fs::File::open(parent)?.sync_data()?;
    }
    Ok(())
}

/// Reports whether a complete, self-verifying frame begins after a damaged
/// frame start.
fn has_verified_frame_after(bytes: &[u8], damaged_offset: usize) -> bool {
    let Some(search_start) = damaged_offset.checked_add(1) else {
        return false;
    };
    let Some(last_start) = bytes.len().checked_sub(16) else {
        return false;
    };
    if search_start > last_start {
        return false;
    }
    (search_start..=last_start).any(|candidate| {
        if bytes[candidate..candidate + 4] != FRAME_MAGIC.to_le_bytes() {
            return false;
        }
        let checksum = u64::from_le_bytes(
            bytes[candidate + 4..candidate + 12].try_into().expect("eight checksum bytes"),
        );
        let payload_len = u32::from_le_bytes(
            bytes[candidate + 12..candidate + 16].try_into().expect("four length bytes"),
        ) as usize;
        let payload_start = candidate + 16;
        let Some(payload_end) = payload_start.checked_add(payload_len) else {
            return false;
        };
        let Some(payload) = bytes.get(payload_start..payload_end) else {
            return false;
        };
        let actual = (codec::fnv1a(payload) & 0xFFFF_FFFF) | (u64::from(FRAME_MAGIC) << 32);
        actual == checksum && decode_frame(payload).is_some()
    })
}

#[cfg(test)]
impl WalLog {
    /// Appends raw bytes without framing. Test-only: it manufactures the
    /// torn tail of an interrupted append.
    fn append_raw_for_test(&mut self, bytes: &[u8]) {
        use std::io::Write;
        self.file.write_all(bytes).unwrap();
        self.path_len += bytes.len() as u64;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{CollectionName, Document, DocumentId, DocumentValue, TenantId};

    fn doc_entry(tenant: &str, id: &str, sequence: u64, value: &[u8]) -> Entry {
        let mut document = Document::new(
            DocumentId::try_new(id).expect("valid document"),
            CollectionName::try_new("c").expect("valid collection"),
            DocumentValue::String(String::from_utf8(value.to_vec()).expect("test value is UTF-8")),
        );
        document.set_version(sequence);
        let mut encoded = Vec::new();
        codec::encode_document(&mut encoded, &document);
        Entry {
            key: InternalKey::document(
                &TenantId::try_new(tenant).expect("valid tenant"),
                &CollectionName::try_new("c").expect("valid collection"),
                &DocumentId::try_new(id).expect("valid document"),
                sequence,
            ),
            value: encoded,
        }
    }

    fn resign_frame(frame: &mut [u8]) {
        let checksum = (codec::fnv1a(&frame[16..]) & 0xFFFF_FFFF) | (u64::from(FRAME_MAGIC) << 32);
        frame[4..12].copy_from_slice(&checksum.to_le_bytes());
    }

    #[test]
    fn frames_round_trip_through_scan() {
        let dir = std::env::temp_dir().join(format!("terrabase-lsm-wal-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("wal");
        let _ = std::fs::remove_file(&path);
        let mut wal = WalLog::open(&path).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        wal.append(&[doc_entry("t", "b", 2, b"two")]).unwrap();
        wal.sync().unwrap();
        let scan = wal.scan().unwrap();
        assert_eq!(scan.entries.len(), 2);
        assert_eq!(scan.entries[0].key.sequence(), 1);
        let mut reader = codec::Reader::new(&scan.entries[1].value);
        assert_eq!(
            codec::decode_document(&mut reader).unwrap().value(),
            &DocumentValue::String("two".to_owned())
        );
        assert_eq!(scan.good_bytes, wal.len());
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn fully_rechecksummed_hostile_entry_semantics_fail_closed() {
        let dir = std::env::temp_dir()
            .join(format!("terrabase-lsm-wal-hostile-semantics-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let canonical = dir.join("canonical");
        let mut wal = WalLog::open(&canonical).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        wal.sync().unwrap();
        drop(wal);
        let original = std::fs::read(&canonical).unwrap();
        let key_len =
            usize::try_from(u32::from_le_bytes(original[20..24].try_into().unwrap())).unwrap();
        let key_start = 24;
        let kind_offset = key_start + key_len + 8;
        let value_start = kind_offset + 1 + 4;

        let mut unknown_keyspace = original.clone();
        unknown_keyspace[key_start] = 0x7f;
        resign_frame(&mut unknown_keyspace);

        let mut invalid_kind = original.clone();
        invalid_kind[kind_offset] = 2;
        resign_frame(&mut invalid_kind);

        let mut mismatched_value = original;
        let document_id_byte = value_start + 4 + 1 + 4;
        mismatched_value[document_id_byte] = b'b';
        resign_frame(&mut mismatched_value);

        for (tag, bytes) in [
            ("unknown-keyspace", unknown_keyspace),
            ("invalid-kind", invalid_kind),
            ("mismatched-value", mismatched_value),
        ] {
            let path = dir.join(tag);
            std::fs::write(&path, bytes).unwrap();
            assert_eq!(
                WalLog::open(&path).err().expect("hostile frame must be refused").kind(),
                io::ErrorKind::InvalidData,
                "{tag}"
            );
        }
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn torn_tail_truncates_at_open() {
        let dir =
            std::env::temp_dir().join(format!("terrabase-lsm-wal-torn-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("wal");
        let _ = std::fs::remove_file(&path);
        let mut wal = WalLog::open(&path).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        wal.sync().unwrap();
        let good = wal.len();
        // Simulate an interrupted append: a real frame's little-endian
        // magic, then a cut header.
        let partial = vec![0x4D, 0x53, 0x4C, 0x54, 0x00];
        wal.append_raw_for_test(&partial);
        let reopened = WalLog::open(&path).unwrap();
        let scan = reopened.scan().unwrap();
        assert_eq!(scan.entries.len(), 1, "only the verified frame replays");
        assert_eq!(scan.good_bytes, good);
        assert_eq!(std::fs::metadata(&path).unwrap().len(), good);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_complete_checksum_failure_refuses_the_location_even_at_the_tail() {
        let dir =
            std::env::temp_dir().join(format!("terrabase-lsm-wal-corrupt-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("wal");
        let _ = std::fs::remove_file(&path);
        let mut wal = WalLog::open(&path).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        wal.append(&[doc_entry("t", "b", 2, b"two")]).unwrap();
        wal.sync().unwrap();
        let scan = wal.scan().unwrap();
        assert_eq!(scan.entries.len(), 2);
        // Flip a payload byte inside the second frame: the scan must stop at
        // the corruption boundary rather than replay garbage.
        let mut bytes = std::fs::read(&path).unwrap();
        let last = bytes.len() - 1;
        bytes[last] ^= 0xFF;
        std::fs::write(&path, &bytes).unwrap();
        drop(wal);
        let error = WalLog::open(&path).err().expect("the corrupt location is refused");
        assert_eq!(error.kind(), io::ErrorKind::InvalidData);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_complete_zero_filled_final_payload_refuses_the_location() {
        let dir = std::env::temp_dir()
            .join(format!("terrabase-lsm-wal-zero-filled-tail-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("wal");
        let _ = std::fs::remove_file(&path);
        let mut wal = WalLog::open(&path).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        let good = wal.len();
        wal.append(&[doc_entry("t", "b", 2, b"two")]).unwrap();
        wal.sync().unwrap();
        drop(wal);

        let mut bytes = std::fs::read(&path).unwrap();
        let good_index = usize::try_from(good).expect("test WAL length fits usize");
        bytes[good_index + 16..].fill(0);
        std::fs::write(&path, bytes).unwrap();

        let error = WalLog::open(&path).err().expect("the corrupt location is refused");
        assert_eq!(error.kind(), io::ErrorKind::InvalidData);
        assert!(
            std::fs::metadata(&path).unwrap().len() > good,
            "recovery must not erase a complete frame that may have been acknowledged"
        );
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_zero_filled_middle_payload_refuses_the_location() {
        let dir = std::env::temp_dir()
            .join(format!("terrabase-lsm-wal-zero-filled-middle-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("wal");
        let _ = std::fs::remove_file(&path);
        let mut wal = WalLog::open(&path).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        let first_end = usize::try_from(wal.len()).expect("test WAL length fits usize");
        wal.append(&[doc_entry("t", "b", 2, b"two")]).unwrap();
        wal.sync().unwrap();
        drop(wal);

        let mut bytes = std::fs::read(&path).unwrap();
        bytes[16..first_end].fill(0);
        std::fs::write(&path, bytes).unwrap();

        let error = WalLog::open(&path).err().expect("middle-frame damage is refused");
        assert_eq!(error.kind(), io::ErrorKind::InvalidData);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_corrupt_length_cannot_swallow_a_later_verified_frame() {
        let dir = std::env::temp_dir()
            .join(format!("terrabase-lsm-wal-length-swallow-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("wal");
        let _ = std::fs::remove_file(&path);
        let mut wal = WalLog::open(&path).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        wal.append(&[doc_entry("t", "b", 2, b"two")]).unwrap();
        wal.sync().unwrap();
        drop(wal);

        let mut bytes = std::fs::read(&path).unwrap();
        let swallowed = u32::try_from(bytes.len()).expect("the test WAL fits a frame length");
        bytes[12..16].copy_from_slice(&swallowed.to_le_bytes());
        std::fs::write(&path, bytes).unwrap();

        let error = WalLog::open(&path).err().expect("the damaged location is refused");
        assert_eq!(error.kind(), io::ErrorKind::InvalidData);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn checkpoint_truncation_empties_the_log() {
        let dir = std::env::temp_dir().join(format!("terrabase-lsm-wal-ck-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("wal");
        let _ = std::fs::remove_file(&path);
        let mut wal = WalLog::open(&path).unwrap();
        wal.append(&[doc_entry("t", "a", 1, b"one")]).unwrap();
        wal.truncate_after_checkpoint().unwrap();
        assert_eq!(wal.len(), 0);
        assert_eq!(std::fs::metadata(&path).unwrap().len(), 0);
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
