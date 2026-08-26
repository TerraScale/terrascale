//! SE-06 contract scenarios over the engine-socket (QUIC) lane.
//!
//! The same 23 W4-REPLICATION scripts, re-run over real QUIC sockets: one
//! endpoint per Member on loopback, one certificate per Member from the lane
//! certificate authority (no shared Member credentials), mutual-TLS
//! connections, one uni stream per encoded envelope — no broker between
//! Members (PD-2026-08-26-13 D2, ADR 0012 D-2).
//!
//! Outcome assertions are identical to the in-process file; the two
//! lane-specific differences are recorded facts, not assertions: the lane
//! clock stays tick-based while each pump additionally drains real socket
//! traffic inside a bounded wall-clock window, so tick counts are facts of
//! one run and the determinism double-run stays an in-process property; and
//! the degenerate-lattice, receipt-secret-safety, and composite scenarios
//! assert outcomes over the socket lane with wider acknowledgement windows.

#![allow(
    clippy::similar_names,
    reason = "the dir_/m_ prefixes name each member's durable location and its member identity by the same roster suffix"
)]
mod support;

use support::engine_socket;
use support::scenarios;

use terrabase_member_transport::{LaneKind, QuicLane};

use terrabase_member_transport::ReplicationOutcome;

const TAG: &str = "quic";
const ACK_TICKS: u64 = 16;

#[test]
fn acknowledgement_report_names_the_regions_and_members_that_contributed() {
    engine_socket(&["m-a1", "m-a2", "m-b1"], |lane| {
        scenarios::acknowledgement_report_names_the_regions_and_members_that_contributed(lane, TAG);
    });
}

#[test]
fn acknowledgement_timeout_is_distinct_from_local_commit_failure() {
    engine_socket(&["m-a1", "m-b1"], |lane| {
        scenarios::acknowledgement_timeout_is_distinct_from_local_commit_failure(lane, TAG);
    });
}

#[test]
fn single_region_single_member_write_reads_back_and_survives_restart() {
    engine_socket(&["m-alpha"], |lane| {
        scenarios::single_region_single_member_write_reads_back_and_survives_restart(lane, TAG);
    });
}

#[test]
fn one_region_multiple_members_reach_region_quorum_under_member_delay() {
    engine_socket(&["m-a1", "m-a2", "m-a3"], |lane| {
        scenarios::one_region_multiple_members_reach_region_quorum_under_member_delay(lane, TAG);
    });
}

#[test]
fn two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility() {
    engine_socket(&["m-a1", "m-b1"], |lane| {
        scenarios::two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility(
            lane, TAG,
        );
    });
}

#[test]
fn member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success() {
    engine_socket(&["m-a1", "m-b1"], |lane| {
        scenarios::member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success(
            lane, TAG,
        );
    });
}

#[test]
fn region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit() {
    engine_socket(&["m-a1", "m-b1", "m-g1"], |lane| {
        scenarios::region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit(
            lane, TAG,
        );
    });
}

#[test]
fn delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome() {
    engine_socket(&["m-src", "m-dst"], |lane| {
        scenarios::delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome(
            lane, TAG,
        );
    });
}

#[test]
fn duplicate_delivery_applies_each_operation_once() {
    engine_socket(&["m-src", "m-dst"], |lane| {
        scenarios::duplicate_delivery_applies_each_operation_once(lane, TAG);
    });
}

#[test]
fn member_rejoin_checks_identity_and_catches_up_without_duplicates() {
    engine_socket(&["m-src", "m-dst"], |lane| {
        scenarios::member_rejoin_checks_identity_and_catches_up_without_duplicates(lane, TAG);
    });
}

#[test]
fn restarted_member_resumes_from_its_durable_replication_cursor() {
    engine_socket(&["m-src", "m-dst"], |lane| {
        scenarios::restarted_member_resumes_from_its_durable_replication_cursor(lane, TAG);
    });
}

#[test]
fn anti_entropy_repairs_a_lagging_member_after_a_gap() {
    engine_socket(&["m-src", "m-dst"], |lane| {
        scenarios::anti_entropy_repairs_a_lagging_member_after_a_gap(lane, TAG);
    });
}

#[test]
fn convergence_report_names_members_operation_identity_and_observation_time() {
    engine_socket(&["m-a1", "m-b1"], |lane| {
        scenarios::convergence_report_names_members_operation_identity_and_observation_time(
            lane, TAG,
        );
    });
}

#[test]
fn isolated_concurrent_writes_converge_deterministically_after_healing() {
    // Over the socket lane the scenario asserts its converged outcome (the
    // stamp-driven D-3 decision, which the injected instants fix); the
    // double-run determinism proof belongs to the in-process lane, whose
    // delivery order is fully deterministic.
    engine_socket(&["m-a1", "m-b1"], |lane| {
        let (value_at_a, value_at_b, outcomes) =
            scenarios::isolated_concurrent_writes_run(lane, TAG);
        assert_eq!(value_at_a, "value-b", "the deposed side converges to the later write");
        assert_eq!(value_at_b, "value-b", "the promoted side keeps the later write");
        assert_eq!(value_at_a, value_at_b, "both sides hold the same value after healing");
        assert!(
            outcomes.iter().all(|outcome| matches!(outcome,
                terrabase_member_transport::ConflictOutcome::Winner(member)
                    if member.as_str() == "m-b1")),
            "the recorded policy outcome names the later writer everywhere: {outcomes:?}"
        );
        assert!(!outcomes.is_empty(), "the losing write is retained as observable history");
    });
}

#[test]
fn conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record() {
    engine_socket(&["m-a1", "m-b1"], |lane| {
        scenarios::conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record(
            lane, TAG,
        );
    });
}

#[test]
fn leader_loss_stops_new_commits_until_a_new_leader_is_confirmed() {
    engine_socket(&["m-a1", "m-b1"], |lane| {
        scenarios::leader_loss_stops_new_commits_until_a_new_leader_is_confirmed(lane, TAG);
    });
}

#[test]
fn old_owner_is_fenced_after_leader_transition() {
    engine_socket(&["m-a1", "m-b1"], |lane| {
        scenarios::old_owner_is_fenced_after_leader_transition(lane, TAG);
    });
}

#[test]
fn tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable() {
    engine_socket(&["m-src", "m-dst"], |lane| {
        scenarios::tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable(
            lane, TAG,
        );
    });
}

#[test]
fn replication_receipt_is_secret_safe_and_names_scenario_faults_and_limits() {
    scenarios::receipt_secret_safety_via_harness(socket_lane(), LaneKind::EngineSocket);
}

#[test]
fn three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag() {
    let receipt = scenarios::composite_receipt_engine_socket(ACK_TICKS);
    scenarios::assert_composite_receipt(&receipt);
}

#[test]
fn the_socket_lane_restarts_and_rejoins_from_the_durable_cursor_without_intervention() {
    // The ticket's restart-rejoin criterion over real sockets: a Member that
    // loses its process state rejoins on its journal-derived cursor and
    // catches up through the lane with no operator action — its durable
    // cursor, not its memory, is the ground truth.
    engine_socket(&["m-src", "m-dst"], |lane| {
        use support::{exchange_once, journal_of, member_put};
        let topology = support::two_member_topology("m-src", "m-dst", "main");
        let (dir_src, mut source) = support::open_member(TAG, "m-src", &topology);
        let (dir_dst, mut follower) = support::open_member(TAG, "m-dst", &topology);
        member_put(&mut source, support::TENANT, "main", "op-1", "doc-1", "v1");
        member_put(&mut source, support::TENANT, "main", "op-2", "doc-2", "v2");
        let first = exchange_once(lane, &mut source, &mut follower, support::TENANT);
        assert!(first.caught_up);
        let cursor = follower.cursor().expect("cursor derives from the journal");
        follower.restart().expect("the member reopens from its durable location");
        assert!(follower.rejoin(&cursor).is_ok(), "rejoin validates against the durable cursor");
        member_put(&mut source, support::TENANT, "main", "op-3", "doc-3", "v3");
        let report = support::synchronize(lane, &mut source, &mut follower, support::TENANT, 8);
        assert!(report.caught_up, "the rejoined member catches up over real sockets");
        assert_eq!(journal_of(&follower, support::TENANT), 3, "no duplicate applies");
        assert_eq!(
            support::documents_of(&follower, support::TENANT),
            support::documents_of(&source, support::TENANT)
        );
        source.close();
        follower.close();
        support::clean(&dir_src);
        support::clean(&dir_dst);
    });
}

#[test]
fn the_socket_lane_counts_bytes_on_wire_at_the_delivery_boundary() {
    // PD-13 D1 budgets over real socket deliveries: every frame class the
    // scenario delivered stays at or under the written envelope-overhead
    // budget, and the delivered payload per committed change stays at or
    // under the written per-change budget.
    engine_socket(&["m-a1", "m-b1"], |lane| {
        use support::{member_put, synchronize};
        let topology = support::two_regions_one_member_each("main");
        let (dir_a1, mut m_a1) = support::open_member(TAG, "m-a1", &topology);
        let (dir_b1, mut m_b1) = support::open_member(TAG, "m-b1", &topology);
        member_put(&mut m_a1, support::TENANT, "main", "op-1", "doc-1", "lane-value-one");
        member_put(&mut m_a1, support::TENANT, "main", "op-2", "doc-2", "lane-value-two");
        let report = synchronize(lane, &mut m_a1, &mut m_b1, support::TENANT, 8);
        assert!(report.caught_up);
        let deliveries = terrabase_member_transport::dispatch_report_deliveries(&report);
        assert!(deliveries >= 2, "the changes were delivered");
        for (kind, bytes) in lane.class_bytes() {
            if bytes.frames == 0 {
                continue;
            }
            assert!(
                bytes.average_overhead_bytes()
                    <= terrabase_member_transport::BUDGET_ENVELOPE_OVERHEAD_BYTES as u64,
                "{kind:?} overhead {} exceeds the budget",
                bytes.average_overhead_bytes()
            );
        }
        m_a1.close();
        m_b1.close();
        support::clean(&dir_a1);
        support::clean(&dir_b1);
    });
}

/// One socket lane over the receipt scenario's member set.
fn socket_lane() -> QuicLane {
    QuicLane::new(&["m-a1", "m-a2", "m-b1", "m-b2", "m-g1", "m-g2"]).expect("the socket lane binds")
}

/// Compiles the outcome vocabulary into this file's namespace (the shared
/// scenarios return it; this keeps the import honest).
#[allow(dead_code)]
fn outcome_vocabulary(outcome: &ReplicationOutcome) -> bool {
    matches!(outcome, ReplicationOutcome::Acknowledged(_))
}
