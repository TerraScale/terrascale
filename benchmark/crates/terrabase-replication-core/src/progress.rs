//! Native durable replication progress and retention guards.
//!
//! This module deliberately has no dependency on the legacy CRDT payload
//! implementation in this crate.  A replication receiver may receive batches
//! out of order, but it can only advertise a contiguous prefix after every
//! earlier counter has crossed its local durability boundary.  Counters above
//! that prefix are represented as a bounded set of closed ranges.

use std::collections::{BTreeMap, BTreeSet};

/// The hard bound on disjoint out-of-order ranges held for one stream.
pub const MAX_GAP_RANGES: usize = 64;

/// The default bound on disjoint out-of-order ranges held for one stream.
pub const DEFAULT_MAX_GAP_RANGES: usize = MAX_GAP_RANGES;

/// A bounded closed range of non-zero replication counters.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct GapRange {
    first: u64,
    last: u64,
}

impl GapRange {
    /// Creates a non-empty range. Counter zero is reserved.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::ZeroCounter`] for a zero endpoint or
    /// [`ProgressError::ReversedRange`] when `first` follows `last`.
    pub const fn new(first: u64, last: u64) -> Result<Self, ProgressError> {
        if first == 0 || last == 0 {
            return Err(ProgressError::ZeroCounter);
        }
        if first > last {
            return Err(ProgressError::ReversedRange);
        }
        Ok(Self { first, last })
    }

    /// Returns the first counter in the range.
    #[must_use]
    pub const fn first(self) -> u64 {
        self.first
    }

    /// Alias for [`GapRange::first`].
    #[must_use]
    pub const fn start(self) -> u64 {
        self.first
    }

    /// Returns the last counter in the range.
    #[must_use]
    pub const fn last(self) -> u64 {
        self.last
    }

    /// Alias for [`GapRange::last`].
    #[must_use]
    pub const fn end(self) -> u64 {
        self.last
    }

    fn contains(self, counter: u64) -> bool {
        self.first <= counter && counter <= self.last
    }
}

/// Why a native progress update was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProgressError {
    /// Counter zero is reserved for an absent prefix.
    ZeroCounter,
    /// A range's first counter followed its last counter.
    ReversedRange,
    /// The update would retain more disjoint ranges than the configured cap.
    GapLimitExceeded {
        /// The configured maximum number of disjoint ranges.
        limit: usize,
    },
    /// An acknowledgement was issued by a member not required by retention.
    UnexpectedMember {
        /// The unrecognized member identity.
        member: u64,
    },
    /// A requester cursor is not proof of durable receiver progress.
    UnverifiedCursor,
}

impl std::fmt::Display for ProgressError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::ZeroCounter => formatter.write_str("counter zero is reserved"),
            Self::ReversedRange => formatter.write_str("range is reversed"),
            Self::GapLimitExceeded { limit } => {
                write!(formatter, "gap range limit {limit} exceeded")
            }
            Self::UnexpectedMember { member } => write!(formatter, "unexpected member {member}"),
            Self::UnverifiedCursor => formatter.write_str("requester cursor is not durable proof"),
        }
    }
}

impl std::error::Error for ProgressError {}

/// A receiver's normalized durable progress for one replication counter
/// stream.
///
/// `through` is the only cumulative acknowledgement. `gaps` contain
/// counters known to be durable above it; they never imply that counters
/// between the prefix and a gap are present. Updates are transactional with
/// respect to the range bound: a rejected update does not change progress.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationProgress {
    through: u64,
    gaps: Vec<GapRange>,
    max_gap_ranges: usize,
}

impl Default for ReplicationProgress {
    fn default() -> Self {
        Self::new()
    }
}

impl ReplicationProgress {
    /// Creates empty progress with [`DEFAULT_MAX_GAP_RANGES`].
    #[must_use]
    pub const fn new() -> Self {
        Self { through: 0, gaps: Vec::new(), max_gap_ranges: DEFAULT_MAX_GAP_RANGES }
    }

    /// Creates empty progress with an explicit range bound. Values above
    /// [`MAX_GAP_RANGES`] are clamped to the protocol hard bound.
    #[must_use]
    pub const fn with_gap_limit(max_gap_ranges: usize) -> Self {
        let max_gap_ranges =
            if max_gap_ranges > MAX_GAP_RANGES { MAX_GAP_RANGES } else { max_gap_ranges };
        Self { through: 0, gaps: Vec::new(), max_gap_ranges }
    }

    /// Returns the configured maximum number of disjoint ranges.
    #[must_use]
    pub const fn gap_limit(&self) -> usize {
        self.max_gap_ranges
    }

    /// Returns the durable contiguous prefix.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.through
    }

    /// Alias for [`ReplicationProgress::through`].
    #[must_use]
    pub const fn contiguous_prefix(&self) -> u64 {
        self.through
    }

    /// Alias for [`ReplicationProgress::through`].
    #[must_use]
    pub const fn prefix(&self) -> u64 {
        self.through
    }

    /// Returns normalized durable ranges above the prefix.
    #[must_use]
    pub fn gaps(&self) -> &[GapRange] {
        &self.gaps
    }

    /// Alias for [`ReplicationProgress::gaps`].
    #[must_use]
    pub fn gap_ranges(&self) -> &[GapRange] {
        &self.gaps
    }

    /// Returns the number of disjoint durable ranges above the prefix.
    #[must_use]
    pub fn gap_count(&self) -> usize {
        self.gaps.len()
    }

    /// Reports whether a counter is durably represented.
    #[must_use]
    pub fn contains(&self, counter: u64) -> bool {
        counter != 0
            && (counter <= self.through || self.gaps.iter().any(|range| range.contains(counter)))
    }

    /// Records one durably stored counter.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::GapLimitExceeded`] when this counter would
    /// create more disjoint ranges than the configured bound.
    pub fn record(&mut self, counter: u64) -> Result<(), ProgressError> {
        let range = GapRange::new(counter, counter)?;
        self.record_range(range)
    }

    /// Alias emphasizing that the update is made after the WAL boundary.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::GapLimitExceeded`] when this counter would
    /// create more disjoint ranges than the configured bound.
    pub fn record_durable(&mut self, counter: u64) -> Result<(), ProgressError> {
        self.record(counter)
    }

    /// Records an inclusive durably stored range and closes any newly
    /// contiguous prefix.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::GapLimitExceeded`] when the resulting gaps
    /// exceed the configured bound.
    pub fn record_range(&mut self, incoming: GapRange) -> Result<(), ProgressError> {
        let mut next = self.clone();
        next.insert_range(incoming);
        if next.gaps.len() > next.max_gap_ranges {
            return Err(ProgressError::GapLimitExceeded { limit: next.max_gap_ranges });
        }
        *self = next;
        Ok(())
    }

    /// Records a raw inclusive range.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::ZeroCounter`] or
    /// [`ProgressError::ReversedRange`] for an invalid range, and
    /// [`ProgressError::GapLimitExceeded`] when its gaps exceed the bound.
    pub fn record_interval(&mut self, first: u64, last: u64) -> Result<(), ProgressError> {
        self.record_range(GapRange::new(first, last)?)
    }

    /// Alias for [`ReplicationProgress::record_interval`].
    ///
    /// # Errors
    ///
    /// Returns the validation or gap-limit error from
    /// [`ReplicationProgress::record_interval`].
    pub fn record_batch(&mut self, first: u64, last: u64) -> Result<(), ProgressError> {
        self.record_interval(first, last)
    }

    /// Alias for [`ReplicationProgress::record`].
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::GapLimitExceeded`] when this counter would
    /// create more disjoint ranges than the configured bound.
    pub fn observe(&mut self, counter: u64) -> Result<(), ProgressError> {
        self.record(counter)
    }

    /// Unions another verified progress observation into this progress.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::GapLimitExceeded`] when the union would
    /// exceed this progress's configured bound.
    pub fn union(&mut self, other: &Self) -> Result<(), ProgressError> {
        let mut next = self.clone();
        if other.through != 0 {
            next.insert_range(GapRange { first: 1, last: other.through });
        }
        for range in &other.gaps {
            next.insert_range(*range);
        }
        if next.gaps.len() > next.max_gap_ranges {
            return Err(ProgressError::GapLimitExceeded { limit: next.max_gap_ranges });
        }
        *self = next;
        Ok(())
    }

    /// Reports whether this progress represents all counters in `other`.
    #[must_use]
    pub fn covers(&self, other: &Self) -> bool {
        other.through <= self.through && other.gaps.iter().all(|range| self.covers_range(*range))
    }

    /// Returns the missing ranges through `last`, without inferring holes.
    #[must_use]
    pub fn missing_ranges(&self, last: u64) -> Vec<GapRange> {
        if last == 0 || last <= self.through {
            return Vec::new();
        }
        let mut missing = Vec::new();
        let mut cursor = self.through.saturating_add(1);
        for received in &self.gaps {
            if received.first > last || cursor > last {
                break;
            }
            if cursor < received.first {
                missing.push(GapRange { first: cursor, last: received.first - 1 });
            }
            if received.last == u64::MAX {
                return missing;
            }
            cursor = received.last.saturating_add(1);
        }
        if cursor <= last {
            missing.push(GapRange { first: cursor, last });
        }
        missing
    }

    /// Returns a cursor snapshot suitable for requesting repair. A cursor is
    /// deliberately not a retention acknowledgement.
    #[must_use]
    pub fn cursor(&self) -> ReplicationCursor {
        ReplicationCursor { progress: self.clone() }
    }

    fn insert_range(&mut self, mut incoming: GapRange) {
        if incoming.last <= self.through {
            return;
        }
        incoming.first = incoming.first.max(self.through.saturating_add(1));
        let mut merged = Vec::with_capacity(self.gaps.len().saturating_add(1));
        let mut placed = false;
        for range in self.gaps.drain(..) {
            if range.last.saturating_add(1) < incoming.first {
                merged.push(range);
            } else if incoming.last.saturating_add(1) < range.first {
                if !placed {
                    merged.push(incoming);
                    placed = true;
                }
                merged.push(range);
            } else {
                incoming = GapRange {
                    first: incoming.first.min(range.first),
                    last: incoming.last.max(range.last),
                };
            }
        }
        if !placed {
            merged.push(incoming);
        }
        self.gaps = merged;
        while self.gaps.first().is_some_and(|range| range.first <= self.through.saturating_add(1)) {
            let range = self.gaps.remove(0);
            self.through = self.through.max(range.last);
        }
    }

    fn covers_range(&self, target: GapRange) -> bool {
        target.last <= self.through
            || self
                .gaps
                .iter()
                .any(|range| range.first <= target.first && range.last >= target.last)
    }
}

/// A requester-provided progress snapshot. It is valid for repair selection,
/// but intentionally cannot advance a retention floor.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationCursor {
    progress: ReplicationProgress,
}

impl ReplicationCursor {
    /// Returns the cursor's contiguous prefix.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.progress.through()
    }

    /// Returns the cursor's explicit ranges.
    #[must_use]
    pub fn gaps(&self) -> &[GapRange] {
        self.progress.gaps()
    }

    /// Returns the progress represented by this cursor for repair logic.
    #[must_use]
    pub fn progress(&self) -> &ReplicationProgress {
        &self.progress
    }
}

/// A receiver-owned progress tracker. Only this type can mint the opaque
/// durable acknowledgement consumed by [`RetentionGuard`].
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurableProgressTracker {
    member: u64,
    generation: u64,
    progress: ReplicationProgress,
}

impl DurableProgressTracker {
    /// Creates an empty receiver tracker for `member`.
    #[must_use]
    pub fn new(member: u64) -> Self {
        Self { member, generation: 0, progress: ReplicationProgress::new() }
    }

    /// Creates a receiver tracker with an explicit gap bound.
    #[must_use]
    pub fn with_gap_limit(member: u64, max_gap_ranges: usize) -> Self {
        Self {
            member,
            generation: 0,
            progress: ReplicationProgress::with_gap_limit(max_gap_ranges),
        }
    }

    /// Returns the receiver member identity.
    #[must_use]
    pub const fn member(&self) -> u64 {
        self.member
    }

    /// Returns the receiver's currently durable progress.
    #[must_use]
    pub fn progress(&self) -> &ReplicationProgress {
        &self.progress
    }

    /// Records a batch after its durable storage boundary.
    ///
    /// # Errors
    ///
    /// Returns the validation or gap-limit error from
    /// [`ReplicationProgress::record_interval`].
    pub fn record_durable_range(&mut self, first: u64, last: u64) -> Result<(), ProgressError> {
        self.progress.record_interval(first, last)?;
        self.generation = self.generation.saturating_add(1);
        Ok(())
    }

    /// Records one durably stored counter.
    ///
    /// # Errors
    ///
    /// Returns the validation or gap-limit error from
    /// [`DurableProgressTracker::record_durable_range`].
    pub fn record_durable(&mut self, counter: u64) -> Result<(), ProgressError> {
        self.record_durable_range(counter, counter)
    }

    /// Mints a receiver-owned acknowledgement snapshot.
    #[must_use]
    pub fn durable_acknowledgement(&self) -> DurableAcknowledgement {
        DurableAcknowledgement {
            member: self.member,
            generation: self.generation,
            progress: self.progress.clone(),
        }
    }

    /// Alias for [`DurableProgressTracker::durable_acknowledgement`].
    #[must_use]
    pub fn acknowledgement(&self) -> DurableAcknowledgement {
        self.durable_acknowledgement()
    }

    /// Alias for [`DurableProgressTracker::durable_acknowledgement`].
    #[must_use]
    pub fn durable_ack(&self) -> DurableAcknowledgement {
        self.durable_acknowledgement()
    }
}

/// Opaque proof of progress observed after a receiver's durability boundary.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurableAcknowledgement {
    member: u64,
    generation: u64,
    progress: ReplicationProgress,
}

impl DurableAcknowledgement {
    /// Returns the member that issued the acknowledgement.
    #[must_use]
    pub const fn member(&self) -> u64 {
        self.member
    }

    /// Returns the acknowledged contiguous prefix.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.progress.through()
    }

    /// Returns the explicit acknowledged ranges.
    #[must_use]
    pub fn gaps(&self) -> &[GapRange] {
        self.progress.gaps()
    }

    fn generation(&self) -> u64 {
        self.generation
    }
}

/// A stable retention floor guarded by verified durable acknowledgements.
///
/// A requester cursor is never accepted by this type. The floor advances
/// only to the minimum contiguous prefix in the latest acknowledgement from
/// every required member; sparse ranges and absent members contribute zero.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RetentionGuard {
    required: BTreeSet<u64>,
    acknowledgements: BTreeMap<u64, DurableAcknowledgement>,
    floor: u64,
}

impl RetentionGuard {
    /// Creates a guard for the required replica members.
    #[must_use]
    pub fn new<I>(required_members: I) -> Self
    where
        I: IntoIterator<Item = u64>,
    {
        Self {
            required: required_members.into_iter().collect(),
            acknowledgements: BTreeMap::new(),
            floor: 0,
        }
    }

    /// Returns the current history-retention floor.
    #[must_use]
    pub const fn floor(&self) -> u64 {
        self.floor
    }

    /// Returns the required members in canonical order.
    #[must_use]
    pub fn required_members(&self) -> impl ExactSizeIterator<Item = &u64> {
        self.required.iter()
    }

    /// Records a receiver-owned durable acknowledgement. Older reconnect
    /// observations are harmless and cannot move progress backwards.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressError::UnexpectedMember`] when the acknowledgement
    /// is not from a required member.
    pub fn record_acknowledgement(
        &mut self,
        acknowledgement: DurableAcknowledgement,
    ) -> Result<(), ProgressError> {
        if !self.required.contains(&acknowledgement.member) {
            return Err(ProgressError::UnexpectedMember { member: acknowledgement.member });
        }
        let replace = self
            .acknowledgements
            .get(&acknowledgement.member)
            .is_none_or(|current| acknowledgement.generation() >= current.generation());
        if replace {
            self.acknowledgements.insert(acknowledgement.member, acknowledgement);
        }
        Ok(())
    }

    /// Alias for [`RetentionGuard::record_acknowledgement`].
    ///
    /// # Errors
    ///
    /// Returns the validation error from
    /// [`RetentionGuard::record_acknowledgement`].
    pub fn record_ack(
        &mut self,
        acknowledgement: DurableAcknowledgement,
    ) -> Result<(), ProgressError> {
        self.record_acknowledgement(acknowledgement)
    }

    /// Alias for [`RetentionGuard::record_acknowledgement`].
    ///
    /// # Errors
    ///
    /// Returns the validation error from
    /// [`RetentionGuard::record_acknowledgement`].
    pub fn accept_ack(
        &mut self,
        acknowledgement: DurableAcknowledgement,
    ) -> Result<(), ProgressError> {
        self.record_acknowledgement(acknowledgement)
    }

    /// Returns the safe contiguous prefix. Every absent required member and
    /// every member with only sparse ranges contributes zero.
    #[must_use]
    pub fn safe_contiguous_prefix(&self) -> u64 {
        self.required
            .iter()
            .map(|member| {
                self.acknowledgements.get(member).map_or(0, DurableAcknowledgement::through)
            })
            .min()
            .unwrap_or(0)
    }

    /// Advances to the safe prefix and returns the resulting floor.
    pub fn advance_floor(&mut self) -> u64 {
        self.floor = self.floor.max(self.safe_contiguous_prefix());
        self.floor
    }

    /// Alias for [`RetentionGuard::advance_floor`].
    pub fn advance(&mut self) -> u64 {
        self.advance_floor()
    }

    /// Tests whether history at `counter` is safe to collect.
    #[must_use]
    pub fn can_collect(&self, counter: u64) -> bool {
        counter != 0 && counter <= self.floor
    }

    /// Explicitly rejects a requester cursor at the retention boundary.
    ///
    /// # Errors
    ///
    /// Always returns [`ProgressError::UnverifiedCursor`].
    #[allow(clippy::unused_self, reason = "the guard is the retention authority")]
    pub fn reject_cursor(&self, _cursor: &ReplicationCursor) -> Result<(), ProgressError> {
        Err(ProgressError::UnverifiedCursor)
    }
}

/// Native naming alias for callers that describe the floor as a stable
/// frontier.
pub type StableRetentionFrontier = RetentionGuard;

/// Native naming alias for the receiver progress model.
pub type NativeReplicationProgress = ReplicationProgress;

/// Native naming alias for a durable receiver progress model.
pub type DurableReplicationProgress = ReplicationProgress;

/// Native naming alias for a requester repair cursor.
pub type ProgressCursor = ReplicationCursor;

/// Native naming alias for a bounded out-of-order interval.
pub type GapInterval = GapRange;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_earlier_batch_does_not_advance_prefix() {
        let mut progress = ReplicationProgress::new();
        progress.record_interval(5, 7).unwrap();
        assert_eq!(progress.through(), 0);
        assert_eq!(progress.gaps(), &[GapRange::new(5, 7).unwrap()]);
        assert!(!progress.contains(4));
        assert!(progress.contains(5));
    }

    #[test]
    fn duplicates_and_reordering_are_idempotent() {
        let mut first = ReplicationProgress::with_gap_limit(4);
        for (a, b) in [(5, 7), (1, 2), (3, 4), (5, 7)] {
            first.record_interval(a, b).unwrap();
        }
        let mut second = ReplicationProgress::with_gap_limit(4);
        for (a, b) in [(3, 4), (5, 7), (1, 2), (5, 7)] {
            second.record_interval(a, b).unwrap();
        }
        assert_eq!(first, second);
        assert_eq!(first.through(), 7);
        assert!(first.gaps().is_empty());
    }

    #[test]
    fn gap_exhaustion_is_atomic() {
        let mut progress = ReplicationProgress::with_gap_limit(2);
        progress.record(2).unwrap();
        progress.record(4).unwrap();
        let before = progress.clone();
        assert_eq!(progress.record(6), Err(ProgressError::GapLimitExceeded { limit: 2 }));
        assert_eq!(progress, before);
    }

    #[test]
    fn repair_ranges_name_each_missing_hole() {
        let mut progress = ReplicationProgress::new();
        progress.record_interval(4, 5).unwrap();
        progress.record_interval(9, 10).unwrap();
        assert_eq!(
            progress.missing_ranges(11),
            [
                GapRange::new(1, 3).unwrap(),
                GapRange::new(6, 8).unwrap(),
                GapRange::new(11, 11).unwrap()
            ]
        );
        progress.record_interval(1, 8).unwrap();
        assert_eq!(progress.through(), 10);
        assert!(progress.missing_ranges(11).iter().any(|range| range.first() == 11));
    }

    #[test]
    fn reconnect_cursor_is_repair_hint_not_retention_proof() {
        let mut receiver = DurableProgressTracker::new(7);
        receiver.record_durable_range(2, 4).unwrap();
        let cursor = receiver.progress().cursor();
        let mut guard = RetentionGuard::new([7]);
        assert_eq!(guard.reject_cursor(&cursor), Err(ProgressError::UnverifiedCursor));
        assert_eq!(guard.floor(), 0);
        guard.record_acknowledgement(receiver.durable_acknowledgement()).unwrap();
        assert_eq!(guard.advance_floor(), 0, "a gap before two blocks collection");
        receiver.record_durable(1).unwrap();
        guard.record_ack(receiver.acknowledgement()).unwrap();
        assert_eq!(guard.advance_floor(), 4);
    }

    #[test]
    fn unsafe_retention_waits_for_every_required_member() {
        let mut first = DurableProgressTracker::new(1);
        first.record_durable_range(1, 9).unwrap();
        let mut second = DurableProgressTracker::new(2);
        second.record_durable_range(1, 4).unwrap();
        let mut guard = RetentionGuard::new([1, 2]);
        guard.record_ack(first.acknowledgement()).unwrap();
        assert_eq!(guard.advance_floor(), 0);
        guard.record_ack(second.acknowledgement()).unwrap();
        assert_eq!(guard.advance_floor(), 4);
        assert!(guard.can_collect(4));
        assert!(!guard.can_collect(5));
    }

    #[test]
    fn reconnect_cannot_replay_a_stale_ack_into_a_lower_floor() {
        let mut receiver = DurableProgressTracker::new(1);
        receiver.record_durable_range(1, 3).unwrap();
        let old = receiver.durable_ack();
        receiver.record_durable_range(4, 6).unwrap();
        let current = receiver.durable_ack();
        let mut guard = RetentionGuard::new([1]);
        guard.record_ack(current).unwrap();
        assert_eq!(guard.advance(), 6);
        guard.record_ack(old).unwrap();
        assert_eq!(guard.advance(), 6);
    }
}
