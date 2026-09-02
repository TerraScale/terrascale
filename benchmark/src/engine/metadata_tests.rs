//! In-crate tests for the document metadata and change-vector contract
//! (GAP-D02, ticket #59).
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. Every assertion goes through the public
//! Engine and Transaction surface; no test inspects raw pages, WAL bytes, or
//! private lock state.

use crate::engine::{
    CommitOutcome, ConditionalLoadError, DocumentHead, DocumentVersionContract, Engine,
    EngineConfig, HeadCondition, Transaction,
};
use crate::types::ReplicationClockStamp;
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-gapd02-engine-{tag}-{}-{index}", std::process::id()))
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

fn scope_of(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), engine.identity())
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn document(id: &str, collection: &str, text: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::String(text.to_owned()),
    )
}

fn users() -> CollectionName {
    CollectionName::try_new("users").expect("valid identifier")
}

fn begin<'engine>(
    engine: &'engine Engine,
    tenant_name: &str,
    operation_name: &str,
) -> Transaction<'engine> {
    let scope = scope_of(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    engine.begin(&scope, snapshot, operation(operation_name)).expect("begins")
}

fn commit_put(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    text: &str,
    expected: ExpectedVersion,
) -> CommitOutcome {
    let scope = scope_of(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction =
        engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
    transaction.put(document(id, "users", text), expected).expect("stages");
    let outcome = transaction.commit(LocalDurability::Synced);
    expect_committed(&outcome);
    outcome
}

fn commit_delete(
    engine: &Engine,
    tenant_name: &str,
    operation_name: &str,
    id: &str,
    expected: ExpectedVersion,
) -> CommitOutcome {
    let scope = scope_of(engine, tenant_name);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction =
        engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
    transaction
        .delete(DocumentId::try_new(id).expect("valid identifier"), expected)
        .expect("stages");
    let outcome = transaction.commit(LocalDurability::Synced);
    expect_committed(&outcome);
    outcome
}

fn receipt_sequence(outcome: CommitOutcome) -> StorageSequence {
    match outcome {
        CommitOutcome::Committed(receipt) => receipt.sequence(),
        other => panic!("expected a committed receipt, got {other:?}"),
    }
}

fn expect_committed(outcome: &CommitOutcome) {
    match outcome {
        CommitOutcome::Committed(_) => {}
        other => panic!("expected a committed receipt, got {other:?}"),
    }
}

fn current_contract(head: DocumentHead) -> DocumentVersionContract {
    match head {
        DocumentHead::Current(contract) => contract,
        other => panic!("expected the current-contract outcome, got {other:?}"),
    }
}

fn head_of(engine: &Engine, tenant_name: &str, id: &str, condition: HeadCondition) -> DocumentHead {
    let transaction = begin(engine, tenant_name, "head-probe");
    transaction
        .head(&users(), &DocumentId::try_new(id).expect("valid identifier"), condition)
        .expect("head answers")
}

fn contract_of(engine: &Engine, tenant_name: &str, id: &str) -> DocumentVersionContract {
    match head_of(engine, tenant_name, id, HeadCondition::Any) {
        DocumentHead::Current(contract) => contract,
        other => panic!("expected a current contract, got {other:?}"),
    }
}

#[test]
fn head_reports_the_published_version_contract() {
    let (_dir, engine) = open_engine("head-published");
    let sequence = receipt_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-put-1",
        "doc-1",
        "first",
        ExpectedVersion::New,
    ));

    let contract = contract_of(&engine, "tenant-a", "doc-1");
    assert_eq!(contract.version(), 1, "the first publication assigns version one");
    assert_eq!(contract.sequence(), sequence, "the sequence is the publication's own");
    assert_eq!(
        contract.stamp(),
        ReplicationClockStamp::new(0, 0),
        "engine-local history is served at the lane's origin clock"
    );
}

#[test]
fn head_reports_missing_for_absent_and_deleted_keys() {
    let (_dir, engine) = open_engine("head-missing");
    commit_put(&engine, "tenant-a", "op-put-1", "doc-1", "first", ExpectedVersion::New);

    assert_eq!(
        head_of(&engine, "tenant-a", "never-there", HeadCondition::Any),
        DocumentHead::Missing
    );
    commit_delete(&engine, "tenant-a", "op-del-1", "doc-1", ExpectedVersion::Any);
    assert_eq!(
        head_of(&engine, "tenant-a", "doc-1", HeadCondition::Any),
        DocumentHead::Missing,
        "a deleted key reads as missing, exactly like an absent one"
    );
}

#[test]
fn head_conditional_answers_the_v1_three_way_outcome() {
    let (_dir, engine) = open_engine("head-three-way");
    commit_put(&engine, "tenant-a", "op-put-1", "doc-1", "first", ExpectedVersion::New);
    let first = contract_of(&engine, "tenant-a", "doc-1");

    // The V1 304 analog: the supplied contract still matches.
    assert_eq!(
        head_of(&engine, "tenant-a", "doc-1", HeadCondition::IfUnchanged(first)),
        DocumentHead::Unchanged(first)
    );

    // A second publication changes the contract; the old one is stale.
    commit_put(&engine, "tenant-a", "op-put-2", "doc-1", "second", ExpectedVersion::Any);
    let second = contract_of(&engine, "tenant-a", "doc-1");
    assert_eq!(second.version(), 2);
    assert_ne!(second.sequence(), first.sequence());
    assert_eq!(
        head_of(&engine, "tenant-a", "doc-1", HeadCondition::IfUnchanged(first)),
        DocumentHead::Current(second),
        "a stale supplied contract yields the current one, never a silent match"
    );
    assert_eq!(
        head_of(&engine, "tenant-a", "doc-1", HeadCondition::IfUnchanged(second)),
        DocumentHead::Unchanged(second)
    );
}

#[test]
fn conditional_load_serves_exact_contracts_and_rejects_stale_vectors() {
    let (_dir, engine) = open_engine("load-if-match");
    commit_put(&engine, "tenant-a", "op-put-1", "doc-1", "first", ExpectedVersion::New);
    let first = contract_of(&engine, "tenant-a", "doc-1");

    let transaction = begin(&engine, "tenant-a", "load-1");
    let loaded = transaction
        .load_if_match(&users(), &DocumentId::try_new("doc-1").expect("valid identifier"), &first)
        .expect("an exact contract loads the document");
    assert_eq!(loaded.id().as_str(), "doc-1");
    assert_eq!(loaded.metadata().version(), 1);
    assert_eq!(*loaded.value(), DocumentValue::String("first".to_owned()));
    drop(transaction);

    commit_put(&engine, "tenant-a", "op-put-2", "doc-1", "second", ExpectedVersion::Any);
    let second = contract_of(&engine, "tenant-a", "doc-1");

    let transaction = begin(&engine, "tenant-a", "load-2");
    let error = transaction
        .load_if_match(&users(), &DocumentId::try_new("doc-1").expect("valid identifier"), &first)
        .expect_err("a stale vector is rejected, never silently served");
    assert_eq!(
        error,
        ConditionalLoadError::Stale { current: second },
        "the rejection carries the current contract so the caller can re-issue"
    );

    let missing = transaction
        .load_if_match(
            &users(),
            &DocumentId::try_new("never-there").expect("valid identifier"),
            &second,
        )
        .expect_err("an If-Match on a missing key cannot hold");
    assert_eq!(missing, ConditionalLoadError::Missing);
}

#[test]
fn conditional_load_rejects_the_recreated_document_stale_vector() {
    let (_dir, engine) = open_engine("load-recreate");
    let first_sequence = receipt_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-put-1",
        "doc-1",
        "first",
        ExpectedVersion::New,
    ));
    let first = contract_of(&engine, "tenant-a", "doc-1");

    commit_delete(&engine, "tenant-a", "op-del-1", "doc-1", ExpectedVersion::Any);
    let third_sequence = receipt_sequence(commit_put(
        &engine,
        "tenant-a",
        "op-put-2",
        "doc-1",
        "recreated",
        ExpectedVersion::New,
    ));
    let recreated = contract_of(&engine, "tenant-a", "doc-1");

    // Delete/re-create resets the per-key version: version one returns. The
    // sequence component is what keeps the two publications apart.
    assert_eq!(recreated.version(), 1);
    assert_eq!(first.version(), 1);
    assert_eq!(first.sequence(), first_sequence);
    assert_eq!(recreated.sequence(), third_sequence);
    assert_ne!(first, recreated);

    let transaction = begin(&engine, "tenant-a", "load-recreate");
    let error = transaction
        .load_if_match(&users(), &DocumentId::try_new("doc-1").expect("valid identifier"), &first)
        .expect_err("the pre-delete contract is stale even at the same version");
    assert_eq!(error, ConditionalLoadError::Stale { current: recreated });
}

#[test]
fn head_reads_the_published_view_not_staged_writes() {
    let (_dir, engine) = open_engine("head-published-view");
    commit_put(&engine, "tenant-a", "op-put-1", "doc-1", "first", ExpectedVersion::New);
    let published = contract_of(&engine, "tenant-a", "doc-1");

    let scope = scope_of(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("staged-head")).expect("begins");
    // A staged write of the same key and of a fresh key: neither has a
    // published identity, so the contract answers the published view.
    transaction.put(document("doc-1", "users", "staged"), ExpectedVersion::Any).expect("stages");
    transaction.put(document("doc-2", "users", "staged"), ExpectedVersion::New).expect("stages");

    assert_eq!(
        transaction
            .head(
                &users(),
                &DocumentId::try_new("doc-1").expect("valid identifier"),
                HeadCondition::Any
            )
            .expect("head answers"),
        DocumentHead::Current(published),
        "a staged put has no published identity until commit"
    );
    assert_eq!(
        transaction
            .head(
                &users(),
                &DocumentId::try_new("doc-2").expect("valid identifier"),
                HeadCondition::Any
            )
            .expect("head answers"),
        DocumentHead::Missing
    );
}

#[test]
fn head_resolves_the_exact_collection_key() {
    let (_dir, engine) = open_engine("head-collection-key");
    let scope = scope_of(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("op-put-both")).expect("begins");
    transaction
        .put(document("shared-id", "users", "in-users"), ExpectedVersion::New)
        .expect("stages");
    transaction
        .put(document("shared-id", "archive", "in-archive"), ExpectedVersion::New)
        .expect("stages");
    {
        let outcome = transaction.commit(LocalDurability::Synced);
        expect_committed(&outcome);
    }

    let id = DocumentId::try_new("shared-id").expect("valid identifier");
    let archive = CollectionName::try_new("archive").expect("valid identifier");
    let transaction = begin(&engine, "tenant-a", "head-archive");
    let head = transaction.head(&archive, &id, HeadCondition::Any).expect("head answers");
    let contract = current_contract(head);
    assert_eq!(contract.version(), 1);
    let loaded = transaction.load_if_match(&archive, &id, &contract).expect("loads");
    assert_eq!(*loaded.value(), DocumentValue::String("in-archive".to_owned()));
}

#[test]
fn head_attributes_each_write_to_its_own_commit_sequence() {
    let (_dir, engine) = open_engine("head-multi-write");
    let scope = scope_of(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("op-put-pair")).expect("begins");
    transaction.put(document("doc-1", "users", "one"), ExpectedVersion::New).expect("stages");
    transaction.put(document("doc-2", "users", "two"), ExpectedVersion::New).expect("stages");
    let sequence = receipt_sequence(transaction.commit(LocalDurability::Synced));

    assert_eq!(contract_of(&engine, "tenant-a", "doc-1").sequence(), sequence);
    assert_eq!(contract_of(&engine, "tenant-a", "doc-2").sequence(), sequence);
}

#[test]
fn head_walks_past_other_tenants_commits() {
    let (_dir, engine) = open_engine("head-tenant-walk");
    commit_put(&engine, "tenant-a", "op-a-1", "doc-a", "a-first", ExpectedVersion::New);
    let tenant_b_sequence = receipt_sequence(commit_put(
        &engine,
        "tenant-b",
        "op-b-1",
        "doc-b",
        "b-first",
        ExpectedVersion::New,
    ));
    commit_put(&engine, "tenant-a", "op-a-2", "doc-a", "a-second", ExpectedVersion::Any);

    // The backward walk must skip tenant A's later commit and attribute
    // tenant B's key to tenant B's own publication sequence.
    assert_eq!(contract_of(&engine, "tenant-b", "doc-b").sequence(), tenant_b_sequence);

    // Tenant isolation: tenant B never observes tenant A's key.
    assert_eq!(head_of(&engine, "tenant-b", "doc-a", HeadCondition::Any), DocumentHead::Missing);
}

#[test]
fn head_survives_reopen_with_the_same_contract() {
    let (dir, engine) = open_engine("head-reopen");
    commit_put(&engine, "tenant-a", "op-put-1", "doc-1", "first", ExpectedVersion::New);
    let before = contract_of(&engine, "tenant-a", "doc-1");
    engine.shutdown().expect("shuts down");

    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("still durable")).expect("reopens");
    assert_eq!(contract_of(&engine, "tenant-a", "doc-1"), before);
}
