//! Online key-range shard split (D-28, E-11).
//!
//! One writer per `SQLite` file per region, always. A hot database splits by
//! key range into more files — never intra-region multi-master. The split
//! order is: create the shard file, backfill by range, tail the journal to
//! catch up, atomically cut routing over, verify, drop the moved range.
//!
//! This module is the typed trigger and cutover-gate domain for that order.
//! It follows the same shape as [`crate::deletion`]: pure facts, typed
//! errors, caller-supplied times, and no storage, transport, replication,
//! sync, or query dependency — wiring the plan into the storage files, the
//! journal tailer, and the router is later work in those modules. The
//! hash-bucket `src/sharding` model stays deleted; key ranges are opaque
//! canonical-key-order bytes and this module never hashes anything.
//!
//! All times are caller-supplied Unix milliseconds; the domain never reads
//! a wall clock, so every transition is deterministic under injected times.
//! No document content, field value, path, or secret ever enters a split
//! record — only key bounds and sequence watermarks.

use std::collections::BTreeMap;

/// Sustained write-rate trigger (D-28): an average above this many writes
/// per second over the trailing [`SPLIT_WRITE_RATE_WINDOW_SECS`] seconds
/// starts a split.
pub const SPLIT_WRITE_RATE_THRESHOLD_PER_SEC: u64 = 10_000;

/// The averaging window of the write-rate trigger (D-28: 5 minutes), in
/// seconds. The window covers exactly this many one-second buckets.
pub const SPLIT_WRITE_RATE_WINDOW_SECS: u64 = 300;

/// Size trigger (D-28): a database file at or above this many bytes starts
/// a split. Ten gibibytes.
pub const SPLIT_SIZE_THRESHOLD_BYTES: u64 = 10 * 1024 * 1024 * 1024;

/// Write-p99-breach trigger window (D-28: 15 minutes), in seconds. The
/// breach must be continuous: any non-breached sample resets the clock.
pub const SPLIT_P99_BREACH_WINDOW_SECS: u64 = 900;

/// The millisecond width of one write-rate bucket. Buckets bound the
/// monitor to one entry per recorded second.
const WRITE_BUCKET_MILLIS: u64 = 1_000;

/// Which D-28 auto-trigger fired for one database.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum SplitTrigger {
    /// Sustained more than 10k writes/s over 5 minutes.
    SustainedWriteRate,
    /// File size reached 10 GiB.
    SizeThreshold,
    /// Write p99 breached continuously for 15 minutes.
    SustainedP99Breach,
}

impl SplitTrigger {
    /// Returns the stable short name of the trigger for logs and metrics.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::SustainedWriteRate => "sustained_write_rate",
            Self::SizeThreshold => "size_threshold",
            Self::SustainedP99Breach => "sustained_p99_breach",
        }
    }
}

/// The oldest bucket the rate window keeps, relative to `current_bucket`:
/// exactly [`SPLIT_WRITE_RATE_WINDOW_SECS`] buckets ending at `current`.
const fn oldest_window_bucket(current_bucket: u64) -> u64 {
    current_bucket.saturating_sub(SPLIT_WRITE_RATE_WINDOW_SECS - 1)
}

/// The per-database auto-trigger monitor (D-28).
///
/// Evaluation priority is size, then write rate, then p99: all three firing
/// at once still reports one trigger, deterministically.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SplitTriggerMonitor {
    /// Write counts keyed by second (`millis / 1000`), pruned to the rate
    /// window on every record.
    write_buckets: BTreeMap<u64, u64>,
    /// Latest observed file size in bytes.
    size_bytes: u64,
    /// Start of the current continuous p99-breach run, if any.
    p99_breach_start_millis: Option<u64>,
}

impl SplitTriggerMonitor {
    /// Creates an empty monitor: no writes, zero size, no breach run.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Records `writes` committed at `at_millis` and prunes buckets that
    /// fell out of the rate window relative to `at_millis`.
    pub fn record_writes(&mut self, at_millis: u64, writes: u64) {
        if writes > 0 {
            let bucket = at_millis / WRITE_BUCKET_MILLIS;
            self.write_buckets
                .entry(bucket)
                .and_modify(|count| *count = count.saturating_add(writes))
                .or_insert(writes);
        }
        let oldest = oldest_window_bucket(at_millis / WRITE_BUCKET_MILLIS);
        self.write_buckets.retain(|bucket, _| *bucket >= oldest);
    }

    /// Records the latest observed database file size in bytes.
    pub fn set_size_bytes(&mut self, bytes: u64) {
        self.size_bytes = bytes;
    }

    /// Records one write-p99 sample at `at_millis`. A breached sample
    /// starts (or continues) the continuous run; a clean sample resets it.
    pub fn note_p99(&mut self, at_millis: u64, breached: bool) {
        if breached {
            self.p99_breach_start_millis.get_or_insert(at_millis);
        } else {
            self.p99_breach_start_millis = None;
        }
    }

    /// Evaluates the triggers at `now_millis`, or `None` when no trigger
    /// fires. Pure: records nothing and prunes nothing.
    #[must_use]
    pub fn evaluate(&self, now_millis: u64) -> Option<SplitTrigger> {
        if self.size_bytes >= SPLIT_SIZE_THRESHOLD_BYTES {
            return Some(SplitTrigger::SizeThreshold);
        }
        let current_bucket = now_millis / WRITE_BUCKET_MILLIS;
        let oldest = oldest_window_bucket(current_bucket);
        let window_writes: u64 = self
            .write_buckets
            .range(oldest..=current_bucket)
            .map(|(_, count)| *count)
            .fold(0, u64::saturating_add);
        if window_writes
            > SPLIT_WRITE_RATE_THRESHOLD_PER_SEC.saturating_mul(SPLIT_WRITE_RATE_WINDOW_SECS)
        {
            return Some(SplitTrigger::SustainedWriteRate);
        }
        if let Some(start) = self.p99_breach_start_millis {
            let held_millis = now_millis.saturating_sub(start);
            if held_millis >= SPLIT_P99_BREACH_WINDOW_SECS * WRITE_BUCKET_MILLIS {
                return Some(SplitTrigger::SustainedP99Breach);
            }
        }
        None
    }
}

/// One key-range split's lifecycle phase (D-28's split order).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum SplitPhase {
    /// Planned: the split key is chosen, the shard file is not yet created.
    Planned,
    /// The shard file exists and the range backfill is running.
    Backfilling,
    /// Backfill done; the journal tailer is catching the shard up.
    CatchingUp,
    /// Routing is cut over to the shard; the moved range is verifying.
    CutOver,
    /// Verified; the moved range is dropped from the source.
    Complete,
    /// Aborted before cutover; routing never moved.
    Aborted,
}

/// Why a split plan was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SplitPlanError {
    /// The split key was empty: a range bound must name a real key.
    EmptySplitKey,
}

/// Why an atomic cutover was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SplitCutoverError {
    /// The shard has not caught up: applying through `target_applied`
    /// while the source (or the acknowledged frontier) stands at
    /// `required` would lose an acknowledged write.
    NotCaughtUp {
        /// Highest sequence the shard has applied.
        target_applied: u64,
        /// Lowest sequence the shard must reach first.
        required: u64,
    },
    /// The split is past (or before) the point where cutover is legal.
    WrongPhase(SplitPhase),
}

/// One online key-range split's catch-up and cutover gate (D-28, E-11).
///
/// The shard backfills the key range below `split_key`, then tails the
/// journal: every notification advances `target_applied_through` toward the
/// source's high water. Cutover is atomic in the router and happens only
/// here, only from [`SplitPhase::CatchingUp`], and only when the shard has
/// applied every acknowledged write — [`Self::cut_over`] refuses otherwise,
/// so zero acknowledged-write loss holds by construction, not by audit.
///
/// Sequences are opaque watermarks in one global order (the storage
/// sequence the journal already speaks); this gate only compares them.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardSplit {
    /// Upper-exclusive bound of the moved range in canonical key order.
    split_key: Vec<u8>,
    /// Highest sequence published on the source, as last reported.
    source_high_water: u64,
    /// Highest sequence durably applied on the shard, as last reported.
    target_applied_through: u64,
    /// Highest acknowledged write: every sequence at or below it was
    /// acknowledged to a client and must survive cutover.
    acked_through: u64,
    /// Current lifecycle phase.
    phase: SplitPhase,
}

impl ShardSplit {
    /// Plans a split moving the range below `split_key` (upper-exclusive,
    /// canonical key order) into a new shard file.
    ///
    /// # Errors
    ///
    /// Returns [`SplitPlanError::EmptySplitKey`] when the key is empty.
    pub fn plan(split_key: Vec<u8>) -> Result<Self, SplitPlanError> {
        if split_key.is_empty() {
            return Err(SplitPlanError::EmptySplitKey);
        }
        Ok(Self {
            split_key,
            source_high_water: 0,
            target_applied_through: 0,
            acked_through: 0,
            phase: SplitPhase::Planned,
        })
    }

    /// Returns the split key: the moved range's upper-exclusive bound.
    #[must_use]
    pub fn split_key(&self) -> &[u8] {
        &self.split_key
    }

    /// Returns the current lifecycle phase.
    #[must_use]
    pub const fn phase(&self) -> SplitPhase {
        self.phase
    }

    /// Marks the shard file created and the range backfill running.
    ///
    /// # Errors
    ///
    /// Returns [`SplitCutoverError::WrongPhase`] unless planned.
    pub fn begin_backfill(&mut self) -> Result<(), SplitCutoverError> {
        if self.phase != SplitPhase::Planned {
            return Err(SplitCutoverError::WrongPhase(self.phase));
        }
        self.phase = SplitPhase::Backfilling;
        Ok(())
    }

    /// Marks the backfill complete through `through` and starts journal
    /// catch-up: the shard has the range as of `through` and now tails
    /// everything above it.
    ///
    /// # Errors
    ///
    /// Returns [`SplitCutoverError::WrongPhase`] unless backfilling.
    pub fn begin_catch_up(&mut self, through: u64) -> Result<(), SplitCutoverError> {
        if self.phase != SplitPhase::Backfilling {
            return Err(SplitCutoverError::WrongPhase(self.phase));
        }
        self.target_applied_through = self.target_applied_through.max(through);
        self.phase = SplitPhase::CatchingUp;
        Ok(())
    }

    /// Advances the source high water (monotonic: regressions are ignored).
    pub fn note_source_high_water(&mut self, sequence: u64) {
        self.source_high_water = self.source_high_water.max(sequence);
    }

    /// Advances the shard's applied-through mark (monotonic).
    pub fn note_target_applied(&mut self, sequence: u64) {
        self.target_applied_through = self.target_applied_through.max(sequence);
    }

    /// Advances the acknowledged-write frontier (monotonic).
    pub fn note_acked(&mut self, sequence: u64) {
        self.acked_through = self.acked_through.max(sequence);
    }

    /// Reports whether cutover is safe right now: catching up and the
    /// shard has applied the source high water and every acknowledged
    /// write.
    #[must_use]
    pub const fn can_cut_over(&self) -> bool {
        matches!(self.phase, SplitPhase::CatchingUp)
            && self.target_applied_through >= self.source_high_water
            && self.target_applied_through >= self.acked_through
    }

    /// Atomically cuts routing over to the shard (the router's single
    /// swap, gated here). Aborts are only legal before this point.
    ///
    /// # Errors
    ///
    /// Returns [`SplitCutoverError::WrongPhase`] when not catching up, and
    /// [`SplitCutoverError::NotCaughtUp`] when the shard still trails the
    /// source or the acknowledged frontier — the zero-loss gate.
    pub fn cut_over(&mut self) -> Result<(), SplitCutoverError> {
        if self.phase != SplitPhase::CatchingUp {
            return Err(SplitCutoverError::WrongPhase(self.phase));
        }
        let required = self.source_high_water.max(self.acked_through);
        if self.target_applied_through < required {
            return Err(SplitCutoverError::NotCaughtUp {
                target_applied: self.target_applied_through,
                required,
            });
        }
        self.phase = SplitPhase::CutOver;
        Ok(())
    }

    /// Marks the moved range verified and dropped from the source.
    ///
    /// # Errors
    ///
    /// Returns [`SplitCutoverError::WrongPhase`] unless cut over.
    pub fn note_verified(&mut self) -> Result<(), SplitCutoverError> {
        if self.phase != SplitPhase::CutOver {
            return Err(SplitCutoverError::WrongPhase(self.phase));
        }
        self.phase = SplitPhase::Complete;
        Ok(())
    }

    /// Aborts the split before cutover; routing never moved.
    ///
    /// # Errors
    ///
    /// Returns [`SplitCutoverError::WrongPhase`] once cut over or later —
    /// a moved route is verified, never unwound.
    pub fn abort(&mut self) -> Result<(), SplitCutoverError> {
        match self.phase {
            SplitPhase::Planned | SplitPhase::Backfilling | SplitPhase::CatchingUp => {
                self.phase = SplitPhase::Aborted;
                Ok(())
            }
            phase => Err(SplitCutoverError::WrongPhase(phase)),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Five minutes of just-over-threshold writes fire the rate trigger.
    #[test]
    fn sustained_write_rate_fires_after_a_full_window() {
        let mut monitor = SplitTriggerMonitor::new();
        for second in 0..=SPLIT_WRITE_RATE_WINDOW_SECS {
            monitor.record_writes(
                second * WRITE_BUCKET_MILLIS,
                SPLIT_WRITE_RATE_THRESHOLD_PER_SEC + 1,
            );
        }
        assert_eq!(
            monitor.evaluate(SPLIT_WRITE_RATE_WINDOW_SECS * WRITE_BUCKET_MILLIS),
            Some(SplitTrigger::SustainedWriteRate)
        );
    }

    /// A short burst at the same rate stays quiet: the trigger is
    /// sustained, not instantaneous.
    #[test]
    fn brief_burst_below_window_stays_quiet() {
        let mut monitor = SplitTriggerMonitor::new();
        for second in 0..60 {
            monitor.record_writes(
                second * WRITE_BUCKET_MILLIS,
                SPLIT_WRITE_RATE_THRESHOLD_PER_SEC * 2,
            );
        }
        assert_eq!(monitor.evaluate(60 * WRITE_BUCKET_MILLIS), None);
    }

    /// Exactly 10k writes/s sustained does not fire: the trigger is
    /// strictly above the threshold.
    #[test]
    fn rate_at_exactly_threshold_stays_quiet() {
        let mut monitor = SplitTriggerMonitor::new();
        for second in 0..SPLIT_WRITE_RATE_WINDOW_SECS {
            monitor.record_writes(second * WRITE_BUCKET_MILLIS, SPLIT_WRITE_RATE_THRESHOLD_PER_SEC);
        }
        assert_eq!(monitor.evaluate(SPLIT_WRITE_RATE_WINDOW_SECS * WRITE_BUCKET_MILLIS), None);
    }

    /// Stale buckets fall out of the window: an old burst plus silence is
    /// not a sustained load.
    #[test]
    fn stale_writes_age_out_of_the_window() {
        let mut monitor = SplitTriggerMonitor::new();
        for second in 0..SPLIT_WRITE_RATE_WINDOW_SECS {
            monitor.record_writes(
                second * WRITE_BUCKET_MILLIS,
                SPLIT_WRITE_RATE_THRESHOLD_PER_SEC * 2,
            );
        }
        assert_eq!(monitor.evaluate(2 * SPLIT_WRITE_RATE_WINDOW_SECS * WRITE_BUCKET_MILLIS), None);
    }

    /// A 10 GiB file fires the size trigger with no writes at all.
    #[test]
    fn size_threshold_fires() {
        let mut monitor = SplitTriggerMonitor::new();
        monitor.set_size_bytes(SPLIT_SIZE_THRESHOLD_BYTES);
        assert_eq!(monitor.evaluate(0), Some(SplitTrigger::SizeThreshold));
    }

    /// One byte below the size line stays quiet.
    #[test]
    fn size_below_threshold_stays_quiet() {
        let mut monitor = SplitTriggerMonitor::new();
        monitor.set_size_bytes(SPLIT_SIZE_THRESHOLD_BYTES - 1);
        assert_eq!(monitor.evaluate(0), None);
    }

    /// A continuous 15-minute p99 breach fires; a clean sample resets it.
    #[test]
    fn continuous_p99_breach_fires_and_reset_clears_it() {
        let mut monitor = SplitTriggerMonitor::new();
        monitor.note_p99(0, true);
        assert_eq!(monitor.evaluate(SPLIT_P99_BREACH_WINDOW_SECS * WRITE_BUCKET_MILLIS - 1), None);
        assert_eq!(
            monitor.evaluate(SPLIT_P99_BREACH_WINDOW_SECS * WRITE_BUCKET_MILLIS),
            Some(SplitTrigger::SustainedP99Breach)
        );
        monitor.note_p99(SPLIT_P99_BREACH_WINDOW_SECS * WRITE_BUCKET_MILLIS + 1, false);
        assert_eq!(monitor.evaluate(u64::MAX / 2), None);
    }

    /// Size wins the priority when every trigger fires at once.
    #[test]
    fn size_trigger_has_evaluation_priority() {
        let mut monitor = SplitTriggerMonitor::new();
        monitor.set_size_bytes(SPLIT_SIZE_THRESHOLD_BYTES);
        for second in 0..=SPLIT_WRITE_RATE_WINDOW_SECS {
            monitor.record_writes(
                second * WRITE_BUCKET_MILLIS,
                SPLIT_WRITE_RATE_THRESHOLD_PER_SEC + 1,
            );
        }
        monitor.note_p99(0, true);
        assert_eq!(
            monitor.evaluate(SPLIT_P99_BREACH_WINDOW_SECS * WRITE_BUCKET_MILLIS),
            Some(SplitTrigger::SizeThreshold)
        );
    }

    /// An empty split key is refused: a range must name a real bound.
    #[test]
    fn empty_split_key_is_refused() {
        assert_eq!(ShardSplit::plan(Vec::new()), Err(SplitPlanError::EmptySplitKey));
    }

    /// Cutover refuses while the shard trails an acknowledged write, then
    /// succeeds once the tail drains — zero acknowledged-write loss.
    #[test]
    fn cutover_gate_holds_until_acked_writes_are_applied() {
        let mut split = ShardSplit::plan(b"m".to_vec()).expect("split key");
        split.begin_backfill().expect("backfill");
        split.begin_catch_up(100).expect("catch-up");
        split.note_source_high_water(120);
        split.note_acked(120);
        split.note_target_applied(119);
        assert!(!split.can_cut_over());
        assert_eq!(
            split.cut_over(),
            Err(SplitCutoverError::NotCaughtUp { target_applied: 119, required: 120 })
        );
        split.note_target_applied(120);
        assert!(split.can_cut_over());
        split.cut_over().expect("cut over once caught up");
        assert_eq!(split.phase(), SplitPhase::CutOver);
        split.note_verified().expect("verified");
        assert_eq!(split.phase(), SplitPhase::Complete);
    }

    /// Cutover before catch-up, and abort after cutover, both fail closed.
    #[test]
    fn phase_violations_fail_closed() {
        let mut split = ShardSplit::plan(b"m".to_vec()).expect("split key");
        assert_eq!(split.cut_over(), Err(SplitCutoverError::WrongPhase(SplitPhase::Planned)));
        split.begin_backfill().expect("backfill");
        split.begin_catch_up(0).expect("catch-up");
        split.cut_over().expect("empty split cuts over");
        assert_eq!(split.abort(), Err(SplitCutoverError::WrongPhase(SplitPhase::CutOver)));
    }

    /// Abort before cutover is legal and routing never moved.
    #[test]
    fn abort_before_cutover_is_legal() {
        let mut split = ShardSplit::plan(b"m".to_vec()).expect("split key");
        split.abort().expect("abort while planned");
        assert_eq!(split.phase(), SplitPhase::Aborted);
    }
}
