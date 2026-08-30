//! Shared scenario support for the SE-06 contract tests.
//!
//! Everything here drives the crate's public seam only: [`LaneMember`] over
//! any [`MemberTransport`], encoded [`Envelope`]s for the hostile-frame
//! probes, and the public acknowledgement/convergence vocabulary. The same
//! scenario functions run unchanged over the in-process lane and the
//! engine-socket (QUIC) lane, which is what "the 23 contract scenarios
//! re-run over engine-owned transports" means: identical scripts, identical
//! outcome assertions, two lanes.
//!
//! Lane-scoped non-claims: the QUIC lane's pumps drain real socket traffic
//! to a causal completion condition (every flushed frame is read off the
//! wire before the pump returns), so a pump is a complete delivery step and
//! the scripts below drive fixed pump counts; the tick counts a run measures
//! are facts of that run, never reproducible constants, so the assertions
//! check outcomes, not tick values, except where the script itself fixes the
//! pumps.

#![allow(dead_code, reason = "shared helpers; each test file uses a subset")]

use std::collections::BTreeSet;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase_member_transport::{
    AcknowledgementLevel, ClockStamp, CollectionName, CommitReceipt, Convergence, DispatchReport,
    DurableResolution, FrameKind, LaneCommit, LaneDocument, LaneMember, LaneWriteRequest, MemberId,
    MemberStatus, MemberTransport, OperationId, RegionId, ReplicationError, ReplicationOutcome,
    TenantId, TopologyView, encode, level_satisfied, normalize_level, observe_convergence,
    strongest_satisfied_level,
};

pub use terrabase_member_transport::{Envelope, InProcessLane, LaneConfig};

pub mod scenarios;

/// The shared lane tenant of the scenario scripts.
pub const TENANT: &str = "tenant-a";

/// One unique temporary directory per open.
pub fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-se06-scenario-{tag}-{}-{index}", std::process::id()))
}

/// Opens one lane member over a durable temporary location.
pub fn open_member(tag: &str, name: &str, topology: &TopologyView) -> (PathBuf, LaneMember) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let config = LaneConfig::try_new(topology.clone()).expect("valid lane config");
    let member = LaneMember::try_new(config, MemberId::try_new(name).expect("valid member"), &dir)
        .expect("lane member opens");
    (dir, member)
}

/// Removes one scenario's durable location.
pub fn clean(dir: &std::path::Path) {
    std::fs::remove_dir_all(dir).ok();
}

/// One validated identity by name.
pub fn member_id(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid member")
}

/// One validated tenant by name.
pub fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

/// One validated operation by name.
pub fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid operation")
}

/// One lane document.
pub fn text_document(id: &str, text: &str) -> LaneDocument {
    LaneDocument {
        id: terrabase_member_transport::DocumentId::try_new(id).expect("valid document"),
        collection: CollectionName::try_new("users").expect("valid collection"),
        value: text.to_owned(),
    }
}

/// One put request for the named range.
pub fn put_request(range: &str, operation_name: &str, doc: &str, text: &str) -> LaneWriteRequest {
    LaneWriteRequest::new(operation(operation_name), range)
        .expect("valid write request")
        .with_put(text_document(doc, text))
}

/// One delete request for the named range.
pub fn delete_request(range: &str, operation_name: &str, doc: &str) -> LaneWriteRequest {
    LaneWriteRequest::new(operation(operation_name), range)
        .expect("valid write request")
        .with_delete(terrabase_member_transport::DocumentId::try_new(doc).expect("valid document"))
}

/// Commits one document on a member and returns the journal sequence.
pub fn member_put(
    member: &mut LaneMember,
    tenant_name: &str,
    range: &str,
    operation_name: &str,
    doc: &str,
    text: &str,
) -> u64 {
    member
        .commit_write(&tenant(tenant_name), put_request(range, operation_name, doc, text))
        .expect("local write commits")
        .sequence
}

/// Reads one tenant's documents through the member's journal state.
pub fn documents_of(member: &LaneMember, tenant_name: &str) -> Vec<LaneDocument> {
    member.journal().documents_at(&tenant(tenant_name)).expect("journal read")
}

/// The number of journaled commits a member holds for a tenant.
pub fn journal_of(member: &LaneMember, tenant_name: &str) -> usize {
    member
        .journal()
        .tenant_commits(&tenant(tenant_name), 0, u32::MAX / 2)
        .expect("journal read")
        .len()
}

/// Drives one explicit catch-up exchange over the transport: request, pump,
/// the source's dispatch pass (which serves the request), pump, and the
/// follower's dispatch pass (which applies the batch).
pub fn exchange_once(
    transport: &mut dyn MemberTransport,
    source: &mut LaneMember,
    follower: &mut LaneMember,
    tenant_name: &str,
) -> DispatchReport {
    let source_id = source.id().clone();
    follower.request_catch_up(transport, &source_id, &tenant(tenant_name)).expect("request sends");
    transport.pump();
    source.dispatch_under(transport, &tenant(tenant_name)).expect("source serves");
    transport.pump();
    follower.dispatch_under(transport, &tenant(tenant_name)).expect("follower applies")
}

/// Drives bounded catch-up rounds until the follower reaches the source's
/// last-known high water, mirroring the W4 `synchronize` helper.
pub fn synchronize(
    transport: &mut dyn MemberTransport,
    source: &mut LaneMember,
    follower: &mut LaneMember,
    tenant_name: &str,
    rounds: usize,
) -> DispatchReport {
    let mut report = DispatchReport::default();
    for _ in 0..rounds {
        report = exchange_once(transport, source, follower, tenant_name);
        if report.caught_up {
            return report;
        }
    }
    report
}

/// The D-2 acknowledgement wait over the public seam: the local commit
/// already happened; each round pushes the operation to the peers, pumps the
/// lane, lets each peer apply and answer, and collects the reports. Mirrors
/// the lane harness's `wait_ack` for arbitrary member sets.
pub fn wait_ack(
    transport: &mut dyn MemberTransport,
    writer: &mut LaneMember,
    peers: &mut [&mut LaneMember],
    tenant_name: &str,
    operation_name: &str,
    level: AcknowledgementLevel,
    ticks: u64,
) -> ReplicationOutcome {
    let topology = writer.topology().clone();
    let writer_id = writer.id().clone();
    let region_count = topology.regions().count();
    let (canonical, normalization) = normalize_level(level, region_count);
    let scope = tenant(tenant_name);
    let operation = operation(operation_name);
    let deadline = transport.now().plus_ticks(ticks);
    loop {
        writer.observe_instant(transport.now());
        if !writer.is_open() {
            return ReplicationOutcome::Failed(ReplicationError::JournalClosed);
        }
        if !matches!(writer.journal().resolve(&operation), Ok(DurableResolution::Committed(_))) {
            return ReplicationOutcome::Failed(ReplicationError::NotReplicated);
        }
        // Dispatch the writer first so late reports are collected.
        writer.dispatch_under(transport, &scope).expect("writer drains");
        let contributing = writer.ack_contributors(&operation);
        if level_satisfied(canonical, &topology, &writer_id, &contributing) {
            let report = writer.build_ack_report(
                level,
                canonical,
                normalization,
                &contributing,
                transport.now(),
            );
            return ReplicationOutcome::Acknowledged(report);
        }
        // Reachability: if no set of still-open Members can ever meet the
        // level, the wait fails with the typed category instead of burning
        // the deadline.
        let mut possible = BTreeSet::from([writer_id.clone()]);
        for peer in peers.iter() {
            if peer.is_open() {
                possible.insert(peer.id().clone());
            }
        }
        if !level_satisfied(canonical, &topology, &writer_id, &possible) {
            let error = if level == AcknowledgementLevel::AllRegions {
                ReplicationError::MemberUnavailable
            } else {
                ReplicationError::QuorumUnreachable
            };
            return ReplicationOutcome::Failed(error);
        }
        if transport.now() >= deadline {
            let strongest = strongest_satisfied_level(&topology, &writer_id, &contributing);
            let (partial, _) = normalize_level(strongest, region_count);
            let observed =
                writer.build_ack_report(strongest, partial, None, &contributing, transport.now());
            return ReplicationOutcome::Timeout {
                operation_id: operation,
                requested: level,
                observed,
            };
        }
        for peer in peers.iter_mut() {
            if !peer.is_open() {
                continue;
            }
            writer.send_push(transport, peer.id(), &scope, &operation).expect("push sends");
        }
        transport.pump();
        for peer in peers.iter_mut() {
            if !peer.is_open() {
                continue;
            }
            peer.dispatch_under(transport, &scope).expect("peer dispatches");
        }
        transport.pump();
    }
}

/// Observes convergence across the named members at the current lane
/// instant.
pub fn observe(
    transport: &dyn MemberTransport,
    members: &[&LaneMember],
    operation_name: &str,
) -> Convergence {
    observe_convergence(&operation(operation_name), members, transport.now())
}

// --- topology builders (the W4 qualification shapes) -----------------------

/// One Region, one Member owning the range.
pub fn single_member_topology(region: &str, member: &str, range: &str) -> TopologyView {
    TopologyView::new()
        .with_region(region)
        .expect("valid region")
        .with_member(member, region, &format!("fd-{region}"))
        .expect("valid member")
        .with_owner(range, member)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// One Region, two Members, the first owning the range.
pub fn two_member_topology(first: &str, second: &str, range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member(first, "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member(second, "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_owner(range, first)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// Two Regions: r-alpha seats two Members, r-beta one — the Region-quorum
/// lattice shape.
pub fn two_region_quorum_topology(range: &str) -> TopologyView {
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
pub fn one_region_three_members(range: &str) -> TopologyView {
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
pub fn two_regions_one_member_each(range: &str) -> TopologyView {
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
pub fn three_regions_one_member_each(range: &str) -> TopologyView {
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

/// Divergent membership views for isolated concurrent writers: each side
/// names itself owner at the same epoch (the split-brain partition shape).
pub fn divergent_owner_views(range: &str) -> (TopologyView, TopologyView) {
    let base = |owner: &str| {
        TopologyView::new()
            .with_region("r-alpha")
            .expect("valid region")
            .with_region("r-beta")
            .expect("valid region")
            .with_member("m-a1", "r-alpha", "fd-alpha")
            .expect("valid member")
            .with_member("m-b1", "r-beta", "fd-beta")
            .expect("valid member")
            .with_owner(range, owner)
            .expect("valid owner")
            .validated()
            .expect("valid topology")
    };
    (base("m-a1"), base("m-b1"))
}

/// The healed membership view: `m-b1` owns the range at the next epoch.
pub fn healed_view(range: &str) -> TopologyView {
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

// --- hostile-frame construction (the member-boundary probes) ---------------

/// One bare commit for hostile deliveries: identity, stamp, sequence, and a
/// reconstructed write set.
pub fn bare_commit(operation_name: &str, sequence: u64, doc: &str, text: &str) -> LaneCommit {
    LaneCommit {
        operation: operation(operation_name),
        stamp: ClockStamp::new(1, 1),
        sequence,
        writes: vec![terrabase_member_transport::LaneWrite::Put {
            document: text_document(doc, text),
        }],
    }
}

/// Encodes one envelope under the discipline header (the hostile probes
/// inject frames the members themselves would never build).
pub fn frame_of(envelope: &Envelope) -> terrabase_member_transport::EncodedFrame {
    encode(envelope).expect("hostile frame encodes")
}

/// One hostile envelope skeleton: valid codec shape, caller-chosen payload
/// identities.
pub fn hostile_envelope(
    kind: FrameKind,
    source: &str,
    target: &str,
    range: Option<String>,
    epoch: Option<u64>,
    payload: terrabase_member_transport::Payload,
) -> Envelope {
    Envelope {
        kind,
        message_id: 99,
        correlation_id: None,
        causation_id: None,
        idempotency_key: None,
        source: member_id(source),
        target: member_id(target),
        issued_at: terrabase_member_transport::LaneInstant::at_ticks(0),
        expires_at: None,
        range,
        epoch,
        payload,
    }
}

// --- type vocabulary shared with the W4 disposition ------------------------

/// The distinct acknowledgement levels, in canonical order.
pub const LEVELS: [AcknowledgementLevel; 4] = [
    AcknowledgementLevel::Local,
    AcknowledgementLevel::RegionQuorum,
    AcknowledgementLevel::MajorityRegions,
    AcknowledgementLevel::AllRegions,
];

/// The health observation vocabulary, in canonical order.
pub const STATUSES: [MemberStatus; 7] = [
    MemberStatus::Assigned,
    MemberStatus::Active,
    MemberStatus::Responsive,
    MemberStatus::Stale,
    MemberStatus::Unavailable,
    MemberStatus::Degraded,
    MemberStatus::Unknown,
];

/// The region identities of the scenario topologies, in canonical order.
pub fn region(name: &str) -> RegionId {
    RegionId::try_new(name).expect("valid region")
}

/// Runs one scenario over a fresh in-process lane, returning the scenario's
/// value.
pub fn in_process<F, R>(scenario: F) -> R
where
    F: FnOnce(&mut dyn MemberTransport) -> R,
{
    let mut lane = InProcessLane::new();
    scenario(&mut lane)
}

/// Runs one scenario over a fresh engine-socket (QUIC) lane seating the
/// named members, closing the endpoints afterwards.
pub fn engine_socket<F>(members: &[&str], scenario: F)
where
    F: FnOnce(&mut dyn MemberTransport),
{
    let mut lane = terrabase_member_transport::QuicLane::new(members)
        .expect("the socket lane binds one endpoint per member");
    scenario(&mut lane);
    lane.close();
}

/// Runs the harness acknowledgement wait for the lane-shaped receipt
/// scenarios (a thin wrapper over the harness's own driver).
pub fn wait_ack_harness<T: MemberTransport>(
    harness: &mut terrabase_member_transport::LaneHarness<T>,
    writer: &str,
    tenant_name: &str,
    operation_name: &str,
    level: AcknowledgementLevel,
    ticks: u64,
) -> ReplicationOutcome {
    harness.wait_ack(writer, tenant_name, operation_name, level, ticks)
}

/// One commit receipt's sequence, for compact assertions.
pub fn sequence_of(receipt: &CommitReceipt) -> u64 {
    receipt.sequence
}
