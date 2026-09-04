//! Range-based set reconciliation for committed journal rows.
//!
//! The reconciled set is `{(origin, sequence) -> row hash}`. Each insertion
//! updates an additive 128-bit fingerprint in a fixed-fanout hierarchy. A
//! comparison starts at one summary per origin, descends only through
//! mismatching ranges, and exchanges an exact id list only at leaf ranges.
//! The kernel owns no connection state, transport, or storage.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::fmt;
use std::ops::Bound;

use terrabase_causal::{CanonicalEncode, CausalContext, DatabaseId, TenantId};

use crate::{GapRange, ReplicationProgress};

/// The native digest width used for journal-row hashes.
pub type Hash32 = [u8; 32];

/// Journal rows covered by one default leaf summary.
pub const DEFAULT_LEAF_ROWS: u64 = 4096;

/// Child summaries covered by one default parent summary.
pub const DEFAULT_FANOUT: u64 = 64;

/// One committed journal row's stable set identity.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct JournalRowId {
    origin: String,
    sequence: u64,
}

impl JournalRowId {
    /// Creates a row identity. Origins must be nonempty and sequences start at one.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::EmptyOrigin`] or [`RepairError::ZeroSequence`]
    /// when the identity cannot name a committed row.
    pub fn new(origin: impl Into<String>, sequence: u64) -> Result<Self, RepairError> {
        let origin = origin.into();
        if origin.is_empty() {
            return Err(RepairError::EmptyOrigin);
        }
        if sequence == 0 {
            return Err(RepairError::ZeroSequence);
        }
        Ok(Self { origin, sequence })
    }

    /// Returns the row's origin Region.
    #[must_use]
    pub fn origin(&self) -> &str {
        &self.origin
    }

    /// Returns the row's per-origin sequence.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }
}

/// One set entry exchanged at a differing leaf.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RowSummary {
    /// Stable journal-row identity.
    pub id: JournalRowId,
    /// Hash covering the canonical bytes and causal metadata.
    pub row_hash: Hash32,
}

impl RowSummary {
    /// Creates one row summary from an already computed row hash.
    #[must_use]
    pub const fn new(id: JournalRowId, row_hash: Hash32) -> Self {
        Self { id, row_hash }
    }
}

/// An additive range fingerprint, summed modulo `2^128`.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct RangeFingerprint {
    /// Number of rows represented by the range.
    pub count: u64,
    /// Sum of domain-separated row contributions modulo `2^128`.
    pub sum: u128,
}

impl RangeFingerprint {
    fn add(&mut self, contribution: u128) {
        self.count = self.count.saturating_add(1);
        self.sum = self.sum.wrapping_add(contribution);
    }

    fn remove(&mut self, contribution: u128) {
        self.count = self.count.saturating_sub(1);
        self.sum = self.sum.wrapping_sub(contribution);
    }
}

/// One fixed-fanout summary range for an origin.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct JournalRange {
    origin: String,
    level: u8,
    bucket: u64,
    first: u64,
    last: u64,
}

impl JournalRange {
    fn new(origin: String, level: u8, bucket: u64, span: u64) -> Self {
        let first = bucket.saturating_mul(span).saturating_add(1);
        let last = first.saturating_add(span.saturating_sub(1));
        Self { origin, level, bucket, first, last }
    }

    /// Returns the range's origin Region.
    #[must_use]
    pub fn origin(&self) -> &str {
        &self.origin
    }

    /// Returns the hierarchy level, where zero is a leaf.
    #[must_use]
    pub const fn level(&self) -> u8 {
        self.level
    }

    /// Returns the first included per-origin sequence.
    #[must_use]
    pub const fn first(&self) -> u64 {
        self.first
    }

    /// Returns the last included per-origin sequence.
    #[must_use]
    pub const fn last(&self) -> u64 {
        self.last
    }
}

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct SummaryKey {
    origin: String,
    level: u8,
    bucket: u64,
}

/// Incrementally maintained range summaries for one database journal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RangeSummaryTable {
    rows: BTreeMap<JournalRowId, Hash32>,
    summaries: BTreeMap<SummaryKey, RangeFingerprint>,
    leaf_rows: u64,
    fanout: u64,
    top_level: u8,
}

impl Default for RangeSummaryTable {
    fn default() -> Self {
        Self::new()
    }
}

impl RangeSummaryTable {
    /// Creates an empty table with the production layout.
    #[must_use]
    pub fn new() -> Self {
        Self::layout(DEFAULT_LEAF_ROWS, DEFAULT_FANOUT)
    }

    /// Creates an empty table with an explicit leaf span and fanout.
    ///
    /// Small layouts are useful for deterministic protocol tests. Production
    /// callers use [`RangeSummaryTable::new`].
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::InvalidBounds`] when either bound is below two.
    pub fn with_layout(leaf_rows: u64, fanout: u64) -> Result<Self, RepairError> {
        if leaf_rows < 2 || fanout < 2 {
            return Err(RepairError::InvalidBounds);
        }
        Ok(Self::layout(leaf_rows, fanout))
    }

    fn layout(leaf_rows: u64, fanout: u64) -> Self {
        let mut top_level = 0_u8;
        let mut span = leaf_rows;
        while span < u64::MAX {
            top_level = top_level.saturating_add(1);
            let next = span.saturating_mul(fanout);
            if next == span || next == u64::MAX {
                break;
            }
            span = next;
        }
        Self { rows: BTreeMap::new(), summaries: BTreeMap::new(), leaf_rows, fanout, top_level }
    }

    /// Inserts or replaces a row and updates every covering summary.
    ///
    /// Returns the replaced hash when the identity was already present.
    pub fn insert(&mut self, row: &RowSummary) -> Option<Hash32> {
        let previous = self.rows.insert(row.id.clone(), row.row_hash);
        if previous == Some(row.row_hash) {
            return previous;
        }
        if let Some(previous) = previous {
            self.update_path(&row.id, previous, false);
        }
        self.update_path(&row.id, row.row_hash, true);
        previous
    }

    /// Removes a row and updates every covering summary.
    pub fn remove(&mut self, id: &JournalRowId) -> Option<Hash32> {
        let removed = self.rows.remove(id)?;
        self.update_path(id, removed, false);
        Some(removed)
    }

    /// Returns the row hash for an identity.
    #[must_use]
    pub fn get(&self, id: &JournalRowId) -> Option<&Hash32> {
        self.rows.get(id)
    }

    /// Returns the number of represented rows.
    #[must_use]
    pub fn len(&self) -> usize {
        self.rows.len()
    }

    /// Reports whether the represented set is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.rows.is_empty()
    }

    /// Returns the additive proof covering every row in the database.
    #[must_use]
    pub fn proof(&self) -> RangeFingerprint {
        let mut proof = RangeFingerprint::default();
        for (id, row_hash) in &self.rows {
            proof.add(row_contribution(id, row_hash));
        }
        proof
    }

    /// Returns the maintained fingerprint for one hierarchy range.
    #[must_use]
    pub fn fingerprint(&self, range: &JournalRange) -> RangeFingerprint {
        self.summaries
            .get(&SummaryKey {
                origin: range.origin.clone(),
                level: range.level,
                bucket: range.bucket,
            })
            .copied()
            .unwrap_or_default()
    }

    /// Reconciles this set with `peer`, descending only through mismatches.
    ///
    /// The returned plan is deterministic and connection-independent. It
    /// identifies exactly which rows each side lacks and which shared row
    /// identities carry conflicting hashes.
    ///
    /// # Errors
    ///
    /// Returns a layout mismatch or a difference-budget failure.
    pub fn reconcile(
        &self,
        peer: &Self,
        max_differences: usize,
    ) -> Result<ReconciliationPlan, RepairError> {
        if max_differences == 0 {
            return Err(RepairError::InvalidBounds);
        }
        if self.leaf_rows != peer.leaf_rows || self.fanout != peer.fanout {
            return Err(RepairError::LayoutMismatch);
        }
        // The pass stays bounded without a row-count pre-check: equal ranges
        // are skipped by fingerprint, and each differing leaf enforces
        // `max_differences` incrementally via `collect_differences`.  A
        // row-count guard here would reject large converged sets (e.g. 20
        // identical rows with `max_differences: 4`) before the hierarchy
        // could prove they match.
        let origins = self
            .rows
            .keys()
            .chain(peer.rows.keys())
            .map(|id| id.origin.clone())
            .collect::<BTreeSet<_>>();
        let mut pending = origins
            .into_iter()
            .map(|origin| {
                let root_level = self.root_level(peer, &origin);
                JournalRange::new(origin, root_level, 0, self.level_span(root_level))
            })
            .collect::<Vec<_>>();
        let mut messages = Vec::new();
        let mut differences = Vec::with_capacity(max_differences);
        let mut rounds = 0_u8;

        while !pending.is_empty() {
            rounds = rounds.saturating_add(1);
            let mut next = Vec::new();
            for range in pending {
                let local = self.fingerprint(&range);
                let remote = peer.fingerprint(&range);
                messages.push(ReconciliationMessage::Fingerprint {
                    range: range.clone(),
                    local,
                    remote,
                });
                if local == remote {
                    messages.push(ReconciliationMessage::Skip { range });
                    continue;
                }
                if range.level == 0 {
                    let local_ids = self.rows_in(&range);
                    let remote_ids = peer.rows_in(&range);
                    messages.push(ReconciliationMessage::IdList {
                        range,
                        local: local_ids.clone(),
                        remote: remote_ids.clone(),
                    });
                    collect_differences(
                        &local_ids,
                        &remote_ids,
                        &mut differences,
                        max_differences,
                    )?;
                    continue;
                }
                let child_level = range.level - 1;
                let child_span = self.level_span(child_level);
                let Some(first_bucket) = range.bucket.checked_mul(self.fanout) else {
                    return Err(RepairError::InvalidRange);
                };
                for offset in 0..self.fanout {
                    let Some(bucket) = first_bucket.checked_add(offset) else {
                        break;
                    };
                    let Some(zero_based_first) = bucket.checked_mul(child_span) else {
                        break;
                    };
                    if zero_based_first >= range.last {
                        break;
                    }
                    let child =
                        JournalRange::new(range.origin.clone(), child_level, bucket, child_span);
                    if self.fingerprint(&child).count != 0 || peer.fingerprint(&child).count != 0 {
                        next.push(child);
                    }
                }
            }
            pending = next;
        }

        if differences.len() > max_differences {
            return Err(RepairError::RepairBudgetExceeded {
                differences: differences.len(),
                max_differences,
            });
        }

        Ok(ReconciliationPlan { messages, differences, rounds })
    }

    fn update_path(&mut self, id: &JournalRowId, row_hash: Hash32, add: bool) {
        let contribution = row_contribution(id, &row_hash);
        for level in 0..=self.top_level {
            let span = self.level_span(level);
            let bucket = (id.sequence - 1) / span;
            let key = SummaryKey { origin: id.origin.clone(), level, bucket };
            let summary = self.summaries.entry(key.clone()).or_default();
            if add {
                summary.add(contribution);
            } else {
                summary.remove(contribution);
            }
            if summary.count == 0 {
                self.summaries.remove(&key);
            }
        }
    }

    fn level_span(&self, level: u8) -> u64 {
        let mut span = self.leaf_rows;
        for _ in 0..level {
            span = span.saturating_mul(self.fanout);
        }
        span
    }

    fn root_level(&self, peer: &Self, origin: &str) -> u8 {
        let maximum = self
            .rows
            .keys()
            .chain(peer.rows.keys())
            .filter(|id| id.origin == origin)
            .map(|id| id.sequence)
            .max()
            .unwrap_or(1);
        let mut level = 0_u8;
        while level < self.top_level && maximum > self.level_span(level) {
            level = level.saturating_add(1);
        }
        level
    }

    fn rows_in(&self, range: &JournalRange) -> Vec<RowSummary> {
        // The table is ordered by `(origin, sequence)`, so a leaf scan must
        // visit only that leaf.  Filtering the complete table here made a
        // single differing leaf do O(retained_rows) work on both peers.
        let first = JournalRowId { origin: range.origin.clone(), sequence: range.first };
        let last = JournalRowId { origin: range.origin.clone(), sequence: range.last };
        self.rows
            .range((Bound::Included(first), Bound::Included(last)))
            .map(|(id, row_hash)| RowSummary::new(id.clone(), *row_hash))
            .collect()
    }
}

fn collect_differences(
    local: &[RowSummary],
    remote: &[RowSummary],
    output: &mut Vec<RowDifference>,
    max_differences: usize,
) -> Result<(), RepairError> {
    let local = local.iter().map(|row| (&row.id, row)).collect::<BTreeMap<_, _>>();
    let remote = remote.iter().map(|row| (&row.id, row)).collect::<BTreeMap<_, _>>();
    let ids = local.keys().chain(remote.keys()).copied().collect::<BTreeSet<_>>();
    for id in ids {
        match (local.get(id), remote.get(id)) {
            (Some(local), Some(remote)) if local.row_hash != remote.row_hash => {
                push_difference(
                    output,
                    RowDifference::HashMismatch {
                        id: id.clone(),
                        local: local.row_hash,
                        remote: remote.row_hash,
                    },
                    max_differences,
                )?;
            }
            (Some(local), None) => {
                push_difference(
                    output,
                    RowDifference::MissingRemote((*local).clone()),
                    max_differences,
                )?;
            }
            (None, Some(remote)) => {
                push_difference(
                    output,
                    RowDifference::MissingLocal((*remote).clone()),
                    max_differences,
                )?;
            }
            _ => {}
        }
    }
    Ok(())
}

fn push_difference(
    output: &mut Vec<RowDifference>,
    difference: RowDifference,
    max_differences: usize,
) -> Result<(), RepairError> {
    if output.len() >= max_differences {
        return Err(RepairError::RepairBudgetExceeded {
            differences: output.len().saturating_add(1),
            max_differences,
        });
    }
    output.push(difference);
    Ok(())
}

/// One stateless wire decision made during reconciliation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ReconciliationMessage {
    /// Both range fingerprints were compared.
    Fingerprint {
        /// Compared range.
        range: JournalRange,
        /// Local additive fingerprint.
        local: RangeFingerprint,
        /// Peer additive fingerprint.
        remote: RangeFingerprint,
    },
    /// Equal fingerprints allow both sides to skip the complete range.
    Skip {
        /// Equal range.
        range: JournalRange,
    },
    /// A differing leaf exchanges exact row identities and hashes.
    IdList {
        /// Differing leaf range.
        range: JournalRange,
        /// Local rows in the leaf.
        local: Vec<RowSummary>,
        /// Peer rows in the leaf.
        remote: Vec<RowSummary>,
    },
}

/// One exact difference found at a leaf.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RowDifference {
    /// The local side needs this peer row.
    MissingLocal(RowSummary),
    /// The peer side needs this local row.
    MissingRemote(RowSummary),
    /// Both sides hold one identity with different row hashes.
    HashMismatch {
        /// Shared journal-row identity.
        id: JournalRowId,
        /// Local row hash.
        local: Hash32,
        /// Peer row hash.
        remote: Hash32,
    },
}

impl RowDifference {
    /// Returns the differing row identity.
    #[must_use]
    pub fn id(&self) -> &JournalRowId {
        match self {
            Self::MissingLocal(row) | Self::MissingRemote(row) => &row.id,
            Self::HashMismatch { id, .. } => id,
        }
    }
}

/// The complete deterministic result of one reconciliation pass.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReconciliationPlan {
    messages: Vec<ReconciliationMessage>,
    differences: Vec<RowDifference>,
    rounds: u8,
}

impl ReconciliationPlan {
    /// Returns the stateless protocol transcript.
    #[must_use]
    pub fn messages(&self) -> &[ReconciliationMessage] {
        &self.messages
    }

    /// Returns the exact rows which differ.
    #[must_use]
    pub fn differences(&self) -> &[RowDifference] {
        &self.differences
    }

    /// Returns the number of fixed-fanout descent rounds.
    #[must_use]
    pub const fn rounds(&self) -> u8 {
        self.rounds
    }

    /// Reports whether both represented sets proved equal.
    #[must_use]
    pub fn is_converged(&self) -> bool {
        self.differences.is_empty()
    }
}

/// Missing contiguous and selective ranges discovered from two progress summaries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProgressRepairPlan {
    /// Ranges the receiver may safely request from the source.
    pub ranges: Vec<GapRange>,
    /// Source contiguous prefix used when the plan was made.
    pub source_through: u64,
}

/// Computes a bounded-prefix/gap repair request from source and receiver progress.
///
/// # Errors
///
/// Returns an error when the range budget is zero or exceeded.
pub fn plan_progress_repair(
    receiver: &ReplicationProgress,
    source: &ReplicationProgress,
    max_ranges: usize,
) -> Result<ProgressRepairPlan, RepairError> {
    if max_ranges == 0 {
        return Err(RepairError::InvalidBounds);
    }
    let mut ranges = BTreeSet::new();
    for range in receiver.missing_ranges(source.through()) {
        ranges.insert(range);
    }
    for available in source.gaps() {
        for missing in receiver.missing_ranges(available.last()) {
            let first = missing.first().max(available.first());
            let last = missing.last().min(available.last());
            if first <= last {
                ranges.insert(GapRange::new(first, last).map_err(|_| RepairError::InvalidRange)?);
            }
        }
    }
    if ranges.len() > max_ranges {
        return Err(RepairError::RangeBudgetExceeded { ranges: ranges.len(), max_ranges });
    }
    Ok(ProgressRepairPlan {
        ranges: ranges.into_iter().collect(),
        source_through: source.through(),
    })
}

/// Typed anti-entropy failure.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RepairError {
    /// A range's start follows its end.
    InvalidRange,
    /// A zero or one bound cannot produce a useful reconciliation layout.
    InvalidBounds,
    /// A journal origin is empty.
    EmptyOrigin,
    /// Journal sequence zero is reserved.
    ZeroSequence,
    /// Two peers attempted reconciliation with different layouts.
    LayoutMismatch,
    /// The exact row-difference budget was exhausted.
    RepairBudgetExceeded {
        /// Exact differing rows found.
        differences: usize,
        /// Maximum accepted differences.
        max_differences: usize,
    },
    /// The progress-range budget was exhausted.
    RangeBudgetExceeded {
        /// Missing ranges found.
        ranges: usize,
        /// Maximum accepted ranges.
        max_ranges: usize,
    },
    /// Queue capacity has been reached.
    QueueFull,
    /// A stable acknowledgement was attempted without verification.
    NotVerified,
    /// The supplied proof differs from the expected proof.
    VerificationMismatch,
    /// One source supplied the same journal identity more than once.
    DuplicateRowIdentity,
}

impl fmt::Display for RepairError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidRange => formatter.write_str("invalid replication range"),
            Self::InvalidBounds => formatter.write_str("anti-entropy bounds must exceed one"),
            Self::EmptyOrigin => formatter.write_str("journal origin is empty"),
            Self::ZeroSequence => formatter.write_str("journal sequence zero is reserved"),
            Self::LayoutMismatch => formatter.write_str("reconciliation layouts differ"),
            Self::RepairBudgetExceeded { .. } => {
                formatter.write_str("row difference budget exceeded")
            }
            Self::RangeBudgetExceeded { .. } => {
                formatter.write_str("progress range budget exceeded")
            }
            Self::QueueFull => formatter.write_str("anti-entropy queue is full"),
            Self::NotVerified => formatter.write_str("frontier is not verified"),
            Self::VerificationMismatch => formatter.write_str("verification proof mismatch"),
            Self::DuplicateRowIdentity => formatter.write_str("duplicate journal row identity"),
        }
    }
}

impl std::error::Error for RepairError {}

/// A trigger that requests an anti-entropy exchange.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum AntiEntropyTrigger {
    /// Deterministic periodic exchange.
    Periodic,
    /// Peer connection was re-established.
    Reconnect,
    /// Process or disk recovery completed.
    Recovery,
    /// A verified snapshot was installed.
    SnapshotInstalled,
    /// Region membership changed.
    MembershipChanged,
    /// A range fingerprint mismatch was observed.
    FingerprintMismatch,
    /// An authorized operator requested repair.
    OperatorRequest,
}

/// Bounded trigger state; repeated triggers coalesce without being lost.
#[derive(Clone, Debug)]
pub struct TriggerState {
    pending: BTreeSet<AntiEntropyTrigger>,
    next_periodic: u64,
    interval: u64,
}

impl TriggerState {
    /// Creates a schedule with a nonzero deterministic interval.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::InvalidBounds`] for a zero interval.
    pub fn new(now: u64, interval: u64) -> Result<Self, RepairError> {
        if interval == 0 {
            return Err(RepairError::InvalidBounds);
        }
        Ok(Self { pending: BTreeSet::new(), next_periodic: now.saturating_add(interval), interval })
    }

    /// Creates a schedule with deterministic peer-specific jitter.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::InvalidBounds`] for a zero interval.
    pub fn new_with_jitter(now: u64, interval: u64, seed: u64) -> Result<Self, RepairError> {
        if interval == 0 {
            return Err(RepairError::InvalidBounds);
        }
        let digest = hash("anti-entropy-jitter", &seed.to_be_bytes());
        let span = (interval / 4).max(1);
        let jitter = u64::from_be_bytes([
            digest[0], digest[1], digest[2], digest[3], digest[4], digest[5], digest[6], digest[7],
        ]) % span;
        Ok(Self {
            pending: BTreeSet::new(),
            next_periodic: now.saturating_add(interval).saturating_add(jitter),
            interval,
        })
    }

    /// Records one trigger.
    pub fn trigger(&mut self, trigger: AntiEntropyTrigger) {
        self.pending.insert(trigger);
    }

    /// Adds `Periodic` when its deterministic deadline has elapsed.
    pub fn poll(&mut self, now: u64) {
        if now >= self.next_periodic {
            self.pending.insert(AntiEntropyTrigger::Periodic);
            let elapsed = now.saturating_sub(self.next_periodic);
            let periods = (elapsed / self.interval).saturating_add(1);
            self.next_periodic =
                self.next_periodic.saturating_add(periods.saturating_mul(self.interval));
        }
    }

    /// Drains coalesced triggers in canonical order.
    pub fn drain(&mut self) -> Vec<AntiEntropyTrigger> {
        let drained = self.pending.iter().copied().collect::<Vec<_>>();
        self.pending.clear();
        drained
    }

    /// Reports whether work is pending.
    #[must_use]
    pub fn is_pending(&self) -> bool {
        !self.pending.is_empty()
    }
}

/// Tenant/database/Region fairness key.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct QueueKey {
    /// Tenant identity.
    pub tenant: TenantId,
    /// Database identity.
    pub database: DatabaseId,
    /// Region catalog name.
    pub region: String,
}

impl QueueKey {
    /// Creates a queue key.
    #[must_use]
    pub fn new(tenant: TenantId, database: DatabaseId, region: impl Into<String>) -> Self {
        Self { tenant, database, region: region.into() }
    }
}

/// One bounded repair work item.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RepairWork {
    /// Fairness key charged for this item.
    pub key: QueueKey,
    /// Trigger that caused the work.
    pub trigger: AntiEntropyTrigger,
    /// Bounded byte charge.
    pub bytes: usize,
    /// Optional journal range to reconcile.
    pub range: Option<JournalRange>,
}

/// A bounded round-robin scheduler across database/Region pairs.
#[derive(Clone, Debug)]
pub struct FairRepairQueue {
    queues: BTreeMap<QueueKey, VecDeque<RepairWork>>,
    order: VecDeque<QueueKey>,
    capacity: usize,
    len: usize,
}

impl FairRepairQueue {
    /// Creates a queue with an item bound.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::InvalidBounds`] for zero capacity.
    pub fn new(capacity: usize) -> Result<Self, RepairError> {
        if capacity == 0 {
            return Err(RepairError::InvalidBounds);
        }
        Ok(Self { queues: BTreeMap::new(), order: VecDeque::new(), capacity, len: 0 })
    }

    /// Enqueues work, coalescing exact duplicate ranges for a key.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::QueueFull`] when capacity is exhausted.
    pub fn push(&mut self, work: RepairWork) -> Result<(), RepairError> {
        if self.queues.get(&work.key).is_some_and(|queue| {
            queue
                .iter()
                .any(|existing| existing.range == work.range && existing.trigger == work.trigger)
        }) {
            return Ok(());
        }
        if self.len >= self.capacity {
            return Err(RepairError::QueueFull);
        }
        let queue = self.queues.entry(work.key.clone()).or_default();
        if queue.is_empty() {
            self.order.push_back(work.key.clone());
        }
        queue.push_back(work);
        self.len += 1;
        Ok(())
    }

    /// Pops one work item using bounded fair scheduling.
    pub fn pop(&mut self) -> Option<RepairWork> {
        let key = self.order.pop_front()?;
        let queue = self.queues.get_mut(&key)?;
        let work = queue.pop_front()?;
        self.len = self.len.saturating_sub(1);
        if queue.is_empty() {
            self.queues.remove(&key);
        } else {
            self.order.push_back(key);
        }
        Some(work)
    }

    /// Returns the number of queued items.
    #[must_use]
    pub const fn len(&self) -> usize {
        self.len
    }

    /// Reports whether no work is queued.
    #[must_use]
    pub const fn is_empty(&self) -> bool {
        self.len == 0
    }
}

/// A verified position suitable for stable-frontier acknowledgement.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VerifiedFrontier {
    /// Member identity in the required set.
    pub member: u64,
    /// Monotonic generation of the verification.
    pub generation: u64,
    /// Contiguous journal prefix.
    pub through: u64,
    /// Verified whole-database range proof.
    pub proof: RangeFingerprint,
}

/// Stable frontier that admits only equality-proven observations.
#[derive(Clone, Debug)]
pub struct VerifiedStableFrontier {
    required: BTreeSet<u64>,
    observations: BTreeMap<u64, VerifiedFrontier>,
    floor: u64,
}

impl VerifiedStableFrontier {
    /// Creates a stable frontier for required members.
    #[must_use]
    pub fn new(required: impl IntoIterator<Item = u64>) -> Self {
        Self { required: required.into_iter().collect(), observations: BTreeMap::new(), floor: 0 }
    }

    /// Records a member only after its range proof matches the expected proof.
    ///
    /// # Errors
    ///
    /// Returns an error if proofs differ or the member is not required.
    pub fn acknowledge(
        &mut self,
        observation: VerifiedFrontier,
        expected: RangeFingerprint,
    ) -> Result<(), RepairError> {
        if observation.proof != expected {
            return Err(RepairError::VerificationMismatch);
        }
        if !self.required.contains(&observation.member) {
            return Err(RepairError::NotVerified);
        }
        let replace = self
            .observations
            .get(&observation.member)
            .is_none_or(|current| observation.generation >= current.generation);
        if replace {
            self.observations.insert(observation.member, observation);
        }
        Ok(())
    }

    /// Advances to the minimum verified contiguous prefix.
    pub fn advance(&mut self) -> u64 {
        let candidate = self
            .required
            .iter()
            .map(|member| {
                self.observations.get(member).map_or(0, |observation| observation.through)
            })
            .min()
            .unwrap_or(0);
        self.floor = self.floor.max(candidate);
        self.floor
    }

    /// Returns the stable contiguous prefix.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.floor
    }

    /// Reports whether a counter is safe to collect.
    #[must_use]
    pub const fn can_collect(&self, counter: u64) -> bool {
        counter != 0 && counter <= self.floor
    }
}

fn row_contribution(id: &JournalRowId, row_hash: &Hash32) -> u128 {
    let mut bytes = Vec::with_capacity(id.origin.len() + 48);
    put_bytes(&mut bytes, id.origin.as_bytes());
    bytes.extend_from_slice(&id.sequence.to_be_bytes());
    bytes.extend_from_slice(row_hash);
    let digest = hash("range-reconciliation-row", &bytes);
    u128::from_be_bytes(digest[..16].try_into().expect("the digest has sixteen leading bytes"))
}

struct Bytes(Vec<u8>);

impl CanonicalEncode for Bytes {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        output.extend_from_slice(&self.0);
    }
}

fn hash(domain: &'static str, bytes: &[u8]) -> Hash32 {
    terrabase_causal::canonical_hash(domain, &Bytes(bytes.to_vec()))
        .expect("the fixed hash domain is valid")
}

fn put_bytes(output: &mut Vec<u8>, bytes: &[u8]) {
    output.extend_from_slice(&(bytes.len() as u64).to_be_bytes());
    output.extend_from_slice(bytes);
}

/// Hashes one canonical journal row, including its causal metadata.
#[must_use]
pub fn row_hash(bytes: &[u8]) -> Hash32 {
    hash("range-reconciliation-journal-row", bytes)
}

/// Hashes one causal context for callers constructing a canonical row payload.
#[must_use]
pub fn causal_hash(context: &CausalContext) -> Hash32 {
    hash("range-reconciliation-causal-state", &context.canonical_bytes())
}

/// Hashes canonical materialized bytes for callers constructing a row payload.
#[must_use]
pub fn materialized_hash(bytes: &[u8]) -> Hash32 {
    hash("range-reconciliation-materialized-state", bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn row(origin: &str, sequence: u64, value: &[u8]) -> RowSummary {
        RowSummary::new(JournalRowId::new(origin, sequence).unwrap(), row_hash(value))
    }

    #[test]
    fn insertion_and_removal_update_every_covering_summary() {
        let mut table = RangeSummaryTable::with_layout(4, 4).unwrap();
        let first = row("gru", 1, b"one");
        let id = first.id.clone();
        table.insert(&first);
        let proof = table.proof();
        assert_eq!(proof.count, 1);
        assert_ne!(proof.sum, 0);
        assert_eq!(table.remove(&id), Some(row_hash(b"one")));
        assert_eq!(table.proof(), RangeFingerprint::default());
    }

    #[test]
    fn reconciliation_descends_only_to_the_differing_leaf() {
        let mut local = RangeSummaryTable::with_layout(4, 4).unwrap();
        let mut peer = RangeSummaryTable::with_layout(4, 4).unwrap();
        for sequence in 1..=20 {
            local.insert(&row("gru", sequence, &sequence.to_be_bytes()));
            peer.insert(&row("gru", sequence, &sequence.to_be_bytes()));
        }
        peer.insert(&row("gru", 18, b"changed"));
        let plan = local.reconcile(&peer, 4).unwrap();
        assert_eq!(plan.differences().len(), 1);
        assert!(matches!(plan.differences()[0], RowDifference::HashMismatch { .. }));
        assert_eq!(
            plan.messages()
                .iter()
                .filter(|message| matches!(message, ReconciliationMessage::IdList { .. }))
                .count(),
            1
        );
    }
}
