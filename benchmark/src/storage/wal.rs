//! Private write-ahead log frames, appends, and verified replay.

use std::{
    collections::BTreeSet,
    fs::{File, OpenOptions},
    io::{self, Read, Write},
    path::{Path, PathBuf},
};

#[cfg(test)]
use std::sync::Mutex;

use super::{
    codec::{self, Reader},
    intent::StagedWrite,
};
use crate::types::{OperationId, TenantId};

/// Frame type: the prepare record naming the operation and write-set digest.
pub(crate) const FRAME_PREPARE: u8 = 1;

/// Frame type: one staged write of the prepared write set.
pub(crate) const FRAME_DATA: u8 = 2;

/// Frame type: the commit marker closing one prepared write set.
pub(crate) const FRAME_COMMIT_MARKER: u8 = 3;

/// Frame header size: one type byte, four payload-length bytes, eight
/// checksum bytes.
const HEADER_BYTES: usize = 13;

/// Maximum payload size of one legal WAL frame.
///
/// A data frame carries one document capped by `MAX_DOCUMENT_BYTES`, plus a
/// bounded staged-write wrapper. Prepare and marker frames carry two bounded
/// identities and fixed fields, so the same ceiling covers every frame.
const MAX_WAL_FRAME_BYTES: usize = codec::MAX_DOCUMENT_BYTES + (1 << 10);

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

/// The identity tuple carried by a commit-marker frame.
#[derive(Debug, Eq, PartialEq)]
struct CommitMarker {
    tenant: String,
    operation: String,
    digest: u64,
    sequence: u64,
    root: String,
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
    /// The location of the log, used only by the test-only fault injection.
    #[cfg_attr(not(test), allow(dead_code))]
    path: PathBuf,
    fresh: bool,
    /// The file offset after the last fully appended frame. Every append
    /// rolls back to this offset if it cannot complete.
    offset: u64,
    /// Test-only: fail the next append after writing half of its frame.
    #[cfg(test)]
    partial_append_next: bool,
}

/// Test-only, one-shot, path-keyed poison: the next `truncate` on exactly this
/// log fails. Keying by path keeps parallel tests that truncate their own
/// logs unaffected.
#[cfg(test)]
static FAIL_TRUNCATE_FOR: Mutex<Option<PathBuf>> = Mutex::new(None);

/// Arms the test-only truncate failure for one log path.
#[cfg(test)]
pub(crate) fn fail_truncate_for(path: PathBuf) {
    *FAIL_TRUNCATE_FOR.lock().expect("truncate poison lock") = Some(path);
}

impl WalWriter {
    /// Opens or creates the WAL at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        let offset = file.metadata()?.len();
        Ok(Self {
            file,
            path: path.to_path_buf(),
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
        if payload.len() > MAX_WAL_FRAME_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "WAL frame exceeds the format limit",
            ));
        }
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
    ///
    /// A failure here must reach the caller: opening with a torn tail still
    /// on disk would leave the append offset spanning it, so the next append
    /// lands behind torn bytes and a later open classifies the damage as
    /// mid-file corruption.
    pub(crate) fn truncate(&mut self, good_bytes: u64) -> io::Result<()> {
        #[cfg(test)]
        {
            let mut poison = FAIL_TRUNCATE_FOR.lock().expect("truncate poison lock");
            if let Some(path) = poison.take()
                && path == self.path
            {
                return Err(io::Error::other("injected truncate failure"));
            } else if let Some(path) = poison.as_ref().cloned() {
                // Not this log: leave the poison armed for its owner.
                *poison = Some(path);
            }
        }
        self.file.set_len(good_bytes)?;
        self.file.sync_all()?;
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
/// ignored: it never published anything. Only a physically incomplete final
/// header or payload is an interrupted append. A complete checksum or
/// structure failure is corruption even in the final position because that
/// frame may already have been synced. An impossible length or a damaged
/// length that conceals a later verified frame also fails closed.
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
    let mut previous_sequence = None;
    let mut seen_sequences = BTreeSet::new();
    let mut seen_operations = BTreeSet::new();
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
        if length > MAX_WAL_FRAME_BYTES {
            return Err(WalCorruption::MidFileCorruption);
        }
        let Some(payload) = reader.take(length) else {
            // A legal length running past EOF is repairable only when no
            // later verified frame proves that the length field swallowed
            // live history.
            if has_verified_frame_after(&bytes, frame_start) {
                return Err(WalCorruption::MidFileCorruption);
            }
            return Ok(torn(commits, frame_start));
        };
        consumed = frame_start + HEADER_BYTES + length;
        if codec::fnv1a(payload) != checksum {
            return Err(WalCorruption::MidFileCorruption);
        }
        let mut payload_reader = Reader::new(payload);
        match frame_type {
            FRAME_PREPARE => {
                let Some((tenant, operation, digest, sequence, write_count)) =
                    decode_prepare(&mut payload_reader)
                else {
                    return Err(WalCorruption::MidFileCorruption);
                };
                // A previous prepare without a marker is an unfinished commit;
                // it is discarded rather than published.
                pending = Some((tenant, operation, digest, sequence, write_count, Vec::new()));
            }
            FRAME_DATA => {
                let Some(write) = codec::decode_staged_write(&mut payload_reader) else {
                    return Err(WalCorruption::MidFileCorruption);
                };
                if payload_reader.remaining() != 0 {
                    return Err(WalCorruption::MidFileCorruption);
                }
                let Some(pending_write) = pending.as_mut() else {
                    return Err(WalCorruption::MidFileCorruption);
                };
                pending_write.5.push(write);
                if pending_write.5.len() > pending_write.4 as usize {
                    return Err(WalCorruption::MidFileCorruption);
                }
            }
            FRAME_COMMIT_MARKER => {
                let Some((tenant, operation, digest, sequence, write_count, writes)) =
                    pending.take()
                else {
                    return Err(WalCorruption::MidFileCorruption);
                };
                if write_count as usize != writes.len() {
                    return Err(WalCorruption::MidFileCorruption);
                }
                let Some(marker) = decode_commit_marker(&mut payload_reader) else {
                    return Err(WalCorruption::MidFileCorruption);
                };
                if marker.tenant != tenant
                    || marker.operation != operation
                    || marker.digest != digest
                    || marker.sequence != sequence
                {
                    return Err(WalCorruption::MidFileCorruption);
                }
                let commit =
                    WalCommit { tenant, operation, digest, sequence, root: marker.root, writes };
                if !commit_is_semantically_valid(&commit)
                    || previous_sequence.is_some_and(|previous| commit.sequence <= previous)
                    || !seen_sequences.insert(commit.sequence)
                    || !seen_operations.insert((commit.tenant.clone(), commit.operation.clone()))
                {
                    return Err(WalCorruption::MidFileCorruption);
                }
                previous_sequence = Some(commit.sequence);
                commits.push(commit);
            }
            _ => return Err(WalCorruption::MidFileCorruption),
        }
    }
    Ok(ScanReport { commits, good_bytes: bytes.len() as u64, torn_tail: false })
}

/// Validates the logical commit carried by a physically valid WAL group.
fn commit_is_semantically_valid(commit: &WalCommit) -> bool {
    let Ok(tenant) = TenantId::try_new(&commit.tenant) else {
        return false;
    };
    if OperationId::try_new(&commit.operation).is_err()
        || commit.sequence == 0
        || !canonical_root_matches_sequence(&commit.root, commit.sequence)
        || commit.writes.is_empty()
        || codec::staged_writes_have_duplicate_targets(&commit.writes)
        || commit.writes.iter().any(|write| {
            matches!(write, StagedWrite::Put { document, .. } if !codec::document_within_limits(document))
        })
    {
        return false;
    }
    codec::write_set_digest(&tenant, &commit.writes) == commit.digest
}

fn canonical_root_matches_sequence(root: &str, sequence: u64) -> bool {
    root.len() == 32
        && root.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        && u64::from_str_radix(&root[..16], 16) == Ok(sequence)
}

/// Decodes a prepare payload and refuses trailing fields.
fn decode_prepare(reader: &mut Reader<'_>) -> Option<(String, String, u64, u64, u32)> {
    let prepare = (
        reader.read_string()?,
        reader.read_string()?,
        reader.read_u64()?,
        reader.read_u64()?,
        reader.read_u32()?,
    );
    (reader.remaining() == 0).then_some(prepare)
}

/// Encodes the persisted commit-marker spelling shared by publication and
/// recovery tests.
pub(crate) fn encode_commit_marker(
    tenant: &str,
    operation: &str,
    digest: u64,
    sequence: u64,
    root: &str,
) -> Vec<u8> {
    let mut marker = Vec::new();
    codec::push_str(&mut marker, tenant);
    codec::push_str(&mut marker, operation);
    codec::push_u64(&mut marker, digest);
    codec::push_u64(&mut marker, sequence);
    codec::push_str(&mut marker, root);
    marker
}

/// Decodes the exact commit-marker tuple and refuses trailing fields.
fn decode_commit_marker(reader: &mut Reader<'_>) -> Option<CommitMarker> {
    let marker = CommitMarker {
        tenant: reader.read_string()?,
        operation: reader.read_string()?,
        digest: reader.read_u64()?,
        sequence: reader.read_u64()?,
        root: reader.read_string()?,
    };
    (reader.remaining() == 0).then_some(marker)
}

/// Reports whether another complete, self-verifying and structurally valid
/// frame begins after a damaged frame start.
fn has_verified_frame_after(bytes: &[u8], damaged_start: usize) -> bool {
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
        let mut reader = Reader::new(&bytes[candidate..]);
        let Some((frame_type, length, checksum)) = read_header(&mut reader) else {
            return false;
        };
        if length > MAX_WAL_FRAME_BYTES {
            return false;
        }
        let Some(payload) = reader.take(length) else {
            return false;
        };
        codec::fnv1a(payload) == checksum && payload_is_structurally_valid(frame_type, payload)
    })
}

/// Validates one frame payload without depending on surrounding prepare
/// state. This is used only to recognize a verified successor after damage.
fn payload_is_structurally_valid(frame_type: u8, payload: &[u8]) -> bool {
    let mut reader = Reader::new(payload);
    match frame_type {
        FRAME_PREPARE => decode_prepare(&mut reader).is_some(),
        FRAME_DATA => codec::decode_staged_write(&mut reader).is_some() && reader.remaining() == 0,
        FRAME_COMMIT_MARKER => decode_commit_marker(&mut reader).is_some(),
        _ => false,
    }
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

#[cfg(test)]
mod tests {
    use std::{
        path::PathBuf,
        sync::atomic::{AtomicU64, Ordering},
    };

    use super::{
        CommitMarker, FRAME_COMMIT_MARKER, FRAME_DATA, FRAME_PREPARE, HEADER_BYTES,
        MAX_WAL_FRAME_BYTES, WalCorruption, WalWriter, decode_commit_marker, encode_commit_marker,
        scan,
    };
    use crate::{
        storage::{
            codec::{self, Reader},
            intent::StagedWrite,
        },
        types::{CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, TenantId},
    };

    fn location(tag: &str) -> PathBuf {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir()
            .join(format!("terrabase-root-wal-{tag}-{}-{index}", std::process::id()))
    }

    fn prepare_payload(operation: &str) -> Vec<u8> {
        let mut payload = Vec::new();
        codec::push_str(&mut payload, "tenant-a");
        codec::push_str(&mut payload, operation);
        codec::push_u64(&mut payload, 7);
        codec::push_u64(&mut payload, 1);
        codec::push_u32(&mut payload, 0);
        payload
    }

    fn one_write(text: String) -> StagedWrite {
        StagedWrite::Put {
            document: Document::new(
                DocumentId::try_new("doc-1").expect("valid document id"),
                CollectionName::try_new("users").expect("valid collection"),
                DocumentValue::String(text),
            ),
            expected: ExpectedVersion::New,
        }
    }

    fn canonical_root(sequence: u64) -> String {
        format!("{sequence:016x}0123456789abcdef")
    }

    fn append_commit(
        wal_writer: &mut WalWriter,
        tenant: &str,
        operation: &str,
        sequence: u64,
        root: &str,
        staged_writes: &[StagedWrite],
        digest: u64,
    ) {
        let mut prepare = Vec::new();
        codec::push_str(&mut prepare, tenant);
        codec::push_str(&mut prepare, operation);
        codec::push_u64(&mut prepare, digest);
        codec::push_u64(&mut prepare, sequence);
        codec::push_u32(
            &mut prepare,
            u32::try_from(staged_writes.len()).expect("test write count fits u32"),
        );
        wal_writer.append(FRAME_PREPARE, &prepare).expect("prepare appends");
        for write in staged_writes {
            let mut payload = Vec::new();
            codec::encode_staged_write(&mut payload, write);
            wal_writer.append(FRAME_DATA, &payload).expect("data appends");
        }
        wal_writer
            .append(
                FRAME_COMMIT_MARKER,
                &encode_commit_marker(tenant, operation, digest, sequence, root),
            )
            .expect("marker appends");
    }

    fn assert_semantic_refusal(
        tag: &str,
        tenant: &str,
        operation: &str,
        sequence: u64,
        root: &str,
        staged_writes: &[StagedWrite],
        digest: u64,
    ) {
        let path = location(tag);
        let mut wal_writer = WalWriter::open(&path).expect("WAL opens");
        append_commit(&mut wal_writer, tenant, operation, sequence, root, staged_writes, digest);
        wal_writer.sync().expect("hostile frames sync");
        drop(wal_writer);
        assert_eq!(scan(&path), Err(WalCorruption::MidFileCorruption));
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn commit_marker_exact_vector_round_trips_every_identity_field() {
        let encoded = encode_commit_marker("t", "op", 0x0102_0304_0506_0708, 9, "r");
        assert_eq!(
            encoded,
            [
                0x01, 0x00, 0x00, 0x00, b't', 0x02, 0x00, 0x00, 0x00, b'o', b'p', 0x08, 0x07, 0x06,
                0x05, 0x04, 0x03, 0x02, 0x01, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, b'r',
            ],
            "the persisted field order must not drift"
        );
        let mut reader = Reader::new(&encoded);
        assert_eq!(
            decode_commit_marker(&mut reader),
            Some(CommitMarker {
                tenant: "t".to_owned(),
                operation: "op".to_owned(),
                digest: 0x0102_0304_0506_0708,
                sequence: 9,
                root: "r".to_owned(),
            })
        );
    }

    #[test]
    fn physically_incomplete_final_payload_is_the_only_repairable_tail() {
        let path = location("incomplete-tail");
        let mut writer = WalWriter::open(&path).expect("WAL opens");
        writer.append(FRAME_PREPARE, &prepare_payload("op-a")).expect("prepare appends");
        writer.sync().expect("prepare syncs");
        drop(writer);
        let length = std::fs::metadata(&path).expect("WAL metadata").len();
        std::fs::OpenOptions::new()
            .write(true)
            .open(&path)
            .expect("WAL opens for the drill")
            .set_len(length - 1)
            .expect("final payload truncates");

        let report = scan(&path).expect("an incomplete final payload is repairable");
        assert!(report.torn_tail);
        assert_eq!(report.good_bytes, 0);
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn complete_final_checksum_failure_is_corruption() {
        let path = location("checksum-tail");
        let mut writer = WalWriter::open(&path).expect("WAL opens");
        writer.append(FRAME_PREPARE, &prepare_payload("op-a")).expect("prepare appends");
        writer.sync().expect("prepare syncs");
        drop(writer);
        let mut bytes = std::fs::read(&path).expect("WAL reads");
        *bytes.last_mut().expect("payload byte") ^= 0x01;
        std::fs::write(&path, bytes).expect("checksum damage writes");

        assert_eq!(scan(&path), Err(WalCorruption::MidFileCorruption));
        std::fs::remove_file(path).ok();
    }

    #[test]
    fn complete_final_structure_failure_and_orphan_marker_are_corruption() {
        let malformed_path = location("structure-tail");
        let mut malformed = WalWriter::open(&malformed_path).expect("WAL opens");
        malformed.append(FRAME_PREPARE, &[0]).expect("malformed frame appends");
        malformed.sync().expect("malformed frame syncs");
        drop(malformed);
        assert_eq!(scan(&malformed_path), Err(WalCorruption::MidFileCorruption));

        let orphan_path = location("orphan-marker");
        let mut orphan = WalWriter::open(&orphan_path).expect("WAL opens");
        orphan
            .append(FRAME_COMMIT_MARKER, &encode_commit_marker("tenant-a", "op-a", 7, 1, "root"))
            .expect("orphan marker appends");
        orphan.sync().expect("orphan marker syncs");
        drop(orphan);
        assert_eq!(scan(&orphan_path), Err(WalCorruption::MidFileCorruption));

        std::fs::remove_file(malformed_path).ok();
        std::fs::remove_file(orphan_path).ok();
    }

    #[test]
    fn prepare_and_marker_must_consume_and_match_every_field() {
        let trailing_path = location("prepare-trailing");
        let mut trailing_payload = prepare_payload("op-a");
        trailing_payload.push(0);
        let mut trailing = WalWriter::open(&trailing_path).expect("WAL opens");
        trailing.append(FRAME_PREPARE, &trailing_payload).expect("prepare appends");
        trailing.sync().expect("prepare syncs");
        drop(trailing);
        assert_eq!(scan(&trailing_path), Err(WalCorruption::MidFileCorruption));

        let mismatch_path = location("marker-mismatch");
        let mut mismatch = WalWriter::open(&mismatch_path).expect("WAL opens");
        mismatch.append(FRAME_PREPARE, &prepare_payload("op-a")).expect("prepare appends");
        mismatch
            .append(FRAME_COMMIT_MARKER, &encode_commit_marker("tenant-a", "op-b", 7, 1, "root"))
            .expect("mismatched marker appends");
        mismatch.sync().expect("frames sync");
        drop(mismatch);
        assert_eq!(scan(&mismatch_path), Err(WalCorruption::MidFileCorruption));

        std::fs::remove_file(trailing_path).ok();
        std::fs::remove_file(mismatch_path).ok();
    }

    #[test]
    fn damaged_or_impossible_length_cannot_swallow_a_verified_successor() {
        let swallowed_path = location("length-swallow");
        let first_payload = prepare_payload("op-a");
        let mut swallowed = WalWriter::open(&swallowed_path).expect("WAL opens");
        swallowed.append(FRAME_PREPARE, &first_payload).expect("first prepare appends");
        swallowed.append(FRAME_PREPARE, &prepare_payload("op-b")).expect("second prepare appends");
        swallowed.sync().expect("prepares sync");
        drop(swallowed);
        let mut bytes = std::fs::read(&swallowed_path).expect("WAL reads");
        let declared = u32::try_from(bytes.len()).expect("test WAL fits a frame length");
        bytes[1..5].copy_from_slice(&declared.to_le_bytes());
        assert!(
            HEADER_BYTES + first_payload.len() < bytes.len(),
            "a complete successor follows the damaged frame"
        );
        std::fs::write(&swallowed_path, bytes).expect("damaged length writes");
        assert_eq!(scan(&swallowed_path), Err(WalCorruption::MidFileCorruption));

        let impossible_path = location("length-impossible");
        let mut impossible = WalWriter::open(&impossible_path).expect("WAL opens");
        impossible.append(FRAME_PREPARE, &prepare_payload("op-a")).expect("prepare appends");
        impossible.sync().expect("prepare syncs");
        drop(impossible);
        let mut bytes = std::fs::read(&impossible_path).expect("WAL reads");
        let impossible_length =
            u32::try_from(MAX_WAL_FRAME_BYTES + 1).expect("format ceiling fits u32");
        bytes[1..5].copy_from_slice(&impossible_length.to_le_bytes());
        std::fs::write(&impossible_path, bytes).expect("impossible length writes");
        assert_eq!(scan(&impossible_path), Err(WalCorruption::MidFileCorruption));

        std::fs::remove_file(swallowed_path).ok();
        std::fs::remove_file(impossible_path).ok();
    }

    #[test]
    fn checksummed_but_impossible_commit_groups_fail_closed() {
        let staged_writes = vec![one_write("valid".to_owned())];
        let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
        let digest = codec::write_set_digest(&tenant, &staged_writes);

        assert_semantic_refusal(
            "zero-sequence",
            "tenant-a",
            "op-zero",
            0,
            &canonical_root(0),
            &staged_writes,
            digest,
        );
        assert_semantic_refusal(
            "bad-tenant",
            "tenant\n",
            "op-tenant",
            1,
            &canonical_root(1),
            &staged_writes,
            digest,
        );
        assert_semantic_refusal(
            "bad-operation",
            "tenant-a",
            "op\n",
            1,
            &canonical_root(1),
            &staged_writes,
            digest,
        );
        assert_semantic_refusal(
            "malformed-root",
            "tenant-a",
            "op-root",
            1,
            "not-a-root",
            &staged_writes,
            digest,
        );
        assert_semantic_refusal(
            "mismatched-root",
            "tenant-a",
            "op-root",
            1,
            &canonical_root(2),
            &staged_writes,
            digest,
        );
        assert_semantic_refusal(
            "wrong-digest",
            "tenant-a",
            "op-digest",
            1,
            &canonical_root(1),
            &staged_writes,
            digest ^ 1,
        );

        let empty = Vec::new();
        assert_semantic_refusal(
            "empty-writes",
            "tenant-a",
            "op-empty",
            1,
            &canonical_root(1),
            &empty,
            codec::write_set_digest(&tenant, &empty),
        );

        let duplicates = vec![staged_writes[0].clone(), staged_writes[0].clone()];
        assert_semantic_refusal(
            "duplicate-target",
            "tenant-a",
            "op-duplicate",
            1,
            &canonical_root(1),
            &duplicates,
            codec::write_set_digest(&tenant, &duplicates),
        );

        let oversized = vec![one_write("x".repeat(codec::MAX_DOCUMENT_BYTES))];
        assert!(!matches!(
            &oversized[0],
            StagedWrite::Put { document, .. } if codec::document_within_limits(document)
        ));
        assert_semantic_refusal(
            "oversized-document",
            "tenant-a",
            "op-oversized",
            1,
            &canonical_root(1),
            &oversized,
            codec::write_set_digest(&tenant, &oversized),
        );
    }

    #[test]
    fn commit_sequences_and_operation_owners_are_unique_in_wal_order() {
        let writes = vec![one_write("valid".to_owned())];
        let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
        let digest = codec::write_set_digest(&tenant, &writes);

        for (tag, first, second, first_op, second_op) in [
            ("reversed", 2, 1, "op-a", "op-b"),
            ("duplicate-sequence", 1, 1, "op-a", "op-b"),
            ("duplicate-operation", 1, 2, "op-a", "op-a"),
        ] {
            let path = location(tag);
            let mut wal_writer = WalWriter::open(&path).expect("WAL opens");
            append_commit(
                &mut wal_writer,
                "tenant-a",
                first_op,
                first,
                &canonical_root(first),
                &writes,
                digest,
            );
            append_commit(
                &mut wal_writer,
                "tenant-a",
                second_op,
                second,
                &canonical_root(second),
                &writes,
                digest,
            );
            wal_writer.sync().expect("hostile frames sync");
            drop(wal_writer);
            assert_eq!(scan(&path), Err(WalCorruption::MidFileCorruption), "{tag}");
            std::fs::remove_file(path).ok();
        }
    }
}
