//! The intra-region WAL-frame follower (S-05 slice 2b, D-35/D-45): the
//! RF=2 copy of a writer's `SQLite` file is a physical follower — committed
//! WAL frames ship verbatim, the follower validates each frame's
//! cumulative checksum, appends to its own `-wal`, `fdatasync`s, and
//! acks. `RegionalDurable` is the local commit plus that ack (which is
//! what lets the writer run `synchronous=NORMAL`, D-43). Promotion opens
//! the follower's file — byte-identical, no logical replay, no CRDT.
//!
//! ## Capture
//!
//! The engine runs one committer per file with `wal_autocheckpoint=0`
//! (D-43), so committed frames appear in the writer's `-wal` as whole
//! 24-byte-header-plus-page frames and a frame-count diff per pump tick is
//! the honest capture: [`WalTail::poll`] reads the WAL header once per
//! generation, validates every newly visible frame's salt and cumulative
//! checksum, and hands back the raw frame bytes. `rusqlite` does not
//! expose `sqlite3_wal_hook`, and this crate denies `unsafe_code`, so the
//! poll is the capture (a wal-hook capture is the same validated walk
//! driven by the hook instead of the tick).
//!
//! ## The salt rule, and the restart per wave
//!
//! `SQLite`'s WAL header carries salts that rotate whenever the writer
//! restarts its WAL. A truncating checkpoint (the retention sweep) is one
//! restart; the routine one is stock `SQLite` behavior after any checkpoint
//! that runs to completion: the group-commit leader checkpoints passively
//! between waves (`wal_autocheckpoint=0`, D-43), and the next wave's first
//! write then restarts the WAL in place — salt-1 increments by one, frames
//! overwrite from frame zero, and the file is NOT truncated, so the old
//! generation's frames linger past the new generation's prefix. The tail
//! therefore treats a salt change as the routine wave boundary, and the
//! validation walk stops at the first salt mismatch so stale frames never
//! ship.
//!
//! The restart overwrites: a generation the tail has not fully shipped
//! before the next wave lands is unrecoverable from the file (its early
//! frames are gone). The tailer must ship each generation inside its
//! wave's quiescent window; losing that race reports
//! [`WalTailStep::GenerationChanged`] with an incomplete prefix, and the
//! composition re-bootstraps from a snapshot instead of adopting.
//!
//! ## The steady state: ship, ack, fold, adopt
//!
//! The writer folds each generation into its own database file with the
//! between-waves checkpoint, so the follower converges without ever
//! re-copying the database: once the tail has shipped a generation fully
//! (a poll that returns [`WalTailStep::UpToDate`] on the old salts) and
//! the follower has acked it, [`WalFollower::fold_generation`] runs the
//! same truncating checkpoint over the follower's location — folding the
//! identical frames, deterministically reaching the identical file — and
//! resets for the next generation, whose first batch (frame zero of the
//! new salts) the follower adopts like a fresh prefix. `tests/wal_follower.rs`
//! pins the whole loop across two commit waves.
//!
//! ## The bootstrap contract
//!
//! [`WalFollower::bootstrap`] copies the writer's ownership record
//! (`owner.tbv1`, when present), `terrabase.sqlite3`, and the valid prefix of
//! its `-wal` — an at-rest set is consistent (main at or past the last
//! generation boundary, WAL holding the current generation from frame zero).
//! It returns the follower alongside the [`WalCheckpoint`] the tailer passes
//! to [`WalTail::resume`], so the tail continues past the snapshot instead of
//! re-shipping it. The caller must invoke it with the writer quiescent (no
//! commit wave or checkpoint in flight). [`WalFollower::bootstrap_engine`]
//! acquires the live engine's commit and checkpoint barrier for this copy.
//! File-only callers must already own an equivalent quiescence boundary.
//!
//! ## Capture and transport boundaries
//!
//! - [`WalFrames`] carry logical SQLite bytes through the authenticated
//!   production dispatcher in [`crate::wal_driver`]. Encrypted locations
//!   use the admitted physical-file VFS for capture and follower writes.
//! - The fold is one `wal_checkpoint(TRUNCATE)` per adopted generation;
//!   there is no pipelining across generations yet.
//! - The tail reads the header and seeks past its shipped prefix. It scans
//!   for a complete transaction using bounded buffers, then sends at most
//!   one MiB of frames per batch. An idle poll reads just the header.
//!   A pending batch remains in memory until its durable acknowledgement.
//!   Partial batches never acknowledge a transaction's commit prematurely.
//!
//! Promotion is opening the follower's location under a new replica
//! epoch: `EngineHost::open` (or `MemberHost::open`) on the follower
//! directory — `SQLite`'s recovery validates every frame the follower
//! appended, and the engine serves the file. `tests/wal_follower.rs`
//! pins exactly that.

use std::fs::File;
use std::io::{Read, Seek, Write};
use std::path::{Path, PathBuf};
use terrabase::storage::{
    create_physical_file, open_physical_file, open_physical_sqlite, EncryptedStorage, PhysicalFile,
};

/// The engine's `SQLite` database file name inside a durable location.
const DATABASE_FILE: &str = "terrabase.sqlite3";

/// The location's ownership record: copied when present for the same reason.
const OWNER_FILE: &str = "owner.tbv1";

/// The WAL file's fixed header size, in bytes.
pub const WAL_HEADER_BYTES: usize = 32;

/// Maximum frame payload held by one tail batch, excluding its fixed header.
pub const MAX_WAL_BATCH_BYTES: usize = 1024 * 1024;

/// One WAL frame's fixed header size, in bytes.
pub const FRAME_HEADER_BYTES: usize = 24;

/// The `SQLite` WAL magics: the low bit selects the byte order of checksum
/// words (`...82` little-endian, `...83` big-endian); the header's other
/// fields are always big-endian.
const WAL_MAGIC_LE: u32 = 0x377F_0682;
const WAL_MAGIC_BE: u32 = 0x377F_0683;

/// A validated WAL header: the fields the tail and follower track.
#[derive(Clone, Copy, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct WalHeader {
    /// The magic as read (selects the checksum word order).
    magic: u32,
    /// The page size, a power of two between 512 and 65536.
    page_size: usize,
    /// The generation's salt pair.
    salts: (u32, u32),
    /// The header's own checksum, the root of the frame checksum chain.
    checksum: (u32, u32),
}

impl WalHeader {
    /// Parses and verifies one 32-byte WAL header.
    ///
    /// # Errors
    ///
    /// Returns [`WalFollowerError::BadMagic`], [`BadPageSize`], or
    /// [`HeaderChecksum`] for every field or checksum violation.
    pub fn parse(bytes: &[u8]) -> Result<Self, WalFollowerError> {
        if bytes.len() < WAL_HEADER_BYTES {
            return Err(WalFollowerError::TruncatedHeader);
        }
        let magic = be_u32(&bytes[0..4]);
        if magic != WAL_MAGIC_LE && magic != WAL_MAGIC_BE {
            return Err(WalFollowerError::BadMagic);
        }
        let big_endian = magic == WAL_MAGIC_BE;
        let page_size =
            usize::try_from(be_u32(&bytes[8..12])).map_err(|_| WalFollowerError::BadPageSize)?;
        if !page_size.is_power_of_two() || !(512..=65_536).contains(&page_size) {
            return Err(WalFollowerError::BadPageSize);
        }
        let salts = (be_u32(&bytes[16..20]), be_u32(&bytes[20..24]));
        let stored = (be_u32(&bytes[24..28]), be_u32(&bytes[28..32]));
        let computed = wal_checksum(big_endian, &bytes[..24], (0, 0));
        if stored != computed {
            return Err(WalFollowerError::HeaderChecksum);
        }
        Ok(Self { magic, page_size, salts, checksum: stored })
    }

    /// The checksum word order the magic selects.
    #[must_use]
    pub const fn big_endian_checksums(&self) -> bool {
        self.magic == WAL_MAGIC_BE
    }

    /// The generation's page size.
    #[must_use]
    pub const fn page_size(&self) -> usize {
        self.page_size
    }

    /// One frame's size: its header plus one page.
    #[must_use]
    pub const fn frame_size(&self) -> usize {
        FRAME_HEADER_BYTES + self.page_size
    }

    /// The whole-frame count a WAL of `len` bytes holds past its header
    /// (a torn trailing frame does not count).
    #[must_use]
    pub fn complete_frames(&self, len: usize) -> usize {
        len.saturating_sub(WAL_HEADER_BYTES) / self.frame_size()
    }

    /// The byte range of frames `from..to` inside the WAL file.
    #[must_use]
    pub fn frame_range(&self, from: usize, to: usize) -> std::ops::Range<usize> {
        let frame = self.frame_size();
        (WAL_HEADER_BYTES + from * frame)..(WAL_HEADER_BYTES + to * frame)
    }
}

/// The Fibonacci-weighted cumulative WAL checksum over `data`'s u32 word
/// pairs, continuing from `state`. The word order follows the WAL magic.
fn wal_checksum(big_endian: bool, data: &[u8], state: (u32, u32)) -> (u32, u32) {
    let mut pair = state;
    let (chunks, _) = data.as_chunks::<8>();
    for chunk in chunks {
        let words = if big_endian {
            (
                u32::from_be_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]),
                u32::from_be_bytes([chunk[4], chunk[5], chunk[6], chunk[7]]),
            )
        } else {
            (
                u32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]),
                u32::from_le_bytes([chunk[4], chunk[5], chunk[6], chunk[7]]),
            )
        };
        pair.0 = pair.0.wrapping_add(words.0).wrapping_add(pair.1);
        pair.1 = pair.1.wrapping_add(words.1).wrapping_add(pair.0);
    }
    pair
}

/// Reads one big-endian u32.
fn be_u32(bytes: &[u8]) -> u32 {
    u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]])
}

/// One validated frame of the current generation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct ValidatedFrame {
    /// Whether the frame is a commit frame (its commit-size field is set).
    commit: bool,
    /// The cumulative checksum after this frame.
    checksum: (u32, u32),
}

/// Walks a WAL's frames from a known chain state, validating salts and
/// cumulative checksums; returns the validated frames, stopping before
/// the first torn or corrupt frame.
/// Walks a generation's frames from a known chain state, validating salts
/// and cumulative checksums; returns the validated frames, stopping before
/// the first torn or corrupt frame.
///
/// `frames` holds exactly `count` frames with NO WAL header prefix (a file
/// slice past its header, or a shipped batch): frame `i` sits at byte
/// offset `i * frame_size`. Callers map offsets back to global frame
/// numbers themselves.
fn validate_frames(
    header: &WalHeader,
    frames: &[u8],
    count: usize,
    state: (u32, u32),
) -> (Vec<ValidatedFrame>, (u32, u32)) {
    let mut validated = Vec::with_capacity(count);
    let mut chain = state;
    for offset in 0..count {
        let start = offset * header.frame_size();
        let frame = &frames[start..start + header.frame_size()];
        let salts = (be_u32(&frame[8..12]), be_u32(&frame[12..16]));
        if salts != header.salts {
            break;
        }
        let computed = wal_checksum(header.big_endian_checksums(), &frame[..8], chain);
        let computed =
            wal_checksum(header.big_endian_checksums(), &frame[FRAME_HEADER_BYTES..], computed);
        if computed != (be_u32(&frame[16..20]), be_u32(&frame[20..24])) {
            break;
        }
        chain = computed;
        validated.push(ValidatedFrame { commit: be_u32(&frame[4..8]) != 0, checksum: computed });
    }
    (validated, chain)
}

/// The writer-side tail of one `SQLite` location's WAL.
pub struct WalTail {
    location: PathBuf,
    acknowledged: WalCheckpoint,
    pending: Option<Box<WalFrames>>,
    header: Option<WalHeader>,
    /// The frames already handed out of the current generation.
    shipped: usize,
    /// The chain state after `shipped` frames.
    chain: (u32, u32),
    /// Source commit boundary confirmed by a bounded scan in this generation.
    confirmed_through: usize,
    /// The latest shipped commit and its checksum.
    last_commit: Option<(usize, (u32, u32))>,
}

/// One poll of the writer's WAL.
#[derive(Debug, Eq, PartialEq)]
pub enum WalTailStep {
    /// No new complete commit and no batch awaiting acknowledgment.
    UpToDate,
    /// Newly validated frames, ready to ship verbatim: the generation's
    /// header, the index of the first new frame, the count, the raw frame
    /// bytes, and the committed prefix (the frame count through the last
    /// commit frame) they carry.
    Frames(Box<WalFrames>),
    /// The writer restarted its WAL (new salts, or a shrink past the
    /// shipped prefix): re-bootstrap the follower from a file snapshot
    /// before tailing again.
    GenerationChanged,
}

/// One shippable batch of validated WAL frames.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WalFrames {
    /// The generation's 32-byte header.
    pub header: Vec<u8>,
    /// The index of the first frame of `bytes` inside the generation.
    pub from_frame: usize,
    /// The raw frame bytes, verbatim.
    pub bytes: Vec<u8>,
    /// The frame count through the batch's last commit frame — the
    /// durable state the batch carries; `None` when the batch holds no
    /// commit frame.
    pub committed_through: Option<usize>,
}

/// The writer-side position a bootstrap hands the tailer: everything the
/// fresh follower already holds durably, so the tail resumes past the
/// snapshot instead of re-shipping it (the follower fails closed on a batch
/// that does not continue its prefix). A live tail snapshots the same
/// position with [`WalTail::checkpoint`] before a tailer restart.
#[derive(Clone, Debug, Default, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct WalCheckpoint {
    header: Option<WalHeader>,
    shipped: usize,
    chain: (u32, u32),
    last_commit: Option<(usize, (u32, u32))>,
}

impl WalTail {
    /// Restores an authenticated follower position after checking its complete
    /// checksum prefix against the live writer under its capture barrier.
    /// The caller must independently establish that both files share a snapshot.
    ///
    /// # Errors
    /// Rejects a changed generation, corrupt prefix, or unavailable barrier.
    pub fn resume_engine(
        engine: &terrabase::Engine,
        checkpoint: WalCheckpoint,
    ) -> Result<Self, WalFollowerError> {
        engine
            .with_quiescent_wal(|location| {
                if let Some(expected) = checkpoint.header {
                    let mut file = open_physical_file(&location.join(wal_file_name()))?;
                    let mut bytes = [0; WAL_HEADER_BYTES];
                    file.read_exact(&mut bytes)?;
                    let header = WalHeader::parse(&bytes)?;
                    if header != expected {
                        return Err(WalFollowerError::GenerationMismatch);
                    }
                    let mut position = 0;
                    let mut chain = header.checksum;
                    let mut last_commit = None;
                    let mut buffer = Vec::new();
                    while position < checkpoint.shipped {
                        let count = (checkpoint.shipped - position)
                            .min(MAX_WAL_BATCH_BYTES / header.frame_size());
                        buffer.resize(count * header.frame_size(), 0);
                        file.read_exact(&mut buffer)?;
                        let (frames, next_chain) = validate_frames(&header, &buffer, count, chain);
                        if frames.len() != count {
                            return Err(WalFollowerError::FrameChecksum);
                        }
                        for (offset, frame) in frames.iter().enumerate() {
                            if frame.commit {
                                last_commit = Some((position + offset + 1, frame.checksum));
                            }
                        }
                        position += count;
                        chain = next_chain;
                    }
                    if chain != checkpoint.chain || last_commit != checkpoint.last_commit {
                        return Err(WalFollowerError::AcknowledgmentMismatch);
                    }
                } else if checkpoint != WalCheckpoint::default() {
                    return Err(WalFollowerError::AcknowledgmentMismatch);
                }
                Ok(Self::resume(location, checkpoint))
            })
            .map_err(|_| WalFollowerError::CaptureUnavailable)?
    }
    /// Creates the tail over one engine location.
    #[must_use]
    pub fn new(location: impl Into<PathBuf>) -> Self {
        Self {
            location: location.into(),
            acknowledged: WalCheckpoint {
                header: None,
                shipped: 0,
                chain: (0, 0),
                last_commit: None,
            },
            pending: None,
            confirmed_through: 0,
            header: None,
            shipped: 0,
            chain: (0, 0),
            last_commit: None,
        }
    }

    /// Snapshots this tail's position: the [`WalCheckpoint`] a restarted
    /// tailer passes to [`WalTail::resume`] to continue past what the
    /// follower already holds, without re-shipping the snapshot prefix.
    /// Merely polling never advances this position; [`Self::acknowledge`]
    /// must verify the follower's durable receipt first.
    #[must_use]
    pub fn checkpoint(&self) -> WalCheckpoint {
        self.acknowledged.clone()
    }

    /// Advances the resumable position only after the pending batch is durable.
    ///
    /// # Errors
    ///
    /// Returns an acknowledgment mismatch for another generation or prefix,
    /// a non-durable receipt, or when no batch is awaiting acknowledgment.
    pub fn acknowledge(&mut self, ack: FollowerAck) -> Result<(), WalFollowerError> {
        if self.pending.is_none()
            || !ack.fdatasynced
            || Some(ack.header) != self.header
            || ack.applied != self.shipped
            || ack.chain != self.chain
            || ack.committed_through != self.last_commit.map(|(frame, _)| frame)
        {
            return Err(WalFollowerError::AcknowledgmentMismatch);
        }
        self.acknowledged = WalCheckpoint {
            header: self.header,
            shipped: self.shipped,
            chain: self.chain,
            last_commit: self.last_commit,
        };
        self.pending = None;
        Ok(())
    }

    /// Resumes a tail past a bootstrap's snapshot (or a stored checkpoint)
    /// over the same writer location: the next poll validates and ships
    /// only frames past the checkpoint's shipped prefix.
    #[must_use]
    pub fn resume(location: impl Into<PathBuf>, checkpoint: WalCheckpoint) -> Self {
        Self {
            location: location.into(),
            acknowledged: checkpoint.clone(),
            pending: None,
            confirmed_through: checkpoint.shipped,
            header: checkpoint.header,
            shipped: checkpoint.shipped,
            chain: checkpoint.chain,
            last_commit: checkpoint.last_commit,
        }
    }

    /// Captures a live engine's committed WAL while writes and checkpoints pause.
    /// The engine must own this tail's location. Network delivery and waiting
    /// for the follower's receipt happen after this method returns.
    ///
    /// # Errors
    ///
    /// Returns a capture-barrier, location-mismatch, filesystem, or WAL
    /// validation error. A mismatched engine never advances the tail.
    pub fn poll_engine(
        &mut self,
        engine: &terrabase::Engine,
    ) -> Result<WalTailStep, WalFollowerError> {
        engine
            .with_quiescent_wal(|location| {
                if std::fs::canonicalize(location)? != std::fs::canonicalize(&self.location)? {
                    return Err(WalFollowerError::CaptureLocationMismatch);
                }
                self.poll()
            })
            .map_err(|_| WalFollowerError::CaptureUnavailable)?
    }

    /// Polls the writer's WAL through the last complete commit. Until
    /// [`Self::acknowledge`] succeeds, polls return the identical pending
    /// batch even if the writer has moved to another generation. Large
    /// committed transactions span bounded batches. Uncommitted spill frames
    /// remain on the writer and can be rewritten without diverging
    /// the follower's prefix.
    /// Live engine callers should use [`Self::poll_engine`] to exclude WAL
    /// rotation during capture; file-only callers own that barrier themselves.
    ///
    /// # Errors
    ///
    /// Returns [`WalFollowerError`] when the WAL cannot be read or its
    /// header is malformed.
    pub fn poll(&mut self) -> Result<WalTailStep, WalFollowerError> {
        if let Some(batch) = &self.pending {
            return Ok(WalTailStep::Frames(batch.clone()));
        }
        match open_physical_file(&self.location.join(wal_file_name())) {
            Ok(mut file) => self.poll_reader(&mut file),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                Ok(if self.header.is_some() {
                    WalTailStep::GenerationChanged
                } else {
                    WalTailStep::UpToDate
                })
            }
            Err(error) => Err(error.into()),
        }
    }

    fn poll_reader(
        &mut self,
        reader: &mut (impl Read + Seek),
    ) -> Result<WalTailStep, WalFollowerError> {
        reader.rewind()?;
        let mut header_bytes = Vec::with_capacity(WAL_HEADER_BYTES);
        reader.take(WAL_HEADER_BYTES as u64).read_to_end(&mut header_bytes)?;
        if header_bytes.len() < WAL_HEADER_BYTES {
            return Ok(if self.header.is_some() {
                WalTailStep::GenerationChanged
            } else {
                WalTailStep::UpToDate
            });
        }
        let header = match WalHeader::parse(&header_bytes) {
            Ok(header) => header,
            Err(WalFollowerError::TruncatedHeader | WalFollowerError::HeaderChecksum)
                if self.header.is_none() =>
            {
                return Ok(WalTailStep::UpToDate)
            }
            Err(error) => return Err(error),
        };
        if self.header.is_some_and(|tracked| tracked != header) {
            return Ok(WalTailStep::GenerationChanged);
        }
        let length = usize::try_from(reader.seek(std::io::SeekFrom::End(0))?)
            .map_err(|_| std::io::Error::other("WAL length exceeds the address space"))?;
        let total = header.complete_frames(length);
        if total < self.shipped {
            return Ok(WalTailStep::GenerationChanged);
        }
        if self.header.is_none() {
            self.header = Some(header);
            self.chain = header.checksum;
        }
        if total == self.shipped {
            return Ok(WalTailStep::UpToDate);
        }
        if total < self.confirmed_through {
            return Ok(WalTailStep::GenerationChanged);
        }
        let count = (total - self.shipped).min(MAX_WAL_BATCH_BYTES / header.frame_size());
        let range = header.frame_range(self.shipped, self.shipped + count);
        reader.seek(std::io::SeekFrom::Start(range.start as u64))?;
        let mut bytes = vec![0; range.len()];
        reader.read_exact(&mut bytes)?;
        if self.confirmed_through <= self.shipped {
            self.confirmed_through = self.find_commit(reader, &bytes, header, total)?;
        }
        if self.confirmed_through <= self.shipped {
            return Ok(WalTailStep::UpToDate);
        }
        let send = count.min(self.confirmed_through - self.shipped);
        bytes.truncate(send * header.frame_size());
        self.extend(&header_bytes, &bytes, header)
    }

    /// Finds a commit without retaining the whole transaction. The first
    /// chunk remains available for transmission once its commit is proven.
    fn find_commit(
        &self,
        reader: &mut impl Read,
        first: &[u8],
        header: WalHeader,
        total: usize,
    ) -> Result<usize, WalFollowerError> {
        let mut position = self.shipped;
        let mut chain = self.chain;
        let mut scratch = Vec::new();
        let mut bytes = first;
        loop {
            let count = bytes.len() / header.frame_size();
            let (frames, next_chain) = validate_frames(&header, bytes, count, chain);
            if let Some(commit) = frames.iter().rposition(|frame| frame.commit) {
                return Ok(position + commit + 1);
            }
            position += frames.len();
            if frames.len() != count || position == total {
                return Ok(self.shipped);
            }
            chain = next_chain;
            let next = (total - position).min(MAX_WAL_BATCH_BYTES / header.frame_size());
            scratch.resize(next * header.frame_size(), 0);
            reader.read_exact(&mut scratch)?;
            bytes = &scratch;
        }
    }

    /// Validates frames past the shipped prefix and packs the batch.
    fn extend(
        &mut self,
        header_bytes: &[u8],
        bytes: &[u8],
        header: WalHeader,
    ) -> Result<WalTailStep, WalFollowerError> {
        let (frames, _) =
            validate_frames(&header, bytes, bytes.len() / header.frame_size(), self.chain);
        if frames.len() != bytes.len() / header.frame_size() || frames.is_empty() {
            return Err(WalFollowerError::FrameChecksum);
        }
        let chain = frames.last().expect("nonempty validated batch").checksum;
        let from = self.shipped;
        let shipped = from + frames.len();
        let mut committed_through = None;
        for (offset, frame) in frames.iter().enumerate() {
            let index = from + offset + 1;
            if frame.commit {
                self.last_commit = Some((index, frame.checksum));
                committed_through = Some(index);
            }
        }
        let bytes = bytes[..frames.len() * header.frame_size()].to_vec();
        self.shipped = shipped;
        self.chain = chain;
        let batch = Box::new(WalFrames {
            header: header_bytes.to_vec(),
            from_frame: from,
            bytes,
            committed_through,
        });
        self.pending = Some(batch.clone());
        Ok(WalTailStep::Frames(batch))
    }
}

/// The follower side: one location holding the writer's file pair and the
/// frames it has durably applied.
pub struct WalFollower {
    encryption: Option<EncryptedStorage>,
    location: PathBuf,
    header: Option<WalHeader>,
    /// The frames durably appended of the current generation.
    applied: usize,
    /// The chain state after `applied` frames.
    chain: (u32, u32),
    /// The committed prefix (frame count through the last commit frame).
    committed_through: Option<usize>,
    last_commit: Option<(usize, (u32, u32))>,
}

/// The follower's acknowledgement of one applied batch: the durable
/// committed prefix, set only after the batch's `fdatasync` returned.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FollowerAck {
    pub(crate) header: WalHeader,
    pub(crate) applied: usize,
    pub(crate) chain: (u32, u32),
    /// The frame count through the last commit frame the follower holds.
    pub committed_through: Option<usize>,
    /// Whether the follower's `fdatasync` completed — the fact
    /// `RegionalDurable` rides on (D-35: local commit plus this ack).
    pub fdatasynced: bool,
}

impl WalFollower {
    /// Captures only progress that has completed the follower's sync operation.
    #[must_use]
    pub fn checkpoint(&self) -> WalCheckpoint {
        WalCheckpoint {
            header: self.header,
            shipped: self.applied,
            chain: self.chain,
            last_commit: self.last_commit,
        }
    }
    /// Bootstraps from a live engine while its commits and checkpoints pause.
    /// The destination has the same fresh-location requirements as [`Self::bootstrap`].
    ///
    /// # Errors
    ///
    /// Returns a capture-barrier, snapshot-validation, or filesystem failure.
    pub fn bootstrap_engine(
        writer: &terrabase::Engine,
        follower: &Path,
    ) -> Result<(Self, WalCheckpoint), WalFollowerError> {
        writer
            .with_quiescent_wal(|location| Self::bootstrap(location, follower))
            .map_err(|_| WalFollowerError::CaptureUnavailable)?
    }

    /// Reopens an existing replica after process restart without copying its writer.
    /// The caller must exclusively own this location, with no SQLite reader,
    /// writer, promoter, or other follower running against it.
    ///
    /// Rebuilds the checksum chain from complete frames, discards only a
    /// trailing partial frame or valid uncommitted suffix, and synchronizes
    /// the recovered files before returning a resumable writer checkpoint.
    ///
    /// # Errors
    ///
    /// Returns an I/O or integrity error for a missing database, malformed
    /// WAL header, corrupt complete frame, or failed durability operation.
    /// Validation failures leave the existing file contents untouched.
    pub fn open_existing(location: &Path) -> Result<(Self, WalCheckpoint), WalFollowerError> {
        let mut wal = open_wal(location)?;
        let checkpoint = wal
            .as_mut()
            .map_or_else(|| Ok(WalCheckpoint::default()), |file| scan_wal(file, true))?;
        verify_snapshot(location)?;
        if let Some(header) = checkpoint.header {
            let file = open_physical_file(&location.join(wal_file_name()))?;
            let prefix_bytes = header.frame_range(0, checkpoint.shipped).end;
            file.set_len(u64::try_from(prefix_bytes).map_err(|_| WalFollowerError::FrameIo)?)?;
            file.sync_all()?;
        }
        open_physical_file(&location.join(DATABASE_FILE))?.sync_all()?;
        match File::open(location.join(OWNER_FILE)) {
            Ok(file) => file.sync_all()?,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
        sync_directory(location)?;
        let state = Self {
            encryption: EncryptedStorage::for_related_location(location, location)?,
            location: location.to_path_buf(),
            header: checkpoint.header,
            applied: checkpoint.shipped,
            chain: checkpoint.chain,
            committed_through: checkpoint.last_commit.map(|(frame, _)| frame),
            last_commit: checkpoint.last_commit,
        };
        Ok((state, checkpoint))
    }

    /// Bootstraps a follower from the writer's at-rest file pair (the
    /// writer quiescent — see the module's bootstrap contract): copies the
    /// ownership record (`owner.tbv1`, when present), `terrabase.sqlite3`,
    /// and the valid WAL prefix through the last commit frame.
    ///
    /// Returns the follower alongside the [`WalCheckpoint`] the tailer
    /// passes to [`WalTail::resume`]: the snapshot's committed prefix is
    /// already durable on the follower, so the tail continues past it.
    /// The destination must be absent or empty. Re-bootstrap uses a fresh
    /// location; the caller retires the old copy after switching to the new one.
    /// Files are staged privately and published together by directory rename.
    /// The destination's parent must already exist. SQLite integrity is verified
    /// over the staged database and committed WAL before publication.
    ///
    /// # Errors
    ///
    /// Returns [`WalFollowerError`] when the pair cannot be read, the
    /// WAL header is malformed, the destination is occupied or a symlink,
    /// or the writes cannot be made durable. A failure before publication
    /// leaves the destination untouched. If the final parent sync fails,
    /// the installed snapshot remains available for recovery.
    pub fn bootstrap(
        writer: &Path,
        follower: &Path,
    ) -> Result<(Self, WalCheckpoint), WalFollowerError> {
        require_empty_destination(follower)?;
        let parent = follower
            .parent()
            .filter(|path| !path.as_os_str().is_empty())
            .unwrap_or_else(|| Path::new("."));
        // The placement owner creates the parent. Do not create an unfenced
        // directory tree as a side effect of installing a replica snapshot.
        let staging = StagedFollower::new(parent)?;
        let (mut state, checkpoint) = Self::bootstrap_into(writer, &staging.path)?;
        verify_snapshot(&staging.path)?;
        require_empty_destination(follower)?;
        std::fs::rename(&staging.path, follower)?;
        sync_directory(parent)?;
        state.encryption = EncryptedStorage::for_related_location(writer, follower)?;
        state.location = follower.to_path_buf();
        Ok((state, checkpoint))
    }

    fn bootstrap_into(
        writer: &Path,
        follower: &Path,
    ) -> Result<(Self, WalCheckpoint), WalFollowerError> {
        let mut wal = open_wal(writer)?;
        let checkpoint = wal
            .as_mut()
            .map_or_else(|| Ok(WalCheckpoint::default()), |file| scan_wal(file, false))?;
        let state = Self {
            encryption: EncryptedStorage::for_related_location(writer, follower)?,
            location: follower.to_path_buf(),
            header: checkpoint.header,
            applied: checkpoint.shipped,
            chain: checkpoint.chain,
            committed_through: checkpoint.last_commit.map(|(frame, _)| frame),
            last_commit: checkpoint.last_commit,
        };
        copy_location_file(writer, follower, DATABASE_FILE)?;
        copy_location_file_optional(writer, follower, OWNER_FILE)?;
        if let Some(header) = checkpoint.header {
            let mut source = wal.expect("a parsed header has an open file");
            source.rewind()?;
            let length = header.frame_range(0, checkpoint.shipped).end as u64;
            let mut target = create_physical_file(&follower.join(wal_file_name()))?;
            target.set_len(0)?;
            let copied = std::io::copy(&mut source.take(length), &mut target)?;
            if copied != length {
                return Err(WalFollowerError::FrameIo);
            }
            target.sync_all()?;
        }
        sync_directory(follower)?;
        Ok((state, checkpoint))
    }

    /// The committed prefix the follower holds durably.
    #[must_use]
    pub fn committed_through(&self) -> Option<usize> {
        self.committed_through
    }

    /// Folds the follower's WAL into its database file and resets for the
    /// next generation: runs the writer's own checkpoint (a truncating one,
    /// so the `-wal` restarts empty) over the follower's location, then
    /// forgets the old generation's header, prefix, and chain. The next
    /// [`WalFollower::apply`] adopts the new generation from frame zero.
    ///
    /// The caller owns the barrier: the tail must have shipped the whole
    /// old generation and the follower must have acked it (the composition
    /// drives commit wave → ship → ack → fold → next wave while the writer
    /// is quiescent). Folding a partial generation would diverge the
    /// follower's file from the writer's folded state, and the new
    /// generation's frames are deltas against that folded state.
    ///
    /// # Errors
    ///
    /// Refuses a disk prefix that differs from the acknowledged commit, or
    /// a checksum or database-integrity failure, before SQLite can fold it.
    /// Returns [`WalFollowerError::CheckpointIncomplete`] when the
    /// checkpoint could not fold every frame (a reader holds the
    /// follower's file — no reader exists while the follower owns the
    /// location, so the composition retries after quiescing promoters),
    /// or [`WalFollowerError::Sqlite`] when the follower's database
    /// cannot be opened.
    pub fn fold_generation(&mut self) -> Result<(), WalFollowerError> {
        self.verify_fold_prefix()?;
        let connection = open_physical_sqlite(
            &self.location.join(DATABASE_FILE),
            rusqlite::OpenFlags::SQLITE_OPEN_READ_WRITE,
        )?;
        let (busy, log_frames, checkpointed_frames): (i64, i64, i64) =
            connection.query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |row| {
                Ok((row.get(0)?, row.get(1)?, row.get(2)?))
            })?;
        if busy != 0 || log_frames != checkpointed_frames {
            return Err(WalFollowerError::CheckpointIncomplete);
        }
        drop(connection);
        self.header = None;
        self.applied = 0;
        self.chain = (0, 0);
        self.committed_through = None;
        self.last_commit = None;
        Ok(())
    }

    /// SQLite recovery may ignore a damaged suffix. Never let checkpointing
    /// silently change the durable prefix this follower has acknowledged.
    fn verify_fold_prefix(&self) -> Result<(), WalFollowerError> {
        let mut wal = open_wal(&self.location)?;
        let length = wal.as_ref().map_or(Ok(0), |file| file.len())?;
        let Some(header) = self.header else {
            if length != 0 {
                return Err(WalFollowerError::CheckpointIncomplete);
            }
            return verify_snapshot(&self.location);
        };
        let Some(file) = wal.as_mut() else {
            return Err(WalFollowerError::TruncatedHeader);
        };
        let checkpoint = scan_wal(file, true)?;
        if checkpoint.header != Some(header) {
            return Err(WalFollowerError::GenerationMismatch);
        }
        if length != header.frame_range(0, self.applied).end as u64
            || self.committed_through.unwrap_or(0) != self.applied
        {
            return Err(WalFollowerError::CheckpointIncomplete);
        }
        if checkpoint.shipped != self.applied || checkpoint.chain != self.chain {
            return Err(WalFollowerError::FrameChecksum);
        }
        verify_snapshot(&self.location)
    }

    /// Applies one shipped batch: adopts its generation when fresh,
    /// re-validates every frame's salt and cumulative checksum against
    /// the follower's own chain, appends the valid bytes verbatim at the
    /// exact frame offset, `fdatasync`s, and acks. A batch fully inside the
    /// already-applied prefix (an acknowledgement lost on the wire, the
    /// tailer's at-least-once re-ship) re-acks the durable state without
    /// touching the file. Frames of another generation, a torn batch, or a
    /// batch that otherwise does not continue the follower's prefix fail
    /// closed — never append garbage.
    ///
    /// # Errors
    ///
    /// Returns [`WalFollowerError`] on any validation failure or I/O
    /// error. Validation failures leave the file untouched. An I/O failure may
    /// leave a partial write, but never advances the acknowledged prefix.
    pub fn apply(&mut self, batch: &WalFrames) -> Result<FollowerAck, WalFollowerError> {
        if batch.bytes.len() > MAX_WAL_BATCH_BYTES {
            return Err(WalFollowerError::BatchTooLarge);
        }
        if batch.header.len() != WAL_HEADER_BYTES {
            return Err(WalFollowerError::HeaderChecksum);
        }
        let header = WalHeader::parse(&batch.header)?;
        if !batch.bytes.len().is_multiple_of(header.frame_size()) {
            return Err(WalFollowerError::FrameChecksum);
        }
        let count = batch.bytes.len() / header.frame_size();
        let batch_end = batch.from_frame.checked_add(count).ok_or(WalFollowerError::FrameIo)?;
        let offset = batch
            .from_frame
            .checked_mul(header.frame_size())
            .and_then(|value| value.checked_add(WAL_HEADER_BYTES))
            .and_then(|value| u64::try_from(value).ok())
            .ok_or(WalFollowerError::FrameIo)?;
        if let Some(tracked) = self.header {
            if tracked != header {
                return Err(WalFollowerError::GenerationMismatch);
            }
            if batch.from_frame != self.applied {
                if batch_end <= self.applied {
                    return self.reack(batch, offset);
                }
                return Err(WalFollowerError::BatchNotContiguous);
            }
        } else if batch.from_frame != 0 {
            return Err(WalFollowerError::BatchNotContiguous);
        }
        let initial_chain = if self.header.is_some() { self.chain } else { header.checksum };
        let (frames, chain) = validate_frames(&header, &batch.bytes, count, initial_chain);
        if frames.len() != count {
            return Err(WalFollowerError::FrameChecksum);
        }
        let batch_commit =
            frames.iter().rposition(|frame| frame.commit).map(|index| batch.from_frame + index + 1);
        if batch.committed_through != batch_commit {
            return Err(WalFollowerError::FrameChecksum);
        }
        // Validate the entire batch before creating or changing the WAL. State
        // advances only after every durability operation succeeds, so an I/O
        // failure can be retried at the same offset.
        let fresh = self.header.is_none();
        let mut wal_file = if fresh {
            let file = create_physical_file(&self.location.join(wal_file_name()))?;
            file.set_len(0)?;
            file
        } else {
            open_physical_file(&self.location.join(wal_file_name()))?
        };
        if fresh {
            wal_file.write_all(&batch.header)?;
        }
        wal_file.seek(std::io::SeekFrom::Start(offset))?;
        wal_file.write_all(&batch.bytes)?;
        wal_file.sync_all()?;
        if fresh {
            sync_directory(&self.location)?;
        }
        self.header = Some(header);
        self.applied = batch_end;
        self.chain = chain;
        self.committed_through = batch_commit.or(self.committed_through);
        if let Some(frame) = batch_commit {
            self.last_commit = Some((frame, frames[frame - batch.from_frame - 1].checksum));
        }
        Ok(FollowerAck {
            header,
            applied: self.applied,
            chain: self.chain,
            committed_through: self.committed_through,
            fdatasynced: true,
        })
    }

    /// A retransmission must contain the exact bytes already made durable.
    fn reack(&self, batch: &WalFrames, offset: u64) -> Result<FollowerAck, WalFollowerError> {
        let mut file = open_physical_file(&self.location.join(wal_file_name()))?;
        file.seek(std::io::SeekFrom::Start(offset))?;
        let mut stored = vec![0; batch.bytes.len()];
        file.read_exact(&mut stored)?;
        if stored != batch.bytes {
            return Err(WalFollowerError::FrameChecksum);
        }
        let header = self.header.ok_or(WalFollowerError::GenerationMismatch)?;
        let batch_commit = stored
            .chunks_exact(header.frame_size())
            .rposition(|frame| be_u32(&frame[4..8]) != 0)
            .map(|index| batch.from_frame + index + 1);
        if batch.committed_through != batch_commit {
            return Err(WalFollowerError::FrameChecksum);
        }
        Ok(FollowerAck {
            header,
            applied: self.applied,
            chain: self.chain,
            committed_through: self.committed_through,
            fdatasynced: true,
        })
    }
}

/// Owns an unpublished snapshot and removes it after a failed installation.
struct StagedFollower {
    path: PathBuf,
}

impl StagedFollower {
    fn new(parent: &Path) -> Result<Self, WalFollowerError> {
        let path = parent
            .join(format!(".terrabase-follower-{}", terrabase::identity::DatabaseUlid::generate()));
        let mut builder = std::fs::DirBuilder::new();
        #[cfg(unix)]
        {
            use std::os::unix::fs::DirBuilderExt as _;
            builder.mode(0o700);
        }
        builder.create(&path)?;
        Ok(Self { path })
    }
}

impl Drop for StagedFollower {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

fn require_empty_destination(path: &Path) -> Result<(), WalFollowerError> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata.is_dir() => {
            if std::fs::read_dir(path)?.next().transpose()?.is_none() {
                return Ok(());
            }
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.into()),
        Ok(_) => {}
    }
    Err(std::io::Error::new(
        std::io::ErrorKind::AlreadyExists,
        "a follower snapshot requires an empty, non-symlink destination",
    )
    .into())
}

fn verify_snapshot(location: &Path) -> Result<(), WalFollowerError> {
    let connection = open_physical_sqlite(
        &location.join(DATABASE_FILE),
        rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY | rusqlite::OpenFlags::SQLITE_OPEN_NO_MUTEX,
    )?;
    let result: String = connection.query_row("PRAGMA integrity_check", [], |row| row.get(0))?;
    if result != "ok" {
        return Err(WalFollowerError::SnapshotIntegrity);
    }
    Ok(())
}

/// The follower's WAL file name (`SQLite`'s own suffix rule).
fn wal_file_name() -> String {
    format!("{DATABASE_FILE}-wal")
}

/// Copies one location file from the writer to the follower and makes the
/// copy durable by content before returning. Permissions travel with the
/// bytes: the ownership record is refused unless it stays `0600`.
fn copy_location_file(writer: &Path, follower: &Path, name: &str) -> Result<(), WalFollowerError> {
    let permissions = std::fs::metadata(writer.join(name))?.permissions();
    let mut source = open_physical_file(&writer.join(name))?;
    let mut target = create_physical_file(&follower.join(name))?;
    target.set_len(0)?;
    std::io::copy(&mut source, &mut target)?;
    std::fs::set_permissions(follower.join(name), permissions)?;
    target.sync_all()?;
    Ok(())
}

/// Copies one location file when the writer holds it, skipping an absent
/// one (the ownership record need not exist yet on every location).
fn copy_location_file_optional(
    writer: &Path,
    follower: &Path,
    name: &str,
) -> Result<(), WalFollowerError> {
    match std::fs::metadata(writer.join(name)) {
        Ok(_) => copy_location_file(writer, follower, name),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

fn open_wal(location: &Path) -> Result<Option<PhysicalFile>, WalFollowerError> {
    match open_physical_file(&location.join(wal_file_name())) {
        Ok(file) => Ok(Some(file)),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error.into()),
    }
}

/// Validates a file using at most one transfer buffer plus bounded checksum
/// metadata. Strict recovery refuses any corrupt complete frame; snapshot
/// capture may stop at stale frames beyond the writer's valid WAL prefix.
fn scan_wal(
    reader: &mut (impl Read + Seek),
    strict: bool,
) -> Result<WalCheckpoint, WalFollowerError> {
    reader.rewind()?;
    let mut header_bytes = Vec::with_capacity(WAL_HEADER_BYTES);
    reader.take(WAL_HEADER_BYTES as u64).read_to_end(&mut header_bytes)?;
    if header_bytes.is_empty() || (!strict && header_bytes.len() < WAL_HEADER_BYTES) {
        return Ok(WalCheckpoint::default());
    }
    let header = WalHeader::parse(&header_bytes)?;
    let length = usize::try_from(reader.seek(std::io::SeekFrom::End(0))?)
        .map_err(|_| WalFollowerError::FrameIo)?;
    let total = header.complete_frames(length);
    reader.seek(std::io::SeekFrom::Start(WAL_HEADER_BYTES as u64))?;
    let mut checkpoint =
        WalCheckpoint { header: Some(header), chain: header.checksum, ..WalCheckpoint::default() };
    let mut chain = header.checksum;
    let mut position = 0;
    let mut buffer = Vec::new();
    while position < total {
        let count = (total - position).min(MAX_WAL_BATCH_BYTES / header.frame_size());
        buffer.resize(count * header.frame_size(), 0);
        reader.read_exact(&mut buffer)?;
        let (frames, next_chain) = validate_frames(&header, &buffer, count, chain);
        for (offset, frame) in frames.iter().enumerate() {
            if frame.commit {
                checkpoint.shipped = position + offset + 1;
                checkpoint.chain = frame.checksum;
                checkpoint.last_commit = Some((checkpoint.shipped, frame.checksum));
            }
        }
        if frames.len() != count {
            if strict {
                return Err(WalFollowerError::FrameChecksum);
            }
            break;
        }
        position += count;
        chain = next_chain;
    }
    Ok(checkpoint)
}

/// Reads the writer location's WAL, tolerating its absence (an empty
/// read is a checkpointed-and-closed writer).
#[cfg(test)]
fn read_wal(location: &Path) -> Result<Vec<u8>, WalFollowerError> {
    let path = location.join(wal_file_name());
    match File::open(&path) {
        Ok(mut file) => {
            let mut bytes = Vec::new();
            file.read_to_end(&mut bytes)?;
            Ok(bytes)
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(Vec::new()),
        Err(error) => Err(error.into()),
    }
}

/// fsyncs a directory so freshly created files are durable by name.
fn sync_directory(path: &Path) -> Result<(), WalFollowerError> {
    File::open(path)?.sync_all()?;
    Ok(())
}

/// The follower protocol's typed failures.
#[derive(Debug)]
pub enum WalFollowerError {
    /// The WAL header's magic is not `SQLite`'s.
    BadMagic,
    /// The declared page size is not a power of two in `SQLite`'s range.
    BadPageSize,
    /// The WAL header's own checksum failed.
    HeaderChecksum,
    /// Fewer than 32 bytes of WAL header.
    TruncatedHeader,
    /// A frame's salt or cumulative checksum failed validation.
    FrameChecksum,
    /// A batch belongs to another WAL generation than the follower's.
    GenerationMismatch,
    /// A batch does not continue the follower's applied prefix.
    BatchNotContiguous,
    /// The frame payload exceeds the bounded transfer size.
    BatchTooLarge,
    /// The underlying file I/O failed.
    Io(std::io::Error),
    /// The follower's database could not be opened or checkpointed.
    Sqlite(rusqlite::Error),
    /// The generation fold could not checkpoint every WAL frame.
    CheckpointIncomplete,
    /// The staged database and WAL failed SQLite's integrity check.
    SnapshotIntegrity,
    /// A receipt does not prove the pending batch is durable.
    AcknowledgmentMismatch,
    /// The engine could not acquire its physical capture barrier.
    CaptureUnavailable,
    /// The engine capture barrier belongs to a different database location.
    CaptureLocationMismatch,
    /// A frame-offset write failed to convert.
    FrameIo,
}

impl std::fmt::Display for WalFollowerError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::BadMagic => formatter.write_str("the WAL header magic is not SQLite's"),
            Self::BadPageSize => formatter.write_str("the WAL page size is invalid"),
            Self::HeaderChecksum => formatter.write_str("the WAL header checksum failed"),
            Self::TruncatedHeader => formatter.write_str("the WAL header is truncated"),
            Self::FrameChecksum => {
                formatter.write_str("a WAL frame failed salt or checksum validation")
            }
            Self::GenerationMismatch => {
                formatter.write_str("the batch belongs to another WAL generation")
            }
            Self::BatchTooLarge => formatter.write_str("the WAL batch exceeds the transfer limit"),
            Self::BatchNotContiguous => {
                formatter.write_str("the batch does not continue the follower's prefix")
            }
            Self::Io(error) => write!(formatter, "the follower's file I/O failed: {error}"),
            Self::Sqlite(error) => {
                write!(formatter, "the follower's checkpoint failed: {error}")
            }
            Self::CheckpointIncomplete => {
                formatter.write_str("the generation fold left WAL frames uncheckpointed")
            }
            Self::SnapshotIntegrity => formatter.write_str("the follower snapshot is corrupt"),
            Self::AcknowledgmentMismatch => {
                formatter.write_str("the WAL acknowledgment does not match the pending batch")
            }
            Self::FrameIo => formatter.write_str("a frame write failed"),
            Self::CaptureUnavailable => {
                formatter.write_str("the WAL capture barrier is unavailable")
            }
            Self::CaptureLocationMismatch => {
                formatter.write_str("the capture engine does not own the WAL location")
            }
        }
    }
}

impl std::error::Error for WalFollowerError {}

impl From<std::io::Error> for WalFollowerError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<rusqlite::Error> for WalFollowerError {
    fn from(error: rusqlite::Error) -> Self {
        Self::Sqlite(error)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct CountedReader {
        bytes: std::io::Cursor<Vec<u8>>,
        read: usize,
        max_request: usize,
    }

    impl Read for CountedReader {
        fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
            self.max_request = self.max_request.max(buffer.len());
            let count = self.bytes.read(buffer)?;
            self.read += count;
            Ok(count)
        }
    }

    impl Seek for CountedReader {
        fn seek(&mut self, position: std::io::SeekFrom) -> std::io::Result<u64> {
            self.bytes.seek(position)
        }
    }

    #[test]
    fn idle_poll_reads_only_header_and_catchup_reads_only_new_frames() {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-wal-seek-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("clock")
                .as_nanos()
        ));
        std::fs::create_dir(&directory).expect("directory");
        let connection =
            rusqlite::Connection::open(directory.join(DATABASE_FILE)).expect("database");
        connection
            .execute_batch(
                "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;
             CREATE TABLE data (value BLOB); INSERT INTO data VALUES (zeroblob(1048576));",
            )
            .expect("large committed prefix");
        let mut reader = CountedReader {
            bytes: std::io::Cursor::new(read_wal(&directory).expect("WAL")),
            read: 0,
            max_request: 0,
        };
        let scanned = scan_wal(&mut reader, true).expect("bounded recovery scan");
        assert!(reader.read > MAX_WAL_BATCH_BYTES, "fixture spans several scan chunks");
        assert!(reader.max_request <= MAX_WAL_BATCH_BYTES, "scan reads are bounded");
        let mut tail = WalTail::new(&directory);
        while let WalTailStep::Frames(_) = tail.poll_reader(&mut reader).expect("initial capture") {
            tail.acknowledge(FollowerAck {
                header: tail.header.expect("header"),
                applied: tail.shipped,
                chain: tail.chain,
                committed_through: tail.last_commit.map(|(frame, _)| frame),
                fdatasynced: true,
            })
            .expect("acknowledge prefix");
        }
        assert_eq!(tail.checkpoint().shipped, scanned.shipped);
        assert_eq!(tail.checkpoint().chain, scanned.chain);
        reader.read = 0;
        assert_eq!(tail.poll_reader(&mut reader).expect("idle"), WalTailStep::UpToDate);
        assert_eq!(reader.read, WAL_HEADER_BYTES);
        let from = tail.shipped;
        connection.execute("INSERT INTO data VALUES (zeroblob(32))", []).expect("new commit");
        reader.bytes = std::io::Cursor::new(read_wal(&directory).expect("extended WAL"));
        reader.read = 0;
        let WalTailStep::Frames(batch) =
            tail.poll_reader(&mut reader).expect("incremental capture")
        else {
            panic!("new committed batch");
        };
        assert_eq!(batch.from_frame, from);
        assert_eq!(reader.read, WAL_HEADER_BYTES + batch.bytes.len());
        assert!(batch.bytes.len() < 65536, "catchup does not retransmit the megabyte prefix");
        drop(connection);
        std::fs::remove_dir_all(directory).expect("cleanup");
    }
}
