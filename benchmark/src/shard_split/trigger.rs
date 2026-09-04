//! Deterministic D-28 split trigger telemetry.

use std::collections::BTreeMap;

/// Sustained write-rate trigger threshold, in writes per second.
pub const SPLIT_WRITE_RATE_THRESHOLD_PER_SEC: u64 = 10_000;
/// Number of one-second buckets in the sustained write-rate window.
pub const SPLIT_WRITE_RATE_WINDOW_SECS: u64 = 300;
/// Size trigger threshold: ten gibibytes.
pub const SPLIT_SIZE_THRESHOLD_BYTES: u64 = 10 * 1024 * 1024 * 1024;
/// Continuous write-p99 breach window, in seconds.
pub const SPLIT_P99_BREACH_WINDOW_SECS: u64 = 900;
pub(crate) const WRITE_BUCKET_MILLIS: u64 = 1_000;
const P99_BREACH_WINDOW_MILLIS: u64 = SPLIT_P99_BREACH_WINDOW_SECS * WRITE_BUCKET_MILLIS;

/// Which D-28 automatic trigger fired for one database.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum SplitTrigger {
    /// Sustained more than 10k writes/s over five minutes.
    SustainedWriteRate,
    /// File size reached 10 GiB.
    SizeThreshold,
    /// Write p99 breached continuously for 15 minutes.
    SustainedP99Breach,
}

impl SplitTrigger {
    /// Returns the stable short name used by logs and metrics.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::SustainedWriteRate => "sustained_write_rate",
            Self::SizeThreshold => "size_threshold",
            Self::SustainedP99Breach => "sustained_p99_breach",
        }
    }
}

const fn oldest_window_bucket(current: u64) -> u64 {
    current.saturating_sub(SPLIT_WRITE_RATE_WINDOW_SECS - 1)
}

/// Caller-supplied telemetry used to decide whether a database needs a split.
///
/// Evaluation never reads a clock and never mutates the monitor.  P99 samples
/// expire after the breach window: a process that records one breach and then
/// stops reporting cannot later manufacture a sustained breach from stale
/// telemetry.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SplitTriggerMonitor {
    write_buckets: BTreeMap<u64, u64>,
    size_bytes: u64,
    p99_breach_start_millis: Option<u64>,
    latest_p99_millis: Option<u64>,
}

impl SplitTriggerMonitor {
    /// Creates an empty monitor.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Records committed writes at a caller-supplied Unix-millisecond time.
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

    /// Records the latest observed SQLite file size in bytes.
    pub fn set_size_bytes(&mut self, bytes: u64) {
        self.size_bytes = bytes;
    }

    /// Records one p99 sample. A clean sample resets the continuous run.
    pub fn note_p99(&mut self, at_millis: u64, breached: bool) {
        self.latest_p99_millis = Some(at_millis);
        if breached {
            self.p99_breach_start_millis.get_or_insert(at_millis);
        } else {
            self.p99_breach_start_millis = None;
        }
    }

    /// Evaluates triggers at `now_millis`, with size, rate, then p99 priority.
    #[must_use]
    pub fn evaluate(&self, now_millis: u64) -> Option<SplitTrigger> {
        if self.size_bytes >= SPLIT_SIZE_THRESHOLD_BYTES {
            return Some(SplitTrigger::SizeThreshold);
        }
        let current_bucket = now_millis / WRITE_BUCKET_MILLIS;
        let oldest = oldest_window_bucket(current_bucket);
        let window_writes = self
            .write_buckets
            .range(oldest..=current_bucket)
            .map(|(_, count)| *count)
            .fold(0, u64::saturating_add);
        if window_writes
            > SPLIT_WRITE_RATE_THRESHOLD_PER_SEC.saturating_mul(SPLIT_WRITE_RATE_WINDOW_SECS)
        {
            return Some(SplitTrigger::SustainedWriteRate);
        }
        let (Some(start), Some(latest)) = (self.p99_breach_start_millis, self.latest_p99_millis)
        else {
            return None;
        };
        // A future timestamp is invalid telemetry, and a breach with no
        // sample inside the current window is not continuous evidence.
        if latest > now_millis || now_millis.saturating_sub(latest) > P99_BREACH_WINDOW_MILLIS {
            return None;
        }
        if now_millis.saturating_sub(start) >= P99_BREACH_WINDOW_MILLIS {
            Some(SplitTrigger::SustainedP99Breach)
        } else {
            None
        }
    }
}
