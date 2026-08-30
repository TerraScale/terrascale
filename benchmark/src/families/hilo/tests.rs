//! In-crate `HiLo` identity-distribution tests for the GAP-F09 packet.
//!
//! Each test reads as one distribution scenario over the engine's identity
//! family: a distributor plans an allocation, the caller commits it through
//! the family seam, the block becomes mintable only at the discipline's
//! acknowledgement step, and the identity record's `next` is the single
//! accounting truth. The V1-observable semantics these tests pin (block
//! bounds, burn-on-transition, exhaustion refusal, no re-handing) carry
//! their pinned-commit citations in
//! `docs/families/hilo-identity-distribution.md`; the tests themselves make
//! no V1 parity claim beyond those pinned behaviors. The member-loss proofs
//! that need the replication doubles live on the replication lane
//! (`src/replication/hilo_tests.rs`), the same split GAP-F07 used.

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::{BlockDurability, HiLoDistributor, HiLoError, MAX_BLOCK_SIZE, MIN_BLOCK_SIZE};
use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::families::{
    FamilyError, FamilyReadContext, FamilyReadRequest, FamilyReadResult, FamilyWriteRequest,
    IdentityCommand, IdentityRead, IdentityWrite,
};
use crate::types::{Cancellation, LocalDurability, OperationId, TenantId, TenantScope};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-families-hilo-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> Engine {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

fn scope(engine: &Engine) -> TenantScope {
    TenantScope::from_parts(
        TenantId::try_new("tenant-hilo").expect("valid tenant"),
        engine.identity(),
    )
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid operation")
}

fn valid_deadline() -> crate::types::Deadline {
    crate::types::Deadline::after(Duration::from_secs(45)).expect("valid deadline")
}

/// Reads one identity name's `next` through the family seam.
fn identity_next(engine: &Engine, scope: &TenantScope, name: &str) -> u64 {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let read = IdentityRead::try_new(vec![name.to_owned()], valid_deadline(), Cancellation::new())
        .expect("valid identity read");
    let result = engine
        .families()
        .read(
            FamilyReadContext::try_new(scope.clone(), snapshot).expect("valid context"),
            FamilyReadRequest::identity_range(read),
        )
        .expect("identity read");
    let FamilyReadResult::IdentityRange(range) = result else {
        panic!("expected an identity range, got {result:?}");
    };
    range
        .entries()
        .iter()
        .find(|entry| entry.name() == name)
        .expect("the requested name is answered")
        .next()
}

/// Commits one identity write through the engine family seam, the same path
/// an owning member's family-write commit takes.
fn commit_identity(
    engine: &Engine,
    scope: &TenantScope,
    write: IdentityWrite,
) -> Result<(), FamilyError> {
    let operation = write.operation_id().clone();
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let mut transaction = engine.begin(scope, snapshot, operation).expect("transaction begins");
    engine.families().stage(&mut transaction, FamilyWriteRequest::identity_allocation(write))?;
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => Ok(()),
        CommitOutcome::Rejected(reason) => panic!("the identity write commits: {reason:?}"),
        CommitOutcome::Unknown(_) => panic!("a synced commit never resolves unknown here"),
    }
}

fn seed_write(scope: &TenantScope, name: &str, value: u64, operation_name: &str) -> IdentityWrite {
    IdentityWrite::try_new(
        scope.clone(),
        name.to_owned(),
        IdentityCommand::Seed { name: name.to_owned(), value, force: true },
        operation(operation_name),
        valid_deadline(),
        Cancellation::new(),
    )
    .expect("valid seed write")
}

/// Plans and commits the distributor's next allocation through the engine
/// family seam, then reports the commit — the owner-side step the host or
/// lane drives through the member's family-write path.
fn allocate(
    engine: &Engine,
    scope: &TenantScope,
    distributor: &mut HiLoDistributor,
    operation_name: &str,
) {
    let write = distributor
        .plan_allocation(engine, scope, operation(operation_name), valid_deadline())
        .expect("plans the allocation");
    commit_identity(engine, scope, write).expect("the allocation commits");
    distributor.allocation_committed().expect("the commit is recorded");
}

#[test]
fn block_size_bounds_match_v1_floor_and_ceiling() {
    // V1's server never returns a capacity below 32 or above 1 Mi
    // (CalculateCapacity, HiLoHandlerProcessorForGetNextHiLo.cs:68-86);
    // the distributor refuses sizes outside those bounds.
    assert_eq!(MIN_BLOCK_SIZE, 32, "the floor is V1's floor");
    assert_eq!(MAX_BLOCK_SIZE, 1024 * 1024, "the ceiling is V1's ceiling");

    assert!(matches!(
        HiLoDistributor::try_new("orders".to_owned(), MIN_BLOCK_SIZE - 1),
        Err(HiLoError::BlockSizeInvalid)
    ));
    assert!(matches!(
        HiLoDistributor::try_new("orders".to_owned(), MAX_BLOCK_SIZE + 1),
        Err(HiLoError::BlockSizeInvalid)
    ));
    assert!(HiLoDistributor::try_new("orders".to_owned(), MIN_BLOCK_SIZE).is_ok());
    assert!(HiLoDistributor::try_new("orders".to_owned(), MAX_BLOCK_SIZE).is_ok());

    // The name is validated by the identity family's rules: empty or
    // padded names never reach an allocation.
    assert!(matches!(
        HiLoDistributor::try_new(" ".to_owned(), MIN_BLOCK_SIZE),
        Err(HiLoError::NameInvalid)
    ));
    assert!(matches!(
        HiLoDistributor::try_new(String::new(), MIN_BLOCK_SIZE),
        Err(HiLoError::NameInvalid)
    ));
}

#[test]
fn first_allocation_of_a_fresh_distributor_is_a_transition_burn() {
    let engine = open_engine("burn-first");
    let scope = scope(&engine);
    let mut distributor =
        HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");

    // The first allocation of a fresh distributor is a burn: committed,
    // discarded, and never handed out (decision D4).
    allocate(&engine, &scope, &mut distributor, "hilo-burn-1");
    assert_eq!(distributor.burned(), 32, "one full block is burned");
    assert!(distributor.live_block().is_none(), "a burn hands nothing out");
    assert_eq!(
        identity_next(&engine, &scope, "orders"),
        32,
        "the burned range is allocated in the identity record"
    );
    assert_eq!(
        distributor.next_id().expect("no live block"),
        None,
        "nothing is mintable from a burn"
    );

    // The second allocation is the first hand-out block, disjoint from the
    // burn by construction.
    allocate(&engine, &scope, &mut distributor, "hilo-block-1");
    let block = distributor.pending_block().expect("the block is pending");
    assert_eq!(block.low(), 32, "the block starts above the burn");
    assert_eq!(block.high(), 64, "the block spans the configured size");
    distributor
        .acknowledged(BlockDurability::SurvivesOwnerLoss)
        .expect("acknowledgement hands the block out");
    assert_eq!(
        distributor.next_id().expect("the block mints"),
        Some(32),
        "the first handed-out id starts above the burned range"
    );
}

#[test]
fn blocks_mint_only_after_loss_surviving_acknowledgement() {
    let engine = open_engine("ack-gate");
    let scope = scope(&engine);
    let mut distributor =
        HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");

    allocate(&engine, &scope, &mut distributor, "hilo-burn-1");
    allocate(&engine, &scope, &mut distributor, "hilo-block-1");

    // Decision D3: a pending block does not mint, and an acknowledgement
    // that does not survive the allocator's loss does not release it.
    assert!(
        matches!(distributor.next_id(), Err(HiLoError::BlockNotDurable)),
        "minting before acknowledgement is refused"
    );
    assert!(
        matches!(
            distributor.acknowledged(BlockDurability::OwnerLocal),
            Err(HiLoError::BlockNotDurable)
        ),
        "an owner-local acknowledgement never releases a block"
    );
    assert!(
        matches!(
            distributor.plan_allocation(
                &engine,
                &scope,
                operation("hilo-block-2"),
                valid_deadline()
            ),
            Err(HiLoError::AllocationOutstanding)
        ),
        "a second allocation cannot start while one is outstanding (D2)"
    );

    distributor
        .acknowledged(BlockDurability::SurvivesOwnerLoss)
        .expect("a loss-surviving acknowledgement releases the block");
    assert!(distributor.pending_block().is_none(), "the acknowledged block is no longer pending");

    // A live block must be exhausted before the next allocation: V1 mints
    // from the local range before fetching the next one.
    assert_eq!(
        distributor.next_id().expect("the live block mints"),
        Some(32),
        "the block mints in order"
    );
    assert!(
        matches!(
            distributor.plan_allocation(
                &engine,
                &scope,
                operation("hilo-block-2"),
                valid_deadline()
            ),
            Err(HiLoError::BlockNotExhausted)
        ),
        "planning past an unexhausted live block is refused"
    );
    let mut minted = vec![32];
    while let Some(id) = distributor.next_id().expect("the live block mints") {
        minted.push(id);
    }
    assert_eq!(minted, (32..64).collect::<Vec<_>>(), "the block mints its whole range in order");
    assert!(
        distributor
            .plan_allocation(&engine, &scope, operation("hilo-block-2"), valid_deadline())
            .is_ok(),
        "an exhausted live block plans its successor"
    );
}

#[test]
fn planned_allocation_abandoned_clears_the_outstanding_step() {
    let engine = open_engine("abandon");
    let scope = scope(&engine);
    let mut distributor =
        HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");

    // A burn plan that is abandoned (the commit was refused or never ran)
    // leaves the distributor fresh: its next allocation is still the burn.
    let _ = distributor
        .plan_allocation(&engine, &scope, operation("hilo-burn-refused"), valid_deadline())
        .expect("plans the burn");
    distributor.allocation_abandoned();
    allocate(&engine, &scope, &mut distributor, "hilo-burn-1");
    assert_eq!(distributor.burned(), 32, "the abandoned plan burned nothing");

    // A block plan that is abandoned clears the outstanding step without
    // consuming the identity.
    let _ = distributor
        .plan_allocation(&engine, &scope, operation("hilo-block-refused"), valid_deadline())
        .expect("plans the block");
    distributor.allocation_abandoned();
    assert_eq!(identity_next(&engine, &scope, "orders"), 32, "an abandoned plan allocates nothing");
    allocate(&engine, &scope, &mut distributor, "hilo-block-1");
    let block = distributor.pending_block().expect("the block is pending");
    assert_eq!(block.low(), 32, "the real block starts where the record stands");
}

#[test]
fn allocation_past_the_identity_ceiling_is_refused_not_wrapped() {
    let engine = open_engine("exhausted");
    let scope = scope(&engine);
    let mut distributor =
        HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");

    // Ten ids of headroom: no burn of 32 fits, so planning is refused with
    // the typed exhaustion error — never a wrapped or clamped block.
    commit_identity(
        &engine,
        &scope,
        seed_write(&scope, "orders", super::IDENTITY_CEILING - 10, "hilo-seed-near-ceiling"),
    )
    .expect("seeds near the ceiling");
    assert!(
        matches!(
            distributor.plan_allocation(
                &engine,
                &scope,
                operation("hilo-burn-1"),
                valid_deadline()
            ),
            Err(HiLoError::Exhausted)
        ),
        "a burn past the ceiling is refused at planning time"
    );
    assert_eq!(
        identity_next(&engine, &scope, "orders"),
        super::IDENTITY_CEILING - 10,
        "the refused plan moved nothing"
    );

    // The same refusal exists at the stage: the family seam's typed guard.
    let write = IdentityWrite::try_new(
        scope.clone(),
        "orders".to_owned(),
        IdentityCommand::Allocate { name: "orders".to_owned(), count: 32 },
        operation("hilo-direct-overrun"),
        valid_deadline(),
        Cancellation::new(),
    )
    .expect("valid allocation write");
    assert!(
        matches!(commit_identity(&engine, &scope, write), Err(FamilyError::IdentityExhausted)),
        "the identity stage refuses past-ceiling allocation"
    );
}

#[test]
fn the_last_fitting_block_ends_at_the_ceiling_and_the_next_is_refused() {
    let engine = open_engine("last-block");
    let scope = scope(&engine);
    let mut distributor =
        HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");

    allocate(&engine, &scope, &mut distributor, "hilo-burn-1");
    // Move the record so the next block ends exactly at the ceiling:
    // [MAX-32, MAX) fits; one id lower would not.
    commit_identity(
        &engine,
        &scope,
        seed_write(&scope, "orders", super::IDENTITY_CEILING - 32, "hilo-seed-boundary"),
    )
    .expect("seeds the boundary");
    allocate(&engine, &scope, &mut distributor, "hilo-block-1");
    let block = distributor.pending_block().expect("the boundary block is pending");
    assert_eq!(block.high(), super::IDENTITY_CEILING, "the block ends at the ceiling");
    distributor
        .acknowledged(BlockDurability::SurvivesOwnerLoss)
        .expect("the boundary block is acknowledged");
    let mut minted = Vec::new();
    while let Some(id) = distributor.next_id().expect("the boundary block mints") {
        minted.push(id);
    }
    assert_eq!(
        minted.last().copied(),
        Some(super::IDENTITY_CEILING - 1),
        "the last mintable id is one below the ceiling"
    );

    // One id less of headroom and the next block no longer fits.
    commit_identity(
        &engine,
        &scope,
        seed_write(&scope, "orders", super::IDENTITY_CEILING - 31, "hilo-seed-past-boundary"),
    )
    .expect("seeds past the boundary");
    assert!(
        matches!(
            distributor.plan_allocation(
                &engine,
                &scope,
                operation("hilo-block-2"),
                valid_deadline()
            ),
            Err(HiLoError::Exhausted)
        ),
        "a block that would cross the ceiling is refused"
    );
}

#[test]
fn unused_tails_of_abandoned_blocks_are_burned_not_rehanded() {
    let engine = open_engine("abandoned-tail");
    let scope = scope(&engine);

    // One distributor hands out five ids of its block and is dropped: the
    // unused tail [37, 64) is abandoned with it (decision D5 — there is no
    // return path).
    let mut first = HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");
    allocate(&engine, &scope, &mut first, "hilo-burn-1");
    allocate(&engine, &scope, &mut first, "hilo-block-1");
    first.acknowledged(BlockDurability::SurvivesOwnerLoss).expect("the block is acknowledged");
    let handed: Vec<u64> =
        (0..5).map(|_| first.next_id().expect("the block mints").expect("ids remain")).collect();
    assert_eq!(handed, vec![32, 33, 34, 35, 36], "five ids were handed out");
    drop(first);

    // The successor distributor burns its own transition block and hands
    // out strictly past everything the first one ever allocated — the
    // unused tail included.
    let mut second = HiLoDistributor::try_new("orders".to_owned(), 32).expect("valid distributor");
    allocate(&engine, &scope, &mut second, "hilo-burn-2");
    assert_eq!(second.burned(), 32, "the successor burned one block");
    allocate(&engine, &scope, &mut second, "hilo-block-2");
    second
        .acknowledged(BlockDurability::SurvivesOwnerLoss)
        .expect("the successor block is acknowledged");
    let block = second.live_block().expect("the successor block is live");
    assert_eq!(block.low(), 96, "the successor starts past the abandoned tail");
    assert!(
        !block.contains(36) && !block.contains(63),
        "no abandoned id is re-handed, used or unused"
    );
    assert_eq!(
        identity_next(&engine, &scope, "orders"),
        128,
        "the record accounts for burn, block, and successor"
    );
}
