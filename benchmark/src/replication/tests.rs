//! In-crate W4-REPLICATION R-1 tests covering the contract's red-test list.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has no
//! public constructor by design; the public-construction paths are covered by
//! `tests/replication/`. Every fixture uses a temporary directory and
//! synthetic documents only, drives time explicitly through transport pump
//! calls (no threads, no async runtimes, no sleeps), and never inspects
//! pages, WAL bytes, or private lock state.
//!
//! R-1 scope: the data-plane foundation — topology validation, the
//! deterministic in-process transport doubles, idempotent catch-up and
//! replay, durable cursors, rejoin, anti-entropy, and tenant isolation. The
//! acknowledgement lattice above `Local`, convergence, conflicts, fencing,
//! and the three-Region fault lane belong to R-2/R-3 and are not claimed
//! here.

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
};

use crate::engine::{Engine, EngineConfig};
use crate::replication::{
    AcknowledgementLevel, MemberId, ReplicationConfig, ReplicationStatus, TopologyView,
    member::{ApplyReport, MemberError, ReplicationMember, ReplicationWrite, synchronize},
    transport::{FrameSelector, ReplicationFrame, ReplicationNetwork},
};
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, LocalDurability, OperationId,
    StorageSequence, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-w4-repl-{tag}-{}-{index}", std::process::id()))
}

/// Opens one replication member over a fresh durable Engine location.
fn open_member(tag: &str, id: &str, topology: &TopologyView) -> (PathBuf, ReplicationMember) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let config = ReplicationConfig::try_new(topology.clone()).expect("valid replication config");
    let engine_config = EngineConfig::durable(&dir).expect("durable location");
    let member = ReplicationMember::try_new(
        config,
        MemberId::try_new(id).expect("valid member identifier"),
        engine_config,
    )
    .expect("member opens");
    (dir, member)
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

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn member_id(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid member identifier")
}

fn text_document(collection_name: &str, id: &str, text: &str) -> Document {
    Document::new(
        document_id(id),
        collection(collection_name),
        DocumentValue::String(text.to_owned()),
    )
}

/// The caller-supplied scope for one member, tenant, and engine instance.
///
/// Scopes are rebuilt after any restart or rejoin: the Engine identity is
/// never reused, so a scope minted before the restart no longer names the
/// reopened Engine.
fn scope(member: &ReplicationMember, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), member.engine().expect("engine open").identity())
}

/// One Region, one Member owning `range`: the charter's one-Region/one-Member
/// qualification row.
fn single_member_topology(region: &str, member: &str, range: &str) -> TopologyView {
    TopologyView::new()
        .with_region(region)
        .expect("valid region")
        .with_member(member, region, region)
        .expect("valid member")
        .with_owner(range, member)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// Two Regions, one Member each: `owner` owns `range`'s write path and
/// `follower` replicates it from the publication history.
fn two_member_topology(owner: &str, follower: &str, range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member(owner, "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member(follower, "r-beta", "r-beta")
        .expect("valid member")
        .with_owner(range, owner)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// One local write request at the explicit `Local` acknowledgement level.
fn put_request(range: &str, operation_name: &str, id: &str, text: &str) -> ReplicationWrite {
    ReplicationWrite::new(operation(operation_name), range, AcknowledgementLevel::Local)
        .expect("valid write request")
        .with_put(text_document("users", id, text))
}

/// One local delete request at the explicit `Local` acknowledgement level.
fn delete_request(range: &str, operation_name: &str, id: &str) -> ReplicationWrite {
    ReplicationWrite::new(operation(operation_name), range, AcknowledgementLevel::Local)
        .expect("valid write request")
        .with_delete(document_id(id))
}

/// Commits one document on a member and returns the publication sequence.
fn member_put(
    member: &mut ReplicationMember,
    tenant_name: &str,
    range: &str,
    operation_name: &str,
    id: &str,
    text: &str,
) -> StorageSequence {
    let tenant_scope = scope(member, tenant_name);
    member
        .commit_write(&tenant_scope, put_request(range, operation_name, id, text))
        .expect("local write commits")
        .sequence()
}

/// Reads one tenant's documents through the member's Engine snapshot.
fn documents_of(member: &ReplicationMember, tenant_name: &str) -> Vec<Document> {
    let tenant_scope = scope(member, tenant_name);
    let engine = member.engine().expect("engine open");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    engine.snapshot_documents(&tenant_scope, &snapshot).expect("documents")
}

/// The journal-recorded commits of one tenant on a member.
fn journal_of(member: &ReplicationMember, tenant_name: &str) -> Vec<PublishedCommit> {
    member
        .engine()
        .expect("engine open")
        .tenant_commits(&tenant(tenant_name), StorageSequence::new(0), 1000)
        .expect("journal read")
}

/// Drives one explicit catch-up exchange: request, pump, serve, pump, apply.
fn exchange_once(
    network: &mut ReplicationNetwork,
    source: &mut ReplicationMember,
    source_tenant: &str,
    follower: &mut ReplicationMember,
    follower_tenant: &str,
) -> ApplyReport {
    let source_id = source.id().clone();
    let follower_scope = scope(follower, follower_tenant);
    follower
        .request_catch_up(network, &source_id, &follower_scope)
        .expect("request sent");
    network.pump();
    let source_scope = scope(source, source_tenant);
    source.serve_catch_up(network, &source_scope).expect("requests served");
    network.pump();
    follower
        .apply_deliveries(network, &follower_scope)
        .expect("deliveries applied")
}

#[test]
fn local_commit_receipt_contains_no_region_or_member_facts() {
    let (dir, mut member) = open_member(
        "receipt-locality",
        "m-alpha",
        &single_member_topology("r-alpha", "m-alpha", "main"),
    );
    let tenant_scope = scope(&member, "tenant-a");
    let receipt = member
        .commit_write(&tenant_scope, put_request("main", "op-1", "doc-1", "value"))
        .expect("local write commits");

    // The receipt stays the engine-local publication fact: its accessors
    // return only the operation identity, the storage sequence, and the
    // local durability point.
    let operation_fact: &OperationId = receipt.operation_id();
    let sequence_fact: StorageSequence = receipt.sequence();
    let durability_fact: LocalDurability = receipt.durability();
    assert_eq!(operation_fact.as_str(), "op-1");
    assert_eq!(durability_fact, LocalDurability::Synced);
    assert!(sequence_fact.get() > 0);

    // No Region, Member, acknowledgement, convergence, topology, or receipt
    // fact is carried, not even textually.
    let rendered = format!("{receipt:?}");
    for forbidden in
        ["region", "member", "acknowledg", "convergen", "topolog", "r-alpha", "m-alpha", "receipt"]
    {
        assert!(
            !rendered.to_ascii_lowercase().contains(forbidden),
            "the local commit receipt carries a remote fact ({forbidden}): {rendered}"
        );
    }
    assert_eq!(
        std::any::type_name::<crate::engine::CommitReceipt>(),
        "terrabase_v2::engine::CommitReceipt",
        "the receipt type stays owned by the engine seam"
    );
    member.close();
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn single_region_single_member_write_reads_back_and_survives_restart() {
    let (dir, mut member) = open_member(
        "single-member",
        "m-alpha",
        &single_member_topology("r-alpha", "m-alpha", "main"),
    );
    let sequence = member_put(&mut member, "tenant-a", "main", "op-1", "doc-1", "v1");
    let before = documents_of(&member, "tenant-a");
    assert_eq!(before.len(), 1, "the write reads back");
    assert_eq!(before[0].value(), &DocumentValue::String("v1".to_owned()));

    let cursor_before = member.cursor().expect("cursor derives from the journal");
    assert_eq!(cursor_before.high_water(), Some(sequence));
    assert_eq!(
        cursor_before.tenant_progress(&tenant("tenant-a")),
        Some(sequence),
        "the cursor is bound to the member's own storage sequence"
    );

    member.restart().expect("the member reopens its Engine");
    let after = documents_of(&member, "tenant-a");
    assert_eq!(after, before, "the committed write survives the restart");
    assert_eq!(
        member.cursor().expect("cursor derives from the journal"),
        cursor_before,
        "the journal-derived cursor survives the restart"
    );
    assert_ne!(member.health(), ReplicationStatus::Unavailable);
    member.close();
    std::fs::remove_dir_all(&dir).ok();
}

