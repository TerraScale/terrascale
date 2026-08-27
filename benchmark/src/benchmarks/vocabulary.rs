//! The charter's exact benchmark claim vocabulary (PD-2026-08-26-03).
//!
//! Two axes, both closed:
//!
//! - [`Verdict`] describes one measured comparison. Its four tokens are the
//!   only words a Benchmark Receipt may carry for a comparison.
//! - [`ObjectiveStatus`] answers the separate question the charter asks per
//!   run — is TerraBaseV2 at PostgreSQL-parity or better? — with `met`,
//!   `not met`, or `inconclusive`.
//!
//! [`ReferenceRun`] records whether one reference baseline actually
//! executed. An unrun reference carries its reason and its owner; it never
//! carries a number.

/// Every comparison verdict a Benchmark Receipt may carry, and its exact
/// charter token.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Verdict {
    /// TerraBaseV2 measurably better than the comparand.
    V2Faster,
    /// No measurable difference beyond the gate's noise band.
    NoImprovement,
    /// TerraBaseV2 measurably worse than the comparand.
    V2Regression,
    /// The evidence cannot support a call.
    Inconclusive,
}

impl Verdict {
    /// The exact charter token.
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::V2Faster => "v2-faster",
            Self::NoImprovement => "no-improvement",
            Self::V2Regression => "v2-regression",
            Self::Inconclusive => "inconclusive",
        }
    }
}

/// The closed verdict vocabulary, in charter order.
pub(crate) const VERDICT_VOCABULARY: [&str; 4] =
    ["v2-faster", "no-improvement", "v2-regression", "inconclusive"];

/// The per-run answer to the charter's objective question.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ObjectiveStatus {
    /// The gate found parity or better on every compared quantity.
    Met,
    /// The gate found a measurable shortfall.
    NotMet,
    /// The evidence cannot answer the question.
    Inconclusive,
}

impl ObjectiveStatus {
    /// The exact charter token.
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::Met => "met",
            Self::NotMet => "not met",
            Self::Inconclusive => "inconclusive",
        }
    }
}

/// The closed objective-status vocabulary, in charter order.
pub(crate) const OBJECTIVE_STATUS_VOCABULARY: [&str; 3] = ["met", "not met", "inconclusive"];

/// Renders the canonical objective-status line every performance receipt
/// carries. One line, one shape, so the standing discipline
/// (`scripts/check_benchmark_receipts.py`) can fail closed on its absence.
pub(crate) fn objective_status_line(status: ObjectiveStatus) -> String {
    format!("PostgreSQL-parity-or-better: {}", status.as_str())
}

/// Whether one side of a comparison actually executed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum ReferenceRun {
    /// The side ran, with the exact identity observed at the run.
    Ran {
        /// The observed identity (a source commit, a `version()` output).
        identity: String,
    },
    /// The side did not run. The reason names the concrete gap; the owner is
    /// who must close it.
    Unrun {
        /// What prevented the run, stated as an environment fact.
        reason: String,
        /// The named owner of closing the gap.
        owner: &'static str,
    },
}

impl ReferenceRun {
    /// Whether the side executed.
    pub(crate) const fn ran(&self) -> bool {
        matches!(self, Self::Ran { .. })
    }

    /// The one-line receipt form: `run (<identity>)` or
    /// `unrun — <reason> (owner: <owner>)`.
    pub(crate) fn describe(&self) -> String {
        match self {
            Self::Ran { identity } => format!("run ({identity})"),
            Self::Unrun { reason, owner } => format!("unrun — {reason} (owner: {owner})"),
        }
    }
}
