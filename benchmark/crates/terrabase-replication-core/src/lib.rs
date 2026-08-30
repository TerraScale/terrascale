//! The leaderless multi-writer replication core (P2, ADR 0018).
//!
//! This crate is the native CRDT plane that makes every Member of a
//! replica set a peer-writer: each Member materializes accepted writes
//! into its own [`ReplicaDoc`] under its own actor identity, tracks a
//! causal frontier, and exchanges bounded delta batches with any other
//! Member — ownership-agnostic, per the frozen leaderless contract
//! (`docs/design/p0-leaderless-replication-contract.md` §5,
//! anti-entropy).
//!
//! The datatypes are TerraBase-native: per-field last-writer-wins
//! registers from `terrabase-crdt`, joined over the dotted version
//! vectors, causal contexts, and hybrid logical clocks of
//! `terrabase-causal`. The materialized state is a pure function of
//! the SET of operations a replica holds — never of their arrival
//! order — which is the convergence property the contract freezes.
//! Document-level deletion is a tombstone-dot layer over the
//! registers: a delete concurrent with an update wins (the losing
//! update stays recoverable through [`ReplicaDoc::value_after`]), and
//! an update that observed the delete resurrects the document.
//!
//! The sibling `terrabase-sync-core` crate holds the client sync plane
//! with the opposite shape: one fixed server peer serving consumer
//! sessions. This crate holds the server plane: N named peers, no
//! distinguished writer, convergence by causal merge rather than by
//! election.
//!
//! What this landing proves over the native registers:
//!
//! - concurrent same-document writes through different Members merge
//!   to one deterministic register winner with identical materialized
//!   state on every replica (contract §8 scenario 6);
//! - delete wins over a concurrent update, with the loser recoverable
//!   from the shadow history (§8 scenario 13);
//! - duplicated and reordered delta delivery converges idempotently
//!   (§8 scenario 11);
//! - a partitioned Member catches up through accumulated deltas after
//!   rejoin (§8 scenario 12's data plane).
//!
//! Honest limits: `terrabase-crdt` ships scalar registers today —
//! counters, lists, and maps are not landed — so each document field
//! is one register carrying the field's value in a canonical text
//! form, and a structured field value (array or object) rides inside
//! that text as a whole: concurrent writes to DIFFERENT fields merge
//! independently, while concurrent writes to the SAME field resolve by
//! register last-writer-wins rather than by element-wise merge of the
//! nested structure. Element-wise structured merges land with the
//! native collection datatypes. The durable persistence of a
//! [`ReplicaDoc`] (its snapshot bytes staged through the engine's
//! object-store tier) and the authenticated transport that carries
//! frontiers and deltas are later landings; this core defines and
//! proves the merge contract over in-memory documents.
//!
//! The native [`progress`] module is intentionally separate from the
//! [`ReplicaDoc`] plane: it tracks durable counter prefixes and bounded
//! received ranges without importing or extending replica state.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write as _;

use terrabase_causal::{
    ActorId, ActorProgress, CODEC_VERSION, CanonicalEncode, CausalContext, CounterInterval, Dot,
    DottedVersion, HlcTimestamp, HybridLogicalClock, MutationOrdinal, OriginCounter, ReplicaEpoch,
    ReplicaId,
};
use terrabase_crdt::{Candidate, DecodeError, DecodeLimits, LwwRegister, RegisterCodec};

pub mod anti_entropy;
pub mod compose;
pub mod durable;
pub mod progress;

pub use progress::{
    DEFAULT_MAX_GAP_RANGES, DurableAcknowledgement, DurableProgressTracker,
    DurableReplicationProgress, GapInterval, GapRange, MAX_GAP_RANGES, NativeReplicationProgress,
    ProgressCursor, ProgressError, ReplicationCursor, ReplicationProgress, RetentionGuard,
    StableRetentionFrontier,
};

pub use compose::{
    ComposeError, ComposedAck, ComposedCoordinationError, ComposedMember, ComposedReceipt,
    ComposedWrite, CoordinatedConstraint, CoordinatedReceipt, EpochError, EpochReceipt,
    MigrationOutcome, ReadRepairReport, ReconcileReport, RuleClass, TransactionRefusal,
    accept_epoch, coordinate_composed_transaction, coordinate_composed_write, migrate_collection,
    quorum_read_with_repair,
};
pub use durable::{DurableMember, DurableMemberError, DurableMemberProgress};

/// The exchange codec's first major version.
const EXCHANGE_CODEC_VERSION: u8 = 1;

/// The maximum number of operations one delta batch may carry: the
/// transport bound that keeps a batch bounded before decode.
const MAX_OPS_PER_BATCH: usize = 65_536;

/// Maximum nesting of a structured field's canonical value. This keeps
/// malformed input from consuming the call stack while preserving ample
/// room for ordinary documents.
const MAX_VALUE_DEPTH: usize = 128;

/// The decode limits for one exchange or snapshot decode.
fn exchange_limits() -> DecodeLimits {
    DecodeLimits {
        max_bytes: 16 << 20,
        max_candidates: 65_536,
        max_context_actors: 65_536,
        max_intervals_per_actor: 65_536,
        max_value_bytes: 8 << 20,
    }
}

/// The materialized state of the whole replicated scope: one body map
/// per live document. The convergence observation surface.
pub type ReplicaScope = BTreeMap<String, BTreeMap<String, ReplicaValue>>;

/// One field value of a replicated document body.
///
/// The variants mirror the engine's document values. A floating-point
/// number is carried as its exact bit pattern so register equality and
/// the canonical text form are total, stable, and never depend on
/// float formatting.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ReplicaValue {
    /// The absent value.
    Null,
    /// A boolean field.
    Boolean(bool),
    /// A signed integer field.
    Integer(i64),
    /// A floating-point field, as its exact bit pattern.
    Number {
        /// The IEEE-754 bit pattern of the value.
        bits: u64,
    },
    /// A text field.
    Text(String),
    /// An array field.
    Array(Vec<ReplicaValue>),
    /// An object field; entries are canonically sorted by key.
    Object(BTreeMap<String, ReplicaValue>),
}

impl ReplicaValue {
    /// Builds a floating-point field value.
    #[must_use]
    pub fn number(value: f64) -> Self {
        Self::Number { bits: value.to_bits() }
    }

    /// Returns the floating-point value of a [`ReplicaValue::Number`],
    /// or `None` for any other variant.
    #[must_use]
    pub fn as_f64(&self) -> Option<f64> {
        match self {
            Self::Number { bits } => Some(f64::from_bits(*bits)),
            _ => None,
        }
    }

    /// Returns the canonical text form carried inside a register:
    /// injective, deterministic, and self-delimiting, so two replicas
    /// materialize the same value exactly when the register text is
    /// equal. Strings are length-prefixed (`s<len>:<bytes>`), numbers
    /// carry exact bit patterns (`n<hex16>`), and objects emit entries
    /// in sorted key order.
    fn canonical_text(&self) -> String {
        let mut out = String::new();
        self.write_canonical_text(&mut out);
        out
    }

    fn write_canonical_text(&self, out: &mut String) {
        match self {
            Self::Null => out.push_str("null"),
            Self::Boolean(flag) => out.push_str(if *flag { "true" } else { "false" }),
            Self::Integer(number) => {
                out.push('i');
                out.push_str(&number.to_string());
            }
            Self::Number { bits } => {
                out.push('n');
                let _ = write!(out, "{bits:016x}");
            }
            Self::Text(text) => {
                out.push('s');
                out.push_str(&text.len().to_string());
                out.push(':');
                out.push_str(text);
            }
            Self::Array(items) => {
                out.push('[');
                for (index, item) in items.iter().enumerate() {
                    if index > 0 {
                        out.push(',');
                    }
                    item.write_canonical_text(out);
                }
                out.push(']');
            }
            Self::Object(fields) => {
                out.push('{');
                for (index, (key, value)) in fields.iter().enumerate() {
                    if index > 0 {
                        out.push(',');
                    }
                    Self::Text(key.clone()).write_canonical_text(out);
                    out.push(':');
                    value.write_canonical_text(out);
                }
                out.push('}');
            }
        }
    }

    /// Parses the canonical text form. `None` marks text that is not
    /// canonical — unreachable for registers this crate wrote.
    fn from_canonical_text(text: &str) -> Option<Self> {
        let bytes = text.as_bytes();
        let mut cursor = 0_usize;
        let value = parse_canonical_text(bytes, &mut cursor, 0)?;
        if cursor == bytes.len() { Some(value) } else { None }
    }
}

/// Parses one canonical text value at `cursor`, advancing past it.
#[allow(
    clippy::too_many_lines,
    reason = "one bounded recursive parser keeps canonical grammar together"
)]
fn parse_canonical_text(bytes: &[u8], cursor: &mut usize, depth: usize) -> Option<ReplicaValue> {
    if depth > MAX_VALUE_DEPTH {
        return None;
    }
    match *bytes.get(*cursor)? {
        b'n' => {
            if bytes.get(*cursor..(*cursor).checked_add(4)?) == Some(b"null") {
                *cursor += 4;
                return Some(ReplicaValue::Null);
            }
            *cursor += 1;
            let end = (*cursor).checked_add(16)?;
            let hex = bytes.get(*cursor..end)?;
            if hex.iter().any(|byte| !byte.is_ascii_digit() && !(b'a'..=b'f').contains(byte)) {
                return None;
            }
            *cursor = end;
            let bits = u64::from_str_radix(std::str::from_utf8(hex).ok()?, 16).ok()?;
            Some(ReplicaValue::Number { bits })
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
            Some(ReplicaValue::Integer(text.parse().ok()?))
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
            let text = std::str::from_utf8(bytes.get(*cursor..(*cursor).checked_add(length)?)?)
                .ok()?
                .to_owned();
            *cursor += length;
            Some(ReplicaValue::Text(text))
        }
        b't' => {
            if bytes.get(*cursor..*cursor + 4)? == b"true" {
                *cursor += 4;
                Some(ReplicaValue::Boolean(true))
            } else {
                None
            }
        }
        b'f' => {
            if bytes.get(*cursor..*cursor + 5)? == b"false" {
                *cursor += 5;
                Some(ReplicaValue::Boolean(false))
            } else {
                None
            }
        }
        b'[' => {
            *cursor += 1;
            let mut items = Vec::new();
            if bytes.get(*cursor) == Some(&b']') {
                *cursor += 1;
                return Some(ReplicaValue::Array(items));
            }
            loop {
                items.push(parse_canonical_text(bytes, cursor, depth + 1)?);
                match bytes.get(*cursor) {
                    Some(b',') => *cursor += 1,
                    Some(b']') => {
                        *cursor += 1;
                        return Some(ReplicaValue::Array(items));
                    }
                    _ => return None,
                }
            }
        }
        b'{' => {
            *cursor += 1;
            let mut fields = BTreeMap::new();
            if bytes.get(*cursor) == Some(&b'}') {
                *cursor += 1;
                return Some(ReplicaValue::Object(fields));
            }
            loop {
                let ReplicaValue::Text(key) = parse_canonical_text(bytes, cursor, depth + 1)?
                else {
                    return None;
                };
                if bytes.get(*cursor) != Some(&b':') {
                    return None;
                }
                *cursor += 1;
                let value = parse_canonical_text(bytes, cursor, depth + 1)?;
                if fields.insert(key, value).is_some() {
                    return None;
                }
                match bytes.get(*cursor) {
                    Some(b',') => *cursor += 1,
                    Some(b'}') => {
                        *cursor += 1;
                        return Some(ReplicaValue::Object(fields));
                    }
                    _ => return None,
                }
            }
        }
        _ => None,
    }
}

/// Derives the native actor identity of a Member's numeric peer id:
/// the id's big-endian bytes in a fresh replica identity at epoch one.
/// Deterministic and injective over the peer ids Members assign.
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

/// A typed failure of the replication core. No internal error text
/// crosses the seam: the caller learns the class, not the internals.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReplicaError {
    /// A materializing operation was refused (the origin counter or
    /// the injected clock exhausted its domain).
    OpRejected,
    /// A delta export failed.
    ExportFailed,
    /// A delta or snapshot import failed.
    ImportFailed,
    /// A versioned read named an operation this replica never held.
    UnknownVersion,
}

/// One accepted mutation with its full causal evidence: the dot that
/// names it, the HLC stamp ordering it, and the accepting replica's
/// complete causal context at apply time. Delta batches and
/// shadow-history reads exchange and replay exactly these.
#[derive(Clone, Debug, Eq, PartialEq)]
struct ReplicaOp {
    dot: Dot,
    hlc: HlcTimestamp,
    observed: CausalContext,
    mutation: ReplicaMutation,
}

/// The mutation one operation carries.
#[derive(Clone, Debug, Eq, PartialEq)]
enum ReplicaMutation {
    /// A whole-body put: the fields the body names are set; the fields
    /// the accepting replica held and the body drops are removed.
    Put {
        /// The document identity.
        document: String,
        /// Per-field candidate values: `None` is a field removal.
        fields: BTreeMap<String, Option<ReplicaValue>>,
    },
    /// A document deletion: a tombstone dot that beats every update
    /// concurrent with it.
    Delete {
        /// The document identity.
        document: String,
    },
}

/// One document's replicated state: one LWW register per field, plus
/// the tombstone dots of the deletions observed for it.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
struct DocState {
    fields: BTreeMap<String, LwwRegister<String>>,
    deletes: Vec<Dot>,
}

/// One Member's materialized replica state: the native registers,
/// causal context, and operation log written under this Member's own
/// actor identity.
///
/// Every Member of the replica set holds the same role over its
/// [`ReplicaDoc`]: it applies accepted writes, answers frontier
/// summaries, exports deltas, and absorbs peers' deltas. Nothing here
/// distinguishes a coordinator — coordination is request-scoped in the
/// engine-side leaderless core (`terrabase::replication::leaderless`).
pub struct ReplicaDoc {
    actor: ActorId,
    next_counter: u64,
    clock: HybridLogicalClock,
    ops: BTreeMap<Dot, ReplicaOp>,
    docs: BTreeMap<String, DocState>,
    floor: CausalContext,
    context: CausalContext,
}

impl ReplicaDoc {
    /// Creates one replica state under a Member's actor identity.
    #[must_use]
    pub fn new(actor: ActorId) -> Self {
        Self {
            actor,
            next_counter: 1,
            clock: HybridLogicalClock::restore(HlcTimestamp::new(0, 0)),
            ops: BTreeMap::new(),
            docs: BTreeMap::new(),
            floor: CausalContext::new(),
            context: CausalContext::new(),
        }
    }

    /// Returns the Member's actor identity.
    #[must_use]
    pub const fn actor(&self) -> ActorId {
        self.actor
    }

    /// Materializes one whole-body document put: fields the body names
    /// are set, fields this replica holds and the body drops are
    /// removed (whole-body replace). The operation is stamped with the
    /// injected physical time through the replica's hybrid logical
    /// clock; equal operation sets merge to equal state whatever times
    /// were injected at acceptance.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::OpRejected`] when the origin counter or
    /// the clock exhausts its domain.
    pub fn apply_put(
        &mut self,
        now_unix_millis: u64,
        document_id: &str,
        body: &BTreeMap<String, ReplicaValue>,
    ) -> Result<(), ReplicaError> {
        let dot = self.fresh_dot()?;
        let hlc = self.clock.send(now_unix_millis).map_err(|_| ReplicaError::OpRejected)?;
        let observed = self.context.clone();
        let mut fields: BTreeMap<String, Option<ReplicaValue>> =
            body.iter().map(|(field, value)| (field.clone(), Some(value.clone()))).collect();
        if let Some(document) = self.docs.get(document_id) {
            for field in document.fields.keys() {
                fields.entry(field.clone()).or_insert(None);
            }
        }
        self.apply_op(ReplicaOp {
            dot,
            hlc,
            observed,
            mutation: ReplicaMutation::Put { document: document_id.to_owned(), fields },
        });
        Ok(())
    }

    /// Materializes one document deletion: a tombstone dot that wins
    /// over every update concurrent with it on every replica. A later
    /// put that observed the deletion (its causal context contains the
    /// tombstone) resurrects the document.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::OpRejected`] when the origin counter or
    /// the clock exhausts its domain.
    pub fn apply_delete(
        &mut self,
        now_unix_millis: u64,
        document_id: &str,
    ) -> Result<(), ReplicaError> {
        let dot = self.fresh_dot()?;
        let hlc = self.clock.send(now_unix_millis).map_err(|_| ReplicaError::OpRejected)?;
        let observed = self.context.clone();
        self.apply_op(ReplicaOp {
            dot,
            hlc,
            observed,
            mutation: ReplicaMutation::Delete { document: document_id.to_owned() },
        });
        Ok(())
    }

    /// Returns the replica's causal frontier: every actor it holds
    /// operations for, summarized by received progress. This is the
    /// bounded summary anti-entropy exchanges (contract §5).
    #[must_use]
    pub fn frontier(&self) -> ReplicaFrontier {
        ReplicaFrontier { context: self.context.clone() }
    }

    /// Exports the delta this replica holds beyond `remote`'s frontier:
    /// the operations `remote` does not name, encoded as one bounded
    /// batch.
    ///
    /// A remote behind the retained floor cannot be served by delta:
    /// the answer names the floor so the caller falls back to snapshot
    /// transfer.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] when the export fails.
    pub fn delta_for(&self, remote: &ReplicaFrontier) -> Result<ReplicaExchange, ReplicaError> {
        if remote.covers(&self.frontier()) {
            return Ok(ReplicaExchange::UpToDate);
        }
        let floored = self.floor.actors().next().is_some();
        if floored && !remote.context().dominates(&self.floor) {
            return Ok(ReplicaExchange::BehindRetainedFloor {
                floor: ReplicaFrontier { context: self.floor.clone() },
            });
        }
        let missing: Vec<&ReplicaOp> =
            self.ops.values().filter(|op| !remote.holds(op.dot)).collect();
        if missing.is_empty() {
            return Ok(ReplicaExchange::UpToDate);
        }
        let mut out = Vec::new();
        out.push(EXCHANGE_CODEC_VERSION);
        encode_u64(missing.len() as u64, &mut out);
        for op in &missing {
            encode_op(op, &mut out);
        }
        Ok(ReplicaExchange::Delta(out))
    }

    /// Absorbs a peer's delta batch. Idempotent: operations the replica
    /// already holds apply nothing; out-of-order delivery is safe — the
    /// materialized state is a function of the operation SET, and the
    /// register join orders candidates by causal dominance and clock,
    /// never by arrival.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ImportFailed`] when the batch is
    /// malformed or exceeds the exchange bounds.
    pub fn absorb(&mut self, bytes: &[u8]) -> Result<(), ReplicaError> {
        let ops = decode_batch(bytes).map_err(|_| ReplicaError::ImportFailed)?;
        for op in ops {
            if self.ops.contains_key(&op.dot) {
                continue;
            }
            let stamp = op.hlc;
            self.clock
                .receive(stamp.physical_unix_millis(), stamp, u64::MAX)
                .map_err(|_| ReplicaError::ImportFailed)?;
            self.apply_op(op);
        }
        Ok(())
    }

    /// Returns the materialized state of the whole scope — the
    /// convergence observation surface.
    #[must_use]
    pub fn materialized(&self) -> ReplicaScope {
        materialize_docs(&self.docs)
    }

    /// Returns the number of retained operations in this replica.
    #[must_use]
    pub fn operation_count(&self) -> u64 {
        self.ops.len() as u64
    }

    /// Returns the materialized state as of just after one operation,
    /// by its dot: everything that operation observed, plus itself.
    /// This is the shadow-history read that recovers a merge loser.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::UnknownVersion`] when this replica does
    /// not hold the operation (never authored or absorbed, or collected
    /// by a compaction below the retained floor).
    pub fn value_after(&self, dot: Dot) -> Result<ReplicaScope, ReplicaError> {
        let Some(op) = self.ops.get(&dot) else {
            return Err(ReplicaError::UnknownVersion);
        };
        let mut seen = op.observed.clone();
        seen.insert(op.dot);
        let mut docs: BTreeMap<String, DocState> = BTreeMap::new();
        for (op_dot, op) in &self.ops {
            if seen.contains(*op_dot) {
                apply_op_to_docs(&mut docs, op);
            }
        }
        Ok(materialize_docs(&docs))
    }

    /// Exports the replica's full state and retained history as one
    /// snapshot — the durable staging form a Member persists locally.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] when the export fails.
    pub fn snapshot_bytes(&self) -> Result<Vec<u8>, ReplicaError> {
        let mut out = Vec::new();
        out.push(EXCHANGE_CODEC_VERSION);
        encode_actor(self.actor, &mut out);
        encode_u64(self.next_counter, &mut out);
        encode_u64(self.clock.last().physical_unix_millis(), &mut out);
        encode_u64(u64::from(self.clock.last().logical()), &mut out);
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
        Ok(out)
    }

    /// Rebuilds one replica state from a persisted snapshot under the
    /// actor identity the caller names — the identity that wrote it on
    /// reopen, the adopting Member's own on a behind-floor adoption.
    /// The rebuilt state is a pure function of the snapshot's
    /// registers and operation set, so every rebuild converges
    /// identically.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ImportFailed`] when the snapshot is
    /// malformed or exceeds the exchange bounds.
    pub fn from_snapshot(actor: ActorId, bytes: &[u8]) -> Result<Self, ReplicaError> {
        let decoded = decode_snapshot(bytes).map_err(|_| ReplicaError::ImportFailed)?;
        let mut replica = Self::new(actor);
        replica.floor = decoded.floor.clone();
        replica.docs = decoded.docs;
        replica.clock = HybridLogicalClock::restore(decoded.clock_last);
        let mut context = decoded.floor;
        for doc in replica.docs.values() {
            for register in doc.fields.values() {
                context.union(register.context());
            }
            for dot in &doc.deletes {
                context.insert(*dot);
            }
        }
        let mut ops = BTreeMap::new();
        for op in decoded.ops {
            context.insert(op.dot);
            context.union(&op.observed);
            ops.insert(op.dot, op);
        }
        // The own-actor counter resumes strictly above everything the
        // rebuilt state already holds for this actor, so a fresh dot is
        // never reused.
        let own_max =
            context.actors().find(|(held, _)| **held == actor).map_or(0_u64, |(_, progress)| {
                let interval_max =
                    progress.intervals().iter().map(|interval| interval.last()).max();
                progress.through().max(interval_max.unwrap_or(0))
            });
        let resumed = if decoded.actor == actor {
            decoded.next_counter.max(own_max.saturating_add(1))
        } else {
            own_max.saturating_add(1)
        };
        replica.next_counter = resumed.max(1);
        replica.context = context;
        replica.ops = ops;
        Ok(replica)
    }

    /// Bounds the retained history: drops every operation the current
    /// frontier already covers and records the frontier as the retained
    /// floor. The materialized state survives unchanged; afterwards
    /// deltas serve only from the new floor
    /// ([`ReplicaDoc::retained_floor`]) and a peer behind it receives
    /// [`ReplicaExchange::BehindRetainedFloor`] and falls back to
    /// snapshot transfer.
    ///
    /// Compaction is only safe below the frontier every live peer
    /// covers: a compacted replica cannot answer a shadow-history read
    /// or serve a delta for a collected operation. Use
    /// [`ReplicaDoc::compact_history_below`] for the guarded form.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] when the compaction
    /// fails. Infallible in this landing.
    pub fn compact_history(&mut self) -> Result<(), ReplicaError> {
        if self.context.actors().next().is_none() {
            return Ok(());
        }
        self.floor = self.context.clone();
        self.ops.clear();
        Ok(())
    }

    /// The guarded compaction: bounds the history only when every live
    /// peer's frontier covers this replica's current frontier, so no
    /// peer can be stranded behind the new floor or hold operations
    /// this replica could never serve again. Returns `false` —
    /// compounding nothing — while any live peer is behind.
    ///
    /// # Errors
    ///
    /// Returns the compaction failures of
    /// [`ReplicaDoc::compact_history`].
    pub fn compact_history_below(&mut self, live: &ReplicaFrontier) -> Result<bool, ReplicaError> {
        let target = self.frontier();
        if !live.covers(&target) {
            return Ok(false);
        }
        if self.context.actors().next().is_none() {
            return Ok(true);
        }
        self.compact_history()?;
        Ok(true)
    }

    /// The oldest frontier this replica can still serve a delta from:
    /// empty before any compaction, the frontier it compacted at after
    /// it.
    #[must_use]
    pub fn retained_floor(&self) -> ReplicaFrontier {
        ReplicaFrontier { context: self.floor.clone() }
    }

    /// Exports every operation this peer itself authored beyond the
    /// retained floor — the piece a behind-floor resync must weigh
    /// across a snapshot adoption, so a peer's own writes survive the
    /// adoption.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] when the export fails.
    pub fn export_own_ops(&self) -> Result<Vec<u8>, ReplicaError> {
        let own: Vec<&ReplicaOp> =
            self.ops.values().filter(|op| op.dot.actor() == self.actor).collect();
        if own.is_empty() {
            return Ok(Vec::new());
        }
        let mut out = Vec::new();
        out.push(EXCHANGE_CODEC_VERSION);
        encode_u64(own.len() as u64, &mut out);
        for op in &own {
            encode_op(op, &mut out);
        }
        Ok(out)
    }

    /// Allocates this replica's next origin counter as a fresh dot.
    fn fresh_dot(&mut self) -> Result<Dot, ReplicaError> {
        let counter =
            OriginCounter::new(self.next_counter).map_err(|_| ReplicaError::OpRejected)?;
        self.next_counter = self.next_counter.checked_add(1).ok_or(ReplicaError::OpRejected)?;
        Ok(Dot::new(self.actor, counter))
    }

    /// Applies one operation to the registers, the context, and the
    /// retained log. Idempotent per dot.
    fn apply_op(&mut self, op: ReplicaOp) {
        if self.ops.contains_key(&op.dot) {
            return;
        }
        self.context.insert(op.dot);
        self.context.union(&op.observed);
        apply_op_to_docs(&mut self.docs, &op);
        self.ops.insert(op.dot, op);
    }
}

/// Folds one operation into a document-state map: the shared join step
/// of local applies, absorbs, and shadow-history replays.
fn apply_op_to_docs(docs: &mut BTreeMap<String, DocState>, op: &ReplicaOp) {
    match &op.mutation {
        ReplicaMutation::Put { document, fields } => {
            let state = docs.entry(document.clone()).or_default();
            for (field, value) in fields {
                let candidate = Candidate::new(
                    value.as_ref().map(ReplicaValue::canonical_text),
                    DottedVersion::new(op.observed.clone(), op.dot),
                    MutationOrdinal::new(0),
                    op.hlc,
                );
                let register = state.fields.entry(field.clone()).or_default();
                *register = register.join(&LwwRegister::from_candidate(candidate));
            }
        }
        ReplicaMutation::Delete { document } => {
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
fn materialize_docs(docs: &BTreeMap<String, DocState>) -> ReplicaScope {
    let mut scope = ReplicaScope::new();
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
                && let Some(value) = ReplicaValue::from_canonical_text(text)
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

/// One replica's causal frontier: the received progress per actor. The
/// wire form anti-entropy exchanges.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ReplicaFrontier {
    context: CausalContext,
}

impl ReplicaFrontier {
    /// Builds a frontier from per-actor contiguous end counters, in
    /// canonical order. A zero end contributes nothing.
    #[must_use]
    pub fn from_ends(ends: Vec<(ActorId, u64)>) -> Self {
        let mut context = CausalContext::new();
        for (actor, end) in ends {
            if end == 0 {
                continue;
            }
            if let Ok(interval) = CounterInterval::new(1, end) {
                let mut progress = ActorProgress::new();
                progress.insert_interval(interval);
                context.insert_progress(actor, &progress);
            }
        }
        Self { context }
    }

    /// Returns the contiguous received prefix per actor, in canonical
    /// actor order.
    #[must_use]
    pub fn ends(&self) -> Vec<(ActorId, u64)> {
        self.context.actors().map(|(actor, progress)| (*actor, progress.through())).collect()
    }

    /// Reports whether this frontier holds every operation `other`
    /// names, at the same counter or later.
    #[must_use]
    pub fn covers(&self, other: &Self) -> bool {
        self.context.dominates(&other.context)
    }

    /// Merges two frontiers into the per-actor maximum — the combined
    /// progress of a peer set.
    #[must_use]
    pub fn merged_max(&self, other: &Self) -> Self {
        let mut context = self.context.clone();
        context.union(&other.context);
        Self { context }
    }

    /// Reports whether this frontier names one dot as received.
    pub(crate) fn holds(&self, dot: Dot) -> bool {
        self.context.contains(dot)
    }

    /// Returns the frontier's causal context — the canonical bytes of
    /// which form stable frontier fingerprints.
    pub(crate) fn context(&self) -> &CausalContext {
        &self.context
    }
}

/// The answer to one delta request.
#[derive(Debug, Eq, PartialEq)]
pub enum ReplicaExchange {
    /// The requester's frontier already covers this replica: nothing
    /// to send.
    UpToDate,
    /// The encoded operation batch the requester is missing.
    Delta(Vec<u8>),
    /// The requester sits behind this replica's retained history floor:
    /// no delta can serve it. The floor is named so the caller falls
    /// back to snapshot transfer.
    BehindRetainedFloor {
        /// The oldest frontier this replica can still serve from.
        floor: ReplicaFrontier,
    },
}

/// The decoded snapshot form of one replica state.
struct DecodedSnapshot {
    actor: ActorId,
    next_counter: u64,
    clock_last: HlcTimestamp,
    floor: CausalContext,
    docs: BTreeMap<String, DocState>,
    ops: Vec<ReplicaOp>,
}

/// Encodes one actor identity: replica bytes then epoch.
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
fn encode_op(op: &ReplicaOp, out: &mut Vec<u8>) {
    encode_dot(op.dot, out);
    encode_u64(op.hlc.physical_unix_millis(), out);
    encode_u64(u64::from(op.hlc.logical()), out);
    op.observed.encode_canonical(out);
    match &op.mutation {
        ReplicaMutation::Put { document, fields } => {
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
        ReplicaMutation::Delete { document } => {
            out.push(2);
            encode_str(document, out);
        }
    }
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

/// A bounded cursor over one exchange or snapshot decode.
struct Reader<'a> {
    input: &'a [u8],
    offset: usize,
    limits: DecodeLimits,
}

impl<'a> Reader<'a> {
    fn new(input: &'a [u8]) -> Result<Self, DecodeError> {
        let limits = exchange_limits();
        if input.len() > limits.max_bytes {
            return Err(DecodeError::LimitExceeded);
        }
        Ok(Self { input, offset: 0, limits })
    }

    fn byte(&mut self) -> Result<u8, DecodeError> {
        let byte = *self.input.get(self.offset).ok_or(DecodeError::UnexpectedEof)?;
        self.offset += 1;
        Ok(byte)
    }

    fn fixed<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        let end = self.offset.checked_add(N).ok_or(DecodeError::LimitExceeded)?;
        let bytes = self.input.get(self.offset..end).ok_or(DecodeError::UnexpectedEof)?;
        self.offset = end;
        bytes.try_into().map_err(|_| DecodeError::UnexpectedEof)
    }

    fn leb_u64(&mut self) -> Result<u64, DecodeError> {
        let mut value = 0_u64;
        for index in 0..10 {
            let byte = self.byte()?;
            let payload = u64::from(byte & 0x7f);
            if index == 9 && (byte & 0x7e) != 0 {
                return Err(DecodeError::InvalidLeb128);
            }
            value |= payload.checked_shl(index * 7).ok_or(DecodeError::InvalidLeb128)?;
            if byte & 0x80 == 0 {
                if index > 0 && payload == 0 {
                    return Err(DecodeError::InvalidLeb128);
                }
                return Ok(value);
            }
        }
        Err(DecodeError::InvalidLeb128)
    }

    fn bytes(&mut self, maximum: usize) -> Result<&'a [u8], DecodeError> {
        let length = usize::try_from(self.leb_u64()?).map_err(|_| DecodeError::LimitExceeded)?;
        if length > maximum {
            return Err(DecodeError::LimitExceeded);
        }
        let end = self.offset.checked_add(length).ok_or(DecodeError::LimitExceeded)?;
        let bytes = self.input.get(self.offset..end).ok_or(DecodeError::UnexpectedEof)?;
        self.offset = end;
        Ok(bytes)
    }

    fn bounded(&mut self, maximum: usize) -> Result<usize, DecodeError> {
        let value = usize::try_from(self.leb_u64()?).map_err(|_| DecodeError::LimitExceeded)?;
        if value > maximum {
            return Err(DecodeError::LimitExceeded);
        }
        Ok(value)
    }

    fn done(&self) -> bool {
        self.offset == self.input.len()
    }
}

/// Decodes one operation batch (a delta or an own-ops export).
fn decode_batch(bytes: &[u8]) -> Result<Vec<ReplicaOp>, DecodeError> {
    let mut reader = Reader::new(bytes)?;
    if reader.byte()? != EXCHANGE_CODEC_VERSION {
        return Err(DecodeError::UnsupportedVersion);
    }
    let count = reader.bounded(MAX_OPS_PER_BATCH)?;
    let mut ops = Vec::with_capacity(count);
    let mut seen_dots = BTreeSet::new();
    for _ in 0..count {
        let op = decode_op(&mut reader)?;
        if !seen_dots.insert(op.dot) {
            return Err(DecodeError::InvalidValue);
        }
        ops.push(op);
    }
    if !reader.done() {
        return Err(DecodeError::TrailingBytes);
    }
    Ok(ops)
}

/// Decodes one operation.
fn decode_op(reader: &mut Reader<'_>) -> Result<ReplicaOp, DecodeError> {
    let dot = decode_dot(reader)?;
    let physical = reader.leb_u64()?;
    let logical = u32::try_from(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?;
    let observed = decode_context(reader)?;
    // A dotted version's event is never part of the context it carries.
    // Otherwise the event is self-causal and replay can vary by arrival.
    if observed.contains(dot) {
        return Err(DecodeError::NonCanonicalContext);
    }
    let mutation = match reader.byte()? {
        1 => {
            let document = decode_str(reader)?;
            let field_count = reader.bounded(reader.limits.max_candidates)?;
            let mut fields = BTreeMap::new();
            for _ in 0..field_count {
                let field = decode_str(reader)?;
                let value = match reader.byte()? {
                    0 => None,
                    1 => Some(parse_value(&decode_str(reader)?)?),
                    _ => return Err(DecodeError::InvalidValue),
                };
                if fields.insert(field, value).is_some() {
                    return Err(DecodeError::InvalidValue);
                }
            }
            ReplicaMutation::Put { document, fields }
        }
        2 => {
            let document = decode_str(reader)?;
            ReplicaMutation::Delete { document }
        }
        _ => return Err(DecodeError::InvalidValue),
    };
    Ok(ReplicaOp { dot, hlc: HlcTimestamp::new(physical, logical), observed, mutation })
}

/// Decodes one dot; its counter must be in the nonzero origin domain.
fn decode_dot(reader: &mut Reader<'_>) -> Result<Dot, DecodeError> {
    let actor = decode_actor(reader)?;
    let counter = OriginCounter::new(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?;
    Ok(Dot::new(actor, counter))
}

/// Decodes one actor identity; its epoch must be nonzero.
fn decode_actor(reader: &mut Reader<'_>) -> Result<ActorId, DecodeError> {
    let replica = ReplicaId::from_bytes(reader.fixed::<16>()?);
    let epoch = ReplicaEpoch::new(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?;
    Ok(ActorId::new(replica, epoch))
}

/// Decodes an actor identity in the canonical causal-context form.
///
/// Replication's enclosing records use compact LEB128 epochs, but an embedded
/// [`CausalContext`] is encoded by `terrabase-causal`, whose actor epoch is a
/// fixed-width big-endian u64 so canonical byte ordering matches actor order.
fn decode_context_actor(reader: &mut Reader<'_>) -> Result<ActorId, DecodeError> {
    let replica = ReplicaId::from_bytes(reader.fixed::<16>()?);
    let epoch = ReplicaEpoch::new(u64::from_be_bytes(reader.fixed::<8>()?))
        .map_err(|_| DecodeError::InvalidValue)?;
    Ok(ActorId::new(replica, epoch))
}

/// Decodes one UTF-8 string under the value bound.
fn decode_str(reader: &mut Reader<'_>) -> Result<String, DecodeError> {
    let bytes = reader.bytes(reader.limits.max_value_bytes)?;
    String::from_utf8(bytes.to_vec()).map_err(|_| DecodeError::InvalidUtf8)
}

/// Decodes the canonical causal-context form: the exact inverse of
/// [`CausalContext::encode_canonical`], including its leading codec
/// version byte.
fn decode_context(reader: &mut Reader<'_>) -> Result<CausalContext, DecodeError> {
    if reader.byte()? != CODEC_VERSION {
        return Err(DecodeError::UnsupportedVersion);
    }
    let actor_count = reader.bounded(reader.limits.max_context_actors)?;
    let mut context = CausalContext::new();
    let mut previous: Option<ActorId> = None;
    for _ in 0..actor_count {
        let actor = decode_context_actor(reader)?;
        if previous.is_some_and(|previous| previous >= actor) {
            return Err(DecodeError::NonCanonicalContext);
        }
        previous = Some(actor);
        let through = reader.leb_u64()?;
        let interval_count = reader.bounded(reader.limits.max_intervals_per_actor)?;
        if through == 0 && interval_count == 0 {
            return Err(DecodeError::NonCanonicalContext);
        }
        let mut progress = ActorProgress::new();
        if through > 0 {
            let interval =
                CounterInterval::new(1, through).map_err(|_| DecodeError::InvalidValue)?;
            progress.insert_interval(interval);
        }
        let mut previous_interval: Option<u64> = None;
        for _ in 0..interval_count {
            let first = reader.leb_u64()?;
            let last = reader.leb_u64()?;
            let interval =
                CounterInterval::new(first, last).map_err(|_| DecodeError::InvalidValue)?;
            if interval.first() <= through
                || previous_interval
                    .is_some_and(|previous| interval.first() <= previous.saturating_add(1))
            {
                return Err(DecodeError::NonCanonicalContext);
            }
            previous_interval = Some(interval.last());
            progress.insert_interval(interval);
        }
        if progress.through() != through || progress.intervals().len() != interval_count {
            return Err(DecodeError::NonCanonicalContext);
        }
        context.insert_progress(actor, &progress);
    }
    Ok(context)
}

/// Parses one field value from its canonical text; malformed text
/// fails the decode.
fn parse_value(text: &str) -> Result<ReplicaValue, DecodeError> {
    ReplicaValue::from_canonical_text(text).ok_or(DecodeError::InvalidValue)
}

/// Decodes one full replica snapshot.
fn decode_snapshot(bytes: &[u8]) -> Result<DecodedSnapshot, DecodeError> {
    let mut reader = Reader::new(bytes)?;
    if reader.byte()? != EXCHANGE_CODEC_VERSION {
        return Err(DecodeError::UnsupportedVersion);
    }
    let actor = decode_actor(&mut reader)?;
    let next_counter = reader.leb_u64()?;
    let physical = reader.leb_u64()?;
    let logical = u32::try_from(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?;
    let floor = decode_context(&mut reader)?;
    let doc_count = reader.bounded(reader.limits.max_candidates)?;
    let mut docs = BTreeMap::new();
    for _ in 0..doc_count {
        let document = decode_str(&mut reader)?;
        let delete_count = reader.bounded(reader.limits.max_candidates)?;
        let mut deletes = Vec::with_capacity(delete_count);
        let mut seen_deletes = BTreeSet::new();
        for _ in 0..delete_count {
            let dot = decode_dot(&mut reader)?;
            if !seen_deletes.insert(dot) {
                return Err(DecodeError::InvalidValue);
            }
            deletes.push(dot);
        }
        let field_count = reader.bounded(reader.limits.max_candidates)?;
        let mut fields = BTreeMap::new();
        for _ in 0..field_count {
            let field = decode_str(&mut reader)?;
            let register_bytes = reader.bytes(reader.limits.max_bytes)?;
            let register = RegisterCodec::decode_lww::<String>(register_bytes, exchange_limits())?;
            if fields.insert(field, register).is_some() {
                return Err(DecodeError::InvalidValue);
            }
        }
        if docs.insert(document, DocState { fields, deletes }).is_some() {
            return Err(DecodeError::InvalidValue);
        }
    }
    let op_count = reader.bounded(MAX_OPS_PER_BATCH)?;
    let mut ops = Vec::with_capacity(op_count);
    let mut seen_dots = BTreeSet::new();
    for _ in 0..op_count {
        let op = decode_op(&mut reader)?;
        if !seen_dots.insert(op.dot) {
            return Err(DecodeError::InvalidValue);
        }
        ops.push(op);
    }
    if !reader.done() {
        return Err(DecodeError::TrailingBytes);
    }
    Ok(DecodedSnapshot {
        actor,
        next_counter,
        clock_last: HlcTimestamp::new(physical, logical),
        floor,
        docs,
        ops,
    })
}

#[cfg(test)]
mod wire_tests {
    use super::*;

    fn operation(dot: Dot, observed: CausalContext) -> ReplicaOp {
        ReplicaOp {
            dot,
            hlc: HlcTimestamp::new(1, 0),
            observed,
            mutation: ReplicaMutation::Delete { document: "doc".to_owned() },
        }
    }

    fn batch(ops: &[ReplicaOp]) -> Vec<u8> {
        let mut bytes = vec![EXCHANGE_CODEC_VERSION];
        encode_u64(ops.len() as u64, &mut bytes);
        for op in ops {
            encode_op(op, &mut bytes);
        }
        bytes
    }

    #[test]
    fn operation_decoder_rejects_self_causal_context() {
        let dot = Dot::new(actor_from_peer(1), OriginCounter::new(1).expect("nonzero counter"));
        let mut observed = CausalContext::new();
        observed.insert(dot);
        assert_eq!(
            decode_batch(&batch(&[operation(dot, observed)])),
            Err(DecodeError::NonCanonicalContext)
        );
    }

    #[test]
    fn operation_decoder_rejects_duplicate_dots_in_one_batch() {
        let dot = Dot::new(actor_from_peer(1), OriginCounter::new(1).expect("nonzero counter"));
        let op = operation(dot, CausalContext::new());
        assert_eq!(decode_batch(&batch(&[op.clone(), op])), Err(DecodeError::InvalidValue));
    }
}
