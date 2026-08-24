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
    /// A frame that is not the trailing frame was truncated or failed its
    /// checksum: mid-file corruption, which fails closed.
    MidFileCorruption,
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

/// The result of one verified scan of the WAL.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct ScanReport {
    /// Every commit whose prepare group and commit marker both verify.
    pub(crate) commits: Vec<WalCommit>,
    /// The byte length of the verified prefix. A torn trailing frame starts
    /// here, so recovery may truncate the file to this length.
    pub(crate) good_bytes: u64,
    /// Whether a torn trailing frame was found and dropped.
    pub(crate) torn_tail: bool,
}

/// An append-only WAL writer.
pub(crate) struct WalWriter {
    file: File,
    fresh: bool,
    /// The file offset after the last fully appended frame. Every append
    /// rolls back to this offset if it cannot complete.
    offset: u64,
    /// Test-only: fail the next append after writing half of its frame.
    #[cfg(test)]
    partial_append_next: bool,
}

impl WalWriter {
    /// Opens or creates the WAL at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        let offset = file.metadata()?.len();
        Ok(Self {
            file,
            fresh,
            offset,
            #[cfg(test)]
            partial_append_next: false,
        })
    }

    /// Reports whether this open created the file.
    pub(crate) fn is_fresh(&self) -> bool {
        self.fresh
    }

    /// Appends one checksummed frame.
    ///
    /// If the write cannot complete, the file is truncated back to the offset
    /// before this append, so a partially written frame never persists.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append(&mut self, frame_type: u8, payload: &[u8]) -> io::Result<()> {
        let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
        codec::push_u8(&mut frame, frame_type);
        codec::push_u32(&mut frame, payload.len() as u32);
        codec::push_u64(&mut frame, codec::fnv1a(payload));
        frame.extend_from_slice(payload);
        #[cfg(test)]
        if self.partial_append_next {
            self.partial_append_next = false;
            let half = frame.len() / 2;
            let injected = io::Error::other("injected partial append");
            if self.file.write_all(&frame[..half]).is_err() {
                return Err(injected);
            }
            return self.rollback(injected);
        }
        match self.file.write_all(&frame) {
            Ok(()) => {
                self.offset += frame.len() as u64;
                Ok(())
            }
            Err(error) => self.rollback(error),
        }
    }

    /// Truncates the file back to the last-good offset after a failed append.
    fn rollback(&mut self, error: io::Error) -> io::Result<()> {
        self.file.set_len(self.offset)?;
        Err(error)
    }

    /// Truncates the file to a verified prefix length, dropping a torn
    /// trailing frame.
    pub(crate) fn truncate(&mut self, good_bytes: u64) -> io::Result<()> {
        self.file.set_len(good_bytes)?;
        self.offset = good_bytes;
        Ok(())
    }

    /// Flushes the file's kernel-visible bytes to the device.
    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }

    /// Arms a test-only failure that writes half of the next frame and then
    /// fails the append.
    #[cfg(test)]
    pub(crate) fn fail_next_append_partially(&mut self) {
        self.partial_append_next = true;
    }
}

/// Scans the WAL and returns every commit whose prepare and commit marker both
/// verify.
///
/// A prepare record without a matching, structurally complete commit marker is
/// ignored: it never published anything. A torn or checksum-failing frame in
/// the *trailing* position is the interrupted-append case: it is reported as
/// absent (`torn_tail`), with `good_bytes` naming the verified prefix, so
/// recovery can truncate it and open. The same damage at any non-trailing
/// position is mid-file corruption and fails the scan so recovery refuses to
/// open.
pub(crate) fn scan(path: &Path) -> Result<ScanReport, WalCorruption> {
    let mut bytes = Vec::new();
    match File::open(path) {
        Ok(mut file) => {
            if file.read_to_end(&mut bytes).is_err() {
                return Err(WalCorruption::Unreadable);
            }
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            return Ok(ScanReport::default());
        }
        Err(_) => return Err(WalCorruption::Unreadable),
    }

    let mut reader = Reader::new(&bytes);
    let mut commits = Vec::new();
    let mut pending: Option<(String, String, u64, u64, u32, Vec<StagedWrite>)> = None;
    let mut consumed = 0_usize;
    while reader.remaining() > 0 {
        let frame_start = consumed;
        let header = read_header(&mut reader);
        let Some((frame_type, length, checksum)) = header else {
            // The header itself is cut off by end of file: a trailing torn
            // frame.
            return Ok(torn(commits, frame_start));
        };
        let Some(payload) = reader.take(length) else {
            // The declared payload runs past end of file: a trailing torn
            // frame.
            return Ok(torn(commits, frame_start));
        };
        consumed = frame_start + HEADER_BYTES + length;
        if codec::fnv1a(payload) != checksum {
            return classify(commits, bytes.len(), frame_start, consumed);
        }
        let mut payload_reader = Reader::new(payload);
        match frame_type {
            FRAME_PREPARE => {
                let Some(tenant) = payload_reader.read_string() else {
                    return classify(commits, bytes.len(), frame_start, consumed);
                };
                let Some(operation) = payload_reader.read_string() else {
                    return classify(commits, bytes.len(), frame_start, consumed);
                };
                let Some(digest) = payload_reader.read_u64() else {
                    return classify(commits, bytes.len(), frame_start, consumed);
                };
                let Some(sequence) = payload_reader.read_u64() else {
                    return classify(commits, bytes.len(), frame_start, consumed);
                };
                let Some(write_count) = payload_reader.read_u32() else {
                    return classify(commits, bytes.len(), frame_start, consumed);
                };
                // A previous prepare without a marker is an unfinished commit;
                // it is discarded rather than published.
                pending = Some((tenant, operation, digest, sequence, write_count, Vec::new()));
            }
            FRAME_DATA => {
                if let Some(pending_write) = pending.as_mut() {
                    match codec::decode_staged_write(&mut payload_reader) {
                        Some(write) => pending_write.5.push(write),
                        None => return classify(commits, bytes.len(), frame_start, consumed),
                    }
                }
            }
            FRAME_COMMIT_MARKER => {
                if let Some((tenant, operation, digest, sequence, write_count, writes)) =
                    pending.take()
                    && write_count as usize == writes.len()
                {
                    let Some(root) = payload_reader.read_string() else {
                        return classify(commits, bytes.len(), frame_start, consumed);
                    };
                    commits.push(WalCommit { tenant, operation, digest, sequence, root, writes });
                }
            }
            _ => return classify(commits, bytes.len(), frame_start, consumed),
        }
    }
    Ok(ScanReport { commits, good_bytes: bytes.len() as u64, torn_tail: false })
}

/// Reads one frame header, or `None` when end of file cuts it off.
fn read_header(reader: &mut Reader<'_>) -> Option<(u8, usize, u64)> {
    let frame_type = reader.read_u8()?;
    let length = reader.read_u32()? as usize;
    let checksum = reader.read_u64()?;
    Some((frame_type, length, checksum))
}

/// Builds the report for a torn trailing frame starting at `frame_start`.
fn torn(commits: Vec<WalCommit>, frame_start: usize) -> ScanReport {
    ScanReport { commits, good_bytes: frame_start as u64, torn_tail: true }
}

/// Classifies a damaged frame: trailing when nothing follows it, mid-file
/// corruption otherwise.
fn classify(
    commits: Vec<WalCommit>,
    file_len: usize,
    frame_start: usize,
    frame_end: usize,
) -> Result<ScanReport, WalCorruption> {
    if frame_end >= file_len {
        Ok(torn(commits, frame_start))
    } else {
        Err(WalCorruption::MidFileCorruption)
    }
}
