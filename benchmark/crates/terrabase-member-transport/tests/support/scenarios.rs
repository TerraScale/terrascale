//! The 23 W4-REPLICATION contract scenarios re-run over engine-owned
//! transports (SE-06).
//!
//! Each scenario is one fixed script driven entirely through the crate's
//! public seam over a caller-chosen [`MemberTransport`]; the two test files
//! run them unchanged over the in-process lane and the engine-socket (QUIC)
//! lane and assert the same outcomes — the W4 in-process plane's semantics,
//! re-proven on real transports.
//!
//! Outcomes are asserted, not timings: the socket lane's tick counts are
//! facts of one run (its pumps drain real socket traffic to a causal
//! completion condition — every flushed frame is read off the wire before
//! the pump returns), so only script-fixed pump counts are compared.

#![allow(
    clippy::similar_names,
    reason = "the dir_/m_ prefixes name each member's durable location and its member identity by the same roster suffix"
)]
use terrabase_member_transport::{
    AcknowledgementLevel, ConflictOutcome, Convergence, DurableResolution, FaultClass, FrameKind,
    FrameSelector, InProcessLane, LaneKind, LaneReceipt, MemberError, MemberStatus,
    MemberTransport, Payload, QuicLane, ReplicationOutcome, observe_region_loss,
    run_composite_lane,
};

use super::{
    TENANT, bare_commit, clean, delete_request, divergent_owner_views, documents_of, exchange_once,
    frame_of, healed_view, hostile_envelope, journal_of, member_id, member_put, open_member,
    operation, put_request, region, synchronize, tenant, wait_ack,
};

// The scenario tenant scope constructor.
use super::tenant as scope;

/// Runs scenario 3 over any lane: the acknowledgement report names the
/// Regions and Members that contributed.
pub fn acknowledgement_report_names_the_regions_and_members_that_contributed(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_region_quorum_topology("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_a2, mut m_a2) = open_member(tag, "m-a2", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);

    // MajorityRegions over two Regions needs both Regions through a Region
    // quorum: r-alpha needs both of its Members, r-beta its one.
    m_a1.commit_write(&scope(TENANT), put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");
    let before = transport.now();
    let mut peers = [&mut m_a2, &mut m_b1];
    let outcome = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-1",
        AcknowledgementLevel::MajorityRegions,
        4,
    );

    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("the cross-region acknowledgement is observed: {outcome:?}")
    };
    assert_eq!(report.requested, AcknowledgementLevel::MajorityRegions);
    assert_eq!(report.observed, AcknowledgementLevel::MajorityRegions);
    assert_eq!(report.normalization, None, "two Regions distinguish this level");
    let contributed = &report.contributed;
    assert_eq!(contributed.len(), 2, "both Regions contributed");
    assert_eq!(contributed[0].0.as_str(), "r-alpha");
    assert_eq!(
        contributed[0]
            .1
            .iter()
            .map(terrabase_member_transport::MemberId::as_str)
            .collect::<Vec<_>>(),
        ["m-a1", "m-a2"]
    );
    assert_eq!(contributed[1].0.as_str(), "r-beta");
    assert_eq!(
        contributed[1]
            .1
            .iter()
            .map(terrabase_member_transport::MemberId::as_str)
            .collect::<Vec<_>>(),
        ["m-b1"]
    );
    assert!(
        report.observed_at.ticks() > before.ticks(),
        "the observation time is a lane instant after the wait began"
    );
    assert_eq!(report.observed_at, transport.now(), "no pump moves between report and read");

    m_a1.close();
    m_a2.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_a2);
    clean(&dir_b1);
}

/// Runs scenario 4 over any lane: the acknowledgement timeout is distinct
/// from a local commit failure.
pub fn acknowledgement_timeout_is_distinct_from_local_commit_failure(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);
    let (a1, b1) = (member_id("m-a1"), member_id("m-b1"));

    // The follower's link holds every push back far longer than the
    // deadline, so nothing it carries is released before the deadline hits.
    transport.link(&a1, &b1).delay_pumps(8);
    let receipt = m_a1
        .commit_write(&scope(TENANT), put_request("main", "op-1", "doc-1", "v1"))
        .expect("the local commit completes on its own");
    assert!(receipt.sequence > 0, "the local journal publication is durable");

    let mut peers = [&mut m_b1];
    let outcome = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-1",
        AcknowledgementLevel::AllRegions,
        2,
    );

    let ReplicationOutcome::Timeout { operation_id, requested, observed } = outcome else {
        panic!("the unmet deadline is a timeout observation, not a commit failure: {outcome:?}")
    };
    assert_eq!(operation_id.as_str(), "op-1");
    assert_eq!(requested, AcknowledgementLevel::AllRegions);
    // The partial observation names exactly who had durably recorded when
    // the deadline hit: only the writer, at its own Region quorum.
    assert_eq!(
        observed.contributed.len(),
        1,
        "the held-back Member contributed nothing: {:?}",
        observed.contributed
    );
    assert_eq!(observed.contributed[0].0.as_str(), "r-alpha");
    assert_eq!(
        observed.contributed[0]
            .1
            .iter()
            .map(terrabase_member_transport::MemberId::as_str)
            .collect::<Vec<_>>(),
        ["m-a1"]
    );
    assert_eq!(
        observed.observed,
        AcknowledgementLevel::RegionQuorum,
        "the strongest level the partial set actually achieved is reported"
    );

    // The timeout is distinct from a local commit failure: the local
    // publication stands and the document still reads back at its writer.
    assert!(matches!(
        m_a1.journal().resolve(&operation("op-1")),
        Ok(DurableResolution::Committed(_))
    ));
    assert_eq!(documents_of(&m_a1, TENANT).len(), 1);
    assert!(transport.link_stats(&a1, &b1).held >= 1, "every pushed frame is still held back");
    assert!(documents_of(&m_b1, TENANT).is_empty());

    m_a1.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_b1);
}

/// Runs scenario 5 over any lane: a single Member's write reads back and
/// survives a restart.
pub fn single_region_single_member_write_reads_back_and_survives_restart(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let _ = transport;
    let topology = super::single_member_topology("r-alpha", "m-alpha", "main");
    let (dir, mut member) = open_member(tag, "m-alpha", &topology);
    let sequence = member_put(&mut member, TENANT, "main", "op-1", "doc-1", "v1");
    let before = documents_of(&member, TENANT);
    assert_eq!(before.len(), 1, "the write reads back");
    assert_eq!(before[0].value, "v1");

    let cursor_before = member.cursor().expect("cursor derives from the journal");
    assert_eq!(cursor_before.high_water(), Some(sequence));
    assert_eq!(
        cursor_before.tenant_progress(&tenant(TENANT)),
        Some(sequence),
        "the cursor is bound to the member's own journal sequence"
    );

    member.restart().expect("the member reopens its journal");
    let after = documents_of(&member, TENANT);
    assert_eq!(after, before, "the committed write survives the restart");
    assert_eq!(
        member.cursor().expect("cursor derives from the journal"),
        cursor_before,
        "the journal-derived cursor survives the restart"
    );
    assert_ne!(member.health(), MemberStatus::Unavailable);
    member.close();
    clean(&dir);
}

/// Runs scenario 6 over any lane: one Region's Members reach Region quorum
/// under a Member delay, and the degenerate lattice normalizes recorded.
pub fn one_region_multiple_members_reach_region_quorum_under_member_delay(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::one_region_three_members("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_a2, mut m_a2) = open_member(tag, "m-a2", &topology);
    let (dir_a3, mut m_a3) = open_member(tag, "m-a3", &topology);
    let (a1, a3) = (member_id("m-a1"), member_id("m-a3"));

    // One Member of the Region is delayed well past the quorum window.
    transport.link(&a1, &a3).delay_pumps(6);
    m_a1.commit_write(&scope(TENANT), put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");
    let mut peers = [&mut m_a2, &mut m_a3];
    let outcome = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-1",
        AcknowledgementLevel::RegionQuorum,
        3,
    );

    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("the Region quorum is reachable without the delayed Member: {outcome:?}")
    };
    assert_eq!(report.observed, AcknowledgementLevel::RegionQuorum);
    assert_eq!(report.requested, AcknowledgementLevel::RegionQuorum);
    assert_eq!(report.contributed.len(), 1);
    assert_eq!(report.contributed[0].0.as_str(), "r-alpha");
    assert_eq!(
        report.contributed[0]
            .1
            .iter()
            .map(terrabase_member_transport::MemberId::as_str)
            .collect::<Vec<_>>(),
        ["m-a1", "m-a2"],
        "the report names exactly the Members that contributed, delayed m-a3 excluded"
    );
    assert!(documents_of(&m_a3, TENANT).is_empty(), "the delayed Member holds nothing yet");

    // D-4 canonical reading of the degenerate one-Region lattice: requesting
    // AllRegions normalizes to the weakest distinct name and the report
    // records the normalization so reports stay comparable.
    m_a1.commit_write(&scope(TENANT), put_request("main", "op-2", "doc-2", "v2"))
        .expect("local write commits");
    let mut peers = [&mut m_a2, &mut m_a3];
    let degenerate = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-2",
        AcknowledgementLevel::AllRegions,
        3,
    );
    let ReplicationOutcome::Acknowledged(normalized) = degenerate else {
        panic!("the degenerate level is satisfiable: {degenerate:?}")
    };
    assert_eq!(normalized.requested, AcknowledgementLevel::AllRegions);
    assert_eq!(normalized.observed, AcknowledgementLevel::RegionQuorum);
    assert_eq!(
        normalized.normalization,
        Some((AcknowledgementLevel::AllRegions, AcknowledgementLevel::RegionQuorum)),
        "the one-Region normalization is recorded in the report"
    );

    m_a1.close();
    m_a2.close();
    m_a3.close();
    clean(&dir_a1);
    clean(&dir_a2);
    clean(&dir_a3);
}

/// Runs scenario 7 over any lane: healthy links reach the cross-Region
/// acknowledgement and the read visibility it names.
pub fn two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);

    // Per-level visibility: before the observing Member's journal publishes
    // the operation, a read at that Region observes nothing.
    m_a1.commit_write(&scope(TENANT), put_request("main", "op-1", "doc-1", "value-one"))
        .expect("local write commits");
    assert!(
        documents_of(&m_b1, TENANT).is_empty(),
        "the write is invisible at r-beta before its Member published it"
    );

    let mut peers = [&mut m_b1];
    let outcome = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-1",
        AcknowledgementLevel::MajorityRegions,
        3,
    );

    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("healthy links reach the cross-Region acknowledgement: {outcome:?}")
    };
    assert_eq!(report.observed, AcknowledgementLevel::MajorityRegions);
    assert_eq!(
        report.contributed.iter().map(|(region, _)| region.as_str()).collect::<Vec<_>>(),
        ["r-alpha", "r-beta"],
        "the report names the Region the read guarantee holds for"
    );

    // After the acknowledgement named r-beta, that Region's Member published
    // the operation and a read there observes it.
    let at_beta = documents_of(&m_b1, TENANT);
    assert_eq!(at_beta.len(), 1);
    assert_eq!(at_beta[0].value, "value-one");
    assert_eq!(at_beta[0].id.as_str(), "doc-1");

    m_a1.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_b1);
}

/// Runs scenario 8 over any lane: Member loss before the acknowledgement
/// returns a typed error, never a silent success; recovery restores the
/// contribution.
pub fn member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);

    m_a1.commit_write(&scope(TENANT), put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");

    // The r-beta Member is lost before the acknowledgement: its journal is
    // closed, so AllRegions cannot be met and the wait says so with the
    // typed MemberUnavailable category — never a silent success.
    m_b1.close();
    let mut peers = [&mut m_b1];
    let outcome = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-1",
        AcknowledgementLevel::AllRegions,
        3,
    );
    assert_eq!(
        outcome,
        ReplicationOutcome::Failed(terrabase_member_transport::ReplicationError::MemberUnavailable),
        "member loss before acknowledgement is a typed failure: {outcome:?}"
    );

    // Recovery: the member reopens from its durable location and the same
    // operation now reaches the requested level.
    m_b1.restart().expect("the lost member reopens");
    let mut peers = [&mut m_b1];
    let recovered = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-1",
        AcknowledgementLevel::AllRegions,
        3,
    );
    assert!(
        matches!(recovered, ReplicationOutcome::Acknowledged(_)),
        "the rejoined member contributes again: {recovered:?}"
    );

    m_a1.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_b1);
}

/// Runs scenario 9 over any lane: Region loss after an acknowledged write
/// states durable visibility and the recovery limit.
pub fn region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::three_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);
    let (dir_g1, mut m_g1) = open_member(tag, "m-g1", &topology);
    let (a1, g1) = (member_id("m-a1"), member_id("m-g1"));

    // The acknowledgement is reached by a Region majority: r-gamma's link is
    // partitioned so two of three Regions durably record the write.
    transport.link(&a1, &g1).partition();
    m_a1.commit_write(&scope(TENANT), put_request("main", "op-1", "doc-1", "v1"))
        .expect("local write commits");
    let mut peers = [&mut m_b1, &mut m_g1];
    let outcome = wait_ack(
        transport,
        &mut m_a1,
        &mut peers,
        TENANT,
        "op-1",
        AcknowledgementLevel::MajorityRegions,
        3,
    );
    let ReplicationOutcome::Acknowledged(report) = outcome else {
        panic!("a Region majority acknowledges without the partitioned Region: {outcome:?}")
    };
    assert_eq!(report.observed, AcknowledgementLevel::MajorityRegions);

    // The entire Region is then lost: its member's journal closes, which is
    // what makes it not durably hold anything observable.
    m_g1.close();
    let observation = observe_region_loss(
        &operation("op-1"),
        &report,
        &[&m_a1, &m_b1, &m_g1],
        &[region("r-gamma")],
        transport.now(),
    );

    // Durable visibility: exactly the Regions and Members that durably hold
    // the acknowledged write are named.
    let held = &observation.durably_held_by;
    assert_eq!(
        held.iter().map(|(region, _)| region.as_str()).collect::<Vec<_>>(),
        ["r-alpha", "r-beta"],
        "the observation states durable visibility: {held:?}"
    );
    assert_eq!(
        held[0].1.iter().map(terrabase_member_transport::MemberId::as_str).collect::<Vec<_>>(),
        ["m-a1"]
    );
    assert_eq!(
        held[1].1.iter().map(terrabase_member_transport::MemberId::as_str).collect::<Vec<_>>(),
        ["m-b1"]
    );
    assert_eq!(observation.operation.as_str(), "op-1");
    assert_eq!(observation.acknowledged, AcknowledgementLevel::MajorityRegions);
    // The recovery limit: what is NOT yet known is stated, not guessed.
    assert_eq!(
        observation
            .unknown_in
            .iter()
            .map(terrabase_member_transport::RegionId::as_str)
            .collect::<Vec<_>>(),
        ["r-gamma"]
    );

    m_a1.close();
    m_b1.close();
    m_g1.close();
    clean(&dir_a1);
    clean(&dir_b1);
    clean(&dir_g1);
}

/// Runs scenario 10 over any lane: delayed and reordered replication
/// replays idempotently to the contract outcome.
pub fn delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_member_topology("m-src", "m-dst", "main");
    let (dir_src, mut source) = open_member(tag, "m-src", &topology);
    let (dir_dst, mut follower) = open_member(tag, "m-dst", &topology);
    let (source_id, follower_id) = (member_id("m-src"), member_id("m-dst"));

    member_put(&mut source, TENANT, "main", "op-1", "doc-a", "one");
    member_put(&mut source, TENANT, "main", "op-2", "doc-b", "two");
    source
        .commit_write(&scope(TENANT), delete_request("main", "op-3", "doc-a"))
        .expect("delete commits");

    // Delay models an ordered hold-back released only by explicit pumps, and
    // reorder releases the held frames newest-first: the follower observes
    // the publications out of publication order.
    let response_link = transport.link(&source_id, &follower_id);
    response_link.delay();
    response_link.reorder();

    // Two pipelined requests overlap, so two batch frames are in flight when
    // the pump releases them in reverse order.
    follower.request_catch_up(transport, &source_id, &scope(TENANT)).expect("first request sent");
    follower.request_catch_up(transport, &source_id, &scope(TENANT)).expect("second request sent");
    transport.pump();
    source.dispatch_under(transport, &scope(TENANT)).expect("requests served");
    assert_eq!(transport.link_stats(&source_id, &follower_id).held, 2, "the batches are held back");
    transport.pump();
    assert!(
        transport.link_stats(&source_id, &follower_id).reordered > 0,
        "the release order was actually reversed"
    );
    let report = follower.dispatch_under(transport, &scope(TENANT)).expect("deliveries apply");

    assert_eq!(report.applied, 3, "every operation applies exactly once");
    assert_eq!(report.duplicates_resolved, 3, "the overlapping delivery resolves idempotently");
    assert_eq!(
        documents_of(&follower, TENANT),
        documents_of(&source, TENANT),
        "the follower reaches the contract outcome"
    );
    assert_eq!(journal_of(&follower, TENANT), 3);
    let follower_view = documents_of(&follower, TENANT);
    assert!(
        follower_view.iter().all(|document| document.id.as_str() != "doc-a"),
        "doc-a stays deleted after the out-of-order replay"
    );
    assert_eq!(follower_view.len(), 1, "doc-b survives");

    source.close();
    follower.close();
    clean(&dir_src);
    clean(&dir_dst);
}

/// Runs scenario 11 over any lane: duplicated delivery applies each
/// operation exactly once.
pub fn duplicate_delivery_applies_each_operation_once(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_member_topology("m-src", "m-dst", "main");
    let (dir_src, mut source) = open_member(tag, "m-src", &topology);
    let (dir_dst, mut follower) = open_member(tag, "m-dst", &topology);
    let (source_id, follower_id) = (member_id("m-src"), member_id("m-dst"));

    member_put(&mut source, TENANT, "main", "op-1", "doc-1", "v1");
    member_put(&mut source, TENANT, "main", "op-2", "doc-2", "v2");

    // The link duplicates every delivered frame: one batch becomes two
    // identical deliveries of the same operations.
    transport.link(&source_id, &follower_id).duplicate();
    let report = exchange_once(transport, &mut source, &mut follower, TENANT);

    assert!(
        transport.link_stats(&source_id, &follower_id).delivered >= 2,
        "the fault actually duplicated the delivery"
    );
    assert_eq!(report.applied, 2, "each operation applies once");
    assert_eq!(report.duplicates_resolved, 2, "the duplicate resolves through the ledger");
    assert_eq!(journal_of(&follower, TENANT), 2);
    assert_eq!(documents_of(&follower, TENANT), documents_of(&source, TENANT));

    // A full re-request of the same range applies nothing: the advisory
    // cursor keeps the source from re-serving history the member already
    // holds. (In-delivery duplication was proven above; this is the
    // request-level idempotence of the protocol.)
    transport.heal();
    let replay = exchange_once(transport, &mut source, &mut follower, TENANT);
    assert_eq!(replay.applied, 0);
    assert_eq!(replay.duplicates_resolved, 0);
    assert!(replay.caught_up);
    assert_eq!(journal_of(&follower, TENANT), 2);
    assert_eq!(documents_of(&follower, TENANT), documents_of(&source, TENANT));

    source.close();
    follower.close();
    clean(&dir_src);
    clean(&dir_dst);
}

/// Runs scenario 12 over any lane: a member rejoins after identity and
/// cursor validation and catches up without duplicates.
pub fn member_rejoin_checks_identity_and_catches_up_without_duplicates(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_member_topology("m-src", "m-dst", "main");
    let (dir_src, mut source) = open_member(tag, "m-src", &topology);
    let (dir_dst, mut follower) = open_member(tag, "m-dst", &topology);

    member_put(&mut source, TENANT, "main", "op-1", "doc-1", "v1");
    member_put(&mut source, TENANT, "main", "op-2", "doc-2", "v2");
    let first = exchange_once(transport, &mut source, &mut follower, TENANT);
    assert!(first.caught_up);

    let cursor_before = follower.cursor().expect("cursor derives from the journal");
    let rejoined = follower.rejoin(&cursor_before);
    assert!(
        rejoined.is_ok(),
        "a member whose durable history is intact rejoins after validation: {rejoined:?}"
    );
    assert_eq!(follower.health(), MemberStatus::Active, "the rejoining member is admitted");
    assert_eq!(
        follower.cursor().expect("cursor derives from the journal"),
        cursor_before,
        "rejoin validation is anchored in the journal-derived cursor"
    );

    // The rejoined member catches up on the operations it missed, without
    // duplicating the operations it already holds.
    member_put(&mut source, TENANT, "main", "op-3", "doc-3", "v3");
    let report = synchronize(transport, &mut source, &mut follower, TENANT, 4);
    assert!(report.caught_up);
    assert_eq!(report.applied, 1, "only the missed operation applies");
    assert_eq!(journal_of(&follower, TENANT), 3, "no operation is duplicated");
    assert_eq!(documents_of(&follower, TENANT), documents_of(&source, TENANT));

    // A member whose durable history went backwards is reported, not
    // admitted: its journal-derived cursor regressed below the cursor it
    // rejoined with.
    let cursor_now = follower.cursor().expect("cursor derives from the journal");
    follower.close();
    clean(&dir_dst);
    let rejected = follower.rejoin(&cursor_now);
    assert!(
        matches!(rejected, Err(MemberError::RejoinRejected(_))),
        "a regressed durable cursor is reported, not admitted: {rejected:?}"
    );
    assert_ne!(follower.health(), MemberStatus::Active);
    assert!(follower.last_rejection().is_some(), "the rejection is observable");

    source.close();
    clean(&dir_src);
}

/// Runs scenario 13 over any lane: a restarted member resumes from its
/// durable replication cursor without operator intervention.
pub fn restarted_member_resumes_from_its_durable_replication_cursor(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_member_topology("m-src", "m-dst", "main");
    let (dir_src, mut source) = open_member(tag, "m-src", &topology);
    let (dir_dst, mut follower) = open_member(tag, "m-dst", &topology);

    for (name, text) in [("op-1", "v1"), ("op-2", "v2"), ("op-3", "v3")] {
        member_put(&mut source, TENANT, "main", name, "doc-1", text);
    }
    let caught_up = exchange_once(transport, &mut source, &mut follower, TENANT);
    assert!(caught_up.caught_up);
    assert_eq!(journal_of(&follower, TENANT), 3);

    // The source advances while the follower is down.
    member_put(&mut source, TENANT, "main", "op-4", "doc-1", "v4");
    member_put(&mut source, TENANT, "main", "op-5", "doc-2", "v5");

    let cursor_before = follower.cursor().expect("cursor derives from the journal");
    assert_eq!(
        cursor_before.tenant_progress(&tenant(TENANT)),
        Some(3),
        "the durable cursor names the follower's own journal progress"
    );
    follower.restart().expect("the member reopens its journal");
    assert_eq!(
        follower.cursor().expect("cursor derives from the journal"),
        cursor_before,
        "the durable cursor is journal ground truth and survives the restart"
    );
    assert_eq!(
        follower.advisory_progress(&tenant(TENANT)),
        None,
        "advisory in-memory progress does not survive the restart"
    );

    // Resumption re-receives the tenant history and resolves the already
    // applied operations through the durable operation ledger.
    let report = synchronize(transport, &mut source, &mut follower, TENANT, 4);
    assert_eq!(report.applied, 2, "only the missed operations apply");
    assert_eq!(report.duplicates_resolved, 3, "already-applied operations resolve idempotently");
    assert_eq!(journal_of(&follower, TENANT), 5);
    assert_eq!(documents_of(&follower, TENANT), documents_of(&source, TENANT));

    source.close();
    follower.close();
    clean(&dir_src);
    clean(&dir_dst);
}

/// Runs scenario 14 over any lane: anti-entropy repairs a lagging member
/// after a gap.
pub fn anti_entropy_repairs_a_lagging_member_after_a_gap(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_member_topology("m-src", "m-dst", "main");
    let (dir_src, mut source) = open_member(tag, "m-src", &topology);
    let (dir_dst, mut follower) = open_member(tag, "m-dst", &topology);
    let (source_id, follower_id) = (member_id("m-src"), member_id("m-dst"));

    for index in 1..=5 {
        member_put(&mut source, TENANT, "main", &format!("op-{index}"), "doc-1", "advancing");
    }
    let caught_up = exchange_once(transport, &mut source, &mut follower, TENANT);
    assert!(caught_up.caught_up);

    // The source advances while the follower is idle, and the next delivery
    // in transit is dropped: the follower is left with a gap.
    for index in 6..=8 {
        member_put(
            &mut source,
            TENANT,
            "main",
            &format!("op-{index}"),
            &format!("doc-{index}"),
            "advancing",
        );
    }
    transport.link(&source_id, &follower_id).drop_next(1);

    let report = synchronize(transport, &mut source, &mut follower, TENANT, 4);
    assert!(
        transport.link_stats(&source_id, &follower_id).dropped >= 1,
        "the fault actually dropped a delivery"
    );
    assert!(report.caught_up, "the gap is repaired");
    assert_eq!(journal_of(&follower, TENANT), 8, "every operation applies exactly once");
    assert_eq!(documents_of(&follower, TENANT), documents_of(&source, TENANT));
    assert_ne!(follower.health(), MemberStatus::Stale, "the repaired member is not lagging");

    source.close();
    follower.close();
    clean(&dir_src);
    clean(&dir_dst);
}

/// Runs scenario 15 over any lane: the convergence report names members,
/// operation identity, and observation time.
pub fn convergence_report_names_members_operation_identity_and_observation_time(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);

    member_put(&mut m_a1, TENANT, "main", "op-1", "doc-1", "v1");
    let report = synchronize(transport, &mut m_a1, &mut m_b1, TENANT, 4);
    assert!(report.caught_up);

    // Convergence is its own observation, produced at an injected lane
    // instant — never inferred from an acknowledgement or a health status.
    let at = transport.now().plus_ticks(2);
    let convergence = observe_convergence_at(&[&m_a1, &m_b1], "op-1", at);
    assert_eq!(convergence.operation.as_str(), "op-1");
    let holdings = &convergence.holdings;
    assert_eq!(
        holdings.iter().map(|(member, _)| member.as_str()).collect::<Vec<_>>(),
        ["m-a1", "m-b1"],
        "the observation names every Member that holds the operation"
    );
    assert!(
        holdings.iter().all(|(_, state)| *state == AcknowledgementLevel::Local),
        "each holding is the Member's own local publication fact: {holdings:?}"
    );
    assert_eq!(convergence.observed_at, at, "the observation time is the injected instant");

    // A member that does not hold the operation is simply not named as
    // holding it, and a different injected instant is a different
    // observation time — deterministic, driven by the caller.
    let later = observe_convergence_at(&[&m_a1, &m_b1], "op-2", at.plus_ticks(5));
    assert!(later.holdings.is_empty(), "an unreplicated operation is held by no one");
    assert_eq!(later.observed_at, at.plus_ticks(5));

    m_a1.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_b1);
}

/// Runs scenario 17 over any lane once and returns the converged values plus
/// the recorded conflict outcomes.
pub fn isolated_concurrent_writes_run(
    transport: &mut dyn MemberTransport,
    tag: &str,
) -> (String, String, Vec<ConflictOutcome>) {
    let (view_a, view_b) = divergent_owner_views("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &view_a);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &view_b);

    m_a1.commit_write(&scope(TENANT), put_request("main", "op-a", "doc-shared", "value-a"))
        .expect("the isolated writer commits locally");
    // The lane clock advances and the second writer observes the instant, so
    // its write is deterministically the later one.
    transport.pump();
    m_b1.observe_instant(transport.now());
    m_b1.commit_write(&scope(TENANT), put_request("main", "op-b", "doc-shared", "value-b"))
        .expect("the isolated writer commits locally");

    // Healing: both sides apply the same converged view.
    let healed = healed_view("main");
    m_a1.apply_view(healed.clone()).expect("the deposed side accepts the healed view");
    m_b1.apply_view(healed).expect("the promoted side accepts the healed view");

    // Catch-up runs in both directions so both writers see each other's
    // journal and the conflict policy decides on both sides.
    let into_b = synchronize(transport, &mut m_a1, &mut m_b1, TENANT, 4);
    assert!(into_b.caught_up, "repair reaches the promoted owner");
    let into_a = synchronize(transport, &mut m_b1, &mut m_a1, TENANT, 4);
    assert!(into_a.caught_up, "repair reaches the deposed side");

    let value_at = |member: &terrabase_member_transport::LaneMember| {
        documents_of(member, TENANT)
            .into_iter()
            .find(|document| document.id.as_str() == "doc-shared")
            .map_or_else(|| "absent".to_owned(), |document| document.value)
    };
    let outcomes = m_a1
        .conflicts()
        .iter()
        .chain(m_b1.conflicts().iter())
        .map(|record| record.outcome.clone())
        .collect::<Vec<_>>();
    let value_at_a = value_at(&m_a1);
    let value_at_b = value_at(&m_b1);

    m_a1.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_b1);
    (value_at_a, value_at_b, outcomes)
}

/// Runs scenario 18 over any lane: conflicted writes produce the recorded
/// policy outcome and a content-free conflict record.
pub fn conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let (view_a, view_b) = divergent_owner_views("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &view_a);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &view_b);

    m_a1.commit_write(&scope(TENANT), put_request("main", "op-a", "doc-shared", "value-a"))
        .expect("writer one commits");
    transport.pump();
    m_b1.observe_instant(transport.now());
    m_b1.commit_write(&scope(TENANT), put_request("main", "op-b", "doc-shared", "value-b"))
        .expect("writer two commits");

    let healed = healed_view("main");
    m_a1.apply_view(healed.clone()).expect("healed view applies");
    m_b1.apply_view(healed).expect("healed view applies");
    let into_b = synchronize(transport, &mut m_a1, &mut m_b1, TENANT, 4);
    assert!(into_b.conflicts_recorded >= 1, "the apply pass reports the conflict it recorded");

    let record = m_b1
        .conflicts()
        .last()
        .expect("the conflict is retained as observable history on the detecting side");
    // The recorded policy outcome: last-writer-wins on the bounded hybrid
    // logical clock, naming the winner.
    assert_eq!(
        record.outcome,
        ConflictOutcome::Winner(member_id("m-b1")),
        "the policy outcome is the recorded decision"
    );
    let mut operations = record.operations.clone();
    operations.sort();
    assert_eq!(
        operations.iter().map(terrabase_member_transport::OperationId::as_str).collect::<Vec<_>>(),
        ["op-a", "op-b"],
        "the record names both conflicting operation identities"
    );
    let mut members = record.members.clone();
    members.sort();
    assert_eq!(
        members.iter().map(terrabase_member_transport::MemberId::as_str).collect::<Vec<_>>(),
        ["m-a1", "m-b1"],
        "the record names both writer identities"
    );
    assert_eq!(record.clocks.len(), 2, "the record carries both clocks");

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
    clean(&dir_a1);
    clean(&dir_b1);
}

/// Runs scenario 19 over any lane: leader loss stops new commits until a new
/// leader is confirmed.
pub fn leader_loss_stops_new_commits_until_a_new_leader_is_confirmed(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);

    member_put(&mut m_a1, TENANT, "main", "op-1", "doc-1", "v1");
    let report = synchronize(transport, &mut m_a1, &mut m_b1, TENANT, 4);
    assert!(report.caught_up, "the follower holds the range state");

    // The leader is lost: its journal closes. No surviving Member owns the
    // range's write path under the current view, so every new commit fails
    // admission with the typed error — there is no silent failover.
    m_a1.close();
    let stopped = m_b1.commit_write(&scope(TENANT), put_request("main", "op-2", "doc-2", "v2"));
    assert!(
        matches!(stopped, Err(MemberError::NotOwner { ref range }) if range == "main"),
        "new commits stop while the leader is lost: {stopped:?}"
    );
    assert!(
        documents_of(&m_b1, TENANT).iter().all(|d| d.id.as_str() != "doc-2"),
        "the rejected write left no state"
    );

    // A new leader is confirmed by applying the transitioned view, and only
    // then do commits to the range resume.
    m_b1.apply_view(healed_view("main")).expect("the promoted leader is confirmed");
    let resumed = m_b1.commit_write(&scope(TENANT), put_request("main", "op-2", "doc-2", "v2"));
    assert!(
        matches!(&resumed, Ok(receipt) if receipt.sequence > 0),
        "commits resume once the new leader is confirmed: {resumed:?}"
    );
    assert!(
        documents_of(&m_b1, TENANT).iter().any(|d| d.id.as_str() == "doc-2"),
        "the resumed write reads back"
    );

    m_a1.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_b1);
}

/// Runs scenario 20 over any lane: the old owner is fenced after a leader
/// transition.
pub fn old_owner_is_fenced_after_leader_transition(transport: &mut dyn MemberTransport, tag: &str) {
    let topology = super::two_regions_one_member_each("main");
    let (dir_a1, mut m_a1) = open_member(tag, "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member(tag, "m-b1", &topology);
    let (a1, b1) = (member_id("m-a1"), member_id("m-b1"));

    // The deposed leader writes before learning of the transition, and the
    // new leader writes its own concurrent value after being promoted.
    m_a1.commit_write(&scope(TENANT), put_request("main", "op-a", "doc-shared", "value-a"))
        .expect("the old owner's write keeps its local receipt");
    let transitioned = healed_view("main");
    m_a1.apply_view(transitioned.clone()).expect("the old owner learns of the transition");
    m_b1.apply_view(transitioned).expect("the new owner is confirmed");
    transport.pump();
    m_b1.observe_instant(transport.now());
    m_b1.commit_write(&scope(TENANT), put_request("main", "op-b", "doc-shared", "value-b"))
        .expect("the new owner commits");

    // Admission fencing: the deposed owner's new writes fail locally, with
    // no cross-Member round trip.
    let deposed_write =
        m_a1.commit_write(&scope(TENANT), put_request("main", "op-late", "doc-late", "late"));
    assert!(
        matches!(deposed_write, Err(MemberError::NotOwner { ref range }) if range == "main"),
        "a deposed owner's new write fails admission: {deposed_write:?}"
    );

    // Apply-time fencing: a batch the deposed owner raced past its
    // deposition is rejected by the new owner's epoch check, and its commits
    // are retained as observable conflict history, never silently dropped.
    let in_flight = m_a1
        .journal()
        .tenant_commits(&tenant(TENANT), 0, 8)
        .expect("journal read")
        .into_iter()
        .find(|commit| commit.operation.as_str() == "op-a")
        .expect("the old owner's committed write is journaled");
    let push = hostile_envelope(
        FrameKind::Push,
        "m-a1",
        "m-b1",
        Some("main".to_owned()),
        Some(1),
        Payload::Push {
            tenant: tenant(TENANT),
            range: "main".to_owned(),
            epoch: 1,
            commits: vec![in_flight],
        },
    );
    transport.send(&a1, &b1, frame_of(&push)).expect("the bounded link accepts the push frame");
    transport.pump();
    let fenced = m_b1.dispatch_under(transport, &scope(TENANT));
    assert!(
        matches!(&fenced, Err(MemberError::FencedBatch { range, batch_epoch, highest_epoch })
            if range == "main" && *batch_epoch == 1 && *highest_epoch == 2),
        "a below-epoch batch is rejected with a typed error: {fenced:?}"
    );
    assert_eq!(m_b1.fenced().len(), 1, "the fencing is observable");
    assert_eq!(m_b1.fenced()[0].range, "main");
    assert_eq!(m_b1.fenced()[0].source, member_id("m-a1"));
    assert!(
        m_b1.conflicts()
            .iter()
            .any(|record| matches!(record.outcome, ConflictOutcome::DeposedOwnerRetained(_))),
        "the deposed owner's write is retained as conflict history, not discarded"
    );
    let rendered = format!("{:?}", m_b1.conflicts());
    assert!(!rendered.contains("value-a"), "the retained record stays content-free");

    // The deposed owner's local receipt is still real locally (a local
    // durable fact), and its write converges under D-3 once repair runs.
    assert!(matches!(
        m_a1.journal().resolve(&operation("op-a")),
        Ok(DurableResolution::Committed(_))
    ));
    let into_b = synchronize(transport, &mut m_a1, &mut m_b1, TENANT, 4);
    assert!(into_b.caught_up, "repair reaches the new owner");
    let into_a = synchronize(transport, &mut m_b1, &mut m_a1, TENANT, 4);
    assert!(into_a.caught_up, "repair reaches the deposed owner");
    let shared_at_a = documents_of(&m_a1, TENANT)
        .into_iter()
        .find(|document| document.id.as_str() == "doc-shared")
        .expect("the converged document exists at the deposed owner");
    let shared_at_b = documents_of(&m_b1, TENANT)
        .into_iter()
        .find(|document| document.id.as_str() == "doc-shared")
        .expect("the converged document exists at the new owner");
    assert_eq!(shared_at_a.id, shared_at_b.id);
    assert_eq!(shared_at_a.value, shared_at_b.value);
    assert_eq!(shared_at_a.value, "value-b");

    m_a1.close();
    m_b1.close();
    clean(&dir_a1);
    clean(&dir_b1);
}

/// Runs scenario 21 over any lane: the tenant-isolation probe denies
/// cross-tenant replication and is observable.
pub fn tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable(
    transport: &mut dyn MemberTransport,
    tag: &str,
) {
    let topology = super::two_member_topology("m-src", "m-dst", "main");
    let (dir_src, mut source) = open_member(tag, "m-src", &topology);
    let (dir_dst, mut follower) = open_member(tag, "m-dst", &topology);
    let (source_id, follower_id) = (member_id("m-src"), member_id("m-dst"));

    source
        .commit_write(&tenant("tenant-a"), put_request("main", "op-1", "doc-a", "tenant-a-value"))
        .expect("source writes tenant-a");

    // A hostile requester asks for tenant-a's history while the serving scope
    // names tenant-b: the serve seam denies the attempt with a typed error.
    follower
        .send_raw_catch_up_request(transport, &source_id, &tenant("tenant-a"), None, 8)
        .expect("the bounded link accepts the request frame");
    transport.pump();
    let denied = source.dispatch_under(transport, &tenant("tenant-b"));
    assert!(
        matches!(&denied, Err(MemberError::CrossTenant { scope, attempted })
            if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
        "cross-tenant serve is denied with a typed error: {denied:?}"
    );
    assert!(
        transport.receive(&follower_id, FrameSelector::Deliveries).is_empty(),
        "no batch crossed the seam for the denied tenant"
    );
    assert_eq!(source.denials().len(), 1, "the denial is observable on the source");
    assert_eq!(source.denials()[0].scope_tenant.as_str(), "tenant-b");
    assert_eq!(source.denials()[0].attempted_tenant.as_str(), "tenant-a");
    assert_eq!(source.denials()[0].counterparty.as_str(), "m-dst");
    assert_eq!(
        source.health(),
        MemberStatus::Degraded,
        "the denial degrades the serving member's health observation"
    );

    // A hostile delivery names tenant-a while the applying scope names
    // tenant-b: the apply seam denies it and nothing of tenant-a crosses.
    let hostile = hostile_envelope(
        FrameKind::CatchUpBatch,
        "m-src",
        "m-dst",
        None,
        None,
        Payload::CatchUpBatch {
            tenant: tenant("tenant-a"),
            commits: vec![bare_commit("op-1", 1, "doc-a", "hostile")],
            through: Some(1),
            tenant_high_water: Some(1),
            servable_from: Some(1),
            gap_below: None,
        },
    );
    transport
        .send(&source_id, &follower_id, frame_of(&hostile))
        .expect("the bounded link accepts the batch frame");
    transport.pump();
    let denied_apply = follower.dispatch_under(transport, &tenant("tenant-b"));
    assert!(
        matches!(&denied_apply, Err(MemberError::CrossTenant { scope, attempted })
            if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
        "cross-tenant apply is denied with a typed error: {denied_apply:?}"
    );
    assert_eq!(follower.denials().len(), 1, "the denial is observable on the follower");
    assert!(
        follower.journal().committed_tenants().expect("tenant enumeration").is_empty(),
        "no cross-tenant data reached the follower's journal"
    );
    assert!(documents_of(&follower, "tenant-b").is_empty());

    // Positive control: the same exchange under matching tenant scopes
    // works, so the denials above are tenant isolation, not a broken path.
    let report = exchange_once(transport, &mut source, &mut follower, "tenant-a");
    assert_eq!(report.applied, 1);
    assert_eq!(documents_of(&follower, "tenant-a"), documents_of(&source, "tenant-a"));

    source.close();
    follower.close();
    clean(&dir_src);
    clean(&dir_dst);
}

/// Observes convergence at an injected instant over the public seam.
pub fn observe_convergence_at(
    members: &[&terrabase_member_transport::LaneMember],
    operation_name: &str,
    at: terrabase_member_transport::LaneInstant,
) -> Convergence {
    terrabase_member_transport::observe_convergence(&operation(operation_name), members, at)
}

/// Runs scenario 22 through the lane harness over the caller's transport:
/// the replication receipt is secret-safe and names scenario, faults, and
/// limits.
pub fn receipt_secret_safety_via_harness<T: MemberTransport>(harness_transport: T, lane: LaneKind) {
    use terrabase_member_transport::LaneHarness;
    let mut h = LaneHarness::new("secret-safety", harness_transport);
    // Canary-bearing documents: if any document content crossed the receipt,
    // these strings would appear in it.
    h.write("m-a1", "tenant-a", "op-1", "doc-canary", "CANARY-document-value-alpha");
    let ack = match super::wait_ack_harness(
        &mut h,
        "m-a1",
        "tenant-a",
        "op-1",
        AcknowledgementLevel::RegionQuorum,
        4,
    ) {
        ReplicationOutcome::Acknowledged(report) => report,
        other => panic!("the healthy pair reaches its Region quorum: {other:?}"),
    };
    h.delay_and_reorder("m-a1", "m-b1");
    let report = h.replicate("m-a1", "m-b1", "tenant-a", 6);
    assert!(report.caught_up);
    h.heal();
    let convergence = h.observe("op-1", &["m-a1", "m-a2", "m-b1", "m-b2"], "tenant-a");

    let receipt = LaneReceipt::new("secret-safety-probe", lane, &h.topology())
        .with_faults(&[FaultClass::LinkDelay, FaultClass::LinkReorder, FaultClass::Healing])
        .with_acknowledgement_report(&operation("op-1"), &ack)
        .with_convergence(convergence, "every named Member durably holds the operation")
        .with_rpo(0, None)
        .with_rto_ticks(1)
        .with_convergence_ticks(1)
        .with_lag(vec![(h.member_id("m-b1"), 0)])
        .with_limit("lane-scoped facts only: no production topology, timing, or performance claim")
        .with_limit("the receipt carries identities, counts, and verdicts only");
    h.clean_up();

    // The receipt names the scenario, its faults, and its limits.
    assert_eq!(receipt.scenario, "secret-safety-probe");
    let fault_names: Vec<&str> = receipt.faults.iter().map(|fault| fault.name()).collect();
    assert_eq!(fault_names, ["link-delay", "link-reorder", "healing"]);
    assert!(receipt.limits.len() >= 2);

    for rendered in [format!("{receipt:?}"), receipt.summary()] {
        for canary in ["CANARY-document-value-alpha", "doc-canary"] {
            assert!(
                !rendered.contains(canary),
                "document content crossed the receipt ({canary}): {rendered}"
            );
        }
        for forbidden in
            ["/", "tmp", "terrabase-se06", "wal", "credential", "connec", "password", "token"]
        {
            assert!(
                !rendered.contains(forbidden),
                "a forbidden fact crossed the receipt ({forbidden}): {rendered}"
            );
        }
    }
    let summary = receipt.summary();
    for identity in ["r-alpha", "r-beta", "r-gamma", "m-a1", "m-b1", "op-1", "RegionQuorum"] {
        assert!(summary.contains(identity), "the summary names {identity}");
    }
    assert!(
        summary.contains(terrabase_member_transport::RPO_DEFINITION),
        "the summary carries the RPO definition"
    );
}

/// Asserts the composite three-Region lane receipt (scenario 23) against the
/// contract's measurement and fault-class expectations.
pub fn assert_composite_receipt(receipt: &LaneReceipt) {
    // Topology: three Regions, two Members each, one failure domain per
    // Region.
    assert_eq!(receipt.regions.len(), 3);
    assert_eq!(receipt.members.len(), 6);
    assert_eq!(receipt.failure_domains.len(), 3);
    assert!(receipt.failure_domains.iter().all(|(_, members)| members.len() == 2));

    // Faults: every fault class the contract's lane lists is named — the
    // full 11-class W4 vocabulary over real links.
    let fault_names: Vec<&str> = receipt.faults.iter().map(|fault| fault.name()).collect();
    for expected in [
        "link-delay",
        "link-reorder",
        "link-drop",
        "link-duplicate",
        "region-partition",
        "member-restart",
        "member-rejoin",
        "leader-loss",
        "region-loss-after-acknowledged-write",
        "healing",
        "duplicate-replay",
    ] {
        assert!(fault_names.contains(&expected), "the receipt names the fault class {expected}");
    }

    // Acknowledgement facts per operation, with their contributing Members.
    let acks = &receipt.acknowledgements;
    assert!(acks.iter().any(|(operation, level, members)| operation == "op-1"
        && *level == AcknowledgementLevel::AllRegions
        && members.len() == 6));
    assert!(acks.iter().any(|(operation, level, members)| operation == "op-3"
        && *level == AcknowledgementLevel::RegionQuorum
        && members.len() == 2));

    // Convergence: its own observation with its condition, naming the
    // surviving Members.
    let convergence = receipt.convergence.as_ref().expect("the lane reports its convergence");
    assert_eq!(convergence.holdings.len(), 4);
    assert!(!receipt.convergence_condition.is_empty());

    // Measurements with their definitions, measured in this scenario.
    let (rpo_count, rpo_window) = receipt.rpo.expect("RPO is measured in this scenario");
    assert_eq!(rpo_count, 1);
    let (window_low, window_high) = rpo_window.expect("a nonzero loss carries a sequence window");
    assert_eq!(window_low, window_high, "one lost operation spans one sequence");
    let rto = receipt.rto_ticks.expect("RTO is measured in this scenario");
    assert_eq!(rto, 2, "two lane ticks of detection and view application");
    let convergence_interval =
        receipt.convergence_ticks.expect("the convergence interval is measured");
    assert!(convergence_interval > 0);
    let lag = &receipt.lag_distances;
    assert!(lag.len() >= 5, "the receipt carries the observed lag distances");
    assert!(lag.iter().all(|(_, distance)| *distance > 0), "measured before repair: {lag:?}");

    // Limits are explicit.
    assert!(!receipt.limits.is_empty());
    assert!(
        receipt
            .limits
            .iter()
            .any(|limit| limit.contains("no production topology, timing, or performance claim"))
    );

    // Secret safety of the rendered artifact.
    let summary = receipt.summary();
    for forbidden in ["/", "tmp", "terrabase-se06", "credential", "connec", "password"] {
        assert!(!summary.contains(forbidden), "a forbidden fact crossed the receipt ({forbidden})");
    }
    eprintln!("{summary}");
}

/// Runs scenario 23 over the in-process lane and returns its receipt.
pub fn composite_receipt_in_process(ack_ticks: u64) -> LaneReceipt {
    run_composite_lane("composite", LaneKind::InProcess, InProcessLane::new(), ack_ticks)
}

/// Runs scenario 23 over the engine-socket lane and returns its receipt.
pub fn composite_receipt_engine_socket(ack_ticks: u64) -> LaneReceipt {
    let members = ["m-a1", "m-a2", "m-b1", "m-b2", "m-g1", "m-g2"];
    let lane = QuicLane::new(&members).expect("the socket lane binds");
    let receipt = run_composite_lane("composite-quic", LaneKind::EngineSocket, lane, ack_ticks);
    assert_composite_receipt(&receipt);
    receipt
}
