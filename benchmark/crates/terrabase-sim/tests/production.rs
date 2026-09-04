//! Focused integration checks for the durable production simulation adapter.

use terrabase_member_transport::MemberTransport;
use terrabase_sim::{
    Fault, FaultPlan, Frontier, ProductionWorld, ReplicationEngine, SessionToken, SyncEngine,
    capability_for,
};

#[test]
fn durable_engine_delivery_converges_and_survives_restart() {
    let mut world = ProductionWorld::new(3).expect("durable simulation world");
    let regions = [
        terrabase_sim::RegionId::new(0),
        terrabase_sim::RegionId::new(1),
        terrabase_sim::RegionId::new(2),
    ];
    let row = world.submit(regions[0], 7, 77).expect("origin commit");
    let mut token = SessionToken::observing(Frontier::new());
    token.observe(&row);
    for _ in 0..8 {
        world.step();
    }
    assert_eq!(world.read(regions[1], &token, 7), Some(Some(77)));
    assert_eq!(world.fingerprint(regions[0]), world.fingerprint(regions[1]));
    world.restart_region(regions[1]).expect("restart durable Engine");
    assert_eq!(world.read(regions[1], &token, 7), Some(Some(77)));
}

#[test]
fn transport_faults_are_applied_and_heal_without_ack_loss() {
    let mut faults = FaultPlan::new();
    faults.push(Fault::partition(
        terrabase_sim::RegionId::new(0),
        terrabase_sim::RegionId::new(1),
        0,
        4,
    ));
    faults.push(Fault::duplicate(
        terrabase_sim::RegionId::new(0),
        terrabase_sim::RegionId::new(2),
        2,
        0,
        4,
    ));
    let mut world = ProductionWorld::with_faults(3, faults).expect("durable simulation world");
    let regions = [
        terrabase_sim::RegionId::new(0),
        terrabase_sim::RegionId::new(1),
        terrabase_sim::RegionId::new(2),
    ];
    let row = world.submit(regions[0], 11, 101).expect("origin commit");
    for _ in 0..16 {
        world.step();
    }
    assert!(world.is_uniform(&row, &regions));
    assert!(
        world
            .transport()
            .link_stats(
                &terrabase_member_transport::MemberId::try_new("sim-member-0").unwrap(),
                &terrabase_member_transport::MemberId::try_new("sim-member-2").unwrap(),
            )
            .delivered
            > 0
    );
}

#[test]
fn real_sync_core_revision_and_capability_cache_are_deterministic() {
    let mut world = ProductionWorld::new(1).expect("durable simulation world");
    let region = terrabase_sim::RegionId::new(0);
    world.submit(region, 2, 22).expect("origin commit");
    let revision = world.prime_cache("all", region).expect("view revision");
    let capability = capability_for("all", revision.generation);
    assert!(world.verify_revision("all", region));
    assert_eq!(world.cached_bytes("all", revision.generation, 0, capability), Some(revision.bytes));
    assert_eq!(world.cached_bytes("all", revision.generation, 0, capability.wrapping_add(1)), None);
}

#[test]
fn reconciliation_uses_production_range_kernel() {
    let mut world = ProductionWorld::new(2).expect("durable simulation world");
    let left = terrabase_sim::RegionId::new(0);
    let right = terrabase_sim::RegionId::new(1);
    world.submit(left, 3, 30).expect("origin commit");
    let stats = world.reconcile(left, right);
    assert!(stats.rounds > 0);
    assert!(stats.rows_shipped > 0);
    assert_eq!(world.fingerprint(left), world.fingerprint(right));
}
