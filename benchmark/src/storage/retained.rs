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
//! serving a prefix. Records never carry document content — a tombstone
//! that kept the body would keep deleted data alive. D-3 clock stamps are
//! process-local lane state and are not durable; evidence re-derived from
//! this store carries the origin stamp.

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
    fn identity(&self) -> (String, String, u64, String) {
        (
            self.tenant.as_str().to_owned(),
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
}

impl RetainedWriter {
    /// Opens or creates the store at `path`.
    pub(crate) fn open(path: &Path) -> io::Result<Self> {
        let fresh = !path.exists();
        let file = OpenOptions::new().create(true).append(true).read(true).open(path)?;
        Ok(Self { file, fresh })
    }

    /// Reports whether this open created the file.
    pub(crate) fn is_fresh(&self) -> bool {
        self.fresh
    }

    /// Appends one checksummed fact record.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append_fact(&mut self, fact: &RetainedFact) -> io::Result<()> {
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
        self.file.write_all(&frame)
    }

    /// Appends one checksummed bound record: the durable declaration that
    /// history below `bound` has been given up with its evidence extracted,
    /// the journal head it was declared against, and the per-tenant
    /// non-deletion facts of the window.
    #[allow(clippy::cast_possible_truncation)]
    pub(crate) fn append_bound(
        &mut self,
        bound: u64,
        head: u64,
        non_deletion: &[(TenantId, Option<StorageSequence>)],
    ) -> io::Result<()> {
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
        let mut frame = Vec::with_capacity(HEADER_BYTES.saturating_add(payload.len()));
        codec::push_u32(&mut frame, payload.len() as u32);
        codec::push_u64(&mut frame, codec::fnv1a(&payload));
        frame.extend_from_slice(&payload);
        self.file.write_all(&frame)
    }

    /// Truncates the store to its verified prefix length.
    pub(crate) fn truncate(&mut self, good_bytes: u64) -> io::Result<()> {
        self.file.set_len(good_bytes)
    }

    /// Flushes the appended bytes to the device.
    pub(crate) fn sync(&mut self) -> io::Result<()> {
        self.file.sync_data()
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
}

/// The in-memory index over the retained-evidence store.
#[derive(Default)]
pub(crate) struct RetainedIndex {
    facts: Vec<RetainedFact>,
    identities: BTreeSet<(String, String, u64, String)>,
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
    /// The classification follows the write-ahead log's position rule: a
    /// frame that is cut off by end of file — its declared payload, or the
    /// header itself, runs past the end — is an interrupted append, so the
    /// verified prefix ends there and the caller truncates the tail; a
    /// structurally complete frame that fails its checksum or parse is
    /// integrity damage, and with bytes behind it the open fails closed
    /// (only the trailing position keeps the gentler torn-tail reading).
    pub(crate) fn scan(path: &Path) -> RetainedScan {
        let empty = RetainedScan {
            index: Self::default(),
            good_bytes: 0,
            total_bytes: 0,
            mid_file_damage: false,
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
        while reader.remaining() > 0 {
            let frame_start = total - reader.remaining();
            let before = reader.remaining();
            let Some(record) = read_retained_record(&mut reader) else {
                // The reader stopped somewhere inside this frame. Where
                // exactly decides torn tail versus mid-file damage.
                let consumed = before.saturating_sub(reader.remaining());
                let stopped = frame_start + consumed;
                let declared_end = declared_frame_end(&bytes, frame_start);
                let mid_file =
                    declared_end.is_some_and(|end| end <= total && (end < total || stopped < end));
                return RetainedScan { index, good_bytes, total_bytes, mid_file_damage: mid_file };
            };
            let consumed = before.saturating_sub(reader.remaining());
            match record {
                RetainedRecord::Fact(fact) => {
                    index.identities.insert(fact.identity());
                    index.facts.push(fact);
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
        }
        RetainedScan { index, good_bytes, total_bytes, mid_file_damage: false }
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

    /// Returns every retained fact, in append order.
    pub(crate) fn facts(&self) -> &[RetainedFact] {
        &self.facts
    }

    /// Returns the merged per-tenant non-deletion facts.
    pub(crate) fn non_deletion(&self) -> Vec<(TenantId, Option<StorageSequence>)> {
        self.non_deletion.iter().map(|(tenant, through)| (tenant.clone(), *through)).collect()
    }
}

/// Returns where the frame starting at `frame_start` declares it ends, when
/// its length header is present and readable.
fn declared_frame_end(bytes: &[u8], frame_start: usize) -> Option<usize> {
    let total = bytes.len();
    if frame_start + 4 > total {
        return None;
    }
    let length = u32::from_le_bytes(bytes[frame_start..frame_start + 4].try_into().expect("length"))
        as usize;
    Some(frame_start + HEADER_BYTES + length)
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
    match payload_reader.read_u8()? {
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
    }
}
