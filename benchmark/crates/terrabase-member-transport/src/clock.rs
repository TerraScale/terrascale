//! The lane clock and the bounded hybrid logical clock stamps.
//!
//! The lane clock advances exactly one tick per transport pump; nothing in
//! the in-process lane reads wall time. Every deadline, expiry bound, and
//! observation instant is a value of this clock, so lane timing behavior is
//! reproducible by driving the same pumps. The engine-socket lane keeps the
//! same tick-based clock for all protocol vocabulary (issued/expiry stamps,
//! deadlines, RTO and convergence intervals); its pumps additionally drain
//! real socket traffic inside a bounded wall-clock window, which is a
//! fixture fact of that lane kind only and never a timing claim.

/// A deterministic instant of the lane's clock.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct LaneInstant {
    ticks: u64,
}

impl LaneInstant {
    /// Creates the instant at `ticks` lane ticks.
    #[must_use]
    pub const fn at_ticks(ticks: u64) -> Self {
        Self { ticks }
    }

    /// Returns the lane tick of this instant.
    #[must_use]
    pub const fn ticks(self) -> u64 {
        self.ticks
    }

    /// Returns the instant `ticks` pumps later than this one.
    #[must_use]
    pub const fn plus_ticks(self, ticks: u64) -> Self {
        Self { ticks: self.ticks.saturating_add(ticks) }
    }
}

/// One stamp of a Member's bounded hybrid logical clock.
///
/// The stamp pairs the physical component — lane ticks observed from the
/// shared transport clock or an injected instant — with a logical counter
/// that orders writes inside one tick. Stamps order by physical time first
/// and the counter second; the tie past that is the deterministic Member
/// identity order. Lane-scoped: no wall-clock claim.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ClockStamp {
    ticks: u64,
    counter: u64,
}

impl ClockStamp {
    /// Creates one stamp from its physical ticks and logical counter.
    #[must_use]
    pub const fn new(ticks: u64, counter: u64) -> Self {
        Self { ticks, counter }
    }

    /// Returns the physical component, in lane ticks.
    #[must_use]
    pub const fn ticks(self) -> u64 {
        self.ticks
    }

    /// Returns the logical counter within the physical tick.
    #[must_use]
    pub const fn counter(self) -> u64 {
        self.counter
    }
}
