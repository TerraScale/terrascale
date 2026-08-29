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

/// The fixed frame magic every verified frame starts with.
const FRAME_MAGIC: u32 = 0x544C_534D;

/// The result of scanning a WAL file: the verified entries, the byte
/// length of the verified prefix, and whether damage sits mid-file.
pub(super) struct WalScan {
    /// Every entry of every verified frame, in append order.
    pub(super) entries: Vec<Entry>,
    /// The byte length of the verified prefix. A file longer than this holds
    /// a torn or unverified trailing frame.
    pub(super) good_bytes: u64,
    /// A verifying frame exists beyond the first damaged one: the damage is
    /// mid-file, not an interrupted trailing append, and the open must fail
    /// closed instead of silently truncating live history.
    pub(super) mid_file_damage: bool,
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
        let key = reader.take(key_len)?.to_owned();
        let sequence = reader.read_u64()?;
        let deletion = reader.read_u8()?;
        let value_len = reader.read_u32()? as usize;
        let value = reader.take(value_len)?.to_owned();
        entries.push(Entry { key: InternalKey::decode(&key, sequence, deletion != 0), value });
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
}

impl WalLog {
    /// Opens or creates the WAL at `path`, truncating any torn tail.
    pub(super) fn open(path: &Path) -> io::Result<Self> {
        let file = OpenOptions::new().create(true).read(true).append(true).open(path)?;
        let scan = scan_file(&file)?;
        if scan.mid_file_damage {
            // Live verified history sits behind the damage: fail closed
            // rather than truncate it away.
            return Err(io::Error::new(io::ErrorKind::InvalidData, "wal mid-file damage"));
        }
        let verified = scan.good_bytes;
        let len = file.metadata()?.len();
        if len > verified {
            // The trailing frame is torn or does not verify: the interrupted
            // append of a commit that never acknowledged. Drop it.
            file.set_len(verified)?;
            file.sync_all()?;
        }
        Ok(Self { file, path_len: verified })
    }

    /// Appends one commit batch as a single checksummed frame.
    ///
    /// The write is one `write_all` of the fully built frame, so a partial
    /// frame is only ever the tail.
    pub(super) fn append(&mut self, entries: &[Entry]) -> io::Result<()> {
        let payload = encode_frame(entries);
        let mut frame = Vec::with_capacity(payload.len() + 12);
        codec::push_u32(&mut frame, FRAME_MAGIC);
        let checksum = codec::fnv1a(&payload);
        let checksum = (checksum & 0xFFFF_FFFF) | (u64::from(FRAME_MAGIC) << 32);
        codec::push_u64(&mut frame, checksum);
        #[allow(clippy::cast_possible_truncation)]
        let payload_len = payload.len() as u32;
        codec::push_u32(&mut frame, payload_len);
        frame.extend_from_slice(&payload);
        self.file.write_all(&frame)?;
        self.path_len += frame.len() as u64;
        Ok(())
    }

    /// Replays every verified entry. The caller applies them idempotently.
    pub(super) fn scan(&self) -> io::Result<WalScan> {
        scan_file(&self.file)
    }

    /// Fsncs the appended bytes to stable storage.
    pub(super) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }

    /// Truncates the WAL to zero after a flush checkpoint published every
    /// entry it held into segments.
    pub(super) fn truncate_after_checkpoint(&mut self) -> io::Result<()> {
        self.file.set_len(0)?;
        self.file.sync_all()?;
        self.path_len = 0;
        Ok(())
    }

    /// The current WAL byte length.
    pub(super) fn len(&self) -> u64 {
        self.path_len
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
            // The declared payload runs past end of file: a trailing torn
            // frame.
            break;
        }
        let payload = &bytes[start..end];
        let actual = (codec::fnv1a(payload) & 0xFFFF_FFFF) | (u64::from(FRAME_MAGIC) << 32);
        let verified = actual == checksum;
        let decoded = if verified { decode_frame(payload) } else { None };
        let Some(mut decoded) = decoded else {
            // Checksum or structure failure: mid-file exactly when bytes
            // sit behind the damaged frame's declared end (the root
            // store's `classify` rule).
            mid_file_damage = end < bytes.len();
            break;
        };
        entries.append(&mut decoded);
        offset = end;
    }
    Ok(WalScan { entries, good_bytes: offset as u64, mid_file_damage })
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
    use crate::types::{CollectionName, DocumentId, TenantId};

    fn doc_entry(tenant: &str, id: &str, sequence: u64, value: &[u8]) -> Entry {
        Entry {
            key: InternalKey::document(
                &TenantId::try_new(tenant).expect("valid tenant"),
                &CollectionName::try_new("c").expect("valid collection"),
                &DocumentId::try_new(id).expect("valid document"),
                sequence,
            ),
            value: value.to_vec(),
        }
    }

    #[test]
    fn frames_round_trip_through_scan() {
        let dir = std::env::temp_dir().join(format!("tbv2-lsm-wal-{}", std::process::id()));
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
        assert_eq!(scan.entries[1].value, b"two");
        assert_eq!(scan.good_bytes, wal.len());
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn torn_tail_truncates_at_open() {
        let dir = std::env::temp_dir().join(format!("tbv2-lsm-wal-torn-{}", std::process::id()));
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
    fn corrupt_frame_stops_the_scan() {
        let dir = std::env::temp_dir().join(format!("tbv2-lsm-wal-corrupt-{}", std::process::id()));
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
        let reopened = WalLog::open(&path).unwrap();
        let rescan = reopened.scan().unwrap();
        assert_eq!(rescan.entries.len(), 1, "the corrupt frame does not replay");
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn checkpoint_truncation_empties_the_log() {
        let dir = std::env::temp_dir().join(format!("tbv2-lsm-wal-ck-{}", std::process::id()));
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
