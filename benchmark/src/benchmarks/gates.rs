//! Conservative statistical gates for benchmark comparisons.
//!
//! A difference is reported only when the median moves beyond the noise
//! budget and the sample ranges do not overlap.

use super::vocabulary::{ObjectiveStatus, ReferenceRun, Verdict};

/// The median and full sample range of one measured quantity.
///
/// The median is the middle sample of an odd run and the mean of the
/// middle pair of an even run. The range is the run's own minimum and
/// maximum, which is what the separation half of the gate tests.
#[derive(Clone, Copy, Debug)]
pub(crate) struct MedianRange {
    /// The median sample.
    pub median: f64,
    /// The smallest sample.
    pub min: f64,
    /// The largest sample.
    pub max: f64,
}

impl MedianRange {
    /// Reduces samples to their median and range, or `None` when no sample
    /// exists — a quantity nobody measured has no median, and inventing one
    /// is how a fabricated comparison would start.
    pub(crate) fn of(samples: &[f64]) -> Option<Self> {
        if samples.is_empty() {
            return None;
        }
        let mut sorted = samples.to_vec();
        sorted.sort_by(|left, right| left.partial_cmp(right).unwrap_or(std::cmp::Ordering::Equal));
        let middle = sorted.len() / 2;
        let median = if sorted.len().is_multiple_of(2) {
            sorted[middle - 1].midpoint(sorted[middle])
        } else {
            sorted[middle]
        };
        Some(Self { median, min: sorted[0], max: sorted[sorted.len() - 1] })
    }
}

/// The gate's classification of one quantity.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Trend {
    /// Measurably better than the comparand.
    Better,
    /// Measurably worse than the comparand.
    Worse,
    /// Inside the noise band, or the sample ranges overlap.
    Stable,
}

/// The signed median delta in percent, matching V1's `PercentDelta`.
fn percent_delta(current: f64, prior: f64) -> f64 {
    (current / prior - 1.0) * 100.0
}

/// Classifies a quantity where a higher value is better (throughput,
/// operations per second). V1's `ClassifyHigher` rule, verbatim.
pub(crate) fn classify_higher(
    current: &MedianRange,
    prior: &MedianRange,
    noise_percent: f64,
) -> Trend {
    let delta = percent_delta(current.median, prior.median);
    if delta > noise_percent && current.min > prior.max {
        Trend::Better
    } else if delta < -noise_percent && current.max < prior.min {
        Trend::Worse
    } else {
        Trend::Stable
    }
}

/// Classifies a quantity where a lower value is better (latency
/// percentiles). V1's `ClassifyLower` rule, verbatim.
pub(crate) fn classify_lower(
    current: &MedianRange,
    prior: &MedianRange,
    noise_percent: f64,
) -> Trend {
    let delta = percent_delta(current.median, prior.median);
    if delta < -noise_percent && current.max < prior.min {
        Trend::Better
    } else if delta > noise_percent && current.min > prior.max {
        Trend::Worse
    } else {
        Trend::Stable
    }
}

/// The gate's verdict on one V2-versus-reference comparison.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct ReferenceComparison {
    /// The throughput trend (higher is better).
    pub throughput: Trend,
    /// The p95 latency trend (lower is better).
    pub p95: Trend,
}

impl ReferenceComparison {
    /// The objective reading of this comparison alone, assuming both sides
    /// really ran.
    ///
    /// Parity-or-better means the throughput trend is `Better` or `Stable`
    /// — `Stable` *is* the gate's definition of parity, a difference inside
    /// the declared noise band — and the p95 trend is not `Worse`.
    pub(crate) fn objective(self) -> (ObjectiveStatus, Verdict) {
        let parity_throughput =
            matches!(self.throughput, Trend::Better | Trend::Stable) && self.p95 != Trend::Worse;
        if !parity_throughput {
            return (ObjectiveStatus::NotMet, Verdict::V2Regression);
        }
        match self.throughput {
            Trend::Better => (ObjectiveStatus::Met, Verdict::V2Faster),
            _ => (ObjectiveStatus::Met, Verdict::NoImprovement),
        }
    }
}

/// Reads a comparison off the gate.
///
/// Kept as a free function so a caller can state the objective reading of a
/// comparison it already classified.
pub(crate) fn objective_status(comparison: ReferenceComparison) -> (ObjectiveStatus, Verdict) {
    comparison.objective()
}

/// The fail-closed rule of the lane: no wall-clock comparison may produce
/// any status other than `inconclusive` while the reference baseline is
/// [`ReferenceRun::Unrun`].
///
/// This is the structural guard behind the charter's "an objective is not a
/// claim" — a missing reference cannot be averaged in, extrapolated from, or
/// assumed away, and the honest outcome is what the type system returns.
pub(crate) fn reference_comparison_status(
    reference: &ReferenceRun,
    comparison: ReferenceComparison,
) -> (ObjectiveStatus, Verdict) {
    if !reference.ran() {
        return (ObjectiveStatus::Inconclusive, Verdict::Inconclusive);
    }
    comparison.objective()
}
