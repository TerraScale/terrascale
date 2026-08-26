//! The server-authoritative mapping: journal events into a Loro document.
//!
//! One Loro document backs one sync scope (the spike uses one collection
//! scope). The engine server is the only writer: every
//! [`JournalEvent`](crate::journal::JournalEvent) is materialized at the
//! fixed [`SERVER_PEER`](crate::SERVER_PEER) id, one Loro commit per
//! journal event, so the Loro op order is exactly the journal order and no
//! concurrent-write path exists in the mapping. The container layout is
//! field-granular — `root "docs" -> one mergeable map per document id ->
//! one entry per body field` — which is what makes a single-field patch a
//! small delta. Document delete is the root-map key delete: the Loro
//! tombstone, which removes the document from every consumer's deep value
//! while keeping the delete visible as history for late resync.

use std::collections::BTreeMap;
use std::collections::VecDeque;

use loro::{ExportMode, Frontiers, ID, IdSpan, LoroDoc, LoroMap, LoroValue};

use crate::SERVER_PEER;
use crate::journal::JournalChange;
use crate::journal::JournalEvent;
use crate::journal::Value;

/// The root container key holding the per-document maps.
pub const DOCS_ROOT: &str = "docs";

/// The envelope headroom (bytes) added to a batch's estimated size before
/// comparing against a caller's byte budget: batch encoding amortizes the
/// per-commit encodings the estimate sums, and the headroom covers the
/// constant envelope overhead of one range export.
pub const BATCH_HEADROOM_BYTES: usize = 64;

/// The error type of the spike's mapping and delivery layer.
#[derive(Debug)]
pub enum MappingError {
    /// A journal event arrived out of storage-sequence order.
    SequenceOutOfOrder {
        /// The sequence that arrived.
        got: u64,
        /// The sequence that was required to be lower.
        expected_after: u64,
    },
    /// Loro rejected an operation.
    Loro(String),
    /// A consumer-side step failed (import, pending dependencies).
    Consumer(String),
    /// A checkpoint did not validate.
    InvalidCheckpoint(String),
}

impl std::fmt::Display for MappingError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            MappingError::SequenceOutOfOrder { got, expected_after } => {
                write!(f, "journal sequence {got} is not after {expected_after}")
            }
            MappingError::Loro(message) => write!(f, "loro error: {message}"),
            MappingError::Consumer(message) => write!(f, "consumer error: {message}"),
            MappingError::InvalidCheckpoint(message) => {
                write!(f, "invalid checkpoint: {message}")
            }
        }
    }
}

impl std::error::Error for MappingError {}

impl From<crate::consumer::ConsumerError> for MappingError {
    fn from(error: crate::consumer::ConsumerError) -> Self {
        MappingError::Consumer(error.to_string())
    }
}

/// Converts a spike [`Value`] into its Loro value.
#[must_use]
pub fn to_loro_value(value: &Value) -> LoroValue {
    match value {
        Value::Null => LoroValue::Null,
        Value::Bool(b) => LoroValue::Bool(*b),
        Value::Int(i) => LoroValue::I64(*i),
        Value::Float(f) => LoroValue::Double(*f),
        Value::Str(s) => LoroValue::String(s.as_str().into()),
    }
}

/// The durable resumption identity of one consumer of this scope: the Loro
/// version-vector entry set (peer, end-counter pairs exactly as Loro's
/// version vectors represent them) plus the storage sequence the consumer
/// has absorbed.
///
/// With one writer the entry set is the single [`SERVER_PEER`] entry; the
/// spike keeps the pair set general so the checkpoint stays opaque and
/// comparable without reifying Loro types in callers.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Checkpoint {
    /// The version-vector end pairs (peer, end) the consumer holds.
    pub vv_ends: Vec<(u64, i32)>,
    /// The storage sequence the consumer has absorbed.
    pub storage_sequence: u64,
}

impl Checkpoint {
    /// The initial checkpoint: nothing absorbed.
    #[must_use]
    pub fn initial() -> Self {
        Self { vv_ends: Vec::new(), storage_sequence: 0 }
    }

    /// The end entry for a peer, or `None` when the checkpoint holds no op
    /// of that peer.
    #[must_use]
    pub fn end_for(&self, peer: u64) -> Option<i32> {
        self.vv_ends.iter().find(|(p, _)| *p == peer).map(|(_, end)| *end)
    }
}

/// One retained commit boundary on the server's checkpoint window.
#[derive(Clone, Copy, Debug)]
struct RingEntry {
    sequence: u64,
    /// The exclusive op end counter after this commit.
    end: i32,
    /// The measured wire size of this commit's own delta.
    delta_bytes: usize,
}

/// The outcome of one [`ServerDelivery::delta_since`] call.
#[derive(Debug)]
pub enum DeltaOutcome {
    /// The checkpoint already names the current state; nothing to send.
    UpToDate,
    /// A delta batch the consumer applies incrementally.
    Delta {
        /// The encoded delta bytes (the wire payload of the batch).
        bytes: Vec<u8>,
        /// The checkpoint after applying this batch.
        next: Checkpoint,
        /// Whether the batch fits the caller's byte budget.
        within_budget: bool,
        /// How many journal commits the batch carries.
        commits: u64,
        /// True only when a single commit's delta alone exceeded the
        /// budget and the batch carries exactly that one commit.
        single_commit_oversize: bool,
    },
    /// The checkpoint is older than the server's retained floor (the
    /// checkpoint window has overflowed or history was compacted past it);
    /// the consumer must resync from a snapshot.
    BehindRetainedFloor {
        /// The oldest checkpoint the server can still serve deltas from.
        floor: Checkpoint,
    },
}

/// The server-side delivery layer: materializes journal events into the
/// Loro document and serves byte-budgeted deltas from checkpoints.
///
/// Batch assembly never exports an unknown-size range: each applied
/// commit's own delta is measured once at apply time, a batch is accepted
/// while the sum of measured commit sizes plus
/// [`BATCH_HEADROOM_BYTES`] fits the budget, and only the accepted range
/// is exported. Transient assembly memory is therefore bounded by the
/// budget plus one oversized single commit, never by the consumer's
/// un-resynced backlog.
pub struct ServerDelivery {
    doc: LoroDoc,
    last_sequence: u64,
    // Retained commit boundaries, oldest first, bounded by the capacity;
    // the oldest entry is the checkpoint-window floor once full.
    checkpoint_ring: VecDeque<RingEntry>,
    ring_capacity: usize,
}

impl ServerDelivery {
    /// Creates a delivery layer for one scope with a retained-checkpoint
    /// window of `ring_capacity` commits.
    ///
    /// # Errors
    ///
    /// Returns [`MappingError::Loro`] if the fixed server peer id cannot
    /// be set.
    pub fn new(ring_capacity: usize) -> Result<Self, MappingError> {
        let doc = LoroDoc::new();
        doc.set_peer_id(SERVER_PEER).map_err(|e| MappingError::Loro(e.to_string()))?;
        Ok(Self {
            doc,
            last_sequence: 0,
            checkpoint_ring: VecDeque::new(),
            ring_capacity: ring_capacity.max(1),
        })
    }

    /// The current server checkpoint.
    #[must_use]
    pub fn checkpoint(&self) -> Checkpoint {
        Checkpoint {
            vv_ends: self.doc.oplog_vv().iter().map(|(p, c)| (*p, *c)).collect(),
            storage_sequence: self.last_sequence,
        }
    }

    /// The oldest checkpoint from which this server can still serve a
    /// delta. The floor is zero while the full history is retained (the
    /// window has not overflowed and no compaction has happened); it
    /// rises to the oldest retained boundary once the window overflows or
    /// [`ServerDelivery::compact_to_shallow`] has run.
    #[must_use]
    pub fn retained_floor(&self) -> Checkpoint {
        let window_full = self.checkpoint_ring.len() >= self.ring_capacity;
        let mut floor_end = 0;
        if window_full && let Some(entry) = self.checkpoint_ring.front() {
            floor_end = entry.end;
        }
        // A shallow doc retains history only since the shallow root.
        for (peer, end) in self.doc.shallow_since_vv().iter() {
            if *peer == SERVER_PEER {
                floor_end = floor_end.max(*end);
            }
        }
        let floor_sequence = if window_full {
            self.checkpoint_ring.front().map_or(0, |entry| entry.sequence.saturating_sub(1))
        } else {
            0
        };
        Checkpoint {
            vv_ends: if floor_end > 0 { vec![(SERVER_PEER, floor_end)] } else { Vec::new() },
            storage_sequence: floor_sequence,
        }
    }

    /// Materializes one journal event server-side and returns the new
    /// checkpoint.
    ///
    /// The event's own delta is measured here (one single-commit range
    /// export) so later batch assembly never has to export an unknown
    /// size; that measurement is part of the spike's byte-budget evidence,
    /// not a performance claim.
    ///
    /// # Errors
    ///
    /// Returns [`MappingError::SequenceOutOfOrder`] when the event's
    /// storage sequence is not strictly after the last applied one, and
    /// [`MappingError::Loro`] when Loro rejects an operation.
    pub fn apply(&mut self, event: &JournalEvent) -> Result<Checkpoint, MappingError> {
        if event.storage_sequence <= self.last_sequence {
            return Err(MappingError::SequenceOutOfOrder {
                got: event.storage_sequence,
                expected_after: self.last_sequence,
            });
        }
        let prev_end = self.current_end();
        apply_event(&self.doc, event)?;
        self.last_sequence = event.storage_sequence;
        let end = self.current_end();
        if end > prev_end {
            let delta_bytes = self.export_range(prev_end, end)?.len();
            self.checkpoint_ring.push_back(RingEntry {
                sequence: event.storage_sequence,
                end,
                delta_bytes,
            });
            while self.checkpoint_ring.len() > self.ring_capacity {
                self.checkpoint_ring.pop_front();
            }
        }
        Ok(self.checkpoint())
    }

    /// Serves one byte-budgeted delta batch for a consumer at `from`.
    ///
    /// # Errors
    ///
    /// Returns [`MappingError::InvalidCheckpoint`] for a checkpoint naming
    /// a foreign peer or a future end, and [`MappingError::Loro`] when an
    /// export fails.
    pub fn delta_since(
        &self,
        from: &Checkpoint,
        budget_bytes: usize,
    ) -> Result<DeltaOutcome, MappingError> {
        let from_end = self.validate_checkpoint(from)?;
        let current_end = self.current_end();
        if from_end >= current_end {
            return Ok(DeltaOutcome::UpToDate);
        }
        let floor_end = self.retained_floor().end_for(SERVER_PEER).unwrap_or(0);
        if from_end < floor_end {
            return Ok(DeltaOutcome::BehindRetainedFloor { floor: self.retained_floor() });
        }

        // Walk retained commits from the consumer's position, accepting
        // while the measured sizes fit the budget. The first commit is
        // always accepted, so a single commit larger than the budget is
        // delivered alone and flagged.
        let mut accepted: Option<RingEntry> = None;
        let mut commits = 0u64;
        let mut estimated = BATCH_HEADROOM_BYTES;
        for entry in self.checkpoint_ring.iter().filter(|entry| entry.end > from_end) {
            if accepted.is_some() && estimated.saturating_add(entry.delta_bytes) > budget_bytes {
                break;
            }
            estimated += entry.delta_bytes;
            accepted = Some(*entry);
            commits += 1;
        }
        let Some(accepted_entry) = accepted else {
            // The consumer's end is behind the current end but no retained
            // commit follows it: history was compacted past this position.
            return Ok(DeltaOutcome::BehindRetainedFloor { floor: self.retained_floor() });
        };

        let bytes = self.export_range(from_end, accepted_entry.end)?;
        Ok(DeltaOutcome::Delta {
            within_budget: bytes.len() <= budget_bytes,
            single_commit_oversize: commits == 1 && bytes.len() > budget_bytes,
            commits,
            bytes,
            next: Checkpoint {
                vv_ends: vec![(SERVER_PEER, accepted_entry.end)],
                storage_sequence: accepted_entry.sequence,
            },
        })
    }

    /// Compacts the server's retained history to a shallow snapshot at its
    /// current version: after this, deltas are served only from the new
    /// floor, and older consumers receive
    /// [`DeltaOutcome::BehindRetainedFloor`].
    ///
    /// # Errors
    ///
    /// Returns [`MappingError::Loro`] on export or import failure.
    pub fn compact_to_shallow(&mut self) -> Result<(), MappingError> {
        let current_end = self.current_end();
        if current_end == 0 {
            return Ok(());
        }
        let shallow = self
            .doc
            .export(ExportMode::shallow_snapshot_owned(Frontiers::from_id(ID::new(
                SERVER_PEER,
                current_end - 1,
            ))))
            .map_err(|e| MappingError::Loro(e.to_string()))?;
        let compacted = LoroDoc::new();
        compacted.set_peer_id(SERVER_PEER).map_err(|e| MappingError::Loro(e.to_string()))?;
        compacted.import(&shallow).map_err(|e| MappingError::Loro(e.to_string()))?;
        self.doc = compacted;
        Ok(())
    }

    /// The full-snapshot bytes (state plus history) for resync.
    ///
    /// # Errors
    ///
    /// Returns [`MappingError::Loro`] when the export fails.
    pub fn snapshot_bytes(&self) -> Result<Vec<u8>, MappingError> {
        self.doc.export(ExportMode::Snapshot).map_err(|e| MappingError::Loro(e.to_string()))
    }

    /// The server document's deep value (the spike's observation surface).
    #[must_use]
    pub fn deep_value(&self) -> LoroValue {
        self.doc.get_deep_value()
    }

    /// The server document (measurement access only).
    #[must_use]
    pub fn doc(&self) -> &LoroDoc {
        &self.doc
    }

    fn current_end(&self) -> i32 {
        self.doc.oplog_vv().get_last(SERVER_PEER).map_or(0, |c| c + 1)
    }

    fn export_range(&self, from_end: i32, till_end: i32) -> Result<Vec<u8>, MappingError> {
        if till_end <= from_end {
            return Ok(Vec::new());
        }
        self.doc
            .export(ExportMode::UpdatesInRange {
                spans: vec![IdSpan::new(SERVER_PEER, from_end, till_end)].into(),
            })
            .map_err(|e| MappingError::Loro(e.to_string()))
    }

    fn validate_checkpoint(&self, from: &Checkpoint) -> Result<i32, MappingError> {
        for (peer, _) in &from.vv_ends {
            if *peer != SERVER_PEER {
                return Err(MappingError::InvalidCheckpoint(format!(
                    "checkpoint names foreign peer {peer}"
                )));
            }
        }
        let from_end = from.end_for(SERVER_PEER).unwrap_or(0);
        if from_end > self.current_end() {
            return Err(MappingError::InvalidCheckpoint(format!(
                "checkpoint end {from_end} is ahead of the server end {}",
                self.current_end()
            )));
        }
        if from.storage_sequence > self.last_sequence {
            return Err(MappingError::InvalidCheckpoint(format!(
                "checkpoint sequence {} is ahead of the server sequence {}",
                from.storage_sequence, self.last_sequence
            )));
        }
        Ok(from_end)
    }
}

/// Applies one journal event to a server document (one Loro commit).
///
/// # Errors
///
/// Returns [`MappingError::Loro`] when Loro rejects an operation.
pub fn apply_event(doc: &LoroDoc, event: &JournalEvent) -> Result<(), MappingError> {
    let root = doc.get_map(DOCS_ROOT);
    match &event.change {
        JournalChange::Put { body } => {
            let entry: LoroMap = root
                .ensure_mergeable_map(&event.document_id)
                .map_err(|e| MappingError::Loro(e.to_string()))?;
            for (key, value) in body {
                entry
                    .insert(key, to_loro_value(value))
                    .map_err(|e| MappingError::Loro(e.to_string()))?;
            }
            // A put replaces the body: delete fields the new body drops.
            let mut existing: Vec<String> = Vec::new();
            entry.for_each(|key, _| existing.push(key.to_string()));
            for key in existing {
                if !body.contains_key(&key) {
                    entry.delete(&key).map_err(|e| MappingError::Loro(e.to_string()))?;
                }
            }
        }
        JournalChange::Delete => {
            // The tombstone: the root-map key delete removes the document
            // from every consumer's deep value while keeping the delete
            // itself replayable for late resync.
            root.delete(&event.document_id).map_err(|e| MappingError::Loro(e.to_string()))?;
        }
    }
    doc.commit();
    Ok(())
}

/// The documents a deep value holds, as (document id, sorted field text)
/// pairs — the spike's deterministic convergence observation helper.
#[must_use]
pub fn documents_of(value: &LoroValue) -> BTreeMap<String, String> {
    let mut documents = BTreeMap::new();
    let LoroValue::Map(root) = value else {
        return documents;
    };
    let Some(LoroValue::Map(docs)) = root.get(DOCS_ROOT) else {
        return documents;
    };
    for (id, doc_value) in docs.iter() {
        let LoroValue::Map(fields) = doc_value else {
            continue;
        };
        let mut rendered: Vec<String> =
            fields.iter().map(|(k, v)| Value::encode_field(k, &value_of(v))).collect();
        rendered.sort_unstable();
        documents.insert(id.clone(), rendered.join(","));
    }
    documents
}

#[allow(clippy::match_same_arms)] // the wildcard keeps unknown Loro kinds null
fn value_of(value: &LoroValue) -> Value {
    match value {
        LoroValue::Null => Value::Null,
        LoroValue::Bool(b) => Value::Bool(*b),
        LoroValue::I64(i) => Value::Int(*i),
        LoroValue::Double(f) => Value::Float(*f),
        LoroValue::String(s) => Value::Str(s.to_string()),
        _ => Value::Null,
    }
}
