//! Engine seams for the simulation harness (E-12).
//!
//! The real replication engine (E-5) and sync engine (E-6) plug in here by
//! implementing [`ReplicationEngine`] and [`SyncEngine`]. The invariant
//! tests in `tests/invariants.rs` speak only to these traits, so swapping
//! the [`crate::world::StubWorld`] for engine adapters changes no test.
//! Single-threaded and infallible by design: simulated time advances in
//! discrete steps, and every operation is total over the stub world.

use crate::model::{Frontier, JournalRow, RegionId, SessionToken, ViewCut};

/// Cost statistics for one pairwise reconciliation.
///
/// The harness asserts invariant 8 against these: `rounds` must stay
/// `O(log n)` in the database size and `rows_shipped` must equal the
/// number of actually differing rows.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ReconcileStats {
    /// Fingerprint-exchange rounds until the differing ranges were isolated.
    pub rounds: usize,
    /// Journal rows shipped through the normal apply path.
    pub rows_shipped: usize,
}

impl ReconcileStats {
    /// Creates statistics for a reconciliation that converged.
    #[must_use]
    pub fn new(rounds: usize, rows_shipped: usize) -> Self {
        Self { rounds, rows_shipped }
    }
}

/// The replication-engine seam (ADR 0044 §2): causal+ journal tailing,
/// convergent merge, durability classes, session guarantees, anti-entropy.
pub trait ReplicationEngine {
    /// Submits a document write at `region`; returns the committed journal
    /// row, or `None` when the region is down and the write is refused
    /// (never acknowledged-then-lost: invariant 1).
    fn submit(&mut self, region: RegionId, key: u64, value: u64) -> Option<JournalRow>;

    /// Submits a document delete at `region`; same refusal contract as
    /// [`ReplicationEngine::submit`].
    fn delete(&mut self, region: RegionId, key: u64) -> Option<JournalRow>;

    /// Advances simulated time by one step: delivers due rows subject to
    /// the active faults and applies dependency-ready rows.
    fn step(&mut self);

    /// Returns the applied frontier of `region`.
    fn applied_frontier(&self, region: RegionId) -> Frontier;

    /// Returns the additive reconciliation fingerprint of `region`
    /// (invariant 2 compares these at quiescence).
    fn fingerprint(&self, region: RegionId) -> u64;

    /// Runs pairwise range-based reconciliation between two regions and
    /// reports its cost (invariant 8).
    fn reconcile(&mut self, left: RegionId, right: RegionId) -> ReconcileStats;

    /// Returns `true` when `row` and all its causal dependencies are
    /// present in every selected region (the `GlobalDurable` condition of
    /// invariant 4).
    fn is_uniform(&self, row: &JournalRow, selected: &[RegionId]) -> bool;

    /// Serves a point read for a client carrying `token`: `Some` value on
    /// success, `None` when the region is behind the token (`StaleReplica`,
    /// invariant 5). The second element reports whether the key exists.
    fn read(&self, region: RegionId, token: &SessionToken, key: u64) -> Option<Option<u64>>;

    /// Applies `rows` in the given order through the normal apply path and
    /// returns the converged-state digest (invariant 3 permutes the order).
    fn digest_after_permutation(&mut self, rows: &[JournalRow]) -> u64;

    /// Returns `true` when every in-flight row is delivered and every
    /// region's dependency queue is empty.
    fn is_quiescent(&self) -> bool;
}

/// One immutable view-log revision (ADR 0044 §3.6).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ViewRevision {
    /// The view this revision belongs to.
    pub view: String,
    /// The authorization generation (rule revision + entitlement epoch).
    pub generation: u64,
    /// The journal cut the revision was computed from.
    pub cut: ViewCut,
    /// Deterministic digest of the revision payload.
    pub payload_digest: u64,
    /// The encoded revision bytes (byte-identical per key at origin).
    pub bytes: Vec<u8>,
}

impl ViewRevision {
    /// Creates a revision for `view` at `cut` with the given payload.
    #[must_use]
    pub fn new(
        view: String,
        generation: u64,
        cut: ViewCut,
        payload_digest: u64,
        bytes: Vec<u8>,
    ) -> Self {
        Self { view, generation, cut, payload_digest, bytes }
    }
}

/// The sync-engine seam (ADR 0044 §3): shared views as Z-set streams with
/// checkpoints, resume/reset, and encode-once cached delivery.
pub trait SyncEngine {
    /// Returns the current revision of `view` as served by `region`.
    fn view_revision(&self, view: &str, region: RegionId) -> Option<ViewRevision>;

    /// Recomputes `Q(database at cut)` from the region snapshot and
    /// returns `true` when it equals the served revision (invariant 6).
    fn verify_revision(&self, view: &str, region: RegionId) -> bool;

    /// Returns the cached log bytes for `(view, generation, offset)` when
    /// `capability` authorizes them, else `None` (invariant 7: byte
    /// equality across the cache and no crossing of capability boundaries).
    fn cached_bytes(
        &self,
        view: &str,
        generation: u64,
        offset: u64,
        capability: u64,
    ) -> Option<Vec<u8>>;
}
