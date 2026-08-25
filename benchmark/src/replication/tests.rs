//! In-crate W4-REPLICATION tests covering the contract's red-test lists for
//! R-1 and R-2.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has no
//! public constructor by design; the public-construction paths are covered by
//! `tests/replication/`. Every fixture uses a temporary directory and
//! synthetic documents only, drives time explicitly through transport pump
//! calls and injected lane instants (no threads, no async runtimes, no
//! sleeps), and never inspects pages, WAL bytes, or private lock state.
//!
//! R-1 scope: the data-plane foundation — topology validation, the
//! deterministic in-process transport doubles, idempotent catch-up and
//! replay, durable cursors, rejoin, anti-entropy, and tenant isolation.
//! R-2 scope: the D-2 acknowledgement lattice with timeout and degenerate
//! normalization, the convergence observation, the D-3 conflict policy on a
//! bounded hybrid logical clock, and D-1 leader fencing. The three-Region
//! fault lane and `ReplicationReceipt` artifacts belong to R-3 and are not
//! claimed here.

#![allow(
    clippy::similar_names,
    reason = "fixtures bind per-member values whose canonical names differ by one character (m_a1/m_a2, dir_a1/dir_b1)"
)]

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
};

use crate::engine::EngineConfig;
use crate::replication::{
    AcknowledgementLevel, ConflictOutcome, ConflictRecord, MemberId, RegionId,
    ReplicationClockStamp, ReplicationConfig, ReplicationOutcome, ReplicationStatus, TopologyView,
    member::{
        ApplyReport, MemberError, ReplicationMember, ReplicationWrite, observe_convergence,
        synchronize,
    },
    transport::{FrameSelector, ReplicationFrame, ReplicationNetwork, StampedCommit},
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
    follower.request_catch_up(network, &source_id, &follower_scope).expect("request sent");
    network.pump();
    let source_scope = scope(source, source_tenant);
    source.serve_catch_up(network, &source_scope).expect("requests served");
    network.pump();
    follower.apply_deliveries(network, &follower_scope).expect("deliveries applied")
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
                commits: vec![StampedCommit::at_origin_clock(hostile)],
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
    let report =
        follower.apply_deliveries(&mut network, &follower_scope).expect("deliveries apply");

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
        member_put(&mut source, "tenant-a", "main", &format!("op-{index}"), "doc-1", "advancing");
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

// ---------------------------------------------------------------------------
// Focused R-1 extras: the hard correctness rules and the fault vocabulary
// the contract names but the ten listed tests above do not isolate.
// ---------------------------------------------------------------------------

#[test]
fn operation_id_reuse_with_a_different_write_set_is_a_typed_failure() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("reuse-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("reuse-follower", "m-dst", &topology);
    let source_id = member_id("m-src");
    let follower_id = member_id("m-dst");
    let mut network = ReplicationNetwork::new();

    member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "original");
    let report = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert_eq!(report.applied(), 1);

    // A hostile delivery reuses the live operation identity for a different
    // write set: the engine's operation ledger rejects it and the failure is
    // surfaced as a typed replication failure, never silently skipped.
    let reuse = PublishedCommit::new(
        tenant("tenant-a"),
        operation("op-1"),
        StorageSequence::new(9),
        LocalDurability::Synced,
        vec![PublishedWrite::Put { document: text_document("users", "doc-9", "hostile") }],
    );
    network
        .send(
            &source_id,
            &follower_id,
            ReplicationFrame::CatchUpBatch {
                source: source_id.clone(),
                tenant: tenant("tenant-a"),
                commits: vec![StampedCommit::at_origin_clock(reuse)],
                through: Some(StorageSequence::new(9)),
                tenant_high_water: Some(StorageSequence::new(9)),
                servable_from: Some(StorageSequence::new(1)),
                gap_below: None,
            },
        )
        .expect("bounded link accepts the batch frame");
    let rejected = follower.apply_deliveries(&mut network, &scope(&follower, "tenant-a"));
    assert!(
        matches!(
            &rejected,
            Err(MemberError::ApplyFailed {
                operation,
                reason: crate::replication::member::ApplyFailure::Rejected(
                    crate::engine::CommitError::OperationIdReuse
                )
            }) if operation.as_str() == "op-1"
        ),
        "identity reuse under a different write set is a typed failure: {rejected:?}"
    );
    // The follower's view still holds the original write only.
    let view = documents_of(&follower, "tenant-a");
    assert_eq!(view.len(), 1);
    assert_eq!(view[0].value(), &DocumentValue::String("original".to_owned()));

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn a_cursor_below_servable_history_falls_back_to_full_catch_up_never_silently_skips() {
    use crate::replication::member::catch_up_gap;

    // The rule itself: a cursor the journal cannot serve below is a gap,
    // except when the journal serves from its first recorded sequence.
    let sequence = |value: u64| Some(StorageSequence::new(value));
    assert_eq!(catch_up_gap(None, sequence(1)), None, "a full scan from a complete journal");
    assert_eq!(catch_up_gap(sequence(2), sequence(1)), None, "an incremental scan above the bound");
    assert_eq!(catch_up_gap(sequence(2), sequence(4)), sequence(2), "history 3 is unservable");
    assert_eq!(catch_up_gap(None, sequence(3)), sequence(0), "the journal lost its beginning");
    assert_eq!(catch_up_gap(sequence(2), None), None, "no servable history, nothing requested");

    // The live fallback: a source that flags the follower's own cursor as
    // unservable makes the follower drop its advisory progress and re-request
    // a full catch-up.
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("gap-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("gap-follower", "m-dst", &topology);
    let source_id = member_id("m-src");
    let follower_id = member_id("m-dst");
    let mut network = ReplicationNetwork::new();

    member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    member_put(&mut source, "tenant-a", "main", "op-2", "doc-2", "v2");
    let caught_up = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert!(caught_up.caught_up());
    assert_eq!(follower.advisory_progress(&tenant("tenant-a")), Some(StorageSequence::new(2)));

    network
        .send(
            &source_id,
            &follower_id,
            ReplicationFrame::CatchUpBatch {
                source: source_id.clone(),
                tenant: tenant("tenant-a"),
                commits: Vec::new(),
                through: None,
                tenant_high_water: Some(StorageSequence::new(2)),
                servable_from: Some(StorageSequence::new(3)),
                gap_below: Some(StorageSequence::new(2)),
            },
        )
        .expect("bounded link accepts the batch frame");
    let report = follower
        .apply_deliveries(&mut network, &scope(&follower, "tenant-a"))
        .expect("the flagged batch falls back instead of failing");
    assert_eq!(report.applied(), 0);
    assert_eq!(follower.fallbacks().len(), 1, "the fallback is observable");
    assert_eq!(follower.fallbacks()[0].tenant().as_str(), "tenant-a");
    assert_eq!(follower.fallbacks()[0].cursor(), StorageSequence::new(2));
    assert_eq!(follower.advisory_progress(&tenant("tenant-a")), None, "advisory progress dropped");

    follower
        .request_catch_up(&mut network, &source_id, &scope(&follower, "tenant-a"))
        .expect("fallback request sent");
    let requests = network.receive(&source_id, FrameSelector::Requests);
    assert_eq!(requests.len(), 1);
    assert!(
        matches!(&requests[0], ReplicationFrame::CatchUpRequest { after: None, .. }),
        "the fallback re-requests the full history: {:?}",
        requests[0]
    );

    // The unservable case: a gap the source cannot serve even from the
    // beginning is a typed failure, never a silent skip.
    network
        .send(
            &source_id,
            &follower_id,
            ReplicationFrame::CatchUpBatch {
                source: source_id.clone(),
                tenant: tenant("tenant-a"),
                commits: Vec::new(),
                through: None,
                tenant_high_water: None,
                servable_from: Some(StorageSequence::new(4)),
                gap_below: Some(StorageSequence::new(0)),
            },
        )
        .expect("bounded link accepts the batch frame");
    let unservable = follower.apply_deliveries(&mut network, &scope(&follower, "tenant-a"));
    assert!(
        matches!(&unservable, Err(MemberError::GapUnservable { cursor, servable_from, .. })
            if *cursor == StorageSequence::new(0) && *servable_from == StorageSequence::new(4)),
        "an unservable history is reported with its bounds: {unservable:?}"
    );

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn member_health_reports_status_only_and_never_convergence() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let source_dir = location("health-source");
    let follower_dir = location("health-follower");
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
    // A one-commit batch bound keeps the follower observably behind after a
    // single round, which is the lag the Stale observation reports.
    let follower_config = ReplicationConfig::try_new(topology.clone())
        .expect("valid config")
        .with_catch_up_batch(1)
        .expect("bounded batch");
    let source_config = ReplicationConfig::try_new(topology).expect("valid config");
    let mut source = ReplicationMember::try_new(
        source_config,
        member_id("m-src"),
        EngineConfig::durable(&source_dir).expect("durable location"),
    )
    .expect("source opens");
    let mut follower = ReplicationMember::try_new(
        follower_config,
        member_id("m-dst"),
        EngineConfig::durable(&follower_dir).expect("durable location"),
    )
    .expect("follower opens");
    let mut network = ReplicationNetwork::new();

    assert_eq!(follower.health(), ReplicationStatus::Assigned, "a seated member that did nothing");

    member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    member_put(&mut source, "tenant-a", "main", "op-2", "doc-2", "v2");
    let partial = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert_eq!(partial.applied(), 1);
    assert_eq!(partial.through(), Some(StorageSequence::new(1)));
    assert!(!partial.caught_up());
    assert_eq!(
        follower.health(),
        ReplicationStatus::Stale,
        "a member that knows it is behind answers outside its bound"
    );
    assert_eq!(source.health(), ReplicationStatus::Responsive, "the source answered a request");

    let healed = exchange_once(&mut network, &mut source, "tenant-a", &mut follower, "tenant-a");
    assert!(healed.caught_up());
    assert_eq!(follower.health(), ReplicationStatus::Active, "a caught-up member is active");

    follower.close();
    assert_eq!(follower.health(), ReplicationStatus::Unavailable, "a closed member is unreachable");
    source.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn partitioned_link_blocks_catch_up_until_healed() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (source_dir, mut source) = open_member("partition-source", "m-src", &topology);
    let (follower_dir, mut follower) = open_member("partition-follower", "m-dst", &topology);
    let source_id = member_id("m-src");
    let follower_id = member_id("m-dst");
    let mut network = ReplicationNetwork::new();

    member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    network.link(&source_id, &follower_id).partition();
    let source_scope = scope(&source, "tenant-a");
    let follower_scope = scope(&follower, "tenant-a");
    let partitioned =
        synchronize(&mut network, &mut source, &source_scope, &mut follower, &follower_scope, 3)
            .expect("the partitioned exchange stays typed");
    assert!(!partitioned.caught_up(), "a full partition stops catch-up");
    assert!(
        network.link(&source_id, &follower_id).dropped() >= 1,
        "the partition observably lost the frames"
    );
    assert_eq!(network.pending(&follower_id), 0, "nothing is in flight while partitioned");
    assert!(journal_of(&follower, "tenant-a").is_empty());

    network.link(&source_id, &follower_id).clear_faults();
    let healed =
        synchronize(&mut network, &mut source, &source_scope, &mut follower, &follower_scope, 3)
            .expect("the healed exchange runs");
    assert!(healed.caught_up(), "healing resumes catch-up");
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));

    source.close();
    follower.close();
    std::fs::remove_dir_all(&source_dir).ok();
    std::fs::remove_dir_all(&follower_dir).ok();
}

#[test]
fn bounded_links_reject_frames_beyond_their_bound() {
    use crate::replication::transport::{MAX_LINK_FRAMES, TransportError};

    let from = member_id("m-src");
    let to = member_id("m-dst");
    let mut network = ReplicationNetwork::new();
    network.link(&from, &to).delay();
    for index in 0..MAX_LINK_FRAMES {
        network
            .send(
                &from,
                &to,
                ReplicationFrame::CatchUpRequest {
                    requester: to.clone(),
                    tenant: tenant("tenant-a"),
                    after: None,
                    limit: 1,
                },
            )
            .unwrap_or_else(|_| panic!("frame {index} fits the bound"));
    }
    let rejected = network.send(
        &from,
        &to,
        ReplicationFrame::CatchUpRequest {
            requester: to.clone(),
            tenant: tenant("tenant-a"),
            after: None,
            limit: 1,
        },
    );
    assert_eq!(rejected, Err(TransportError::LinkFull), "the link is bounded");
    assert_eq!(network.link(&from, &to).held(), MAX_LINK_FRAMES);
    assert_eq!(network.link(&from, &to).overflowed(), 1, "the refused send is counted as overflow");
}

#[test]
fn rejoin_rejects_a_location_whose_format_does_not_verify() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (dir, mut member) = open_member("rejoin-format", "m-src", &topology);
    let tenant_scope = scope(&member, "tenant-a");
    member
        .commit_write(&tenant_scope, put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");
    let cursor = member.cursor().expect("cursor derives from the journal");

    // Corrupt the recorded storage format identity: the reopen must fail the
    // format check instead of admitting the member over foreign bytes.
    member.close();
    std::fs::write(dir.join("format"), b"not a terrabase format file").expect("format corrupted");
    let rejected = member.rejoin(&cursor);
    assert!(
        matches!(
            rejected,
            Err(MemberError::EngineOpen(crate::engine::EngineOpenError::FormatMismatch))
        ),
        "a location whose format does not verify is reported, not admitted: {rejected:?}"
    );
    assert_eq!(member.health(), ReplicationStatus::Unavailable);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn write_admission_is_per_range_and_stronger_levels_commit_locally_then_wait() {
    let topology = two_member_topology("m-src", "m-dst", "main");
    let (dir_src, mut source) = open_member("admission-source", "m-src", &topology);
    let (dir_dst, mut follower) = open_member("admission", "m-dst", &topology);
    let tenant_scope = scope(&follower, "tenant-a");

    // D-1 admission: only the owner of a range's write path commits it.
    let not_owner =
        follower.commit_write(&tenant_scope, put_request("main", "op-1", "doc-1", "v1"));
    assert!(
        matches!(not_owner, Err(MemberError::NotOwner { ref range }) if range == "main"),
        "a non-owner's write fails admission locally: {not_owner:?}"
    );

    // R-2: naming a stronger level no longer refuses the local commit — the
    // local publication completes first and the replication wait is a
    // separate, explicitly driven step (ADR-0004 D-2, V1's post-commit
    // waits). The wait itself is exercised by the listed R-2 tests.
    let stronger =
        ReplicationWrite::new(operation("op-2"), "main", AcknowledgementLevel::RegionQuorum)
            .expect("valid write request")
            .with_put(text_document("users", "doc-1", "v1"));
    let owner_scope = scope(&source, "tenant-a");
    let receipt = source.commit_write(&owner_scope, stronger).expect("local commit completes");
    assert_eq!(receipt.durability(), LocalDurability::Synced);

    // Invalid range text is rejected before any engine work. Inner spaces
    // are valid identifier text; leading whitespace is not.
    let invalid_range =
        ReplicationWrite::new(operation("op-3"), " bad-range", AcknowledgementLevel::Local);
    assert!(matches!(invalid_range, Err(MemberError::InvalidRange)));

    // The owner-side control: a member not seated by the view never opens.
    let view = single_member_topology("r-gamma", "m-gamma-real", "main");
    let config = ReplicationConfig::try_new(view).expect("valid config");
    let outside = ReplicationMember::try_new(
        config,
        member_id("m-ghost"),
        EngineConfig::durable(location("admission-ghost")).expect("durable location"),
    );
    assert!(
        matches!(outside, Err(MemberError::NotInTopology)),
        "a member outside the view is never constructed"
    );

    source.close();
    follower.close();
    std::fs::remove_dir_all(&dir_src).ok();
    std::fs::remove_dir_all(&dir_dst).ok();
}

// ---------------------------------------------------------------------------
// R-2: the D-2 acknowledgement lattice, timeout, member and Region loss.
// ---------------------------------------------------------------------------

/// Two Regions where the home Region seats a full quorum pair.
fn two_region_quorum_topology(range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-a2", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner(range, "m-a1")
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// One Region seating three Members, `m-a1` owning the range.
fn one_region_three_members(range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-a2", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-a3", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_owner(range, "m-a1")
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// Two Regions, one Member each: the cross-Region acknowledgement shape.
fn two_regions_one_member_each(range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner(range, "m-a1")
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// Three Regions, one Member each: the non-degenerate D-4 topology.
fn three_regions_one_member_each(range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_region("r-gamma")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_member("m-g1", "r-gamma", "fd-gamma")
        .expect("valid member")
        .with_owner(range, "m-a1")
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

#[test]
fn acknowledgement_report_names_the_regions_and_members_that_contributed() {
    let topology = two_region_quorum_topology("main");
    let (dir_a1, mut m_a1) = open_member("ack-a1", "m-a1", &topology);
    let (dir_a2, mut m_a2) = open_member("ack-a2", "m-a2", &topology);
    let (dir_b1, mut m_b1) = open_member("ack-b1", "m-b1", &topology);
    let mut network = ReplicationNetwork::new();

    // MajorityRegions over two Regions needs both Regions through a Region
    // quorum: r-alpha needs both of its Members, r-beta its one.
    let scope_a1 = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a1, put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");
    let before = network.now();
    let deadline = network.now().plus_ticks(4);
    let scope_a2 = scope(&m_a2, "tenant-a");
    let scope_b1 = scope(&m_b1, "tenant-a");
    let mut peers = [(&mut m_a2, scope_a2), (&mut m_b1, scope_b1)];
    let outcome = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::MajorityRegions,
        &mut peers,
        deadline,
    );

    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("the cross-region acknowledgement is observed: {outcome:?}")
    };
    assert_eq!(report.requested(), AcknowledgementLevel::MajorityRegions);
    assert_eq!(report.observed(), AcknowledgementLevel::MajorityRegions);
    assert_eq!(report.normalization(), None, "two Regions distinguish this level");
    let contributed = report.contributed();
    assert_eq!(contributed.len(), 2, "both Regions contributed");
    assert_eq!(contributed[0].0.as_str(), "r-alpha");
    assert_eq!(contributed[0].1.iter().map(MemberId::as_str).collect::<Vec<_>>(), ["m-a1", "m-a2"]);
    assert_eq!(contributed[1].0.as_str(), "r-beta");
    assert_eq!(contributed[1].1.iter().map(MemberId::as_str).collect::<Vec<_>>(), ["m-b1"]);
    assert!(
        report.observed_at().ticks() > before.ticks(),
        "the observation time is the deterministic lane instant at acknowledgement"
    );
    assert_eq!(report.observed_at(), network.now(), "no pump moves between report and read");

    m_a1.close();
    m_a2.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_a2).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn acknowledgement_timeout_is_distinct_from_local_commit_failure() {
    let topology = two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member("timeout-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("timeout-b1", "m-b1", &topology);
    let a1 = member_id("m-a1");
    let b1 = member_id("m-b1");
    let mut network = ReplicationNetwork::new();

    // The follower's link holds every push back far longer than the deadline,
    // so nothing it carries is released before the deadline hits.
    network.link(&a1, &b1).delay_pumps(8);
    let scope_a1 = scope(&m_a1, "tenant-a");
    let receipt = m_a1
        .commit_write(&scope_a1, put_request("main", "op-1", "doc-1", "v1"))
        .expect("the local commit completes on its own");
    assert_eq!(receipt.durability(), LocalDurability::Synced);

    let deadline = network.now().plus_ticks(2);
    let scope_b1 = scope(&m_b1, "tenant-a");
    let mut peers = [(&mut m_b1, scope_b1)];
    let outcome = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::AllRegions,
        &mut peers,
        deadline,
    );

    let ReplicationOutcome::Timeout { operation_id, requested, observed } = outcome else {
        panic!("the unmet deadline is a timeout observation, not a commit failure: {outcome:?}")
    };
    assert_eq!(operation_id.as_str(), "op-1");
    assert_eq!(requested, AcknowledgementLevel::AllRegions);
    // The partial observation names exactly who had durably recorded when
    // the deadline hit: only the writer, at its own Region quorum.
    assert_eq!(
        observed.contributed().len(),
        1,
        "the held-back Member contributed nothing: {:?}",
        observed.contributed()
    );
    assert_eq!(observed.contributed()[0].0.as_str(), "r-alpha");
    assert_eq!(
        observed.contributed()[0].1.iter().map(MemberId::as_str).collect::<Vec<_>>(),
        ["m-a1"]
    );
    assert_eq!(
        observed.observed(),
        AcknowledgementLevel::RegionQuorum,
        "the strongest level the partial set actually achieved is reported"
    );

    // The timeout is distinct from a local commit failure: the local receipt
    // stands and the document still reads back at its writer.
    let resolution = m_a1
        .engine()
        .expect("engine open")
        .resolve_commit(&scope_a1, operation("op-1"))
        .expect("durable resolution");
    assert!(
        matches!(&resolution, crate::engine::DurableResolution::Committed(receipt)
            if receipt.durability() == LocalDurability::Synced),
        "the local commit is unaffected by the replication timeout: {resolution:?}"
    );
    assert_eq!(documents_of(&m_a1, "tenant-a").len(), 1);
    // And nothing crossed to the delayed Member before the deadline.
    assert_eq!(network.link(&a1, &b1).held(), 2, "every pushed frame is still held back");
    assert!(documents_of(&m_b1, "tenant-a").is_empty());

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn one_region_multiple_members_reach_region_quorum_under_member_delay() {
    let topology = one_region_three_members("main");
    let (dir_a1, mut m_a1) = open_member("quorum-a1", "m-a1", &topology);
    let (dir_a2, mut m_a2) = open_member("quorum-a2", "m-a2", &topology);
    let (dir_a3, mut m_a3) = open_member("quorum-a3", "m-a3", &topology);
    let a1 = member_id("m-a1");
    let a3 = member_id("m-a3");
    let mut network = ReplicationNetwork::new();

    // One Member of the Region is delayed well past the quorum window.
    network.link(&a1, &a3).delay_pumps(6);
    let scope_a1 = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a1, put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");
    let deadline = network.now().plus_ticks(3);
    let scope_a2 = scope(&m_a2, "tenant-a");
    let scope_a3 = scope(&m_a3, "tenant-a");
    let mut peers = [(&mut m_a2, scope_a2), (&mut m_a3, scope_a3)];
    let outcome = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::RegionQuorum,
        &mut peers,
        deadline,
    );

    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("the Region quorum is reachable without the delayed Member: {outcome:?}")
    };
    assert_eq!(report.observed(), AcknowledgementLevel::RegionQuorum);
    assert_eq!(report.requested(), AcknowledgementLevel::RegionQuorum);
    let contributed = report.contributed();
    assert_eq!(contributed.len(), 1);
    assert_eq!(contributed[0].0.as_str(), "r-alpha");
    assert_eq!(
        contributed[0].1.iter().map(MemberId::as_str).collect::<Vec<_>>(),
        ["m-a1", "m-a2"],
        "the report names exactly the Members that contributed, delayed m-a3 excluded"
    );
    assert!(documents_of(&m_a3, "tenant-a").is_empty(), "the delayed Member holds nothing yet");

    // D-4 canonical reading of the degenerate one-Region lattice: requesting
    // AllRegions normalizes to the weakest distinct name and the report
    // records the normalization so reports stay comparable.
    m_a1.commit_write(&scope_a1, put_request("main", "op-2", "doc-2", "v2"))
        .expect("local write commits");
    let deadline = network.now().plus_ticks(2);
    let scope_a2 = scope(&m_a2, "tenant-a");
    let scope_a3 = scope(&m_a3, "tenant-a");
    let mut peers = [(&mut m_a2, scope_a2), (&mut m_a3, scope_a3)];
    let degenerate = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-2"),
        AcknowledgementLevel::AllRegions,
        &mut peers,
        deadline,
    );
    let ReplicationOutcome::Acknowledged(normalized) = degenerate else {
        panic!("the degenerate level is satisfiable: {degenerate:?}")
    };
    assert_eq!(normalized.requested(), AcknowledgementLevel::AllRegions);
    assert_eq!(normalized.observed(), AcknowledgementLevel::RegionQuorum);
    assert_eq!(
        normalized.normalization(),
        Some((AcknowledgementLevel::AllRegions, AcknowledgementLevel::RegionQuorum)),
        "the one-Region normalization is recorded in the report"
    );

    m_a1.close();
    m_a2.close();
    m_a3.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_a2).ok();
    std::fs::remove_dir_all(&dir_a3).ok();
}

#[test]
fn two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility() {
    let topology = two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member("cross-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("cross-b1", "m-b1", &topology);
    let mut network = ReplicationNetwork::new();

    // Per-level visibility: before the observing Member's storage publishes
    // the operation, a read at that Region observes nothing — no level
    // implies read-your-writes at a Region that has not recorded it.
    let scope_a1 = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a1, put_request("main", "op-1", "doc-1", "value-one"))
        .expect("local write commits");
    assert!(
        documents_of(&m_b1, "tenant-a").is_empty(),
        "the write is invisible at r-beta before its Member published it"
    );

    let deadline = network.now().plus_ticks(3);
    let scope_b1 = scope(&m_b1, "tenant-a");
    let mut peers = [(&mut m_b1, scope_b1)];
    let outcome = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::MajorityRegions,
        &mut peers,
        deadline,
    );

    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("healthy links reach the cross-Region acknowledgement: {outcome:?}")
    };
    assert_eq!(report.observed(), AcknowledgementLevel::MajorityRegions);
    let contributed = report.contributed();
    assert_eq!(
        contributed.iter().map(|(region, _)| region.as_str()).collect::<Vec<_>>(),
        ["r-alpha", "r-beta"],
        "the report names the Region the read-your-writes guarantee holds for"
    );

    // After the acknowledgement named r-beta, that Region's Member published
    // the operation and a read there observes it.
    let at_beta = documents_of(&m_b1, "tenant-a");
    assert_eq!(at_beta.len(), 1);
    assert_eq!(at_beta[0].value(), &DocumentValue::String("value-one".to_owned()));
    assert_eq!(at_beta[0].id().as_str(), "doc-1");

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success() {
    let topology = two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member("loss-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("loss-b1", "m-b1", &topology);
    let mut network = ReplicationNetwork::new();

    let scope_a1 = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a1, put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");

    // The r-beta Member is lost before the acknowledgement: its engine is
    // closed, so AllRegions cannot be met and the wait says so with the
    // typed MemberUnavailable category — never a silent success.
    let scope_b1 = scope(&m_b1, "tenant-a");
    m_b1.close();
    let deadline = network.now().plus_ticks(3);
    let mut peers = [(&mut m_b1, scope_b1)];
    let outcome = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::AllRegions,
        &mut peers,
        deadline,
    );
    assert_eq!(
        outcome,
        ReplicationOutcome::Failed(crate::replication::ReplicationError::MemberUnavailable),
        "member loss before acknowledgement is a typed failure: {outcome:?}"
    );

    // Recovery: the member reopens from its durable location, its scope is
    // rebuilt against the new engine identity, and the same operation now
    // reaches the requested level.
    m_b1.restart().expect("the lost member reopens");
    let deadline = network.now().plus_ticks(3);
    let scope_b1 = scope(&m_b1, "tenant-a");
    let mut peers = [(&mut m_b1, scope_b1)];
    let recovered = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::AllRegions,
        &mut peers,
        deadline,
    );
    assert!(
        matches!(recovered, ReplicationOutcome::Acknowledged(_)),
        "the rejoined member contributes again: {recovered:?}"
    );

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit() {
    let topology = three_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member("regionloss-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("regionloss-b1", "m-b1", &topology);
    let (dir_g1, mut m_g1) = open_member("regionloss-g1", "m-g1", &topology);
    let a1 = member_id("m-a1");
    let g1 = member_id("m-g1");
    let mut network = ReplicationNetwork::new();

    // The acknowledgement is reached by a Region majority: r-gamma's link is
    // partitioned so two of three Regions durably record the write.
    network.link(&a1, &g1).partition();
    let scope_a1 = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a1, put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");
    let deadline = network.now().plus_ticks(3);
    let scope_b1 = scope(&m_b1, "tenant-a");
    let scope_g1 = scope(&m_g1, "tenant-a");
    let mut peers = [(&mut m_b1, scope_b1), (&mut m_g1, scope_g1)];
    let outcome = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::MajorityRegions,
        &mut peers,
        deadline,
    );
    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("a Region majority acknowledges without the partitioned Region: {outcome:?}")
    };
    assert_eq!(report.observed(), AcknowledgementLevel::MajorityRegions);

    // The entire Region is then lost: its member is closed. The closed
    // member keeps its scope from before the loss, and its engine being
    // absent is what makes it not durably hold anything observable.
    let scope_b1_observed = scope(&m_b1, "tenant-a");
    let scope_g1_observed = scope(&m_g1, "tenant-a");
    m_g1.close();
    let observation = m_a1.observe_region_loss(
        network.now(),
        &scope_a1,
        &operation("op-1"),
        &report,
        &[(&m_b1, &scope_b1_observed), (&m_g1, &scope_g1_observed)],
        &[crate::replication::RegionId::try_new("r-gamma").expect("valid region")],
    );

    // Durable visibility: exactly the Regions and Members that durably hold
    // the acknowledged write are named.
    let held = observation.durably_held_by();
    assert_eq!(
        held.iter().map(|(region, _)| region.as_str()).collect::<Vec<_>>(),
        ["r-alpha", "r-beta"],
        "the observation states durable visibility: {held:?}"
    );
    assert_eq!(held[0].1.iter().map(MemberId::as_str).collect::<Vec<_>>(), ["m-a1"]);
    assert_eq!(held[1].1.iter().map(MemberId::as_str).collect::<Vec<_>>(), ["m-b1"]);
    assert_eq!(observation.operation().as_str(), "op-1");
    assert_eq!(observation.acknowledged(), AcknowledgementLevel::MajorityRegions);
    // The recovery limit: what is NOT yet known is stated, not guessed.
    assert_eq!(
        observation.unknown_in().iter().map(RegionId::as_str).collect::<Vec<_>>(),
        ["r-gamma"]
    );
    assert_eq!(
        observation.recovery_limit(),
        crate::replication::member::RegionLossLimit::UnknownUntilRejoin,
        "the limit names the condition that would resolve the unknown"
    );

    m_a1.close();
    m_b1.close();
    m_g1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
    std::fs::remove_dir_all(&dir_g1).ok();
}

// ---------------------------------------------------------------------------
// R-2: convergence, the D-3 conflict policy, and D-1 leader fencing.
// ---------------------------------------------------------------------------

/// Divergent membership views for isolated concurrent writers: each side's
/// control-plane state names itself owner of the range at the same epoch,
/// which is exactly a split-brain partition.
fn divergent_owner_views(range: &str) -> (TopologyView, TopologyView) {
    let view_a = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner(range, "m-a1")
        .expect("valid owner")
        .validated()
        .expect("valid topology");
    let view_b = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner(range, "m-b1")
        .expect("valid owner")
        .validated()
        .expect("valid topology");
    (view_a, view_b)
}

/// The healed membership view: `m-b1` owns the range at the next epoch.
fn healed_view(range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner_at_epoch(range, "m-b1", 2)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

#[test]
fn convergence_report_names_members_operation_identity_and_observation_time() {
    let topology = two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member("convergence-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("convergence-b1", "m-b1", &topology);
    let mut network = ReplicationNetwork::new();

    member_put(&mut m_a1, "tenant-a", "main", "op-1", "doc-1", "v1");
    let scope_m_a1 = scope(&m_a1, "tenant-a");
    let scope_m_b1 = scope(&m_b1, "tenant-a");
    let report = synchronize(&mut network, &mut m_a1, &scope_m_a1, &mut m_b1, &scope_m_b1, 4)
        .expect("the write replicates");
    assert!(report.caught_up());

    // Convergence is its own observation, produced by its own seam at an
    // injected lane instant — never inferred from an acknowledgement or a
    // health status.
    let at = network.now().plus_ticks(2);
    let convergence = observe_convergence(
        &operation("op-1"),
        &[(&m_a1, &scope(&m_a1, "tenant-a")), (&m_b1, &scope(&m_b1, "tenant-a"))],
        at,
    );
    assert_eq!(convergence.operation().as_str(), "op-1");
    let holdings = convergence.holdings();
    assert_eq!(
        holdings.iter().map(|(member, _)| member.as_str()).collect::<Vec<_>>(),
        ["m-a1", "m-b1"],
        "the observation names every Member that holds the operation"
    );
    assert!(
        holdings.iter().all(|(_, state)| *state == AcknowledgementLevel::Local),
        "each holding is the Member's own local publication fact: {holdings:?}"
    );
    assert_eq!(convergence.observed_at(), at, "the observation time is the injected instant");

    // A member that does not hold the operation is simply not named as
    // holding it, and a different injected instant is a different
    // observation time — deterministic, driven by the caller.
    let later = observe_convergence(
        &operation("op-2"),
        &[(&m_a1, &scope(&m_a1, "tenant-a")), (&m_b1, &scope(&m_b1, "tenant-a"))],
        at.plus_ticks(5),
    );
    assert!(later.holdings().is_empty(), "an unreplicated operation is held by no one");
    assert_eq!(later.observed_at(), at.plus_ticks(5));

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

/// Runs the isolated-concurrent-writers scenario once and returns the
/// converged value each member holds plus the recorded conflict outcomes.
///
/// Both members run the same deterministic script: divergent views, one
/// write each to the same document key, a lane pump and an injected instant
/// between the writes so the second write is the later one, then healing
/// (one owner at epoch 2 on both sides) and catch-up in both directions.
fn isolated_writers_scenario() -> (String, String, Vec<ConflictOutcome>) {
    let (view_a, view_b) = divergent_owner_views("main");
    let (dir_a1, mut m_a1) = open_member("isolated-a1", "m-a1", &view_a);
    let (dir_b1, mut m_b1) = open_member("isolated-b1", "m-b1", &view_b);
    let mut network = ReplicationNetwork::new();

    let scope_a = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a, put_request("main", "op-a", "doc-shared", "value-a"))
        .expect("the isolated writer commits locally");
    // The lane clock advances and the second writer observes the instant, so
    // its write is deterministically the later one.
    network.pump();
    m_b1.observe_instant(network.now());
    let scope_b = scope(&m_b1, "tenant-a");
    m_b1.commit_write(&scope_b, put_request("main", "op-b", "doc-shared", "value-b"))
        .expect("the isolated writer commits locally");

    // Healing: both sides apply the same converged view.
    let healed = healed_view("main");
    m_a1.apply_view(healed.clone()).expect("the deposed side accepts the healed view");
    m_b1.apply_view(healed).expect("the promoted side accepts the healed view");

    // Catch-up runs in both directions so both writers see each other's
    // journal and the conflict policy decides on both sides.
    let scope_m_a1 = scope(&m_a1, "tenant-a");
    let scope_m_b1 = scope(&m_b1, "tenant-a");
    let report_into_b =
        synchronize(&mut network, &mut m_a1, &scope_m_a1, &mut m_b1, &scope_m_b1, 4)
            .expect("repair into the promoted owner");
    assert!(report_into_b.caught_up());
    let scope_m_b1 = scope(&m_b1, "tenant-a");
    let scope_m_a1 = scope(&m_a1, "tenant-a");
    let report_into_a =
        synchronize(&mut network, &mut m_b1, &scope_m_b1, &mut m_a1, &scope_m_a1, 4)
            .expect("repair into the deposed side");
    assert!(report_into_a.caught_up());

    let value_at = |member: &ReplicationMember| {
        documents_of(member, "tenant-a")
            .into_iter()
            .find(|document| document.id().as_str() == "doc-shared")
            .map_or_else(
                || "absent".to_owned(),
                |document| match document.value() {
                    DocumentValue::String(text) => text.clone(),
                    _ => "non-text".to_owned(),
                },
            )
    };
    let outcomes = m_a1
        .conflicts()
        .iter()
        .chain(m_b1.conflicts().iter())
        .map(ConflictRecord::outcome)
        .collect::<Vec<_>>();
    let value_at_a = value_at(&m_a1);
    let value_at_b = value_at(&m_b1);

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
    (value_at_a, value_at_b, outcomes)
}

#[test]
fn isolated_concurrent_writes_converge_deterministically_after_healing() {
    // One run establishes the outcome; the second run proves it is
    // deterministic — the same injected instants and the same script must
    // produce the same converged values and the same recorded outcomes.
    let first = isolated_writers_scenario();
    let second = isolated_writers_scenario();
    assert_eq!(first, second, "the convergence outcome is deterministic");

    let (value_at_a, value_at_b, outcomes) = first;
    assert_eq!(value_at_a, "value-b", "the deposed side converges to the later write");
    assert_eq!(value_at_b, "value-b", "the promoted side keeps the later write");
    assert_eq!(value_at_a, value_at_b, "both sides hold the same value after healing");
    assert!(
        outcomes.iter().all(|outcome| matches!(outcome, ConflictOutcome::Winner(member)
            if member.as_str() == "m-b1")),
        "the recorded policy outcome names the later writer everywhere: {outcomes:?}"
    );
    assert!(!outcomes.is_empty(), "the losing write is retained as observable history");
}

#[test]
fn conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record() {
    let (view_a, view_b) = divergent_owner_views("main");
    let (dir_a1, mut m_a1) = open_member("conflict-a1", "m-a1", &view_a);
    let (dir_b1, mut m_b1) = open_member("conflict-b1", "m-b1", &view_b);
    let mut network = ReplicationNetwork::new();

    let scope_a = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a, put_request("main", "op-a", "doc-shared", "value-a"))
        .expect("writer one commits");
    network.pump();
    m_b1.observe_instant(network.now());
    let scope_b = scope(&m_b1, "tenant-a");
    m_b1.commit_write(&scope_b, put_request("main", "op-b", "doc-shared", "value-b"))
        .expect("writer two commits");

    let healed = healed_view("main");
    m_a1.apply_view(healed.clone()).expect("healed view applies");
    m_b1.apply_view(healed).expect("healed view applies");
    let scope_m_a1 = scope(&m_a1, "tenant-a");
    let scope_m_b1 = scope(&m_b1, "tenant-a");
    let report_into_b =
        synchronize(&mut network, &mut m_a1, &scope_m_a1, &mut m_b1, &scope_m_b1, 4)
            .expect("repair into the promoted owner");
    assert!(report_into_b.caught_up());
    assert!(
        report_into_b.conflicts_recorded() >= 1,
        "the apply pass reports the conflict it recorded"
    );

    let record = m_b1
        .conflicts()
        .last()
        .expect("the conflict is retained as observable history on the detecting side");
    // The recorded policy outcome: last-writer-wins on the bounded hybrid
    // logical clock, naming the winner.
    assert_eq!(
        record.outcome(),
        ConflictOutcome::Winner(member_id("m-b1")),
        "the policy outcome is the recorded decision"
    );
    let mut operations = record.operations().to_vec();
    operations.sort();
    assert_eq!(
        operations.iter().map(OperationId::as_str).collect::<Vec<_>>(),
        ["op-a", "op-b"],
        "the record names both conflicting operation identities"
    );
    let mut members = record.members().to_vec();
    members.sort();
    assert_eq!(
        members.iter().map(MemberId::as_str).collect::<Vec<_>>(),
        ["m-a1", "m-b1"],
        "the record names both writer identities"
    );
    assert_eq!(record.clocks().len(), 2, "the record carries both clocks");

    // Content-free: no document payload, field value, or collection text
    // appears anywhere in the record.
    let rendered = format!("{record:?}");
    for forbidden in ["value-a", "value-b", "users", "doc-shared", "payload"] {
        assert!(
            !rendered.contains(forbidden),
            "the conflict record carries document content ({forbidden}): {rendered}"
        );
    }
    let all_records = format!("{:?}", m_b1.conflicts());
    for forbidden in ["value-a", "value-b", "doc-shared"] {
        assert!(
            !all_records.contains(forbidden),
            "no retained conflict record carries document content: {all_records}"
        );
    }

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn leader_loss_stops_new_commits_until_a_new_leader_is_confirmed() {
    let topology = two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member("leaderloss-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("leaderloss-b1", "m-b1", &topology);
    let mut network = ReplicationNetwork::new();

    member_put(&mut m_a1, "tenant-a", "main", "op-1", "doc-1", "v1");
    let scope_m_a1 = scope(&m_a1, "tenant-a");
    let scope_m_b1 = scope(&m_b1, "tenant-a");
    let report = synchronize(&mut network, &mut m_a1, &scope_m_a1, &mut m_b1, &scope_m_b1, 4)
        .expect("the follower holds the range state");
    assert!(report.caught_up());

    // The leader is lost: its member closes. No surviving Member owns the
    // range's write path under the current view, so every new commit fails
    // admission with the typed error — there is no silent failover.
    m_a1.close();
    let scope_b = scope(&m_b1, "tenant-a");
    let stopped = m_b1.commit_write(&scope_b, put_request("main", "op-2", "doc-2", "v2"));
    assert!(
        matches!(stopped, Err(MemberError::NotOwner { ref range }) if range == "main"),
        "new commits stop while the leader is lost: {stopped:?}"
    );
    assert!(documents_of(&m_b1, "tenant-a").iter().all(|d| d.id().as_str() != "doc-2"));

    // A new leader is confirmed by applying the transitioned view, and only
    // then do commits to the range resume.
    m_b1.apply_view(healed_view("main")).expect("the promoted leader is confirmed");
    let resumed = m_b1.commit_write(&scope_b, put_request("main", "op-2", "doc-2", "v2"));
    assert!(
        matches!(resumed, Ok(ref receipt) if receipt.durability() == LocalDurability::Synced),
        "commits resume once the new leader is confirmed: {resumed:?}"
    );
    assert!(documents_of(&m_b1, "tenant-a").iter().any(|d| d.id().as_str() == "doc-2"));

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
#[allow(clippy::too_many_lines, reason = "one fencing scenario proving all four D-1 clauses")]
fn old_owner_is_fenced_after_leader_transition() {
    let topology = two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member("fence-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("fence-b1", "m-b1", &topology);
    let a1 = member_id("m-a1");
    let b1 = member_id("m-b1");
    let mut network = ReplicationNetwork::new();

    // The deposed leader writes before learning of the transition, and the
    // new leader writes its own concurrent value after being promoted.
    let scope_a = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a, put_request("main", "op-a", "doc-shared", "value-a"))
        .expect("the old owner's write keeps its local receipt");
    let transitioned = healed_view("main");
    m_a1.apply_view(transitioned.clone()).expect("the old owner learns of the transition");
    m_b1.apply_view(transitioned).expect("the new owner is confirmed");
    network.pump();
    m_b1.observe_instant(network.now());
    let scope_b = scope(&m_b1, "tenant-a");
    m_b1.commit_write(&scope_b, put_request("main", "op-b", "doc-shared", "value-b"))
        .expect("the new owner commits");

    // Admission fencing: the deposed owner's new writes fail locally, with
    // no cross-Member round trip.
    let deposed_write =
        m_a1.commit_write(&scope_a, put_request("main", "op-late", "doc-late", "late"));
    assert!(
        matches!(deposed_write, Err(MemberError::NotOwner { ref range }) if range == "main"),
        "a deposed owner's new write fails admission: {deposed_write:?}"
    );

    // Apply-time fencing: a batch the deposed owner raced past its
    // deposition is rejected by the new owner's epoch check, and its commits
    // are retained as observable conflict history, never silently dropped.
    let in_flight = PublishedCommit::new(
        tenant("tenant-a"),
        operation("op-a"),
        StorageSequence::new(1),
        LocalDurability::Synced,
        vec![PublishedWrite::Put { document: text_document("users", "doc-shared", "value-a") }],
    );
    network
        .send(
            &a1,
            &b1,
            ReplicationFrame::Push {
                source: a1.clone(),
                range: "main".to_owned(),
                epoch: 1,
                tenant: tenant("tenant-a"),
                commits: vec![StampedCommit::at_origin_clock(in_flight)],
            },
        )
        .expect("bounded link accepts the push frame");
    network.pump();
    let fenced = m_b1.apply_deliveries(&mut network, &scope_b);
    assert!(
        matches!(&fenced, Err(MemberError::FencedBatch { range, batch_epoch, highest_epoch })
            if range == "main" && *batch_epoch == 1 && *highest_epoch == 2),
        "a below-epoch batch is rejected with a typed error: {fenced:?}"
    );
    assert_eq!(m_b1.fenced().len(), 1, "the fencing is observable");
    assert_eq!(m_b1.fenced()[0].range(), "main");
    assert_eq!(m_b1.fenced()[0].source().as_str(), "m-a1");
    let retained = m_b1
        .conflicts()
        .iter()
        .find(|record| matches!(record.outcome(), ConflictOutcome::DeposedOwnerRetained(_)));
    assert!(
        retained.is_some(),
        "the deposed owner's write is retained as conflict history, not discarded"
    );
    let rendered = format!("{:?}", m_b1.conflicts());
    assert!(!rendered.contains("value-a"), "the retained record stays content-free");

    // The deposed owner's local receipt is still real locally (a local
    // durable fact), and its write converges under D-3 once repair runs.
    let resolution = m_a1
        .engine()
        .expect("engine open")
        .resolve_commit(&scope_a, operation("op-a"))
        .expect("durable resolution");
    assert!(
        matches!(&resolution, crate::engine::DurableResolution::Committed(receipt)
            if receipt.durability() == LocalDurability::Synced),
        "the deposed owner keeps its local receipt: {resolution:?}"
    );
    let scope_m_a1 = scope(&m_a1, "tenant-a");
    let scope_m_b1 = scope(&m_b1, "tenant-a");
    let into_b = synchronize(&mut network, &mut m_a1, &scope_m_a1, &mut m_b1, &scope_m_b1, 4)
        .expect("repair into the new owner");
    assert!(into_b.caught_up());
    let scope_m_b1 = scope(&m_b1, "tenant-a");
    let scope_m_a1 = scope(&m_a1, "tenant-a");
    let into_a = synchronize(&mut network, &mut m_b1, &scope_m_b1, &mut m_a1, &scope_m_a1, 4)
        .expect("repair into the deposed owner");
    assert!(into_a.caught_up());
    // The converged value, not the per-member document version, is what
    // convergence means here: replay assigns each member its own version
    // counter, so identity and value are compared.
    let shared_at_a = documents_of(&m_a1, "tenant-a")
        .into_iter()
        .find(|document| document.id().as_str() == "doc-shared")
        .expect("the converged document exists at the deposed owner");
    let shared_at_b = documents_of(&m_b1, "tenant-a")
        .into_iter()
        .find(|document| document.id().as_str() == "doc-shared")
        .expect("the converged document exists at the new owner");
    assert_eq!(shared_at_a.id(), shared_at_b.id());
    assert_eq!(shared_at_a.value(), shared_at_b.value());
    assert_eq!(shared_at_a.value(), &DocumentValue::String("value-b".to_owned()));

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn a_peer_stamp_beyond_the_skew_bound_converges_as_a_conflict() {
    // The observing member owns the range, so it holds its own write to the
    // key that the untrusted stamp competes with.
    let topology = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner("main", "m-b1")
        .expect("valid owner")
        .validated()
        .expect("valid topology");
    let (dir_a1, mut m_a1) = open_member("skew-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("skew-b1", "m-b1", &topology);
    let a1 = member_id("m-a1");
    let b1 = member_id("m-b1");
    let mut network = ReplicationNetwork::new();

    let scope_b = scope(&m_b1, "tenant-a");
    m_b1.commit_write(&scope_b, put_request("main", "op-b", "doc-1", "value-b"))
        .expect("the local write commits");

    // A peer claims a stamp far beyond the lane clock plus the skew bound:
    // its timestamps are untrusted, so the pair converges as a conflict —
    // not rejected, not clamped — decided by the deterministic tie-break.
    let beyond_bound = PublishedCommit::new(
        tenant("tenant-a"),
        operation("op-future"),
        StorageSequence::new(7),
        LocalDurability::Synced,
        vec![PublishedWrite::Put { document: text_document("users", "doc-1", "value-future") }],
    );
    network
        .send(
            &a1,
            &b1,
            ReplicationFrame::Push {
                source: a1.clone(),
                range: "main".to_owned(),
                epoch: 1,
                tenant: tenant("tenant-a"),
                commits: vec![StampedCommit {
                    stamp: ReplicationClockStamp::new(
                        network.now().ticks().saturating_add(1_000),
                        1,
                    ),
                    commit: beyond_bound,
                }],
            },
        )
        .expect("bounded link accepts the push frame");
    network.pump();
    let applied = m_b1.apply_deliveries(&mut network, &scope_b);
    assert!(applied.is_ok(), "an untrusted stamp is a conflict, not a rejection: {applied:?}");
    let record = m_b1
        .conflicts()
        .iter()
        .find(|record| matches!(record.outcome(), ConflictOutcome::UntrustedClock(_)))
        .expect("the untrusted clock is recorded as a conflict");
    let rendered = format!("{record:?}");
    assert!(!rendered.contains("value-future"), "the record stays content-free");
    // The deterministic tie-break resolved the pair: both members' views are
    // single-valued (the key holds exactly one document).
    let view = documents_of(&m_b1, "tenant-a");
    assert_eq!(
        view.iter().filter(|document| document.id().as_str() == "doc-1").count(),
        1,
        "the key holds exactly one value after the untrusted-clock conflict"
    );

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}
