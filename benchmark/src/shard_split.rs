//! Online key-range shard split contracts (D-28, E-11).
//!
#![allow(clippy::all, reason = "shard split contracts are integrated by the routing worker")]
#![allow(clippy::pedantic, reason = "shard split contracts are integrated by the routing worker")]
mod adapters;
mod cutover;
mod journal;
mod lifecycle;
mod range_copy;
mod recovery;
mod routing;
mod state;
mod trigger;
mod verification;

pub use adapters::{
    AtomicSplitCutover, CutoverContext, SplitEngineAdapter, SplitOrchestratorAdapter,
    SplitWiringReport,
};
pub use cutover::{CutoverPermit, FenceError, WriterFence, WriterFenceState};
pub use journal::{
    CatchUpError, JournalCatchUp, JournalEntry, JournalOperation, JournalReader, JournalWriter,
};
pub use lifecycle::{ShardSplit, SplitCutoverError, SplitPhase, SplitPlanError};
pub use range_copy::{
    CopyBatch, CopyEntry, CopyError, RangeCopier, RangeCopySource, RangeCopyTarget,
};
pub use recovery::{RecoveryAction, RecoveryDecision, RecoveryError, RecoveryPlan};
pub use routing::{RouteError, RouteTable, ShardRoute, ShardRouter};
pub use state::{
    DurableSplitState, SPLIT_STATE_VERSION, SplitContext, SplitStateError, SplitStateStore,
};
#[cfg(test)]
pub(crate) use trigger::WRITE_BUCKET_MILLIS;
pub use trigger::{
    SPLIT_P99_BREACH_WINDOW_SECS, SPLIT_SIZE_THRESHOLD_BYTES, SPLIT_WRITE_RATE_THRESHOLD_PER_SEC,
    SPLIT_WRITE_RATE_WINDOW_SECS, SplitTrigger, SplitTriggerMonitor,
};
pub use verification::{
    RangeVerification, VerificationError, VerificationOutcome, VerificationReport, Verifier, verify,
};

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

    /// A single breach sample cannot fire after its telemetry has gone stale.
    #[test]
    fn stale_p99_sample_cannot_create_a_breach() {
        let mut monitor = SplitTriggerMonitor::new();
        monitor.note_p99(0, true);
        assert_eq!(monitor.evaluate(SPLIT_P99_BREACH_WINDOW_SECS * WRITE_BUCKET_MILLIS + 1), None);
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

    /// Durable state round-trips without losing the cutover gate facts.
    #[test]
    fn durable_state_round_trips() {
        let mut split = ShardSplit::plan(b"m".to_vec()).expect("split key");
        split.begin_backfill().expect("backfill");
        split.begin_catch_up(7).expect("catch-up");
        split.note_source_high_water(9);
        split.note_acked(9);
        let state = split.durable_state();
        let restored = DurableSplitState::decode(&state.encode()).expect("decode state");
        assert_eq!(restored, state);
        assert_eq!(ShardSplit::from_durable_state(restored), Ok(split));
    }

    /// A fence permit makes the route replacement a single domain operation.
    #[test]
    fn fenced_route_split_keeps_both_ranges() {
        let mut fence = WriterFence::new();
        fence.begin_drain(10).expect("begin drain");
        fence.note_acked(10).expect("ack");
        let permit = fence.try_fence(10).expect("target caught up");
        let mut router = ShardRouter::single(1);
        router.cut_over(1, 2, b"m".to_vec(), permit).expect("route cutover");
        assert_eq!(router.route(b"a"), Some(2));
        assert_eq!(router.route(b"z"), Some(1));
    }
}
