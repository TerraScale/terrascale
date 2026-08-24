//! In-crate repair-loop tests for the Warden's W4-REPLICATION findings.
//!
//! Each test here is the regression proof for one finding from repair loop
//! 1: it failed on the code as handed back by the R-3 phase, and the fix
//! that follows it is the minimal change that makes it pass. The fixtures
//! mirror `tests.rs`: `TenantScope::from_parts` stands in for Platform's
//! scope-minting role, every engine is durable in a unique temporary
//! directory, and time moves only through pumps and injected lane instants.

use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use super::member::{MemberError, ReplicationMember, ReplicationWrite, synchronize};
use super::transport::{ReplicationFrame, ReplicationNetwork, StampedCommit};
use super::{AcknowledgementLevel, ConflictOutcome, MemberId, ReplicationOutcome, TopologyView};
use crate::engine::{CommitReceipt, DurableResolution, EngineConfig};
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, LocalDurability, OperationId,
    StorageSequence, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-w4-repair-{tag}-{}-{index}", std::process::id()))
}

fn open_member(tag: &str, id: &str, topology: &TopologyView) -> (PathBuf, ReplicationMember) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let config = super::ReplicationConfig::try_new(topology.clone()).expect("valid config");
    let member = ReplicationMember::try_new(
        config,
        MemberId::try_new(id).expect("valid member identifier"),
        EngineConfig::durable(&dir).expect("durable location"),
    )
    .expect("member opens");
    (dir, member)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn member_id(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid member identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn text_document(id: &str, text: &str) -> Document {
    Document::new(
        document_id(id),
        CollectionName::try_new("users").expect("valid collection"),
        DocumentValue::String(text.to_owned()),
    )
}

fn put_request(range: &str, operation_name: &str, id: &str, text: &str) -> ReplicationWrite {
    ReplicationWrite::new(operation(operation_name), range, AcknowledgementLevel::Local)
        .expect("valid write request")
        .with_put(text_document(id, text))
}

fn scope(member: &ReplicationMember, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), member.engine().expect("engine open").identity())
}

fn holds(member: &ReplicationMember, tenant_name: &str, operation_name: &str) -> bool {
    let Some(engine) = member.engine() else { return false };
    matches!(
        engine.resolve_commit(&scope(member, tenant_name), operation(operation_name)),
        Ok(DurableResolution::Committed(_))
    )
}

fn value_of(member: &ReplicationMember, tenant_name: &str, id: &str) -> Option<String> {
    let member_scope = scope(member, tenant_name);
    let engine = member.engine().expect("engine open");
    let snapshot = engine.read_snapshot(&member_scope).expect("snapshot");
    engine
        .snapshot_documents(&member_scope, &snapshot)
        .expect("documents")
        .into_iter()
        .find(|document| document.id().as_str() == id)
        .and_then(|document| match document.value() {
            DocumentValue::String(text) => Some(text.clone()),
            _ => None,
        })
}

// ---------------------------------------------------------------------------
// M1 — the home Region of a write is the writer's Region, not the Region of
// whichever contributing member happens to be seated first.
// ---------------------------------------------------------------------------

#[test]
fn region_quorum_counts_only_the_writers_home_region() {
    // Seating order places r-alpha's lone member first, so the buggy
    // derivation reads the home Region from it; the writer lives in r-beta.
    let topology = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-x", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-a1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_member("m-a2", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner("main", "m-a1")
        .expect("valid owner")
        .validated()
        .expect("valid topology");
    let (dir_x, mut m_x) = open_member("m1-x", "m-x", &topology);
    let (dir_a1, mut m_a1) = open_member("m1-a1", "m-a1", &topology);
    let (dir_a2, mut m_a2) = open_member("m1-a2", "m-a2", &topology);
    let mut network = ReplicationNetwork::new();

    let scope_a1 = scope(&m_a1, "tenant-a");
    m_a1
        .commit_write(&scope_a1, put_request("main", "op-1", "doc-1", "v1"))
        .expect("the writer commits locally");

    // The writer's home-Region peer is lost; the foreign-Region member is
    // alive and will durably record the write. That must not satisfy a
    // Region quorum of the writer's home Region.
    let scope_x = scope(&m_x, "tenant-a");
    let scope_a2 = scope(&m_a2, "tenant-a");
    m_a2.close();
    let deadline = network.now().plus_ticks(3);
    let mut peers = [(&mut m_x, scope_x.clone()), (&mut m_a2, scope_a2)];
    let outcome = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::RegionQuorum,
        &mut peers,
        deadline,
    );
    assert_eq!(
        outcome,
        ReplicationOutcome::Failed(crate::replication::ReplicationError::QuorumUnreachable),
        "a foreign Region's member cannot supply the writer's home-Region quorum: {outcome:?}"
    );
    assert!(
        holds(&m_x, "tenant-a", "op-1"),
        "the foreign member still durably recorded the write (it is simply not a quorum of home)"
    );

    // Recovery: the home-Region peer reopens, and the same request now holds
    // with exactly the home Region's Members named.
    m_a2.restart().expect("the home-Region peer reopens");
    let scope_a2 = scope(&m_a2, "tenant-a");
    let scope_x = scope(&m_x, "tenant-a");
    let deadline = network.now().plus_ticks(3);
    let mut peers = [(&mut m_x, scope_x), (&mut m_a2, scope_a2)];
    let recovered = m_a1.wait_for_acknowledgement(
        &mut network,
        &scope_a1,
        &operation("op-1"),
        AcknowledgementLevel::RegionQuorum,
        &mut peers,
        deadline,
    );
    let ReplicationOutcome::Acknowledged(report) = recovered else {
        panic!("the home Region reaches its quorum after recovery: {recovered:?}")
    };
    assert_eq!(report.observed(), AcknowledgementLevel::RegionQuorum);
    let home = report
        .contributed()
        .iter()
        .find(|(region, _)| region.as_str() == "r-beta")
        .expect("the report names the writer's home Region");
    assert_eq!(
        home.1.iter().map(MemberId::as_str).collect::<Vec<_>>(),
        ["m-a1", "m-a2"],
        "the home-Region quorum is the writer's Region's Members"
    );

    m_x.close();
    m_a1.close();
    m_a2.close();
    std::fs::remove_dir_all(&dir_x).ok();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_a2).ok();
}

// ---------------------------------------------------------------------------
// M2 — resumption is per source: the old source's advisory cursor must not
// mask a new source's commits, and two sources' commits never collide in the
// pass merge.
// ---------------------------------------------------------------------------

/// The M2 topology: `m-a1` owns `main`, `m-b1` owns `archive`, `m-c1` owns
/// nothing (the follower), each in its own Region.
fn m2_topology(main_epoch: u64) -> TopologyView {
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
        .with_member("m-c1", "r-gamma", "fd-gamma")
        .expect("valid member")
        .with_owner_at_epoch("main", "m-a1", main_epoch)
        .expect("valid owner")
        .with_owner_at_epoch("archive", "m-b1", 1)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

#[test]
fn catch_up_from_a_new_source_is_not_masked_by_the_old_source_cursor() {
    let topology = m2_topology(1);
    let (dir_a, mut m_a1) = open_member("m2-a1", "m-a1", &topology);
    let (dir_b, mut m_b1) = open_member("m2-b1", "m-b1", &topology);
    let (dir_c, mut m_c1) = open_member("m2-c1", "m-c1", &topology);
    let mut network = ReplicationNetwork::new();

    // The follower catches up from source A, whose commits occupy sequences
    // 1 and 2 of its journal.
    m_a1.commit_write(&scope(&m_a1, "tenant-a"), put_request("main", "op-a1", "doc-a1", "va1"))
        .expect("A writes");
    m_a1.commit_write(&scope(&m_a1, "tenant-a"), put_request("main", "op-a2", "doc-a2", "va2"))
        .expect("A writes");
    let scope_a = scope(&m_a1, "tenant-a");
    let scope_c = scope(&m_c1, "tenant-a");
    let from_a = synchronize(&mut network, &mut m_a1, &scope_a, &mut m_c1, &scope_c, 4)
        .expect("catch-up from A runs");
    assert!(from_a.caught_up());
    assert!(holds(&m_c1, "tenant-a", "op-a1") && holds(&m_c1, "tenant-a", "op-a2"));

    // Source B owns a different range from the start, so its own legitimate
    // commits occupy the SAME low sequences of its journal while remaining
    // unknown to the follower.
    m_b1
        .commit_write(&scope(&m_b1, "tenant-a"), put_request("archive", "op-b1", "doc-b1", "vb1"))
        .expect("B writes its own range");
    m_b1
        .commit_write(&scope(&m_b1, "tenant-a"), put_request("archive", "op-b2", "doc-b2", "vb2"))
        .expect("B writes its own range");

    // The control plane transitions the write path to B; the follower
    // applies the transitioned view and catches up from the new source.
    let transitioned = TopologyView::new()
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
        .with_member("m-c1", "r-gamma", "fd-gamma")
        .expect("valid member")
        .with_owner_at_epoch("main", "m-b1", 2)
        .expect("valid owner")
        .with_owner_at_epoch("archive", "m-b1", 2)
        .expect("valid owner")
        .validated()
        .expect("valid topology");
    m_c1.apply_view(transitioned).expect("the follower applies the transition");

    let scope_b = scope(&m_b1, "tenant-a");
    let from_b = synchronize(&mut network, &mut m_b1, &scope_b, &mut m_c1, &scope_c, 4)
        .expect("catch-up from the new source runs");
    assert!(
        holds(&m_c1, "tenant-a", "op-b1") && holds(&m_c1, "tenant-a", "op-b2"),
        "the new source's low-sequence commits must not be masked by the old source's cursor"
    );
    assert_eq!(
        value_of(&m_c1, "tenant-a", "doc-b1").as_deref(),
        Some("vb1"),
        "the new source's write is visible after the transition"
    );
    assert!(from_b.caught_up());

    // A repeated exchange from the same source applies nothing new: the
    // per-source cursor now correctly masks the redelivery.
    let again = synchronize(&mut network, &mut m_b1, &scope_b, &mut m_c1, &scope_c, 4)
        .expect("the repeated exchange runs");
    assert_eq!(again.applied(), 0, "duplicates resolve once through the per-source cursor");
    assert!(again.caught_up());

    m_a1.close();
    m_b1.close();
    m_c1.close();
    std::fs::remove_dir_all(&dir_a).ok();
    std::fs::remove_dir_all(&dir_b).ok();
    std::fs::remove_dir_all(&dir_c).ok();
}

#[test]
fn commits_from_two_sources_sharing_a_sequence_both_apply() {
    let topology = m2_topology(1);
    let (dir_a, mut m_a1) = open_member("m2s-a1", "m-a1", &topology);
    let (dir_b, mut m_b1) = open_member("m2s-b1", "m-b1", &topology);
    let (dir_c, mut m_c1) = open_member("m2s-c1", "m-c1", &topology);
    let a1 = member_id("m-a1");
    let b1 = member_id("m-b1");
    let c1 = member_id("m-c1");
    let mut network = ReplicationNetwork::new();

    // Both sources hold exactly one commit for the tenant, each at its own
    // journal's sequence 1.
    m_a1.commit_write(&scope(&m_a1, "tenant-a"), put_request("main", "op-a1", "doc-a1", "va1"))
        .expect("A writes");
    m_b1
        .commit_write(&scope(&m_b1, "tenant-a"), put_request("archive", "op-b1", "doc-b1", "vb1"))
        .expect("B writes");

    // One delivery pass receives both sources' commits at once — the pass
    // merge must not let one source's sequence-1 commit overwrite the
    // other's.
    let commit_of = |member: &ReplicationMember, name: &str| {
        let engine = member.engine().expect("engine open");
        let commits = engine
            .tenant_commits(&tenant("tenant-a"), StorageSequence::new(0), 10)
            .expect("journal read");
        let commit = commits.into_iter().find(|c| c.operation_id().as_str() == name).expect("op");
        StampedCommit::at_origin_clock(commit)
    };
    for (source, name) in [(&a1, "op-a1"), (&b1, "op-b1")] {
        network
            .send(
                source,
                &c1,
                ReplicationFrame::Push {
                    source: source.clone(),
                    range: "main".to_owned(),
                    epoch: 1,
                    tenant: tenant("tenant-a"),
                    commits: vec![commit_of(if source == &a1 { &m_a1 } else { &m_b1 }, name)],
                },
            )
            .expect("bounded link accepts the push frame");
    }
    network.pump();
    let report = m_c1
        .apply_deliveries(&mut network, &scope(&m_c1, "tenant-a"))
        .expect("both sources' commits apply in one pass");
    assert_eq!(report.applied(), 2, "both sequence-1 commits applied: {report:?}");
    assert!(
        holds(&m_c1, "tenant-a", "op-a1") && holds(&m_c1, "tenant-a", "op-b1"),
        "no commit was silently overwritten by the shared sequence"
    );

    m_a1.close();
    m_b1.close();
    m_c1.close();
    std::fs::remove_dir_all(&dir_a).ok();
    std::fs::remove_dir_all(&dir_b).ok();
    std::fs::remove_dir_all(&dir_c).ok();
}

// ---------------------------------------------------------------------------
// M3 — a remote-applied key state competes: a third replica converges by the
// (stamp, Member) total order, not by arrival order.
// ---------------------------------------------------------------------------

#[test]
fn a_third_replica_converges_remote_competing_writes_by_stamp_not_arrival() {
    // R's view names W1 the owner; W2 runs the divergent view naming itself
    // owner at the same epoch — the split-brain state under which both
    // writers legitimately commit to the same key.
    let base = m2_topology(1);
    let divergent = TopologyView::new()
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
        .with_member("m-c1", "r-gamma", "fd-gamma")
        .expect("valid member")
        .with_owner_at_epoch("main", "m-b1", 1)
        .expect("valid owner")
        .with_owner_at_epoch("archive", "m-b1", 1)
        .expect("valid owner")
        .validated()
        .expect("valid topology");
    let (dir_a, mut w1) = open_member("m3-w1", "m-a1", &base);
    let (dir_b, mut w2) = open_member("m3-w2", "m-b1", &divergent);
    let (dir_c, mut replica) = open_member("m3-r", "m-c1", &base);
    let mut network = ReplicationNetwork::new();

    // W1 writes first; the lane clock advances; W2 observes the instant and
    // writes the same key, so W2's stamp is deterministically the later one.
    w1.commit_write(&scope(&w1, "tenant-a"), put_request("main", "op-old", "doc-shared", "older"))
        .expect("the earlier writer commits");
    network.pump();
    w2.observe_instant(network.now());
    w2.commit_write(&scope(&w2, "tenant-a"), put_request("main", "op-new", "doc-shared", "newer"))
        .expect("the later writer commits");

    // Arrival order delivers the NEWER write first and the older one second
    // — the order under which arrival-order convergence would pick the
    // wrong winner.
    let scope_w2 = scope(&w2, "tenant-a");
    let scope_r = scope(&replica, "tenant-a");
    let newer_first = synchronize(&mut network, &mut w2, &scope_w2, &mut replica, &scope_r, 4)
        .expect("the newer write replicates");
    assert!(newer_first.caught_up());
    assert_eq!(value_of(&replica, "tenant-a", "doc-shared").as_deref(), Some("newer"));

    let scope_w1 = scope(&w1, "tenant-a");
    let older_second = synchronize(&mut network, &mut w1, &scope_w1, &mut replica, &scope_r, 4)
        .expect("the older write arrives");
    assert!(older_second.caught_up());

    assert_eq!(
        value_of(&replica, "tenant-a", "doc-shared").as_deref(),
        Some("newer"),
        "the replica converges to the (stamp, Member) winner, not the last arrival"
    );
    assert!(
        replica.conflicts().iter().any(|record| matches!(
            record.outcome(),
            ConflictOutcome::Winner(member) if member.as_str() == "m-b1"
        )),
        "the loser is retained as a conflict record naming the true winner: {:?}",
        replica.conflicts()
    );
    let rendered = format!("{:?}", replica.conflicts());
    assert!(!rendered.contains("older") && !rendered.contains("newer"), "content-free");

    // The writer side reaches the same verdict, so the pair has converged.
    assert_eq!(value_of(&w1, "tenant-a", "doc-shared").as_deref(), Some("newer"));
    assert_eq!(value_of(&w2, "tenant-a", "doc-shared").as_deref(), Some("newer"));

    w1.close();
    w2.close();
    replica.close();
    std::fs::remove_dir_all(&dir_a).ok();
    std::fs::remove_dir_all(&dir_b).ok();
    std::fs::remove_dir_all(&dir_c).ok();
}

// ---------------------------------------------------------------------------
// m4 — an operation past the head-scan bound still acknowledges.
// ---------------------------------------------------------------------------

#[test]
fn an_operation_beyond_the_head_scan_bound_still_acknowledges() {
    let topology = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_owner("main", "m-a1")
        .expect("valid owner")
        .validated()
        .expect("valid topology");
    let (dir, mut member) = open_member("m4-bound", "m-a1", &topology);
    let member_scope = scope(&member, "tenant-a");

    // More commits in one tenant than the catch-up batch bound the scan used
    // to stop at.
    let total = usize::try_from(super::topology::MAX_CATCH_UP_BATCH).expect("usize batch")
        + 6;
    for index in 1..=total {
        let name = format!("op-{index:04}");
        member
            .commit_write(
                &member_scope,
                put_request("main", &name, &format!("doc-{index:04}"), "v"),
            )
            .expect("the tenant's writes commit");
    }
    let last = format!("op-{total:04}");
    let deadline = super::ReplicationInstant::at_ticks(0).plus_ticks(1);
    let outcome = member.wait_for_acknowledgement(
        &mut ReplicationNetwork::new(),
        &member_scope,
        &operation(&last),
        AcknowledgementLevel::Local,
        &mut [],
        deadline,
    );
    assert!(
        matches!(outcome, ReplicationOutcome::Acknowledged(_)),
        "a committed operation past the head-scan bound still acknowledges: {outcome:?}"
    );

    member.close();
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// m5 — the observable logs are bounded.
// ---------------------------------------------------------------------------

#[test]
fn observation_logs_are_bounded() {
    let base = |main_owner: &str, main_epoch: u64| {
        TopologyView::new()
            .with_region("r-alpha")
            .expect("valid region")
            .with_region("r-beta")
            .expect("valid region")
            .with_member("m-a1", "r-alpha", "fd-alpha")
            .expect("valid member")
            .with_member("m-x", "r-alpha", "fd-alpha")
            .expect("valid member")
            .with_member("m-b1", "r-beta", "fd-beta")
            .expect("valid member")
            .with_owner_at_epoch("main", main_owner, main_epoch)
            .expect("valid owner")
            .validated()
            .expect("valid topology")
    };
    let (dir, mut member) = open_member("m5-logs", "m-a1", &base("m-a1", 1));
    let (dir_x, mut m_x) = open_member("m5-x", "m-x", &base("m-a1", 1));
    let mut network = ReplicationNetwork::new();

    // Depositions: alternate losing and regaining ownership far past the
    // log bound.
    let mut epoch = 1u64;
    for _ in 0..80 {
        epoch += 1;
        member.apply_view(base("m-x", epoch)).expect("the deposing view applies");
        epoch += 1;
        member.apply_view(base("m-a1", epoch)).expect("the regaining view applies");
    }
    assert_eq!(member.depositions().len(), 80.min(64), "the deposition log is bounded");

    // Fenced batches: pushes stamped far below the highest seen epoch.
    m_x.apply_view(base("m-a1", 1)).expect("the peer's view applies");
    let hostile = PublishedCommit::new(
        tenant("tenant-a"),
        operation("op-stale"),
        StorageSequence::new(1),
        LocalDurability::Synced,
        vec![PublishedWrite::Put { document: text_document("doc-stale", "stale") }],
    );
    for _ in 0..80 {
        network
            .send(
                &member_id("m-x"),
                &member_id("m-a1"),
                ReplicationFrame::Push {
                    source: member_id("m-x"),
                    range: "main".to_owned(),
                    epoch: 1,
                    tenant: tenant("tenant-a"),
                    commits: vec![StampedCommit::at_origin_clock(hostile.clone())],
                },
            )
            .expect("bounded link accepts the push frame");
        network.pump();
        let fenced = member.apply_deliveries(&mut network, &scope(&member, "tenant-a"));
        assert!(matches!(fenced, Err(MemberError::FencedBatch { .. })), "stale pushes stay fenced");
    }
    assert_eq!(member.fenced().len(), 80.min(64), "the fenced-batch log is bounded");

    // Catch-up fallbacks: alternate a real exchange (which restores the
    // per-source advisory) with a batch that flags the advisory cursor as
    // unservable.
    m_x.commit_write(&scope(&m_x, "tenant-a"), put_request("main", "op-1", "doc-1", "v1"))
        .expect("the peer writes");
    let x_id = member_id("m-x");
    let my_id = member_id("m-a1");
    for _ in 0..80 {
        let scope_x = scope(&m_x, "tenant-a");
        let scope_mine = scope(&member, "tenant-a");
        synchronize(&mut network, &mut m_x, &scope_x, &mut member, &scope_mine, 4)
            .expect("the exchange runs");
        let advisory = member.advisory_progress(&tenant("tenant-a")).expect("advisory set");
        network
            .send(
                &x_id,
                &my_id,
                ReplicationFrame::CatchUpBatch {
                    source: x_id.clone(),
                    tenant: tenant("tenant-a"),
                    commits: Vec::new(),
                    through: None,
                    tenant_high_water: Some(advisory),
                    servable_from: Some(StorageSequence::new(advisory.get() + 1)),
                    gap_below: Some(advisory),
                },
            )
            .expect("bounded link accepts the batch frame");
        network.pump();
        member
            .apply_deliveries(&mut network, &scope_mine)
            .expect("the flagged batch falls back");
    }
    assert_eq!(member.fallbacks().len(), 80.min(64), "the fallback log is bounded");

    member.close();
    m_x.close();
    std::fs::remove_dir_all(&dir).ok();
    std::fs::remove_dir_all(&dir_x).ok();
}

// ---------------------------------------------------------------------------
// m6 — a view may not lower (or flip without raising) the epoch of a range
// the member does not own.
// ---------------------------------------------------------------------------

#[test]
fn a_view_may_not_lower_the_epoch_of_a_range_the_member_does_not_own() {
    let view = |main_owner: &str, main_epoch: u64| {
        TopologyView::new()
            .with_region("r-alpha")
            .expect("valid region")
            .with_region("r-beta")
            .expect("valid region")
            .with_member("m-a1", "r-alpha", "fd-alpha")
            .expect("valid member")
            .with_member("m-x", "r-alpha", "fd-alpha")
            .expect("valid member")
            .with_member("m-b1", "r-beta", "fd-beta")
            .expect("valid member")
            .with_owner_at_epoch("main", main_owner, main_epoch)
            .expect("valid owner")
            .validated()
            .expect("valid topology")
    };
    // The member owns nothing; its view names main at epoch 3.
    let (dir, mut member) = open_member("m6-view", "m-b1", &view("m-a1", 3));

    // A lowered epoch on the non-owned range is a stale transition.
    let lowered = member.apply_view(view("m-a1", 2));
    assert!(
        matches!(lowered, Err(MemberError::StaleView { ref range, current_epoch, offered_epoch })
            if range == "main" && current_epoch == 3 && offered_epoch == 2),
        "a lowered non-owned epoch is rejected: {lowered:?}"
    );

    // An owner flip without a raised epoch is a stale transition too.
    let flipped = member.apply_view(view("m-x", 3));
    assert!(
        matches!(flipped, Err(MemberError::StaleView { .. })),
        "a non-raised owner flip on a non-owned range is rejected: {flipped:?}"
    );

    // The rejected views left the fencing state intact: a push at the
    // lowered epoch is still fenced by the highest epoch seen.
    let mut network = ReplicationNetwork::new();
    let hostile = PublishedCommit::new(
        tenant("tenant-a"),
        operation("op-stale"),
        StorageSequence::new(1),
        LocalDurability::Synced,
        vec![PublishedWrite::Put { document: text_document("doc-stale", "stale") }],
    );
    network
        .send(
            &member_id("m-a1"),
            &member_id("m-b1"),
            ReplicationFrame::Push {
                source: member_id("m-a1"),
                range: "main".to_owned(),
                epoch: 2,
                tenant: tenant("tenant-a"),
                commits: vec![StampedCommit::at_origin_clock(hostile)],
            },
        )
        .expect("bounded link accepts the push frame");
    network.pump();
    let fenced = member.apply_deliveries(&mut network, &scope(&member, "tenant-a"));
    assert!(
        matches!(&fenced, Err(MemberError::FencedBatch { range, batch_epoch, highest_epoch })
            if range == "main" && *batch_epoch == 2 && *highest_epoch == 3),
        "the fencing state kept the highest epoch: {fenced:?}"
    );

    // A raised epoch on the non-owned range applies.
    member.apply_view(view("m-x", 4)).expect("a raised non-owned epoch applies");

    member.close();
    std::fs::remove_dir_all(&dir).ok();
}

/// Suppresses unused-import warnings for helpers the individual tests share.
#[allow(dead_code)]
fn _all_fixtures_exercised(_receipt: Option<CommitReceipt>) {}
