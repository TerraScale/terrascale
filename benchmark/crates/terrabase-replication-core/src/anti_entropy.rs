//! Range-based set reconciliation for committed journal rows.
//!
//! The reconciled set is `{(origin, sequence) -> row hash}`. Each insertion
//! updates an additive 128-bit fingerprint in a fixed-fanout hierarchy. A
//! comparison starts at one summary per origin, descends only through
//! mismatching ranges, and exchanges an exact id list only at leaf ranges.
//! The kernel owns no connection state, transport, or storage.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::ops::Bound;

use terrabase_causal::CanonicalEncode;

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
    #[allow(clippy::too_many_lines)]
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
                let last_bucket = first_bucket.saturating_add(self.fanout.saturating_sub(1));
                // A range may have a very large configured fanout.  Walking
                // every possible child would make a sparse reconciliation
                // proportional to that bound (and permits an accidental
                // `u64::MAX` loop).  Every non-empty child has a maintained
                // summary, so enumerate the union of those summaries in
                // canonical bucket order instead.  Empty children cannot
                // differ and need no wire message.
                let mut child_buckets = BTreeSet::new();
                for table in [self, peer] {
                    let start = SummaryKey {
                        origin: range.origin.clone(),
                        level: child_level,
                        bucket: first_bucket,
                    };
                    let end = SummaryKey {
                        origin: range.origin.clone(),
                        level: child_level,
                        bucket: last_bucket,
                    };
                    child_buckets.extend(
                        table
                            .summaries
                            .range((Bound::Included(start), Bound::Included(end)))
                            .filter_map(|(key, fingerprint)| {
                                (fingerprint.count != 0).then_some(key.bucket)
                            }),
                    );
                }
                for bucket in child_buckets {
                    // The bucket interval above already clips to the parent;
                    // this check also documents the inclusive sequence bound
                    // when the final child range saturates at `u64::MAX`.
                    let Some(zero_based_first) = bucket.checked_mul(child_span) else {
                        continue;
                    };
                    if zero_based_first >= range.last {
                        continue;
                    }
                    let child =
                        JournalRange::new(range.origin.clone(), child_level, bucket, child_span);
                    next.push(child);
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

    /// Constructs a validated range in this table's fixed hierarchy.
    ///
    /// # Errors
    /// Rejects empty origins, levels above the hierarchy, and overflowing buckets.
    pub fn range(&self, origin: &str, level: u8, bucket: u64) -> Result<JournalRange, RepairError> {
        let span = self.level_span(level);
        if origin.is_empty()
            || level > self.top_level
            || bucket.checked_mul(span).is_none_or(|first| first == u64::MAX)
        {
            return Err(RepairError::InvalidRange);
        }
        Ok(JournalRange::new(origin.to_owned(), level, bucket, span))
    }

    /// Returns the complete sequence-space root for one origin.
    ///
    /// # Errors
    /// Rejects an empty origin.
    pub fn root(&self, origin: &str) -> Result<JournalRange, RepairError> {
        self.range(origin, self.top_level, 0)
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
            Self::DuplicateRowIdentity => formatter.write_str("duplicate journal row identity"),
        }
    }
}

impl std::error::Error for RepairError {}

/// Domain-separated additive contribution of one immutable publication identity and hash.
#[must_use]
pub fn row_contribution(id: &JournalRowId, row_hash: &Hash32) -> u128 {
    let mut bytes = Vec::with_capacity(id.origin.len() + 48);
    put_bytes(&mut bytes, id.origin.as_bytes());
    bytes.extend_from_slice(&id.sequence.to_be_bytes());
    bytes.extend_from_slice(row_hash);
    let digest = hash("range-reconciliation-row", &bytes);
    let mut contribution = [0; 16];
    contribution.copy_from_slice(&digest[..16]);
    u128::from_be_bytes(contribution)
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
