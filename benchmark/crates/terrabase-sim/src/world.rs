//! In-memory stub engine proving the harness mechanics (E-12 skeleton).
//!
//! [`StubWorld`] implements [`ReplicationEngine`] and [`SyncEngine`] with a
//! single-threaded, discrete-time model: per-region document maps with
//! causal remove-wins merge and HLC tie-breaks, per-link in-flight queues subject
//! to the [`FaultPlan`], bounded dependency queues retried every step, and
//! encode-once view caches. It is deliberately simple — delivery is
//! reliable once faults heal, so acknowledged writes are never lost — which
//! is exactly what lets the seed batch qualify the *harness* (fault
//! application, determinism, quiescence, convergence) before engine
//! adapters arrive with E-5/E-6.
//!
//! The merge rule is a pure causal/LWW function per key, so convergence is
//! order-independent by construction; [`merge_doc`] is the single apply path
//! shared by delivery, reconciliation, and the permutation check.

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

/// Adds a length-delimited byte string to a digest. Length delimiters matter:
/// without them, `(body = [1, 2])` and two adjacent fields `[1]`, `[2]` can
/// become the same digest input.
fn fnv_field(state: u64, bytes: &[u8]) -> u64 {
    let hash = fnv_u64(fnv_bytes(state, &[0xF0]), u64::try_from(bytes.len()).unwrap_or(u64::MAX));
    fnv_bytes(hash, bytes)
}

/// Hashes every identity, value, tombstone, and causal field in a row.
fn digest_row(mut hash: u64, row: &JournalRow) -> u64 {
    hash = fnv_bytes(hash, &[0x52]);
    hash = fnv_u64(hash, u64::from(row.origin.0));
    hash = fnv_u64(hash, row.seq);
    hash = fnv_u64(hash, row.hlc.value());
    hash = fnv_u64(hash, row.key);
    hash = fnv_bytes(hash, &[u8::from(row.tombstone)]);
    hash = fnv_u64(hash, u64::try_from(row.context.len()).unwrap_or(u64::MAX));
    for (origin, seq) in row.context.entries() {
        hash = fnv_u64(hash, u64::from(origin.0));
        hash = fnv_u64(hash, seq);
    }
    fnv_field(hash, &row.value)
}

/// The converged per-key version: canonical live bytes or an explicit
/// tombstone, plus the causal metadata of the row that won it.
#[derive(Clone, Debug, PartialEq, Eq)]
struct DocVersion {
    /// The raw canonical body; an empty body is valid for a live row.
    value: Vec<u8>,
    /// Whether the winning row is a tombstone.
    tombstone: bool,
    /// The winning write's HLC.
    hlc: u64,
    /// The winning write's origin (deterministic tiebreak).
    origin: RegionId,
    /// The winning write's sequence (its dotted identity).
    seq: u64,
    /// The winning write's causal context.
    context: Frontier,
}

/// Applies `row` to `docs` with causal remove-wins map semantics and HLC
/// tiebreak (ADR 0044 §2.2). Returns `true` when the visible state changed.
///
/// This is the one apply path: delivery, reconciliation, and the
/// permutation check all funnel through it, so order-independence
/// (invariant 3) holds wherever it is used.
fn merge_doc(docs: &mut BTreeMap<u64, DocVersion>, row: &JournalRow) -> bool {
    let incoming_dominates = |stored: &DocVersion| row.context.get(stored.origin) >= stored.seq;
    let stored_dominates = |stored: &DocVersion| stored.context.get(row.origin) >= row.seq;
    let wins = match docs.get(&row.key) {
        None => true,
        Some(stored) if incoming_dominates(stored) => true,
        Some(stored) if stored_dominates(stored) => false,
        Some(stored) => {
            // A concurrent remove wins regardless of clock skew. HLC and
            // origin are the deterministic tiebreak for competing puts (or
            // competing removes).
            if row.tombstone != stored.tombstone {
                row.tombstone
            } else {
                (row.hlc.value(), row.origin) > (stored.hlc, stored.origin)
            }
        }
    };
    if !wins {
        return false;
    }
    docs.insert(
        row.key,
        DocVersion {
            value: row.value.clone(),
            tombstone: row.tombstone,
            hlc: row.hlc.value(),
            origin: row.origin,
            seq: row.seq,
            context: row.context.clone(),
        },
    );
    true
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
        Self { regions, faults, step: 0, pending: Vec::new(), cache: BTreeMap::new() }
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
            // The workload's numeric values stand in for already parsed
            // canonical JSON. Keep the bytes opaque after this boundary.
            value: value.map_or_else(Vec::new, |value| value.to_string().into_bytes()),
            tombstone: value.is_none(),
        };
        region.accept(&row);
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
            let window = self.faults.reorder_window(row.origin, peer.id, self.step);
            let reorder_jitter = if window == 0 {
                0
            } else {
                // Deterministic spread over [0, window]: same row always
                // takes the same slot, different rows spread across slots.
                (row.seq.wrapping_add(u64::from(peer.id.0))) % window.saturating_add(1)
            };
            let deliver_at = self.step.saturating_add(delay).saturating_add(reorder_jitter);
            // Duplicate faults model repeated network delivery, not distinct
            // durable work. One queued identity is enough: the apply path is
            // idempotent, and deduplication keeps memory bounded under a
            // large duplicate fan-out.
            if !self
                .pending
                .iter()
                .any(|flight| flight.target == target && flight.row.id() == row.id())
            {
                self.pending.push(InFlight { row: row.clone(), target, deliver_at });
            }
        }
    }

    /// Queues one anti-entropy repair frame. Repair is intentionally just
    /// another delivery: link faults, down targets, dependency checks, and
    /// idempotence all remain in force.
    fn schedule_repair_into(
        pending: &mut Vec<InFlight>,
        step: u64,
        row: &JournalRow,
        target: usize,
    ) -> usize {
        if !pending.iter().any(|flight| flight.target == target && flight.row.id() == row.id()) {
            pending.push(InFlight { row: row.clone(), target, deliver_at: step });
        }
        // Count the missing row even if a tailer frame for the same identity
        // was already queued: stats describe the set difference, while the
        // queue remains deduplicated for bounded memory.
        1
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
            } else if !region.waiting.iter().any(|row| row.id() == flight.row.id()) {
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
                    } else if !still.iter().any(|pending: &JournalRow| pending.id() == row.id()) {
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
        hash = fnv_u64(hash, u64::try_from(region.applied.len()).unwrap_or(u64::MAX));
        // `BTreeMap` gives the journal set a stable order independent of
        // delivery order. Include the complete row rather than only its
        // identity: a body, tombstone, or context mutation is divergence.
        for row in region.applied.values() {
            hash = digest_row(hash, row);
        }
        // Also cover materialized state so a broken apply path cannot hide
        // behind an otherwise equal journal set.
        hash = fnv_u64(hash, u64::try_from(region.docs.len()).unwrap_or(u64::MAX));
        for (key, version) in &region.docs {
            hash = fnv_u64(hash, *key);
            hash = fnv_bytes(hash, &[u8::from(version.tombstone)]);
            hash = fnv_field(hash, &version.value);
            hash = fnv_u64(hash, version.hlc);
            hash = fnv_u64(hash, u64::from(version.origin.0));
            hash = fnv_u64(hash, version.seq);
            hash = fnv_u64(hash, u64::try_from(version.context.len()).unwrap_or(u64::MAX));
            for (origin, seq) in version.context.entries() {
                hash = fnv_u64(hash, u64::from(origin.0));
                hash = fnv_u64(hash, seq);
            }
        }
        hash
    }

    /// Returns the world digest: every region's complete journal and
    /// materialized state. Delivery counters are intentionally excluded so
    /// duplicate/reordered transport cannot change an equal state digest.
    #[must_use]
    pub fn digest(&self) -> u64 {
        let mut hash = 0xCBF2_9CE4_8422_2325;
        for position in 0..self.regions.len() {
            hash = fnv_u64(hash, self.region_digest(position));
        }
        hash
    }

    /// Reconstructs the materialized document map at `cut` from the retained
    /// publication rows. Frontier membership, rather than the current state,
    /// is the historical boundary; tombstones participate in reconstruction
    /// even though they are not emitted by a live view.
    fn state_at_cut(&self, position: usize, cut: &ViewCut) -> Option<BTreeMap<u64, DocVersion>> {
        let region = self.regions.get(position)?;
        let mut docs = BTreeMap::new();
        for row in region.applied.values() {
            if row.seq <= cut.frontier.get(row.origin) {
                merge_doc(&mut docs, row);
            }
        }
        Some(docs)
    }

    /// Renders the payload bytes of `view` against a supplied historical cut.
    fn render_view_at(&self, view: &str, position: usize, cut: &ViewCut) -> Option<(u64, Vec<u8>)> {
        if view != VIEW_ALL && view != VIEW_EVEN_KEYS {
            return None;
        }
        let docs = self.state_at_cut(position, cut)?;
        // Length-delimited binary framing keeps the encoded view injective
        // while carrying each canonical body without parsing or reencoding.
        let mut payload = Vec::new();
        payload.extend_from_slice(view.as_bytes());
        payload.push(0);
        payload.extend_from_slice(&STUB_GENERATION.to_be_bytes());
        for (key, version) in &docs {
            if view == VIEW_EVEN_KEYS && key % 2 == 1 {
                continue;
            }
            if !version.tombstone {
                payload.extend_from_slice(&key.to_be_bytes());
                let body_len = u64::try_from(version.value.len()).unwrap_or(u64::MAX);
                payload.extend_from_slice(&body_len.to_be_bytes());
                payload.extend_from_slice(&version.value);
            }
        }
        let mut hash = 0xCBF2_9CE4_8422_2325u64;
        hash = fnv_bytes(hash, &payload);
        Some((hash, payload))
    }

    /// Renders the current revision and records the exact cut it reflects.
    fn render_view(&self, view: &str, position: usize) -> Option<(ViewCut, u64, Vec<u8>)> {
        let region = self.regions.get(position)?;
        let seq_hi = region.applied.keys().map(|(_, seq)| *seq).max().unwrap_or(0);
        let cut = ViewCut::new(region.frontier(), seq_hi);
        let (digest, bytes) = self.render_view_at(view, position, &cut)?;
        Some((cut, digest, bytes))
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

/// Input accepted by [`StubWorld::verify_revision`]. The `&str` form keeps
/// source compatibility with the original seam; revisions are verified from
/// their supplied cut once callers provide a [`ViewRevision`].
pub trait RevisionInput {
    /// Resolves the supplied input to the revision that must be checked.
    fn revision(&self, world: &StubWorld, region: RegionId) -> Option<ViewRevision>;
}

impl RevisionInput for &str {
    fn revision(&self, world: &StubWorld, region: RegionId) -> Option<ViewRevision> {
        world.view_revision(self, region)
    }
}

impl RevisionInput for &ViewRevision {
    fn revision(&self, _world: &StubWorld, _region: RegionId) -> Option<ViewRevision> {
        Some((**self).clone())
    }
}

impl RevisionInput for ViewRevision {
    fn revision(&self, _world: &StubWorld, _region: RegionId) -> Option<ViewRevision> {
        Some(self.clone())
    }
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
        // Walk the two sorted journal sets without materializing their union.
        // Every difference becomes an in-flight frame and is delivered via
        // the same dependency and fault gates as tailer traffic.
        let left_rows = &self.regions[left_position].applied;
        let right_rows = &self.regions[right_position].applied;
        let mut left_iter = left_rows.iter().peekable();
        let mut right_iter = right_rows.iter().peekable();
        let pending = &mut self.pending;
        let step = self.step;
        let mut union_len = 0usize;
        let mut scheduled = 0usize;
        loop {
            let left_id = left_iter.peek().map(|(id, _)| *id);
            let right_id = right_iter.peek().map(|(id, _)| *id);
            match (left_id, right_id) {
                (None, None) => break,
                (Some(_), None) => {
                    union_len = union_len.saturating_add(1);
                    if let Some((_, row)) = left_iter.next() {
                        scheduled += Self::schedule_repair_into(pending, step, row, right_position);
                    }
                }
                (None, Some(_)) => {
                    union_len = union_len.saturating_add(1);
                    if let Some((_, row)) = right_iter.next() {
                        scheduled += Self::schedule_repair_into(pending, step, row, left_position);
                    }
                }
                (Some(left_id), Some(right_id)) if left_id == right_id => {
                    union_len = union_len.saturating_add(1);
                    let _left = left_iter.next();
                    let _right = right_iter.next();
                }
                (Some(left_id), Some(right_id)) if left_id < right_id => {
                    union_len = union_len.saturating_add(1);
                    if let Some((_, row)) = left_iter.next() {
                        scheduled += Self::schedule_repair_into(pending, step, row, right_position);
                    }
                }
                (Some(_), Some(_)) => {
                    union_len = union_len.saturating_add(1);
                    if let Some((_, row)) = right_iter.next() {
                        scheduled += Self::schedule_repair_into(pending, step, row, left_position);
                    }
                }
            }
        }
        self.drain_due();
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
        let rounds = if scheduled == 0 { 1 } else { depth + 1 };
        ReconcileStats::new(rounds, scheduled)
    }

    fn is_uniform(&self, row: &JournalRow, selected: &[RegionId]) -> bool {
        selected.iter().all(|region| {
            self.regions.iter().find(|item| item.id == *region).is_some_and(|peer| {
                // Presence of an identity alone is not uniformity: a
                // conflicting body or tombstone at the same identity must be
                // visible to the simulation's truth checks.
                peer.applied.get(&row.id()) == Some(row) && peer.frontier().covers(&row.context)
            })
        })
    }

    fn read(&self, region: RegionId, token: &SessionToken, key: u64) -> Option<Option<u64>> {
        self.read_bytes(region, token, key).map(|body| {
            body.and_then(|bytes| std::str::from_utf8(&bytes).ok()?.parse::<u64>().ok())
        })
    }

    fn digest_after_permutation(&mut self, rows: &[JournalRow]) -> u64 {
        let mut docs = BTreeMap::new();
        for row in rows {
            merge_doc(&mut docs, row);
        }
        let mut hash = 0xCBF2_9CE4_8422_2325u64;
        let mut ordered_rows: Vec<&JournalRow> = rows.iter().collect();
        ordered_rows.sort_by(|left, right| {
            (
                left.origin,
                left.seq,
                left.hlc,
                left.key,
                left.tombstone,
                &left.value,
                left.context.entries().collect::<Vec<_>>(),
            )
                .cmp(&(
                    right.origin,
                    right.seq,
                    right.hlc,
                    right.key,
                    right.tombstone,
                    &right.value,
                    right.context.entries().collect::<Vec<_>>(),
                ))
        });
        hash = fnv_u64(hash, u64::try_from(ordered_rows.len()).unwrap_or(u64::MAX));
        for row in ordered_rows {
            hash = digest_row(hash, row);
        }
        hash = fnv_u64(hash, u64::try_from(docs.len()).unwrap_or(u64::MAX));
        for (key, version) in &docs {
            hash = fnv_u64(hash, *key);
            hash = fnv_bytes(hash, &[u8::from(version.tombstone)]);
            hash = fnv_field(hash, &version.value);
            hash = fnv_u64(hash, version.hlc);
            hash = fnv_u64(hash, u64::from(version.origin.0));
            hash = fnv_u64(hash, version.seq);
            hash = fnv_u64(hash, u64::try_from(version.context.len()).unwrap_or(u64::MAX));
            for (origin, seq) in version.context.entries() {
                hash = fnv_u64(hash, u64::from(origin.0));
                hash = fnv_u64(hash, seq);
            }
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
        self.view_revision(view, region)
            .is_some_and(|revision| self.verify_revision_at(&revision, region))
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
    /// Reads the opaque canonical bytes for a key. This is the D-30 seam used
    /// by adapters and tests; the legacy trait read decodes the stub's numeric
    /// workload values on top of it.
    #[must_use]
    pub fn read_bytes(
        &self,
        region: RegionId,
        token: &SessionToken,
        key: u64,
    ) -> Option<Option<Vec<u8>>> {
        let peer = self.regions.iter().find(|item| item.id == region)?;
        if self.faults.region_down(region, self.step) {
            return None;
        }
        if !peer.frontier().covers(&token.frontier) {
            return None; // StaleReplica: the SDK follows the named region.
        }
        Some(
            peer.docs
                .get(&key)
                .and_then(|version| (!version.tombstone).then(|| version.value.clone())),
        )
    }

    /// Verifies either a supplied historical revision or, for compatibility,
    /// obtains the current revision for `view` before checking its cut.
    #[must_use]
    pub fn verify_revision<R: RevisionInput>(&self, input: R, region: RegionId) -> bool {
        input
            .revision(self, region)
            .is_some_and(|revision| self.verify_revision_at(&revision, region))
    }

    /// Verifies a supplied revision against the database state at the
    /// revision's own historical cut. This deliberately does not obtain a
    /// fresh revision from current state: callers can retain a revision,
    /// perform later writes, and still verify the original cut.
    #[must_use]
    pub fn verify_revision_at(&self, revision: &ViewRevision, region: RegionId) -> bool {
        let Some(position) = self.regions.iter().position(|item| item.id == region) else {
            return false;
        };
        let current = &self.regions[position];
        if !current.frontier().covers(&revision.cut.frontier)
            || revision.cut.seq_hi > current.applied.keys().map(|(_, seq)| *seq).max().unwrap_or(0)
            || revision.cut.frontier.entries().any(|(_, seq)| seq > revision.cut.seq_hi)
        {
            return false;
        }
        if revision.generation != STUB_GENERATION {
            return false;
        }
        let Some((digest, bytes)) = self.render_view_at(&revision.view, position, &revision.cut)
        else {
            return false;
        };
        revision.payload_digest == digest && revision.bytes == bytes
    }

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

    fn row(
        origin: u32,
        seq: u64,
        context: Frontier,
        hlc: u64,
        key: u64,
        value: &[u8],
        tombstone: bool,
    ) -> JournalRow {
        JournalRow {
            origin: RegionId::new(origin),
            seq,
            context,
            hlc: Hlc::new(hlc),
            key,
            value: value.to_vec(),
            tombstone,
        }
    }

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
        // Region 1 is down while writes happen, so delivery cannot converge
        // on its own; repair queues exactly the missing rows and waits for
        // the normal fault gate to heal.
        let mut faults = FaultPlan::new();
        faults.push(Fault::kill(RegionId::new(1), 0, 10));
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
        for _ in 0..10 {
            world.step();
        }
        assert!(world.is_quiescent());
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

    #[test]
    fn digest_distinguishes_live_bytes_and_tombstone() {
        let mut world = StubWorld::new(1);
        let live = row(0, 1, Frontier::new(), 1, 7, b"\xff", false);
        let tombstone = row(0, 1, Frontier::new(), 1, 7, b"\xff", true);
        assert_ne!(
            world.digest_after_permutation(std::slice::from_ref(&live)),
            world.digest_after_permutation(std::slice::from_ref(&tombstone)),
            "tombstone is a row, not absence of a body"
        );
        assert_ne!(
            world.digest_after_permutation(&[row(0, 1, Frontier::new(), 1, 7, b"ab", false)]),
            world.digest_after_permutation(&[row(0, 1, Frontier::new(), 1, 7, b"a", false)]),
            "body framing must include lengths"
        );
        let mut context = Frontier::new();
        context.advance(RegionId::new(4), 9);
        assert_ne!(
            world.digest_after_permutation(&[row(0, 1, Frontier::new(), 1, 7, b"a", false)]),
            world.digest_after_permutation(&[row(0, 1, context, 1, 7, b"a", false)]),
            "causal context belongs to the row digest"
        );
    }

    #[test]
    fn revision_verification_uses_the_supplied_historical_cut() {
        let mut world = StubWorld::new(1);
        let region = RegionId::new(0);
        world.submit(region, 1, 10).expect("first write");
        let revision = world.view_revision(VIEW_ALL, region).expect("revision");
        world.submit(region, 1, 20).expect("later write");
        assert!(world.verify_revision(&revision, region));
        assert!(world.verify_revision_at(&revision, region));
        let mut forged = revision.clone();
        forged.bytes.push(0);
        assert!(!world.verify_revision_at(&forged, region));
    }

    #[test]
    fn reconciliation_obeys_partition_and_dependency_delivery() {
        let left = RegionId::new(0);
        let right = RegionId::new(1);
        let mut faults = FaultPlan::new();
        faults.push(Fault::partition(left, right, 0, 3));
        let mut world = StubWorld::with_faults(2, faults);
        let committed = world.submit(left, 1, 10).expect("left is up");
        let stats = world.reconcile(left, right);
        assert_eq!(stats.rows_shipped, 1);
        assert!(!world.is_uniform(&committed, &[left, right]));
        world.step();
        world.step();
        world.step();
        assert!(world.is_quiescent());
        assert!(world.is_uniform(&committed, &[left, right]));
    }
}
