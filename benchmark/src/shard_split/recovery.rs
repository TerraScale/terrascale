//! Restart recovery and pre-cutover rollback contracts.

use super::{DurableSplitState, SplitPhase};

/// Recovery work selected from a durable split phase.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RecoveryAction {
    /// Re-open/create the target and resume range copy.
    ResumeBackfill,
    /// Resume journal tailing from the persisted target checkpoint.
    ResumeCatchUp,
    /// Re-verify a route that was already cut over.
    VerifyCutOver,
    /// Remove source data after successful verification.
    CleanupSource,
    /// No work remains for an aborted or complete split.
    Noop,
}

/// Recovery decision with the state that drove it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RecoveryDecision {
    action: RecoveryAction,
    phase: SplitPhase,
}

impl RecoveryDecision {
    /// Returns the selected action.
    #[must_use]
    pub const fn action(&self) -> RecoveryAction {
        self.action
    }

    /// Returns the persisted phase.
    #[must_use]
    pub const fn phase(&self) -> SplitPhase {
        self.phase
    }
}

/// Failure while constructing or applying recovery work.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RecoveryError {
    /// Rollback is unsafe after routing moved.
    CutoverAlreadyInstalled,
    /// No durable record exists to recover.
    MissingState,
}

/// Recovery planner for one active split.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct RecoveryPlan;

impl RecoveryPlan {
    /// Chooses idempotent work for a durable state snapshot.
    #[must_use]
    pub fn decide(state: &DurableSplitState) -> RecoveryDecision {
        let action = match state.phase() {
            SplitPhase::Planned | SplitPhase::Backfilling => RecoveryAction::ResumeBackfill,
            SplitPhase::CatchingUp => RecoveryAction::ResumeCatchUp,
            SplitPhase::CutOver => RecoveryAction::VerifyCutOver,
            SplitPhase::Complete | SplitPhase::Aborted => RecoveryAction::Noop,
        };
        RecoveryDecision { action, phase: state.phase() }
    }

    /// Returns an action that safely removes a pre-cutover target.
    ///
    /// # Errors
    ///
    /// Returns [`RecoveryError::CutoverAlreadyInstalled`] when the persisted
    /// phase is [`SplitPhase::CutOver`] or [`SplitPhase::Complete`].
    pub fn rollback(state: &DurableSplitState) -> Result<RecoveryAction, RecoveryError> {
        match state.phase() {
            SplitPhase::Planned
            | SplitPhase::Backfilling
            | SplitPhase::CatchingUp
            | SplitPhase::Aborted => Ok(RecoveryAction::Noop),
            SplitPhase::CutOver | SplitPhase::Complete => {
                Err(RecoveryError::CutoverAlreadyInstalled)
            }
        }
    }
}
