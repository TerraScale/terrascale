//! Independent durable anchors for the root backend's append-only safety records.

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::Path,
    sync::atomic::{AtomicU64, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};

use crate::types::OperationId;

use super::{
    ledger::{LedgerAppend, LedgerScan},
    retained::{RetainedAppend, RetainedScan},
    root,
};

const FRONTIER_FILE: &str = "storage.frontier";
const FRONTIER_TEMP_FILE: &str = "storage.frontier.tmp";
const SLOT_MAGIC: &[u8; 8] = b"TBSTFRN1";
const SLOT_SCHEMA: u16 = 1;
const SLOT_BYTES: usize = 176;
const FRONTIER_BYTES: usize = SLOT_BYTES * 2;
const SLOT_CHECKSUM_START: usize = 144;
const LEDGER_CHAIN_DOMAIN: &[u8] = b"terrabase.storage-frontier.ledger-chain.v1\0";
const RETAINED_CHAIN_DOMAIN: &[u8] = b"terrabase.storage-frontier.retained-chain.v1\0";
const SLOT_DOMAIN: &[u8] = b"terrabase.storage-frontier.slot.v1\0";
const STORE_ID_DOMAIN: &[u8] = b"terrabase.storage-frontier.store-id.v1\0";

pub(crate) const LEGACY_FORMAT_MAJOR: u64 = 1;
pub(crate) const FRONTIER_FORMAT_MAJOR: u64 = 2;
pub(crate) const STORAGE_FRONTIER_FEATURE: u64 = 1;

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct LedgerFrontierObservation {
    pub(crate) format_major: u64,
    pub(crate) frontier_generation: u64,
    pub(crate) ledger_bytes: u64,
    pub(crate) record_count: u64,
    pub(crate) chain_head: [u8; 32],
    pub(crate) retained_bytes: u64,
    pub(crate) retained_record_count: u64,
    pub(crate) retained_chain_head: [u8; 32],
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct LedgerFrontierActivationIdentity {
    pub(crate) activation_id: OperationId,
    pub(crate) external_fence_generation: u64,
    pub(crate) external_record_digest: [u8; 32],
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct LedgerFrontierActivationRequest {
    pub(crate) identity: LedgerFrontierActivationIdentity,
    pub(crate) expected: LedgerFrontierObservation,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct LedgerFrontierActivationReceipt {
    pub(crate) identity: LedgerFrontierActivationIdentity,
    pub(crate) observation: LedgerFrontierObservation,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum LedgerFrontierStatus {
    UnsupportedBackend,
    Unavailable,
    Inactive(LedgerFrontierObservation),
    Active { observation: LedgerFrontierObservation, activation: LedgerFrontierActivationIdentity },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LedgerFrontierActivationError {
    UnsupportedBackend,
    NotQuiescent,
    StaleObservation,
    AlreadyActivatedByDifferentRecord,
    IntegrityFailure,
    StorageUnavailable,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) struct ActiveFormat {
    pub(super) store_id: StoreId,
    pub(super) activation: LedgerFrontierActivationIdentity,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct StoreId([u8; 16]);

impl StoreId {
    pub(super) fn generate(dir: &Path) -> Self {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let counter = COUNTER.fetch_add(1, Ordering::Relaxed);
        let nanos =
            SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |elapsed| elapsed.as_nanos());
        let mut hasher = blake3::Hasher::new();
        hasher.update(STORE_ID_DOMAIN);
        hasher.update(&nanos.to_le_bytes());
        hasher.update(&std::process::id().to_le_bytes());
        hasher.update(&counter.to_le_bytes());
        hasher.update(dir.to_string_lossy().as_bytes());
        let mut id = [0_u8; 16];
        id.copy_from_slice(&hasher.finalize().as_bytes()[..16]);
        Self(id)
    }

    pub(super) const fn from_bytes(bytes: [u8; 16]) -> Self {
        Self(bytes)
    }

    pub(super) const fn as_bytes(self) -> [u8; 16] {
        self.0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct StreamAnchor {
    bytes: u64,
    record_count: u64,
    chain_head: [u8; 32],
}

impl StreamAnchor {
    fn empty(store_id: StoreId, domain: &[u8]) -> Self {
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(&store_id.0);
        Self { bytes: 0, record_count: 0, chain_head: *hasher.finalize().as_bytes() }
    }

    fn advance(self, store_id: StoreId, domain: &[u8], start: u64, frame: &[u8]) -> Option<Self> {
        if start != self.bytes {
            return None;
        }
        let frame_len = u64::try_from(frame.len()).ok()?;
        let bytes = self.bytes.checked_add(frame_len)?;
        let record_count = self.record_count.checked_add(1)?;
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(&store_id.0);
        hasher.update(&self.chain_head);
        hasher.update(&record_count.to_le_bytes());
        hasher.update(&start.to_le_bytes());
        hasher.update(&frame_len.to_le_bytes());
        hasher.update(frame);
        Some(Self { bytes, record_count, chain_head: *hasher.finalize().as_bytes() })
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct StorageAnchor {
    ledger: StreamAnchor,
    retained: StreamAnchor,
}

impl StorageAnchor {
    fn empty(store_id: StoreId) -> Self {
        Self {
            ledger: StreamAnchor::empty(store_id, LEDGER_CHAIN_DOMAIN),
            retained: StreamAnchor::empty(store_id, RETAINED_CHAIN_DOMAIN),
        }
    }

    fn advance_ledger(self, store_id: StoreId, append: &LedgerAppend) -> Option<Self> {
        Some(Self {
            ledger: self.ledger.advance(
                store_id,
                LEDGER_CHAIN_DOMAIN,
                append.start,
                &append.frame,
            )?,
            retained: self.retained,
        })
    }

    fn advance_retained(self, store_id: StoreId, append: &RetainedAppend) -> Option<Self> {
        Some(Self {
            ledger: self.ledger,
            retained: self.retained.advance(
                store_id,
                RETAINED_CHAIN_DOMAIN,
                append.start,
                &append.frame,
            )?,
        })
    }

    fn observation(self, format_major: u64, generation: u64) -> LedgerFrontierObservation {
        LedgerFrontierObservation {
            format_major,
            frontier_generation: generation,
            ledger_bytes: self.ledger.bytes,
            record_count: self.ledger.record_count,
            chain_head: self.ledger.chain_head,
            retained_bytes: self.retained.bytes,
            retained_record_count: self.retained.record_count,
            retained_chain_head: self.retained.chain_head,
        }
    }
}

struct VerifiedStream {
    anchors: Vec<StreamAnchor>,
}

impl VerifiedStream {
    fn from_scan(
        path: &Path,
        good_bytes: u64,
        frame_ends: &[u64],
        store_id: StoreId,
        domain: &[u8],
    ) -> io::Result<Self> {
        let mut bytes = Vec::new();
        File::open(path)?.read_to_end(&mut bytes)?;
        let good_len = usize::try_from(good_bytes).map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidData, "verified stream is too large")
        })?;
        if bytes.len() < good_len {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "stream shrank while its frontier was computed",
            ));
        }
        let mut anchors = Vec::with_capacity(frame_ends.len().saturating_add(1));
        let mut anchor = StreamAnchor::empty(store_id, domain);
        anchors.push(anchor);
        let mut start = 0_usize;
        for &end_u64 in frame_ends {
            let end = usize::try_from(end_u64).map_err(|_| {
                io::Error::new(io::ErrorKind::InvalidData, "stream frame boundary is too large")
            })?;
            if end > good_len || end < start {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "stream frame boundary is outside the verified prefix",
                ));
            }
            anchor = anchor
                .advance(
                    store_id,
                    domain,
                    u64::try_from(start).expect("a verified stream offset fits u64"),
                    &bytes[start..end],
                )
                .ok_or_else(|| {
                    io::Error::new(io::ErrorKind::InvalidData, "stream chain cannot advance")
                })?;
            anchors.push(anchor);
            start = end;
        }
        if start != good_len {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "verified stream does not end on a frame boundary",
            ));
        }
        Ok(Self { anchors })
    }

    fn final_anchor(&self) -> StreamAnchor {
        *self.anchors.last().expect("the empty anchor is always present")
    }

    fn contains(&self, anchor: StreamAnchor) -> bool {
        self.anchors.contains(&anchor)
    }
}

pub(super) struct VerifiedAnchors {
    ledger: VerifiedStream,
    retained: VerifiedStream,
}

impl VerifiedAnchors {
    pub(super) fn from_scans(
        ledger_path: &Path,
        ledger_scan: &LedgerScan,
        retained_path: &Path,
        retained_scan: &RetainedScan,
        store_id: StoreId,
    ) -> io::Result<Self> {
        Ok(Self {
            ledger: VerifiedStream::from_scan(
                ledger_path,
                ledger_scan.good_bytes,
                ledger_scan.frame_ends(),
                store_id,
                LEDGER_CHAIN_DOMAIN,
            )?,
            retained: VerifiedStream::from_scan(
                retained_path,
                retained_scan.good_bytes,
                retained_scan.frame_ends(),
                store_id,
                RETAINED_CHAIN_DOMAIN,
            )?,
        })
    }

    pub(super) fn final_anchor(&self) -> StorageAnchor {
        StorageAnchor { ledger: self.ledger.final_anchor(), retained: self.retained.final_anchor() }
    }

    fn contains(&self, anchor: StorageAnchor) -> bool {
        self.ledger.contains(anchor.ledger) && self.retained.contains(anchor.retained)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Slot {
    index: u8,
    generation: u64,
    anchor: StorageAnchor,
}

pub(super) struct LedgerFrontier {
    file: File,
    store_id: StoreId,
    generation: u64,
    anchor: StorageAnchor,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum FrontierError {
    Integrity,
    Unavailable,
}

impl LedgerFrontier {
    pub(super) fn rebuild_shadow(
        dir: &Path,
        store_id: StoreId,
        final_anchor: StorageAnchor,
    ) -> Result<Self, FrontierError> {
        let temp = dir.join(FRONTIER_TEMP_FILE);
        let path = dir.join(FRONTIER_FILE);
        let mut file = OpenOptions::new()
            .create(true)
            .truncate(true)
            .read(true)
            .write(true)
            .open(&temp)
            .map_err(|_| FrontierError::Unavailable)?;
        file.set_len(u64::try_from(FRONTIER_BYTES).expect("frontier length fits u64"))
            .map_err(|_| FrontierError::Unavailable)?;
        let empty = StorageAnchor::empty(store_id);
        write_slot(&mut file, Slot { index: 0, generation: 0, anchor: empty }, store_id)
            .map_err(|_| FrontierError::Unavailable)?;
        write_slot(&mut file, Slot { index: 1, generation: 1, anchor: empty }, store_id)
            .map_err(|_| FrontierError::Unavailable)?;
        let (generation, anchor) = if final_anchor == empty {
            (1, empty)
        } else {
            write_slot(&mut file, Slot { index: 0, generation: 2, anchor: final_anchor }, store_id)
                .map_err(|_| FrontierError::Unavailable)?;
            (2, final_anchor)
        };
        file.sync_all().map_err(|_| FrontierError::Unavailable)?;
        fs::rename(&temp, &path).map_err(|_| FrontierError::Unavailable)?;
        root::sync_dir(dir).map_err(|_| FrontierError::Unavailable)?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
            .map_err(|_| FrontierError::Unavailable)?;
        Ok(Self { file, store_id, generation, anchor })
    }

    pub(super) fn open_active(
        dir: &Path,
        store_id: StoreId,
        verified: &VerifiedAnchors,
    ) -> Result<Self, FrontierError> {
        let path = dir.join(FRONTIER_FILE);
        let mut file = OpenOptions::new().read(true).write(true).open(&path).map_err(|error| {
            if error.kind() == io::ErrorKind::NotFound {
                FrontierError::Integrity
            } else {
                FrontierError::Unavailable
            }
        })?;
        if file.metadata().map_err(|_| FrontierError::Unavailable)?.len()
            != u64::try_from(FRONTIER_BYTES).expect("frontier length fits u64")
        {
            return Err(FrontierError::Integrity);
        }
        let mut bytes = Vec::new();
        file.read_to_end(&mut bytes).map_err(|_| FrontierError::Unavailable)?;
        if bytes.len() != FRONTIER_BYTES {
            return Err(FrontierError::Integrity);
        }
        let decoded = [
            decode_slot(&bytes[..SLOT_BYTES], 0, store_id),
            decode_slot(&bytes[SLOT_BYTES..], 1, store_id),
        ];
        if decoded
            .iter()
            .any(|slot| matches!(slot, SlotDecode::Incompatible | SlotDecode::ForeignStore))
        {
            return Err(FrontierError::Integrity);
        }
        let mut valid = decoded
            .into_iter()
            .filter_map(|slot| match slot {
                SlotDecode::Valid(slot) => Some(slot),
                SlotDecode::Damaged | SlotDecode::Incompatible | SlotDecode::ForeignStore => None,
            })
            .collect::<Vec<_>>();
        if valid.is_empty() {
            return Err(FrontierError::Integrity);
        }
        valid.sort_by_key(|slot| slot.generation);
        if valid.len() == 2 && valid[1].generation.checked_sub(valid[0].generation) != Some(1) {
            return Err(FrontierError::Integrity);
        }
        if valid.iter().any(|slot| !verified.contains(slot.anchor)) {
            return Err(FrontierError::Integrity);
        }
        let selected = *valid.last().expect("at least one slot is valid");
        let mut frontier =
            Self { file, store_id, generation: selected.generation, anchor: selected.anchor };
        let final_anchor = verified.final_anchor();
        if selected.anchor != final_anchor || valid.len() == 1 {
            frontier.write_next(final_anchor)?;
        }
        Ok(frontier)
    }

    pub(super) fn advance_ledger(&mut self, append: &LedgerAppend) -> Result<(), FrontierError> {
        let next =
            self.anchor.advance_ledger(self.store_id, append).ok_or(FrontierError::Integrity)?;
        self.write_next(next)
    }

    pub(super) fn advance_retained(
        &mut self,
        appends: &[RetainedAppend],
    ) -> Result<(), FrontierError> {
        let mut next = self.anchor;
        for append in appends {
            next = next.advance_retained(self.store_id, append).ok_or(FrontierError::Integrity)?;
        }
        if next != self.anchor {
            self.write_next(next)?;
        }
        Ok(())
    }

    fn write_next(&mut self, anchor: StorageAnchor) -> Result<(), FrontierError> {
        let generation = self.generation.checked_add(1).ok_or(FrontierError::Integrity)?;
        let index = u8::try_from(generation & 1).expect("slot parity is one bit");
        write_slot(&mut self.file, Slot { index, generation, anchor }, self.store_id)
            .map_err(|_| FrontierError::Unavailable)?;
        self.file.sync_data().map_err(|_| FrontierError::Unavailable)?;
        self.generation = generation;
        self.anchor = anchor;
        Ok(())
    }

    pub(super) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }

    pub(super) fn observation(&self, format_major: u64) -> LedgerFrontierObservation {
        self.anchor.observation(format_major, self.generation)
    }

    pub(super) const fn store_id(&self) -> StoreId {
        self.store_id
    }

    pub(super) const fn anchor(&self) -> StorageAnchor {
        self.anchor
    }
}

fn write_slot(file: &mut File, slot: Slot, store_id: StoreId) -> io::Result<()> {
    let bytes = encode_slot(slot, store_id);
    let offset = u64::from(slot.index)
        .checked_mul(u64::try_from(SLOT_BYTES).expect("slot length fits u64"))
        .expect("two slot offsets fit u64");
    file.seek(SeekFrom::Start(offset))?;
    file.write_all(&bytes)
}

fn encode_slot(slot: Slot, store_id: StoreId) -> [u8; SLOT_BYTES] {
    let mut bytes = [0_u8; SLOT_BYTES];
    bytes[..8].copy_from_slice(SLOT_MAGIC);
    bytes[8..10].copy_from_slice(&SLOT_SCHEMA.to_le_bytes());
    bytes[10] = slot.index;
    bytes[16..32].copy_from_slice(&store_id.0);
    bytes[32..40].copy_from_slice(&slot.generation.to_le_bytes());
    bytes[40..48].copy_from_slice(&slot.anchor.ledger.bytes.to_le_bytes());
    bytes[48..56].copy_from_slice(&slot.anchor.ledger.record_count.to_le_bytes());
    bytes[56..88].copy_from_slice(&slot.anchor.ledger.chain_head);
    bytes[88..96].copy_from_slice(&slot.anchor.retained.bytes.to_le_bytes());
    bytes[96..104].copy_from_slice(&slot.anchor.retained.record_count.to_le_bytes());
    bytes[104..136].copy_from_slice(&slot.anchor.retained.chain_head);
    let checksum = slot_checksum(&bytes[..SLOT_CHECKSUM_START]);
    bytes[SLOT_CHECKSUM_START..].copy_from_slice(&checksum);
    bytes
}

enum SlotDecode {
    Valid(Slot),
    Damaged,
    Incompatible,
    ForeignStore,
}

fn decode_slot(bytes: &[u8], expected_index: u8, store_id: StoreId) -> SlotDecode {
    if bytes.len() != SLOT_BYTES
        || bytes[SLOT_CHECKSUM_START..] != slot_checksum(&bytes[..SLOT_CHECKSUM_START])
    {
        return SlotDecode::Damaged;
    }
    if bytes[..8] != *SLOT_MAGIC
        || u16::from_le_bytes(bytes[8..10].try_into().expect("slot schema")) != SLOT_SCHEMA
        || bytes[10] != expected_index
        || bytes[11] != 0
        || bytes[12..16] != [0; 4]
        || bytes[136..144] != [0; 8]
    {
        return SlotDecode::Incompatible;
    }
    if bytes[16..32] != store_id.0 {
        return SlotDecode::ForeignStore;
    }
    let generation = u64::from_le_bytes(bytes[32..40].try_into().expect("slot generation"));
    if generation & 1 != u64::from(expected_index) {
        return SlotDecode::Incompatible;
    }
    let ledger_bytes = u64::from_le_bytes(bytes[40..48].try_into().expect("ledger bytes"));
    let ledger_records = u64::from_le_bytes(bytes[48..56].try_into().expect("ledger records"));
    let mut ledger_chain = [0_u8; 32];
    ledger_chain.copy_from_slice(&bytes[56..88]);
    let retained_bytes = u64::from_le_bytes(bytes[88..96].try_into().expect("retained bytes"));
    let retained_records = u64::from_le_bytes(bytes[96..104].try_into().expect("retained records"));
    let mut retained_chain = [0_u8; 32];
    retained_chain.copy_from_slice(&bytes[104..136]);
    SlotDecode::Valid(Slot {
        index: expected_index,
        generation,
        anchor: StorageAnchor {
            ledger: StreamAnchor {
                bytes: ledger_bytes,
                record_count: ledger_records,
                chain_head: ledger_chain,
            },
            retained: StreamAnchor {
                bytes: retained_bytes,
                record_count: retained_records,
                chain_head: retained_chain,
            },
        },
    })
}

fn slot_checksum(bytes: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(SLOT_DOMAIN);
    hasher.update(bytes);
    *hasher.finalize().as_bytes()
}

#[cfg(test)]
mod tests {
    use std::{
        io::Write,
        path::PathBuf,
        sync::atomic::{AtomicU64, Ordering},
    };

    use crate::{
        storage::{
            intent::CommitRejection,
            ledger::{LedgerIndex, LedgerRecord, LedgerWriter, RejectedRecord},
            retained::{RetainedFact, RetainedIndex, RetainedWriter},
        },
        types::{
            CollectionName, DocumentId, LocalDurability, OperationId, StorageSequence, TenantId,
        },
    };

    use super::{
        FRONTIER_BYTES, FrontierError, LedgerFrontier, SLOT_BYTES, SLOT_CHECKSUM_START, SLOT_MAGIC,
        SlotDecode, StoreId, VerifiedAnchors, decode_slot, slot_checksum,
    };

    fn location(tag: &str) -> PathBuf {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir()
            .join(format!("terrabase-storage-frontier-{tag}-{}-{index}", std::process::id()))
    }

    fn create_streams(dir: &std::path::Path) {
        std::fs::create_dir_all(dir).expect("test directory creates");
        drop(LedgerWriter::open(&dir.join("ledger")).expect("ledger creates"));
        drop(RetainedWriter::open(&dir.join("retained")).expect("retained creates"));
    }

    fn verified(dir: &std::path::Path, store_id: StoreId) -> VerifiedAnchors {
        let ledger_scan = LedgerIndex::scan(&dir.join("ledger")).expect("ledger scans");
        let retained_scan = RetainedIndex::scan(&dir.join("retained"));
        VerifiedAnchors::from_scans(
            &dir.join("ledger"),
            &ledger_scan,
            &dir.join("retained"),
            &retained_scan,
            store_id,
        )
        .expect("verified anchors build")
    }

    fn rejection(operation: &str) -> LedgerRecord {
        LedgerRecord::Rejected(RejectedRecord {
            tenant: "tenant-a".to_owned(),
            operation: operation.to_owned(),
            digest: 17,
            reason: CommitRejection::Conflict,
        })
    }

    fn retained_fact(operation: &str) -> RetainedFact {
        RetainedFact {
            tenant: TenantId::try_new("tenant-a").expect("valid tenant"),
            collection: Some(CollectionName::try_new("users").expect("valid collection")),
            id: DocumentId::try_new("doc-1").expect("valid document id"),
            version_at_delete: 1,
            sequence: StorageSequence::new(1),
            operation: OperationId::try_new(operation).expect("valid operation"),
            durability: LocalDurability::Synced,
        }
    }

    #[test]
    fn two_slot_layout_repairs_one_damaged_slot_and_rejects_foreign_identity() {
        let dir = location("slot-repair");
        create_streams(&dir);
        let store_id = StoreId::from_bytes([0x11; 16]);
        let anchors = verified(&dir, store_id);
        drop(
            LedgerFrontier::rebuild_shadow(&dir, store_id, anchors.final_anchor())
                .expect("frontier builds"),
        );
        let path = dir.join("storage.frontier");
        let mut bytes = std::fs::read(&path).expect("frontier reads");
        assert_eq!(bytes.len(), FRONTIER_BYTES);
        assert_eq!(&bytes[..8], SLOT_MAGIC);
        assert_eq!(&bytes[SLOT_BYTES..SLOT_BYTES + 8], SLOT_MAGIC);

        bytes[0] ^= 1;
        std::fs::write(&path, bytes).expect("one slot corrupts");
        drop(LedgerFrontier::open_active(&dir, store_id, &anchors).expect("one slot repairs"));
        let repaired = std::fs::read(&path).expect("repaired frontier reads");
        assert!(matches!(decode_slot(&repaired[..SLOT_BYTES], 0, store_id), SlotDecode::Valid(_)));
        assert!(matches!(decode_slot(&repaired[SLOT_BYTES..], 1, store_id), SlotDecode::Valid(_)));

        let foreign = StoreId::from_bytes([0x22; 16]);
        let foreign_anchors = verified(&dir, foreign);
        assert_eq!(
            LedgerFrontier::open_active(&dir, foreign, &foreign_anchors).err(),
            Some(FrontierError::Integrity)
        );
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn active_frontier_rejects_wrong_size_both_damage_and_checksummed_incompatibility() {
        for tag in ["wrong-size", "both-damaged", "unknown-schema", "generation-gap"] {
            let dir = location(tag);
            create_streams(&dir);
            let store_id = StoreId::from_bytes([0x33; 16]);
            let anchors = verified(&dir, store_id);
            drop(
                LedgerFrontier::rebuild_shadow(&dir, store_id, anchors.final_anchor())
                    .expect("frontier builds"),
            );
            let path = dir.join("storage.frontier");
            let mut bytes = std::fs::read(&path).expect("frontier reads");
            match tag {
                "wrong-size" => bytes.pop().map(|_| ()).expect("frontier has a byte"),
                "both-damaged" => {
                    bytes[0] ^= 1;
                    bytes[SLOT_BYTES] ^= 1;
                }
                "unknown-schema" => {
                    bytes[8..10].copy_from_slice(&2_u16.to_le_bytes());
                    let checksum = slot_checksum(&bytes[..SLOT_CHECKSUM_START]);
                    bytes[SLOT_CHECKSUM_START..SLOT_BYTES].copy_from_slice(&checksum);
                }
                "generation-gap" => {
                    let start = SLOT_BYTES;
                    bytes[start + 32..start + 40].copy_from_slice(&5_u64.to_le_bytes());
                    let checksum = slot_checksum(&bytes[start..start + SLOT_CHECKSUM_START]);
                    bytes[start + SLOT_CHECKSUM_START..].copy_from_slice(&checksum);
                }
                _ => unreachable!(),
            }
            std::fs::write(&path, bytes).expect("hostile frontier writes");
            assert_eq!(
                LedgerFrontier::open_active(&dir, store_id, &anchors).err(),
                Some(FrontierError::Integrity),
                "{tag}"
            );
            std::fs::remove_dir_all(dir).ok();
        }
    }

    #[test]
    fn active_frontier_rejects_exact_ledger_fact_and_bound_loss() {
        let ledger_dir = location("ledger-loss");
        create_streams(&ledger_dir);
        let ledger_id = StoreId::from_bytes([1; 16]);
        let mut ledger = LedgerWriter::open(&ledger_dir.join("ledger")).expect("ledger opens");
        ledger.append(&rejection("op-a")).expect("rejection appends");
        ledger.sync().expect("rejection syncs");
        drop(ledger);
        let anchored = verified(&ledger_dir, ledger_id);
        drop(
            LedgerFrontier::rebuild_shadow(&ledger_dir, ledger_id, anchored.final_anchor())
                .expect("frontier builds"),
        );
        LedgerWriter::open(&ledger_dir.join("ledger"))
            .expect("ledger reopens")
            .truncate(0)
            .expect("exact frame is removed");
        let shortened = verified(&ledger_dir, ledger_id);
        assert_eq!(
            LedgerFrontier::open_active(&ledger_dir, ledger_id, &shortened).err(),
            Some(FrontierError::Integrity)
        );

        let fact_dir = location("fact-loss");
        create_streams(&fact_dir);
        let fact_id = StoreId::from_bytes([2; 16]);
        let mut retained =
            RetainedWriter::open(&fact_dir.join("retained")).expect("retained opens");
        retained.append_fact(&retained_fact("op-fact")).expect("fact appends");
        retained.sync().expect("fact syncs");
        drop(retained);
        let anchored = verified(&fact_dir, fact_id);
        drop(
            LedgerFrontier::rebuild_shadow(&fact_dir, fact_id, anchored.final_anchor())
                .expect("frontier builds"),
        );
        RetainedWriter::open(&fact_dir.join("retained"))
            .expect("retained reopens")
            .truncate(0)
            .expect("exact fact is removed");
        let shortened = verified(&fact_dir, fact_id);
        assert_eq!(
            LedgerFrontier::open_active(&fact_dir, fact_id, &shortened).err(),
            Some(FrontierError::Integrity)
        );

        let bound_dir = location("bound-loss");
        create_streams(&bound_dir);
        let bound_id = StoreId::from_bytes([3; 16]);
        let mut retained =
            RetainedWriter::open(&bound_dir.join("retained")).expect("retained opens");
        retained.append_bound(7, 7, &[]).expect("bound appends");
        retained.sync().expect("bound syncs");
        drop(retained);
        let anchored = verified(&bound_dir, bound_id);
        drop(
            LedgerFrontier::rebuild_shadow(&bound_dir, bound_id, anchored.final_anchor())
                .expect("frontier builds"),
        );
        RetainedWriter::open(&bound_dir.join("retained"))
            .expect("retained reopens")
            .truncate(0)
            .expect("exact bound is removed");
        let shortened = verified(&bound_dir, bound_id);
        assert_eq!(
            LedgerFrontier::open_active(&bound_dir, bound_id, &shortened).err(),
            Some(FrontierError::Integrity)
        );

        std::fs::remove_dir_all(ledger_dir).ok();
        std::fs::remove_dir_all(fact_dir).ok();
        std::fs::remove_dir_all(bound_dir).ok();
    }

    #[test]
    fn active_frontier_repairs_a_valid_suffix_before_a_torn_tail() {
        let dir = location("suffix-repair");
        create_streams(&dir);
        let store_id = StoreId::from_bytes([4; 16]);
        let empty = verified(&dir, store_id);
        drop(
            LedgerFrontier::rebuild_shadow(&dir, store_id, empty.final_anchor())
                .expect("empty frontier builds"),
        );
        let mut ledger = LedgerWriter::open(&dir.join("ledger")).expect("ledger opens");
        ledger.append(&rejection("op-a")).expect("valid suffix appends");
        ledger.sync().expect("valid suffix syncs");
        drop(ledger);
        std::fs::OpenOptions::new()
            .append(true)
            .open(dir.join("ledger"))
            .expect("ledger opens for torn tail")
            .write_all(&[1, 2, 3])
            .expect("torn tail appends");
        let scan = LedgerIndex::scan(&dir.join("ledger")).expect("ledger scans");
        assert!(scan.total_bytes > scan.good_bytes);
        let anchors = verified(&dir, store_id);
        let frontier =
            LedgerFrontier::open_active(&dir, store_id, &anchors).expect("suffix repairs");
        assert_eq!(frontier.observation(2).record_count, 1);
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn same_length_checksummed_stream_substitution_fails_the_blake3_anchor() {
        let dir = location("substitution");
        create_streams(&dir);
        let store_id = StoreId::from_bytes([5; 16]);
        let mut ledger = LedgerWriter::open(&dir.join("ledger")).expect("ledger opens");
        ledger.append(&rejection("op-a")).expect("original appends");
        ledger.sync().expect("original syncs");
        drop(ledger);
        let anchors = verified(&dir, store_id);
        drop(
            LedgerFrontier::rebuild_shadow(&dir, store_id, anchors.final_anchor())
                .expect("frontier builds"),
        );

        let replacement_dir = location("substitution-replacement");
        std::fs::create_dir_all(&replacement_dir).expect("replacement directory creates");
        let replacement_path = replacement_dir.join("ledger");
        let mut replacement = LedgerWriter::open(&replacement_path).expect("replacement opens");
        replacement.append(&rejection("op-b")).expect("replacement appends");
        replacement.sync().expect("replacement syncs");
        drop(replacement);
        let original_len = std::fs::metadata(dir.join("ledger")).expect("metadata").len();
        let replacement_bytes = std::fs::read(&replacement_path).expect("replacement reads");
        assert_eq!(original_len, replacement_bytes.len() as u64);
        std::fs::write(dir.join("ledger"), replacement_bytes).expect("stream substitutes");
        let substituted = verified(&dir, store_id);
        assert_eq!(
            LedgerFrontier::open_active(&dir, store_id, &substituted).err(),
            Some(FrontierError::Integrity)
        );
        std::fs::remove_dir_all(dir).ok();
        std::fs::remove_dir_all(replacement_dir).ok();
    }
}
