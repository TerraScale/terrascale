//! In-crate Engine tests covering the W3-1 contract list.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has no
//! public constructor by design. Every fault is injected through the private
//! test-only storage fault control at the public commit seam, and no test
//! inspects raw pages, WAL bytes, or private lock state.

use std::{
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
};

use crate::engine::{
    CommitError, CommitOutcome, DurableResolution, Engine, EngineConfig, EngineConfigError,
    EngineError, EngineOpenError, MAX_SNAPSHOT_RECORD_PAGE, UnknownReason,
};
use crate::replication::SharedTombstoneRetention;
use crate::storage::{CompactionPolicy, CompactionRequest, FaultPoint, HistoryReclaimOutcome};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, RetryDisposition, StorageSequence, TenantId, TenantScope,
};
use crate::usage::{
    MeasurementWindow, UsageAdmissionContext, UsageCommandError, UsageContinuation, UsageFactId,
    UsagePageRequest, UsageRecordMeter, UsageRecordRequest,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-w31-engine-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn reopen_engine(dir: &PathBuf) -> Engine {
    Engine::open(EngineConfig::durable(dir).expect("durable location")).expect("engine reopens")
}

/// Opens a location on the W3-1 root-file engine explicitly.
///
/// The publication-order mechanics these tests verify — root-file
/// manifests, `ReadyToPublish` ledger states, WAL prepare-group frames,
/// post-publication root syncs — belong to that engine's format; the LSM
/// engine (P1, the default) proves its own equivalents in
/// `src/storage/lsm/tests.rs`.
fn open_root_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine = Engine::open(
        EngineConfig::durable(&dir)
            .expect("durable location")
            .with_storage_backend(crate::engine::StorageBackend::RootFiles),
    )
    .expect("engine opens");
    (dir, engine)
}

/// Reopens a root-file-engine location.
fn reopen_root_engine(dir: &PathBuf) -> Engine {
    Engine::open(
        EngineConfig::durable(dir)
            .expect("durable location")
            .with_storage_backend(crate::engine::StorageBackend::RootFiles),
    )
    .expect("engine reopens")
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn document(id: &str, collection: &str, text: &str) -> Document {
    Document::new(
        document_id(id),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::String(text.to_owned()),
    )
}

fn usage_record_request(fact_id: &str, recorded_at_millis: u64) -> UsageRecordRequest {
    UsageRecordRequest::try_new(
        UsageRecordMeter::PublicEgressBytes,
        "project-a",
        "database-a",
        "partition-a",
        crate::RegionCode::GRU,
        UsageFactId::try_new(fact_id).expect("usage fact id"),
        recorded_at_millis,
        1,
        128,
    )
    .expect("usage request")
}

fn tenant_scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), engine.identity())
}

fn commit_put(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    text: &str,
    expected: ExpectedVersion,
    durability: LocalDurability,
) -> CommitOutcome {
    let scope = tenant_scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction =
        engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
    transaction.put(document(id, "users", text), expected).expect("stages");
    transaction.commit(durability)
}

fn commit_delete(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    expected: ExpectedVersion,
    durability: LocalDurability,
) -> CommitOutcome {
    let scope = tenant_scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction =
        engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
    transaction.delete(document_id(id), expected).expect("stages");
    transaction.commit(durability)
}

fn read_document(engine: &Engine, tenant_name: &str, id: &str) -> Option<Document> {
    let scope = tenant_scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let transaction = engine.begin(&scope, snapshot, operation("read-probe")).expect("begins");
    transaction.read(&document_id(id)).expect("reads")
}

fn committed(outcome: CommitOutcome) -> crate::engine::CommitReceipt {
    match outcome {
        CommitOutcome::Committed(receipt) => receipt,
        other => panic!("expected a committed receipt, got {other:?}"),
    }
}

fn rejected(outcome: CommitOutcome) -> CommitError {
    match outcome {
        CommitOutcome::Rejected(error) => error,
        other => panic!("expected a rejection, got {other:?}"),
    }
}

fn unknown(outcome: CommitOutcome) -> crate::engine::UnknownCommit {
    match outcome {
        CommitOutcome::Unknown(unknown) => unknown,
        other => panic!("expected an unknown outcome, got {other:?}"),
    }
}

#[test]
fn engine_opens_only_configured_durable_storage() {
    assert_eq!(EngineConfig::durable("").err(), Some(EngineConfigError::EmptyLocation));

    let file_path = location("engine-opens-file");
    std::fs::write(&file_path, b"occupied").expect("writes a file");
    assert_eq!(
        EngineConfig::durable(&file_path).err(),
        Some(EngineConfigError::NonDurableLocation)
    );
    std::fs::remove_file(&file_path).ok();

    let base = EngineConfig::durable(location("engine-opens-forged")).expect("durable");
    let forged = EngineConfig { location: PathBuf::new(), ..base };
    assert_eq!(Engine::open(forged).err(), Some(EngineOpenError::InvalidConfig));

    let foreign = location("engine-opens-foreign");
    EngineConfig::durable(&foreign).expect("creates the directory");
    std::fs::write(foreign.join("format"), b"FOREIGNFORMAT-v9").expect("foreign format");
    assert_eq!(
        Engine::open(EngineConfig::durable(&foreign).expect("durable")).err(),
        Some(EngineOpenError::FormatMismatch)
    );
    std::fs::remove_dir_all(&foreign).ok();

    let fresh = location("engine-opens-fresh");
    let config = EngineConfig::durable(&fresh).expect("creates an absent directory");
    assert!(fresh.is_dir(), "the durable location must exist before opening");
    Engine::open(config).expect("opens").shutdown().expect("shuts down");
    std::fs::remove_dir_all(&fresh).ok();
}

#[test]
fn synced_commit_reopens_with_the_published_document() {
    let (dir, engine) = open_engine("synced-reopen");
    let receipt = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "ada",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(receipt.sequence().get(), 1);
    assert_eq!(receipt.durability(), LocalDurability::Synced);
    engine.shutdown().expect("shuts down");

    let reopened = reopen_engine(&dir);
    let found = read_document(&reopened, "tenant-a", "doc-1").expect("survives the reopen");
    assert_eq!(found.value(), &DocumentValue::String("ada".to_owned()));
    assert_eq!(found.metadata().version(), 1);
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn publication_does_not_expose_a_root_before_the_commit_marker() {
    let (dir, engine) = open_engine("no-root-before-marker");
    commit_put(
        &engine,
        "tenant-a",
        "op-seed",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let (events, _sink_guard) = install_event_recorder(&engine);
    engine.arm_storage_fault(FaultPoint::CommitMarkerApplication);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-marker",
        "doc-1",
        "v2",
        ExpectedVersion::Exact(1),
        LocalDurability::Written,
    );
    assert_eq!(rejected(outcome), CommitError::StorageFailure);
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("still readable").value(),
        &DocumentValue::String("v1".to_owned()),
        "an unpublished root must never become visible to live reads"
    );
    let scope = tenant_scope(&engine, "tenant-a");
    assert_eq!(
        engine.resolve_commit(&scope, operation("op-marker")).expect("resolves"),
        DurableResolution::Pending
    );
    assert!(events.lock().expect("events").is_empty());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn recovery_discards_prepare_frames_without_a_valid_commit_marker() {
    let (dir, engine) = open_engine("discard-prepare");
    commit_put(
        &engine,
        "tenant-a",
        "op-seed",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    engine.arm_storage_fault(FaultPoint::CommitMarkerApplication);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-prepare",
        "doc-2",
        "prepared",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    assert_eq!(rejected(outcome), CommitError::StorageFailure);
    drop(engine);

    let reopened = reopen_engine(&dir);
    assert!(
        read_document(&reopened, "tenant-a", "doc-2").is_none(),
        "recovery must discard prepare frames without a valid commit marker"
    );
    assert_eq!(
        read_document(&reopened, "tenant-a", "doc-1").expect("seed survives").value(),
        &DocumentValue::String("v1".to_owned())
    );
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn recovery_rejects_bad_checksum_and_truncated_frame() {
    // Mid-file corruption fails closed: commit twice, then damage a byte
    // inside the first commit's frames so an intact frame still follows it.
    let (dir, engine) = open_engine("reject-damage");
    for index in 0..2 {
        commit_put(
            &engine,
            "tenant-a",
            &format!("op-{index}"),
            &format!("doc-{index}"),
            "value",
            ExpectedVersion::New,
            LocalDurability::Synced,
        );
    }
    engine.shutdown().expect("shuts down");
    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    bytes[5] = bytes[5].wrapping_add(1);
    std::fs::write(&wal, bytes).expect("wal rewrites");
    assert_eq!(
        Engine::open(EngineConfig::durable(&dir).expect("durable")).err(),
        Some(EngineOpenError::FormatMismatch),
        "a checksum failure in a non-trailing frame must fail closed"
    );

    // A frame cut through while bytes still follow it is the same mid-file
    // corruption.
    let (second_dir, second) = open_engine("reject-truncation");
    for index in 0..2 {
        commit_put(
            &second,
            "tenant-a",
            &format!("op-{index}"),
            &format!("doc-{index}"),
            &format!("value-{index}"),
            ExpectedVersion::New,
            LocalDurability::Synced,
        );
    }
    second.shutdown().expect("shuts down");
    let wal = second_dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    let cut = bytes.len() / 2;
    bytes.truncate(cut);
    bytes.extend_from_slice(&[3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF]);
    std::fs::write(&wal, bytes).expect("wal rewrites");
    assert_eq!(
        Engine::open(EngineConfig::durable(&second_dir).expect("durable")).err(),
        Some(EngineOpenError::FormatMismatch),
        "a truncated frame with bytes behind it must fail closed"
    );
    std::fs::remove_dir_all(&dir).ok();
    std::fs::remove_dir_all(&second_dir).ok();
}

#[test]
fn torn_trailing_wal_frame_is_truncated_at_recovery_and_data_survives() {
    let (dir, engine) = open_engine("torn-tail");
    commit_put(
        &engine,
        "tenant-a",
        "op-first",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    commit_put(
        &engine,
        "tenant-a",
        "op-second",
        "doc-2",
        "v2",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    engine.shutdown().expect("shuts down");

    // Cut the last byte of the log: an interrupted append of the final frame.
    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    bytes.pop().expect("the final frame has payload bytes");
    std::fs::write(&wal, bytes).expect("wal rewrites");

    let reopened = reopen_engine(&dir);
    assert!(
        read_document(&reopened, "tenant-a", "doc-1").is_some(),
        "the commit before the torn tail must survive recovery"
    );
    let scope = tenant_scope(&reopened, "tenant-a");
    match reopened.resolve_commit(&scope, operation("op-first")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.sequence().get(), 1);
        }
        other => panic!("the surviving commit must resolve as committed, got {other:?}"),
    }
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn complete_lsm_wal_checksum_failure_fails_engine_open_closed() {
    let (dir, engine) = open_engine("final-wal-checksum");
    commit_put(
        &engine,
        "tenant-a",
        "op-first",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    engine.shutdown().expect("shuts down");

    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    let last = bytes.last_mut().expect("the final frame has payload bytes");
    *last = last.wrapping_add(1);
    std::fs::write(&wal, bytes).expect("wal rewrites");

    assert_eq!(
        Engine::open(EngineConfig::durable(&dir).expect("durable")).err(),
        Some(EngineOpenError::FormatMismatch),
        "a complete final frame may have been acknowledged and cannot be truncated as torn"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn failed_wal_append_rolls_back_to_the_last_good_offset() {
    let (dir, engine) = open_root_engine("append-rollback");
    commit_put(
        &engine,
        "tenant-a",
        "op-seed",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    engine.arm_storage_fault(FaultPoint::WalAppend);
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-torn",
            "doc-2",
            "v2",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::StorageFailure
    );
    engine.shutdown().expect("shuts down");

    // The engine still opens on the clean log, and a later commit works.
    let reopened = reopen_root_engine(&dir);
    assert!(read_document(&reopened, "tenant-a", "doc-2").is_none());
    let receipt = committed(commit_put(
        &reopened,
        "tenant-a",
        "op-after",
        "doc-3",
        "v3",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(receipt.sequence().get(), 2);
    assert!(read_document(&reopened, "tenant-a", "doc-3").is_some());
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn mid_publication_failure_blocks_a_later_branch_until_recovery() {
    let (dir, engine) = open_root_engine("sequence-reuse");
    // Commit A fails after its WAL commit group is durable (step 6 fault), so
    // the log already names sequence 1 while nothing published.
    engine.arm_storage_fault(FaultPoint::ReadyLedgerSync);
    assert_eq!(
        unknown(commit_put(
            &engine,
            "tenant-a",
            "op-a",
            "doc-a",
            "a",
            ExpectedVersion::New,
            LocalDurability::Synced,
        ))
        .reason(),
        UnknownReason::ResponseInterrupted
    );
    // B cannot publish from the old table while A's verified group still
    // awaits recovery. Doing so would create a sibling sequence-2 root that
    // omits A while recovery later reports A as committed.
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-b",
            "doc-b",
            "b",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::StorageFailure
    );
    engine.shutdown().expect("shuts down");

    // Normal reopen, with the manifest untouched, rolls A forward first.
    // Only then may B publish as A's successor.
    let reopened = reopen_root_engine(&dir);
    assert!(
        read_document(&reopened, "tenant-a", "doc-a").is_some(),
        "A's recovered resolution and data must agree"
    );
    let receipt = committed(commit_put(
        &reopened,
        "tenant-a",
        "op-b",
        "doc-b",
        "b",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(receipt.sequence().get(), 2, "recovery preserves the sequence watermark");
    assert!(
        read_document(&reopened, "tenant-a", "doc-b").is_some(),
        "B publishes after A instead of replacing it"
    );
    let scope = tenant_scope(&reopened, "tenant-a");
    match reopened.resolve_commit(&scope, operation("op-b")).expect("resolves") {
        DurableResolution::Committed(resolved) => {
            assert_eq!(resolved.sequence().get(), 2);
        }
        other => panic!("op-b must resolve as committed, got {other:?}"),
    }
    // op-a's verified WAL group rolls forward, so its identity resolves too.
    assert!(matches!(
        reopened.resolve_commit(&scope, operation("op-a")).expect("resolves"),
        DurableResolution::Committed(_)
    ));
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn same_operation_id_in_two_tenants_both_proceed() {
    let (dir, engine) = open_engine("tenant-scoped-reservation");
    let first_scope = tenant_scope(&engine, "tenant-a");
    let second_scope = tenant_scope(&engine, "tenant-b");
    let first_snapshot = engine.read_snapshot(&first_scope).expect("snapshot");
    let second_snapshot = engine.read_snapshot(&second_scope).expect("snapshot");
    let shared = operation("op-shared");

    let mut first = engine
        .begin(&first_scope, first_snapshot, shared.clone())
        .expect("the first tenant begins");
    let mut second = engine
        .begin(&second_scope, second_snapshot, shared.clone())
        .expect("the same operation identity in another tenant must not be blocked");

    first.put(document("doc-1", "users", "a"), ExpectedVersion::New).expect("stages");
    second.put(document("doc-1", "users", "b"), ExpectedVersion::New).expect("stages");
    assert!(matches!(first.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    assert!(matches!(second.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("present").value(),
        &DocumentValue::String("a".to_owned())
    );
    assert_eq!(
        read_document(&engine, "tenant-b", "doc-1").expect("present").value(),
        &DocumentValue::String("b".to_owned())
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_digest_is_independent_of_snapshot_state() {
    let (dir, engine) = open_engine("delete-digest");
    // Snapshot the tenant before the document exists: a delete staged from
    // this view resolves no collection.
    let absent_scope = tenant_scope(&engine, "tenant-a");
    let absent_snapshot = engine.read_snapshot(&absent_scope).expect("snapshot");
    let mut absent =
        engine.begin(&absent_scope, absent_snapshot, operation("op-probe")).expect("begins");
    absent.delete(document_id("doc-1"), ExpectedVersion::Any).expect("stages");

    // Seed the document, then stage the same logical delete from a snapshot
    // that contains it: this view resolves the collection.
    commit_put(
        &engine,
        "tenant-a",
        "op-seed",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let existing_scope = tenant_scope(&engine, "tenant-a");
    let existing_snapshot = engine.read_snapshot(&existing_scope).expect("snapshot");
    let mut existing =
        engine.begin(&existing_scope, existing_snapshot, operation("op-delete")).expect("begins");
    existing.delete(document_id("doc-1"), ExpectedVersion::Any).expect("stages");
    assert_eq!(
        crate::storage::write_set_digest(&tenant("tenant-a"), &absent.staged_writes()),
        crate::storage::write_set_digest(&tenant("tenant-a"), &existing.staged_writes()),
        "a delete's digest must not depend on the snapshot-resolved collection"
    );
    drop(absent);

    // The retry half: after the delete commits, retrying the same operation
    // identity against a snapshot where the document is now absent must
    // resolve to the one recorded commit instead of OperationIdReuse.
    assert!(matches!(existing.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let retry_scope = tenant_scope(&engine, "tenant-a");
    let retry_snapshot = engine.read_snapshot(&retry_scope).expect("snapshot");
    let mut retry =
        engine.begin(&retry_scope, retry_snapshot, operation("op-delete")).expect("begins");
    retry.delete(document_id("doc-1"), ExpectedVersion::Any).expect("stages");
    let replayed = committed(retry.commit(LocalDurability::Synced));
    assert_eq!(replayed.sequence().get(), 2, "the retry replays the recorded commit");
    assert!(read_document(&engine, "tenant-a", "doc-1").is_none());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn retrying_the_same_operation_id_returns_one_commit() {
    let (dir, engine) = open_engine("idempotent-retry");
    let first = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let retry = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(
        retry.sequence(),
        first.sequence(),
        "an idempotent retry must not publish a second commit"
    );
    assert_eq!(retry.durability(), LocalDurability::Synced);
    assert_eq!(retry.operation_id(), first.operation_id());
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("present").metadata().version(),
        1,
        "one commit means one version"
    );
    drop(engine);

    let reopened = reopen_engine(&dir);
    let after_reopen = committed(commit_put(
        &reopened,
        "tenant-a",
        "op-1",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(after_reopen.sequence(), first.sequence());
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn roll_forward_digest_lets_a_same_identity_retry_replay_after_reopen() {
    // Commit A fails at the ReadyLedgerSync fault: its WAL commit group is
    // durable but no Ready record landed. Recovery roll-forward publishes it
    // without a prior ledger entry, so the Published record itself must carry
    // the digest and the identity must stay bound for a same-digest retry.
    let (dir, engine) = open_root_engine("roll-forward-digest");
    engine.arm_storage_fault(FaultPoint::ReadyLedgerSync);
    assert_eq!(
        unknown(commit_put(
            &engine,
            "tenant-a",
            "op-a",
            "doc-a",
            "a",
            ExpectedVersion::New,
            LocalDurability::Synced,
        ))
        .reason(),
        UnknownReason::ResponseInterrupted
    );
    drop(engine);

    let reopened = reopen_root_engine(&dir);
    let scope = tenant_scope(&reopened, "tenant-a");
    match reopened.resolve_commit(&scope, operation("op-a")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.operation_id(), &operation("op-a"));
            assert_eq!(receipt.sequence().get(), 1);
            assert_ne!(receipt.durability(), LocalDurability::Synced);
        }
        other => panic!("the rolled-forward commit must resolve as committed, got {other:?}"),
    }
    // Retrying the exact same operation must replay the recorded resolution,
    // not be rejected as identity reuse.
    let replayed = committed(commit_put(
        &reopened,
        "tenant-a",
        "op-a",
        "doc-a",
        "a",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(replayed.sequence().get(), 1, "the same identity and write set replay one commit");
    assert_eq!(
        read_document(&reopened, "tenant-a", "doc-a").expect("present").metadata().version(),
        1,
        "one commit means one version"
    );
    // A different write set under the same identity is still reuse.
    assert_eq!(
        rejected(commit_put(
            &reopened,
            "tenant-a",
            "op-a",
            "doc-a",
            "mutated",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::OperationIdReuse
    );
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn different_payload_with_same_operation_id_is_rejected() {
    let (dir, engine) = open_engine("reuse-rejected");
    commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "different",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let error = rejected(outcome);
    assert_eq!(error, CommitError::OperationIdReuse);
    assert_eq!(error.retry_disposition(), RetryDisposition::Never);
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("unchanged").value(),
        &DocumentValue::String("v1".to_owned())
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn unknown_commit_resolves_from_the_durable_operation_ledger() {
    let (dir, engine) = open_root_engine("unknown-resolves");
    engine.arm_storage_fault(FaultPoint::PublishedLedgerSync);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let uncertain = unknown(outcome);
    assert_eq!(uncertain.reason(), UnknownReason::PublishedLedgerSync);
    assert_eq!(uncertain.operation_id(), &operation("op-1"));
    let scope = tenant_scope(&engine, "tenant-a");
    match engine.resolve_commit(&scope, operation("op-1")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.operation_id(), &operation("op-1"));
            assert_eq!(receipt.sequence().get(), 1);
            assert_ne!(
                receipt.durability(),
                LocalDurability::Synced,
                "no Synced claim is allowed without the confirmed post-publication syncs"
            );
        }
        other => panic!("expected a committed resolution, got {other:?}"),
    }
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-2",
            "doc-2",
            "later",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::StorageFailure,
        "a later commit waits until recovery records op-1's terminal binding"
    );
    drop(engine);

    let reopened = reopen_root_engine(&dir);
    assert!(read_document(&reopened, "tenant-a", "doc-1").is_some());
    let later = committed(commit_put(
        &reopened,
        "tenant-a",
        "op-2",
        "doc-2",
        "later",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(later.sequence().get(), 2);
    assert!(read_document(&reopened, "tenant-a", "doc-2").is_some());
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn pending_resolution_is_not_reported_as_rejection() {
    let (dir, engine) = open_engine("pending-not-rejected");
    engine.arm_storage_fault(FaultPoint::PointerPublish);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let error = rejected(outcome);
    assert_eq!(error, CommitError::StorageFailure);
    assert_eq!(error.retry_disposition(), RetryDisposition::AfterRecovery);
    let scope = tenant_scope(&engine, "tenant-a");
    assert_eq!(
        engine.resolve_commit(&scope, operation("op-1")).expect("resolves"),
        DurableResolution::Pending,
        "a durable ReadyToPublish record without its root must stay pending"
    );
    assert!(read_document(&engine, "tenant-a", "doc-1").is_none());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn ready_to_publish_ledger_record_is_durable_before_pointer_replacement() {
    // First, publication must not move the pointer while the ledger record is
    // not durable.
    let (dir, engine) = open_root_engine("ready-before-pointer");
    engine.arm_storage_fault(FaultPoint::ReadyLedgerSync);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-ledger-first",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(unknown(outcome).reason(), UnknownReason::ResponseInterrupted);
    assert!(
        read_document(&engine, "tenant-a", "doc-1").is_none(),
        "the pointer must not be replaced before the ledger record is durable"
    );
    drop(engine);

    // Second, the durable record must answer before the replacement is even
    // attempted: an interrupted pointer publication leaves an identity-bound,
    // resolvable record behind. Recovery rolls the verified write-ahead commit
    // forward, and the durable record resolves the operation by identity.
    let engine = reopen_root_engine(&dir);
    engine.arm_storage_fault(FaultPoint::PointerPublish);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-pointer-second",
        "doc-2",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(unknown(outcome).reason(), UnknownReason::ResponseInterrupted);
    assert!(read_document(&engine, "tenant-a", "doc-2").is_none());
    drop(engine);
    let reopened = reopen_root_engine(&dir);
    let scope = tenant_scope(&reopened, "tenant-a");
    match reopened.resolve_commit(&scope, operation("op-pointer-second")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.operation_id(), &operation("op-pointer-second"));
            assert_eq!(receipt.sequence().get(), 2);
            assert_ne!(receipt.durability(), LocalDurability::Synced);
        }
        other => panic!("the durable record must resolve the operation, got {other:?}"),
    }
    // The durable binding answers a same-identity retry instead of allowing a
    // fresh commit under the same identity.
    let outcome = commit_put(
        &reopened,
        "tenant-a",
        "op-pointer-second",
        "doc-2",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let replay = committed(outcome);
    assert_eq!(replay.sequence().get(), 2, "the retry replays the recorded resolution");
    let different = commit_put(
        &reopened,
        "tenant-a",
        "op-pointer-second",
        "doc-2",
        "mutated",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(rejected(different), CommitError::OperationIdReuse);
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn reopen_resolves_a_ready_to_publish_record_by_operation_id() {
    let (dir, engine) = open_root_engine("reopen-ready");
    engine.arm_storage_fault(FaultPoint::PointerSync);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(unknown(outcome).reason(), UnknownReason::PostPublicationPointerSync);
    drop(engine);

    let reopened = reopen_root_engine(&dir);
    let scope = tenant_scope(&reopened, "tenant-a");
    match reopened.resolve_commit(&scope, operation("op-1")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.operation_id(), &operation("op-1"));
            assert_eq!(receipt.sequence().get(), 1);
            assert_eq!(receipt.durability(), LocalDurability::Written);
        }
        other => panic!("expected a deterministic committed resolution, got {other:?}"),
    }
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn transaction_conflict_returns_rejected_without_partial_writes() {
    let (dir, engine) = open_engine("conflict");
    commit_put(
        &engine,
        "tenant-a",
        "op-seed",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");

    commit_put(
        &engine,
        "tenant-a",
        "op-other",
        "doc-1",
        "v2",
        ExpectedVersion::Exact(1),
        LocalDurability::Synced,
    );

    let mut transaction = engine.begin(&scope, snapshot, operation("op-conflict")).expect("begins");
    transaction.put(document("doc-1", "users", "v3"), ExpectedVersion::Exact(1)).expect("stages");
    transaction.put(document("doc-2", "users", "new"), ExpectedVersion::New).expect("stages");
    let error = rejected(transaction.commit(LocalDurability::Synced));
    assert_eq!(error, CommitError::Conflict);
    assert_eq!(error.retry_disposition(), RetryDisposition::NewOperation);
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("unchanged").value(),
        &DocumentValue::String("v2".to_owned())
    );
    assert!(read_document(&engine, "tenant-a", "doc-2").is_none());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn one_transaction_publishes_all_writes_atomically() {
    let (dir, engine) = open_engine("atomic");
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("op-both")).expect("begins");
    transaction.put(document("doc-a", "users", "a"), ExpectedVersion::New).expect("stages");
    transaction.put(document("doc-b", "users", "b"), ExpectedVersion::New).expect("stages");
    let receipt = committed(transaction.commit(LocalDurability::Synced));
    assert_eq!(receipt.sequence().get(), 1);
    assert!(read_document(&engine, "tenant-a", "doc-a").is_some());
    assert!(read_document(&engine, "tenant-a", "doc-b").is_some());

    engine.arm_storage_fault(FaultPoint::PointerPublish);
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("op-none")).expect("begins");
    transaction.put(document("doc-c", "users", "c"), ExpectedVersion::New).expect("stages");
    transaction.put(document("doc-d", "users", "d"), ExpectedVersion::New).expect("stages");
    assert_eq!(rejected(transaction.commit(LocalDurability::Synced)), CommitError::StorageFailure);
    assert!(read_document(&engine, "tenant-a", "doc-c").is_none());
    assert!(read_document(&engine, "tenant-a", "doc-d").is_none());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn buffered_written_and_synced_have_distinct_loss_windows() {
    let (dir, engine) = open_engine("loss-windows");
    let written = committed(commit_put(
        &engine,
        "tenant-a",
        "op-written",
        "doc-written",
        "written",
        ExpectedVersion::New,
        LocalDurability::Written,
    ));
    let synced = committed(commit_put(
        &engine,
        "tenant-a",
        "op-synced",
        "doc-synced",
        "synced",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let buffered = committed(commit_put(
        &engine,
        "tenant-a",
        "op-buffered",
        "doc-buffered",
        "buffered",
        ExpectedVersion::New,
        LocalDurability::Buffered,
    ));
    assert_eq!(written.durability(), LocalDurability::Written);
    assert_eq!(synced.durability(), LocalDurability::Synced);
    assert_eq!(buffered.durability(), LocalDurability::Buffered);
    assert_eq!(written.sequence().get(), 1);
    assert_eq!(synced.sequence().get(), 2);
    assert_eq!(buffered.sequence().get(), 3);
    // All three are visible to this live process.
    assert!(read_document(&engine, "tenant-a", "doc-written").is_some());
    assert!(read_document(&engine, "tenant-a", "doc-synced").is_some());
    assert!(read_document(&engine, "tenant-a", "doc-buffered").is_some());
    // Dropping the Engine models process loss: a Buffered commit loses its
    // bytes while its resolution metadata survives.
    drop(engine);
    let reopened = reopen_engine(&dir);
    assert!(read_document(&reopened, "tenant-a", "doc-written").is_some());
    assert!(read_document(&reopened, "tenant-a", "doc-synced").is_some());
    assert!(
        read_document(&reopened, "tenant-a", "doc-buffered").is_none(),
        "a Buffered commit is process-local by contract"
    );
    let scope = tenant_scope(&reopened, "tenant-a");
    match reopened.resolve_commit(&scope, operation("op-buffered")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.durability(), LocalDurability::Buffered);
        }
        other => panic!("the buffered operation committed, got {other:?}"),
    }
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn read_snapshot_remains_immutable_after_a_later_commit() {
    let (dir, engine) = open_engine("immutable-snapshot");
    commit_put(
        &engine,
        "tenant-a",
        "op-seed",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let unchanged_clone = snapshot.clone();
    let reader = engine.begin(&scope, snapshot, operation("op-read")).expect("begins");
    assert_eq!(
        reader.read(&document_id("doc-1")).expect("reads").expect("present").value(),
        &DocumentValue::String("v1".to_owned())
    );

    commit_put(
        &engine,
        "tenant-a",
        "op-later",
        "doc-1",
        "v2",
        ExpectedVersion::Exact(1),
        LocalDurability::Synced,
    );

    assert_eq!(
        reader.read(&document_id("doc-1")).expect("reads").expect("present").value(),
        &DocumentValue::String("v1".to_owned()),
        "a snapshot taken before a commit must keep reading the pre-commit state"
    );
    // A transaction begun after the commit with the original snapshot still
    // reads the pre-commit state: the snapshot value itself never advanced.
    let late_reader =
        engine.begin(&scope, unchanged_clone, operation("op-read-late")).expect("begins");
    assert_eq!(
        late_reader.read(&document_id("doc-1")).expect("reads").expect("present").value(),
        &DocumentValue::String("v1".to_owned())
    );
    drop(reader);
    drop(late_reader);
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("new snapshot").value(),
        &DocumentValue::String("v2".to_owned())
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[allow(clippy::let_underscore_untyped)]
fn install_event_recorder(
    engine: &Engine,
) -> (Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>>, crate::engine::SubscriptionGuard) {
    let events: Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>> =
        Arc::new(Mutex::new(Vec::new()));
    let sink_events = Arc::clone(&events);
    let guard = engine.subscribe_publications(Arc::new(
        move |event: &crate::storage::CommitPublicationEvent| {
            sink_events.lock().expect("event lock").push(event.clone());
        },
    ));
    (events, guard)
}

#[test]
fn commit_publication_event_is_emitted_after_storage_locks_release() {
    let (_dir, engine) = open_engine("event-after-locks");
    let storage_probe = Arc::clone(&engine.storage);
    let released = Arc::new(Mutex::new(false));
    let released_flag = Arc::clone(&released);
    let _sink_guard = engine.subscribe_publications(Arc::new(
        move |_event: &crate::storage::CommitPublicationEvent| {
            // If storage locks were still held, this non-reentrant probe would
            // fail to acquire them.
            let acquired = storage_probe.try_lock().is_ok();
            *released_flag.lock().expect("flag lock") = acquired;
        },
    ));
    let receipt = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(receipt.sequence().get(), 1);
    assert!(
        *released.lock().expect("flag lock"),
        "the publication event must fire after storage locks release"
    );
}

#[test]
fn commit_publication_event_carries_an_immutable_root_and_storage_sequence() {
    let (_dir, engine) = open_engine("event-payload");
    let (events, _sink_guard) = install_event_recorder(&engine);
    let receipt = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let recorded = events.lock().expect("events").clone();
    assert_eq!(recorded.len(), 1);
    let event = &recorded[0];
    assert_eq!(event.sequence(), receipt.sequence());
    assert_eq!(event.sequence().get(), 1);
    assert!(!event.root().as_str().is_empty());
    assert_eq!(event.operation_id(), &operation("op-1"));
    let frozen = event.clone();
    commit_put(
        &engine,
        "tenant-a",
        "op-2",
        "doc-2",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(events.lock().expect("events").len(), 2);
    assert_eq!(frozen.sequence().get(), 1, "an emitted event is immutable");
}

#[test]
fn commit_publication_event_is_not_emitted_before_pointer_publication() {
    let (_dir, engine) = open_engine("event-order");
    let (events, _sink_guard) = install_event_recorder(&engine);
    engine.arm_storage_fault(FaultPoint::PointerPublish);
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-1",
            "doc-1",
            "value",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::StorageFailure
    );
    assert!(
        events.lock().expect("events").is_empty(),
        "no publication event may precede pointer publication"
    );
    engine.arm_storage_fault(FaultPoint::CommitMarkerApplication);
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-2",
            "doc-2",
            "value",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::StorageFailure
    );
    assert!(events.lock().expect("events").is_empty());
}

#[test]
fn post_publication_pointer_sync_failure_returns_unknown_and_reopens_deterministically() {
    post_publication_sync_failure_is_deterministic(FaultPoint::PointerSync, "pointer");
}

#[test]
fn post_publication_directory_sync_failure_returns_unknown_and_reopens_deterministically() {
    post_publication_sync_failure_is_deterministic(FaultPoint::DirectorySync, "directory");
}

fn post_publication_sync_failure_is_deterministic(point: FaultPoint, tag: &str) {
    let (dir, engine) = open_engine(tag);
    engine.arm_storage_fault(point);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let uncertain = unknown(outcome);
    assert_eq!(uncertain.operation_id(), &operation("op-1"));
    assert_eq!(
        uncertain.reason(),
        match point {
            FaultPoint::PointerSync => UnknownReason::PostPublicationPointerSync,
            _ => UnknownReason::PostPublicationDirectorySync,
        }
    );
    // The fault happens after pointer publication, so the controlled fixture's
    // live reads see the new document.
    assert!(
        read_document(&engine, "tenant-a", "doc-1").is_some(),
        "the published root must be visible to live reads"
    );
    // No Synced claim may be returned or resolvable.
    let scope = tenant_scope(&engine, "tenant-a");
    match engine.resolve_commit(&scope, operation("op-1")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_ne!(receipt.durability(), LocalDurability::Synced);
        }
        other => panic!("the publication is durable, got {other:?}"),
    }
    drop(engine);

    // Reopen resolves the same operation deterministically from the persisted
    // ledger and root identity, still without a Synced claim.
    let reopened = reopen_engine(&dir);
    let scope = tenant_scope(&reopened, "tenant-a");
    let resolved = match reopened.resolve_commit(&scope, operation("op-1")).expect("resolves") {
        DurableResolution::Committed(receipt) => receipt,
        other => panic!("reopen must resolve the operation, got {other:?}"),
    };
    assert_eq!(resolved.operation_id(), &operation("op-1"));
    assert_eq!(resolved.sequence().get(), 1);
    assert_eq!(resolved.durability(), LocalDurability::Written);

    // Retrying the same logical request with the same identity returns the
    // same resolution.
    let retry = committed(commit_put(
        &reopened,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(retry.sequence(), resolved.sequence());
    assert_eq!(retry.durability(), LocalDurability::Written);

    // A different digest under the same identity is rejected as reuse.
    let different = commit_put(
        &reopened,
        "tenant-a",
        "op-1",
        "doc-1",
        "mutated",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    assert_eq!(rejected(different), CommitError::OperationIdReuse);
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn begin_rejects_snapshot_from_another_engine() {
    let (first_dir, first) = open_engine("engine-a");
    let (second_dir, second) = open_engine("engine-b");
    let first_scope = tenant_scope(&first, "tenant-a");
    let foreign_snapshot = first.read_snapshot(&first_scope).expect("snapshot");
    let second_scope = tenant_scope(&second, "tenant-a");
    assert_eq!(
        second.begin(&second_scope, foreign_snapshot, operation("op-1")).err(),
        Some(EngineError::SnapshotEngineMismatch)
    );
    drop(first);
    drop(second);
    std::fs::remove_dir_all(&first_dir).ok();
    std::fs::remove_dir_all(&second_dir).ok();
}

#[test]
fn begin_rejects_snapshot_for_another_tenant() {
    let (_dir, engine) = open_engine("tenant-mismatch");
    let other_scope = TenantScope::from_parts(tenant("tenant-b"), engine.identity());
    let foreign_snapshot = engine.read_snapshot(&other_scope).expect("snapshot");
    let own_scope = tenant_scope(&engine, "tenant-a");
    assert_eq!(
        engine.begin(&own_scope, foreign_snapshot, operation("op-1")).err(),
        Some(EngineError::SnapshotTenantMismatch)
    );
}

#[test]
fn begin_rejects_scope_different_from_snapshot() {
    let (_dir, engine) = open_engine("scope-mismatch");
    let foreign_scope =
        TenantScope::from_parts(tenant("tenant-a"), engine.identity().wrapping_add(1));
    let snapshot = engine
        .read_snapshot(&foreign_scope)
        .expect_err("a scope from another engine is rejected before any snapshot exists");
    assert_eq!(snapshot, EngineError::ScopeMismatch);
    let own_scope = tenant_scope(&engine, "tenant-a");
    let own_snapshot = engine.read_snapshot(&own_scope).expect("snapshot");
    assert_eq!(
        engine.begin(&foreign_scope, own_snapshot, operation("op-1")).err(),
        Some(EngineError::ScopeMismatch)
    );
}

#[test]
fn begin_rejects_invalid_operation_id() {
    let (_dir, engine) = open_engine("invalid-operation");
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let first = engine
        .begin(&scope, snapshot.clone(), operation("op-1"))
        .expect("first transaction begins");
    // `OperationId` is validated at construction, so the invalid identity at
    // `begin` is one already used by another live transaction of this Engine.
    assert_eq!(
        engine.begin(&scope, snapshot, operation("op-1")).err(),
        Some(EngineError::OperationIdReuse { operation_id: operation("op-1") })
    );
    // An empty write set is a validation rejection: nothing is published.
    assert_eq!(
        first.commit(LocalDurability::Synced),
        CommitOutcome::Rejected(CommitError::Validation)
    );
    // Ending the transaction releases the reservation, so the same identity
    // can begin again because nothing was ever published under it.
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut second = engine
        .begin(&scope, snapshot, operation("op-1"))
        .expect("the reservation is released when the transaction ends");
    second.put(document("doc-1", "users", "value"), ExpectedVersion::New).expect("stages");
    assert!(matches!(second.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
}

#[test]
fn usage_admission_requires_synced_commit_durability() {
    let (_dir, engine) = open_engine("usage-admission-durability");
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("op-admitted")).expect("begins");
    let context = UsageAdmissionContext::try_new(
        "project-a",
        "database-a",
        "partition-a",
        crate::RegionCode::GRU,
        UsageFactId::try_new("op-admitted").expect("request id"),
        1_788_105_600_000,
        1,
        7,
    )
    .expect("admission context");
    transaction.attach_usage_admission(context).expect("attaches");
    transaction.put(document("doc-1", "users", "value"), ExpectedVersion::New).expect("stages");

    assert_eq!(
        transaction.commit(LocalDurability::Written),
        CommitOutcome::Rejected(CommitError::UsageRequiresSynced)
    );
}

#[test]
fn usage_pages_bind_snapshot_filter_page_size_and_tenant() {
    let (_dir, engine) = open_engine("usage-pages");
    let scope_a = tenant_scope(&engine, "tenant-a");
    let scope_b = tenant_scope(&engine, "tenant-b");
    for (id, at) in [("fact-1", 1_000), ("fact-2", 2_000), ("fact-3", 3_000)] {
        engine.record_usage(&scope_a, usage_record_request(id, at)).expect("usage records");
    }

    let first = engine
        .usage_page(&scope_a, UsagePageRequest::try_new(None, 1, None).unwrap())
        .expect("first page");
    assert_eq!(first.facts().len(), 1);
    assert_eq!(first.facts()[0].fact().identity().id().as_str(), "fact-1");
    let continuation = first.continuation().expect("more facts").clone();
    let fixed_snapshot = first.snapshot_sequence();

    engine
        .record_usage(&scope_a, usage_record_request("fact-after-snapshot", 4_000))
        .expect("later usage records");
    let mut ids = vec!["fact-1".to_owned()];
    let mut next = Some(continuation.clone());
    while let Some(cursor) = next {
        let page = engine
            .usage_page(&scope_a, UsagePageRequest::try_new(None, 1, Some(cursor)).unwrap())
            .expect("continuation page");
        assert_eq!(page.snapshot_sequence(), fixed_snapshot);
        ids.extend(page.facts().iter().map(|fact| fact.fact().identity().id().as_str().to_owned()));
        next = page.continuation().cloned();
    }
    assert_eq!(ids, ["fact-1", "fact-2", "fact-3"]);

    assert_eq!(
        engine.usage_page(
            &scope_b,
            UsagePageRequest::try_new(None, 1, Some(continuation.clone())).unwrap(),
        ),
        Err(UsageCommandError::InvalidRequest)
    );
    assert_eq!(
        engine.usage_page(
            &scope_a,
            UsagePageRequest::try_new(
                Some(MeasurementWindow::interval(1, 5_000).unwrap()),
                1,
                Some(continuation.clone()),
            )
            .unwrap(),
        ),
        Err(UsageCommandError::InvalidRequest)
    );
    assert_eq!(
        engine.usage_page(
            &scope_a,
            UsagePageRequest::try_new(None, 2, Some(continuation.clone())).unwrap(),
        ),
        Err(UsageCommandError::InvalidRequest)
    );
    let mut damaged = continuation.as_bytes().to_vec();
    damaged[0] ^= 0xff;
    assert_eq!(
        engine.usage_page(
            &scope_a,
            UsagePageRequest::try_new(None, 1, Some(UsageContinuation::try_new(damaged).unwrap()),)
                .unwrap(),
        ),
        Err(UsageCommandError::InvalidRequest)
    );
}

#[test]
fn root_file_backend_refuses_durable_usage_commands() {
    let (_dir, engine) = open_root_engine("usage-unsupported");
    let scope = tenant_scope(&engine, "tenant-a");
    assert_eq!(
        engine.record_usage(&scope, usage_record_request("fact-1", 1_000)),
        Err(UsageCommandError::UnsupportedBackend)
    );
    assert_eq!(
        engine.usage_page(&scope, UsagePageRequest::try_new(None, 10, None).unwrap()),
        Err(UsageCommandError::UnsupportedBackend)
    );
}

#[test]
fn unknown_operation_id_resolution_preserves_original_identity() {
    let (dir, engine) = open_root_engine("identity-preserved");
    engine.arm_storage_fault(FaultPoint::PointerSync);
    let outcome = commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    );
    let uncertain = unknown(outcome);
    assert_eq!(uncertain.operation_id(), &operation("op-1"));
    drop(engine);

    let reopened = reopen_root_engine(&dir);
    let scope = tenant_scope(&reopened, "tenant-a");
    match reopened.resolve_commit(&scope, operation("op-1")).expect("resolves") {
        DurableResolution::Committed(receipt) => {
            assert_eq!(receipt.operation_id(), &operation("op-1"));
        }
        other => panic!("resolution must preserve the original identity, got {other:?}"),
    }
    // An identity with no durable record is pending, never a silent rejection.
    assert_eq!(
        reopened.resolve_commit(&scope, operation("op-never")).expect("resolves"),
        DurableResolution::Pending
    );
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn tenant_scope_cannot_change_during_a_transaction() {
    let (dir, engine) = open_engine("frozen-scope");
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("op-1")).expect("begins");
    transaction.put(document("doc-1", "users", "value"), ExpectedVersion::New).expect("stages");
    // A second scope exists for another tenant of the same Engine, but the
    // transaction has no way to adopt it: it publishes under its own scope.
    let other_scope = tenant_scope(&engine, "tenant-b");
    let receipt = committed(transaction.commit(LocalDurability::Synced));
    assert_eq!(receipt.sequence().get(), 1);
    assert!(read_document(&engine, "tenant-a", "doc-1").is_some());
    let other_snapshot = engine.read_snapshot(&other_scope).expect("snapshot");
    let other_reader =
        engine.begin(&other_scope, other_snapshot, operation("op-read-b")).expect("begins");
    assert!(
        other_reader.read(&document_id("doc-1")).expect("reads").is_none(),
        "a transaction cannot publish into another tenant's scope"
    );
    drop(other_reader);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn production_api_has_no_ephemeral_durable_engine_constructor() {
    // The absence of `new_in_memory`, `from_memory`, and `ephemeral` is proven
    // by the compile-fail doc tests on `Engine` and `EngineConfig`. The
    // runtime half proves the only configuration path is a durable directory.
    assert_eq!(EngineConfig::durable("").err(), Some(EngineConfigError::EmptyLocation));
    let occupied = location("ephemeral-occupied");
    std::fs::write(&occupied, b"file").expect("writes");
    assert_eq!(EngineConfig::durable(&occupied).err(), Some(EngineConfigError::NonDurableLocation));
    std::fs::remove_file(&occupied).ok();
    let durable = location("ephemeral-durable");
    let engine = Engine::open(EngineConfig::durable(&durable).expect("durable"))
        .expect("opens a durable location");
    engine.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&durable).ok();
}

#[test]
fn expected_version_any_new_and_exact_are_enforced() {
    let (_dir, engine) = open_engine("expected-versions");
    // Any accepts a fresh insert and any later replacement.
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "op-any-1",
            "doc-1",
            "v1",
            ExpectedVersion::Any,
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));
    // New requires absence once the document exists.
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-new",
            "doc-1",
            "value",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::Conflict
    );
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "op-any-2",
            "doc-1",
            "v2",
            ExpectedVersion::Any,
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));
    // Exact requires the stated version.
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-exact",
            "doc-1",
            "v3",
            ExpectedVersion::Exact(1),
            LocalDurability::Synced,
        )),
        CommitError::Conflict
    );
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "op-exact-ok",
            "doc-1",
            "v3",
            ExpectedVersion::Exact(2),
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));
    assert_eq!(
        read_document(&engine, "tenant-a", "doc-1").expect("present").metadata().version(),
        3
    );
}

#[test]
fn oversized_document_is_rejected_as_invalid() {
    let (_dir, engine) = open_engine("invalid-document");
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("op-1")).expect("begins");
    let large = Document::new(
        document_id("doc-1"),
        CollectionName::try_new("users").expect("valid identifier"),
        DocumentValue::String("x".repeat(2 << 20)),
    );
    assert_eq!(
        transaction.put(large, ExpectedVersion::New).err(),
        Some(EngineError::InvalidDocument)
    );
}

#[test]
fn storage_sequence_is_monotonic_across_commits_and_reopens() {
    let (dir, engine) = open_engine("monotonic-sequence");
    let first = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let second = committed(commit_put(
        &engine,
        "tenant-a",
        "op-2",
        "doc-2",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert!(second.sequence().get() > first.sequence().get());
    drop(engine);
    let reopened = reopen_engine(&dir);
    let third = committed(commit_put(
        &reopened,
        "tenant-a",
        "op-3",
        "doc-3",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert!(third.sequence().get() > second.sequence().get());
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

// Integrator seam tests for the snapshot document view (W3-2 collision
// action). They observe only typed outcomes at the crate-private seam.

#[test]
fn snapshot_documents_returns_only_the_snapshot_tenants_documents() {
    let (dir, engine) = open_engine("snapshot-view-tenant");
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "op-a1",
            "doc-1",
            "a-one",
            ExpectedVersion::New,
            LocalDurability::Synced
        ),
        CommitOutcome::Committed(_)
    ));
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-b",
            "op-b1",
            "doc-1",
            "b-one",
            ExpectedVersion::New,
            LocalDurability::Synced
        ),
        CommitOutcome::Committed(_)
    ));
    let scope = tenant_scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let documents = engine.snapshot_documents(&scope, &snapshot).expect("view");
    assert_eq!(documents.len(), 1, "only the snapshot tenant is visible");
    assert_eq!(documents[0].id().as_str(), "doc-1");
    assert_eq!(documents[0].value(), &crate::types::DocumentValue::String("a-one".to_owned()));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn snapshot_documents_stay_immutable_after_a_later_commit() {
    let (dir, engine) = open_engine("snapshot-view-immutable");
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "op-1",
            "doc-1",
            "v1",
            ExpectedVersion::New,
            LocalDurability::Synced
        ),
        CommitOutcome::Committed(_)
    ));
    let scope = tenant_scope(&engine, "tenant-a");
    let held = engine.read_snapshot(&scope).expect("snapshot");
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "op-2",
            "doc-2",
            "v2",
            ExpectedVersion::New,
            LocalDurability::Synced
        ),
        CommitOutcome::Committed(_)
    ));
    let held_view = engine.snapshot_documents(&scope, &held).expect("held view");
    assert_eq!(held_view.len(), 1, "held snapshot excludes the later write");
    let fresh = engine.read_snapshot(&scope).expect("fresh snapshot");
    let fresh_view = engine.snapshot_documents(&scope, &fresh).expect("fresh view");
    assert_eq!(fresh_view.len(), 2, "fresh snapshot sees both documents");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn snapshot_documents_rejects_cross_tenant_and_foreign_seams() {
    let (dir, engine) = open_engine("snapshot-view-validation");
    let scope_a = tenant_scope(&engine, "tenant-a");
    let snapshot_a = engine.read_snapshot(&scope_a).expect("snapshot");
    let scope_b = tenant_scope(&engine, "tenant-b");
    assert_eq!(
        engine.snapshot_documents(&scope_b, &snapshot_a),
        Err(EngineError::SnapshotTenantMismatch),
        "a scope naming another tenant is rejected before data access"
    );
    drop(engine);
    let other = reopen_engine(&dir);
    let scope = tenant_scope(&other, "tenant-a");
    assert_eq!(
        other.snapshot_documents(&scope, &snapshot_a),
        Err(EngineError::SnapshotEngineMismatch),
        "a snapshot from the closed instance is rejected"
    );
    drop(other);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// W4 collision actions at the Engine seam
// ---------------------------------------------------------------------------

/// Proves the W4-CA-2 acquisition shape at compile time: a `StreamStore`
/// value exists only inside the borrow region of its `Engine`, so the store
/// and the engine borrow are returned together and shutdown must wait.
fn streams_store_holds_the_engine_borrow<'engine>(
    engine: &'engine Engine,
    store: crate::streaming::StreamStore<'engine>,
) -> (crate::streaming::StreamStore<'engine>, &'engine Engine) {
    (store, engine)
}

#[test]
fn engine_streams_acquisition_path_exists_and_borrows_the_engine() {
    // W4-CA-2: `Engine::streams` exists, takes `&self`, returns a
    // `StreamStore<'_>` borrowed from the Engine, and mirrors the
    // `queries`/`families` acquisition precedents. The signature is asserted
    // by the function-pointer coercion, so the seam cannot regress silently.
    let _: fn(&Engine) -> crate::streaming::StreamStore<'_> = Engine::streams;
    // The returned store holds the engine borrow for as long as it is live.
    let _ = streams_store_holds_the_engine_borrow;
    // Calling the seam is W4-STREAM's red behavior test
    // (`engine_streams_returns_store_bound_to_durable_engine`): the
    // declaration-only `StreamStore::from_engine` constructor this method
    // delegates to stays unimplemented until that packet lands, so this test
    // deliberately proves the acquisition path without invoking it.
}

#[test]
fn publication_events_reach_every_registered_sink() {
    // W4-CA-3: the registry is multi-subscriber in production. Two sinks
    // registered independently both observe the same immutable events, in
    // publication order, from the same commit.
    let (_dir, engine) = open_engine("registry-multi-sink");
    let first: Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>> =
        Arc::new(Mutex::new(Vec::new()));
    let second: Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>> =
        Arc::new(Mutex::new(Vec::new()));
    let first_events = Arc::clone(&first);
    let second_events = Arc::clone(&second);
    let _first_guard = engine.subscribe_publications(Arc::new(
        move |event: &crate::storage::CommitPublicationEvent| {
            first_events.lock().expect("first sink lock").push(event.clone());
        },
    ));
    let _second_guard = engine.subscribe_publications(Arc::new(
        move |event: &crate::storage::CommitPublicationEvent| {
            second_events.lock().expect("second sink lock").push(event.clone());
        },
    ));
    let receipt = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(receipt.sequence().get(), 1);
    for sink in [&first, &second] {
        let recorded = sink.lock().expect("sink lock").clone();
        assert_eq!(recorded.len(), 1, "every registered sink observes the event");
        assert_eq!(recorded[0].sequence(), receipt.sequence());
        assert_eq!(recorded[0].operation_id(), &operation("op-1"));
    }
}

#[test]
fn publication_sink_detaches_when_its_guard_drops() {
    // W4-CA-3: a guard detaches its sink on drop, and only its sink.
    let (_dir, engine) = open_engine("registry-detach");
    let dropped_sink: Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>> =
        Arc::new(Mutex::new(Vec::new()));
    let kept_sink: Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>> =
        Arc::new(Mutex::new(Vec::new()));
    let dropped_events = Arc::clone(&dropped_sink);
    let kept_events = Arc::clone(&kept_sink);
    let detach_guard = engine.subscribe_publications(Arc::new(
        move |event: &crate::storage::CommitPublicationEvent| {
            dropped_events.lock().expect("dropped sink lock").push(event.clone());
        },
    ));
    let _kept_guard = engine.subscribe_publications(Arc::new(
        move |event: &crate::storage::CommitPublicationEvent| {
            kept_events.lock().expect("kept sink lock").push(event.clone());
        },
    ));
    committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(dropped_sink.lock().expect("dropped sink lock").len(), 1);
    drop(detach_guard);
    committed(commit_put(
        &engine,
        "tenant-a",
        "op-2",
        "doc-2",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(
        dropped_sink.lock().expect("dropped sink lock").len(),
        1,
        "a detached sink observes nothing after its guard drops"
    );
    assert_eq!(kept_sink.lock().expect("kept sink lock").len(), 2);
}

#[test]
fn publication_registry_bounds_its_subscribers_without_blocking_the_commit() {
    // W4-CA-3's bound rule: the emit path calls sinks synchronously on the
    // committing thread, so the registry keeps a small subscriber bound, a
    // subscribe attempt beyond it returns a detached guard, and the registry
    // lock is never held while a sink runs — proven by registering a new sink
    // from another thread while a deliberately slow sink is still executing.
    let (_dir, engine) = open_engine("registry-bound");
    let engine = Arc::new(engine);
    let observed: Arc<Mutex<u32>> = Arc::new(Mutex::new(0));
    let counter = Arc::clone(&observed);
    let (sink_entered_tx, sink_entered_rx) = std::sync::mpsc::channel::<()>();
    let (release_sink_tx, release_sink_rx) = std::sync::mpsc::channel::<()>();
    // The channel ends are mutex-wrapped because a publication sink must be
    // `Sync`; the channels only coordinate this test's threads.
    let entered_from_sink = Mutex::new(sink_entered_tx);
    let release_into_sink = Mutex::new(release_sink_rx);
    let _slow_guard = engine.subscribe_publications(Arc::new(
        move |_event: &crate::storage::CommitPublicationEvent| {
            *counter.lock().expect("counter lock") += 1;
            entered_from_sink.lock().expect("entered lock").send(()).ok();
            // Stay inside the sink: the committing thread waits here by
            // design, and the registry must stay operable meanwhile.
            let _ = release_into_sink
                .lock()
                .expect("release lock")
                .recv_timeout(std::time::Duration::from_secs(10));
        },
    ));
    let committer_engine = Arc::clone(&engine);
    let committer = std::thread::spawn(move || {
        committed(commit_put(
            &committer_engine,
            "tenant-a",
            "op-1",
            "doc-1",
            "value",
            ExpectedVersion::New,
            LocalDurability::Synced,
        ))
    });
    sink_entered_rx
        .recv_timeout(std::time::Duration::from_secs(10))
        .expect("the synchronous sink starts on the committing thread");

    // The registry lock is free while the sink runs: a new subscription
    // completes from another thread instead of deadlocking the registry.
    let subscriber_engine = Arc::clone(&engine);
    let late_subscription = std::thread::spawn(move || {
        let guard = subscriber_engine.subscribe_publications(Arc::new(|_event| {}));
        let attached = guard.is_attached();
        drop(guard);
        attached
    })
    .join()
    .expect("subscribing while a sink runs must not deadlock");
    assert!(late_subscription, "the registry admits a subscriber while a sink runs");

    let mut detached_guards = Vec::new();
    for _ in 0..crate::engine::MAX_PUBLICATION_SINKS {
        detached_guards.push(engine.subscribe_publications(Arc::new(|_event| {})));
    }
    let overflow = engine.subscribe_publications(Arc::new(|_event| {}));
    assert!(!overflow.is_attached(), "a sink offered beyond the registry bound is not registered");
    drop(overflow);
    drop(detached_guards);

    release_sink_tx.send(()).ok();
    let receipt = committer.join().expect("the committing thread finishes");
    assert_eq!(receipt.sequence().get(), 1, "the bound never blocks a commit");
    assert_eq!(*observed.lock().expect("counter lock"), 1);
}

#[test]
fn engine_index_catalog_receives_publication_events_in_production() {
    // W4-CA-3 absorbs the queued W3-2 integrator action: the Engine owns an
    // index catalog whose sink is registered at open, so index maintenance is
    // live for real commits without any test-installed sink.
    let (_dir, engine) = open_engine("engine-index-catalog");
    assert!(
        engine.index_catalog().delta().is_none(),
        "a fresh catalog has observed no publication"
    );
    let receipt = committed(commit_put(
        &engine,
        "tenant-a",
        "op-1",
        "doc-1",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let delta = engine.index_catalog().delta().expect("the engine catalog observed the commit");
    assert_eq!(delta.covered_sequence(), receipt.sequence().get());
    committed(commit_put(
        &engine,
        "tenant-a",
        "op-2",
        "doc-2",
        "value",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let advanced = engine.index_catalog().delta().expect("the catalog advances");
    assert_eq!(advanced.covered_sequence(), 2);
}

#[test]
fn engine_publication_history_serves_reconstructable_commits_across_tenants() {
    // W4-CA-4 at the Engine seam: the wrappers serve the journal's
    // reconstructable commits per tenant, map a global cursor to per-tenant
    // progress, and the history survives a reopen of the durable location.
    let (dir, engine) = open_engine("engine-history");
    for (tenant_name, operation_name, id) in [
        ("tenant-a", "op-1", "doc-1"),
        ("tenant-b", "op-2", "doc-1"),
        ("tenant-a", "op-3", "doc-2"),
    ] {
        let scope = tenant_scope(&engine, tenant_name);
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction =
            engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
        transaction.put(document(id, "users", "value"), ExpectedVersion::New).expect("stages");
        let receipt = match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => receipt,
            other => panic!("commit did not succeed: {other:?}"),
        };
        assert!(receipt.sequence().get() <= 3, "the three commits publish in order");
    }
    // The published sequence names the third commit; the journal serves up
    // to it.
    let scope = tenant_scope(&engine, "tenant-a");
    let published = engine.read_snapshot(&scope).expect("snapshot").sequence();
    assert_eq!(published.get(), 3);
    let tenant_a = tenant("tenant-a");
    let tenant_b = tenant("tenant-b");
    assert_eq!(
        engine.committed_tenants().expect("enumerates"),
        vec![tenant_a.clone(), tenant_b.clone()]
    );
    let commits = engine
        .tenant_commits(&tenant_a, crate::types::StorageSequence::new(0), 10)
        .expect("serves the tenant's commits");
    assert_eq!(
        commits.iter().map(|commit| commit.sequence().get()).collect::<Vec<u64>>(),
        [1, 3],
        "one tenant sees only its own commits, in global sequence order"
    );
    assert_eq!(
        engine
            .tenant_sequence(&tenant_b, crate::types::StorageSequence::new(3))
            .expect("maps the cursor"),
        Some(crate::types::StorageSequence::new(2))
    );
    assert_eq!(
        engine.servable_history_start().expect("reports the window"),
        Some(crate::types::StorageSequence::new(1))
    );
    // Repair F2: the journaled high-water mark names the newest journaled
    // sequence, so a consumer can compare it with the published sequence.
    assert_eq!(
        engine.journaled_high_water().expect("reports the mark"),
        Some(crate::types::StorageSequence::new(3))
    );
    drop(engine);

    let reopened = reopen_engine(&dir);
    let reopened_commits = reopened
        .tenant_commits(&tenant_a, crate::types::StorageSequence::new(0), 10)
        .expect("serves after the reopen");
    assert_eq!(reopened_commits, commits, "the journal serves the same records after a reopen");
    assert_eq!(
        reopened.journaled_high_water().expect("reports the mark"),
        Some(crate::types::StorageSequence::new(3)),
        "the durable mark survives the reopen"
    );
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn snapshot_documents_from_serves_bounded_windows_and_validates_the_view() {
    // W4-CA-7: the Engine's bounded windowed read pages exactly the view the
    // full snapshot read materializes, and rejects a foreign snapshot, scope,
    // or tenant with the same typed errors as that read.
    let (dir, engine) = open_engine("engine-window");
    let scope = tenant_scope(&engine, "tenant-a");
    for (operation_name, id, collection) in
        [("op-1", "doc-1", "beta"), ("op-2", "doc-2", "alpha"), ("op-3", "doc-3", "gamma")]
    {
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction =
            engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
        transaction.put(document(id, collection, "value"), ExpectedVersion::Any).expect("stages");
        match transaction.commit(LocalDurability::Written) {
            CommitOutcome::Committed(receipt) => {
                assert!(receipt.sequence().get() <= 3, "three commits publish");
            }
            other => panic!("commit did not succeed: {other:?}"),
        }
    }
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let full = engine.snapshot_documents(&scope, &snapshot).expect("full view");

    let mut paged: Vec<Document> = Vec::new();
    let mut after: Option<DocumentId> = None;
    loop {
        let window =
            engine.snapshot_documents_from(&scope, &snapshot, after, 2).expect("window reads");
        let Some(last) = window.last() else {
            break;
        };
        let last_id = last.id().clone();
        paged.extend(window);
        after = Some(last_id);
    }
    assert_eq!(paged, full, "paging by two reproduces the full canonical view");

    let other_scope = tenant_scope(&engine, "tenant-b");
    assert_eq!(
        engine.snapshot_documents_from(&other_scope, &snapshot, None, 2),
        Err(EngineError::SnapshotTenantMismatch),
        "a scope naming another tenant is rejected before data access"
    );
    let (_, other_engine) = open_engine("engine-window-other");
    let other_engine_scope = tenant_scope(&other_engine, "tenant-a");
    let foreign = other_engine.read_snapshot(&other_engine_scope).expect("snapshot");
    assert_eq!(
        engine.snapshot_documents_from(&scope, &foreign, None, 2),
        Err(EngineError::SnapshotEngineMismatch),
        "a snapshot from another Engine is rejected"
    );
    drop(other_engine);
    drop(engine);
    let reopened = reopen_engine(&dir);
    let reopened_scope = tenant_scope(&reopened, "tenant-a");
    assert_eq!(
        reopened.snapshot_documents_from(&reopened_scope, &snapshot, None, 2),
        Err(EngineError::SnapshotEngineMismatch),
        "a snapshot from the closed instance is rejected"
    );
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn snapshot_records_from_pages_full_keys_and_winning_durability_lsm() {
    let (dir, engine) = open_engine("engine-record-pages");
    assert_snapshot_records_from_pages(&dir, engine);
}

#[test]
fn snapshot_records_from_pages_full_keys_and_winning_durability_root() {
    let (dir, engine) = open_root_engine("engine-record-pages-root");
    assert_snapshot_records_from_pages(&dir, engine);
}

fn assert_snapshot_records_from_pages(dir: &PathBuf, engine: Engine) {
    let scope = tenant_scope(&engine, "tenant-a");
    for (operation_name, id, collection, value, durability, expected) in [
        ("record-1", "same", "alpha", "a1", LocalDurability::Written, ExpectedVersion::New),
        ("record-2", "same", "beta", "b1", LocalDurability::Synced, ExpectedVersion::New),
        ("record-3", "same", "alpha", "a2", LocalDurability::Buffered, ExpectedVersion::Exact(1)),
    ] {
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction =
            engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
        transaction.put(document(id, collection, value), expected).expect("stages");
        assert!(matches!(transaction.commit(durability), CommitOutcome::Committed(_)));
    }
    let boundary = engine.published_sequence().expect("boundary");
    let mut records = Vec::new();
    let mut after = None;
    loop {
        let page = engine
            .snapshot_records_from(tenant("tenant-a"), boundary, after.clone(), 1)
            .expect("bounded page");
        let Some(record) = page.into_iter().next() else {
            break;
        };
        after = Some((record.document().collection().clone(), record.document().id().clone()));
        records.push(record);
    }
    assert_eq!(records.len(), 2, "same identifier in two collections remains lossless");
    assert_eq!(records[0].document().collection().as_str(), "alpha");
    assert_eq!(records[0].document().value(), &DocumentValue::String("a2".to_owned()));
    assert_eq!(records[0].durability(), LocalDurability::Buffered);
    assert_eq!(records[1].document().collection().as_str(), "beta");
    assert_eq!(records[1].durability(), LocalDurability::Synced);
    assert!(matches!(
        engine.snapshot_records_from(tenant("tenant-a"), boundary, after, 1),
        Ok(page) if page.is_empty()
    ));
    assert!(matches!(
        engine.snapshot_records_from(
            tenant("tenant-a"),
            boundary,
            None,
            MAX_SNAPSHOT_RECORD_PAGE + 1
        ),
        Err(EngineError::SnapshotPageLimitExceeded)
    ));

    // A later publication cannot enter a page stream pinned at the earlier
    // boundary.
    committed(commit_put(
        &engine,
        "tenant-a",
        "record-after-boundary",
        "later",
        "later",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let first = engine
        .snapshot_records_from(tenant("tenant-a"), boundary, None, MAX_SNAPSHOT_RECORD_PAGE)
        .expect("old boundary remains readable");
    assert!(first.iter().all(|record| record.document().id().as_str() != "later"));
    drop(engine);
    let reopened = reopen_engine(&dir);
    assert_eq!(
        reopened
            .snapshot_records_from(tenant("tenant-a"), boundary, None, MAX_SNAPSHOT_RECORD_PAGE)
            .expect("boundary survives restart")
            .len(),
        2
    );
    drop(reopened);
    std::fs::remove_dir_all(&dir).ok();
}

/// Seeds the compaction workload through the public transaction seam: two
/// live documents, one deleted document whose tombstone is the tracked
/// evidence, and one trailing put so the delete lands inside the window a
/// history-reclaim pass gives up (the head record itself is always kept,
/// decision record D2).
fn seed_compaction_workload(engine: &Engine) {
    committed(commit_put(
        engine,
        "tenant-a",
        "op-put-1",
        "doc-1",
        "v1",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    committed(commit_put(
        engine,
        "tenant-a",
        "op-put-2",
        "doc-2",
        "v2",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    committed(commit_delete(
        engine,
        "tenant-a",
        "op-delete-1",
        "doc-2",
        ExpectedVersion::Exact(1),
        LocalDurability::Synced,
    ));
    committed(commit_put(
        engine,
        "tenant-a",
        "op-put-3",
        "doc-3",
        "v3",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
}

/// The no-loss shape the compaction workload must keep: both live documents
/// readable, the deleted one absent.
fn assert_compaction_workload_intact(engine: &Engine) {
    assert!(read_document(engine, "tenant-a", "doc-1").is_some(), "doc-1 survives");
    assert!(read_document(engine, "tenant-a", "doc-2").is_none(), "doc-2 stays deleted");
    assert!(read_document(engine, "tenant-a", "doc-3").is_some(), "doc-3 survives");
}

#[test]
fn snapshot_records_fail_closed_when_a_live_winner_is_below_history_floor() {
    let (dir, engine) = open_engine("snapshot-history-floor");
    seed_compaction_workload(&engine);
    let retention = SharedTombstoneRetention::new();
    engine
        .compact_with_retention(
            CompactionRequest::new(CompactionPolicy::ReclaimHistory),
            &retention,
        )
        .expect("history reclaim runs");
    let boundary = StorageSequence::new(4);
    assert_eq!(engine.servable_history_start().expect("history window"), Some(boundary));
    assert_eq!(
        engine.snapshot_records_from(tenant("tenant-a"), StorageSequence::new(3), None, 256),
        Err(EngineError::SnapshotBoundaryUnavailable),
        "a boundary below the history floor is refused before reading state"
    );
    assert_eq!(
        engine.snapshot_records_from(tenant("tenant-a"), boundary, None, 256),
        Err(EngineError::SnapshotBoundaryUnavailable),
        "a live document whose winning put was reclaimed cannot be upgraded or omitted"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// The production retention-driving path with data (GAP-S1 D5):
/// [`Engine::compact_with_retention`] on an engine that actually committed —
/// the declaration half (bound, held facts, non-deletion watermark) and the
/// prologue half (restart re-derivation from the durable store) both run
/// with a real window here.
#[test]
fn compact_with_retention_declares_and_rederives_the_bound_on_a_data_bearing_engine() {
    let (dir, engine) = open_engine("compact-retention-data");
    seed_compaction_workload(&engine);
    let head = StorageSequence::new(4);
    assert_eq!(engine.servable_history_start().expect("window"), Some(StorageSequence::new(1)));

    // First pass: the window [1, 4) is given up after its evidence is
    // durable, and the engine declares the new bound to the watermark from
    // the window it just extracted.
    let retention = SharedTombstoneRetention::new();
    let receipt = engine
        .compact_with_retention(
            CompactionRequest::new(CompactionPolicy::ReclaimHistory),
            &retention,
        )
        .expect("the data-bearing pass runs");
    assert_eq!(receipt.history_reclaim(), HistoryReclaimOutcome::Reclaimed { bound: head });
    assert_eq!(receipt.journaled_head(), Some(head));
    assert_eq!(receipt.evidence_extracted(), 1, "the tombstone was extracted");
    assert_eq!(receipt.evidence_retained(), 1);
    retention.with_lock(|state| {
        assert_eq!(state.history_bound(), Some(head), "the bound is declared");
        assert_eq!(state.held_evidence_count(), 1, "the tombstone is held");
        assert_eq!(
            state.non_deletion_through(&tenant("tenant-a")),
            Some(StorageSequence::new(2)),
            "the window's newest put is the recorded non-deletion fact"
        );
        let evidence = state.deletion_evidence_above(&tenant("tenant-a"), StorageSequence::new(0));
        assert_eq!(evidence.len(), 1);
        assert_eq!(evidence[0].id().as_str(), "doc-2");
        assert_eq!(evidence[0].sequence(), StorageSequence::new(3));
        assert_eq!(evidence[0].operation().as_str(), "op-delete-1");
        assert_eq!(evidence[0].durability(), LocalDurability::Synced);
    });
    assert_compaction_workload_intact(&engine);
    engine.shutdown().expect("shuts down");

    // Restart: the durable prologue alone re-derives a fresh watermark —
    // this second pass is a plain Reclaim, so nothing after the prologue
    // declares anything; every observation below comes from the store.
    let reopened = reopen_engine(&dir);
    assert_compaction_workload_intact(&reopened);
    assert_eq!(reopened.servable_history_start().expect("window"), Some(head));
    let fresh = SharedTombstoneRetention::new();
    let second = reopened
        .compact_with_retention(CompactionRequest::new(CompactionPolicy::Reclaim), &fresh)
        .expect("the second pass runs");
    assert_eq!(second.history_reclaim(), HistoryReclaimOutcome::NotRequested);
    fresh.with_lock(|state| {
        assert_eq!(
            state.history_bound(),
            Some(head),
            "the prologue restored the durably declared bound"
        );
        assert_eq!(state.held_evidence_count(), 1, "the durable fact was held");
        assert_eq!(
            state.non_deletion_through(&tenant("tenant-a")),
            Some(StorageSequence::new(2)),
            "the bound record's non-deletion fact was restored"
        );
        let evidence = state.deletion_evidence_above(&tenant("tenant-a"), StorageSequence::new(0));
        assert_eq!(evidence.len(), 1);
        assert_eq!(evidence[0].id().as_str(), "doc-2");
        assert_eq!(
            evidence[0].stamp(),
            crate::replication::ReplicationClockStamp::new(0, 0),
            "re-derived evidence carries the origin stamp (stamps are process-local, D3)"
        );
    });
    assert_compaction_workload_intact(&reopened);
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

/// The floor the watermark holds is lowered into the pass (GAP-S1 D5): a
/// handle whose floor sits above the tombstone makes the pass skip the
/// below-floor fact durably — a disclosed count, never a silent drop —
/// while the bound is still declared and nothing below-floor is written
/// to the durable store.
#[test]
fn compact_with_retention_lowers_the_watermarks_floor_into_the_pass() {
    let (dir, engine) = open_engine("compact-retention-floor");
    seed_compaction_workload(&engine);
    let retention = SharedTombstoneRetention::new();
    retention
        .with_lock(|state| state.advance_floor(StorageSequence::new(4)).expect("the floor rises"));

    let receipt = engine
        .compact_with_retention(
            CompactionRequest::new(CompactionPolicy::ReclaimHistory),
            &retention,
        )
        .expect("the pass runs");
    assert_eq!(
        receipt.history_reclaim(),
        HistoryReclaimOutcome::Reclaimed { bound: StorageSequence::new(4) }
    );
    assert_eq!(receipt.evidence_extracted(), 1);
    assert_eq!(receipt.evidence_retained(), 0, "the below-floor fact is not written durably");
    assert_eq!(receipt.evidence_below_floor(), 1, "the skip is a disclosed count");
    retention.with_lock(|state| {
        assert_eq!(state.history_bound(), Some(StorageSequence::new(4)));
        assert_eq!(state.held_evidence_count(), 0, "the watermark holds nothing below its floor");
        assert_eq!(
            state.non_deletion_through(&tenant("tenant-a")),
            Some(StorageSequence::new(2)),
            "the non-deletion fact is independent of the floor"
        );
    });
    assert_compaction_workload_intact(&engine);
    engine.shutdown().expect("shuts down");

    // The durable store holds the bound and no below-floor fact: a fresh
    // handle re-derived by the prologue after the restart holds nothing.
    let reopened = reopen_engine(&dir);
    let fresh = SharedTombstoneRetention::new();
    reopened
        .compact_with_retention(CompactionRequest::new(CompactionPolicy::Reclaim), &fresh)
        .expect("the second pass runs");
    fresh.with_lock(|state| {
        assert_eq!(state.history_bound(), Some(StorageSequence::new(4)));
        assert_eq!(state.held_evidence_count(), 0, "no below-floor fact was written durably");
    });
    assert_compaction_workload_intact(&reopened);
    reopened.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}
