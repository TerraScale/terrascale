//! Immutable disk segments of the LSM engine (P1).
//!
//! A segment file holds sorted internal keys in self-checksummed blocks, a
//! sparse index over every `SPARSE_STRIDE`-th key, and a verifying footer.
//! Readers keep only the sparse index in memory and read record ranges on
//! demand through positioned reads, so an open segment costs one footer plus
//! one index section, never the whole file.
//!
//! Layout (all integers little-endian):
//!
//! ```text
//! header   "TBLSMSG1" | segment_id u64 | level u16 | reserved u16
//! records  repeated: key_len u32 | key | sequence u64 | kind u8 | val_len u32 | value
//! sparse   count u32 | repeated: key_len u32 | key | offset u64
//! footer   "TBLSMFT1" | sparse_offset u64 | entry_count u64
//!          | min_key | max_key | crc u64 (over header+records+sparse)
//! ```
#![allow(clippy::cast_possible_truncation)]

use std::{
    fs::{File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::Path,
};

use super::super::codec;
use super::internal::{Entry, InternalKey};

const HEADER_MAGIC: &[u8; 8] = b"TBLSMSG1";
const FOOTER_MAGIC: &[u8; 8] = b"TBLSMFT1";
/// One sparse-index entry per this many records.
const SPARSE_STRIDE: usize = 32;

/// Segments with at most this many records keep their decoded records in
/// memory: the sparse-index read path costs a file read and a record
/// decode per query, which a small segment never pays back.
const CACHE_ENTRY_LIMIT: u64 = 8_192;

/// The process-wide budget of in-memory segment records. Segments are
/// immutable and readers come and go with compaction; the budget keeps
/// the resident set bounded no matter how many small segments exist.
const CACHE_ENTRY_BUDGET: usize = 262_144;

/// The process-wide count of in-memory segment records.
static CACHED_ENTRIES: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);

/// The immutable metadata of one segment, as the manifest records it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) struct SegmentMeta {
    /// The segment's unique identity.
    pub(super) id: u64,
    /// The merge level the segment lives at.
    pub(super) level: u16,
    /// The number of records the segment holds.
    pub(super) entry_count: u64,
    /// The segment file's byte length.
    pub(super) byte_len: u64,
    /// The smallest internal-key user key in the segment.
    pub(super) min_key: Vec<u8>,
    /// The largest internal-key user key in the segment.
    pub(super) max_key: Vec<u8>,
}

/// Writes one immutable segment file from sorted entries.
///
/// Returns the segment's metadata. The file is synced before returning; the
/// caller publishes it through the manifest.
pub(super) fn write_segment(
    path: &Path,
    id: u64,
    level: u16,
    entries: &[Entry],
) -> io::Result<SegmentMeta> {
    debug_assert!(entries.windows(2).all(|pair| pair[0].key <= pair[1].key) || entries.len() < 2);
    let mut file = OpenOptions::new().create(true).write(true).truncate(true).open(path)?;
    let mut body = Vec::new();
    body.extend_from_slice(HEADER_MAGIC);
    codec::push_u64(&mut body, id);
    codec::push_u16(&mut body, level);
    codec::push_u16(&mut body, 0);
    let mut sparse: Vec<(Vec<u8>, u64)> = Vec::new();
    for (index, entry) in entries.iter().enumerate() {
        if index % SPARSE_STRIDE == 0 {
            sparse.push((entry.key.user_key().to_vec(), body.len() as u64));
        }
        codec::push_u32(&mut body, entry.key.user_key().len() as u32);
        body.extend_from_slice(entry.key.user_key());
        codec::push_u64(&mut body, entry.key.sequence());
        codec::push_u8(&mut body, u8::from(entry.key.is_deletion()));
        codec::push_u32(&mut body, entry.value.len() as u32);
        body.extend_from_slice(&entry.value);
    }
    let sparse_offset = body.len() as u64;
    codec::push_u32(&mut body, sparse.len() as u32);
    for (key, offset) in &sparse {
        codec::push_u32(&mut body, key.len() as u32);
        body.extend_from_slice(key);
        codec::push_u64(&mut body, *offset);
    }
    let mut footer = Vec::new();
    footer.extend_from_slice(FOOTER_MAGIC);
    codec::push_u64(&mut footer, sparse_offset);
    codec::push_u64(&mut footer, entries.len() as u64);
    // Key lengths follow their bytes so the footer parses backwards from
    // the crc without knowing the key sizes first.
    let min_key = entries.first().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec());
    let max_key = entries.last().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec());
    footer.extend_from_slice(&min_key);
    codec::push_u32(&mut footer, min_key.len() as u32);
    footer.extend_from_slice(&max_key);
    codec::push_u32(&mut footer, max_key.len() as u32);
    let crc = codec::fnv1a(&body);
    codec::push_u64(&mut footer, crc);
    file.write_all(&body)?;
    file.write_all(&footer)?;
    file.sync_all()?;
    let byte_len = (body.len() + footer.len()) as u64;
    Ok(SegmentMeta { id, level, entry_count: entries.len() as u64, byte_len, min_key, max_key })
}

/// An open immutable segment reader.
pub(super) struct SegmentReader {
    file: File,
    /// The sparse index, in file order.
    sparse: Vec<(Vec<u8>, u64)>,
    meta: SegmentMeta,
    records_end: u64,
    /// The decoded records of a small segment, held for the reader's
    /// lifetime; `None` for large segments or while unbudgeted.
    cached: Option<Vec<Entry>>,
}

impl SegmentReader {
    /// Opens and verifies one segment file.
    ///
    /// Verification failure is an error: a segment named by a published
    /// manifest must verify, and an unnamed partial segment is simply not
    /// opened by recovery.
    pub(super) fn open(path: &Path, id: u64, level: u16) -> io::Result<Self> {
        let mut file = OpenOptions::new().read(true).open(path)?;
        let byte_len = file.metadata()?.len();
        if byte_len < 20 + 4 + 32 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "segment truncated"));
        }
        // The footer's fixed tail is magic(8) + sparse_offset(8) +
        // count(8) + crc(8) = 32 bytes; the keys add up to
        // MAX_KEY_BOUND * 2 at worst. Read a tail window covering it.
        let tail_bound = u64::try_from(32 + 2 * MAX_KEY_BOUND).expect("footer bound fits u64");
        let tail_window = usize::try_from(byte_len.min(tail_bound))
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "segment is too large"))?;
        let tail_start = byte_len - u64::try_from(tail_window).expect("tail length fits u64");
        let mut tail = vec![0_u8; tail_window];
        file.seek(SeekFrom::Start(tail_start))?;
        file.read_exact(&mut tail)?;
        let crc_end = tail.len();
        let crc_start = crc_end - 8;
        let crc: [u8; 8] = tail[crc_start..crc_end].try_into().expect("eight bytes");
        // Walk the footer fields backwards: crc, max_key, min_key,
        // entry_count, sparse_offset, magic — the keys are variable length
        // and sit between the fixed fields and the crc.
        let tail_bytes = &tail[..crc_start];
        let mut cursor = tail_bytes;
        let max_key = pop_key_back(&mut cursor)?;
        let min_key = pop_key_back(&mut cursor)?;
        // After the keys, the fixed tail reads (from the back): count,
        // sparse_offset, magic — the magic leads the footer going forward.
        if cursor.len() < 24 || &cursor[cursor.len() - 24..cursor.len() - 16] != FOOTER_MAGIC {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "segment footer magic"));
        }
        let fixed_at = |bytes: &[u8]| u64::from_le_bytes(bytes.try_into().expect("eight bytes"));
        let sparse_offset = fixed_at(&cursor[cursor.len() - 16..cursor.len() - 8]);
        let entry_count = fixed_at(&cursor[cursor.len() - 8..]);
        let footer_start = tail_start + (cursor.len() - 24) as u64;
        if sparse_offset < 20 || sparse_offset > footer_start {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "segment footer layout"));
        }
        // Verify the body checksum: header + records + sparse index.
        let body_len = usize::try_from(footer_start)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "segment is too large"))?;
        let mut body = vec![0_u8; body_len];
        file.rewind()?;
        file.read_exact(&mut body)?;
        if codec::fnv1a(&body) != fixed_at(&crc) {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "segment checksum"));
        }
        if body.len() < 20
            || &body[..8] != HEADER_MAGIC
            || u64::from_le_bytes(body[8..16].try_into().expect("segment id")) != id
            || u16::from_le_bytes(body[16..18].try_into().expect("segment level")) != level
            || body[18..20] != [0_u8; 2]
        {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "segment header"));
        }
        // Keep only the sparse index section in memory.
        let sparse_len = usize::try_from(footer_start - sparse_offset)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "sparse index is too large"))?;
        let mut sparse_bytes = vec![0_u8; sparse_len];
        file.seek(SeekFrom::Start(sparse_offset))?;
        file.read_exact(&mut sparse_bytes)?;
        let sparse = decode_sparse(&sparse_bytes)?;
        let records_end = usize::try_from(sparse_offset).map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidData, "record region is too large")
        })?;
        let (entries, offsets) = decode_record_region_at(&body[20..records_end], 20)?;
        let expected_count = u64::try_from(entries.len())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "too many segment records"))?;
        let actual_min =
            entries.first().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec());
        let actual_max =
            entries.last().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec());
        let expected_sparse = entries
            .iter()
            .zip(offsets)
            .enumerate()
            .filter(|(index, _)| index % SPARSE_STRIDE == 0)
            .map(|(_, (entry, offset))| (entry.key.user_key().to_vec(), offset))
            .collect::<Vec<_>>();
        if expected_count != entry_count
            || actual_min != min_key
            || actual_max != max_key
            || entries.windows(2).any(|pair| pair[0].key >= pair[1].key)
            || sparse != expected_sparse
        {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "segment semantic mismatch"));
        }
        Ok(Self {
            file,
            sparse,
            meta: SegmentMeta { id, level, entry_count, byte_len, min_key, max_key },
            records_end: sparse_offset,
            cached: None,
        })
    }

    /// Populates the decoded-record cache when this segment is small
    /// enough and the process-wide budget has room. Idempotent.
    fn cache_if_small(&mut self) {
        if self.cached.is_some() || self.meta.entry_count > CACHE_ENTRY_LIMIT {
            return;
        }
        let count = usize::try_from(self.meta.entry_count).unwrap_or(usize::MAX);
        let budget = CACHED_ENTRIES.load(std::sync::atomic::Ordering::Relaxed);
        if budget.saturating_add(count) > CACHE_ENTRY_BUDGET {
            return;
        }
        let Ok(all) = self.read_all_uncached() else {
            return;
        };
        CACHED_ENTRIES.fetch_add(count, std::sync::atomic::Ordering::Relaxed);
        self.cached = Some(all);
    }

    /// The cached window `[from, end)` of a small segment, binary-searched.
    ///
    /// The dense cache has no anchor slack, so the window is exact: it
    /// starts at the first record whose user key is `>= from` and ends
    /// before the first whose user key is `>= end` (or at the last record
    /// when `end` is `None`).
    fn cached_between(&self, from: Option<&[u8]>, end: Option<&[u8]>) -> Option<Vec<Entry>> {
        let cached = self.cached.as_ref()?;
        let low =
            from.map_or(0, |from| cached.partition_point(|entry| entry.key.user_key() < from));
        let high = end
            .map_or(cached.len(), |end| cached.partition_point(|entry| entry.key.user_key() < end));
        Some(cached[low..high].to_vec())
    }

    /// Returns the segment's metadata.
    pub(super) fn meta(&self) -> &SegmentMeta {
        &self.meta
    }

    /// Reads every record in the segment, in sorted order.
    ///
    /// Full scans serve merges and open-time index rebuilds; point reads use
    /// the sparse index through `read_from`.
    pub(super) fn read_all(&mut self) -> io::Result<Vec<Entry>> {
        if let Some(cached) = &self.cached {
            return Ok(cached.clone());
        }
        self.read_all_uncached()
    }

    /// Reads and decodes every record from the file, bypassing the cache.
    fn read_all_uncached(&mut self) -> io::Result<Vec<Entry>> {
        let mut body = vec![0_u8; self.records_end as usize];
        self.file.rewind()?;
        self.file.read_exact(&mut body)?;
        if body.len() < 20 || &body[..8] != HEADER_MAGIC {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "segment header"));
        }
        decode_record_region(&body[20..])
    }

    /// Reads the records from the first whose user key is `>= from` (or the
    /// first record when `from` is `None`) to the end of the segment.
    #[cfg(test)]
    pub(super) fn read_from(&mut self, from: Option<&[u8]>) -> io::Result<Vec<Entry>> {
        let Some(from) = from else {
            return self.read_all();
        };
        if self.cached.is_none() {
            self.cache_if_small();
        }
        if let Some(cached) = self.cached_between(Some(from), None) {
            return Ok(cached);
        }
        let start = self.seek_offset(from);
        let mut body = vec![0_u8; (self.records_end - start) as usize];
        self.file.seek(SeekFrom::Start(start))?;
        self.file.read_exact(&mut body)?;
        decode_record_region(&body)
    }

    /// Reads the records whose user keys lie in `[from, end)`: starting at
    /// the sparse anchor that reaches `from` (or the first record) and
    /// stopping at the first sparse anchor whose key is `>= end`, because
    /// every record from that anchor onward carries a key `>= end`.
    ///
    /// The sparse index makes both bounds approximate in the same safe
    /// direction: the read may include extra records below `from` (before
    /// the anchor) and at or above `end` (before the next anchor), and the
    /// caller's range filter keeps exactly `[from, end)`. It never omits a
    /// record inside the range.
    pub(super) fn read_between(
        &mut self,
        from: Option<&[u8]>,
        end: &[u8],
    ) -> io::Result<Vec<Entry>> {
        if self.cached.is_none() {
            self.cache_if_small();
        }
        if let Some(cached) = self.cached_between(from, Some(end)) {
            return Ok(cached);
        }
        let start = from.map_or(20_u64, |from| self.seek_offset(from));
        let stop = self.stop_offset(end).max(start).min(self.records_end);
        let mut body = vec![0_u8; (stop - start) as usize];
        self.file.seek(SeekFrom::Start(start))?;
        self.file.read_exact(&mut body)?;
        decode_record_region(&body)
    }

    /// The offset of the first sparse anchor whose key is `>= bound`, or
    /// the end of the record region when no anchor reaches `bound`.
    fn stop_offset(&self, bound: &[u8]) -> u64 {
        for (key, offset) in &self.sparse {
            if key.as_slice() >= bound {
                return *offset;
            }
        }
        self.records_end
    }
}

impl Drop for SegmentReader {
    fn drop(&mut self) {
        if let Some(cached) = self.cached.take() {
            CACHED_ENTRIES.fetch_sub(cached.len(), std::sync::atomic::Ordering::Relaxed);
        }
    }
}

/// The largest key the footer tail window can carry.
const MAX_KEY_BOUND: usize = 4096;

/// Pops one key-and-trailing-length pair off the back of `cursor`.
fn pop_key_back(cursor: &mut &[u8]) -> io::Result<Vec<u8>> {
    if cursor.len() < 4 {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "segment footer truncated"));
    }
    let len =
        u32::from_le_bytes(cursor[cursor.len() - 4..].try_into().expect("four bytes")) as usize;
    if cursor.len() < 4 + len || len > MAX_KEY_BOUND {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "segment footer key bound"));
    }
    let start = cursor.len() - 4 - len;
    let key = cursor[start..start + len].to_vec();
    *cursor = &cursor[..start];
    Ok(key)
}

/// Decodes the sparse index section.
fn decode_sparse(bytes: &[u8]) -> io::Result<Vec<(Vec<u8>, u64)>> {
    let mut reader = codec::Reader::new(bytes);
    let Some(count) = reader.read_u32() else {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "sparse index truncated"));
    };
    let count = count as usize;
    if count > 1_000_000 || count > reader.remaining() / 12 {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "sparse index count"));
    }
    let mut sparse = Vec::with_capacity(count.min(4096));
    for _ in 0..count {
        let Some(key_len) = reader.read_u32() else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "sparse entry truncated"));
        };
        if key_len as usize > MAX_KEY_BOUND {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "sparse key bound"));
        }
        let Some(key) = reader.take(key_len as usize) else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "sparse key truncated"));
        };
        let Some(offset) = reader.read_u64() else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "sparse offset truncated"));
        };
        sparse.push((key.to_owned(), offset));
    }
    if reader.remaining() != 0 {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "sparse index trailing bytes"));
    }
    Ok(sparse)
}

/// Decodes one raw record region (no header).
fn decode_record_region(region: &[u8]) -> io::Result<Vec<Entry>> {
    decode_record_region_at(region, 0).map(|(entries, _)| entries)
}

fn decode_record_region_at(region: &[u8], base_offset: u64) -> io::Result<(Vec<Entry>, Vec<u64>)> {
    let mut reader = codec::Reader::new(region);
    let mut entries = Vec::new();
    let mut offsets = Vec::new();
    while reader.remaining() > 0 {
        let consumed = region.len() - reader.remaining();
        let offset = base_offset
            .checked_add(u64::try_from(consumed).expect("a segment offset fits u64"))
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "segment offset overflow"))?;
        let Some(key_len) = reader.read_u32() else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "record key length truncated"));
        };
        if key_len as usize > MAX_KEY_BOUND {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "record key bound"));
        }
        let Some(key) = reader.take(key_len as usize) else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "record key truncated"));
        };
        let Some(sequence) = reader.read_u64() else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "record sequence truncated"));
        };
        let Some(kind) = reader.read_u8() else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "record kind truncated"));
        };
        let deletion = match kind {
            0 => false,
            1 => true,
            _ => return Err(io::Error::new(io::ErrorKind::InvalidData, "record kind invalid")),
        };
        let Some(value_len) = reader.read_u32() else {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "record value length truncated",
            ));
        };
        let Some(value) = reader.take(value_len as usize) else {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "record value truncated"));
        };
        let key = InternalKey::decode(key, sequence, deletion)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "record key invalid"))?;
        let entry = Entry { key, value: value.to_owned() };
        if !super::persisted_entry_is_semantically_valid(&entry) {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "record value invalid"));
        }
        entries.push(entry);
        offsets.push(offset);
    }
    Ok((entries, offsets))
}

impl SegmentReader {
    /// Locates the file offset of the first record whose user key is
    /// `>= target`, using the sparse index.
    fn seek_offset(&self, target: &[u8]) -> u64 {
        let mut best = 0_u64;
        for (key, offset) in &self.sparse {
            if key.as_slice() >= target {
                break;
            }
            best = *offset;
        }
        best.max(20)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{CollectionName, Document, DocumentId, DocumentValue, TenantId};

    fn entry(tenant: &str, id: &str, sequence: u64, value: &[u8]) -> Entry {
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

    fn segment_dir(tag: &str) -> std::path::PathBuf {
        let dir =
            std::env::temp_dir().join(format!("terrabase-lsm-seg-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn resign_segment(bytes: &mut [u8]) {
        let footer_start = bytes
            .windows(FOOTER_MAGIC.len())
            .rposition(|window| window == FOOTER_MAGIC)
            .expect("test segment has a footer");
        let checksum = codec::fnv1a(&bytes[..footer_start]).to_le_bytes();
        let checksum_start = bytes.len() - 8;
        bytes[checksum_start..].copy_from_slice(&checksum);
    }

    #[test]
    fn round_trips_sorted_entries() {
        let dir = segment_dir("rt");
        let entries: Vec<_> = (1..=100_u64)
            .map(|sequence| entry("t", &format!("doc-{sequence:03}"), sequence, b"payload"))
            .collect();
        let meta = write_segment(&dir.join("seg-1"), 1, 0, &entries).unwrap();
        assert_eq!(meta.entry_count, 100);
        assert_eq!(meta.min_key, entries[0].key.user_key().to_vec());
        let mut reader = SegmentReader::open(&dir.join("seg-1"), 1, 0).unwrap();
        let read = reader.read_all().unwrap();
        assert_eq!(read.len(), 100);
        assert_eq!(read[42].key.user_key(), entries[42].key.user_key());
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn corruption_fails_verification() {
        let dir = segment_dir("corrupt");
        let entries = vec![entry("t", "a", 1, b"one"), entry("t", "b", 2, b"two")];
        write_segment(&dir.join("seg-1"), 1, 0, &entries).unwrap();
        let mut bytes = std::fs::read(dir.join("seg-1")).unwrap();
        let mid = bytes.len() / 2;
        bytes[mid] ^= 0xFF;
        std::fs::write(dir.join("seg-1"), &bytes).unwrap();
        let result = SegmentReader::open(&dir.join("seg-1"), 1, 0);
        assert!(result.is_err(), "checksum mismatch fails closed");
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn read_from_seeks_by_sparse_index() {
        let dir = segment_dir("seek");
        let entries: Vec<_> = (1..=80_u64)
            .map(|sequence| entry("t", &format!("doc-{sequence:03}"), sequence, b"payload"))
            .collect();
        write_segment(&dir.join("seg-1"), 1, 0, &entries).unwrap();
        let mut reader = SegmentReader::open(&dir.join("seg-1"), 1, 0).unwrap();
        let target = InternalKey::document(
            &TenantId::try_new("t").expect("valid tenant"),
            &CollectionName::try_new("c").expect("valid collection"),
            &DocumentId::try_new("doc-050").expect("valid document"),
            0,
        );
        let read = reader.read_from(Some(target.user_key())).unwrap();
        assert!(!read.is_empty());
        assert!(read[0].key.user_key() <= target.user_key());
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn rechecksummed_header_identity_reserved_and_sparse_substitutions_fail_closed() {
        let dir = segment_dir("hostile-structure");
        let entries = vec![entry("t", "a", 1, b"one"), entry("t", "b", 2, b"two")];
        let canonical = dir.join("canonical");
        write_segment(&canonical, 1, 0, &entries).unwrap();
        let original = std::fs::read(&canonical).unwrap();

        for (tag, mutate) in [
            ("id", (8_usize, 0x7f_u8)),
            ("level", (16_usize, 0x01_u8)),
            ("reserved", (18_usize, 0x01_u8)),
        ] {
            let mut bytes = original.clone();
            bytes[mutate.0] = mutate.1;
            resign_segment(&mut bytes);
            let path = dir.join(tag);
            std::fs::write(&path, bytes).unwrap();
            assert!(SegmentReader::open(&path, 1, 0).is_err(), "{tag}");
        }

        let mut sparse = original.clone();
        let footer_start =
            sparse.windows(FOOTER_MAGIC.len()).rposition(|window| window == FOOTER_MAGIC).unwrap();
        let sparse_offset = usize::try_from(u64::from_le_bytes(
            sparse[footer_start + 8..footer_start + 16].try_into().unwrap(),
        ))
        .unwrap();
        let first_key_len = usize::try_from(u32::from_le_bytes(
            sparse[sparse_offset + 4..sparse_offset + 8].try_into().unwrap(),
        ))
        .unwrap();
        let first_offset = sparse_offset + 8 + first_key_len;
        sparse[first_offset] ^= 1;
        resign_segment(&mut sparse);
        let sparse_path = dir.join("sparse");
        std::fs::write(&sparse_path, sparse).unwrap();
        assert!(SegmentReader::open(&sparse_path, 1, 0).is_err());

        let mut footer_count = original;
        let footer_start = footer_count
            .windows(FOOTER_MAGIC.len())
            .rposition(|window| window == FOOTER_MAGIC)
            .unwrap();
        footer_count[footer_start + 16] ^= 1;
        let footer_path = dir.join("footer-count");
        std::fs::write(&footer_path, footer_count).unwrap();
        assert!(SegmentReader::open(&footer_path, 1, 0).is_err());
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
