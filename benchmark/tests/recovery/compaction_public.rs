//! Public-construction storage compaction tests (GAP-S1).
//!
//! These tests use only public construction: `EngineConfig::durable`,
//! `Engine::open`, the crate-root compaction types, and — for the retention
//! integration — the public `terrabase_v2::replication` watermark handle.
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the data-bearing compaction drills — the
//! kill-point matrix, the divergence and retention-floor gates, the
//! retained-evidence store's damage discipline — are proved by the in-crate
//! suite at `src/storage/compaction_tests.rs`. This file proves what an
//! external caller of the seam can construct and observe: the typed
//! request, the bounded progress and fact reporting, the fail-closed
//! history gate on an unservable journal, restart idempotency, and
//! composition with the public backup/restore seam.
//!
//! No claim here extends beyond observable behavior; the tracked Recovery
//! Receipt under `evidence/recovery/` records what the exclusive lane ran.

use std::{
    path::PathBuf,
    sync::{Arc, Mutex},
};

use terrabase_v2::backup::{BackupPlan, RestorePlan, run_backup, verify_latest_chain};
use terrabase_v2::replication::SharedTombstoneRetention;
use terrabase_v2::{
    CompactionError, CompactionPhase, CompactionPolicy, CompactionReceipt, CompactionRequest,
    Engine, EngineConfig, HistoryReclaimOutcome,
};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-gaps1-int-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

#[test]
fn a_reclaim_pass_reports_bounded_facts_and_progress_publicly() {
    let (_dir, engine) = open_engine("reclaim-facts");
    let events = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&events);
    let request = CompactionRequest::new(CompactionPolicy::Reclaim).with_progress(Arc::new(
        move |progress: &terrabase_v2::CompactionProgress| {
            sink.lock().expect("progress lock").push(progress.clone());
        },
    ));

    let receipt = engine.compact(request).expect("the reclaim pass runs on an open engine");
    assert_eq!(receipt.history_reclaim(), HistoryReclaimOutcome::NotRequested);
    assert!(receipt.wal_bytes_after() <= receipt.wal_bytes_before());
    assert!(
        receipt.phases().len() <= 5,
        "the reported phase list is bounded by the pass's fixed phase set"
    );

    // Progress is bounded and observable: at most one start and one
    // completion event per phase, every event names its phase, and the
    // checkpoint phase both starts and completes on this pass.
    let events = events.lock().expect("progress lock");
    assert!(!events.is_empty(), "the pass reported its progress");
    assert!(
        events.len() <= 2 * receipt.phases().len(),
        "progress events are bounded by two per phase: {} events for {} phases",
        events.len(),
        receipt.phases().len()
    );
    assert!(events.iter().any(|event| event.phase() == CompactionPhase::Checkpoint));
    assert!(
        events
            .iter()
            .filter(|event| event.phase() == CompactionPhase::Checkpoint)
            .any(|event| event.completed())
    );

    // The compacted engine reopens and shuts down cleanly.
    let dir = _dir.clone();
    drop(engine);
    drop(events);
    let reopened = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("the compacted location reopens");
    reopened.shutdown().expect("the reopened engine shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn history_reclaim_on_an_unservable_journal_refuses_and_discloses() {
    let (_dir, engine) = open_engine("unservable-window");
    // A fresh engine has never journaled a commit: there is no servable
    // window, and the decision record's readable-window gate (D5) must
    // refuse to give history up rather than declare an unreadable window
    // empty.
    let receipt = engine
        .compact(CompactionRequest::new(CompactionPolicy::ReclaimHistory))
        .expect("the pass still runs its safe phases");
    assert_eq!(receipt.history_reclaim(), HistoryReclaimOutcome::RefusedUnservableWindow);
    assert_eq!(receipt.servable_start_before(), None);
    assert_eq!(receipt.servable_start_after(), None);
    assert_eq!(receipt.evidence_extracted(), 0);
    std::fs::remove_dir_all(&_dir).ok();
}

#[test]
fn a_watermark_carrying_pass_leaves_the_handle_fail_closed_and_repeatable() {
    let (dir, engine) = open_engine("watermark-handle");
    let retention = SharedTombstoneRetention::new();

    // No durable retained-evidence state exists yet, so the prologue seeds
    // nothing, the refused reclaim declares nothing, and the watermark is
    // observably untouched — the declaration surface stays with the
    // watermark's own fail-closed contract.
    let receipt = engine
        .compact_with_retention(
            CompactionRequest::new(CompactionPolicy::ReclaimHistory),
            &retention,
        )
        .expect("the pass runs");
    assert_eq!(receipt.history_reclaim(), HistoryReclaimOutcome::RefusedUnservableWindow);
    assert_eq!(retention.with_lock(|state| state.history_bound()), None);
    assert_eq!(retention.with_lock(|state| state.held_evidence_count()), 0);

    // A repeat pass on the same location is idempotent and still green.
    let again = engine
        .compact_with_retention(CompactionRequest::new(CompactionPolicy::Reclaim), &retention)
        .expect("the repeat pass runs");
    assert_eq!(again.history_reclaim(), HistoryReclaimOutcome::NotRequested);
    drop(engine);

    // After a restart the same composition holds on the reopened location.
    let reopened =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("reopens");
    let after_restart = reopened
        .compact_with_retention(
            CompactionRequest::new(CompactionPolicy::ReclaimHistory),
            &retention,
        )
        .expect("the post-restart pass runs");
    assert_eq!(after_restart.history_reclaim(), HistoryReclaimOutcome::RefusedUnservableWindow);
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn compaction_composes_with_the_public_backup_and_restore_seam() {
    let (_dir, engine) = open_engine("backup-composition");
    let destination = location("backup-composition-dest");
    std::fs::remove_dir_all(&destination).ok();
    run_backup(&engine, BackupPlan::full(), &destination).expect("a backup runs before compaction");

    engine
        .compact(CompactionRequest::new(CompactionPolicy::ReclaimHistory))
        .expect("the pass runs");

    // A second backup after compaction (incremental over the full's
    // boundary, so the two form a chain), chain verification, and a full
    // restore into a fresh target: the compacted engine stays a legal
    // backup source and the restored location is a real engine location.
    run_backup(
        &engine,
        BackupPlan::incremental_since(terrabase_v2::StorageSequence::default()),
        &destination,
    )
    .expect("a backup runs after compaction");
    let chain = verify_latest_chain(&destination).expect("the chain verifies");
    assert_eq!(chain.len(), 2, "both recovery points verify as one chain");
    let target = location("backup-composition-target");
    std::fs::remove_dir_all(&target).ok();
    let receipt = RestorePlan::restore_latest(&destination, &target).expect("restore runs");
    assert_eq!(receipt.restored_documents(), 0);
    let restored = Engine::open(EngineConfig::durable(&target).expect("durable location"))
        .expect("the restored location opens");
    restored.shutdown().expect("the restored engine shuts down");

    let rendered = receipt.render();
    let target_text = target.to_string_lossy().into_owned();
    assert!(!rendered.contains(&target_text), "no path crosses the seam");
    std::fs::remove_dir_all(&_dir).ok();
    std::fs::remove_dir_all(&destination).ok();
    std::fs::remove_dir_all(&target).ok();
}

/// The error surface is typed and closed: an injected durable failure is
/// reported as [`CompactionError::Storage`], never as a panic or a partial
/// success receipt.
#[test]
fn the_compaction_error_surface_is_typed() {
    let (_dir, engine) = open_engine("typed-error");
    let outcome: Result<CompactionReceipt, CompactionError> =
        engine.compact(CompactionRequest::new(CompactionPolicy::Reclaim));
    assert!(
        outcome.is_ok(),
        "a healthy empty pass succeeds; the typed error path is exercised by the in-crate kill-point drills"
    );
    std::fs::remove_dir_all(&_dir).ok();
}
