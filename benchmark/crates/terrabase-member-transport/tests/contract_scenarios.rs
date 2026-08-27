//! SE-06 contract scenarios over the in-process engine-owned lane.
//!
//! The 23 W4-REPLICATION red-test names, re-run over real encoded member
//! envelopes crossing bounded in-process links (Members share a process
//! boundary). The scripts and outcome assertions are the W4 in-process
//! plane's; the lane beneath them is the SE-06 transport. Everything stays
//! deterministic: one tick per pump, no wall time, no threads, no sleeps.
//!
//! Non-claims: every measured value here is a lane-scoped fact; no
//! production topology, timing, or performance capability is implied.

mod support;

use support::scenarios::{
    assert_composite_receipt, composite_receipt_in_process, isolated_concurrent_writes_run,
    receipt_secret_safety_via_harness,
};
use support::{LEVELS, STATUSES, in_process, open_member, put_request};

use terrabase_member_transport::{
    AcknowledgementLevel, Convergence, DEFAULT_CATCH_UP_BATCH, InProcessLane, LaneConfig, LaneKind,
    MemberError,
};

const TAG: &str = "inproc";

// --- the two type-level scenarios (no transport beneath them) --------------

#[test]
fn local_commit_receipt_contains_no_region_or_member_facts() {
    let topology = support::single_member_topology("r-alpha", "m-alpha", "main");
    let (dir, mut member) = open_member(TAG, "m-alpha", &topology);
    let receipt = member
        .commit_write(&support::tenant("tenant-a"), put_request("main", "op-1", "doc-1", "value"))
        .expect("local write commits");

    // The receipt stays the member-local publication fact: its fields carry
    // only the operation identity and the journal sequence.
    assert_eq!(receipt.operation.as_str(), "op-1");
    assert!(receipt.sequence > 0);

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
    assert!(
        std::any::type_name::<terrabase_member_transport::CommitReceipt>()
            .starts_with("terrabase_member_transport::"),
        "the receipt type stays owned by the member-transport seam"
    );
    member.close();
    support::clean(&dir);
}

#[test]
fn replication_request_names_an_explicit_acknowledgement_level() {
    // The default is `Local` (ADR-0004 D-2: V1 parity — local save,
    // replication asynchronous).
    assert_eq!(AcknowledgementLevel::default(), AcknowledgementLevel::Local);

    let topology = support::two_regions_one_member_each("main");
    let config = LaneConfig::try_new(topology).expect("valid topology");
    assert_eq!(config.catch_up_batch, DEFAULT_CATCH_UP_BATCH);

    // Every stronger level exists as an explicit, distinct variant, and the
    // only way a wait names one is by passing it: the acknowledgement is a
    // per-request input, never inferred from a local durability fact.
    for (index, level) in LEVELS.iter().enumerate() {
        for other in LEVELS.iter().skip(index + 1) {
            assert_ne!(level, other, "each acknowledgement level is a distinct request");
        }
    }
    let (canonical, normalization) =
        terrabase_member_transport::normalize_level(AcknowledgementLevel::Local, 3);
    assert_eq!(canonical, AcknowledgementLevel::Local);
    assert_eq!(normalization, None, "Local is never normalized away");

    // An empty, never-validated membership view is rejected with the typed
    // category: the configuration does not authorize around it.
    assert_eq!(
        LaneConfig::try_new(terrabase_member_transport::TopologyView::new()),
        Err(MemberError::InvalidTopology)
    );
}

#[test]
fn replication_status_reports_health_separately_from_convergence() {
    for (index, status) in STATUSES.iter().enumerate() {
        for other in STATUSES.iter().skip(index + 1) {
            assert_ne!(status, other, "each health observation is a distinct state");
        }
        // A status value is a pure observation: it carries no payload, so no
        // convergence, acknowledgement, member set, or receipt fact can ride
        // on it.
        let rendered = format!("{status:?}");
        assert!(
            !rendered.to_ascii_lowercase().contains("convergen"),
            "a health observation names no convergence fact: {rendered}"
        );
        assert!(
            !rendered.to_ascii_lowercase().contains("acknowledg"),
            "a health observation names no acknowledgement fact: {rendered}"
        );
    }
    assert_eq!(
        std::mem::size_of::<terrabase_member_transport::MemberStatus>(),
        1,
        "the health vocabulary stays a payload-free observation"
    );

    // Convergence is its own observation type, never inferred from an
    // acknowledgement or a health status.
    assert_ne!(
        std::any::type_name::<terrabase_member_transport::MemberStatus>(),
        std::any::type_name::<Convergence>(),
        "health and convergence are separate types"
    );
    assert!(std::mem::size_of::<Convergence>() > 0, "convergence carries its observation content");
}

// --- the transport-driven scenarios over the in-process lane ---------------

#[test]
fn acknowledgement_report_names_the_regions_and_members_that_contributed() {
    in_process(|lane| {
        support::scenarios::acknowledgement_report_names_the_regions_and_members_that_contributed(
            lane, TAG,
        );
    });
}

#[test]
fn acknowledgement_timeout_is_distinct_from_local_commit_failure() {
    in_process(|lane| {
        support::scenarios::acknowledgement_timeout_is_distinct_from_local_commit_failure(
            lane, TAG,
        );
    });
}

#[test]
fn single_region_single_member_write_reads_back_and_survives_restart() {
    in_process(|lane| {
        support::scenarios::single_region_single_member_write_reads_back_and_survives_restart(
            lane, TAG,
        );
    });
}

#[test]
fn one_region_multiple_members_reach_region_quorum_under_member_delay() {
    in_process(|lane| {
        support::scenarios::one_region_multiple_members_reach_region_quorum_under_member_delay(
            lane, TAG,
        );
    });
}

#[test]
fn two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility() {
    in_process(|lane| {
        support::scenarios::two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility(
            lane, TAG,
        );
    });
}

#[test]
fn member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success() {
    in_process(|lane| {
        support::scenarios::member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success(
            lane, TAG,
        );
    });
}

#[test]
fn region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit() {
    in_process(|lane| {
        support::scenarios::region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit(
            lane, TAG,
        );
    });
}

#[test]
fn delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome() {
    in_process(|lane| {
        support::scenarios::delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome(
            lane, TAG,
        );
    });
}

#[test]
fn duplicate_delivery_applies_each_operation_once() {
    in_process(|lane| {
        support::scenarios::duplicate_delivery_applies_each_operation_once(lane, TAG);
    });
}

#[test]
fn member_rejoin_checks_identity_and_catches_up_without_duplicates() {
    in_process(|lane| {
        support::scenarios::member_rejoin_checks_identity_and_catches_up_without_duplicates(
            lane, TAG,
        );
    });
}

#[test]
fn restarted_member_resumes_from_its_durable_replication_cursor() {
    in_process(|lane| {
        support::scenarios::restarted_member_resumes_from_its_durable_replication_cursor(lane, TAG);
    });
}

#[test]
fn anti_entropy_repairs_a_lagging_member_after_a_gap() {
    in_process(|lane| {
        support::scenarios::anti_entropy_repairs_a_lagging_member_after_a_gap(lane, TAG);
    });
}

#[test]
fn convergence_report_names_members_operation_identity_and_observation_time() {
    in_process(|lane| {
        support::scenarios::convergence_report_names_members_operation_identity_and_observation_time(
            lane, TAG,
        );
    });
}

#[test]
fn isolated_concurrent_writes_converge_deterministically_after_healing() {
    // One run establishes the outcome; the second run proves it is
    // deterministic — the same injected instants and the same script must
    // produce the same converged values and the same recorded outcomes.
    let first = in_process(|lane| isolated_concurrent_writes_run(lane, "isolated-a"));
    let second = in_process(|lane| isolated_concurrent_writes_run(lane, "isolated-b"));
    assert_eq!(first, second, "the convergence outcome is deterministic");

    let (value_at_a, value_at_b, outcomes) = first;
    assert_eq!(value_at_a, "value-b", "the deposed side converges to the later write");
    assert_eq!(value_at_b, "value-b", "the promoted side keeps the later write");
    assert_eq!(value_at_a, value_at_b, "both sides hold the same value after healing");
    assert!(
        outcomes.iter().all(|outcome| matches!(outcome,
            terrabase_member_transport::ConflictOutcome::Winner(member) if member.as_str() == "m-b1")),
        "the recorded policy outcome names the later writer everywhere: {outcomes:?}"
    );
    assert!(!outcomes.is_empty(), "the losing write is retained as observable history");
}

#[test]
fn conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record() {
    in_process(|lane| {
        support::scenarios::conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record(
            lane, TAG,
        );
    });
}

#[test]
fn leader_loss_stops_new_commits_until_a_new_leader_is_confirmed() {
    in_process(|lane| {
        support::scenarios::leader_loss_stops_new_commits_until_a_new_leader_is_confirmed(
            lane, TAG,
        );
    });
}

#[test]
fn old_owner_is_fenced_after_leader_transition() {
    in_process(|lane| {
        support::scenarios::old_owner_is_fenced_after_leader_transition(lane, TAG);
    });
}

#[test]
fn tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable() {
    in_process(|lane| {
        support::scenarios::tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable(
            lane, TAG,
        );
    });
}

#[test]
fn replication_receipt_is_secret_safe_and_names_scenario_faults_and_limits() {
    receipt_secret_safety_via_harness(InProcessLane::new(), LaneKind::InProcess);
}

#[test]
fn three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag() {
    let receipt = composite_receipt_in_process(4);
    assert_composite_receipt(&receipt);
}

// --- the in-process lane's own determinism extra ---------------------------

#[test]
fn lane_composite_scenario_is_deterministic() {
    let first = composite_receipt_in_process(4);
    let second = composite_receipt_in_process(4);
    assert_eq!(first, second, "the in-process composite lane is deterministic");
}
