//! The eight-invariant checklist and the seed-batch runner (ADR 0044 §4).
//!
//! [`Invariant`] names the eight simulation invariants verbatim from the
//! ADR so nightly failures report against the contract, not the code.
//! [`run_scenario`] executes one full deterministic scenario — seeded
//! workload, seeded faults, drain to quiescence — and returns a
//! [`SimReport`]; the CI seed batch runs it over [`seed_batch`](crate::rng::seed_batch)
//! and asserts determinism plus quiescent convergence.

use crate::faults::FaultPlan;
use crate::model::RegionId;
use crate::production::ProductionWorld;
use crate::rng::SimRng;
use crate::traits::ReplicationEngine as _;
use crate::workload::{Op, Workload};

/// One of the eight simulation invariants of ADR 0044 §4.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Invariant {
    /// 1. No acknowledged write is lost under member loss, Region loss,
    ///    WAN partition, clock skew ≤ 500 ms, duplicate or reordered delivery.
    NoAcknowledgedWriteLost,
    /// 2. All Regions hosting a database converge to byte-identical
    ///    canonical bodies and equal reconciliation fingerprints at quiescence.
    ConvergentFingerprints,
    /// 3. Merge is order-independent: any permutation of the same journal
    ///    rows yields the same state and the same history explanations.
    OrderIndependentMerge,
    /// 4. A `GlobalDurable` ack implies the row and all its causal
    ///    dependencies are present in every selected Region.
    GlobalDurableUniform,
    /// 5. Read-your-writes holds for any client carrying its session
    ///    token, across Region failover.
    ReadYourWrites,
    /// 6. `view revision = Q(database at its cut)` for every revision of
    ///    every view.
    ViewRevisionMatchesCut,
    /// 7. Cached log bytes equal origin bytes for the same key; no bytes
    ///    ever cross a capability boundary.
    CacheIntegrity,
    /// 8. Reconciliation after any fault completes in `O(log n)` rounds
    ///    and ships only the differing rows.
    ReconciliationComplexity,
}

impl Invariant {
    /// Returns the ADR invariant number (1–8).
    #[must_use]
    pub fn number(self) -> u8 {
        match self {
            Self::NoAcknowledgedWriteLost => 1,
            Self::ConvergentFingerprints => 2,
            Self::OrderIndependentMerge => 3,
            Self::GlobalDurableUniform => 4,
            Self::ReadYourWrites => 5,
            Self::ViewRevisionMatchesCut => 6,
            Self::CacheIntegrity => 7,
            Self::ReconciliationComplexity => 8,
        }
    }

    /// Returns the short title used in failure reports.
    #[must_use]
    pub fn title(self) -> &'static str {
        match self {
            Self::NoAcknowledgedWriteLost => "no acknowledged write lost",
            Self::ConvergentFingerprints => "convergent fingerprints",
            Self::OrderIndependentMerge => "order-independent merge",
            Self::GlobalDurableUniform => "global-durable uniformity",
            Self::ReadYourWrites => "read-your-writes",
            Self::ViewRevisionMatchesCut => "view revision matches cut",
            Self::CacheIntegrity => "cache integrity",
            Self::ReconciliationComplexity => "reconciliation complexity",
        }
    }

    /// Returns all eight invariants in ADR order.
    #[must_use]
    pub fn all() -> [Self; 8] {
        [
            Self::NoAcknowledgedWriteLost,
            Self::ConvergentFingerprints,
            Self::OrderIndependentMerge,
            Self::GlobalDurableUniform,
            Self::ReadYourWrites,
            Self::ViewRevisionMatchesCut,
            Self::CacheIntegrity,
            Self::ReconciliationComplexity,
        ]
    }
}

/// Scenario configuration: everything a run derives from besides the seed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SimConfig {
    /// The scenario seed; the whole run derives from it.
    pub seed: u64,
    /// The number of regions in the world.
    pub regions: usize,
    /// Client operations in the workload.
    pub ops: usize,
    /// Keys in the document key space.
    pub keys: u64,
    /// Steps over which workload ops and faults play out.
    pub steps: u64,
    /// Upper bound on generated faults.
    pub max_faults: usize,
}

impl SimConfig {
    /// Creates a configuration for `seed` with harness defaults.
    #[must_use]
    pub fn with_seed(seed: u64) -> Self {
        Self { seed, regions: 3, ops: 64, keys: 16, steps: 64, max_faults: 4 }
    }
}

impl Default for SimConfig {
    /// Returns the default configuration for seed `0`.
    fn default() -> Self {
        Self::with_seed(0)
    }
}

/// The outcome of one deterministic scenario run.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SimReport {
    /// The seed the run derived from.
    pub seed: u64,
    /// Steps executed including the quiescence drain.
    pub steps_run: u64,
    /// Journal rows committed (acknowledged).
    pub committed: u64,
    /// Whether the world drained to quiescence after faults healed.
    pub quiescent: bool,
    /// Whether all regions agree on the reconciliation fingerprint.
    pub converged: bool,
    /// The world digest (determinism key: equal seeds ⇒ equal digests).
    pub digest: u64,
}

/// Upper bound on drain steps after the fault horizon.
const MAX_DRAIN_STEPS: u64 = 10_000;

/// Executes one full deterministic scenario and reports the outcome.
///
/// The workload and fault plan derive from `config.seed`; operations play
/// out round-robin over `config.steps`; then the world drains until
/// quiescence (faults from [`FaultPlan::generate`] always heal before the
/// horizon, so a correct engine always drains).
#[must_use]
pub fn run_scenario(config: SimConfig) -> SimReport {
    let regions: Vec<RegionId> = (0..config.regions).map(RegionId::from_index).collect();
    if regions.is_empty() {
        return SimReport {
            seed: config.seed,
            steps_run: 0,
            committed: 0,
            quiescent: true,
            converged: true,
            digest: 0,
        };
    }
    let mut rng = SimRng::new(config.seed);
    let workload = Workload::generate(&mut rng, &regions, config.ops, config.keys);
    let faults = FaultPlan::generate(&mut rng, &regions, config.steps.max(1), config.max_faults);
    let Ok(mut world) = ProductionWorld::with_faults(regions.len(), faults) else {
        return SimReport {
            seed: config.seed,
            steps_run: 0,
            committed: 0,
            quiescent: false,
            converged: false,
            digest: 0,
        };
    };
    let mut committed = 0u64;
    let mut op_index = 0usize;
    let horizon = config.steps.max(1);
    let ops = workload.ops();
    for _ in 0..horizon {
        if !ops.is_empty() {
            // Spread the workload evenly: one op per step, cycling.
            if let Some(op) = ops.get(op_index % ops.len()) {
                match op {
                    Op::Write { region, key, value } => {
                        if world.submit(*region, *key, *value).is_some() {
                            committed = committed.saturating_add(1);
                        }
                    }
                    Op::Delete { region, key } => {
                        if world.delete(*region, *key).is_some() {
                            committed = committed.saturating_add(1);
                        }
                    }
                    Op::Read { .. } => {}
                }
                op_index = op_index.saturating_add(1);
            }
        }
        world.step();
    }
    let mut steps_run = horizon;
    while !world.is_quiescent() && steps_run < horizon.saturating_add(MAX_DRAIN_STEPS) {
        world.step();
        steps_run = steps_run.saturating_add(1);
    }
    let quiescent = world.is_quiescent();
    let first = world.fingerprint(regions[0]);
    let converged = regions.iter().all(|region| world.fingerprint(*region) == first);
    SimReport {
        seed: config.seed,
        steps_run,
        committed,
        quiescent,
        converged,
        digest: world.digest(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::rng::seed_batch;

    #[test]
    fn checklist_covers_all_eight_invariants() {
        let all = Invariant::all();
        assert_eq!(all.len(), 8);
        for (position, invariant) in all.iter().enumerate() {
            let expected = u8::try_from(position).expect("eight invariants fit in a u8") + 1;
            assert_eq!(invariant.number(), expected, "invariant order must follow the ADR");
        }
    }

    #[test]
    fn scenario_is_deterministic_for_one_seed() {
        let config = SimConfig::with_seed(2026);
        assert_eq!(run_scenario(config), run_scenario(config));
    }

    #[test]
    fn seed_batch_converges_at_quiescence() {
        for seed in seed_batch(7, 4) {
            let report = run_scenario(SimConfig::with_seed(seed));
            assert!(report.quiescent, "seed {seed} did not drain");
            assert!(report.converged, "seed {seed} did not converge");
            assert!(report.committed > 0, "seed {seed} committed nothing");
        }
    }
}
