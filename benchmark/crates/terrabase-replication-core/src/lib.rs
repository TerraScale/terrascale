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

use loro::{ExportMode, Frontiers, ID, LoroDoc, LoroMap, LoroValue, VersionVector};

pub mod compose;

pub use compose::{ComposeError, ComposedMember, ReconcileReport};

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
    /// # Errors
    ///
    /// Returns [`ReplicaError::ExportFailed`] when the export fails.
    pub fn delta_for(&self, remote: &ReplicaFrontier) -> Result<ReplicaExchange, ReplicaError> {
        if remote.covers(&self.frontier()) {
            return Ok(ReplicaExchange::UpToDate);
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
}
