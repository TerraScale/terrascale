//! In-crate W4-REPLICATION R-3 lane tests: the three-Region synthetic fault
//! lane and the `ReplicationReceipt` artifacts.
//!
//! These tests drive the lane harness (`super::lane`) — three Regions
//! `r-alpha`, `r-beta`, `r-gamma`; two Members per Region; each Region one
//! failure domain; every Member a real durable Engine in a temporary
//! directory; controlled in-process links — through the contract's fault
//! classes and charter matrix rows, and assert the receipt shape, the
//! measurement definitions, and secret safety. Everything stays
//! deterministic: pumps and injected lane instants only, no threads, no
//! async runtimes, no sleeps, no wall time.
//!
//! Non-claims: measured values are lane-scoped artifacts reported by the
//! tests, never claims beyond the lane; no production topology, timing, or
//! performance capability is implied by anything here.

use super::lane::{LaneHarness, lane_topology_with_owner};
use super::member::MemberError;
use super::transport::{FrameSelector, ReplicationFrame};
use super::{
    AcknowledgementLevel, CONVERGENCE_DEFINITION, ConflictOutcome, FaultClass, LAG_DEFINITION,
    MemberId, RPO_DEFINITION, RTO_DEFINITION, ReplicationOutcome, ReplicationReceipt,
};

use crate::types::{DocumentValue, OperationId, StorageSequence, TenantId};

const TENANT: &str = "tenant-lane";

/// Runs the composite three-Region lane scenario and returns its receipt.
///
/// The script is fixed and deterministic: healthy cross-Region
/// acknowledgement, a lag observation, link delay, reorder, drop, and
/// duplicate faults with repair, a member restart and rejoin, a full
/// two-Region partition with isolated concurrent writes under divergent
/// ownership views, an acknowledgement at Region quorum inside the
/// surviving Region, the loss of the entire r-alpha Region (leader loss),
/// recovery to a confirmed new leader, healing, and the convergence
/// observation naming every surviving Member.
#[allow(clippy::too_many_lines, reason = "one fixed lane script, one act per fault class")]
pub(crate) fn run_composite_lane() -> ReplicationReceipt {
    let mut h = LaneHarness::new("composite");
    let mut faults = Vec::new();

    // Act A: healthy links, cross-Region acknowledgement at AllRegions.
    h.write("m-a1", TENANT, "op-1", "doc-1", "lane-value-one");
    let acked_op1 = match h.wait_ack("m-a1", TENANT, "op-1", AcknowledgementLevel::AllRegions, 4) {
        ReplicationOutcome::Acknowledged(report) => {
            assert_eq!(report.contributed().len(), 3, "every Region contributed");
            (OperationId::try_new("op-1").expect("valid id"), report)
        }
        other => panic!("the healthy lane acknowledges at AllRegions: {other:?}"),
    };

    // Act B: the lag observation — one unreplicated write leaves every
    // non-writer exactly one commit behind the source.
    h.write("m-a1", TENANT, "op-2", "doc-2", "lane-value-two");
    let lag_distances: Vec<(MemberId, u64)> = ["m-a2", "m-b1", "m-b2", "m-g1", "m-g2"]
        .iter()
        .map(|name| (h.member_id(name), h.lag("m-a1", name)))
        .collect();

    // Act C: link faults — delay and reorder, drop, and duplicate — then
    // repair and heal.
    h.delay_and_reorder("m-a1", "m-b2");
    h.drop_next("m-a1", "m-b1", 1);
    h.duplicate("m-a1", "m-g1");
    for target in ["m-b1", "m-b2", "m-g1"] {
        let report = h.replicate("m-a1", target, TENANT, 6);
        assert!(report.caught_up(), "repair reaches {target} under the link faults");
    }
    for fault in [
        FaultClass::LinkDelay,
        FaultClass::LinkReorder,
        FaultClass::LinkDrop,
        FaultClass::LinkDuplicate,
        FaultClass::DuplicateReplay,
        FaultClass::Healing,
    ] {
        faults.push(fault);
    }
    h.heal();

    // Act D: member restart (engine closed and reopened from its directory)
    // and rejoin with cursor validation, then catch-up without duplicates.
    let journal_before = h.journal_len("m-b2", TENANT);
    let cursor_before = h.member("m-b2").cursor().expect("cursor derives from the journal");
    h.restart("m-b2");
    h.rejoin_with("m-b2", &cursor_before);
    let report = h.replicate("m-a1", "m-b2", TENANT, 4);
    assert!(report.caught_up());
    assert_eq!(
        h.journal_len("m-b2", TENANT),
        journal_before,
        "the rejoined member applies no duplicate"
    );
    faults.push(FaultClass::MemberRestart);
    faults.push(FaultClass::MemberRejoin);

    // Act E: full Region partition (r-beta and r-gamma isolated) with
    // isolated concurrent writes under divergent ownership views. Commits
    // stop for the non-owner until a new leader is confirmed by a
    // transitioned view.
    h.partition_region("r-beta");
    h.partition_region("r-gamma");
    let stopped = h.try_write("m-b1", TENANT, "op-stopped", "doc-stopped", "never-commits");
    assert!(
        matches!(stopped, Err(MemberError::NotOwner { ref range }) if range == "main"),
        "commits stop while the leader is unreachable and none is confirmed: {stopped:?}"
    );
    let promoted = lane_topology_with_owner("m-b1", 2);
    for name in ["m-b1", "m-b2", "m-g1", "m-g2"] {
        h.apply_view(name, promoted.clone());
    }
    faults.push(FaultClass::LeaderLoss);
    faults.push(FaultClass::RegionPartition);
    h.write("m-b1", TENANT, "op-b", "doc-shared", "beta-isolated-value");
    h.write("m-a1", TENANT, "op-3", "doc-shared", "alpha-isolated-value");

    // The isolated writer acknowledges inside its own Region: r-alpha still
    // has both Members and their link is healthy.
    let acked_op3 = match h.wait_ack("m-a1", TENANT, "op-3", AcknowledgementLevel::RegionQuorum, 3)
    {
        ReplicationOutcome::Acknowledged(report) => {
            (OperationId::try_new("op-3").expect("valid id"), report)
        }
        other => panic!("the isolated Region still reaches its own quorum: {other:?}"),
    };
    h.heal();
    let healed_at = h.network.now();

    // Act F: the entire r-alpha Region is lost after the acknowledged
    // write; the acknowledged operation is absent from every survivor.
    let fault_at = h.network.now();
    h.close("m-a1");
    h.close("m-a2");
    faults.push(FaultClass::RegionLossAfterAcknowledgedWrite);
    let survivors = ["m-b1", "m-b2", "m-g1", "m-g2"];
    let acked = [
        super::lane::AckedOperation {
            operation: acked_op1.0.clone(),
            level: acked_op1.1.observed(),
            sequence: StorageSequence::new(1),
            contributing: acked_op1
                .1
                .contributed()
                .iter()
                .flat_map(|(_, members)| members.iter().cloned())
                .collect(),
        },
        super::lane::AckedOperation {
            operation: acked_op3.0.clone(),
            level: acked_op3.1.observed(),
            sequence: StorageSequence::new(3),
            contributing: acked_op3
                .1
                .contributed()
                .iter()
                .flat_map(|(_, members)| members.iter().cloned())
                .collect(),
        },
    ];
    let (rpo_count, rpo_window) = h.rpo(TENANT, &acked, &survivors);
    assert_eq!(rpo_count, 1, "the quorum-acknowledged write is lost with its Region");

    // Act G: recovery — two lane ticks of detection and view application,
    // then the first post-fault successful operation by the surviving
    // owner.
    h.network.pump();
    h.network.pump();
    let recovered = h.write("m-b1", TENANT, "op-4", "doc-4", "post-fault-value");
    let recovered_at = h.network.now();
    let rto_ticks = recovered_at.ticks() - fault_at.ticks();

    // Act H: the lag observation for the surviving source, then repair and
    // the convergence observation naming every surviving Member.
    let survivor_lag: Vec<(MemberId, u64)> =
        survivors[1..].iter().map(|name| (h.member_id(name), h.lag("m-b1", name))).collect();
    for target in &survivors[1..] {
        let report = h.replicate("m-b1", target, TENANT, 6);
        assert!(report.caught_up());
    }
    let observed_at = h.network.now();
    let convergence = h.observe("op-4", &survivors, TENANT, observed_at);
    let convergence_ticks = observed_at.ticks() - healed_at.ticks();
    assert_eq!(
        convergence.holdings().len(),
        survivors.len(),
        "the observation names every surviving Member"
    );

    let receipt = ReplicationReceipt::new("three-region-composite", &h.topology())
        .with_faults(&faults)
        .with_acknowledgement(&acked_op1.0, acked_op1.1.observed(), &acked_op1.1)
        .with_acknowledgement(&acked_op3.0, acked_op3.1.observed(), &acked_op3.1)
        .with_acknowledgement_members(
            &OperationId::try_new("op-4").expect("valid id"),
            AcknowledgementLevel::Local,
            &[h.member_id("m-b1")],
        )
        .with_convergence(convergence, "every surviving Member durably holds the operation")
        .with_rpo(rpo_count, rpo_window)
        .with_rto_ticks(rto_ticks)
        .with_convergence_ticks(convergence_ticks)
        .with_lag(lag_distances.iter().chain(survivor_lag.iter()).cloned().collect())
        .with_limit("lane-scoped facts only: no production topology, timing, or performance claim")
        .with_limit("RPO counts operations acknowledged then absent from every survivor")
        .with_limit(
            "RTO availability condition: first post-fault local write by the surviving owner",
        )
        .with_limit("convergence condition names the surviving Members, not the lost Region")
        .with_limit("replication lag is a storage-sequence distance, not a latency");
    let _ = recovered;
    h.clean_up();
    receipt
}

#[test]
fn three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag() {
    let receipt = run_composite_lane();

    // Topology: three Regions, two Members each, one failure domain per
    // Region.
    assert_eq!(receipt.regions().len(), 3);
    assert_eq!(receipt.members().len(), 6);
    assert_eq!(receipt.failure_domains().len(), 3);
    assert!(receipt.failure_domains().iter().all(|(_, members)| members.len() == 2));

    // Faults: every fault class the contract's lane lists is named.
    let fault_names: Vec<&str> = receipt.faults().iter().map(|fault| fault.name()).collect();
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
    let acks = receipt.acknowledgements();
    assert!(acks.iter().any(|(operation, level, members)| operation.as_str() == "op-1"
        && *level == AcknowledgementLevel::AllRegions
        && members.len() == 6));
    assert!(acks.iter().any(|(operation, level, members)| operation.as_str() == "op-3"
        && *level == AcknowledgementLevel::RegionQuorum
        && members.len() == 2));

    // Convergence: its own observation with its condition, naming the
    // surviving Members.
    let convergence = receipt.convergence().expect("the lane reports its convergence observation");
    assert_eq!(convergence.holdings().len(), 4);
    assert!(!receipt.convergence_condition().is_empty());

    // Measurements with their definitions, measured in this scenario.
    let (rpo_count, rpo_window) = receipt.rpo().expect("RPO is measured in this scenario");
    assert_eq!(rpo_count, 1);
    let (window_low, window_high) = rpo_window.expect("a nonzero loss carries a sequence window");
    assert_eq!(window_low, window_high, "one lost operation spans one sequence");
    assert_eq!(receipt.rpo_definition(), RPO_DEFINITION);

    let rto = receipt.rto_ticks().expect("RTO is measured in this scenario");
    assert_eq!(rto, 2, "two lane ticks of detection and view application");
    assert_eq!(receipt.rto_definition(), RTO_DEFINITION);

    let convergence_interval =
        receipt.convergence_ticks().expect("the convergence interval is measured");
    assert!(convergence_interval > 0);
    assert_eq!(receipt.convergence_definition(), CONVERGENCE_DEFINITION);

    let lag = receipt.lag_distances();
    assert!(lag.len() >= 5, "the receipt carries the observed lag distances");
    assert!(lag.iter().all(|(_, distance)| *distance > 0), "measured before repair: {lag:?}");
    assert_eq!(receipt.lag_definition(), LAG_DEFINITION);

    // Limits are explicit.
    assert!(!receipt.limits().is_empty());
    assert!(
        receipt
            .limits()
            .iter()
            .any(|limit| limit.contains("no production topology, timing, or performance claim"))
    );

    // Reported as an artifact, never as a claim beyond the lane.
    eprintln!("{}", receipt.summary());
}

#[test]
fn replication_receipt_is_secret_safe_and_names_scenario_faults_and_limits() {
    let mut h = LaneHarness::new("secret-safety");
    // Canary-bearing documents: if any document content crossed the receipt,
    // these strings would appear in it.
    h.write("m-a1", "tenant-a", "op-1", "doc-canary", "CANARY-document-value-alpha");
    let ack = match h.wait_ack("m-a1", "tenant-a", "op-1", AcknowledgementLevel::RegionQuorum, 3) {
        ReplicationOutcome::Acknowledged(report) => report,
        other => panic!("the healthy pair reaches its Region quorum: {other:?}"),
    };
    h.delay_and_reorder("m-a1", "m-b1");
    let report = h.replicate("m-a1", "m-b1", "tenant-a", 4);
    assert!(report.caught_up());
    h.heal();
    let at = h.network.now();
    let convergence = h.observe("op-1", &["m-a1", "m-a2", "m-b1", "m-b2"], "tenant-a", at);

    let receipt = ReplicationReceipt::new("secret-safety-probe", &h.topology())
        .with_faults(&[FaultClass::LinkDelay, FaultClass::LinkReorder, FaultClass::Healing])
        .with_acknowledgement(
            &OperationId::try_new("op-1").expect("valid id"),
            ack.observed(),
            &ack,
        )
        .with_convergence(convergence, "every named Member durably holds the operation")
        .with_rpo(0, None)
        .with_rto_ticks(1)
        .with_convergence_ticks(1)
        .with_lag(vec![(h.member_id("m-b1"), 0)])
        .with_limit("lane-scoped facts only: no production topology, timing, or performance claim")
        .with_limit("the receipt carries identities, counts, and verdicts only");
    h.clean_up();

    // The receipt names the scenario, its faults, and its limits.
    assert_eq!(receipt.scenario(), "secret-safety-probe");
    let fault_names: Vec<&str> = receipt.faults().iter().map(|fault| fault.name()).collect();
    assert_eq!(fault_names, ["link-delay", "link-reorder", "healing"]);
    assert!(receipt.limits().len() >= 2);

    // Secret safety: no document content, credential, connection string,
    // private host path, or temp directory marker appears anywhere in the
    // receipt — neither in its Debug form nor in its summary.
    for rendered in [format!("{receipt:?}"), receipt.summary()] {
        for canary in ["CANARY-document-value-alpha", "doc-canary"] {
            assert!(
                !rendered.contains(canary),
                "document content crossed the receipt ({canary}): {rendered}"
            );
        }
        for forbidden in
            ["/", "tmp", "tbv2-w4", "wal", "ledger", "credential", "connec", "password", "token"]
        {
            assert!(
                !rendered.contains(forbidden),
                "a forbidden fact crossed the receipt ({forbidden}): {rendered}"
            );
        }
    }

    // The identities it does carry are the topology and operation facts.
    let summary = receipt.summary();
    for identity in ["r-alpha", "r-beta", "r-gamma", "m-a1", "m-b1", "op-1", "RegionQuorum"] {
        assert!(summary.contains(identity), "the summary names {identity}");
    }
    assert!(summary.contains(RPO_DEFINITION), "the summary carries the RPO definition");
}

#[test]
fn lane_composite_scenario_is_deterministic() {
    let first = run_composite_lane();
    let second = run_composite_lane();
    assert_eq!(first, second, "the same lane script produces the same receipt");
    assert_eq!(first.summary(), second.summary());
}

#[test]
fn lane_duplicate_replay_applies_each_operation_once() {
    let mut h = LaneHarness::new("duplicate-replay");
    h.write("m-a1", TENANT, "op-1", "doc-1", "lane-value-one");
    h.write("m-a1", TENANT, "op-2", "doc-2", "lane-value-two");
    h.duplicate("m-a1", "m-b1");
    let report = h.replicate("m-a1", "m-b1", TENANT, 4);
    assert!(report.caught_up());
    assert!(report.duplicates_resolved() >= 2, "the duplicated delivery replays idempotently");
    assert_eq!(h.journal_len("m-b1", TENANT), 2, "each operation applies exactly once");
    h.heal();
    h.clean_up();
}

#[test]
fn lane_tenant_isolation_probe_denies_cross_tenant_and_is_observable() {
    let mut h = LaneHarness::new("tenant-probe");
    h.write("m-a1", "tenant-a", "op-1", "doc-a", "tenant-a-canary-value");
    let source = h.member_id("m-a1");
    let requester = h.member_id("m-b1");

    // A hostile requester asks for tenant-a's history while the serving
    // scope names tenant-b: denied, observable, and nothing crosses.
    h.network
        .send(
            &requester,
            &source,
            ReplicationFrame::CatchUpRequest {
                requester: requester.clone(),
                tenant: TenantId::try_new("tenant-a").expect("valid tenant"),
                after: None,
                limit: 8,
            },
        )
        .expect("bounded link accepts the request frame");
    let denied = h.serve_under("m-a1", "tenant-b");
    assert!(
        matches!(&denied, Err(MemberError::CrossTenant { scope, attempted })
            if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
        "the lane serves no cross-tenant history: {denied:?}"
    );
    assert_eq!(h.member("m-a1").denials().len(), 1, "the denial is observable");
    assert!(
        h.network.receive(&requester, FrameSelector::Deliveries).is_empty(),
        "no batch crossed for the denied tenant"
    );

    // Positive control through the same lane.
    let report = h.replicate("m-a1", "m-b1", "tenant-a", 4);
    assert!(report.caught_up());
    assert!(h.holds("m-b1", "tenant-a", "op-1"));
    assert!(!h.holds("m-b1", "tenant-b", "op-1"), "the write never existed under tenant-b");
    h.clean_up();
}

#[test]
fn lane_region_partition_with_isolated_concurrent_writes_converges() {
    let mut h = LaneHarness::new("partition-isolated");
    h.partition_region("r-gamma");
    let promoted = lane_topology_with_owner("m-g1", 2);
    h.apply_view("m-g1", promoted.clone());
    h.apply_view("m-g2", promoted);
    h.write("m-g1", TENANT, "op-g", "doc-shared", "gamma-side-value");
    h.write("m-a1", TENANT, "op-a", "doc-shared", "alpha-side-value");
    h.heal();
    let deposed_view = lane_topology_with_owner("m-g1", 2);
    h.apply_view("m-a1", deposed_view);

    // Repair in both directions: both writers see each other's journal and
    // the D-3 policy decides identically on both sides.
    let into_alpha = h.replicate("m-g1", "m-a1", TENANT, 4);
    assert!(into_alpha.caught_up());
    let into_gamma = h.replicate("m-a1", "m-g1", TENANT, 4);
    assert!(into_gamma.caught_up());

    let value_at = |name: &str| -> String {
        h.documents(name, TENANT)
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
    let (at_alpha, at_gamma) = (value_at("m-a1"), value_at("m-g1"));
    assert_eq!(at_alpha, at_gamma, "the isolated concurrent writes converge");
    assert!(
        h.member("m-a1")
            .conflicts()
            .iter()
            .chain(h.member("m-g1").conflicts().iter())
            .any(|record| matches!(record.outcome(), ConflictOutcome::Winner(_))),
        "the losing write is retained as conflict history"
    );
    let rendered = format!("{:?}", h.member("m-a1").conflicts());
    assert!(!rendered.contains("alpha-side-value") && !rendered.contains("gamma-side-value"));
    h.clean_up();
}
