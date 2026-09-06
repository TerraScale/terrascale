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
    CommitError, CommitOutcome, Engine, EngineConfig, EngineConfigError, EngineError,
    EngineOpenError, MAX_SNAPSHOT_RECORD_PAGE, ReplicatedRecord,
};
use crate::storage::FaultPoint;
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, RetryDisposition, TenantId, TenantScope,
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

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn context(origin: &str, sequence: u64) -> Vec<u8> {
    let mut bytes = vec![1];
    bytes.extend_from_slice(&1_u32.to_le_bytes());
    bytes.extend_from_slice(&u32::try_from(origin.len()).unwrap_or(u32::MAX).to_le_bytes());
    bytes.extend_from_slice(origin.as_bytes());
    bytes.extend_from_slice(&sequence.to_le_bytes());
    bytes
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
        UsageRecordMeter::DocumentWriteUnits,
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

fn open_owned_engine(tag: &str) -> (PathBuf, Engine, TenantId) {
    let dir = location(tag);
    let owner = crate::storage::DatabaseOwner::local_development();
    let owner_tenant = tenant(&owner.tenant().to_text());
    let keys = test_storage_keys(&owner, 23);
    let config = EngineConfig::durable(&dir)
        .expect("durable location")
        .with_owner(owner)
        .with_storage_keys(keys);
    let engine = Engine::open(config).expect("owned engine opens");
    engine.activate_origin_epoch(1).unwrap();
    (dir, engine, owner_tenant)
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

#[test]
fn raw_document_body_read_returns_storage_bytes_without_document_materialization() {
    let (dir, engine) = open_engine("raw-body-read");
    let scope = tenant_scope(&engine, "raw-body-tenant");
    let committed = commit_put(
        &engine,
        "raw-body-tenant",
        "raw-body-op",
        "raw-body-doc",
        "opaque",
        ExpectedVersion::New,
        LocalDurability::Written,
    );
    let sequence = match committed {
        CommitOutcome::Committed(receipt) => receipt.sequence(),
        other => panic!("expected commit, got {other:?}"),
    };
    let body = engine
        .raw_document_body_in_collection_at(
            &scope,
            sequence,
            &CollectionName::try_new("users").expect("collection"),
            &document_id("raw-body-doc"),
        )
        .expect("raw body read")
        .expect("body exists");
    assert!(!body.is_empty());
    assert!(body.starts_with(b"{\"$collection\":"));
    assert_eq!(
        engine.raw_document_body_in_collection_at(
            &scope,
            sequence,
            &CollectionName::try_new("users").expect("collection"),
            &document_id("missing"),
        ),
        Ok(None)
    );
    drop(engine);
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn replicated_context_gives_causal_precedence_over_hlc_order() {
    let (dir, engine) = open_engine("replication-context");
    let scope = tenant_scope(&engine, "replication-context-tenant");
    let collection = CollectionName::try_new("users").expect("collection");
    let id = document_id("replicated-context-doc");
    let first = ReplicatedRecord {
        collection: collection.clone(),
        id: id.clone(),
        body: DocumentValue::String("first".into()),
        deleted: false,
        commit_ulid: "context-first".into(),
        hlc: 100,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    engine.apply_replicated(&scope, &first).expect("first apply");
    let second = ReplicatedRecord {
        collection: collection.clone(),
        id: id.clone(),
        body: DocumentValue::String("causally-later".into()),
        deleted: false,
        commit_ulid: "context-second".into(),
        hlc: 1,
        origin: "peer-a".into(),
        origin_sequence: 2,
        context: context("peer-a", 1),
    };
    engine.apply_replicated(&scope, &second).expect("causal apply");
    assert_eq!(
        read_document(&engine, "replication-context-tenant", "replicated-context-doc")
            .expect("document")
            .value(),
        &DocumentValue::String("causally-later".into())
    );
    drop(engine);
    std::fs::remove_dir_all(dir).ok();
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

#[test]
fn host_clock_offset_bounds_origin_hlc_minting_and_recovers() {
    let (dir, engine) = open_engine("clock-safety");

    // The hard boundary is inclusive, and a negative offset is measured by
    // magnitude rather than direction.
    engine.set_clock_offset_seconds(0.5);
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "clock-boundary",
            "boundary",
            "kept",
            ExpectedVersion::New,
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));

    engine.set_clock_offset_seconds(-0.500_001);
    let before_clock = *engine.replication_clock.lock().expect("clock is not poisoned");
    let before_sequence = engine
        .read_snapshot(&tenant_scope(&engine, "tenant-a"))
        .expect("snapshot remains readable")
        .sequence();
    assert_eq!(
        rejected(commit_put(
            &engine,
            "tenant-a",
            "clock-refused-negative",
            "refused",
            "not-written",
            ExpectedVersion::New,
            LocalDurability::Synced,
        )),
        CommitError::Validation,
        "clock refusal preserves the frozen commit-error vocabulary"
    );
    assert_eq!(
        *engine.replication_clock.lock().expect("clock is not poisoned"),
        before_clock,
        "a refused write does not mint or advance an HLC"
    );
    assert_eq!(
        engine
            .read_snapshot(&tenant_scope(&engine, "tenant-a"))
            .expect("reads remain available")
            .sequence(),
        before_sequence,
        "a refused write does not publish storage"
    );
    assert_eq!(
        read_document(&engine, "tenant-a", "boundary").map(|doc| doc.value().clone()),
        Some(DocumentValue::String("kept".into()))
    );
    assert_eq!(read_document(&engine, "tenant-a", "refused"), None);

    // Unknown/stale input is explicit and fail-closed; a fresh safe sample
    // reopens origin writes and records only the corresponding refusal.
    engine.clear_clock_offset_seconds();
    assert_eq!(engine.clock_offset_seconds(), None);
    assert_eq!(engine.metrics().snapshot().clock_offset_seconds(), None);
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "clock-refused-unknown",
            "unknown",
            "not-written",
            ExpectedVersion::New,
            LocalDurability::Synced,
        ),
        CommitOutcome::Rejected(CommitError::Validation)
    ));
    engine.set_clock_offset_seconds(-0.499_999);
    assert!(matches!(
        commit_put(
            &engine,
            "tenant-a",
            "clock-recovered",
            "recovered",
            "written",
            ExpectedVersion::New,
            LocalDurability::Synced,
        ),
        CommitOutcome::Committed(_)
    ));

    let metrics = engine.metrics().snapshot();
    assert_eq!(metrics.clock_offset_seconds(), Some(-0.499_999));
    assert_eq!(metrics.clock_write_refusals(), 2);
    drop(engine);
    std::fs::remove_dir_all(dir).ok();
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
    std::fs::write(foreign.join("unrelated"), b"foreign bytes").expect("foreign file");
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
fn production_owner_rejects_forged_tenant_without_side_effects() {
    let (dir, engine, owner_tenant) = open_owned_engine("owner-scope");
    let owner_scope = TenantScope::from_parts(owner_tenant.clone(), engine.identity());
    let snapshot = engine.read_snapshot(&owner_scope).expect("owner snapshot");
    let mut transaction =
        engine.begin(&owner_scope, snapshot, operation("owner-write")).expect("begins");
    transaction.put(document("owner-doc", "users", "kept"), ExpectedVersion::New).expect("stages");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    let forged_scope = tenant_scope(&engine, "forged-tenant");
    let before_sequence = engine.published_sequence().expect("sequence");
    let before_metrics = engine.metrics().snapshot();
    let before_documents = engine
        .snapshot_documents(&owner_scope, &engine.read_snapshot(&owner_scope).expect("snapshot"))
        .expect("documents");
    let before_read_count = engine.metering_read_count(&owner_scope);
    let boundary = before_sequence;

    assert_eq!(engine.read_snapshot(&forged_scope), Err(EngineError::ScopeMismatch));
    assert_eq!(
        engine
            .begin(
                &forged_scope,
                engine.read_snapshot(&owner_scope).expect("owner snapshot"),
                operation("forged-begin"),
            )
            .err(),
        Some(EngineError::ScopeMismatch)
    );
    assert_eq!(
        engine.record_usage(&forged_scope, usage_record_request("forged-fact", 1_000)),
        Err(UsageCommandError::ScopeMismatch)
    );
    assert_eq!(
        engine.record_usage(&owner_scope, usage_record_request("wrong-owner-fact", 1_001)),
        Err(UsageCommandError::InvalidRequest),
        "usage facts must bind their Project and Database dimensions too"
    );
    assert_eq!(
        engine.usage_page(&forged_scope, UsagePageRequest::try_new(None, 1, None).unwrap()),
        Err(UsageCommandError::ScopeMismatch)
    );
    assert_eq!(engine.metering_fact(&forged_scope, "forged-tenant"), None);
    assert_eq!(engine.metering_facts_page(&forged_scope, None, 1), (Vec::new(), None));
    assert_eq!(engine.metering_live_bytes(&forged_scope), 0);
    assert_eq!(engine.metering_read_count(&forged_scope), 0);
    let replicated = crate::engine::ReplicatedRecord {
        collection: CollectionName::try_new("users").unwrap(),
        id: document_id("forged-doc"),
        body: DocumentValue::String("forged".into()),
        deleted: false,
        commit_ulid: "forged-replication".into(),
        hlc: 1,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    assert_eq!(
        engine.apply_replicated(&forged_scope, &replicated),
        Err(EngineError::ScopeMismatch)
    );
    assert_eq!(
        engine.snapshot_records_from(forged_scope.tenant().clone(), boundary, None, 1),
        Err(EngineError::ScopeMismatch)
    );
    assert_eq!(
        engine.tenant_commits(
            &forged_scope.tenant().clone(),
            crate::types::StorageSequence::new(0),
            1
        ),
        Err(EngineError::ScopeMismatch)
    );
    assert_eq!(
        engine.tenant_sequence(&forged_scope.tenant().clone(), boundary),
        Err(EngineError::ScopeMismatch)
    );

    assert_eq!(engine.metering_read_count(&owner_scope), before_read_count);
    assert_eq!(engine.published_sequence().expect("sequence unchanged"), before_sequence);
    assert_eq!(
        engine
            .snapshot_documents(
                &owner_scope,
                &engine.read_snapshot(&owner_scope).expect("snapshot")
            )
            .expect("documents unchanged"),
        before_documents
    );
    assert_eq!(engine.metrics().snapshot(), before_metrics);
    drop(engine);
    std::fs::remove_dir_all(dir).ok();
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
fn snapshot_records_from_pages_full_keys_and_winning_durability_sqlite() {
    let (dir, engine) = open_engine("engine-record-pages");
    assert_snapshot_records_from_pages(&dir, engine);
}

#[test]
fn snapshot_records_from_pages_full_keys_and_winning_durability_root() {
    let (dir, engine) = open_engine("engine-record-pages-root");
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
    let reopened = reopen_engine(dir);
    assert_eq!(
        reopened
            .snapshot_records_from(tenant("tenant-a"), boundary, None, MAX_SNAPSHOT_RECORD_PAGE)
            .expect("boundary survives restart")
            .len(),
        2
    );
    drop(reopened);
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn configured_replication_origin_and_clock_survive_reopen() {
    #[derive(Debug)]
    struct FixedClock;
    impl super::ReplicationClock for FixedClock {
        fn now_millis(&self) -> u64 {
            1 << 47
        }
    }
    let dir = location("configured-replication-clock");
    let config = EngineConfig::durable(&dir)
        .unwrap()
        .with_replication_origin(crate::types::RegionId::try_new("gru").unwrap())
        .with_replication_clock(Arc::new(FixedClock));
    let engine = Engine::open(config.clone()).unwrap();
    committed(commit_put(
        &engine,
        "tenant-a",
        "clock-1",
        "doc",
        "one",
        ExpectedVersion::New,
        LocalDurability::Synced,
    ));
    let scope = tenant_scope(&engine, "tenant-a");
    let first = engine
        .streams()
        .replay_sync_journal(&scope, None, 1, &crate::types::Cancellation::default())
        .unwrap();
    let first = first.records()[0].provenance();
    assert_eq!(first.origin(), "gru");
    assert_eq!(first.origin_sequence(), 1);
    let first_hlc = first.hlc();
    assert_eq!(first_hlc >> 16, 1 << 47);
    engine.shutdown().unwrap();
    let engine = Engine::open(config.clone()).unwrap();
    committed(commit_put(
        &engine,
        "tenant-a",
        "clock-2",
        "doc",
        "two",
        ExpectedVersion::Any,
        LocalDurability::Synced,
    ));
    let scope = tenant_scope(&engine, "tenant-a");
    let records = engine
        .streams()
        .replay_sync_journal(&scope, None, 2, &crate::types::Cancellation::default())
        .unwrap();
    let second = records.records()[1].provenance();
    assert_eq!(second.origin_sequence(), 2);
    assert!(second.hlc() > first_hlc);
    engine.shutdown().unwrap();
    assert!(
        Engine::open(
            config.with_replication_origin(crate::types::RegionId::try_new("sfo").unwrap())
        )
        .is_err()
    );
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn losing_merge_preserves_transactional_field_metadata() {
    let (dir, engine) = open_engine("losing-merge-metadata");
    let scope = tenant_scope(&engine, "tenant-a");
    let collection = CollectionName::try_new("users").expect("collection");
    let id = document_id("counter");
    let schema = crate::storage::MergeSchema::try_new(std::collections::BTreeMap::from([(
        "count".to_owned(),
        crate::storage::MergeFieldKind::PnCounter,
    )]))
    .expect("schema");
    engine.declare_merge_schema(&scope, &collection, &schema).expect("declare");
    let first = ReplicatedRecord {
        collection: collection.clone(),
        id: id.clone(),
        body: DocumentValue::Object(std::collections::BTreeMap::from([(
            "count".to_owned(),
            DocumentValue::Integer(7),
        )])),
        deleted: false,
        commit_ulid: "first-counter".into(),
        hlc: 100,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    engine.apply_replicated(&scope, &first).expect("first apply");
    let kernels = engine.merge_kernels_for(&scope, &collection, &id).expect("metadata");
    assert_eq!(kernels.len(), 1);
    let losing = ReplicatedRecord {
        body: DocumentValue::Object(std::collections::BTreeMap::from([(
            "count".to_owned(),
            DocumentValue::Integer(5),
        )])),
        commit_ulid: "losing-counter".into(),
        hlc: 200,
        origin: "peer-b".into(),
        ..first
    };
    engine.apply_replicated(&scope, &losing).expect("losing apply");
    assert_eq!(
        engine.merge_kernels_for(&scope, &collection, &id).expect("unchanged metadata"),
        kernels
    );
    assert_eq!(
        engine.document_replication_stamp(&scope, &collection, &id).expect("stamp"),
        Some((100, "peer-a".into(), false))
    );
    let conflicts = engine.merge_conflicts_for(&scope, &collection, &id, 10).expect("conflicts");
    assert_eq!(conflicts.len(), 1);
    assert_eq!(conflicts[0].policy, "pn-counter-max");
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn conflict_history_failure_rolls_back_document_and_retry_recovers_after_restart() {
    let (dir, engine) = open_engine("conflict-history-atomicity");
    let scope = tenant_scope(&engine, "tenant-a");
    let collection = CollectionName::try_new("users").expect("collection");
    let id = document_id("conflict");
    let value = |number| {
        DocumentValue::Object(std::collections::BTreeMap::from([(
            "value".to_owned(),
            DocumentValue::Integer(number),
        )]))
    };
    let first = ReplicatedRecord {
        collection: collection.clone(),
        id: id.clone(),
        body: value(1),
        deleted: false,
        commit_ulid: "conflict-first".into(),
        hlc: 100,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    engine.apply_replicated(&scope, &first).expect("first apply");
    let incoming = ReplicatedRecord {
        body: value(2),
        commit_ulid: "conflict-second".into(),
        hlc: 200,
        origin: "peer-b".into(),
        ..first
    };
    let before = engine.published_sequence().expect("sequence");
    let sql = rusqlite::Connection::open(dir.join("terrabase.sqlite3")).expect("fault connection");
    sql.execute_batch(
        "CREATE TRIGGER reject_conflict BEFORE INSERT ON merge_conflicts
        BEGIN SELECT RAISE(ABORT, 'injected history failure'); END;",
    )
    .expect("install fault");
    let losing = ReplicatedRecord {
        body: value(0),
        hlc: 50,
        commit_ulid: "conflict-loser".into(),
        ..incoming.clone()
    };
    assert_eq!(engine.apply_replicated(&scope, &losing), Err(EngineError::StorageFailure));
    assert_eq!(engine.apply_replicated(&scope, &incoming), Err(EngineError::StorageFailure));
    assert_eq!(engine.published_sequence().expect("unchanged sequence"), before);
    assert_eq!(
        read_document(&engine, "tenant-a", "conflict").expect("old document").value(),
        &value(1)
    );
    assert!(
        engine
            .merge_conflicts_for(&scope, &collection, &id, 10)
            .expect("no partial history")
            .is_empty()
    );
    let operation = super::replication_operation_id(&incoming).expect("identity");
    assert_eq!(
        engine
            .lock_storage()
            .expect("storage")
            .port
            .replication_fingerprint(scope.tenant(), &operation)
            .expect("binding read"),
        None,
        "a failed publication must not retain a source binding"
    );
    engine.shutdown().expect("shutdown");
    sql.execute_batch("DROP TRIGGER reject_conflict").expect("remove fault");
    drop(sql);
    let engine = Engine::open(EngineConfig::durable(&dir).expect("config")).expect("reopen");
    let scope = tenant_scope(&engine, "tenant-a");
    assert_eq!(
        read_document(&engine, "tenant-a", "conflict").expect("durable old document").value(),
        &value(1)
    );
    engine.apply_replicated(&scope, &incoming).expect("retry publishes document and history");
    let history = engine.merge_conflicts_for(&scope, &collection, &id, 10).expect("history");
    assert_eq!(history.len(), 1);
    assert_eq!(history[0].loser_commit_ulid, "conflict-first");
    assert_eq!(
        read_document(&engine, "tenant-a", "conflict").expect("new document").value(),
        &value(2)
    );
    engine.apply_replicated(&scope, &incoming).expect("idempotent retry");
    assert_eq!(
        engine.merge_conflicts_for(&scope, &collection, &id, 10).expect("unchanged history"),
        history
    );
    engine.shutdown().expect("close");
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn stale_replicated_merge_rejects_without_binding_identity_and_recomputes_on_retry() {
    let (dir, engine) = open_engine("stale-merge-snapshot");
    let scope = tenant_scope(&engine, "tenant-a");
    let collection = CollectionName::try_new("users").expect("collection");
    let id = document_id("counter");
    let schema = crate::storage::MergeSchema::try_new(std::collections::BTreeMap::from([
        ("count".to_owned(), crate::storage::MergeFieldKind::PnCounter),
        ("new".to_owned(), crate::storage::MergeFieldKind::PnCounter),
    ]))
    .expect("schema");
    engine.declare_merge_schema(&scope, &collection, &schema).expect("declare");
    let object = |fields: &[(&str, i64)]| {
        DocumentValue::Object(
            fields
                .iter()
                .map(|(name, value)| ((*name).to_owned(), DocumentValue::Integer(*value)))
                .collect(),
        )
    };
    let first = ReplicatedRecord {
        collection: collection.clone(),
        id: id.clone(),
        body: object(&[("count", 7)]),
        deleted: false,
        commit_ulid: "snapshot-first".into(),
        hlc: 100,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    engine.apply_replicated(&scope, &first).expect("first apply");
    let snapshot = engine.read_snapshot(&scope).expect("merge input snapshot");
    let incoming = ReplicatedRecord {
        body: object(&[("count", 9)]),
        commit_ulid: "snapshot-incoming".into(),
        hlc: 200,
        origin: "peer-b".into(),
        ..first.clone()
    };
    let intervening = ReplicatedRecord {
        body: object(&[("count", 7), ("new", 11)]),
        commit_ulid: "snapshot-intervening".into(),
        hlc: 300,
        origin: "peer-c".into(),
        context: context("peer-a", 1),
        ..first
    };
    engine.apply_replicated(&scope, &intervening).expect("intervening write");
    let operation = super::replication_operation_id(&incoming).expect("replication identity");
    assert_eq!(
        engine.apply_replicated_clean(&scope, &incoming, &operation, snapshot),
        Err(EngineError::Conflict)
    );
    assert_eq!(
        engine.lock_storage().expect("storage").port.resolve_operation(scope.tenant(), &operation),
        None
    );
    engine.apply_replicated(&scope, &incoming).expect("retry recomputes the merge");
    assert_eq!(
        read_document(&engine, "tenant-a", "counter").expect("document").value(),
        &object(&[("count", 9), ("new", 11)])
    );
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn replicated_source_content_is_bound_across_merge_and_restart() {
    let (dir, engine) = open_engine("replication-source-binding");
    let scope = tenant_scope(&engine, "tenant-a");
    let collection = CollectionName::try_new("users").expect("collection");
    let id = document_id("bound");
    let object = |fields: &[(&str, i64)]| {
        DocumentValue::Object(
            fields
                .iter()
                .map(|(name, value)| ((*name).to_owned(), DocumentValue::Integer(*value)))
                .collect(),
        )
    };
    let schema = crate::storage::MergeSchema::try_new(std::collections::BTreeMap::from([
        ("count".into(), crate::storage::MergeFieldKind::PnCounter),
        ("new".into(), crate::storage::MergeFieldKind::PnCounter),
    ]))
    .expect("schema");
    engine.declare_merge_schema(&scope, &collection, &schema).expect("declare");
    let first = ReplicatedRecord {
        collection: collection.clone(),
        id: id.clone(),
        body: object(&[("count", 7)]),
        deleted: false,
        commit_ulid: "bound-first".into(),
        hlc: 100,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    engine.apply_replicated(&scope, &first).expect("seed");
    let incoming = ReplicatedRecord {
        body: object(&[("count", 5), ("new", 11)]),
        commit_ulid: "bound-incoming".into(),
        hlc: 200,
        origin: "peer-b".into(),
        ..first
    };
    let applied = engine.apply_replicated(&scope, &incoming).expect("merged publication");
    assert!(!applied.replayed());
    assert_eq!(
        read_document(&engine, "tenant-a", "bound").expect("merged").value(),
        &object(&[("count", 7), ("new", 11)])
    );
    engine.shutdown().expect("shutdown");
    let engine = Engine::open(EngineConfig::durable(&dir).expect("config")).expect("reopen");
    let scope = tenant_scope(&engine, "tenant-a");
    assert!(engine.apply_replicated(&scope, &incoming).expect("original replay").replayed());
    let operation = super::replication_operation_id(&incoming).expect("identity");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    assert!(
        engine
            .apply_replicated_clean(&scope, &incoming, &operation, snapshot)
            .expect("commit-time replay uses source fingerprint, not merged bytes")
            .replayed()
    );
    let before = engine.published_sequence().expect("sequence");
    let history = engine.merge_conflicts_for(&scope, &collection, &id, 10).expect("history");
    let variants = [
        ReplicatedRecord { body: object(&[("count", 99)]), ..incoming.clone() },
        ReplicatedRecord { hlc: 300, ..incoming.clone() },
        ReplicatedRecord { origin_sequence: 2, ..incoming.clone() },
        ReplicatedRecord { context: context("peer-c", 1), ..incoming.clone() },
        ReplicatedRecord { deleted: true, ..incoming.clone() },
    ];
    for changed in variants {
        assert!(
            matches!(
                engine.apply_replicated(&scope, &changed),
                Err(EngineError::OperationIdReuse { .. })
            ),
            "changed source content must not replay"
        );
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        assert!(
            matches!(
                engine.apply_replicated_clean(&scope, &changed, &operation, snapshot),
                Err(EngineError::OperationIdReuse { .. })
            ),
            "the transaction must also reject changed source content"
        );
    }
    assert_eq!(engine.published_sequence().expect("unchanged"), before);
    assert_eq!(engine.merge_conflicts_for(&scope, &collection, &id, 10).expect("history"), history);
    assert!(engine.apply_replicated(&scope, &incoming).expect("still replays").replayed());
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn first_replication_rejects_a_causal_context_covering_its_own_dot() {
    let (dir, engine) = open_engine("replication-invalid-first-context");
    let scope = tenant_scope(&engine, "tenant-a");
    let record = ReplicatedRecord {
        collection: CollectionName::try_new("users").expect("collection"),
        id: document_id("invalid"),
        body: DocumentValue::Integer(1),
        deleted: false,
        commit_ulid: "invalid-causal-source".into(),
        hlc: 100,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: context("peer-a", 1),
    };
    let before = engine.published_sequence().expect("sequence");
    assert_eq!(engine.apply_replicated(&scope, &record), Err(EngineError::InvalidDocument));
    assert_eq!(engine.published_sequence().expect("unchanged"), before);
    assert!(read_document(&engine, "tenant-a", "invalid").is_none());
    let operation = super::replication_operation_id(&record).expect("operation");
    assert_eq!(
        engine
            .lock_storage()
            .expect("storage")
            .port
            .replication_fingerprint(scope.tenant(), &operation)
            .expect("binding"),
        None
    );
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn losing_replication_replays_after_restart_and_later_document_changes() {
    let (dir, engine) = open_engine("superseded-replay");
    let scope = tenant_scope(&engine, "tenant-a");
    let first = ReplicatedRecord {
        collection: CollectionName::try_new("users").expect("collection"),
        id: document_id("loser"),
        body: DocumentValue::Integer(10),
        deleted: false,
        commit_ulid: "superseded-first".into(),
        hlc: 300,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    engine.apply_replicated(&scope, &first).expect("seed");
    let losing = ReplicatedRecord {
        body: DocumentValue::Integer(5),
        commit_ulid: "superseded-loser".into(),
        hlc: 200,
        origin: "peer-b".into(),
        ..first.clone()
    };
    let before = engine.published_sequence().expect("sequence");
    let result = engine.apply_replicated(&scope, &losing).expect("loss");
    assert!(result.superseded());
    assert!(!result.replayed());
    assert!(result.receipt().is_none());
    assert_eq!(engine.published_sequence().expect("sequence"), before);
    let later = ReplicatedRecord {
        body: DocumentValue::Integer(1),
        commit_ulid: "superseded-later".into(),
        hlc: 100,
        origin: "peer-c".into(),
        context: context("peer-a", 1),
        ..first
    };
    engine.apply_replicated(&scope, &later).expect("causally later value");
    engine.shutdown().expect("shutdown");
    let engine = Engine::open(EngineConfig::durable(&dir).expect("config")).expect("reopen");
    let scope = tenant_scope(&engine, "tenant-a");
    let before = engine.published_sequence().expect("sequence");
    let history =
        engine.merge_conflicts_for(&scope, &losing.collection, &losing.id, 10).expect("history");
    assert_eq!(history.len(), 1);
    let replay = engine.apply_replicated(&scope, &losing).expect("durable loss");
    assert!(replay.superseded());
    assert!(replay.replayed());
    assert_eq!(engine.published_sequence().expect("sequence"), before);
    assert_eq!(
        read_document(&engine, "tenant-a", "loser").expect("preserved").value(),
        &DocumentValue::Integer(1)
    );
    assert_eq!(
        engine.merge_conflicts_for(&scope, &losing.collection, &losing.id, 10).expect("history"),
        history
    );
    let operation = super::replication_operation_id(&losing).expect("operation");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    assert_eq!(
        engine.apply_replicated_clean(&scope, &losing, &operation, snapshot),
        Err(EngineError::Conflict),
        "a commit racing the stored loss must retry instead of publishing"
    );
    let changed = ReplicatedRecord { body: DocumentValue::Integer(99), ..losing };
    assert!(matches!(
        engine.apply_replicated(&scope, &changed),
        Err(EngineError::OperationIdReuse { .. })
    ));
    engine.shutdown().expect("shutdown");
    let sql =
        rusqlite::Connection::open(dir.join("terrabase.sqlite3")).expect("corruption connection");
    sql.execute("DELETE FROM replication_bindings WHERE operation = ?1", [operation.as_str()])
        .expect("remove binding");
    drop(sql);
    assert!(
        Engine::open(EngineConfig::durable(&dir).expect("config")).is_err(),
        "a durable loss without its source fingerprint must refuse startup"
    );
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn stale_losing_decision_does_not_bind_the_source_and_retry_can_win() {
    let (dir, engine) = open_engine("superseded-stale");
    let scope = tenant_scope(&engine, "tenant-a");
    let first = ReplicatedRecord {
        collection: CollectionName::try_new("users").expect("collection"),
        id: document_id("stale-loser"),
        body: DocumentValue::Integer(10),
        deleted: false,
        commit_ulid: "stale-loss-first".into(),
        hlc: 300,
        origin: "peer-a".into(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    engine.apply_replicated(&scope, &first).expect("seed");
    let snapshot = engine.read_snapshot(&scope).expect("merge snapshot");
    let incoming = ReplicatedRecord {
        body: DocumentValue::Integer(5),
        commit_ulid: "stale-loss-incoming".into(),
        hlc: 200,
        origin: "peer-b".into(),
        ..first.clone()
    };
    let later = ReplicatedRecord {
        body: DocumentValue::Integer(1),
        commit_ulid: "stale-loss-later".into(),
        hlc: 100,
        origin: "peer-c".into(),
        context: context("peer-a", 1),
        ..first
    };
    engine.apply_replicated(&scope, &later).expect("intervening publication");
    assert_eq!(
        engine.record_superseded_replication(&scope, &incoming, &snapshot, None),
        Err(EngineError::Conflict)
    );
    let operation = super::replication_operation_id(&incoming).expect("operation");
    assert_eq!(
        engine
            .lock_storage()
            .expect("storage")
            .port
            .replication_fingerprint(scope.tenant(), &operation)
            .expect("binding"),
        None
    );
    let result = engine.apply_replicated(&scope, &incoming).expect("recomputed apply");
    assert!(!result.superseded());
    assert_eq!(
        read_document(&engine, "tenant-a", "stale-loser").expect("winner").value(),
        &DocumentValue::Integer(5)
    );
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(dir).expect("cleanup");
}

#[test]
fn index_control_generation_has_one_concurrent_winner_and_survives_restart() {
    let (dir, engine) = open_engine("index-generation-race");
    let engine = Arc::new(engine);
    let barrier = Arc::new(std::sync::Barrier::new(17));
    let threads: Vec<_> = (0..16)
        .map(|id| {
            let engine = Arc::clone(&engine);
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || {
                barrier.wait();
                engine.auto_index_forbid_shape(
                    &tenant("tenant-a"),
                    &format!("candidate-{id}"),
                    "score",
                    0,
                )
            })
        })
        .collect();
    barrier.wait();
    let mut winners = 0;
    for thread in threads {
        match thread.join().unwrap() {
            Ok(generation) => {
                assert_eq!(generation, 1);
                winners += 1;
            }
            Err(EngineError::Conflict) => {}
            result => panic!("unexpected control outcome: {result:?}"),
        }
    }
    assert_eq!(winners, 1);
    let owner = tenant("tenant-a");
    let audit = engine.auto_index_lifecycle_audit(&owner, None, None, 100).unwrap();
    assert_eq!(audit.len(), 1);
    assert_eq!(audit[0].generation, 1);
    drop(engine);
    let reopened = reopen_engine(&dir);
    assert_eq!(reopened.auto_index_lifecycle_generation(&owner).unwrap(), 1);
    assert_eq!(reopened.auto_index_lifecycle_audit(&owner, None, None, 100).unwrap(), audit);
    assert!(matches!(
        reopened.auto_index_forbid_shape(&owner, "next", "score", 0),
        Err(EngineError::Conflict)
    ));
    assert_eq!(reopened.auto_index_forbid_shape(&owner, "next", "score", 1).unwrap(), 2);
    drop(reopened);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn invisible_index_build_lifecycle_restarts_without_exposing_the_candidate() {
    use crate::auto_index::{
        LifecyclePhase, LogicalCost, Observation, Predicate, PredicateKind, Template,
    };
    use crate::storage::{InvisibleColumnKind, InvisibleIndexColumn};
    let (dir, engine) = open_engine("invisible-lifecycle");
    let owner = tenant("tenant-a");
    let scope = engine.scope_for(&owner);
    let collection = CollectionName::try_new("users").unwrap();
    let mut transaction =
        engine.begin(&scope, engine.read_snapshot(&scope).unwrap(), operation("initial")).unwrap();
    for id in ["a", "b"] {
        transaction
            .put(
                Document::new(
                    document_id(id),
                    collection.clone(),
                    DocumentValue::Object(std::collections::BTreeMap::from([(
                        "score".into(),
                        DocumentValue::Integer(1),
                    )])),
                ),
                ExpectedVersion::New,
            )
            .unwrap();
    }
    assert!(matches!(transaction.commit(LocalDurability::Written), CommitOutcome::Committed(_)));
    engine.record_auto_index_observation(Observation {
        tenant: owner.as_str().into(),
        template: Template {
            collection: "users".into(),
            predicates: vec![Predicate { field: "score".into(), kind: PredicateKind::Equality }],
            ordering: vec!["score".into()],
            projection: None,
        },
        cost: LogicalCost::new(10, 10),
        rows_returned: 2,
        index: None,
        at_millis: 1,
    });
    let recommended = engine.auto_index_recommendations(&owner, None, 10).unwrap();
    let id = &recommended.recommendations[0].candidate_id;
    let columns =
        [InvisibleIndexColumn { field: "score".into(), kind: InvisibleColumnKind::Numeric }];
    engine.auto_index_apply(&owner, id, 0, 1).unwrap();
    assert!(
        engine.begin_index_build(&owner, id, &columns, 1, 2).is_err(),
        "missing extraction must refuse build"
    );
    assert_eq!(engine.auto_index_lifecycle_generation(&owner).unwrap(), 1);
    engine.register_index_field(&collection, "score").unwrap();
    let building = engine.begin_index_build(&owner, id, &columns, 1, 2).unwrap();
    assert_eq!(building.phase, LifecyclePhase::Building);
    assert_eq!(building.generation, 2);
    assert!(!building.visible);
    let partial = engine.advance_index_build(&owner, id, 1, 2, 3).unwrap();
    assert_eq!(partial.phase, LifecyclePhase::Building);
    assert_eq!(partial.covered_watermark, 0);
    let filter = crate::storage::ScanFilter::try_new(vec![crate::storage::ScanCondition {
        field: "score".into(),
        op: crate::storage::ScanOp::Equals,
        value: DocumentValue::Integer(1),
    }])
    .unwrap();
    let request = crate::storage::ShadowReadRequest {
        collection: &collection,
        filter: &filter,
        candidate_field: "score",
        hidden_candidate: Some(id),
        after: None,
        limit: 10,
        max_vm_steps: 10_000,
    };
    assert_eq!(
        engine.measure_index_shadow(&scope, &engine.read_snapshot(&scope).unwrap(), &request),
        Err(crate::storage::ShadowReadError::InvalidRequest)
    );
    drop(engine);
    let engine = reopen_engine(&dir);
    assert_eq!(
        engine.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap().indexes,
        vec![partial]
    );
    let shadowing = engine.advance_index_build(&owner, id, 1, 3, 4).unwrap();
    assert_eq!(shadowing.phase, LifecyclePhase::Shadowing);
    assert_eq!(Some(shadowing.covered_watermark), shadowing.target_watermark);
    assert!(!shadowing.visible && !shadowing.zero_stale_reads && !shadowing.regression_free);
    assert_eq!(shadowing.generation, 4);
    assert_hidden_shadow_scope(&engine, &owner, &request);
    assert!(engine.auto_index_shadow_evidence(&owner, id, [7; 32], 20).unwrap().is_none());
    assert!(matches!(engine.advance_index_build(&owner, id, 1, 4, 5), Err(EngineError::Conflict)));
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
}

fn assert_hidden_shadow_scope(
    engine: &Engine,
    owner: &TenantId,
    request: &crate::storage::ShadowReadRequest<'_>,
) {
    let scope = engine.scope_for(owner);
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let comparison = engine.measure_index_shadow(&scope, &snapshot, request).unwrap();
    assert_eq!(comparison.ordinary.rows, 2);
    assert_eq!(comparison.ordinary.result_digest, comparison.candidate.result_digest);
    let foreign_scope = engine.scope_for(&tenant("foreign"));
    assert_eq!(
        engine.measure_index_shadow(
            &foreign_scope,
            &engine.read_snapshot(&foreign_scope).unwrap(),
            request
        ),
        Err(crate::storage::ShadowReadError::InvalidRequest)
    );
}

#[test]
fn index_shadow_measurements_validate_scope_and_do_not_authorize_activation() {
    use crate::storage::{ScanCondition, ScanFilter, ScanOp, ShadowReadError, ShadowReadRequest};
    let (dir, engine) = open_engine("index-shadow-scope");
    let owner = tenant("tenant-a");
    let scope = engine.scope_for(&owner);
    let collection = CollectionName::try_new("orders").unwrap();
    engine.register_index_field(&collection, "score").unwrap();
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let filter = ScanFilter::try_new(vec![ScanCondition {
        field: "score".into(),
        op: ScanOp::Equals,
        value: DocumentValue::Integer(1),
    }])
    .unwrap();
    let request = ShadowReadRequest {
        collection: &collection,
        filter: &filter,
        candidate_field: "score",
        hidden_candidate: None,
        after: None,
        limit: 10,
        max_vm_steps: 10_000,
    };
    let comparison = engine.measure_index_shadow(&scope, &snapshot, &request).unwrap();
    assert_eq!(comparison.ordinary.rows, 0);
    assert_eq!(comparison.ordinary.result_digest, comparison.candidate.result_digest);
    let foreign_scope = engine.scope_for(&tenant("tenant-b"));
    assert_eq!(
        engine.measure_index_shadow(&foreign_scope, &snapshot, &request),
        Err(ShadowReadError::ScopeMismatch)
    );
    let (foreign_dir, foreign_engine) = open_engine("index-shadow-foreign");
    assert_eq!(
        foreign_engine.measure_index_shadow(&scope, &snapshot, &request),
        Err(ShadowReadError::ScopeMismatch)
    );
    assert_eq!(engine.auto_index_lifecycle_generation(&owner).unwrap(), 0);
    assert!(engine.auto_index_shadow_evidence(&owner, "score", [7; 32], 20).unwrap().is_none());
    drop(foreign_engine);
    drop(engine);
    std::fs::remove_dir_all(foreign_dir).unwrap();
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn index_control_receipts_match_the_committed_snapshot() {
    use crate::auto_index::{LogicalCost, Observation, Predicate, PredicateKind, Template};
    let (dir, engine) = open_engine("index-control-receipts");
    let owner = tenant("tenant-a");
    engine.record_auto_index_observation(Observation {
        tenant: owner.as_str().to_owned(),
        template: Template {
            collection: "orders".into(),
            predicates: vec![Predicate { field: "score".into(), kind: PredicateKind::Equality }],
            ordering: vec![],
            projection: None,
        },
        cost: LogicalCost::new(100, 100),
        rows_returned: 1,
        index: None,
        at_millis: 1,
    });
    let recommendations = engine.auto_index_recommendations(&owner, None, 10).unwrap();
    let candidate = &recommendations.recommendations[0].candidate_id;
    let applied =
        engine.auto_index_apply(&owner, candidate, recommendations.generation, 2).unwrap();
    assert_eq!(applied.generation, 1);
    assert_eq!(applied.phase, crate::auto_index::LifecyclePhase::Selected);
    assert!(!applied.visible && !applied.zero_stale_reads && !applied.regression_free);
    assert!(engine.index_descriptors().unwrap().is_empty());
    assert!(matches!(engine.auto_index_apply(&owner, candidate, 1, 3), Err(EngineError::Conflict)));
    assert!(matches!(
        engine.auto_index_pin_template(&owner, candidate, "orders where score = ?", 1),
        Err(EngineError::Conflict)
    ));
    assert_eq!(
        engine.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap().indexes,
        vec![applied.clone()]
    );
    drop(engine);
    let engine = reopen_engine(&dir);
    assert_eq!(
        engine.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap().indexes,
        vec![applied.clone()]
    );
    assert!(engine.index_descriptors().unwrap().is_empty());
    assert!(engine.auto_index_shadow_evidence(&owner, candidate, [7; 32], 20).unwrap().is_none());
    // Seed active metadata to exercise pin persistence independently of the
    // unfinished build worker. This is not evidence of engine activation.
    seed_active_index_control_fixture(&engine, applied);
    let pinned =
        engine.auto_index_pin_template(&owner, candidate, "orders where score = ?", 2).unwrap();
    assert_eq!(pinned.generation, 3);
    assert!(pinned.pinned);
    assert_eq!(
        engine.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap().indexes,
        vec![pinned.clone()]
    );
    assert!(matches!(engine.auto_index_apply(&owner, candidate, 1, 3), Err(EngineError::Conflict)));
    assert!(matches!(
        engine.auto_index_pin_template(&owner, candidate, "orders where score = ?", 1),
        Err(EngineError::Conflict)
    ));
    assert!(matches!(
        engine.auto_index_retire(&owner, candidate, 1, 3),
        Err(EngineError::Conflict)
    ));
    assert!(matches!(
        engine.auto_index_retire(&owner, candidate, 3, 3),
        Err(EngineError::Conflict)
    ));
    let snapshot = engine.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap();
    drop(engine);
    let reopened = reopen_engine(&dir);
    assert_eq!(reopened.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap(), snapshot);
    drop(reopened);
    std::fs::remove_dir_all(dir).unwrap();
}

fn seed_active_index_control_fixture(
    engine: &Engine,
    mut view: crate::auto_index::LifecycleIndexView,
) {
    view.phase = crate::auto_index::LifecyclePhase::Active;
    view.visible = true;
    view.zero_stale_reads = true;
    view.regression_free = true;
    engine
        .auto_index_lifecycle
        .lock()
        .unwrap()
        .install(engine.lock_storage().unwrap().port.as_mut(), view, 2, [0; 32])
        .unwrap();
}

#[test]
fn index_tenant_controls_and_audits_stay_isolated_after_restart() {
    use crate::auto_index::{LogicalCost, Observation, Predicate, PredicateKind, Template};
    let (dir, engine) = open_engine("index-tenant-isolation");
    let a = tenant("tenant-a");
    let b = tenant("tenant-b");
    let c = tenant("tenant-c");
    let mut observation = Observation {
        tenant: a.as_str().into(),
        template: Template {
            collection: "orders".into(),
            predicates: vec![Predicate { field: "score".into(), kind: PredicateKind::Equality }],
            ordering: vec![],
            projection: None,
        },
        cost: LogicalCost::new(10, 10),
        rows_returned: 1,
        index: None,
        at_millis: 1,
    };
    engine.record_auto_index_observation(observation.clone());
    assert!(engine.auto_index_recommendations(&b, None, 10).unwrap().recommendations.is_empty());
    observation.tenant = b.as_str().into();
    engine.record_auto_index_observation(observation);
    let recommendation = engine.auto_index_recommendations(&a, None, 10).unwrap();
    let id = &recommendation.recommendations[0].candidate_id;
    assert_eq!(
        &engine.auto_index_recommendations(&b, None, 10).unwrap().recommendations[0].candidate_id,
        id
    );
    let installed_a = engine.auto_index_apply(&a, id, 0, 2).unwrap();
    // Tenant A's commit cannot invalidate tenant B's optimistic generation.
    let installed_b = engine.auto_index_apply(&b, id, 0, 3).unwrap();
    assert_eq!(installed_a.generation, 1);
    assert_eq!(installed_b.generation, 1);
    assert_eq!(engine.auto_index_forbid_shape(&a, id, "score", 1).unwrap(), 2);
    assert!(engine.auto_index_recommendations(&a, None, 10).unwrap().recommendations.is_empty());
    assert_eq!(engine.auto_index_recommendations(&b, None, 10).unwrap().recommendations.len(), 1);
    seed_active_index_control_fixture(&engine, installed_b);
    let pinned_b = engine.auto_index_pin_template(&b, id, "orders where score = ?", 2).unwrap();
    assert!(pinned_b.pinned);
    assert!(!pinned_b.forbidden);
    let snapshot_a = engine.auto_index_lifecycle_snapshot(&a, None, 10).unwrap();
    let snapshot_b = engine.auto_index_lifecycle_snapshot(&b, None, 10).unwrap();
    assert!(snapshot_a.indexes[0].forbidden);
    assert!(!snapshot_a.indexes[0].pinned);
    assert_eq!(snapshot_a.indexes[0].tenant, a.as_str());
    assert_eq!(snapshot_b.indexes[0].tenant, b.as_str());
    let audit_a = engine.auto_index_lifecycle_audit(&a, None, Some(id), 10).unwrap();
    let audit_b = engine.auto_index_lifecycle_audit(&b, None, Some(id), 10).unwrap();
    assert_eq!(audit_a.len(), 2);
    assert_eq!(audit_b.len(), 3);
    assert_ne!(audit_a, audit_b);
    assert!(engine.auto_index_lifecycle_audit(&c, None, None, 10).unwrap().is_empty());
    assert_eq!(engine.auto_index_lifecycle_generation(&c).unwrap(), 0);
    drop(engine);
    let reopened = reopen_engine(&dir);
    assert_eq!(reopened.auto_index_lifecycle_snapshot(&a, None, 10).unwrap(), snapshot_a);
    assert_eq!(reopened.auto_index_lifecycle_snapshot(&b, None, 10).unwrap(), snapshot_b);
    assert_eq!(reopened.auto_index_lifecycle_audit(&a, None, Some(id), 10).unwrap(), audit_a);
    assert_eq!(reopened.auto_index_lifecycle_audit(&b, None, Some(id), 10).unwrap(), audit_b);
    assert!(reopened.auto_index_lifecycle_audit(&c, None, None, 10).unwrap().is_empty());
    drop(reopened);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn index_durable_snapshot_excludes_process_local_planner_entries() {
    use crate::query::optimizer::{IndexCapabilities, IndexDefinition, IndexField, IndexKind};
    let (dir, engine) = open_engine("durable-index-snapshot");
    let owner = tenant("tenant-a");
    let definition = IndexDefinition::try_new(
        CollectionName::try_new("orders").unwrap(),
        vec![IndexField::try_new("score", IndexCapabilities::FULL).unwrap()],
        IndexKind::Auto,
    )
    .unwrap();
    engine.indexes.register_auto(&owner, &definition).unwrap();
    assert_eq!(engine.queries().registered_indexes(&owner).len(), 1);
    let snapshot = engine.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap();
    assert!(snapshot.indexes.is_empty());
    assert_eq!(snapshot.generation, 0);
    drop(engine);
    let reopened = reopen_engine(&dir);
    assert_eq!(reopened.auto_index_lifecycle_snapshot(&owner, None, 10).unwrap(), snapshot);
    drop(reopened);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn uniform_context_guard_rejects_new_dependencies_and_allows_safe_retry_and_replay() {
    let (dir, engine) = open_engine("uniform-context-guard");
    let scope = tenant_scope(&engine, "uniform-tenant");
    let old = engine.durable_origin_prefixes(&scope).expect("empty frontier");
    assert!(old.is_empty());
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut pending = engine.begin(&scope, snapshot, operation("uniform-target")).expect("pending");
    pending.require_uniform_context(old.clone()).expect("guard");
    pending.put(document("target", "users", "target"), ExpectedVersion::New).expect("write");
    assert!(matches!(
        commit_put(
            &engine,
            "uniform-tenant",
            "new-dependency",
            "dependency",
            "new",
            ExpectedVersion::New,
            LocalDurability::Synced
        ),
        CommitOutcome::Committed(_)
    ));
    assert_eq!(
        pending.commit(LocalDurability::Synced),
        CommitOutcome::Rejected(CommitError::Conflict)
    );
    assert!(read_document(&engine, "uniform-tenant", "target").is_none());
    let new = engine.durable_origin_prefixes(&scope).expect("new frontier");
    assert!(!new.is_empty());
    let mut retry = engine
        .begin(&scope, engine.read_snapshot(&scope).expect("snapshot"), operation("uniform-target"))
        .expect("same identity retry");
    retry.require_uniform_context(new).expect("new uniform proof");
    retry.put(document("target", "users", "target"), ExpectedVersion::New).expect("write");
    let CommitOutcome::Committed(receipt) = retry.commit(LocalDurability::Synced) else {
        panic!("uniform retry commits");
    };
    let mut replay = engine
        .begin(&scope, engine.read_snapshot(&scope).expect("snapshot"), operation("uniform-target"))
        .expect("replay");
    replay.require_uniform_context(old).expect("stale cut");
    replay.put(document("target", "users", "target"), ExpectedVersion::New).expect("write");
    let CommitOutcome::Committed(replayed) = replay.commit(LocalDurability::Synced) else {
        panic!("committed identity replays before guard");
    };
    assert_eq!(receipt.sequence(), replayed.sequence());
    drop(engine);
    std::fs::remove_dir_all(dir).expect("cleanup");
}

fn replicated_group(ids: &[&str], stamp: u64) -> super::ReplicatedPublication {
    super::ReplicatedPublication {
        origin: "group-peer".into(),
        origin_sequence: 1,
        hlc: stamp,
        commit_ulid: "group-source-operation".into(),
        context: Vec::new(),
        private_payload: Vec::new(),
        records: ids
            .iter()
            .map(|id| ReplicatedRecord {
                collection: CollectionName::try_new("users").unwrap(),
                id: document_id(id),
                body: DocumentValue::Integer(stamp as i64),
                deleted: false,
                commit_ulid: "group-source-operation".into(),
                hlc: stamp,
                origin: "group-peer".into(),
                origin_sequence: 1,
                context: Vec::new(),
            })
            .collect(),
    }
}

#[test]
fn replicated_publication_rolls_back_every_document_and_replays_after_restart() {
    let (dir, engine) = open_engine("atomic-replicated-group");
    let scope = tenant_scope(&engine, "tenant-a");
    let publication = replicated_group(&["group-a", "group-b"], 100);
    let sql = rusqlite::Connection::open(dir.join("terrabase.sqlite3")).unwrap();
    sql.execute_batch("CREATE TRIGGER fail_second_group BEFORE INSERT ON documents WHEN NEW.id = 'group-b' BEGIN SELECT RAISE(ABORT, 'group fault'); END;").unwrap();
    let before = engine.published_sequence().unwrap();
    assert_eq!(
        engine.apply_replicated_publication(&scope, &publication),
        Err(EngineError::StorageFailure)
    );
    assert_eq!(engine.published_sequence().unwrap(), before);
    assert!(read_document(&engine, "tenant-a", "group-a").is_none());
    assert!(read_document(&engine, "tenant-a", "group-b").is_none());
    sql.execute_batch("DROP TRIGGER fail_second_group").unwrap();
    let applied = engine.apply_replicated_publication(&scope, &publication).unwrap();
    assert!(!applied.replayed());
    let sequence = engine.published_sequence().unwrap();
    let distinct: i64 = sql
        .query_row(
            "SELECT COUNT(DISTINCT commit_seq) FROM documents WHERE id IN ('group-a', 'group-b')",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(distinct, 1);
    engine.shutdown().unwrap();
    drop(sql);
    let engine = reopen_engine(&dir);
    let scope = tenant_scope(&engine, "tenant-a");
    assert!(engine.apply_replicated_publication(&scope, &publication).unwrap().replayed());
    assert_eq!(engine.published_sequence().unwrap(), sequence);
    let mut reordered = publication.clone();
    reordered.records.reverse();
    assert!(engine.apply_replicated_publication(&scope, &reordered).unwrap().replayed());
    let mut changed = publication;
    changed.records[0].body = DocumentValue::Integer(900);
    assert!(matches!(
        engine.apply_replicated_publication(&scope, &changed),
        Err(EngineError::OperationIdReuse { .. })
    ));
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn replicated_publication_conflict_history_failure_rolls_back_clean_sibling() {
    let (dir, engine) = open_engine("atomic-replicated-conflict");
    let scope = tenant_scope(&engine, "tenant-a");
    let mut original = replicated_group(&["group-b"], 50).records.remove(0);
    original.origin = "other-peer".into();
    original.commit_ulid = "older-source".into();
    engine.apply_replicated(&scope, &original).unwrap();
    let publication = replicated_group(&["group-a", "group-b"], 100);
    let sql = rusqlite::Connection::open(dir.join("terrabase.sqlite3")).unwrap();
    sql.execute_batch("CREATE TRIGGER fail_group_history BEFORE INSERT ON merge_conflicts BEGIN SELECT RAISE(ABORT, 'group history fault'); END;").unwrap();
    assert_eq!(
        engine.apply_replicated_publication(&scope, &publication),
        Err(EngineError::StorageFailure)
    );
    assert!(read_document(&engine, "tenant-a", "group-a").is_none());
    assert_eq!(
        read_document(&engine, "tenant-a", "group-b").unwrap().value(),
        &DocumentValue::Integer(50)
    );
    sql.execute_batch("DROP TRIGGER fail_group_history").unwrap();
    engine.apply_replicated_publication(&scope, &publication).unwrap();
    assert_eq!(
        read_document(&engine, "tenant-a", "group-b").unwrap().value(),
        &DocumentValue::Integer(100)
    );
    engine.shutdown().unwrap();
    drop(sql);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn replicated_publication_private_controls_and_documents_share_rollback_and_restart() {
    let (source_dir, source) = open_engine("atomic-private-source");
    let source_scope = tenant_scope(&source, "tenant-a");
    let snapshot = source.read_snapshot(&source_scope).unwrap();
    let mut transaction = source
        .begin(&source_scope, snapshot, OperationId::try_new("private-source").unwrap())
        .unwrap();
    transaction
        .put(
            Document::new(
                document_id("private-doc"),
                CollectionName::try_new("users").unwrap(),
                DocumentValue::Integer(7),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    transaction.metadata.push(crate::storage::PrivateMetadataWrite {
        key: "migration:test:cursor".into(),
        value: Some(DocumentValue::Integer(7)),
    });
    assert!(matches!(transaction.commit(LocalDurability::Written), CommitOutcome::Committed(_)));
    let groups = source.replication_publications(&source_scope, 0, 10).unwrap();
    let publication = &groups.last().unwrap().1;
    assert_eq!(publication.records.len(), 1);
    assert!(!publication.private_payload.is_empty());
    let (dir, engine) = open_engine("atomic-private-target");
    let scope = tenant_scope(&engine, "tenant-a");
    let sql = rusqlite::Connection::open(dir.join("terrabase.sqlite3")).unwrap();
    sql.execute_batch("CREATE TRIGGER fail_private_document BEFORE INSERT ON documents BEGIN SELECT RAISE(ABORT, 'private group fault'); END;").unwrap();
    assert_eq!(
        engine.apply_replicated_publication(&scope, publication),
        Err(EngineError::StorageFailure)
    );
    let count: i64 =
        sql.query_row("SELECT COUNT(*) FROM family_metadata", [], |row| row.get(0)).unwrap();
    assert_eq!(count, 0);
    sql.execute_batch("DROP TRIGGER fail_private_document").unwrap();
    engine.apply_replicated_publication(&scope, publication).unwrap();
    let sequence: i64 = sql
        .query_row(
            "SELECT seq FROM family_metadata WHERE record_key='migration:test:cursor'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    let doc_sequence: i64 = sql
        .query_row("SELECT commit_seq FROM documents WHERE id='private-doc'", [], |row| row.get(0))
        .unwrap();
    assert_eq!(sequence, doc_sequence);
    engine.shutdown().unwrap();
    drop(sql);
    let engine = reopen_engine(&dir);
    let scope = tenant_scope(&engine, "tenant-a");
    assert!(engine.apply_replicated_publication(&scope, publication).unwrap().replayed());
    assert_eq!(
        engine
            .replication_publications(&scope, 0, 10)
            .unwrap()
            .last()
            .unwrap()
            .1
            .fingerprint()
            .unwrap(),
        publication.fingerprint().unwrap()
    );
    engine.shutdown().unwrap();
    source.shutdown().unwrap();
    std::fs::remove_dir_all(dir).unwrap();
    std::fs::remove_dir_all(source_dir).unwrap();
}

#[test]
fn replicated_publication_missing_dependency_binds_nothing_and_retries() {
    let (dir, engine) = open_engine("atomic-group-dependency");
    let scope = tenant_scope(&engine, "tenant-a");
    let predecessor = replicated_group(&["dependency-first"], 10);
    let mut later = replicated_group(&["dependency-later"], 20);
    later.origin_sequence = 2;
    later.commit_ulid = "group-second-source".into();
    later.context =
        super::merge::encode_context(&super::merge::DocumentContext::from_frontier([(
            later.origin.clone(),
            1,
        )]));
    for record in &mut later.records {
        record.origin_sequence = 2;
        record.commit_ulid = later.commit_ulid.clone();
        record.context = later.context.clone();
    }
    assert_eq!(engine.apply_replicated_publication(&scope, &later), Err(EngineError::Conflict));
    assert!(read_document(&engine, "tenant-a", "dependency-later").is_none());
    engine.apply_replicated_publication(&scope, &predecessor).unwrap();
    engine.apply_replicated_publication(&scope, &later).unwrap();
    assert!(read_document(&engine, "tenant-a", "dependency-later").is_some());
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(dir).unwrap();
}

fn test_storage_keys(
    owner: &crate::storage::DatabaseOwner,
    byte: u8,
) -> Arc<dyn crate::encryption::DatabaseKeySource> {
    use crate::encryption::{
        DatabaseKey, DatabaseKeyLease, DatabaseKeySource, DatabaseKeyVersion, EncryptionError,
        KeyPurpose, KeyScope, KeyVersionMetadata,
    };
    #[derive(Debug)]
    struct Keys(Arc<DatabaseKeyLease>);
    impl DatabaseKeySource for Keys {
        fn current_lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
            Ok(self.0.clone())
        }
        fn lease(
            &self,
            version: DatabaseKeyVersion,
        ) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
            if self.0.metadata().version() == version {
                self.current_lease()
            } else {
                Err(EncryptionError::InvalidKeyMetadata)
            }
        }
    }
    let scope = KeyScope::new_with_project(
        owner.environment().as_str(),
        owner.tenant(),
        owner.project(),
        owner.database(),
        KeyPurpose::Data,
    )
    .unwrap();
    let metadata = KeyVersionMetadata::new(
        scope,
        DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
    );
    Arc::new(Keys(Arc::new(
        DatabaseKeyLease::test_oci_vault(
            metadata,
            DatabaseKey::from_bytes([byte; 32]),
            std::time::Duration::from_secs(300),
        )
        .unwrap(),
    )))
}

#[test]
fn encrypted_owner_reopen_and_wrong_key_fail_closed() {
    let dir = location("encrypted-owner");
    let owner = crate::storage::DatabaseOwner::local_development();
    let tenant = tenant(&owner.tenant().to_text());
    let config = EngineConfig::durable(&dir).unwrap().with_owner(owner.clone());
    assert!(matches!(Engine::open(config.clone()), Err(EngineOpenError::InvalidConfig)));
    let engine =
        Engine::open(config.clone().with_storage_keys(test_storage_keys(&owner, 91))).unwrap();
    let scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction = engine.begin(&scope, snapshot, operation("encrypted-write")).unwrap();
    transaction
        .put(document("secret", "items", "encrypted-owner-payload"), ExpectedVersion::New)
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    engine.shutdown().unwrap();
    assert!(Engine::open(config.clone().with_storage_keys(test_storage_keys(&owner, 92))).is_err());
    let reopened = Engine::open(config.with_storage_keys(test_storage_keys(&owner, 91))).unwrap();
    let scope = reopened.scope_for(&tenant);
    let snapshot = reopened.read_snapshot(&scope).unwrap();
    let transaction = reopened.begin(&scope, snapshot, operation("encrypted-read")).unwrap();
    assert!(transaction.read(&document_id("secret")).unwrap().is_some());
    drop(transaction);
    drop(reopened);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn encrypted_owner_revocation_blocks_cached_and_staged_reads() {
    use crate::encryption::{
        DatabaseKeyLease, DatabaseKeySource, DatabaseKeyVersion, EncryptionError,
    };
    #[derive(Debug)]
    struct Revocable {
        source: Arc<dyn DatabaseKeySource>,
        revoked: std::sync::atomic::AtomicBool,
    }
    impl DatabaseKeySource for Revocable {
        fn current_lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
            if self.revoked.load(Ordering::Acquire) {
                Err(EncryptionError::KeyLeaseExpired)
            } else {
                self.source.current_lease()
            }
        }
        fn lease(
            &self,
            version: DatabaseKeyVersion,
        ) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
            self.current_lease()?;
            self.source.lease(version)
        }
    }
    let dir = location("encrypted-revocation");
    let owner = crate::storage::DatabaseOwner::local_development();
    let tenant = tenant(&owner.tenant().to_text());
    let keys = Arc::new(Revocable {
        source: test_storage_keys(&owner, 93),
        revoked: std::sync::atomic::AtomicBool::new(false),
    });
    let engine = Engine::open(
        EngineConfig::durable(&dir).unwrap().with_owner(owner).with_storage_keys(keys.clone()),
    )
    .unwrap();
    let scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let mut transaction = engine.begin(&scope, snapshot, operation("staged-secret")).unwrap();
    transaction.put(document("secret", "items", "staged-secret"), ExpectedVersion::New).unwrap();
    assert!(transaction.read(&document_id("secret")).unwrap().is_some());
    keys.revoked.store(true, Ordering::Release);
    assert!(matches!(transaction.read(&document_id("secret")), Err(EngineError::StorageFailure)));
    assert!(matches!(engine.read_snapshot(&scope), Err(EngineError::StorageFailure)));
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        CommitOutcome::Rejected(CommitError::StorageFailure)
    ));
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn replicated_private_only_conflicts_converge_and_keep_loser_source() {
    let (source_dir, source) = open_engine("private-conflict-source");
    let source_scope = tenant_scope(&source, "tenant-a");
    for number in [1, 2] {
        let snapshot = source.read_snapshot(&source_scope).unwrap();
        let mut transaction = source
            .begin(
                &source_scope,
                snapshot,
                OperationId::try_new(&format!("private-control-{number}")).unwrap(),
            )
            .unwrap();
        transaction.metadata.push(crate::storage::PrivateMetadataWrite {
            key: "migration:test:cursor".into(),
            value: Some(DocumentValue::Integer(number)),
        });
        assert!(matches!(
            transaction.commit(LocalDurability::Written),
            CommitOutcome::Committed(_)
        ));
    }
    let mut groups = source.replication_publications(&source_scope, 0, 10).unwrap();
    for (index, (_, group)) in groups.iter_mut().enumerate() {
        group.origin = format!("private-peer-{index}");
        group.origin_sequence = 1;
        group.hlc = 100 + index as u64;
        group.context = Vec::new();
        assert!(group.records.is_empty());
    }
    let mut observed = Vec::new();
    for reverse in [false, true] {
        let (dir, engine) =
            open_engine(if reverse { "private-reverse" } else { "private-forward" });
        let scope = tenant_scope(&engine, "tenant-a");
        for index in if reverse { [1, 0] } else { [0, 1] } {
            engine.apply_replicated_publication(&scope, &groups[index].1).unwrap();
        }
        assert_eq!(engine.replication_publications(&scope, 0, 10).unwrap().len(), 2);
        let sql = rusqlite::Connection::open(dir.join("terrabase.sqlite3")).unwrap();
        observed.push(sql.query_row("SELECT value FROM family_metadata WHERE record_key='migration:test:cursor' ORDER BY seq DESC LIMIT 1", [], |row| row.get::<_,Vec<u8>>(0)).unwrap());
        engine.shutdown().unwrap();
        drop(sql);
        std::fs::remove_dir_all(dir).unwrap();
    }
    assert_eq!(observed[0], observed[1]);
    assert_eq!(
        observed[0],
        crate::storage::encode_private_value(&DocumentValue::Integer(2)).unwrap()
    );
    source.shutdown().unwrap();
    std::fs::remove_dir_all(source_dir).unwrap();
}

#[test]
fn origin_sequences_are_contiguous_per_tenant_under_interleaved_commits() {
    let (dir, engine) = open_engine("tenant-origin-counter");
    for (tenant, op, id) in [
        ("tenant-one", "first-a", "a"),
        ("tenant-two", "first-b", "b"),
        ("tenant-one", "second-a", "c"),
    ] {
        assert!(matches!(
            commit_put(
                &engine,
                tenant,
                op,
                id,
                "body",
                ExpectedVersion::New,
                LocalDurability::Synced
            ),
            CommitOutcome::Committed(_)
        ));
    }
    let first = engine
        .durable_origin_prefixes(&tenant_scope(&engine, "tenant-one"))
        .expect("first frontier");
    let second = engine
        .durable_origin_prefixes(&tenant_scope(&engine, "tenant-two"))
        .expect("second frontier");
    assert_eq!(first.values().copied().collect::<Vec<_>>(), vec![2]);
    assert_eq!(second.values().copied().collect::<Vec<_>>(), vec![1]);
    drop(engine);
    let engine = reopen_engine(&dir);
    assert_eq!(
        engine
            .durable_origin_prefixes(&tenant_scope(&engine, "tenant-two"))
            .expect("restart frontier"),
        second
    );
    drop(engine);
    std::fs::remove_dir_all(dir).expect("cleanup");
}
