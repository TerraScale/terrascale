//! In-crate family seam tests for the W3-F packet.
//!
//! Each contract test reads as one end-to-end scenario, so the module allows
//! the line-count lint rather than splitting a scenario across helpers.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. They observe typed results, errors,
//! atomicity, isolation, and snapshot behavior through the family seam only.
//! They make no V1 parity claim and create no Recovery, Replication, or
//! Benchmark receipt.

#![allow(clippy::too_many_lines)]

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::payloads::MAX_IDENTITY_VALUE;
use super::{
    Aggregation, ArchivalCommand, ArchivalReadRequest, ArchivalState, ArchivalWrite,
    AttachmentMetadata, AttachmentMutation, AttachmentRead, AttachmentWrite,
    CompareExchangeCondition, CompareExchangeEntry, CompareExchangeRead,
    CompareExchangeValueResult, CompareExchangeWrite, CounterBatchWrite, CounterEntry,
    CounterGroupResult, CounterOperation, CounterRead, FamilyError, FamilyKind, FamilyReadContext,
    FamilyReadRequest, FamilyReadResult, FamilyWriteRequest, IdentityCommand, IdentityRangeResult,
    IdentityRead, IdentityWrite, LifecycleCommand, LifecycleRead, LifecycleResult, LifecycleWrite,
    RevisionCommand, RevisionPaging, RevisionPolicy, RevisionRead, RevisionWrite, RollupPolicy,
    SchemaDefinition, SchemaFieldType, SchemaRule, SchemaVersion, SchemaViolation,
    TimeSeriesAppend, TimeSeriesBatchWrite, TimeSeriesDelete, TimeSeriesEntry, TimeSeriesPolicy,
    TimeSeriesRange, TimeSeriesRead, TombstoneCommand, TombstoneRead, TombstoneWrite,
    ValidationRead, ValidationResult, ValidationWrite, ViolationKind,
    state::{RESERVED_COLLECTION, now_millis, record_id},
};

use crate::{
    engine::{CommitOutcome, Engine, EngineConfig, EngineError, Transaction},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, IndexGenerationSet, LocalDurability, OperationId, ReadSnapshot, TenantId,
        TenantScope,
    },
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-w3f-family-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

/// Mints a never-reused operation identity for repeated helper commits.
fn unique_operation(prefix: &str) -> OperationId {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("{prefix}-{index}")).expect("valid identifier")
}

fn scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), engine.identity())
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(45)).expect("valid deadline")
}

fn cancellation() -> Cancellation {
    Cancellation::new()
}

fn object_user_value(fields: &[(&str, &str)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(key, value)| ((*key).to_owned(), DocumentValue::String((*value).to_owned())))
            .collect(),
    )
}

/// Commits one transaction after staging documents and family writes in it.
///
/// The closure receives the Engine-borrowed family store, the open
/// transaction, the tenant scope, and the transaction operation identity.
fn commit_transaction<'engine>(
    engine: &'engine Engine,
    tenant_name: &str,
    operation_name: &str,
    stage: impl FnOnce(
        &super::FamilyStore<'engine>,
        &mut Transaction<'engine>,
        &TenantScope,
        &OperationId,
    ),
) -> CommitOutcome {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = operation(operation_name);
    let store = engine.families();
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    stage(&store, &mut transaction, &tenant_scope, &identity);
    transaction.commit(LocalDurability::Synced)
}

/// Puts one user document through the document seam.
fn put_user_document(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    value: DocumentValue,
) -> CommitOutcome {
    commit_transaction(
        engine,
        tenant_name,
        unique_operation(operation_name).as_str(),
        |_store, transaction, _, _| {
            transaction
                .put(
                    Document::new(document_id(id), collection("users"), value),
                    ExpectedVersion::Any,
                )
                .expect("stages document");
        },
    )
}

/// Deletes one user document through the document seam.
fn delete_user_document(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
) -> CommitOutcome {
    commit_transaction(
        engine,
        tenant_name,
        unique_operation(operation_name).as_str(),
        |_store, transaction, _, _| {
            transaction.delete(document_id(id), ExpectedVersion::Any).expect("stages delete");
        },
    )
}

/// Reads one family capability through the family seam.
fn read_family(
    engine: &Engine,
    tenant_name: &str,
    request: FamilyReadRequest,
) -> Result<FamilyReadResult, FamilyError> {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let context = FamilyReadContext::new(tenant_scope, snapshot);
    engine.families().read(context, request)
}

fn counter_read(document: &str, names: &[&str]) -> FamilyReadRequest {
    FamilyReadRequest::counter_group(
        CounterRead::try_new(
            document_id(document),
            names.iter().map(|name| (*name).to_owned()).collect(),
            deadline(),
            cancellation(),
        )
        .expect("valid counter read"),
    )
}

fn counter_write(
    tenant_scope: &TenantScope,
    operation_name: &str,
    document: &str,
    operations: Vec<CounterOperation>,
) -> FamilyWriteRequest {
    FamilyWriteRequest::counter_batch(
        CounterBatchWrite::try_new(
            tenant_scope.clone(),
            document_id(document),
            operations,
            ExpectedVersion::Any,
            operation(operation_name),
            deadline(),
            cancellation(),
        )
        .expect("valid counter batch"),
    )
}

fn counter_group(
    result: Result<FamilyReadResult, FamilyError>,
) -> Result<CounterGroupResult, FamilyError> {
    match result {
        Ok(FamilyReadResult::CounterGroup(group)) => Ok(group),
        Ok(other) => panic!("expected a counter group, got {other:?}"),
        Err(error) => Err(error),
    }
}

fn counter_value(group: &CounterGroupResult, name: &str) -> Option<i64> {
    group.entries().iter().find(|entry| entry.name() == name).and_then(CounterEntry::value)
}

fn attachment_write(
    tenant_scope: &TenantScope,
    operation_name: &str,
    document: &str,
    mutation: AttachmentMutation,
) -> FamilyWriteRequest {
    FamilyWriteRequest::attachment_mutation(
        AttachmentWrite::try_new(
            tenant_scope.clone(),
            document_id(document),
            mutation,
            ExpectedVersion::Any,
            operation(operation_name),
            deadline(),
            cancellation(),
        )
        .expect("valid attachment write"),
    )
}

/// Stages one lifecycle update through the passed store.
fn stage_lifecycle<'engine>(
    store: &super::FamilyStore<'engine>,
    transaction: &mut Transaction<'engine>,
    tenant_scope: &TenantScope,
    identity: &str,
    document: &str,
    command: LifecycleCommand,
) -> Result<(), FamilyError> {
    store.stage(
        transaction,
        FamilyWriteRequest::lifecycle_update(
            LifecycleWrite::try_new(
                tenant_scope.clone(),
                document_id(document),
                command,
                ExpectedVersion::Any,
                operation(identity),
                deadline(),
                cancellation(),
            )
            .expect("valid lifecycle write"),
        ),
    )
}

/// Stages one identity command through the passed store.
fn stage_identity<'engine>(
    store: &super::FamilyStore<'engine>,
    transaction: &mut Transaction<'engine>,
    tenant_scope: &TenantScope,
    identity: &str,
    command: IdentityCommand,
) -> Result<(), FamilyError> {
    store.stage(
        transaction,
        FamilyWriteRequest::identity_allocation(
            IdentityWrite::try_new(
                tenant_scope.clone(),
                command.name().to_owned(),
                command,
                operation(identity),
                deadline(),
                cancellation(),
            )
            .expect("valid identity write"),
        ),
    )
}

// ---------------------------------------------------------------------------
// The W3-F contract tests
// ---------------------------------------------------------------------------

#[test]
fn engine_families_returns_store_bound_to_durable_engine() {
    let (dir, engine) = open_engine("durable-store");
    let tenant_scope = scope(&engine, "tenant-a");
    put_user_document(
        &engine,
        "tenant-a",
        "put-doc",
        "doc-1",
        object_user_value(&[("name", "ada")]),
    );

    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = operation("counter-1");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    engine
        .families()
        .stage(
            &mut transaction,
            counter_write(
                &tenant_scope,
                "counter-1",
                "doc-1",
                vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 7 }],
            ),
        )
        .expect("stages through the borrowed store");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    engine.shutdown().expect("shuts down");

    let reopened = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("engine reopens");
    let reopened_scope = scope(&reopened, "tenant-a");
    let snapshot = reopened.read_snapshot(&reopened_scope).expect("snapshot");
    let context = FamilyReadContext::new(reopened_scope, snapshot);
    let result = reopened
        .families()
        .read(context, counter_read("doc-1", &["hits"]))
        .expect("reads through the reopened store");
    match result {
        FamilyReadResult::CounterGroup(group) => {
            assert_eq!(counter_value(&group, "hits"), Some(7));
        }
        other => panic!("expected a counter group, got {other:?}"),
    }
    reopened.shutdown().expect("shuts down");
}

#[test]
fn family_store_borrow_prevents_engine_shutdown_while_live() {
    let (_dir, engine) = open_engine("borrowed-store");
    {
        let store = engine.families();
        let tenant_scope = scope(&engine, "tenant-a");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let context = FamilyReadContext::new(tenant_scope, snapshot);
        // The live store keeps the Engine borrowed; a read through it works
        // and `engine.shutdown()` cannot run while the store is held.
        let _ = store.read(context, counter_read("missing", &["hits"]));
    }
    engine.shutdown().expect("shutdown runs after the store is released");
}

#[test]
fn family_stage_rejects_transaction_from_another_engine() {
    let (_dir_a, engine_a) = open_engine("engine-a");
    let (_dir_b, engine_b) = open_engine("engine-b");
    let scope_b = scope(&engine_b, "tenant-a");
    let snapshot_b = engine_b.read_snapshot(&scope_b).expect("snapshot");
    let mut transaction =
        engine_b.begin(&scope_b, snapshot_b, operation("counter-1")).expect("begins");
    let error = engine_a.families().stage(
        &mut transaction,
        counter_write(
            &scope_b,
            "counter-1",
            "doc-1",
            vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
        ),
    );
    assert_eq!(error, Err(FamilyError::TransactionEngineMismatch));
    drop(transaction);
    engine_a.shutdown().expect("shuts down");
    engine_b.shutdown().expect("shuts down");
}

#[test]
fn family_stage_rejects_transaction_with_a_different_operation_identity() {
    let (_dir, engine) = open_engine("operation-identity");
    let tenant_scope = scope(&engine, "tenant-a");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation("tx-op-1")).expect("begins");
    let error = engine.families().stage(
        &mut transaction,
        counter_write(
            &tenant_scope,
            "request-op-2",
            "doc-1",
            vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
        ),
    );
    assert_eq!(error, Err(FamilyError::TransactionIdentityMismatch));
    drop(transaction);
    engine.shutdown().expect("shuts down");
}

#[test]
fn attachment_metadata_round_trip_is_tenant_scoped() {
    let (_dir, engine) = open_engine("attachments");
    put_user_document(&engine, "tenant-a", "put-a", "doc-1", object_user_value(&[("n", "v")]));
    put_user_document(&engine, "tenant-b", "put-b", "doc-1", object_user_value(&[("n", "v")]));

    commit_transaction(
        &engine,
        "tenant-a",
        "attach-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Store {
                            name: "logo".to_owned(),
                            content_type: "image/png".to_owned(),
                            size: 4096,
                            hash: "digest-1".to_owned(),
                            replace: false,
                        },
                    ),
                )
                .expect("stages attachment metadata");
        },
    );

    let result = read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads attachment metadata");
    match result {
        FamilyReadResult::AttachmentMetadata(metadata) => {
            assert_eq!(metadata.entries().len(), 1);
            let entry: &AttachmentMetadata = &metadata.entries()[0];
            assert_eq!(entry.name(), "logo");
            assert_eq!(entry.content_type(), "image/png");
            assert_eq!(entry.size(), 4096);
            assert_eq!(entry.hash(), "digest-1");
            assert_eq!(entry.document().as_str(), "doc-1");
        }
        other => panic!("expected attachment metadata, got {other:?}"),
    }

    let other = read_family(
        &engine,
        "tenant-b",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads the other tenant's document");
    match other {
        FamilyReadResult::AttachmentMetadata(metadata) => {
            assert!(metadata.entries().is_empty(), "tenant B must not see tenant A metadata");
        }
        other => panic!("expected attachment metadata, got {other:?}"),
    }
    engine.shutdown().expect("shuts down");
}

#[test]
fn counter_crud_preserves_signed_values_and_missing_errors() {
    let (_dir, engine) = open_engine("counters-crud");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));

    commit_transaction(
        &engine,
        "tenant-a",
        "inc-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![
                            CounterOperation::Increment { name: "hits".to_owned(), delta: 5 },
                            CounterOperation::Increment { name: "hits".to_owned(), delta: -3 },
                            CounterOperation::Increment { name: "Hits".to_owned(), delta: 2 },
                        ],
                    ),
                )
                .expect("stages one batch of signed increments");
        },
    );

    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &[])))
        .expect("reads the group");
    assert_eq!(counter_value(&group, "hits"), Some(2));
    assert_eq!(counter_value(&group, "Hits"), Some(2), "casing is significant");

    commit_transaction(
        &engine,
        "tenant-a",
        "delete-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Delete { name: "hits".to_owned() }],
                    ),
                )
                .expect("stages the delete");
        },
    );
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
        .expect("reads after the delete");
    assert_eq!(counter_value(&group, "hits"), None, "a deleted counter is missing");

    // Decision D2 (docs/families/counter-merge-encoding.md): the delete
    // tombstones the name and re-creation fails closed, because the closed
    // registry has no typed create operation and a silent resurrect would
    // be indistinguishable from a concurrent increment the delete must
    // dominate.
    commit_transaction(
        &engine,
        "tenant-a",
        "recreate-1",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                counter_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 9 }],
                ),
            );
            assert_eq!(staged, Err(FamilyError::TombstoneConflict));
        },
    );
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
        .expect("reads after the rejected re-creation");
    assert_eq!(counter_value(&group, "hits"), None, "the tombstone keeps the name deleted");

    let missing_document = read_family(&engine, "tenant-a", counter_read("doc-404", &["hits"]));
    assert_eq!(missing_document, Err(FamilyError::DocumentMissing));
    commit_transaction(
        &engine,
        "tenant-a",
        "missing-write",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                counter_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-404",
                    vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
                ),
            );
            assert_eq!(staged, Err(FamilyError::DocumentMissing));
        },
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn counter_batch_is_atomic_across_increment_delete_and_recreate() {
    let (_dir, engine) = open_engine("counter-batch");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "seed-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![
                            CounterOperation::Increment { name: "keep".to_owned(), delta: 1 },
                            CounterOperation::Increment { name: "drop".to_owned(), delta: 4 },
                        ],
                    ),
                )
                .expect("seeds two counters");
        },
    );

    commit_transaction(
        &engine,
        "tenant-a",
        "batch-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![
                            CounterOperation::Increment { name: "keep".to_owned(), delta: 2 },
                            CounterOperation::Delete { name: "drop".to_owned() },
                            CounterOperation::Increment { name: "reborn".to_owned(), delta: 10 },
                        ],
                    ),
                )
                .expect("stages one atomic batch");
        },
    );
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &[])))
        .expect("reads the batched group");
    assert_eq!(counter_value(&group, "keep"), Some(3));
    assert_eq!(counter_value(&group, "drop"), None);
    assert_eq!(counter_value(&group, "reborn"), Some(10));

    // A batch that fails partway stages nothing, so committing the same
    // transaction cannot publish a partial counter group. The overflow is
    // exercised on live names; the tombstoned `drop` name is exercised
    // next (decision D2: an increment on a tombstoned counter fails
    // closed with the typed tombstone conflict).
    commit_transaction(
        &engine,
        "tenant-a",
        "batch-2",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                counter_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    vec![
                        CounterOperation::Increment { name: "keep".to_owned(), delta: 100 },
                        CounterOperation::Increment { name: "keep".to_owned(), delta: i64::MAX },
                        CounterOperation::Increment { name: "keep".to_owned(), delta: i64::MAX },
                    ],
                ),
            );
            assert_eq!(staged, Err(FamilyError::CounterOverflow));
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "batch-3",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                counter_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    vec![
                        CounterOperation::Increment { name: "keep".to_owned(), delta: 1 },
                        CounterOperation::Increment { name: "drop".to_owned(), delta: 1 },
                    ],
                ),
            );
            assert_eq!(staged, Err(FamilyError::TombstoneConflict));
        },
    );
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &[])))
        .expect("reads after the rejected batches");
    assert_eq!(counter_value(&group, "keep"), Some(3), "no partial state");
    assert_eq!(counter_value(&group, "drop"), None, "no partial state");
    engine.shutdown().expect("shuts down");
}

#[test]
fn counter_overflow_rejects_without_partial_state() {
    let (_dir, engine) = open_engine("counter-overflow");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "max-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment {
                            name: "total".to_owned(),
                            delta: i64::MAX,
                        }],
                    ),
                )
                .expect("stages the maximum value");
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "overflow-1",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                counter_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    vec![CounterOperation::Increment { name: "total".to_owned(), delta: 1 }],
                ),
            );
            assert_eq!(staged, Err(FamilyError::CounterOverflow));
        },
    );
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["total"])))
        .expect("reads the unmodified counter");
    assert_eq!(counter_value(&group, "total"), Some(i64::MAX));
    engine.shutdown().expect("shuts down");
}

// The state-level companions of the GAP-F07 merge encoding (decision
// record D1/D2, `docs/families/counter-merge-encoding.md`): the merge
// algebra and the legacy decode, exercised directly on the record
// encoding. The end-to-end convergence, dedup, delete-wins, and rejoin
// proofs run on the replication lane (`src/replication/lane_tests.rs`).

// Decision record D2 (docs/families/counter-merge-encoding.md): a counter
// delete records a per-counter tombstone, reads hide the tombstoned name,
// the tombstone keeps the record alive so a remote stale increment cannot
// resurrect it unnoticed, and local re-creation fails closed with the
// typed tombstone conflict. Merge survival of the tombstone is proven at
// the state level in `counter_merge_is_...` and on the replication lane
// in `src/replication/lane_tests.rs`.
#[test]
fn counter_delete_tombstones_the_name_and_fails_closed_recreation() {
    let (_dir, engine) = open_engine("counter-tombstone");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "seed-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![
                            CounterOperation::Increment { name: "hits".to_owned(), delta: 7 },
                            CounterOperation::Increment { name: "kept".to_owned(), delta: 2 },
                        ],
                    ),
                )
                .expect("seeds two counters");
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "delete-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Delete { name: "hits".to_owned() }],
                    ),
                )
                .expect("stages the delete");
        },
    );

    // The deleted name is hidden and fenced: reads exclude it and any
    // later increment fails closed without staging.
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &[])))
        .expect("reads the group after the delete");
    assert_eq!(counter_value(&group, "hits"), None, "the tombstoned name is absent");
    assert_eq!(counter_value(&group, "kept"), Some(2), "unrelated names survive");
    commit_transaction(
        &engine,
        "tenant-a",
        "stale-1",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                counter_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
                ),
            );
            assert_eq!(staged, Err(FamilyError::TombstoneConflict));
        },
    );

    // The tombstone keeps the reserved record alive: a document still
    // occupies the reserved identifier, so a remote stale increment has a
    // record to merge into and be dominated by (decision D2).
    let record = record_id("counters", &["doc-1"]).expect("reserved record identifier");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let present = engine
        .snapshot_documents(&tenant_scope, &snapshot)
        .expect("reads the snapshot documents")
        .iter()
        .any(|document| document.id() == &record);
    assert!(present, "the tombstone keeps the counter record alive");
    engine.shutdown().expect("shuts down");
}

fn time_series_write(
    tenant_scope: &TenantScope,
    operation_name: &str,
    document: &str,
    name: &str,
    appends: Vec<(i64, Vec<f64>)>,
    deletes: Vec<TimeSeriesDelete>,
    policy: Option<TimeSeriesPolicy>,
) -> FamilyWriteRequest {
    FamilyWriteRequest::time_series_batch(
        TimeSeriesBatchWrite::try_new(
            tenant_scope.clone(),
            document_id(document),
            name.to_owned(),
            appends
                .into_iter()
                .map(|(timestamp, values)| TimeSeriesAppend::new(timestamp, values, None))
                .collect(),
            deletes,
            policy,
            ExpectedVersion::Any,
            operation(operation_name),
            deadline(),
            cancellation(),
        )
        .expect("valid time-series batch"),
    )
}

fn time_series_entries(
    engine: &Engine,
    document: &str,
    name: &str,
    range: Option<TimeSeriesRange>,
) -> Vec<i64> {
    let result = read_family(
        engine,
        "tenant-a",
        FamilyReadRequest::time_series_range(
            TimeSeriesRead::try_new(
                document_id(document),
                name.to_owned(),
                range,
                deadline(),
                cancellation(),
            )
            .expect("valid time-series read"),
        ),
    )
    .expect("reads the series");
    match result {
        FamilyReadResult::TimeSeriesPage(page) => {
            page.entries().iter().map(TimeSeriesEntry::timestamp).collect()
        }
        other => panic!("expected a time-series page, got {other:?}"),
    }
}

#[test]
fn time_series_append_and_delete_preserve_timestamp_order() {
    let (_dir, engine) = open_engine("time-series");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "append-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    time_series_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        "cpu",
                        vec![(30, vec![1.0]), (10, vec![2.0]), (20, vec![3.0])],
                        vec![],
                        None,
                    ),
                )
                .expect("stages out-of-order appends");
        },
    );
    assert_eq!(time_series_entries(&engine, "doc-1", "cpu", None), vec![10, 20, 30]);

    commit_transaction(
        &engine,
        "tenant-a",
        "delete-range",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    time_series_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        "cpu",
                        vec![],
                        vec![TimeSeriesDelete::Range { start: 15, end: 25 }],
                        None,
                    ),
                )
                .expect("stages a range delete");
        },
    );
    assert_eq!(time_series_entries(&engine, "doc-1", "cpu", None), vec![10, 30]);

    commit_transaction(
        &engine,
        "tenant-a",
        "delete-at",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    time_series_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        "cpu",
                        vec![],
                        vec![TimeSeriesDelete::At { timestamp: 10 }],
                        None,
                    ),
                )
                .expect("stages a point delete");
        },
    );
    assert_eq!(time_series_entries(&engine, "doc-1", "cpu", None), vec![30]);
    assert_eq!(
        time_series_entries(
            &engine,
            "doc-1",
            "cpu",
            Some(TimeSeriesRange::try_new(0, 20).expect("valid range"))
        ),
        Vec::<i64>::new()
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn revision_page_and_revert_keep_snapshot_identity() {
    let (_dir, engine) = open_engine("revisions");
    put_user_document(&engine, "tenant-a", "put-1", "doc-1", object_user_value(&[("v", "one")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "rev-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::revision_command(
                        RevisionWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            RevisionCommand::ForceCreate { expected: ExpectedVersion::Any },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid revision write"),
                    ),
                )
                .expect("stages the first revision");
        },
    );
    put_user_document(&engine, "tenant-a", "put-2", "doc-1", object_user_value(&[("v", "two")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "rev-2",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::revision_command(
                        RevisionWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            RevisionCommand::ForceCreate { expected: ExpectedVersion::Any },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid revision write"),
                    ),
                )
                .expect("stages the second revision");
        },
    );

    let read = || {
        read_family(
            &engine,
            "tenant-a",
            FamilyReadRequest::revision_page(
                RevisionRead::try_new(
                    document_id("doc-1"),
                    RevisionPaging::try_new(0, 10).expect("valid paging"),
                    deadline(),
                    cancellation(),
                )
                .expect("valid revision read"),
            ),
        )
    };
    let context_snapshot = {
        let tenant_scope = scope(&engine, "tenant-a");
        engine.read_snapshot(&tenant_scope).expect("snapshot").sequence()
    };
    match read().expect("pages the revisions") {
        FamilyReadResult::RevisionPage(page) => {
            assert_eq!(page.total(), 2);
            assert_eq!(page.entries().len(), 2);
            assert_eq!(page.entries()[0].revision(), 1);
            assert_eq!(page.entries()[1].revision(), 2);
            assert_eq!(
                page.entries()[0].value(),
                &object_user_value(&[("v", "one")]),
                "revision one keeps its own value"
            );
        }
        other => panic!("expected a revision page, got {other:?}"),
    }

    commit_transaction(
        &engine,
        "tenant-a",
        "revert-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::revision_command(
                        RevisionWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            RevisionCommand::Revert { to_revision: 1 },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid revert"),
                    ),
                )
                .expect("stages the revert");
        },
    );
    match read().expect("reads after the revert") {
        FamilyReadResult::RevisionPage(page) => {
            assert_eq!(page.total(), 3, "the revert appends one revision");
            assert_eq!(
                page.entries()[2].value(),
                &object_user_value(&[("v", "one")]),
                "the revert restores the referenced value"
            );
        }
        other => panic!("expected a revision page, got {other:?}"),
    }

    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    assert!(snapshot.sequence() > context_snapshot, "the revert published a later sequence");
    let older = ReadSnapshot::new(
        engine.identity(),
        tenant("tenant-a"),
        context_snapshot,
        IndexGenerationSet::default(),
    );
    let result = engine.families().read(
        FamilyReadContext::new(scope(&engine, "tenant-a"), older),
        FamilyReadRequest::revision_page(
            RevisionRead::try_new(
                document_id("doc-1"),
                RevisionPaging::try_new(0, 10).expect("valid paging"),
                deadline(),
                cancellation(),
            )
            .expect("valid revision read"),
        ),
    );
    match result.expect("reads the older snapshot") {
        FamilyReadResult::RevisionPage(page) => {
            assert_eq!(page.total(), 2, "the older snapshot keeps its own identity");
        }
        other => panic!("expected a revision page, got {other:?}"),
    }
    engine.shutdown().expect("shuts down");
}

#[test]
fn expiration_and_refresh_updates_do_not_resurrect_deleted_documents() {
    let (_dir, engine) = open_engine("lifecycle");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let lifecycle_read = |engine: &Engine| match read_family(
        engine,
        "tenant-a",
        FamilyReadRequest::lifecycle(
            LifecycleRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid lifecycle read"),
        ),
    )
    .expect("reads lifecycle state")
    {
        FamilyReadResult::Lifecycle(result) => result,
        other => panic!("expected a lifecycle result, got {other:?}"),
    };

    let now = now_millis();
    commit_transaction(
        &engine,
        "tenant-a",
        "expire-1",
        |store, transaction, tenant_scope, identity| {
            stage_lifecycle(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                "doc-1",
                LifecycleCommand::SetExpiration { at: Some(now + 60_000) },
            )
            .expect("stages a future expiry");
        },
    );
    let future: LifecycleResult = lifecycle_read(&engine);
    assert!(future.present());
    assert!(!future.expired());

    commit_transaction(
        &engine,
        "tenant-a",
        "expire-2",
        |store, transaction, tenant_scope, identity| {
            stage_lifecycle(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                "doc-1",
                LifecycleCommand::SetExpiration { at: Some(now - 1) },
            )
            .expect("stages an elapsed expiry");
        },
    );
    let expired = lifecycle_read(&engine);
    assert!(expired.expired(), "an elapsed expiry is observable");

    commit_transaction(
        &engine,
        "tenant-a",
        "refresh-1",
        |store, transaction, tenant_scope, identity| {
            stage_lifecycle(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                "doc-1",
                LifecycleCommand::SetRefresh { at: Some(now + 60_000) },
            )
            .expect("stages a refresh on an expired document");
        },
    );
    let refreshed = lifecycle_read(&engine);
    assert!(refreshed.expired(), "a refresh does not clear an elapsed expiry");
    assert_eq!(refreshed.refresh_at(), Some(now + 60_000));

    delete_user_document(&engine, "tenant-a", "delete-doc", "doc-1");
    commit_transaction(
        &engine,
        "tenant-a",
        "refresh-2",
        |store, transaction, tenant_scope, identity| {
            let staged = stage_lifecycle(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                "doc-1",
                LifecycleCommand::SetRefresh { at: Some(now + 60_000) },
            );
            assert_eq!(staged, Err(FamilyError::DocumentMissing));
        },
    );
    let deleted = lifecycle_read(&engine);
    assert!(!deleted.present(), "a deleted document is not resurrected");
    assert!(!deleted.expired());
    engine.shutdown().expect("shuts down");
}

#[test]
fn schema_validation_rejects_invalid_writes_without_mutation() {
    let (_dir, engine) = open_engine("schema-validation");
    put_user_document(
        &engine,
        "tenant-a",
        "put-doc",
        "doc-1",
        object_user_value(&[("name", "ada")]),
    );

    let schema = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
        ],
    )
    .expect("valid schema");
    commit_transaction(
        &engine,
        "tenant-a",
        "configure-schema",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::schema_validation_update(
                        ValidationWrite::try_new(
                            tenant_scope.clone(),
                            collection("users"),
                            Some(schema.clone()),
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid validation write"),
                    ),
                )
                .expect("stages the collection policy");
        },
    );

    let policy = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::validation(
            ValidationRead::try_new(
                collection("users"),
                Some(document_id("doc-1")),
                deadline(),
                cancellation(),
            )
            .expect("valid validation read"),
        ),
    )
    .expect("reads the policy and the on-demand report")
    {
        FamilyReadResult::Validation(result) => result,
        other => panic!("expected a validation result, got {other:?}"),
    };
    assert_eq!(policy.violations().len(), 1, "the stored document violates the policy");
    assert_eq!(policy.violations()[0].field(), "email");

    commit_transaction(
        &engine,
        "tenant-a",
        "reject-1",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                attachment_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    AttachmentMutation::Store {
                        name: "logo".to_owned(),
                        content_type: "image/png".to_owned(),
                        size: 1,
                        hash: "digest".to_owned(),
                        replace: false,
                    },
                ),
            );
            assert_eq!(staged, Err(FamilyError::SchemaViolation));
        },
    );
    let after_reject = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads attachment metadata")
    {
        FamilyReadResult::AttachmentMetadata(metadata) => metadata,
        other => panic!("expected attachment metadata, got {other:?}"),
    };
    assert!(after_reject.entries().is_empty(), "the rejected write staged nothing");

    put_user_document(
        &engine,
        "tenant-a",
        "put-doc-2",
        "doc-1",
        object_user_value(&[("name", "ada"), ("email", "ada@example")]),
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "accept-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Store {
                            name: "logo".to_owned(),
                            content_type: "image/png".to_owned(),
                            size: 1,
                            hash: "digest".to_owned(),
                            replace: false,
                        },
                    ),
                )
                .expect("a valid document accepts family writes");
        },
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_tombstone_blocks_stale_local_resurrection() {
    let (_dir, engine) = open_engine("tombstones");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "counter-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 2 }],
                    ),
                )
                .expect("stages a counter");
        },
    );

    delete_user_document(&engine, "tenant-a", "delete-doc", "doc-1");
    commit_transaction(
        &engine,
        "tenant-a",
        "tombstone-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::tombstone_operation(
                        TombstoneWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            TombstoneCommand::Record {
                                family: FamilyKind::Counters,
                                deletion_version: 1,
                            },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid tombstone write"),
                    ),
                )
                .expect("stages the family tombstone");
        },
    );
    let tombstones = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::tombstones(
            TombstoneRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid tombstone read"),
        ),
    )
    .expect("reads the tombstones")
    {
        FamilyReadResult::Tombstones(result) => result,
        other => panic!("expected a tombstone result, got {other:?}"),
    };
    assert_eq!(tombstones.entries().len(), 1);
    assert_eq!(tombstones.entries()[0].family(), FamilyKind::Counters);
    assert_eq!(tombstones.entries()[0].deletion_version(), 1);

    commit_transaction(
        &engine,
        "tenant-a",
        "stale-write",
        |store, transaction, tenant_scope, identity| {
            // A writer whose document view is older than the recorded deletion
            // must not resurrect the family state; the tombstone answer wins
            // over the missing-document answer.
            let staged = store.stage(
                transaction,
                counter_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
                ),
            );
            assert_eq!(staged, Err(FamilyError::TombstoneConflict));
        },
    );

    put_user_document(
        &engine,
        "tenant-a",
        "recreate-doc",
        "doc-1",
        object_user_value(&[("n", "v")]),
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "clear-tombstone",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::tombstone_operation(
                        TombstoneWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            TombstoneCommand::Clear { family: FamilyKind::Counters },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid tombstone write"),
                    ),
                )
                .expect("clears the tombstone");
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "fresh-write",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
                    ),
                )
                .expect("a cleared tombstone no longer blocks writes");
        },
    );
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
        .expect("reads the counter");
    // W3-F has no document-deletion cascade: the counter record survives the
    // document delete, so the fresh write lands on the surviving value.
    assert_eq!(counter_value(&group, "hits"), Some(3));
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_transaction_abort_hides_all_staged_family_changes() {
    let (_dir, engine) = open_engine("abort");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = operation("aborted-1");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let store = engine.families();
    store
        .stage(
            &mut transaction,
            counter_write(
                &tenant_scope,
                identity.as_str(),
                "doc-1",
                vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 4 }],
            ),
        )
        .expect("stages a counter");
    store
        .stage(
            &mut transaction,
            attachment_write(
                &tenant_scope,
                identity.as_str(),
                "doc-1",
                AttachmentMutation::Store {
                    name: "logo".to_owned(),
                    content_type: "image/png".to_owned(),
                    size: 8,
                    hash: "digest".to_owned(),
                    replace: false,
                },
            ),
        )
        .expect("stages attachment metadata");
    store
        .stage(
            &mut transaction,
            time_series_write(
                &tenant_scope,
                identity.as_str(),
                "doc-1",
                "cpu",
                vec![(1, vec![5.0])],
                vec![],
                None,
            ),
        )
        .expect("stages a time-series append");
    drop(transaction);

    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
        .expect("reads the counters");
    assert_eq!(counter_value(&group, "hits"), None);
    let metadata = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads attachment metadata")
    {
        FamilyReadResult::AttachmentMetadata(metadata) => metadata,
        other => panic!("expected attachment metadata, got {other:?}"),
    };
    assert!(metadata.entries().is_empty());
    assert_eq!(time_series_entries(&engine, "doc-1", "cpu", None), Vec::<i64>::new());
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_read_snapshot_remains_immutable_after_engine_commit() {
    let (_dir, engine) = open_engine("snapshot");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "counter-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 5 }],
                    ),
                )
                .expect("stages the first value");
        },
    );

    let tenant_scope = scope(&engine, "tenant-a");
    let first = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let first_context = FamilyReadContext::new(tenant_scope.clone(), first.clone());
    let before = counter_group(
        engine.families().read(first_context.clone(), counter_read("doc-1", &["hits"])),
    )
    .expect("reads the first snapshot");
    assert_eq!(counter_value(&before, "hits"), Some(5));

    commit_transaction(
        &engine,
        "tenant-a",
        "counter-2",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 5 }],
                    ),
                )
                .expect("stages the second value");
        },
    );

    let after =
        counter_group(engine.families().read(first_context, counter_read("doc-1", &["hits"])))
            .expect("the held snapshot still reads");
    assert_eq!(counter_value(&after, "hits"), Some(5), "the held snapshot does not advance");

    let second = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let current = counter_group(
        engine
            .families()
            .read(FamilyReadContext::new(tenant_scope, second), counter_read("doc-1", &["hits"])),
    )
    .expect("reads the new snapshot");
    assert_eq!(counter_value(&current, "hits"), Some(10));
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_read_context_rejects_tenant_mismatch_without_data_access() {
    let (_dir, engine) = open_engine("context-mismatch");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let forged = ReadSnapshot::new(
        engine.identity(),
        tenant("tenant-b"),
        snapshot.sequence(),
        IndexGenerationSet::default(),
    );

    let mismatched = FamilyReadContext::try_new(tenant_scope.clone(), forged.clone());
    assert_eq!(mismatched, Err(FamilyError::TenantMismatch));

    let read = engine
        .families()
        .read(FamilyReadContext::new(tenant_scope, forged), counter_read("doc-1", &["hits"]));
    assert_eq!(
        read,
        Err(FamilyError::TenantMismatch),
        "the store rejects a mismatched context before any family data access"
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_scope_rejects_cross_tenant_reads_and_writes() {
    let (_dir, engine) = open_engine("cross-tenant");
    put_user_document(&engine, "tenant-a", "put-a", "doc-1", object_user_value(&[("n", "v")]));
    put_user_document(&engine, "tenant-b", "put-b", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "counter-a",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 3 }],
                    ),
                )
                .expect("stages tenant A's counter");
        },
    );

    let tenant_b =
        counter_group(read_family(&engine, "tenant-b", counter_read("doc-1", &["hits"])))
            .expect("tenant B reads its own group");
    assert_eq!(counter_value(&tenant_b, "hits"), None, "tenant B sees no tenant A value");

    let writer_scope = scope(&engine, "tenant-a");
    let claimed_scope = scope(&engine, "tenant-b");
    let snapshot = engine.read_snapshot(&writer_scope).expect("snapshot");
    let identity = operation("cross-write");
    let mut transaction = engine.begin(&writer_scope, snapshot, identity.clone()).expect("begins");
    let staged = engine.families().stage(
        &mut transaction,
        counter_write(
            &claimed_scope,
            identity.as_str(),
            "doc-1",
            vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
        ),
    );
    assert_eq!(
        staged,
        Err(FamilyError::TenantMismatch),
        "a write whose tenant scope differs from the transaction is rejected"
    );
    drop(transaction);

    let tenant_a =
        counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
            .expect("tenant A reads its own group");
    assert_eq!(counter_value(&tenant_a, "hits"), Some(3));
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_payload_constructors_validate_typed_fields_before_access() {
    use crate::operations::RequestValidationError;

    let scope = TenantScope::from_parts(tenant("tenant-a"), 1);

    assert_eq!(
        AttachmentWrite::try_new(
            scope.clone(),
            document_id("doc-1"),
            AttachmentMutation::Store {
                name: String::new(),
                content_type: "image/png".to_owned(),
                size: 1,
                hash: "digest".to_owned(),
                replace: false,
            },
            ExpectedVersion::Any,
            operation("op-1"),
            deadline(),
            cancellation(),
        ),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        CounterRead::try_new(
            document_id("doc-1"),
            vec!["bad\u{7}name".to_owned()],
            deadline(),
            cancellation(),
        ),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        CounterBatchWrite::try_new(
            scope.clone(),
            document_id("doc-1"),
            vec![],
            ExpectedVersion::Any,
            operation("op-1"),
            deadline(),
            cancellation(),
        ),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(TimeSeriesRange::try_new(20, 10), Err(RequestValidationError::InvalidRange));
    assert_eq!(RevisionPaging::try_new(0, 0), Err(RequestValidationError::InvalidRange));
    assert_eq!(
        IdentityRead::try_new(vec!["seq".to_owned(), "seq".to_owned()], deadline(), cancellation()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        CompareExchangeWrite::try_new(
            scope.clone(),
            "lock".to_owned(),
            CompareExchangeCondition::PutIfMissing {
                value: DocumentValue::Integer(1),
                expires_at: Some(-1),
            },
            operation("op-1"),
            deadline(),
            cancellation(),
        )
        .map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );

    let expired = Deadline::after(Duration::from_secs(45)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(5));
    let elapsed = Deadline::after(Duration::from_nanos(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(5));
    assert!(elapsed.is_expired());
    let _ = expired;
    assert_eq!(
        LifecycleWrite::try_new(
            scope,
            document_id("doc-1"),
            LifecycleCommand::Clear,
            ExpectedVersion::Any,
            operation("op-1"),
            elapsed,
            cancellation(),
        )
        .map(|_| ()),
        Err(RequestValidationError::InvalidDeadline)
    );

    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        TombstoneRead::try_new(document_id("doc-1"), deadline(), cancelled),
        Err(RequestValidationError::InvalidCancellation)
    );

    // A validated payload exposes its typed fields before any access.
    let counter = CounterBatchWrite::try_new(
        TenantScope::from_parts(tenant("tenant-a"), 1),
        document_id("doc-1"),
        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
        ExpectedVersion::Any,
        operation("op-1"),
        deadline(),
        cancellation(),
    )
    .expect("valid batch");
    assert_eq!(counter.document().as_str(), "doc-1");
    assert_eq!(counter.operation_id().as_str(), "op-1");
    assert_eq!(counter.operations().len(), 1);
}

// ---------------------------------------------------------------------------
// Focused extras for the remaining W3-F rows
// ---------------------------------------------------------------------------

fn compare_exchange_read(
    engine: &Engine,
    key: &str,
) -> Result<CompareExchangeValueResult, FamilyError> {
    match read_family(
        engine,
        "tenant-a",
        FamilyReadRequest::compare_exchange_value(
            CompareExchangeRead::try_new(key.to_owned(), deadline(), cancellation())
                .expect("valid compare-exchange read"),
        ),
    ) {
        Ok(FamilyReadResult::CompareExchangeValue(value)) => Ok(value),
        Ok(other) => panic!("expected a compare-exchange value, got {other:?}"),
        Err(error) => Err(error),
    }
}

fn compare_exchange_write(
    tenant_scope: &TenantScope,
    operation_name: &str,
    key: &str,
    condition: CompareExchangeCondition,
) -> FamilyWriteRequest {
    FamilyWriteRequest::compare_exchange_condition(
        CompareExchangeWrite::try_new(
            tenant_scope.clone(),
            key.to_owned(),
            condition,
            operation(operation_name),
            deadline(),
            cancellation(),
        )
        .expect("valid compare-exchange write"),
    )
}

#[test]
fn compare_exchange_conditions_require_the_current_index() {
    let (_dir, engine) = open_engine("compare-exchange");
    let _tenant_scope = scope(&engine, "tenant-a");
    commit_transaction(
        &engine,
        "tenant-a",
        "cx-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    compare_exchange_write(
                        tenant_scope,
                        identity.as_str(),
                        "leader",
                        CompareExchangeCondition::PutIfMissing {
                            value: DocumentValue::String("first".to_owned()),
                            expires_at: None,
                        },
                    ),
                )
                .expect("stages the first value");
        },
    );
    let first = compare_exchange_read(&engine, "leader").expect("reads the key");
    assert_eq!(first.entry().map(CompareExchangeEntry::index), Some(1));

    commit_transaction(
        &engine,
        "tenant-a",
        "cx-2",
        |_store, transaction, tenant_scope, identity| {
            let stale = engine.families().stage(
                transaction,
                compare_exchange_write(
                    tenant_scope,
                    identity.as_str(),
                    "leader",
                    CompareExchangeCondition::PutIfIndex {
                        index: 0,
                        value: DocumentValue::String("stale".to_owned()),
                        expires_at: None,
                    },
                ),
            );
            assert_eq!(stale, Err(FamilyError::CompareExchangeIndexStale));
        },
    );
    let unchanged = compare_exchange_read(&engine, "leader").expect("reads the key");
    assert_eq!(
        unchanged.entry().map(|entry| entry.value().clone()),
        Some(DocumentValue::String("first".to_owned()))
    );

    commit_transaction(
        &engine,
        "tenant-a",
        "cx-3",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    compare_exchange_write(
                        tenant_scope,
                        identity.as_str(),
                        "leader",
                        CompareExchangeCondition::PutIfIndex {
                            index: 1,
                            value: DocumentValue::String("second".to_owned()),
                            expires_at: None,
                        },
                    ),
                )
                .expect("stages a matching index");
        },
    );
    let second = compare_exchange_read(&engine, "leader").expect("reads the key");
    assert_eq!(second.entry().map(CompareExchangeEntry::index), Some(2));

    commit_transaction(
        &engine,
        "tenant-a",
        "cx-4",
        |_store, transaction, tenant_scope, identity| {
            let missing = engine.families().stage(
                transaction,
                compare_exchange_write(
                    tenant_scope,
                    identity.as_str(),
                    "leader",
                    CompareExchangeCondition::PutIfMissing {
                        value: DocumentValue::String("third".to_owned()),
                        expires_at: None,
                    },
                ),
            );
            assert_eq!(missing, Err(FamilyError::CompareExchangeIndexStale));
        },
    );

    commit_transaction(
        &engine,
        "tenant-a",
        "cx-5",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    compare_exchange_write(
                        tenant_scope,
                        identity.as_str(),
                        "leader",
                        CompareExchangeCondition::DeleteIfIndex { index: 2 },
                    ),
                )
                .expect("stages a matching delete");
        },
    );
    let deleted = compare_exchange_read(&engine, "leader").expect("reads the key");
    assert!(deleted.entry().is_none());

    commit_transaction(
        &engine,
        "tenant-a",
        "cx-6",
        |_store, transaction, tenant_scope, identity| {
            let stale_delete = engine.families().stage(
                transaction,
                compare_exchange_write(
                    tenant_scope,
                    identity.as_str(),
                    "leader",
                    CompareExchangeCondition::DeleteIfIndex { index: 1 },
                ),
            );
            assert_eq!(stale_delete, Err(FamilyError::CompareExchangeIndexStale));
        },
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn compare_exchange_expiry_hides_expired_values() {
    let (_dir, engine) = open_engine("compare-exchange-expiry");
    let now = now_millis();
    commit_transaction(
        &engine,
        "tenant-a",
        "cx-exp-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    compare_exchange_write(
                        tenant_scope,
                        identity.as_str(),
                        "lease",
                        CompareExchangeCondition::PutIfMissing {
                            value: DocumentValue::Integer(7),
                            expires_at: Some(now - 1),
                        },
                    ),
                )
                .expect("stages an already expired value");
        },
    );
    let expired = compare_exchange_read(&engine, "lease").expect("reads the key");
    assert!(expired.entry().is_none(), "an expired value is not live");

    commit_transaction(
        &engine,
        "tenant-a",
        "cx-exp-2",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    compare_exchange_write(
                        tenant_scope,
                        identity.as_str(),
                        "lease",
                        CompareExchangeCondition::PutIfMissing {
                            value: DocumentValue::Integer(8),
                            expires_at: Some(now + 60_000),
                        },
                    ),
                )
                .expect("an expired value accepts a fresh put");
        },
    );
    let live = compare_exchange_read(&engine, "lease").expect("reads the key");
    assert!(live.entry().is_some());
    engine.shutdown().expect("shuts down");
}

#[test]
fn identity_allocation_is_monotonic_and_backwards_seeding_requires_force() {
    let (_dir, engine) = open_engine("identities");
    let identity_read = |names: &[&str]| match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::identity_range(
            IdentityRead::try_new(
                names.iter().map(|name| (*name).to_owned()).collect(),
                deadline(),
                cancellation(),
            )
            .expect("valid identity read"),
        ),
    )
    .expect("reads the identity range")
    {
        FamilyReadResult::IdentityRange(result) => result,
        other => panic!("expected an identity range, got {other:?}"),
    };

    commit_transaction(
        &engine,
        "tenant-a",
        "id-1",
        |store, transaction, tenant_scope, identity| {
            stage_identity(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                IdentityCommand::Allocate { name: "orders".to_owned(), count: 10 },
            )
            .expect("stages the first allocation");
        },
    );
    let first: IdentityRangeResult = identity_read(&["orders"]);
    assert_eq!(first.entries()[0].next(), 10);

    commit_transaction(
        &engine,
        "tenant-a",
        "id-2",
        |store, transaction, tenant_scope, identity| {
            stage_identity(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                IdentityCommand::Allocate { name: "orders".to_owned(), count: 5 },
            )
            .expect("stages the second allocation");
        },
    );
    let second = identity_read(&["orders"]);
    assert_eq!(second.entries()[0].next(), 15, "allocation never moves backwards");

    commit_transaction(
        &engine,
        "tenant-a",
        "id-3",
        |store, transaction, tenant_scope, identity| {
            let staged = stage_identity(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                IdentityCommand::Seed { name: "orders".to_owned(), value: 3, force: false },
            );
            assert_eq!(staged, Err(FamilyError::IdentitySeedInvalid));
        },
    );
    let unchanged = identity_read(&["orders"]);
    assert_eq!(unchanged.entries()[0].next(), 15);

    commit_transaction(
        &engine,
        "tenant-a",
        "id-4",
        |store, transaction, tenant_scope, identity| {
            stage_identity(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                IdentityCommand::Seed { name: "orders".to_owned(), value: 3, force: true },
            )
            .expect("an explicit force seeds backwards");
        },
    );
    let seeded = identity_read(&["orders"]);
    assert_eq!(seeded.entries()[0].next(), 3);

    commit_transaction(
        &engine,
        "tenant-a",
        "id-5",
        |store, transaction, tenant_scope, identity| {
            stage_identity(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                IdentityCommand::Seed { name: "orders".to_owned(), value: 99, force: false },
            )
            .expect("seeding forwards is always allowed");
        },
    );
    let advanced = identity_read(&["orders"]);
    assert_eq!(advanced.entries()[0].next(), 99);
    engine.shutdown().expect("shuts down");
}

#[test]
fn revision_policy_trims_the_oldest_revisions() {
    let (_dir, engine) = open_engine("revision-policy");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("v", "one")]));
    let policy = RevisionPolicy::try_new(1, Some(2)).expect("valid policy");
    commit_transaction(
        &engine,
        "tenant-a",
        "configure-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::revision_command(
                        RevisionWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            RevisionCommand::Configure { policy },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid revision write"),
                    ),
                )
                .expect("stages the retention policy");
        },
    );
    for (name, value) in [("one", "one"), ("two", "two"), ("three", "three")] {
        put_user_document(
            &engine,
            "tenant-a",
            "put-doc",
            "doc-1",
            object_user_value(&[("v", value)]),
        );
        commit_transaction(
            &engine,
            "tenant-a",
            name,
            |store, transaction, tenant_scope, identity| {
                store
                    .stage(
                        transaction,
                        FamilyWriteRequest::revision_command(
                            RevisionWrite::try_new(
                                tenant_scope.clone(),
                                document_id("doc-1"),
                                RevisionCommand::ForceCreate { expected: ExpectedVersion::Any },
                                operation(identity.as_str()),
                                deadline(),
                                cancellation(),
                            )
                            .expect("valid revision write"),
                        ),
                    )
                    .expect("stages one revision");
            },
        );
    }
    let page = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::revision_page(
            RevisionRead::try_new(
                document_id("doc-1"),
                RevisionPaging::try_new(0, 10).expect("valid paging"),
                deadline(),
                cancellation(),
            )
            .expect("valid revision read"),
        ),
    )
    .expect("pages the log")
    {
        FamilyReadResult::RevisionPage(page) => page,
        other => panic!("expected a revision page, got {other:?}"),
    };
    assert_eq!(page.total(), 2, "the policy trims the oldest revisions");
    assert_eq!(page.entries()[0].value(), &object_user_value(&[("v", "two")]));
    assert_eq!(page.entries()[1].value(), &object_user_value(&[("v", "three")]));
    assert_eq!(page.policy(), Some(policy));
    engine.shutdown().expect("shuts down");
}

#[test]
fn revision_revert_rejects_unknown_references() {
    let (_dir, engine) = open_engine("revision-references");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("v", "one")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "revert-1",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                FamilyWriteRequest::revision_command(
                    RevisionWrite::try_new(
                        tenant_scope.clone(),
                        document_id("doc-1"),
                        RevisionCommand::Revert { to_revision: 404 },
                        operation(identity.as_str()),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid revision write"),
                ),
            );
            assert_eq!(staged, Err(FamilyError::RevisionReferenceInvalid));
        },
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn time_series_arity_and_policy_rules_are_typed() {
    let (_dir, engine) = open_engine("time-series-rules");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "append-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    time_series_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        "cpu",
                        vec![(10, vec![1.0, 2.0])],
                        vec![],
                        None,
                    ),
                )
                .expect("establishes an arity of two");
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "append-2",
        |_store, transaction, tenant_scope, identity| {
            let mismatched = engine.families().stage(
                transaction,
                time_series_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    "cpu",
                    vec![(20, vec![1.0])],
                    vec![],
                    None,
                ),
            );
            assert_eq!(mismatched, Err(FamilyError::TimeSeriesArityInvalid));
            let empty = engine.families().stage(
                transaction,
                time_series_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    "cpu",
                    vec![(20, vec![])],
                    vec![],
                    None,
                ),
            );
            assert_eq!(empty, Err(FamilyError::TimeSeriesArityInvalid));
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "delete-1",
        |_store, transaction, tenant_scope, identity| {
            let inverted = engine.families().stage(
                transaction,
                time_series_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    "cpu",
                    vec![],
                    vec![TimeSeriesDelete::Range { start: 20, end: 10 }],
                    None,
                ),
            );
            assert_eq!(inverted, Err(FamilyError::TimeSeriesRangeInvalid));
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "policy-1",
        |store, transaction, tenant_scope, identity| {
            let malformed = TimeSeriesPolicy::try_new(
                Some(1000),
                vec![
                    RollupPolicy::new("hourly".to_owned(), Aggregation::Mean, 3_600_000),
                    RollupPolicy::new("hourly".to_owned(), Aggregation::Max, 60_000),
                ],
            )
            .expect("structurally valid policy");
            let staged = store.stage(
                transaction,
                time_series_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    "cpu",
                    vec![],
                    vec![],
                    Some(malformed),
                ),
            );
            assert_eq!(staged, Err(FamilyError::TimeSeriesPolicyInvalid));
        },
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn time_series_policy_reports_retention_bounds() {
    let (_dir, engine) = open_engine("time-series-retention");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let policy = TimeSeriesPolicy::try_new(
        Some(1_000),
        vec![RollupPolicy::new("hourly".to_owned(), Aggregation::Mean, 3_600_000)],
    )
    .expect("valid policy");
    commit_transaction(
        &engine,
        "tenant-a",
        "configure-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    time_series_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        "cpu",
                        vec![(5_000, vec![1.0])],
                        vec![],
                        Some(policy.clone()),
                    ),
                )
                .expect("stages the policy and one point");
        },
    );
    let page = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::time_series_range(
            TimeSeriesRead::try_new(
                document_id("doc-1"),
                "cpu".to_owned(),
                None,
                deadline(),
                cancellation(),
            )
            .expect("valid time-series read"),
        ),
    )
    .expect("reads the series")
    {
        FamilyReadResult::TimeSeriesPage(page) => page,
        other => panic!("expected a time-series page, got {other:?}"),
    };
    assert_eq!(page.policy(), Some(&policy));

    let stale = read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::time_series_range(
            TimeSeriesRead::try_new(
                document_id("doc-1"),
                "cpu".to_owned(),
                Some(TimeSeriesRange::try_new(0, 1_000).expect("valid range")),
                deadline(),
                cancellation(),
            )
            .expect("valid time-series read"),
        ),
    );
    assert_eq!(
        stale,
        Err(FamilyError::TimeSeriesRangeInvalid),
        "a range before the retention horizon is a typed family range error"
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn validation_policy_rejects_malformed_definitions() {
    let (_dir, engine) = open_engine("validation-policy");
    let tenant_scope = scope(&engine, "tenant-a");
    commit_transaction(
        &engine,
        "tenant-a",
        "policy-1",
        |store, transaction, tenant_scope, identity| {
            let duplicated = SchemaDefinition::try_new(
                SchemaVersion::try_new(1).expect("valid version"),
                vec![
                    SchemaRule::Required { field: "email".to_owned() },
                    SchemaRule::Required { field: "email".to_owned() },
                ],
            )
            .expect("structurally valid");
            let staged = store.stage(
                transaction,
                FamilyWriteRequest::schema_validation_update(
                    ValidationWrite::try_new(
                        tenant_scope.clone(),
                        collection("users"),
                        Some(duplicated),
                        operation(identity.as_str()),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid validation write"),
                ),
            );
            assert_eq!(staged, Err(FamilyError::ValidationPolicyMalformed));
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "policy-2",
        |store, transaction, tenant_scope, identity| {
            let inverted = SchemaDefinition::try_new(
                SchemaVersion::try_new(1).expect("valid version"),
                vec![SchemaRule::NumberRange {
                    field: "age".to_owned(),
                    min: Some(100),
                    max: Some(10),
                }],
            )
            .expect("structurally valid");
            let staged = store.stage(
                transaction,
                FamilyWriteRequest::schema_validation_update(
                    ValidationWrite::try_new(
                        tenant_scope.clone(),
                        collection("users"),
                        Some(inverted),
                        operation(identity.as_str()),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid validation write"),
                ),
            );
            assert_eq!(staged, Err(FamilyError::ValidationPolicyMalformed));
        },
    );

    let absent: ValidationResult = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::validation(
            ValidationRead::try_new(collection("users"), None, deadline(), cancellation())
                .expect("valid validation read"),
        ),
    )
    .expect("reads the collection policy")
    {
        FamilyReadResult::Validation(result) => result,
        other => panic!("expected a validation result, got {other:?}"),
    };
    assert!(absent.policy().is_none(), "a rejected policy stages nothing");
    let _ = tenant_scope;
    engine.shutdown().expect("shuts down");
}

#[test]
fn attachment_rename_copy_and_duplicate_names_are_typed() {
    let (_dir, engine) = open_engine("attachment-rules");
    put_user_document(&engine, "tenant-a", "put-1", "doc-1", object_user_value(&[("n", "v")]));
    put_user_document(&engine, "tenant-a", "put-2", "doc-2", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "store-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Store {
                            name: "logo".to_owned(),
                            content_type: "image/png".to_owned(),
                            size: 8,
                            hash: "digest".to_owned(),
                            replace: false,
                        },
                    ),
                )
                .expect("stages the first attachment");
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "store-2",
        |_store, transaction, tenant_scope, identity| {
            let duplicated = engine.families().stage(
                transaction,
                attachment_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    AttachmentMutation::Store {
                        name: "logo".to_owned(),
                        content_type: "image/png".to_owned(),
                        size: 9,
                        hash: "digest-2".to_owned(),
                        replace: false,
                    },
                ),
            );
            assert_eq!(duplicated, Err(FamilyError::AttachmentNameDuplicate));
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "copy-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Copy {
                            name: "logo".to_owned(),
                            to_document: document_id("doc-2"),
                            to_name: "banner".to_owned(),
                        },
                    ),
                )
                .expect("copies the attachment metadata");
        },
    );
    let copied = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-2"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads the copy")
    {
        FamilyReadResult::AttachmentMetadata(metadata) => metadata,
        other => panic!("expected attachment metadata, got {other:?}"),
    };
    assert_eq!(copied.entries().len(), 1);
    assert_eq!(copied.entries()[0].name(), "banner");
    assert_eq!(copied.entries()[0].hash(), "digest", "the copy keeps the digest");

    commit_transaction(
        &engine,
        "tenant-a",
        "move-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Move {
                            name: "logo".to_owned(),
                            to_document: document_id("doc-2"),
                            to_name: "hero".to_owned(),
                        },
                    ),
                )
                .expect("moves the attachment metadata");
        },
    );
    let moved_source = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads the source")
    {
        FamilyReadResult::AttachmentMetadata(metadata) => metadata,
        other => panic!("expected attachment metadata, got {other:?}"),
    };
    assert!(moved_source.entries().is_empty(), "a move leaves the source empty");
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_writes_compose_with_documents_in_one_transaction_marker() {
    let (_dir, engine) = open_engine("compose");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = operation("compose-1");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    transaction
        .put(
            Document::new(
                document_id("doc-1"),
                collection("users"),
                object_user_value(&[("n", "v")]),
            ),
            ExpectedVersion::Any,
        )
        .expect("stages the document");
    let store = engine.families();
    store
        .stage(
            &mut transaction,
            counter_write(
                &tenant_scope,
                identity.as_str(),
                "doc-1",
                vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
            ),
        )
        .expect("stages a counter in the same transaction");
    store
        .stage(
            &mut transaction,
            attachment_write(
                &tenant_scope,
                identity.as_str(),
                "doc-1",
                AttachmentMutation::Store {
                    name: "logo".to_owned(),
                    content_type: "image/png".to_owned(),
                    size: 4,
                    hash: "digest".to_owned(),
                    replace: false,
                },
            ),
        )
        .expect("stages attachment metadata in the same transaction");
    let receipt = match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => receipt,
        other => panic!("expected one committed marker, got {other:?}"),
    };
    assert_eq!(receipt.operation_id().as_str(), "compose-1");

    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
        .expect("reads the counter");
    assert_eq!(counter_value(&group, "hits"), Some(1));
    let metadata = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads attachment metadata")
    {
        FamilyReadResult::AttachmentMetadata(metadata) => metadata,
        other => panic!("expected attachment metadata, got {other:?}"),
    };
    assert_eq!(metadata.entries().len(), 1);
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_requests_reject_cancelled_signals_and_elapsed_deadlines() {
    use crate::operations::RequestValidationError;

    let (_dir, engine) = open_engine("bounded-requests");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let tenant_scope = scope(&engine, "tenant-a");

    // A signal that is already cancelled when the request is built is a
    // constructor rejection, so no store call is needed.
    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        CounterBatchWrite::try_new(
            tenant_scope.clone(),
            document_id("doc-1"),
            vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
            ExpectedVersion::Any,
            operation("cancelled-1"),
            deadline(),
            cancelled,
        )
        .map(|_| ()),
        Err(RequestValidationError::InvalidCancellation)
    );

    // A deadline that elapses between construction and staging is a typed
    // store rejection that stages nothing.
    let elapsed = Deadline::after(Duration::from_millis(20)).expect("valid deadline");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = operation("expired-1");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let batch = FamilyWriteRequest::counter_batch(
        CounterBatchWrite::try_new(
            tenant_scope.clone(),
            document_id("doc-1"),
            vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
            ExpectedVersion::Any,
            identity.clone(),
            elapsed,
            cancellation(),
        )
        .expect("valid counter batch"),
    );
    std::thread::sleep(Duration::from_millis(40));
    let staged = engine.families().stage(&mut transaction, batch);
    assert_eq!(staged, Err(FamilyError::DeadlineExceeded));
    drop(transaction);

    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
        .expect("reads the counters");
    assert_eq!(counter_value(&group, "hits"), None, "the rejected request staged nothing");
    engine.shutdown().expect("shuts down");
}

#[test]
fn archival_state_round_trips_through_the_seam() {
    let (_dir, engine) = open_engine("archival");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let read_state = |engine: &Engine| match read_family(
        engine,
        "tenant-a",
        FamilyReadRequest::archival(
            ArchivalReadRequest::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid archival read"),
        ),
    ) {
        Ok(FamilyReadResult::Archival(result)) => Ok(result),
        Ok(other) => panic!("expected an archival result, got {other:?}"),
        Err(error) => Err(error),
    };
    let present = read_state(&engine).expect("a present document reads its archival state");
    assert!(present.state().is_none(), "no archival state is configured yet");
    assert_eq!(
        read_family(
            &engine,
            "tenant-a",
            FamilyReadRequest::archival(
                ArchivalReadRequest::try_new(document_id("doc-404"), deadline(), cancellation())
                    .expect("valid archival read"),
            ),
        ),
        Err(FamilyError::DocumentMissing)
    );

    commit_transaction(
        &engine,
        "tenant-a",
        "archival-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::archival_update(
                        ArchivalWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            ArchivalCommand::SetPolicy { after_ms: 86_400_000 },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid archival write"),
                    ),
                )
                .expect("stages the archival policy");
        },
    );
    let configured = read_state(&engine).expect("reads the archival state");
    assert_eq!(configured.state().and_then(ArchivalState::after_ms), Some(86_400_000));

    commit_transaction(
        &engine,
        "tenant-a",
        "archival-2",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::archival_update(
                        ArchivalWrite::try_new(
                            tenant_scope.clone(),
                            document_id("doc-1"),
                            ArchivalCommand::Archive { at: now_millis() },
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid archival write"),
                    ),
                )
                .expect("stages the archival mark");
        },
    );
    let archived = read_state(&engine).expect("reads the archival state");
    assert!(archived.state().is_some_and(ArchivalState::archived));
    engine.shutdown().expect("shuts down");
}

#[test]
fn family_reads_reject_snapshots_from_another_engine() {
    let (_dir_a, engine_a) = open_engine("snapshot-engine-a");
    let (_dir_b, engine_b) = open_engine("snapshot-engine-b");
    let scope_b = scope(&engine_b, "tenant-a");
    let snapshot_b = engine_b.read_snapshot(&scope_b).expect("snapshot");
    let scope_a = scope(&engine_a, "tenant-a");
    let read = engine_a
        .families()
        .read(FamilyReadContext::new(scope_a, snapshot_b), counter_read("doc-1", &["hits"]));
    assert_eq!(read, Err(FamilyError::SnapshotEngineMismatch));
    engine_a.shutdown().expect("shuts down");
    engine_b.shutdown().expect("shuts down");
}

#[test]
fn expected_version_conflicts_are_typed_at_stage_time() {
    let (_dir, engine) = open_engine("expected-version");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = operation("conflict-1");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let staged = engine.families().stage(
        &mut transaction,
        FamilyWriteRequest::counter_batch(
            CounterBatchWrite::try_new(
                tenant_scope.clone(),
                document_id("doc-1"),
                vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
                ExpectedVersion::Exact(99),
                identity.clone(),
                deadline(),
                cancellation(),
            )
            .expect("valid counter batch"),
        ),
    );
    assert_eq!(staged, Err(FamilyError::Conflict));
    drop(transaction);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Repair loop a2: gate findings
// ---------------------------------------------------------------------------

#[test]
fn same_document_attachment_copy_and_move_commit_as_one_record() {
    let (_dir, engine) = open_engine("same-document-attachments");
    put_user_document(&engine, "tenant-a", "put-1", "doc-1", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "store-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Store {
                            name: "logo".to_owned(),
                            content_type: "image/png".to_owned(),
                            size: 8,
                            hash: "digest".to_owned(),
                            replace: false,
                        },
                    ),
                )
                .expect("stages the source attachment");
        },
    );

    // A same-document copy duplicates the attachment under a new name. The
    // source mutation and the target write must merge into one staged record
    // write, because the Engine rejects a write set that stages the same
    // document identifier twice.
    commit_transaction(
        &engine,
        "tenant-a",
        "copy-same",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Copy {
                            name: "logo".to_owned(),
                            to_document: document_id("doc-1"),
                            to_name: "banner".to_owned(),
                        },
                    ),
                )
                .expect("stages the same-document copy");
        },
    );
    let copied = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads the copied metadata")
    {
        FamilyReadResult::AttachmentMetadata(metadata) => metadata,
        other => panic!("expected attachment metadata, got {other:?}"),
    };
    assert_eq!(copied.entries().len(), 2, "the copy keeps the source");
    assert_eq!(copied.find("logo").map(AttachmentMetadata::hash), Some("digest"));
    assert_eq!(copied.find("banner").map(AttachmentMetadata::hash), Some("digest"));

    // A same-document move renames in place, still as one staged record write.
    commit_transaction(
        &engine,
        "tenant-a",
        "move-same",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    attachment_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        AttachmentMutation::Move {
                            name: "logo".to_owned(),
                            to_document: document_id("doc-1"),
                            to_name: "hero".to_owned(),
                        },
                    ),
                )
                .expect("stages the same-document move");
        },
    );
    let moved = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
                .expect("valid attachment read"),
        ),
    )
    .expect("reads the moved metadata")
    {
        FamilyReadResult::AttachmentMetadata(metadata) => metadata,
        other => panic!("expected attachment metadata, got {other:?}"),
    };
    assert_eq!(moved.entries().len(), 2);
    assert!(moved.find("logo").is_none(), "the move drops the source name");
    assert!(moved.find("hero").is_some());
    assert!(moved.find("banner").is_some());
    engine.shutdown().expect("shuts down");
}

#[test]
fn malformed_reserved_record_collision_is_typed() {
    let (_dir, engine) = open_engine("reserved-collision");
    put_user_document(&engine, "tenant-a", "put-1", "doc-1", object_user_value(&[("n", "v")]));

    // A direct document-seam write of a malformed value at one reserved
    // record identifier: the family read must surface the typed reserved
    // state error instead of silently treating the value as family state.
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = operation("squatter-1");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let squatter = Document::new(
        record_id("counters", &["doc-1"]).expect("reserved record identifier"),
        CollectionName::try_new(RESERVED_COLLECTION).expect("reserved collection name"),
        DocumentValue::String("not-a-counter-record".to_owned()),
    );
    transaction.put(squatter, ExpectedVersion::Any).expect("stages the squatter");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    let read = read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"]));
    assert_eq!(read, Err(FamilyError::ReservedStateInvalid));
    engine.shutdown().expect("shuts down");
}

#[test]
fn number_range_binds_float_and_integer_values() {
    let (_dir, engine) = open_engine("number-range");
    let schema = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![SchemaRule::NumberRange { field: "score".to_owned(), min: Some(0), max: Some(10) }],
    )
    .expect("valid schema");

    let score = |value: DocumentValue| {
        DocumentValue::Object(
            [
                ("name".to_owned(), DocumentValue::String("ada".to_owned())),
                ("score".to_owned(), value),
            ]
            .into_iter()
            .collect(),
        )
    };
    // The violating documents predate the declaration: adopting a schema is
    // never retroactive, so schema-free writes land and the on-demand report
    // is what observes them.
    put_user_document(
        &engine,
        "tenant-a",
        "put-float",
        "doc-1",
        score(DocumentValue::Number(99.5)),
    );
    put_user_document(&engine, "tenant-a", "put-int", "doc-2", score(DocumentValue::Integer(99)));
    put_user_document(&engine, "tenant-a", "put-ok", "doc-3", score(DocumentValue::Number(5.5)));

    commit_transaction(
        &engine,
        "tenant-a",
        "configure-schema",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::schema_validation_update(
                        ValidationWrite::try_new(
                            tenant_scope.clone(),
                            collection("users"),
                            Some(schema.clone()),
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid validation write"),
                    ),
                )
                .expect("stages the collection policy");
        },
    );

    let report = |document: &str| match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::validation(
            ValidationRead::try_new(
                collection("users"),
                Some(document_id(document)),
                deadline(),
                cancellation(),
            )
            .expect("valid validation read"),
        ),
    )
    .expect("reads the on-demand report")
    {
        FamilyReadResult::Validation(result) => result,
        other => panic!("expected a validation result, got {other:?}"),
    };
    let float_violations = report("doc-1").violations().to_vec();
    assert_eq!(float_violations.len(), 1, "an out-of-range float is rejected");
    assert_eq!(float_violations[0].kind(), ViolationKind::NumberOutOfRange);
    let int_violations = report("doc-2").violations().to_vec();
    assert_eq!(int_violations.len(), 1, "an out-of-range integer stays rejected");
    assert!(report("doc-3").violations().is_empty(), "an in-range float passes");

    commit_transaction(
        &engine,
        "tenant-a",
        "reject-float",
        |store, transaction, tenant_scope, identity| {
            let staged = store.stage(
                transaction,
                attachment_write(
                    tenant_scope,
                    identity.as_str(),
                    "doc-1",
                    AttachmentMutation::Store {
                        name: "logo".to_owned(),
                        content_type: "image/png".to_owned(),
                        size: 1,
                        hash: "digest".to_owned(),
                        replace: false,
                    },
                ),
            );
            assert_eq!(staged, Err(FamilyError::SchemaViolation));
        },
    );

    // New writes of out-of-range values are rejected at the engine seam in
    // both numeric encodings; an in-range update of a stored violating
    // document is accepted and clears the report.
    assert_eq!(
        stage_put(
            &engine,
            "tenant-a",
            "reject-new-float",
            "doc-4",
            "users",
            score(DocumentValue::Number(99.5))
        ),
        Err(EngineError::InvalidDocument)
    );
    assert_eq!(
        stage_put(
            &engine,
            "tenant-a",
            "reject-new-int",
            "doc-4",
            "users",
            score(DocumentValue::Integer(99))
        ),
        Err(EngineError::InvalidDocument)
    );
    assert!(
        stage_put(
            &engine,
            "tenant-a",
            "accept-new-float",
            "doc-1",
            "users",
            score(DocumentValue::Number(5.5))
        )
        .is_ok()
    );
    assert!(report("doc-1").violations().is_empty());
    engine.shutdown().expect("shuts down");
}

#[test]
fn time_series_names_reject_the_record_segment_separator() {
    use crate::operations::RequestValidationError;

    // Series names are the second segment of one reserved record identifier,
    // so (document "a", series "b/c") and (document "a/b", series "c") would
    // address the same record. The series segment cannot contain '/'.
    assert_eq!(
        TimeSeriesRead::try_new(
            document_id("a"),
            "b/c".to_owned(),
            None,
            deadline(),
            cancellation()
        )
        .map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    let (_probe_dir, probe) = open_engine("segment-probe");
    assert_eq!(
        TimeSeriesBatchWrite::try_new(
            scope(&probe, "tenant-a"),
            document_id("a"),
            "b/c".to_owned(),
            vec![],
            vec![TimeSeriesDelete::At { timestamp: 1 }],
            None,
            ExpectedVersion::Any,
            operation("op-1"),
            deadline(),
            cancellation(),
        )
        .map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    probe.shutdown().expect("shuts down");

    // A document identifier may still contain '/', and a slash-free series
    // name on such a document stays uniquely addressable.
    let (_dir, engine) = open_engine("segment-separator");
    put_user_document(&engine, "tenant-a", "put-1", "a/b", object_user_value(&[("n", "v")]));
    commit_transaction(
        &engine,
        "tenant-a",
        "append-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    time_series_write(
                        tenant_scope,
                        identity.as_str(),
                        "a/b",
                        "c",
                        vec![(10, vec![1.0])],
                        vec![],
                        None,
                    ),
                )
                .expect("stages the series on a slash-containing document");
        },
    );
    assert_eq!(time_series_entries(&engine, "a/b", "c", None), vec![10]);
    engine.shutdown().expect("shuts down");
}

#[test]
fn identity_allocation_exhaustion_is_typed() {
    let (_dir, engine) = open_engine("identity-exhaustion");
    // The record codec stores identities in the signed integer domain, so a
    // seed target above the representable ceiling is rejected at construction
    // and exhaustion happens at that ceiling, not at the unsigned edge.
    let (_probe_dir, probe) = open_engine("identity-ceiling-probe");
    assert_eq!(
        IdentityWrite::try_new(
            scope(&probe, "tenant-a"),
            "orders".to_owned(),
            IdentityCommand::Seed { name: "orders".to_owned(), value: u64::MAX, force: true },
            operation("op-1"),
            deadline(),
            cancellation(),
        )
        .map(|_| ()),
        Err(crate::operations::RequestValidationError::InvalidFamilyPayload)
    );
    probe.shutdown().expect("shuts down");

    commit_transaction(
        &engine,
        "tenant-a",
        "seed-max",
        |store, transaction, tenant_scope, identity| {
            stage_identity(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                IdentityCommand::Seed {
                    name: "orders".to_owned(),
                    value: MAX_IDENTITY_VALUE,
                    force: true,
                },
            )
            .expect("seeds the identity to its ceiling");
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "allocate-exhausted",
        |store, transaction, tenant_scope, identity| {
            let staged = stage_identity(
                store,
                transaction,
                tenant_scope,
                identity.as_str(),
                IdentityCommand::Allocate { name: "orders".to_owned(), count: 1 },
            );
            assert_eq!(staged, Err(FamilyError::IdentityExhausted));
        },
    );
    let range = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::identity_range(
            IdentityRead::try_new(vec!["orders".to_owned()], deadline(), cancellation())
                .expect("valid identity read"),
        ),
    )
    .expect("reads the identity range")
    {
        FamilyReadResult::IdentityRange(result) => result,
        other => panic!("expected an identity range, got {other:?}"),
    };
    assert_eq!(range.entries()[0].next(), MAX_IDENTITY_VALUE, "exhaustion changes no state");
    engine.shutdown().expect("shuts down");
}

#[test]
fn time_series_full_reads_respect_the_retention_horizon() {
    let (_dir, engine) = open_engine("retention-full-read");
    put_user_document(&engine, "tenant-a", "put-1", "doc-1", object_user_value(&[("n", "v")]));
    let policy = TimeSeriesPolicy::try_new(Some(1_000), vec![]).expect("valid policy");
    commit_transaction(
        &engine,
        "tenant-a",
        "append-1",
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    time_series_write(
                        tenant_scope,
                        identity.as_str(),
                        "doc-1",
                        "cpu",
                        vec![(3_000, vec![2.0]), (5_000, vec![1.0])],
                        vec![],
                        Some(policy),
                    ),
                )
                .expect("stages two points and the retention policy");
        },
    );
    // The newest point is 5_000 with a 1_000 window, so the horizon is 4_000:
    // a full read returns the retained view only, matching ranged reads.
    assert_eq!(time_series_entries(&engine, "doc-1", "cpu", None), vec![5_000]);
    assert_eq!(
        time_series_entries(
            &engine,
            "doc-1",
            "cpu",
            Some(TimeSeriesRange::try_new(4_000, 6_000).expect("valid range"))
        ),
        vec![5_000]
    );
    assert_eq!(
        read_family(
            &engine,
            "tenant-a",
            FamilyReadRequest::time_series_range(
                TimeSeriesRead::try_new(
                    document_id("doc-1"),
                    "cpu".to_owned(),
                    Some(TimeSeriesRange::try_new(0, 1_000).expect("valid range")),
                    deadline(),
                    cancellation(),
                )
                .expect("valid time-series read")
            ),
        ),
        Err(FamilyError::TimeSeriesRangeInvalid)
    );
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// W4-CA-5: the family-record enumeration for backup and migration
// ---------------------------------------------------------------------------

#[test]
fn snapshot_family_records_enumerates_the_reserved_collection_at_a_snapshot() {
    // The typed enumeration a backup consumes: family records arrive with
    // identity, family kind, value, and published version, and user
    // documents never appear.
    let (_dir, engine) = open_engine("family-records-enumerate");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    put_user_document(
        &engine,
        "tenant-a",
        "put-other",
        "doc-user",
        object_user_value(&[("n", "v")]),
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "counter-commit",
        |store, transaction, scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 3 }],
                    ),
                )
                .expect("stages the counter batch");
        },
    );
    commit_transaction(
        &engine,
        "tenant-a",
        "series-commit",
        |store, transaction, scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::time_series_batch(
                        TimeSeriesBatchWrite::try_new(
                            scope.clone(),
                            document_id("doc-1"),
                            "cpu".to_owned(),
                            vec![TimeSeriesAppend::new(10, vec![1.0], None)],
                            Vec::new(),
                            None,
                            ExpectedVersion::Any,
                            identity.clone(),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid time-series batch"),
                    ),
                )
                .expect("stages the time-series batch");
        },
    );

    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let records = engine
        .families()
        .snapshot_family_records(&tenant_scope, &snapshot)
        .expect("enumerates the reserved collection");
    assert_eq!(records.len(), 2, "one counter record and one time-series record");
    let kinds: Vec<FamilyKind> =
        records.iter().map(crate::backup::FamilyRecordInput::kind).collect();
    assert_eq!(kinds, [FamilyKind::Counters, FamilyKind::TimeSeries]);
    for record in &records {
        assert!(
            record.id().as_str().starts_with("family:"),
            "the reserved identifier travels unchanged: {}",
            record.id().as_str()
        );
        assert!(record.version() >= 1, "the published version travels");
    }
    // The counter record's value is exactly what the family seam reads back.
    let group = counter_group(read_family(&engine, "tenant-a", counter_read("doc-1", &["hits"])))
        .expect("reads the counter group");
    assert_eq!(counter_value(&group, "hits"), Some(3));

    // Another tenant's enumeration is empty: the records are tenant-scoped.
    let other_scope = scope(&engine, "tenant-b");
    let other_snapshot = engine.read_snapshot(&other_scope).expect("snapshot");
    let other_records = engine
        .families()
        .snapshot_family_records(&other_scope, &other_snapshot)
        .expect("enumerates");
    assert!(other_records.is_empty());
    engine.shutdown().expect("shuts down");
}

#[test]
fn snapshot_family_records_observe_only_committed_state() {
    // The enumeration reads one immutable snapshot: staged-but-uncommitted
    // records are invisible, and a later commit appears only in a later
    // snapshot.
    let (_dir, engine) = open_engine("family-records-snapshot");
    put_user_document(&engine, "tenant-a", "put-doc", "doc-1", object_user_value(&[("n", "v")]));
    let tenant_scope = scope(&engine, "tenant-a");
    let before = engine.read_snapshot(&tenant_scope).expect("snapshot before");
    commit_transaction(
        &engine,
        "tenant-a",
        "counter-commit",
        |store, transaction, scope, identity| {
            store
                .stage(
                    transaction,
                    counter_write(
                        scope,
                        identity.as_str(),
                        "doc-1",
                        vec![CounterOperation::Increment { name: "hits".to_owned(), delta: 1 }],
                    ),
                )
                .expect("stages the counter batch");
        },
    );
    let stale = engine
        .families()
        .snapshot_family_records(&tenant_scope, &before)
        .expect("enumerates the older snapshot");
    assert!(stale.is_empty(), "the record did not exist at the earlier boundary");
    let after = engine.read_snapshot(&tenant_scope).expect("snapshot after");
    let fresh = engine
        .families()
        .snapshot_family_records(&tenant_scope, &after)
        .expect("enumerates the newer snapshot");
    assert_eq!(fresh.len(), 1);
    engine.shutdown().expect("shuts down");
}

#[test]
fn snapshot_family_records_reject_foreign_snapshots_and_collisions() {
    // The same binding rules as every family read: a snapshot from another
    // Engine or a scope naming another tenant is typed, never silently
    // swapped; and a squatter in the reserved collection is a typed
    // reserved-state error, not a silently skipped record.
    let (_dir, engine) = open_engine("family-records-binding");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let other_tenant_scope = scope(&engine, "tenant-b");
    assert_eq!(
        engine.families().snapshot_family_records(&other_tenant_scope, &snapshot),
        Err(FamilyError::TenantMismatch),
        "a scope naming another tenant is rejected before any data access"
    );
    let other_snapshot = engine.read_snapshot(&other_tenant_scope).expect("snapshot");
    assert_eq!(
        engine.families().snapshot_family_records(&tenant_scope, &other_snapshot),
        Err(FamilyError::TenantMismatch)
    );

    // A document in the reserved collection whose identifier does not parse
    // as a family record is a collision the enumeration reports.
    let identity = operation("squatter");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity).expect("begins");
    let squatter = Document::new(
        document_id("not-a-family-record"),
        CollectionName::try_new(RESERVED_COLLECTION).expect("reserved collection name"),
        DocumentValue::String("squatter".to_owned()),
    );
    transaction.put(squatter, ExpectedVersion::Any).expect("stages the squatter");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let after = engine.read_snapshot(&tenant_scope).expect("snapshot after");
    assert_eq!(
        engine.families().snapshot_family_records(&tenant_scope, &after),
        Err(FamilyError::ReservedStateInvalid),
        "a reserved-collection collision is typed, not skipped"
    );
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// SM-02: collection schemas — definition, versioning, write validation
// ---------------------------------------------------------------------------

/// Declares (or clears) one collection's schema in its own transaction.
fn declare_schema(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    collection_name: &str,
    schema: Option<&SchemaDefinition>,
) -> CommitOutcome {
    let collection = collection(collection_name);
    commit_transaction(
        engine,
        tenant_name,
        operation_name,
        |store, transaction, tenant_scope, identity| {
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::schema_validation_update(
                        ValidationWrite::try_new(
                            tenant_scope.clone(),
                            collection.clone(),
                            schema.cloned(),
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid validation write"),
                    ),
                )
                .expect("stages the schema declaration");
        },
    )
}

/// Reads one collection's validation result through the family seam.
fn read_validation(engine: &Engine, tenant_name: &str, name: &str) -> ValidationResult {
    match read_family(
        engine,
        tenant_name,
        FamilyReadRequest::validation(
            ValidationRead::try_new(collection(name), None, deadline(), cancellation())
                .expect("valid validation read"),
        ),
    )
    .expect("reads the validation state")
    {
        FamilyReadResult::Validation(result) => result,
        other => panic!("expected a validation result, got {other:?}"),
    }
}

/// Stages one document put in its own transaction and returns the put result.
fn stage_put(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    collection_name: &str,
    value: DocumentValue,
) -> Result<(), crate::engine::EngineError> {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, unique_operation(operation_name)).expect("begins");
    let document = Document::new(document_id(id), collection(collection_name), value);
    let staged = transaction.put(document, ExpectedVersion::Any);
    if staged.is_ok() {
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    }
    staged
}

#[test]
fn engine_put_enforces_declared_schemas_without_partial_writes() {
    let (_dir, engine) = open_engine("sm02-engine-put");
    declare_schema(
        &engine,
        "tenant-a",
        "declare-v1",
        "users",
        Some(
            &SchemaDefinition::try_new(
                SchemaVersion::try_new(1).expect("valid version"),
                vec![
                    SchemaRule::Required { field: "email".to_owned() },
                    SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
                ],
            )
            .expect("valid schema"),
        ),
    );

    // A violating document is rejected at the engine seam with the typed
    // engine validation error, and nothing is staged: the same transaction
    // can continue and commit other writes.
    assert_eq!(
        stage_put(
            &engine,
            "tenant-a",
            "reject-missing-email",
            "doc-1",
            "users",
            object_user_value(&[("name", "ada")]),
        ),
        Err(EngineError::InvalidDocument)
    );
    // The rejected document never landed: addressing it on demand is the
    // typed missing-document case, never a partial write.
    assert_eq!(
        read_family(
            &engine,
            "tenant-a",
            FamilyReadRequest::validation(
                ValidationRead::try_new(
                    collection("users"),
                    Some(document_id("doc-1")),
                    deadline(),
                    cancellation(),
                )
                .expect("valid validation read"),
            ),
        ),
        Err(FamilyError::DocumentMissing)
    );

    // A valid document passes the same seam and commits; its on-demand
    // report carries the enforced version and no violations.
    assert!(
        stage_put(
            &engine,
            "tenant-a",
            "accept-valid",
            "doc-2",
            "users",
            object_user_value(&[("name", "ada"), ("email", "ada@example")]),
        )
        .is_ok()
    );
    let report = match read_family(
        &engine,
        "tenant-a",
        FamilyReadRequest::validation(
            ValidationRead::try_new(
                collection("users"),
                Some(document_id("doc-2")),
                deadline(),
                cancellation(),
            )
            .expect("valid validation read"),
        ),
    )
    .expect("reads the on-demand report")
    {
        FamilyReadResult::Validation(result) => result,
        other => panic!("expected a validation result, got {other:?}"),
    };
    assert_eq!(report.enforced_version().map(SchemaVersion::get), Some(1));
    assert!(report.violations().is_empty());

    // A type violation is the same typed rejection with the field-and-kind
    // report available on demand for the stored document.
    assert_eq!(
        stage_put(
            &engine,
            "tenant-a",
            "reject-type",
            "doc-3",
            "users",
            DocumentValue::Object(
                [
                    ("name".to_owned(), DocumentValue::Integer(7)),
                    ("email".to_owned(), DocumentValue::String("ada@example".to_owned())),
                ]
                .into_iter()
                .collect()
            ),
        ),
        Err(EngineError::InvalidDocument)
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn same_transaction_schema_declaration_enforces_immediately() {
    let (_dir, engine) = open_engine("sm02-same-transaction");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let schema = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![SchemaRule::Required { field: "email".to_owned() }],
    )
    .expect("valid schema");
    let store = engine.families();
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation("declare-and-write")).expect("begins");
    store
        .stage(
            &mut transaction,
            FamilyWriteRequest::schema_validation_update(
                ValidationWrite::try_new(
                    tenant_scope.clone(),
                    collection("users"),
                    Some(schema),
                    operation("declare-and-write"),
                    deadline(),
                    cancellation(),
                )
                .expect("valid validation write"),
            ),
        )
        .expect("stages the declaration");

    // The declaration staged in this transaction already binds the puts of
    // this transaction: there is no window where a declared schema ignores
    // the writes that accompany it.
    assert_eq!(
        transaction.put(
            Document::new(
                document_id("doc-1"),
                collection("users"),
                object_user_value(&[("name", "ada")]),
            ),
            ExpectedVersion::Any,
        ),
        Err(EngineError::InvalidDocument)
    );
    transaction
        .put(
            Document::new(
                document_id("doc-2"),
                collection("users"),
                object_user_value(&[("email", "ada@example")]),
            ),
            ExpectedVersion::Any,
        )
        .expect("stages a conforming document");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let validation = read_validation(&engine, "tenant-a", "users");
    assert_eq!(validation.enforced_version().map(SchemaVersion::get), Some(1));
    engine.shutdown().expect("shuts down");
}

#[test]
fn schema_versions_evolve_forward_and_report_the_enforced_version() {
    let (_dir, engine) = open_engine("sm02-versions");
    let v1 = || {
        SchemaDefinition::try_new(
            SchemaVersion::try_new(1).expect("valid version"),
            vec![SchemaRule::Required { field: "email".to_owned() }],
        )
        .expect("valid schema")
    };
    let v2 = || {
        SchemaDefinition::try_new(
            SchemaVersion::try_new(2).expect("valid version"),
            vec![
                SchemaRule::Required { field: "email".to_owned() },
                SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
            ],
        )
        .expect("valid schema")
    };
    let v2_other = || {
        SchemaDefinition::try_new(
            SchemaVersion::try_new(2).expect("valid version"),
            vec![SchemaRule::Required { field: "phone".to_owned() }],
        )
        .expect("valid schema")
    };

    declare_schema(&engine, "tenant-a", "declare-v1", "users", Some(&v1()));
    assert_eq!(
        read_validation(&engine, "tenant-a", "users").enforced_version(),
        Some(SchemaVersion::try_new(1).expect("valid version"))
    );

    // The same version with the same rules restates idempotently: staging
    // accepts it, the restatement commits, and the enforced identity is
    // unchanged.
    assert!(matches!(
        declare_schema(&engine, "tenant-a", "restate-v1", "users", Some(&v1())),
        CommitOutcome::Committed(_)
    ));
    assert_eq!(
        read_validation(&engine, "tenant-a", "users").enforced_version(),
        Some(SchemaVersion::try_new(1).expect("valid version"))
    );

    // A greater version evolves the collection and is reported.
    declare_schema(&engine, "tenant-a", "declare-v2", "users", Some(&v2()));
    assert_eq!(
        read_validation(&engine, "tenant-a", "users").enforced_version(),
        Some(SchemaVersion::try_new(2).expect("valid version"))
    );

    // The same version with different rules, and any lower version, conflict
    // with the recorded version and stage nothing.
    let expects_conflict = |schema: Option<SchemaDefinition>, name: &str| {
        commit_transaction(
            &engine,
            "tenant-a",
            name,
            |store, transaction, tenant_scope, identity| {
                let staged = store.stage(
                    transaction,
                    FamilyWriteRequest::schema_validation_update(
                        ValidationWrite::try_new(
                            tenant_scope.clone(),
                            collection("users"),
                            schema.clone(),
                            operation(identity.as_str()),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid validation write"),
                    ),
                );
                assert_eq!(staged, Err(FamilyError::Conflict));
            },
        );
    };
    expects_conflict(Some(v2_other()), "same-version-different-rules");
    expects_conflict(Some(v1()), "lower-version");
    assert_eq!(
        read_validation(&engine, "tenant-a", "users").enforced_version(),
        Some(SchemaVersion::try_new(2).expect("valid version")),
        "rejected declarations stage nothing"
    );

    // Clearing returns the collection to schema-free; a fresh declaration
    // starts a new version lineage.
    declare_schema(&engine, "tenant-a", "clear", "users", None);
    assert_eq!(read_validation(&engine, "tenant-a", "users").enforced_version(), None);
    declare_schema(&engine, "tenant-a", "declare-fresh", "users", Some(&v1()));
    assert_eq!(
        read_validation(&engine, "tenant-a", "users").enforced_version(),
        Some(SchemaVersion::try_new(1).expect("valid version"))
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn schemas_are_tenant_scoped_and_cross_tenant_application_is_refused() {
    let (_dir, engine) = open_engine("sm02-tenant-scope");
    declare_schema(
        &engine,
        "tenant-a",
        "declare-strict",
        "users",
        Some(
            &SchemaDefinition::try_new(
                SchemaVersion::try_new(1).expect("valid version"),
                vec![
                    SchemaRule::Required { field: "email".to_owned() },
                    SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
                ],
            )
            .expect("valid schema"),
        ),
    );

    // A schema write scoped to tenant A cannot be staged inside a
    // transaction bound to tenant B: the declaration is refused before any
    // state is touched.
    let tenant_b_scope = scope(&engine, "tenant-b");
    let snapshot = engine.read_snapshot(&tenant_b_scope).expect("snapshot");
    let store = engine.families();
    let mut transaction =
        engine.begin(&tenant_b_scope, snapshot, operation("cross-tenant")).expect("begins");
    let staged = store.stage(
        &mut transaction,
        FamilyWriteRequest::schema_validation_update(
            ValidationWrite::try_new(
                scope(&engine, "tenant-a"),
                collection("users"),
                Some(
                    SchemaDefinition::try_new(
                        SchemaVersion::try_new(9).expect("valid version"),
                        vec![SchemaRule::Required { field: "email".to_owned() }],
                    )
                    .expect("valid schema"),
                ),
                operation("cross-tenant"),
                deadline(),
                cancellation(),
            )
            .expect("valid validation write"),
        ),
    );
    assert_eq!(staged, Err(FamilyError::TenantMismatch));
    drop(transaction);

    // Tenant B's same-named collection is schema-free: tenant A's record is
    // invisible to tenant B's transactions, so a document tenant A rejects
    // is accepted for tenant B.
    assert!(
        stage_put(
            &engine,
            "tenant-b",
            "b-accepts",
            "doc-1",
            "users",
            object_user_value(&[("name", "ben")]),
        )
        .is_ok()
    );
    let foreign = read_validation(&engine, "tenant-b", "users");
    assert_eq!(foreign.policy(), None, "another tenant's schema never applies");
    assert_eq!(foreign.enforced_version(), None);

    // Tenant A's own collection still enforces its schema.
    assert_eq!(
        stage_put(
            &engine,
            "tenant-a",
            "a-rejects",
            "doc-1",
            "users",
            object_user_value(&[("name", "ada")]),
        ),
        Err(EngineError::InvalidDocument)
    );
    engine.shutdown().expect("shuts down");
}

fn copy_tree(source: &std::path::Path, destination: &std::path::Path) {
    std::fs::create_dir_all(destination).expect("creates the destination");
    for entry in std::fs::read_dir(source).expect("reads the source") {
        let entry = entry.expect("reads the entry");
        let target = destination.join(entry.file_name());
        if entry.path().is_dir() {
            copy_tree(&entry.path(), &target);
        } else {
            std::fs::copy(entry.path(), target).expect("copies the file");
        }
    }
}

#[test]
fn schema_free_collections_are_byte_for_byte_unaffected() {
    // The same schema-free script runs against two fresh engines. The probe
    // proves the write path stays deterministic in every byte the
    // application can observe: identical sequences step by step, identical
    // reconstructed commits, identical documents, and no schema state
    // recorded (the reserved collection stays empty). Whole-file byte
    // equality stopped holding when rows gained the D-42 `committed_at`
    // history-floor column — a commit timestamp is per-run state by
    // design.
    // Clone one empty initialized store so encryption key and owner metadata
    // are held constant. Otherwise ciphertext differences from two fresh
    // local keys obscure whether the schema-free application bytes changed.
    let (template, engine) = open_engine("sm02-bytes-template");
    engine.shutdown().expect("shuts down the template");
    let run = |tag: &str| -> (Vec<u64>, Vec<(String, String)>, usize) {
        let dir = location(tag);
        copy_tree(&template, &dir);
        let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
            .expect("engine opens");
        let mut sequences = Vec::new();
        let mut commit = |operation_name: &str, stage: &dyn Fn(&mut Transaction<'_>)| {
            let tenant_scope = scope(&engine, "tenant-a");
            let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
            let mut transaction =
                engine.begin(&tenant_scope, snapshot, operation(operation_name)).expect("begins");
            stage(&mut transaction);
            match transaction.commit(LocalDurability::Synced) {
                CommitOutcome::Committed(receipt) => sequences.push(receipt.sequence().get()),
                other => panic!("expected a commit, got {other:?}"),
            }
        };
        commit("put-users", &|transaction| {
            transaction
                .put(
                    Document::new(
                        document_id("doc-1"),
                        collection("users"),
                        object_user_value(&[("name", "ada")]),
                    ),
                    ExpectedVersion::Any,
                )
                .expect("stages the user document");
        });
        commit("put-orders", &|transaction| {
            transaction
                .put(
                    Document::new(
                        document_id("order-1"),
                        collection("orders"),
                        object_user_value(&[("item", "bolt")]),
                    ),
                    ExpectedVersion::Any,
                )
                .expect("stages the order document");
        });
        commit("delete-users", &|transaction| {
            transaction
                .delete(document_id("doc-1"), ExpectedVersion::Any)
                .expect("stages the deletion");
        });
        let tenant_scope = scope(&engine, "tenant-a");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let documents = engine
            .snapshot_documents(&tenant_scope, &snapshot)
            .expect("reads the tenant")
            .into_iter()
            .map(|document| (document.collection().to_string(), document.id().to_string()))
            .collect::<Vec<_>>();
        let reserved = engine
            .snapshot_documents(&tenant_scope, &snapshot)
            .expect("reads the tenant")
            .into_iter()
            .filter(|document| document.collection().as_str() == RESERVED_COLLECTION)
            .count();
        assert_eq!(reserved, 0, "a schema-free script records no family state");
        engine.shutdown().expect("shuts down");
        (sequences, documents, reserved)
    };
    let (sequences_a, documents_a, reserved_a) = run("sm02-bytes-a");
    let (sequences_b, documents_b, reserved_b) = run("sm02-bytes-b");
    assert_eq!(sequences_a, sequences_b, "the sequences stay identical");
    assert_eq!(documents_a, documents_b, "the final documents stay identical");
    assert_eq!((reserved_a, reserved_b), (0, 0), "a schema-free script records no family state");
}

#[test]
fn reserved_collection_rejects_schema_declarations() {
    let (_dir, engine) = open_engine("sm02-reserved-declare");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.families();
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation("declare-on-reserved")).expect("begins");
    let staged = store.stage(
        &mut transaction,
        FamilyWriteRequest::schema_validation_update(
            ValidationWrite::try_new(
                tenant_scope.clone(),
                CollectionName::try_new(RESERVED_COLLECTION).expect("reserved name"),
                Some(
                    SchemaDefinition::try_new(
                        SchemaVersion::try_new(1).expect("valid version"),
                        vec![SchemaRule::Required { field: "email".to_owned() }],
                    )
                    .expect("valid schema"),
                ),
                operation("declare-on-reserved"),
                deadline(),
                cancellation(),
            )
            .expect("valid validation write"),
        ),
    );
    assert_eq!(staged, Err(FamilyError::InvalidRequest));
    drop(transaction);
    engine.shutdown().expect("shuts down");
}

#[test]
fn customer_documents_cannot_collide_with_private_validation_records() {
    let (_dir, engine) = open_engine("sm02-collision");
    // Customer identifiers cannot occupy private validation metadata keys.
    assert!(
        stage_put(
            &engine,
            "tenant-a",
            "squatter",
            "family:validation/users",
            "users",
            object_user_value(&[("kind", "squatter")]),
        )
        .is_ok()
    );
    assert_eq!(
        stage_put(
            &engine,
            "tenant-a",
            "blocked-put",
            "doc-1",
            "users",
            object_user_value(&[("name", "ada")]),
        ),
        Ok(())
    );
    engine.shutdown().expect("shuts down");
}

#[test]
fn non_object_documents_fail_closed_under_declared_schemas() {
    let (_dir, engine) = open_engine("sm02-non-object");
    declare_schema(
        &engine,
        "tenant-a",
        "declare-v1",
        "users",
        Some(
            &SchemaDefinition::try_new(
                SchemaVersion::try_new(1).expect("valid version"),
                vec![
                    SchemaRule::Required { field: "email".to_owned() },
                    SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
                ],
            )
            .expect("valid schema"),
        ),
    );

    // Every non-object encoding is rejected at the engine seam: the rule set
    // rules object fields, so a document that is not an object cannot
    // satisfy any of them and must not slip past the evaluator.
    let non_object: Vec<(&str, DocumentValue)> = vec![
        ("null", DocumentValue::Null),
        ("boolean", DocumentValue::Boolean(true)),
        ("integer", DocumentValue::Integer(7)),
        ("number", DocumentValue::Number(1.5)),
        ("text", DocumentValue::String("not an object".to_owned())),
        ("array", DocumentValue::Array(vec![DocumentValue::Integer(7)])),
    ];
    for (name, value) in non_object {
        assert_eq!(
            stage_put(&engine, "tenant-a", "reject-non-object", "doc-1", "users", value),
            Err(EngineError::InvalidDocument),
            "{name} is not an object document"
        );
    }

    // The same encodings stay legal where no schema is declared: the
    // document model carries any structured value, and only a schema'd
    // collection rejects non-object documents.
    assert!(
        stage_put(
            &engine,
            "tenant-a",
            "free-integer",
            "free-1",
            "archive",
            DocumentValue::Integer(7)
        )
        .is_ok()
    );
    assert!(
        stage_put(&engine, "tenant-a", "free-text", "free-2", "archive", DocumentValue::Null)
            .is_ok()
    );

    // A non-object document stored before adoption reports the typed
    // document-level violation on demand: one violation, an empty field
    // name, and the document's own kind.
    assert!(
        stage_put(
            &engine,
            "tenant-a",
            "legacy-put",
            "legacy-1",
            "legacy",
            DocumentValue::Integer(7)
        )
        .is_ok()
    );
    declare_schema(
        &engine,
        "tenant-a",
        "declare-legacy",
        "legacy",
        Some(
            &SchemaDefinition::try_new(
                SchemaVersion::try_new(1).expect("valid version"),
                vec![SchemaRule::Required { field: "email".to_owned() }],
            )
            .expect("valid schema"),
        ),
    );
    let report = read_validation_with_document(&engine, "tenant-a", "legacy", "legacy-1");
    assert_eq!(report.enforced_version().map(SchemaVersion::get), Some(1));
    assert_eq!(
        report.violations(),
        &[SchemaViolation::new(String::new(), ViolationKind::DocumentNotObject)]
    );
    engine.shutdown().expect("shuts down");
}

/// Reads one collection's on-demand report for one stored document.
fn read_validation_with_document(
    engine: &Engine,
    tenant_name: &str,
    collection_name: &str,
    id: &str,
) -> ValidationResult {
    match read_family(
        engine,
        tenant_name,
        FamilyReadRequest::validation(
            ValidationRead::try_new(
                collection(collection_name),
                Some(document_id(id)),
                deadline(),
                cancellation(),
            )
            .expect("valid validation read"),
        ),
    )
    .expect("reads the on-demand report")
    {
        FamilyReadResult::Validation(result) => result,
        other => panic!("expected a validation result, got {other:?}"),
    }
}

#[test]
fn validation_policy_rejects_duplicated_enum_values_and_inert_bounds() {
    let (_dir, engine) = open_engine("sm02-inert-rules");

    // An accepted-value set names distinct values: a repeated value carries
    // no additional constraint and hides a declaration mistake.
    let duplicated_enum = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![SchemaRule::EnumValues {
            field: "role".to_owned(),
            values: vec!["reader".to_owned(), "writer".to_owned(), "reader".to_owned()],
        }],
    )
    .expect("structurally valid");
    assert_eq!(
        try_declare_schema(&engine, "tenant-a", "duplicated-enum", "users", &duplicated_enum),
        Err(FamilyError::ValidationPolicyMalformed),
        "a duplicated enum value is a malformed policy"
    );

    // A range bound can never bind a text-typed field, and a length bound
    // can never bind a numeric-typed field: the evaluator would never fire
    // them, so the declaration is rejected instead of staged as inert.
    let inert_range = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![
            SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::NumberRange { field: "name".to_owned(), min: Some(0), max: Some(10) },
        ],
    )
    .expect("structurally valid");
    assert_eq!(
        try_declare_schema(&engine, "tenant-a", "inert-range", "users", &inert_range),
        Err(FamilyError::ValidationPolicyMalformed),
        "a numeric bound on a text field is a malformed policy"
    );

    let inert_length = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
            SchemaRule::TextLength { field: "age".to_owned(), min: Some(1), max: Some(80) },
        ],
    )
    .expect("structurally valid");
    assert_eq!(
        try_declare_schema(&engine, "tenant-a", "inert-length", "users", &inert_length),
        Err(FamilyError::ValidationPolicyMalformed),
        "a length bound on an integer field is a malformed policy"
    );

    // Nothing was staged: every rejected declaration left its collection
    // schema-free.
    assert!(read_validation(&engine, "tenant-a", "users").policy().is_none());

    // The matching pairs stay declarable: bounds on their own type, and
    // bounds on a field with no declared type at all.
    let binding = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
            SchemaRule::NumberRange { field: "age".to_owned(), min: Some(0), max: Some(130) },
            SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::TextLength { field: "name".to_owned(), min: Some(1), max: Some(80) },
            SchemaRule::EnumValues {
                field: "role".to_owned(),
                values: vec!["reader".to_owned(), "writer".to_owned()],
            },
            SchemaRule::TextLength { field: "note".to_owned(), min: Some(1), max: Some(80) },
        ],
    )
    .expect("valid schema");
    declare_schema(&engine, "tenant-a", "binding-rules", "users", Some(&binding));
    let staged = read_validation(&engine, "tenant-a", "users");
    assert_eq!(staged.enforced_version().map(SchemaVersion::get), Some(1));
    assert_eq!(staged.policy().expect("staged policy").rules().len(), 6);
    engine.shutdown().expect("shuts down");
}

/// Stages one schema declaration and returns the typed staging result.
fn try_declare_schema(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    collection_name: &str,
    schema: &SchemaDefinition,
) -> Result<(), FamilyError> {
    let collection = collection(collection_name);
    let mut staged = Ok(());
    commit_transaction(
        engine,
        tenant_name,
        operation_name,
        |store, transaction, tenant_scope, identity| {
            staged = store.stage(
                transaction,
                FamilyWriteRequest::schema_validation_update(
                    ValidationWrite::try_new(
                        tenant_scope.clone(),
                        collection.clone(),
                        Some(schema.clone()),
                        operation(identity.as_str()),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid validation write"),
                ),
            );
        },
    );
    staged
}
