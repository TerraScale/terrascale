//! Deterministic anti-entropy and Merkle repair primitives.
//!
//! This module deliberately contains no transport or storage code.  A caller
//! gives it canonical causal and materialized entries, and receives bounded
//! comparison, repair, scheduling, and verification decisions.  Hashes are
//! made through the native causal codec's domain-separated BLAKE3 helper; the
//! replication crate therefore does not grow a second crypto dependency.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::fmt;

use terrabase_causal::{CanonicalEncode, CausalContext, DatabaseId, PartitionId, TenantId};

use crate::{GapRange, ReplicationProgress};
use crate::{ReplicaDoc, ReplicaValue};

/// The native digest width.
pub type Hash32 = [u8; 32];

/// The largest number of entries accepted in one Merkle leaf by default.
pub const DEFAULT_LEAF_ENTRIES: usize = 64;

/// The largest number of logical leaves accepted by default.
pub const DEFAULT_MAX_LEAVES: usize = 4096;

/// A half-open logical key range. `end` is inclusive so a range can name a
/// single key and can be used directly for replacement during repair.
#[derive(Clone, Debug, Eq, PartialEq, Ord, PartialOrd)]
pub struct KeyRange {
    /// First key in the range, or the beginning of the key space.
    pub start: Option<Vec<u8>>,
    /// Last key in the range, or the end of the key space.
    pub end: Option<Vec<u8>>,
}

impl KeyRange {
    /// Creates a range and rejects reversed bounds.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::InvalidRange`] for reversed bounds.
    pub fn new(start: Option<Vec<u8>>, end: Option<Vec<u8>>) -> Result<Self, RepairError> {
        if let (Some(start), Some(end)) = (&start, &end)
            && start > end
        {
            return Err(RepairError::InvalidRange);
        }
        Ok(Self { start, end })
    }

    fn contains(&self, key: &[u8]) -> bool {
        self.start.as_ref().is_none_or(|start| key >= start.as_slice())
            && self.end.as_ref().is_none_or(|end| key <= end.as_slice())
    }
}

/// One canonical logical entry in a dual Merkle tree.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MerkleEntry {
    /// Canonical object or bucket key.
    pub key: Vec<u8>,
    /// Canonical causal kernel bytes (dots, summaries, tombstones, and CRDT
    /// payload required for future joins).
    pub causal: Vec<u8>,
    /// Canonical visible state bytes.
    pub materialized: Vec<u8>,
}

impl MerkleEntry {
    /// Constructs one entry.
    #[must_use]
    pub fn new(
        key: impl Into<Vec<u8>>,
        causal: impl Into<Vec<u8>>,
        materialized: impl Into<Vec<u8>>,
    ) -> Self {
        Self { key: key.into(), causal: causal.into(), materialized: materialized.into() }
    }
}

/// Both independently named roots at one frontier.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MerkleRoots {
    /// Root of causal state.
    pub causal: Hash32,
    /// Root of materialized state.
    pub materialized: Hash32,
}

/// A differing logical range and the independent state that differs there.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MerkleMismatch {
    /// The bounded logical range requiring inspection or repair.
    pub range: KeyRange,
    /// Causal state differs in this range.
    pub causal: bool,
    /// Materialized state differs in this range.
    pub materialized: bool,
}

/// A bounded Merkle tree over sorted logical entries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MerkleTree {
    entries: Vec<MerkleEntry>,
    leaves: Vec<MerkleLeaf>,
    leaf_entries: usize,
    max_leaves: usize,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct MerkleLeaf {
    range: KeyRange,
    causal: Hash32,
    materialized: Hash32,
    entries: Vec<MerkleEntry>,
}

/// Builds a bounded tree from canonical entries.
impl MerkleTree {
    /// Builds with the default bounds.
    ///
    /// # Errors
    ///
    /// Returns a bound or duplicate-key error when entries cannot be represented.
    pub fn build(entries: impl IntoIterator<Item = MerkleEntry>) -> Result<Self, RepairError> {
        Self::build_bounded(entries, DEFAULT_LEAF_ENTRIES, DEFAULT_MAX_LEAVES)
    }

    /// Builds with explicit leaf and total-leaf bounds.
    ///
    /// # Errors
    ///
    /// Returns an error when a bound is zero, a key is duplicated, or the tree is too large.
    pub fn build_bounded(
        entries: impl IntoIterator<Item = MerkleEntry>,
        leaf_entries: usize,
        max_leaves: usize,
    ) -> Result<Self, RepairError> {
        if leaf_entries == 0 || max_leaves == 0 {
            return Err(RepairError::InvalidBounds);
        }
        let mut entries: Vec<_> = entries.into_iter().collect();
        entries.sort_by(|left, right| left.key.cmp(&right.key));
        for pair in entries.windows(2) {
            if pair[0].key == pair[1].key {
                return Err(RepairError::DuplicateKey);
            }
        }
        let leaves = entries
            .chunks(leaf_entries)
            .map(|chunk| {
                let chunk = chunk.to_vec();
                let range = KeyRange {
                    start: chunk.first().map(|entry| entry.key.clone()),
                    end: chunk.last().map(|entry| entry.key.clone()),
                };
                MerkleLeaf {
                    range,
                    causal: causal_leaf_hash(&chunk),
                    materialized: materialized_leaf_hash(&chunk),
                    entries: chunk,
                }
            })
            .collect::<Vec<_>>();
        if leaves.len() > max_leaves {
            return Err(RepairError::BoundsExceeded { leaves: leaves.len(), max_leaves });
        }
        Ok(Self { entries, leaves, leaf_entries, max_leaves })
    }

    /// Returns the sorted entries represented by the tree.
    #[must_use]
    pub fn entries(&self) -> &[MerkleEntry] {
        &self.entries
    }

    /// Returns the number of logical leaves.
    #[must_use]
    pub const fn leaf_count(&self) -> usize {
        self.leaves.len()
    }

    /// Computes independent causal and materialized roots.
    #[must_use]
    pub fn roots(&self) -> MerkleRoots {
        MerkleRoots {
            causal: tree_root(
                "anti-entropy-causal-node",
                self.entries.iter().map(causal_entry_hash).collect(),
            ),
            materialized: tree_root(
                "anti-entropy-materialized-node",
                self.entries.iter().map(materialized_entry_hash).collect(),
            ),
        }
    }

    /// Locates bounded ranges whose causal or materialized leaf differs.
    #[must_use]
    pub fn mismatches(&self, other: &Self) -> Vec<MerkleMismatch> {
        let mut ranges = BTreeMap::<KeyRange, (bool, bool)>::new();
        let left = self
            .entries
            .iter()
            .map(|entry| (entry.key.as_slice(), entry))
            .collect::<BTreeMap<_, _>>();
        let right = other
            .entries
            .iter()
            .map(|entry| (entry.key.as_slice(), entry))
            .collect::<BTreeMap<_, _>>();
        let keys = left.keys().chain(right.keys()).copied().collect::<BTreeSet<_>>();
        for key in keys {
            let left_entry = left.get(key).copied();
            let right_entry = right.get(key).copied();
            let range = KeyRange { start: Some(key.to_vec()), end: Some(key.to_vec()) };
            let causal = left_entry.map(causal_entry_hash) != right_entry.map(causal_entry_hash);
            let materialized =
                left_entry.map(materialized_entry_hash) != right_entry.map(materialized_entry_hash);
            if causal || materialized {
                ranges
                    .entry(range)
                    .and_modify(|flags| {
                        flags.0 |= causal;
                        flags.1 |= materialized;
                    })
                    .or_insert((causal, materialized));
            }
        }
        ranges
            .into_iter()
            .map(|(range, (causal, materialized))| MerkleMismatch { range, causal, materialized })
            .collect()
    }

    /// Plans bounded verified range repair from `source` into this tree.
    ///
    /// # Errors
    ///
    /// Returns an error when the range budget is zero or mismatches exceed it.
    pub fn plan_repair(&self, source: &Self, max_ranges: usize) -> Result<RepairPlan, RepairError> {
        if max_ranges == 0 {
            return Err(RepairError::InvalidBounds);
        }
        let mismatches = self.mismatches(source);
        if mismatches.len() > max_ranges {
            return Err(RepairError::RepairBudgetExceeded { ranges: mismatches.len(), max_ranges });
        }
        let ranges = mismatches.iter().map(|mismatch| mismatch.range.clone()).collect();
        Ok(RepairPlan { ranges, expected: source.roots(), source_entries: source.entries.clone() })
    }

    /// Produces one source chunk for a planned range.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::InvalidRange`] when the range is invalid.
    pub fn chunk(&self, range: &KeyRange) -> Result<RepairChunk, RepairError> {
        let entries = self
            .entries
            .iter()
            .filter(|entry| range.contains(&entry.key))
            .cloned()
            .collect::<Vec<_>>();
        let causal = causal_leaf_hash(&entries);
        let materialized = materialized_leaf_hash(&entries);
        Ok(RepairChunk { range: range.clone(), entries, causal, materialized })
    }

    /// Applies source chunks after checking their hashes and the final roots.
    ///
    /// # Errors
    ///
    /// Returns an error for missing, unexpected, corrupt, or root-mismatching chunks.
    pub fn apply_verified_chunks(
        &mut self,
        plan: &RepairPlan,
        chunks: &[RepairChunk],
    ) -> Result<(), RepairError> {
        if chunks.len() != plan.ranges.len()
            || chunks.iter().any(|chunk| !plan.ranges.contains(&chunk.range))
        {
            return Err(RepairError::UnexpectedChunk);
        }
        for chunk in chunks {
            if causal_leaf_hash(&chunk.entries) != chunk.causal
                || materialized_leaf_hash(&chunk.entries) != chunk.materialized
            {
                return Err(RepairError::ChunkCorrupt);
            }
        }
        let mut next = self.entries.clone();
        for chunk in chunks {
            next.retain(|entry| !chunk.range.contains(&entry.key));
            next.extend(chunk.entries.clone());
        }
        let rebuilt = Self::build_bounded(next, self.leaf_entries, self.max_leaves)?;
        if rebuilt.roots() != plan.expected {
            return Err(RepairError::RootMismatch);
        }
        *self = rebuilt;
        Ok(())
    }
}

/// A source-authenticated range payload. The hashes are checked before use.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RepairChunk {
    /// The logical range replaced by this chunk.
    pub range: KeyRange,
    /// Complete canonical entries in the range.
    pub entries: Vec<MerkleEntry>,
    /// Causal chunk hash.
    pub causal: Hash32,
    /// Materialized chunk hash.
    pub materialized: Hash32,
}

/// A bounded repair decision.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RepairPlan {
    /// Ranges that must be transferred.
    pub ranges: Vec<KeyRange>,
    /// Roots expected after all ranges verify.
    pub expected: MerkleRoots,
    source_entries: Vec<MerkleEntry>,
}

/// Missing contiguous and selective ranges discovered from two progress
/// summaries.  A range is requested only when the source explicitly proves
/// it has the counters; source gaps are never treated as an empty interval.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProgressRepairPlan {
    /// Ranges the receiver may safely request from the source.
    pub ranges: Vec<GapRange>,
    /// Source contiguous prefix used when the plan was made.
    pub source_through: u64,
}

/// Computes a bounded-prefix/gap repair request from source and receiver
/// progress.  The source's sparse ranges are intersected with the receiver's
/// missing ranges, preserving the distinction between received and unknown.
///
/// # Errors
///
/// Returns an error when the range budget is zero or the budget is exceeded.
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
        return Err(RepairError::RepairBudgetExceeded { ranges: ranges.len(), max_ranges });
    }
    Ok(ProgressRepairPlan {
        ranges: ranges.into_iter().collect(),
        source_through: source.through(),
    })
}

impl RepairPlan {
    /// Creates verified chunks from the source tree represented by this plan.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::SourceChanged`] if the source no longer matches the plan.
    pub fn source_chunks(&self, source: &MerkleTree) -> Result<Vec<RepairChunk>, RepairError> {
        if source.entries != self.source_entries {
            return Err(RepairError::SourceChanged);
        }
        self.ranges.iter().map(|range| source.chunk(range)).collect()
    }
}

/// Typed anti-entropy failure.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RepairError {
    /// A range's start follows its end.
    InvalidRange,
    /// A zero bound cannot produce a bounded tree or schedule.
    InvalidBounds,
    /// Two entries use one logical key.
    DuplicateKey,
    /// The tree exceeds its leaf bound.
    BoundsExceeded {
        /// Number of leaves requested.
        leaves: usize,
        /// Maximum permitted leaves.
        max_leaves: usize,
    },
    /// The repair range budget is exhausted.
    RepairBudgetExceeded {
        /// Number of differing ranges.
        ranges: usize,
        /// Maximum permitted ranges.
        max_ranges: usize,
    },
    /// A chunk was not part of the requested plan.
    UnexpectedChunk,
    /// Chunk contents do not match its authenticated hashes.
    ChunkCorrupt,
    /// Rebuilt roots differ from the source roots.
    RootMismatch,
    /// The source changed between planning and transfer.
    SourceChanged,
    /// Queue capacity has been reached.
    QueueFull,
    /// A stable acknowledgement was attempted without verification.
    NotVerified,
    /// The supplied verification roots differ from the expected roots.
    VerificationMismatch,
}

impl fmt::Display for RepairError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidRange => f.write_str("invalid logical range"),
            Self::InvalidBounds => f.write_str("anti-entropy bound must be nonzero"),
            Self::DuplicateKey => f.write_str("duplicate Merkle key"),
            Self::BoundsExceeded { .. } => f.write_str("Merkle tree bound exceeded"),
            Self::RepairBudgetExceeded { .. } => f.write_str("repair range budget exceeded"),
            Self::UnexpectedChunk => f.write_str("unexpected repair chunk"),
            Self::ChunkCorrupt => f.write_str("corrupt repair chunk"),
            Self::RootMismatch => f.write_str("post-repair Merkle root mismatch"),
            Self::SourceChanged => f.write_str("repair source changed"),
            Self::QueueFull => f.write_str("anti-entropy queue is full"),
            Self::NotVerified => f.write_str("frontier is not verified"),
            Self::VerificationMismatch => f.write_str("verification roots mismatch"),
        }
    }
}

impl std::error::Error for RepairError {}

/// Builds an anti-entropy tree from a native replica. The causal frontier is
/// included in every object entry, so equal visible state with different
/// causal history still produces different causal roots.
///
/// # Errors
///
/// Returns an error when the supplied tree bounds cannot represent the replica.
pub fn tree_for_replica(
    replica: &ReplicaDoc,
    leaf_entries: usize,
    max_leaves: usize,
) -> Result<MerkleTree, RepairError> {
    let causal = replica.frontier().context().canonical_bytes();
    let mut entries = replica
        .materialized()
        .into_iter()
        .map(|(document, body)| {
            MerkleEntry::new(document.into_bytes(), causal.clone(), canonical_scope_body(&body))
        })
        .collect::<Vec<_>>();
    if entries.is_empty() {
        // Keep a causal-only leaf even when the visible state is empty (for
        // example, after a delete or when a replica has only retained dots).
        entries.push(MerkleEntry::new(b"\0frontier", causal, Vec::new()));
    }
    MerkleTree::build_bounded(entries, leaf_entries, max_leaves)
}

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
    /// Membership changed.
    MembershipChanged,
    /// A checksum or Merkle mismatch was observed.
    ChecksumMismatch,
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

    /// Creates a schedule with deterministic peer-specific jitter.  Jitter
    /// is bounded to one quarter of the interval and is derived solely from
    /// `seed`, so reconnects and process restarts produce the same schedule.
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

/// Tenant/database/Partition/Region fairness key.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct QueueKey {
    /// Tenant identity.
    pub tenant: TenantId,
    /// Database identity.
    pub database: DatabaseId,
    /// Logical Partition.
    pub partition: PartitionId,
    /// Region catalog name.
    pub region: String,
}

impl QueueKey {
    /// Creates a queue key.
    #[must_use]
    pub fn new(
        tenant: TenantId,
        database: DatabaseId,
        partition: PartitionId,
        region: impl Into<String>,
    ) -> Self {
        Self { tenant, database, partition, region: region.into() }
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
    /// Opaque range work identifier.
    pub range: Option<KeyRange>,
}

/// A bounded round-robin scheduler. Every nonempty key receives a turn before
/// a key can receive another turn, preventing a hot tenant from starving one
/// quiet tenant while keeping memory bounded.
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
    /// Contiguous causal prefix.
    pub through: u64,
    /// Verified causal root.
    pub causal: Hash32,
    /// Verified materialized root.
    pub materialized: Hash32,
}

/// Stable frontier that admits only verified observations.
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

    /// Records a member only after its roots match the expected roots.
    ///
    /// # Errors
    ///
    /// Returns an error if roots differ or the member is not required.
    pub fn acknowledge(
        &mut self,
        observation: VerifiedFrontier,
        expected: MerkleRoots,
    ) -> Result<(), RepairError> {
        if observation.causal != expected.causal
            || observation.materialized != expected.materialized
        {
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

fn causal_leaf_hash(entries: &[MerkleEntry]) -> Hash32 {
    let mut bytes = Vec::new();
    for entry in entries {
        put_bytes(&mut bytes, &entry.key);
        put_bytes(&mut bytes, &entry.causal);
    }
    hash("anti-entropy-causal-leaf", &bytes)
}

fn causal_entry_hash(entry: &MerkleEntry) -> Hash32 {
    let mut bytes = Vec::new();
    put_bytes(&mut bytes, &entry.key);
    put_bytes(&mut bytes, &entry.causal);
    hash("anti-entropy-causal-leaf", &bytes)
}

fn materialized_entry_hash(entry: &MerkleEntry) -> Hash32 {
    let mut bytes = Vec::new();
    put_bytes(&mut bytes, &entry.key);
    put_bytes(&mut bytes, &entry.materialized);
    hash("anti-entropy-materialized-leaf", &bytes)
}

fn materialized_leaf_hash(entries: &[MerkleEntry]) -> Hash32 {
    let mut bytes = Vec::new();
    for entry in entries {
        put_bytes(&mut bytes, &entry.key);
        put_bytes(&mut bytes, &entry.materialized);
    }
    hash("anti-entropy-materialized-leaf", &bytes)
}

fn tree_root(domain: &'static str, mut hashes: Vec<Hash32>) -> Hash32 {
    if hashes.is_empty() {
        return hash(domain, &[]);
    }
    while hashes.len() > 1 {
        hashes = hashes
            .chunks(2)
            .map(|pair| {
                let mut bytes = Vec::with_capacity(64);
                bytes.extend_from_slice(&pair[0]);
                bytes.extend_from_slice(pair.get(1).unwrap_or(&pair[0]));
                hash(domain, &bytes)
            })
            .collect();
    }
    hashes[0]
}

struct Bytes(Vec<u8>);
impl CanonicalEncode for Bytes {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        output.extend_from_slice(&self.0);
    }
}

fn hash(domain: &'static str, bytes: &[u8]) -> Hash32 {
    terrabase_causal::canonical_hash(domain, &Bytes(bytes.to_vec()))
        .expect("fixed hash domain is valid")
}

fn put_bytes(output: &mut Vec<u8>, bytes: &[u8]) {
    output.extend_from_slice(&(bytes.len() as u64).to_be_bytes());
    output.extend_from_slice(bytes);
}

fn canonical_scope_body(body: &BTreeMap<String, ReplicaValue>) -> Vec<u8> {
    let mut bytes = Vec::new();
    for (key, value) in body {
        put_bytes(&mut bytes, key.as_bytes());
        canonical_value(value, &mut bytes);
    }
    bytes
}

fn canonical_value(value: &ReplicaValue, output: &mut Vec<u8>) {
    match value {
        ReplicaValue::Null => output.push(0),
        ReplicaValue::Boolean(value) => output.extend_from_slice(&[1, u8::from(*value)]),
        ReplicaValue::Integer(value) => {
            output.push(2);
            output.extend_from_slice(&value.to_be_bytes());
        }
        ReplicaValue::Number { bits } => {
            output.push(3);
            output.extend_from_slice(&bits.to_be_bytes());
        }
        ReplicaValue::Text(value) => {
            output.push(4);
            put_bytes(output, value.as_bytes());
        }
        ReplicaValue::Array(values) => {
            output.push(5);
            output.extend_from_slice(&(values.len() as u64).to_be_bytes());
            for value in values {
                canonical_value(value, output);
            }
        }
        ReplicaValue::Object(values) => {
            output.push(6);
            output.extend_from_slice(&(values.len() as u64).to_be_bytes());
            for (key, value) in values {
                put_bytes(output, key.as_bytes());
                canonical_value(value, output);
            }
        }
    }
}

/// Hashes one causal context with the anti-entropy causal domain.
#[must_use]
pub fn causal_hash(context: &CausalContext) -> Hash32 {
    hash("anti-entropy-causal-state", &context.canonical_bytes())
}

/// Hashes canonical materialized bytes with the anti-entropy materialized domain.
#[must_use]
pub fn materialized_hash(bytes: &[u8]) -> Hash32 {
    hash("anti-entropy-materialized-state", bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entries(value: &str) -> Vec<MerkleEntry> {
        vec![
            MerkleEntry::new(b"a", b"causal", value.as_bytes()),
            MerkleEntry::new(b"b", b"causal", value.as_bytes()),
        ]
    }

    #[test]
    fn order_is_canonical() {
        let first = MerkleTree::build(entries("v")).unwrap();
        let mut reversed = entries("v");
        reversed.reverse();
        let second = MerkleTree::build(reversed).unwrap();
        assert_eq!(first.roots(), second.roots());
    }

    #[test]
    fn corruption_is_rejected() {
        let local = MerkleTree::build(entries("old")).unwrap();
        let source = MerkleTree::build(entries("new")).unwrap();
        let plan = local.plan_repair(&source, 4).unwrap();
        let mut chunks = plan.source_chunks(&source).unwrap();
        chunks[0].entries[0].materialized[0] ^= 1;
        let mut target = local;
        assert_eq!(target.apply_verified_chunks(&plan, &chunks), Err(RepairError::ChunkCorrupt));
    }
}
