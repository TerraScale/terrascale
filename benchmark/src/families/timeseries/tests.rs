//! In-crate GAP-F04 tests: time-series execution (rollups, retention,
//! paging).
//!
//! Each test drives the engine's family seams exactly as a host-driven
//! maintenance tick would: series state is established through committed
//! family writes, a run is one module step (`rollup_series`,
//! `enforce_retention`, or `run_series` with the tombstone watermark
//! handle), and the observable outcomes are the public read seam (pages,
//! cursors) and the typed receipts. The V1-observable semantics these tests
//! pin carry their pinned-commit citations in
//! `docs/families/gapf04-timeseries-execution.md`; the tests themselves make
//! no parity claim beyond those pinned behaviors.

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::{enforce_retention, rollup_series, run_series, run_tenant};
use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::families::payloads::{
    MAX_TIMESERIES_PAGE_LIMIT, RollupPolicy, TimeSeriesAppend, TimeSeriesBatchWrite,
    TimeSeriesDelete, TimeSeriesEntry, TimeSeriesPage, TimeSeriesPaging, TimeSeriesPolicy,
    TimeSeriesRead,
};
use crate::families::{
    Aggregation, FamilyError, FamilyReadContext, FamilyReadRequest, FamilyReadResult,
    FamilyWriteRequest,
};
use crate::operations::RequestValidationError;
use crate::replication::{DeletionEvidence, ReplicationClockStamp, SharedTombstoneRetention};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, StorageSequence, TenantId, TenantScope,
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-gapf04-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> Engine {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

fn scope(engine: &Engine) -> TenantScope {
    TenantScope::from_parts(
        TenantId::try_new("tenant-gapf04").expect("valid tenant"),
        engine.identity(),
    )
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn collection() -> CollectionName {
    CollectionName::try_new("users").expect("valid collection")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid operation")
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(45)).expect("valid deadline")
}

/// Widens one small fixture timestamp to its series value.
#[allow(clippy::cast_precision_loss, reason = "fixtures convert small integer timestamps")]
fn value(timestamp: i64) -> f64 {
    timestamp as f64
}

/// Puts one user document so the series' owner exists.
fn put_user_document(engine: &Engine, scope: &TenantScope, name: &str) {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let identity = operation(&format!("gapf04-put-user-{name}"));
    let mut transaction = engine.begin(scope, snapshot, identity).expect("begins");
    transaction
        .put(
            Document::new(
                document_id(name),
                collection(),
                DocumentValue::Object(
                    [("kind".to_owned(), DocumentValue::String("owner".to_owned()))]
                        .into_iter()
                        .collect(),
                ),
            ),
            ExpectedVersion::Any,
        )
        .expect("stages the owner document");
    assert!(
        matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)),
        "owner document commits"
    );
}

/// Commits one time-series batch through the family write seam.
#[allow(
    clippy::too_many_arguments,
    reason = "one frozen fixture helper: every parameter is a distinct series-write fact"
)]
fn commit_series_write(
    engine: &Engine,
    scope: &TenantScope,
    operation_name: &str,
    document: &str,
    name: &str,
    appends: Vec<(i64, Vec<f64>)>,
    deletes: Vec<TimeSeriesDelete>,
    policy: Option<TimeSeriesPolicy>,
) {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let identity = operation(operation_name);
    let mut transaction = engine.begin(scope, snapshot, identity.clone()).expect("begins");
    engine
        .families()
        .stage(
            &mut transaction,
            FamilyWriteRequest::time_series_batch(
                TimeSeriesBatchWrite::try_new(
                    scope.clone(),
                    document_id(document),
                    name.to_owned(),
                    appends
                        .into_iter()
                        .map(|(timestamp, values)| TimeSeriesAppend::new(timestamp, values, None))
                        .collect(),
                    deletes,
                    policy,
                    ExpectedVersion::Any,
                    identity,
                    deadline(),
                    Cancellation::new(),
                )
                .expect("valid time-series batch"),
            ),
        )
        .expect("stages the series batch");
    assert!(
        matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)),
        "series batch commits"
    );
}

/// Builds one series policy with named rollups.
fn policy(retention_ms: Option<i64>, rollups: &[(&str, Aggregation, i64)]) -> TimeSeriesPolicy {
    TimeSeriesPolicy::try_new(
        retention_ms,
        rollups
            .iter()
            .map(|(name, aggregation, window)| {
                RollupPolicy::new((*name).to_owned(), *aggregation, *window)
            })
            .collect(),
    )
    .expect("valid series policy")
}

/// Reads one series (raw or rollup, optionally paged) through the public
/// family read seam.
fn read_page(
    engine: &Engine,
    scope: &TenantScope,
    document: &str,
    name: &str,
    rollup: Option<&str>,
    paging: Option<TimeSeriesPaging>,
) -> Result<TimeSeriesPage, FamilyError> {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let mut read = TimeSeriesRead::try_new(
        document_id(document),
        name.to_owned(),
        None,
        deadline(),
        Cancellation::new(),
    )
    .expect("valid time-series read");
    if let Some(rollup) = rollup {
        read = read.reading_rollup(rollup).expect("valid rollup selector");
    }
    if let Some(paging) = paging {
        read = read.with_paging(paging);
    }
    let result = engine.families().read(
        FamilyReadContext::try_new(scope.clone(), snapshot).expect("valid context"),
        FamilyReadRequest::time_series_range(read),
    )?;
    match result {
        FamilyReadResult::TimeSeriesPage(page) => Ok(page),
        other => panic!("expected a time-series page, got {other:?}"),
    }
}

/// Reads the raw series' timestamps (the whole retained view).
fn raw_timestamps(engine: &Engine, scope: &TenantScope, document: &str, name: &str) -> Vec<i64> {
    read_page(engine, scope, document, name, None, None)
        .expect("raw read")
        .entries()
        .iter()
        .map(TimeSeriesEntry::timestamp)
        .collect()
}

/// Reads one rollup series' (timestamp, values) pairs.
fn rollup_points(
    engine: &Engine,
    scope: &TenantScope,
    document: &str,
    name: &str,
    rollup: &str,
) -> Vec<(i64, Vec<f64>)> {
    read_page(engine, scope, document, name, Some(rollup), None)
        .expect("rollup read")
        .entries()
        .iter()
        .map(|entry| (entry.timestamp(), entry.values().to_vec()))
        .collect()
}

/// One series with points every unit across `[0, 12)` and a 5ms window
/// rollup, owned by `doc-1`.
fn series_with_rollup(engine: &Engine, scope: &TenantScope, aggregation: Aggregation) {
    put_user_document(engine, scope, "doc-1");
    commit_series_write(
        engine,
        scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        (0..12).map(|timestamp| (timestamp, vec![value(timestamp)])).collect(),
        vec![],
        Some(policy(Some(100), &[("five", aggregation, 5)])),
    );
}

/// One deletion-evidence fact for one owning document, held by a watermark.
fn evidence_for(scope: &TenantScope, document: &str, sequence: u64) -> DeletionEvidence {
    DeletionEvidence::new(
        scope.tenant().clone(),
        Some(CollectionName::try_new("users").expect("valid collection")),
        document_id(document),
        3,
        StorageSequence::new(sequence),
        operation("gapf04-delete-evidence"),
        ReplicationClockStamp::new(1, 0),
        LocalDurability::Synced,
    )
}

// ---------------------------------------------------------------------------
// Rollups: windowing
// ---------------------------------------------------------------------------

#[test]
fn rollup_windows_align_to_the_policy_window_and_stop_at_now() {
    let engine = open_engine("rollup-windowing");
    let scope = scope(&engine);
    series_with_rollup(&engine, &scope, Aggregation::Sum);

    // now = 10: windows [0,5) and [5,10) are complete; [10,15) is not.
    let receipt = rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 10)
        .expect("rollup run succeeds");
    assert!(receipt.changed(), "the run wrote the rollup section");
    assert_eq!(receipt.points_written(), 2, "two complete windows aggregate");
    assert_eq!(
        rollup_points(&engine, &scope, "doc-1", "cpu", "five"),
        vec![(0, vec![0.0 + 1.0 + 2.0 + 3.0 + 4.0]), (5, vec![5.0 + 6.0 + 7.0 + 8.0 + 9.0])],
        "window starts are the aggregated timestamps and sums cover exactly their windows"
    );

    // now = 9: only [0,5) is complete.
    let receipt = rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 9)
        .expect("rollup run succeeds");
    assert_eq!(
        rollup_points(&engine, &scope, "doc-1", "cpu", "five"),
        vec![(0, vec![0.0 + 1.0 + 2.0 + 3.0 + 4.0])],
        "the incomplete window is not aggregated"
    );
    assert_eq!(receipt.points_written(), 1, "the receipt counts one point");
    engine.shutdown().expect("shuts down");
}

#[test]
fn rollup_windows_floor_negative_timestamps() {
    let engine = open_engine("rollup-negative");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(-7, vec![1.0]), (-5, vec![2.0]), (-1, vec![3.0]), (0, vec![4.0]), (1, vec![5.0])],
        vec![],
        Some(policy(None, &[("five", Aggregation::Sum, 5)])),
    );
    rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 10).expect("run succeeds");
    // Euclidean windows: [-10,-5), [-5,0), [0,5).
    assert_eq!(
        rollup_points(&engine, &scope, "doc-1", "cpu", "five"),
        vec![(-10, vec![1.0]), (-5, vec![2.0 + 3.0]), (0, vec![4.0 + 5.0])],
        "negative timestamps floor into the window that covers them"
    );
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Rollups: aggregation vocabulary
// ---------------------------------------------------------------------------

#[test]
fn rollup_aggregations_match_the_policy_per_column() {
    let engine = open_engine("rollup-aggregations");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    let appends = vec![
        (0, vec![4.0, 10.0]),
        (1, vec![1.0, 20.0]),
        (2, vec![3.0, 30.0]),
        (3, vec![2.0, 40.0]),
    ];
    for (aggregation, expected) in [
        (Aggregation::Min, vec![1.0, 10.0]),
        (Aggregation::Max, vec![4.0, 40.0]),
        (Aggregation::Sum, vec![10.0, 100.0]),
        (Aggregation::Mean, vec![2.5, 25.0]),
        (Aggregation::Count, vec![4.0, 4.0]),
    ] {
        let series = format!("cpu-{aggregation:?}").to_lowercase();
        commit_series_write(
            &engine,
            &scope,
            &format!("gapf04-establish-{aggregation:?}").to_lowercase(),
            "doc-1",
            &series,
            appends.clone(),
            vec![],
            Some(policy(None, &[("five", aggregation, 5)])),
        );
        rollup_series(&engine, &scope, &document_id("doc-1"), &series, 10)
            .expect("rollup run succeeds");
        assert_eq!(
            rollup_points(&engine, &scope, "doc-1", &series, "five"),
            vec![(0, expected)],
            "{aggregation:?} aggregates every column"
        );
    }
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Rollups: idempotent re-runs
// ---------------------------------------------------------------------------

#[test]
fn rollup_rerun_without_new_points_stages_nothing() {
    let engine = open_engine("rollup-idempotent");
    let scope = scope(&engine);
    series_with_rollup(&engine, &scope, Aggregation::Sum);
    rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 10).expect("first run");
    let before = rollup_points(&engine, &scope, "doc-1", "cpu", "five");

    let receipt =
        rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 10).expect("re-run succeeds");
    assert!(!receipt.changed(), "an unchanged recompute stages nothing");
    assert_eq!(receipt.points_written(), 0, "no points are written");
    assert_eq!(rollup_points(&engine, &scope, "doc-1", "cpu", "five"), before);
    engine.shutdown().expect("shuts down");
}

#[test]
fn rollup_rerun_after_new_points_extends_and_preserves_prior_windows() {
    let engine = open_engine("rollup-extend");
    let scope = scope(&engine);
    series_with_rollup(&engine, &scope, Aggregation::Sum);
    rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 10).expect("first run");
    let before = rollup_points(&engine, &scope, "doc-1", "cpu", "five");

    commit_series_write(
        &engine,
        &scope,
        "gapf04-extend",
        "doc-1",
        "cpu",
        vec![(12, vec![100.0]), (13, vec![200.0])],
        vec![],
        None,
    );
    let receipt =
        rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 15).expect("re-run succeeds");
    assert!(receipt.changed(), "the new window changes the section");
    let after = rollup_points(&engine, &scope, "doc-1", "cpu", "five");
    assert_eq!(
        after[..before.len()],
        before[..],
        "prior windows stay byte-identical after the incremental extension"
    );
    assert_eq!(after.len(), before.len() + 1, "exactly the new window is added");
    // The new window [10,15) aggregates the raw points 10, 11, 12, and 13.
    assert_eq!(after.last().map(|(ts, values)| (*ts, values[0])), Some((10, 321.0)));
    engine.shutdown().expect("shuts down");
}

#[test]
fn rollup_policy_change_prunes_the_removed_section() {
    let engine = open_engine("rollup-prune");
    let scope = scope(&engine);
    series_with_rollup(&engine, &scope, Aggregation::Sum);
    rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 10).expect("first run");
    assert_eq!(rollup_points(&engine, &scope, "doc-1", "cpu", "five").len(), 2);

    // The policy replaces `five` with `ten`; the next run must drop the
    // `five` section and write `ten`.
    commit_series_write(
        &engine,
        &scope,
        "gapf04-repolicy",
        "doc-1",
        "cpu",
        vec![],
        vec![],
        Some(policy(None, &[("ten", Aggregation::Max, 10)])),
    );
    rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 20).expect("re-run");
    assert_eq!(
        read_page(&engine, &scope, "doc-1", "cpu", Some("five"), None)
            .expect_err("the removed rollup no longer addresses a section"),
        FamilyError::TimeSeriesPolicyInvalid,
        "a rollup absent from the stored policy is a typed refusal"
    );
    assert_eq!(
        rollup_points(&engine, &scope, "doc-1", "cpu", "ten"),
        vec![(0, vec![9.0]), (10, vec![11.0])],
        "the new policy's section replaces the old"
    );
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Retention
// ---------------------------------------------------------------------------

#[test]
fn retention_drops_pre_horizon_entries_and_trims_rollups() {
    let engine = open_engine("retention-horizon");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    // retention 10ms: the horizon at now=100 is 90.
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(80, vec![1.0]), (85, vec![2.0]), (90, vec![3.0]), (95, vec![4.0]), (100, vec![5.0])],
        vec![],
        Some(policy(Some(10), &[("fifty", Aggregation::Sum, 50)])),
    );
    rollup_series(&engine, &scope, &document_id("doc-1"), "cpu", 100).expect("rollups first");
    let receipt = enforce_retention(
        &engine,
        &scope,
        &document_id("doc-1"),
        "cpu",
        100,
        &SharedTombstoneRetention::new(),
    )
    .expect("retention run succeeds");
    assert!(receipt.changed(), "the run dropped pre-horizon entries");
    assert_eq!(receipt.entries_deleted(), 2, "80 and 85 precede the horizon 90");
    assert_eq!(raw_timestamps(&engine, &scope, "doc-1", "cpu"), vec![90, 95, 100]);
    // The [50,100) window's rollup point stays (its timestamp 50 is below the
    // horizon, so the section is trimmed to the horizon too).
    assert_eq!(
        rollup_points(&engine, &scope, "doc-1", "cpu", "fifty"),
        vec![],
        "the rollup section is trimmed to the same horizon"
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn retention_keeps_points_whose_windows_are_incomplete() {
    let engine = open_engine("retention-pending");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    // retention 30ms with a 50ms window: at now=99 the horizon is 70, but
    // the points at 55 and 60 sit in the incomplete window [50,100) whose
    // end is still in the future — they must survive this pass.
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(55, vec![1.0]), (60, vec![2.0]), (95, vec![3.0]), (100, vec![4.0])],
        vec![],
        Some(policy(Some(30), &[("fifty", Aggregation::Sum, 50)])),
    );
    let receipt = enforce_retention(
        &engine,
        &scope,
        &document_id("doc-1"),
        "cpu",
        99,
        &SharedTombstoneRetention::new(),
    )
    .expect("retention run succeeds");
    assert_eq!(
        receipt.entries_deleted(),
        0,
        "no pre-horizon point is deletable while its window is incomplete"
    );
    // The read seam's own horizon already hides pre-horizon points, so the
    // storage proof is the next pass: at now=100 the window [50,100) is
    // complete and the same points become deletable — they survived only
    // because the first pass kept them.
    let next = enforce_retention(
        &engine,
        &scope,
        &document_id("doc-1"),
        "cpu",
        100,
        &SharedTombstoneRetention::new(),
    )
    .expect("the follow-up run succeeds");
    assert_eq!(next.entries_deleted(), 2, "55 and 60 were still stored after the guarded pass");
    assert_eq!(raw_timestamps(&engine, &scope, "doc-1", "cpu"), vec![95, 100]);
    engine.shutdown().expect("shuts down");
}

#[test]
fn retention_without_a_policy_is_a_no_op() {
    let engine = open_engine("retention-unconfigured");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(1, vec![1.0]), (2, vec![2.0])],
        vec![],
        None,
    );
    let receipt = enforce_retention(
        &engine,
        &scope,
        &document_id("doc-1"),
        "cpu",
        1000,
        &SharedTombstoneRetention::new(),
    )
    .expect("retention run succeeds");
    assert!(!receipt.changed(), "a series without retention policy is untouched");
    assert_eq!(raw_timestamps(&engine, &scope, "doc-1", "cpu"), vec![1, 2]);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Retention x GAP-R02 tombstone watermark
// ---------------------------------------------------------------------------

#[test]
fn retention_skips_a_series_a_tombstones_evidence_window_still_covers() {
    let engine = open_engine("retention-watermark-skip");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(10, vec![1.0]), (20, vec![2.0]), (100, vec![3.0])],
        vec![],
        Some(policy(Some(30), &[])),
    );
    // The watermark holds deletion evidence for doc-1 (its owner was deleted
    // somewhere a consumer still needs): the evidence window covers the
    // series' record, so the pass must not delete from it.
    let watermark = SharedTombstoneRetention::new();
    watermark.with_lock(|retention| retention.hold(&[evidence_for(&scope, "doc-1", 5)]));

    let receipt = enforce_retention(&engine, &scope, &document_id("doc-1"), "cpu", 100, &watermark)
        .expect("retention run succeeds");
    assert!(!receipt.changed(), "the covered series is not touched");
    assert!(
        receipt.skipped_document_evidence(),
        "the skip is disclosed: the tombstone's evidence window still covers the owner"
    );
    // The read seam's own horizon hides pre-horizon points either way, so
    // the storage proof is the next pass with an empty watermark: the
    // covered pass deleted nothing, and this pass reports both pre-horizon
    // entries still stored.
    let after = enforce_retention(
        &engine,
        &scope,
        &document_id("doc-1"),
        "cpu",
        100,
        &SharedTombstoneRetention::new(),
    )
    .expect("the uncovered follow-up run succeeds");
    assert_eq!(after.entries_deleted(), 2, "the covered pass left both pre-horizon entries stored");
    assert_eq!(raw_timestamps(&engine, &scope, "doc-1", "cpu"), vec![100]);
    engine.shutdown().expect("shuts down");
}

#[test]
fn retention_proceeds_once_the_evidence_window_no_longer_covers_the_document() {
    let engine = open_engine("retention-watermark-pass");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(10, vec![1.0]), (20, vec![2.0]), (100, vec![3.0])],
        vec![],
        Some(policy(Some(30), &[])),
    );
    let watermark = SharedTombstoneRetention::new();
    watermark.with_lock(|retention| retention.hold(&[evidence_for(&scope, "doc-1", 5)]));
    // Every consumer passed the fact: the floor advances past its sequence
    // and the retained set drops it, so the evidence window no longer covers
    // the document and the pass proceeds.
    watermark.with_lock(|retention| {
        retention.advance_floor(StorageSequence::new(6)).expect("floor advances past the fact");
    });

    let receipt = enforce_retention(&engine, &scope, &document_id("doc-1"), "cpu", 100, &watermark)
        .expect("retention run succeeds");
    assert!(receipt.changed(), "the uncovered series is cleaned");
    assert!(!receipt.skipped_document_evidence());
    assert_eq!(raw_timestamps(&engine, &scope, "doc-1", "cpu"), vec![100]);
    engine.shutdown().expect("shuts down");
}

#[test]
fn retention_evidence_for_another_document_or_tenant_does_not_bind_the_series() {
    let engine = open_engine("retention-watermark-scope");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    put_user_document(&engine, &scope, "doc-2");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(10, vec![1.0]), (100, vec![3.0])],
        vec![],
        Some(policy(Some(30), &[])),
    );
    let watermark = SharedTombstoneRetention::new();
    watermark.with_lock(|retention| {
        retention.hold(&[
            evidence_for(&scope, "doc-2", 5),
            DeletionEvidence::new(
                TenantId::try_new("tenant-other").expect("valid tenant"),
                None,
                document_id("doc-1"),
                1,
                StorageSequence::new(7),
                operation("gapf04-other-tenant"),
                ReplicationClockStamp::new(1, 0),
                LocalDurability::Synced,
            ),
        ])
    });

    let receipt = enforce_retention(&engine, &scope, &document_id("doc-1"), "cpu", 100, &watermark)
        .expect("retention run succeeds");
    assert!(receipt.changed(), "foreign evidence does not hold this series");
    assert_eq!(raw_timestamps(&engine, &scope, "doc-1", "cpu"), vec![100]);
    engine.shutdown().expect("shuts down");
}

#[test]
fn retention_never_transitions_the_watermark() {
    let engine = open_engine("retention-watermark-inert");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        vec![(10, vec![1.0]), (100, vec![3.0])],
        vec![],
        Some(policy(Some(30), &[("fifty", Aggregation::Sum, 50)])),
    );
    let watermark = SharedTombstoneRetention::new();
    watermark.with_lock(|retention| retention.hold(&[evidence_for(&scope, "doc-9", 5)]));
    let before = watermark.snapshot();

    run_series(&engine, &scope, &document_id("doc-1"), "cpu", 100, &watermark)
        .expect("the drive succeeds");
    let after = watermark.snapshot();
    assert_eq!(after.retention_floor(), before.retention_floor(), "the floor is untouched");
    assert_eq!(after.history_bound(), before.history_bound(), "the bound is untouched");
    assert_eq!(
        after
            .deletion_evidence_above(
                &TenantId::try_new("tenant-gapf04").expect("valid tenant"),
                StorageSequence::new(0)
            )
            .len(),
        before
            .deletion_evidence_above(
                &TenantId::try_new("tenant-gapf04").expect("valid tenant"),
                StorageSequence::new(0)
            )
            .len(),
        "no evidence is minted or dropped by the pass"
    );
    assert_eq!(after.refusals().len(), before.refusals().len(), "no refusal is recorded");
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// The drive order (rollups before retention)
// ---------------------------------------------------------------------------

#[test]
fn run_tenant_drives_every_series_of_the_tenant() {
    let engine = open_engine("drive-tenant");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    put_user_document(&engine, &scope, "docs/a");
    // Two documents, two series, one with a document id that contains the
    // record segment separator shape ('/'): the tenant drive must reach both
    // and report each series' own receipt.
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish-a",
        "doc-1",
        "cpu",
        vec![(1, vec![1.0]), (11, vec![2.0])],
        vec![],
        Some(policy(None, &[("ten", Aggregation::Sum, 10)])),
    );
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish-b",
        "docs/a",
        "cpu",
        vec![(1, vec![3.0]), (11, vec![4.0])],
        vec![],
        Some(policy(None, &[("ten", Aggregation::Max, 10)])),
    );
    let receipt = run_tenant(&engine, &scope, 20, &SharedTombstoneRetention::new())
        .expect("the tenant drive succeeds");
    assert_eq!(receipt.len(), 2, "both series of the tenant are driven");
    assert_eq!(
        rollup_points(&engine, &scope, "doc-1", "cpu", "ten"),
        vec![(0, vec![1.0]), (10, vec![2.0])],
        "the first series rolled up"
    );
    assert_eq!(
        rollup_points(&engine, &scope, "docs/a", "cpu", "ten"),
        vec![(0, vec![3.0]), (10, vec![4.0])],
        "the separator-bearing document id's series rolled up too"
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn run_series_rolls_up_before_retaining() {
    let engine = open_engine("drive-order");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    // retention 40ms, window 10ms: at now=100 the horizon is 60; every
    // window at or before 60 is complete, so a single drive must roll the
    // [0,10)..[50,60) windows up and then drop the raw points below 60.
    let appends: Vec<(i64, Vec<f64>)> =
        (0..10).map(|unit| (unit * 10 + 1, vec![value(unit)])).collect();
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        appends,
        vec![],
        Some(policy(Some(40), &[("ten", Aggregation::Sum, 10)])),
    );
    let receipt = run_series(
        &engine,
        &scope,
        &document_id("doc-1"),
        "cpu",
        100,
        &SharedTombstoneRetention::new(),
    )
    .expect("the drive succeeds");
    assert!(receipt.rollups().changed(), "the rollup half ran");
    assert!(receipt.retention().changed(), "the retention half ran");
    assert_eq!(
        rollup_points(&engine, &scope, "doc-1", "cpu", "ten"),
        vec![(60, vec![6.0]), (70, vec![7.0]), (80, vec![8.0]), (90, vec![9.0]),],
        "every complete window is aggregated, then the section is trimmed to the horizon"
    );
    assert_eq!(raw_timestamps(&engine, &scope, "doc-1", "cpu"), vec![51, 61, 71, 81, 91]);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Paging
// ---------------------------------------------------------------------------

#[test]
fn paging_limits_are_validated_at_the_seam_bounds() {
    assert_eq!(
        TimeSeriesPaging::try_new(None, Some(0)),
        Err(RequestValidationError::InvalidRange),
        "a zero limit is rejected"
    );
    assert_eq!(
        TimeSeriesPaging::try_new(None, Some(MAX_TIMESERIES_PAGE_LIMIT + 1)),
        Err(RequestValidationError::InvalidRange),
        "a limit above the seam cap is rejected"
    );
    assert!(
        TimeSeriesPaging::try_new(Some(10), Some(MAX_TIMESERIES_PAGE_LIMIT)).is_ok(),
        "the cap itself is accepted"
    );
    assert!(TimeSeriesPaging::try_new(None, None).is_ok(), "no paging is a valid request");
}

#[test]
fn paging_chains_bounded_pages_to_exhaustion() {
    let engine = open_engine("paging-chain");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        (0..10).map(|timestamp| (timestamp, vec![value(timestamp)])).collect(),
        vec![],
        None,
    );
    let mut served: Vec<i64> = Vec::new();
    let mut after = None;
    for _ in 0..5 {
        let page = read_page(
            &engine,
            &scope,
            "doc-1",
            "cpu",
            None,
            Some(TimeSeriesPaging::try_new(after, Some(3)).expect("valid paging")),
        )
        .expect("paged read");
        assert_eq!(page.total(), 10, "the matched total is reported before paging");
        served.extend(page.entries().iter().map(TimeSeriesEntry::timestamp));
        if let Some(cursor) = page.next_cursor() {
            after = Some(cursor);
        } else {
            assert!(
                served.len() < 4 * 3,
                "the terminal page arrives after the bounded ones, not early"
            );
            break;
        }
    }
    assert_eq!(served, (0..10).collect::<Vec<_>>(), "every entry is served exactly once");
    engine.shutdown().expect("shuts down");
}

#[test]
fn paging_cursors_are_stable_against_late_older_inserts() {
    let engine = open_engine("paging-stability");
    let scope = scope(&engine);
    put_user_document(&engine, &scope, "doc-1");
    commit_series_write(
        &engine,
        &scope,
        "gapf04-establish",
        "doc-1",
        "cpu",
        (0..10).map(|timestamp| (timestamp, vec![value(timestamp)])).collect(),
        vec![],
        None,
    );
    let first = read_page(
        &engine,
        &scope,
        "doc-1",
        "cpu",
        None,
        Some(TimeSeriesPaging::try_new(None, Some(3)).expect("valid paging")),
    )
    .expect("first page");
    assert_eq!(first.next_cursor(), Some(2), "the cursor is the last served timestamp");

    // A late insert at or before the cursor is invisible to the resumed
    // read; an insert strictly after it is served.
    commit_series_write(
        &engine,
        &scope,
        "gapf04-late-older",
        "doc-1",
        "cpu",
        vec![(1, vec![99.0]), (2, vec![99.0]), (11, vec![42.0])],
        vec![],
        None,
    );
    let second = read_page(
        &engine,
        &scope,
        "doc-1",
        "cpu",
        None,
        Some(TimeSeriesPaging::try_new(first.next_cursor(), Some(3)).expect("valid paging")),
    )
    .expect("resumed page");
    assert_eq!(
        second.entries().iter().map(TimeSeriesEntry::timestamp).collect::<Vec<_>>(),
        vec![3, 4, 5],
        "the resumed page continues exactly after the cursor"
    );
    assert_eq!(
        second.total(),
        11,
        "the total counts the whole matched view, including the late insert"
    );
    let rest = read_page(
        &engine,
        &scope,
        "doc-1",
        "cpu",
        None,
        Some(TimeSeriesPaging::try_new(second.next_cursor(), None).expect("valid paging")),
    )
    .expect("terminal page");
    assert_eq!(
        rest.entries().iter().map(TimeSeriesEntry::timestamp).collect::<Vec<_>>(),
        vec![6, 7, 8, 9, 11],
        "the terminal page serves the remainder, including the later insert"
    );
    assert_eq!(rest.next_cursor(), None, "an exhausted view reports no cursor");
    engine.shutdown().expect("shuts down");
}

#[test]
fn paging_applies_to_rollup_reads_and_unknown_rollups_refuse() {
    let engine = open_engine("paging-rollup");
    let scope = scope(&engine);
    series_with_rollup(&engine, &scope, Aggregation::Sum);
    run_series(&engine, &scope, &document_id("doc-1"), "cpu", 15, &SharedTombstoneRetention::new())
        .expect("drive runs");

    let page = read_page(
        &engine,
        &scope,
        "doc-1",
        "cpu",
        Some("five"),
        Some(TimeSeriesPaging::try_new(None, Some(1)).expect("valid paging")),
    )
    .expect("paged rollup read");
    assert_eq!(
        page.entries().iter().map(TimeSeriesEntry::timestamp).collect::<Vec<_>>(),
        vec![0],
        "the rollup view pages by the same cursor rule"
    );
    assert_eq!(page.total(), 3, "the rollup matched total counts the whole section");
    assert_eq!(page.next_cursor(), Some(0));

    assert_eq!(
        read_page(&engine, &scope, "doc-1", "cpu", Some("missing"), None)
            .expect_err("an unknown rollup name refuses"),
        FamilyError::TimeSeriesPolicyInvalid,
        "a rollup selector absent from the stored policy is the typed policy error"
    );
    engine.shutdown().expect("shuts down");
}
