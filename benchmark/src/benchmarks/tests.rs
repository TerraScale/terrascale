//! Contract tests for the SE-15 benchmark-lane adapter.
//!
//! Every test here names a rule the lane owes the charter
//! (PD-2026-08-26-03): the exact verdict vocabulary, the statistical gate
//! carried over from V1, the recorded-run minimums, the honest unrun status
//! of a reference that did not execute, and the fail-closed rule that keeps
//! a wall-clock comparison from producing any verdict while a reference
//! baseline is missing.

#![allow(clippy::float_cmp)] // medians are asserted on exactly representable values

use super::{
    driver::V2Lane,
    gates::{self, MedianRange, ReferenceComparison, Trend},
    lane::{LaneConfig, LaneError, Workload},
    record::RunRecord,
    reference,
    vocabulary::{self, ObjectiveStatus, ReferenceRun, Verdict},
};

fn throughput(current: &[f64], prior: &[f64]) -> ReferenceComparison {
    ReferenceComparison {
        throughput: gates::classify_higher(
            &MedianRange::of(current).expect("current samples"),
            &MedianRange::of(prior).expect("prior samples"),
            5.0,
        ),
        p95: gates::classify_lower(
            &MedianRange::of(current).expect("current samples"),
            &MedianRange::of(prior).expect("prior samples"),
            5.0,
        ),
    }
}

/// The charter's verdict words are the only words a receipt may carry, and
/// each renders to exactly its charter token.
#[test]
fn verdict_vocabulary_is_exact_and_closed() {
    let tokens: Vec<&str> =
        [Verdict::V2Faster, Verdict::NoImprovement, Verdict::V2Regression, Verdict::Inconclusive]
            .iter()
            .map(|verdict| verdict.as_str())
            .collect();
    assert_eq!(tokens, ["v2-faster", "no-improvement", "v2-regression", "inconclusive"]);
    assert_eq!(vocabulary::VERDICT_VOCABULARY.len(), 4);
    for verdict in vocabulary::VERDICT_VOCABULARY {
        assert!(tokens.contains(&verdict), "unknown verdict token {verdict}");
    }
}

/// Objective status is the charter's separate axis and has its own three
/// exact words, including the space in `not met`.
#[test]
fn objective_status_vocabulary_is_exact_and_closed() {
    assert_eq!(ObjectiveStatus::Met.as_str(), "met");
    assert_eq!(ObjectiveStatus::NotMet.as_str(), "not met");
    assert_eq!(ObjectiveStatus::Inconclusive.as_str(), "inconclusive");
    assert_eq!(vocabulary::OBJECTIVE_STATUS_VOCABULARY, ["met", "not met", "inconclusive"]);
}

/// The canonical receipt line the standing discipline checks for. It must be
/// one line, in one shape, so a scanner can fail closed on its absence.
#[test]
fn objective_status_line_has_one_canonical_shape() {
    assert_eq!(
        vocabulary::objective_status_line(ObjectiveStatus::NotMet),
        "PostgreSQL-parity-or-better: not met"
    );
    assert_eq!(
        vocabulary::objective_status_line(ObjectiveStatus::Met),
        "PostgreSQL-parity-or-better: met"
    );
    assert_eq!(
        vocabulary::objective_status_line(ObjectiveStatus::Inconclusive),
        "PostgreSQL-parity-or-better: inconclusive"
    );
}

/// V1's median: the middle of an odd run, the mean of the middle pair of an
/// even run, over the sample's own full range.
#[test]
fn median_matches_v1_for_odd_and_even_sample_counts() {
    let odd = MedianRange::of(&[3.0, 1.0, 2.0]).expect("samples");
    assert_eq!(odd.median, 2.0);
    assert_eq!((odd.min, odd.max), (1.0, 3.0));

    let even = MedianRange::of(&[4.0, 1.0, 3.0, 2.0]).expect("samples");
    assert_eq!(even.median, 2.5);
    assert_eq!((even.min, even.max), (1.0, 4.0));

    assert!(MedianRange::of(&[]).is_none(), "no median exists without samples");
}

/// V1's classification, verbatim: a change is called only when the median
/// moves by more than the noise percent **and** every sample of one side
/// separates from every sample of the other side. Overlapping ranges are
/// stable noise, whatever the medians say.
#[test]
fn gate_requires_both_median_delta_and_range_separation() {
    let noise = 5.0;

    // Clearly separated: 100 -> 120 is +20% with disjoint ranges. Better for
    // a higher-is-better quantity, worse for a lower-is-better one.
    let prior = MedianRange { median: 100.0, min: 98.0, max: 102.0 };
    let current = MedianRange { median: 120.0, min: 118.0, max: 122.0 };
    assert_eq!(gates::classify_higher(&current, &prior, noise), Trend::Better);
    assert_eq!(gates::classify_lower(&current, &prior, noise), Trend::Worse);

    // Big median delta but overlapping ranges: stable noise, not a call.
    let overlapping = MedianRange { median: 120.0, min: 90.0, max: 150.0 };
    assert_eq!(gates::classify_higher(&overlapping, &prior, noise), Trend::Stable);
    assert_eq!(gates::classify_lower(&overlapping, &prior, noise), Trend::Stable);

    // Disjoint ranges but a median delta inside the noise percent: stable.
    let within_noise = MedianRange { median: 103.0, min: 102.5, max: 103.5 };
    assert_eq!(gates::classify_higher(&within_noise, &prior, noise), Trend::Stable);

    // A regression in the higher-is-better direction, an improvement in the
    // lower-is-better one — the same numbers read through both directions.
    let regressed = MedianRange { median: 80.0, min: 78.0, max: 82.0 };
    assert_eq!(gates::classify_higher(&regressed, &prior, noise), Trend::Worse);
    assert_eq!(gates::classify_lower(&regressed, &prior, noise), Trend::Better);
}

/// The objective reading of one comparison: parity-or-better means the
/// throughput trend is `Better` or `Stable` (the gate's own definition of
/// parity) and the p95 trend is not `Worse`.
#[test]
fn objective_status_reads_the_comparison() {
    let better = ReferenceComparison { throughput: Trend::Better, p95: Trend::Stable };
    let stable = ReferenceComparison { throughput: Trend::Stable, p95: Trend::Stable };
    let slower = ReferenceComparison { throughput: Trend::Worse, p95: Trend::Stable };
    let slower_p95 = ReferenceComparison { throughput: Trend::Better, p95: Trend::Worse };

    assert_eq!(gates::objective_status(better), (ObjectiveStatus::Met, Verdict::V2Faster));
    assert_eq!(gates::objective_status(stable), (ObjectiveStatus::Met, Verdict::NoImprovement));
    assert_eq!(gates::objective_status(slower), (ObjectiveStatus::NotMet, Verdict::V2Regression));
    assert_eq!(
        gates::objective_status(slower_p95),
        (ObjectiveStatus::NotMet, Verdict::V2Regression)
    );
}

/// The fail-closed rule: without **both** reference baselines having actually
/// run, no wall-clock comparison may produce any status other than
/// `inconclusive` — not `met`, and not a verdict. A missing reference can
/// never be averaged, extrapolated, or assumed away.
#[test]
fn a_missing_reference_forces_inconclusive() {
    let ran = ReferenceRun::Ran { identity: "observed-at-a-lane-run".to_owned() };
    let unrun = ReferenceRun::Unrun {
        reason: "no pinned endpoint on this host".to_owned(),
        owner: reference::POSTGRESQL_REFERENCE.owner,
    };
    let comparison = ReferenceComparison { throughput: Trend::Better, p95: Trend::Better };

    for (postgres, v1) in
        [(ran.clone(), unrun.clone()), (unrun.clone(), ran.clone()), (unrun.clone(), unrun)]
    {
        let (status, verdict) = gates::reference_comparison_status(&postgres, &v1, comparison);
        assert_eq!(status, ObjectiveStatus::Inconclusive, "postgres={postgres:?} v1={v1:?}");
        assert_eq!(verdict, Verdict::Inconclusive);
    }

    let (status, verdict) = gates::reference_comparison_status(&ran, &ran, comparison);
    assert_eq!((status, verdict), (ObjectiveStatus::Met, Verdict::V2Faster));
}

/// V1's recorded-run minimums: a recorded run needs at least 1,000 seed
/// documents, 10,000 operations per sample, and 5 samples. Trial runs may go
/// smaller and must say so.
#[test]
fn recorded_runs_enforce_v1_minimums() {
    assert!(LaneConfig::RECORD_MINIMUM.validate_for_record().is_ok());
    assert!(LaneConfig::RECORD_MINIMUM.is_recorded());

    for trial in [
        LaneConfig::COMMISSIONING_TRIAL.with_seed_documents(999),
        LaneConfig::COMMISSIONING_TRIAL.with_operations(9_999),
        LaneConfig::COMMISSIONING_TRIAL.with_samples(4),
    ] {
        assert!(
            matches!(trial.validate_for_record(), Err(LaneError::BelowRecordedMinimum { .. })),
            "{trial:?} must not be recordable"
        );
        assert!(!trial.is_recorded());
    }

    assert!(LaneConfig::COMMISSIONING_TRIAL.validate_for_record().is_err());
}

/// V1 alternated which server ran first inside each sample to cancel
/// first-run and second-run bias. The alternation must be a pure function of
/// the workload and sample indices and must actually alternate.
#[test]
fn run_order_alternates_within_a_workload() {
    let mut seen = Vec::new();
    for sample in 0..6 {
        seen.push(Workload::IndexedQuery.postgres_runs_first(sample));
    }
    assert_eq!(seen, vec![true, false, true, false, true, false]);

    // The workload index flips the phase, so the two workloads start on
    // opposite sides.
    assert_ne!(
        Workload::IndexedQuery.postgres_runs_first(0),
        Workload::Mixed.postgres_runs_first(0)
    );
}

/// The five comparable workloads exist, each carries its PostgreSQL and
/// TerraBaseV2 equivalents as data (the mapping document is generated from
/// these, not hand-copied beside them), and the mixed workload keeps V1's
/// 50/30/20 read/write/query ratio.
#[test]
fn every_workload_names_both_sides_of_its_mapping() {
    assert_eq!(Workload::ALL.len(), 5);
    for workload in Workload::ALL {
        assert!(!workload.terrabase_v2_operation().is_empty());
        assert!(workload.postgres_equivalent().starts_with("-- "), "{workload:?}");
    }
    assert_eq!(Workload::Mixed.mixed_ratios(), Some((50, 30, 20)));
    assert_eq!(Workload::DocumentRead.mixed_ratios(), None);
    assert_eq!(
        [Workload::DocumentRead, Workload::DocumentWrite, Workload::IndexedQuery, Workload::Mixed]
            .map(Workload::as_str),
        ["read", "write", "query", "mixed"]
    );
}

/// The mixed workload selects V1's 50/30/20 read/write/query split by
/// operation index, exactly as V1 selected it.
#[test]
fn the_mixed_workload_selects_v1_ratio_by_index() {
    let mut reads = 0;
    let mut writes = 0;
    let mut queries = 0;
    for index in 0..10 {
        match Workload::Mixed.select_mixed(index) {
            Workload::DocumentRead => reads += 1,
            Workload::DocumentWrite => writes += 1,
            Workload::IndexedQuery => queries += 1,
            other => panic!("unexpected sub-workload {other:?}"),
        }
    }
    assert_eq!((reads, writes, queries), (5, 3, 2));

    // An unmixed workload selects itself at every index.
    for index in 0..4 {
        assert_eq!(Workload::DocumentWrite.select_mixed(index), Workload::DocumentWrite);
    }
}

/// The session workload is mapped on both sides but has no in-process driver
/// here; the lane must say so instead of silently skipping it.
#[test]
fn the_session_workload_declares_its_open_adapter_slot() {
    assert_eq!(Workload::SessionCycle.as_str(), "session");
    let (reason, owner) = Workload::SessionCycle.driver_status().expect("the open slot is named");
    assert!(reason.contains("host session"), "{reason}");
    assert!(!owner.is_empty());
    assert!(Workload::DocumentRead.driver_status().is_none());
}

/// The PostgreSQL pin is a decision with a full configuration, and its
/// observed identity is honestly absent until someone captures it at a lane
/// run. Nothing in the pin invents a `version()` string.
#[test]
fn the_postgresql_pin_records_a_decision_and_an_empty_observation_slot() {
    let pin = &reference::POSTGRESQL_REFERENCE;
    assert_eq!(pin.decided_version, "18 (exact point release captured at the first lane run)");
    assert!(pin.observed_identity.is_none(), "no version() output has ever been observed");
    assert!(!pin.server_configuration.is_empty());
    assert!(!pin.client_configuration.is_empty());
    assert!(pin.server_configuration.iter().any(|(key, _)| *key == "fsync"));
    assert!(!pin.owner.is_empty());
    assert!(pin.describe().starts_with("postgresql — decided 18"));
}

/// The TerraBase V1 baseline pin keeps the exact source identity the parity
/// work already cites.
#[test]
fn the_v1_reference_pin_is_the_pinned_source_commit() {
    let pin = &reference::TERRABASE_V1_REFERENCE;
    assert_eq!(pin.decided_version, "fa99b9257776dbe625e6a2388c74c9ce916637ee");
    assert_eq!(pin.observed_identity, Some("fa99b9257776dbe625e6a2388c74c9ce916637ee"));
    assert!(pin.describe().contains("observed: fa99b925"));
}

/// On this host the PostgreSQL reference reports `Unrun` with a reason that
/// names the concrete environment gap and an owner, never a silent skip and
/// never a number.
#[test]
fn a_reference_that_did_not_run_says_why_and_who_owns_it() {
    let postgres = reference::postgresql_reference_run();
    let ReferenceRun::Unrun { reason, owner } = &postgres else {
        panic!("no pinned PostgreSQL endpoint exists on this host, so the run cannot be `Ran`");
    };
    assert!(reason.contains("no postgres"), "{reason}");
    assert!(!owner.is_empty());

    let v1 = reference::terrabase_v1_reference_run();
    let ReferenceRun::Unrun { reason, owner } = &v1 else {
        panic!("the pinned V1 tree is read-only here, so its server cannot run");
    };
    assert!(reason.contains("read-only"), "{reason}");
    assert!(!owner.is_empty());
}

/// A rendered receipt carries the canonical objective-status line, the exact
/// verdict, both references' honest statuses, and — when the objective is not
/// met — a regression follow-up with a named owner.
#[test]
fn a_receipt_renders_the_discipline_lines() {
    let record = RunRecord::commissioning(Workload::IndexedQuery);
    let text = record.render();
    assert!(text.contains("PostgreSQL-parity-or-better: inconclusive"), "{text}");
    assert!(text.contains("verdict: inconclusive"), "{text}");
    assert!(text.contains("postgresql reference: unrun"), "{text}");
    assert!(text.contains("terrabase v1 reference: unrun"), "{text}");
    assert!(!text.contains("PostgreSQL-parity-or-better: met"), "{text}");
    assert!(!text.contains("v2-faster"), "{text}");
    assert!(text.contains("postgresql pin: "), "{text}");
    assert!(text.contains("terrabase v1 pin: "), "{text}");
}

/// A regressing recorded run must name an owner; the rule is a function, not
/// a promise in prose.
#[test]
fn a_regression_assigns_a_named_owner() {
    let follow_up =
        RunRecord::regression_follow_up(ObjectiveStatus::NotMet).expect("a regression is tracked");
    assert_eq!(follow_up.owner, "TerraScale benchmark lane owner (SE-15 follow-up)");
    assert!(follow_up.rule.contains("never re-benchmarked into agreement"));
    assert!(RunRecord::regression_follow_up(ObjectiveStatus::Met).is_none());
    assert!(RunRecord::regression_follow_up(ObjectiveStatus::Inconclusive).is_none());
}

/// The V2 side of the lane really runs: the driver seeds a durable engine
/// and returns exact, deterministic counters for each workload it drives.
/// These are counters, not timings, so they are reproducible facts.
#[test]
fn the_v2_lane_measures_deterministic_counters_per_workload() {
    let lane = V2Lane::open("counters");
    lane.seed(32);

    let seeded_bytes = lane.durable_bytes();
    assert!(seeded_bytes > 0, "seeding must write durable bytes");

    let write = lane.run(Workload::DocumentWrite, 16);
    assert_eq!(write.operations, 16);
    assert!(write.durable_bytes_written > 0, "each commit must append to the log");
    assert_eq!(write.documents_returned, 0);

    let read = lane.run(Workload::DocumentRead, 16);
    assert_eq!(read.operations, 16);
    assert_eq!(read.documents_returned, 16);
    assert_eq!(read.durable_bytes_written, 0, "reads append nothing");

    let query = lane.run(Workload::IndexedQuery, 16);
    assert_eq!(query.operations, 16);
    assert!(query.documents_returned > 0, "the seeded equality query must match");

    let mixed = lane.run(Workload::Mixed, 10);
    assert_eq!(mixed.operations, 10);

    lane.remove();
}

/// Seeding the same document count twice through the same lane writes the
/// same durable bytes both times — the counter is a fact about the
/// operations, not about the machine or the moment.
#[test]
fn the_durable_byte_counter_is_reproducible() {
    let first = {
        let lane = V2Lane::open("reproducible-a");
        lane.seed(16);
        let bytes = lane.durable_bytes();
        lane.remove();
        bytes
    };
    let second = {
        let lane = V2Lane::open("reproducible-b");
        lane.seed(16);
        let bytes = lane.durable_bytes();
        lane.remove();
        bytes
    };
    assert_eq!(first, second, "identical operation streams must write identical bytes");
}

/// The warm-up discipline: warm-up operations run before any measured sample
/// and are excluded from the counters, exactly as V1 excluded them.
#[test]
fn warm_up_runs_are_excluded_from_the_measurement() {
    let lane = V2Lane::open("warmup");
    lane.seed(16);

    let warmed = lane.warm_up(Workload::DocumentWrite, 8);
    assert_eq!(warmed.operations, 8);
    let baseline = lane.durable_bytes();

    let measured = lane.run(Workload::DocumentWrite, 8);
    assert_eq!(measured.operations, 8);
    assert!(measured.durable_bytes_written > 0);
    assert!(lane.durable_bytes() > baseline, "the measured window must append after warm-up");

    lane.remove();
}

/// The commissioning sampler drives the whole gate machinery end to end on
/// real wall-clock samples and still reports `inconclusive`, because no
/// reference baseline ran. This is the proof that the honest outcome is
/// reachable by construction, not by discipline alone.
#[test]
fn the_commissioning_sampler_reports_inconclusive_without_references() {
    let outcome = V2Lane::open("commissioning").commission(Workload::DocumentRead, 3, 4);
    assert_eq!(outcome.samples().len(), 3, "one throughput sample per measured round");
    assert!(outcome.samples().iter().all(|sample| *sample > 0.0));
    assert_eq!(outcome.status, ObjectiveStatus::Inconclusive);
    assert_eq!(outcome.verdict, Verdict::Inconclusive);
    assert!(outcome.rendered.contains("PostgreSQL-parity-or-better: inconclusive"));
}

/// Sanity on the gate math through real samples: a workload measured against
/// itself is stable, so the objective reading of a self-comparison is parity.
#[test]
fn a_self_comparison_reads_as_parity() {
    let first = V2Lane::open("self-a").commission(Workload::DocumentRead, 5, 8);
    let second = V2Lane::open("self-b").commission(Workload::DocumentRead, 5, 8);
    let comparison = throughput(first.samples(), second.samples());
    assert_eq!(comparison.throughput, Trend::Stable);
}
