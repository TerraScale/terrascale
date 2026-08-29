//! A small bounded-exhaustive state-machine checker for the cluster
//! model lanes (qualification gap-sweep rows 15/19; GOAL.md
//! "Correctness and simulation" requires model-based or formal
//! state-machine checking for membership and authorization-generation
//! transitions in addition to ordinary tests).
//!
//! The checker is deliberately tiny and dependency-free so the exact
//! same source compiles twice: once as its own integration-test crate
//! (the self-tests at the bottom of this file) and once as a module of
//! another integration test — `membership_model.rs` includes it with
//! `#[path = "model_checker.rs"]`. One implementation, two compilation
//! units; the self-tests below run in both, which is harmless (they
//! are deterministic toy models).
//!
//! Semantics, stated exactly:
//!
//! - Exploration is a depth-first walk over the model's transition
//!   relation, memoized by the canonical state key. A state first
//!   reached at some depth is expanded exactly once. This is sound for
//!   invariant checking because both `steps` and the invariants are
//!   pure functions of the state: any step suffix available from a
//!   later, deeper occurrence of the same key was already explored
//!   from the earliest occurrence.
//! - A state invariant is checked once per distinct state (the initial
//!   state included). A transition invariant is checked for every
//!   fired transition, including transitions that land in
//!   already-visited states — refusals that leave the state unchanged
//!   are still transitions and are still checked.
//! - Bounds are honest: if the depth bound or the distinct-state cap
//!   stopped the walk from trying every offered step, the report says
//!   `truncated: true`. A green report with `truncated: true` means
//!   "no violation within the explored prefix", never "exhaustive".

use std::collections::HashSet;
use std::fmt;

/// Bounds for one exploration.
#[derive(Clone, Copy, Debug)]
pub struct Bounds {
    /// Maximum number of steps from the initial state.
    pub max_depth: usize,
    /// Maximum number of distinct states visited; beyond it the walk
    /// stops descending and the report is marked truncated.
    pub max_states: usize,
}

/// A bounded state machine to explore exhaustively.
pub trait BoundedModel {
    /// The explored value. Must be cheaply describable via [`Self::key`].
    type State;
    /// One offered transition. Must be `Clone` because the checker
    /// renders counterexample traces from retained steps.
    type Step: Clone + fmt::Debug;

    /// The state exploration starts from.
    fn initial(&self) -> Self::State;
    /// Every step offered in `state`, including steps the system under
    /// test will refuse — refusals are part of the contract.
    fn steps(&self, state: &Self::State) -> Vec<Self::Step>;
    /// Applies one step and returns the successor state.
    fn apply(&self, state: &Self::State, step: &Self::Step) -> Self::State;
    /// Canonical identity for memoization. Two states with equal keys
    /// must behave identically under every future step, or the walk is
    /// unsound. Observational scratch fields must be excluded.
    fn key(&self, state: &Self::State) -> String;
    /// State invariant, checked once per distinct state.
    fn check_state(&self, state: &Self::State) -> Result<(), String>;
    /// Transition invariant, checked for every fired transition.
    fn check_transition(
        &self,
        _before: &Self::State,
        _step: &Self::Step,
        _after: &Self::State,
    ) -> Result<(), String> {
        Ok(())
    }
}

/// Coverage figures for one completed exploration.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Report {
    /// Distinct states visited (initial included).
    pub distinct_states: usize,
    /// Transitions fired, including ones into already-visited states.
    pub transitions_fired: usize,
    /// Deepest transition fired, in steps from the initial state.
    pub max_depth_reached: usize,
    /// True when a bound stopped the walk while steps were still
    /// offerable — the exploration is a prefix, not the whole space.
    /// With unbounded counter models this is expected: the claim is
    /// exhaustive over all traces up to `max_depth`, not closure.
    pub truncated: bool,
    /// True when the distinct-state cap (not the depth bound) cut the
    /// walk: some trace of length <= max_depth was NOT explored. A
    /// sweep claiming exhaustiveness to its depth must not set this.
    pub hit_state_cap: bool,
}

/// A checked violation with the exact step trace that reaches it.
pub struct Violation<M: BoundedModel> {
    /// The steps from the initial state, in order.
    pub trace: Vec<M::Step>,
    /// The canonical key of the state the violation was found in.
    pub state_key: String,
    /// What invariant failed.
    pub message: String,
}

impl<M: BoundedModel> fmt::Debug for Violation<M> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "model violation in state {}: {} (trace: {:?})",
            self.state_key, self.message, self.trace
        )
    }
}

struct Frame<M: BoundedModel> {
    state: M::State,
    steps: Vec<M::Step>,
    next: usize,
}

/// Explores `model` within `bounds`, checking every invariant.
///
/// # Errors
///
/// Returns the first [`Violation`] with a full reproduction trace.
pub fn explore<M: BoundedModel>(model: &M, bounds: Bounds) -> Result<Report, Violation<M>> {
    let initial = model.initial();
    let initial_key = model.key(&initial);
    if let Err(message) = model.check_state(&initial) {
        return Err(Violation { trace: Vec::new(), state_key: initial_key, message });
    }
    let mut visited = HashSet::from([initial_key]);
    let mut report = Report { distinct_states: 1, ..Report::default() };
    let mut path: Vec<M::Step> = Vec::new();
    let root_steps = model.steps(&initial);
    let mut stack: Vec<Frame<M>> = vec![Frame { state: initial, steps: root_steps, next: 0 }];

    while let Some(frame) = stack.last_mut() {
        if frame.next >= frame.steps.len() {
            stack.pop();
            path.pop();
            continue;
        }
        let step = frame.steps[frame.next].clone();
        frame.next += 1;
        let after = model.apply(&frame.state, &step);
        report.transitions_fired += 1;
        if let Err(message) = model.check_transition(&frame.state, &step, &after) {
            let mut trace = path.clone();
            trace.push(step);
            return Err(Violation { trace, state_key: model.key(&after), message });
        }
        // The frame borrow ends above; the depth of this transition is
        // the stack height it was fired from (root fires at depth 1).
        let depth = stack.len();
        report.max_depth_reached = report.max_depth_reached.max(depth);

        let key = model.key(&after);
        if !visited.insert(key.clone()) {
            continue;
        }
        // Exact state cap: a state past the cap has had its incoming
        // transition checked, but is neither counted nor expanded —
        // and the report says the walk was truncated by the CAP, not
        // merely by the depth bound.
        if report.distinct_states >= bounds.max_states {
            report.truncated = true;
            report.hit_state_cap = true;
            continue;
        }
        report.distinct_states += 1;
        if let Err(message) = model.check_state(&after) {
            let mut trace = path.clone();
            trace.push(step);
            return Err(Violation { trace, state_key: key, message });
        }
        // Bound honesty: if we cannot descend, the exploration is a
        // prefix. Depth: `depth` steps were taken to reach `after`, so
        // descending would fire transitions at depth + 1.
        if depth >= bounds.max_depth {
            report.truncated = true;
            continue;
        }
        path.push(step);
        let steps = model.steps(&after);
        stack.push(Frame { state: after, steps, next: 0 });
    }
    Ok(report)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A toy bounded counter: `Inc`/`Dec` with a ceiling invariant.
    struct BoundedCounter {
        cap: u8,
    }

    impl BoundedModel for BoundedCounter {
        type State = u8;
        type Step = &'static str;

        fn initial(&self) -> u8 {
            0
        }

        fn steps(&self, state: &u8) -> Vec<&'static str> {
            let mut steps = Vec::new();
            if *state < self.cap {
                steps.push("Inc");
            }
            if *state > 0 {
                steps.push("Dec");
            }
            steps
        }

        fn apply(&self, state: &u8, step: &&'static str) -> u8 {
            match *step {
                "Inc" => state + 1,
                "Dec" => state - 1,
                _ => *state,
            }
        }

        fn key(&self, state: &u8) -> String {
            format!("n={state}")
        }

        fn check_state(&self, state: &u8) -> Result<(), String> {
            if *state <= self.cap {
                Ok(())
            } else {
                Err(format!("counter {state} exceeds cap {}", self.cap))
            }
        }
    }

    #[test]
    fn the_checker_explores_a_toy_space_exhaustively() {
        let report =
            explore(&BoundedCounter { cap: 3 }, Bounds { max_depth: 6, max_states: 100 }).unwrap();
        // States 0..=3 are all reachable; the exact figures pin the
        // traversal determinism (any change in what counts as a
        // transition or a state shows up here). The deepest fired
        // transition is the `Dec` out of n=3, which is the 4th step.
        assert_eq!(report.distinct_states, 4);
        assert_eq!(report.transitions_fired, 6);
        assert_eq!(report.max_depth_reached, 4);
        assert!(!report.truncated, "no bound was hit: {report:?}");
    }

    /// The same counter with a step that breaks the ceiling: the
    /// checker must return a trace whose last step is the offender.
    struct BuggyCounter {
        cap: u8,
    }

    impl BoundedModel for BuggyCounter {
        type State = u8;
        type Step = &'static str;

        fn initial(&self) -> u8 {
            0
        }

        fn steps(&self, state: &u8) -> Vec<&'static str> {
            let mut steps = vec!["Jump"];
            if *state > 0 {
                steps.push("Dec");
            }
            steps
        }

        fn apply(&self, state: &u8, step: &&'static str) -> u8 {
            match *step {
                "Jump" => self.cap + 1,
                "Dec" => state - 1,
                _ => *state,
            }
        }

        fn key(&self, state: &u8) -> String {
            format!("n={state}")
        }

        fn check_state(&self, state: &u8) -> Result<(), String> {
            if *state <= self.cap {
                Ok(())
            } else {
                Err(format!("counter {state} exceeds cap {}", self.cap))
            }
        }
    }

    #[test]
    fn the_checker_catches_a_violation_with_a_reproduction_trace() {
        let violation = explore(&BuggyCounter { cap: 3 }, Bounds { max_depth: 4, max_states: 100 })
            .expect_err("the buggy model must be caught");
        assert_eq!(violation.trace, vec!["Jump"], "the trace reaches the violation in one step");
        assert!(violation.message.contains("exceeds cap"), "message: {}", violation.message);
    }

    /// An unbounded chain: the state cap must stop the walk and say so.
    struct Chain;

    impl BoundedModel for Chain {
        type State = usize;
        type Step = &'static str;

        fn initial(&self) -> usize {
            0
        }

        fn steps(&self, _state: &usize) -> Vec<&'static str> {
            vec!["Inc"]
        }

        fn apply(&self, state: &usize, _step: &&'static str) -> usize {
            state + 1
        }

        fn key(&self, state: &usize) -> String {
            format!("n={state}")
        }

        fn check_state(&self, _state: &usize) -> Result<(), String> {
            Ok(())
        }
    }

    #[test]
    fn the_checker_reports_truncation_when_bounds_cut_the_space() {
        let report = explore(&Chain, Bounds { max_depth: 1000, max_states: 10 }).unwrap();
        assert_eq!(report.distinct_states, 10, "exactly the cap was visited");
        assert!(report.truncated, "the walk was cut while steps remained: {report:?}");
        assert!(report.hit_state_cap, "the cap, not the depth, cut it: {report:?}");

        let report = explore(&Chain, Bounds { max_depth: 5, max_states: 1000 }).unwrap();
        assert_eq!(report.distinct_states, 6, "initial plus five fired transitions");
        assert_eq!(report.max_depth_reached, 5);
        assert!(report.truncated, "the depth bound cut the space: {report:?}");
        assert!(!report.hit_state_cap, "every trace of length <= 5 was explored");
    }
}
