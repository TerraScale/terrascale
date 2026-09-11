//! Temporary reproducer for the wedged drain seed; deleted once the root
//! cause is fixed.

use terrabase_member_transport::MemberTransport as _;
use terrabase_sim::check::SimConfig;
use terrabase_sim::faults::FaultPlan;
use terrabase_sim::model::RegionId;
use terrabase_sim::production::ProductionWorld;
use terrabase_sim::rng::SimRng;
use terrabase_sim::traits::ReplicationEngine as _;
use terrabase_sim::workload::{Op, Workload};

fn main() {
    let seed = 15_755_400_384_260_043_847_u64;
    let config = SimConfig::with_seed(seed);
    let regions: Vec<RegionId> = (0..config.regions).map(RegionId::from_index).collect();
    let mut rng = SimRng::new(config.seed);
    let workload = Workload::generate(&mut rng, &regions, config.ops, config.keys);
    let faults = FaultPlan::generate(&mut rng, &regions, config.steps.max(1), config.max_faults);
    println!("faults: {faults:#?}");
    let Ok(mut world) = ProductionWorld::with_faults(regions.len(), faults.clone()) else {
        println!("world failed to open");
        return;
    };
    let mut committed = 0u64;
    let mut op_index = 0usize;
    let horizon = config.steps.max(1);
    let ops = workload.ops();
    for _ in 0..horizon {
        if let Some(op) = ops.get(op_index % ops.len()) {
            match op {
                Op::Write { region, key, value } => {
                    if world.submit(*region, *key, *value).is_some() {
                        committed += 1;
                    }
                }
                Op::Delete { region, key } => {
                    if world.delete(*region, *key).is_some() {
                        committed += 1;
                    }
                }
                Op::Read { .. } => {}
            }
            op_index += 1;
        }
        world.step();
    }
    let mut steps_run = horizon;
    while !world.is_quiescent() && steps_run < horizon + 10_000 {
        world.step();
        steps_run += 1;
    }
    println!("committed={committed} steps_run={steps_run} quiescent={}", world.is_quiescent());
    println!("{}", world.debug_wedge());
    for source in world.region_ids() {
        for target in world.region_ids() {
            let stats = world.transport().link_stats(
                &terrabase_member_transport::MemberId::try_new(&format!(
                    "sim-member-{}",
                    source.index()
                ))
                .unwrap(),
                &terrabase_member_transport::MemberId::try_new(&format!(
                    "sim-member-{}",
                    target.index()
                ))
                .unwrap(),
            );
            if stats.queued > 0 || stats.held > 0 {
                println!("link {source:?}->{target:?} queued={} held={}", stats.queued, stats.held);
            }
        }
    }
    for region in world.region_ids() {
        println!("fingerprint {region:?} = {}", world.fingerprint(region));
    }
}
