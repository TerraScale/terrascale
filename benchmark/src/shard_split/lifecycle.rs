//! Split lifecycle and zero-loss cutover gate.

use super::{
    CutoverPermit, WriterFence,
    state::{DurableSplitState, SplitStateError},
};

/// One key-range split's lifecycle phase.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum SplitPhase {
    /// Planned: the shard file is not yet created.
    Planned,
    /// The shard file exists and range backfill is running.
    Backfilling,
    /// Backfill is complete and the journal tail is being applied.
    CatchingUp,
    /// Routing is on the target and the moved range is being verified.
    CutOver,
    /// Verification succeeded and the source range was removed.
    Complete,
    /// The split was aborted before routing moved.
    Aborted,
}

/// Why a split plan was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SplitPlanError {
    /// The split key was empty.
    EmptySplitKey,
}

/// Why a lifecycle transition was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SplitCutoverError {
    /// The target trails the source or acknowledged frontier.
    NotCaughtUp {
        /// Highest sequence durably applied by the target.
        target_applied: u64,
        /// Highest sequence required before cutover.
        required: u64,
    },
    /// A route cutover must carry a permit produced by the writer fence.
    InvalidFencePermit,
    /// The transition is not valid in the current phase.
    WrongPhase(SplitPhase),
}

/// One online key-range split's durable lifecycle and cutover gate.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardSplit {
    split_key: Vec<u8>,
    source_high_water: u64,
    target_applied_through: u64,
    acked_through: u64,
    phase: SplitPhase,
    context: Option<super::SplitContext>,
}

impl ShardSplit {
    /// Plans a split moving keys below the upper-exclusive `split_key`.
    pub fn plan(split_key: Vec<u8>) -> Result<Self, SplitPlanError> {
        if split_key.is_empty() {
            return Err(SplitPlanError::EmptySplitKey);
        }
        Ok(Self {
            split_key,
            source_high_water: 0,
            target_applied_through: 0,
            acked_through: 0,
            phase: SplitPhase::Planned,
            context: None,
        })
    }

    /// Plans a split with the identity and placement context required for a
    /// restart-safe orchestration attempt.
    pub fn plan_with_context(
        split_key: Vec<u8>,
        context: super::SplitContext,
    ) -> Result<Self, SplitPlanError> {
        let mut split = Self::plan(split_key)?;
        split.context = Some(context);
        Ok(split)
    }

    /// Restores a split from its durable state.
    pub fn from_durable_state(state: DurableSplitState) -> Result<Self, SplitStateError> {
        if state.split_key().is_empty() {
            return Err(SplitStateError::EmptySplitKey);
        }
        Ok(Self {
            split_key: state.split_key().to_vec(),
            source_high_water: state.source_high_water(),
            target_applied_through: state.target_applied_through(),
            acked_through: state.acked_through(),
            phase: state.phase(),
            context: state.context(),
        })
    }

    /// Returns a restart-safe state snapshot.
    #[must_use]
    pub fn durable_state(&self) -> DurableSplitState {
        let state = DurableSplitState::new(
            self.split_key.clone(),
            self.source_high_water,
            self.target_applied_through,
            self.acked_through,
            self.phase,
        );
        self.context.map_or(state, |context| state.with_context(context))
    }

    /// Returns the moved range's upper-exclusive bound.
    #[must_use]
    pub fn split_key(&self) -> &[u8] {
        &self.split_key
    }

    /// Returns the current lifecycle phase.
    #[must_use]
    pub const fn phase(&self) -> SplitPhase {
        self.phase
    }

    /// Returns the latest source journal high-water mark.
    #[must_use]
    pub const fn source_high_water(&self) -> u64 {
        self.source_high_water
    }

    /// Returns the target's applied-through mark.
    #[must_use]
    pub const fn target_applied_through(&self) -> u64 {
        self.target_applied_through
    }

    /// Returns the highest acknowledged write sequence.
    #[must_use]
    pub const fn acked_through(&self) -> u64 {
        self.acked_through
    }

    /// Returns the identity, session frontier, and placement context.
    #[must_use]
    pub const fn context(&self) -> Option<super::SplitContext> {
        self.context
    }

    /// Marks the target shard file created and begins range backfill.
    pub fn begin_backfill(&mut self) -> Result<(), SplitCutoverError> {
        match self.phase {
            SplitPhase::Planned => {
                self.phase = SplitPhase::Backfilling;
                Ok(())
            }
            // Recovery may replay the operation after the durable transition
            // was committed but before the worker observed its response.
            SplitPhase::Backfilling => Ok(()),
            _ => Err(SplitCutoverError::WrongPhase(self.phase)),
        }
    }

    /// Marks backfill complete through `through` and begins journal catch-up.
    pub fn begin_catch_up(&mut self, through: u64) -> Result<(), SplitCutoverError> {
        match self.phase {
            SplitPhase::Backfilling => {
                self.target_applied_through = self.target_applied_through.max(through);
                self.phase = SplitPhase::CatchingUp;
                Ok(())
            }
            // The catch-up start is also a retry-safe checkpoint update.
            SplitPhase::CatchingUp => {
                self.target_applied_through = self.target_applied_through.max(through);
                Ok(())
            }
            _ => Err(SplitCutoverError::WrongPhase(self.phase)),
        }
    }

    /// Advances the source high-water mark monotonically.
    pub fn note_source_high_water(&mut self, sequence: u64) {
        if matches!(
            self.phase,
            SplitPhase::Planned | SplitPhase::Backfilling | SplitPhase::CatchingUp
        ) {
            self.source_high_water = self.source_high_water.max(sequence);
        }
    }

    /// Advances the target applied-through mark monotonically.
    pub fn note_target_applied(&mut self, sequence: u64) {
        if matches!(self.phase, SplitPhase::Backfilling | SplitPhase::CatchingUp) {
            self.target_applied_through = self.target_applied_through.max(sequence);
        }
    }

    /// Advances the acknowledged-write frontier monotonically.
    pub fn note_acked(&mut self, sequence: u64) {
        if matches!(
            self.phase,
            SplitPhase::Planned | SplitPhase::Backfilling | SplitPhase::CatchingUp
        ) {
            self.acked_through = self.acked_through.max(sequence);
        }
    }

    /// Reports whether source and acknowledged writes are fully applied.
    #[must_use]
    pub const fn can_cut_over(&self) -> bool {
        matches!(self.phase, SplitPhase::CatchingUp)
            && self.target_applied_through >= self.source_high_water
            && self.target_applied_through >= self.acked_through
    }

    /// Moves routing to the target, only after the zero-loss gate passes.
    pub fn cut_over(&mut self) -> Result<(), SplitCutoverError> {
        if self.phase == SplitPhase::CutOver {
            return Ok(());
        }
        self.require_phase(SplitPhase::CatchingUp)?;
        let required = self.source_high_water.max(self.acked_through);
        if self.target_applied_through < required {
            return Err(SplitCutoverError::NotCaughtUp {
                target_applied: self.target_applied_through,
                required,
            });
        }
        self.phase = SplitPhase::CutOver;
        Ok(())
    }

    /// Moves the lifecycle to cut-over using a writer-fence permit.
    ///
    /// The permit is checked before changing any state.  The plain
    /// [`Self::cut_over`] method remains available for owners that perform
    /// the equivalent fence and route replacement in one storage transaction;
    /// this method is the explicit, easy-to-audit form for that path.
    pub fn cut_over_with_permit(&mut self, permit: CutoverPermit) -> Result<(), SplitCutoverError> {
        if !permit.is_valid() {
            return Err(SplitCutoverError::InvalidFencePermit);
        }
        let required = self.source_high_water.max(self.acked_through);
        if permit.required() < required {
            return Err(SplitCutoverError::NotCaughtUp {
                target_applied: self.target_applied_through,
                required,
            });
        }
        self.cut_over()
    }

    /// Moves to cut-over only when the permit belongs to `fence`.
    ///
    /// A permit is intentionally opaque, but accepting a permit from an
    /// unrelated fence would let a concurrent split move a route.  Callers
    /// that have a writer fence should use this method; the permit-only
    /// method remains for storage adapters that perform both checks in one
    /// transaction.
    pub fn cut_over_with_fence(
        &mut self,
        fence: &WriterFence,
        permit: CutoverPermit,
    ) -> Result<(), SplitCutoverError> {
        if !fence.permits(permit) {
            return Err(SplitCutoverError::InvalidFencePermit);
        }
        self.cut_over_with_permit(permit)
    }

    /// Marks the moved range verified and eligible for source cleanup.
    pub fn note_verified(&mut self) -> Result<(), SplitCutoverError> {
        match self.phase {
            SplitPhase::CutOver => {
                self.phase = SplitPhase::Complete;
                Ok(())
            }
            // Verification and source cleanup are commonly retried after a
            // process restart.  Completion is terminal and idempotent.
            SplitPhase::Complete => Ok(()),
            _ => Err(SplitCutoverError::WrongPhase(self.phase)),
        }
    }

    /// Aborts a split before cutover; no route was moved.
    pub fn abort(&mut self) -> Result<(), SplitCutoverError> {
        match self.phase {
            SplitPhase::Planned | SplitPhase::Backfilling | SplitPhase::CatchingUp => {
                self.phase = SplitPhase::Aborted;
                Ok(())
            }
            SplitPhase::Aborted => Ok(()),
            phase => Err(SplitCutoverError::WrongPhase(phase)),
        }
    }

    fn require_phase(&self, expected: SplitPhase) -> Result<(), SplitCutoverError> {
        if self.phase == expected { Ok(()) } else { Err(SplitCutoverError::WrongPhase(self.phase)) }
    }
}
