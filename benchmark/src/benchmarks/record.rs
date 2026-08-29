//! The receipt renderer and the regression-tracking rule.
//!
//! A receipt is the only place a performance statement may live, so the
//! renderer emits every line the standing discipline checks for: the exact
//! verdict, the canonical objective-status line, the reference's honest
//! run statuses, and — when the objective is not met — a regression
//! follow-up with a named owner.

use std::fmt::Write as _;

use super::{
    lane::{LaneConfig, Workload},
    reference,
    vocabulary::{self, ObjectiveStatus, ReferenceRun, Verdict},
};

/// A recorded follow-up owed by a run that regressed against the objective.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct RegressionFollowUp {
    /// The named owner of the follow-up.
    pub owner: &'static str,
    /// The tracking rule, stated once here so every receipt states it the
    /// same way.
    pub rule: &'static str,
}

/// The standing regression-tracking rule (PD-2026-08-26-03 D3).
pub(crate) const REGRESSION_RULE: &str = "a run whose objective status is `not met` is recorded and tracked to a named owner, \
     never hidden and never re-benchmarked into agreement";

/// One benchmark-lane run record, in the SE-07 receipt shape.
#[derive(Clone, Debug)]
pub(crate) struct RunRecord {
    /// The run's label.
    pub label: String,
    /// The workload measured.
    pub workload: Workload,
    /// The lane configuration used.
    pub config: LaneConfig,
    /// The TerraBaseV2 source identity measured.
    pub source_identity: String,
    /// The comparison verdict, from the closed vocabulary.
    pub verdict: Verdict,
    /// The objective status, from the closed vocabulary.
    pub status: ObjectiveStatus,
    /// The PostgreSQL reference's honest run status.
    pub postgresql: ReferenceRun,
    /// Validity notes, retained whether they flatter the run or not.
    pub notes: Vec<String>,
}

impl RunRecord {
    /// The commissioning record for a workload: the lane's own machinery
    /// exercised end to end, with the outcome forced to the honest
    /// `inconclusive` because no reference baseline ran.
    ///
    /// The status is not a default that a caller may overwrite: a
    /// commissioning run has no comparand, so it structurally cannot
    /// support `met` or a verdict.
    pub(crate) fn commissioning(workload: Workload) -> Self {
        let postgresql = reference::postgresql_reference_run();
        Self {
            label: format!("se15-commissioning-{}", workload.as_str()),
            workload,
            config: LaneConfig::COMMISSIONING_TRIAL,
            source_identity: source_identity(),
            verdict: Verdict::Inconclusive,
            status: ObjectiveStatus::Inconclusive,
            postgresql,
            notes: vec![
                "commissioning run: the lane's gates, warm-up discipline, and renderer \
                 exercised end to end on real engine operations"
                    .to_owned(),
                format!(
                    "wall-clock samples are engineering evidence only; without a comparand \
                     they support no verdict, per {}",
                    REGRESSION_RULE
                ),
            ],
        }
    }

    /// The follow-up a run owes when it regresses against the objective.
    ///
    /// `None` for `met` (nothing to follow up) and for `inconclusive` (no
    /// regression was measured, so there is nothing to track — the open
    /// question is the reference gap recorded in the receipt itself).
    pub(crate) fn regression_follow_up(status: ObjectiveStatus) -> Option<RegressionFollowUp> {
        (status == ObjectiveStatus::NotMet).then_some(RegressionFollowUp {
            owner: "TerraScale benchmark lane owner (SE-15 follow-up)",
            rule: REGRESSION_RULE,
        })
    }

    /// Renders the receipt block a performance statement cites.
    pub(crate) fn render(&self) -> String {
        let mut text = String::new();
        let _unused = writeln!(text, "run label: {}", self.label);
        let _unused = writeln!(text, "workload: {}", self.workload.as_str());
        let _unused = writeln!(
            text,
            "lane configuration: seed {} documents, {} operations per sample, {} samples, \
             concurrency {}, warm-up {} operations, noise {}%",
            self.config.seed_documents,
            self.config.operations,
            self.config.samples,
            self.config.concurrency,
            self.config.warmup_operations,
            self.config.noise_percent
        );
        let _unused = writeln!(text, "terrabase v2 source: {}", self.source_identity);
        let _unused =
            writeln!(text, "postgresql pin: {}", reference::POSTGRESQL_REFERENCE.describe());
        let _unused = writeln!(text, "postgresql reference: {}", self.postgresql.describe());
        let _unused = writeln!(text, "verdict: {}", self.verdict.as_str());
        let _unused = writeln!(text, "{}", vocabulary::objective_status_line(self.status));
        if let Some(follow_up) = Self::regression_follow_up(self.status) {
            let _unused = writeln!(text, "regression follow-up owner: {}", follow_up.owner);
            let _unused = writeln!(text, "regression tracking rule: {}", follow_up.rule);
        }
        if let Some((reason, owner)) = self.workload.driver_status() {
            let _unused = writeln!(text, "open adapter slot: {reason} (owner: {owner})");
        }
        for note in &self.notes {
            let _unused = writeln!(text, "note: {note}");
        }
        text
    }
}

/// The TerraBaseV2 source identity a receipt names.
///
/// The adapter cannot observe the working tree's commit, so it names the
/// crate version the engine was built as plus the packet branch, and the
/// evidence receipt records the exact commit beside this line.
pub(crate) fn source_identity() -> String {
    format!(
        "terrabase-v2 {} on branch {}, commit recorded in the evidence receipt",
        crate::VERSION,
        branch_name()
    )
}

/// The packet's branch, or `unresolved` when the tree is not a checkout the
/// adapter can read.
fn branch_name() -> String {
    std::env::var("TERRABASE_BENCH_BRANCH").unwrap_or_else(|_| "unresolved".to_owned())
}
