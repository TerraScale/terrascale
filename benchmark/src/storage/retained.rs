//! The durable retained-evidence store (GAP-S1, decision record D3).
//!
//! This is the durable deletion-evidence store the GAP-R02 watermark record
//! said this seam must provide: an append-only, checksummed record stream in
//! the location, holding one content-free fact per retained delete plus the
//! bound records that name what publication history has been given up and
//! the per-tenant non-deletion facts extracted with it. It exists so that
//! [`crate::replication::TombstoneRetention::declare_history_bound`]'s
//! `durably_held` assertion is honest after the journal no longer covers
//! the window, and so a restarted process can re-derive the watermark from
//! durable state.
//!
//! Damage discipline (the journal's, applied to evidence): a torn or
//! checksum-failing **trailing** record is an interrupted append, truncated
//! at open; the same damage **mid-file** is integrity loss for evidence a
//! declared bound rests on, so the open fails closed instead of silently
//! serving a prefix. The torn-tail reading is bounded by
//! [`MAX_RETAINED_RECORD_BYTES`]: an interrupted append can only ever be a
//! prefix of a record this store writes, so a length header declaring more
//! than any legal record could carry is damage to the length field itself —
//! integrity damage wherever it sits, never a silent truncation. That
//! promise is enforced at the write side too: the bound writer refuses a
//! window whose record would pass the ceiling (roughly 3,912 tenants at
//! the identifier maximum) rather than append a frame its own reader must
//! misclassify, so a window that large is a disclosed refusal, not a
//! latent integrity failure (`docs/storage/followups-90-91.md`). Records
//! never carry document content — a tombstone that kept the body would keep
//! deleted data alive. D-3 clock stamps are process-local lane state and
//! are not durable; evidence re-derived from this store carries the origin
//! stamp.

use std::{
    collections::{BTreeMap, BTreeSet},
    fs::{File, OpenOptions},
    io::{self, Read, Write},
    path::Path,
};

use crate::types::{
    CollectionName, DocumentId, LocalDurability, OperationId, StorageSequence, TenantId,
};

use super::codec::{self, LocalDurabilityCode, Reader};

/// Record kind: one content-free retained deletion fact.
const RETAINED_RECORD_FACT: u8 = 1;

/// Record kind: one durable history-bound declaration.
const RETAINED_RECORD_BOUND: u8 = 2;

/// Record header size: four length bytes and eight checksum bytes.
const HEADER_BYTES: usize = 12;

/// The maximum payload size one legal retained record may declare.
///
/// This bounds the torn-tail reading of a damaged store. A fact record
/// holds four validated identifiers — each at most 255 bytes plus its
/// length prefix — and fixed fields, so it stays just over one KiB. A
/// bound record holds one row per tenant that committed in the given-up
/// window, 268 bytes a row; the ceiling leaves room for every tenant a
/// window could plausibly hold while staying far below any damaged length
/// field. A declared length beyond it cannot be an interrupted append of
/// any record this store writes — interrupted appends only ever produce
/// prefixes of legal frames — so it is integrity damage, and the open
/// fails closed wherever it sits instead of silently truncating every
/// record behind it.
///
/// The claim rests on the writer, not the reader: [`RetainedWriter`]
/// refuses to append any record whose payload would pass this ceiling
/// (fact records cannot reach it by construction), so no append this
/// store starts can end in a frame its own reader would classify as a
/// damaged length field.
const MAX_RETAINED_RECORD_BYTES: usize = 1 << 20;

/// One content-free retained deletion fact, as the store holds it.
///
/// The fact carries everything a consumer needs to replay the delete
/// idempotently — the exact key, the version that existed at it, the journal
/// sequence and operation identity that published it, and the achieved local
/// durability — and nothing else.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct RetainedFact {
    /// The tenant that owns the deleted key.
    pub(crate) tenant: TenantId,
    /// The deleted key's collection, or `None` for the provable no-op
    /// delete.
    pub(crate) collection: Option<CollectionName>,
    /// The deleted document's identifier.
    pub(crate) id: DocumentId,
    /// The version that existed at the deleted key.
    pub(crate) version_at_delete: u64,
    /// The journal sequence that published the delete.
    pub(crate) sequence: StorageSequence,
    /// The operation identity that published the delete.
    pub(crate) operation: OperationId,
    /// The local durability the publishing commit achieved.
    pub(crate) durability: LocalDurability,
}

impl RetainedFact {
    /// Returns the dedup identity: the same fact appended twice lands once.
    fn identity(&self) -> (String, Option<String>, String, u64, String) {
        (
            self.tenant.as_str().to_owned(),
            self.collection.as_ref().map(|collection| collection.as_str().to_owned()),
            self.id.as_str().to_owned(),
            self.sequence.get(),
            self.operation.as_str().to_owned(),
        )
    }
}

/// The append-only retained-evidence writer.
pub(crate) struct RetainedWriter {
    file: File,
    fresh: bool,
    path_len: u64,
}

/// The exact retained-evidence frame written by one successful append.
///
/// The independent storage frontier hashes these bytes only after the
/// retained file itself has been synced.
#[derive(Debug)]
pub(super) struct RetainedAppend {
    pub(super) start: u64,
    pub(super) frame: Vec<u8>,
}

impl RetainedWriter {
    /// Opens or creates the store at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        let path_len = file.metadata()?.len();
        Ok(Self { file, fresh, path_len })
    }

    /// Reports whether this open created the file.
    pub(crate) fn is_fresh(&self) -> bool {
        self.fresh
    }

    /// Returns the current append offset.
    pub(crate) const fn len(&self) -> u64 {
        self.path_len
    }

    /// Appends one checksummed fact record.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append_fact(&mut self, fact: &RetainedFact) -> io::Result<RetainedAppend> {
        if fact.sequence.get() == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "retained fact sequence must be nonzero",
            ));
        }
        let mut payload = Vec::new();
        codec::push_u8(&mut payload, RETAINED_RECORD_FACT);
        codec::push_str(&mut payload, fact.tenant.as_str());
        match &fact.collection {
            Some(collection) => {
                codec::push_u8(&mut payload, 1);
                codec::push_str(&mut payload, collection.as_str());
            }
            None => codec::push_u8(&mut payload, 0),
        }
        codec::push_str(&mut payload, fact.id.as_str());
        codec::push_u64(&mut payload, fact.version_at_delete);
        codec::push_u64(&mut payload, fact.sequence.get());
        codec::push_str(&mut payload, fact.operation.as_str());
        codec::encode_durability(&mut payload, LocalDurabilityCode::of(fact.durability));
        let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
        codec::push_u32(&mut frame, payload.len() as u32);
        codec::push_u64(&mut frame, codec::fnv1a(&payload));
        frame.extend_from_slice(&payload);
        self.append_frame(frame)
    }

    /// Appends one checksummed bound record: the durable declaration that
    /// history below `bound` has been given up with its evidence extracted,
    /// the journal head it was declared against, and the per-tenant
    /// non-deletion facts of the window.
    ///
    /// The record is refused, before one byte is written, when its payload
    /// would exceed [`MAX_RETAINED_RECORD_BYTES`]: the reader classifies a
    /// declared length past that ceiling as a damaged length field wherever
    /// it sits, so a writer that could produce one would turn its own
    /// interrupted append into spurious integrity damage. Fact records
    /// carry four bounded identifiers and cannot reach the ceiling by
    /// construction, so only the bound writer needs the check.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append_bound(
        &mut self,
        bound: u64,
        head: u64,
        non_deletion: &[(TenantId, Option<StorageSequence>)],
    ) -> io::Result<RetainedAppend> {
        let mut tenants = BTreeSet::new();
        if bound == 0
            || head < bound
            || non_deletion.iter().any(|(tenant, through)| {
                !tenants.insert(tenant) || through.is_some_and(|sequence| sequence.get() >= bound)
            })
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "retained bound declaration is semantically invalid",
            ));
        }
        let mut payload = Vec::new();
        codec::push_u8(&mut payload, RETAINED_RECORD_BOUND);
        codec::push_u64(&mut payload, bound);
        codec::push_u64(&mut payload, head);
        #[allow(clippy::cast_possible_truncation)]
        let count = non_deletion.len() as u32;
        codec::push_u32(&mut payload, count);
        for (tenant, through) in non_deletion {
            codec::push_str(&mut payload, tenant.as_str());
            match through {
                Some(sequence) => {
                    codec::push_u8(&mut payload, 1);
                    codec::push_u64(&mut payload, sequence.get());
                }
                None => codec::push_u8(&mut payload, 0),
            }
        }
        if payload.len() > MAX_RETAINED_RECORD_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!(
                    "retained bound record payload of {} bytes over {} tenant rows exceeds the \
                     {}-byte record ceiling",
                    payload.len(),
                    non_deletion.len(),
                    MAX_RETAINED_RECORD_BYTES
                ),
            ));
        }
        let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
        codec::push_u32(&mut frame, payload.len() as u32);
        codec::push_u64(&mut frame, codec::fnv1a(&payload));
        frame.extend_from_slice(&payload);
        self.append_frame(frame)
    }

    /// Truncates the store to its verified prefix length.
    pub(crate) fn truncate(&mut self, good_bytes: u64) -> io::Result<()> {
        self.file.set_len(good_bytes)?;
        self.file.sync_all()?;
        self.path_len = good_bytes;
        Ok(())
    }

    /// Flushes the appended bytes to the device.
    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }

    fn append_frame(&mut self, frame: Vec<u8>) -> io::Result<RetainedAppend> {
        let start = self.path_len;
        self.file.write_all(&frame)?;
        let frame_len = u64::try_from(frame.len()).map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidData, "retained frame is too large")
        })?;
        self.path_len = self.path_len.checked_add(frame_len).ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidData, "retained file is too large")
        })?;
        Ok(RetainedAppend { start, frame })
    }
}

/// The verified scan outcome of the retained-evidence store.
pub(crate) struct RetainedScan {
    /// The index over the verified prefix.
    pub(crate) index: RetainedIndex,
    /// The byte length of the verified prefix.
    pub(crate) good_bytes: u64,
    /// The total byte length of the file.
    pub(crate) total_bytes: u64,
    /// Whether damage was found with bytes behind it: mid-file integrity
    /// loss, which fails the open.
    pub(crate) mid_file_damage: bool,
    /// Every semantically verified frame end, in append order.
    frame_ends: Vec<u64>,
}

impl RetainedScan {
    pub(super) fn frame_ends(&self) -> &[u64] {
        &self.frame_ends
    }
}

/// The in-memory index over the retained-evidence store.
#[derive(Default)]
pub(crate) struct RetainedIndex {
    facts: Vec<RetainedFact>,
    identities: BTreeSet<(String, Option<String>, String, u64, String)>,
    /// The highest declared bound, with the journal head it was declared
    /// against.
    bound: Option<(u64, u64)>,
    /// The per-tenant highest non-deletion sequence below the bound, merged
    /// across every bound record.
    non_deletion: BTreeMap<TenantId, Option<StorageSequence>>,
}

impl RetainedIndex {
    /// Scans the durable store and builds the index over its verified
    /// prefix, the prefix length, and the damage classification.
    ///
    /// The classification follows the write-ahead log's position rule,
    /// bounded by the maximum legal record size: a frame that is cut off
    /// by end of file — its declared payload, or the header itself, runs
    /// past the end — is an interrupted append, so the verified prefix ends
    /// there and the caller truncates the tail; a structurally complete
    /// frame that fails its checksum or parse is integrity damage, and with
    /// bytes behind it the open fails closed (only the trailing position
    /// keeps the gentler torn-tail reading). A declared length beyond
    /// [`MAX_RETAINED_RECORD_BYTES`] is never an interrupted append — no
    /// legal record carries it — so it is integrity damage wherever it
    /// sits, and the open fails closed instead of silently truncating
    /// every record behind a corrupted length field.
    pub(crate) fn scan(path: &Path) -> RetainedScan {
        let empty = RetainedScan {
            index: Self::default(),
            good_bytes: 0,
            total_bytes: 0,
            mid_file_damage: false,
            frame_ends: Vec::new(),
        };
        let mut bytes = Vec::new();
        let Ok(mut file) = File::open(path) else {
            return empty;
        };
        if file.read_to_end(&mut bytes).is_err() {
            return empty;
        }
        #[allow(clippy::cast_possible_truncation)]
        let total_bytes = bytes.len() as u64;
        let total = bytes.len();
        let mut index = Self::default();
        let mut good_bytes: u64 = 0;
        let mut reader = Reader::new(&bytes);
        let mut frame_ends = Vec::new();
        while reader.remaining() > 0 {
            let frame_start = total - reader.remaining();
            let before = reader.remaining();
            let Some(record) = read_retained_record(&mut reader) else {
                // The reader stopped somewhere inside this frame. Where
                // exactly decides torn tail versus mid-file damage.
                let mid_file = match declared_frame(&bytes, frame_start) {
                    // The header itself is cut off by the end of the file:
                    // the only explanation is an interrupted append.
                    None => false,
                    // A length no legal record could carry is a damaged
                    // length field, not a torn tail.
                    Some((length, _)) if length > MAX_RETAINED_RECORD_BYTES => true,
                    // A legal length running past EOF is an interrupted append
                    // only when it does not conceal a later verified record.
                    Some((_, end)) if end > total => {
                        has_verified_retained_frame_after(&bytes, frame_start)
                    }
                    // All declared bytes exist. A checksum, structure, or
                    // semantic failure may have been acknowledged, even at
                    // EOF, so it is integrity damage rather than a torn tail.
                    Some((_, _)) => true,
                };
                return RetainedScan {
                    index,
                    good_bytes,
                    total_bytes,
                    mid_file_damage: mid_file,
                    frame_ends,
                };
            };
            let consumed = before.saturating_sub(reader.remaining());
            match record {
                RetainedRecord::Fact(fact) => {
                    if index.identities.insert(fact.identity()) {
                        index.facts.push(fact);
                    }
                }
                RetainedRecord::Bound { bound, head, non_deletion } => {
                    index.bound = Some(match index.bound {
                        Some((known_bound, known_head)) => {
                            (known_bound.max(bound), known_head.max(head))
                        }
                        None => (bound, head),
                    });
                    for (tenant, through) in non_deletion {
                        let entry = index.non_deletion.entry(tenant).or_default();
                        *entry = match (*entry, through) {
                            (Some(known), Some(seen)) => Some(known.max(seen)),
                            (Some(known), None) => Some(known),
                            (None, seen) => seen,
                        };
                    }
                }
            }
            good_bytes += consumed as u64;
            frame_ends.push(good_bytes);
        }
        RetainedScan { index, good_bytes, total_bytes, mid_file_damage: false, frame_ends }
    }

    /// Holds one fact if its identity is new, reporting whether it landed.
    pub(crate) fn contains_fact(&self, fact: &RetainedFact) -> bool {
        self.identities.contains(&fact.identity())
    }

    /// Holds one fact if its identity is new, reporting whether it landed.
    pub(crate) fn upsert_fact(&mut self, fact: RetainedFact) -> bool {
        if self.identities.insert(fact.identity()) {
            self.facts.push(fact);
            true
        } else {
            false
        }
    }

    /// Returns the durable declared bound, when one exists.
    pub(crate) fn declared_bound(&self) -> Option<u64> {
        self.bound.map(|(bound, _)| bound)
    }

    /// Records a bound the writer just appended durably (LSM compaction,
    /// P1): the same merge the scan path applies to bound records.
    pub(crate) fn declare_bound(&mut self, bound: u64, head: u64) {
        self.bound = Some(self.bound.map_or((bound, head), |(prior, _)| (prior.max(bound), head)));
    }

    /// Returns every retained fact, in append order.
    pub(crate) fn facts(&self) -> &[RetainedFact] {
        &self.facts
    }

    /// Returns the merged per-tenant non-deletion facts.
    pub(crate) fn non_deletion(&self) -> Vec<(TenantId, Option<StorageSequence>)> {
        self.non_deletion.iter().map(|(tenant, through)| (tenant.clone(), *through)).collect()
    }
}

/// Returns the payload length the frame starting at `frame_start` declares
/// and where that frame declares it ends, when the length header is present
/// and readable.
fn declared_frame(bytes: &[u8], frame_start: usize) -> Option<(usize, usize)> {
    let total = bytes.len();
    if frame_start + 4 > total {
        return None;
    }
    let length = u32::from_le_bytes(bytes[frame_start..frame_start + 4].try_into().expect("length"))
        as usize;
    Some((length, frame_start + HEADER_BYTES + length))
}

fn has_verified_retained_frame_after(bytes: &[u8], damaged_start: usize) -> bool {
    let Some(search_start) = damaged_start.checked_add(1) else {
        return false;
    };
    let Some(last_start) = bytes.len().checked_sub(HEADER_BYTES) else {
        return false;
    };
    if search_start > last_start {
        return false;
    }
    (search_start..=last_start).any(|candidate| {
        let Some((length, end)) = declared_frame(bytes, candidate) else {
            return false;
        };
        if length > MAX_RETAINED_RECORD_BYTES || end > bytes.len() {
            return false;
        }
        let mut reader = Reader::new(&bytes[candidate..end]);
        read_retained_record(&mut reader).is_some() && reader.remaining() == 0
    })
}

/// One decoded record of the store.
enum RetainedRecord {
    Fact(RetainedFact),
    Bound { bound: u64, head: u64, non_deletion: Vec<(TenantId, Option<StorageSequence>)> },
}

/// Reads one framed retained-evidence record.
fn read_retained_record(reader: &mut Reader<'_>) -> Option<RetainedRecord> {
    let length = reader.read_u32()? as usize;
    let checksum = reader.read_u64()?;
    let payload = reader.take(length)?;
    if codec::fnv1a(payload) != checksum {
        return None;
    }
    let mut payload_reader = Reader::new(payload);
    let record = match payload_reader.read_u8()? {
        RETAINED_RECORD_FACT => {
            let tenant = TenantId::try_new(&payload_reader.read_string()?).ok()?;
            let collection = match payload_reader.read_u8()? {
                0 => None,
                1 => Some(CollectionName::try_new(&payload_reader.read_string()?).ok()?),
                _ => return None,
            };
            let id = DocumentId::try_new(&payload_reader.read_string()?).ok()?;
            let version_at_delete = payload_reader.read_u64()?;
            let sequence = StorageSequence::new(payload_reader.read_u64()?);
            let operation = OperationId::try_new(&payload_reader.read_string()?).ok()?;
            let durability = codec::decode_durability(&mut payload_reader)?.to_level();
            Some(RetainedRecord::Fact(RetainedFact {
                tenant,
                collection,
                id,
                version_at_delete,
                sequence,
                operation,
                durability,
            }))
        }
        RETAINED_RECORD_BOUND => {
            let bound = payload_reader.read_u64()?;
            let head = payload_reader.read_u64()?;
            let count = payload_reader.read_u32()? as usize;
            let mut non_deletion = Vec::with_capacity(count.min(1024));
            for _ in 0..count {
                let tenant = TenantId::try_new(&payload_reader.read_string()?).ok()?;
                let through = match payload_reader.read_u8()? {
                    0 => None,
                    1 => Some(StorageSequence::new(payload_reader.read_u64()?)),
                    _ => return None,
                };
                non_deletion.push((tenant, through));
            }
            Some(RetainedRecord::Bound { bound, head, non_deletion })
        }
        _ => None,
    }?;
    if payload_reader.remaining() != 0 {
        return None;
    }
    match &record {
        RetainedRecord::Fact(fact) if fact.sequence.get() == 0 => None,
        RetainedRecord::Bound { bound, head, non_deletion }
            if *bound == 0
                || *head < *bound
                || non_deletion.iter().any(|(_, through)| {
                    through.is_some_and(|sequence| sequence.get() >= *bound)
                })
                || {
                    let mut tenants = BTreeSet::new();
                    non_deletion.iter().any(|(tenant, _)| !tenants.insert(tenant))
                } =>
        {
            None
        }
        _ => Some(record),
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::{HEADER_BYTES, MAX_RETAINED_RECORD_BYTES, RetainedIndex, RetainedWriter};
    use crate::{
        storage::codec,
        types::{StorageSequence, TenantId},
    };

    /// One temp location per test, never shared across the fleet's runs.
    fn location(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!("terrabase-fups-retained-{tag}-{}", std::process::id()))
    }

    /// One tenant whose encoded row is the maximum 268 bytes: a 255-byte
    /// identifier with its length prefix, the flag byte, and a sequence.
    fn wide_tenant(index: usize) -> (TenantId, Option<StorageSequence>) {
        let stem = format!("t{index:05}");
        let padding = 255 - stem.len();
        let text = format!("{stem}{}", "p".repeat(padding));
        let tenant = TenantId::try_new(&text).expect("a padded identifier is a valid tenant");
        (tenant, Some(StorageSequence::new(index as u64 + 1)))
    }

    /// The largest window of maximum-size rows whose bound record still
    /// fits the ceiling: 21 fixed bytes plus 268 bytes a row.
    fn largest_legal_window() -> Vec<(TenantId, Option<StorageSequence>)> {
        let rows = (MAX_RETAINED_RECORD_BYTES - 21) / 268;
        (0..rows).map(wide_tenant).collect()
    }

    #[test]
    fn append_bound_writes_the_largest_legal_window() {
        let path = location("largest-legal");
        let rows = largest_legal_window();
        let mut writer = RetainedWriter::open(&path).expect("opens");
        writer
            .append_bound(u64::MAX, u64::MAX, &rows)
            .expect("the largest legal window is written");
        writer.sync().expect("syncs");
        drop(writer);
        let scan = RetainedIndex::scan(&path);
        assert!(!scan.mid_file_damage, "an intact record at the ceiling is not damage");
        assert_eq!(scan.index.declared_bound(), Some(u64::MAX));
        assert_eq!(scan.index.non_deletion().len(), rows.len());
        assert_eq!(
            scan.total_bytes,
            (HEADER_BYTES + 21 + 268 * rows.len()) as u64,
            "the payload is the fixed 21 bytes plus one 268-byte row per tenant"
        );
        std::fs::remove_dir_all(&path).ok();
    }

    #[test]
    fn append_bound_refuses_a_window_past_the_record_ceiling() {
        let path = location("past-ceiling");
        let mut rows = largest_legal_window();
        rows.push(wide_tenant(rows.len()));
        let mut writer = RetainedWriter::open(&path).expect("opens");
        let refusal = writer
            .append_bound(u64::MAX, u64::MAX, &rows)
            .expect_err("a bound record past the ceiling would misclassify as damage when torn");
        assert_eq!(
            refusal.kind(),
            std::io::ErrorKind::InvalidInput,
            "the refusal is a typed input refusal, not a write failure"
        );
        assert_eq!(
            std::fs::metadata(&path).map_or(0, |meta| meta.len()),
            0,
            "the refusal happens before one byte is written"
        );
        drop(writer);
        let scan = RetainedIndex::scan(&path);
        assert!(!scan.mid_file_damage);
        assert_eq!(scan.index.declared_bound(), None, "no bound was declared");
        std::fs::remove_dir_all(&path).ok();
    }

    #[test]
    fn a_trailing_impossible_declared_length_is_damage_not_a_torn_tail() {
        let path = location("trailing-impossible");
        let mut writer = RetainedWriter::open(&path).expect("opens");
        writer
            .append_bound(4, 4, &[(TenantId::try_new("tenant-a").expect("valid"), None)])
            .expect("writes");
        writer.sync().expect("syncs");
        drop(writer);

        // A trailing frame whose length header declares more than any
        // legal record could carry: not a prefix of any frame this store
        // writes, so it cannot be an interrupted append — the open must
        // fail closed instead of truncating it away as a torn tail.
        let mut damaged = std::fs::read(&path).expect("store reads");
        let verified = damaged.len() as u64;
        let impossible =
            u32::try_from(MAX_RETAINED_RECORD_BYTES + 1).expect("the ceiling fits a u32");
        damaged.extend_from_slice(&impossible.to_le_bytes());
        damaged.extend_from_slice(&[0_u8; 8]);
        damaged.extend_from_slice(&[0xa5, 0xa5]);
        std::fs::write(&path, &damaged).expect("store rewrites");
        let scan = RetainedIndex::scan(&path);
        assert!(
            scan.mid_file_damage,
            "a declared length past the ceiling is a damaged length field wherever it sits"
        );
        assert_eq!(scan.good_bytes, verified, "nothing behind the damage is served");
        std::fs::remove_dir_all(&path).ok();
    }

    #[test]
    fn a_trailing_legal_length_with_a_torn_payload_truncates() {
        let path = location("trailing-legal");
        let mut writer = RetainedWriter::open(&path).expect("opens");
        writer
            .append_bound(4, 4, &[(TenantId::try_new("tenant-a").expect("valid"), None)])
            .expect("writes");
        writer.sync().expect("syncs");
        drop(writer);

        // The genuine torn tail keeps the gentler reading: a trailing
        // frame declaring a legal length whose payload is cut off by the
        // end of the file is an interrupted append, truncated away.
        let mut torn = std::fs::read(&path).expect("store reads");
        let verified = torn.len() as u64;
        torn.extend_from_slice(&24_u32.to_le_bytes());
        torn.extend_from_slice(&[0_u8; 8]);
        torn.extend_from_slice(&[0xa5, 0xa5]);
        std::fs::write(&path, &torn).expect("store rewrites");
        let scan = RetainedIndex::scan(&path);
        assert!(!scan.mid_file_damage, "a legal-length torn tail is an interrupted append");
        assert_eq!(scan.good_bytes, verified);
        assert_eq!(scan.index.declared_bound(), Some(4));
        std::fs::remove_dir_all(&path).ok();
    }

    #[test]
    fn a_complete_checksummed_record_with_trailing_fields_is_integrity_damage() {
        let path = location("trailing-fields");
        let mut writer = RetainedWriter::open(&path).expect("opens");
        writer
            .append_bound(4, 4, &[(TenantId::try_new("tenant-a").expect("valid"), None)])
            .expect("bound writes");
        writer.sync().expect("bound syncs");
        drop(writer);

        let original = std::fs::read(&path).expect("store reads");
        let mut payload = original[HEADER_BYTES..].to_vec();
        payload.push(0xa5);
        let mut hostile = Vec::new();
        codec::push_u32(
            &mut hostile,
            u32::try_from(payload.len()).expect("payload fits frame length"),
        );
        codec::push_u64(&mut hostile, codec::fnv1a(&payload));
        hostile.extend_from_slice(&payload);
        std::fs::write(&path, hostile).expect("hostile record writes");

        let scan = RetainedIndex::scan(&path);
        assert!(scan.mid_file_damage);
        assert_eq!(scan.good_bytes, 0);
        std::fs::remove_file(path).ok();
    }
}
