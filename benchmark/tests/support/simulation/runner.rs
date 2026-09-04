//! Deterministic reference run. Production adapters are intentionally absent.

use std::collections::BTreeSet;

use super::faults::FaultPlan;
use super::model::{Dot, JournalRow, ReferenceWorld, Region};

/// Limits for one test run.
#[derive(Clone, Copy, Debug)]
pub struct ReferenceConfig {
    /// Seed used for workload and faults.
    pub seed: u64,
    /// Number of simulated steps.
    pub steps: u32,
    /// Maximum number of faults.
    pub max_faults: usize,
    /// Number of acknowledged rows.
    pub writes: u32,
}

impl Default for ReferenceConfig {
    fn default() -> Self {
        Self { seed: 0x0E12, steps: 64, max_faults: 12, writes: 32 }
    }
}

/// Facts from a reference run, with no production durability claim.
#[derive(Debug)]
pub struct ReferenceReport {
    /// The generated bounded schedule.
    pub plan: FaultPlan,
    /// Acknowledged rows in origin order.
    pub acknowledged: Vec<JournalRow>,
    /// Drained reference state.
    pub world: ReferenceWorld,
    /// Delivery count including duplicates.
    pub deliveries: usize,
}

/// Runs a deterministic finite reference scenario and drains all temporary
/// faults before checking state. This is test mechanics, not engine evidence.
#[must_use]
pub fn run_reference(config: ReferenceConfig) -> ReferenceReport {
    let regions: [Region; 3] = [0, 1, 2];
    let plan = FaultPlan::generate(config.seed, config.steps, config.max_faults, &regions);
    let mut world = ReferenceWorld::new(&regions);
    let mut acknowledged = Vec::with_capacity(config.writes as usize);
    let mut previous = None;
    for index in 0..config.writes {
        let origin = (index % regions.len() as u32) as Region;
        let dot = Dot { origin, sequence: u64::from(index) + 1 };
        let context = previous.into_iter().collect::<BTreeSet<_>>();
        let step = index % config.steps.max(1);
        let skew = i64::from(plan.clock_offset_ms(origin, step));
        let row = JournalRow {
            dot,
            context,
            key: u64::from(index % 7),
            value: Some(config.seed ^ u64::from(index)),
            hlc: i64::from(index).saturating_add(skew).max(0),
        };
        previous = Some(dot);
        world.acknowledge(row.clone());
        acknowledged.push(row);
    }

    let mut deliveries = 0;
    for step in 0..config.steps {
        for row in &acknowledged {
            for region in regions {
                if region == row.dot.origin {
                    continue;
                }
                if plan.restarting(row.dot.origin, step) || plan.restarting(region, step) {
                    continue;
                }
                let link = super::faults::Link::new(row.dot.origin, region);
                if plan.blocked(link, step) {
                    continue;
                }
                let copies = usize::from(plan.copies(link, step));
                for _ in 0..copies {
                    world.apply(region, row);
                    deliveries += 1;
                }
            }
        }
    }
    // Faults are finite. The drain models retries after a healed link and is
    // what lets the reference checks distinguish temporary drops from loss.
    for row in &acknowledged {
        for region in regions {
            world.apply(region, row);
            deliveries += 1;
        }
    }
    ReferenceReport { plan, acknowledged, world, deliveries }
}
