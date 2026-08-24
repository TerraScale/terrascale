//! Private durable operation ledger: the resolution anchor for retries.

use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    io::{self, Read, Write},
    path::Path,
};

use super::{
    codec::{self, Reader},
    intent::CommitRejection,
};

/// Record kind: intent to publish, durably synced before pointer replacement.
const RECORD_READY: u8 = 1;

/// Record kind: publication completed, appended after pointer publication.
const RECORD_PUBLISHED: u8 = 2;

/// Record kind: durable rejection outcome.
const RECORD_REJECTED: u8 = 3;

/// Record header size: four length bytes and eight checksum bytes.
const HEADER_BYTES: usize = 12;

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
}

impl LedgerWriter {
    /// Opens or creates the ledger at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        Ok(Self { file, fresh })
    }

    /// Reports whether this open created the file.
    pub(crate) fn is_fresh(&self) -> bool {
        self.fresh
    }

    /// Appends one checksummed record.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append(&mut self, record: &LedgerRecord) -> io::Result<()> {
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
        let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
        codec::push_u32(&mut frame, payload.len() as u32);
        codec::push_u64(&mut frame, codec::fnv1a(&payload));
        frame.extend_from_slice(&payload);
        self.file.write_all(&frame)
    }

    /// Flushes the appended bytes to the device.
    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
    }
}

impl LedgerIndex {
    /// Scans the durable ledger and builds the resolution index.
    ///
    /// A torn or checksum-failing trailing record is ignored: it was appended
    /// but never durably synced, so it never became part of the durable
    /// resolution state. Records before it are kept.
    pub(crate) fn scan(path: &Path) -> Self {
        let mut bytes = Vec::new();
        let Ok(mut file) = File::open(path) else {
            return Self::default();
        };
        if file.read_to_end(&mut bytes).is_err() {
            return Self::default();
        }
        let mut index = Self::default();
        let mut reader = Reader::new(&bytes);
        while reader.remaining() > 0 {
            let Some(record) = read_record(&mut reader) else {
                break;
            };
            index.apply(record);
        }
        index
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

    /// Applies one record to the index.
    pub(crate) fn apply(&mut self, record: LedgerRecord) {
        match record {
            LedgerRecord::Ready(ready) => {
                let entry = OperationEntry {
                    tenant: ready.tenant,
                    operation: ready.operation,
                    digest: ready.digest,
                    sequence: ready.sequence,
                    root: ready.root,
                    requested: ready.requested,
                    state: LedgerState::Ready,
                };
                self.upsert(entry);
            }
            LedgerRecord::Published(published) => {
                let key = (published.tenant.clone(), published.operation.clone());
                if let Some(entry) = self.entries.get_mut(&key) {
                    entry.state = LedgerState::Published {
                        post_publish_synced: published.post_publish_synced,
                    };
                } else {
                    self.upsert(OperationEntry {
                        tenant: published.tenant,
                        operation: published.operation,
                        digest: 0,
                        sequence: published.sequence,
                        root: published.root,
                        requested: LocalDurabilityCode::Written,
                        state: LedgerState::Published {
                            post_publish_synced: published.post_publish_synced,
                        },
                    });
                }
            }
            LedgerRecord::Rejected(rejected) => {
                let key = (rejected.tenant.clone(), rejected.operation.clone());
                if let Some(entry) = self.entries.get_mut(&key) {
                    entry.state = LedgerState::Rejected(rejected.reason);
                } else {
                    self.upsert(OperationEntry {
                        tenant: rejected.tenant,
                        operation: rejected.operation,
                        digest: rejected.digest,
                        sequence: 0,
                        root: String::new(),
                        requested: LocalDurabilityCode::Written,
                        state: LedgerState::Rejected(rejected.reason),
                    });
                }
            }
        }
    }

    fn upsert(&mut self, entry: OperationEntry) {
        let key = (entry.tenant.clone(), entry.operation.clone());
        match self.entries.entry(key) {
            std::collections::btree_map::Entry::Occupied(mut occupied) => {
                // A ready record may only rebind an identity whose digest
                // matches; a different digest is never written by this
                // implementation, so a mismatching replay is ignored.
                let existing = occupied.get_mut();
                if existing.digest == entry.digest {
                    *existing = entry;
                }
            }
            std::collections::btree_map::Entry::Vacant(vacant) => {
                vacant.insert(entry);
            }
        }
    }
}

fn read_record(reader: &mut Reader<'_>) -> Option<LedgerRecord> {
    let length = reader.read_u32()? as usize;
    let checksum = reader.read_u64()?;
    let payload = reader.take(length)?;
    if codec::fnv1a(payload) != checksum {
        return None;
    }
    let mut payload_reader = Reader::new(payload);
    let kind = payload_reader.read_u8()?;
    match kind {
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
            let root = payload_reader.read_string()?;
            let sequence = payload_reader.read_u64()?;
            let post_publish_synced = matches!(payload_reader.read_u8()?, 1);
            Some(LedgerRecord::Published(Published {
                tenant,
                operation,
                root,
                sequence,
                post_publish_synced,
            }))
        }
        RECORD_REJECTED => {
            let tenant = payload_reader.read_string()?;
            let operation = payload_reader.read_string()?;
            let digest = payload_reader.read_u64()?;
            let reason = decode_rejection(payload_reader.read_u8()?)?;
            Some(LedgerRecord::Rejected(RejectedRecord { tenant, operation, digest, reason }))
        }
        _ => None,
    }
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
