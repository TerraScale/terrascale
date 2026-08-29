//! Deterministic causal foundations for `TerraBaseV2`.
//!
//! This crate has no storage, transport, async-runtime, or wall-clock I/O. Callers
//! inject physical time and membership-owned identities. The types implement the
//! native contracts fixed by ADR 0022.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;

/// The first native causal codec major version.
pub const CODEC_VERSION: u8 = 1;

macro_rules! opaque_id {
    ($name:ident, $docs:literal) => {
        #[doc = $docs]
        #[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
        pub struct $name([u8; 16]);

        impl $name {
            /// Creates an identity from its exact canonical bytes.
            #[must_use]
            pub const fn from_bytes(bytes: [u8; 16]) -> Self {
                Self(bytes)
            }

            /// Returns the exact canonical bytes.
            #[must_use]
            pub const fn as_bytes(&self) -> &[u8; 16] {
                &self.0
            }
        }
    };
}

opaque_id!(TenantId, "An opaque tenant identity.");
opaque_id!(DatabaseId, "An opaque database identity.");
opaque_id!(ReplicaId, "A membership-allocated replica identity.");

/// A logical Partition identifier.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct PartitionId(u64);

impl PartitionId {
    /// Creates a logical Partition identifier.
    #[must_use]
    pub const fn new(value: u64) -> Self {
        Self(value)
    }

    /// Returns the numeric identifier.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// A membership-allocated incarnation of one replica.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ReplicaEpoch(u64);

impl ReplicaEpoch {
    /// Creates an epoch. Zero is reserved and rejected.
    ///
    /// # Errors
    ///
    /// Returns [`IdentityError::ZeroReserved`] for zero.
    pub const fn new(value: u64) -> Result<Self, IdentityError> {
        if value == 0 { Err(IdentityError::ZeroReserved) } else { Ok(Self(value)) }
    }

    /// Returns the epoch value.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// A counter in one Database, Partition, and actor domain.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct OriginCounter(u64);

impl OriginCounter {
    /// Creates a counter. Zero is reserved and rejected.
    ///
    /// # Errors
    ///
    /// Returns [`IdentityError::ZeroReserved`] for zero.
    pub const fn new(value: u64) -> Result<Self, IdentityError> {
        if value == 0 { Err(IdentityError::ZeroReserved) } else { Ok(Self(value)) }
    }

    /// Returns the counter value.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// An index inside one canonical commit.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct MutationOrdinal(u32);

impl MutationOrdinal {
    /// Creates a mutation ordinal. Ordinals start at zero.
    #[must_use]
    pub const fn new(value: u32) -> Self {
        Self(value)
    }

    /// Returns the ordinal value.
    #[must_use]
    pub const fn get(self) -> u32 {
        self.0
    }
}

/// An identity validation failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IdentityError {
    /// The protocol reserves zero for absence or an invalid identity.
    ZeroReserved,
}

impl fmt::Display for IdentityError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ZeroReserved => formatter.write_str("zero is reserved"),
        }
    }
}

impl std::error::Error for IdentityError {}

/// A server replication actor.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ActorId {
    replica: ReplicaId,
    epoch: ReplicaEpoch,
}

impl ActorId {
    /// Creates an actor identity.
    #[must_use]
    pub const fn new(replica: ReplicaId, epoch: ReplicaEpoch) -> Self {
        Self { replica, epoch }
    }

    /// Returns the replica identity.
    #[must_use]
    pub const fn replica(self) -> ReplicaId {
        self.replica
    }

    /// Returns the replica epoch.
    #[must_use]
    pub const fn epoch(self) -> ReplicaEpoch {
        self.epoch
    }
}

/// The globally unique identity of one Partition commit.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct CommitId {
    database: DatabaseId,
    partition: PartitionId,
    actor: ActorId,
    counter: OriginCounter,
}

impl CommitId {
    /// Creates a commit identity.
    #[must_use]
    pub const fn new(
        database: DatabaseId,
        partition: PartitionId,
        actor: ActorId,
        counter: OriginCounter,
    ) -> Self {
        Self { database, partition, actor, counter }
    }

    /// Returns the database identity.
    #[must_use]
    pub const fn database(self) -> DatabaseId {
        self.database
    }

    /// Returns the Partition identity.
    #[must_use]
    pub const fn partition(self) -> PartitionId {
        self.partition
    }

    /// Returns the origin actor.
    #[must_use]
    pub const fn actor(self) -> ActorId {
        self.actor
    }

    /// Returns the origin counter.
    #[must_use]
    pub const fn counter(self) -> OriginCounter {
        self.counter
    }

    /// Returns this commit's dot inside its known Database and Partition.
    #[must_use]
    pub const fn dot(self) -> Dot {
        Dot::new(self.actor, self.counter)
    }
}

/// A causal event inside a known Database and Partition.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Dot {
    actor: ActorId,
    counter: OriginCounter,
}

impl Dot {
    /// Creates a causal dot.
    #[must_use]
    pub const fn new(actor: ActorId, counter: OriginCounter) -> Self {
        Self { actor, counter }
    }

    /// Returns the actor.
    #[must_use]
    pub const fn actor(self) -> ActorId {
        self.actor
    }

    /// Returns the counter.
    #[must_use]
    pub const fn counter(self) -> OriginCounter {
        self.counter
    }
}

/// Identity of a mutation or element created inside a commit.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct MutationTag {
    commit: CommitId,
    ordinal: MutationOrdinal,
}

impl MutationTag {
    /// Creates a mutation tag.
    #[must_use]
    pub const fn new(commit: CommitId, ordinal: MutationOrdinal) -> Self {
        Self { commit, ordinal }
    }

    /// Returns the commit identity.
    #[must_use]
    pub const fn commit(self) -> CommitId {
        self.commit
    }

    /// Returns the mutation ordinal.
    #[must_use]
    pub const fn ordinal(self) -> MutationOrdinal {
        self.ordinal
    }
}

/// A closed interval of received, nonzero actor counters.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CounterInterval {
    first: u64,
    last: u64,
}

impl CounterInterval {
    /// Creates a nonempty received interval.
    ///
    /// # Errors
    ///
    /// Returns [`IntervalError::ZeroReserved`] for a zero start or
    /// [`IntervalError::Reversed`] when `first` follows `last`.
    pub const fn new(first: u64, last: u64) -> Result<Self, IntervalError> {
        if first == 0 {
            return Err(IntervalError::ZeroReserved);
        }
        if first > last {
            return Err(IntervalError::Reversed);
        }
        Ok(Self { first, last })
    }

    /// Returns the first received counter.
    #[must_use]
    pub const fn first(self) -> u64 {
        self.first
    }

    /// Returns the last received counter.
    #[must_use]
    pub const fn last(self) -> u64 {
        self.last
    }

    const fn contains(self, counter: u64) -> bool {
        self.first <= counter && counter <= self.last
    }
}

/// An invalid counter interval.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IntervalError {
    /// Counter zero is reserved.
    ZeroReserved,
    /// The first counter follows the last counter.
    Reversed,
}

impl fmt::Display for IntervalError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ZeroReserved => formatter.write_str("counter zero is reserved"),
            Self::Reversed => formatter.write_str("counter interval is reversed"),
        }
    }
}

impl std::error::Error for IntervalError {}

/// Normalized received progress for one actor.
///
/// `through` means every counter from one through that value is present. The
/// intervals name counters present above the prefix, never counters missing.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ActorProgress {
    through: u64,
    intervals: Vec<CounterInterval>,
}

impl ActorProgress {
    /// Creates empty progress.
    #[must_use]
    pub const fn new() -> Self {
        Self { through: 0, intervals: Vec::new() }
    }

    /// Returns the durable contiguous prefix.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.through
    }

    /// Returns normalized received intervals above the prefix.
    #[must_use]
    pub fn intervals(&self) -> &[CounterInterval] {
        &self.intervals
    }

    /// Reports whether the counter is represented by this progress.
    #[must_use]
    pub fn contains(&self, counter: OriginCounter) -> bool {
        let counter = counter.get();
        counter <= self.through || self.intervals.iter().any(|interval| interval.contains(counter))
    }

    fn covers(&self, interval: CounterInterval) -> bool {
        interval.last <= self.through
            || self
                .intervals
                .iter()
                .any(|ours| ours.first <= interval.first && ours.last >= interval.last)
    }

    /// Inserts one received counter and restores canonical normalization.
    pub fn insert(&mut self, counter: OriginCounter) {
        self.insert_interval(CounterInterval { first: counter.get(), last: counter.get() });
    }

    /// Inserts a received interval and restores canonical normalization.
    pub fn insert_interval(&mut self, mut incoming: CounterInterval) {
        if incoming.last <= self.through {
            return;
        }
        incoming.first = incoming.first.max(self.through.saturating_add(1));
        let mut merged = Vec::with_capacity(self.intervals.len().saturating_add(1));
        let mut placed = false;
        for interval in self.intervals.drain(..) {
            if interval.last.saturating_add(1) < incoming.first {
                merged.push(interval);
            } else if incoming.last.saturating_add(1) < interval.first {
                if !placed {
                    merged.push(incoming);
                    placed = true;
                }
                merged.push(interval);
            } else {
                incoming.first = incoming.first.min(interval.first);
                incoming.last = incoming.last.max(interval.last);
            }
        }
        if !placed {
            merged.push(incoming);
        }
        self.intervals = merged;
        while self
            .intervals
            .first()
            .is_some_and(|interval| interval.first <= self.through.saturating_add(1))
        {
            let interval = self.intervals.remove(0);
            self.through = self.through.max(interval.last);
        }
    }

    /// Unions another actor's received progress into this one.
    pub fn union(&mut self, other: &Self) {
        if other.through > 0 {
            self.insert_interval(CounterInterval { first: 1, last: other.through });
        }
        for interval in &other.intervals {
            self.insert_interval(*interval);
        }
    }
}

/// A normalized causal context sorted by canonical actor identity.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct CausalContext(BTreeMap<ActorId, ActorProgress>);

impl CausalContext {
    /// Creates an empty context.
    #[must_use]
    pub const fn new() -> Self {
        Self(BTreeMap::new())
    }

    /// Returns sorted actor progress entries.
    #[must_use]
    pub fn actors(&self) -> impl ExactSizeIterator<Item = (&ActorId, &ActorProgress)> {
        self.0.iter()
    }

    /// Reports whether the context contains a dot.
    #[must_use]
    pub fn contains(&self, dot: Dot) -> bool {
        self.0.get(&dot.actor()).is_some_and(|progress| progress.contains(dot.counter()))
    }

    /// Inserts a received dot.
    pub fn insert(&mut self, dot: Dot) {
        self.0.entry(dot.actor()).or_default().insert(dot.counter());
    }

    /// Inserts normalized actor progress. Empty progress is omitted.
    pub fn insert_progress(&mut self, actor: ActorId, progress: &ActorProgress) {
        if progress.through == 0 && progress.intervals.is_empty() {
            return;
        }
        self.0.entry(actor).or_default().union(progress);
    }

    /// Unions another context into this context.
    pub fn union(&mut self, other: &Self) {
        for (actor, progress) in &other.0 {
            self.insert_progress(*actor, progress);
        }
    }

    /// Returns whether this context includes every dot represented by `other`.
    #[must_use]
    pub fn dominates(&self, other: &Self) -> bool {
        other.0.iter().all(|(actor, progress)| {
            self.0.get(actor).is_some_and(|ours| {
                ours.through >= progress.through
                    && progress.intervals.iter().all(|interval| ours.covers(*interval))
            })
        })
    }
}

/// The causally stable contiguous prefix for each required actor.
///
/// A stable frontier is computed from durable progress attestations, not from
/// elapsed time or transport acknowledgements.  Only each attestation's
/// contiguous `through` prefix participates; sparse intervals never advance
/// a collection point.  An actor present in the required set but absent from
/// an attestation contributes zero.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct StableFrontier(BTreeMap<ActorId, u64>);

impl StableFrontier {
    /// Computes the componentwise minimum of required actor progress.
    #[must_use]
    pub fn from_attestations<I>(attestations: I) -> Self
    where
        I: IntoIterator<Item = (ActorId, ActorProgress)>,
    {
        let mut frontier: BTreeMap<ActorId, u64> = BTreeMap::new();
        for (actor, progress) in attestations {
            frontier
                .entry(actor)
                .and_modify(|through| *through = (*through).min(progress.through()))
                .or_insert(progress.through());
        }
        Self(frontier)
    }

    /// Computes a frontier for a required actor set, treating an omitted
    /// attestation as zero progress.
    #[must_use]
    pub fn from_required_attestations<A, I>(required: A, attestations: I) -> Self
    where
        A: IntoIterator<Item = ActorId>,
        I: IntoIterator<Item = (ActorId, ActorProgress)>,
    {
        let required: BTreeSet<ActorId> = required.into_iter().collect();
        let mut observed: BTreeMap<ActorId, u64> = BTreeMap::new();
        for (actor, progress) in attestations {
            if required.contains(&actor) {
                observed
                    .entry(actor)
                    .and_modify(|through| *through = (*through).min(progress.through()))
                    .or_insert(progress.through());
            }
        }
        Self(
            required
                .into_iter()
                .map(|actor| (actor, observed.get(&actor).copied().unwrap_or_default()))
                .collect(),
        )
    }

    /// Creates a frontier with the named actors initially at zero.
    #[must_use]
    pub fn required<I>(actors: I) -> Self
    where
        I: IntoIterator<Item = ActorId>,
    {
        Self(actors.into_iter().map(|actor| (actor, 0)).collect())
    }

    /// Returns the stable contiguous prefix for `actor`, or zero when the
    /// actor is absent from the frontier.
    #[must_use]
    pub fn through(&self, actor: ActorId) -> u64 {
        self.0.get(&actor).copied().unwrap_or_default()
    }

    /// Returns the sorted actor components.
    #[must_use]
    pub fn actors(&self) -> impl ExactSizeIterator<Item = (&ActorId, &u64)> {
        self.0.iter()
    }

    /// Reports whether the frontier proves that a dot is stable.
    #[must_use]
    pub fn contains(&self, dot: Dot) -> bool {
        dot.counter().get() <= self.through(dot.actor())
    }

    /// Reports whether this frontier is at least as advanced as `other`.
    #[must_use]
    pub fn dominates(&self, other: &Self) -> bool {
        other.0.iter().all(|(actor, through)| self.through(*actor) >= *through)
    }
}

/// The lifecycle state of server replication actors.
///
/// Retired actors remain represented by a final counter summary.  Registering
/// another epoch for the same replica is allowed only when it is strictly
/// newer, so an erased actor incarnation can never be reused.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ActorLifecycle {
    latest: BTreeMap<ReplicaId, ReplicaEpoch>,
    active: BTreeSet<ActorId>,
    retired: BTreeMap<ActorId, OriginCounter>,
}

impl ActorLifecycle {
    /// Registers a new active actor incarnation.
    ///
    /// # Errors
    ///
    /// Returns [`ActorLifecycleError::AlreadyActive`] when the exact actor is
    /// already active, or [`ActorLifecycleError::EpochReuse`] when its epoch
    /// does not advance the replica's latest recorded incarnation.
    pub fn register(&mut self, actor: ActorId) -> Result<(), ActorLifecycleError> {
        if self.active.contains(&actor) {
            return Err(ActorLifecycleError::AlreadyActive);
        }
        if self.latest.get(&actor.replica()).is_some_and(|latest| actor.epoch() <= *latest) {
            return Err(ActorLifecycleError::EpochReuse);
        }
        self.latest.insert(actor.replica(), actor.epoch());
        self.active.insert(actor);
        Ok(())
    }

    /// Retires an actor after a stable frontier proves its final counter.
    ///
    /// # Errors
    ///
    /// Returns [`ActorLifecycleError::NotActive`] when the actor is not active,
    /// or [`ActorLifecycleError::FinalCounterNotStable`] when the supplied
    /// frontier does not cover the actor's final counter.
    pub fn retire(
        &mut self,
        actor: ActorId,
        final_counter: OriginCounter,
        proof: &StableFrontier,
    ) -> Result<(), ActorLifecycleError> {
        if !self.active.remove(&actor) {
            return Err(ActorLifecycleError::NotActive);
        }
        if !proof.contains(Dot::new(actor, final_counter)) {
            self.active.insert(actor);
            return Err(ActorLifecycleError::FinalCounterNotStable);
        }
        self.retired.insert(actor, final_counter);
        Ok(())
    }

    /// Returns whether an actor may still originate or be admitted as live.
    #[must_use]
    pub fn is_active(&self, actor: ActorId) -> bool {
        self.active.contains(&actor)
    }

    /// Returns the summarized final counter of a retired actor.
    #[must_use]
    pub fn retired_through(&self, actor: ActorId) -> Option<OriginCounter> {
        self.retired.get(&actor).copied()
    }

    /// Returns whether this lifecycle has a summary that suppresses an old
    /// actor incarnation from being admitted again.
    #[must_use]
    pub fn is_retired(&self, actor: ActorId) -> bool {
        self.retired.contains_key(&actor)
    }
}

/// A membership lifecycle violation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ActorLifecycleError {
    /// The actor is already active.
    AlreadyActive,
    /// The replica attempted to reuse an existing or older epoch.
    EpochReuse,
    /// The actor is not an active member.
    NotActive,
    /// The final counter was not covered by the stable frontier proof.
    FinalCounterNotStable,
}

impl fmt::Display for ActorLifecycleError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::AlreadyActive => formatter.write_str("actor is already active"),
            Self::EpochReuse => formatter.write_str("actor epoch would be reused"),
            Self::NotActive => formatter.write_str("actor is not active"),
            Self::FinalCounterNotStable => formatter.write_str("actor final counter is not stable"),
        }
    }
}

impl std::error::Error for ActorLifecycleError {}

/// One event dot and the events it observed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DottedVersion {
    context: CausalContext,
    dot: Dot,
}

impl DottedVersion {
    /// Creates a dotted version.
    #[must_use]
    pub const fn new(context: CausalContext, dot: Dot) -> Self {
        Self { context, dot }
    }

    /// Returns the causal context.
    #[must_use]
    pub const fn context(&self) -> &CausalContext {
        &self.context
    }

    /// Returns the event dot.
    #[must_use]
    pub const fn dot(&self) -> Dot {
        self.dot
    }

    /// Reports whether this event happened before `other`.
    #[must_use]
    pub fn happens_before(&self, other: &Self) -> bool {
        other.context.contains(self.dot)
    }

    /// Reports whether neither event observed the other.
    #[must_use]
    pub fn concurrent_with(&self, other: &Self) -> bool {
        !self.happens_before(other) && !other.happens_before(self)
    }
}

/// A Hybrid Logical Clock timestamp.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct HlcTimestamp {
    physical_unix_millis: u64,
    logical: u32,
}

impl HlcTimestamp {
    /// Creates a timestamp from injected physical time and a logical value.
    #[must_use]
    pub const fn new(physical_unix_millis: u64, logical: u32) -> Self {
        Self { physical_unix_millis, logical }
    }

    /// Returns the physical component.
    #[must_use]
    pub const fn physical_unix_millis(self) -> u64 {
        self.physical_unix_millis
    }

    /// Returns the logical component.
    #[must_use]
    pub const fn logical(self) -> u32 {
        self.logical
    }
}

/// A deterministic HLC whose caller supplies physical time.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HybridLogicalClock {
    last: HlcTimestamp,
}

impl HybridLogicalClock {
    /// Restores a clock from its durably persisted last stamp.
    #[must_use]
    pub const fn restore(last: HlcTimestamp) -> Self {
        Self { last }
    }

    /// Returns the last issued or observed stamp for persistence.
    #[must_use]
    pub const fn last(self) -> HlcTimestamp {
        self.last
    }

    /// Issues a local stamp using injected physical time.
    ///
    /// # Errors
    ///
    /// Returns [`ClockError::LogicalOverflow`] instead of wrapping logical time.
    pub fn send(&mut self, now_unix_millis: u64) -> Result<HlcTimestamp, ClockError> {
        if now_unix_millis > self.last.physical_unix_millis {
            self.last = HlcTimestamp::new(now_unix_millis, 0);
        } else {
            self.last.logical =
                self.last.logical.checked_add(1).ok_or(ClockError::LogicalOverflow)?;
        }
        Ok(self.last)
    }

    /// Observes a remote stamp after enforcing the configured future-skew bound.
    ///
    /// # Errors
    ///
    /// Returns [`ClockError::FutureSkew`] when the remote physical component is
    /// too far ahead, or [`ClockError::LogicalOverflow`] instead of wrapping.
    pub fn receive(
        &mut self,
        now_unix_millis: u64,
        remote: HlcTimestamp,
        maximum_future_skew_millis: u64,
    ) -> Result<HlcTimestamp, ClockError> {
        let maximum = now_unix_millis.saturating_add(maximum_future_skew_millis);
        if remote.physical_unix_millis > maximum {
            return Err(ClockError::FutureSkew {
                remote_physical: remote.physical_unix_millis,
                maximum_accepted: maximum,
            });
        }
        let local = self.last;
        let physical =
            now_unix_millis.max(local.physical_unix_millis).max(remote.physical_unix_millis);
        let logical =
            if physical == local.physical_unix_millis && physical == remote.physical_unix_millis {
                local.logical.max(remote.logical).checked_add(1)
            } else if physical == local.physical_unix_millis {
                local.logical.checked_add(1)
            } else if physical == remote.physical_unix_millis {
                remote.logical.checked_add(1)
            } else {
                Some(0)
            }
            .ok_or(ClockError::LogicalOverflow)?;
        self.last = HlcTimestamp::new(physical, logical);
        Ok(self.last)
    }
}

/// A bounded HLC validation or issuance failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClockError {
    /// Logical time cannot wrap.
    LogicalOverflow,
    /// The remote physical component exceeds the configured future bound.
    FutureSkew {
        /// The rejected remote physical component.
        remote_physical: u64,
        /// The largest physical component allowed for this observation.
        maximum_accepted: u64,
    },
}

impl fmt::Display for ClockError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::LogicalOverflow => formatter.write_str("HLC logical component exhausted"),
            Self::FutureSkew { .. } => {
                formatter.write_str("HLC physical component exceeds the future-skew bound")
            }
        }
    }
}

impl std::error::Error for ClockError {}

/// Values with a stable native canonical representation.
pub trait CanonicalEncode {
    /// Appends canonical bytes to `output`.
    fn encode_canonical(&self, output: &mut Vec<u8>);

    /// Returns freshly allocated canonical bytes.
    #[must_use]
    fn canonical_bytes(&self) -> Vec<u8> {
        let mut output = Vec::new();
        self.encode_canonical(&mut output);
        output
    }
}

fn encode_u64(mut value: u64, output: &mut Vec<u8>) {
    loop {
        let mut byte = (value & 0x7f) as u8;
        value >>= 7;
        if value != 0 {
            byte |= 0x80;
        }
        output.push(byte);
        if value == 0 {
            break;
        }
    }
}

impl CanonicalEncode for ActorId {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        output.extend_from_slice(self.replica.as_bytes());
        encode_u64(self.epoch.get(), output);
    }
}

impl CanonicalEncode for Dot {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        self.actor.encode_canonical(output);
        encode_u64(self.counter.get(), output);
    }
}

impl CanonicalEncode for CommitId {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        output.push(CODEC_VERSION);
        output.extend_from_slice(self.database.as_bytes());
        encode_u64(self.partition.get(), output);
        self.actor.encode_canonical(output);
        encode_u64(self.counter.get(), output);
    }
}

impl CanonicalEncode for CausalContext {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        output.push(CODEC_VERSION);
        encode_u64(self.0.len() as u64, output);
        for (actor, progress) in &self.0 {
            actor.encode_canonical(output);
            encode_u64(progress.through, output);
            encode_u64(progress.intervals.len() as u64, output);
            for interval in &progress.intervals {
                encode_u64(interval.first, output);
                encode_u64(interval.last, output);
            }
        }
    }
}

/// Computes BLAKE3-256 over a domain-separated canonical value.
///
/// The domain must be a fixed nonempty printable ASCII protocol constant, not
/// caller or tenant input.
///
/// # Errors
///
/// Returns [`HashError::InvalidDomain`] when the domain is empty, longer than
/// 255 bytes, or contains non-printable ASCII.
pub fn canonical_hash<T: CanonicalEncode>(
    domain: &'static str,
    value: &T,
) -> Result<[u8; 32], HashError> {
    let domain_length = u8::try_from(domain.len()).map_err(|_| HashError::InvalidDomain)?;
    if domain.is_empty() || !domain.bytes().all(|byte| byte.is_ascii_graphic()) {
        return Err(HashError::InvalidDomain);
    }
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"TerraBaseV2\0");
    hasher.update(&[CODEC_VERSION]);
    hasher.update(&[domain_length]);
    hasher.update(domain.as_bytes());
    hasher.update(&value.canonical_bytes());
    Ok(*hasher.finalize().as_bytes())
}

/// A canonical hashing request failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HashError {
    /// The domain was empty or not printable ASCII.
    InvalidDomain,
}

impl fmt::Display for HashError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("canonical hash domain must be nonempty printable ASCII")
    }
}

impl std::error::Error for HashError {}

#[cfg(test)]
mod tests {
    use super::*;

    fn actor(byte: u8, epoch: u64) -> ActorId {
        ActorId::new(
            ReplicaId::from_bytes([byte; 16]),
            ReplicaEpoch::new(epoch).expect("nonzero epoch"),
        )
    }

    fn dot(actor: ActorId, counter: u64) -> Dot {
        Dot::new(actor, OriginCounter::new(counter).expect("nonzero counter"))
    }

    #[test]
    fn received_intervals_never_infer_holes_and_close_only_contiguous_prefixes() {
        let actor = actor(1, 1);
        let mut context = CausalContext::new();
        context.insert(dot(actor, 100));
        assert!(!context.contains(dot(actor, 1)));
        assert!(context.contains(dot(actor, 100)));

        context.insert_progress(actor, &{
            let mut progress = ActorProgress::new();
            progress.insert_interval(CounterInterval::new(2, 99).expect("valid interval"));
            progress
        });
        assert!(!context.contains(dot(actor, 1)));
        context.insert(dot(actor, 1));
        let progress = context.actors().next().expect("actor progress").1;
        assert_eq!(progress.through(), 100);
        assert!(progress.intervals().is_empty());
    }

    #[test]
    fn interval_insertion_is_permutation_and_duplication_invariant() {
        let intervals = [(8, 10), (2, 4), (6, 8), (1, 1), (5, 5), (3, 7)];
        let mut forward = ActorProgress::new();
        for (first, last) in intervals {
            forward.insert_interval(CounterInterval::new(first, last).unwrap());
        }
        let mut reverse = ActorProgress::new();
        for (first, last) in intervals.into_iter().rev() {
            let interval = CounterInterval::new(first, last).unwrap();
            reverse.insert_interval(interval);
            reverse.insert_interval(interval);
        }
        assert_eq!(forward, reverse);
        assert_eq!(forward.through(), 10);
    }

    #[test]
    fn stable_frontier_uses_only_durable_contiguous_minima() {
        let a = actor(1, 1);
        let b = actor(2, 1);
        let mut a_progress = ActorProgress::new();
        a_progress.insert_interval(CounterInterval::new(4, 9).unwrap());
        let mut b_progress = ActorProgress::new();
        b_progress.insert_interval(CounterInterval::new(1, 3).unwrap());
        let frontier = StableFrontier::from_required_attestations(
            [a, b, actor(3, 1)],
            [(a, a_progress), (b, b_progress)],
        );
        assert_eq!(frontier.through(a), 0, "a has a gap before its sparse interval");
        assert_eq!(frontier.through(b), 3);
        assert_eq!(frontier.through(actor(3, 1)), 0, "missing seats block collection");
        assert!(!frontier.contains(dot(a, 4)));
        assert!(frontier.contains(dot(b, 3)));
    }

    #[test]
    fn actor_retirement_requires_stable_final_counter_and_never_reuses_epoch() {
        let replica = ReplicaId::from_bytes([7; 16]);
        let actor_one = ActorId::new(replica, ReplicaEpoch::new(1).unwrap());
        let actor_two = ActorId::new(replica, ReplicaEpoch::new(2).unwrap());
        let final_counter = OriginCounter::new(4).unwrap();
        let mut lifecycle = ActorLifecycle::default();
        lifecycle.register(actor_one).unwrap();
        let empty = StableFrontier::required([actor_one]);
        assert_eq!(
            lifecycle.retire(actor_one, final_counter, &empty),
            Err(ActorLifecycleError::FinalCounterNotStable)
        );
        let proof = StableFrontier::from_attestations([(actor_one, {
            let mut progress = ActorProgress::new();
            progress.insert_interval(CounterInterval::new(1, 4).unwrap());
            progress
        })]);
        lifecycle.retire(actor_one, final_counter, &proof).unwrap();
        assert_eq!(lifecycle.retired_through(actor_one), Some(final_counter));
        assert_eq!(lifecycle.register(actor_one), Err(ActorLifecycleError::EpochReuse));
        lifecycle.register(actor_two).unwrap();
    }

    #[test]
    fn causal_order_and_concurrency_do_not_depend_on_arrival_or_hlc() {
        let a = actor(1, 1);
        let b = actor(2, 1);
        let first = DottedVersion::new(CausalContext::new(), dot(a, 1));
        let concurrent = DottedVersion::new(CausalContext::new(), dot(b, 1));
        assert!(first.concurrent_with(&concurrent));

        let mut observed = CausalContext::new();
        observed.insert(first.dot());
        let later = DottedVersion::new(observed, dot(b, 2));
        assert!(first.happens_before(&later));
        assert!(!first.concurrent_with(&later));
    }

    #[test]
    fn hlc_uses_standard_max_rule_and_refuses_future_skew_and_wrap() {
        let mut clock = HybridLogicalClock::restore(HlcTimestamp::new(100, 2));
        assert_eq!(clock.send(99).unwrap(), HlcTimestamp::new(100, 3));
        assert_eq!(
            clock.receive(101, HlcTimestamp::new(101, 8), 10).unwrap(),
            HlcTimestamp::new(101, 9)
        );
        assert_eq!(
            clock.receive(101, HlcTimestamp::new(112, 0), 10),
            Err(ClockError::FutureSkew { remote_physical: 112, maximum_accepted: 111 })
        );
        let mut exhausted = HybridLogicalClock::restore(HlcTimestamp::new(5, u32::MAX));
        assert_eq!(exhausted.send(5), Err(ClockError::LogicalOverflow));
    }

    #[test]
    fn canonical_bytes_sort_actors_and_use_minimal_leb128() {
        let high = actor(9, 1);
        let low = actor(1, 1);
        let mut left = CausalContext::new();
        left.insert(dot(high, 128));
        left.insert(dot(low, 1));
        let mut right = CausalContext::new();
        right.insert(dot(low, 1));
        right.insert(dot(high, 128));
        assert_eq!(left.canonical_bytes(), right.canonical_bytes());
        assert_eq!(
            canonical_hash("causal-context", &left).unwrap(),
            canonical_hash("causal-context", &right).unwrap()
        );
    }

    #[test]
    fn commit_identity_canonical_bytes_change_for_every_component() {
        let base = CommitId::new(
            DatabaseId::from_bytes([1; 16]),
            PartitionId::new(7),
            actor(2, 3),
            OriginCounter::new(4).unwrap(),
        );
        let changed = CommitId::new(
            DatabaseId::from_bytes([1; 16]),
            PartitionId::new(7),
            actor(2, 3),
            OriginCounter::new(5).unwrap(),
        );
        assert_eq!(
            base.canonical_bytes(),
            vec![
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 7, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
                2, 2, 2, 2, 2, 2, 3, 4,
            ]
        );
        assert_ne!(base.canonical_bytes(), changed.canonical_bytes());
        assert_ne!(
            canonical_hash("commit", &base).unwrap(),
            canonical_hash("commit", &changed).unwrap()
        );
        assert_eq!(
            canonical_hash("commit", &base).unwrap(),
            [
                0x0e, 0x0c, 0xcb, 0x9a, 0x62, 0x77, 0x06, 0x76, 0x65, 0xfb, 0xb3, 0xff, 0xac, 0xec,
                0x16, 0xe8, 0x93, 0x11, 0x25, 0x73, 0x19, 0xdf, 0x05, 0x09, 0x24, 0x9d, 0xbe, 0x50,
                0xe5, 0x08, 0x3b, 0xfd,
            ]
        );
        assert_eq!(canonical_hash("", &base), Err(HashError::InvalidDomain));
    }
}
