//! D-31 / E-13 auto-index benchmark evidence and gate helpers.
//!
//! This file is intentionally independent of the benchmark command registry.
//! The shell harness (`scripts/auto-index.sh`) talks to the served TerraBase
//! and RavenDB HTTP boundaries and records the wire evidence.  This module is
//! the small, dependency-free contract that a runner can use before admitting
//! a receipt.  It never manufactures a pass when an endpoint or evidence is
//! absent.

#![allow(
    dead_code,
    reason = "the shell harness is the production entry point; this module is its testable contract"
)]
#![allow(clippy::field_reassign_with_default)]

use std::fmt;

/// D-31's maximum tolerated p95/pages/rows regression.
pub const REGRESSION_THRESHOLD: f64 = 0.05;
/// Minimum paired samples needed for a shadow comparison.
pub const MIN_SHADOW_SAMPLES: usize = 20;
/// D-31's per-collection visible index cap.
pub const INDEX_CAP: usize = 12;
/// D-31's maintenance budget as a multiple of the no-index write cost.
pub const WRITE_BUDGET_MULTIPLE: f64 = 2.0;
/// E-13's only admitted launch-member envelope.
pub const LAUNCH_MEMBER_ENVELOPE: &str = "2ocpu/12GiB";

/// A measured benchmark timing in milliseconds.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Timing {
    /// Wall time at the public HTTP boundary.
    pub milliseconds: f64,
}

impl Timing {
    /// Construct a finite, non-negative timing.
    pub fn new(milliseconds: f64) -> Result<Self, EvidenceError> {
        if milliseconds.is_finite() && milliseconds >= 0.0 {
            Ok(Self { milliseconds })
        } else {
            Err(EvidenceError::InvalidNumber("timing"))
        }
    }
}

/// An immutable identity observed from one comparator.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ComparatorIdentity {
    /// Subject label (`terrabase` or `ravendb`).
    pub subject: String,
    /// Exact operator-supplied immutable pin, normally an image/build digest.
    pub pin: String,
    /// SHA-256 of the server-reported identity response.
    pub observed_sha256: String,
}

impl ComparatorIdentity {
    /// Validate that identity is server-backed and immutable-looking.
    pub fn validate(&self) -> Result<(), EvidenceError> {
        if self.subject.trim().is_empty() {
            return Err(EvidenceError::MissingEvidence("identity.subject"));
        }
        if self.pin.trim().is_empty() {
            return Err(EvidenceError::MissingEvidence("identity.pin"));
        }
        if self.observed_sha256.len() != 64
            || !self.observed_sha256.bytes().all(|byte| byte.is_ascii_hexdigit())
        {
            return Err(EvidenceError::InvalidIdentity);
        }
        Ok(())
    }
}

/// Logical work counters from one query sample.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LogicalWork {
    /// SQLite pages read, or RavenDB's equivalent server counter.
    pub pages_read: u64,
    /// Rows examined by the query executor.
    pub rows_examined: u64,
}

impl LogicalWork {
    /// The conservative work scalar used by the gate.
    pub const fn total(self) -> u64 {
        self.pages_read.saturating_add(self.rows_examined)
    }
}

/// One side of a paired shadow replay.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ShadowSide {
    /// Public-boundary timings.
    pub timings: Vec<Timing>,
    /// Logical work counters paired by replay ordinal.
    pub work: Vec<LogicalWork>,
}

impl ShadowSide {
    fn validate(&self, name: &'static str) -> Result<(), EvidenceError> {
        if self.timings.len() < MIN_SHADOW_SAMPLES || self.work.len() < MIN_SHADOW_SAMPLES {
            return Err(EvidenceError::InsufficientSamples(name));
        }
        if self.timings.len() != self.work.len() {
            return Err(EvidenceError::MismatchedSamples(name));
        }
        Ok(())
    }
}

/// Result of comparing no-index and candidate-index shadow replays.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShadowDecision {
    /// Candidate is safe to activate for the replayed templates.
    Activate,
    /// Candidate remains invisible and the affected templates keep a pin.
    ActivateWithPins,
    /// Candidate must be rolled back immediately.
    Rollback,
}

/// Measured shadow evidence and the resulting gate decision.
#[derive(Clone, Debug, PartialEq)]
pub struct ShadowEvidence {
    /// No-index replay.
    pub baseline: ShadowSide,
    /// Candidate-index replay.
    pub candidate: ShadowSide,
    /// Welch/owner-side significance result; false means the observed change
    /// is not statistically significant.  The harness must obtain this from
    /// the server audit, never infer it from a missing field.
    pub statistically_significant: bool,
}

impl ShadowEvidence {
    /// Apply D-31's 5% guard to paired public timings and logical work.
    pub fn decide(&self) -> Result<ShadowDecision, EvidenceError> {
        self.baseline.validate("baseline")?;
        self.candidate.validate("candidate")?;
        let baseline_p95 = percentile(&self.baseline.timings)?;
        let candidate_p95 = percentile(&self.candidate.timings)?;
        let baseline_work = percentile_work(&self.baseline.work)?;
        let candidate_work = percentile_work(&self.candidate.work)?;
        let timing_regressed = exceeds_regression(candidate_p95, baseline_p95);
        let work_regressed = exceeds_regression(candidate_work, baseline_work);
        if timing_regressed || work_regressed {
            // Crossing the operational 5% guard is itself sufficient for an
            // immediate rollback.  A rollback is never reported as a passing
            // activation, even when the server has not finished its Welch
            // significance calculation.
            return Ok(ShadowDecision::Rollback);
        }
        if !self.statistically_significant {
            return Err(EvidenceError::MissingEvidence("shadow.welch_significance"));
        }
        Ok(ShadowDecision::Activate)
    }
}

/// A fixed-point observation for one replayed template set.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FixedPoint {
    /// Canonical index-state digest at each replay round.
    pub state_digests: Vec<String>,
}

impl FixedPoint {
    /// A fixed point requires two equal, non-empty final states.
    pub fn validate(&self) -> Result<(), EvidenceError> {
        if self.state_digests.len() < 2 {
            return Err(EvidenceError::InsufficientSamples("fixed_point.rounds"));
        }
        if self.state_digests.iter().any(|digest| digest.trim().is_empty()) {
            return Err(EvidenceError::MissingEvidence("fixed_point.state_digest"));
        }
        if self.state_digests[self.state_digests.len() - 1]
            != self.state_digests[self.state_digests.len() - 2]
        {
            return Err(EvidenceError::NoFixedPoint);
        }
        Ok(())
    }
}

/// The launch resource envelope recorded beside every receipt.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ResourceEvidence {
    /// Fixed cgroup/cpu envelope; only `2ocpu/12GiB` is admissible.
    pub envelope: String,
    /// Host kernel identity.
    pub kernel: String,
    /// Observed cgroup/affinity evidence, not a claim from configuration.
    pub observed: String,
}

impl ResourceEvidence {
    /// Reject configuration-only or empty resource records.
    pub fn validate(&self) -> Result<(), EvidenceError> {
        if self.envelope != LAUNCH_MEMBER_ENVELOPE {
            return Err(EvidenceError::MissingEvidence("resource.envelope.2ocpu/12GiB"));
        }
        for (name, value) in [
            ("resource.envelope", &self.envelope),
            ("resource.kernel", &self.kernel),
            ("resource.observed", &self.observed),
        ] {
            if value.trim().is_empty() {
                return Err(EvidenceError::MissingEvidence(name));
            }
        }
        Ok(())
    }
}

/// The minimum receipt facts required by E-13.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReceiptFacts {
    /// Server identities are pinned and observed.
    pub identities: Vec<ComparatorIdentity>,
    /// Launch resource evidence.
    pub resource: ResourceEvidence,
    /// Whether every requested phase produced real endpoint evidence.
    pub phases_complete: bool,
    /// Explicit phase names that could not be evidenced.
    pub missing_phases: Vec<String>,
}

impl ReceiptFacts {
    /// Fail closed before a result can be compared.
    pub fn validate(&self) -> Result<(), EvidenceError> {
        if self.identities.len() < 2 {
            return Err(EvidenceError::MissingEvidence("identities"));
        }
        for identity in &self.identities {
            identity.validate()?;
        }
        self.resource.validate()?;
        if !self.phases_complete || !self.missing_phases.is_empty() {
            return Err(EvidenceError::IncompletePhases);
        }
        Ok(())
    }
}

/// Errors are deliberately descriptive because they are retained in failed
/// benchmark receipts rather than converted to a synthetic zero or pass.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EvidenceError {
    /// A required server/evidence field was not supplied.
    MissingEvidence(&'static str),
    /// A numeric measurement was NaN, infinite, or negative.
    InvalidNumber(&'static str),
    /// An identity was not a 64-character hexadecimal digest.
    InvalidIdentity,
    /// Fewer than the required paired observations were supplied.
    InsufficientSamples(&'static str),
    /// Timing and logical-work vectors do not pair by ordinal.
    MismatchedSamples(&'static str),
    /// A candidate regressed but the server supplied no significance result.
    NoFixedPoint,
    /// Replay never reached a stable index state.
    IncompletePhases,
}

impl fmt::Display for EvidenceError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingEvidence(name) => write!(formatter, "missing required evidence: {name}"),
            Self::InvalidNumber(name) => write!(formatter, "invalid numeric evidence: {name}"),
            Self::InvalidIdentity => formatter.write_str("invalid immutable comparator identity"),
            Self::InsufficientSamples(name) => write!(formatter, "insufficient samples: {name}"),
            Self::MismatchedSamples(name) => write!(formatter, "mismatched samples: {name}"),
            Self::NoFixedPoint => {
                formatter.write_str("template replay did not reach a fixed point")
            }
            Self::IncompletePhases => {
                formatter.write_str("one or more benchmark phases lack endpoint evidence")
            }
        }
    }
}

impl std::error::Error for EvidenceError {}

fn exceeds_regression(candidate: f64, baseline: f64) -> bool {
    !candidate.is_finite()
        || !baseline.is_finite()
        || (baseline == 0.0 && candidate > 0.0)
        || (baseline > 0.0 && candidate > baseline * (1.0 + REGRESSION_THRESHOLD))
}

fn percentile(values: &[Timing]) -> Result<f64, EvidenceError> {
    if values.len() < MIN_SHADOW_SAMPLES {
        return Err(EvidenceError::InsufficientSamples("timings"));
    }
    let mut values = values.iter().map(|timing| timing.milliseconds).collect::<Vec<_>>();
    if values.iter().any(|value| !value.is_finite() || *value < 0.0) {
        return Err(EvidenceError::InvalidNumber("timings"));
    }
    values.sort_by(f64::total_cmp);
    let index = values.len().saturating_mul(95).div_ceil(100).saturating_sub(1);
    Ok(values[index])
}

fn percentile_work(values: &[LogicalWork]) -> Result<f64, EvidenceError> {
    if values.len() < MIN_SHADOW_SAMPLES {
        return Err(EvidenceError::InsufficientSamples("logical_work"));
    }
    let mut values = values.iter().map(|work| work.total() as f64).collect::<Vec<_>>();
    values.sort_by(f64::total_cmp);
    let index = values.len().saturating_mul(95).div_ceil(100).saturating_sub(1);
    Ok(values[index])
}

#[cfg(test)]
mod tests {
    use super::*;

    fn timings(value: f64) -> Vec<Timing> {
        (0..MIN_SHADOW_SAMPLES).map(|_| Timing::new(value).expect("finite timing")).collect()
    }

    fn work(value: u64) -> Vec<LogicalWork> {
        (0..MIN_SHADOW_SAMPLES)
            .map(|_| LogicalWork { pages_read: value, rows_examined: 0 })
            .collect()
    }

    #[test]
    fn five_percent_regression_rolls_back_automatically() {
        let evidence = ShadowEvidence {
            baseline: ShadowSide { timings: timings(10.0), work: work(100) },
            candidate: ShadowSide { timings: timings(10.6), work: work(100) },
            statistically_significant: true,
        };
        assert_eq!(evidence.decide(), Ok(ShadowDecision::Rollback));
    }

    #[test]
    fn missing_significance_fails_closed_instead_of_activating() {
        let evidence = ShadowEvidence {
            baseline: ShadowSide { timings: timings(10.0), work: work(100) },
            candidate: ShadowSide { timings: timings(10.6), work: work(100) },
            statistically_significant: false,
        };
        assert_eq!(evidence.decide(), Ok(ShadowDecision::Rollback));
    }

    #[test]
    fn fixed_point_requires_two_equal_states() {
        assert!(FixedPoint { state_digests: vec!["a".into(), "a".into()] }.validate().is_ok());
        assert_eq!(
            FixedPoint { state_digests: vec!["a".into(), "b".into()] }.validate(),
            Err(EvidenceError::NoFixedPoint)
        );
    }

    #[test]
    fn comparator_identity_rejects_unpinned_or_non_digest_identity() {
        let identity = ComparatorIdentity {
            subject: "ravendb".into(),
            pin: String::new(),
            observed_sha256: "0".repeat(64),
        };
        assert_eq!(identity.validate(), Err(EvidenceError::MissingEvidence("identity.pin")));
        let identity = ComparatorIdentity {
            subject: "ravendb".into(),
            pin: "sha256:deadbeef".into(),
            observed_sha256: "unknown".into(),
        };
        assert_eq!(identity.validate(), Err(EvidenceError::InvalidIdentity));
    }

    #[test]
    fn too_few_shadow_samples_are_not_a_pass() {
        let evidence = ShadowEvidence {
            baseline: ShadowSide {
                timings: timings(10.0)[..MIN_SHADOW_SAMPLES - 1].to_vec(),
                work: work(10)[..MIN_SHADOW_SAMPLES - 1].to_vec(),
            },
            candidate: ShadowSide { timings: timings(10.0), work: work(10) },
            statistically_significant: false,
        };
        assert_eq!(evidence.decide(), Err(EvidenceError::InsufficientSamples("baseline")));
    }
}
