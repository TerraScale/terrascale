//! Public-construction tombstone-retention tests (GAP-R02).
//!
//! These tests use only public construction: the
//! `terrabase_v2::replication` watermark types, the public
//! `terrabase_v2::backup` seam, and `EngineConfig::durable`/`Engine::open`.
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization) and neither has `StorageSequence`, so the
//! data-bearing below-bound behavior — a member catching up across a
//! declared bound, a chain restoring without resurrecting, a smuggler
//! export carrying retained evidence — is proved by the in-crate suites at
//! `src/replication/retention_tests.rs`, `src/backup/tests.rs`, and
//! `src/backup/smuggler/tests.rs`. This file proves what an external caller
//! of the two seams can construct and observe: the watermark contract's
//! fail-closed surface and its composition with a backup plan.
//!
//! No claim here extends beyond observable behavior: formal Recovery
//! Receipts come exclusively from the exclusive recovery lane, and no
//! engine component gives up journal history today, so the declared bound
//! this file drives is the seam the storage compaction path (GAP-S1) will
//! declare in production.

use std::path::PathBuf;

use terrabase_v2::backup::{BackupKind, BackupPlan, RestorePlan, run_backup, verify_latest_chain};
use terrabase_v2::replication::{
    ConsumerObservation, RetentionConsumer, RetentionRefusal, SharedTombstoneRetention,
    TombstoneRetention,
};
use terrabase_v2::types::{StorageSequence, TenantId};
use terrabase_v2::{Engine, EngineConfig};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-gapr02-int-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

/// The zero sequence, the one a public caller can construct: every other
/// sequence a public surface carries arrives as a fact (a manifest's
/// boundary, a receipt's target), never as caller arithmetic.
fn zero() -> StorageSequence {
    StorageSequence::default()
}

#[test]
fn the_watermark_contract_is_public_and_fail_closed() {
    let mut watermark = TombstoneRetention::new(None);
    assert_eq!(watermark.retention_floor(), zero(), "everything is retained at first");
    assert_eq!(watermark.history_bound(), None, "no history has been given up");
    assert_eq!(watermark.floor_cap(), None);
    assert!(watermark.refusals().is_empty());

    // A consumer that has consumed nothing needs all of the evidence, and a
    // consumer floor never goes backwards.
    let consumer = RetentionConsumer::Migration {
        name: "logical-migration-1".to_owned(),
        tenant: tenant("alpha"),
    };
    assert_eq!(watermark.observe_consumer(consumer.clone(), None), ConsumerObservation::Recorded);
    assert_eq!(
        watermark.observe_consumer(consumer.clone(), Some(zero())),
        ConsumerObservation::Recorded
    );
    assert_eq!(
        watermark.observe_consumer(consumer.clone(), None),
        ConsumerObservation::Recorded,
        "no progress and the zero floor are the same position, not a regression"
    );
    assert_eq!(
        watermark.consumers(),
        vec![(consumer.clone(), Some(zero()))],
        "the recorded floor stands"
    );

    // History is not given up before the retained evidence is durable, and
    // the declaration discloses who sits below the bound. The journal head
    // the declaration carries is the zero sequence here — the one a public
    // caller can construct — and the bound sits at it, so the window (a
    // bound past the journal's durable head is refused, decision D2.4)
    // fully exists.
    let bound = zero();
    assert_eq!(
        watermark.declare_history_bound(bound, bound, false),
        Err(RetentionRefusal::EvidenceNotDurable { bound })
    );
    let declared = watermark.declare_history_bound(bound, bound, true).expect("declares cleanly");
    assert_eq!(declared.bound(), bound);
    assert_eq!(declared.held(), 0);
    assert_eq!(declared.below_floor_discarded(), 0);
    assert_eq!(
        declared.consumers_below_bound(),
        &[],
        "a consumer at the bound itself needs nothing below it; the below-bound disclosure is proved with real sequences in-crate"
    );

    // A shared handle is the same watermark: a transition through one handle
    // is observable through another.
    let shared = SharedTombstoneRetention::from_retention(watermark);
    assert_eq!(shared.with_lock(|state| state.retention_floor()), zero());
    shared.with_lock(|state| {
        state.observe_consumer(
            RetentionConsumer::BackupChain { identity: "rp-base".to_owned() },
            Some(bound),
        )
    });
    assert_eq!(shared.snapshot().consumers().len(), 2, "the snapshot carries both consumer floors");
    assert_eq!(
        shared.with_lock(|state| state.advance_floor(bound)).map(|advance| advance.floor()),
        Ok(zero())
    );

    // A floor cap bounds the watermark from below: with the cap at the
    // bound, nothing above it can ever be discarded.
    let mut capped = TombstoneRetention::new(Some(bound));
    assert_eq!(capped.floor_cap(), Some(bound));
    assert!(capped.advance_floor(bound).is_ok());
}

#[test]
fn a_backup_plan_carries_the_shared_watermark_on_the_public_seam() {
    let (_dir, engine) = open_engine("plan-retention");
    let destination = location("plan-retention-dest");
    std::fs::remove_dir_all(&destination).ok();
    let target = location("plan-retention-target");
    std::fs::remove_dir_all(&target).ok();

    let retention = SharedTombstoneRetention::new();
    let full =
        run_backup(&engine, BackupPlan::full().with_retention(retention.clone()), &destination)
            .expect("a full backup runs with the watermark on the plan");
    assert_eq!(full.manifest().kind(), BackupKind::Full);
    assert_eq!(full.manifest().documents(), 0);

    let chain = verify_latest_chain(&destination).expect("the chain verifies");
    assert_eq!(chain.len(), 1);

    let receipt = RestorePlan::restore_latest(&destination, &target).expect("restore runs");
    assert_eq!(receipt.restored_documents(), 0);
    assert_eq!(receipt.restore_commits(), 0);

    // A plan without an incremental base records no chain anchor, and the
    // watermark it carried still retains everything.
    assert!(retention.with_lock(|state| state.consumers()).is_empty());
    assert_eq!(retention.with_lock(|state| state.retention_floor()), zero());

    // The rendered receipt stays secret-safe: no private host path.
    let rendered = receipt.render();
    let target_text = target.to_string_lossy().into_owned();
    assert!(!rendered.contains(&target_text), "no path crosses the seam");
}
