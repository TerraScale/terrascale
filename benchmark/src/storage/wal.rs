//! Private write-ahead log frames, appends, and verified replay.

use std::{
    fs::{File, OpenOptions},
    io::{self, Read, Write},
    path::Path,
};

use super::{
    codec::{self, Reader},
    intent::StagedWrite,
};

/// Frame type: the prepare record naming the operation and write-set digest.
pub(crate) const FRAME_PREPARE: u8 = 1;

/// Frame type: one staged write of the prepared write set.
pub(crate) const FRAME_DATA: u8 = 2;

/// Frame type: the commit marker closing one prepared write set.
pub(crate) const FRAME_COMMIT_MARKER: u8 = 3;

/// Frame header size: one type byte, four payload-length bytes, eight
/// checksum bytes.
const HEADER_BYTES: usize = 13;

/// Why a WAL could not be replayed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum WalCorruption {
    /// A frame payload was shorter than its declared length.
    TruncatedFrame,
    /// A frame payload did not match its checksum.
    BadChecksum,
    /// The file could not be read.
    Unreadable,
}

/// One verified commit recovered from the WAL.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct WalCommit {
    /// The tenant the commit belongs to.
    pub(crate) tenant: String,
    /// The operation identity of the commit.
    pub(crate) operation: String,
    /// The logical write-set digest of the commit.
    pub(crate) digest: u64,
    /// The storage sequence the commit published.
    pub(crate) sequence: u64,
    /// The immutable root the commit produced.
    pub(crate) root: String,
    /// The verified write set of the commit.
    pub(crate) writes: Vec<StagedWrite>,
}

/// An append-only WAL writer.
pub(crate) struct WalWriter {
    file: File,
    fresh: bool,
}

impl WalWriter {
    /// Opens or creates the WAL at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        Ok(Self { file, fresh })
    }

    /// Reports whether this open created the file.
    pub(crate) fn is_fresh(&self) -> bool {
        self.fresh
    }

    /// Appends one checksummed frame.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append(&mut self, frame_type: u8, payload: &[u8]) -> io::Result<()> {
        let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
        codec::push_u8(&mut frame, frame_type);
        codec::push_u32(&mut frame, payload.len() as u32);
        codec::push_u64(&mut frame, codec::fnv1a(payload));
        frame.extend_from_slice(payload);
        self.file.write_all(&frame)
    }

    /// Flushes the file's kernel-visible bytes to the device.
    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }
}

/// Scans the WAL and returns every commit whose prepare and commit marker both
/// verify.
///
/// A prepare record without a matching, structurally complete commit marker is
/// ignored: it never published anything. Any frame that is truncated or fails
/// its checksum makes the whole scan fail so recovery can refuse to open.
pub(crate) fn scan(path: &Path) -> Result<Vec<WalCommit>, WalCorruption> {
    let mut bytes = Vec::new();
    match File::open(path) {
        Ok(mut file) => {
            if file.read_to_end(&mut bytes).is_err() {
                return Err(WalCorruption::Unreadable);
            }
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(_) => return Err(WalCorruption::Unreadable),
    }

    let mut reader = Reader::new(&bytes);
    let mut commits = Vec::new();
    let mut pending: Option<(String, String, u64, u64, u32, Vec<StagedWrite>)> = None;
    while reader.remaining() > 0 {
        let frame_type = reader.read_u8().ok_or(WalCorruption::TruncatedFrame)?;
        let length = reader.read_u32().ok_or(WalCorruption::TruncatedFrame)? as usize;
        let checksum = reader.read_u64().ok_or(WalCorruption::TruncatedFrame)?;
        let payload = reader.take(length).ok_or(WalCorruption::TruncatedFrame)?;
        if codec::fnv1a(payload) != checksum {
            return Err(WalCorruption::BadChecksum);
        }
        let mut payload_reader = Reader::new(payload);
        match frame_type {
            FRAME_PREPARE => {
                let tenant = payload_reader.read_string().ok_or(WalCorruption::BadChecksum)?;
                let operation = payload_reader.read_string().ok_or(WalCorruption::BadChecksum)?;
                let digest = payload_reader.read_u64().ok_or(WalCorruption::BadChecksum)?;
                let sequence = payload_reader.read_u64().ok_or(WalCorruption::BadChecksum)?;
                let write_count = payload_reader.read_u32().ok_or(WalCorruption::BadChecksum)?;
                // A previous prepare without a marker is an unfinished commit;
                // it is discarded rather than published.
                pending = Some((tenant, operation, digest, sequence, write_count, Vec::new()));
            }
            FRAME_DATA => {
                if let Some(pending_write) = pending.as_mut() {
                    let write = codec::decode_staged_write(&mut payload_reader)
                        .ok_or(WalCorruption::BadChecksum)?;
                    pending_write.5.push(write);
                }
            }
            FRAME_COMMIT_MARKER => {
                if let Some((tenant, operation, digest, sequence, write_count, writes)) =
                    pending.take()
                    && write_count as usize == writes.len()
                {
                    let root = payload_reader.read_string().ok_or(WalCorruption::BadChecksum)?;
                    commits.push(WalCommit { tenant, operation, digest, sequence, root, writes });
                }
            }
            _ => return Err(WalCorruption::BadChecksum),
        }
    }
    Ok(commits)
}
