//! The leaderless multi-writer replication core (P2, ADR 0018).
//!
//! This crate is the Loro CRDT plane that makes every Member of a
//! replica set a peer-writer: each Member materializes accepted writes
//! into its own [`ReplicaDoc`] under its own peer identity, tracks a
//! causal frontier, and exchanges bounded deltas with any other Member
//! — ownership-agnostic, per the frozen leaderless contract
//! (`docs/design/p0-leaderless-replication-contract.md` §5,
//! anti-entropy).
//!
//! The sibling `terrabase-sync-core` crate holds the client sync plane
//! with the opposite shape: one fixed server peer serving consumer
//! sessions. This crate holds the server plane: N named peers, no
//! distinguished writer, convergence by causal merge rather than by
//! election.
//!
//! What this first landing proves over real Loro documents:
//!
//! - concurrent same-document writes through different Members merge
//!   to one deterministic scalar winner with identical materialized
//!   state on every replica (contract §8 scenario 6);
//! - delete wins over a concurrent update, with the loser recoverable
//!   from history — the shadow history (§8 scenario 13);
//! - duplicated and reordered delta delivery converges idempotently
//!   (§8 scenario 11);
//! - a partitioned Member catches up through accumulated deltas after
//!   rejoin (§8 scenario 12's data plane).
//!
//! Honest limits: the durable persistence of a [`ReplicaDoc`] (its
//! snapshot bytes staged through the engine's object-store tier) and
//! the authenticated transport that carries frontiers and deltas are
//! the next landings; this core defines and proves the merge contract
//! over in-memory documents.

use std::collections::BTreeMap;

use loro::{ExportMode, Frontiers, ID, IdSpan, LoroDoc, LoroMap, LoroValue, VersionVector};

pub mod compose;

pub use compose::{
    ComposeError, ComposedAck, ComposedCoordinationError, ComposedMember, ComposedReceipt,
    ComposedWrite, EpochError, EpochReceipt, ReadRepairReport, ReconcileReport, accept_epoch,
    coordinate_composed_write, quorum_read_with_repair,
};

/// The root container holding one entry per document — the same shape
/// `terrabase-sync-core` materializes, so the two planes read the same
/// document bodies.
pub(crate) const DOCS_ROOT: &str = "docs";

/// A typed failure of the replication core. No internal error text
/// crosses the seam: the caller learns the class, not the internals.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReplicaError {
    /// The Member's peer identity was rejected by the CRDT engine.
    PeerIdRejected,
    /// A materializing operation was refused.
    OpRejected,
    /// A delta export failed.
    ExportFailed,
    /// A delta import failed.
    ImportFailed,
    /// A versioned read named an operation this replica never held.
    UnknownVersion,
}

/// One Member's materialized replica state: a Loro document writing
/// under this Member's own peer identity.
///
/// Every Member of the replica set holds the same role over its
/// [`ReplicaDoc`]: it applies accepted writes, answers frontier
/// summaries, exports deltas, and absorbs peers' deltas. Nothing here
/// distinguishes a coordinator — coordination is request-scoped in the
/// engine-side leaderless core (`terrabase-v2::replication::leaderless`).
pub struct ReplicaDoc {
    peer_id: u64,
    doc: LoroDoc,
}

impl ReplicaDoc {
    /// Creates one replica state under a Member's peer identity.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::PeerIdRejected`] when the identity is
    /// not usable as a CRDT peer id.
    pub fn new(peer_id: u64) -> Result<Self, ReplicaError> {
        let doc = LoroDoc::new();
        doc.set_peer_id(peer_id).map_err(|_| ReplicaError::PeerIdRejected)?;
        Ok(Self { peer_id, doc })
    }

    /// Returns the Member's peer identity.
    #[must_use]
    pub const fn peer_id(&self) -> u64 {
        self.peer_id
    }

    /// Materializes one whole-body document put: fields the body names
    /// are set, fields it drops are deleted (whole-body replace, the
    /// same semantics the client sync plane materializes).
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::OpRejected`] when the CRDT engine
    /// refuses an operation.
    pub fn apply_put(
        &mut self,
        document_id: &str,
        body: &BTreeMap<String, LoroValue>,
    ) -> Result<(), ReplicaError> {
        let root = self.doc.get_map(DOCS_ROOT);
        let entry: LoroMap =
            root.ensure_mergeable_map(document_id).map_err(|_| ReplicaError::OpRejected)?;
        for (key, value) in body {
            entry.insert(key, value.clone()).map_err(|_| ReplicaError::OpRejected)?;
        }
        let mut existing: Vec<String> = Vec::new();
        entry.for_each(|key, _| existing.push(key.to_string()));
        for key in existing {
            if !body.contains_key(&key) {
                entry.delete(&key).map_err(|_| ReplicaError::OpRejected)?;
            }
        }
        self.doc.commit();
        Ok(())
    }

    /// Materializes one document deletion. A delete concurrent with a
    /// remote update wins the merge: both replicas materialize the
    /// document absent, and the losing update stays recoverable from
    /// history ([`ReplicaDoc::value_after`]).
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::OpRejected`] when the CRDT engine
    /// refuses the deletion.
    pub fn apply_delete(&mut self, document_id: &str) -> Result<(), ReplicaError> {
        let root = self.doc.get_map(DOCS_ROOT);
        root.delete(document_id).map_err(|_| ReplicaError::OpRejected)?;
        self.doc.commit();
        Ok(())
    }

    /// Returns the replica's causal frontier: every peer identity it
    /// holds operations for, with its last-seen counter. This is the
    /// bounded summary anti-entropy exchanges (contract §5).
    #[must_use]
    pub fn frontier(&self) -> ReplicaFrontier {
        let mut ends: Vec<(u64, i32)> =
            self.doc.oplog_vv().iter().map(|(peer, counter)| (*peer, *counter)).collect();
        ends.sort_unstable();
        ReplicaFrontier { ends }
    }

    /// Exports the delta this replica holds beyond `remote`'s frontier.
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
        let floor = self.retained_floor();
        if !floor.ends().is_empty() && !remote.covers(&floor) {
            return Ok(ReplicaExchange::BehindRetainedFloor { floor });
        }
        let bytes = self
            .doc
            .export(ExportMode::updates(&remote.to_version_vector()))
            .map_err(|_| ReplicaError::ExportFailed)?;
        Ok(ReplicaExchange::Delta(bytes))
    }

    /// Absorbs a peer's delta. Idempotent: importing operations the
    /// replica already holds applies nothing; importing out of order is
    /// safe — the CRDT engine orders by causality, not arrival.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ImportFailed`] when the import fails.
    pub fn absorb(&mut self, bytes: &[u8]) -> Result<(), ReplicaError> {
        self.doc.import(bytes).map_err(|_| ReplicaError::ImportFailed)?;
        Ok(())
    }

    /// Returns the materialized state of the whole scope — the
    /// convergence observation surface.
    #[must_use]
    pub fn deep_value(&self) -> LoroValue {
        self.doc.get_deep_value()
    }

    /// Returns the materialized state as of just after one operation,
    /// by its peer and counter — the shadow-history read that recovers
    /// a merge loser.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::UnknownVersion`] when this replica
    /// cannot fork at that operation.
    pub fn value_after(&self, peer: u64, counter: i32) -> Result<LoroValue, ReplicaError> {
        let forked = self
            .doc
            .fork_at(&Frontiers::from_id(ID::new(peer, counter)))
            .map_err(|_| ReplicaError::UnknownVersion)?;
        Ok(forked.get_deep_value())
    }

    /// Exports the replica's full state and history as one snapshot —
    /// the durable staging form a Member persists locally.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] when the export fails.
    pub fn snapshot_bytes(&self) -> Result<Vec<u8>, ReplicaError> {
        self.doc.export(ExportMode::Snapshot).map_err(|_| ReplicaError::ExportFailed)
    }

    /// Rebuilds one replica state from a persisted snapshot under the
    /// same peer identity that wrote it.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::PeerIdRejected`] or
    /// [`ReplicaError::ImportFailed`] when the identity or snapshot is
    /// refused.
    pub fn from_snapshot(peer_id: u64, bytes: &[u8]) -> Result<Self, ReplicaError> {
        let replica = Self::new(peer_id)?;
        replica.doc.import(bytes).map_err(|_| ReplicaError::ImportFailed)?;
        Ok(Self { peer_id, doc: replica.doc })
    }

    /// Bounds the retained history: compacts the document to a shallow
    /// snapshot at the current frontier. Afterwards deltas serve only
    /// from the new floor ([`ReplicaDoc::retained_floor`]) and a peer
    /// behind it receives [`ReplicaExchange::BehindRetainedFloor`] and
    /// falls back to snapshot transfer.
    ///
    /// Compaction is only safe below the frontier every live peer
    /// covers: a compacted replica cannot absorb an operation whose
    /// causal context reaches into the shallow region. Use
    /// [`ReplicaDoc::compact_history_below`] for the guarded form.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] or
    /// [`ReplicaError::ImportFailed`] when the compaction export or
    /// re-import fails.
    pub fn compact_history(&mut self) -> Result<(), ReplicaError> {
        let current_end = self.own_end();
        if current_end == 0 && self.frontier().ends().is_empty() {
            return Ok(());
        }
        // Compact at the document's actual heads — after absorbing
        // peers' operations the latest op is not necessarily this
        // peer's own, and only a valid frontier cut shallow-exports.
        let shallow = self
            .doc
            .export(ExportMode::shallow_snapshot_owned(self.doc.oplog_frontiers()))
            .map_err(|_| ReplicaError::ExportFailed)?;
        let compacted = LoroDoc::new();
        compacted.set_peer_id(self.peer_id).map_err(|_| ReplicaError::PeerIdRejected)?;
        compacted.import(&shallow).map_err(|_| ReplicaError::ImportFailed)?;
        self.doc = compacted;
        Ok(())
    }

    /// The guarded compaction: bounds the history only when every live
    /// peer's frontier covers this replica's current frontier, so no
    /// peer can be stranded behind the new floor or hold operations
    /// this replica could never absorb again. Returns `false` —
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
        if target.ends().is_empty() {
            return Ok(true);
        }
        self.compact_history()?;
        Ok(true)
    }

    /// The oldest frontier this replica can still serve a delta from:
    /// empty before any compaction, the shallow root's floor after it.
    #[must_use]
    pub fn retained_floor(&self) -> ReplicaFrontier {
        let mut ends: Vec<(u64, i32)> =
            self.doc.shallow_since_vv().iter().map(|(peer, end)| (*peer, *end)).collect();
        ends.sort_unstable();
        ReplicaFrontier { ends }
    }

    /// This peer's own end counter (the last held counter plus one).
    fn own_end(&self) -> i32 {
        self.doc.oplog_vv().get_last(self.peer_id).map_or(0, |counter| counter + 1)
    }

    /// Exports every operation this peer itself authored — the piece a
    /// behind-floor resync must carry across a snapshot adoption, so a
    /// peer's own writes survive the adoption.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] when the export fails.
    pub fn export_own_ops(&self) -> Result<Vec<u8>, ReplicaError> {
        let end = self.own_end();
        if end == 0 {
            return Ok(Vec::new());
        }
        self.doc
            .export(ExportMode::updates_in_range(vec![IdSpan::new(self.peer_id, 0, end)]))
            .map_err(|_| ReplicaError::ExportFailed)
    }
}

/// One replica's causal frontier: the per-peer operation counters it
/// holds. The wire form anti-entropy exchanges.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ReplicaFrontier {
    ends: Vec<(u64, i32)>,
}

impl ReplicaFrontier {
    /// Builds a frontier from per-peer ends, in canonical order.
    #[must_use]
    pub fn from_ends(mut ends: Vec<(u64, i32)>) -> Self {
        ends.sort_unstable();
        ends.dedup();
        Self { ends }
    }

    /// Returns the per-peer ends, in canonical order.
    #[must_use]
    pub fn ends(&self) -> &[(u64, i32)] {
        &self.ends
    }

    /// Reports whether this frontier holds every operation `other`
    /// names, at the same counter or later.
    #[must_use]
    pub fn covers(&self, other: &ReplicaFrontier) -> bool {
        other.ends.iter().all(|(peer, end)| {
            self.ends.iter().any(|(held_peer, held_end)| held_peer == peer && held_end >= end)
        })
    }

    /// Merges two frontiers into the per-peer maximum — the combined
    /// progress of a peer set.
    #[must_use]
    pub fn merged_max(&self, other: &ReplicaFrontier) -> ReplicaFrontier {
        let mut ends = self.ends.clone();
        for (peer, end) in &other.ends {
            match ends.iter_mut().find(|(held_peer, _)| held_peer == peer) {
                Some(held) => {
                    if *end > held.1 {
                        held.1 = *end;
                    }
                }
                None => ends.push((*peer, *end)),
            }
        }
        Self::from_ends(ends)
    }

    fn to_version_vector(&self) -> VersionVector {
        VersionVector::from_iter(self.ends.iter().copied())
    }
}

/// The answer to one delta request.
#[derive(Debug, Eq, PartialEq)]
pub enum ReplicaExchange {
    /// The requester's frontier already covers this replica: nothing
    /// to send.
    UpToDate,
    /// The delta bytes the requester is missing.
    Delta(Vec<u8>),
    /// The requester sits behind this replica's retained history floor:
    /// no delta can serve it. The floor is named so the caller falls
    /// back to snapshot transfer.
    BehindRetainedFloor {
        /// The oldest frontier this replica can still serve from.
        floor: ReplicaFrontier,
    },
}
