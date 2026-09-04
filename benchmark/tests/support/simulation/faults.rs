//! Bounded, replayable fault schedules.

use super::rng::SeededRng;

/// Maximum clock skew accepted by ADR 0044.
pub const MAX_CLOCK_SKEW_MS: i64 = 500;

/// A link endpoint pair. Link ordering is canonical.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Link {
    /// Sending or receiving region.
    pub first: u8,
    /// Receiving or sending region.
    pub second: u8,
}

impl Link {
    /// Creates a canonical unordered link.
    #[must_use]
    pub const fn new(first: u8, second: u8) -> Self {
        if first <= second { Self { first, second } } else { Self { first: second, second: first } }
    }
}

/// A bounded fault applied over the half-open interval `[start, end)`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Fault {
    /// First active step.
    pub start: u32,
    /// First inactive step.
    pub end: u32,
    /// Fault behavior.
    pub kind: FaultKind,
}

/// Faults covered by the Wave-0 scheduler.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FaultKind {
    /// Holds and drops deliveries on a link while active.
    Drop { link: Link },
    /// Holds deliveries and releases them in a bounded window.
    Reorder { link: Link, window: u32 },
    /// Sends one frame more than once.
    Duplicate { link: Link, copies: u8 },
    /// Severs a link in both directions.
    Partition { link: Link },
    /// Restarts one region. Durable rows remain in the reference model.
    Restart { region: u8 },
    /// Shifts the reference HLC clock for one region.
    ClockSkew { region: u8, offset_ms: i16 },
}

impl Fault {
    /// Returns whether the fault is active at `step`.
    #[must_use]
    pub const fn active_at(self, step: u32) -> bool {
        self.start <= step && step < self.end
    }
}

/// The deterministic schedule used by one reference run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FaultPlan {
    horizon: u32,
    faults: Vec<Fault>,
}

impl FaultPlan {
    /// Builds a schedule. The number of entries and each interval are bounded.
    #[must_use]
    pub fn generate(seed: u64, horizon: u32, max_faults: usize, regions: &[u8]) -> Self {
        let horizon = horizon.max(1);
        if max_faults == 0 || regions.is_empty() {
            return Self { horizon, faults: Vec::new() };
        }
        let mut rng = SeededRng::new(seed);
        let links: Vec<_> = regions
            .iter()
            .copied()
            .enumerate()
            .flat_map(|(index, first)| {
                regions[index + 1..].iter().copied().map(move |second| Link::new(first, second))
            })
            .collect();
        let mut faults = Vec::with_capacity(max_faults);
        for index in 0..max_faults {
            let start = rng.below(u64::from(horizon)) as u32;
            let max_duration = (horizon / 4).max(1);
            let duration = 1 + rng.below(u64::from(max_duration)) as u32;
            let end = start.saturating_add(duration).min(horizon);
            let region = regions[rng.below(regions.len() as u64) as usize];
            let link = links
                .get(rng.below(links.len() as u64) as usize)
                .copied()
                .unwrap_or_else(|| Link::new(region, region));
            let kind = match index % 6 {
                0 => FaultKind::Drop { link },
                1 => FaultKind::Reorder { link, window: 2 + rng.below(4) as u32 },
                2 => FaultKind::Duplicate { link, copies: 2 + rng.below(2) as u8 },
                3 => FaultKind::Partition { link },
                4 => FaultKind::Restart { region },
                _ => {
                    let magnitude = 1 + rng.below((MAX_CLOCK_SKEW_MS as u64) + 1) as i16;
                    let offset_ms = if rng.below(2) == 0 { magnitude } else { -magnitude };
                    FaultKind::ClockSkew { region, offset_ms }
                }
            };
            faults.push(Fault { start, end, kind });
        }
        Self { horizon, faults }
    }

    /// Returns the schedule horizon.
    #[must_use]
    pub const fn horizon(&self) -> u32 {
        self.horizon
    }

    /// Returns all scheduled faults.
    #[must_use]
    pub fn faults(&self) -> &[Fault] {
        &self.faults
    }

    /// Returns the faults active at `step`.
    pub fn active_at(&self, step: u32) -> impl Iterator<Item = Fault> + '_ {
        self.faults.iter().copied().filter(move |fault| fault.active_at(step))
    }

    /// Returns true when a region is restarting at `step`.
    #[must_use]
    pub fn restarting(&self, region: u8, step: u32) -> bool {
        self.active_at(step).any(|fault| {
            matches!(fault.kind, FaultKind::Restart { region: candidate } if candidate == region)
        })
    }

    /// Returns true when delivery over `link` is blocked at `step`.
    #[must_use]
    pub fn blocked(&self, link: Link, step: u32) -> bool {
        self.active_at(step).any(|fault| {
            matches!(fault.kind, FaultKind::Drop { link: candidate } | FaultKind::Partition { link: candidate } if candidate == link)
        })
    }

    /// Returns the duplicate fan-out, with one meaning normal delivery.
    #[must_use]
    pub fn copies(&self, link: Link, step: u32) -> u8 {
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::Duplicate { link: candidate, copies } if candidate == link => {
                    Some(copies)
                }
                _ => None,
            })
            .max()
            .unwrap_or(1)
    }

    /// Returns the summed clock offset active for one region.
    #[must_use]
    pub fn clock_offset_ms(&self, region: u8, step: u32) -> i16 {
        self.active_at(step)
            .filter_map(|fault| match fault.kind {
                FaultKind::ClockSkew { region: candidate, offset_ms } if candidate == region => {
                    Some(offset_ms)
                }
                _ => None,
            })
            .fold(0_i16, i16::saturating_add)
            .clamp(-(MAX_CLOCK_SKEW_MS as i16), MAX_CLOCK_SKEW_MS as i16)
    }
}
