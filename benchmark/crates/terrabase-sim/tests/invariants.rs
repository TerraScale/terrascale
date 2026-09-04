//! One executable qualification per simulation invariant (ADR 0044 §4),
//! plus the bounded CI seed batch.
//!
//! The eight invariant tests execute against the durable production adapter.
//! Scenarios remain seeded and bounded so failures reproduce from the printed
//! seed while CI can run the small matrix without an unbounded sweep.

use terrabase_sim::{
    Fault, FaultPlan, Frontier, Invariant, JournalRow, ProductionWorld, RegionId, SessionToken,
    SimConfig, SimRng, SyncEngine as _, ViewCut, Workload, capability_for, run_scenario,
    seed_batch,
};
use terrabase_sim::{ReplicationEngine as _, VIEW_ALL, VIEW_EVEN_KEYS};

/// Builds a three-region world under a seeded fault plan.
fn seeded_world(seed: u64, steps: u64, max_faults: usize) -> (ProductionWorld, Vec<RegionId>) {
    let regions = vec![RegionId::new(0), RegionId::new(1), RegionId::new(2)];
    let mut rng = SimRng::new(seed);
    let faults = FaultPlan::generate(&mut rng, &regions, steps, max_faults);
    (
        ProductionWorld::with_faults(regions.len(), faults).expect("durable production world"),
        regions,
    )
}

/// Plays a seeded workload, then drains past the fault horizon.
/// Returns every acknowledged row, so invariant 1 can prove none was lost.
fn drive(
    world: &mut ProductionWorld,
    regions: &[RegionId],
    seed: u64,
    ops: usize,
    keys: u64,
) -> Vec<JournalRow> {
    let mut rng = SimRng::new(seed);
    let workload = Workload::generate(&mut rng, regions, ops, keys);
    let mut acked = Vec::new();
    for op in workload.ops() {
        match op {
            terrabase_sim::Op::Write { region, key, value } => {
                if let Some(row) = world.submit(*region, *key, *value) {
                    acked.push(row);
                }
            }
            terrabase_sim::Op::Delete { region, key } => {
                if let Some(row) = world.delete(*region, *key) {
                    acked.push(row);
                }
            }
            terrabase_sim::Op::Read { .. } => {}
        }
        world.step();
    }
    for _ in 0..500 {
        if world.is_quiescent() {
            break;
        }
        world.step();
    }
    acked
}

fn assert_invariant_header(invariant: Invariant, number: u8) {
    assert_eq!(invariant.number(), number);
    assert!(!invariant.title().is_empty());
}

#[test]
fn invariant_1_no_acknowledged_write_lost() {
    assert_invariant_header(Invariant::NoAcknowledgedWriteLost, 1);
    let (mut world, regions) = seeded_world(101, 64, 6);
    let acked = drive(&mut world, &regions, 101, 96, 12);
    assert!(!acked.is_empty(), "scenario must acknowledge writes");
    assert!(world.is_quiescent(), "world must drain after faults heal");
    // Every acknowledged row — and all its causal dependencies — is
    // present in every region: nothing acked was lost to any fault.
    for row in &acked {
        assert!(world.is_uniform(row, &regions), "acked row {:?} lost under faults", row.id());
    }
    let first = world.fingerprint(regions[0]);
    for region in &regions {
        assert_eq!(world.fingerprint(*region), first, "lost write at {region:?}");
    }
}

#[test]
fn invariant_2_regions_converge_to_equal_fingerprints() {
    assert_invariant_header(Invariant::ConvergentFingerprints, 2);
    for seed in seed_batch(202, 4) {
        let (mut world, regions) = seeded_world(seed, 64, 6);
        let _ = drive(&mut world, &regions, seed, 96, 12);
        assert!(world.is_quiescent(), "seed {seed} did not drain");
        let first = world.fingerprint(regions[0]);
        for region in &regions {
            assert_eq!(world.fingerprint(*region), first, "seed {seed}: divergence at {region:?}");
        }
    }
}

#[test]
fn invariant_3_merge_is_order_independent() {
    assert_invariant_header(Invariant::OrderIndependentMerge, 3);
    let mut rng = SimRng::new(303);
    let regions = vec![RegionId::new(0), RegionId::new(1), RegionId::new(2)];
    let mut world = ProductionWorld::new(regions.len()).expect("durable production world");
    let mut rows: Vec<JournalRow> = Vec::new();
    for _ in 0..48 {
        let region = rng.pick(&regions).copied().unwrap_or(RegionId::new(0));
        let key = rng.below(8);
        let row = if rng.biased(1, 5) {
            world.delete(region, key)
        } else {
            world.submit(region, key, rng.next_u64())
        };
        if let Some(row) = row {
            rows.push(row);
        }
    }
    assert!(!rows.is_empty());
    let mut digests = Vec::new();
    for round in 0..8 {
        let mut shuffled = rows.clone();
        let mut round_rng = SimRng::new(1000 + round);
        round_rng.shuffle(&mut shuffled);
        digests.push(world.digest_after_permutation(&shuffled));
    }
    for digest in &digests {
        assert_eq!(*digest, digests[0], "permutation changed the merged state");
    }
}

#[test]
fn invariant_4_global_durable_implies_uniform_presence() {
    assert_invariant_header(Invariant::GlobalDurableUniform, 4);
    let (mut world, regions) = seeded_world(404, 64, 6);
    let mut acked: Vec<JournalRow> = Vec::new();
    let mut rng = SimRng::new(404);
    for _ in 0..32 {
        let region = rng.pick(&regions).copied().unwrap_or(RegionId::new(0));
        if let Some(row) = world.submit(region, rng.below(8), rng.next_u64()) {
            acked.push(row);
        }
        world.step();
    }
    for _ in 0..500 {
        if world.is_quiescent() {
            break;
        }
        world.step();
    }
    assert!(world.is_quiescent());
    for row in &acked {
        assert!(world.is_uniform(row, &regions), "acked row {:?} is not uniform", row.id());
        // Uniformity covers causal dependencies too: the context frontier
        // is covered on every selected region.
        for region in &regions {
            assert!(
                world.applied_frontier(*region).covers(&row.context),
                "dependency of {:?} missing at {region:?}",
                row.id()
            );
        }
    }
}

#[test]
fn invariant_5_read_your_writes_across_failover() {
    assert_invariant_header(Invariant::ReadYourWrites, 5);
    let mut faults = FaultPlan::new();
    faults.push(Fault::kill(RegionId::new(0), 20, 40));
    let mut world = ProductionWorld::with_faults(3, faults).expect("durable production world");
    let writer = RegionId::new(1);
    let row = world.submit(writer, 7, 77).expect("writer is up");
    let mut token = SessionToken::observing(Frontier::new());
    token.observe(&row);
    // A region that has applied the write serves it under the token.
    for _ in 0..30 {
        world.step();
    }
    assert_eq!(world.read(writer, &token, 7), Some(Some(77)), "writer must serve its own write");
    // A region behind the token refuses instead of serving stale data.
    let behind = RegionId::new(0);
    if !world.applied_frontier(behind).covers(&token.frontier) {
        assert_eq!(
            world.read(behind, &token, 7),
            None,
            "behind region must return StaleReplica, not stale data"
        );
    }
    // After failover and healing, the token still resolves.
    for _ in 0..200 {
        if world.is_quiescent() {
            break;
        }
        world.step();
    }
    assert_eq!(world.read(RegionId::new(2), &token, 7), Some(Some(77)));
}

#[test]
fn invariant_6_view_revision_equals_query_at_cut() {
    assert_invariant_header(Invariant::ViewRevisionMatchesCut, 6);
    let (mut world, regions) = seeded_world(606, 64, 6);
    let _ = drive(&mut world, &regions, 606, 96, 12);
    for region in &regions {
        for view in [VIEW_ALL, VIEW_EVEN_KEYS] {
            assert!(
                world.verify_revision(view, *region),
                "view {view} at {region:?} != Q(database at its cut)"
            );
        }
    }
    // Retain one revision, then advance the database. Verification must use
    // the revision's frontier cut rather than silently checking a fresh view.
    let revision = world.view_revision(VIEW_ALL, regions[0]).expect("view revision");
    world.submit(regions[0], 0, 9_999).expect("later write");
    assert!(world.verify_revision_at(&revision, regions[0]));
}

#[test]
fn invariant_7_cached_bytes_equal_origin_bytes_within_capability() {
    assert_invariant_header(Invariant::CacheIntegrity, 7);
    let (mut world, regions) = seeded_world(707, 32, 2);
    let _ = drive(&mut world, &regions, 707, 48, 8);
    for region in &regions {
        let revision = world.prime_cache(VIEW_ALL, *region).expect("view exists");
        let capability = capability_for(VIEW_ALL, revision.generation);
        let cached = world
            .cached_bytes(VIEW_ALL, revision.generation, 0, capability)
            .expect("same key must hit");
        assert_eq!(cached, revision.bytes, "cache must equal origin bytes");
        assert_eq!(
            world.cached_bytes(VIEW_ALL, revision.generation, 0, capability.wrapping_add(1)),
            None,
            "bytes must never cross a capability boundary"
        );
        assert_eq!(
            world.cached_bytes(VIEW_ALL, revision.generation, 1, capability),
            None,
            "unknown offsets must miss"
        );
    }
}

#[test]
fn invariant_8_reconciliation_is_logarithmic_and_ships_only_diffs() {
    assert_invariant_header(Invariant::ReconciliationComplexity, 8);
    let regions = [RegionId::new(0), RegionId::new(1)];
    let mut world = ProductionWorld::new(regions.len()).expect("durable production world");
    let mut rng = SimRng::new(808);
    // Skew the two sides: disjoint writers, then a hard partition worth
    // of independent commits with no delivery in between.
    for _ in 0..600 {
        let side = usize::try_from(rng.below(2)).unwrap_or(0);
        world.submit(regions[side], rng.below(200), rng.next_u64());
    }
    // Simulate the partition by reconciling directly: count the cost.
    let stats = world.reconcile(regions[0], regions[1]);
    assert_eq!(
        world.fingerprint(regions[0]),
        world.fingerprint(regions[1]),
        "reconciliation must converge"
    );
    // 64-ary descent over ~600 rows: root + 2 levels = 3 rounds worst case.
    assert!(stats.rounds <= 4, "reconciliation took {} rounds, want O(log n)", stats.rounds);
    assert!(stats.rows_shipped > 0, "divergent sides must ship diffs");
    // A second run is a single root round shipping nothing.
    let again = world.reconcile(regions[0], regions[1]);
    assert_eq!(again.rows_shipped, 0);
    assert_eq!(again.rounds, 1);
}

/// CI seed batch: fast, non-ignored, deterministic. Runs a small matrix of
/// seeds through workload + faults + drain and asserts determinism,
/// quiescence, and convergence. Must stay well under a second.
#[test]
fn seed_batch_is_deterministic_convergent_and_fast() {
    for seed in seed_batch(4_044, 6) {
        let config = SimConfig { seed, regions: 3, ops: 48, keys: 12, steps: 48, max_faults: 4 };
        let first = run_scenario(config);
        let second = run_scenario(config);
        assert_eq!(first, second, "seed {seed} is not deterministic");
        assert!(first.quiescent, "seed {seed} did not drain");
        assert!(first.converged, "seed {seed} did not converge");
        assert!(first.committed > 0, "seed {seed} committed nothing");
    }
}

/// The checklist itself is a contract: exactly the eight ADR §4 invariants.
#[test]
fn checklist_names_all_eight_invariants() {
    let all = Invariant::all();
    assert_eq!(all.len(), 8);
    let titles: Vec<&str> = all.iter().map(|invariant| invariant.title()).collect();
    assert_eq!(
        titles,
        [
            "no acknowledged write lost",
            "convergent fingerprints",
            "order-independent merge",
            "global-durable uniformity",
            "read-your-writes",
            "view revision matches cut",
            "cache integrity",
            "reconciliation complexity",
        ]
    );
    // `ViewCut` is part of the invariant-6 vocabulary; keep it linked.
    let cut = ViewCut::new(Frontier::new(), 0);
    assert!(cut.frontier.is_empty());
}
