//! Deterministic, bounded fault schedules for the simulation harness.
//!
//! A fault is active over the half-open interval `[start, end)`. The six
//! controls are deliberately explicit: [`FaultKind::Drop`],
//! [`FaultKind::Reorder`], [`FaultKind::Duplicate`], [`FaultKind::Partition`],
//! [`FaultKind::Restart`], and [`FaultKind::ClockSkew`]. A generated plan
//! contains every control when at least six faults are requested. This makes
//! a seed run useful as a fault-coverage run as well as making it replayable.
//!
//! Plans have a small binary trace format. It is intentionally implemented
//! here instead of relying on a format whose version or map ordering could
//! change: a trace written for a failing seed must decode to the exact same
//! schedule on every machine.

use std::fmt;

use crate::model::RegionId;
use crate::rng::SimRng;

/// Maximum clock skew the harness injects (ms); matches invariant 1.
pub const MAX_CLOCK_SKEW_MS: i64 = 500;

/// Maximum clock-skew magnitude as an unsigned millisecond count.
pub const MAX_CLOCK_SKEW_MAGNITUDE_MS: u64 = 500;

/// Maximum number of faults in a plan. This is also the maximum number of
/// entries accepted by the trace decoder, preventing an untrusted trace from
/// allocating an unbounded schedule.
pub const MAX_FAULTS: usize = 256;

/// Maximum reorder window in simulation steps.
pub const MAX_REORDER_WINDOW_STEPS: u64 = 16;

/// Maximum duplicate fan-out for one frame.
pub const MAX_DUPLICATE_COPIES: u32 = 8;

const TRACE_MAGIC: [u8; 4] = *b"TBSF";
const TRACE_VERSION: u8 = 1;

/// One seeded fault. Use the constructors, not struct literals: they keep
/// link endpoint ordering canonical so both directions agree.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Fault {
    /// First step the fault is active (inclusive).
    pub start_step: u64,
    /// First step the fault is healed (exclusive).
    pub end_step: u64,
    /// What the fault does.
    pub kind: FaultKind,
}

/// The six fault injectors exercised by the harness.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FaultKind {
    /// Drop all deliveries on an unordered link while active.
    Drop {
        /// One side of the link.
        first: RegionId,
        /// The other side of the link.
        second: RegionId,
    },
    /// Hold deliveries and release them in a bounded window.
    Reorder {
        /// One side of the link.
        first: RegionId,
        /// The other side of the link.
        second: RegionId,
        /// Reorder window in simulation steps (at least two).
        window: u64,
    },
    /// Deliver one frame more than once.
    Duplicate {
        /// One side of the link.
        first: RegionId,
        /// The other side of the link.
        second: RegionId,
        /// Total deliveries per frame (at least two).
        copies: u32,
    },
    /// Partition an unordered link in both directions.
    Partition {
        /// One side of the severed link.
        first: RegionId,
        /// The other side of the severed link.
        second: RegionId,
    },
    /// Restart one region. Durable rows remain available after healing.
    Restart {
        /// The restarted region.
        region: RegionId,
    },
    /// Shift one region's HLC by `offset_ms`, bounded by
    /// [`MAX_CLOCK_SKEW_MS`].
    ClockSkew {
        /// The skewed region.
        region: RegionId,
        /// Clock offset in milliseconds in `[-500, 500]`.
        offset_ms: i64,
    },
}

impl Fault {
    /// Creates a drop control over `[start, end)`.
    #[must_use]
    pub fn drop(first: RegionId, second: RegionId, start: u64, end: u64) -> Self {
        let (first, second) = canonical_link(first, second);
        Self { start_step: start, end_step: end, kind: FaultKind::Drop { first, second } }
    }

    /// Creates a WAN partition over `[start, end)`.
    #[must_use]
    pub fn partition(first: RegionId, second: RegionId, start: u64, end: u64) -> Self {
        let (first, second) = canonical_link(first, second);
        Self { start_step: start, end_step: end, kind: FaultKind::Partition { first, second } }
    }

    /// Creates a region restart over `[start, end)`.
    #[must_use]
    pub fn restart(region: RegionId, start: u64, end: u64) -> Self {
        Self { start_step: start, end_step: end, kind: FaultKind::Restart { region } }
    }

    /// Compatibility spelling for callers of the old member-loss control.
    /// It maps exactly to [`Fault::restart`]; there is no separate Kill fault.
    #[must_use]
    pub fn kill(region: RegionId, start: u64, end: u64) -> Self {
        Self::restart(region, start, end)
    }

    /// Creates a clock-skew injection over `[start, end)`. Values outside
    /// the invariant bound are clamped.
    #[must_use]
    pub fn clock_skew(region: RegionId, offset_ms: i64, start: u64, end: u64) -> Self {
        Self {
            start_step: start,
            end_step: end,
            kind: FaultKind::ClockSkew {
                region,
                offset_ms: offset_ms.clamp(-MAX_CLOCK_SKEW_MS, MAX_CLOCK_SKEW_MS),
            },
        }
    }

    /// Creates duplicate delivery on a link over `[start, end)`.
    #[must_use]
    pub fn duplicate(first: RegionId, second: RegionId, copies: u32, start: u64, end: u64) -> Self {
        let (first, second) = canonical_link(first, second);
        Self {
            start_step: start,
            end_step: end,
            kind: FaultKind::Duplicate {
                first,
                second,
                copies: copies.clamp(2, MAX_DUPLICATE_COPIES),
            },
        }
    }

    /// Creates reordered delivery on a link over `[start, end)`.
    #[must_use]
    pub fn reorder(first: RegionId, second: RegionId, window: u64, start: u64, end: u64) -> Self {
        let (first, second) = canonical_link(first, second);
        Self {
            start_step: start,
            end_step: end,
            kind: FaultKind::Reorder {
                first,
                second,
                window: window.clamp(2, MAX_REORDER_WINDOW_STEPS),
            },
        }
    }

    /// Returns `true` when this fault is active at `step`.
    #[must_use]
    pub const fn is_active(&self, step: u64) -> bool {
        self.start_step <= step && step < self.end_step
    }

    /// Alias used by trace consumers for [`Self::is_active`].
    #[must_use]
    pub const fn active_at(&self, step: u64) -> bool {
        self.is_active(step)
    }
}

/// Orders link endpoints so each unordered pair has one representation.
fn canonical_link(first: RegionId, second: RegionId) -> (RegionId, RegionId) {
    if first <= second { (first, second) } else { (second, first) }
}

fn kind_severs(kind: &FaultKind, first: RegionId, second: RegionId) -> bool {
    let (first, second) = canonical_link(first, second);
    matches!(
        kind,
        FaultKind::Drop { first: link_first, second: link_second }
            | FaultKind::Partition { first: link_first, second: link_second }
            if *link_first == first && *link_second == second
    )
}

fn link_matches(kind: &FaultKind, first: RegionId, second: RegionId) -> bool {
    let (first, second) = canonical_link(first, second);
    match kind {
        FaultKind::Reorder { first: link_first, second: link_second, .. }
        | FaultKind::Duplicate { first: link_first, second: link_second, .. } => {
            *link_first == first && *link_second == second
        }
        _ => false,
    }
}

/// A deterministic schedule of faults for one simulation scenario.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct FaultPlan {
    horizon: u64,
    faults: Vec<Fault>,
}

impl FaultPlan {
    /// Creates an empty plan (no faults).
    #[must_use]
    pub const fn new() -> Self {
        Self { horizon: 0, faults: Vec::new() }
    }

    /// Appends a non-empty fault interval. Empty intervals are ignored. The
    /// schedule horizon tracks the latest healing boundary.
    pub fn push(&mut self, fault: Fault) {
        if fault.end_step <= fault.start_step || self.faults.len() >= MAX_FAULTS {
            return;
        }
        self.horizon = self.horizon.max(fault.end_step);
        self.faults.push(fault);
    }

    /// Returns the schedule horizon (the first step after the run).
    #[must_use]
    pub const fn horizon(&self) -> u64 {
        self.horizon
    }

    /// Returns scheduled faults in deterministic insertion order.
    #[must_use]
    pub fn faults(&self) -> &[Fault] {
        &self.faults
    }

    /// Returns the number of scheduled faults.
    #[must_use]
    pub fn len(&self) -> usize {
        self.faults.len()
    }

    /// Returns `true` when no fault is scheduled.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.faults.is_empty()
    }

    /// Generates a seeded schedule. For six or more requested entries, the
    /// first six entries are one each of the six controls; later entries cycle
    /// through the controls. Random endpoints, starts, durations, and
    /// parameters still make each seed's trace distinct.
    ///
    /// # Panics
    ///
    /// Panics if a generated index or bounded clock offset cannot be represented
    /// on the target platform. Supported 32- and 64-bit targets fit these bounds.
    #[must_use]
    pub fn generate(rng: &mut SimRng, regions: &[RegionId], steps: u64, max_faults: usize) -> Self {
        let mut plan = Self { horizon: steps, faults: Vec::new() };
        if regions.is_empty() || steps == 0 || max_faults == 0 {
            return plan;
        }
        let count = max_faults.min(MAX_FAULTS);
        let max_duration = (steps / 4).max(1);
        for index in 0..count {
            let region_count = u64::try_from(regions.len()).expect("slice length fits in u64");
            let first_index = usize::try_from(rng.below(region_count)).expect("random index fits");
            let second_index = if regions.len() == 1 {
                first_index
            } else {
                let remaining = u64::try_from(regions.len() - 1).expect("slice length fits in u64");
                let offset = 1 + usize::try_from(rng.below(remaining)).expect("random index fits");
                (first_index + offset) % regions.len()
            };
            let first = regions[first_index];
            let second = regions[second_index];
            let start = rng.below(steps);
            let duration = rng.between(1, max_duration);
            let end = start.saturating_add(duration).min(steps);
            let fault = match index % 6 {
                0 => Fault::drop(first, second, start, end),
                1 => Fault::reorder(first, second, rng.between(2, 4), start, end),
                2 => Fault::duplicate(
                    first,
                    second,
                    u32::try_from(rng.between(2, 3)).expect("duplicate count fits"),
                    start,
                    end,
                ),
                3 => Fault::partition(first, second, start, end),
                4 => Fault::restart(first, start, end),
                _ => {
                    let magnitude = i64::try_from(rng.between(1, MAX_CLOCK_SKEW_MAGNITUDE_MS))
                        .expect("clock skew magnitude fits");
                    let offset = if rng.biased(1, 2) { magnitude } else { -magnitude };
                    Fault::clock_skew(first, offset, start, end)
                }
            };
            plan.push(fault);
        }
        plan
    }

    /// Iterates over faults active at `step`.
    pub fn active_at(&self, step: u64) -> impl Iterator<Item = &Fault> {
        self.faults.iter().filter(move |fault| fault.is_active(step))
    }

    /// Returns `true` when `region` is restarting at `step`.
    #[must_use]
    pub fn region_down(&self, region: RegionId, step: u64) -> bool {
        self.active_at(step).any(
            |fault| matches!(fault.kind, FaultKind::Restart { region: down } if down == region),
        )
    }

    /// Returns `true` when `region` is restarting at `step`.
    #[must_use]
    pub fn restarting(&self, region: RegionId, step: u64) -> bool {
        self.region_down(region, step)
    }

    /// Returns `true` when a frame can flow between the regions at `step`.
    #[must_use]
    pub fn link_open(&self, first: RegionId, second: RegionId, step: u64) -> bool {
        if first != second && (self.region_down(first, step) || self.region_down(second, step)) {
            return false;
        }
        !self.active_at(step).any(|fault| kind_severs(&fault.kind, first, second))
    }

    /// Returns `true` when delivery over `first`–`second` is blocked.
    #[must_use]
    pub fn blocked(&self, first: RegionId, second: RegionId, step: u64) -> bool {
        !self.link_open(first, second, step)
    }

    /// Returns the summed clock offset (ms) for `region` at `step`.
    #[must_use]
    pub fn clock_offset_ms(&self, region: RegionId, step: u64) -> i64 {
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::ClockSkew { region: skewed, offset_ms } if skewed == region => {
                    Some(offset_ms)
                }
                _ => None,
            })
            .fold(0, i64::saturating_add)
            .clamp(-MAX_CLOCK_SKEW_MS, MAX_CLOCK_SKEW_MS)
    }

    /// Returns the maximum duplicate fan-out on a link (`1` when inactive).
    #[must_use]
    pub fn duplicate_copies(&self, first: RegionId, second: RegionId, step: u64) -> u32 {
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::Duplicate { copies, .. } if link_matches(&fault.kind, first, second) => {
                    Some(copies)
                }
                _ => None,
            })
            .max()
            .unwrap_or(1)
    }

    /// Returns the duplicate fan-out (`1` when inactive).
    #[must_use]
    pub fn copies(&self, first: RegionId, second: RegionId, step: u64) -> u32 {
        self.duplicate_copies(first, second, step)
    }

    /// Returns the maximum reorder window on a link (`0` when inactive).
    #[must_use]
    pub fn reorder_window(&self, first: RegionId, second: RegionId, step: u64) -> u64 {
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::Reorder { window, .. } if link_matches(&fault.kind, first, second) => {
                    Some(window)
                }
                _ => None,
            })
            .max()
            .unwrap_or(0)
    }

    /// Slow links were removed from the fault vocabulary. The old query now
    /// returns the neutral delay, preserving the world adapter's seam.
    #[must_use]
    pub const fn link_delay_steps(&self, _first: RegionId, _second: RegionId, _step: u64) -> u64 {
        0
    }

    /// Encodes this plan as a deterministic versioned trace.
    ///
    /// # Panics
    ///
    /// Panics if the internal fault count exceeds `u32::MAX`. Constructors
    /// enforce the stricter [`MAX_FAULTS`] limit.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(17 * self.faults.len() + 17);
        bytes.extend_from_slice(&TRACE_MAGIC);
        bytes.push(TRACE_VERSION);
        bytes.extend_from_slice(&self.horizon.to_le_bytes());
        bytes.extend_from_slice(
            &u32::try_from(self.faults.len()).expect("bounded fault count fits").to_le_bytes(),
        );
        for fault in &self.faults {
            bytes.extend_from_slice(&fault.start_step.to_le_bytes());
            bytes.extend_from_slice(&fault.end_step.to_le_bytes());
            match fault.kind {
                FaultKind::Drop { first, second } => {
                    bytes.push(0);
                    put_region(&mut bytes, first);
                    put_region(&mut bytes, second);
                }
                FaultKind::Reorder { first, second, window } => {
                    bytes.push(1);
                    put_region(&mut bytes, first);
                    put_region(&mut bytes, second);
                    bytes.extend_from_slice(&window.to_le_bytes());
                }
                FaultKind::Duplicate { first, second, copies } => {
                    bytes.push(2);
                    put_region(&mut bytes, first);
                    put_region(&mut bytes, second);
                    bytes.extend_from_slice(&copies.to_le_bytes());
                }
                FaultKind::Partition { first, second } => {
                    bytes.push(3);
                    put_region(&mut bytes, first);
                    put_region(&mut bytes, second);
                }
                FaultKind::Restart { region } => {
                    bytes.push(4);
                    put_region(&mut bytes, region);
                }
                FaultKind::ClockSkew { region, offset_ms } => {
                    bytes.push(5);
                    put_region(&mut bytes, region);
                    bytes.extend_from_slice(&offset_ms.to_le_bytes());
                }
            }
        }
        bytes
    }

    /// Decodes a trace produced by [`Self::to_bytes`].
    ///
    /// # Errors
    ///
    /// Returns a decode error for invalid framing, unsupported versions,
    /// excessive fault counts, invalid intervals, or trailing bytes.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, FaultPlanDecodeError> {
        let mut reader = TraceReader::new(bytes);
        if reader.take(4)? != TRACE_MAGIC {
            return Err(FaultPlanDecodeError::InvalidMagic);
        }
        let version = reader.u8()?;
        if version != TRACE_VERSION {
            return Err(FaultPlanDecodeError::UnsupportedVersion(version));
        }
        let horizon = reader.u64()?;
        let count_raw = reader.u32()?;
        let count = usize::try_from(count_raw).unwrap_or(usize::MAX);
        if count > MAX_FAULTS {
            return Err(FaultPlanDecodeError::TooManyFaults(count));
        }
        let mut plan = Self { horizon, faults: Vec::with_capacity(count) };
        for _ in 0..count {
            let start = reader.u64()?;
            let end = reader.u64()?;
            if end <= start || end > horizon {
                return Err(FaultPlanDecodeError::InvalidInterval);
            }
            let kind = match reader.u8()? {
                0 => FaultKind::Drop { first: reader.region()?, second: reader.region()? },
                1 => {
                    let first = reader.region()?;
                    let second = reader.region()?;
                    let window = reader.u64()?;
                    if !(2..=MAX_REORDER_WINDOW_STEPS).contains(&window) {
                        return Err(FaultPlanDecodeError::InvalidParameter);
                    }
                    FaultKind::Reorder { first, second, window }
                }
                2 => {
                    let first = reader.region()?;
                    let second = reader.region()?;
                    let copies = reader.u32()?;
                    if !(2..=MAX_DUPLICATE_COPIES).contains(&copies) {
                        return Err(FaultPlanDecodeError::InvalidParameter);
                    }
                    FaultKind::Duplicate { first, second, copies }
                }
                3 => FaultKind::Partition { first: reader.region()?, second: reader.region()? },
                4 => FaultKind::Restart { region: reader.region()? },
                5 => {
                    let region = reader.region()?;
                    let offset_ms = reader.i64()?;
                    if !(-MAX_CLOCK_SKEW_MS..=MAX_CLOCK_SKEW_MS).contains(&offset_ms) {
                        return Err(FaultPlanDecodeError::InvalidParameter);
                    }
                    FaultKind::ClockSkew { region, offset_ms }
                }
                kind => return Err(FaultPlanDecodeError::InvalidKind(kind)),
            };
            plan.faults.push(Fault { start_step: start, end_step: end, kind });
        }
        if !reader.is_empty() {
            return Err(FaultPlanDecodeError::TrailingBytes);
        }
        Ok(plan)
    }

    /// Alias for [`Self::to_bytes`] useful to trace writers.
    #[must_use]
    pub fn serialize(&self) -> Vec<u8> {
        self.to_bytes()
    }

    /// Alias for [`Self::from_bytes`] useful to replay readers.
    ///
    /// # Errors
    ///
    /// Returns a decode error for invalid framing, unsupported versions,
    /// excessive fault counts, invalid intervals, or trailing bytes.
    pub fn deserialize(bytes: &[u8]) -> Result<Self, FaultPlanDecodeError> {
        Self::from_bytes(bytes)
    }

    /// Alias for [`Self::to_bytes`] used by simulation trace writers.
    #[must_use]
    pub fn trace(&self) -> Vec<u8> {
        self.to_bytes()
    }

    /// Alias for [`Self::from_bytes`] used by simulation replay readers.
    ///
    /// # Errors
    ///
    /// Returns a decode error for invalid framing, unsupported versions,
    /// excessive fault counts, invalid intervals, or trailing bytes.
    pub fn replay(bytes: &[u8]) -> Result<Self, FaultPlanDecodeError> {
        Self::from_bytes(bytes)
    }
}

fn put_region(bytes: &mut Vec<u8>, region: RegionId) {
    bytes.extend_from_slice(&region.index().to_le_bytes());
}

/// Error returned for malformed or incompatible fault traces.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FaultPlanDecodeError {
    /// The trace did not begin with the `TBSF` marker.
    InvalidMagic,
    /// The trace format version is not supported.
    UnsupportedVersion(u8),
    /// The trace ended before a complete value could be read.
    UnexpectedEof,
    /// The trace contains an unknown fault tag.
    InvalidKind(u8),
    /// A fault interval is empty or outside the plan horizon.
    InvalidInterval,
    /// A fault parameter exceeds its documented bound.
    InvalidParameter,
    /// The trace asks for more entries than the bounded schedule allows.
    TooManyFaults(usize),
    /// Bytes after the final encoded fault are not accepted.
    TrailingBytes,
}

impl fmt::Display for FaultPlanDecodeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidMagic => formatter.write_str("invalid fault trace magic"),
            Self::UnsupportedVersion(version) => {
                write!(formatter, "unsupported fault trace version {version}")
            }
            Self::UnexpectedEof => formatter.write_str("truncated fault trace"),
            Self::InvalidKind(kind) => write!(formatter, "invalid fault kind tag {kind}"),
            Self::InvalidInterval => formatter.write_str("invalid fault interval"),
            Self::InvalidParameter => formatter.write_str("invalid fault parameter"),
            Self::TooManyFaults(count) => {
                write!(formatter, "fault trace has {count} entries; limit is {MAX_FAULTS}")
            }
            Self::TrailingBytes => formatter.write_str("trailing bytes in fault trace"),
        }
    }
}

impl std::error::Error for FaultPlanDecodeError {}

struct TraceReader<'bytes> {
    bytes: &'bytes [u8],
    position: usize,
}

impl<'bytes> TraceReader<'bytes> {
    const fn new(bytes: &'bytes [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn take(&mut self, length: usize) -> Result<&'bytes [u8], FaultPlanDecodeError> {
        let end = self.position.checked_add(length).ok_or(FaultPlanDecodeError::UnexpectedEof)?;
        let value =
            self.bytes.get(self.position..end).ok_or(FaultPlanDecodeError::UnexpectedEof)?;
        self.position = end;
        Ok(value)
    }

    fn u8(&mut self) -> Result<u8, FaultPlanDecodeError> {
        Ok(self.take(1)?[0])
    }

    fn u32(&mut self) -> Result<u32, FaultPlanDecodeError> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().expect("length checked")))
    }

    fn u64(&mut self) -> Result<u64, FaultPlanDecodeError> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().expect("length checked")))
    }

    fn i64(&mut self) -> Result<i64, FaultPlanDecodeError> {
        Ok(i64::from_le_bytes(self.take(8)?.try_into().expect("length checked")))
    }

    fn region(&mut self) -> Result<RegionId, FaultPlanDecodeError> {
        Ok(RegionId::new(self.u32()?))
    }

    const fn is_empty(&self) -> bool {
        self.position == self.bytes.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn regions() -> [RegionId; 3] {
        [RegionId::new(0), RegionId::new(1), RegionId::new(2)]
    }

    #[test]
    fn empty_intervals_are_dropped() {
        let mut plan = FaultPlan::new();
        plan.push(Fault::restart(RegionId::new(0), 5, 5));
        assert!(plan.is_empty());
    }

    #[test]
    fn all_controls_are_guaranteed_and_heal() {
        let mut rng = SimRng::new(7);
        let plan = FaultPlan::generate(&mut rng, &regions(), 100, 6);
        assert_eq!(plan.len(), 6);
        for fault in plan.faults() {
            assert!(fault.is_active(fault.start_step));
            assert!(!fault.is_active(fault.end_step));
            assert!(fault.end_step <= plan.horizon());
        }
        assert!(plan.faults().iter().any(|fault| matches!(fault.kind, FaultKind::Drop { .. })));
        assert!(plan.faults().iter().any(|fault| matches!(fault.kind, FaultKind::Reorder { .. })));
        assert!(
            plan.faults().iter().any(|fault| matches!(fault.kind, FaultKind::Duplicate { .. }))
        );
        assert!(
            plan.faults().iter().any(|fault| matches!(fault.kind, FaultKind::Partition { .. }))
        );
        assert!(plan.faults().iter().any(|fault| matches!(fault.kind, FaultKind::Restart { .. })));
        assert!(
            plan.faults().iter().any(|fault| matches!(fault.kind, FaultKind::ClockSkew { .. }))
        );
    }

    #[test]
    fn drop_and_partition_close_both_directions_then_heal() {
        let first = RegionId::new(0);
        let second = RegionId::new(1);
        let mut plan = FaultPlan::new();
        plan.push(Fault::drop(first, second, 2, 4));
        plan.push(Fault::partition(second, first, 6, 8));
        assert!(plan.link_open(first, second, 1));
        assert!(!plan.link_open(second, first, 2));
        assert!(plan.link_open(first, second, 4));
        assert!(!plan.link_open(first, second, 7));
        assert!(plan.link_open(second, first, 8));
    }

    #[test]
    fn restart_closes_region_links_then_heals() {
        let mut plan = FaultPlan::new();
        plan.push(Fault::restart(RegionId::new(0), 2, 4));
        assert!(plan.region_down(RegionId::new(0), 2));
        assert!(plan.region_down(RegionId::new(0), 3));
        assert!(!plan.link_open(RegionId::new(0), RegionId::new(1), 3));
        assert!(plan.link_open(RegionId::new(0), RegionId::new(1), 4));
    }

    #[test]
    fn controls_are_bounded() {
        let first = RegionId::new(0);
        let second = RegionId::new(1);
        let mut plan = FaultPlan::new();
        plan.push(Fault::clock_skew(first, i64::MAX, 0, 1));
        plan.push(Fault::duplicate(first, second, u32::MAX, 1, 2));
        plan.push(Fault::reorder(first, second, u64::MAX, 2, 3));
        assert_eq!(plan.clock_offset_ms(first, 0), MAX_CLOCK_SKEW_MS);
        assert_eq!(plan.duplicate_copies(first, second, 1), MAX_DUPLICATE_COPIES);
        assert_eq!(plan.reorder_window(first, second, 2), MAX_REORDER_WINDOW_STEPS);
    }

    #[test]
    fn generated_plans_are_deterministic_and_bounded() {
        let mut first_rng = SimRng::new(7);
        let mut second_rng = SimRng::new(7);
        let first = FaultPlan::generate(&mut first_rng, &regions(), 100, usize::MAX);
        let second = FaultPlan::generate(&mut second_rng, &regions(), 100, usize::MAX);
        assert_eq!(first, second);
        assert_eq!(first.len(), MAX_FAULTS);
        assert!(first.faults().iter().all(|fault| fault.end_step <= 100));
    }

    #[test]
    fn trace_round_trip_is_byte_stable() {
        let mut rng = SimRng::new(42);
        let plan = FaultPlan::generate(&mut rng, &regions(), 80, 12);
        let trace = plan.to_bytes();
        assert_eq!(FaultPlan::from_bytes(&trace).expect("trace decodes"), plan);
        assert_eq!(FaultPlan::from_bytes(&trace).expect("trace decodes").to_bytes(), trace);
        assert_eq!(plan.serialize(), trace);
        assert_eq!(FaultPlan::deserialize(&trace).expect("trace decodes"), plan);
    }

    #[test]
    fn malformed_traces_are_rejected() {
        let mut rng = SimRng::new(1);
        let trace = FaultPlan::generate(&mut rng, &regions(), 8, 1).to_bytes();
        assert_eq!(FaultPlan::from_bytes(&trace[..3]), Err(FaultPlanDecodeError::UnexpectedEof));
        let mut trailing = trace.clone();
        trailing.push(0);
        assert_eq!(FaultPlan::from_bytes(&trailing), Err(FaultPlanDecodeError::TrailingBytes));
    }

    #[test]
    fn old_kill_spelling_maps_to_restart() {
        assert_eq!(Fault::kill(RegionId::new(1), 2, 4), Fault::restart(RegionId::new(1), 2, 4));
    }
}
