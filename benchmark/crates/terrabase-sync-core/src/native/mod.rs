//! Transport-independent state for the `terrascale.sync.v1` result view.
//!
//! This module deliberately has no transport, storage, query, or replication
//! dependency.  A view is an ordered, authorized projection; its revisions
//! and checkpoints are delivery facts, not database causal positions.  The
//! small model is also useful as a reference harness for other protocol
//! implementations.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::fmt;

/// The maximum number of bytes in a caller idempotency identity.
pub const MAX_IDEMPOTENCY_BYTES: usize = 256;
/// The maximum number of items in a result snapshot.
pub const DEFAULT_MAX_ITEMS: usize = 10_000;
/// The maximum number of bytes in one projected item value.
pub const DEFAULT_MAX_VALUE_BYTES: usize = 64 * 1024;
/// Maximum number of in-flight optimistic mutation identities per view.
pub const MAX_PENDING_MUTATIONS: usize = 1024;

/// An opaque authenticated resume checkpoint.
///
/// No fields, decoder, or merge operation are exposed.  Equality is the only
/// comparison a client needs; a server creates a replacement checkpoint when
/// it publishes a snapshot or result revision.
#[derive(Clone, PartialEq, Eq, Hash)]
pub struct Checkpoint {
    token: Vec<u8>,
    view_generation: u64,
    revision: u64,
    retention_generation: u64,
    expires_at: u64,
    integrity: u64,
}

impl fmt::Debug for Checkpoint {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Checkpoint").field("token", &"<opaque>").finish()
    }
}

impl Checkpoint {
    fn issue(
        view_generation: u64,
        revision: u64,
        digest: ResultDigest,
        retention_generation: u64,
        expires_at: u64,
    ) -> Self {
        let mut token = Vec::with_capacity(8 * 5 + 32);
        token.extend_from_slice(&view_generation.to_le_bytes());
        token.extend_from_slice(&revision.to_le_bytes());
        token.extend_from_slice(&retention_generation.to_le_bytes());
        token.extend_from_slice(&expires_at.to_le_bytes());
        token.extend_from_slice(&digest.as_bytes());
        let integrity = checksum(&token);
        token.extend_from_slice(&integrity.to_le_bytes());
        Self { token, view_generation, revision, retention_generation, expires_at, integrity }
    }

    /// Returns whether this checkpoint can be used at `now` and the current
    /// retained-history generation.
    #[must_use]
    pub fn is_valid_at(&self, now: u64, retention_generation: u64) -> bool {
        now <= self.expires_at
            && retention_generation == self.retention_generation
            && self.integrity == checksum(&self.token[..self.token.len().saturating_sub(8)])
    }
}

/// A stable digest of the complete ordered result.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct ResultDigest([u8; 32]);

impl ResultDigest {
    /// The all-empty digest for an empty result.
    #[must_use]
    pub const fn empty() -> Self {
        Self([0; 32])
    }

    /// Returns the digest bytes for protocol framing and independent tests.
    #[must_use]
    pub const fn as_bytes(self) -> [u8; 32] {
        self.0
    }
}

/// A bounded projected value.  Keys are canonicalized by their ordered map.
pub type ProjectedValue = BTreeMap<String, Vec<u8>>;

/// One item in an authorized ordered result.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ResultItem {
    /// Stable identity including any incarnation identity chosen by the view.
    pub identity: String,
    /// The bounded projected fields.
    pub value: ProjectedValue,
}

impl ResultItem {
    /// Creates a projected item.
    #[must_use]
    pub fn new(identity: impl Into<String>, value: ProjectedValue) -> Self {
        Self { identity: identity.into(), value }
    }

    fn encoded_size(&self) -> usize {
        self.identity.len()
            + self.value.iter().map(|(k, v)| k.len() + v.len() + 16).sum::<usize>()
            + 16
    }
}

/// A bounded field operation in a [`ResultOperation::Patch`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FieldChange {
    /// Canonical typed field path (a single projected field in this harness).
    pub path: String,
    /// New value, or `None` to remove the field.
    pub value: Option<Vec<u8>>,
}

/// One atomic operation against an ordered result.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ResultOperation {
    /// Add a new item at `position`.
    Insert {
        /// Stable item identity.
        identity: String,
        /// Destination position in the ordered result.
        position: usize,
        /// Authorized projected value.
        value: ProjectedValue,
    },
    /// Replace an existing item's complete projected value.
    Update {
        /// Stable item identity.
        identity: String,
        /// Authorized projected replacement.
        value: ProjectedValue,
    },
    /// Apply bounded typed field changes to an existing item.
    Patch {
        /// Stable item identity.
        identity: String,
        /// Field-level changes to apply atomically.
        changes: Vec<FieldChange>,
    },
    /// Remove an item by identity.
    Remove {
        /// Stable item identity.
        identity: String,
    },
    /// Move an item to a new ordered position.
    Move {
        /// Stable item identity.
        identity: String,
        /// Destination position in the ordered result.
        position: usize,
    },
    /// Replace the complete result with a fresh authorized snapshot.
    Reset {
        /// Fresh authorized snapshot.
        snapshot: Box<Snapshot>,
    },
}

/// A complete authorized snapshot.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Snapshot {
    /// The view generation that produced this snapshot.
    pub view_generation: u64,
    /// The authorization generation used for filtering.
    pub authorization_generation: u64,
    /// The result revision represented by the snapshot.
    pub revision: u64,
    /// Ordered, authorized projected items.
    pub items: Vec<ResultItem>,
    /// Digest over all items in order.
    pub digest: ResultDigest,
    /// Resume checkpoint valid after applying this snapshot.
    pub checkpoint: Checkpoint,
}

impl Snapshot {
    /// Builds a snapshot and issues its opaque checkpoint.
    #[must_use]
    pub fn new(
        view_generation: u64,
        authorization_generation: u64,
        revision: u64,
        items: Vec<ResultItem>,
        retention_generation: u64,
        expires_at: u64,
    ) -> Self {
        let digest = result_digest(&items);
        let checkpoint =
            Checkpoint::issue(view_generation, revision, digest, retention_generation, expires_at);
        Self { view_generation, authorization_generation, revision, items, digest, checkpoint }
    }

    fn validate(&self, limits: Limits) -> Result<(), ViewError> {
        validate_items(&self.items, limits)?;
        if self.digest != result_digest(&self.items) {
            return Err(ViewError::DigestMismatch);
        }
        if self.checkpoint.view_generation != self.view_generation
            || self.checkpoint.revision != self.revision
            || !self.checkpoint.is_valid_at(0, self.checkpoint.retention_generation)
        {
            return Err(ViewError::InvalidCheckpoint);
        }
        Ok(())
    }
}

/// One result revision delivered after a snapshot.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ResultDelta {
    /// View generation for this delta.
    pub view_generation: u64,
    /// Revision from which operations apply.
    pub from_revision: u64,
    /// Revision reached after all operations apply.
    pub to_revision: u64,
    /// Atomic ordered operations.
    pub operations: Vec<ResultOperation>,
    /// Digest of the complete result at `to_revision`.
    pub result_digest: ResultDigest,
    /// Checkpoint issued only for the resulting revision.
    pub checkpoint: Checkpoint,
}

impl ResultDelta {
    /// Creates a delta, calculating its digest from `result` and issuing a
    /// checkpoint.  The caller supplies the already-authoritative final
    /// result; operations are never interpreted as a partial digest.
    #[must_use]
    pub fn new(
        view_generation: u64,
        from_revision: u64,
        to_revision: u64,
        operations: Vec<ResultOperation>,
        result: &[ResultItem],
        retention_generation: u64,
        expires_at: u64,
    ) -> Self {
        let result_digest = result_digest(result);
        let checkpoint = Checkpoint::issue(
            view_generation,
            to_revision,
            result_digest,
            retention_generation,
            expires_at,
        );
        Self { view_generation, from_revision, to_revision, operations, result_digest, checkpoint }
    }
}

/// One record that entered scope because the subscription widened.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ScopedItem {
    /// The record's current authorized projection. The expansion path is
    /// skip-free precisely because it consumes current values, never a
    /// suffix of the mutation log behind the old checkpoint.
    pub item: ResultItem,
    /// Destination position in the ordered result, with the same semantics
    /// as the `position` field of [`ResultOperation::Insert`]: positions
    /// apply in order against the result as it stands after removals and
    /// earlier inserts. Ignored when the identity already exists — an
    /// existing record is updated in place.
    pub position: usize,
    /// Causal position of the record's most recent mutation relative to the
    /// old checkpoint's scan frontier. May precede that frontier; such
    /// records are exactly the ones an older cursor would scan past, and
    /// they are delivered like every other record.
    pub last_mutation_position: u64,
}

/// Inputs of one subscription expansion into a new view generation.
#[derive(Clone, Debug)]
pub struct ExpansionRequest {
    /// View generation of the widened subscription. Must strictly exceed the
    /// view's current generation.
    pub next_view_generation: u64,
    /// Authorization generation of the widened scope. Must not move
    /// backwards: it advances when the expansion is itself a policy, role,
    /// or relationship change, and stays equal for a pure query widening.
    pub authorization_generation: u64,
    /// Records that entered scope with their current authorized
    /// projections. Identities already present are updated in place; new
    /// identities are inserted at the supplied positions. Duplicate
    /// identities inside this list are refused.
    pub newly_visible: Vec<ScopedItem>,
    /// Identities that left the authorized scope concurrently with the
    /// widening. Emitted as removals in the old view generation before it
    /// advances whenever the authorized diff stays bounded; carried by the
    /// replacement snapshot otherwise. An identity listed here and also in
    /// `newly_visible` is removed first and then reinserted — the widened
    /// scope wins.
    pub no_longer_visible: Vec<String>,
    /// The causal position the old checkpoint's cursor scanned through.
    /// Declared so callers and tests can name the skip class (records whose
    /// mutations precede this frontier); the expansion path never filters
    /// by it.
    pub scan_frontier: u64,
    /// Retained-history generation bound into the new checkpoints.
    pub retention_generation: u64,
    /// Expiry bound into the new checkpoints.
    pub expires_at: u64,
}

/// How a widened subscription advances the view generation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum GenerationAdvance {
    /// A bounded authorized diff onto the new generation. Removals have
    /// already landed in the old generation, and every newly visible record
    /// — including those whose mutations precede the old checkpoint —
    /// appears as an insert or update.
    Diff(ResultDelta),
    /// The authorized diff was not bounded, so a fresh authorized snapshot
    /// replaces the result. `Reset` is a correct bounded fallback; the
    /// engine never emits a guessed partial patch.
    Reset(Snapshot),
}

/// What one subscription expansion delivers, in wire order.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ExpansionDelivery {
    /// Authorization generation of the new view generation.
    pub authorization_generation: u64,
    /// Removals for items that left the authorized scope, delivered as a
    /// delta in the **old** view generation so permission loss is observed
    /// before the generation advances. `None` when there is nothing to
    /// remove, or when the diff is unbounded and the replacement snapshot
    /// carries the removals atomically.
    pub removals: Option<ResultDelta>,
    /// The new view generation as a bounded diff or a fresh snapshot.
    pub advance: GenerationAdvance,
}

/// A bounded result-view state machine.
#[derive(Clone, Debug)]
pub struct ViewState {
    view_generation: u64,
    authorization_generation: u64,
    revision: u64,
    items: Vec<ResultItem>,
    digest: ResultDigest,
    checkpoint: Option<Checkpoint>,
    limits: Limits,
    online: bool,
    optimistic: BTreeMap<IdempotencyKey, Vec<ResultOperation>>,
    completed_mutations: BTreeSet<IdempotencyKey>,
}

/// Limits applied before allocating or retaining view state.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Limits {
    /// Maximum result items.
    pub max_items: usize,
    /// Maximum bytes in any projected item.
    pub max_value_bytes: usize,
    /// Maximum operations in one delta.
    pub max_operations: usize,
}

impl Default for Limits {
    fn default() -> Self {
        Self {
            max_items: DEFAULT_MAX_ITEMS,
            max_value_bytes: DEFAULT_MAX_VALUE_BYTES,
            max_operations: 1024,
        }
    }
}

impl ViewState {
    /// Creates an empty state awaiting snapshot bootstrap.
    #[must_use]
    pub fn new(limits: Limits) -> Self {
        Self {
            view_generation: 0,
            authorization_generation: 0,
            revision: 0,
            items: Vec::new(),
            digest: ResultDigest::empty(),
            checkpoint: None,
            limits,
            online: true,
            optimistic: BTreeMap::new(),
            completed_mutations: BTreeSet::new(),
        }
    }

    /// Installs a complete authorized bootstrap snapshot atomically.
    ///
    /// # Errors
    ///
    /// Returns [`ViewError`] when the snapshot exceeds limits or carries an
    /// invalid digest or checkpoint.
    pub fn bootstrap(&mut self, snapshot: Snapshot) -> Result<(), ViewError> {
        snapshot.validate(self.limits)?;
        self.install_snapshot(snapshot);
        Ok(())
    }

    /// Applies one ordered delta. A duplicate is ignored; a gap or future
    /// reorder is reported so the caller can resume or request a reset.
    pub fn apply_delta(&mut self, delta: &ResultDelta) -> ApplyOutcome {
        if delta.view_generation != self.view_generation {
            return ApplyOutcome::ResetRequired(ResetReason::GenerationChanged);
        }
        if delta.to_revision <= self.revision {
            return if delta.to_revision == self.revision && delta.result_digest != self.digest {
                ApplyOutcome::ResetRequired(ResetReason::ViewDiverged)
            } else {
                ApplyOutcome::Duplicate
            };
        }
        if delta.from_revision != self.revision || delta.to_revision <= delta.from_revision {
            return ApplyOutcome::Gap { expected: self.revision, got: delta.from_revision };
        }
        if delta.operations.len() > self.limits.max_operations {
            return ApplyOutcome::ResetRequired(ResetReason::Oversized);
        }
        let mut candidate = self.items.clone();
        if apply_operations(&mut candidate, &delta.operations, self.limits).is_err() {
            return ApplyOutcome::ResetRequired(ResetReason::InvalidOperations);
        }
        if result_digest(&candidate) != delta.result_digest {
            return ApplyOutcome::ResetRequired(ResetReason::ViewDiverged);
        }
        if delta.checkpoint.view_generation != delta.view_generation
            || delta.checkpoint.revision != delta.to_revision
        {
            return ApplyOutcome::ResetRequired(ResetReason::InvalidCheckpoint);
        }
        self.items = candidate;
        self.revision = delta.to_revision;
        self.digest = delta.result_digest;
        self.checkpoint = Some(delta.checkpoint.clone());
        ApplyOutcome::Applied
    }

    /// Applies an authorization transition. Visibility removals are applied
    /// to the old generation before the replacement snapshot advances it.
    ///
    /// # Errors
    ///
    /// Returns [`ViewError`] for stale authorization or view generations, or
    /// when the replacement snapshot is invalid or exceeds limits.
    pub fn transition_authorization(
        &mut self,
        next_authorization_generation: u64,
        removals: &[String],
        replacement: Snapshot,
    ) -> Result<(), ViewError> {
        if next_authorization_generation <= self.authorization_generation {
            return Err(ViewError::StaleAuthorization);
        }
        replacement.validate(self.limits)?;
        if replacement.authorization_generation != next_authorization_generation
            || replacement.view_generation <= self.view_generation
        {
            return Err(ViewError::GenerationChanged);
        }
        for identity in removals {
            if let Some(index) = self.items.iter().position(|item| &item.identity == identity) {
                self.items.remove(index);
            }
        }
        self.install_snapshot_with_authorization(replacement, next_authorization_generation);
        Ok(())
    }

    /// Widens the subscription into a new view generation.
    ///
    /// The widened scope's newly visible records — including every record
    /// whose mutations precede the old checkpoint's scan frontier — are
    /// computed from their **current** authorized values, so an older
    /// cursor can never cause a skip. When the authorized diff is bounded
    /// it is delivered as a delta in the new generation through the
    /// coalescing queue; when it is not (operation bound exceeded, or the
    /// queue's backlog bounds trip), the queue's coalesced form — a fresh
    /// snapshot — is delivered instead. Concurrently revoked identities
    /// are emitted as removals in the old generation before it advances,
    /// whenever the removals themselves fit a bounded delta.
    ///
    /// The authoritative state advances to the new generation and the
    /// final authorized result regardless of which delivery shape the
    /// queue chose.
    ///
    /// # Errors
    ///
    /// Returns [`ViewError::InvalidCheckpoint`] when the view has not
    /// bootstrapped, [`ViewError::StaleGeneration`] or
    /// [`ViewError::StaleAuthorization`] for non-advancing generations,
    /// and [`ViewError::InvalidSnapshot`] for contradictory inputs
    /// (duplicate scoped identities, malformed positions, or values
    /// outside the configured bounds).
    #[allow(clippy::too_many_lines)]
    pub fn expand_subscription(
        &mut self,
        request: &ExpansionRequest,
        queue: &mut DeliveryQueue,
    ) -> Result<ExpansionDelivery, ViewError> {
        if self.checkpoint.is_none() {
            return Err(ViewError::InvalidCheckpoint);
        }
        if request.next_view_generation <= self.view_generation {
            return Err(ViewError::StaleGeneration);
        }
        if request.authorization_generation < self.authorization_generation {
            return Err(ViewError::StaleAuthorization);
        }
        let scoped_identities = request
            .newly_visible
            .iter()
            .map(|scoped| scoped.item.identity.as_str())
            .collect::<BTreeSet<_>>();
        if scoped_identities.len() != request.newly_visible.len() {
            return Err(ViewError::InvalidSnapshot);
        }

        // Removals are authoritative in the old generation first, exactly
        // like `transition_authorization`.
        let mut after_removals = self.items.clone();
        let mut removal_ops = Vec::new();
        for identity in &request.no_longer_visible {
            if let Some(index) = after_removals.iter().position(|item| &item.identity == identity) {
                after_removals.remove(index);
                removal_ops.push(ResultOperation::Remove { identity: identity.clone() });
            }
        }

        // The diff is built from current authorized values: an identity
        // already in scope is updated in place, a new one is inserted.
        let mut advance_ops = Vec::with_capacity(request.newly_visible.len());
        for scoped in &request.newly_visible {
            if after_removals.iter().any(|item| item.identity == scoped.item.identity) {
                advance_ops.push(ResultOperation::Update {
                    identity: scoped.item.identity.clone(),
                    value: scoped.item.value.clone(),
                });
            } else {
                advance_ops.push(ResultOperation::Insert {
                    identity: scoped.item.identity.clone(),
                    position: scoped.position,
                    value: scoped.item.value.clone(),
                });
            }
        }

        // Compute the final authorized result under the item and value
        // bounds; the operation bound is judged separately below so an
        // unbounded diff degrades to a snapshot instead of an error.
        let compute_limits = Limits { max_operations: usize::MAX, ..self.limits };
        let mut final_items = after_removals.clone();
        apply_operations(&mut final_items, &advance_ops, compute_limits)
            .map_err(|()| ViewError::InvalidSnapshot)?;
        // Skip-free by construction, guarded unconditionally (never only
        // for post-frontier records) so a future position filter cannot
        // reintroduce the skip class.
        for scoped in &request.newly_visible {
            if !final_items.iter().any(|item| item.identity == scoped.item.identity) {
                return Err(ViewError::InvalidSnapshot);
            }
        }

        let bounded = removal_ops.len() <= self.limits.max_operations
            && advance_ops.len() <= self.limits.max_operations;
        let removal_revision =
            if removal_ops.is_empty() { self.revision } else { self.revision + 1 };
        let expansion_snapshot = Snapshot::new(
            request.next_view_generation,
            request.authorization_generation,
            removal_revision + 1,
            final_items.clone(),
            request.retention_generation,
            request.expires_at,
        );

        let (removals, advance) = if bounded {
            // Removals ride the tail of the old generation's stream; the
            // coalescing machinery then decides the advance's shape.
            let removals = (!removal_ops.is_empty()).then(|| {
                ResultDelta::new(
                    self.view_generation,
                    self.revision,
                    removal_revision,
                    removal_ops,
                    &after_removals,
                    request.retention_generation,
                    request.expires_at,
                )
            });
            if let Some(removals) = &removals {
                queue.push(removals.clone(), expansion_snapshot.clone());
            }
            let advance_delta = ResultDelta::new(
                request.next_view_generation,
                removal_revision,
                removal_revision + 1,
                advance_ops,
                &final_items,
                request.retention_generation,
                request.expires_at,
            );
            let advance = match queue.push(advance_delta.clone(), expansion_snapshot.clone()) {
                QueueOutcome::Queued => GenerationAdvance::Diff(advance_delta),
                QueueOutcome::CoalescedReset => GenerationAdvance::Reset(expansion_snapshot),
            };
            (removals, advance)
        } else {
            // Unbounded: never a guessed partial patch. The snapshot
            // subsumes the removals atomically.
            queue.replace_with_snapshot(expansion_snapshot.clone());
            (None, GenerationAdvance::Reset(expansion_snapshot))
        };

        // The authoritative result advances regardless of delivery shape.
        self.authorization_generation = request.authorization_generation;
        self.view_generation = request.next_view_generation;
        self.revision = removal_revision + 1;
        self.items = final_items;
        self.digest = result_digest(&self.items);
        self.checkpoint = Some(match &advance {
            GenerationAdvance::Diff(delta) => delta.checkpoint.clone(),
            GenerationAdvance::Reset(snapshot) => snapshot.checkpoint.clone(),
        });
        Ok(ExpansionDelivery {
            authorization_generation: request.authorization_generation,
            removals,
            advance,
        })
    }

    /// Applies a produced expansion delivery in wire order: removals in the
    /// old view generation first, then the generation advance as a bounded
    /// diff or a fresh snapshot. Replaying an already-applied delivery is a
    /// typed refusal, never a re-application.
    ///
    /// # Errors
    ///
    /// Returns [`ViewError`] when the removals delta does not apply to the
    /// current state, when the advance does not strictly advance the
    /// generation or continue the current revision, when the resulting
    /// digest does not validate, or when the delivery would move the
    /// authorization generation backwards.
    pub fn apply_expansion(&mut self, delivery: &ExpansionDelivery) -> Result<(), ViewError> {
        if delivery.authorization_generation < self.authorization_generation {
            return Err(ViewError::StaleAuthorization);
        }
        if let Some(removals) = &delivery.removals {
            match self.apply_delta(removals) {
                ApplyOutcome::Applied | ApplyOutcome::Duplicate => {}
                ApplyOutcome::Gap { .. } => return Err(ViewError::StaleRevision),
                ApplyOutcome::ResetRequired(reason) => {
                    return Err(match reason {
                        ResetReason::GenerationChanged => ViewError::GenerationChanged,
                        ResetReason::ViewDiverged => ViewError::DigestMismatch,
                        ResetReason::Oversized => ViewError::Oversized,
                        ResetReason::InvalidOperations => ViewError::InvalidSnapshot,
                        _ => ViewError::InvalidCheckpoint,
                    });
                }
            }
        }
        match &delivery.advance {
            GenerationAdvance::Diff(delta) => {
                self.apply_generation_advance(delta, delivery.authorization_generation)
            }
            GenerationAdvance::Reset(snapshot) => {
                if snapshot.authorization_generation != delivery.authorization_generation {
                    return Err(ViewError::GenerationChanged);
                }
                self.reset(snapshot.clone())?;
                Ok(())
            }
        }
    }

    /// Applies a diff whose view generation strictly advances the current
    /// one, continuing exactly from the current revision.
    ///
    /// # Errors
    ///
    /// Returns [`ViewError`] when the generation does not strictly advance,
    /// the revision does not continue, the operations exceed bounds or do
    /// not apply, the resulting digest does not validate, or the delta's
    /// checkpoint does not match its own fields.
    pub fn apply_generation_advance(
        &mut self,
        delta: &ResultDelta,
        next_authorization_generation: u64,
    ) -> Result<(), ViewError> {
        if delta.view_generation <= self.view_generation {
            return Err(ViewError::StaleGeneration);
        }
        if delta.from_revision != self.revision || delta.to_revision <= delta.from_revision {
            return Err(ViewError::StaleRevision);
        }
        if delta.operations.len() > self.limits.max_operations {
            return Err(ViewError::Oversized);
        }
        let mut candidate = self.items.clone();
        apply_operations(&mut candidate, &delta.operations, self.limits)
            .map_err(|()| ViewError::InvalidSnapshot)?;
        if result_digest(&candidate) != delta.result_digest {
            return Err(ViewError::DigestMismatch);
        }
        if delta.checkpoint.view_generation != delta.view_generation
            || delta.checkpoint.revision != delta.to_revision
        {
            return Err(ViewError::InvalidCheckpoint);
        }
        self.view_generation = delta.view_generation;
        self.authorization_generation = next_authorization_generation;
        self.items = candidate;
        self.revision = delta.to_revision;
        self.digest = delta.result_digest;
        self.checkpoint = Some(delta.checkpoint.clone());
        Ok(())
    }

    /// Determines whether the current checkpoint can resume, returning a
    /// typed reset requirement for expiry or retained-history advancement.
    #[must_use]
    pub fn resume(
        &self,
        presented: &Checkpoint,
        now: u64,
        retention_generation: u64,
    ) -> ResumeDecision {
        if !presented.is_valid_at(now, retention_generation) {
            if now > presented.expires_at {
                ResumeDecision::ResetRequired(ResetReason::CheckpointExpired)
            } else {
                ResumeDecision::ResetRequired(ResetReason::RetentionAdvanced)
            }
        } else if self.checkpoint.as_ref() == Some(presented) {
            ResumeDecision::ReplayFrom(self.revision)
        } else {
            ResumeDecision::ResetRequired(ResetReason::CheckpointNotCurrent)
        }
    }

    /// Replaces the result with an authorized reset snapshot.
    ///
    /// # Errors
    ///
    /// Returns [`ViewError`] when the snapshot is invalid or older than the
    /// state already installed.
    pub fn reset(&mut self, snapshot: Snapshot) -> Result<(), ViewError> {
        snapshot.validate(self.limits)?;
        if snapshot.view_generation < self.view_generation {
            return Err(ViewError::StaleGeneration);
        }
        if snapshot.view_generation == self.view_generation && snapshot.revision < self.revision {
            return Err(ViewError::StaleRevision);
        }
        self.install_snapshot(snapshot);
        Ok(())
    }

    /// Sets whether online server-authoritative mutations may be attempted.
    pub fn set_online(&mut self, online: bool) {
        self.online = online;
    }

    /// Starts a reversible optimistic presentation. It cannot advance the
    /// authoritative revision, digest, or checkpoint.
    ///
    /// # Errors
    ///
    /// Returns [`MutationError`] when offline, over the pending or operation
    /// bounds, or when the presentation cannot apply to the current result.
    pub fn begin_mutation(
        &mut self,
        key: IdempotencyKey,
        presentation: Vec<ResultOperation>,
    ) -> Result<MutationStatus, MutationError> {
        if !self.online {
            return Err(MutationError::Offline);
        }
        if self.completed_mutations.contains(&key) || self.optimistic.contains_key(&key) {
            return Err(MutationError::DuplicateIdentity);
        }
        if presentation.len() > self.limits.max_operations {
            return Err(MutationError::Oversized);
        }
        if self.optimistic.len() >= MAX_PENDING_MUTATIONS {
            return Err(MutationError::TooManyPending);
        }
        let mut candidate = self.visible_items();
        apply_operations(&mut candidate, &presentation, self.limits)
            .map_err(|()| MutationError::InvalidPresentation)?;
        self.optimistic.insert(key, presentation);
        Ok(MutationStatus::Pending)
    }

    /// Accepts a server outcome and rebases remaining presentations on the
    /// authoritative result. Retrying the same identity is idempotent.
    ///
    /// # Errors
    ///
    /// Returns [`MutationError`] when the identity is unknown or the
    /// authoritative result requires a gap or reset.
    pub fn accept_mutation(
        &mut self,
        key: &IdempotencyKey,
        delta: &ResultDelta,
    ) -> Result<MutationStatus, MutationError> {
        if self.completed_mutations.contains(key) {
            return Ok(MutationStatus::Accepted);
        }
        if !self.optimistic.contains_key(key) {
            return Err(MutationError::UnknownIdentity);
        }
        match self.apply_delta(delta) {
            ApplyOutcome::Applied | ApplyOutcome::Duplicate => {
                self.optimistic.remove(key);
                self.remember_completed(key.clone());
                Ok(MutationStatus::Accepted)
            }
            ApplyOutcome::ResetRequired(reason) => Err(MutationError::AuthoritativeReset(reason)),
            ApplyOutcome::Gap { .. } => Err(MutationError::AuthoritativeGap),
        }
    }

    /// Rejects a pending mutation and removes its presentation. Repeated
    /// rejection is safe and returns the already-rejected status.
    ///
    /// # Errors
    ///
    /// Returns [`MutationError::UnknownIdentity`] when the identity is not
    /// pending or already completed.
    pub fn reject_mutation(
        &mut self,
        key: &IdempotencyKey,
    ) -> Result<MutationStatus, MutationError> {
        if self.completed_mutations.contains(key) {
            return Ok(MutationStatus::Rejected);
        }
        if self.optimistic.remove(key).is_some() {
            self.remember_completed(key.clone());
            Ok(MutationStatus::Rejected)
        } else {
            Err(MutationError::UnknownIdentity)
        }
    }

    /// Returns the authoritative ordered items, excluding optimistic
    /// presentation. Use [`Self::visible_items`] for rendering.
    #[must_use]
    pub fn items(&self) -> &[ResultItem] {
        &self.items
    }

    /// Applies pending presentations over a clone of authoritative state.
    #[must_use]
    pub fn visible_items(&self) -> Vec<ResultItem> {
        let mut visible = self.items.clone();
        for operations in self.optimistic.values() {
            if apply_operations(&mut visible, operations, self.limits).is_err() {
                return self.items.clone();
            }
        }
        visible
    }

    /// Current authoritative revision.
    #[must_use]
    pub const fn revision(&self) -> u64 {
        self.revision
    }

    /// Current complete-result digest.
    #[must_use]
    pub const fn digest(&self) -> ResultDigest {
        self.digest
    }

    /// Current view generation.
    #[must_use]
    pub const fn view_generation(&self) -> u64 {
        self.view_generation
    }

    /// Current authorization generation.
    #[must_use]
    pub const fn authorization_generation(&self) -> u64 {
        self.authorization_generation
    }

    /// Current opaque checkpoint, if bootstrap has completed.
    #[must_use]
    pub fn checkpoint(&self) -> Option<&Checkpoint> {
        self.checkpoint.as_ref()
    }

    fn install_snapshot(&mut self, snapshot: Snapshot) {
        self.view_generation = snapshot.view_generation;
        self.authorization_generation = snapshot.authorization_generation;
        self.install_result(snapshot);
    }

    fn install_snapshot_with_authorization(&mut self, snapshot: Snapshot, auth: u64) {
        self.view_generation = snapshot.view_generation;
        self.authorization_generation = auth;
        self.install_result(snapshot);
    }

    fn install_result(&mut self, snapshot: Snapshot) {
        self.revision = snapshot.revision;
        self.items = snapshot.items;
        self.digest = snapshot.digest;
        self.checkpoint = Some(snapshot.checkpoint);
    }

    fn remember_completed(&mut self, key: IdempotencyKey) {
        if self.completed_mutations.len() >= MAX_PENDING_MUTATIONS
            && let Some(oldest) = self.completed_mutations.iter().next().cloned()
        {
            self.completed_mutations.remove(&oldest);
        }
        self.completed_mutations.insert(key);
    }
}

/// Result of applying a revision.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ApplyOutcome {
    /// The revision was applied atomically.
    Applied,
    /// The same or an older revision was already applied.
    Duplicate,
    /// A missing or reordered revision requires resume.
    Gap {
        /// Revision currently applied by the consumer.
        expected: u64,
        /// Revision named as the delta's source.
        got: u64,
    },
    /// The stream cannot be safely repaired by applying this delta.
    ResetRequired(ResetReason),
}

/// Why a stream requires a fresh authorized snapshot.
///
/// The wire values are the stable `reset_reason` enum values of the
/// `terrascale.sync.v1.msgpack` manifest (1–9); unknown values fail
/// closed on decode. The [`ResetReason::name`] strings are the
/// manifest's snake-case names, shared with `ts-typescript`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResetReason {
    /// Checkpoint lifetime elapsed.
    CheckpointExpired,
    /// Retained-history generation changed.
    RetentionAdvanced,
    /// Authorization or view generation changed.
    GenerationChanged,
    /// The checkpoint was not the current view checkpoint.
    CheckpointNotCurrent,
    /// Complete-result digest did not validate.
    ViewDiverged,
    /// Operation list or snapshot exceeded bounds.
    Oversized,
    /// Operations were not valid for the current result.
    InvalidOperations,
    /// Checkpoint fields did not match the delta.
    InvalidCheckpoint,
    /// Backlog was coalesced into a reset.
    Backpressure,
}

impl ResetReason {
    /// The stable wire value from the `MessagePack` manifest.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::CheckpointExpired => 1,
            Self::RetentionAdvanced => 2,
            Self::GenerationChanged => 3,
            Self::CheckpointNotCurrent => 4,
            Self::ViewDiverged => 5,
            Self::Oversized => 6,
            Self::InvalidOperations => 7,
            Self::InvalidCheckpoint => 8,
            Self::Backpressure => 9,
        }
    }

    /// The reason for a wire value, or `None` for unknown values.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        Some(match code {
            1 => Self::CheckpointExpired,
            2 => Self::RetentionAdvanced,
            3 => Self::GenerationChanged,
            4 => Self::CheckpointNotCurrent,
            5 => Self::ViewDiverged,
            6 => Self::Oversized,
            7 => Self::InvalidOperations,
            8 => Self::InvalidCheckpoint,
            9 => Self::Backpressure,
            _ => return None,
        })
    }

    /// The stable manifest name shared with `ts-typescript`.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::CheckpointExpired => "checkpoint_expired",
            Self::RetentionAdvanced => "retention_advanced",
            Self::GenerationChanged => "generation_changed",
            Self::CheckpointNotCurrent => "checkpoint_not_current",
            Self::ViewDiverged => "view_diverged",
            Self::Oversized => "oversized",
            Self::InvalidOperations => "invalid_operations",
            Self::InvalidCheckpoint => "invalid_checkpoint",
            Self::Backpressure => "backpressure",
        }
    }
}

/// Resume decision after validating an opaque checkpoint.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResumeDecision {
    /// The server may replay from this already-applied revision.
    ReplayFrom(u64),
    /// A new authorized snapshot is required.
    ResetRequired(ResetReason),
}

/// A bounded caller identity for mutation retries.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct IdempotencyKey(String);

impl IdempotencyKey {
    /// Validates and creates an identity. Identities are opaque to mutation
    /// semantics and are never used as replication identities.
    ///
    /// # Errors
    ///
    /// Returns [`MutationError::InvalidIdentity`] for an empty, overlong, or
    /// control-character-containing value.
    pub fn new(value: impl Into<String>) -> Result<Self, MutationError> {
        let value = value.into();
        if value.trim().is_empty()
            || value.len() > MAX_IDEMPOTENCY_BYTES
            || value.chars().any(char::is_control)
        {
            return Err(MutationError::InvalidIdentity);
        }
        Ok(Self(value))
    }

    /// Returns the validated identity for request correlation.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// Mutation lifecycle status.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MutationStatus {
    /// A reversible presentation is visible while the request is in flight.
    Pending,
    /// The server accepted the durable request.
    Accepted,
    /// The server rejected the request and presentation was rolled back.
    Rejected,
}

/// Why an online optimistic mutation could not complete.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MutationError {
    /// Mutations attempted while disconnected are never queued.
    Offline,
    /// Identity is empty, invalid, or oversized.
    InvalidIdentity,
    /// Identity is already pending or completed.
    DuplicateIdentity,
    /// Presentation exceeds the operation bound.
    Oversized,
    /// The view already has its bounded number of pending mutations.
    TooManyPending,
    /// Presentation cannot apply to the authoritative result.
    InvalidPresentation,
    /// The identity is not pending.
    UnknownIdentity,
    /// The authoritative result requires a reset.
    AuthoritativeReset(ResetReason),
    /// The authoritative result had a revision gap.
    AuthoritativeGap,
}

/// A queued delivery, either one delta or a coalesced reset snapshot.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum QueuedDelivery {
    /// An incremental result revision.
    Delta(ResultDelta),
    /// A fresh snapshot replacing all pending revisions.
    Reset(Snapshot),
}

/// Outcome of adding to the bounded delivery queue.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum QueueOutcome {
    /// Delta was retained.
    Queued,
    /// Older pending work was replaced with a reset.
    CoalescedReset,
}

/// A per-subscriber bounded outbound result queue.
#[derive(Clone, Debug)]
pub struct DeliveryQueue {
    max_revisions: usize,
    max_bytes: usize,
    bytes: usize,
    pending: VecDeque<ResultDelta>,
    reset: Option<Snapshot>,
}

impl DeliveryQueue {
    /// Creates a queue with independent revision and byte bounds.
    #[must_use]
    pub fn new(max_revisions: usize, max_bytes: usize) -> Self {
        Self { max_revisions, max_bytes, bytes: 0, pending: VecDeque::new(), reset: None }
    }

    /// Queues a delta, replacing all pending work with `snapshot` if the
    /// backlog exceeds either bound.  The reset is always one bounded object.
    pub fn push(&mut self, delta: ResultDelta, snapshot: Snapshot) -> QueueOutcome {
        let delta_bytes = estimate_delta_bytes(&delta);
        if self.reset.is_some()
            || self.pending.len() >= self.max_revisions
            || self.bytes.saturating_add(delta_bytes) > self.max_bytes
        {
            self.pending.clear();
            self.bytes = 0;
            self.reset = Some(snapshot);
            QueueOutcome::CoalescedReset
        } else {
            self.bytes = self.bytes.saturating_add(delta_bytes);
            self.pending.push_back(delta);
            QueueOutcome::Queued
        }
    }

    /// Takes the next bounded delivery.
    pub fn pop(&mut self) -> Option<QueuedDelivery> {
        if let Some(snapshot) = self.reset.take() {
            return Some(QueuedDelivery::Reset(snapshot));
        }
        let delta = self.pending.pop_front()?;
        self.bytes = self.bytes.saturating_sub(estimate_delta_bytes(&delta));
        Some(QueuedDelivery::Delta(delta))
    }

    /// Installs a reset snapshot as the queue's sole delivery without a
    /// delta to attempt first: the terminal coalesced form, used when an
    /// authorized diff is unbounded and a snapshot must replace the
    /// backlog directly.
    pub fn replace_with_snapshot(&mut self, snapshot: Snapshot) {
        self.pending.clear();
        self.bytes = 0;
        self.reset = Some(snapshot);
    }

    /// Number of retained deliveries (zero or one after coalescing).
    #[must_use]
    pub fn len(&self) -> usize {
        self.pending.len() + usize::from(self.reset.is_some())
    }

    /// Whether no delivery is retained.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

fn apply_operations(
    items: &mut Vec<ResultItem>,
    operations: &[ResultOperation],
    limits: Limits,
) -> Result<(), ()> {
    if operations.len() > limits.max_operations {
        return Err(());
    }
    let mut identities = items.iter().map(|item| item.identity.clone()).collect::<BTreeSet<_>>();
    for operation in operations {
        match operation {
            ResultOperation::Insert { identity, position, value } => {
                if identity.is_empty()
                    || !identities.insert(identity.clone())
                    || *position > items.len()
                {
                    return Err(());
                }
                let item = ResultItem::new(identity.clone(), value.clone());
                if item.encoded_size() > limits.max_value_bytes {
                    return Err(());
                }
                items.insert(*position, item);
            }
            ResultOperation::Update { identity, value } => {
                let item = items.iter_mut().find(|item| item.identity == *identity).ok_or(())?;
                item.value = value.clone();
                if item.encoded_size() > limits.max_value_bytes {
                    return Err(());
                }
            }
            ResultOperation::Patch { identity, changes } => {
                let item = items.iter_mut().find(|item| item.identity == *identity).ok_or(())?;
                for change in changes {
                    if change.path.is_empty() {
                        return Err(());
                    }
                    match &change.value {
                        Some(value) => {
                            item.value.insert(change.path.clone(), value.clone());
                        }
                        None => {
                            item.value.remove(&change.path);
                        }
                    }
                }
                if item.encoded_size() > limits.max_value_bytes {
                    return Err(());
                }
            }
            ResultOperation::Remove { identity } => {
                let index = items.iter().position(|item| item.identity == *identity).ok_or(())?;
                items.remove(index);
                identities.remove(identity);
            }
            ResultOperation::Move { identity, position } => {
                let index = items.iter().position(|item| item.identity == *identity).ok_or(())?;
                if *position >= items.len() {
                    return Err(());
                }
                let item = items.remove(index);
                items.insert(*position, item);
            }
            ResultOperation::Reset { snapshot } => {
                if snapshot.validate(limits).is_err() {
                    return Err(());
                }
                items.clone_from(&snapshot.items);
                identities = items.iter().map(|item| item.identity.clone()).collect();
            }
        }
        if items.len() > limits.max_items {
            return Err(());
        }
    }
    Ok(())
}

fn validate_items(items: &[ResultItem], limits: Limits) -> Result<(), ViewError> {
    if items.len() > limits.max_items {
        return Err(ViewError::Oversized);
    }
    let mut identities = BTreeSet::new();
    for item in items {
        if item.identity.is_empty()
            || !identities.insert(&item.identity)
            || item.encoded_size() > limits.max_value_bytes
        {
            return Err(ViewError::InvalidSnapshot);
        }
    }
    Ok(())
}

/// Computes the canonical four-lane FNV-1a digest of an ordered result.
/// An empty result has the all-zero digest. Field order follows UTF-8 keys.
#[must_use]
pub fn result_digest(items: &[ResultItem]) -> ResultDigest {
    if items.is_empty() {
        return ResultDigest::empty();
    }
    let mut bytes = Vec::new();
    for item in items {
        bytes.extend_from_slice(&(item.identity.len() as u64).to_le_bytes());
        bytes.extend_from_slice(item.identity.as_bytes());
        for (key, value) in &item.value {
            bytes.extend_from_slice(&(key.len() as u64).to_le_bytes());
            bytes.extend_from_slice(key.as_bytes());
            bytes.extend_from_slice(&(value.len() as u64).to_le_bytes());
            bytes.extend_from_slice(value);
        }
        bytes.push(0xff);
    }
    let mut out = [0; 32];
    for lane in 0..4 {
        let mut hash =
            0xcbf2_9ce4_8422_2325_u64 ^ (lane as u64).wrapping_mul(0x9e37_79b9_7f4a_7c15);
        for byte in &bytes {
            hash ^= u64::from(*byte);
            hash = hash.wrapping_mul(0x0100_0000_01b3);
        }
        out[lane * 8..lane * 8 + 8].copy_from_slice(&hash.to_le_bytes());
    }
    ResultDigest(out)
}

fn checksum(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0100_0000_01b3);
    }
    hash
}

fn estimate_delta_bytes(delta: &ResultDelta) -> usize {
    64 + delta.operations.len() * 32
        + delta
            .operations
            .iter()
            .map(|operation| match operation {
                ResultOperation::Insert { identity, value, .. }
                | ResultOperation::Update { identity, value } => identity.len() + value.len(),
                ResultOperation::Patch { identity, changes } => {
                    identity.len()
                        + changes
                            .iter()
                            .map(|change| {
                                change.path.len() + change.value.as_ref().map_or(0, Vec::len)
                            })
                            .sum::<usize>()
                }
                ResultOperation::Remove { identity } | ResultOperation::Move { identity, .. } => {
                    identity.len()
                }
                ResultOperation::Reset { snapshot } => {
                    snapshot.items.iter().map(ResultItem::encoded_size).sum()
                }
            })
            .sum::<usize>()
}

/// Errors raised while installing an authorized snapshot or transition.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ViewError {
    /// Snapshot exceeds configured bounds.
    Oversized,
    /// Snapshot contains malformed identities or duplicate rows.
    InvalidSnapshot,
    /// Snapshot digest does not cover the supplied rows.
    DigestMismatch,
    /// Checkpoint does not match its snapshot.
    InvalidCheckpoint,
    /// Transition would move backwards.
    StaleGeneration,
    /// A reset would move a generation's revision backwards.
    StaleRevision,
    /// Authorization generation is stale.
    StaleAuthorization,
    /// Transition supplied inconsistent generation values.
    GenerationChanged,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn value(name: &str, body: &str) -> ProjectedValue {
        BTreeMap::from([(name.to_owned(), body.as_bytes().to_vec())])
    }

    fn snapshot(
        generation: u64,
        auth: u64,
        revision: u64,
        names: &[&str],
        expiry: u64,
    ) -> Snapshot {
        Snapshot::new(
            generation,
            auth,
            revision,
            names.iter().map(|name| ResultItem::new(*name, value("v", name))).collect(),
            1,
            expiry,
        )
    }

    #[derive(Clone)]
    enum CorpusStep {
        Bootstrap,
        Insert,
        Update,
        Patch,
        Move,
        Remove,
        Duplicate,
        Reordered,
        Reset,
    }

    #[test]
    #[allow(clippy::too_many_lines)]
    fn semantic_corpus_snapshot_operations_and_delivery() {
        let corpus = [
            CorpusStep::Bootstrap,
            CorpusStep::Insert,
            CorpusStep::Update,
            CorpusStep::Patch,
            CorpusStep::Move,
            CorpusStep::Remove,
            CorpusStep::Duplicate,
            CorpusStep::Reordered,
            CorpusStep::Reset,
        ];
        let mut state = ViewState::new(Limits::default());
        let initial = snapshot(1, 1, 0, &["a", "b"], 100);
        for step in corpus {
            match step {
                CorpusStep::Bootstrap => state.bootstrap(initial.clone()).unwrap(),
                CorpusStep::Insert => {
                    let mut final_items = state.items().to_vec();
                    final_items.insert(1, ResultItem::new("c", value("v", "c")));
                    let delta = ResultDelta::new(
                        1,
                        0,
                        1,
                        vec![ResultOperation::Insert {
                            identity: "c".into(),
                            position: 1,
                            value: value("v", "c"),
                        }],
                        &final_items,
                        1,
                        100,
                    );
                    assert_eq!(state.apply_delta(&delta), ApplyOutcome::Applied);
                }
                CorpusStep::Update => {
                    let mut final_items = state.items().to_vec();
                    final_items[0].value = value("v", "updated");
                    let delta = ResultDelta::new(
                        1,
                        1,
                        2,
                        vec![ResultOperation::Update {
                            identity: "a".into(),
                            value: value("v", "updated"),
                        }],
                        &final_items,
                        1,
                        100,
                    );
                    assert_eq!(state.apply_delta(&delta), ApplyOutcome::Applied);
                }
                CorpusStep::Patch => {
                    let mut final_items = state.items().to_vec();
                    final_items[0].value.insert("extra".into(), b"yes".to_vec());
                    let delta = ResultDelta::new(
                        1,
                        2,
                        3,
                        vec![ResultOperation::Patch {
                            identity: "a".into(),
                            changes: vec![FieldChange {
                                path: "extra".into(),
                                value: Some(b"yes".to_vec()),
                            }],
                        }],
                        &final_items,
                        1,
                        100,
                    );
                    assert_eq!(state.apply_delta(&delta), ApplyOutcome::Applied);
                }
                CorpusStep::Move => {
                    let mut final_items = state.items().to_vec();
                    let item = final_items.remove(1);
                    final_items.insert(0, item);
                    let delta = ResultDelta::new(
                        1,
                        3,
                        4,
                        vec![ResultOperation::Move { identity: "c".into(), position: 0 }],
                        &final_items,
                        1,
                        100,
                    );
                    assert_eq!(state.apply_delta(&delta), ApplyOutcome::Applied);
                }
                CorpusStep::Remove => {
                    let mut final_items = state.items().to_vec();
                    final_items.retain(|item| item.identity != "b");
                    let delta = ResultDelta::new(
                        1,
                        4,
                        5,
                        vec![ResultOperation::Remove { identity: "b".into() }],
                        &final_items,
                        1,
                        100,
                    );
                    assert_eq!(state.apply_delta(&delta), ApplyOutcome::Applied);
                }
                CorpusStep::Duplicate => {
                    let current = state.items().to_vec();
                    let delta = ResultDelta::new(1, 4, 5, vec![], &current, 1, 100);
                    assert_eq!(state.apply_delta(&delta), ApplyOutcome::Duplicate);
                }
                CorpusStep::Reordered => {
                    let current = state.items().to_vec();
                    let delta = ResultDelta::new(1, 8, 9, vec![], &current, 1, 100);
                    assert_eq!(
                        state.apply_delta(&delta),
                        ApplyOutcome::Gap { expected: 5, got: 8 }
                    );
                }
                CorpusStep::Reset => state.reset(snapshot(2, 1, 0, &["z"], 100)).unwrap(),
            }
        }
        assert_eq!(state.items()[0].identity, "z");
    }

    #[test]
    fn optimistic_accept_reject_and_online_only() {
        let mut state = ViewState::new(Limits::default());
        state.bootstrap(snapshot(1, 1, 0, &["a"], 100)).unwrap();
        let key = IdempotencyKey::new("request-1").unwrap();
        state
            .begin_mutation(
                key.clone(),
                vec![ResultOperation::Update {
                    identity: "a".into(),
                    value: value("v", "optimistic"),
                }],
            )
            .unwrap();
        assert_eq!(state.items()[0].value["v"], b"a");
        assert_eq!(state.visible_items()[0].value["v"], b"optimistic");
        let mut accepted = state.items().to_vec();
        accepted[0].value = value("v", "server");
        let delta = ResultDelta::new(
            1,
            0,
            1,
            vec![ResultOperation::Update { identity: "a".into(), value: value("v", "server") }],
            &accepted,
            1,
            100,
        );
        assert_eq!(state.accept_mutation(&key, &delta), Ok(MutationStatus::Accepted));
        assert_eq!(state.visible_items()[0].value["v"], b"server");
        let rejected = IdempotencyKey::new("request-2").unwrap();
        state
            .begin_mutation(
                rejected.clone(),
                vec![ResultOperation::Update {
                    identity: "a".into(),
                    value: value("v", "temporary"),
                }],
            )
            .unwrap();
        assert_eq!(state.reject_mutation(&rejected), Ok(MutationStatus::Rejected));
        state.set_online(false);
        assert_eq!(
            state.begin_mutation(IdempotencyKey::new("request-3").unwrap(), vec![]),
            Err(MutationError::Offline)
        );
    }

    #[test]
    fn checkpoint_expiry_authorization_and_slow_consumer_reset() {
        let mut state = ViewState::new(Limits::default());
        state.bootstrap(snapshot(1, 1, 0, &["a", "secret"], 5)).unwrap();
        let checkpoint = state.checkpoint().unwrap().clone();
        assert_eq!(
            state.resume(&checkpoint, 6, 1),
            ResumeDecision::ResetRequired(ResetReason::CheckpointExpired)
        );
        let replacement = snapshot(2, 2, 0, &["a"], 100);
        state.transition_authorization(2, &["secret".into()], replacement).unwrap();
        assert_eq!(state.authorization_generation(), 2);
        assert_eq!(
            state.items().iter().map(|item| item.identity.as_str()).collect::<Vec<_>>(),
            ["a"]
        );

        let mut queue = DeliveryQueue::new(1, 1);
        let base = state.items().to_vec();
        let delta = ResultDelta::new(2, 0, 1, vec![], &base, 1, 100);
        assert_eq!(queue.push(delta, snapshot(2, 2, 1, &["a"], 100)), QueueOutcome::CoalescedReset);
        assert!(matches!(queue.pop(), Some(QueuedDelivery::Reset(_))));
    }

    fn scoped(name: &str, position: usize, last_mutation_position: u64) -> ScopedItem {
        ScopedItem {
            item: ResultItem::new(name, value("v", name)),
            position,
            last_mutation_position,
        }
    }

    fn request(next: u64, newly_visible: Vec<ScopedItem>) -> ExpansionRequest {
        ExpansionRequest {
            next_view_generation: next,
            authorization_generation: 1,
            newly_visible,
            no_longer_visible: Vec::new(),
            scan_frontier: 100,
            retention_generation: 1,
            expires_at: 100,
        }
    }

    #[test]
    fn expansion_input_validation() {
        let mut state = ViewState::new(Limits::default());
        let mut queue = DeliveryQueue::new(8, 1 << 20);
        assert_eq!(
            state.expand_subscription(&request(1, vec![]), &mut queue),
            Err(ViewError::InvalidCheckpoint)
        );
        state.bootstrap(snapshot(1, 1, 0, &["a"], 100)).unwrap();
        assert_eq!(
            state.expand_subscription(&request(1, vec![scoped("n", 0, 5)]), &mut queue),
            Err(ViewError::StaleGeneration)
        );
        assert_eq!(
            state.expand_subscription(&request(0, vec![scoped("n", 0, 5)]), &mut queue),
            Err(ViewError::StaleGeneration)
        );
        let mut backwards = request(2, vec![scoped("n", 0, 5)]);
        backwards.authorization_generation = 0;
        assert_eq!(
            state.expand_subscription(&backwards, &mut queue),
            Err(ViewError::StaleAuthorization)
        );
        assert_eq!(
            state.expand_subscription(
                &request(2, vec![scoped("n", 0, 5), scoped("n", 1, 6)]),
                &mut queue
            ),
            Err(ViewError::InvalidSnapshot)
        );
        assert_eq!(
            state.expand_subscription(&request(2, vec![scoped("n", 9, 5)]), &mut queue),
            Err(ViewError::InvalidSnapshot)
        );
        // Nothing above mutated the view: it is still the bootstrapped
        // generation and revision.
        assert_eq!(state.view_generation(), 1);
        assert_eq!(state.revision(), 0);
    }

    #[test]
    fn reset_reasons_match_the_msgpack_manifest() {
        // The stable `reset_reason` enum of
        // `contracts/msgpack/terrascale.sync.v1.msgpack.json` (protocol
        // 1.0) and the generated `ts-typescript` module: values 1–9
        // with the manifest's snake-case names.
        let expected: &[(ResetReason, u8, &str)] = &[
            (ResetReason::CheckpointExpired, 1, "checkpoint_expired"),
            (ResetReason::RetentionAdvanced, 2, "retention_advanced"),
            (ResetReason::GenerationChanged, 3, "generation_changed"),
            (ResetReason::CheckpointNotCurrent, 4, "checkpoint_not_current"),
            (ResetReason::ViewDiverged, 5, "view_diverged"),
            (ResetReason::Oversized, 6, "oversized"),
            (ResetReason::InvalidOperations, 7, "invalid_operations"),
            (ResetReason::InvalidCheckpoint, 8, "invalid_checkpoint"),
            (ResetReason::Backpressure, 9, "backpressure"),
        ];
        for (reason, code, name) in expected {
            assert_eq!(reason.code(), *code, "{reason:?} code");
            assert_eq!(reason.name(), *name, "{reason:?} name");
            assert_eq!(ResetReason::from_code(*code), Some(*reason), "code {code}");
        }
        assert_eq!(ResetReason::from_code(0), None, "0 is not a reason");
        assert_eq!(ResetReason::from_code(10), None, "10 is not a reason");
    }
}
