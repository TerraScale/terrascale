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
//! [`WalFollower::bootstrap`] copies the writer's location marker
//! (`format`), ownership record (`owner.tbv1`, when present),
//! `terrabase.sqlite3`, and the valid prefix of its `-wal` — an at-rest
//! set is consistent (main at or past the last generation boundary, WAL
//! holding the current generation from frame zero). It returns the
//! follower alongside the [`WalCheckpoint`] the tailer passes to
//! [`WalTail::resume`], so the tail continues past the snapshot instead
//! of re-shipping it. The caller must invoke it with the writer quiescent
//! (no commit wave or checkpoint in flight): the S-09 member composition
//! takes that barrier at the member-composition boundary, and the
//! orchestration that promotes or re-syncs a follower owns it in
//! production.
//!
//! ## What is deliberately M1
//!
//! - The ship is an in-process handoff ([`WalFrames`]); the QUIC socket
//!   adapter is the S-09 member-composition step, alongside the
//!   `[replication] peers` wiring the cross-region lanes already parse.
//! - The fold is one `wal_checkpoint(TRUNCATE)` per adopted generation;
//!   there is no pipelining across generations yet.
//! - The tail reads the whole WAL file per poll; it is O(WAL) per tick
//!   with the validation walk — fine at dev WAL sizes, replaced by a
//!   seeked incremental read if it ever measures hot.
//!   ponytail: whole-file poll; seeked reads when the WAL grows large.
//!
//! Promotion is opening the follower's location under a new replica
//! epoch: `EngineHost::open` (or `MemberHost::open`) on the follower
//! directory — `SQLite`'s recovery validates every frame the follower
//! appended, and the engine serves the file. `tests/wal_follower.rs`
//! pins exactly that.

use std::collections::BTreeMap;
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, Write};
use std::path::{Path, PathBuf};

/// The engine's `SQLite` database file name inside a durable location.
const DATABASE_FILE: &str = "terrabase.sqlite3";

/// The location's format marker: [`bootstrap`](WalFollower::bootstrap) copies
/// it so the follower's directory opens as the same database instead of
/// failing the marker check as a loss.
const FORMAT_FILE: &str = "format";

/// The location's ownership record: copied when present for the same reason.
const OWNER_FILE: &str = "owner.tbv1";

/// The WAL file's fixed header size, in bytes.
pub const WAL_HEADER_BYTES: usize = 32;

/// One WAL frame's fixed header size, in bytes.
pub const FRAME_HEADER_BYTES: usize = 24;

/// The `SQLite` WAL magics: the low bit selects the byte order of checksum
/// words (`...82` little-endian, `...83` big-endian); the header's other
/// fields are always big-endian.
const WAL_MAGIC_LE: u32 = 0x377F_0682;
const WAL_MAGIC_BE: u32 = 0x377F_0683;

/// A validated WAL header: the fields the tail and follower track.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
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
    header: Option<WalHeader>,
    /// The frames already handed out of the current generation.
    shipped: usize,
    /// The chain state after `shipped` frames.
    chain: (u32, u32),
    /// The chain state after each shipped commit frame (rollback points).
    commit_chains: BTreeMap<usize, (u32, u32)>,
}

/// One poll of the writer's WAL.
#[derive(Debug, Eq, PartialEq)]
pub enum WalTailStep {
    /// No newly validated frame since the last poll.
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
#[derive(Debug, Eq, PartialEq)]
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
#[derive(Clone, Debug)]
pub struct WalCheckpoint {
    header: Option<WalHeader>,
    shipped: usize,
    chain: (u32, u32),
    commit_chains: BTreeMap<usize, (u32, u32)>,
}

impl WalTail {
    /// Creates the tail over one engine location.
    #[must_use]
    pub fn new(location: impl Into<PathBuf>) -> Self {
        Self {
            location: location.into(),
            header: None,
            shipped: 0,
            chain: (0, 0),
            commit_chains: BTreeMap::new(),
        }
    }

    /// Snapshots this tail's position: the [`WalCheckpoint`] a restarted
    /// tailer passes to [`WalTail::resume`] to continue past what the
    /// follower already holds, without re-shipping the snapshot prefix.
    #[must_use]
    pub fn checkpoint(&self) -> WalCheckpoint {
        WalCheckpoint {
            header: self.header,
            shipped: self.shipped,
            chain: self.chain,
            commit_chains: self.commit_chains.clone(),
        }
    }

    /// Resumes a tail past a bootstrap's snapshot (or a stored checkpoint)
    /// over the same writer location: the next poll validates and ships
    /// only frames past the checkpoint's shipped prefix.
    #[must_use]
    pub fn resume(location: impl Into<PathBuf>, checkpoint: WalCheckpoint) -> Self {
        Self {
            location: location.into(),
            header: checkpoint.header,
            shipped: checkpoint.shipped,
            chain: checkpoint.chain,
            commit_chains: checkpoint.commit_chains,
        }
    }

    /// Polls the writer's WAL: validates every newly visible frame and
    /// hands back the ones past the shipped prefix. A torn or corrupt
    /// tail stops the batch at the last valid frame; a rewritten
    /// uncommitted tail (the writer crashed and restarted its commit)
    /// rolls the shipped prefix back to the last commit frame.
    ///
    /// # Errors
    ///
    /// Returns [`WalFollowerError`] when the WAL cannot be read or its
    /// header is malformed.
    pub fn poll(&mut self) -> Result<WalTailStep, WalFollowerError> {
        let wal = read_wal(&self.location)?;
        let Some(tracked) = self.header else {
            if wal.len() < WAL_HEADER_BYTES {
                return Ok(WalTailStep::UpToDate);
            }
            let header = match WalHeader::parse(&wal[..WAL_HEADER_BYTES]) {
                Ok(header) => header,
                // A torn header write is not a generation we can tail;
                // the next poll sees the finished one.
                Err(WalFollowerError::TruncatedHeader | WalFollowerError::HeaderChecksum) => {
                    return Ok(WalTailStep::UpToDate);
                }
                Err(error) => return Err(error),
            };
            self.header = Some(header);
            self.chain = header.checksum;
            return Ok(self.extend(&wal, header));
        };
        if wal.len() < WAL_HEADER_BYTES {
            // The WAL was truncated away (a clean close checkpoints and
            // deletes it): the next write opens a new generation.
            return Ok(WalTailStep::GenerationChanged);
        }
        let header = WalHeader::parse(&wal[..WAL_HEADER_BYTES])?;
        if header.salts != tracked.salts {
            return Ok(WalTailStep::GenerationChanged);
        }
        let total = header.complete_frames(wal.len());
        if total < self.shipped {
            // Same salts but fewer frames than we shipped: the writer
            // rewound the WAL (a crash-recovered restart keeps the salts
            // only if the file was not reset; either way our prefix is
            // gone). Re-bootstrap.
            return Ok(WalTailStep::GenerationChanged);
        }
        // The uncommitted tail we already shipped may have been rewritten
        // by a recovered writer: re-validate it from the last commit
        // boundary and roll back if the chain no longer holds.
        if self.shipped > 0 {
            let boundary = self.commit_chains.last_key_value().map_or(0, |(frame, _)| *frame);
            let state =
                self.commit_chains.last_key_value().map_or(header.checksum, |(_, chain)| *chain);
            let (still_valid, chain) = validate_frames(
                &header,
                &wal[header.frame_range(boundary, self.shipped)],
                self.shipped - boundary,
                state,
            );
            let valid_count = boundary + still_valid.len();
            if valid_count < self.shipped {
                self.shipped = valid_count;
                self.chain = chain;
                self.commit_chains.retain(|frame, _| *frame <= self.shipped);
            }
        }
        Ok(self.extend(&wal, header))
    }

    /// Validates frames past the shipped prefix and packs the batch.
    fn extend(&mut self, wal: &[u8], header: WalHeader) -> WalTailStep {
        let total = header.complete_frames(wal.len());
        if total <= self.shipped {
            return WalTailStep::UpToDate;
        }
        let (frames, chain) = validate_frames(
            &header,
            &wal[header.frame_range(self.shipped, total)],
            total - self.shipped,
            self.chain,
        );
        if frames.is_empty() {
            return WalTailStep::UpToDate;
        }
        let from = self.shipped;
        let shipped = from + frames.len();
        let mut committed_through = None;
        for (offset, frame) in frames.iter().enumerate() {
            let index = from + offset + 1;
            if frame.commit {
                self.commit_chains.insert(index, frame.checksum);
                committed_through = Some(index);
            }
        }
        let bytes = wal[header.frame_range(from, shipped)].to_vec();
        self.shipped = shipped;
        self.chain = chain;
        WalTailStep::Frames(Box::new(WalFrames {
            header: wal[..WAL_HEADER_BYTES].to_vec(),
            from_frame: from,
            bytes,
            committed_through,
        }))
    }
}

/// The follower side: one location holding the writer's file pair and the
/// frames it has durably applied.
pub struct WalFollower {
    location: PathBuf,
    header: Option<WalHeader>,
    /// The frames durably appended of the current generation.
    applied: usize,
    /// The chain state after `applied` frames.
    chain: (u32, u32),
    /// The committed prefix (frame count through the last commit frame).
    committed_through: Option<usize>,
}

/// The follower's acknowledgement of one applied batch: the durable
/// committed prefix, set only after the batch's `fdatasync` returned.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FollowerAck {
    /// The frame count through the last commit frame the follower holds.
    pub committed_through: Option<usize>,
    /// Whether the follower's `fdatasync` completed — the fact
    /// `RegionalDurable` rides on (D-35: local commit plus this ack).
    pub fdatasynced: bool,
}

impl WalFollower {
    /// Bootstraps a follower from the writer's at-rest file pair (the
    /// writer quiescent — see the module's bootstrap contract): copies the
    /// location marker (`format`), the ownership record (`owner.tbv1`,
    /// when present), `terrabase.sqlite3`, and the valid WAL prefix through
    /// the last commit frame. Without the marker the follower's directory
    /// would fail the open as a loss instead of promoting as the same
    /// database.
    ///
    /// Returns the follower alongside the [`WalCheckpoint`] the tailer
    /// passes to [`WalTail::resume`]: the snapshot's committed prefix is
    /// already durable on the follower, so the tail continues past it.
    ///
    /// # Errors
    ///
    /// Returns [`WalFollowerError`] when the pair cannot be read, the
    /// WAL header is malformed, or the writes cannot be made durable.
    pub fn bootstrap(
        writer: &Path,
        follower: &Path,
    ) -> Result<(Self, WalCheckpoint), WalFollowerError> {
        std::fs::create_dir_all(follower)?;
        let wal = read_wal(writer)?;
        let mut state = Self {
            location: follower.to_path_buf(),
            header: None,
            applied: 0,
            chain: (0, 0),
            committed_through: None,
        };
        let mut checkpoint = WalCheckpoint {
            header: None,
            shipped: 0,
            chain: (0, 0),
            commit_chains: BTreeMap::new(),
        };
        let mut wal_prefix: Vec<u8> = Vec::new();
        if wal.len() >= WAL_HEADER_BYTES {
            let header = WalHeader::parse(&wal[..WAL_HEADER_BYTES])?;
            let total = header.complete_frames(wal.len());
            let (frames, _) = validate_frames(
                &header,
                &wal[header.frame_range(0, total)],
                total,
                header.checksum,
            );
            // Only the prefix through the last commit frame is durable
            // state; a trailing uncommitted tail stays behind.
            let committed =
                frames.iter().rposition(|frame| frame.commit).map_or(0, |position| position + 1);
            let mut chain = header.checksum;
            let mut commit_chains = BTreeMap::new();
            for (offset, frame) in frames[..committed].iter().enumerate() {
                chain = frame.checksum;
                if frame.commit {
                    commit_chains.insert(offset + 1, frame.checksum);
                }
            }
            state.header = Some(header);
            state.chain = chain;
            state.applied = committed;
            state.committed_through = (committed > 0).then_some(committed);
            checkpoint =
                WalCheckpoint { header: Some(header), shipped: committed, chain, commit_chains };
            // The snapshot is the header plus the committed frames: the
            // pair the follower's recovery replays on promotion.
            wal_prefix = wal[..header.frame_range(0, committed).end].to_vec();
        }
        copy_location_file(writer, follower, DATABASE_FILE)?;
        copy_location_file(writer, follower, FORMAT_FILE)?;
        copy_location_file_optional(writer, follower, OWNER_FILE)?;
        if state.header.is_some() {
            let mut wal_file = File::create(follower.join(wal_file_name()))?;
            wal_file.write_all(&wal_prefix)?;
            wal_file.sync_all()?;
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
    /// Returns [`WalFollowerError::CheckpointIncomplete`] when the
    /// checkpoint could not fold every frame (a reader holds the
    /// follower's file — no reader exists while the follower owns the
    /// location, so the composition retries after quiescing promoters),
    /// or [`WalFollowerError::Sqlite`] when the follower's database
    /// cannot be opened.
    pub fn fold_generation(&mut self) -> Result<(), WalFollowerError> {
        let connection = rusqlite::Connection::open(self.location.join(DATABASE_FILE))?;
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
        Ok(())
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
    /// error; the follower's file is untouched on failure.
    pub fn apply(&mut self, batch: &WalFrames) -> Result<FollowerAck, WalFollowerError> {
        let header = WalHeader::parse(&batch.header)?;
        if !batch.bytes.len().is_multiple_of(header.frame_size()) {
            return Err(WalFollowerError::FrameChecksum);
        }
        let batch_end = batch.from_frame + batch.bytes.len() / header.frame_size();
        match self.header {
            None => {
                if batch.from_frame != 0 {
                    return Err(WalFollowerError::BatchNotContiguous);
                }
                let mut wal_file = OpenOptions::new()
                    .write(true)
                    .create(true)
                    .truncate(true)
                    .open(self.location.join(wal_file_name()))?;
                wal_file.write_all(&batch.header)?;
                wal_file.sync_all()?;
                sync_directory(&self.location)?;
                self.header = Some(header);
                self.chain = header.checksum;
                self.applied = 0;
            }
            Some(tracked) => {
                if tracked.salts != header.salts {
                    return Err(WalFollowerError::GenerationMismatch);
                }
                if batch.from_frame != self.applied {
                    // The whole batch sits inside the applied prefix: the
                    // tailer re-shipped what the follower already holds
                    // (a lost acknowledgement). Re-ack the durable state;
                    // the file is untouched. Committed frames are never
                    // rewritten inside a generation, so the ack stays
                    // truthful whatever the re-shipped bytes hold. A
                    // partial overlap fails closed below.
                    if batch_end <= self.applied {
                        return Ok(FollowerAck {
                            committed_through: self.committed_through,
                            fdatasynced: true,
                        });
                    }
                    return Err(WalFollowerError::BatchNotContiguous);
                }
            }
        }
        let (frames, chain) =
            validate_frames(&header, &batch.bytes, batch_end - batch.from_frame, self.chain);
        if frames.len() != batch_end - batch.from_frame {
            return Err(WalFollowerError::FrameChecksum);
        }
        let mut committed_through = self.committed_through;
        for (offset, frame) in frames.iter().enumerate() {
            if frame.commit {
                committed_through = Some(batch.from_frame + offset + 1);
            }
        }
        let mut wal_file =
            OpenOptions::new().write(true).open(self.location.join(wal_file_name()))?;
        wal_file.seek(std::io::SeekFrom::Start(
            u64::try_from(header.frame_range(batch.from_frame, batch_end).start)
                .map_err(|_| WalFollowerError::FrameIo)?,
        ))?;
        wal_file.write_all(&batch.bytes)?;
        // The acknowledgement's whole meaning is durability: the sync
        // completes before the ack exists (D-35's RegionalDurable).
        wal_file.sync_data()?;
        self.applied = batch_end;
        self.chain = chain;
        self.committed_through = committed_through;
        Ok(FollowerAck { committed_through: self.committed_through, fdatasynced: true })
    }
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
    let mut source = File::open(writer.join(name))?;
    let mut target = File::create(follower.join(name))?;
    std::io::copy(&mut source, &mut target)?;
    target.sync_all()?;
    drop(target);
    std::fs::set_permissions(follower.join(name), permissions)?;
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
        Ok(permissions) => {
            let mut source = File::open(writer.join(name))?;
            let mut target = File::create(follower.join(name))?;
            std::io::copy(&mut source, &mut target)?;
            target.sync_all()?;
            drop(target);
            std::fs::set_permissions(follower.join(name), permissions.permissions())?;
            Ok(())
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

/// Reads the writer location's WAL, tolerating its absence (an empty
/// read is a checkpointed-and-closed writer).
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
    /// The underlying file I/O failed.
    Io(std::io::Error),
    /// The follower's database could not be opened or checkpointed.
    Sqlite(rusqlite::Error),
    /// The generation fold could not checkpoint every WAL frame.
    CheckpointIncomplete,
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
            Self::FrameIo => formatter.write_str("a frame write failed"),
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
