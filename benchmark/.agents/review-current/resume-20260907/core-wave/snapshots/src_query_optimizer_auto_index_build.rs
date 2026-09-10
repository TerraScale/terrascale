//! Bounded, invisible automatic-index builds.
//!
//! The builder in this module is deliberately independent of SQLite.  A
//! storage adapter supplies contiguous journal batches and an atomic
//! catch-up transaction; this module owns the durable identity, checkpoint,
//! and publication invariants.

/// A monotonic publication sequence observed while an index is built.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct BuildWatermark {
    seq: u64,
}

impl BuildWatermark {
    /// Creates a watermark from a durable publication sequence.
    pub(crate) const fn new(seq: u64) -> Self {
        Self { seq }
    }

    /// Returns the publication sequence.
    pub(crate) const fn seq(self) -> u64 {
        self.seq
    }

    /// Returns the next sequence, refusing to wrap at `u64::MAX`.
    pub(crate) const fn checked_next(self) -> Result<Self, BuildError> {
        match self.seq.checked_add(1) {
            Some(seq) => Ok(Self::new(seq)),
            None => Err(BuildError::SequenceOverflow),
        }
    }
}

/// The durable identity of the index definition being built.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct BuildIdentity {
    schema_generation: u64,
    plan_generation: u64,
    plan_fingerprint: u64,
}

impl BuildIdentity {
    /// Creates an identity when the plan generation is its definition key.
    pub(crate) const fn new(schema_generation: u64, plan_generation: u64) -> Self {
        Self { schema_generation, plan_generation, plan_fingerprint: 0 }
    }

    /// Creates an identity with an additional stable plan fingerprint.
    pub(crate) const fn with_fingerprint(
        schema_generation: u64,
        plan_generation: u64,
        plan_fingerprint: u64,
    ) -> Self {
        Self { schema_generation, plan_generation, plan_fingerprint }
    }

    /// Returns the schema generation.
    pub(crate) const fn schema_generation(self) -> u64 {
        self.schema_generation
    }

    /// Returns the index-plan generation.
    pub(crate) const fn plan_generation(self) -> u64 {
        self.plan_generation
    }

    /// Returns the stable index-plan fingerprint.
    pub(crate) const fn plan_fingerprint(self) -> u64 {
        self.plan_fingerprint
    }
}

/// A contiguous range of journal work supplied to one bounded build step.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct BuildBatch {
    first: BuildWatermark,
    last: BuildWatermark,
    items: u64,
}

impl BuildBatch {
    /// Creates a batch. Every journal sequence in the range must be present.
    pub(crate) fn new(
        first: BuildWatermark,
        last: BuildWatermark,
        items: u64,
    ) -> Result<Self, BuildError> {
        if first > last || items == 0 {
            return Err(BuildError::InvalidBatch);
        }
        let span = last
            .seq()
            .checked_sub(first.seq())
            .and_then(|distance| distance.checked_add(1))
            .ok_or(BuildError::SequenceOverflow)?;
        if span != items {
            return Err(BuildError::SequenceGap { expected: span, received: items });
        }
        Ok(Self { first, last, items })
    }

    /// Creates the one-entry batch at `sequence`.
    pub(crate) const fn single(sequence: BuildWatermark) -> Self {
        Self { first: sequence, last: sequence, items: 1 }
    }

    /// Returns the first sequence in the batch.
    pub(crate) const fn first(self) -> BuildWatermark {
        self.first
    }

    /// Returns the last sequence in the batch.
    pub(crate) const fn last(self) -> BuildWatermark {
        self.last
    }

    /// Returns the number of journal entries in the batch.
    pub(crate) const fn items(self) -> u64 {
        self.items
    }
}

/// Durable phase of one invisible build.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum BuildPhase {
    /// Backfill is accepting bounded journal batches.
    Building,
    /// Full coverage was verified, but publication has not completed.
    Verified,
    /// The index is atomically visible to query planning.
    Active,
    /// The build was failed or explicitly discarded.
    Abandoned,
}

/// Query-side availability while a build is progressing.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum BuildAvailability {
    /// A hybrid plan may use the covered prefix and another source for the tail.
    Hybrid,
    /// The index is not yet safe for a hybrid handoff and must not be used.
    IndexBuilding,
    /// The fully verified index is visible.
    Active,
}

/// The two disjoint portions of a building index's exact read plan.
///
/// The index is only trusted through [`Self::index_through`].  Rows after
/// that watermark must be read from the base collection and rechecked by the
/// query predicate.  Keeping the boundary in a value (rather than having the
/// planner infer it from mutable builder state) prevents a hybrid plan from
/// silently omitting the first row in the scan tail.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct HybridPlan {
    index_through: BuildWatermark,
    scan_from: BuildWatermark,
    scan_through: BuildWatermark,
}

impl HybridPlan {
    /// Returns the last sequence that is safe to read from the index.
    pub(crate) const fn index_through(self) -> BuildWatermark {
        self.index_through
    }

    /// Returns the first sequence that must be read from the base collection.
    pub(crate) const fn scan_from(self) -> BuildWatermark {
        self.scan_from
    }

    /// Returns the last sequence in the bounded scan tail.
    pub(crate) const fn scan_through(self) -> BuildWatermark {
        self.scan_through
    }

    /// Returns the number of journal entries in the scan tail.
    pub(crate) const fn scan_items(self) -> u64 {
        self.scan_through.seq() - self.index_through.seq()
    }
}

/// A checkpoint safe to write to the lifecycle journal and use after restart.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct BuildCheckpoint {
    identity: BuildIdentity,
    snapshot: BuildWatermark,
    target: BuildWatermark,
    covered: BuildWatermark,
    batch_limit: u64,
    phase: BuildPhase,
    visible: bool,
}

impl BuildCheckpoint {
    /// Returns the identity recorded by the checkpoint.
    pub(crate) const fn identity(self) -> BuildIdentity {
        self.identity
    }

    /// Returns the snapshot boundary from which backfill started.
    pub(crate) const fn snapshot(self) -> BuildWatermark {
        self.snapshot
    }

    /// Returns the target sequence captured by the checkpoint.
    pub(crate) const fn target(self) -> BuildWatermark {
        self.target
    }

    /// Returns the last sequence durably applied to the invisible index.
    pub(crate) const fn covered(self) -> BuildWatermark {
        self.covered
    }

    /// Returns the maximum number of journal entries per step.
    pub(crate) const fn batch_limit(self) -> u64 {
        self.batch_limit
    }

    /// Returns the durable build phase.
    pub(crate) const fn phase(self) -> BuildPhase {
        self.phase
    }

    /// Returns whether the checkpoint permits serving this index.
    pub(crate) const fn visible(self) -> bool {
        self.visible
    }
}

/// Proof that backfill and verification happened in one committed write wave.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct CatchUpReceipt {
    identity: BuildIdentity,
    through: BuildWatermark,
    transaction_id: u64,
}

impl CatchUpReceipt {
    /// Creates a receipt for an atomic catch-up transaction.
    pub(crate) const fn new(identity: BuildIdentity, through: BuildWatermark) -> Self {
        Self { identity, through, transaction_id: 0 }
    }

    /// Adds the durable transaction identifier to a receipt.
    pub(crate) const fn with_transaction_id(mut self, transaction_id: u64) -> Self {
        self.transaction_id = transaction_id;
        self
    }

    /// Returns the build identity verified by the transaction.
    pub(crate) const fn identity(self) -> BuildIdentity {
        self.identity
    }

    /// Returns the sequence through which the transaction caught up.
    pub(crate) const fn through(self) -> BuildWatermark {
        self.through
    }

    /// Returns the durable transaction identifier, when one was supplied.
    pub(crate) const fn transaction_id(self) -> u64 {
        self.transaction_id
    }
}

/// Transaction boundary used for the final same-transaction catch-up.
///
/// Implementations must extract acknowledged writes and maintain the index
/// before committing, then return a receipt only after verification succeeds.
pub(crate) trait CatchUpTransaction {
    /// Catches up from `covered` through at least `target` and verifies it
    /// before committing the transaction.
    fn catch_up_and_verify(
        &mut self,
        identity: BuildIdentity,
        covered: BuildWatermark,
        target: BuildWatermark,
    ) -> Result<CatchUpReceipt, BuildError>;
}

/// Port for an invisible, sequence-ordered SQLite index build.
pub(crate) trait InvisibleBuilder {
    /// Builds through `watermark` without exposing a partially built index.
    fn build_to(&mut self, watermark: BuildWatermark) -> Result<(), BuildError>;
}

/// Build refusal; the concrete SQLite error stays behind the port.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum BuildError {
    /// The requested sequence is older than or equal to the durable position.
    StaleWatermark,
    /// The build exceeded its write budget.
    BudgetExceeded,
    /// A sequence moved backwards.
    SequenceRegression { current: u64, requested: u64 },
    /// A journal range skipped one or more sequences.
    SequenceGap { expected: u64, received: u64 },
    /// A sequence calculation would wrap.
    SequenceOverflow,
    /// The batch was empty, reversed, or otherwise malformed.
    InvalidBatch,
    /// The persisted batch limit cannot make progress.
    InvalidBatchLimit,
    /// The requested sequence is past the build target.
    TargetExceeded { target: u64, requested: u64 },
    /// A checkpoint is internally inconsistent.
    InvalidCheckpoint,
    /// The schema generation does not match the build.
    SchemaMismatch { expected: u64, actual: u64 },
    /// The index-plan generation does not match the build.
    GenerationMismatch { expected: u64, actual: u64 },
    /// The index-plan fingerprint does not match the build.
    PlanMismatch { expected: u64, actual: u64 },
    /// Verification did not prove complete coverage.
    VerificationFailed,
    /// Activation was attempted before full verification.
    IndexBuilding,
    /// Activation must be performed by the same transaction that catches up.
    TransactionRequired,
    /// The build was already abandoned or cannot accept more work.
    InvalidPhase,
}

impl Default for BuildError {
    fn default() -> Self {
        Self::StaleWatermark
    }
}

/// A resumable, invisible build coordinator.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct InvisibleBuild {
    identity: BuildIdentity,
    snapshot: BuildWatermark,
    target: BuildWatermark,
    covered: BuildWatermark,
    batch_limit: u64,
    phase: BuildPhase,
    visible: bool,
}

impl InvisibleBuild {
    /// Starts a build at a durable snapshot and target sequence.
    pub(crate) fn new(
        identity: BuildIdentity,
        snapshot: BuildWatermark,
        target: BuildWatermark,
        batch_limit: u64,
    ) -> Result<Self, BuildError> {
        if batch_limit == 0 {
            return Err(BuildError::InvalidBatchLimit);
        }
        if target < snapshot {
            return Err(BuildError::SequenceRegression {
                current: snapshot.seq(),
                requested: target.seq(),
            });
        }
        Ok(Self {
            identity,
            snapshot,
            target,
            covered: snapshot,
            batch_limit,
            phase: BuildPhase::Building,
            visible: false,
        })
    }

    /// Restores a checkpoint only when its identity and invariants are valid.
    pub(crate) fn resume(
        checkpoint: BuildCheckpoint,
        expected: BuildIdentity,
    ) -> Result<Self, BuildError> {
        Self::check_identity(expected, checkpoint.identity)?;
        if checkpoint.batch_limit == 0
            || checkpoint.snapshot > checkpoint.target
            || checkpoint.covered < checkpoint.snapshot
            || checkpoint.covered > checkpoint.target
        {
            return Err(BuildError::InvalidCheckpoint);
        }
        let valid_visibility = match checkpoint.phase {
            BuildPhase::Active => checkpoint.visible,
            BuildPhase::Building | BuildPhase::Verified | BuildPhase::Abandoned => {
                !checkpoint.visible
            }
        };
        // A durable checkpoint is also a proof of the lifecycle transition:
        // Active and Verified may only be persisted after the complete target
        // is covered.  Without this check a corrupt/replayed checkpoint could
        // expose a partially built B-tree after restart.
        let valid_phase = match checkpoint.phase {
            BuildPhase::Verified | BuildPhase::Active => checkpoint.covered == checkpoint.target,
            BuildPhase::Building | BuildPhase::Abandoned => true,
        };
        if !valid_visibility || !valid_phase {
            return Err(BuildError::InvalidCheckpoint);
        }
        Ok(Self {
            identity: checkpoint.identity,
            snapshot: checkpoint.snapshot,
            target: checkpoint.target,
            covered: checkpoint.covered,
            batch_limit: checkpoint.batch_limit,
            phase: checkpoint.phase,
            visible: checkpoint.visible,
        })
    }

    /// Returns the current durable checkpoint.
    #[must_use]
    pub(crate) const fn checkpoint(&self) -> BuildCheckpoint {
        BuildCheckpoint {
            identity: self.identity,
            snapshot: self.snapshot,
            target: self.target,
            covered: self.covered,
            batch_limit: self.batch_limit,
            phase: self.phase,
            visible: self.visible,
        }
    }

    /// Returns the identity tied to this build.
    #[must_use]
    pub(crate) const fn identity(&self) -> BuildIdentity {
        self.identity
    }

    /// Returns the snapshot boundary.
    #[must_use]
    pub(crate) const fn snapshot(&self) -> BuildWatermark {
        self.snapshot
    }

    /// Returns the target sequence.
    #[must_use]
    pub(crate) const fn target(&self) -> BuildWatermark {
        self.target
    }

    /// Returns the sequence currently covered by the invisible index.
    #[must_use]
    pub(crate) const fn covered(&self) -> BuildWatermark {
        self.covered
    }

    /// Returns the durable phase.
    #[must_use]
    pub(crate) const fn phase(&self) -> BuildPhase {
        self.phase
    }

    /// Returns whether this build is visible to query planning.
    #[must_use]
    pub(crate) const fn is_visible(&self) -> bool {
        self.visible
    }

    /// Returns whether all sequences through the target have been applied.
    #[must_use]
    pub(crate) fn fully_covered(&self) -> bool {
        self.covered == self.target
    }

    /// Returns the safe query behavior for the current build state.
    #[must_use]
    pub(crate) fn availability(&self) -> BuildAvailability {
        if self.phase == BuildPhase::Active && self.visible {
            BuildAvailability::Active
        } else if self.phase == BuildPhase::Building && self.covered < self.target {
            BuildAvailability::Hybrid
        } else {
            BuildAvailability::IndexBuilding
        }
    }

    /// Returns whether the remaining build tail fits the caller's scan cap.
    ///
    /// This is the planner hook for ADR 0045 §3.6: a partial index is useful
    /// only when its uncovered suffix can be scanned in bounded work.  A
    /// verified-but-not-yet-published index deliberately remains unavailable;
    /// callers must complete [`Self::activate_in_transaction`] first.
    #[must_use]
    pub(crate) fn availability_with_scan_cap(&self, scan_cap: u64) -> BuildAvailability {
        if self.phase == BuildPhase::Active && self.visible {
            return BuildAvailability::Active;
        }
        if self.phase != BuildPhase::Building || self.covered >= self.target {
            return BuildAvailability::IndexBuilding;
        }
        let remaining = self.target.seq() - self.covered.seq();
        if remaining <= scan_cap {
            BuildAvailability::Hybrid
        } else {
            BuildAvailability::IndexBuilding
        }
    }

    /// Constructs the exact index-prefix/base-tail split for a hybrid read.
    ///
    /// `None` means the query must use a full scan (or wait for activation),
    /// not that the caller may use the partial index by itself.  The returned
    /// ranges are contiguous and disjoint: the scan always starts at
    /// `covered + 1` and includes every sequence through `target`.
    pub(crate) fn hybrid_plan(&self, scan_cap: u64) -> Option<HybridPlan> {
        if self.availability_with_scan_cap(scan_cap) != BuildAvailability::Hybrid {
            return None;
        }
        let scan_from = self.covered.checked_next().ok()?;
        Some(HybridPlan { index_through: self.covered, scan_from, scan_through: self.target })
    }

    /// Extends the target by one contiguous journal sequence.
    pub(crate) fn extend_target(&mut self, target: BuildWatermark) -> Result<(), BuildError> {
        if target <= self.target {
            return Err(BuildError::SequenceRegression {
                current: self.target.seq(),
                requested: target.seq(),
            });
        }
        let expected = self.target.checked_next()?;
        if target != expected {
            return Err(BuildError::SequenceGap {
                expected: expected.seq(),
                received: target.seq(),
            });
        }
        if self.phase != BuildPhase::Building {
            return Err(BuildError::InvalidPhase);
        }
        self.target = target;
        Ok(())
    }

    /// Applies one bounded, contiguous batch to the invisible index.
    pub(crate) fn apply_batch(&mut self, batch: BuildBatch) -> Result<(), BuildError> {
        if self.phase != BuildPhase::Building {
            return Err(BuildError::InvalidPhase);
        }
        // Fail closed on replay: a batch at or behind the durable position is
        // stale whatever its size, so report the watermark violation first and
        // never mask it as a budget or shape error.
        if batch.first() <= self.covered {
            return Err(BuildError::StaleWatermark);
        }
        if batch.items() > self.batch_limit {
            return Err(BuildError::BudgetExceeded);
        }
        let expected = self.covered.checked_next()?;
        if batch.first() != expected {
            return Err(BuildError::SequenceGap {
                expected: expected.seq(),
                received: batch.first().seq(),
            });
        }
        if batch.last() > self.target {
            return Err(BuildError::TargetExceeded {
                target: self.target.seq(),
                requested: batch.last().seq(),
            });
        }
        self.covered = batch.last();
        Ok(())
    }

    /// Applies a contiguous range after checking its item count and budget.
    pub(crate) fn advance_to(
        &mut self,
        last: BuildWatermark,
        items: u64,
    ) -> Result<(), BuildError> {
        // Monotonic advance: a target at or behind the durable position is a
        // stale replay. Reject it here so it reports StaleWatermark instead of
        // falling into BuildBatch::new's InvalidBatch (first > last).
        if last <= self.covered {
            return Err(BuildError::StaleWatermark);
        }
        // Check the budget before constructing the range.  The caller must
        // never be able to make an unbounded journal read merely by supplying
        // a malformed item count.
        if items > self.batch_limit {
            return Err(BuildError::BudgetExceeded);
        }
        let first = self.covered.checked_next()?;
        self.apply_batch(BuildBatch::new(first, last, items)?)
    }

    /// Marks complete coverage as verified while retaining invisibility.
    pub(crate) fn mark_verified(&mut self, watermark: BuildWatermark) -> Result<(), BuildError> {
        if self.phase != BuildPhase::Building {
            return Err(BuildError::InvalidPhase);
        }
        if watermark != self.target || self.covered != self.target {
            return Err(BuildError::VerificationFailed);
        }
        self.phase = BuildPhase::Verified;
        Ok(())
    }

    /// Performs same-transaction catch-up, verification, and atomic publish.
    pub(crate) fn activate_in_transaction<T: CatchUpTransaction>(
        &mut self,
        transaction: &mut T,
    ) -> Result<(), BuildError> {
        if self.phase != BuildPhase::Verified {
            return Err(BuildError::IndexBuilding);
        }
        let receipt = transaction.catch_up_and_verify(self.identity, self.covered, self.target)?;
        // A zero transaction id is the default value used by a receipt that
        // was not tied to a committed SQLite write wave.  Never publish on
        // that weaker proof, even if its watermark happens to be complete.
        if receipt.transaction_id() == 0 {
            return Err(BuildError::TransactionRequired);
        }
        Self::check_identity(self.identity, receipt.identity)?;
        if receipt.through < self.target {
            return Err(BuildError::SequenceRegression {
                current: self.target.seq(),
                requested: receipt.through.seq(),
            });
        }
        // The receipt is returned only after the transaction has committed
        // both extraction/index maintenance and verification. Publish the
        // visibility bit last, so a crash cannot expose a partial index.
        self.covered = receipt.through;
        self.target = receipt.through;
        self.phase = BuildPhase::Active;
        self.visible = true;
        Ok(())
    }

    /// Abandons the build without changing any previously visible index.
    pub(crate) fn abandon(&mut self) {
        self.phase = BuildPhase::Abandoned;
        self.visible = false;
    }

    fn check_identity(expected: BuildIdentity, actual: BuildIdentity) -> Result<(), BuildError> {
        if expected.schema_generation != actual.schema_generation {
            return Err(BuildError::SchemaMismatch {
                expected: expected.schema_generation,
                actual: actual.schema_generation,
            });
        }
        if expected.plan_generation != actual.plan_generation {
            return Err(BuildError::GenerationMismatch {
                expected: expected.plan_generation,
                actual: actual.plan_generation,
            });
        }
        if expected.plan_fingerprint != actual.plan_fingerprint {
            return Err(BuildError::PlanMismatch {
                expected: expected.plan_fingerprint,
                actual: actual.plan_fingerprint,
            });
        }
        Ok(())
    }
}

impl InvisibleBuilder for InvisibleBuild {
    fn build_to(&mut self, watermark: BuildWatermark) -> Result<(), BuildError> {
        if watermark <= self.covered {
            return Err(BuildError::StaleWatermark);
        }
        let items =
            watermark.seq().checked_sub(self.covered.seq()).ok_or(BuildError::SequenceOverflow)?;
        self.advance_to(watermark, items)
    }
}

#[cfg(test)]
mod tests {
    use super::{
        BuildAvailability, BuildBatch, BuildError, BuildIdentity, BuildPhase, BuildWatermark,
        CatchUpReceipt, CatchUpTransaction, InvisibleBuild, InvisibleBuilder,
    };

    #[derive(Default)]
    struct Transaction {
        fail: bool,
        calls: u8,
    }

    impl CatchUpTransaction for Transaction {
        fn catch_up_and_verify(
            &mut self,
            identity: BuildIdentity,
            _covered: BuildWatermark,
            target: BuildWatermark,
        ) -> Result<CatchUpReceipt, BuildError> {
            self.calls += 1;
            if self.fail {
                return Err(BuildError::VerificationFailed);
            }
            Ok(CatchUpReceipt::new(identity, target).with_transaction_id(7))
        }
    }

    fn build() -> InvisibleBuild {
        InvisibleBuild::new(
            BuildIdentity::with_fingerprint(3, 4, 5),
            BuildWatermark::new(10),
            BuildWatermark::new(15),
            2,
        )
        .expect("valid build")
    }

    #[test]
    fn restart_resumes_at_the_last_durable_watermark() {
        let mut original = build();
        original.advance_to(BuildWatermark::new(12), 2).expect("first batch");
        let checkpoint = original.checkpoint();
        let resumed = InvisibleBuild::resume(checkpoint, original.identity()).expect("resume");
        assert_eq!(resumed.covered(), BuildWatermark::new(12));
        assert_eq!(resumed.availability(), BuildAvailability::Hybrid);
        assert!(!resumed.is_visible());
    }

    #[test]
    fn replay_and_lower_watermarks_fail_closed() {
        let mut state = build();
        state.advance_to(BuildWatermark::new(12), 2).expect("first batch");
        assert_eq!(state.build_to(BuildWatermark::new(12)), Err(BuildError::StaleWatermark));
        assert_eq!(state.advance_to(BuildWatermark::new(11), 1), Err(BuildError::StaleWatermark));
    }

    #[test]
    fn sequence_gaps_and_budget_are_rejected_without_advancing() {
        let mut state = build();
        assert_eq!(
            state.apply_batch(
                BuildBatch::new(BuildWatermark::new(12), BuildWatermark::new(13), 2).unwrap()
            ),
            Err(BuildError::SequenceGap { expected: 11, received: 12 })
        );
        assert_eq!(state.covered(), BuildWatermark::new(10));
        assert_eq!(state.advance_to(BuildWatermark::new(13), 3), Err(BuildError::BudgetExceeded));
        assert_eq!(state.covered(), BuildWatermark::new(10));
    }

    #[test]
    fn generation_and_schema_mismatches_reject_restart() {
        let state = build();
        let checkpoint = state.checkpoint();
        assert!(matches!(
            InvisibleBuild::resume(checkpoint, BuildIdentity::with_fingerprint(2, 4, 5)),
            Err(BuildError::SchemaMismatch { .. })
        ));
        assert!(matches!(
            InvisibleBuild::resume(checkpoint, BuildIdentity::with_fingerprint(3, 9, 5)),
            Err(BuildError::GenerationMismatch { .. })
        ));
    }

    #[test]
    fn overflow_is_rejected() {
        assert_eq!(BuildWatermark::new(u64::MAX).checked_next(), Err(BuildError::SequenceOverflow));
        assert!(matches!(
            InvisibleBuild::new(
                BuildIdentity::new(1, 1),
                BuildWatermark::new(u64::MAX),
                BuildWatermark::new(u64::MAX),
                1,
            ),
            Ok(_)
        ));
    }

    #[test]
    fn visibility_waits_for_coverage_and_verification() {
        let mut state = build();
        assert_eq!(state.availability(), BuildAvailability::Hybrid);
        assert_eq!(
            state.mark_verified(BuildWatermark::new(10)),
            Err(BuildError::VerificationFailed)
        );
        state.advance_to(BuildWatermark::new(12), 2).expect("backfill");
        assert_eq!(
            state.mark_verified(BuildWatermark::new(12)),
            Err(BuildError::VerificationFailed)
        );
        // batch_limit=2: cover 13-14 then 15 instead of one 3-item advance.
        state.advance_to(BuildWatermark::new(14), 2).expect("backfill");
        state.advance_to(BuildWatermark::new(15), 1).expect("backfill");
        assert_eq!(state.availability(), BuildAvailability::IndexBuilding);
        state.mark_verified(BuildWatermark::new(15)).expect("verify");
        assert!(!state.is_visible());
    }

    #[test]
    fn same_transaction_catch_up_publishes_atomically() {
        let mut state = build();
        // batch_limit=2: backfill 11-12, 13-14, 15 in three bounded steps.
        state.advance_to(BuildWatermark::new(12), 2).expect("backfill");
        state.advance_to(BuildWatermark::new(14), 2).expect("backfill");
        state.advance_to(BuildWatermark::new(15), 1).expect("backfill");
        state.mark_verified(BuildWatermark::new(15)).expect("verify");
        let mut transaction = Transaction::default();
        state.activate_in_transaction(&mut transaction).expect("activate");
        assert_eq!(transaction.calls, 1);
        assert_eq!(state.phase(), BuildPhase::Active);
        assert!(state.is_visible());
        assert_eq!(state.availability(), BuildAvailability::Active);
    }

    #[test]
    fn failed_transaction_leaves_prior_visibility_unchanged() {
        let mut state = build();
        // batch_limit=2: backfill 11-12, 13-14, 15 in three bounded steps.
        state.advance_to(BuildWatermark::new(12), 2).expect("backfill");
        state.advance_to(BuildWatermark::new(14), 2).expect("backfill");
        state.advance_to(BuildWatermark::new(15), 1).expect("backfill");
        state.mark_verified(BuildWatermark::new(15)).expect("verify");
        let mut transaction = Transaction { fail: true, calls: 0 };
        assert_eq!(
            state.activate_in_transaction(&mut transaction),
            Err(BuildError::VerificationFailed)
        );
        assert_eq!(state.phase(), BuildPhase::Verified);
        assert!(!state.is_visible());
    }

    #[test]
    fn incomplete_build_cannot_activate() {
        let mut state = build();
        let mut transaction = Transaction::default();
        assert_eq!(state.activate_in_transaction(&mut transaction), Err(BuildError::IndexBuilding));
        assert!(!state.is_visible());
    }

    #[test]
    fn hybrid_hook_returns_disjoint_bounded_ranges() {
        let mut state = build();
        state.advance_to(BuildWatermark::new(12), 2).expect("backfill");
        assert_eq!(
            state.availability_with_scan_cap(2),
            BuildAvailability::IndexBuilding,
            "the tail has three entries and cannot fit a two-entry scan cap"
        );
        assert_eq!(state.availability_with_scan_cap(3), BuildAvailability::Hybrid);
        let plan = state.hybrid_plan(3).expect("bounded hybrid plan");
        assert_eq!(plan.index_through(), BuildWatermark::new(12));
        assert_eq!(plan.scan_from(), BuildWatermark::new(13));
        assert_eq!(plan.scan_through(), BuildWatermark::new(15));
        assert_eq!(plan.scan_items(), 3);
    }

    #[test]
    fn abandoned_and_verified_builds_never_offer_a_hybrid_plan() {
        let mut abandoned = build();
        abandoned.abandon();
        assert_eq!(abandoned.availability(), BuildAvailability::IndexBuilding);
        assert_eq!(abandoned.hybrid_plan(u64::MAX), None);

        let mut verified = build();
        verified.advance_to(BuildWatermark::new(12), 2).expect("backfill");
        verified.advance_to(BuildWatermark::new(14), 2).expect("backfill");
        verified.advance_to(BuildWatermark::new(15), 1).expect("backfill");
        verified.mark_verified(BuildWatermark::new(15)).expect("verify");
        assert_eq!(verified.availability(), BuildAvailability::IndexBuilding);
        assert_eq!(verified.hybrid_plan(u64::MAX), None);
    }

    #[test]
    fn incomplete_active_checkpoint_is_rejected_on_restart() {
        let mut checkpoint = build().checkpoint();
        checkpoint.phase = BuildPhase::Active;
        checkpoint.visible = true;
        assert_eq!(
            InvisibleBuild::resume(checkpoint, checkpoint.identity()),
            Err(BuildError::InvalidCheckpoint)
        );
    }

    #[test]
    fn activation_requires_a_durable_transaction_receipt() {
        struct Uncommitted;
        impl CatchUpTransaction for Uncommitted {
            fn catch_up_and_verify(
                &mut self,
                identity: BuildIdentity,
                _covered: BuildWatermark,
                target: BuildWatermark,
            ) -> Result<CatchUpReceipt, BuildError> {
                Ok(CatchUpReceipt::new(identity, target))
            }
        }

        let mut state = build();
        state.advance_to(BuildWatermark::new(12), 2).expect("backfill");
        state.advance_to(BuildWatermark::new(14), 2).expect("backfill");
        state.advance_to(BuildWatermark::new(15), 1).expect("backfill");
        state.mark_verified(BuildWatermark::new(15)).expect("verify");
        assert_eq!(
            state.activate_in_transaction(&mut Uncommitted),
            Err(BuildError::TransactionRequired)
        );
        assert_eq!(state.phase(), BuildPhase::Verified);
        assert!(!state.is_visible());
    }
}
