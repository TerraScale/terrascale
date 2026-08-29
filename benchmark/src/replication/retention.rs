//! The tombstone retention watermark (GAP-R02, PB-5023).
//!
//! This module owns how long deletion evidence survives: the retention
//! floor, the retained evidence set, the consumer floors that bound the
//! floor, and the history bound a storage compaction declares to the
//! watermark. The decision record is
//! `docs/replication/tombstone-retention-watermark.md`; this documentation
//! states the rules, the record states why they are these rules.
//!
//! The invariant is one line:
//!
//! ```text
//! retention floor <= every consumer floor
//! ```
//!
//! Everything else follows from it. An offline member, an incremental
//! backup chain, and a migration each register a consumer floor — the
//! durable progress in this engine's sequence space below which they still
//! need deletion evidence — and the floor may not rise past the slowest of
//! them. The history bound composes with the engine's own
//! `servable_history_start` in one direction only
//! (`max(engine bound, declared bound)`), and a declaration that gives up
//! history must hand the window's deletion evidence over and assert it is
//! durably held before the bound may move. The declared bound also may not
//! exceed the journal's durable head (decision D2.4): the window must
//! fully exist, or the range between the journal's end and the bound would
//! be given up as if it were empty.
//!
//! Deletion evidence is content-free by construction: identity, version,
//! sequence, operation, stamp, and durability. A tombstone that kept the
//! document body would keep deleted data alive.
//!
//! # Limits
//!
//! The watermark state is process-local. Its inputs are durable — member
//! cursors are journal-derived and chain anchors are sealed manifests — and
//! the retained evidence is the journal's while the journal covers it, but
//! a durable retained-evidence store does not exist yet; until the storage
//! seam's compaction path (GAP-S1) provides one, the durability assertion
//! of [`TombstoneRetention::declare_history_bound`] is honest only in the
//! pre-compaction state, which is why the declaration fails closed without
//! it.

use std::collections::{BTreeMap, VecDeque};
use std::sync::{Arc, Mutex};

use super::transport::StampedCommit;
use super::{MemberId, ReplicationClockStamp};
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{
    CollectionName, DocumentId, LocalDurability, OperationId, StorageSequence, TenantId,
};

/// A shared handle to one tombstone retention watermark.
///
/// The watermark is engine-level state with more than one holder: the
/// member that owns it drives its transitions, while a backup run carries
/// the same watermark on its plan to read retained evidence and record its
/// chain's anchor. The handle is the one watermark, not a copy: every
/// transition through any handle is visible through all of them.
///
/// Locking is unpoisoned by recovery — a panicked holder's state is still
/// the watermark's state, and the next holder continues from it rather
/// than losing retention facts to a poison flag. The engine is
/// synchronous, so the lock is never held across a caller-visible await
/// point; it exists for shared ownership, not parallelism.
#[derive(Clone, Debug, Default)]
pub struct SharedTombstoneRetention {
    inner: Arc<Mutex<TombstoneRetention>>,
}

impl SharedTombstoneRetention {
    /// Creates one shared watermark that retains everything.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Shares an already-configured watermark.
    #[must_use]
    pub fn from_retention(retention: TombstoneRetention) -> Self {
        Self { inner: Arc::new(Mutex::new(retention)) }
    }

    /// Runs `body` with the watermark, returning its result.
    pub fn with_lock<R>(&self, body: impl FnOnce(&mut TombstoneRetention) -> R) -> R {
        let mut guard = self.inner.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        body(&mut guard)
    }

    /// Returns a point-in-time snapshot of the watermark.
    ///
    /// The snapshot is a copy: transitions through the handle after it are
    /// not reflected in it. Read facts through [`Self::with_lock`] when
    /// the live state matters.
    #[must_use]
    pub fn snapshot(&self) -> TombstoneRetention {
        self.with_lock(|retention| retention.clone())
    }
}

/// The bounded number of refusals the watermark retains for observation.
///
/// Refusals are operator-visible facts, not errors the caller fixes: the
/// same anti-flood bound as the member's other observation logs applies,
/// evicting the oldest entry.
pub const MAX_RETENTION_REFUSALS: usize = 64;

/// One content-free retained deletion fact.
///
/// The fact carries everything a consumer needs to replay the delete
/// idempotently — the exact key, the version that existed at it, the
/// journal sequence and operation identity that published it, the D-3
/// stamp it was minted under, and the local durability the commit achieved
/// — and nothing else. Document content never crosses into evidence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DeletionEvidence {
    tenant: TenantId,
    collection: Option<CollectionName>,
    id: DocumentId,
    version_at_delete: u64,
    sequence: StorageSequence,
    operation: OperationId,
    stamp: ReplicationClockStamp,
    durability: LocalDurability,
}

impl DeletionEvidence {
    /// Creates one retained fact from the publication facts.
    #[allow(
        clippy::too_many_arguments,
        reason = "one retained fact of a delete: every field is a distinct publication fact"
    )]
    #[must_use]
    pub fn new(
        tenant: TenantId,
        collection: Option<CollectionName>,
        id: DocumentId,
        version_at_delete: u64,
        sequence: StorageSequence,
        operation: OperationId,
        stamp: ReplicationClockStamp,
        durability: LocalDurability,
    ) -> Self {
        Self { tenant, collection, id, version_at_delete, sequence, operation, stamp, durability }
    }

    /// Extracts the deletion evidence of one journaled commit.
    ///
    /// A commit with no delete yields nothing. The stamp is the one the
    /// writing member minted for the operation, so a consumer that later
    /// applies the evidence records the same D-3 stamp the original
    /// publication carried.
    #[must_use]
    fn of_commit(
        tenant: &TenantId,
        commit: &PublishedCommit,
        stamp: ReplicationClockStamp,
    ) -> Vec<Self> {
        commit
            .writes()
            .iter()
            .filter_map(|write| match write {
                PublishedWrite::Delete { collection, id, version_at_delete } => Some(Self {
                    tenant: tenant.clone(),
                    collection: collection.clone(),
                    id: id.clone(),
                    version_at_delete: *version_at_delete,
                    sequence: commit.sequence(),
                    operation: commit.operation_id().clone(),
                    stamp,
                    durability: commit.durability(),
                }),
                PublishedWrite::Put { .. } => None,
            })
            .collect()
    }

    /// Returns the owning tenant.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the deleted key's collection, or `None` when the delete
    /// named a key nothing existed at.
    #[must_use]
    pub fn collection(&self) -> Option<&CollectionName> {
        self.collection.as_ref()
    }

    /// Returns the deleted document's identifier.
    #[must_use]
    pub fn id(&self) -> &DocumentId {
        &self.id
    }

    /// Returns the version that existed at the deleted key.
    #[must_use]
    #[allow(clippy::unused_self, reason = "accessor symmetry with the other facts")]
    pub const fn version_at_delete(&self) -> u64 {
        self.version_at_delete
    }

    /// Returns the journal sequence that published the delete.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the operation identity that published the delete.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }

    /// Returns the D-3 clock stamp the delete was minted under.
    #[must_use]
    pub const fn stamp(&self) -> ReplicationClockStamp {
        self.stamp
    }

    /// Returns the local durability the publishing commit achieved.
    ///
    /// A below-`Synced` fact may describe bytes the source could have
    /// lost; a consumer discloses it rather than presenting the delete as
    /// fully durable, exactly as an archived journal record does.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }
}

/// One consumer whose floor the watermark must respect.
///
/// The classes name the three spans of the decision record: replication
/// catch-up, backup chains, and migration. Each carries the identity its
/// floor is keyed by, and a member's floor is per tenant because a member
/// that has never requested a tenant needs that tenant's whole evidence.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum RetentionConsumer {
    /// A member's durable catch-up floor for one tenant, expressed in the
    /// serving engine's sequence space.
    Member {
        /// The consuming member.
        member: MemberId,
        /// The tenant whose history it consumes.
        tenant: TenantId,
    },
    /// An incremental backup chain anchored at a sealed recovery point.
    BackupChain {
        /// The base recovery point's identity.
        identity: String,
    },
    /// A migration reading deletion evidence above its floor.
    Migration {
        /// The migration's stable name.
        name: String,
        /// The tenant whose evidence it reads.
        tenant: TenantId,
    },
}

/// The outcome of one consumer-floor observation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ConsumerObservation {
    /// The consumer's floor advanced (or was recorded for the first
    /// time).
    Recorded,
    /// The observed floor was below the consumer's recorded floor, so it
    /// was refused: a consumer's progress never goes backwards, and the
    /// recorded floor stands.
    Regression,
}

/// The typed refusal of one watermark transition.
///
/// Every refusal is fail-closed: the floor or bound does not move, and the
/// refusal is recorded in the bounded observation log.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RetentionRefusal {
    /// A consumer still needs deletion evidence below the candidate floor.
    ConsumerBelowFloor {
        /// The binding consumer.
        consumer: RetentionConsumer,
        /// The consumer's recorded floor; `None` means it has consumed
        /// nothing, so it needs all of the evidence.
        floor: Option<StorageSequence>,
        /// The floor that was refused.
        candidate: StorageSequence,
    },
    /// The candidate floor is below the current floor: the watermark is
    /// monotone.
    FloorRegression {
        /// The current floor.
        current: StorageSequence,
        /// The refused candidate.
        candidate: StorageSequence,
    },
    /// The candidate floor exceeds the configured cap, so evidence at or
    /// above the cap is retained unconditionally.
    FloorExceededCap {
        /// The configured cap.
        cap: StorageSequence,
        /// The refused candidate.
        candidate: StorageSequence,
    },
    /// The candidate history bound is below the declared one.
    BoundRegression {
        /// The currently declared bound.
        current: StorageSequence,
        /// The refused candidate.
        candidate: StorageSequence,
    },
    /// History may not be given up while the retained evidence covering
    /// the window is not durably held (decision record D2.3).
    EvidenceNotDurable {
        /// The bound that was refused.
        bound: StorageSequence,
    },
    /// The candidate bound exceeds the journal's durable head, so the
    /// window below it does not fully exist (decision record D2.4). The
    /// range between the journal's end and the candidate would be given up
    /// as if it were empty — every publication landing there afterwards
    /// would be servable by neither the journal nor the retained evidence.
    BoundBeyondWindow {
        /// The journal's durable head at declaration time.
        head: StorageSequence,
        /// The refused candidate.
        candidate: StorageSequence,
    },
}

/// The outcome of one accepted floor advance.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RetentionAdvance {
    floor: StorageSequence,
    discarded: usize,
}

/// The outcome of one retained-evidence hold.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RetentionHold {
    held: usize,
    below_floor_skipped: usize,
}

impl RetentionHold {
    /// Returns how many facts landed in the retained set — a newer fact of
    /// the same key lands by replacing the one it subsumes, so this counts
    /// landings, while one key keeps only its newest fact.
    #[must_use]
    pub const fn held(self) -> usize {
        self.held
    }

    /// Returns how many facts fell below the retention floor and were
    /// therefore not held — a disclosed count, never a silent drop.
    #[must_use]
    pub const fn below_floor_skipped(self) -> usize {
        self.below_floor_skipped
    }
}

impl RetentionAdvance {
    /// Returns the floor after the advance.
    #[must_use]
    pub const fn floor(self) -> StorageSequence {
        self.floor
    }

    /// Returns how many retained facts the advance discarded (all below
    /// the new floor).
    #[must_use]
    #[allow(clippy::unused_self, reason = "accessor symmetry with the floor")]
    pub const fn discarded(self) -> usize {
        self.discarded
    }
}

/// The outcome of one accepted history-bound declaration.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RetentionCompaction {
    bound: StorageSequence,
    held: usize,
    below_floor_discarded: usize,
    consumers_below_bound: Vec<RetentionConsumer>,
}

impl RetentionCompaction {
    /// Returns the declared bound.
    #[must_use]
    pub const fn bound(&self) -> StorageSequence {
        self.bound
    }

    /// Returns how many window deletion facts were retained.
    #[must_use]
    #[allow(clippy::unused_self, reason = "accessor symmetry with the bound")]
    pub const fn held(&self) -> usize {
        self.held
    }

    /// Returns how many window deletion facts fell below the retention
    /// floor and were therefore not retained — a disclosed count, never a
    /// silent drop.
    #[must_use]
    #[allow(clippy::unused_self, reason = "accessor symmetry with the bound")]
    pub const fn below_floor_discarded(&self) -> usize {
        self.below_floor_discarded
    }

    /// Returns the consumers whose recorded floor is below the declared
    /// bound: they will need deletion evidence or a full state transfer,
    /// and the disclosure names them.
    #[must_use]
    pub fn consumers_below_bound(&self) -> &[RetentionConsumer] {
        &self.consumers_below_bound
    }
}

/// The tombstone retention watermark.
///
/// Owns the retention floor, the retained evidence set, the consumer
/// floors, and the declared history bound. Every transition is typed and
/// fail-closed; nothing here reads a clock, a path, or the journal — the
/// caller hands the watermark the facts it decided on.
#[derive(Clone, Debug)]
pub struct TombstoneRetention {
    floor: StorageSequence,
    floor_cap: Option<StorageSequence>,
    bound: Option<StorageSequence>,
    non_deletion_through: BTreeMap<TenantId, Option<StorageSequence>>,
    consumers: BTreeMap<RetentionConsumer, Option<StorageSequence>>,
    // Collection is part of the logical document key.  Keeping it in this
    // index is essential: the same document id may legitimately exist in
    // more than one collection, and one delete must never hide the other's
    // evidence.
    retained: BTreeMap<(TenantId, Option<CollectionName>, DocumentId), DeletionEvidence>,
    refusals: VecDeque<RetentionRefusal>,
}

impl Default for TombstoneRetention {
    /// Retains everything: floor zero, no cap, no declared bound.
    fn default() -> Self {
        Self {
            floor: StorageSequence::default(),
            floor_cap: None,
            bound: None,
            non_deletion_through: BTreeMap::new(),
            consumers: BTreeMap::new(),
            retained: BTreeMap::new(),
            refusals: VecDeque::new(),
        }
    }
}

impl TombstoneRetention {
    /// Creates one watermark with an optional floor cap.
    ///
    /// The cap is an absolute sequence: evidence at or above it is
    /// retained unconditionally, so the floor may never rise past it. It
    /// is a lower bound on retention, never an upper one.
    #[must_use]
    pub fn new(floor_cap: Option<StorageSequence>) -> Self {
        Self { floor_cap, ..Self::default() }
    }

    /// Returns the retention floor: deletion evidence below it may be
    /// discarded, evidence at or above it is retained.
    #[must_use]
    pub const fn retention_floor(&self) -> StorageSequence {
        self.floor
    }

    /// Returns the configured floor cap, when one was set.
    #[must_use]
    pub const fn floor_cap(&self) -> Option<StorageSequence> {
        self.floor_cap
    }

    /// Returns the declared history bound.
    ///
    /// The effective bound a consumer is served against is
    /// `max(engine.servable_history_start(), this)`.
    #[must_use]
    pub const fn history_bound(&self) -> Option<StorageSequence> {
        self.bound
    }

    /// Returns the highest non-deletion sequence a tenant has below the
    /// declared bound.
    ///
    /// This is the fact that decides whether a consumer below the bound
    /// can be served completely by deletion evidence alone: it can be when
    /// this value is at or below the consumer's floor, because then no
    /// put was missed.
    #[must_use]
    pub fn non_deletion_through(&self, tenant: &TenantId) -> Option<StorageSequence> {
        self.non_deletion_through.get(tenant).copied().flatten()
    }

    /// Records one consumer's floor.
    ///
    /// A consumer's floor never goes backwards: a regressing observation
    /// is refused and the recorded floor stands, because a consumer that
    /// has durably consumed through a sequence still needs nothing below
    /// it even if it re-requests from an earlier position.
    pub fn observe_consumer(
        &mut self,
        consumer: RetentionConsumer,
        floor: Option<StorageSequence>,
    ) -> ConsumerObservation {
        let Some(recorded) = self.consumers.get(&consumer).copied().flatten() else {
            self.consumers.insert(consumer, floor);
            return ConsumerObservation::Recorded;
        };
        let observed = floor.unwrap_or_default();
        if observed.get() < recorded.get() {
            return ConsumerObservation::Regression;
        }
        self.consumers.insert(consumer, Some(observed));
        ConsumerObservation::Recorded
    }

    /// Drops one consumer's floor, returning whether it was known.
    ///
    /// Dropping a consumer releases its constraint on the floor — a
    /// decommissioned member or a pruned chain stops holding the watermark
    /// — and never lowers any other consumer's need.
    pub fn forget_consumer(&mut self, consumer: &RetentionConsumer) -> bool {
        self.consumers.remove(consumer).is_some()
    }

    /// Returns every recorded consumer and its floor.
    #[must_use]
    pub fn consumers(&self) -> Vec<(RetentionConsumer, Option<StorageSequence>)> {
        self.consumer_floors().collect()
    }

    /// Returns the consumer with the lowest floor, if any consumer is
    /// recorded.
    #[must_use]
    pub fn slowest_consumer(&self) -> Option<(RetentionConsumer, Option<StorageSequence>)> {
        self.consumers
            .iter()
            .min_by_key(|(_, floor)| floor.map_or(0, StorageSequence::get))
            .map(|(consumer, floor)| (consumer.clone(), *floor))
    }

    /// Retains deletion evidence, disclosing both counts.
    ///
    /// Facts below the retention floor are not held — the floor is the
    /// discard boundary — and the count dropped below it is disclosed,
    /// exactly as [`Self::advance_floor`] and
    /// [`Self::declare_history_bound`] disclose theirs, never silently
    /// dropped. One key keeps only its newest fact, because a delete that a
    /// later delete of the same key subsumes carries no additional
    /// information for a consumer that must end with the key absent.
    pub fn hold(&mut self, evidence: &[DeletionEvidence]) -> RetentionHold {
        let mut held = 0;
        let mut below_floor_skipped = 0;
        for fact in evidence {
            if fact.sequence.get() < self.floor.get() {
                below_floor_skipped += 1;
                continue;
            }
            let key = (fact.tenant.clone(), fact.collection.clone(), fact.id.clone());
            match self.retained.get(&key) {
                Some(existing) if existing.sequence.get() >= fact.sequence.get() => {}
                _ => {
                    self.retained.insert(key, fact.clone());
                    held += 1;
                }
            }
        }
        RetentionHold { held, below_floor_skipped }
    }

    /// Returns how many facts the retained set holds.
    #[must_use]
    pub fn held_evidence_count(&self) -> usize {
        self.retained.len()
    }

    /// Returns one tenant's retained evidence above `after`, in sequence
    /// order.
    #[must_use]
    pub fn deletion_evidence_above(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
    ) -> Vec<DeletionEvidence> {
        let mut facts = self
            .retained
            .values()
            .filter(|fact| fact.tenant == *tenant && fact.sequence.get() > after.get())
            .cloned()
            .collect::<Vec<_>>();
        // The retained set is keyed by (tenant, collection, key) so one
        // logical key keeps only its newest fact; the serving order is the
        // publication order, and the stable sort keeps same-sequence facts in
        // key order.
        facts.sort_by_key(|fact| fact.sequence.get());
        facts
    }

    /// Advances the retention floor.
    ///
    /// # Errors
    ///
    /// Fails closed with [`RetentionRefusal::FloorRegression`] for a
    /// candidate below the current floor,
    /// [`RetentionRefusal::ConsumerBelowFloor`] when any recorded consumer
    /// still needs evidence below the candidate, and
    /// [`RetentionRefusal::FloorExceededCap`] when a configured cap would
    /// be passed. A refused candidate does not move the floor.
    pub fn advance_floor(
        &mut self,
        candidate: StorageSequence,
    ) -> Result<RetentionAdvance, RetentionRefusal> {
        if candidate.get() < self.floor.get() {
            let refusal = RetentionRefusal::FloorRegression { current: self.floor, candidate };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        if let Some(cap) = self.floor_cap
            && candidate.get() > cap.get()
        {
            let refusal = RetentionRefusal::FloorExceededCap { cap, candidate };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        // The slowest consumer decides: every recorded floor must be at or
        // above the candidate, and a consumer that has consumed nothing
        // needs all of the evidence.
        for (consumer, floor) in &self.consumers {
            if floor.unwrap_or_default().get() < candidate.get() {
                let refusal = RetentionRefusal::ConsumerBelowFloor {
                    consumer: consumer.clone(),
                    floor: *floor,
                    candidate,
                };
                self.record_refusal(refusal.clone());
                return Err(refusal);
            }
        }
        let before = self.retained.len();
        self.retained.retain(|_, fact| fact.sequence.get() >= candidate.get());
        let discarded = before - self.retained.len();
        self.floor = candidate;
        Ok(RetentionAdvance { floor: candidate, discarded })
    }

    /// Declares that publication history below `bound` has been given up,
    /// restoring a bound the durable retained-evidence store backs (GAP-S1
    /// D5).
    ///
    /// This is the store-backed form the storage compaction seam
    /// (GAP-S1) drives when it re-derives a watermark after a restart: the
    /// window's facts were extracted and made durable before the journal
    /// records were dropped, so there is no journal window to hand over —
    /// the facts arrive through [`TombstoneRetention::hold`] and the
    /// non-deletion facts arrive from the store's bound record, because
    /// after compaction the journal can no longer recompute them. The
    /// gates are exactly the windowed form's: the bound may not exceed the
    /// journal's durable head (D2.4), may not regress, and the caller must
    /// assert durability (D2.3).
    ///
    /// # Errors
    ///
    /// Fails closed exactly as the windowed form does, with
    /// [`RetentionRefusal::BoundBeyondWindow`],
    /// [`RetentionRefusal::BoundRegression`], and
    /// [`RetentionRefusal::EvidenceNotDurable`]. A refused declaration does
    /// not move the bound.
    pub fn declare_history_bound_retained(
        &mut self,
        bound: StorageSequence,
        journal_head: StorageSequence,
        non_deletion: &[(TenantId, Option<StorageSequence>)],
        durably_held: bool,
    ) -> Result<RetentionCompaction, RetentionRefusal> {
        if bound.get() > journal_head.get() {
            let refusal =
                RetentionRefusal::BoundBeyondWindow { head: journal_head, candidate: bound };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        if let Some(current) = self.bound
            && bound.get() < current.get()
        {
            let refusal = RetentionRefusal::BoundRegression { current, candidate: bound };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        if !durably_held {
            let refusal = RetentionRefusal::EvidenceNotDurable { bound };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        let mut restored = self.non_deletion_through.clone();
        for (tenant, through) in non_deletion {
            let entry = restored.entry(tenant.clone()).or_default();
            *entry = match (*entry, *through) {
                (Some(known), Some(seen)) => Some(known.max(seen)),
                (Some(known), None) => Some(known),
                (None, seen) => seen,
            };
        }
        self.non_deletion_through = restored;
        self.bound = Some(bound);
        let consumers_below_bound = self
            .consumers
            .iter()
            .filter(|(_, floor)| floor.is_none_or(|value| value.get() < bound.get()))
            .map(|(consumer, _)| consumer.clone())
            .collect::<Vec<_>>();
        Ok(RetentionCompaction { bound, held: 0, below_floor_discarded: 0, consumers_below_bound })
    }

    /// Declares that publication history below `bound` has been given up,
    /// with no window to extract from.
    ///
    /// This form asserts the discarded window contributes no deletion
    /// evidence and no non-deletion publication the watermark must record —
    /// the honest declaration for an engine whose history below the bound
    /// is empty. A windowed extraction uses
    /// `TombstoneRetention::declare_history_bound_from_commits`, which is
    /// crate-private because it takes the journal's commit records.
    ///
    /// # Errors
    ///
    /// Fails closed exactly as the windowed form does.
    pub fn declare_history_bound(
        &mut self,
        bound: StorageSequence,
        journal_head: StorageSequence,
        durably_held: bool,
    ) -> Result<RetentionCompaction, RetentionRefusal> {
        self.declare_history_bound_from_commits(bound, journal_head, &[], durably_held)
    }

    /// Declares that publication history below `bound` has been given up,
    /// extracting the window's facts from the journaled commits the caller
    /// hands over.
    ///
    /// The declaring caller hands the window's journaled commits so the
    /// watermark extracts the deletion evidence itself (nothing below the
    /// retention floor is retained, and the discarded count is disclosed)
    /// and asserts that the retained set is durably held. The declaration
    /// also records, per tenant, the highest non-deletion sequence below
    /// the bound — the fact that decides whether a consumer below the
    /// bound can be served completely by evidence alone.
    ///
    /// `journal_head` is the engine's durable journal head at declaration
    /// time: the watermark never reads the journal, so the caller hands it
    /// this one fact. Decision D2.4 — a declared bound may not exceed the
    /// journal's durable head, because the window must fully exist.
    ///
    /// # Errors
    ///
    /// Fails closed with [`RetentionRefusal::BoundBeyondWindow`] for a
    /// candidate past the journal's durable head,
    /// [`RetentionRefusal::BoundRegression`] for a candidate below the
    /// declared bound, and [`RetentionRefusal::EvidenceNotDurable`] when
    /// the retained set is not asserted durable. A refused declaration does
    /// not move the bound.
    pub(crate) fn declare_history_bound_from_commits(
        &mut self,
        bound: StorageSequence,
        journal_head: StorageSequence,
        window: &[StampedCommit],
        durably_held: bool,
    ) -> Result<RetentionCompaction, RetentionRefusal> {
        if bound.get() > journal_head.get() {
            let refusal =
                RetentionRefusal::BoundBeyondWindow { head: journal_head, candidate: bound };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        if let Some(current) = self.bound
            && bound.get() < current.get()
        {
            let refusal = RetentionRefusal::BoundRegression { current, candidate: bound };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        if !durably_held {
            let refusal = RetentionRefusal::EvidenceNotDurable { bound };
            self.record_refusal(refusal.clone());
            return Err(refusal);
        }
        let mut held = 0;
        let mut below_floor_discarded = 0;
        let mut non_deletion: BTreeMap<TenantId, Option<StorageSequence>> =
            self.non_deletion_through.clone();
        for stamped in window {
            let commit = &stamped.commit;
            // The window is the half-open range below the bound: a commit
            // at or above the bound is still servable from the journal, so
            // it is not the declarer's to hand over.
            if commit.sequence().get() >= bound.get() {
                continue;
            }
            let tenant = commit.tenant().clone();
            let deletes = DeletionEvidence::of_commit(&tenant, commit, stamped.stamp);
            // A commit that published anything other than a delete is the
            // fact a below-bound consumer cannot be served by evidence
            // alone: record its sequence as the tenant's non-deletion
            // watermark below the bound.
            if commit.writes().iter().any(|write| matches!(write, PublishedWrite::Put { .. })) {
                let entry = non_deletion.entry(tenant).or_default();
                *entry = (*entry).max(Some(commit.sequence()));
            }
            for fact in deletes {
                if fact.sequence.get() < self.floor.get() {
                    below_floor_discarded += 1;
                    continue;
                }
                let key = (fact.tenant.clone(), fact.collection.clone(), fact.id.clone());
                match self.retained.get(&key) {
                    Some(existing) if existing.sequence.get() >= fact.sequence.get() => {}
                    _ => {
                        self.retained.insert(key, fact);
                        held += 1;
                    }
                }
            }
        }
        self.non_deletion_through = non_deletion;
        self.bound = Some(bound);
        let consumers_below_bound = self
            .consumers
            .iter()
            .filter(|(_, floor)| floor.is_none_or(|value| value.get() < bound.get()))
            .map(|(consumer, _)| consumer.clone())
            .collect::<Vec<_>>();
        Ok(RetentionCompaction { bound, held, below_floor_discarded, consumers_below_bound })
    }

    /// Returns the bounded refusal log, oldest first.
    #[must_use]
    pub fn refusals(&self) -> Vec<&RetentionRefusal> {
        self.refusals.iter().collect()
    }

    /// Records one refusal in the bounded observation log.
    fn record_refusal(&mut self, refusal: RetentionRefusal) {
        if self.refusals.len() == MAX_RETENTION_REFUSALS {
            self.refusals.pop_front();
        }
        self.refusals.push_back(refusal);
    }

    /// Records one refusal without attempting a transition.
    ///
    /// For a caller that enforces a watermark rule at its own seam, with
    /// the engine facts the watermark cannot read — the member's D2.4
    /// journal-head gate before window extraction, or a backup run's gate
    /// on a bound its source cannot cover: the refusal those seams return
    /// owes the same bounded-log observability a transition refusal has.
    pub(crate) fn observe_refusal(&mut self, refusal: RetentionRefusal) {
        self.record_refusal(refusal);
    }

    /// Iterates the recorded consumers and their floors.
    fn consumer_floors(
        &self,
    ) -> impl Iterator<Item = (RetentionConsumer, Option<StorageSequence>)> {
        self.consumers.iter().map(|(consumer, floor)| (consumer.clone(), *floor))
    }
}
