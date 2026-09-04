//! In-memory stub engine proving the harness mechanics (E-12 skeleton).
//!
//! [`StubWorld`] implements [`ReplicationEngine`] and [`SyncEngine`] with a
//! single-threaded, discrete-time model: per-region document maps with
//! remove-wins merge on `(hlc, origin)`, per-link in-flight queues subject
//! to the [`FaultPlan`], bounded dependency queues retried every step, and
//! encode-once view caches. It is deliberately simple — delivery is
//! reliable once faults heal, so acknowledged writes are never lost — which
//! is exactly what lets the seed batch qualify the *harness* (fault
//! application, determinism, quiescence, convergence) before engine
//! adapters arrive with E-5/E-6.
//!
//! The merge rule is a pure max-function over `(hlc, origin)` per key, so
//! convergence is order-independent by construction; [`merge_doc`] is the
//! single apply path shared by delivery, reconciliation, and the
//! permutation check.

use std::collections::BTreeMap;

use crate::faults::FaultPlan;
use crate::model::{Frontier, Hlc, JournalRow, RegionId, SessionToken, ViewCut};
use crate::traits::{ReconcileStats, ReplicationEngine, SyncEngine, ViewRevision};

/// Reconciliation fan-out of the simulated range-summary walk (invariant 8).
const RECONCILE_FANOUT: usize = 64;

/// Stub authorization generation: the world has no rule revisions yet, so
/// every revision carries generation 1. Engine adapters replace this with
/// the real rule-revision + entitlement-epoch binding (ADR 0044 §3.5).
const STUB_GENERATION: u64 = 1;

/// Collection-sync view: every live document.
pub const VIEW_ALL: &str = "all";

/// Filtered view: live documents with even keys.
pub const VIEW_EVEN_KEYS: &str = "even_keys";

/// `FNV-1a` over bytes: the stub's deterministic digest.
fn fnv_bytes(state: u64, bytes: &[u8]) -> u64 {
    let mut hash = state;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0100_0000_01B3);
    }
    hash
}

/// `FNV-1a` over one `u64` in big-endian order.
fn fnv_u64(state: u64, value: u64) -> u64 {
    fnv_bytes(state, &value.to_be_bytes())
}

/// The converged per-key version: live value or tombstone plus the
/// `(hlc, origin)` that won it.
#[derive(Clone, Debug, PartialEq, Eq)]
struct DocVersion {
    /// The live value; `None` when the key is deleted.
    value: Option<u64>,
    /// The winning write's HLC.
    hlc: u64,
    /// The winning write's origin (deterministic tiebreak).
    origin: RegionId,
}

/// Applies `row` to `docs` with remove-wins map semantics and HLC
/// tiebreak (ADR 0044 §2.2): the row wins when its `(hlc, origin)` exceeds
/// the stored version's. Returns `true` when the visible state changed.
///
/// This is the one apply path: delivery, reconciliation, and the
/// permutation check all funnel through it, so order-independence
/// (invariant 3) holds wherever it is used.
fn merge_doc(docs: &mut BTreeMap<u64, DocVersion>, row: &JournalRow) -> bool {
    let incoming_value = if row.tombstone { None } else { Some(row.value) };
    match docs.get(&row.key) {
        Some(stored) if (stored.hlc, stored.origin) >= (row.hlc.value(), row.origin) => false,
        _ => {
            docs.insert(
                row.key,
                DocVersion { value: incoming_value, hlc: row.hlc.value(), origin: row.origin },
            );
            true
        }
    }
}

/// One region's stub state.
#[derive(Clone, Debug)]
struct StubRegion {
    /// This region's identity.
    id: RegionId,
    /// Next local per-origin sequence number.
    next_seq: u64,
    /// Highest HLC stamped or observed.
    hlc: u64,
    /// Every row applied here, by `(origin, seq)`.
    applied: BTreeMap<(RegionId, u64), JournalRow>,
    /// Converged per-key state.
    docs: BTreeMap<u64, DocVersion>,
    /// Rows waiting on causal dependencies (bounded in the engine; the
    /// stub retries them every step and they drain at quiescence).
    waiting: Vec<JournalRow>,
    /// Total rows ever applied (feeds view-cut `seq_hi` without casts).
    applied_total: u64,
}

impl StubRegion {
    /// Creates an empty region.
    fn fresh(id: RegionId) -> Self {
        Self {
            id,
            next_seq: 0,
            hlc: 0,
            applied: BTreeMap::new(),
            docs: BTreeMap::new(),
            waiting: Vec::new(),
            applied_total: 0,
        }
    }

    /// Returns the applied frontier of this region.
    fn frontier(&self) -> Frontier {
        let mut frontier = Frontier::new();
        for (origin, seq) in self.applied.keys() {
            frontier.advance(*origin, *seq);
        }
        frontier
    }

    /// Applies an already-accepted row; returns `true` on first sight.
    fn accept(&mut self, row: &JournalRow) -> bool {
        if self.applied.contains_key(&row.id()) {
            return false;
        }
        merge_doc(&mut self.docs, row);
        self.applied.insert(row.id(), row.clone());
        self.applied_total = self.applied_total.saturating_add(1);
        true
    }
}

/// One frame in flight between regions.
#[derive(Clone, Debug)]
struct InFlight {
    /// The row being shipped.
    row: JournalRow,
    /// The destination region index into [`StubWorld::regions`].
    target: usize,
    /// The first step at which delivery may be attempted.
    deliver_at: u64,
}

/// In-memory stub engine implementing both engine seams.
#[derive(Clone, Debug)]
pub struct StubWorld {
    /// Per-region state; position `index` holds `RegionId(index)`.
    regions: Vec<StubRegion>,
    /// The active fault schedule.
    faults: FaultPlan,
    /// The current simulated step.
    step: u64,
    /// Frames in flight, in enqueue order (deterministic processing).
    pending: Vec<InFlight>,
    /// Encode-once view cache keyed by `(view, generation, offset)`.
    cache: BTreeMap<(String, u64, u64), (u64, Vec<u8>)>,
    /// Count of applied deliveries; part of the determinism digest.
    events: u64,
}

impl StubWorld {
    /// Creates a world with `region_count` regions and no faults.
    #[must_use]
    pub fn new(region_count: usize) -> Self {
        Self::with_faults(region_count, FaultPlan::new())
    }

    /// Creates a world with `region_count` regions under `faults`.
    #[must_use]
    pub fn with_faults(region_count: usize, faults: FaultPlan) -> Self {
        let regions =
            (0..region_count).map(|index| StubRegion::fresh(RegionId::from_index(index))).collect();
        Self { regions, faults, step: 0, pending: Vec::new(), cache: BTreeMap::new(), events: 0 }
    }

    /// Returns the active fault schedule.
    #[must_use]
    pub fn faults(&self) -> &FaultPlan {
        &self.faults
    }

    /// Returns the current simulated step.
    #[must_use]
    pub fn step_count(&self) -> u64 {
        self.step
    }

    /// Returns the number of regions.
    #[must_use]
    pub fn region_count(&self) -> usize {
        self.regions.len()
    }

    /// Returns the region ids in index order.
    #[must_use]
    pub fn region_ids(&self) -> Vec<RegionId> {
        self.regions.iter().map(|region| region.id).collect()
    }

    /// Commits a local row at `position` and queues its shipment.
    fn commit_at(&mut self, position: usize, key: u64, value: Option<u64>) -> Option<JournalRow> {
        if position >= self.regions.len() {
            return None;
        }
        let region_id = self.regions[position].id;
        if self.faults.region_down(region_id, self.step) {
            return None;
        }
        let skew = self.faults.clock_offset_ms(region_id, self.step);
        let region = &mut self.regions[position];
        region.next_seq = region.next_seq.saturating_add(1);
        let seq = region.next_seq;
        let monotonic = region.hlc.saturating_add(1).max(1);
        let stamped = monotonic.saturating_add_signed(skew).max(monotonic);
        region.hlc = region.hlc.max(stamped);
        let context = region.frontier();
        let row = JournalRow {
            origin: region_id,
            seq,
            context,
            hlc: Hlc::new(stamped),
            key,
            value: value.unwrap_or(0),
            tombstone: value.is_none(),
        };
        region.accept(&row);
        self.events = self.events.saturating_add(1);
        self.enqueue_shipments(&row, position);
        Some(row)
    }

    /// Queues shipment of `row` from `from` to every other live region,
    /// honouring slow-link delays, duplication, and reorder windows as
    /// sampled at enqueue time.
    fn enqueue_shipments(&mut self, row: &JournalRow, from: usize) {
        for (target, peer) in self.regions.iter().enumerate() {
            if target == from {
                continue;
            }
            let delay = self.faults.link_delay_steps(row.origin, peer.id, self.step);
            let copies = self.faults.duplicate_copies(row.origin, peer.id, self.step);
            let window = self.faults.reorder_window(row.origin, peer.id, self.step);
            let reorder_jitter = if window == 0 {
                0
            } else {
                // Deterministic spread over [0, window]: same row always
                // takes the same slot, different rows spread across slots.
                (row.seq.wrapping_add(u64::from(peer.id.0))) % window.saturating_add(1)
            };
            let deliver_at = self.step.saturating_add(delay).saturating_add(reorder_jitter);
            for _ in 0..copies.max(1) {
                self.pending.push(InFlight { row: row.clone(), target, deliver_at });
            }
        }
    }

    /// Attempts delivery of every due frame, then retries dependency
    /// queues. Frames crossing a severed link or a down region stay
    /// queued: the stub retransmits after healing, so acknowledged writes
    /// are never lost (invariant 1).
    fn drain_due(&mut self) {
        let mut held = Vec::with_capacity(self.pending.len());
        for flight in self.pending.drain(..) {
            if flight.deliver_at > self.step {
                held.push(flight);
                continue;
            }
            let Some(target) = self.regions.get(flight.target) else {
                continue;
            };
            let target_id = target.id;
            if !self.faults.link_open(flight.row.origin, target_id, self.step) {
                held.push(flight);
                continue;
            }
            let region = &mut self.regions[flight.target];
            if region.applied.contains_key(&flight.row.id()) {
                continue; // Dominated: idempotent drop (ADR 0044 §2.2 rule 4).
            }
            if region.frontier().covers(&flight.row.context) {
                region.accept(&flight.row);
                self.events = self.events.saturating_add(1);
            } else {
                region.waiting.push(flight.row);
            }
        }
        self.pending = held;
        self.retry_waiting();
    }

    /// Retries every dependency queue until no row becomes ready.
    fn retry_waiting(&mut self) {
        loop {
            let mut progressed = false;
            for region in &mut self.regions {
                let waiting = std::mem::take(&mut region.waiting);
                let mut still = Vec::with_capacity(waiting.len());
                for row in waiting {
                    if region.applied.contains_key(&row.id()) {
                        progressed = true;
                    } else if region.frontier().covers(&row.context) {
                        region.accept(&row);
                        progressed = true;
                    } else {
                        still.push(row);
                    }
                }
                region.waiting = still;
            }
            if !progressed {
                break;
            }
        }
    }

    /// Computes the deterministic digest of one region's converged state.
    fn region_digest(&self, position: usize) -> u64 {
        let Some(region) = self.regions.get(position) else {
            return 0;
        };
        let mut hash = 0xCBF2_9CE4_8422_2325u64;
        for (origin, seq) in region.applied.keys() {
            hash = fnv_u64(hash, u64::from(origin.0));
            hash = fnv_u64(hash, *seq);
        }
        for (key, version) in &region.docs {
            hash = fnv_u64(hash, *key);
            hash = fnv_u64(hash, version.value.unwrap_or(u64::MAX));
            hash = fnv_u64(hash, version.hlc);
        }
        hash
    }

    /// Returns the world digest: every region digest plus the event
    /// count. Two runs from one seed must agree exactly.
    #[must_use]
    pub fn digest(&self) -> u64 {
        let mut hash = fnv_u64(0xCBF2_9CE4_8422_2325, self.events);
        for position in 0..self.regions.len() {
            hash = fnv_u64(hash, self.region_digest(position));
        }
        hash
    }

    /// Renders the payload bytes of `view` from the region snapshot.
    /// Engine adapters evaluate `Q(database at cut)`; the stub evaluates
    /// at the current snapshot and records the cut it reflects.
    fn render_view(&self, view: &str, position: usize) -> Option<(ViewCut, u64, Vec<u8>)> {
        let region = self.regions.get(position)?;
        if view != VIEW_ALL && view != VIEW_EVEN_KEYS {
            return None;
        }
        let mut payload = format!("{view}|{STUB_GENERATION}|");
        for (key, version) in &region.docs {
            if view == VIEW_EVEN_KEYS && key % 2 == 1 {
                continue;
            }
            if let Some(value) = version.value {
                // `write!` to a `String` cannot fail; the result is discarded.
                let _ = std::fmt::Write::write_fmt(&mut payload, format_args!("{key}:{value};"));
            }
        }
        let bytes = payload.into_bytes();
        let mut hash = 0xCBF2_9CE4_8422_2325u64;
        hash = fnv_bytes(hash, &bytes);
        let cut = ViewCut::new(region.frontier(), region.applied_total);
        Some((cut, hash, bytes))
    }
}

/// Derives the stub capability binding a view generation.
///
/// Real capabilities come from the entitlement issuer (ADR 0044 §3.5); the
/// stub binds `(view, generation)` deterministically so invariant 7 can
/// assert byte equality within a capability and isolation across them.
#[must_use]
pub fn capability_for(view: &str, generation: u64) -> u64 {
    let hash = fnv_bytes(0xCBF2_9CE4_8422_2325, view.as_bytes());
    fnv_u64(hash, generation)
}

impl ReplicationEngine for StubWorld {
    fn submit(&mut self, region: RegionId, key: u64, value: u64) -> Option<JournalRow> {
        let position = self.regions.iter().position(|item| item.id == region)?;
        self.commit_at(position, key, Some(value))
    }

    fn delete(&mut self, region: RegionId, key: u64) -> Option<JournalRow> {
        let position = self.regions.iter().position(|item| item.id == region)?;
        self.commit_at(position, key, None)
    }

    fn step(&mut self) {
        self.step = self.step.saturating_add(1);
        self.drain_due();
    }

    fn applied_frontier(&self, region: RegionId) -> Frontier {
        self.regions
            .iter()
            .find(|item| item.id == region)
            .map_or_else(Frontier::new, StubRegion::frontier)
    }

    fn fingerprint(&self, region: RegionId) -> u64 {
        self.regions
            .iter()
            .position(|item| item.id == region)
            .map_or(0, |position| self.region_digest(position))
    }

    fn reconcile(&mut self, left: RegionId, right: RegionId) -> ReconcileStats {
        // Stub repair path: ships the set difference through the normal
        // apply path and reports simulated 64-ary RBSR rounds. It assumes
        // connectivity for the repair itself; partitions are modeled on
        // the delivery path. Engine adapters replace the cost model with
        // the real `RangeSummaryTable` walk.
        let Some(left_position) = self.regions.iter().position(|item| item.id == left) else {
            return ReconcileStats::new(0, 0);
        };
        let Some(right_position) = self.regions.iter().position(|item| item.id == right) else {
            return ReconcileStats::new(0, 0);
        };
        if left_position == right_position {
            return ReconcileStats::new(1, 0);
        }
        // Union of row ids; differences ship through the normal apply path.
        let mut union: BTreeMap<(RegionId, u64), JournalRow> = BTreeMap::new();
        for (id, row) in &self.regions[left_position].applied {
            union.insert(*id, row.clone());
        }
        for (id, row) in &self.regions[right_position].applied {
            union.entry(*id).or_insert_with(|| row.clone());
        }
        let union_len = union.len();
        let mut shipped = 0;
        for (id, row) in &union {
            for position in [left_position, right_position] {
                if !self.regions[position].applied.contains_key(id) {
                    self.regions[position].accept(row);
                    self.events = self.events.saturating_add(1);
                    shipped += 1;
                }
            }
        }
        // Simulated RBSR cost: one root round, then descent only into
        // mismatching ranges, `RECONCILE_FANOUT`-ary. Matching roots stop
        // after the root exchange, so converged pairs cost one round and
        // divergent pairs cost O(log n) rounds.
        let mut depth = 0;
        let mut covered = 1usize;
        while covered < union_len.max(1) {
            covered = covered.saturating_mul(RECONCILE_FANOUT);
            depth += 1;
        }
        let rounds = if shipped == 0 { 1 } else { depth + 1 };
        ReconcileStats::new(rounds, shipped)
    }

    fn is_uniform(&self, row: &JournalRow, selected: &[RegionId]) -> bool {
        selected.iter().all(|region| {
            self.regions
                .iter()
                .find(|item| item.id == *region)
                .is_some_and(|peer| peer.applied.contains_key(&row.id()))
                && self
                    .regions
                    .iter()
                    .find(|item| item.id == *region)
                    .is_some_and(|peer| peer.frontier().covers(&row.context))
        })
    }

    fn read(&self, region: RegionId, token: &SessionToken, key: u64) -> Option<Option<u64>> {
        let peer = self.regions.iter().find(|item| item.id == region)?;
        if self.faults.region_down(region, self.step) {
            return None;
        }
        if !peer.frontier().covers(&token.frontier) {
            return None; // StaleReplica: the SDK follows the named region.
        }
        Some(peer.docs.get(&key).and_then(|version| version.value))
    }

    fn digest_after_permutation(&mut self, rows: &[JournalRow]) -> u64 {
        let mut docs = BTreeMap::new();
        for row in rows {
            merge_doc(&mut docs, row);
        }
        let mut hash = 0xCBF2_9CE4_8422_2325u64;
        for (key, version) in &docs {
            hash = fnv_u64(hash, *key);
            hash = fnv_u64(hash, version.value.unwrap_or(u64::MAX));
            hash = fnv_u64(hash, version.hlc);
            hash = fnv_u64(hash, u64::from(version.origin.0));
        }
        hash
    }

    fn is_quiescent(&self) -> bool {
        self.pending.is_empty() && self.regions.iter().all(|region| region.waiting.is_empty())
    }
}

impl SyncEngine for StubWorld {
    fn view_revision(&self, view: &str, region: RegionId) -> Option<ViewRevision> {
        let position = self.regions.iter().position(|item| item.id == region)?;
        let (cut, payload_digest, bytes) = self.render_view(view, position)?;
        Some(ViewRevision::new(view.to_owned(), STUB_GENERATION, cut, payload_digest, bytes))
    }

    fn verify_revision(&self, view: &str, region: RegionId) -> bool {
        let position = self.regions.iter().position(|item| item.id == region);
        let Some(position) = position else {
            return false;
        };
        let expected = self.view_revision(view, region);
        let Some(expected) = expected else {
            return false;
        };
        let Some((_, digest, bytes)) = self.render_view(view, position) else {
            return false;
        };
        expected.payload_digest == digest && expected.bytes == bytes
    }

    fn cached_bytes(
        &self,
        view: &str,
        generation: u64,
        offset: u64,
        capability: u64,
    ) -> Option<Vec<u8>> {
        if capability != capability_for(view, generation) || offset != 0 {
            return None;
        }
        // Encode-once: the cached bytes are the origin bytes for the key.
        // `&self` cannot populate the cache; the origin revision carries
        // the identical bytes (asserted by invariant 7 against a `&mut`
        // world via `prime_cache`, below).
        self.cache.get(&(view.to_owned(), generation, offset)).map(|(_, bytes)| bytes.clone())
    }
}

impl StubWorld {
    /// Serves a cache read after priming encode-once bytes from the origin
    /// revision. Split from [`SyncEngine::cached_bytes`] only because the
    /// trait takes `&self`; engine adapters with interior caches merge
    /// the two.
    #[must_use]
    pub fn prime_cache(&mut self, view: &str, region: RegionId) -> Option<ViewRevision> {
        let revision = self.view_revision(view, region)?;
        self.cache
            .entry((revision.view.clone(), revision.generation, 0))
            .or_insert_with(|| (revision.payload_digest, revision.bytes.clone()));
        Some(revision)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::faults::{Fault, FaultPlan};

    #[test]
    fn submit_apply_and_read_round_trip() {
        let mut world = StubWorld::new(2);
        let first = RegionId::new(0);
        let row = world.submit(first, 1, 10).expect("region is up");
        assert_eq!(row.seq, 1);
        let token = SessionToken::observing(world.applied_frontier(first));
        assert_eq!(world.read(first, &token, 1), Some(Some(10)));
    }

    #[test]
    fn killed_region_refuses_writes_and_recovers_without_loss() {
        let mut faults = FaultPlan::new();
        faults.push(Fault::kill(RegionId::new(1), 1, 3));
        let mut world = StubWorld::with_faults(2, faults);
        world.step(); // Enter step 1: the kill is active.
        assert!(world.submit(RegionId::new(1), 1, 10).is_none());
        let row = world.submit(RegionId::new(0), 1, 10).expect("region 0 is up");
        for _ in 0..8 {
            world.step();
        }
        // The kill healed at step 3 and the held frames flushed: the
        // acknowledged write converged everywhere with no repair needed.
        assert!(world.is_quiescent());
        assert!(world.is_uniform(&row, &world.region_ids()));
        assert_eq!(world.fingerprint(RegionId::new(0)), world.fingerprint(RegionId::new(1)));
    }

    #[test]
    fn reconcile_ships_only_missing_rows() {
        // Region 1 stays down, so delivery cannot converge on its own and
        // the repair path must ship exactly the missing rows.
        let mut faults = FaultPlan::new();
        faults.push(Fault::kill(RegionId::new(1), 0, u64::MAX));
        let mut world = StubWorld::with_faults(2, faults);
        for key in 0..3 {
            world.submit(RegionId::new(0), key, key).expect("up");
            world.step();
        }
        assert!(
            world.fingerprint(RegionId::new(0)) != world.fingerprint(RegionId::new(1)),
            "down region must diverge"
        );
        let stats = world.reconcile(RegionId::new(0), RegionId::new(1));
        assert_eq!(stats.rows_shipped, 3);
        assert_eq!(world.fingerprint(RegionId::new(0)), world.fingerprint(RegionId::new(1)));
    }

    #[test]
    fn duplicate_delivery_is_idempotent() {
        let mut faults = FaultPlan::new();
        faults.push(Fault::duplicate(RegionId::new(0), RegionId::new(1), 3, 0, 10));
        let mut world = StubWorld::with_faults(2, faults);
        world.submit(RegionId::new(0), 5, 50).expect("up");
        for _ in 0..6 {
            world.step();
        }
        assert_eq!(world.fingerprint(RegionId::new(0)), world.fingerprint(RegionId::new(1)));
    }
}
