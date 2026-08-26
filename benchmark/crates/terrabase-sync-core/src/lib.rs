//! `terrabase-sync-core`: the `TerraBaseV2` sync-core seam.
//!
//! This crate is the engine-side half of the client sync channel behind
//! the typed frame/checkpoint contract (ticket SE-02, GO decision
//! recorded in `docs/spike/se02-base-engine-spike.md`). It wraps the
//! adopted base engine — Loro — and exposes only what TBEP's
//! sync-delivery frames consume:
//!
//! - [`SyncCore::apply`] materializes committed journal events into the
//!   scope's Loro document. The engine server is the single writer (the
//!   fixed [`SERVER_PEER_ID`]); the op order is the journal order.
//! - [`SyncCore::delta_since`] serves byte-budgeted delta batches from a
//!   [`SyncCheckpoint`], never exporting an unknown-size range, so batch
//!   assembly memory is bounded by the configured budget and not by a
//!   consumer's backlog.
//! - [`SyncCore::resync_snapshot`] answers consumers behind the retained
//!   floor (initial sync, or after history compaction).
//! - [`SyncFrame`] is the typed, versioned envelope unit carrying
//!   correlation and causation identity and a content digest over its
//!   body, in the shape the glossary's Sync Frame entry fixes.
//!
//! Scope limits, stated exactly:
//!
//! - Server-authoritative only (PD-2026-08-26-05 D1/D2): there is no
//!   client write path in this crate. SDK sync clients implement
//!   `terrascale.sync.v2` natively (PD-2026-08-26-12) and never
//!   multi-master the document.
//! - The frame digest is a correlation-integrity digest (FNV-1a 64), not
//!   a cryptographic guarantee; wire integrity is TBEP/SE-04's contract.
//! - This crate is a standalone workspace member by design: wiring it
//!   into the engine workspace is an integrator action named by the SE-02
//!   decision record.
//! - No claim of performance is made or implied; the spike's
//!   bytes-on-wire observations are recorded under
//!   `docs/spike/se02-prototype-evidence.md` (the tracked copy; the
//!   untracked mirror `evidence/spike/` stays local until the integrator
//!   publishes reviewed receipts).
//!
//! The [`protocol`] module is the `terrascale.sync.v2` client-channel
//! wire vocabulary (ticket SE-04) built on this substrate; its wire
//! spec is `docs/sync/terrascale-sync-v2.md` and its golden conformance
//! corpus lives under `vectors/terrascale.sync.v2/`.

use std::collections::BTreeMap;
use std::collections::VecDeque;

use loro::{ExportMode, Frontiers, ID, IdSpan, LoroDoc, LoroMap, LoroValue};

pub mod protocol;

/// The contract identity every frame and checkpoint carries. Unknown
/// versions fail closed.
pub const SYNC_CORE_CONTRACT: &str = "terrabase.sync-core/1";

/// The fixed Loro peer id of the engine server's materializer for every
/// scope. Server-authoritative means exactly one writer per scope; the
/// constant peer id is also what makes materialization deterministic.
pub const SERVER_PEER_ID: u64 = 1;

/// The root container key holding the per-document maps.
const DOCS_ROOT: &str = "docs";

/// The envelope headroom (bytes) added to a batch's estimated size before
/// comparing against the configured byte budget: range-export envelopes
/// amortize the per-commit encodings the estimate sums, and this covers
/// the constant overhead of one range export.
const BATCH_HEADROOM_BYTES: usize = 64;

/// The error type of the sync-core seam.
#[derive(Debug, PartialEq)]
pub enum SyncCoreError {
    /// A journal commit arrived out of storage-sequence order.
    SequenceOutOfOrder {
        /// The sequence that arrived.
        got: u64,
        /// The sequence that was required to be lower.
        expected_after: u64,
    },
    /// A checkpoint named a peer other than the server's.
    ForeignPeer(u64),
    /// A checkpoint named a version the server has not reached.
    CheckpointAheadOfServer {
        /// The end counter the checkpoint claimed.
        claimed_end: i32,
        /// The server's current end counter.
        server_end: i32,
    },
    /// A checkpoint or frame carried an unknown encoding version.
    UnknownCheckpointVersion(u8),
    /// A frame could not be decoded.
    InvalidFrame(&'static str),
    /// The wrapped base engine rejected an operation.
    BaseEngine(String),
}

impl std::fmt::Display for SyncCoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SyncCoreError::SequenceOutOfOrder { got, expected_after } => {
                write!(f, "journal sequence {got} is not after {expected_after}")
            }
            SyncCoreError::ForeignPeer(peer) => {
                write!(f, "checkpoint names foreign peer {peer}")
            }
            SyncCoreError::CheckpointAheadOfServer { claimed_end, server_end } => {
                write!(f, "checkpoint end {claimed_end} is ahead of the server end {server_end}")
            }
            SyncCoreError::UnknownCheckpointVersion(version) => {
                write!(f, "unknown checkpoint encoding version {version}")
            }
            SyncCoreError::InvalidFrame(reason) => write!(f, "invalid frame: {reason}"),
            SyncCoreError::BaseEngine(message) => write!(f, "base engine error: {message}"),
        }
    }
}

impl std::error::Error for SyncCoreError {}

/// The identity of one materialized sync scope: one tenant's collection.
///
/// One [`SyncCore`] owns exactly one scope; the scope is carried on the
/// core (not per call) so a core instance can never mix tenants.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SyncScope {
    tenant: String,
    collection: String,
}

impl SyncScope {
    /// Creates a scope identity.
    #[must_use]
    pub fn new(tenant: impl Into<String>, collection: impl Into<String>) -> Self {
        Self { tenant: tenant.into(), collection: collection.into() }
    }

    /// The tenant identity of the scope.
    #[must_use]
    pub fn tenant(&self) -> &str {
        &self.tenant
    }

    /// The collection identity of the scope.
    #[must_use]
    pub fn collection(&self) -> &str {
        &self.collection
    }
}

/// The typed value model of a document body at this seam: the JSON-ish
/// scalar subset a schema-free collection body carries today. Composite
/// values stay document bodies' concern; the seam maps one nesting level
/// (document -> fields).
#[derive(Clone, Debug, PartialEq)]
pub enum SyncValue {
    /// A null field.
    Null,
    /// A boolean field.
    Bool(bool),
    /// A signed integer field.
    Int(i64),
    /// A double field.
    Float(f64),
    /// A string field.
    Str(String),
}

impl SyncValue {
    /// Converts into the base engine's value.
    fn to_loro(&self) -> LoroValue {
        match self {
            SyncValue::Null => LoroValue::Null,
            SyncValue::Bool(b) => LoroValue::Bool(*b),
            SyncValue::Int(i) => LoroValue::I64(*i),
            SyncValue::Float(x) => LoroValue::Double(*x),
            SyncValue::Str(s) => LoroValue::String(s.as_str().into()),
        }
    }
}

/// One committed change as the seam consumes it from the journal side
/// (the Change Event Backbone's commit facts, SE-03).
#[derive(Clone, Debug)]
pub struct JournalCommit {
    /// The storage sequence ordering this commit. Strictly increasing.
    pub storage_sequence: u64,
    /// The stable operation identity of the commit (at-least-once anchor).
    pub operation_id: String,
    /// The identity of the document within the scope's collection.
    pub document_id: String,
    /// What happened to the document.
    pub change: JournalChange,
}

/// The shape of a committed document change.
#[derive(Clone, Debug)]
pub enum JournalChange {
    /// The document body is put (replaced) with these fields; fields the
    /// body drops are deleted (whole-body replace semantics).
    Put {
        /// The full new body of the document.
        body: BTreeMap<String, SyncValue>,
    },
    /// The document is deleted: a tombstone is materialized and every
    /// consumer's deep value drops the document.
    Delete,
}

fn le_u64(bytes: &[u8]) -> Result<u64, SyncCoreError> {
    bytes
        .try_into()
        .map(u64::from_le_bytes)
        .map_err(|_| SyncCoreError::InvalidFrame("byte run too short"))
}

fn le_i32(bytes: &[u8]) -> Result<i32, SyncCoreError> {
    bytes
        .try_into()
        .map(i32::from_le_bytes)
        .map_err(|_| SyncCoreError::InvalidFrame("byte run too short"))
}

fn le_u32(bytes: &[u8]) -> Result<u32, SyncCoreError> {
    bytes
        .try_into()
        .map(u32::from_le_bytes)
        .map_err(|_| SyncCoreError::InvalidFrame("byte run too short"))
}

/// The durable resumption identity of one Sync Session over this scope:
/// the base engine's version-vector end pairs plus the storage sequence
/// the session has absorbed.
///
/// With one writer the pair set is the single [`SERVER_PEER_ID`] entry.
/// The encoding is versioned and fails closed on unknown versions.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SyncCheckpoint {
    /// The version-vector end pairs (peer, end) the session holds, in
    /// canonical ascending peer order.
    vv_ends: Vec<(u64, i32)>,
    /// The storage sequence the session has absorbed.
    pub storage_sequence: u64,
}

impl SyncCheckpoint {
    /// The initial checkpoint: nothing absorbed.
    #[must_use]
    pub fn initial() -> Self {
        Self { vv_ends: Vec::new(), storage_sequence: 0 }
    }

    /// Builds a checkpoint from explicit end pairs (canonicalized to
    /// ascending peer order), the constructor consumers and tests use to
    /// report their resumption position.
    #[must_use]
    pub fn from_ends(mut vv_ends: Vec<(u64, i32)>, storage_sequence: u64) -> Self {
        vv_ends.sort_unstable();
        vv_ends.dedup();
        Self { vv_ends, storage_sequence }
    }

    /// The end entry for a peer, or `None` when the checkpoint holds no
    /// op of that peer.
    #[must_use]
    pub fn end_for(&self, peer: u64) -> Option<i32> {
        self.vv_ends.iter().find(|(p, _)| *p == peer).map(|(_, end)| *end)
    }

    /// The canonical version-vector pairs (ascending peer order).
    #[must_use]
    pub fn ends(&self) -> &[(u64, i32)] {
        &self.vv_ends
    }

    /// The versioned byte encoding of the checkpoint: version byte,
    /// storage sequence, pair count, then (peer, end) pairs, all
    /// little-endian. Deterministic across runs and platforms.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(11 + self.vv_ends.len() * 12);
        bytes.push(1);
        bytes.extend_from_slice(&self.storage_sequence.to_le_bytes());
        bytes.extend_from_slice(
            &u32::try_from(self.vv_ends.len()).unwrap_or(u32::MAX).to_le_bytes(),
        );
        for (peer, end) in &self.vv_ends {
            bytes.extend_from_slice(&peer.to_le_bytes());
            bytes.extend_from_slice(&end.to_le_bytes());
        }
        bytes
    }

    /// Decodes a checkpoint, failing closed on unknown versions and
    /// truncated input.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::UnknownCheckpointVersion`] for any
    /// leading version byte other than `1`, and
    /// [`SyncCoreError::InvalidFrame`] for truncated input.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, SyncCoreError> {
        let bad = || SyncCoreError::InvalidFrame("truncated checkpoint encoding");
        if bytes.first() != Some(&1) {
            return Err(SyncCoreError::UnknownCheckpointVersion(
                bytes.first().copied().unwrap_or(0),
            ));
        }
        let mut rest = &bytes[1..];
        let mut take = |n: usize| -> Result<&[u8], SyncCoreError> {
            if rest.len() < n {
                Err(bad())
            } else {
                let (head, tail) = rest.split_at(n);
                rest = tail;
                Ok(head)
            }
        };
        let storage_sequence = le_u64(take(8)?)?;
        let count = le_u32(take(4)?)? as usize;
        let mut vv_ends = Vec::with_capacity(count.min(64));
        for _ in 0..count {
            let peer = le_u64(take(8)?)?;
            let end = le_i32(take(4)?)?;
            vv_ends.push((peer, end));
        }
        Ok(Self { vv_ends, storage_sequence })
    }
}

/// The configuration of one [`SyncCore`].
#[derive(Clone, Debug)]
pub struct SyncCoreConfig {
    /// The byte budget one delta batch may carry. A single commit whose
    /// delta alone exceeds the budget is delivered as a batch of exactly
    /// one commit with [`SyncBatch::Delta`]'s oversize flag set — never
    /// silently, never merged with others.
    pub batch_byte_budget: usize,
    /// How many recent commit boundaries the core retains for batch
    /// assembly. A consumer older than the retained floor is answered
    /// with [`SyncBatch::BehindRetainedFloor`] and resyncs from a
    /// snapshot (checkpoint-bounded retransmission).
    pub checkpoint_window: usize,
}

impl Default for SyncCoreConfig {
    fn default() -> Self {
        Self { batch_byte_budget: 64 * 1024, checkpoint_window: 4096 }
    }
}

/// One retained commit boundary on the core's checkpoint window.
#[derive(Clone, Copy, Debug)]
struct CommitBoundary {
    sequence: u64,
    end: i32,
    delta_bytes: usize,
}

/// The outcome of one [`SyncCore::delta_since`] call.
#[derive(Debug)]
pub enum SyncBatch {
    /// The checkpoint already names the current state; nothing to send.
    UpToDate,
    /// A delta batch the session applies incrementally.
    Delta {
        /// The encoded delta bytes (the batch's payload).
        bytes: Vec<u8>,
        /// The checkpoint after applying this batch.
        next: SyncCheckpoint,
        /// Whether the batch fits the configured byte budget.
        within_budget: bool,
        /// How many journal commits the batch carries.
        commits: u64,
        /// True only when a single commit's delta alone exceeded the
        /// budget and the batch carries exactly that one commit.
        single_commit_oversize: bool,
    },
    /// The checkpoint is older than the retained floor; the session must
    /// resync from [`SyncCore::resync_snapshot`].
    BehindRetainedFloor {
        /// The oldest checkpoint the core can still serve deltas from.
        floor: SyncCheckpoint,
    },
}

/// A full-state resync answer.
#[derive(Debug)]
pub struct SyncSnapshot {
    /// The encoded snapshot bytes (state plus history).
    pub bytes: Vec<u8>,
    /// The checkpoint the snapshot corresponds to.
    pub checkpoint: SyncCheckpoint,
}

/// The engine-side sync core for one scope.
pub struct SyncCore {
    scope: SyncScope,
    config: SyncCoreConfig,
    doc: LoroDoc,
    last_sequence: u64,
    boundaries: VecDeque<CommitBoundary>,
}

impl SyncCore {
    /// Creates the sync core for one scope.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::BaseEngine`] if the fixed server peer id
    /// cannot be set.
    pub fn new(scope: SyncScope, config: SyncCoreConfig) -> Result<Self, SyncCoreError> {
        let doc = LoroDoc::new();
        doc.set_peer_id(SERVER_PEER_ID).map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
        Ok(Self { scope, config, doc, last_sequence: 0, boundaries: VecDeque::new() })
    }

    /// The scope this core serves.
    #[must_use]
    pub fn scope(&self) -> &SyncScope {
        &self.scope
    }

    /// The core's configuration (the budget and window in force).
    #[must_use]
    pub fn config(&self) -> &SyncCoreConfig {
        &self.config
    }

    /// Materializes one committed journal event and returns the new
    /// checkpoint. The event's own delta is measured here (one
    /// single-commit range export) so batch assembly never exports an
    /// unknown-size range later.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::SequenceOutOfOrder`] when the event's
    /// storage sequence is not strictly after the last applied one, and
    /// [`SyncCoreError::BaseEngine`] when the base engine rejects an
    /// operation.
    pub fn apply(&mut self, commit: &JournalCommit) -> Result<SyncCheckpoint, SyncCoreError> {
        if commit.storage_sequence <= self.last_sequence {
            return Err(SyncCoreError::SequenceOutOfOrder {
                got: commit.storage_sequence,
                expected_after: self.last_sequence,
            });
        }
        let prev_end = self.current_end();
        self.materialize(commit)?;
        self.last_sequence = commit.storage_sequence;
        let end = self.current_end();
        if end > prev_end {
            let delta_bytes = self.export_range(prev_end, end)?.len();
            self.boundaries.push_back(CommitBoundary {
                sequence: commit.storage_sequence,
                end,
                delta_bytes,
            });
            while self.boundaries.len() > self.config.checkpoint_window.max(1) {
                self.boundaries.pop_front();
            }
        }
        Ok(self.checkpoint())
    }

    /// The current checkpoint of the core.
    #[must_use]
    pub fn checkpoint(&self) -> SyncCheckpoint {
        let mut vv_ends: Vec<(u64, i32)> =
            self.doc.oplog_vv().iter().map(|(p, c)| (*p, *c)).collect();
        vv_ends.sort_unstable();
        SyncCheckpoint { vv_ends, storage_sequence: self.last_sequence }
    }

    /// The oldest checkpoint this core can still serve a delta from:
    /// zero (full history) while the window has not overflowed, the
    /// oldest retained boundary afterwards. History compaction raises
    /// the floor the same way, because a shallow (compacted) document
    /// retains history only since its shallow root.
    #[must_use]
    pub fn retained_floor(&self) -> SyncCheckpoint {
        let window_full = self.boundaries.len() >= self.config.checkpoint_window.max(1);
        let mut floor_end = 0;
        let mut floor_sequence = 0;
        if window_full && let Some(entry) = self.boundaries.front() {
            floor_end = entry.end;
            floor_sequence = entry.sequence.saturating_sub(1);
        }
        // A shallow doc retains history only since the shallow root, so
        // the floor can never sit below it (the spike's
        // `shallow_since_vv` adjustment).
        for (peer, end) in self.doc.shallow_since_vv().iter() {
            if *peer == SERVER_PEER_ID {
                floor_end = floor_end.max(*end);
            }
        }
        SyncCheckpoint {
            vv_ends: if floor_end > 0 { vec![(SERVER_PEER_ID, floor_end)] } else { Vec::new() },
            storage_sequence: floor_sequence,
        }
    }

    /// Serves one byte-budgeted delta batch for a session at `from`.
    ///
    /// # Errors
    ///
    /// Returns the typed checkpoint errors (foreign peer, ahead of
    /// server) and [`SyncCoreError::BaseEngine`] when an export fails.
    pub fn delta_since(&self, from: &SyncCheckpoint) -> Result<SyncBatch, SyncCoreError> {
        let from_end = self.validate_checkpoint(from)?;
        let current_end = self.current_end();
        if from_end >= current_end {
            return Ok(SyncBatch::UpToDate);
        }
        let floor = self.retained_floor();
        let floor_end = floor.end_for(SERVER_PEER_ID).unwrap_or(0);
        if from_end < floor_end {
            return Ok(SyncBatch::BehindRetainedFloor { floor });
        }

        // Walk retained boundaries from the session's position,
        // accepting while the measured sizes fit the budget. The first
        // commit is always accepted so an oversized single commit is
        // delivered alone and flagged.
        let mut accepted: Option<CommitBoundary> = None;
        let mut commits = 0u64;
        let mut estimated = BATCH_HEADROOM_BYTES;
        for boundary in self.boundaries.iter().filter(|b| b.end > from_end) {
            if accepted.is_some()
                && estimated.saturating_add(boundary.delta_bytes) > self.config.batch_byte_budget
            {
                break;
            }
            estimated += boundary.delta_bytes;
            accepted = Some(*boundary);
            commits += 1;
        }
        let Some(accepted) = accepted else {
            return Ok(SyncBatch::BehindRetainedFloor { floor });
        };

        let bytes = self.export_range(from_end, accepted.end)?;
        Ok(SyncBatch::Delta {
            within_budget: bytes.len() <= self.config.batch_byte_budget,
            single_commit_oversize: commits == 1 && bytes.len() > self.config.batch_byte_budget,
            commits,
            bytes,
            next: SyncCheckpoint {
                vv_ends: vec![(SERVER_PEER_ID, accepted.end)],
                storage_sequence: accepted.sequence,
            },
        })
    }

    /// Serves a full snapshot for initial sync or forced resync.
    ///
    /// A resync is a full-state answer: a session behind the retained
    /// floor adopts the snapshot as its state (a fresh document) instead
    /// of merging it into retained history — after
    /// [`SyncCore::compact_history`] the snapshot is shallow since the
    /// new floor, and merging it with pre-compaction history would keep
    /// superseded values in place.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::BaseEngine`] when the export fails.
    pub fn resync_snapshot(&self) -> Result<SyncSnapshot, SyncCoreError> {
        let bytes = self
            .doc
            .export(ExportMode::Snapshot)
            .map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
        Ok(SyncSnapshot { bytes, checkpoint: self.checkpoint() })
    }

    /// Compacts retained history to a shallow snapshot at the current
    /// version: afterwards deltas serve only from the new floor and older
    /// sessions receive [`SyncBatch::BehindRetainedFloor`].
    ///
    /// The floor rises to the compacted document's shallow root (see
    /// [`SyncCore::retained_floor`]), which is the oldest delta the
    /// document can still export.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::BaseEngine`] on export or import failure.
    pub fn compact_history(&mut self) -> Result<(), SyncCoreError> {
        let current_end = self.current_end();
        if current_end == 0 {
            return Ok(());
        }
        let shallow = self
            .doc
            .export(ExportMode::shallow_snapshot_owned(Frontiers::from_id(ID::new(
                SERVER_PEER_ID,
                current_end - 1,
            ))))
            .map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
        let compacted = LoroDoc::new();
        compacted
            .set_peer_id(SERVER_PEER_ID)
            .map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
        compacted.import(&shallow).map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
        self.doc = compacted;
        Ok(())
    }

    /// The core's observation surface: the deep value of the materialized
    /// scope (measurement and verification only).
    #[must_use]
    pub fn deep_value(&self) -> LoroValue {
        self.doc.get_deep_value()
    }

    fn materialize(&self, commit: &JournalCommit) -> Result<(), SyncCoreError> {
        let root = self.doc.get_map(DOCS_ROOT);
        match &commit.change {
            JournalChange::Put { body } => {
                let entry: LoroMap = root
                    .ensure_mergeable_map(&commit.document_id)
                    .map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
                for (key, value) in body {
                    entry
                        .insert(key, value.to_loro())
                        .map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
                }
                let mut existing: Vec<String> = Vec::new();
                entry.for_each(|key, _| existing.push(key.to_string()));
                for key in existing {
                    if !body.contains_key(&key) {
                        entry.delete(&key).map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
                    }
                }
            }
            JournalChange::Delete => {
                root.delete(&commit.document_id)
                    .map_err(|e| SyncCoreError::BaseEngine(e.to_string()))?;
            }
        }
        self.doc.commit();
        Ok(())
    }

    fn current_end(&self) -> i32 {
        self.doc.oplog_vv().get_last(SERVER_PEER_ID).map_or(0, |c| c + 1)
    }

    fn export_range(&self, from_end: i32, till_end: i32) -> Result<Vec<u8>, SyncCoreError> {
        if till_end <= from_end {
            return Ok(Vec::new());
        }
        self.doc
            .export(ExportMode::UpdatesInRange {
                spans: vec![IdSpan::new(SERVER_PEER_ID, from_end, till_end)].into(),
            })
            .map_err(|e| SyncCoreError::BaseEngine(e.to_string()))
    }

    fn validate_checkpoint(&self, from: &SyncCheckpoint) -> Result<i32, SyncCoreError> {
        for (peer, _) in from.ends() {
            if *peer != SERVER_PEER_ID {
                return Err(SyncCoreError::ForeignPeer(*peer));
            }
        }
        let from_end = from.end_for(SERVER_PEER_ID).unwrap_or(0);
        let current_end = self.current_end();
        if from_end > current_end {
            return Err(SyncCoreError::CheckpointAheadOfServer {
                claimed_end: from_end,
                server_end: current_end,
            });
        }
        if from.storage_sequence > self.last_sequence {
            return Err(SyncCoreError::CheckpointAheadOfServer {
                claimed_end: from_end,
                server_end: current_end,
            });
        }
        Ok(from_end)
    }
}

/// The correlation or causation identity of one [`SyncFrame`]. Monotonic
/// per sender; opaque here.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FrameId(u64);

impl FrameId {
    /// Creates a frame identity from its numeric form.
    #[must_use]
    pub fn new(value: u64) -> Self {
        Self(value)
    }

    /// The numeric form of the identity.
    #[must_use]
    pub fn value(self) -> u64 {
        self.0
    }
}

/// The correlation-integrity digest of a frame body: FNV-1a 64 over the
/// body's canonical digest input. Not a cryptographic guarantee; wire
/// integrity belongs to TBEP (SE-04).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FrameDigest(u64);

impl FrameDigest {
    /// The digest of empty input (the FNV-1a offset basis).
    fn of(bytes: &[u8]) -> Self {
        let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
        for byte in bytes {
            hash ^= u64::from(*byte);
            hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
        }
        Self(hash)
    }

    /// The numeric form of the digest.
    #[must_use]
    pub fn value(self) -> u64 {
        self.0
    }
}

/// The payload a [`SyncFrame`] carries.
#[derive(Debug)]
pub enum FrameBody {
    /// A delta batch (the delivery payload).
    Delta {
        /// The encoded delta bytes.
        bytes: Vec<u8>,
        /// The checkpoint after applying them.
        next: SyncCheckpoint,
    },
    /// A full snapshot answer (initial sync or forced resync).
    Snapshot {
        /// The encoded snapshot bytes.
        bytes: Vec<u8>,
        /// The checkpoint the snapshot corresponds to.
        checkpoint: SyncCheckpoint,
    },
    /// A checkpoint report (session resumption position).
    Checkpoint(SyncCheckpoint),
    /// The retained-floor answer: the session must resync.
    BehindRetainedFloor {
        /// The oldest checkpoint deltas still serve from.
        floor: SyncCheckpoint,
    },
}

impl FrameBody {
    /// The body's canonical digest input: a kind tag, then the payload
    /// and checkpoint encodings.
    fn digest_input(&self) -> Vec<u8> {
        let mut input = Vec::new();
        match self {
            FrameBody::Delta { bytes, next } => {
                input.push(1);
                input.extend_from_slice(&(bytes.len() as u64).to_le_bytes());
                input.extend_from_slice(bytes);
                let checkpoint = next.to_bytes();
                input.extend_from_slice(&(checkpoint.len() as u64).to_le_bytes());
                input.extend_from_slice(&checkpoint);
            }
            FrameBody::Snapshot { bytes, checkpoint } => {
                input.push(2);
                input.extend_from_slice(&(bytes.len() as u64).to_le_bytes());
                input.extend_from_slice(bytes);
                let checkpoint = checkpoint.to_bytes();
                input.extend_from_slice(&(checkpoint.len() as u64).to_le_bytes());
                input.extend_from_slice(&checkpoint);
            }
            FrameBody::Checkpoint(checkpoint) => {
                input.push(3);
                let checkpoint = checkpoint.to_bytes();
                input.extend_from_slice(&(checkpoint.len() as u64).to_le_bytes());
                input.extend_from_slice(&checkpoint);
            }
            FrameBody::BehindRetainedFloor { floor } => {
                input.push(4);
                let floor = floor.to_bytes();
                input.extend_from_slice(&(floor.len() as u64).to_le_bytes());
                input.extend_from_slice(&floor);
            }
        }
        input
    }

    /// The correlation-integrity digest of this body.
    #[must_use]
    pub fn digest(&self) -> FrameDigest {
        FrameDigest::of(&self.digest_input())
    }
}

/// One typed, versioned envelope unit of the sync-delivery surface: the
/// shape TBEP's sync-delivery frames consume. It carries correlation and
/// causation identity and a content digest over its body; there is no
/// idempotency key because delivery frames are not mutations.
#[derive(Debug)]
pub struct SyncFrame {
    contract: &'static str,
    correlation: FrameId,
    causation: FrameId,
    digest: FrameDigest,
    body: FrameBody,
}

impl SyncFrame {
    /// Seals a body into a frame, computing the content digest.
    #[must_use]
    pub fn seal(body: FrameBody, correlation: FrameId, causation: FrameId) -> Self {
        let digest = body.digest();
        Self { contract: SYNC_CORE_CONTRACT, correlation, causation, digest, body }
    }

    /// The contract identity the frame carries.
    #[must_use]
    pub fn contract(&self) -> &'static str {
        self.contract
    }

    /// The correlation identity of the frame.
    #[must_use]
    pub fn correlation(&self) -> FrameId {
        self.correlation
    }

    /// The causation identity of the frame.
    #[must_use]
    pub fn causation(&self) -> FrameId {
        self.causation
    }

    /// The content digest the frame was sealed with.
    #[must_use]
    pub fn digest(&self) -> FrameDigest {
        self.digest
    }

    /// Whether the sealed digest still matches the body (tamper
    /// detection).
    #[must_use]
    pub fn verify(&self) -> bool {
        self.body.digest() == self.digest
    }

    /// The frame's body, by reference.
    #[must_use]
    pub fn body(&self) -> &FrameBody {
        &self.body
    }

    /// The versioned byte encoding of the frame: contract tag byte,
    /// correlation, causation, digest, body-length, then the body's
    /// canonical digest input (which begins with the body-kind tag).
    /// Unknown contract versions fail closed on decode.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let body = self.body.digest_input();
        let mut bytes = Vec::with_capacity(body.len() + 34);
        bytes.push(1);
        bytes.extend_from_slice(&self.correlation.value().to_le_bytes());
        bytes.extend_from_slice(&self.causation.value().to_le_bytes());
        bytes.extend_from_slice(&self.digest.0.to_le_bytes());
        bytes.extend_from_slice(&(body.len() as u64).to_le_bytes());
        bytes.extend_from_slice(&body);
        bytes
    }

    /// Decodes a frame, failing closed on unknown versions, truncation,
    /// and length disagreement with the actual bytes.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::InvalidFrame`] for any structural
    /// problem; digest disagreement is not a decode error — call
    /// [`SyncFrame::verify`] on the result.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, SyncCoreError> {
        let bad = SyncCoreError::InvalidFrame;
        if bytes.first() != Some(&1) {
            return Err(SyncCoreError::UnknownCheckpointVersion(
                bytes.first().copied().unwrap_or(0),
            ));
        }
        let mut rest = &bytes[1..];
        let mut take = |n: usize| -> Result<&[u8], SyncCoreError> {
            if rest.len() < n {
                Err(bad("truncated frame encoding"))
            } else {
                let (head, tail) = rest.split_at(n);
                rest = tail;
                Ok(head)
            }
        };
        let correlation = FrameId::new(le_u64(take(8)?)?);
        let causation = FrameId::new(le_u64(take(8)?)?);
        let digest = FrameDigest(le_u64(take(8)?)?);
        let length = usize::try_from(le_u64(take(8)?)?)
            .map_err(|_| bad("frame body length does not fit the platform"))?;
        let body_bytes = take(length)?;
        if !rest.is_empty() {
            return Err(bad("trailing bytes after the frame body"));
        }
        let mut input = body_bytes;
        let mut take_body = |n: usize| -> Result<&[u8], SyncCoreError> {
            if input.len() < n {
                Err(bad("truncated frame body"))
            } else {
                let (head, tail) = input.split_at(n);
                input = tail;
                Ok(head)
            }
        };
        let kind = take_body(1)?[0];
        let body = match kind {
            1 => {
                let len = usize::try_from(le_u64(take_body(8)?)?)
                    .map_err(|_| bad("length does not fit the platform"))?;
                let bytes = take_body(len)?.to_vec();
                let checkpoint_len = usize::try_from(le_u64(take_body(8)?)?)
                    .map_err(|_| bad("length does not fit the platform"))?;
                let next = SyncCheckpoint::from_bytes(take_body(checkpoint_len)?)?;
                FrameBody::Delta { bytes, next }
            }
            2 => {
                let len = usize::try_from(le_u64(take_body(8)?)?)
                    .map_err(|_| bad("length does not fit the platform"))?;
                let bytes = take_body(len)?.to_vec();
                let checkpoint_len = usize::try_from(le_u64(take_body(8)?)?)
                    .map_err(|_| bad("length does not fit the platform"))?;
                let checkpoint = SyncCheckpoint::from_bytes(take_body(checkpoint_len)?)?;
                FrameBody::Snapshot { bytes, checkpoint }
            }
            3 => {
                let len = usize::try_from(le_u64(take_body(8)?)?)
                    .map_err(|_| bad("length does not fit the platform"))?;
                FrameBody::Checkpoint(SyncCheckpoint::from_bytes(take_body(len)?)?)
            }
            4 => {
                let len = usize::try_from(le_u64(take_body(8)?)?)
                    .map_err(|_| bad("length does not fit the platform"))?;
                FrameBody::BehindRetainedFloor {
                    floor: SyncCheckpoint::from_bytes(take_body(len)?)?,
                }
            }
            _ => return Err(bad("unknown frame body kind")),
        };
        Ok(Self { contract: SYNC_CORE_CONTRACT, correlation, causation, digest, body })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn checkpoint_encoding_is_versioned_and_deterministic() {
        let checkpoint = SyncCheckpoint::from_ends(vec![(1, 9)], 42);
        let bytes = checkpoint.to_bytes();
        assert_eq!(bytes.first(), Some(&1));
        assert_eq!(SyncCheckpoint::from_bytes(&bytes), Ok(checkpoint));
    }

    #[test]
    fn frame_digest_input_is_canonical() {
        let checkpoint = SyncCheckpoint::from_ends(vec![(1, 4)], 9);
        let a = FrameBody::Checkpoint(checkpoint.clone());
        let mut b = FrameBody::Checkpoint(checkpoint);
        assert_eq!(a.digest_input(), b.digest_input());
        if let FrameBody::Checkpoint(ref mut c) = b {
            c.storage_sequence += 1;
        }
        assert_ne!(a.digest_input(), b.digest_input());
    }
}
