//! Private durable operation ledger: the resolution anchor for retries.

use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    io::{self, Read, Write},
    path::Path,
};

use super::{
    PublishedCommit,
    codec::{self, Reader},
    intent::CommitRejection,
};
use crate::types::{OperationId, StorageSequence, TenantId};

/// Record kind: intent to publish, durably synced before pointer replacement.
const RECORD_READY: u8 = 1;

/// Record kind: publication completed, appended after pointer publication.
const RECORD_PUBLISHED: u8 = 2;

/// Record kind: durable rejection outcome.
const RECORD_REJECTED: u8 = 3;

/// Record header size: four length bytes and eight checksum bytes.
const HEADER_BYTES: usize = 12;

/// Maximum encoded byte length of one operation-ledger payload.
///
/// The largest legal record contains two 255-byte identities, a derived
/// commit marker, and a 32-byte root identity, so 1 KiB leaves headroom while
/// still distinguishing a damaged length field from an interrupted append.
const MAX_LEDGER_RECORD_BYTES: usize = 1 << 10;

/// Intent to publish one operation, durable before the pointer moves.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct ReadyToPublish {
    /// The tenant that owns the operation.
    pub(crate) tenant: String,
    /// The operation identity.
    pub(crate) operation: String,
    /// The logical write-set digest bound to the identity.
    pub(crate) digest: u64,
    /// The storage sequence the commit will publish.
    pub(crate) sequence: u64,
    /// The commit-marker identity that closes the prepared write set.
    pub(crate) commit_marker: String,
    /// The immutable root the commit will publish.
    pub(crate) root: String,
    /// The requested local durability level.
    pub(crate) requested: LocalDurabilityCode,
}

/// Publication of one operation completed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct Published {
    /// The tenant that owns the operation.
    pub(crate) tenant: String,
    /// The operation identity.
    pub(crate) operation: String,
    /// The logical write-set digest the identity is bound to. Recovery
    /// roll-forward appends this record without a preceding `Ready` record,
    /// so the digest travels here too and the identity stays bound for a
    /// same-digest retry.
    pub(crate) digest: u64,
    /// Whether the persisted spelling carried the digest. The original
    /// format-v1 publication omitted it, and zero is a legal digest, so the
    /// numeric value cannot act as a provenance marker.
    pub(crate) digest_known: bool,
    /// The immutable root that became visible.
    pub(crate) root: String,
    /// The storage sequence of the publication.
    pub(crate) sequence: u64,
    /// Whether the post-publication pointer and directory syncs were
    /// confirmed before this record was appended.
    pub(crate) post_publish_synced: bool,
}

/// A durable rejection outcome.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct RejectedRecord {
    /// The tenant that owns the operation.
    pub(crate) tenant: String,
    /// The operation identity.
    pub(crate) operation: String,
    /// The logical write-set digest bound to the identity.
    pub(crate) digest: u64,
    /// The typed rejection reason.
    pub(crate) reason: CommitRejection,
}

/// One durable ledger record.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum LedgerRecord {
    /// Intent recorded before pointer replacement.
    Ready(ReadyToPublish),
    /// Publication recorded after pointer replacement and its syncs.
    Published(Published),
    /// Rejection recorded instead of publication.
    Rejected(RejectedRecord),
}

/// The local durability level a record names.
use super::codec::LocalDurabilityCode;

/// The in-memory index over the durable ledger.
#[derive(Default)]
pub(crate) struct LedgerIndex {
    entries: BTreeMap<(String, String), OperationEntry>,
    sequence_owners: BTreeMap<u64, (String, String)>,
}

/// The verified scan outcome of the durable operation ledger.
pub(crate) struct LedgerScan {
    /// The operation index rebuilt from the verified prefix.
    pub(crate) index: LedgerIndex,
    /// The byte length of the verified prefix.
    pub(crate) good_bytes: u64,
    /// The total byte length of the ledger file.
    pub(crate) total_bytes: u64,
    /// Whether a complete frame failed verification or declared an impossible
    /// length. Recovery must fail closed in either case.
    pub(crate) mid_file_damage: bool,
    /// End offsets of every complete semantically verified record. The
    /// independent frontier binds only these exact boundaries.
    frame_ends: Vec<u64>,
}

impl LedgerScan {
    pub(crate) fn frame_ends(&self) -> &[u64] {
        &self.frame_ends
    }
}

/// The durable state of one operation identity inside one tenant.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct OperationEntry {
    /// The tenant that owns the operation.
    pub(crate) tenant: String,
    /// The operation identity.
    pub(crate) operation: String,
    /// The digest the identity is bound to.
    pub(crate) digest: u64,
    /// Whether durable evidence names the digest.
    pub(crate) digest_known: bool,
    /// The storage sequence of the publication attempt.
    pub(crate) sequence: u64,
    /// The immutable root of the publication attempt.
    pub(crate) root: String,
    /// The requested local durability level.
    pub(crate) requested: LocalDurabilityCode,
    /// The durable outcome so far.
    pub(crate) state: LedgerState,
}

/// The durable outcome of one operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LedgerState {
    /// Intent is durable and publication is not decided.
    Ready,
    /// Publication completed; the flag records the confirmed syncs.
    Published {
        /// Whether the post-publication pointer and directory syncs were
        /// confirmed.
        post_publish_synced: bool,
    },
    /// The operation was rejected.
    Rejected(CommitRejection),
}

/// An append-only ledger writer.
pub(crate) struct LedgerWriter {
    file: File,
    fresh: bool,
    bytes: u64,
}

/// One exact raw frame appended to the ledger.
pub(crate) struct LedgerAppend {
    pub(crate) start: u64,
    pub(crate) frame: Vec<u8>,
}

impl LedgerWriter {
    /// Opens or creates the ledger at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        let bytes = file.metadata()?.len();
        Ok(Self { file, fresh, bytes })
    }

    /// Reports whether this open created the file.
    pub(crate) fn is_fresh(&self) -> bool {
        self.fresh
    }

    /// Appends one checksummed record.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append(&mut self, record: &LedgerRecord) -> io::Result<LedgerAppend> {
        let mut payload = Vec::new();
        match record {
            LedgerRecord::Ready(ready) => {
                codec::push_u8(&mut payload, RECORD_READY);
                codec::push_str(&mut payload, &ready.tenant);
                codec::push_str(&mut payload, &ready.operation);
                codec::push_u64(&mut payload, ready.digest);
                codec::push_u64(&mut payload, ready.sequence);
                codec::push_str(&mut payload, &ready.commit_marker);
                codec::push_str(&mut payload, &ready.root);
                codec::encode_durability(&mut payload, ready.requested);
            }
            LedgerRecord::Published(published) => {
                codec::push_u8(&mut payload, RECORD_PUBLISHED);
                codec::push_str(&mut payload, &published.tenant);
                codec::push_str(&mut payload, &published.operation);
                codec::push_u64(&mut payload, published.digest);
                codec::push_str(&mut payload, &published.root);
                codec::push_u64(&mut payload, published.sequence);
                codec::push_u8(&mut payload, u8::from(published.post_publish_synced));
            }
            LedgerRecord::Rejected(rejected) => {
                codec::push_u8(&mut payload, RECORD_REJECTED);
                codec::push_str(&mut payload, &rejected.tenant);
                codec::push_str(&mut payload, &rejected.operation);
                codec::push_u64(&mut payload, rejected.digest);
                codec::push_u8(&mut payload, rejected.reason as u8);
            }
        }
        if payload.len() > MAX_LEDGER_RECORD_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "operation-ledger record exceeds the format limit",
            ));
        }
        let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
        codec::push_u32(&mut frame, payload.len() as u32);
        codec::push_u64(&mut frame, codec::fnv1a(&payload));
        frame.extend_from_slice(&payload);
        let start = self.bytes;
        self.file.write_all(&frame)?;
        self.bytes = self
            .bytes
            .checked_add(u64::try_from(frame.len()).expect("a ledger frame length fits u64"))
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "ledger length overflow"))?;
        Ok(LedgerAppend { start, frame })
    }

    /// Truncates an interrupted trailing append to the verified prefix and
    /// makes the repaired length durable before recovery continues.
    pub(crate) fn truncate(&mut self, good_bytes: u64) -> io::Result<()> {
        self.file.set_len(good_bytes)?;
        self.file.sync_all()?;
        self.bytes = good_bytes;
        Ok(())
    }

    /// Flushes the appended bytes to the device.
    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }
}

impl LedgerIndex {
    /// Scans the durable ledger and builds the resolution index.
    ///
    /// An incomplete trailing record ends the verified prefix; the caller
    /// truncates it before another append. A complete frame that fails its
    /// checksum or structure fails closed even in the final position because
    /// it may be a synced operation binding, not an interrupted append. A
    /// record length beyond [`MAX_LEDGER_RECORD_BYTES`] is always damage,
    /// never a legal interrupted append.
    pub(crate) fn scan(path: &Path) -> io::Result<LedgerScan> {
        let mut bytes = Vec::new();
        let mut file = match File::open(path) {
            Ok(file) => file,
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                return Ok(LedgerScan {
                    index: Self::default(),
                    good_bytes: 0,
                    total_bytes: 0,
                    mid_file_damage: false,
                    frame_ends: Vec::new(),
                });
            }
            Err(error) => return Err(error),
        };
        file.read_to_end(&mut bytes)?;
        #[allow(clippy::cast_possible_truncation)]
        let total_bytes = bytes.len() as u64;
        let total = bytes.len();
        let mut index = Self::default();
        let mut good_bytes = 0_u64;
        let mut frame_ends = Vec::new();
        let mut reader = Reader::new(&bytes);
        while reader.remaining() > 0 {
            let frame_start = total - reader.remaining();
            let before = reader.remaining();
            let Some(record) = read_record(&mut reader) else {
                let mid_file_damage = match declared_ledger_frame(&bytes, frame_start) {
                    // A header cut off by end of file is an interrupted
                    // trailing append.
                    None => false,
                    // No writer can emit a frame this large. Treat a damaged
                    // length as corruption even when it claims to run past
                    // end of file, or it could hide later bindings.
                    Some((length, _)) if length > MAX_LEDGER_RECORD_BYTES => true,
                    // A whole frame is present but did not verify. Its
                    // position does not make integrity loss safe.
                    Some((_, end)) if end <= total => true,
                    // An in-range but damaged length can claim that the first
                    // frame runs past EOF and swallow later valid frames. A
                    // verified frame inside that claimed tail proves this is
                    // mid-file damage, not an interrupted final append.
                    Some((_, _)) => has_verified_ledger_frame_after(&bytes, frame_start),
                };
                return Ok(LedgerScan {
                    index,
                    good_bytes,
                    total_bytes,
                    mid_file_damage,
                    frame_ends,
                });
            };
            let consumed = before.saturating_sub(reader.remaining());
            if !record_is_semantically_valid(&record) || !index.apply(record) {
                // The frame is complete and checksummed, but its state
                // transition contradicts the durable binding already seen
                // for this operation. Treat that as corruption even at EOF;
                // accepting a prefix could authorize a different outcome.
                return Ok(LedgerScan {
                    index,
                    good_bytes,
                    total_bytes,
                    mid_file_damage: true,
                    frame_ends,
                });
            }
            good_bytes += consumed as u64;
            frame_ends.push(good_bytes);
        }
        Ok(LedgerScan { index, good_bytes, total_bytes, mid_file_damage: false, frame_ends })
    }

    /// Returns the durable entry for one tenant and operation identity.
    pub(crate) fn entry(
        &self,
        tenant: &crate::types::TenantId,
        operation: &crate::types::OperationId,
    ) -> Option<&OperationEntry> {
        self.entry_by_text(tenant.as_str(), operation.as_str())
    }

    /// Returns the durable entry addressed by canonical identity text.
    pub(crate) fn entry_by_text(&self, tenant: &str, operation: &str) -> Option<&OperationEntry> {
        self.entries.get(&(tenant.to_owned(), operation.to_owned()))
    }

    /// Returns the durable entries, in canonical key order.
    pub(crate) fn entries(&self) -> impl Iterator<Item = &OperationEntry> {
        self.entries.values()
    }

    /// Reports whether one Ready or Published binding names the exact
    /// published root head.
    pub(crate) fn binds_published_root(&self, sequence: u64, root: &str) -> bool {
        self.entries.values().any(|entry| {
            entry.sequence == sequence
                && entry.root == root
                && matches!(entry.state, LedgerState::Ready | LedgerState::Published { .. })
        })
    }

    /// Reports whether a durable publication intent lies beyond the root a
    /// compaction checkpoint would preserve.
    pub(crate) fn has_ready_after(&self, sequence: u64) -> bool {
        self.entries
            .values()
            .any(|entry| entry.sequence > sequence && entry.state == LedgerState::Ready)
    }

    /// Reports whether the ledger proves that one verified WAL commit
    /// reached its exact publication outcome.
    pub(crate) fn binds_published_commit(
        &self,
        tenant: &str,
        operation: &str,
        digest: u64,
        sequence: u64,
        root: &str,
    ) -> bool {
        self.entry_by_text(tenant, operation).is_some_and(|entry| {
            entry.digest == digest
                && entry.digest_known
                && entry.sequence == sequence
                && entry.root == root
                && matches!(entry.state, LedgerState::Published { .. })
        })
    }

    /// Checks the per-operation, global-sequence, and single-publication
    /// transition rules without mutating the index.
    pub(crate) fn can_apply(&self, record: &LedgerRecord) -> bool {
        if !record_is_semantically_valid(record) {
            return false;
        }
        let key = match record {
            LedgerRecord::Ready(ready) => (ready.tenant.clone(), ready.operation.clone()),
            LedgerRecord::Published(published) => {
                (published.tenant.clone(), published.operation.clone())
            }
            LedgerRecord::Rejected(rejected) => {
                (rejected.tenant.clone(), rejected.operation.clone())
            }
        };

        match record {
            LedgerRecord::Ready(ready) => {
                !self.entries.contains_key(&key)
                    && !self.sequence_owners.contains_key(&ready.sequence)
            }
            LedgerRecord::Published(published) => match self.entries.get(&key) {
                Some(entry) if entry.state == LedgerState::Ready => {
                    entry.sequence == published.sequence
                        && entry.root == published.root
                        && (!published.digest_known || entry.digest == published.digest)
                }
                Some(entry) => {
                    matches!(entry.state, LedgerState::Published { .. })
                        && !entry.digest_known
                        && published.digest_known
                        && entry.sequence == published.sequence
                        && entry.root == published.root
                }
                None => !self.sequence_owners.contains_key(&published.sequence),
            },
            LedgerRecord::Rejected(_) => !self.entries.contains_key(&key),
        }
    }

    /// Applies one valid record transition to the index.
    ///
    /// An operation may be recorded once as `Rejected`, once as a standalone
    /// recovery `Published`, or as the exact `Ready` to `Published`
    /// transition. Every other transition contradicts the durable binding
    /// and must fail closed.
    #[must_use]
    pub(crate) fn apply(&mut self, record: LedgerRecord) -> bool {
        if !self.can_apply(&record) {
            return false;
        }
        match record {
            LedgerRecord::Ready(ready) => {
                let key = (ready.tenant.clone(), ready.operation.clone());
                self.sequence_owners.insert(ready.sequence, key.clone());
                let entry = OperationEntry {
                    tenant: ready.tenant,
                    operation: ready.operation,
                    digest: ready.digest,
                    digest_known: true,
                    sequence: ready.sequence,
                    root: ready.root,
                    requested: ready.requested,
                    state: LedgerState::Ready,
                };
                self.entries.insert(key, entry);
                true
            }
            LedgerRecord::Published(published) => {
                let key = (published.tenant.clone(), published.operation.clone());
                if let Some(entry) = self.entries.get_mut(&key) {
                    if matches!(entry.state, LedgerState::Published { .. }) {
                        // A standalone format-v1 recovery publication had no
                        // digest field. A verified WAL group may append the
                        // current spelling to supply that missing binding,
                        // but only for the same sequence and root. Preserve
                        // the older record's sync evidence.
                        entry.digest = published.digest;
                        entry.digest_known = true;
                        return true;
                    }
                    entry.state = LedgerState::Published {
                        post_publish_synced: published.post_publish_synced,
                    };
                    // The legacy format-v1 Published spelling carries a zero
                    // digest. Only the exact sequence and root above let it
                    // inherit the digest already bound by Ready.
                    true
                } else {
                    self.sequence_owners.insert(published.sequence, key.clone());
                    self.entries.insert(
                        key,
                        OperationEntry {
                            tenant: published.tenant,
                            operation: published.operation,
                            digest: published.digest,
                            digest_known: published.digest_known,
                            sequence: published.sequence,
                            root: published.root,
                            requested: LocalDurabilityCode::Written,
                            state: LedgerState::Published {
                                post_publish_synced: published.post_publish_synced,
                            },
                        },
                    );
                    true
                }
            }
            LedgerRecord::Rejected(rejected) => {
                let key = (rejected.tenant.clone(), rejected.operation.clone());
                self.entries.insert(
                    key,
                    OperationEntry {
                        tenant: rejected.tenant,
                        operation: rejected.operation,
                        digest: rejected.digest,
                        digest_known: true,
                        sequence: 0,
                        root: String::new(),
                        requested: LocalDurabilityCode::Written,
                        state: LedgerState::Rejected(rejected.reason),
                    },
                );
                true
            }
        }
    }
}

fn read_record(reader: &mut Reader<'_>) -> Option<LedgerRecord> {
    let length = reader.read_u32()? as usize;
    if length > MAX_LEDGER_RECORD_BYTES {
        return None;
    }
    let checksum = reader.read_u64()?;
    let payload = reader.take(length)?;
    if codec::fnv1a(payload) != checksum {
        return None;
    }
    let mut payload_reader = Reader::new(payload);
    let kind = payload_reader.read_u8()?;
    let record = match kind {
        RECORD_READY => {
            let tenant = payload_reader.read_string()?;
            let operation = payload_reader.read_string()?;
            let digest = payload_reader.read_u64()?;
            let sequence = payload_reader.read_u64()?;
            let commit_marker = payload_reader.read_string()?;
            let root = payload_reader.read_string()?;
            let requested = codec::decode_durability(&mut payload_reader)?;
            Some(LedgerRecord::Ready(ReadyToPublish {
                tenant,
                operation,
                digest,
                sequence,
                commit_marker,
                root,
                requested,
            }))
        }
        RECORD_PUBLISHED => {
            let tenant = payload_reader.read_string()?;
            let operation = payload_reader.read_string()?;
            let tail = payload_reader.take(payload_reader.remaining())?;
            decode_current_published(&tenant, &operation, tail)
                .or_else(|| decode_legacy_published(&tenant, &operation, tail))
        }
        RECORD_REJECTED => {
            let tenant = payload_reader.read_string()?;
            let operation = payload_reader.read_string()?;
            let digest = payload_reader.read_u64()?;
            let reason = decode_rejection(payload_reader.read_u8()?)?;
            Some(LedgerRecord::Rejected(RejectedRecord { tenant, operation, digest, reason }))
        }
        _ => None,
    }?;
    if payload_reader.remaining() == 0 { Some(record) } else { None }
}

/// Decodes the current publication spelling, which carries its write-set
/// digest for recovery records that have no preceding `Ready` entry.
fn decode_current_published(tenant: &str, operation: &str, tail: &[u8]) -> Option<LedgerRecord> {
    let mut reader = Reader::new(tail);
    let digest = reader.read_u64()?;
    let root = reader.read_string()?;
    let sequence = reader.read_u64()?;
    let post_publish_synced = decode_bool(&mut reader)?;
    if reader.remaining() != 0 {
        return None;
    }
    Some(LedgerRecord::Published(Published {
        tenant: tenant.to_owned(),
        operation: operation.to_owned(),
        digest,
        digest_known: true,
        root,
        sequence,
        post_publish_synced,
    }))
}

/// Decodes the original format-version-1 publication spelling, retained for
/// N/N-1 persisted compatibility under ADR 0031.
///
/// The old record omitted the digest. A preceding `Ready` record already
/// carries the binding and [`LedgerIndex::apply`] preserves it. A standalone
/// recovery publication retains explicit unknown-digest provenance, which
/// refuses every retry binding until verified WAL evidence upgrades it.
fn decode_legacy_published(tenant: &str, operation: &str, tail: &[u8]) -> Option<LedgerRecord> {
    let mut reader = Reader::new(tail);
    let root = reader.read_string()?;
    let sequence = reader.read_u64()?;
    let post_publish_synced = decode_bool(&mut reader)?;
    if reader.remaining() != 0 {
        return None;
    }
    Some(LedgerRecord::Published(Published {
        tenant: tenant.to_owned(),
        operation: operation.to_owned(),
        digest: 0,
        digest_known: false,
        root,
        sequence,
        post_publish_synced,
    }))
}

/// Validates the logical invariants guaranteed by the ledger writer beyond
/// frame structure and checksum integrity.
fn record_is_semantically_valid(record: &LedgerRecord) -> bool {
    match record {
        LedgerRecord::Ready(ready) => {
            valid_operation_identity(&ready.tenant, &ready.operation)
                && valid_root_binding(ready.sequence, &ready.root)
                && ready.commit_marker == format!("{}:{}", ready.operation, ready.sequence)
        }
        LedgerRecord::Published(published) => {
            valid_operation_identity(&published.tenant, &published.operation)
                && valid_root_binding(published.sequence, &published.root)
        }
        LedgerRecord::Rejected(rejected) => {
            valid_operation_identity(&rejected.tenant, &rejected.operation)
        }
    }
}

fn valid_operation_identity(tenant: &str, operation: &str) -> bool {
    TenantId::try_new(tenant).is_ok() && OperationId::try_new(operation).is_ok()
}

fn valid_root_binding(sequence: u64, root: &str) -> bool {
    sequence != 0
        && root.len() == 32
        && root.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        && u64::from_str_radix(&root[..16], 16) == Ok(sequence)
}

/// Decodes the exact two-state boolean used by ledger records.
fn decode_bool(reader: &mut Reader<'_>) -> Option<bool> {
    match reader.read_u8()? {
        0 => Some(false),
        1 => Some(true),
        _ => None,
    }
}

/// Returns the payload length and declared end of the frame at `frame_start`
/// when its length prefix is present.
fn declared_ledger_frame(bytes: &[u8], frame_start: usize) -> Option<(usize, usize)> {
    let length_end = frame_start.checked_add(4)?;
    let length_bytes = bytes.get(frame_start..length_end)?;
    let length = u32::from_le_bytes(length_bytes.try_into().expect("four length bytes")) as usize;
    let end = frame_start.checked_add(HEADER_BYTES)?.checked_add(length)?;
    Some((length, end))
}

/// Reports whether another self-verifying ledger frame begins after the
/// damaged frame start.
///
/// The scan reaches EOF rather than stopping after the immediate successor:
/// adjacent damaged frames must not hide a later valid operation binding. Each
/// candidate is still bounded by [`MAX_LEDGER_RECORD_BYTES`]. The checksum and
/// full structural decode make an arbitrary payload byte pattern insufficient
/// to count as a successor.
fn has_verified_ledger_frame_after(bytes: &[u8], frame_start: usize) -> bool {
    let Some(search_start) = frame_start.checked_add(1) else {
        return false;
    };
    (search_start..bytes.len()).any(|candidate| {
        let mut reader = Reader::new(&bytes[candidate..]);
        read_record(&mut reader).is_some()
    })
}

fn decode_rejection(code: u8) -> Option<CommitRejection> {
    match code {
        0 => Some(CommitRejection::Validation),
        1 => Some(CommitRejection::Conflict),
        2 => Some(CommitRejection::OperationIdReuse),
        3 => Some(CommitRejection::Storage),
        _ => None,
    }
}

// ---------------------------------------------------------------------------
// The durable publication-history journal (W4-CA-4)
// ---------------------------------------------------------------------------

/// Record kind: one published commit's reconstructable write set.
const HISTORY_RECORD_COMMIT: u8 = 1;

/// Record kind: the durable journal high-water mark (repair F2).
const HISTORY_RECORD_HIGH_WATER: u8 = 2;

/// The append-only publication-history journal (W4-CA-4).
///
/// This side structure answers the inspector facts recorded with the
/// collision action: the operation ledger binds identities to digests but
/// records no document identities, so a ledger-derived read cannot
/// reconstruct a write set; and the ledger index is keyed
/// `(tenant, operation)` in canonical key order, not storage-sequence order.
/// The journal therefore records, per published commit, the tenant, the
/// operation identity, the global storage sequence, the achieved local
/// durability, and the reconstructable write set itself — full documents for
/// puts, the exact deleted key for deletes — in its own sequence-ordered
/// index. It records no digest: the operation ledger already binds identity
/// to write set, and this structure exists to be inverted, not to bind.
///
/// The journal is appended after the root pointer has been published and its
/// post-publication syncs completed, and before the `Published` ledger
/// record: an entry can therefore only ever describe an already published
/// commit, and the common uncertain path (a failed published-ledger sync)
/// still leaves the entry durably recorded. The append is best-effort and
/// never delays or fails the publication path. Because sequences have
/// legitimate holes — the allocation watermark advances before the durable
/// write, and other tenants consume interleaved sequences — a consumer
/// cannot detect a skipped append by discontinuity; the journal therefore
/// also carries a durable high-water mark, updated with each successful
/// append, so `journaled_high_water() < published_state().0` is observable
/// (repair F2).
///
/// A torn or checksum-failing trailing record ends the servable window, and
/// [`FileStorage::open`] truncates the file to the verified prefix so later
/// appends stay scannable (repair F1); the window bound and the high-water
/// mark keep reporting the divergence instead of silently skipping a gap.
pub(crate) struct HistoryWriter {
    file: File,
    fresh: bool,
}

impl HistoryWriter {
    /// Opens or creates the journal at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        Ok(Self { file, fresh })
    }

    /// Reports whether this open created the file.
    pub(crate) fn is_fresh(&self) -> bool {
        self.fresh
    }

    /// Appends one checksummed publication record.
    pub(crate) fn append(&mut self, commit: &PublishedCommit) -> io::Result<()> {
        self.file.write_all(&encode_commit_frame(commit))
    }

    /// Appends one checksummed high-water-mark record (repair F2).
    ///
    /// The mark is written after each successful commit append, so the last
    /// durably recorded mark names the last successfully journaled sequence.
    /// A failed mark append leaves the mark conservative — behind a commit
    /// whose own record is durable — which is safe: the signal never
    /// over-reports completeness.
    pub(crate) fn append_high_water(&mut self, sequence: u64) -> io::Result<()> {
        self.file.write_all(&encode_high_water_frame(sequence))
    }

    /// Truncates the journal to its verified prefix length (repair F1).
    ///
    /// Called at open when the scan stopped before the file's end: without
    /// it, every later append lands behind the torn or damaged bytes where
    /// no scan can see it, and the servable window freezes permanently.
    pub(crate) fn truncate(&mut self, good_bytes: u64) -> io::Result<()> {
        self.file.set_len(good_bytes)?;
        self.file.sync_all()
    }

    /// Flushes the appended bytes to the device.
    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }
}

/// Encodes one publication record's full checksummed frame.
///
/// The compaction pass rewrites the journal through the same spelling the
/// append path writes, so a rewritten journal scans identically.
#[allow(clippy::cast_possible_truncation)]
pub(crate) fn encode_commit_frame(commit: &PublishedCommit) -> Vec<u8> {
    let mut payload = Vec::new();
    codec::push_u8(&mut payload, HISTORY_RECORD_COMMIT);
    codec::push_str(&mut payload, commit.tenant().as_str());
    codec::push_str(&mut payload, commit.operation_id().as_str());
    codec::push_u64(&mut payload, commit.sequence().get());
    codec::encode_durability(&mut payload, codec::LocalDurabilityCode::of(commit.durability()));
    codec::push_u32(&mut payload, commit.writes().len() as u32);
    for write in commit.writes() {
        codec::encode_published_write(&mut payload, write);
    }
    let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
    codec::push_u32(&mut frame, payload.len() as u32);
    codec::push_u64(&mut frame, codec::fnv1a(&payload));
    frame.extend_from_slice(&payload);
    frame
}

/// Decodes one commit-record payload (the bytes inside a commit frame's
/// length and checksum) into its commit. The LSM history keyspace stores
/// exactly these payloads (P1).
pub(crate) fn decode_commit_payload(payload: &[u8]) -> Option<PublishedCommit> {
    let mut reader = Reader::new(payload);
    if reader.read_u8()? != HISTORY_RECORD_COMMIT {
        return None;
    }
    let tenant = TenantId::try_new(&reader.read_string()?).ok()?;
    let operation_id = OperationId::try_new(&reader.read_string()?).ok()?;
    let sequence = StorageSequence::new(reader.read_u64()?);
    let durability = codec::decode_durability(&mut reader)?.to_level();
    let count = reader.read_u32()? as usize;
    let mut writes = Vec::with_capacity(count.min(1024));
    for _ in 0..count {
        writes.push(codec::decode_published_write(&mut reader)?);
    }
    if reader.remaining() != 0 {
        return None;
    }
    Some(PublishedCommit::new(tenant, operation_id, sequence, durability, writes))
}

/// Encodes one high-water-mark record's full checksummed frame.
#[allow(clippy::cast_possible_truncation)]
pub(crate) fn encode_high_water_frame(sequence: u64) -> Vec<u8> {
    let mut payload = Vec::new();
    codec::push_u8(&mut payload, HISTORY_RECORD_HIGH_WATER);
    codec::push_u64(&mut payload, sequence);
    let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
    codec::push_u32(&mut frame, payload.len() as u32);
    codec::push_u64(&mut frame, codec::fnv1a(&payload));
    frame.extend_from_slice(&payload);
    frame
}

/// One entry of the journal's durable byte stream.
enum HistoryEntry {
    /// One published commit's reconstructable write set.
    Commit(PublishedCommit),
    /// One durable high-water-mark observation.
    HighWater(u64),
}

/// The verified scan outcome of the publication-history journal.
pub(crate) struct HistoryScan {
    /// The sequence-ordered index over the verified prefix.
    pub(crate) index: HistoryIndex,
    /// The byte length of the verified prefix.
    pub(crate) good_bytes: u64,
    /// The total byte length of the journal file.
    pub(crate) total_bytes: u64,
    /// The last durably journaled global sequence: the newest verified
    /// commit record or high-water mark, whichever is later.
    pub(crate) high_water: Option<u64>,
}

/// The in-memory, sequence-ordered index over the publication-history
/// journal (W4-CA-4).
///
/// The index is the secondary iteration path the operation-ledger index
/// cannot provide: per tenant, entries are keyed by global storage sequence,
/// so `tenant_commits` pages in publication order without scanning the
/// `(tenant, operation)` ledger order.
#[derive(Default)]
pub(crate) struct HistoryIndex {
    tenants: BTreeMap<String, BTreeMap<u64, PublishedCommit>>,
}

impl HistoryIndex {
    /// Scans the durable journal and builds the sequence-ordered index, the
    /// verified-prefix length, and the durable high-water mark.
    ///
    /// A torn or checksum-failing record stops the scan: everything after it
    /// is unservable, which is exactly the servable-window bound the seam
    /// reports, and the caller truncates the trailing bytes so later
    /// appends stay scannable (repair F1).
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn scan(path: &Path) -> HistoryScan {
        let mut bytes = Vec::new();
        let Ok(mut file) = File::open(path) else {
            return HistoryScan {
                index: Self::default(),
                good_bytes: 0,
                total_bytes: 0,
                high_water: None,
            };
        };
        if file.read_to_end(&mut bytes).is_err() {
            return HistoryScan {
                index: Self::default(),
                good_bytes: 0,
                total_bytes: 0,
                high_water: None,
            };
        }
        let total_bytes = bytes.len() as u64;
        let mut index = Self::default();
        let mut high_water: Option<u64> = None;
        let mut good_bytes: u64 = 0;
        let mut reader = Reader::new(&bytes);
        while reader.remaining() > 0 {
            let before = reader.remaining();
            let Some(entry) = read_history_entry(&mut reader) else {
                break;
            };
            // A record is verified only when the reader consumed a whole
            // frame; a partial or failing frame leaves the tail unservable.
            let consumed = before.saturating_sub(reader.remaining());
            match entry {
                HistoryEntry::Commit(commit) => {
                    high_water =
                        Some(high_water.map_or(commit.sequence().get(), |mark| {
                            mark.max(commit.sequence().get())
                        }));
                    index.upsert(commit);
                }
                HistoryEntry::HighWater(mark) => {
                    high_water = Some(high_water.map_or(mark, |recorded| recorded.max(mark)));
                }
            }
            good_bytes += consumed as u64;
        }
        HistoryScan { index, good_bytes, total_bytes, high_water }
    }

    /// Records one published commit.
    pub(crate) fn upsert(&mut self, commit: PublishedCommit) {
        self.tenants
            .entry(commit.tenant().as_str().to_owned())
            .or_default()
            .insert(commit.sequence().get(), commit);
    }

    /// Reports whether one tenant and sequence pair is already recorded.
    pub(crate) fn contains(&self, tenant: &str, sequence: u64) -> bool {
        self.tenants.get(tenant).is_some_and(|entries| entries.contains_key(&sequence))
    }

    /// Reports whether history records the exact operation at one tenant and
    /// storage sequence.
    pub(crate) fn binds_publication(&self, tenant: &str, operation: &str, sequence: u64) -> bool {
        self.tenants
            .get(tenant)
            .and_then(|entries| entries.get(&sequence))
            .is_some_and(|commit| commit.operation_id().as_str() == operation)
    }

    /// Returns one tenant's commits after `after`, in sequence order.
    pub(crate) fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Vec<PublishedCommit> {
        let Some(entries) = self.tenants.get(tenant.as_str()) else {
            return Vec::new();
        };
        entries
            .range((std::ops::Bound::Excluded(after.get()), std::ops::Bound::Unbounded))
            .take(usize::try_from(limit).unwrap_or(usize::MAX))
            .map(|(_, commit)| commit.clone())
            .collect()
    }

    /// Returns every tenant with recorded commits, in canonical order.
    pub(crate) fn committed_tenants(&self) -> Vec<TenantId> {
        self.tenants.keys().filter_map(|tenant| TenantId::try_new(tenant).ok()).collect()
    }

    /// Returns the tenant's latest recorded sequence at or below `at`.
    pub(crate) fn tenant_sequence(
        &self,
        tenant: &TenantId,
        at: StorageSequence,
    ) -> Option<StorageSequence> {
        let entries = self.tenants.get(tenant.as_str())?;
        entries.range(..=at.get()).next_back().map(|(sequence, _)| StorageSequence::new(*sequence))
    }

    /// Returns the oldest sequence the journal can serve, when it can serve
    /// anything.
    pub(crate) fn servable_start(&self) -> Option<StorageSequence> {
        self.tenants
            .values()
            .filter_map(|entries| entries.keys().next().copied())
            .min()
            .map(StorageSequence::new)
    }

    /// Returns the number of journal commit records the index holds.
    pub(crate) fn record_count(&self) -> u64 {
        self.tenants.values().map(|entries| u64::try_from(entries.len()).unwrap_or(u64::MAX)).sum()
    }

    /// Reports whether every journaled publication still has its durable
    /// operation-ledger binding at the same storage sequence.
    ///
    /// Publication appends `Ready` before the pointer and history record, so
    /// a history commit without this binding is suffix loss in the operation
    /// ledger. The reverse is allowed because history append is best-effort
    /// and its divergence is reported separately.
    pub(crate) fn all_commits_bound_in(&self, ledger: &LedgerIndex) -> bool {
        self.tenants.iter().all(|(tenant, entries)| {
            entries.values().all(|commit| {
                ledger.entry_by_text(tenant, commit.operation_id().as_str()).is_some_and(|entry| {
                    entry.sequence == commit.sequence().get()
                        && matches!(entry.state, LedgerState::Ready | LedgerState::Published { .. })
                })
            })
        })
    }

    /// Returns every recorded commit with a sequence strictly below
    /// `bound`, in global sequence order — the window a compaction pass
    /// gives up (GAP-S1 D5).
    pub(crate) fn commits_below(&self, bound: u64) -> Vec<PublishedCommit> {
        let mut commits: Vec<PublishedCommit> = self
            .tenants
            .values()
            .flat_map(|entries| entries.range(..bound).map(|(_, commit)| commit.clone()))
            .collect();
        commits.sort_by_key(|commit| commit.sequence().get());
        commits
    }

    /// Drops every commit record whose sequence is strictly below `bound`,
    /// leaving the journal's post-compaction in-memory shape.
    pub(crate) fn retain_from(&mut self, bound: u64) {
        for entries in self.tenants.values_mut() {
            let below = entries.range(..bound).map(|(sequence, _)| *sequence).collect::<Vec<_>>();
            for sequence in below {
                entries.remove(&sequence);
            }
        }
    }

    /// Returns every recorded commit with a sequence at or above `bound` —
    /// the records a compaction pass keeps when it rewrites the journal.
    pub(crate) fn commits_at_or_above(&self, bound: u64) -> Vec<PublishedCommit> {
        self.tenants
            .values()
            .flat_map(|entries| entries.range(bound..).map(|(_, commit)| commit.clone()))
            .collect()
    }
}

/// Reads one framed publication-history entry.
fn read_history_entry(reader: &mut Reader<'_>) -> Option<HistoryEntry> {
    let length = reader.read_u32()? as usize;
    let checksum = reader.read_u64()?;
    let payload = reader.take(length)?;
    if codec::fnv1a(payload) != checksum {
        return None;
    }
    let mut payload_reader = Reader::new(payload);
    match payload_reader.read_u8()? {
        HISTORY_RECORD_COMMIT => {
            let tenant = TenantId::try_new(&payload_reader.read_string()?).ok()?;
            let operation_id = OperationId::try_new(&payload_reader.read_string()?).ok()?;
            let sequence = StorageSequence::new(payload_reader.read_u64()?);
            let durability = codec::decode_durability(&mut payload_reader)?.to_level();
            let count = payload_reader.read_u32()? as usize;
            let mut writes = Vec::with_capacity(count.min(1024));
            for _ in 0..count {
                writes.push(codec::decode_published_write(&mut payload_reader)?);
            }
            Some(HistoryEntry::Commit(PublishedCommit::new(
                tenant,
                operation_id,
                sequence,
                durability,
                writes,
            )))
        }
        HISTORY_RECORD_HIGH_WATER => Some(HistoryEntry::HighWater(payload_reader.read_u64()?)),
        _ => None,
    }
}
