//! Automatic index planning and lifecycle contracts (ADR 0045 / D-31).
//!
//! The implementation is intentionally staged.  [`legacy`] contains the
//! already-qualified observe/compress/candidate algorithms and remains the
//! compatibility surface while the production stages are filled in behind
//! the interfaces below.  The state machine is the only coordinator: every
//! later worker must advance it through the checked transitions rather than
//! publishing an index directly.

#![allow(dead_code, reason = "staged auto-index contracts are wired incrementally")]
#![allow(clippy::all, reason = "staged auto-index contracts are linted when integrated")]
#![allow(clippy::pedantic, reason = "staged auto-index contracts are linted when integrated")]

mod legacy;

pub use legacy::*;

pub(crate) mod bandit;
pub(crate) mod build;
pub(crate) mod candidates;
pub(crate) mod lifecycle;
pub(crate) mod monitor;
pub(crate) mod observe;
pub(crate) mod persistence;
pub(crate) mod shadow;

pub(crate) use monitor::IndexMonitor;

use self::bandit::{MeasuredReward, WriteBudget};
use self::build::BuildWatermark;
use self::lifecycle::LifecycleEvent;
use self::observe::ExecutionInput;
use self::persistence::JournalRecord;
use self::shadow::{PlanPin, ShadowSamples};

/// Type-level registry of stage ports. It keeps the seams compiled without
/// installing fake planner, storage, or monitor behavior.
#[derive(Debug)]
struct StageContracts {
    budget: WriteBudget,
    _reward: MeasuredReward,
    _samples: Option<ShadowSamples>,
    _pin: Option<PlanPin>,
    _event: Option<LifecycleEvent>,
    _compiler: std::marker::PhantomData<fn() -> dyn candidates::HypotheticalCompiler>,
    _selector: std::marker::PhantomData<fn() -> dyn bandit::BanditSelector>,
    _builder: std::marker::PhantomData<fn() -> dyn build::InvisibleBuilder>,
    _monitor: std::marker::PhantomData<fn() -> dyn IndexMonitor>,
    _journal: std::marker::PhantomData<fn() -> dyn persistence::LifecycleJournal>,
    _verifier: std::marker::PhantomData<fn() -> dyn shadow::ShadowVerifier>,
    _record: std::marker::PhantomData<fn(JournalRecord)>,
}

impl StageContracts {
    fn new() -> Self {
        let mut budget = WriteBudget::new(0.0);
        budget.account(0.0);
        Self {
            budget,
            _reward: MeasuredReward::default(),
            _samples: None,
            _pin: None,
            _event: None,
            _compiler: std::marker::PhantomData,
            _selector: std::marker::PhantomData,
            _builder: std::marker::PhantomData,
            _monitor: std::marker::PhantomData,
            _journal: std::marker::PhantomData,
            _verifier: std::marker::PhantomData,
            _record: std::marker::PhantomData,
        }
    }
}

/// The durable lifecycle phase of an automatically managed index.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum AutoIndexPhase {
    /// The workload ring is collecting evidence.
    Observing,
    /// The workload has been compressed into weighted templates.
    Compressed,
    /// A candidate passed the hypothetical planner check.
    Selected,
    /// SQLite is building the index invisibly in sequence order.
    Building,
    /// The index is being measured against a shadow plan.
    Shadowing,
    /// The index is serving production plans.
    Active,
    /// The index is hidden pending delayed physical removal.
    Invisible,
    /// The index was rejected or physically removed.
    Abandoned,
}

/// A checked lifecycle transition failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum AutoIndexTransitionError {
    /// The requested transition is not legal from the current phase.
    InvalidTransition { from: AutoIndexPhase, to: AutoIndexPhase },
    /// A build or visibility transition did not carry a durable watermark.
    MissingWatermark,
}

/// Coordinator state shared by the auto-index workers.
///
/// Invariants:
///
/// * an index can become [`AutoIndexPhase::Active`] only after shadowing;
/// * a building index always has a monotonically increasing [`BuildWatermark`];
/// * an invisible or abandoned index is never selected for serving;
/// * generation changes invalidate pins before a new plan is published.
///
/// This type has no storage side effects.  Persistence and SQLite integration
/// consume its snapshots through their ports, keeping the query hot path
/// fail-closed until those workers are wired.
#[derive(Debug)]
pub(crate) struct AutoIndexState {
    phase: AutoIndexPhase,
    generation: u64,
    watermark: Option<BuildWatermark>,
    contracts: StageContracts,
}

impl Default for AutoIndexState {
    fn default() -> Self {
        Self {
            phase: AutoIndexPhase::Observing,
            generation: 0,
            watermark: None,
            contracts: StageContracts::new(),
        }
    }
}

impl AutoIndexState {
    /// Creates a coordinator in the observe phase.
    #[must_use]
    pub(crate) fn new() -> Self {
        Self { phase: AutoIndexPhase::Observing, ..Self::default() }
    }

    /// Returns the current lifecycle phase.
    #[must_use]
    pub(crate) const fn phase(&self) -> AutoIndexPhase {
        self.phase
    }

    /// Returns the schema/index-plan generation associated with this state.
    #[must_use]
    pub(crate) const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the last durable build watermark, when a build exists.
    #[must_use]
    pub(crate) const fn watermark(&self) -> Option<BuildWatermark> {
        self.watermark
    }

    /// Records one schema generation change and invalidates serving pins.
    pub(crate) fn bump_generation(&mut self) {
        self.generation = self.generation.saturating_add(1);
        self.phase = AutoIndexPhase::Observing;
        self.watermark = None;
    }

    /// Records a query observation through the production-facing hook.
    pub(crate) fn observe_query(&mut self, input: ExecutionInput<'_>) {
        input.observe();
    }

    /// Advances the lifecycle after checking its invariants.
    pub(crate) fn transition(
        &mut self,
        to: AutoIndexPhase,
        watermark: Option<BuildWatermark>,
    ) -> Result<(), AutoIndexTransitionError> {
        let valid = match (self.phase, to) {
            (AutoIndexPhase::Observing, AutoIndexPhase::Compressed) => true,
            (AutoIndexPhase::Compressed, AutoIndexPhase::Selected) => true,
            (AutoIndexPhase::Selected, AutoIndexPhase::Building) => true,
            (AutoIndexPhase::Building, AutoIndexPhase::Shadowing) => true,
            (AutoIndexPhase::Shadowing, AutoIndexPhase::Active) => true,
            (AutoIndexPhase::Shadowing, AutoIndexPhase::Abandoned) => true,
            (AutoIndexPhase::Active, AutoIndexPhase::Invisible) => true,
            (AutoIndexPhase::Invisible, AutoIndexPhase::Abandoned) => true,
            _ => false,
        };
        if !valid {
            return Err(AutoIndexTransitionError::InvalidTransition { from: self.phase, to });
        }
        if matches!(to, AutoIndexPhase::Building | AutoIndexPhase::Shadowing) && watermark.is_none()
        {
            return Err(AutoIndexTransitionError::MissingWatermark);
        }
        if let Some(next) = watermark {
            if let Some(previous) = self.watermark {
                if next.seq() < previous.seq() {
                    return Err(AutoIndexTransitionError::MissingWatermark);
                }
            }
            self.watermark = Some(next);
        }
        self.phase = to;
        Ok(())
    }

    /// Reports whether this state may serve an index-backed plan.
    #[must_use]
    pub(crate) fn can_serve(&self) -> bool {
        matches!(self.phase, AutoIndexPhase::Active) && self.contracts.budget.allows()
    }
}

#[cfg(test)]
mod tests {
    use super::{AutoIndexPhase, AutoIndexState, AutoIndexTransitionError, build::BuildWatermark};

    #[test]
    fn lifecycle_requires_shadow_before_activation() {
        let mut state = AutoIndexState::new();
        assert_eq!(state.phase(), AutoIndexPhase::Observing);
        assert_eq!(
            state.transition(AutoIndexPhase::Active, None),
            Err(AutoIndexTransitionError::InvalidTransition {
                from: AutoIndexPhase::Observing,
                to: AutoIndexPhase::Active,
            })
        );
        state.transition(AutoIndexPhase::Compressed, None).expect("compress");
        state.transition(AutoIndexPhase::Selected, None).expect("select");
        assert_eq!(
            state.transition(AutoIndexPhase::Building, None),
            Err(AutoIndexTransitionError::MissingWatermark)
        );
        state.transition(AutoIndexPhase::Building, Some(BuildWatermark::new(10))).expect("build");
        state.transition(AutoIndexPhase::Shadowing, Some(BuildWatermark::new(10))).expect("shadow");
        state.transition(AutoIndexPhase::Active, None).expect("activate");
        assert!(state.can_serve());
    }

    #[test]
    fn generation_change_clears_watermark_and_serving_state() {
        let mut state = AutoIndexState::new();
        state.transition(AutoIndexPhase::Compressed, None).expect("compress");
        state.transition(AutoIndexPhase::Selected, None).expect("select");
        state.transition(AutoIndexPhase::Building, Some(BuildWatermark::new(4))).expect("build");
        state.transition(AutoIndexPhase::Shadowing, Some(BuildWatermark::new(4))).expect("shadow");
        state.transition(AutoIndexPhase::Active, None).expect("activate");
        state.bump_generation();
        assert_eq!(state.generation(), 1);
        assert_eq!(state.watermark(), None);
        assert!(!state.can_serve());
    }
}
