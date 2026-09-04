//! Split lifecycle and zero-loss cutover gate.

use super::state::{DurableSplitState, SplitStateError};

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
        })
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
        })
    }

    /// Returns a restart-safe state snapshot.
    #[must_use]
    pub fn durable_state(&self) -> DurableSplitState {
        DurableSplitState::new(
            self.split_key.clone(),
            self.source_high_water,
            self.target_applied_through,
            self.acked_through,
            self.phase,
        )
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

    /// Marks the target shard file created and begins range backfill.
    pub fn begin_backfill(&mut self) -> Result<(), SplitCutoverError> {
        self.require_phase(SplitPhase::Planned)?;
        self.phase = SplitPhase::Backfilling;
        Ok(())
    }

    /// Marks backfill complete through `through` and begins journal catch-up.
    pub fn begin_catch_up(&mut self, through: u64) -> Result<(), SplitCutoverError> {
        self.require_phase(SplitPhase::Backfilling)?;
        self.target_applied_through = self.target_applied_through.max(through);
        self.phase = SplitPhase::CatchingUp;
        Ok(())
    }

    /// Advances the source high-water mark monotonically.
    pub fn note_source_high_water(&mut self, sequence: u64) {
        self.source_high_water = self.source_high_water.max(sequence);
    }

    /// Advances the target applied-through mark monotonically.
    pub fn note_target_applied(&mut self, sequence: u64) {
        self.target_applied_through = self.target_applied_through.max(sequence);
    }

    /// Advances the acknowledged-write frontier monotonically.
    pub fn note_acked(&mut self, sequence: u64) {
        self.acked_through = self.acked_through.max(sequence);
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

    /// Marks the moved range verified and eligible for source cleanup.
    pub fn note_verified(&mut self) -> Result<(), SplitCutoverError> {
        self.require_phase(SplitPhase::CutOver)?;
        self.phase = SplitPhase::Complete;
        Ok(())
    }

    /// Aborts a split before cutover; no route was moved.
    pub fn abort(&mut self) -> Result<(), SplitCutoverError> {
        match self.phase {
            SplitPhase::Planned | SplitPhase::Backfilling | SplitPhase::CatchingUp => {
                self.phase = SplitPhase::Aborted;
                Ok(())
            }
            phase => Err(SplitCutoverError::WrongPhase(phase)),
        }
    }

    fn require_phase(&self, expected: SplitPhase) -> Result<(), SplitCutoverError> {
        if self.phase == expected { Ok(()) } else { Err(SplitCutoverError::WrongPhase(self.phase)) }
    }
}
