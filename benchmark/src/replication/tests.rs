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

use crate::engine::EngineConfig;
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

    // No Region, Member, acknowledgement, convergence, or topology fact is
    // carried, not even textually.
    let rendered = format!("{receipt:?}");
    for forbidden in
        ["region", "member", "acknowledg", "convergen", "topolog", "r-alpha", "m-alpha"]
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

#[test]
fn tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("isolation-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("isolation-follower", "m-dst", &topology);
    let source_id = member_id("m-src");
    let follower_id = member_id("m-dst");
    let mut network = ReplicationNetwork::new();

    let source_scope_a = scope(&source, "tenant-a");
    source
        .commit_write(&source_scope_a, put_request("main", "op-1", "doc-a", "tenant-a-value"))
        .expect("source writes tenant-a");

    // A hostile requester asks for tenant-a's history while the serving scope
    // names tenant-b: the serve seam denies the attempt with a typed error.
    network
        .send(
            &follower_id,
            &source_id,
            ReplicationFrame::CatchUpRequest {
                requester: follower_id.clone(),
                tenant: tenant("tenant-a"),
                after: None,
                limit: 8,
            },
        )
        .expect("bounded link accepts the request frame");
    let denied = source.serve_catch_up(&mut network, &scope(&source, "tenant-b"));
    assert!(
        matches!(&denied, Err(MemberError::CrossTenant { scope, attempted })
            if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
        "cross-tenant serve is denied with a typed error: {denied:?}"
    );
    assert!(
        network.receive(&follower_id, FrameSelector::Deliveries).is_empty(),
        "no batch crossed the seam for the denied tenant"
    );
    assert_eq!(source.denials().len(), 1, "the denial is observable on the source");
    assert_eq!(source.denials()[0].scope_tenant().as_str(), "tenant-b");
    assert_eq!(source.denials()[0].attempted_tenant().as_str(), "tenant-a");
    assert_eq!(source.denials()[0].counterparty().as_str(), "m-dst");
    assert_eq!(
        source.health(),
        ReplicationStatus::Degraded,
        "the denial degrades the serving member's health observation"
    );

    // A hostile delivery names tenant-a while the applying scope names
    // tenant-b: the apply seam denies it and nothing of tenant-a crosses.
    let hostile = PublishedCommit::new(
        tenant("tenant-a"),
        operation("op-1"),
        StorageSequence::new(1),
        LocalDurability::Synced,
        vec![PublishedWrite::Put { document: text_document("users", "doc-a", "hostile") }],
    );
    network
        .send(
            &source_id,
            &follower_id,
            ReplicationFrame::CatchUpBatch {
                source: source_id.clone(),
                tenant: tenant("tenant-a"),
                commits: vec![hostile],
                through: Some(StorageSequence::new(1)),
                tenant_high_water: Some(StorageSequence::new(1)),
                servable_from: Some(StorageSequence::new(1)),
                gap_below: None,
            },
        )
        .expect("bounded link accepts the batch frame");
    let denied_apply = follower.apply_deliveries(&mut network, &scope(&follower, "tenant-b"));
    assert!(
        matches!(&denied_apply, Err(MemberError::CrossTenant { scope, attempted })
            if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
        "cross-tenant apply is denied with a typed error: {denied_apply:?}"
    );
    assert_eq!(follower.denials().len(), 1, "the denial is observable on the follower");
    assert!(
        follower
            .engine()
            .expect("engine open")
            .committed_tenants()
            .expect("tenant enumeration")
            .is_empty(),
        "no cross-tenant data reached the follower's journal"
    );
    assert!(documents_of(&follower, "tenant-b").is_empty());

    // Positive control: the same exchange under matching tenant scopes works,
    // so the denials above are tenant isolation, not a broken path.
    let report = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert_eq!(report.applied(), 1);
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("reorder-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("reorder-follower", "m-dst", &topology);
    let source_id = member_id("m-src");
    let follower_id = member_id("m-dst");
    let mut network = ReplicationNetwork::new();

    member_put(&mut source, "tenant-a", "main", "op-1", "doc-a", "one");
    member_put(&mut source, "tenant-a", "main", "op-2", "doc-b", "two");
    let tenant_scope = scope(&source, "tenant-a");
    source
        .commit_write(&tenant_scope, delete_request("main", "op-3", "doc-a"))
        .expect("delete commits");

    // Delay models an ordered hold-back released only by explicit pumps, and
    // reorder releases the held frames newest-first: the follower observes
    // the publications out of publication order.
    let response_link = network.link(&source_id, &follower_id);
    response_link.delay();
    response_link.reorder();

    // Two pipelined requests overlap, so two batch frames are in flight when
    // the pump releases them in reverse order.
    let follower_scope = scope(&follower, "tenant-a");
    follower
        .request_catch_up(&mut network, &source_id, &follower_scope)
        .expect("first request sent");
    follower
        .request_catch_up(&mut network, &source_id, &follower_scope)
        .expect("second request sent");
    network.pump();
    let source_scope = scope(&source, "tenant-a");
    source.serve_catch_up(&mut network, &source_scope).expect("requests served");
    assert_eq!(network.link(&source_id, &follower_id).held(), 2, "the batches are held back");
    network.pump();
    assert!(
        network.link(&source_id, &follower_id).reordered() > 0,
        "the release order was actually reversed"
    );
    let report = follower
        .apply_deliveries(&mut network, &follower_scope)
        .expect("deliveries apply");

    assert_eq!(report.applied(), 3, "every operation applies exactly once");
    assert_eq!(report.duplicates_resolved(), 3, "the overlapping delivery resolves idempotently");
    assert_eq!(
        documents_of(&follower, "tenant-a"),
        documents_of(&source, "tenant-a"),
        "the follower reaches the contract outcome"
    );
    assert_eq!(journal_of(&follower, "tenant-a").len(), 3);
    let follower_view = documents_of(&follower, "tenant-a");
    assert!(
        follower_view.iter().all(|document| document.id().as_str() != "doc-a"),
        "doc-a stays deleted after the out-of-order replay"
    );
    assert_eq!(follower_view.len(), 1, "doc-b survives");

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn duplicate_delivery_applies_each_operation_once() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("duplicate-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("duplicate-follower", "m-dst", &topology);
    let source_id = member_id("m-src");
    let follower_id = member_id("m-dst");
    let mut network = ReplicationNetwork::new();

    member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    member_put(&mut source, "tenant-a", "main", "op-2", "doc-2", "v2");

    // The link duplicates every delivered frame: one batch becomes two
    // identical deliveries of the same operations.
    network.link(&source_id, &follower_id).duplicate();
    let report = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");

    assert!(
        network.link(&source_id, &follower_id).delivered() >= 2,
        "the fault actually duplicated the delivery"
    );
    assert_eq!(report.applied(), 2, "each operation applies once");
    assert_eq!(report.duplicates_resolved(), 2, "the duplicate resolves through the ledger");
    assert_eq!(journal_of(&follower, "tenant-a").len(), 2);
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));

    // A full re-request of the same range applies nothing: the advisory
    // cursor keeps the source from re-serving history the member already
    // holds. (In-delivery duplication was proven above; this is the
    // request-level idempotence of the protocol.)
    network.link(&source_id, &follower_id).clear_faults();
    let replay = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert_eq!(replay.applied(), 0);
    assert_eq!(replay.duplicates_resolved(), 0);
    assert!(replay.caught_up());
    assert_eq!(journal_of(&follower, "tenant-a").len(), 2);
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn member_rejoin_checks_identity_and_catches_up_without_duplicates() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("rejoin-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("rejoin-follower", "m-dst", &topology);
    let mut network = ReplicationNetwork::new();

    member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    member_put(&mut source, "tenant-a", "main", "op-2", "doc-2", "v2");
    let first = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert!(first.caught_up());

    let cursor_before = follower.cursor().expect("cursor derives from the journal");
    let rejoined = follower.rejoin(&cursor_before);
    assert!(
        rejoined.is_ok(),
        "a member whose durable history is intact rejoins after validation: {rejoined:?}"
    );
    assert_eq!(follower.health(), ReplicationStatus::Active, "the rejoining member is admitted");
    assert_eq!(
        follower.cursor().expect("cursor derives from the journal"),
        cursor_before,
        "rejoin validation is anchored in the journal-derived cursor"
    );

    // The rejoined member catches up on the operations it missed, without
    // duplicating the operations it already holds.
    member_put(&mut source, "tenant-a", "main", "op-3", "doc-3", "v3");
    let source_scope = scope(&source, "tenant-a");
    let follower_scope = scope(&follower, "tenant-a");
    let report =
        synchronize(&mut network, &mut source, &source_scope, &mut follower, &follower_scope, 4)
            .expect("catch-up after rejoin");
    assert!(report.caught_up());
    assert_eq!(report.applied(), 1, "only the missed operation applies");
    assert_eq!(journal_of(&follower, "tenant-a").len(), 3, "no operation is duplicated");
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));

    // A member whose durable history went backwards is reported, not
    // admitted: its journal-derived cursor regressed below the cursor it
    // rejoined with.
    let cursor_now = follower.cursor().expect("cursor derives from the journal");
    follower.close();
    std::fs::remove_dir_all(&follower_dir).ok();
    let rejected = follower.rejoin(&cursor_now);
    assert!(
        matches!(rejected, Err(MemberError::RejoinRejected(_))),
        "a regressed durable cursor is reported, not admitted: {rejected:?}"
    );
    assert_ne!(follower.health(), ReplicationStatus::Active);
    assert!(follower.last_rejection().is_some(), "the rejection is observable");

    source.close();
    std::fs::remove_dir_all(&source_dir).ok();
}

#[test]
fn restarted_member_resumes_from_its_durable_replication_cursor() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("resume-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("resume-follower", "m-dst", &topology);
    let mut network = ReplicationNetwork::new();

    for (name, text) in [("op-1", "v1"), ("op-2", "v2"), ("op-3", "v3")] {
        member_put(&mut source, "tenant-a", "main", name, "doc-1", text);
    }
    let caught_up = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert!(caught_up.caught_up());
    assert_eq!(journal_of(&follower, "tenant-a").len(), 3);

    // The source advances while the follower is down.
    member_put(&mut source, "tenant-a", "main", "op-4", "doc-1", "v4");
    member_put(&mut source, "tenant-a", "main", "op-5", "doc-2", "v5");

    let cursor_before = follower.cursor().expect("cursor derives from the journal");
    assert_eq!(
        cursor_before.tenant_progress(&tenant("tenant-a")),
        Some(StorageSequence::new(3)),
        "the durable cursor names the follower's own journal progress"
    );
    follower.restart().expect("the member reopens its Engine");
    assert_eq!(
        follower.cursor().expect("cursor derives from the journal"),
        cursor_before,
        "the durable cursor is journal ground truth and survives the restart"
    );
    assert_eq!(
        follower.advisory_progress(&tenant("tenant-a")),
        None,
        "advisory in-memory progress does not survive the restart"
    );

    // Resumption re-receives the tenant history and resolves the already
    // applied operations through the durable operation ledger.
    let source_scope = scope(&source, "tenant-a");
    let follower_scope = scope(&follower, "tenant-a");
    let report =
        synchronize(&mut network, &mut source, &source_scope, &mut follower, &follower_scope, 4)
            .expect("the restarted member resumes");
    assert_eq!(report.applied(), 2, "only the missed operations apply");
    assert_eq!(report.duplicates_resolved(), 3, "already-applied operations resolve idempotently");
    assert_eq!(journal_of(&follower, "tenant-a").len(), 5);
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn anti_entropy_repairs_a_lagging_member_after_a_gap() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("anti-entropy-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("anti-entropy-follower", "m-dst", &topology);
    let source_id = member_id("m-src");
    let follower_id = member_id("m-dst");
    let mut network = ReplicationNetwork::new();

    for index in 1..=5 {
        member_put(
            &mut source,
            "tenant-a",
            "main",
            &format!("op-{index}"),
            "doc-1",
            "advancing",
        );
    }
    let caught_up = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert!(caught_up.caught_up());

    // The source advances while the follower is idle, and the next delivery
    // in transit is dropped: the follower is left with a gap.
    for index in 6..=8 {
        member_put(
            &mut source,
            "tenant-a",
            "main",
            &format!("op-{index}"),
            &format!("doc-{index}"),
            "advancing",
        );
    }
    network.link(&source_id, &follower_id).drop_next(1);

    let source_scope = scope(&source, "tenant-a");
    let follower_scope = scope(&follower, "tenant-a");
    let report =
        synchronize(&mut network, &mut source, &source_scope, &mut follower, &follower_scope, 4)
            .expect("anti-entropy repair runs");
    assert!(
        network.link(&source_id, &follower_id).dropped() >= 1,
        "the fault actually dropped a delivery"
    );
    assert!(report.caught_up(), "the gap is repaired");
    assert_eq!(journal_of(&follower, "tenant-a").len(), 8, "every operation applies exactly once");
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));
    assert_ne!(follower.health(), ReplicationStatus::Stale, "the repaired member is not lagging");

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}
