//! `terrabase-sync-core`: the `TerraBaseV2` sync-core seam over
//! TerraBase-native CRDT types.
//!
//! This crate is the engine-side half of the client sync channel behind
//! the typed frame/checkpoint contract. Per ADR 0024 and the sync-v2
//! replacement contract (`docs/sync/terrascale-sync-v2.md`) it contains
//! no external CRDT engine: journal events materialize into per-field
//! last-writer-wins registers from `terrabase-crdt` joined over the
//! dots, dotted versions, causal contexts, and hybrid logical clocks of
//! `terrabase-causal`. The seam exposes only what TBEP's sync-delivery
//! frames consume:
//!
//! - [`SyncCore::apply`] materializes committed journal events into the
//!   scope's native document state. The engine server is the single
//!   writer (the fixed [`SERVER_PEER_ID`] actor); the op order is the
//!   journal order.
//! - [`SyncCore::delta_since`] serves byte-budgeted delta batches from a
//!   [`SyncCheckpoint`], never exporting an unknown-size range, so batch
//!   assembly memory is bounded by the configured budget and not by a
//!   consumer's backlog.
//! - [`SyncCore::resync_snapshot`] answers consumers behind the retained
//!   floor (initial sync, or after history compaction).
//! - [`SyncFrame`] is the typed, versioned envelope unit carrying
//!   correlation and causation identity and a content digest over its
//!   body, in the shape the glossary's Sync Frame entry fixes.
//! - [`SyncConsumer`] is the native receiving side: it applies delta
//!   batches and adopts snapshots idempotently, the same semantics every
//!   SDK sync client of `terrascale.sync.v2` implements.
//!
//! Native payload codecs (ADR 0024): delta batches encode as
//! `terrabase-native/1` and snapshots as `terrabase-snapshot/1` —
//! versioned, magic-prefixed native encodings. The superseded external
//! CRDT engine's payload frames never decode in their place: the frame
//! version byte, the checkpoint encoding version, the payload codec
//! codes, and the payload magics all reject them fail-closed.
//!
//! Scope limits, stated exactly:
//!
//! - Server-authoritative only: there is no client write path in this
//!   crate. SDK sync clients implement `terrascale.sync.v2` natively and
//!   never multi-master the document.
//! - The materialized state is a pure function of the operation SET the
//!   core holds, never of arrival order: duplicated and reordered delta
//!   delivery is idempotent. Document-level deletion is a tombstone-dot
//!   layer over the registers — a delete concurrent with an update wins.
//! - `terrabase-crdt` ships scalar registers today, so each document
//!   field is one register carrying the field's value in an injective
//!   canonical text form; concurrent writes to the SAME field resolve by
//!   register last-writer-wins, not by element-wise merge of a nested
//!   structure. Element-wise structured merges land with the native
//!   collection datatypes.
//! - The HLC stamp of a materialized op derives its physical component
//!   from the commit's storage sequence (single writer, strictly
//!   increasing), so equal journals produce byte-identical snapshots.
//! - The frame digest is a correlation-integrity digest (FNV-1a 64), not
//!   a cryptographic guarantee; wire integrity is TBEP's contract.
//! - ADR 0024's full query-view state machine (view plans, authorized
//!   result revisions, AEAD checkpoints) is not implemented here yet;
//!   this landing is the native payload and state substrate it builds
//!   on. Deferred coverage is recorded in the crate's vector corpus.
//!
//! The [`protocol`] module is the `terrascale.sync.v2` client-channel
//! wire vocabulary built on this substrate; its wire spec is
//! `docs/sync/terrascale-sync-v2.md` and its golden conformance corpus
//! lives under `vectors/terrascale.sync.v2/`.

use std::collections::BTreeMap;
use std::collections::VecDeque;

use terrabase_causal::{
    ActorId, ActorProgress, CanonicalEncode, CausalContext, CounterInterval, Dot, DottedVersion,
    HlcTimestamp, MutationOrdinal, OriginCounter, ReplicaEpoch, ReplicaId,
};
use terrabase_crdt::{Candidate, LwwRegister, RegisterCodec};

pub mod native;
pub mod protocol;

/// The contract identity every frame and checkpoint carries. Unknown
/// versions fail closed.
pub const SYNC_CORE_CONTRACT: &str = "terrabase.sync-core/1";

/// The fixed peer id of the engine server's materializer for every
/// scope. Server-authoritative means exactly one writer per scope; the
/// constant peer id is also what makes materialization deterministic.
pub const SERVER_PEER_ID: u64 = 1;

/// Derives the native actor identity of the engine server's
/// materializer from its fixed peer id: the id's big-endian bytes in a
/// fresh replica identity at epoch one — the same derivation
/// `terrabase-replication-core` applies to Member peer ids.
#[must_use]
pub fn server_actor() -> ActorId {
    actor_from_peer(SERVER_PEER_ID)
}

/// Derives a native actor identity from a numeric peer id: the id's
/// big-endian bytes in a fresh replica identity at epoch one.
/// Deterministic and injective over the peer ids the sync plane
/// assigns.
#[must_use]
pub fn actor_from_peer(peer_id: u64) -> ActorId {
    let mut bytes = [0_u8; 16];
    bytes[8..].copy_from_slice(&peer_id.to_be_bytes());
    // Epoch one is nonzero by construction; the derivation cannot fail.
    match ReplicaEpoch::new(1) {
        Ok(epoch) => ActorId::new(ReplicaId::from_bytes(bytes), epoch),
        Err(_) => unreachable!("epoch one is never the reserved zero"),
    }
}

/// The inverse of [`actor_from_peer`] for actors it produced: the peer
/// id from the replica identity's low big-endian half.
const fn peer_of_actor(actor: ActorId) -> u64 {
    let replica = actor.replica();
    let bytes = replica.as_bytes();
    u64::from_be_bytes([
        bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15],
    ])
}

/// The delta-batch payload codec identity: `terrabase-native/1`. The
/// encoded form opens with the magic bytes `TSN` and codec version 1.
pub const NATIVE_DELTA_MAGIC: &[u8; 3] = b"TSN";

/// The snapshot payload codec identity: `terrabase-snapshot/1`. The
/// encoded form opens with the magic bytes `TSS` and codec version 1.
pub const NATIVE_SNAPSHOT_MAGIC: &[u8; 3] = b"TSS";

/// The first major version of both native payload codecs.
const NATIVE_CODEC_VERSION: u8 = 1;

/// The maximum number of operations one delta batch may carry: the
/// transport bound that keeps a batch bounded before decode.
const MAX_OPS_PER_BATCH: usize = 65_536;

/// The maximum number of actor entries in a sync checkpoint. Checkpoints in
/// this server-authoritative seam carry one entry today; the bound keeps
/// malformed public frames from driving unbounded vector work.
pub(crate) const MAX_CHECKPOINT_ENTRIES: usize = 64;

/// Maximum encoded size of a checkpoint under [`MAX_CHECKPOINT_ENTRIES`].
pub(crate) const MAX_CHECKPOINT_BYTES: usize = 1 + 8 + 4 + MAX_CHECKPOINT_ENTRIES * 16;

/// The maximum nesting of a structured field's canonical value text.
const MAX_VALUE_DEPTH: usize = 128;

/// The envelope headroom (bytes) added to a batch's estimated size before
/// comparing against the configured byte budget: combined-batch encodings
/// amortize the per-commit headers the estimate sums, and this covers the
/// constant overhead of one combined export.
const BATCH_HEADROOM_BYTES: usize = 64;

/// The packing-utilization target, in parts per thousand of the
/// configured batch byte budget, that triggers measured re-export
/// feedback when a first estimate-based acceptance under-fills the batch.
const PACKING_UTILIZATION_TARGET_PER_MILLE: u64 = 700;

/// The bound on additional range exports one `delta_since` call may
/// perform while tightening batch packing with measured feedback.
const MAX_PACKING_PROBES: usize = 3;

/// The transient assembly bound during packing probes.
const PACKING_PROBE_BOUND_MULTIPLIER: usize = 2;

/// The fixed header size of a frame's wire encoding: one version tag byte
/// plus four little-endian `u64` fields (correlation, causation, digest,
/// body length) — 1 + 4 * 8 = 33 bytes before the body.
const FRAME_HEADER_BYTES: usize = 33;

/// The decode limits for one payload register decode.
fn payload_limits() -> terrabase_crdt::DecodeLimits {
    terrabase_crdt::DecodeLimits {
        max_bytes: 16 << 20,
        max_candidates: 65_536,
        max_context_actors: 65_536,
        max_intervals_per_actor: 65_536,
        max_value_bytes: 8 << 20,
    }
}

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
    /// A checkpoint named a counter the server has not reached.
    CheckpointAheadOfServer {
        /// The end counter the checkpoint claimed.
        claimed_end: u64,
        /// The server's current end counter.
        server_end: u64,
    },
    /// A checkpoint or frame carried an unknown encoding version.
    UnknownCheckpointVersion(u8),
    /// A frame could not be decoded.
    InvalidFrame(&'static str),
    /// A native payload failed to encode or decode.
    NativeCodec(String),
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
            SyncCoreError::NativeCodec(message) => {
                write!(f, "native payload codec error: {message}")
            }
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
    /// The canonical text form carried inside a native register:
    /// injective, deterministic, and self-delimiting, so two states
    /// materialize the same value exactly when the register text is
    /// equal. Strings are length-prefixed (`s<len>:<bytes>`) and doubles
    /// carry their exact IEEE-754 bit pattern (`n<hex16>`).
    #[must_use]
    pub fn canonical_text(&self) -> String {
        let mut out = String::new();
        self.write_canonical_text(&mut out);
        out
    }

    fn write_canonical_text(&self, out: &mut String) {
        match self {
            SyncValue::Null => out.push_str("null"),
            SyncValue::Bool(flag) => out.push_str(if *flag { "true" } else { "false" }),
            SyncValue::Int(number) => {
                out.push('i');
                out.push_str(&number.to_string());
            }
            SyncValue::Float(value) => {
                out.push('n');
                let bits = value.to_bits();
                let _ = std::fmt::Write::write_fmt(out, format_args!("{bits:016x}"));
            }
            SyncValue::Str(text) => {
                out.push('s');
                out.push_str(&text.len().to_string());
                out.push(':');
                out.push_str(text);
            }
        }
    }

    /// Parses the canonical text form. `None` marks text that is not
    /// canonical — unreachable for registers this crate wrote.
    #[must_use]
    pub fn from_canonical_text(text: &str) -> Option<Self> {
        let bytes = text.as_bytes();
        let mut cursor = 0_usize;
        let value = parse_canonical_text(bytes, &mut cursor, 0)?;
        if cursor == bytes.len() { Some(value) } else { None }
    }
}

/// Parses one canonical text value at `cursor`, advancing past it.
fn parse_canonical_text(bytes: &[u8], cursor: &mut usize, depth: usize) -> Option<SyncValue> {
    if depth > MAX_VALUE_DEPTH {
        return None;
    }
    match *bytes.get(*cursor)? {
        b'n' => {
            if bytes.get(*cursor..(*cursor).checked_add(4)?) == Some(b"null") {
                *cursor += 4;
                return Some(SyncValue::Null);
            }
            *cursor += 1;
            let end = (*cursor).checked_add(16)?;
            let hex = bytes.get(*cursor..end)?;
            if hex.iter().any(|byte| !byte.is_ascii_digit() && !(b'a'..=b'f').contains(byte)) {
                return None;
            }
            *cursor = end;
            let bits = u64::from_str_radix(std::str::from_utf8(hex).ok()?, 16).ok()?;
            Some(SyncValue::Float(f64::from_bits(bits)))
        }
        b'i' => {
            *cursor += 1;
            let start = *cursor;
            if bytes.get(start) == Some(&b'-') {
                *cursor += 1;
            }
            let digits_start = *cursor;
            while bytes.get(*cursor).is_some_and(u8::is_ascii_digit) {
                *cursor += 1;
            }
            let digits = bytes.get(digits_start..*cursor)?;
            if digits.is_empty()
                || (digits.len() > 1 && digits.first() == Some(&b'0'))
                || (bytes.get(start) == Some(&b'-') && digits == b"0")
            {
                return None;
            }
            let text = std::str::from_utf8(bytes.get(start..*cursor)?).ok()?;
            Some(SyncValue::Int(text.parse().ok()?))
        }
        b's' => {
            *cursor += 1;
            let start = *cursor;
            while bytes.get(*cursor).is_some_and(u8::is_ascii_digit) {
                *cursor += 1;
            }
            let length_bytes = bytes.get(start..*cursor)?;
            if length_bytes.is_empty()
                || (length_bytes.len() > 1 && length_bytes.first() == Some(&b'0'))
            {
                return None;
            }
            let length: usize = std::str::from_utf8(length_bytes).ok()?.parse().ok()?;
            if bytes.get(*cursor) != Some(&b':') {
                return None;
            }
            *cursor += 1;
            let raw = bytes.get(*cursor..(*cursor).checked_add(length)?)?;
            let text = std::str::from_utf8(raw).ok()?.to_owned();
            *cursor += length;
            Some(SyncValue::Str(text))
        }
        b't' => {
            if bytes.get(*cursor..*cursor + 4)? == b"true" {
                *cursor += 4;
                Some(SyncValue::Bool(true))
            } else {
                None
            }
        }
        b'f' => {
            if bytes.get(*cursor..*cursor + 5)? == b"false" {
                *cursor += 5;
                Some(SyncValue::Bool(false))
            } else {
                None
            }
        }
        _ => None,
    }
}

/// One committed change as the seam consumes it from the journal side
/// (the Change Event Backbone's commit facts).
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
    /// The document is deleted: a tombstone dot is materialized and every
    /// consumer's materialized state drops the document.
    Delete,
}

/// The materialized state of the whole scope: one body map per live
/// document. The convergence observation surface.
pub type SyncScopeState = BTreeMap<String, BTreeMap<String, SyncValue>>;

fn le_u64(bytes: &[u8]) -> Result<u64, SyncCoreError> {
    bytes
        .try_into()
        .map(u64::from_le_bytes)
        .map_err(|_| SyncCoreError::InvalidFrame("byte run too short"))
}

fn le_u32(bytes: &[u8]) -> Result<u32, SyncCoreError> {
    bytes
        .try_into()
        .map(u32::from_le_bytes)
        .map_err(|_| SyncCoreError::InvalidFrame("byte run too short"))
}

/// The commit count of the next packing probe: a linear extrapolation
/// from the measured bytes of the current acceptance toward the byte
/// budget with a safety margin, always at least one more commit and
/// never past the retained window.
fn packing_probe_count(current: u64, measured: usize, budget: usize, total: u64) -> u64 {
    if measured == 0 || budget == 0 {
        return total.min(current + 1);
    }
    let scaled = (u128::from(current) * budget as u128 / measured as u128) * 9 / 10;
    let target = u64::try_from(scaled).unwrap_or(u64::MAX);
    target.clamp(current + 1, total)
}

/// The durable resumption identity of one Sync Session over this scope:
/// the server actor's received counter plus the storage sequence the
/// session has absorbed.
///
/// The encoding is versioned and fails closed on unknown versions —
/// including version 1, the superseded external-engine checkpoint
/// encoding this contract deliberately does not accept.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SyncCheckpoint {
    /// The per-peer end counters (peer, counter) the session holds, in
    /// canonical ascending peer order.
    vv_ends: Vec<(u64, u64)>,
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
    pub fn from_ends(mut vv_ends: Vec<(u64, u64)>, storage_sequence: u64) -> Self {
        vv_ends.sort_unstable();
        vv_ends.dedup();
        Self { vv_ends, storage_sequence }
    }

    /// The end entry for a peer, or `None` when the checkpoint holds no
    /// op of that peer.
    #[must_use]
    pub fn end_for(&self, peer: u64) -> Option<u64> {
        self.vv_ends.iter().find(|(p, _)| *p == peer).map(|(_, end)| *end)
    }

    /// The canonical end pairs (ascending peer order).
    #[must_use]
    pub fn ends(&self) -> &[(u64, u64)] {
        &self.vv_ends
    }

    /// The checkpoint's absorbed causal context (the actor-form
    /// counterpart of its end pairs, under the peer-to-actor
    /// derivation).
    #[must_use]
    pub fn context(&self) -> CausalContext {
        let mut context = CausalContext::new();
        for (peer, end) in &self.vv_ends {
            let actor = actor_from_peer(*peer);
            if *end == 0 {
                continue;
            }
            if let Ok(interval) = CounterInterval::new(1, *end) {
                let mut progress = ActorProgress::new();
                progress.insert_interval(interval);
                context.insert_progress(actor, &progress);
            }
        }
        context
    }

    /// The versioned byte encoding of the checkpoint: version byte,
    /// storage sequence, pair count, then (peer, end) pairs, all
    /// little-endian. Deterministic across runs and platforms.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(13 + self.vv_ends.len() * 16);
        bytes.push(2);
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
    /// leading version byte other than `2` — the superseded version-1
    /// encoding included — and [`SyncCoreError::InvalidFrame`] for
    /// truncated input.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, SyncCoreError> {
        let bad = || SyncCoreError::InvalidFrame("truncated checkpoint encoding");
        if bytes.first() != Some(&2) {
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
        if count > MAX_CHECKPOINT_ENTRIES {
            return Err(SyncCoreError::InvalidFrame("checkpoint entry count exceeds bound"));
        }
        let mut vv_ends = Vec::with_capacity(count);
        let mut previous_peer = None;
        for _ in 0..count {
            let peer = le_u64(take(8)?)?;
            let end = le_u64(take(8)?)?;
            if previous_peer.is_some_and(|previous| peer <= previous) {
                return Err(SyncCoreError::InvalidFrame("checkpoint entries are not canonical"));
            }
            previous_peer = Some(peer);
            vv_ends.push((peer, end));
        }
        if !rest.is_empty() {
            return Err(SyncCoreError::InvalidFrame("trailing checkpoint bytes"));
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
    end: u64,
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

/// One materialized operation with its full causal evidence: the dot
/// that names it, the deterministic HLC stamp ordering it, and the
/// server's complete causal context at apply time. Delta batches and
/// retained history exchange exactly these.
#[derive(Clone, Debug, PartialEq)]
struct SyncOp {
    dot: Dot,
    hlc: HlcTimestamp,
    observed: CausalContext,
    mutation: SyncMutation,
}

/// The mutation one operation carries.
#[derive(Clone, Debug, PartialEq)]
enum SyncMutation {
    /// A whole-body put: the fields the body names are set; the fields
    /// the core held and the body drops are removed.
    Put {
        /// The document identity.
        document: String,
        /// Per-field candidate values: `None` is a field removal.
        fields: BTreeMap<String, Option<SyncValue>>,
    },
    /// A document deletion: a tombstone dot that beats every update
    /// concurrent with it.
    Delete {
        /// The document identity.
        document: String,
    },
}

/// One document's materialized state: one LWW register per field, plus
/// the tombstone dots of the deletions observed for it.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
struct DocState {
    fields: BTreeMap<String, LwwRegister<String>>,
    deletes: Vec<Dot>,
}

/// The engine-side sync core for one scope.
pub struct SyncCore {
    scope: SyncScope,
    config: SyncCoreConfig,
    actor: ActorId,
    next_counter: u64,
    ops: BTreeMap<Dot, SyncOp>,
    docs: BTreeMap<String, DocState>,
    floor: CausalContext,
    context: CausalContext,
    last_sequence: u64,
    boundaries: VecDeque<CommitBoundary>,
}

impl SyncCore {
    /// Creates the sync core for one scope. The core materializes under
    /// the fixed server actor ([`SERVER_PEER_ID`]'s derived identity).
    ///
    /// # Errors
    ///
    /// Never fails in this landing; the result shape keeps the seam's
    /// contract stable for callers that map failures to typed errors.
    pub fn new(scope: SyncScope, config: SyncCoreConfig) -> Result<Self, SyncCoreError> {
        Ok(Self {
            scope,
            config,
            actor: server_actor(),
            next_counter: 1,
            ops: BTreeMap::new(),
            docs: BTreeMap::new(),
            floor: CausalContext::new(),
            context: CausalContext::new(),
            last_sequence: 0,
            boundaries: VecDeque::new(),
        })
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
    /// single-commit batch encoding) so batch assembly never encodes an
    /// unknown-size range later.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::SequenceOutOfOrder`] when the event's
    /// storage sequence is not strictly after the last applied one.
    pub fn apply(&mut self, commit: &JournalCommit) -> Result<SyncCheckpoint, SyncCoreError> {
        if commit.storage_sequence <= self.last_sequence {
            return Err(SyncCoreError::SequenceOutOfOrder {
                got: commit.storage_sequence,
                expected_after: self.last_sequence,
            });
        }
        let prev_end = self.current_end();
        let op = self.build_op(commit);
        self.apply_op(op.clone());
        self.last_sequence = commit.storage_sequence;
        let end = self.current_end();
        if end > prev_end {
            let delta_bytes = encode_batch(std::slice::from_ref(&op)).len();
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

    /// Builds the native operation of one journal commit: one dot, the
    /// deterministic HLC stamp derived from the storage sequence, the
    /// observed causal context, and the whole-body mutation.
    fn build_op(&mut self, commit: &JournalCommit) -> SyncOp {
        let counter =
            OriginCounter::new(self.next_counter).expect("the origin counter domain is bounded");
        self.next_counter = self.next_counter.checked_add(1).expect("counter domain exhausted");
        let dot = Dot::new(self.actor, counter);
        // The stamp's physical component is the storage sequence: one
        // writer, strictly increasing, so equal journals produce equal
        // bytes and every stamp is monotone.
        let hlc = HlcTimestamp::new(commit.storage_sequence, 0);
        let observed = self.context.clone();
        let mutation = match &commit.change {
            JournalChange::Put { body } => {
                let mut fields: BTreeMap<String, Option<SyncValue>> = body
                    .iter()
                    .map(|(field, value)| (field.clone(), Some(value.clone())))
                    .collect();
                if let Some(state) = self.docs.get(&commit.document_id) {
                    for field in state.fields.keys() {
                        fields.entry(field.clone()).or_insert(None);
                    }
                }
                SyncMutation::Put { document: commit.document_id.clone(), fields }
            }
            JournalChange::Delete => SyncMutation::Delete { document: commit.document_id.clone() },
        };
        SyncOp { dot, hlc, observed, mutation }
    }

    /// Applies one operation to the registers, the context, and the
    /// retained log. Idempotent per dot.
    fn apply_op(&mut self, op: SyncOp) {
        if self.ops.contains_key(&op.dot) {
            return;
        }
        self.context.insert(op.dot);
        self.context.union(&op.observed);
        apply_op_to_docs(&mut self.docs, &op);
        self.ops.insert(op.dot, op);
    }

    /// The current checkpoint of the core.
    #[must_use]
    pub fn checkpoint(&self) -> SyncCheckpoint {
        SyncCheckpoint {
            vv_ends: if self.current_end() > 0 {
                vec![(SERVER_PEER_ID, self.current_end())]
            } else {
                Vec::new()
            },
            storage_sequence: self.last_sequence,
        }
    }

    /// The server's current end counter (its highest issued op).
    #[must_use]
    pub fn current_end(&self) -> u64 {
        self.context
            .actors()
            .find(|(actor, _)| **actor == self.actor)
            .map_or(0, |(_, progress)| progress.through())
    }

    /// The oldest checkpoint this core can still serve a delta from:
    /// zero (full history) while the window has not overflowed, the
    /// oldest retained boundary afterwards. History compaction raises
    /// the floor the same way, because a compacted core retains
    /// operations only above its floor.
    #[must_use]
    pub fn retained_floor(&self) -> SyncCheckpoint {
        let window_full = self.boundaries.len() >= self.config.checkpoint_window.max(1);
        let mut floor_end = 0;
        let mut floor_sequence = 0;
        if window_full && let Some(entry) = self.boundaries.front() {
            floor_end = entry.end;
            floor_sequence = entry.sequence.saturating_sub(1);
        }
        // A compacted core retains operations only above the compaction
        // floor, so the floor can never sit below it.
        if let Some((_, progress)) = self.floor.actors().find(|(actor, _)| **actor == self.actor) {
            floor_end = floor_end.max(progress.through());
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
    /// server).
    ///
    /// # Panics
    ///
    /// Panics only if a retained-window length or a packing-probe commit
    /// count exceeds the platform's `usize`; the window is bounded by
    /// [`SyncCoreConfig::checkpoint_window`], so this cannot occur on a
    /// supported platform.
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
        let candidates: Vec<CommitBoundary> =
            self.boundaries.iter().filter(|b| b.end > from_end).copied().collect();
        let mut accepted: Option<CommitBoundary> = None;
        let mut commits = 0u64;
        let mut estimated = BATCH_HEADROOM_BYTES;
        for boundary in &candidates {
            if accepted.is_some()
                && estimated.saturating_add(boundary.delta_bytes) > self.config.batch_byte_budget
            {
                break;
            }
            estimated = estimated.saturating_add(boundary.delta_bytes);
            accepted = Some(*boundary);
            commits += 1;
        }
        let Some(mut accepted) = accepted else {
            return Ok(SyncBatch::BehindRetainedFloor { floor });
        };

        let mut bytes = self.export_range(from_end, accepted.end);
        // Packing feedback: the sum-of-parts estimate over-counts what
        // one combined batch carries — per-commit headers amortize
        // inside a combined span — so an under-filled batch is
        // re-accepted against the measured encoding, probing forward
        // with a bounded number of encodings. A probe is accepted only
        // when the measured bytes still fit the budget, so a delivered
        // multi-commit batch never exceeds it.
        let budget = self.config.batch_byte_budget;
        let underfilled = |served: &usize| {
            served.saturating_mul(1000)
                < usize::try_from(
                    PACKING_UTILIZATION_TARGET_PER_MILLE.saturating_mul(budget as u64),
                )
                .expect("budget target fits the platform")
        };
        let mut probes = 0;
        while commits < u64::try_from(candidates.len()).expect("retained window fits")
            && probes < MAX_PACKING_PROBES
            && underfilled(&bytes.len())
        {
            probes += 1;
            let target = packing_probe_count(
                commits,
                bytes.len(),
                budget,
                u64::try_from(candidates.len()).expect("retained window fits"),
            );
            let probe = candidates[usize::try_from(target).expect("retained window fits") - 1];
            let probe_bytes = self.export_range(from_end, probe.end);
            if probe_bytes.len() <= budget {
                accepted = probe;
                commits = target;
                bytes = probe_bytes;
                continue;
            }
            if probe_bytes.len() <= budget * PACKING_PROBE_BOUND_MULTIPLIER {
                let mid = u64::midpoint(commits, target);
                if mid > commits {
                    let refined =
                        candidates[usize::try_from(mid).expect("retained window fits") - 1];
                    let refined_bytes = self.export_range(from_end, refined.end);
                    if refined_bytes.len() <= budget {
                        accepted = refined;
                        commits = mid;
                        bytes = refined_bytes;
                    }
                }
            }
            break;
        }

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
    /// floor adopts the snapshot as its state instead of merging it into
    /// retained history — after [`SyncCore::compact_history`] the
    /// snapshot retains operations only above the new floor.
    ///
    /// # Errors
    ///
    /// Never fails in this landing; the result shape keeps the seam's
    /// contract stable for callers that map failures to typed errors.
    pub fn resync_snapshot(&self) -> Result<SyncSnapshot, SyncCoreError> {
        Ok(SyncSnapshot { bytes: self.snapshot_bytes(), checkpoint: self.checkpoint() })
    }

    /// Compacts retained history at the current version: afterwards
    /// deltas serve only from the new floor and older sessions receive
    /// [`SyncBatch::BehindRetainedFloor`]. The materialized state
    /// survives unchanged.
    ///
    /// # Errors
    ///
    /// Never fails in this landing; the result shape keeps the seam's
    /// contract stable for callers that map failures to typed errors.
    pub fn compact_history(&mut self) -> Result<(), SyncCoreError> {
        if self.context.actors().next().is_none() {
            return Ok(());
        }
        self.floor = self.context.clone();
        self.ops.clear();
        Ok(())
    }

    /// The core's observation surface: the materialized state of the
    /// scope (measurement and verification only). A document is live
    /// when no tombstone beats its surviving fields.
    #[must_use]
    pub fn materialized(&self) -> SyncScopeState {
        materialize_docs(&self.docs)
    }

    /// Encodes every operation this core holds in the counter range
    /// `(from_end, till_end]` as one native `terrabase-native/1` batch.
    fn export_range(&self, from_end: u64, till_end: u64) -> Vec<u8> {
        if till_end <= from_end {
            return Vec::new();
        }
        let ops: Vec<&SyncOp> = self
            .ops
            .values()
            .filter(|op| {
                let counter = op.dot.counter().get();
                counter > from_end && counter <= till_end
            })
            .collect();
        let owned: Vec<SyncOp> = ops.into_iter().cloned().collect();
        encode_batch(&owned)
    }

    /// The full-state snapshot encoding (`terrabase-snapshot/1`).
    fn snapshot_bytes(&self) -> Vec<u8> {
        let mut out = Vec::new();
        out.extend_from_slice(NATIVE_SNAPSHOT_MAGIC);
        out.push(NATIVE_CODEC_VERSION);
        encode_actor(self.actor, &mut out);
        encode_u64(self.next_counter, &mut out);
        encode_u64(self.last_sequence, &mut out);
        self.floor.encode_canonical(&mut out);
        encode_u64(self.docs.len() as u64, &mut out);
        for (document, doc) in &self.docs {
            encode_str(document, &mut out);
            encode_u64(doc.deletes.len() as u64, &mut out);
            for dot in &doc.deletes {
                encode_dot(*dot, &mut out);
            }
            encode_u64(doc.fields.len() as u64, &mut out);
            for (field, register) in &doc.fields {
                encode_str(field, &mut out);
                let bytes = RegisterCodec::encode_lww(register);
                encode_u64(bytes.len() as u64, &mut out);
                out.extend_from_slice(&bytes);
            }
        }
        encode_u64(self.ops.len() as u64, &mut out);
        for op in self.ops.values() {
            encode_op(op, &mut out);
        }
        out
    }

    fn validate_checkpoint(&self, from: &SyncCheckpoint) -> Result<u64, SyncCoreError> {
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

/// The native receiving side of the sync plane: applies the server's
/// delta batches and adopts snapshots idempotently. This is the
/// Rust-side expression of what every `terrascale.sync.v2` SDK client
/// does with delivered payloads; it never writes back.
#[derive(Clone, Debug, Default)]
pub struct SyncConsumer {
    docs: BTreeMap<String, DocState>,
    context: CausalContext,
    max_sequence: u64,
}

impl SyncConsumer {
    /// Creates an empty consumer at the initial checkpoint.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Applies one native `terrabase-native/1` delta batch. Idempotent:
    /// operations already held apply nothing; duplicated and reordered
    /// delivery converges to the same state because materialization is a
    /// function of the operation set.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::NativeCodec`] when the batch is
    /// malformed or exceeds the payload bounds.
    pub fn apply_delta(&mut self, bytes: &[u8]) -> Result<(), SyncCoreError> {
        let ops =
            decode_batch(bytes).map_err(|reason| SyncCoreError::NativeCodec(reason.to_string()))?;
        for op in ops {
            if self.context.contains(op.dot) {
                continue;
            }
            self.max_sequence = self.max_sequence.max(op.hlc.physical_unix_millis());
            self.context.insert(op.dot);
            self.context.union(&op.observed);
            apply_op_to_docs(&mut self.docs, &op);
        }
        Ok(())
    }

    /// Adopts one native `terrabase-snapshot/1` snapshot as the
    /// consumer's complete state, returning the checkpoint the snapshot
    /// corresponds to. Adoption replaces state; it never merges into
    /// pre-existing history.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::NativeCodec`] when the snapshot is
    /// malformed or exceeds the payload bounds.
    pub fn adopt_snapshot(&mut self, bytes: &[u8]) -> Result<SyncCheckpoint, SyncCoreError> {
        let decoded = decode_snapshot(bytes)
            .map_err(|reason| SyncCoreError::NativeCodec(reason.to_string()))?;
        let mut context = decoded.floor.clone();
        for doc in decoded.docs.values() {
            for register in doc.fields.values() {
                context.union(register.context());
            }
            for dot in &doc.deletes {
                context.insert(*dot);
            }
        }
        for op in &decoded.ops {
            context.insert(op.dot);
            context.union(&op.observed);
        }
        let mut ends: Vec<(u64, u64)> = context
            .actors()
            .map(|(actor, progress)| (peer_of_actor(*actor), progress.through()))
            .collect();
        ends.sort_unstable();
        let checkpoint = SyncCheckpoint::from_ends(ends, decoded.last_sequence);
        self.docs = decoded.docs;
        self.context = context;
        self.max_sequence = decoded.last_sequence;
        Ok(checkpoint)
    }

    /// The consumer's absorbed position.
    #[must_use]
    pub fn checkpoint(&self) -> SyncCheckpoint {
        let mut ends: Vec<(u64, u64)> = self
            .context
            .actors()
            .map(|(actor, progress)| (peer_of_actor(*actor), progress.through()))
            .collect();
        ends.sort_unstable();
        SyncCheckpoint::from_ends(ends, self.max_sequence)
    }

    /// The consumer's materialized state (the convergence observation
    /// surface, identical in shape to [`SyncCore::materialized`]).
    #[must_use]
    pub fn materialized(&self) -> SyncScopeState {
        materialize_docs(&self.docs)
    }

    /// Whether the consumer holds a live document.
    #[must_use]
    pub fn holds_document(&self, document: &str) -> bool {
        self.materialized().contains_key(document)
    }

    /// One document field of the consumer's materialized state.
    #[must_use]
    pub fn field(&self, document: &str, field: &str) -> Option<SyncValue> {
        self.materialized().get(document).and_then(|body| body.get(field)).cloned()
    }
}

/// Folds one operation into a document-state map: the shared join step
/// of server applies, consumer applies, and snapshot adoptions.
fn apply_op_to_docs(docs: &mut BTreeMap<String, DocState>, op: &SyncOp) {
    match &op.mutation {
        SyncMutation::Put { document, fields } => {
            let state = docs.entry(document.clone()).or_default();
            for (field, value) in fields {
                let candidate = Candidate::new(
                    value.as_ref().map(SyncValue::canonical_text),
                    DottedVersion::new(op.observed.clone(), op.dot),
                    MutationOrdinal::new(0),
                    op.hlc,
                );
                let register = state.fields.entry(field.clone()).or_default();
                *register = register.join(&LwwRegister::from_candidate(candidate));
            }
        }
        SyncMutation::Delete { document } => {
            let state = docs.entry(document.clone()).or_default();
            if !state.deletes.contains(&op.dot) {
                state.deletes.push(op.dot);
            }
        }
    }
}

/// Materializes a document-state map into the visible scope: a document
/// is live when it has no tombstones, or when some surviving field
/// candidate observed every tombstone (a resurrection); each live field
/// contributes its register's winning value.
fn materialize_docs(docs: &BTreeMap<String, DocState>) -> SyncScopeState {
    let mut scope = SyncScopeState::new();
    for (document, state) in docs {
        let resurrected = state.deletes.iter().all(|dot| {
            state.fields.values().any(|register| {
                register.winner().is_some_and(|candidate| {
                    !candidate.is_remove() && candidate.version().context().contains(*dot)
                })
            })
        });
        if !resurrected {
            continue;
        }
        let mut body = BTreeMap::new();
        for (field, register) in &state.fields {
            if let Some(text) = register.materialize()
                && let Some(value) = SyncValue::from_canonical_text(text)
            {
                body.insert(field.clone(), value);
            }
        }
        if !body.is_empty() {
            scope.insert(document.clone(), body);
        }
    }
    scope
}

// --- Native payload codecs: terrabase-native/1 and terrabase-snapshot/1 ---

/// The decoded snapshot form of one server state.
struct DecodedSnapshot {
    last_sequence: u64,
    floor: CausalContext,
    docs: BTreeMap<String, DocState>,
    ops: Vec<SyncOp>,
}

/// Encodes one unsigned LEB128 value, minimally.
fn encode_u64(mut value: u64, out: &mut Vec<u8>) {
    loop {
        let mut byte = (value & 0x7f) as u8;
        value >>= 7;
        if value != 0 {
            byte |= 0x80;
        }
        out.push(byte);
        if value == 0 {
            return;
        }
    }
}

/// Encodes one actor identity: replica bytes then a compact epoch.
fn encode_actor(actor: ActorId, out: &mut Vec<u8>) {
    out.extend_from_slice(actor.replica().as_bytes());
    encode_u64(actor.epoch().get(), out);
}

/// Encodes one dot: its actor then its counter.
fn encode_dot(dot: Dot, out: &mut Vec<u8>) {
    encode_actor(dot.actor(), out);
    encode_u64(dot.counter().get(), out);
}

/// Encodes one length-prefixed UTF-8 string.
fn encode_str(text: &str, out: &mut Vec<u8>) {
    encode_u64(text.len() as u64, out);
    out.extend_from_slice(text.as_bytes());
}

/// Encodes one operation: dot, HLC stamp, observed causal context, and
/// the mutation.
fn encode_op(op: &SyncOp, out: &mut Vec<u8>) {
    encode_dot(op.dot, out);
    encode_u64(op.hlc.physical_unix_millis(), out);
    encode_u64(u64::from(op.hlc.logical()), out);
    op.observed.encode_canonical(out);
    match &op.mutation {
        SyncMutation::Put { document, fields } => {
            out.push(1);
            encode_str(document, out);
            encode_u64(fields.len() as u64, out);
            for (field, value) in fields {
                encode_str(field, out);
                match value {
                    Some(value) => {
                        out.push(1);
                        encode_str(&value.canonical_text(), out);
                    }
                    None => out.push(0),
                }
            }
        }
        SyncMutation::Delete { document } => {
            out.push(2);
            encode_str(document, out);
        }
    }
}

/// Encodes one delta batch (the `terrabase-native/1` payload): magic,
/// codec version, op count, then the operations in counter order.
fn encode_batch(ops: &[SyncOp]) -> Vec<u8> {
    let mut out = Vec::new();
    out.extend_from_slice(NATIVE_DELTA_MAGIC);
    out.push(NATIVE_CODEC_VERSION);
    encode_u64(ops.len() as u64, &mut out);
    for op in ops {
        encode_op(op, &mut out);
    }
    out
}

/// A typed native payload decode failure with a stable reason.
#[derive(Clone, Debug, PartialEq)]
pub(crate) enum PayloadDecodeError {
    /// The input did not open with the expected magic and version.
    BadMagic,
    /// The input ended before one field was complete.
    UnexpectedEof,
    /// A field carried an invalid value or reserved tag.
    InvalidValue,
    /// The input exceeded one of the payload bounds.
    LimitExceeded,
    /// Bytes remained after the complete payload.
    TrailingBytes,
    /// An embedded register or context failed to decode.
    Nested(String),
}

impl PayloadDecodeError {
    fn reason(&self) -> &'static str {
        match self {
            Self::BadMagic => "the payload does not open with the native codec magic and version",
            Self::UnexpectedEof => "payload bytes ended early",
            Self::InvalidValue => "invalid payload value",
            Self::LimitExceeded => "payload decode limit exceeded",
            Self::TrailingBytes => "payload bytes have a trailing suffix",
            Self::Nested(_) => "an embedded register or context did not decode",
        }
    }
}

impl std::fmt::Display for PayloadDecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Nested(detail) => write!(f, "{}: {detail}", self.reason()),
            _ => f.write_str(self.reason()),
        }
    }
}

/// A bounded cursor over one native payload decode.
struct PayloadReader<'a> {
    input: &'a [u8],
    offset: usize,
}

impl<'a> PayloadReader<'a> {
    fn new(input: &'a [u8]) -> Result<Self, PayloadDecodeError> {
        if input.len() > payload_limits().max_bytes {
            return Err(PayloadDecodeError::LimitExceeded);
        }
        Ok(Self { input, offset: 0 })
    }

    fn byte(&mut self) -> Result<u8, PayloadDecodeError> {
        let byte = *self.input.get(self.offset).ok_or(PayloadDecodeError::UnexpectedEof)?;
        self.offset += 1;
        Ok(byte)
    }

    fn fixed<const N: usize>(&mut self) -> Result<[u8; N], PayloadDecodeError> {
        let end = self.offset.checked_add(N).ok_or(PayloadDecodeError::LimitExceeded)?;
        let bytes = self.input.get(self.offset..end).ok_or(PayloadDecodeError::UnexpectedEof)?;
        self.offset = end;
        bytes.try_into().map_err(|_| PayloadDecodeError::UnexpectedEof)
    }

    fn leb_u64(&mut self) -> Result<u64, PayloadDecodeError> {
        let mut value = 0_u64;
        for index in 0..10 {
            let byte = self.byte()?;
            let payload = u64::from(byte & 0x7f);
            if index == 9 && (byte & 0x7e) != 0 {
                return Err(PayloadDecodeError::InvalidValue);
            }
            value |= payload.checked_shl(index * 7).ok_or(PayloadDecodeError::InvalidValue)?;
            if byte & 0x80 == 0 {
                if index > 0 && payload == 0 {
                    return Err(PayloadDecodeError::InvalidValue);
                }
                return Ok(value);
            }
        }
        Err(PayloadDecodeError::InvalidValue)
    }

    fn bytes(&mut self, maximum: usize) -> Result<&'a [u8], PayloadDecodeError> {
        let length =
            usize::try_from(self.leb_u64()?).map_err(|_| PayloadDecodeError::LimitExceeded)?;
        if length > maximum {
            return Err(PayloadDecodeError::LimitExceeded);
        }
        let end = self.offset.checked_add(length).ok_or(PayloadDecodeError::LimitExceeded)?;
        let bytes = self.input.get(self.offset..end).ok_or(PayloadDecodeError::UnexpectedEof)?;
        self.offset = end;
        Ok(bytes)
    }

    fn bounded(&mut self, maximum: usize) -> Result<usize, PayloadDecodeError> {
        let value =
            usize::try_from(self.leb_u64()?).map_err(|_| PayloadDecodeError::LimitExceeded)?;
        if value > maximum {
            return Err(PayloadDecodeError::LimitExceeded);
        }
        Ok(value)
    }

    fn str(&mut self) -> Result<String, PayloadDecodeError> {
        let bytes = self.bytes(payload_limits().max_value_bytes)?;
        String::from_utf8(bytes.to_vec()).map_err(|_| PayloadDecodeError::InvalidValue)
    }

    fn magic(&mut self, expected: [u8; 3]) -> Result<(), PayloadDecodeError> {
        if self.byte()? != expected[0]
            || self.byte()? != expected[1]
            || self.byte()? != expected[2]
            || self.byte()? != NATIVE_CODEC_VERSION
        {
            return Err(PayloadDecodeError::BadMagic);
        }
        Ok(())
    }

    fn done(&self) -> bool {
        self.offset == self.input.len()
    }
}

/// Decodes one delta batch (a `terrabase-native/1` payload).
fn decode_batch(bytes: &[u8]) -> Result<Vec<SyncOp>, PayloadDecodeError> {
    let mut reader = PayloadReader::new(bytes)?;
    reader.magic(*NATIVE_DELTA_MAGIC)?;
    let count = reader.bounded(MAX_OPS_PER_BATCH)?;
    let mut ops = Vec::with_capacity(count);
    for _ in 0..count {
        ops.push(decode_op(&mut reader)?);
    }
    if !reader.done() {
        return Err(PayloadDecodeError::TrailingBytes);
    }
    Ok(ops)
}

/// Decodes one operation.
fn decode_op(reader: &mut PayloadReader<'_>) -> Result<SyncOp, PayloadDecodeError> {
    let dot = decode_dot(reader)?;
    let physical = reader.leb_u64()?;
    let logical = u32::try_from(reader.leb_u64()?).map_err(|_| PayloadDecodeError::InvalidValue)?;
    let observed = decode_context(reader)?;
    let mutation = match reader.byte()? {
        1 => {
            let document = reader.str()?;
            let field_count = reader.bounded(payload_limits().max_candidates)?;
            let mut fields = BTreeMap::new();
            for _ in 0..field_count {
                let field = reader.str()?;
                let value = match reader.byte()? {
                    0 => None,
                    1 => {
                        let text = reader.str()?;
                        Some(
                            SyncValue::from_canonical_text(&text)
                                .ok_or(PayloadDecodeError::InvalidValue)?,
                        )
                    }
                    _ => return Err(PayloadDecodeError::InvalidValue),
                };
                if fields.insert(field, value).is_some() {
                    return Err(PayloadDecodeError::InvalidValue);
                }
            }
            SyncMutation::Put { document, fields }
        }
        2 => {
            let document = reader.str()?;
            SyncMutation::Delete { document }
        }
        _ => return Err(PayloadDecodeError::InvalidValue),
    };
    Ok(SyncOp { dot, hlc: HlcTimestamp::new(physical, logical), observed, mutation })
}

/// Decodes one dot; its counter must be in the nonzero origin domain.
fn decode_dot(reader: &mut PayloadReader<'_>) -> Result<Dot, PayloadDecodeError> {
    let actor = decode_actor(reader)?;
    let counter =
        OriginCounter::new(reader.leb_u64()?).map_err(|_| PayloadDecodeError::InvalidValue)?;
    Ok(Dot::new(actor, counter))
}

/// Decodes one actor identity; its epoch must be nonzero.
fn decode_actor(reader: &mut PayloadReader<'_>) -> Result<ActorId, PayloadDecodeError> {
    let replica = ReplicaId::from_bytes(reader.fixed::<16>()?);
    let epoch =
        ReplicaEpoch::new(reader.leb_u64()?).map_err(|_| PayloadDecodeError::InvalidValue)?;
    Ok(ActorId::new(replica, epoch))
}

/// Decodes the canonical causal-context form: the exact inverse of
/// [`CausalContext::encode_canonical`], including its leading codec
/// version byte and the fixed-width big-endian actor epoch.
fn decode_context(reader: &mut PayloadReader<'_>) -> Result<CausalContext, PayloadDecodeError> {
    if reader.byte()? != terrabase_causal::CODEC_VERSION {
        return Err(PayloadDecodeError::Nested("unsupported context codec version".into()));
    }
    let actor_count = reader.bounded(payload_limits().max_context_actors)?;
    let mut context = CausalContext::new();
    let mut previous: Option<ActorId> = None;
    for _ in 0..actor_count {
        let replica = ReplicaId::from_bytes(reader.fixed::<16>()?);
        let epoch = ReplicaEpoch::new(u64::from_be_bytes(reader.fixed::<8>()?))
            .map_err(|_| PayloadDecodeError::InvalidValue)?;
        let actor = ActorId::new(replica, epoch);
        if previous.is_some_and(|previous| previous >= actor) {
            return Err(PayloadDecodeError::InvalidValue);
        }
        previous = Some(actor);
        let through = reader.leb_u64()?;
        let interval_count = reader.bounded(payload_limits().max_intervals_per_actor)?;
        if through == 0 && interval_count == 0 {
            return Err(PayloadDecodeError::InvalidValue);
        }
        let mut progress = ActorProgress::new();
        if through > 0 {
            let interval =
                CounterInterval::new(1, through).map_err(|_| PayloadDecodeError::InvalidValue)?;
            progress.insert_interval(interval);
        }
        let mut previous_interval: Option<u64> = None;
        for _ in 0..interval_count {
            let first = reader.leb_u64()?;
            let last = reader.leb_u64()?;
            let interval =
                CounterInterval::new(first, last).map_err(|_| PayloadDecodeError::InvalidValue)?;
            if interval.first() <= through
                || previous_interval
                    .is_some_and(|previous| interval.first() <= previous.saturating_add(1))
            {
                return Err(PayloadDecodeError::InvalidValue);
            }
            previous_interval = Some(interval.last());
            progress.insert_interval(interval);
        }
        if progress.through() != through || progress.intervals().len() != interval_count {
            return Err(PayloadDecodeError::InvalidValue);
        }
        context.insert_progress(actor, &progress);
    }
    Ok(context)
}

/// Decodes one full snapshot (a `terrabase-snapshot/1` payload).
fn decode_snapshot(bytes: &[u8]) -> Result<DecodedSnapshot, PayloadDecodeError> {
    let mut reader = PayloadReader::new(bytes)?;
    reader.magic(*NATIVE_SNAPSHOT_MAGIC)?;
    // The writer identity and its next counter ride the snapshot for
    // forward compatibility; the consumer rebuilds its position from
    // the absorbed context, so both are read and released here.
    let _actor = decode_actor(&mut reader)?;
    let _next_counter = reader.leb_u64()?;
    let last_sequence = reader.leb_u64()?;
    let floor = decode_context(&mut reader)?;
    let limits = payload_limits();
    let doc_count = reader.bounded(limits.max_candidates)?;
    let mut docs = BTreeMap::new();
    for _ in 0..doc_count {
        let document = reader.str()?;
        let delete_count = reader.bounded(limits.max_candidates)?;
        let mut deletes = Vec::with_capacity(delete_count);
        for _ in 0..delete_count {
            deletes.push(decode_dot(&mut reader)?);
        }
        let field_count = reader.bounded(limits.max_candidates)?;
        let mut fields = BTreeMap::new();
        for _ in 0..field_count {
            let field = reader.str()?;
            let register_bytes = reader.bytes(limits.max_bytes)?;
            let register = RegisterCodec::decode_lww::<String>(register_bytes, limits)
                .map_err(|error| PayloadDecodeError::Nested(error.to_string()))?;
            if fields.insert(field, register).is_some() {
                return Err(PayloadDecodeError::InvalidValue);
            }
        }
        if docs.insert(document, DocState { fields, deletes }).is_some() {
            return Err(PayloadDecodeError::InvalidValue);
        }
    }
    let op_count = reader.bounded(MAX_OPS_PER_BATCH)?;
    let mut ops = Vec::with_capacity(op_count);
    for _ in 0..op_count {
        ops.push(decode_op(&mut reader)?);
    }
    if !reader.done() {
        return Err(PayloadDecodeError::TrailingBytes);
    }
    Ok(DecodedSnapshot { last_sequence, floor, docs, ops })
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
/// integrity belongs to TBEP.
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
    /// The body's canonical digest input emitted chunk by chunk: a kind
    /// tag, then the payload and checkpoint encodings. The emitter form
    /// is what keeps both digesting and wire encoding allocation-light:
    /// a consumer hashes the chunks directly or writes them into one
    /// pre-sized buffer, and neither path builds an intermediate byte
    /// vector per field.
    fn emit_digest_input<E: FnMut(&[u8])>(&self, mut emit: E) {
        let emit_checkpoint = |emit: &mut E, checkpoint: &SyncCheckpoint| {
            emit(&((13 + checkpoint.ends().len() * 16) as u64).to_le_bytes());
            emit(&[2]);
            emit(&checkpoint.storage_sequence.to_le_bytes());
            emit(&u32::try_from(checkpoint.ends().len()).unwrap_or(u32::MAX).to_le_bytes());
            for (peer, end) in checkpoint.ends() {
                emit(&peer.to_le_bytes());
                emit(&end.to_le_bytes());
            }
        };
        match self {
            FrameBody::Delta { bytes, next } => {
                emit(&[1]);
                emit(&(bytes.len() as u64).to_le_bytes());
                emit(bytes);
                emit_checkpoint(&mut emit, next);
            }
            FrameBody::Snapshot { bytes, checkpoint } => {
                emit(&[2]);
                emit(&(bytes.len() as u64).to_le_bytes());
                emit(bytes);
                emit_checkpoint(&mut emit, checkpoint);
            }
            FrameBody::Checkpoint(checkpoint) => {
                emit(&[3]);
                emit_checkpoint(&mut emit, checkpoint);
            }
            FrameBody::BehindRetainedFloor { floor } => {
                emit(&[4]);
                emit_checkpoint(&mut emit, floor);
            }
        }
    }

    /// The body's canonical digest input as one vector (tests and
    /// canonical-form checks).
    #[cfg(test)]
    fn digest_input(&self) -> Vec<u8> {
        let mut input = Vec::new();
        self.emit_digest_input(|chunk| input.extend_from_slice(chunk));
        input
    }

    /// The exact encoded length of the canonical digest input, so callers
    /// can pre-size one buffer instead of growing it.
    fn encoded_len(&self) -> usize {
        match self {
            FrameBody::Delta { bytes, next } | FrameBody::Snapshot { bytes, checkpoint: next } => {
                1 + 8 + bytes.len() + 8 + 13 + next.ends().len() * 16
            }
            FrameBody::Checkpoint(checkpoint)
            | FrameBody::BehindRetainedFloor { floor: checkpoint } => {
                1 + 8 + 13 + checkpoint.ends().len() * 16
            }
        }
    }

    /// The correlation-integrity digest of this body, computed
    /// incrementally over the emitted chunks (no intermediate buffer).
    #[must_use]
    pub fn digest(&self) -> FrameDigest {
        let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
        self.emit_digest_input(|chunk| {
            for byte in chunk {
                hash ^= u64::from(*byte);
                hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
            }
        });
        FrameDigest(hash)
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
    /// Unknown contract versions fail closed on decode — including
    /// version 1, the superseded external-engine payload encoding this
    /// contract deliberately does not accept. The body is emitted once
    /// into one exactly-sized buffer.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let body_len = self.body.encoded_len();
        let mut bytes = Vec::with_capacity(body_len + FRAME_HEADER_BYTES);
        bytes.push(2);
        bytes.extend_from_slice(&self.correlation.value().to_le_bytes());
        bytes.extend_from_slice(&self.causation.value().to_le_bytes());
        bytes.extend_from_slice(&self.digest.0.to_le_bytes());
        bytes.extend_from_slice(&(body_len as u64).to_le_bytes());
        self.body.emit_digest_input(|chunk| bytes.extend_from_slice(chunk));
        debug_assert_eq!(
            bytes.len(),
            body_len + FRAME_HEADER_BYTES,
            "the emitted body matches its length"
        );
        bytes
    }

    /// Decodes a frame, failing closed on unknown versions, truncation,
    /// and length disagreement with the actual bytes.
    ///
    /// # Errors
    ///
    /// Returns [`SyncCoreError::InvalidFrame`] for any structural
    /// problem and [`SyncCoreError::UnknownCheckpointVersion`] for an
    /// unknown leading version byte; digest disagreement is not a decode
    /// error — call [`SyncFrame::verify`] on the result.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, SyncCoreError> {
        let bad = SyncCoreError::InvalidFrame;
        if bytes.first() != Some(&2) {
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
        assert_eq!(bytes.first(), Some(&2));
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

    #[test]
    fn native_payload_magics_reject_foreign_bytes() {
        // The superseded external-engine payload encodings never open
        // with the native magic, so they fail closed before any field
        // is read.
        let mut consumer = SyncConsumer::new();
        assert!(matches!(
            consumer.apply_delta(&[0x82, 0x01, 0x02]),
            Err(SyncCoreError::NativeCodec(_))
        ));
        assert!(matches!(
            consumer.adopt_snapshot(&[0x00, 0x01, 0x02, 0x03]),
            Err(SyncCoreError::NativeCodec(_))
        ));
    }

    #[test]
    fn canonical_value_text_round_trips() {
        for value in [
            SyncValue::Null,
            SyncValue::Bool(true),
            SyncValue::Bool(false),
            SyncValue::Int(-42),
            SyncValue::Float(std::f64::consts::PI),
            SyncValue::Str("s\"weird\":".to_string()),
        ] {
            let text = value.canonical_text();
            assert_eq!(SyncValue::from_canonical_text(&text), Some(value));
        }
    }
}
