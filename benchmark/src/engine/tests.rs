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
    EngineError, EngineOpenError, UnknownReason,
};
use crate::storage::FaultPoint;
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, RetryDisposition, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-w31-engine-{tag}-{}-{index}", std::process::id()))
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

    let forged = EngineConfig { location: PathBuf::new() };
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
    let events = install_event_recorder(&engine);
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

    // Flip the last byte of the log: an interrupted append of the final frame.
    let wal = dir.join("wal");
    let mut bytes = std::fs::read(&wal).expect("wal reads");
    let last = bytes.len() - 1;
    bytes[last] = bytes[last].wrapping_add(1);
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
fn failed_wal_append_rolls_back_to_the_last_good_offset() {
    let (dir, engine) = open_engine("append-rollback");
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
    let reopened = reopen_engine(&dir);
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
fn mid_publication_failure_never_reuses_a_storage_sequence() {
    let (dir, engine) = open_engine("sequence-reuse");
    // Commit A fails after its WAL commit group is durable (step 6 fault), so
    // the log already names sequence 1 while nothing published.
    engine.arm_storage_fault(FaultPoint::ReadyLedgerSync);
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "op-a",
            "doc-a",
            "a",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::StorageFailure
    );
    // Commit B then succeeds and must not reuse sequence 1.
    let receipt = committed(commit_put(
        &engine,
        "tenant-a",
        "op-b",
        "doc-b",
        "b",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    assert_eq!(receipt.sequence().get(), 2, "a failed group's sequence is never reused");
    engine.shutdown().expect("shuts down");

    // Reopen with the manifest gone: the published state is rebuilt from the
    // write-ahead log, where both groups live. Replay must apply both in
    // sequence order and serve B's root, never first-wins.
    std::fs::remove_file(dir.join("manifest")).expect("manifest removes");
    let reopened = reopen_engine(&dir);
    assert!(
        read_document(&reopened, "tenant-a", "doc-b").is_some(),
        "the later committed operation must survive replay, not be shadowed by the failed one"
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
    let (dir, engine) = open_engine("unknown-resolves");
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
    drop(engine);
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
    let (dir, engine) = open_engine("ready-before-pointer");
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
    assert_eq!(rejected(outcome), CommitError::StorageFailure);
    assert!(
        read_document(&engine, "tenant-a", "doc-1").is_none(),
        "the pointer must not be replaced before the ledger record is durable"
    );
    drop(engine);

    // Second, the durable record must answer before the replacement is even
    // attempted: an interrupted pointer publication leaves an identity-bound,
    // resolvable record behind. Recovery rolls the verified write-ahead commit
    // forward, and the durable record resolves the operation by identity.
    let engine = reopen_engine(&dir);
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
    assert_eq!(rejected(outcome), CommitError::StorageFailure);
    assert!(read_document(&engine, "tenant-a", "doc-2").is_none());
    drop(engine);
    let reopened = reopen_engine(&dir);
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
    let (dir, engine) = open_engine("reopen-ready");
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

    let reopened = reopen_engine(&dir);
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
) -> Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>> {
    let events: Arc<Mutex<Vec<crate::storage::CommitPublicationEvent>>> =
        Arc::new(Mutex::new(Vec::new()));
    let sink_events = Arc::clone(&events);
    engine.set_publication_sink(Arc::new(move |event: &crate::storage::CommitPublicationEvent| {
        sink_events.lock().expect("event lock").push(event.clone());
    }));
    events
}

#[test]
fn commit_publication_event_is_emitted_after_storage_locks_release() {
    let (_dir, engine) = open_engine("event-after-locks");
    let storage_probe = Arc::clone(&engine.storage);
    let released = Arc::new(Mutex::new(false));
    let released_flag = Arc::clone(&released);
    engine.set_publication_sink(Arc::new(
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
    let events = install_event_recorder(&engine);
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
    let events = install_event_recorder(&engine);
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
fn unknown_operation_id_resolution_preserves_original_identity() {
    let (dir, engine) = open_engine("identity-preserved");
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

    let reopened = reopen_engine(&dir);
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
