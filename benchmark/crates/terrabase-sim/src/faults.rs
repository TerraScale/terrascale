//! Fault injectors for the simulation harness (E-12, ADR 0044 §4).
//!
//! Every fault the eight invariants name — WAN partition, member/region
//! loss, clock skew, slow links, duplicate and reordered delivery — is a
//! [`FaultKind`] active over a half-open step interval `[start, end)` in a
//! deterministic [`FaultPlan`]. [`FaultPlan::generate`] derives the schedule
//! from the scenario seed, so a failing run reproduces fault-for-fault.

use crate::model::RegionId;
use crate::rng::SimRng;

/// Maximum clock skew the harness injects (ms); matches invariant 1.
pub const MAX_CLOCK_SKEW_MS: i64 = 500;

/// Maximum clock-skew magnitude as an unsigned millisecond count.
///
/// Exists so skew magnitudes stay in `u64` RNG ranges without a
/// sign-changing cast; always equals `MAX_CLOCK_SKEW_MS as u64`.
pub const MAX_CLOCK_SKEW_MAGNITUDE_MS: u64 = 500;

/// One seeded fault. Use the constructors, not struct literals: they keep
/// the endpoint ordering canonical so link faults match in both directions.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Fault {
    /// First step the fault is active (inclusive).
    pub start_step: u64,
    /// First step the fault is healed (exclusive).
    pub end_step: u64,
    /// What the fault does.
    pub kind: FaultKind,
}

/// The fault injectors. Each variant is one knob the nightly sweep turns.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FaultKind {
    /// WAN partition: no frames flow between the two regions.
    Partition {
        /// One side of the severed link.
        first: RegionId,
        /// The other side of the severed link.
        second: RegionId,
    },
    /// Member/region loss: the region serves, applies, and acks nothing.
    Kill {
        /// The lost region.
        region: RegionId,
    },
    /// Clock skew: the region's HLC stamps shift by `offset_ms`
    /// (bounded by [`MAX_CLOCK_SKEW_MS`]).
    ClockSkew {
        /// The skewed region.
        region: RegionId,
        /// Clock offset in milliseconds, in `[-500, 500]`.
        offset_ms: i64,
    },
    /// Slow link: frames between the regions arrive `delay_steps` late.
    SlowLink {
        /// One side of the slow link.
        first: RegionId,
        /// The other side of the slow link.
        second: RegionId,
        /// Delivery delay in simulation steps.
        delay_steps: u64,
    },
    /// Duplicate delivery: each frame on the link arrives `copies` times.
    Duplicate {
        /// The sending side.
        first: RegionId,
        /// The receiving side.
        second: RegionId,
        /// Total deliveries per frame (≥ 2 while active).
        copies: u32,
    },
    /// Reordered delivery: frames on the link are held and released in a
    /// shuffled window of `window` steps.
    Reorder {
        /// The sending side.
        first: RegionId,
        /// The receiving side.
        second: RegionId,
        /// The reorder window in simulation steps (≥ 2 while active).
        window: u64,
    },
}

impl Fault {
    /// Creates a WAN partition between two regions over `[start, end)`.
    #[must_use]
    pub fn partition(first: RegionId, second: RegionId, start: u64, end: u64) -> Self {
        let (ordered_first, ordered_second) = canonical_link(first, second);
        Self {
            start_step: start,
            end_step: end,
            kind: FaultKind::Partition { first: ordered_first, second: ordered_second },
        }
    }

    /// Creates a member/region loss over `[start, end)`.
    #[must_use]
    pub fn kill(region: RegionId, start: u64, end: u64) -> Self {
        Self { start_step: start, end_step: end, kind: FaultKind::Kill { region } }
    }

    /// Creates a clock-skew injection over `[start, end)`; the offset is
    /// clamped to `[-MAX_CLOCK_SKEW_MS, MAX_CLOCK_SKEW_MS]`.
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

    /// Creates a slow link over `[start, end)`.
    #[must_use]
    pub fn slow_link(
        first: RegionId,
        second: RegionId,
        delay_steps: u64,
        start: u64,
        end: u64,
    ) -> Self {
        let (ordered_first, ordered_second) = canonical_link(first, second);
        Self {
            start_step: start,
            end_step: end,
            kind: FaultKind::SlowLink {
                first: ordered_first,
                second: ordered_second,
                delay_steps: delay_steps.max(1),
            },
        }
    }

    /// Creates duplicate delivery on a link over `[start, end)`.
    #[must_use]
    pub fn duplicate(first: RegionId, second: RegionId, copies: u32, start: u64, end: u64) -> Self {
        let (ordered_first, ordered_second) = canonical_link(first, second);
        Self {
            start_step: start,
            end_step: end,
            kind: FaultKind::Duplicate {
                first: ordered_first,
                second: ordered_second,
                copies: copies.max(2),
            },
        }
    }

    /// Creates reordered delivery on a link over `[start, end)`.
    #[must_use]
    pub fn reorder(first: RegionId, second: RegionId, window: u64, start: u64, end: u64) -> Self {
        let (ordered_first, ordered_second) = canonical_link(first, second);
        Self {
            start_step: start,
            end_step: end,
            kind: FaultKind::Reorder {
                first: ordered_first,
                second: ordered_second,
                window: window.max(2),
            },
        }
    }

    /// Returns `true` when this fault is active at `step`.
    #[must_use]
    pub fn is_active(&self, step: u64) -> bool {
        self.start_step <= step && step < self.end_step
    }
}

/// Orders link endpoints so each unordered pair has one representation.
fn canonical_link(first: RegionId, second: RegionId) -> (RegionId, RegionId) {
    if first <= second { (first, second) } else { (second, first) }
}

/// Returns `true` when `kind` severs the unordered link `(first, second)`.
fn kind_severs(kind: &FaultKind, first: RegionId, second: RegionId) -> bool {
    let (ordered_first, ordered_second) = canonical_link(first, second);
    matches!(
        kind,
        FaultKind::Partition {
            first: link_first,
            second: link_second,
        } if *link_first == ordered_first && *link_second == ordered_second
    )
}

/// Deterministic schedule of faults for one scenario.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct FaultPlan {
    /// The scheduled faults in insertion order.
    faults: Vec<Fault>,
}

impl FaultPlan {
    /// Creates an empty plan (no faults).
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Appends one fault; faults with `end_step <= start_step` are dropped.
    pub fn push(&mut self, fault: Fault) {
        if fault.end_step > fault.start_step {
            self.faults.push(fault);
        }
    }

    /// Returns the scheduled faults in insertion order.
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

    /// Derives a deterministic fault schedule from the scenario RNG.
    ///
    /// Picks up to `max_faults` faults of uniformly random kinds over
    /// `[0, steps)`, each lasting at least one step and at most a quarter
    /// of the run so every fault heals before quiescence is checked.
    #[must_use]
    pub fn generate(rng: &mut SimRng, regions: &[RegionId], steps: u64, max_faults: usize) -> Self {
        let mut plan = Self::new();
        if regions.is_empty() || steps == 0 || max_faults == 0 {
            return plan;
        }
        let horizon = steps.max(4);
        let max_duration = (horizon / 4).max(1);
        for _ in 0..max_faults {
            let Some(first) = rng.pick(regions).copied() else {
                break;
            };
            let Some(second) = rng.pick(regions).copied() else {
                break;
            };
            if first == second {
                continue;
            }
            let start = rng.below(horizon);
            let duration = rng.between(1, max_duration);
            let end = (start + duration).min(horizon);
            if end <= start {
                continue;
            }
            let choice = rng.below(6);
            match choice {
                0 => plan.push(Fault::partition(first, second, start, end)),
                1 => plan.push(Fault::kill(first, start, end)),
                2 => {
                    let magnitude = rng.between(0, MAX_CLOCK_SKEW_MAGNITUDE_MS);
                    let Some(positive) = i64::try_from(magnitude).ok() else {
                        continue;
                    };
                    let clamped = MAX_CLOCK_SKEW_MS.min(positive);
                    let signed = if rng.biased(1, 2) { clamped } else { clamped.saturating_neg() };
                    plan.push(Fault::clock_skew(first, signed, start, end));
                }
                3 => plan.push(Fault::slow_link(first, second, rng.between(1, 5), start, end)),
                4 => {
                    let copies: u32 = if rng.biased(1, 2) { 2 } else { 3 };
                    plan.push(Fault::duplicate(first, second, copies, start, end));
                }
                _ => plan.push(Fault::reorder(first, second, rng.between(2, 4), start, end)),
            }
        }
        plan
    }

    /// Iterates over the faults active at `step`.
    pub fn active_at(&self, step: u64) -> impl Iterator<Item = &Fault> {
        self.faults.iter().filter(move |fault| fault.is_active(step))
    }

    /// Returns `true` when `region` is killed at `step`.
    #[must_use]
    pub fn region_down(&self, region: RegionId, step: u64) -> bool {
        self.active_at(step)
            .any(|fault| matches!(fault.kind, FaultKind::Kill { region: dead } if dead == region))
    }

    /// Returns `true` when frames flow between the regions at `step`:
    /// no partition severs the link and neither endpoint is down.
    #[must_use]
    pub fn link_open(&self, first: RegionId, second: RegionId, step: u64) -> bool {
        if first != second && (self.region_down(first, step) || self.region_down(second, step)) {
            return false;
        }
        !self.active_at(step).any(|fault| kind_severs(&fault.kind, first, second))
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

    /// Returns the maximum slow-link delay (steps) on the link at `step`.
    #[must_use]
    pub fn link_delay_steps(&self, first: RegionId, second: RegionId, step: u64) -> u64 {
        let (ordered_first, ordered_second) = canonical_link(first, second);
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::SlowLink { first: link_first, second: link_second, delay_steps }
                    if link_first == ordered_first && link_second == ordered_second =>
                {
                    Some(delay_steps)
                }
                _ => None,
            })
            .max()
            .unwrap_or(0)
    }

    /// Returns the maximum duplicate-delivery fan-out on the link at `step`
    /// (`1` means no duplication).
    #[must_use]
    pub fn duplicate_copies(&self, first: RegionId, second: RegionId, step: u64) -> u32 {
        let (ordered_first, ordered_second) = canonical_link(first, second);
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::Duplicate { first: link_first, second: link_second, copies }
                    if link_first == ordered_first && link_second == ordered_second =>
                {
                    Some(copies)
                }
                _ => None,
            })
            .max()
            .unwrap_or(1)
    }

    /// Returns the maximum reorder window (steps) on the link at `step`
    /// (`0` means no reordering).
    #[must_use]
    pub fn reorder_window(&self, first: RegionId, second: RegionId, step: u64) -> u64 {
        let (ordered_first, ordered_second) = canonical_link(first, second);
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::Reorder { first: link_first, second: link_second, window }
                    if link_first == ordered_first && link_second == ordered_second =>
                {
                    Some(window)
                }
                _ => None,
            })
            .max()
            .unwrap_or(0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_intervals_are_dropped() {
        let mut plan = FaultPlan::new();
        plan.push(Fault::kill(RegionId::new(0), 5, 5));
        assert!(plan.is_empty());
    }

    #[test]
    fn partition_closes_the_link_in_both_directions() {
        let mut plan = FaultPlan::new();
        let first = RegionId::new(0);
        let second = RegionId::new(1);
        plan.push(Fault::partition(first, second, 2, 6));
        assert!(plan.link_open(first, second, 1));
        assert!(!plan.link_open(first, second, 2));
        assert!(!plan.link_open(second, first, 5));
        assert!(plan.link_open(first, second, 6));
    }

    #[test]
    fn kill_closes_every_link_of_the_region() {
        let mut plan = FaultPlan::new();
        plan.push(Fault::kill(RegionId::new(0), 0, 10));
        assert!(plan.region_down(RegionId::new(0), 3));
        assert!(!plan.region_down(RegionId::new(1), 3));
        assert!(!plan.link_open(RegionId::new(0), RegionId::new(1), 3));
        assert!(plan.link_open(RegionId::new(1), RegionId::new(2), 3));
    }

    #[test]
    fn generated_plans_are_deterministic() {
        let regions = [RegionId::new(0), RegionId::new(1), RegionId::new(2)];
        let mut first_rng = SimRng::new(7);
        let mut second_rng = SimRng::new(7);
        assert_eq!(
            FaultPlan::generate(&mut first_rng, &regions, 100, 6),
            FaultPlan::generate(&mut second_rng, &regions, 100, 6)
        );
    }
}
