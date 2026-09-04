//! Active-index monitoring, control, and retirement.
//!
//! The monitor deliberately consumes logical counters rather than elapsed
//! execution time. A small deterministic state machine then turns bounded
//! observations into visibility changes.

use std::{
    collections::{BTreeMap, BTreeSet, VecDeque},
    time::{Duration, Instant},
};

use super::{Candidate, CandidateKind, LogicalCost, Template, later};

/// Monitoring outcome for one measurement window.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum MonitorDecision {
    /// Keep serving the index.
    #[default]
    Keep,
    /// Hide it immediately and schedule a delayed drop.
    MakeInvisible,
    /// It has remained invisible long enough to retire.
    Retire,
}

/// Port for logical-cost regression and unused-index monitoring.
pub(crate) trait IndexMonitor {
    /// Evaluates the active candidate for the current window.
    fn inspect(&mut self, candidate: &Candidate) -> MonitorDecision;
}

/// Why a monitor action was produced.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum MonitorReason {
    /// There is not enough paired evidence yet.
    InsufficientEvidence,
    /// A logical-cost regression crossed both safety thresholds.
    LogicalRegression,
    /// Index maintenance crossed the measured write-cost budget.
    WriteCostRegression,
    /// The index has not served a query for the configured idle period.
    Unused,
    /// An invisible index passed the delayed-drop grace period.
    DropGraceElapsed,
    /// The candidate is still blocked after a failed verification.
    Cooldown,
    /// The candidates have compatible ordered prefixes and enough proof.
    CompatiblePrefix,
    /// The candidates cannot safely be merged.
    IncompatiblePrefix,
    /// A merge did not improve cost by the required hysteresis margin.
    MergeNoImprovement,
    /// A merge would increase maintenance cost beyond its safety margin.
    MergeWriteCost,
}

/// The action named by one audit entry.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum MonitorAction {
    /// No lifecycle mutation.
    Keep,
    /// Flip planner visibility off.
    MakeInvisible,
    /// Physically retire the index after the grace period.
    Retire,
    /// Keep both indexes because a merge was refused.
    KeepSeparate,
    /// Retire the narrow index after its replacement is verified.
    Merge,
}

/// A stable identity for a candidate. Benefit estimates are excluded: two
/// proposals for the same physical shape are one index.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub(crate) struct CandidateIdentity {
    collection: String,
    kind: CandidateKind,
    fields: Vec<String>,
}

impl CandidateIdentity {
    /// Derives the physical identity of a candidate.
    #[must_use]
    pub(crate) fn of(candidate: &Candidate) -> Self {
        Self {
            collection: candidate.collection().to_owned(),
            kind: candidate.kind(),
            fields: candidate.fields().into_iter().map(str::to_owned).collect(),
        }
    }

    /// Returns the candidate collection.
    #[must_use]
    pub(crate) fn collection(&self) -> &str {
        &self.collection
    }

    /// Returns the physical kind.
    #[must_use]
    pub(crate) const fn kind(&self) -> CandidateKind {
        self.kind
    }

    /// Returns the ordered physical fields.
    #[must_use]
    pub(crate) fn fields(&self) -> &[String] {
        &self.fields
    }
}

/// One bounded logical/write-cost observation.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MonitorSample {
    candidate: Option<CandidateIdentity>,
    template: Template,
    logical_cost: LogicalCost,
    write_cost: f64,
    used: bool,
    sequence: u64,
    observed_at: Instant,
}

impl MonitorSample {
    /// Returns the candidate identity, or `None` for a baseline sample.
    #[must_use]
    pub(crate) fn candidate(&self) -> Option<&CandidateIdentity> {
        self.candidate.as_ref()
    }
    /// Returns the workload template.
    #[must_use]
    pub(crate) fn template(&self) -> &Template {
        &self.template
    }
    /// Returns the logical cost.
    #[must_use]
    pub(crate) const fn logical_cost(&self) -> LogicalCost {
        self.logical_cost
    }
    /// Returns measured maintenance cost.
    #[must_use]
    pub(crate) fn write_cost(&self) -> f64 {
        self.write_cost
    }
    /// Returns whether this execution selected the candidate.
    #[must_use]
    pub(crate) const fn used(&self) -> bool {
        self.used
    }
    /// Returns the source sequence.
    #[must_use]
    pub(crate) const fn sequence(&self) -> u64 {
        self.sequence
    }
    /// Returns the injected timestamp.
    #[must_use]
    pub(crate) const fn observed_at(&self) -> Instant {
        self.observed_at
    }
}

/// A clock and sequence source injected by the caller.
pub(crate) trait MonitorClock {
    /// Returns the current monotonic time.
    fn now(&self) -> Instant;
    /// Returns the next durable observation/audit sequence.
    fn next_sequence(&mut self) -> u64;
}

/// Production monotonic clock.
#[derive(Debug)]
pub(crate) struct SystemMonitorClock {
    started: Instant,
    next: u64,
}

impl SystemMonitorClock {
    /// Starts a clock at the current monotonic instant.
    #[must_use]
    pub(crate) fn new() -> Self {
        Self { started: Instant::now(), next: 0 }
    }
}

impl Default for SystemMonitorClock {
    fn default() -> Self {
        Self::new()
    }
}

impl MonitorClock for SystemMonitorClock {
    fn now(&self) -> Instant {
        self.started + self.started.elapsed()
    }
    fn next_sequence(&mut self) -> u64 {
        let sequence = self.next;
        self.next = self.next.saturating_add(1);
        sequence
    }
}

/// A deterministic clock useful for tests and journal replay.
#[derive(Clone, Copy, Debug)]
pub(crate) struct TestMonitorClock {
    now: Instant,
    next: u64,
}

impl TestMonitorClock {
    /// Creates a clock at `now` with sequence zero.
    #[must_use]
    pub(crate) const fn new(now: Instant) -> Self {
        Self { now, next: 0 }
    }
    /// Advances the clock without sleeping.
    pub(crate) fn advance(&mut self, duration: Duration) {
        self.now += duration;
    }
    /// Sets the next sequence for a replay.
    pub(crate) const fn set_sequence(&mut self, next: u64) {
        self.next = next;
    }
}

impl MonitorClock for TestMonitorClock {
    fn now(&self) -> Instant {
        self.now
    }
    fn next_sequence(&mut self) -> u64 {
        let sequence = self.next;
        self.next = self.next.saturating_add(1);
        sequence
    }
}

/// Tunables for the monitor. Defaults include floors so tiny-cost statements
/// and one noisy sample cannot cause a revert.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct MonitorConfig {
    rolling_window: Duration,
    max_samples: usize,
    min_samples: usize,
    regression_fraction: f64,
    regression_floor: f64,
    write_cost_multiple: f64,
    write_cost_floor: f64,
    cooldown: Duration,
    retire_after_unused: Duration,
    drop_grace: Duration,
    merge_improvement_fraction: f64,
    merge_write_tolerance: f64,
}

impl MonitorConfig {
    /// ADR 0045 defaults: one-day evidence, seven-day block/retirement, and
    /// a one-day invisible grace period before physical deletion.
    pub(crate) const DEFAULT: Self = Self {
        rolling_window: Duration::from_secs(24 * 60 * 60),
        max_samples: 4096,
        min_samples: later::SHADOW_MIN_EXECUTIONS,
        regression_fraction: 0.20,
        regression_floor: 8.0,
        write_cost_multiple: later::WRITE_BUDGET_MULTIPLE,
        write_cost_floor: 1.0,
        cooldown: Duration::from_secs(later::BLOCKLIST_DAYS * 24 * 60 * 60),
        retire_after_unused: Duration::from_secs(later::RETIRE_UNUSED_DAYS * 24 * 60 * 60),
        drop_grace: Duration::from_secs(later::REVERT_DROP_DELAY_HOURS * 60 * 60),
        merge_improvement_fraction: 0.05,
        merge_write_tolerance: 0.05,
    };

    /// Creates a configuration with the principal safety thresholds.
    #[must_use]
    pub(crate) const fn new(
        rolling_window: Duration,
        min_samples: usize,
        regression_fraction: f64,
        regression_floor: f64,
        cooldown: Duration,
        retire_after_unused: Duration,
        drop_grace: Duration,
    ) -> Self {
        Self {
            rolling_window,
            max_samples: 4096,
            min_samples,
            regression_fraction,
            regression_floor,
            write_cost_multiple: 2.0,
            write_cost_floor: 1.0,
            cooldown,
            retire_after_unused,
            drop_grace,
            merge_improvement_fraction: 0.05,
            merge_write_tolerance: 0.05,
        }
    }

    /// Returns the default configuration.
    #[must_use]
    pub(crate) const fn default_config() -> Self {
        Self::DEFAULT
    }
    /// Sets the maximum retained samples (zero is clamped to one).
    #[must_use]
    pub(crate) const fn with_max_samples(mut self, value: usize) -> Self {
        self.max_samples = if value == 0 { 1 } else { value };
        self
    }
    /// Sets the minimum paired samples required for a decision.
    #[must_use]
    pub(crate) const fn with_min_samples(mut self, value: usize) -> Self {
        self.min_samples = if value == 0 { 1 } else { value };
        self
    }
    /// Sets the write-cost cap and its absolute floor.
    #[must_use]
    pub(crate) const fn with_write_cost_cap(mut self, multiple: f64, floor: f64) -> Self {
        self.write_cost_multiple = if multiple.is_finite() { multiple.max(1.0) } else { 2.0 };
        self.write_cost_floor = if floor.is_finite() { floor.max(0.0) } else { 1.0 };
        self
    }
    /// Sets merge hysteresis thresholds.
    #[must_use]
    pub(crate) const fn with_merge_thresholds(mut self, improvement: f64, tolerance: f64) -> Self {
        self.merge_improvement_fraction =
            if improvement.is_finite() { improvement.max(0.0) } else { 0.05 };
        self.merge_write_tolerance = if tolerance.is_finite() { tolerance.max(0.0) } else { 0.05 };
        self
    }
    /// Returns the rolling window.
    #[must_use]
    pub(crate) const fn rolling_window(&self) -> Duration {
        self.rolling_window
    }
    /// Returns the sample cap.
    #[must_use]
    pub(crate) const fn max_samples(&self) -> usize {
        self.max_samples
    }
    /// Returns the paired-sample floor.
    #[must_use]
    pub(crate) const fn min_samples(&self) -> usize {
        self.min_samples
    }
}

impl Default for MonitorConfig {
    fn default() -> Self {
        Self::DEFAULT
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CandidateStatus {
    Active,
    Invisible,
    Retired,
}

#[derive(Clone, Copy, Debug)]
struct CandidateState {
    status: CandidateStatus,
    first_seen: Instant,
    last_used: Option<Instant>,
    invisible_at: Option<Instant>,
    blocked_until: Option<Instant>,
}

/// One audit value emitted by a decision.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MonitorAudit {
    candidate: CandidateIdentity,
    action: MonitorAction,
    reason: MonitorReason,
    measured: Option<f64>,
    threshold: Option<f64>,
    sequence: u64,
    observed_at: Instant,
}

impl MonitorAudit {
    /// Returns the affected candidate.
    #[must_use]
    pub(crate) fn candidate(&self) -> &CandidateIdentity {
        &self.candidate
    }
    /// Returns the action.
    #[must_use]
    pub(crate) const fn action(&self) -> MonitorAction {
        self.action
    }
    /// Returns the typed reason.
    #[must_use]
    pub(crate) const fn reason(&self) -> MonitorReason {
        self.reason
    }
    /// Returns the observed value.
    #[must_use]
    pub(crate) const fn measured(&self) -> Option<f64> {
        self.measured
    }
    /// Returns the triggering threshold.
    #[must_use]
    pub(crate) const fn threshold(&self) -> Option<f64> {
        self.threshold
    }
    /// Returns the audit sequence.
    #[must_use]
    pub(crate) const fn sequence(&self) -> u64 {
        self.sequence
    }
    /// Returns the audit timestamp.
    #[must_use]
    pub(crate) const fn observed_at(&self) -> Instant {
        self.observed_at
    }
}

/// A proposed prefix merge and its measured evidence.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MergePlan {
    replacement: Candidate,
    retire: Candidate,
    measured_savings: f64,
    measured_write_delta: f64,
}

impl MergePlan {
    /// Returns the wider replacement candidate.
    #[must_use]
    pub(crate) fn replacement(&self) -> &Candidate {
        &self.replacement
    }
    /// Returns the narrow candidate to retire after verification.
    #[must_use]
    pub(crate) fn retire(&self) -> &Candidate {
        &self.retire
    }
    /// Returns measured logical-cost saving fraction.
    #[must_use]
    pub(crate) const fn measured_savings(&self) -> f64 {
        self.measured_savings
    }
    /// Returns measured maintenance-cost delta.
    #[must_use]
    pub(crate) const fn measured_write_delta(&self) -> f64 {
        self.measured_write_delta
    }
}

/// Result of asking whether two compatible candidates should be merged.
#[derive(Clone, Debug, PartialEq)]
pub(crate) enum MergeDecision {
    /// The wider candidate is safe to verify and replace the narrow one.
    Merge(MergePlan),
    /// No replacement is safe under current evidence.
    KeepSeparate(MonitorReason),
}

/// A bounded rolling monitor and its control state.
#[derive(Debug)]
pub(crate) struct RollingIndexMonitor<C: MonitorClock = SystemMonitorClock> {
    config: MonitorConfig,
    clock: C,
    samples: VecDeque<MonitorSample>,
    states: BTreeMap<CandidateIdentity, CandidateState>,
    blocklist: BTreeMap<(CandidateIdentity, Template), Instant>,
    audits: VecDeque<MonitorAudit>,
}

/// Short name retained for callers that treat the monitor as a port.
pub(crate) type AutoIndexMonitor<C = SystemMonitorClock> = RollingIndexMonitor<C>;

impl RollingIndexMonitor<SystemMonitorClock> {
    /// Creates a monitor using the production monotonic clock.
    #[must_use]
    pub(crate) fn new(config: MonitorConfig) -> Self {
        Self::with_clock(config, SystemMonitorClock::new())
    }
}

impl<C: MonitorClock> RollingIndexMonitor<C> {
    /// Creates a monitor with an injected clock and sequence source.
    #[must_use]
    pub(crate) fn with_clock(config: MonitorConfig, clock: C) -> Self {
        Self {
            config,
            clock,
            samples: VecDeque::new(),
            states: BTreeMap::new(),
            blocklist: BTreeMap::new(),
            audits: VecDeque::new(),
        }
    }

    /// Records a baseline (no candidate selected) observation.
    pub(crate) fn record_baseline(
        &mut self,
        template: Template,
        logical_cost: LogicalCost,
        write_cost: f64,
    ) {
        let sequence = self.clock.next_sequence();
        let observed_at = self.clock.now();
        self.record_at(None, template, logical_cost, write_cost, false, sequence, observed_at);
    }
    /// Records one execution while the candidate was selected.
    pub(crate) fn record_candidate(
        &mut self,
        candidate: &Candidate,
        template: Template,
        logical_cost: LogicalCost,
        write_cost: f64,
    ) {
        let sequence = self.clock.next_sequence();
        let observed_at = self.clock.now();
        self.record_at(
            Some(candidate),
            template,
            logical_cost,
            write_cost,
            true,
            sequence,
            observed_at,
        );
    }
    /// Records a candidate observation with an explicit usage bit.
    pub(crate) fn record(
        &mut self,
        candidate: &Candidate,
        template: Template,
        logical_cost: LogicalCost,
        write_cost: f64,
        used: bool,
    ) {
        let sequence = self.clock.next_sequence();
        let observed_at = self.clock.now();
        self.record_at(
            Some(candidate),
            template,
            logical_cost,
            write_cost,
            used,
            sequence,
            observed_at,
        );
    }
    /// Records an observation with replayed sequence and time.
    pub(crate) fn record_at(
        &mut self,
        candidate: Option<&Candidate>,
        template: Template,
        logical_cost: LogicalCost,
        write_cost: f64,
        used: bool,
        sequence: u64,
        observed_at: Instant,
    ) {
        let key = candidate.map(CandidateIdentity::of);
        let write_cost = if write_cost.is_finite() { write_cost.max(0.0) } else { 0.0 };
        if let Some(candidate) = candidate {
            let identity = CandidateIdentity::of(candidate);
            self.states.entry(identity.clone()).or_insert(CandidateState {
                status: CandidateStatus::Active,
                first_seen: observed_at,
                last_used: None,
                invisible_at: None,
                blocked_until: None,
            });
            if used {
                if let Some(state) = self.states.get_mut(&identity) {
                    state.last_used = Some(observed_at);
                    if state.status == CandidateStatus::Invisible && state.blocked_until.is_none() {
                        state.status = CandidateStatus::Active;
                        state.invisible_at = None;
                    }
                }
            }
        }
        self.samples.push_back(MonitorSample {
            candidate: key,
            template,
            logical_cost,
            write_cost,
            used,
            sequence,
            observed_at,
        });
        self.prune(observed_at);
    }

    /// Inspects a candidate using the injected clock.
    pub(crate) fn inspect_now(&mut self, candidate: &Candidate) -> MonitorDecision {
        let now = self.clock.now();
        let sequence = self.clock.next_sequence();
        self.inspect_at(candidate, now, sequence)
    }
    /// Inspects a candidate at an explicit time and audit sequence.
    pub(crate) fn inspect_at(
        &mut self,
        candidate: &Candidate,
        now: Instant,
        sequence: u64,
    ) -> MonitorDecision {
        self.prune(now);
        let identity = CandidateIdentity::of(candidate);
        let state = self.states.entry(identity.clone()).or_insert(CandidateState {
            status: CandidateStatus::Active,
            first_seen: now,
            last_used: None,
            invisible_at: None,
            blocked_until: None,
        });
        let status = state.status;
        let blocked_until = state.blocked_until;
        let invisible_at = state.invisible_at;
        let last_used = state.last_used;
        let first_seen = state.first_seen;
        if status == CandidateStatus::Retired {
            return MonitorDecision::Keep;
        }
        if let Some(until) = blocked_until {
            if now < until {
                self.audit(
                    identity,
                    MonitorAction::Keep,
                    MonitorReason::Cooldown,
                    None,
                    None,
                    sequence,
                    now,
                );
                return MonitorDecision::Keep;
            }
        }
        if status == CandidateStatus::Invisible {
            if let Some(invisible_at) = invisible_at {
                if now.duration_since(invisible_at) >= self.config.drop_grace {
                    if let Some(state) = self.states.get_mut(&identity) {
                        state.status = CandidateStatus::Retired;
                    }
                    self.audit(
                        identity,
                        MonitorAction::Retire,
                        MonitorReason::DropGraceElapsed,
                        Some(now.duration_since(invisible_at).as_secs_f64()),
                        Some(self.config.drop_grace.as_secs_f64()),
                        sequence,
                        now,
                    );
                    return MonitorDecision::Retire;
                }
            }
            return MonitorDecision::Keep;
        }
        if let Some((reason, measured, threshold, template_keys)) =
            self.regression_evidence(&identity)
        {
            let blocked_until = now + self.config.cooldown;
            if let Some(state) = self.states.get_mut(&identity) {
                state.status = CandidateStatus::Invisible;
                state.invisible_at = Some(now);
                state.blocked_until = Some(blocked_until);
            }
            for template in template_keys {
                self.blocklist.insert((identity.clone(), template), blocked_until);
            }
            self.audit(
                identity,
                MonitorAction::MakeInvisible,
                reason,
                Some(measured),
                Some(threshold),
                sequence,
                now,
            );
            return MonitorDecision::MakeInvisible;
        }
        let unused_for = now.duration_since(first_seen);
        if last_used.is_none() && unused_for >= self.config.retire_after_unused {
            if let Some(state) = self.states.get_mut(&identity) {
                state.status = CandidateStatus::Invisible;
                state.invisible_at = Some(now);
            }
            self.audit(
                identity,
                MonitorAction::MakeInvisible,
                MonitorReason::Unused,
                Some(unused_for.as_secs_f64()),
                Some(self.config.retire_after_unused.as_secs_f64()),
                sequence,
                now,
            );
            return MonitorDecision::MakeInvisible;
        }
        self.audit(
            identity,
            MonitorAction::Keep,
            MonitorReason::InsufficientEvidence,
            None,
            None,
            sequence,
            now,
        );
        MonitorDecision::Keep
    }
    /// Returns the next action according to the [`IndexMonitor`] port.
    pub(crate) fn inspect(&mut self, candidate: &Candidate) -> MonitorDecision {
        self.inspect_now(candidate)
    }

    /// Returns whether an index/candidate is currently blocked.
    #[must_use]
    pub(crate) fn is_blocklisted(&mut self, candidate: &Candidate) -> bool {
        let now = self.clock.now();
        let identity = CandidateIdentity::of(candidate);
        self.blocklist.retain(|(key, _), until| key != &identity || *until > now);
        self.blocklist.keys().any(|(key, _)| key == &identity)
    }
    /// Returns all retained samples, oldest first.
    #[must_use]
    pub(crate) fn samples(&self) -> Vec<&MonitorSample> {
        self.samples.iter().collect()
    }
    /// Returns the bounded audit history, oldest first.
    #[must_use]
    pub(crate) fn audits(&self) -> Vec<&MonitorAudit> {
        self.audits.iter().collect()
    }
    /// Returns whether the candidate is invisible or retired.
    #[must_use]
    pub(crate) fn is_invisible(&self, candidate: &Candidate) -> bool {
        self.states
            .get(&CandidateIdentity::of(candidate))
            .is_some_and(|state| state.status != CandidateStatus::Active)
    }

    /// Returns a prefix merge decision based on this monitor's evidence.
    pub(crate) fn consider_merge(&mut self, narrow: &Candidate, wide: &Candidate) -> MergeDecision {
        let now = self.clock.now();
        let sequence = self.clock.next_sequence();
        let narrow_id = CandidateIdentity::of(narrow);
        if !compatible_prefix(narrow, wide) {
            self.audit(
                narrow_id,
                MonitorAction::KeepSeparate,
                MonitorReason::IncompatiblePrefix,
                None,
                None,
                sequence,
                now,
            );
            return MergeDecision::KeepSeparate(MonitorReason::IncompatiblePrefix);
        }
        let wide_id = CandidateIdentity::of(wide);
        let templates: BTreeSet<Template> = self
            .samples
            .iter()
            .filter(|sample| sample.candidate.as_ref() == Some(&narrow_id) && sample.used)
            .map(|sample| sample.template.clone())
            .collect();
        let mut narrow_cost = 0.0;
        let mut wide_cost = 0.0;
        let mut narrow_write = 0.0;
        let mut wide_write = 0.0;
        let mut paired = 0_usize;
        for template in templates {
            let left: Vec<&MonitorSample> = self
                .samples
                .iter()
                .filter(|sample| {
                    sample.candidate.as_ref() == Some(&narrow_id)
                        && sample.used
                        && sample.template == template
                })
                .collect();
            let right: Vec<&MonitorSample> = self
                .samples
                .iter()
                .filter(|sample| {
                    sample.candidate.as_ref() == Some(&wide_id)
                        && sample.used
                        && sample.template == template
                })
                .collect();
            if left.len() < self.config.min_samples || right.len() < self.config.min_samples {
                continue;
            }
            paired += left.len().min(right.len());
            narrow_cost += mean_logical(&left);
            wide_cost += mean_logical(&right);
            narrow_write += mean_write(&left);
            wide_write += mean_write(&right);
        }
        if paired < self.config.min_samples {
            self.audit(
                narrow_id,
                MonitorAction::KeepSeparate,
                MonitorReason::InsufficientEvidence,
                None,
                Some(self.config.min_samples as f64),
                sequence,
                now,
            );
            return MergeDecision::KeepSeparate(MonitorReason::InsufficientEvidence);
        }
        let savings = if narrow_cost > 0.0 { (narrow_cost - wide_cost) / narrow_cost } else { 0.0 };
        let write_delta = wide_write - narrow_write;
        if savings < self.config.merge_improvement_fraction {
            self.audit(
                narrow_id,
                MonitorAction::KeepSeparate,
                MonitorReason::MergeNoImprovement,
                Some(savings),
                Some(self.config.merge_improvement_fraction),
                sequence,
                now,
            );
            return MergeDecision::KeepSeparate(MonitorReason::MergeNoImprovement);
        }
        if write_delta
            > narrow_write.max(self.config.write_cost_floor) * self.config.merge_write_tolerance
        {
            self.audit(
                narrow_id,
                MonitorAction::KeepSeparate,
                MonitorReason::MergeWriteCost,
                Some(write_delta),
                Some(
                    narrow_write.max(self.config.write_cost_floor)
                        * self.config.merge_write_tolerance,
                ),
                sequence,
                now,
            );
            return MergeDecision::KeepSeparate(MonitorReason::MergeWriteCost);
        }
        self.audit(
            narrow_id,
            MonitorAction::Merge,
            MonitorReason::CompatiblePrefix,
            Some(savings),
            Some(self.config.merge_improvement_fraction),
            sequence,
            now,
        );
        MergeDecision::Merge(MergePlan {
            replacement: wide.clone(),
            retire: narrow.clone(),
            measured_savings: savings,
            measured_write_delta: write_delta,
        })
    }

    fn regression_evidence(
        &self,
        identity: &CandidateIdentity,
    ) -> Option<(MonitorReason, f64, f64, Vec<Template>)> {
        let templates: BTreeSet<Template> = self
            .samples
            .iter()
            .filter(|sample| sample.candidate.as_ref() == Some(identity))
            .map(|sample| sample.template.clone())
            .collect();
        let mut write_regression = None;
        for template in templates {
            let candidate: Vec<&MonitorSample> = self
                .samples
                .iter()
                .filter(|sample| {
                    sample.candidate.as_ref() == Some(identity)
                        && sample.used
                        && sample.template == template
                })
                .collect();
            let baseline: Vec<&MonitorSample> = self
                .samples
                .iter()
                .filter(|sample| sample.candidate.is_none() && sample.template == template)
                .collect();
            if candidate.len() < self.config.min_samples || baseline.len() < self.config.min_samples
            {
                continue;
            }
            let candidate_mean = mean_logical(&candidate);
            let baseline_mean = mean_logical(&baseline);
            let threshold = baseline_mean * (1.0 + self.config.regression_fraction);
            if candidate_mean > threshold
                && candidate_mean - baseline_mean >= self.config.regression_floor
            {
                return Some((
                    MonitorReason::LogicalRegression,
                    candidate_mean,
                    threshold,
                    vec![template],
                ));
            }
            let candidate_write = mean_write(&candidate);
            let baseline_write = mean_write(&baseline);
            let write_threshold =
                baseline_write * self.config.write_cost_multiple + self.config.write_cost_floor;
            if candidate_write > write_threshold {
                write_regression = Some((
                    MonitorReason::WriteCostRegression,
                    candidate_write,
                    write_threshold,
                    vec![template],
                ));
            }
        }
        write_regression
    }

    fn prune(&mut self, now: Instant) {
        while self.samples.len() > self.config.max_samples {
            self.samples.pop_front();
        }
        while self.samples.front().is_some_and(|sample| {
            now.duration_since(sample.observed_at) > self.config.rolling_window
        }) {
            self.samples.pop_front();
        }
        self.blocklist.retain(|_, until| *until > now);
        while self.audits.len() > self.config.max_samples.min(256) {
            self.audits.pop_front();
        }
    }
    fn audit(
        &mut self,
        candidate: CandidateIdentity,
        action: MonitorAction,
        reason: MonitorReason,
        measured: Option<f64>,
        threshold: Option<f64>,
        sequence: u64,
        observed_at: Instant,
    ) {
        self.audits.push_back(MonitorAudit {
            candidate,
            action,
            reason,
            measured,
            threshold,
            sequence,
            observed_at,
        });
        while self.audits.len() > self.config.max_samples.min(256) {
            self.audits.pop_front();
        }
    }
}

impl<C: MonitorClock> IndexMonitor for RollingIndexMonitor<C> {
    fn inspect(&mut self, candidate: &Candidate) -> MonitorDecision {
        self.inspect_now(candidate)
    }
}

/// Returns whether `narrow` is a strict ordered prefix of `wide`.
#[must_use]
pub(crate) fn compatible_prefix(narrow: &Candidate, wide: &Candidate) -> bool {
    narrow.collection() == wide.collection()
        && narrow.kind() == CandidateKind::BTree
        && wide.kind() == CandidateKind::BTree
        && narrow.columns().len() < wide.columns().len()
        && narrow.fields().iter().zip(wide.fields()).all(|(left, right)| *left == right)
}

#[allow(
    clippy::cast_precision_loss,
    reason = "bounded logical counters; precision is below measurement noise"
)]
fn mean_logical(samples: &[&MonitorSample]) -> f64 {
    if samples.is_empty() {
        return 0.0;
    }
    samples.iter().map(|sample| sample.logical_cost.total() as f64).sum::<f64>()
        / samples.len() as f64
}
fn mean_write(samples: &[&MonitorSample]) -> f64 {
    if samples.is_empty() {
        return 0.0;
    }
    samples.iter().map(|sample| sample.write_cost).sum::<f64>() / samples.len() as f64
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::query::optimizer::auto_index::{
        CandidateColumn, CandidateSource, ColumnRole, PredicateKind, TemplatePredicate,
    };

    fn template(field: &str) -> Template {
        Template::try_new(
            field,
            vec![TemplatePredicate::try_new(field, PredicateKind::Equality).expect("field")],
            vec![],
            None,
        )
        .expect("template")
    }
    fn candidate(fields: &[&str]) -> Candidate {
        Candidate {
            collection: "items".to_owned(),
            columns: fields
                .iter()
                .map(|field| CandidateColumn::try_new(field, ColumnRole::Equality).expect("field"))
                .collect(),
            kind: CandidateKind::BTree,
            source: CandidateSource::Workload,
            needs_descriptor: false,
            benefit_per_byte: 1.0,
        }
    }
    fn monitor(
        min_samples: usize,
        max_samples: usize,
    ) -> (RollingIndexMonitor<TestMonitorClock>, Instant) {
        let now = Instant::now();
        let config = MonitorConfig::default_config()
            .with_min_samples(min_samples)
            .with_max_samples(max_samples)
            .with_merge_thresholds(0.05, 0.10);
        (RollingIndexMonitor::with_clock(config, TestMonitorClock::new(now)), now)
    }

    #[test]
    fn regression_is_immediate_but_noise_is_kept() {
        let (mut monitor, _) = monitor(3, 64);
        let index = candidate(&["tenant"]);
        let shape = template("tenant");
        for _ in 0..3 {
            monitor.record_baseline(shape.clone(), LogicalCost::new(0, 100), 1.0);
        }
        for _ in 0..3 {
            monitor.record_candidate(&index, shape.clone(), LogicalCost::new(0, 105), 1.0);
        }
        assert_eq!(monitor.inspect(&index), MonitorDecision::Keep);
        for _ in 0..3 {
            monitor.record_candidate(&index, shape.clone(), LogicalCost::new(0, 140), 1.0);
        }
        assert_eq!(monitor.inspect(&index), MonitorDecision::MakeInvisible);
        assert!(monitor.is_invisible(&index));
        assert_eq!(
            monitor.audits().last().expect("audit").reason(),
            MonitorReason::LogicalRegression
        );
    }

    #[test]
    fn cooldown_blocks_repeated_control_and_grace_retires() {
        let (mut monitor, now) = monitor(1, 32);
        let index = candidate(&["tenant"]);
        let shape = template("tenant");
        monitor.record_baseline(shape.clone(), LogicalCost::new(0, 10), 1.0);
        monitor.record_candidate(&index, shape, LogicalCost::new(0, 30), 1.0);
        assert_eq!(monitor.inspect(&index), MonitorDecision::MakeInvisible);
        assert!(monitor.is_blocklisted(&index));
        assert_eq!(monitor.inspect(&index), MonitorDecision::Keep);
        assert_eq!(monitor.audits().last().expect("audit").reason(), MonitorReason::Cooldown);
        let mut clock = TestMonitorClock::new(now);
        clock.advance(Duration::from_secs(7 * 24 * 60 * 60 + 1));
        let config = MonitorConfig::default_config().with_min_samples(1);
        let mut replay = RollingIndexMonitor::with_clock(config, clock);
        replay.record_at(
            Some(&index),
            template("tenant"),
            LogicalCost::new(0, 1),
            0.0,
            false,
            1,
            now,
        );
        assert_eq!(
            replay.inspect_at(&index, now + Duration::from_secs(7 * 24 * 60 * 60 + 1), 2),
            MonitorDecision::MakeInvisible
        );
        assert_eq!(
            replay.inspect_at(&index, now + Duration::from_secs(8 * 24 * 60 * 60 + 1), 3),
            MonitorDecision::Retire
        );
    }

    #[test]
    fn compatible_prefix_merge_requires_savings_and_is_audited() {
        let (mut monitor, _) = monitor(2, 128);
        let narrow = candidate(&["tenant"]);
        let wide = candidate(&["tenant", "created"]);
        let shape = template("tenant");
        assert!(compatible_prefix(&narrow, &wide));
        for _ in 0..2 {
            monitor.record_candidate(&narrow, shape.clone(), LogicalCost::new(0, 100), 10.0);
            monitor.record_candidate(&wide, shape.clone(), LogicalCost::new(0, 80), 10.2);
        }
        assert!(matches!(monitor.consider_merge(&narrow, &wide), MergeDecision::Merge(_)));
        assert_eq!(
            monitor.audits().last().expect("audit").reason(),
            MonitorReason::CompatiblePrefix
        );
        assert!(!compatible_prefix(&wide, &narrow));
    }

    #[test]
    fn sample_ring_is_bounded_and_windowed() {
        let (mut monitor, now) = monitor(1, 3);
        let index = candidate(&["tenant"]);
        for sequence in 0..10 {
            monitor.record_at(
                Some(&index),
                template("tenant"),
                LogicalCost::new(0, sequence),
                1.0,
                true,
                sequence,
                now,
            );
        }
        assert_eq!(monitor.samples().len(), 3);
        let old = now + Duration::from_secs(24 * 60 * 60 + 1);
        monitor.record_at(
            Some(&index),
            template("tenant"),
            LogicalCost::new(0, 1),
            1.0,
            true,
            10,
            old,
        );
        assert_eq!(monitor.samples().len(), 1);
    }
}
