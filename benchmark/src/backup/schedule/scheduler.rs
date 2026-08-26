//! The periodic backup scheduler (PB-6011): a typed, agent-drivable
//! state machine over the BK-01 run seam.
//!
//! The engine deliberately runs **no timers**. Platform-owned task
//! controls disposition to the Platform (BK-04's ticket line), so the
//! scheduler is a deterministic state machine driven by typed operations:
//! the backend — or an agent through it, per PD-2026-08-26-11 D2 — supplies
//! `now` as a caller tick, and [`ScheduleDriver::run_due`] decides,
//! executes, and records. All cadence facts live in one clock domain,
//! caller ticks; retention ages live in the destination's wall-clock
//! domain and are injected separately, so no test or status readout ever
//! depends on a hidden clock.
//!
//! # Cadence
//!
//! A schedule is a full interval plus an optional incremental interval.
//! The first run of a schedule is always a full — an incremental with no
//! sealed base fails closed at the run seam, so the scheduler never
//! mints one. Incrementals chain on the newest sealed point's boundary
//! until the full interval rotates the chain; an incremental window with
//! no new journal since that boundary is a recorded no-op that rearms, so
//! a quiet source seals nothing rather than chains of empty links.
//!
//! # Missed-run policy
//!
//! [`MissedRunPolicy::RunOneNow`] executes a late boundary immediately,
//! however late, and re-anchors the cadence at the completion time.
//! [`MissedRunPolicy::SkipUntilNextWindow`] never backfills: boundaries
//! whose whole window passed unexecuted are absorbed as skips, the grid
//! never drifts, and execution happens only when a driven tick lands on a
//! boundary.
//!
//! # Pause and status
//!
//! Pause and resume are typed operations; a paused schedule never runs
//! and its cadence anchors stand still, so resume re-applies the
//! missed-run policy to whatever accrued. Status is a typed read carrying
//! phases, boundaries, identities, counts, and the last typed failure —
//! never a destination path, never key material, never archive content.

use std::time::SystemTime;

use crate::backup::schedule::codec::{
    ArtifactBundle, BundleOptions, EncryptionError, EncryptionKey, seal_artifact_bundle,
};
use crate::backup::schedule::destination::{
    DestinationConfig, DestinationError, DestinationInvalid,
};
use crate::backup::schedule::retention::{
    PrunedPoint, RetentionFailure, RetentionPolicy, RetentionReceipt, enforce_retention,
};
use crate::backup::{BackupError, BackupKind, BackupPlan, run_backup};
use crate::engine::Engine;
use crate::types::StorageSequence;

/// The caller-supplied cadence clock: a monotonically nondecreasing tick.
/// The engine never reads a clock for cadence; the driving caller owns it.
pub type ScheduleTick = u64;

/// The private classification of one driven tick, shared by the pure
/// decision and the driving execution so they can never disagree.
enum Classification {
    /// The schedule is paused.
    Paused,
    /// No recovery point exists yet; the first run must be a full.
    FirstFull,
    /// A full boundary is due under the missed-run policy.
    FullDue,
    /// An incremental boundary is time-due against the newest point's
    /// boundary, and whether the source has new journal to archive.
    IncrementalDue {
        /// The base boundary the incremental would chain on.
        base: StorageSequence,
        /// Whether the source has journal the base does not cover.
        has_journal: bool,
    },
    /// Nothing is due; the next candidate tick, when one exists.
    NotDue {
        /// The next tick at which something may run.
        next_due: Option<ScheduleTick>,
    },
}

/// How a schedule treats a cadence boundary it failed to execute on time.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MissedRunPolicy {
    /// Execute one late boundary immediately, however late, and re-anchor
    /// the cadence at the completion time (catch-up).
    RunOneNow,
    /// Never backfill: a boundary whose whole window passed unexecuted is
    /// absorbed as a skip, the grid never drifts, and execution happens
    /// only at boundaries the driven ticks land on.
    SkipUntilNextWindow,
}

/// Why a schedule or its destination is invalid at construction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ScheduleInvalid {
    /// A cadence interval is zero.
    ZeroInterval,
    /// The destination failed validation, with its typed reason.
    Destination(DestinationInvalid),
    /// The schedule seals encrypted bundles, so every run must be supplied
    /// a key; none was.
    EncryptionKeyRequired,
}

/// The typed failure of one schedule operation or run.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ScheduleError {
    /// The schedule or destination was invalid.
    Invalid(ScheduleInvalid),
    /// The backup run failed with its typed cause.
    Backup(BackupError),
    /// The destination probe or write failed.
    Destination(DestinationError),
    /// Sealing or opening a bundle failed.
    Encryption(EncryptionError),
    /// The retention sweep failed.
    Retention(RetentionFailure),
}

/// A periodic backup schedule: cadence, missed-run policy, artifact
/// transforms, and retention.
///
/// The value is immutable after construction; the `with_*` builders
/// return a new schedule.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BackupSchedule {
    full_interval: ScheduleTick,
    incremental_interval: Option<ScheduleTick>,
    missed_run: MissedRunPolicy,
    compression: bool,
    encryption: bool,
    retention: RetentionPolicy,
}

impl BackupSchedule {
    /// Schedules a full backup every `ticks` cadence ticks.
    #[must_use]
    pub fn full_every(ticks: ScheduleTick) -> Self {
        Self {
            full_interval: ticks,
            incremental_interval: None,
            missed_run: MissedRunPolicy::RunOneNow,
            compression: false,
            encryption: false,
            retention: RetentionPolicy::disabled(),
        }
    }

    /// Adds an incremental cadence every `ticks` cadence ticks. A due
    /// incremental runs only when new journal exists since the newest
    /// sealed point.
    #[must_use]
    pub fn with_incremental_every(mut self, ticks: ScheduleTick) -> Self {
        self.incremental_interval = Some(ticks);
        self
    }

    /// Sets the missed-run policy.
    #[must_use]
    pub fn with_missed_run_policy(mut self, policy: MissedRunPolicy) -> Self {
        self.missed_run = policy;
        self
    }

    /// Seals a compressed artifact bundle beside every run's recovery
    /// point.
    #[must_use]
    pub fn with_compression(mut self, enabled: bool) -> Self {
        self.compression = enabled;
        self
    }

    /// Seals an encrypted (and digest-bound) artifact bundle beside every
    /// run's recovery point. The key is supplied per operation and never
    /// stored; a run without a key is a typed failure.
    #[must_use]
    pub fn with_encryption(mut self, enabled: bool) -> Self {
        self.encryption = enabled;
        self
    }

    /// Enforces a retention policy after every successful run.
    #[must_use]
    pub fn with_retention(mut self, policy: RetentionPolicy) -> Self {
        self.retention = policy;
        self
    }

    /// Returns the full cadence interval.
    #[must_use]
    pub fn full_interval(&self) -> ScheduleTick {
        self.full_interval
    }

    /// Returns the incremental cadence interval, if any.
    #[must_use]
    pub fn incremental_interval(&self) -> Option<ScheduleTick> {
        self.incremental_interval
    }

    /// Returns the missed-run policy.
    #[must_use]
    pub fn missed_run_policy(&self) -> MissedRunPolicy {
        self.missed_run
    }

    /// Whether runs seal compressed bundles.
    #[must_use]
    pub fn compression_enabled(&self) -> bool {
        self.compression
    }

    /// Whether runs seal encrypted bundles.
    #[must_use]
    pub fn encryption_enabled(&self) -> bool {
        self.encryption
    }

    /// Returns the retention policy.
    #[must_use]
    pub fn retention_policy(&self) -> RetentionPolicy {
        self.retention
    }

    /// Validates the cadence intervals.
    ///
    /// # Errors
    ///
    /// Returns [`ScheduleInvalid::ZeroInterval`] when an interval is zero.
    pub fn validate(&self) -> Result<(), ScheduleInvalid> {
        if self.full_interval == 0 || self.incremental_interval.is_some_and(|ticks| ticks == 0) {
            return Err(ScheduleInvalid::ZeroInterval);
        }
        Ok(())
    }
}

/// One completed run, receipted without archive content.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CompletedRun {
    kind: BackupKind,
    identity: String,
    boundary: StorageSequence,
    completed_at: ScheduleTick,
}

impl CompletedRun {
    /// Creates the fact set of one completed run.
    fn new(
        kind: BackupKind,
        identity: String,
        boundary: StorageSequence,
        completed_at: ScheduleTick,
    ) -> Self {
        Self { kind, identity, boundary, completed_at }
    }

    /// Returns the run's kind.
    #[must_use]
    pub fn kind(&self) -> BackupKind {
        self.kind
    }

    /// Returns the sealed recovery point's identity.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the sealed recovery point's boundary.
    #[must_use]
    pub fn boundary(&self) -> StorageSequence {
        self.boundary
    }

    /// Returns the tick the run completed at.
    #[must_use]
    pub fn completed_at(&self) -> ScheduleTick {
        self.completed_at
    }
}

/// One recorded failure: the typed cause and the tick it was recorded at.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScheduleFailure {
    cause: ScheduleError,
    at: ScheduleTick,
}

impl ScheduleFailure {
    /// Records one failure.
    fn new(cause: ScheduleError, at: ScheduleTick) -> Self {
        Self { cause, at }
    }

    /// Returns the typed cause.
    #[must_use]
    pub fn cause(&self) -> ScheduleError {
        self.cause
    }

    /// Returns the tick the failure was recorded at.
    #[must_use]
    pub fn at(&self) -> ScheduleTick {
        self.at
    }
}

/// The summary of one retention sweep a run enforced.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RetentionSummary {
    pruned: u64,
    kept_chains: u64,
    at: ScheduleTick,
}

impl RetentionSummary {
    /// Summarizes one sweep.
    fn new(pruned: u64, kept_chains: u64, at: ScheduleTick) -> Self {
        Self { pruned, kept_chains, at }
    }

    /// Returns the number of pruned recovery points.
    #[must_use]
    pub fn pruned(&self) -> u64 {
        self.pruned
    }

    /// Returns the number of kept chain roots.
    #[must_use]
    pub fn kept_chains(&self) -> u64 {
        self.kept_chains
    }

    /// Returns the tick the sweep ran at.
    #[must_use]
    pub fn at(&self) -> ScheduleTick {
        self.at
    }
}

/// The persisted-shape state of one schedule. The caller owns durability:
/// the engine keeps no scheduler state, so the backend stores this value
/// between driven ticks and hands it back to every operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScheduleState {
    created_at: ScheduleTick,
    paused_at: Option<ScheduleTick>,
    full_anchor: ScheduleTick,
    next_incremental_from: ScheduleTick,
    last_full: Option<CompletedRun>,
    last_point: Option<CompletedRun>,
    last_error: Option<ScheduleFailure>,
    last_retention: Option<RetentionSummary>,
    completed_runs: u64,
    skipped_noop_runs: u64,
}

impl ScheduleState {
    /// Creates the state of a schedule born at `at`. Nothing has run.
    #[must_use]
    pub fn new(at: ScheduleTick) -> Self {
        Self {
            created_at: at,
            paused_at: None,
            full_anchor: at,
            next_incremental_from: at,
            last_full: None,
            last_point: None,
            last_error: None,
            last_retention: None,
            completed_runs: 0,
            skipped_noop_runs: 0,
        }
    }

    /// Returns the tick this schedule's state was created at.
    #[must_use]
    pub fn created_at(&self) -> ScheduleTick {
        self.created_at
    }

    /// Returns the tick the schedule was paused at, if paused.
    #[must_use]
    pub fn paused_at(&self) -> Option<ScheduleTick> {
        self.paused_at
    }

    /// Returns the newest completed run, whatever its kind.
    #[must_use]
    pub fn last_point(&self) -> Option<&CompletedRun> {
        self.last_point.as_ref()
    }

    /// Returns the last completed full run.
    #[must_use]
    pub fn last_full(&self) -> Option<&CompletedRun> {
        self.last_full.as_ref()
    }

    /// Returns the last recorded failure, cleared by the next success.
    #[must_use]
    pub fn last_error(&self) -> Option<&ScheduleFailure> {
        self.last_error.as_ref()
    }

    /// Returns the retention summary of the last enforcing run.
    #[must_use]
    pub fn last_retention(&self) -> Option<RetentionSummary> {
        self.last_retention
    }

    /// Returns how many runs completed, including idempotent re-seals.
    #[must_use]
    pub fn completed_runs(&self) -> u64 {
        self.completed_runs
    }

    /// Returns how many due incremental windows produced no run because
    /// the source had no new journal.
    #[must_use]
    pub fn skipped_noop_runs(&self) -> u64 {
        self.skipped_noop_runs
    }
}

/// The pure decision of one driven tick.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ScheduleDecision {
    /// A run of this kind is due now.
    Run(BackupKind),
    /// Nothing is runnable; the next candidate tick, if the source ever
    /// has new journal or time simply advances to it.
    Idle {
        /// The next tick at which something may run.
        next_due: Option<ScheduleTick>,
    },
    /// The schedule is paused.
    Paused,
}

/// The phase of a schedule as status reports it.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SchedulePhase {
    /// No recovery point exists yet; the first run must be a full.
    NeedsFirstFull,
    /// Nothing due; the next candidate tick, if any.
    Idle {
        /// The next tick at which something may run.
        next_due: Option<ScheduleTick>,
    },
    /// A run of this kind is due at the next driven tick.
    Due {
        /// The kind that is due.
        kind: BackupKind,
    },
    /// Paused since the recorded tick.
    Paused {
        /// The tick the pause was recorded at.
        since: ScheduleTick,
    },
}

/// The typed status read of one schedule (PB-6011, PB-6021): phases,
/// cadence facts, the newest completed runs, counts, the last typed
/// failure, and the last retention summary. It never carries a
/// destination path, key material, or archive content.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScheduleStatus {
    phase: SchedulePhase,
    full_interval: ScheduleTick,
    incremental_interval: Option<ScheduleTick>,
    missed_run: MissedRunPolicy,
    encryption_enabled: bool,
    compression_enabled: bool,
    destination_kind: crate::backup::schedule::destination::DestinationKind,
    last_full: Option<CompletedRun>,
    last_point: Option<CompletedRun>,
    last_error: Option<ScheduleFailure>,
    last_retention: Option<RetentionSummary>,
    completed_runs: u64,
    skipped_noop_runs: u64,
}

impl ScheduleStatus {
    /// Returns the schedule's phase at the read's tick.
    #[must_use]
    pub fn phase(&self) -> SchedulePhase {
        self.phase
    }

    /// Returns the full cadence interval.
    #[must_use]
    pub fn full_interval(&self) -> ScheduleTick {
        self.full_interval
    }

    /// Returns the incremental cadence interval, if any.
    #[must_use]
    pub fn incremental_interval(&self) -> Option<ScheduleTick> {
        self.incremental_interval
    }

    /// Returns the missed-run policy.
    #[must_use]
    pub fn missed_run_policy(&self) -> MissedRunPolicy {
        self.missed_run
    }

    /// Whether runs seal encrypted bundles. The key is supplied per
    /// operation and never stored; status carries this flag only.
    #[must_use]
    pub fn encryption_enabled(&self) -> bool {
        self.encryption_enabled
    }

    /// Whether runs seal compressed bundles.
    #[must_use]
    pub fn compression_enabled(&self) -> bool {
        self.compression_enabled
    }

    /// Returns the destination's kind — never its path.
    #[must_use]
    pub fn destination_kind(&self) -> crate::backup::schedule::destination::DestinationKind {
        self.destination_kind
    }

    /// Returns the last completed full run.
    #[must_use]
    pub fn last_full(&self) -> Option<&CompletedRun> {
        self.last_full.as_ref()
    }

    /// Returns the newest completed run.
    #[must_use]
    pub fn last_point(&self) -> Option<&CompletedRun> {
        self.last_point.as_ref()
    }

    /// Returns the last recorded failure.
    #[must_use]
    pub fn last_error(&self) -> Option<&ScheduleFailure> {
        self.last_error.as_ref()
    }

    /// Returns the last retention summary.
    #[must_use]
    pub fn last_retention(&self) -> Option<RetentionSummary> {
        self.last_retention
    }

    /// Returns how many runs completed.
    #[must_use]
    pub fn completed_runs(&self) -> u64 {
        self.completed_runs
    }

    /// Returns how many due incremental windows were recorded as no-ops.
    #[must_use]
    pub fn skipped_noop_runs(&self) -> u64 {
        self.skipped_noop_runs
    }
}

/// The outcome of one executed run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScheduleRunOutcome {
    run: CompletedRun,
    bundle: Option<ArtifactBundle>,
    retention: Option<RetentionReceipt>,
}

impl ScheduleRunOutcome {
    /// Assembles one outcome.
    fn new(
        run: CompletedRun,
        bundle: Option<ArtifactBundle>,
        retention: Option<RetentionReceipt>,
    ) -> Self {
        Self { run, bundle, retention }
    }

    /// Returns the completed run's facts.
    #[must_use]
    pub fn run(&self) -> &CompletedRun {
        &self.run
    }

    /// Returns the sealed bundle's facts when the schedule seals bundles.
    #[must_use]
    pub fn bundle(&self) -> Option<&ArtifactBundle> {
        self.bundle.as_ref()
    }

    /// Returns the retention receipt when the schedule enforces retention.
    #[must_use]
    pub fn retention(&self) -> Option<&RetentionReceipt> {
        self.retention.as_ref()
    }
}

/// The typed failure of one driven run, with whatever completed before it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScheduleRunError {
    cause: ScheduleError,
    completed_run: Option<CompletedRun>,
    pruned_before_failure: Vec<PrunedPoint>,
}

impl ScheduleRunError {
    /// Assembles one run failure.
    fn new(
        cause: ScheduleError,
        completed_run: Option<CompletedRun>,
        pruned_before_failure: Vec<PrunedPoint>,
    ) -> Self {
        Self { cause, completed_run, pruned_before_failure }
    }

    /// Returns the typed cause.
    #[must_use]
    pub fn cause(&self) -> ScheduleError {
        self.cause
    }

    /// Returns the run that sealed before the failure, when one did.
    #[must_use]
    pub fn completed_run(&self) -> Option<&CompletedRun> {
        self.completed_run.as_ref()
    }

    /// Returns what retention pruned before failing, when it failed.
    #[must_use]
    pub fn pruned_before_failure(&self) -> &[PrunedPoint] {
        &self.pruned_before_failure
    }
}

/// The driver of one schedule against one destination: the typed
/// operation surface the backend calls.
///
/// Construction validates the schedule and the destination once, so every
/// driven tick after that fails only on runtime causes. The driver holds
/// no mutable state of its own — all state travels with the caller-owned
/// [`ScheduleState`] — and it never stores a key.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScheduleDriver {
    schedule: BackupSchedule,
    destination: DestinationConfig,
}

impl ScheduleDriver {
    /// Builds the driver of `schedule` against `destination`.
    ///
    /// # Errors
    ///
    /// Returns the typed validation failure of the schedule or the
    /// destination.
    pub fn new(
        schedule: BackupSchedule,
        destination: DestinationConfig,
    ) -> Result<Self, ScheduleError> {
        schedule.validate().map_err(ScheduleError::Invalid)?;
        destination.validate().map_err(|error| match error {
            DestinationError::Invalid(reason) => {
                ScheduleError::Invalid(ScheduleInvalid::Destination(reason))
            }
            DestinationError::Unavailable => {
                ScheduleError::Destination(DestinationError::Unavailable)
            }
        })?;
        Ok(Self { schedule, destination })
    }

    /// Returns the driven schedule.
    #[must_use]
    pub fn schedule(&self) -> &BackupSchedule {
        &self.schedule
    }

    /// Returns the driven destination.
    #[must_use]
    pub fn destination(&self) -> &DestinationConfig {
        &self.destination
    }

    /// Decides, purely, what a driven tick at `now` would run. Reading the
    /// source's high-water mark is part of the decision — an incremental
    /// without new journal is not runnable — but nothing is executed and
    /// no state moves. Missed-window absorption happens in
    /// [`ScheduleDriver::run_due`], the driving operation; an
    /// unabsorbed expired window reports the next grid boundary as
    /// `next_due`.
    #[must_use]
    pub fn evaluate(
        &self,
        state: &ScheduleState,
        engine: &Engine,
        now: ScheduleTick,
    ) -> ScheduleDecision {
        match self.classify(state, engine, now) {
            Classification::Paused => ScheduleDecision::Paused,
            Classification::FirstFull | Classification::FullDue => {
                ScheduleDecision::Run(BackupKind::Full)
            }
            Classification::IncrementalDue { base, has_journal } => {
                if has_journal {
                    ScheduleDecision::Run(BackupKind::Incremental { base })
                } else {
                    // A due incremental with nothing to archive is not
                    // runnable; the next runnable fact is the full boundary.
                    ScheduleDecision::Idle {
                        next_due: Some(
                            state.full_anchor.saturating_add(self.schedule.full_interval),
                        ),
                    }
                }
            }
            Classification::NotDue { next_due } => ScheduleDecision::Idle { next_due },
        }
    }

    /// The shared classification behind [`ScheduleDriver::evaluate`] and
    /// [`ScheduleDriver::run_due`], so the pure decision and the driving
    /// execution can never disagree about what a tick owes.
    fn classify(
        &self,
        state: &ScheduleState,
        engine: &Engine,
        now: ScheduleTick,
    ) -> Classification {
        if state.paused_at.is_some() {
            return Classification::Paused;
        }
        let Some(last_point) = state.last_point.as_ref() else {
            return Classification::FirstFull;
        };
        let full_interval = self.schedule.full_interval;
        let next_full = state.full_anchor.saturating_add(full_interval);
        if now >= next_full {
            let boundary_due = match self.schedule.missed_run {
                MissedRunPolicy::RunOneNow => true,
                MissedRunPolicy::SkipUntilNextWindow => {
                    // Execution happens only when the driven tick lands on
                    // a grid boundary; a tick anywhere else idles until the
                    // next one, however overdue the grid is.
                    first_boundary_at_or_after(next_full, full_interval, now) == now
                }
            };
            if boundary_due {
                return Classification::FullDue;
            }
            let boundary = first_boundary_at_or_after(next_full, full_interval, now);
            return Classification::NotDue { next_due: Some(boundary) };
        }
        if let Some(incremental_interval) = self.schedule.incremental_interval {
            let next_incremental = state.next_incremental_from.saturating_add(incremental_interval);
            if now >= next_incremental {
                let aligned = match self.schedule.missed_run {
                    MissedRunPolicy::RunOneNow => true,
                    MissedRunPolicy::SkipUntilNextWindow => {
                        first_boundary_at_or_after(next_incremental, incremental_interval, now)
                            == now
                    }
                };
                if aligned {
                    return Classification::IncrementalDue {
                        base: last_point.boundary(),
                        has_journal: source_journal_beyond(engine, last_point.boundary()),
                    };
                }
                return Classification::NotDue {
                    next_due: Some(next_full.min(first_boundary_at_or_after(
                        next_incremental,
                        incremental_interval,
                        now,
                    ))),
                };
            }
            return Classification::NotDue { next_due: Some(next_full.min(next_incremental)) };
        }
        Classification::NotDue { next_due: Some(next_full) }
    }

    /// Drives one tick at `now`: absorbs fully-missed windows under the
    /// skip policy, executes at most one due run through the public run
    /// seam, seals the scheduled bundle, and enforces retention.
    ///
    /// `key` is the caller-supplied encryption key for this operation; it
    /// is required when the schedule seals encrypted bundles and ignored
    /// otherwise. It is never stored.
    ///
    /// # Errors
    ///
    /// Returns the typed failure with whatever completed before it. A
    /// paused or idle tick — and a no-op incremental window — return
    /// `Ok(None)` and never fail.
    pub fn run_due(
        &self,
        engine: &Engine,
        state: &mut ScheduleState,
        now: ScheduleTick,
        key: Option<&EncryptionKey>,
    ) -> Result<Option<ScheduleRunOutcome>, ScheduleRunError> {
        if state.paused_at.is_some() {
            return Ok(None);
        }
        self.absorb_missed_windows(state, now);
        let kind = match self.classify(state, engine, now) {
            Classification::Paused | Classification::NotDue { .. } => return Ok(None),
            Classification::FirstFull | Classification::FullDue => BackupKind::Full,
            Classification::IncrementalDue { base, has_journal } => {
                if !has_journal {
                    // A no-op due incremental (no new journal) rearms the
                    // window and records the skip without sealing anything.
                    state.skipped_noop_runs += 1;
                    state.next_incremental_from = now;
                    return Ok(None);
                }
                BackupKind::Incremental { base }
            }
        };
        if let Err(error) = self.destination.probe() {
            let cause = ScheduleError::Destination(error);
            state.last_error = Some(ScheduleFailure::new(cause, now));
            return Err(ScheduleRunError::new(cause, None, Vec::new()));
        }
        let plan = match kind {
            BackupKind::Full => BackupPlan::full(),
            BackupKind::Incremental { base } => BackupPlan::incremental_since(base),
            BackupKind::Snapshot => {
                // The scheduler never plans snapshots: a pinned boundary is
                // a caller fact, not a cadence decision.
                return Ok(None);
            }
        };
        let root = self.destination.root().to_owned();
        match run_backup(engine, plan, &root) {
            Err(cause) => {
                let cause = ScheduleError::Backup(cause);
                state.last_error = Some(ScheduleFailure::new(cause, now));
                Err(ScheduleRunError::new(cause, None, Vec::new()))
            }
            Ok(result) => {
                let completed = CompletedRun::new(
                    result.manifest().kind(),
                    result.manifest().identity().to_owned(),
                    result.manifest().boundary_sequence(),
                    now,
                );
                state.completed_runs += 1;
                state.next_incremental_from = now;
                if completed.kind() == BackupKind::Full {
                    // Only a full run moves the full cadence anchor;
                    // incrementals run inside the full's window.
                    state.full_anchor = now;
                    state.last_full = Some(completed.clone());
                }
                state.last_point = Some(completed.clone());
                state.last_error = None;
                self.seal_and_prune(completed, state, now, key)
            }
        }
    }

    /// Pauses the schedule at `now`. A paused schedule never runs; the
    /// first pause tick is the recorded one, and pausing again is a typed
    /// no-op.
    #[allow(
        clippy::unused_self,
        clippy::unnecessary_wraps,
        reason = "the typed-operation surface keeps one Result shape; pause validation is a Platform-side follow-up"
    )]
    pub fn pause(&self, state: &mut ScheduleState, now: ScheduleTick) -> Result<(), ScheduleError> {
        if state.paused_at.is_none() {
            state.paused_at = Some(now);
        }
        Ok(())
    }

    /// Resumes the paused schedule at `now`. Cadence anchors are
    /// untouched, so whatever accrued while paused is judged by the
    /// missed-run policy on the next driven tick.
    #[allow(
        clippy::unused_self,
        clippy::unnecessary_wraps,
        reason = "the typed-operation surface keeps one Result shape; resume validation is a Platform-side follow-up"
    )]
    pub fn resume(
        &self,
        state: &mut ScheduleState,
        _now: ScheduleTick,
    ) -> Result<(), ScheduleError> {
        state.paused_at = None;
        Ok(())
    }

    /// Reads the schedule's status at `now`: phase, cadence, the newest
    /// completed runs, counts, and the last typed failure. A pure read —
    /// no clock, no engine, no destination touch. A `Due` phase is
    /// time-due; the driven run still decides runnability (a due
    /// incremental with no new journal is a recorded no-op).
    #[must_use]
    pub fn status(&self, state: &ScheduleState, now: ScheduleTick) -> ScheduleStatus {
        let full_interval = self.schedule.full_interval;
        let phase = if let Some(since) = state.paused_at {
            SchedulePhase::Paused { since }
        } else if state.last_point.is_none() {
            SchedulePhase::NeedsFirstFull
        } else {
            let next_full = state.full_anchor.saturating_add(full_interval);
            let full_due = now >= next_full
                && match self.schedule.missed_run {
                    MissedRunPolicy::RunOneNow => true,
                    MissedRunPolicy::SkipUntilNextWindow => {
                        first_boundary_at_or_after(next_full, full_interval, now) == now
                    }
                };
            if full_due {
                SchedulePhase::Due { kind: BackupKind::Full }
            } else if let Some(incremental_interval) = self.schedule.incremental_interval {
                let next_incremental =
                    state.next_incremental_from.saturating_add(incremental_interval);
                if now >= next_incremental {
                    SchedulePhase::Due {
                        kind: BackupKind::Incremental {
                            base: state
                                .last_point
                                .as_ref()
                                .map_or(StorageSequence::default(), CompletedRun::boundary),
                        },
                    }
                } else {
                    SchedulePhase::Idle { next_due: Some(next_full.min(next_incremental)) }
                }
            } else {
                SchedulePhase::Idle { next_due: Some(next_full) }
            }
        };
        ScheduleStatus {
            phase,
            full_interval: self.schedule.full_interval,
            incremental_interval: self.schedule.incremental_interval,
            missed_run: self.schedule.missed_run,
            encryption_enabled: self.schedule.encryption,
            compression_enabled: self.schedule.compression,
            destination_kind: self.destination.kind(),
            last_full: state.last_full.clone(),
            last_point: state.last_point.clone(),
            last_error: state.last_error,
            last_retention: state.last_retention,
            completed_runs: state.completed_runs,
            skipped_noop_runs: state.skipped_noop_runs,
        }
    }

    /// Seals the scheduled bundle and enforces retention after a sealed
    /// run, mapping failures to typed run errors with the run attached.
    fn seal_and_prune(
        &self,
        completed: CompletedRun,
        state: &mut ScheduleState,
        now: ScheduleTick,
        key: Option<&EncryptionKey>,
    ) -> Result<Option<ScheduleRunOutcome>, ScheduleRunError> {
        let root = self.destination.root();
        let mut bundle = None;
        if self.schedule.encryption || self.schedule.compression {
            if self.schedule.encryption && key.is_none() {
                let cause = ScheduleError::Invalid(ScheduleInvalid::EncryptionKeyRequired);
                state.last_error = Some(ScheduleFailure::new(cause, now));
                return Err(ScheduleRunError::new(cause, Some(completed), Vec::new()));
            }
            let options = if self.schedule.compression {
                BundleOptions::compressed()
            } else {
                BundleOptions::plaintext()
            };
            match seal_artifact_bundle(root, completed.identity(), options, key) {
                Ok(sealed) => bundle = Some(sealed),
                Err(cause) => {
                    let cause = ScheduleError::Encryption(cause);
                    state.last_error = Some(ScheduleFailure::new(cause, now));
                    return Err(ScheduleRunError::new(cause, Some(completed), Vec::new()));
                }
            }
        }
        let mut retention = None;
        if self.schedule.retention.is_enabled() {
            match enforce_retention(root, &self.schedule.retention, SystemTime::now()) {
                Ok(receipt) => {
                    state.last_retention = Some(RetentionSummary::new(
                        receipt.pruned().len() as u64,
                        receipt.protected_roots().len() as u64,
                        now,
                    ));
                    retention = Some(receipt);
                }
                Err(failure) => {
                    let cause = ScheduleError::Retention(failure.cause());
                    state.last_error = Some(ScheduleFailure::new(cause, now));
                    return Err(ScheduleRunError::new(
                        cause,
                        Some(completed),
                        failure.pruned_before_failure().to_vec(),
                    ));
                }
            }
        }
        Ok(Some(ScheduleRunOutcome::new(completed, bundle, retention)))
    }

    /// Absorbs fully-missed windows under the skip policy: the anchors
    /// advance past boundaries whose whole window expired, by exact grid
    /// multiples, so the grid never drifts and nothing backfills.
    fn absorb_missed_windows(&self, state: &mut ScheduleState, now: ScheduleTick) {
        if self.schedule.missed_run != MissedRunPolicy::SkipUntilNextWindow {
            return;
        }
        let interval = self.schedule.full_interval;
        while state.full_anchor.saturating_add(interval).saturating_add(interval) <= now {
            state.full_anchor += interval;
        }
        if let Some(interval) = self.schedule.incremental_interval {
            while state.next_incremental_from.saturating_add(interval).saturating_add(interval)
                <= now
            {
                state.next_incremental_from += interval;
            }
        }
    }
}

/// Whether the source has journal the boundary does not cover. A
/// high-water mark that cannot be read fails closed: no new journal is
/// claimable.
fn source_journal_beyond(engine: &Engine, boundary: StorageSequence) -> bool {
    engine
        .journaled_high_water()
        .ok()
        .flatten()
        .is_some_and(|high_water| high_water.get() > boundary.get())
}

/// The next grid boundary at or after `now`, starting from `first`.
fn first_boundary_at_or_after(
    first: ScheduleTick,
    interval: ScheduleTick,
    now: ScheduleTick,
) -> ScheduleTick {
    if now <= first || interval == 0 {
        return first.max(now);
    }
    let steps = (now - first).div_ceil(interval);
    first.saturating_add(steps.saturating_mul(interval))
}
