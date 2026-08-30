//! Independent BLAKE3 anchors for the activated LSM WAL and retained stream.
//!
//! Each stream has two fixed-size alternating slots. The WAL anchor includes
//! an epoch so a manifest can prove exactly which WAL it checkpointed before
//! recovery moves to the next empty epoch and truncates the old bytes.

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::Path,
};

use crate::storage::{
    frontier::StoreId,
    retained::{RetainedAppend, RetainedScan},
    root,
};

use super::wal::{WalAppend, WalScan};

const SLOT_SCHEMA: u16 = 1;
pub(crate) const SLOT_BYTES: usize = 128;
pub(crate) const FRONTIER_BYTES: usize = SLOT_BYTES * 2;
const CHECKSUM_START: usize = 96;

const WAL_FILE: &str = "wal.frontier";
const WAL_TEMP: &str = "wal.frontier.tmp";
const WAL_MAGIC: &[u8; 8] = b"TBWALFR1";
const WAL_CHAIN_DOMAIN: &[u8] = b"terrabase.lsm.wal-frontier.chain.v1\0";
const WAL_SLOT_DOMAIN: &[u8] = b"terrabase.lsm.wal-frontier.slot.v1\0";

const RETAINED_FILE: &str = "retained.frontier";
const RETAINED_TEMP: &str = "retained.frontier.tmp";
const RETAINED_MAGIC: &[u8; 8] = b"TBRETFR1";
const RETAINED_CHAIN_DOMAIN: &[u8] = b"terrabase.lsm.retained-frontier.chain.v1\0";
const RETAINED_SLOT_DOMAIN: &[u8] = b"terrabase.lsm.retained-frontier.slot.v1\0";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum FrontierError {
    Integrity,
    Unavailable,
}

/// One exact append-only stream prefix.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct StreamAnchor {
    pub(super) epoch: u64,
    pub(super) bytes: u64,
    pub(super) frame_count: u64,
    pub(super) chain_head: [u8; 32],
}

impl StreamAnchor {
    fn empty(store_id: StoreId, epoch: u64, domain: &[u8]) -> Self {
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(&store_id.as_bytes());
        hasher.update(&epoch.to_le_bytes());
        Self { epoch, bytes: 0, frame_count: 0, chain_head: *hasher.finalize().as_bytes() }
    }

    fn advance(self, store_id: StoreId, domain: &[u8], start: u64, frame: &[u8]) -> Option<Self> {
        if start != self.bytes {
            return None;
        }
        let raw_frame_len = u64::try_from(frame.len()).ok()?;
        let bytes = self.bytes.checked_add(raw_frame_len)?;
        let frame_count = self.frame_count.checked_add(1)?;
        let mut hasher = blake3::Hasher::new();
        hasher.update(domain);
        hasher.update(&store_id.as_bytes());
        hasher.update(&self.epoch.to_le_bytes());
        hasher.update(&self.chain_head);
        hasher.update(&frame_count.to_le_bytes());
        hasher.update(&start.to_le_bytes());
        hasher.update(&raw_frame_len.to_le_bytes());
        hasher.update(frame);
        Some(Self {
            epoch: self.epoch,
            bytes,
            frame_count,
            chain_head: *hasher.finalize().as_bytes(),
        })
    }
}

/// Every BLAKE3 anchor at a semantically verified frame boundary.
pub(super) struct VerifiedStream {
    anchors: Vec<StreamAnchor>,
}

impl VerifiedStream {
    fn from_boundaries(
        path: &Path,
        good_bytes: u64,
        frame_ends: &[u64],
        store_id: StoreId,
        epoch: u64,
        domain: &[u8],
    ) -> io::Result<Self> {
        let mut bytes = Vec::new();
        File::open(path)?.read_to_end(&mut bytes)?;
        let good_len = usize::try_from(good_bytes)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "stream is too large"))?;
        if bytes.len() < good_len {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "stream shrank while its frontier was computed",
            ));
        }
        let mut anchor = StreamAnchor::empty(store_id, epoch, domain);
        let mut anchors = Vec::with_capacity(frame_ends.len().saturating_add(1));
        anchors.push(anchor);
        let mut start = 0_usize;
        for &end_raw in frame_ends {
            let end = usize::try_from(end_raw).map_err(|_| {
                io::Error::new(io::ErrorKind::InvalidData, "frame boundary is too large")
            })?;
            if end < start || end > good_len {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "frame boundary lies outside the verified prefix",
                ));
            }
            anchor = anchor
                .advance(
                    store_id,
                    domain,
                    u64::try_from(start).expect("a file offset fits u64"),
                    &bytes[start..end],
                )
                .ok_or_else(|| {
                    io::Error::new(io::ErrorKind::InvalidData, "frontier chain cannot advance")
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

    pub(super) fn for_wal(
        path: &Path,
        scan: &WalScan,
        store_id: StoreId,
        epoch: u64,
    ) -> io::Result<Self> {
        Self::from_boundaries(
            path,
            scan.good_bytes,
            scan.frame_ends(),
            store_id,
            epoch,
            WAL_CHAIN_DOMAIN,
        )
    }

    pub(super) fn for_retained(
        path: &Path,
        scan: &RetainedScan,
        store_id: StoreId,
    ) -> io::Result<Self> {
        Self::from_boundaries(
            path,
            scan.good_bytes,
            scan.frame_ends(),
            store_id,
            0,
            RETAINED_CHAIN_DOMAIN,
        )
    }

    pub(super) fn final_anchor(&self) -> StreamAnchor {
        *self.anchors.last().expect("the empty anchor is always present")
    }

    pub(super) fn empty_anchor(&self) -> StreamAnchor {
        self.anchors[0]
    }

    pub(super) fn contains(&self, anchor: StreamAnchor) -> bool {
        self.anchors.contains(&anchor)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct FrontierSlot {
    pub(super) index: u8,
    pub(super) generation: u64,
    pub(super) anchor: StreamAnchor,
}

#[derive(Clone, Copy)]
struct FrontierKind {
    file: &'static str,
    temp: &'static str,
    magic: &'static [u8; 8],
    chain_domain: &'static [u8],
    slot_domain: &'static [u8],
}

const WAL_KIND: FrontierKind = FrontierKind {
    file: WAL_FILE,
    temp: WAL_TEMP,
    magic: WAL_MAGIC,
    chain_domain: WAL_CHAIN_DOMAIN,
    slot_domain: WAL_SLOT_DOMAIN,
};

const RETAINED_KIND: FrontierKind = FrontierKind {
    file: RETAINED_FILE,
    temp: RETAINED_TEMP,
    magic: RETAINED_MAGIC,
    chain_domain: RETAINED_CHAIN_DOMAIN,
    slot_domain: RETAINED_SLOT_DOMAIN,
};

struct SlotFrontier {
    file: File,
    store_id: StoreId,
    kind: FrontierKind,
    slots: [Option<FrontierSlot>; 2],
    generation: u64,
    anchor: StreamAnchor,
}

impl SlotFrontier {
    fn rebuild(
        dir: &Path,
        store_id: StoreId,
        epoch: u64,
        final_anchor: StreamAnchor,
        kind: FrontierKind,
    ) -> Result<Self, FrontierError> {
        let temp = dir.join(kind.temp);
        let live = dir.join(kind.file);
        let mut file = OpenOptions::new()
            .create(true)
            .truncate(true)
            .read(true)
            .write(true)
            .open(&temp)
            .map_err(|_| FrontierError::Unavailable)?;
        file.set_len(u64::try_from(FRONTIER_BYTES).expect("frontier length fits u64"))
            .map_err(|_| FrontierError::Unavailable)?;
        let empty = StreamAnchor::empty(store_id, epoch, kind.chain_domain);
        let first = FrontierSlot { index: 0, generation: 0, anchor: empty };
        let second = FrontierSlot { index: 1, generation: 1, anchor: empty };
        write_slot(&mut file, first, store_id, kind).map_err(|_| FrontierError::Unavailable)?;
        write_slot(&mut file, second, store_id, kind).map_err(|_| FrontierError::Unavailable)?;
        let (slots, generation, anchor) = if final_anchor == empty {
            ([Some(first), Some(second)], 1, empty)
        } else {
            let final_slot = FrontierSlot { index: 0, generation: 2, anchor: final_anchor };
            write_slot(&mut file, final_slot, store_id, kind)
                .map_err(|_| FrontierError::Unavailable)?;
            ([Some(final_slot), Some(second)], 2, final_anchor)
        };
        file.sync_all().map_err(|_| FrontierError::Unavailable)?;
        fs::rename(&temp, &live).map_err(|_| FrontierError::Unavailable)?;
        root::sync_dir(dir).map_err(|_| FrontierError::Unavailable)?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(&live)
            .map_err(|_| FrontierError::Unavailable)?;
        Ok(Self { file, store_id, kind, slots, generation, anchor })
    }

    fn open_active(
        dir: &Path,
        store_id: StoreId,
        kind: FrontierKind,
    ) -> Result<Self, FrontierError> {
        let path = dir.join(kind.file);
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
            decode_slot(&bytes[..SLOT_BYTES], 0, store_id, kind),
            decode_slot(&bytes[SLOT_BYTES..], 1, store_id, kind),
        ];
        if decoded
            .iter()
            .any(|slot| matches!(slot, SlotDecode::Incompatible | SlotDecode::ForeignStore))
        {
            return Err(FrontierError::Integrity);
        }
        let mut slots = [None, None];
        for (index, decoded) in decoded.into_iter().enumerate() {
            if let SlotDecode::Valid(slot) = decoded {
                slots[index] = Some(slot);
            }
        }
        let mut valid = slots.iter().flatten().copied().collect::<Vec<_>>();
        if valid.is_empty() {
            return Err(FrontierError::Integrity);
        }
        valid.sort_by_key(|slot| slot.generation);
        if valid.len() == 2 && valid[1].generation.checked_sub(valid[0].generation) != Some(1) {
            return Err(FrontierError::Integrity);
        }
        let selected = *valid.last().expect("at least one slot verifies");
        Ok(Self {
            file,
            store_id,
            kind,
            slots,
            generation: selected.generation,
            anchor: selected.anchor,
        })
    }

    fn write_next(&mut self, anchor: StreamAnchor) -> Result<(), FrontierError> {
        let generation = self.generation.checked_add(1).ok_or(FrontierError::Integrity)?;
        let index = u8::try_from(generation & 1).expect("slot parity is one bit");
        let slot = FrontierSlot { index, generation, anchor };
        write_slot(&mut self.file, slot, self.store_id, self.kind)
            .map_err(|_| FrontierError::Unavailable)?;
        self.file.sync_data().map_err(|_| FrontierError::Unavailable)?;
        self.slots[usize::from(index)] = Some(slot);
        self.generation = generation;
        self.anchor = anchor;
        Ok(())
    }

    fn advance(&mut self, start: u64, frame: &[u8]) -> Result<(), FrontierError> {
        let next = self
            .anchor
            .advance(self.store_id, self.kind.chain_domain, start, frame)
            .ok_or(FrontierError::Integrity)?;
        self.write_next(next)
    }

    fn repair_or_advance(&mut self, verified: &VerifiedStream) -> Result<(), FrontierError> {
        for slot in self.slots.iter().flatten() {
            if slot.anchor.epoch != self.anchor.epoch || !verified.contains(slot.anchor) {
                return Err(FrontierError::Integrity);
            }
        }
        if !verified.contains(self.anchor) {
            return Err(FrontierError::Integrity);
        }
        let final_anchor = verified.final_anchor();
        if self.anchor != final_anchor || self.slots.iter().flatten().count() == 1 {
            self.write_next(final_anchor)?;
        }
        Ok(())
    }

    fn both_slots_equal(&self, anchor: StreamAnchor) -> bool {
        self.slots.iter().all(|slot| slot.is_some_and(|slot| slot.anchor == anchor))
    }

    fn ensure_both(&mut self, anchor: StreamAnchor) -> Result<(), FrontierError> {
        for _ in 0..2 {
            if self.both_slots_equal(anchor) {
                return Ok(());
            }
            self.write_next(anchor)?;
        }
        if self.both_slots_equal(anchor) { Ok(()) } else { Err(FrontierError::Integrity) }
    }
}

/// The activated WAL frontier.
pub(super) struct WalFrontier(SlotFrontier);

impl WalFrontier {
    pub(super) fn rebuild_shadow(
        dir: &Path,
        store_id: StoreId,
        epoch: u64,
        final_anchor: StreamAnchor,
    ) -> Result<Self, FrontierError> {
        SlotFrontier::rebuild(dir, store_id, epoch, final_anchor, WAL_KIND).map(Self)
    }

    pub(super) fn open_active(dir: &Path, store_id: StoreId) -> Result<Self, FrontierError> {
        SlotFrontier::open_active(dir, store_id, WAL_KIND).map(Self)
    }

    pub(super) fn advance(&mut self, append: &WalAppend) -> Result<(), FrontierError> {
        self.0.advance(append.start, &append.frame)
    }

    pub(super) fn repair_or_advance(
        &mut self,
        verified: &VerifiedStream,
    ) -> Result<(), FrontierError> {
        self.0.repair_or_advance(verified)
    }

    /// Writes and syncs one next-epoch empty slot. The checkpoint calls this
    /// twice and exposes the boundary between the writes to fault injection.
    pub(super) fn write_epoch_zero(&mut self, next_epoch: u64) -> Result<(), FrontierError> {
        let empty = StreamAnchor::empty(self.0.store_id, next_epoch, WAL_CHAIN_DOMAIN);
        self.0.write_next(empty)
    }

    pub(super) fn ensure_epoch_established(&mut self, epoch: u64) -> Result<(), FrontierError> {
        let empty = StreamAnchor::empty(self.0.store_id, epoch, WAL_CHAIN_DOMAIN);
        self.0.ensure_both(empty)
    }

    pub(super) const fn anchor(&self) -> StreamAnchor {
        self.0.anchor
    }

    pub(super) const fn generation(&self) -> u64 {
        self.0.generation
    }

    pub(super) fn slots(&self) -> &[Option<FrontierSlot>; 2] {
        &self.0.slots
    }

    pub(super) fn epoch_established(&self, epoch: u64) -> bool {
        let empty = StreamAnchor::empty(self.0.store_id, epoch, WAL_CHAIN_DOMAIN);
        self.0.both_slots_equal(empty)
    }
}

/// The activated retained-evidence frontier.
pub(super) struct RetainedFrontier(SlotFrontier);

impl RetainedFrontier {
    pub(super) fn rebuild_shadow(
        dir: &Path,
        store_id: StoreId,
        final_anchor: StreamAnchor,
    ) -> Result<Self, FrontierError> {
        SlotFrontier::rebuild(dir, store_id, 0, final_anchor, RETAINED_KIND).map(Self)
    }

    pub(super) fn open_active(dir: &Path, store_id: StoreId) -> Result<Self, FrontierError> {
        SlotFrontier::open_active(dir, store_id, RETAINED_KIND).map(Self)
    }

    pub(super) fn advance(&mut self, appends: &[RetainedAppend]) -> Result<(), FrontierError> {
        for append in appends {
            self.0.advance(append.start, &append.frame)?;
        }
        Ok(())
    }

    pub(super) fn repair_or_advance(
        &mut self,
        verified: &VerifiedStream,
    ) -> Result<(), FrontierError> {
        self.0.repair_or_advance(verified)
    }

    pub(super) const fn anchor(&self) -> StreamAnchor {
        self.0.anchor
    }
}

fn write_slot(
    file: &mut File,
    slot: FrontierSlot,
    store_id: StoreId,
    kind: FrontierKind,
) -> io::Result<()> {
    let bytes = encode_slot(slot, store_id, kind);
    let offset = u64::from(slot.index)
        .checked_mul(u64::try_from(SLOT_BYTES).expect("slot length fits u64"))
        .expect("two slot offsets fit u64");
    file.seek(SeekFrom::Start(offset))?;
    file.write_all(&bytes)
}

fn encode_slot(slot: FrontierSlot, store_id: StoreId, kind: FrontierKind) -> [u8; SLOT_BYTES] {
    let mut bytes = [0_u8; SLOT_BYTES];
    bytes[..8].copy_from_slice(kind.magic);
    bytes[8..10].copy_from_slice(&SLOT_SCHEMA.to_le_bytes());
    bytes[10] = slot.index;
    bytes[16..32].copy_from_slice(&store_id.as_bytes());
    bytes[32..40].copy_from_slice(&slot.generation.to_le_bytes());
    bytes[40..48].copy_from_slice(&slot.anchor.epoch.to_le_bytes());
    bytes[48..56].copy_from_slice(&slot.anchor.bytes.to_le_bytes());
    bytes[56..64].copy_from_slice(&slot.anchor.frame_count.to_le_bytes());
    bytes[64..96].copy_from_slice(&slot.anchor.chain_head);
    let checksum = slot_checksum(&bytes[..CHECKSUM_START], kind.slot_domain);
    bytes[CHECKSUM_START..].copy_from_slice(&checksum);
    bytes
}

enum SlotDecode {
    Valid(FrontierSlot),
    Damaged,
    Incompatible,
    ForeignStore,
}

fn decode_slot(
    bytes: &[u8],
    expected_index: u8,
    store_id: StoreId,
    kind: FrontierKind,
) -> SlotDecode {
    if bytes.len() != SLOT_BYTES
        || bytes[CHECKSUM_START..] != slot_checksum(&bytes[..CHECKSUM_START], kind.slot_domain)
    {
        return SlotDecode::Damaged;
    }
    if bytes[..8] != *kind.magic
        || u16::from_le_bytes(bytes[8..10].try_into().expect("slot schema")) != SLOT_SCHEMA
        || bytes[10] != expected_index
        || bytes[11..16] != [0; 5]
    {
        return SlotDecode::Incompatible;
    }
    if bytes[16..32] != store_id.as_bytes() {
        return SlotDecode::ForeignStore;
    }
    let generation = u64::from_le_bytes(bytes[32..40].try_into().expect("generation"));
    if generation & 1 != u64::from(expected_index) {
        return SlotDecode::Incompatible;
    }
    let epoch = u64::from_le_bytes(bytes[40..48].try_into().expect("stream epoch"));
    let stream_bytes = u64::from_le_bytes(bytes[48..56].try_into().expect("stream bytes"));
    let frame_count = u64::from_le_bytes(bytes[56..64].try_into().expect("frame count"));
    let mut chain_head = [0_u8; 32];
    chain_head.copy_from_slice(&bytes[64..96]);
    SlotDecode::Valid(FrontierSlot {
        index: expected_index,
        generation,
        anchor: StreamAnchor { epoch, bytes: stream_bytes, frame_count, chain_head },
    })
}

fn slot_checksum(bytes: &[u8], domain: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(domain);
    hasher.update(bytes);
    *hasher.finalize().as_bytes()
}

#[cfg(test)]
pub(super) mod test_support {
    pub(crate) use super::{FRONTIER_BYTES, SLOT_BYTES};

    /// Recomputes one WAL slot checksum after a hostile test changes a
    /// checksummed compatibility field.
    pub(crate) fn resign_wal_slot(bytes: &mut [u8], slot_index: usize) {
        let start = slot_index * super::SLOT_BYTES;
        let checksum = super::slot_checksum(
            &bytes[start..start + super::CHECKSUM_START],
            super::WAL_SLOT_DOMAIN,
        );
        bytes[start + super::CHECKSUM_START..start + super::SLOT_BYTES].copy_from_slice(&checksum);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(bytes: &[u8]) -> String {
        use std::fmt::Write as _;

        let mut output = String::with_capacity(bytes.len() * 2);
        for byte in bytes {
            write!(output, "{byte:02x}").expect("writing to a String cannot fail");
        }
        output
    }

    #[test]
    fn wal_frontier_slot_encoding_matches_the_pinned_golden_vector() {
        let store_id = StoreId::from_bytes([
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d,
            0x0e, 0x0f,
        ]);
        let slot = FrontierSlot {
            index: 1,
            generation: 9,
            anchor: StreamAnchor { epoch: 7, bytes: 1_234, frame_count: 5, chain_head: [0xa5; 32] },
        };
        assert_eq!(
            hex(&encode_slot(slot, store_id, WAL_KIND)),
            "544257414c4652310100010000000000000102030405060708090a0b0c0d0e0f09000000000000000700000000000000d2040000000000000500000000000000a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a581c30a588ced4d909b2e4c03bb61dc14d06283a7db646d1e2f48d97b303b5d4c"
        );
    }
}
