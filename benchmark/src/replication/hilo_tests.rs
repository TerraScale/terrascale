//! The GAP-F09 HiLo distribution lane: the member-loss proofs that need
//! the replication doubles.
//!
//! These tests drive the identity-distribution contract of
//! `src/families/hilo/` (decision record
//! `docs/families/hilo-identity-distribution.md`) across real member
//! doubles: block allocations commit through the owning member's
//! family-write path (D-1 admission fencing), replication moves them
//! through the deterministic transport exactly as the catch-up data plane
//! does, and a member that dies holding an allocated block is replaced by
//! an epoch-raised ownership transition. Fixtures use temporary
//! directories and synthetic identities only, drive time explicitly
//! through transport pump calls (no threads, no async runtimes, no
//! sleeps), and create no Recovery, Replication-lane receipt beyond the
//! run logs, or Benchmark claim.

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use crate::engine::{DurableResolution, EngineConfig};
use crate::families::FamilyWriteRequest;
use crate::families::hilo::{BlockDurability, HiLoDistributor};
use crate::replication::{
    MemberId, ReplicationConfig, TopologyView,
    member::{MemberError, ReplicationMember, synchronize},
    transport::ReplicationNetwork,
};
use crate::types::{Deadline, OperationId, TenantId, TenantScope};

/// The data range identity records live under for this lane.
const HILO_RANGE: &str = "hilo";
const TENANT: &str = "fleet";

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-repl-hilo-{tag}-{}-{index}", std::process::id()))
}

/// Opens one replication member over a fresh durable Engine location.
fn open_member(tag: &str, id: &str, topology: &TopologyView) -> ReplicationMember {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let config = ReplicationConfig::try_new(topology.clone()).expect("valid replication config");
    ReplicationMember::try_new(
        config,
        MemberId::try_new(id).expect("valid member identifier"),
        EngineConfig::durable(&dir).expect("durable location"),
    )
    .expect("member opens")
}

/// Three Regions, one Member each; `owner` owns the HiLo range at `epoch`.
fn hilo_topology(owner: &str, epoch: u64) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_region("r-gamma")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "r-beta")
        .expect("valid member")
        .with_member("m-g1", "r-gamma", "r-gamma")
        .expect("valid member")
        .with_owner_at_epoch(HILO_RANGE, owner, epoch)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

fn scope(member: &ReplicationMember) -> TenantScope {
    TenantScope::from_parts(
        TenantId::try_new(TENANT).expect("valid tenant"),
        member.engine().expect("engine open").identity(),
    )
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid operation")
}

fn valid_deadline() -> Deadline {
    Deadline::after(Duration::from_secs(45)).expect("valid deadline")
}

/// Plans the distributor's next allocation and commits it through the
/// member's owner-fenced family-write path, then reports the commit.
fn allocate(
    member: &mut ReplicationMember,
    member_scope: &TenantScope,
    distributor: &mut HiLoDistributor,
    operation_name: &str,
) -> Result<(), MemberError> {
    let engine = member.engine().expect("engine open");
    let write = distributor
        .plan_allocation(engine, member_scope, operation(operation_name), valid_deadline())
        .expect("plans the allocation");
    member.commit_family_write(
        member_scope,
        HILO_RANGE,
        FamilyWriteRequest::identity_allocation(write),
    )?;
    distributor.allocation_committed().expect("the commit is recorded");
    Ok(())
}

/// Returns the distributor's most recently allocated block, whether it is
/// still pending or already acknowledged.
fn current_block(distributor: &HiLoDistributor) -> crate::families::hilo::HiLoBlock {
    distributor.pending_block().or(distributor.live_block()).expect("a block was just allocated")
}

/// Reports whether one member durably holds one operation — the fact the
/// acknowledgement mapping is derived from, never assumed.
fn holds_durably(
    member: &ReplicationMember,
    member_scope: &TenantScope,
    operation_name: &str,
) -> bool {
    member.engine().is_some_and(|engine| {
        matches!(
            engine.resolve_commit(member_scope, operation(operation_name)),
            Ok(DurableResolution::Committed(_))
        )
    })
}

/// Replicates one source's tenant history into one follower and asserts it
/// caught up.
fn catch_up(
    network: &mut ReplicationNetwork,
    source: &mut ReplicationMember,
    source_scope: &TenantScope,
    follower: &mut ReplicationMember,
    follower_scope: &TenantScope,
) {
    let report = synchronize(network, source, source_scope, follower, follower_scope, 4)
        .expect("catch-up runs");
    assert!(report.caught_up(), "the follower catches up from the source");
}

/// The worst window of decision D4: the owning member allocates its next
/// block, dies before that allocation reaches any other member, and is
/// replaced by an epoch-raised transition. The replacement's handed-out
/// ids must be disjoint from every id the dead owner allocated — the
/// in-flight block included.
#[test]
fn hilo_member_loss_never_rehands_the_dead_owners_allocated_blocks() {
    let mut network = ReplicationNetwork::new();
    let topology = hilo_topology("m-a1", 1);
    let mut m_a1 = open_member("loss", "m-a1", &topology);
    let mut m_b1 = open_member("loss", "m-b1", &topology);
    let mut m_g1 = open_member("loss", "m-g1", &topology);
    let scope_a1 = scope(&m_a1);
    let scope_b1 = scope(&m_b1);

    // The owner distributes: a transition burn, then its first handed-out
    // block, acknowledged only after a surviving member durably holds the
    // allocation (decision D3).
    let mut owner = HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");
    allocate(&mut m_a1, &scope_a1, &mut owner, "hilo-burn-a1").expect("the burn commits");
    allocate(&mut m_a1, &scope_a1, &mut owner, "hilo-block-a1").expect("the block commits");
    let first = current_block(&owner);
    catch_up(&mut network, &mut m_a1, &scope_a1, &mut m_b1, &scope_b1);
    assert!(
        holds_durably(&m_b1, &scope_b1, "hilo-block-a1"),
        "a surviving member durably holds the acknowledged allocation"
    );
    owner
        .acknowledged(BlockDurability::SurvivesOwnerLoss)
        .expect("the loss-surviving fact releases the block");
    // The owner hands out its whole block: under D2 the next allocation
    // exists only in the exhausted-to-refetch window, which is exactly
    // where the loss below strikes.
    let mut handed_by_owner = Vec::new();
    while let Some(id) = owner.next_id().expect("the live block mints") {
        handed_by_owner.push(id);
    }
    assert_eq!(handed_by_owner, (32..64).collect::<Vec<_>>(), "the whole block was handed out");

    // The owner allocates its NEXT block and dies before it reaches any
    // other member: the in-flight allocation nobody else received.
    allocate(&mut m_a1, &scope_a1, &mut owner, "hilo-block-a2")
        .expect("the in-flight block commits");
    let in_flight = current_block(&owner);
    assert_eq!(in_flight.low(), 64, "the in-flight block starts past the acknowledged one");
    m_a1.close();

    // The replacement appears: an epoch-raised view naming m-g1 owner, and
    // the control-plane precondition — the replacement catches up from a
    // surviving member before its first allocation.
    let promoted = hilo_topology("m-g1", 2);
    m_g1.apply_view(promoted.clone()).expect("the replacement adopts the view");
    m_b1.apply_view(promoted).expect("the survivor adopts the view");
    let scope_g1 = scope(&m_g1);
    catch_up(&mut network, &mut m_b1, &scope_b1, &mut m_g1, &scope_g1);

    // The replacement distributes. Its first allocation is its own
    // transition burn — which covers exactly the dead owner's in-flight
    // block — and only then does it hand out.
    let mut replacement =
        HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");
    allocate(&mut m_g1, &scope_g1, &mut replacement, "hilo-burn-g1").expect("the burn commits");
    // The first allocation of a fresh distributor is the transition burn:
    // none of its ids may be handed out. This is the assertion the dead
    // owner's in-flight block lives or dies by — a first allocation that
    // mints hands out exactly the range the dead owner allocated.
    let minted_from_burn = replacement.next_id().expect("minting is typed");
    assert!(
        minted_from_burn.is_none(),
        "the transition burn mints nothing: id {minted_from_burn:?} would re-hand the dead owner's in-flight block {in_flight:?}"
    );
    assert!(
        replacement.pending_block().is_none() && replacement.live_block().is_none(),
        "a burn is neither pending nor live"
    );
    assert_eq!(replacement.burned(), 32, "the replacement burned one block");
    allocate(&mut m_g1, &scope_g1, &mut replacement, "hilo-block-g1").expect("the block commits");
    let replacement_block = current_block(&replacement);
    assert_eq!(
        replacement_block.low(),
        in_flight.high(),
        "the replacement hands out strictly past the dead owner's in-flight block"
    );
    catch_up(&mut network, &mut m_g1, &scope_g1, &mut m_b1, &scope_b1);
    assert!(
        holds_durably(&m_b1, &scope_b1, "hilo-block-g1"),
        "a surviving member durably holds the replacement's allocation"
    );
    replacement
        .acknowledged(BlockDurability::SurvivesOwnerLoss)
        .expect("the loss-surviving fact releases the block");

    let handed_by_replacement: Vec<u64> = (0..5)
        .map(|_| replacement.next_id().expect("the live block mints").expect("ids remain"))
        .collect();
    for id in &handed_by_replacement {
        assert!(
            !first.contains(*id) && !in_flight.contains(*id),
            "the replacement's id {id} re-hands a range the dead owner allocated"
        );
    }
    assert_eq!(
        handed_by_replacement,
        vec![96, 97, 98, 99, 100],
        "the replacement's ids start above everything the dead owner allocated"
    );
    assert_eq!(replacement.burned(), 32, "the replacement burned one block");
}

/// Decision D1 on the lane: once a replacement appears at a raised epoch,
/// the deposed owner's next allocation is fenced by admission — the module
/// has no allocation path around the owner check — and the replacement's
/// hand-outs start past everything the deposed owner allocated.
#[test]
fn hilo_deposed_owner_cannot_allocate_after_the_replacement_appears() {
    let mut network = ReplicationNetwork::new();
    let topology = hilo_topology("m-a1", 1);
    let mut m_a1 = open_member("deposed", "m-a1", &topology);
    let mut m_g1 = open_member("deposed", "m-g1", &topology);
    let scope_a1 = scope(&m_a1);
    let scope_g1 = scope(&m_g1);

    let mut owner = HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");
    allocate(&mut m_a1, &scope_a1, &mut owner, "hilo-burn-a1").expect("the burn commits");
    allocate(&mut m_a1, &scope_a1, &mut owner, "hilo-block-a1").expect("the block commits");
    let deposed_block = current_block(&owner);
    catch_up(&mut network, &mut m_a1, &scope_a1, &mut m_g1, &scope_g1);
    assert!(
        holds_durably(&m_g1, &scope_g1, "hilo-block-a1"),
        "the future replacement durably holds the acknowledged allocation"
    );
    owner
        .acknowledged(BlockDurability::SurvivesOwnerLoss)
        .expect("the loss-surviving fact releases the block");
    while owner.next_id().expect("the live block mints").is_some() {}

    // The replacement appears at a raised epoch; the deposed owner adopts
    // the view and its next allocation is refused by admission fencing.
    let promoted = hilo_topology("m-g1", 2);
    m_a1.apply_view(promoted.clone()).expect("the deposed owner adopts the view");
    m_g1.apply_view(promoted).expect("the replacement adopts the view");
    let refused = allocate(&mut m_a1, &scope_a1, &mut owner, "hilo-block-a2");
    assert!(
        matches!(refused, Err(MemberError::NotOwner { .. })),
        "the deposed owner's allocation is fenced, got {refused:?}"
    );
    owner.allocation_abandoned();

    // The replacement catches up from the deposed owner's own history (it
    // is alive and serving), burns its transition block, and hands out
    // strictly past the deposed owner's allocations.
    catch_up(&mut network, &mut m_a1, &scope_a1, &mut m_g1, &scope_g1);
    let mut replacement =
        HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");
    allocate(&mut m_g1, &scope_g1, &mut replacement, "hilo-burn-g1").expect("the burn commits");
    // The replacement's first allocation is its transition burn: nothing
    // in it may be handed out, and it covers the fenced owner's possible
    // in-flight window exactly (decision D4).
    assert_eq!(
        replacement.next_id().expect("minting is typed"),
        None,
        "the transition burn mints nothing"
    );
    assert_eq!(replacement.burned(), 32, "the burn covers the fenced in-flight window");
    allocate(&mut m_g1, &scope_g1, &mut replacement, "hilo-block-g1").expect("the block commits");
    let block = current_block(&replacement);
    assert_eq!(
        block.low(),
        deposed_block.high() + 32,
        "the replacement's hand-outs start one burned block past the deposed owner's last allocation"
    );
}
