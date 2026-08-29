//! Qualification cases for the deterministic anti-entropy kernel.

use terrabase_causal::{DatabaseId, PartitionId, TenantId};
use terrabase_replication_core::ReplicationProgress;
use terrabase_replication_core::anti_entropy::{
    AntiEntropyTrigger, FairRepairQueue, KeyRange, MerkleEntry, MerkleTree, QueueKey, RepairError,
    RepairWork, TriggerState, VerifiedFrontier, VerifiedStableFrontier,
};

fn entry(key: &'static [u8], causal: &'static [u8], materialized: &'static [u8]) -> MerkleEntry {
    MerkleEntry::new(key, causal, materialized)
}

fn key(id: u8) -> QueueKey {
    QueueKey::new(
        TenantId::from_bytes([id; 16]),
        DatabaseId::from_bytes([id; 16]),
        PartitionId::new(u64::from(id)),
        format!("region-{id}"),
    )
}

#[test]
fn equal_mutation_set_different_order_has_equal_roots() {
    let left = MerkleTree::build_bounded(
        [entry(b"b", b"dot-2", b"two"), entry(b"a", b"dot-1", b"one")],
        1,
        8,
    )
    .unwrap();
    let right = MerkleTree::build_bounded(
        [entry(b"a", b"dot-1", b"one"), entry(b"b", b"dot-2", b"two")],
        1,
        8,
    )
    .unwrap();
    assert_eq!(left.roots(), right.roots());
    assert!(left.mismatches(&right).is_empty());
    let differently_bounded = MerkleTree::build_bounded(
        [entry(b"a", b"dot-1", b"one"), entry(b"b", b"dot-2", b"two")],
        2,
        8,
    )
    .unwrap();
    assert_eq!(left.roots(), differently_bounded.roots());
}

#[test]
fn causal_only_and_materialized_only_mismatches_are_independent() {
    let base = MerkleTree::build([entry(b"a", b"dot-1", b"value")]).unwrap();
    let causal = MerkleTree::build([entry(b"a", b"dot-2", b"value")]).unwrap();
    let materialized = MerkleTree::build([entry(b"a", b"dot-1", b"changed")]).unwrap();
    let causal_mismatch = base.mismatches(&causal);
    assert!(causal_mismatch[0].causal);
    assert!(!causal_mismatch[0].materialized);
    let materialized_mismatch = base.mismatches(&materialized);
    assert!(!materialized_mismatch[0].causal);
    assert!(materialized_mismatch[0].materialized);
}

#[test]
fn corrupt_chunk_does_not_mutate_target() {
    let target = MerkleTree::build([entry(b"a", b"old", b"old")]).unwrap();
    let source = MerkleTree::build([entry(b"a", b"new", b"new")]).unwrap();
    let plan = target.plan_repair(&source, 1).unwrap();
    let mut chunks = plan.source_chunks(&source).unwrap();
    chunks[0].entries[0].causal[0] ^= 0xff;
    let mut target_after = target.clone();
    assert_eq!(target_after.apply_verified_chunks(&plan, &chunks), Err(RepairError::ChunkCorrupt));
    assert_eq!(target_after, target);
}

#[test]
fn bounded_repair_and_range_application_verify_both_roots() {
    let target = MerkleTree::build_bounded(
        [entry(b"a", b"a", b"old"), entry(b"b", b"b", b"old"), entry(b"c", b"c", b"old")],
        1,
        8,
    )
    .unwrap();
    let source = MerkleTree::build_bounded(
        [entry(b"a", b"a", b"new"), entry(b"b", b"b", b"old"), entry(b"c", b"c", b"new")],
        1,
        8,
    )
    .unwrap();
    let plan = target.plan_repair(&source, 2).unwrap();
    assert_eq!(plan.ranges.len(), 2);
    let chunks = plan.source_chunks(&source).unwrap();
    let mut repaired = target;
    repaired.apply_verified_chunks(&plan, &chunks).unwrap();
    assert_eq!(repaired.roots(), source.roots());
}

#[test]
fn queue_is_bounded_and_fair_across_keys() {
    let mut queue = FairRepairQueue::new(3).unwrap();
    let first = key(1);
    let second = key(2);
    let work = |key, n: u8| RepairWork {
        key,
        trigger: AntiEntropyTrigger::ChecksumMismatch,
        bytes: usize::from(n),
        range: Some(KeyRange::new(Some(vec![n]), Some(vec![n])).unwrap()),
    };
    queue.push(work(first.clone(), 1)).unwrap();
    queue.push(work(first, 2)).unwrap();
    queue.push(work(second.clone(), 3)).unwrap();
    assert_eq!(queue.push(work(second, 4)), Err(RepairError::QueueFull));
    assert_eq!(queue.pop().unwrap().key.region, "region-1");
    assert_eq!(queue.pop().unwrap().key.region, "region-2");
}

#[test]
fn reconnect_recovery_membership_and_checksum_triggers_coalesce() {
    let mut state = TriggerState::new(10, 100).unwrap();
    for trigger in [
        AntiEntropyTrigger::Reconnect,
        AntiEntropyTrigger::Recovery,
        AntiEntropyTrigger::MembershipChanged,
        AntiEntropyTrigger::ChecksumMismatch,
        AntiEntropyTrigger::SnapshotInstalled,
    ] {
        state.trigger(trigger);
    }
    state.poll(109);
    assert!(!state.drain().contains(&AntiEntropyTrigger::Periodic));
    state.poll(110);
    assert!(state.drain().contains(&AntiEntropyTrigger::Periodic));
}

#[test]
fn stable_frontier_waits_for_every_verified_member_and_contiguous_prefix() {
    let roots = MerkleTree::build([entry(b"a", b"dot", b"value")]).unwrap().roots();
    let mut stable = VerifiedStableFrontier::new([1, 2]);
    stable
        .acknowledge(
            VerifiedFrontier {
                member: 1,
                generation: 1,
                through: 10,
                causal: roots.causal,
                materialized: roots.materialized,
            },
            roots,
        )
        .unwrap();
    assert_eq!(stable.advance(), 0);
    stable
        .acknowledge(
            VerifiedFrontier {
                member: 2,
                generation: 1,
                through: 7,
                causal: roots.causal,
                materialized: roots.materialized,
            },
            roots,
        )
        .unwrap();
    assert_eq!(stable.advance(), 7);
    assert!(stable.can_collect(7));
    assert!(!stable.can_collect(8));
}

#[test]
fn progress_repair_never_infers_source_gaps() {
    let mut receiver = ReplicationProgress::default();
    receiver.record(1).unwrap();
    let mut source = ReplicationProgress::default();
    source.record(1).unwrap();
    source.record(3).unwrap();
    let plan =
        terrabase_replication_core::anti_entropy::plan_progress_repair(&receiver, &source, 4)
            .unwrap();
    assert_eq!(plan.ranges.len(), 1);
    assert_eq!(plan.ranges[0].first(), 3);
    receiver.record(2).unwrap();
    let plan =
        terrabase_replication_core::anti_entropy::plan_progress_repair(&receiver, &source, 4)
            .unwrap();
    assert_eq!(plan.ranges.len(), 1);
    assert_eq!(plan.ranges[0].first(), 3);
}
