//! Bounded shadow verification and generation-bound plan pins (ADR 0045 §3.7).
//!
//! Shadow execution is a safety gate: samples are validated before any
//! visibility decision, candidate work is capped against the historical
//! average, and a deterministic Welch-style comparison decides whether the
//! candidate improved, regressed, or lacks evidence. Plan pins are retained
//! only for the exact template/candidate and schema-plan generations that
//! produced them.

use super::{Candidate, LogicalCost, Template};

/// Maximum executions retained from either side of one shadow comparison.
pub(crate) const MAX_SHADOW_SAMPLES: usize = 4_096;
/// Minimum executions required on each side of a comparison.
pub(crate) const MIN_SHADOW_SAMPLES: usize = super::later::SHADOW_MIN_EXECUTIONS;
/// Minimum improvement required for activation.
pub(crate) const MIN_IMPROVEMENT_FRACTION: f64 = super::later::SHADOW_MIN_IMPROVEMENT_FRACTION;
/// Candidate execution cap multiplier from ADR 0045 §3.7.
pub(crate) const COST_CAP_MULTIPLE: f64 = super::later::SHADOW_COST_CAP_MULTIPLE;
/// Fixed floor for the candidate execution cap.
pub(crate) const COST_CAP_FLOOR: f64 = 1.0;
/// Conservative deterministic critical value for the Welch statistic.
pub(crate) const WELCH_CRITICAL_VALUE: f64 = 2.0;

/// A bounded pair of logical-cost samples for Welch verification.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct ShadowSamples {
    /// Existing-plan costs.
    pub(crate) baseline: Vec<LogicalCost>,
    /// Candidate-plan costs.
    pub(crate) candidate: Vec<LogicalCost>,
}

impl ShadowSamples {
    /// Constructs a sample pair. Bounds are enforced when it is evaluated.
    pub(crate) fn new(baseline: Vec<LogicalCost>, candidate: Vec<LogicalCost>) -> Self {
        Self { baseline, candidate }
    }

    /// Whether both sides carry enough bounded observations.
    #[must_use]
    pub(crate) fn has_minimum(&self) -> bool {
        self.baseline.len() >= MIN_SHADOW_SAMPLES
            && self.candidate.len() >= MIN_SHADOW_SAMPLES
            && self.baseline.len() <= MAX_SHADOW_SAMPLES
            && self.candidate.len() <= MAX_SHADOW_SAMPLES
    }
}

/// Result of shadow verification.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum ShadowDecision {
    /// Candidate is safe to activate for every observed template.
    Activate,
    /// Candidate is safe to activate, but this template needs a baseline-plan pin.
    ActivateWithPins,
    /// Candidate failed its safety, cost, or statistical check.
    #[default]
    Abandon,
}

impl ShadowDecision {
    /// Whether the candidate may become visible to the planner.
    #[must_use]
    pub(crate) const fn activates(self) -> bool {
        matches!(self, Self::Activate | Self::ActivateWithPins)
    }

    /// Whether the baseline plan must be pinned for this template.
    #[must_use]
    pub(crate) const fn needs_pin(self) -> bool {
        matches!(self, Self::ActivateWithPins)
    }
}

/// Welch comparison evidence retained in the audit record.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct WelchEvidence {
    /// Baseline mean logical cost.
    pub(crate) baseline_mean: f64,
    /// Candidate mean logical cost.
    pub(crate) candidate_mean: f64,
    /// Fractional reduction (negative means regression).
    pub(crate) improvement_fraction: f64,
    /// Absolute Welch-style statistic.
    pub(crate) statistic: f64,
    /// Whether the means differ at the fixed conservative threshold.
    pub(crate) significant: bool,
}

/// Port for replaying templates without changing their serving plan.
pub(crate) trait ShadowVerifier {
    /// Replays one template and returns a bounded verification decision.
    fn verify(
        &mut self,
        template: &Template,
        candidate: &Candidate,
        samples: &ShadowSamples,
    ) -> ShadowDecision;
}

/// Deterministic verifier used by the production shadow worker.
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct WelchShadowVerifier;

impl ShadowVerifier for WelchShadowVerifier {
    fn verify(
        &mut self,
        template: &Template,
        candidate: &Candidate,
        samples: &ShadowSamples,
    ) -> ShadowDecision {
        verify_shadow(template, candidate, samples).map_or(ShadowDecision::Abandon, decision_for)
    }
}

/// Evaluates one template/candidate pair without side effects.
pub(crate) fn verify_shadow(
    template: &Template,
    candidate: &Candidate,
    samples: &ShadowSamples,
) -> Option<WelchEvidence> {
    if template.collection() != candidate.collection() || !samples.has_minimum() {
        return None;
    }
    let baseline = statistics(&samples.baseline)?;
    let candidate_stats = statistics(&samples.candidate)?;
    let cap = baseline.mean.mul_add(COST_CAP_MULTIPLE, COST_CAP_FLOOR);
    if !cap.is_finite() {
        return None;
    }
    if samples
        .candidate
        .iter()
        .map(|cost| scalar_cost(*cost))
        .collect::<Option<Vec<_>>>()?
        .iter()
        .any(|cost| *cost > cap)
    {
        return None;
    }
    let difference = baseline.mean - candidate_stats.mean;
    let improvement_fraction = difference / baseline.mean;
    if !improvement_fraction.is_finite() {
        return None;
    }
    let standard_error_squared =
        baseline.variance / baseline.count + candidate_stats.variance / candidate_stats.count;
    let (statistic, significant) = if standard_error_squared == 0.0 {
        (if difference == 0.0 { 0.0 } else { f64::INFINITY }, difference != 0.0)
    } else {
        let statistic = difference.abs() / standard_error_squared.sqrt();
        if !statistic.is_finite() {
            return None;
        }
        (statistic, statistic >= WELCH_CRITICAL_VALUE)
    };
    Some(WelchEvidence {
        baseline_mean: baseline.mean,
        candidate_mean: candidate_stats.mean,
        improvement_fraction,
        statistic,
        significant,
    })
}

fn decision_for(evidence: WelchEvidence) -> ShadowDecision {
    if evidence.improvement_fraction >= MIN_IMPROVEMENT_FRACTION && evidence.significant {
        ShadowDecision::Activate
    } else if evidence.improvement_fraction < 0.0 && evidence.significant {
        ShadowDecision::ActivateWithPins
    } else {
        ShadowDecision::Abandon
    }
}

#[derive(Clone, Copy)]
struct Statistics {
    mean: f64,
    variance: f64,
    count: f64,
}

fn statistics(samples: &[LogicalCost]) -> Option<Statistics> {
    if samples.is_empty() || samples.len() > MAX_SHADOW_SAMPLES {
        return None;
    }
    // Sorting integer costs makes replay deterministic regardless of sample
    // arrival order and avoids order-dependent floating-point accumulation.
    let mut values = samples.iter().map(|cost| scalar_cost(*cost)).collect::<Option<Vec<_>>>()?;
    values.sort_unstable_by(|left, right| left.total_cmp(right));
    let count = values.len() as f64;
    let mean = values.iter().sum::<f64>() / count;
    if !mean.is_finite() || mean <= 0.0 {
        return None;
    }
    let variance = if values.len() == 1 {
        0.0
    } else {
        values.iter().map(|value| (value - mean).powi(2)).sum::<f64>() / (count - 1.0)
    };
    if !variance.is_finite() {
        return None;
    }
    Some(Statistics { mean, variance, count })
}

fn scalar_cost(cost: LogicalCost) -> Option<f64> {
    let total = cost.pages_read().checked_add(cost.rows_examined())?;
    let value = total as f64;
    value.is_finite().then_some(value)
}

/// A generation- and schema-bound plan pin.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct PlanPin {
    /// Template to which the pin applies.
    pub(crate) template: Template,
    /// Candidate whose plan is excluded for this template.
    pub(crate) candidate: Candidate,
    /// Index-plan generation.
    pub(crate) generation: u64,
    /// Schema generation used during verification.
    pub(crate) schema_generation: u64,
    /// UNIX-seconds timestamp at which the pin was installed.
    pub(crate) issued_at: u64,
    /// Exclusive expiry of the pin.
    pub(crate) expires_at: u64,
    /// Exclusive retention bound for the evidence and pin.
    pub(crate) retention_until: u64,
}

/// Refusal to create an unsafe or ambiguous plan pin.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum PlanPinError {
    /// The expiry or retention timestamp is not strictly after issuance.
    InvalidLifetime,
    /// The retention bound ends before the pin's expiry.
    RetentionBeforeExpiry,
}

impl PlanPin {
    /// Creates a pin with explicit expiry and evidence-retention bounds.
    pub(crate) fn try_new(
        template: Template,
        candidate: Candidate,
        generation: u64,
        schema_generation: u64,
        issued_at: u64,
        expires_at: u64,
        retention_until: u64,
    ) -> Result<Self, PlanPinError> {
        if expires_at <= issued_at {
            return Err(PlanPinError::InvalidLifetime);
        }
        if retention_until < expires_at {
            return Err(PlanPinError::RetentionBeforeExpiry);
        }
        Ok(Self {
            template,
            candidate,
            generation,
            schema_generation,
            issued_at,
            expires_at,
            retention_until,
        })
    }

    /// Whether the pin is valid for the exact current generations at `now`.
    #[must_use]
    pub(crate) fn valid_at(&self, generation: u64, schema_generation: u64, now: u64) -> bool {
        self.generation == generation
            && self.schema_generation == schema_generation
            && now >= self.issued_at
            && now < self.expires_at
            && now < self.retention_until
    }

    /// Whether this pin applies to a template/candidate pair.
    #[must_use]
    pub(crate) fn applies_to(&self, template: &Template, candidate: &Candidate) -> bool {
        &self.template == template && &self.candidate == candidate
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::query::optimizer::auto_index::{
        CandidateColumn, CandidateKind, CandidateSource, ColumnRole, PredicateKind,
        TemplatePredicate,
    };

    fn template() -> Template {
        Template::try_new(
            "users",
            vec![TemplatePredicate::try_new("tenant", PredicateKind::Equality).unwrap()],
            vec![],
            None,
        )
        .unwrap()
    }

    fn candidate() -> Candidate {
        Candidate {
            collection: "users".into(),
            columns: vec![CandidateColumn::try_new("tenant", ColumnRole::Equality).unwrap()],
            kind: CandidateKind::BTree,
            source: CandidateSource::Workload,
            needs_descriptor: false,
            benefit_per_byte: 1.0,
        }
    }

    fn samples(baseline: u64, candidate: u64) -> ShadowSamples {
        ShadowSamples::new(
            (0..MIN_SHADOW_SAMPLES).map(|n| LogicalCost::new(baseline + n as u64 % 2, 0)).collect(),
            (0..MIN_SHADOW_SAMPLES)
                .map(|n| LogicalCost::new(candidate + n as u64 % 2, 0))
                .collect(),
        )
    }

    #[test]
    fn improvement_requires_evidence_before_activation() {
        let mut verifier = WelchShadowVerifier;
        assert_eq!(
            verifier.verify(&template(), &candidate(), &samples(100, 50)),
            ShadowDecision::Activate
        );
    }

    #[test]
    fn significant_regression_activates_only_with_a_pin() {
        let mut verifier = WelchShadowVerifier;
        assert_eq!(
            verifier.verify(&template(), &candidate(), &samples(100, 120)),
            ShadowDecision::ActivateWithPins
        );
    }

    #[test]
    fn insufficient_and_capped_evidence_fails_closed() {
        let mut verifier = WelchShadowVerifier;
        let short =
            ShadowSamples::new(vec![LogicalCost::new(1, 0); 2], vec![LogicalCost::new(1, 0); 2]);
        assert_eq!(verifier.verify(&template(), &candidate(), &short), ShadowDecision::Abandon);
        assert_eq!(
            verifier.verify(&template(), &candidate(), &samples(10, 100)),
            ShadowDecision::Abandon
        );
        let overflow = ShadowSamples::new(
            vec![LogicalCost::new(u64::MAX, 1); MIN_SHADOW_SAMPLES],
            vec![LogicalCost::new(1, 0); MIN_SHADOW_SAMPLES],
        );
        assert_eq!(verifier.verify(&template(), &candidate(), &overflow), ShadowDecision::Abandon);
    }

    #[test]
    fn pins_invalidate_on_generation_schema_expiry_and_retention() {
        let pin = PlanPin::try_new(template(), candidate(), 4, 9, 100, 200, 300).unwrap();
        assert!(pin.valid_at(4, 9, 100));
        assert!(pin.valid_at(4, 9, 199));
        assert!(!pin.valid_at(5, 9, 150));
        assert!(!pin.valid_at(4, 10, 150));
        assert!(!pin.valid_at(4, 9, 200));
        assert!(!pin.valid_at(4, 9, 300));
        assert!(pin.applies_to(&template(), &candidate()));
        assert_eq!(
            PlanPin::try_new(template(), candidate(), 1, 1, 10, 10, 20),
            Err(PlanPinError::InvalidLifetime)
        );
        assert_eq!(
            PlanPin::try_new(template(), candidate(), 1, 1, 10, 20, 19),
            Err(PlanPinError::RetentionBeforeExpiry)
        );
    }

    #[test]
    fn replay_is_deterministic_under_sample_reordering() {
        let first = samples(100, 70);
        let mut second = first.clone();
        second.baseline.reverse();
        second.candidate.reverse();
        assert_eq!(
            verify_shadow(&template(), &candidate(), &first),
            verify_shadow(&template(), &candidate(), &second)
        );
    }
}
