//! Gap-sweep row 10 at the cluster acknowledgement boundary: one Member
//! fails AFTER the acknowledgement (`docs/evidence/replication/
//! member-loss-after-ack-lane.md`).
//!
//! The lane pins the RF=2/2 continuation window on the deterministic
//! acknowledgement ledger: an acknowledged write names BOTH seats of its
//! Region; after one seat fails, the survivor's durable observation is
//! still recorded, every NEW local write in the degraded Region is a typed
//! refusal naming the missing seat (never a weakened single-Member
//! receipt), the other selected Region keeps accepting writes at its own
//! two seats, and a replacement seat taken through an overlapping
//! membership step restores local writes at the full RF=2 boundary — with
//! the partial write from the degraded window fenced by the epoch step,
//! never silently completed.

use terrabase::replication::{MemberId, RegionId};
use terrabase_cluster::{
    DurabilityCoordinator, DurabilityError, DurabilityFailure, DurabilityLevel, DurableAck,
    Membership, RegionMembers,
};

/// Two selected Regions, two seats each: the RF=2/2 production shape.
fn two_region_rf2_membership() -> Membership {
    Membership::new(
        1,
        1,
        vec![
            RegionMembers::try_new("r-alpha", ["m-a1", "m-a2"]).expect("valid r-alpha seats"),
            RegionMembers::try_new("r-beta", ["m-b1", "m-b2"]).expect("valid r-beta seats"),
        ],
    )
    .expect("valid membership")
}

/// The overlapping replacement: `m-a3` takes the failed `m-a2` seat with
/// both fencing values advanced.
fn replacement_membership() -> Membership {
    Membership::new(
        2,
        2,
        vec![
            RegionMembers::try_new("r-alpha", ["m-a1", "m-a3"]).expect("valid r-alpha seats"),
            RegionMembers::try_new("r-beta", ["m-b1", "m-b2"]).expect("valid r-beta seats"),
        ],
    )
    .expect("valid replacement membership")
}

fn id(value: &str) -> MemberId {
    MemberId::try_new(value).expect("valid member identifier")
}

fn region(value: &str) -> RegionId {
    RegionId::try_new(value).expect("valid region identifier")
}

fn seat_names(receipt: &terrabase_cluster::DurabilityReceipt) -> Vec<MemberId> {
    receipt.contributors().iter().map(|ack| ack.member().clone()).collect()
}

// The charter row: the write is acknowledged at both seats, one Member
// fails after the acknowledgement, the survivor retains the durable fact,
// new local writes in the degraded Region refuse typed until RF=2 returns,
// and the other selected Region accepts writes throughout the window.
#[test]
fn member_loss_after_ack_survivor_retains_the_write_and_local_writes_refuse_until_rf2_returns() {
    let mut cluster = DurabilityCoordinator::new(two_region_rf2_membership());

    // The acknowledged write: RegionalDurable names BOTH seats of the
    // accepting Region — RF=2/2, the smallest shape that keeps the write
    // after either Member's loss.
    let receipt = cluster
        .submit("op-1", &id("m-a1"), DurabilityLevel::RegionalDurable)
        .expect("the write acknowledges at the Region's two seats");
    assert_eq!(receipt.level(), DurabilityLevel::RegionalDurable);
    assert_eq!(receipt.coordinator(), &id("m-a1"));
    assert_eq!(
        seat_names(&receipt),
        [id("m-a1"), id("m-a2")],
        "both seats of r-alpha durably hold the acknowledged write"
    );

    // One Member fails AFTER the acknowledgement.
    cluster.set_member_active(&id("m-a2"), false).expect("the failed seat is a known member");

    // The survivor retains the acknowledged durability fact: its stored
    // observation of op-1 outlives the other Member's failure, and
    // re-recording it is the typed duplicate refusal — never a fresh fact.
    let retained = DurableAck::new("op-1".into(), id("m-a1"), region("r-alpha"), 1, 1);
    assert_eq!(
        cluster.record_ack(retained),
        Err(DurabilityError::DuplicateAcknowledgement { member: id("m-a1") }),
        "the survivor's durable observation of the acknowledged write is still recorded"
    );

    // New local writes in the degraded Region are refused typed, again and
    // again across the whole window: the refusal names exactly the missing
    // seat, and the surviving Member's store alone is reported as internal
    // progress — never a weakened single-Member receipt.
    for operation in ["op-2", "op-3"] {
        let refusal = cluster
            .submit(operation, &id("m-a1"), DurabilityLevel::RegionalDurable)
            .expect_err("the degraded Region never weakens its quorum");
        let DurabilityFailure::Partial(observation) = &refusal else {
            panic!("{operation}: the refusal is the typed partial, got {refusal:?}")
        };
        assert_eq!(observation.missing_members(), &[id("m-a2")], "the lost seat is named");
        assert_eq!(observation.missing_regions(), &[region("r-alpha")], "the Region is named");
        assert_eq!(
            observation.stored().len(),
            1,
            "{operation}: one store is internal progress only, never a claim"
        );
        assert!(!refusal.is_durable(), "a failure variant never claims durability");
    }

    // The other selected Region accepts writes at its own full RF=2
    // boundary throughout the window — the state a router above this seam
    // routes the refused work to.
    let routed = cluster
        .submit("op-4", &id("m-b1"), DurabilityLevel::RegionalDurable)
        .expect("the healthy Region keeps accepting local writes");
    assert_eq!(seat_names(&routed), [id("m-b1"), id("m-b2")], "r-beta answers at both seats");

    // Member replacement: a NEW Member takes the failed seat through the
    // overlapping membership step (both fencing values advance, the old
    // quorum boundary is kept by overlap).
    cluster
        .advance_membership(replacement_membership())
        .expect("the overlapping replacement step accepts");

    // The partial write from the degraded window never silently completes
    // under the new epoch: its identity is fenced with the typed
    // stale-operation refusal, so the client carries that work as a NEW
    // operation identity.
    let fenced = cluster
        .submit("op-2", &id("m-a1"), DurabilityLevel::RegionalDurable)
        .expect_err("the pre-replacement partial is fenced by the epoch step");
    assert!(
        matches!(
            &fenced,
            DurabilityFailure::StaleOperation { operation, generation: 1, epoch: 1 }
                if operation == "op-2"
        ),
        "the fenced retry is typed: {fenced:?}"
    );

    // Local writes resume at the full RF=2 boundary with the replacement
    // seat contributing.
    let resumed = cluster
        .submit("op-5", &id("m-a1"), DurabilityLevel::RegionalDurable)
        .expect("the Region's local writes resume at RF=2");
    assert_eq!(
        seat_names(&resumed),
        [id("m-a1"), id("m-a3")],
        "the replacement seat contributes: RF=2 semantics hold again"
    );
    assert_eq!(resumed.generation(), 2, "the receipt is fenced by the new generation");
    assert_eq!(resumed.epoch(), 2, "the receipt is fenced by the new epoch");
}

// The degraded window blocks the global boundary as well — GlobalDurable
// is RegionalDurable in EVERY selected Region, so one Region below RF=2
// refuses it too — and the global level returns with RF=2 everywhere once
// the seat is replaced.
#[test]
fn the_degraded_window_blocks_global_durability_too_and_it_returns_after_replacement() {
    let mut cluster = DurabilityCoordinator::new(two_region_rf2_membership());
    cluster.set_member_active(&id("m-a2"), false).expect("the failed seat is a known member");

    let refusal = cluster
        .submit("op-g", &id("m-b1"), DurabilityLevel::GlobalDurable)
        .expect_err("one Region below RF=2 blocks the global boundary");
    let DurabilityFailure::Partial(observation) = &refusal else {
        panic!("the refusal is the typed partial, got {refusal:?}")
    };
    assert_eq!(
        observation.missing_regions(),
        &[region("r-alpha")],
        "the Region below RF=2 is named"
    );
    assert_eq!(observation.missing_members(), &[id("m-a2")], "the lost seat is named");
    assert!(!refusal.is_durable());

    cluster
        .advance_membership(replacement_membership())
        .expect("the overlapping replacement step accepts");
    let receipt = cluster
        .submit("op-g2", &id("m-b1"), DurabilityLevel::GlobalDurable)
        .expect("global durability returns with RF=2 in every selected Region");
    assert_eq!(receipt.level(), DurabilityLevel::GlobalDurable);
    assert_eq!(receipt.contributors().len(), 4, "every selected Region contributes both seats");
}
