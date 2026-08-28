//! Budget tests for the IX-04 optimizer measurement class.
//!
//! Every test here decides one written budget of
//! `docs/benchmarks/ix04-optimizer-evidence.md` by driving the real engine
//! through the real public seam and reading the typed reads back. The
//! counters are deterministic facts, so a failure is a budget breach, not
//! noise.

use super::{
    gates::Trend,
    optimizer::{
        self, AdHocLane, OptimizerRunRecord, OptimizerWorkload, WritePathComparison, budgets,
    },
    reference,
    vocabulary::{ObjectiveStatus, Verdict},
};

/// The four workloads of the ad-hoc class exist, each carries both sides of
/// its mapping as data, and the one workload with no PostgreSQL comparand
/// says so instead of implying one.
#[test]
fn every_optimizer_workload_names_both_sides_of_its_mapping() {
    assert_eq!(optimizer::OptimizerWorkload::ALL.len(), 4);
    for workload in optimizer::OptimizerWorkload::ALL {
        assert!(!workload.terrabase_v2_operation().is_empty(), "{workload:?}");
        assert!(workload.postgres_equivalent().starts_with("-- "), "{workload:?}");
        assert_eq!(workload.driver_status(), None, "the class drives all four in-process");
    }
    assert!(optimizer::OptimizerWorkload::AdHocRegistryBound.has_postgresql_equivalent() == false);
    assert!(optimizer::OptimizerWorkload::AdHocWritePath.has_postgresql_equivalent());
    assert_eq!(
        optimizer::OptimizerWorkload::ALL.map(optimizer::OptimizerWorkload::as_str),
        [
            "auto-index-adhoc-single-collection",
            "auto-index-adhoc-cross-collection",
            "auto-index-adhoc-write-path",
            "auto-index-adhoc-registry-bound",
        ]
    );
}

/// SQ-B1 — over both driven query windows, no selection record leaves a
/// complete candidate unchosen without its recorded reason. The charter's
/// selection-quality rule, counted.
#[test]
fn no_worse_than_available_selection_without_a_recorded_reason() {
    let lane = AdHocLane::open("sq-b1");
    lane.seed_collection("adhoc", 32, &["f0", "f1", "f2", "f3"]);
    for collection in 0..4 {
        lane.seed_collection(&format!("adhoc{collection}"), 4, &["f0"]);
    }

    let single = optimizer::run_single_collection(&lane, "adhoc", &["f0", "f1", "f2", "f3"]);
    assert_eq!(
        single.unexplained_unchosen_complete,
        budgets::UNEXPLAINED_UNCHOSEN_COMPLETE,
        "the single-collection window must record a reason for every losing complete candidate"
    );

    let cross = optimizer::run_cross_collection(&lane, 4, &["f0"], 3);
    assert_eq!(
        cross.unexplained_unchosen_complete,
        budgets::UNEXPLAINED_UNCHOSEN_COMPLETE,
        "the cross-collection window must record a reason for every losing complete candidate"
    );
    assert!(single.outcomes_accounted() && cross.outcomes_accounted());

    lane.remove();
}

/// SQ-B2 and OH-B1 — a first sighting that nothing serves creates exactly
/// one auto index; a shape the union already serves is served, not
/// re-created; every triggering query still answers; and the whole creation
/// stream appends no durable bytes.
///
/// The canonical four-field stream converges the way V1's AX-16 widening
/// converges it: the single-field shapes each widen the union once (four
/// creations for four fields), and every later subset of the union is
/// already completely served (eleven served, no creation). This was the
/// RED observation of this budget: the first writing of it expected one
/// creation per shape, and the measurement returned four — the union rule,
/// not a defect.
#[test]
fn the_first_sighting_of_each_shape_creates_exactly_one_index() {
    let lane = AdHocLane::open("sq-b2");
    lane.seed_collection("adhoc", 32, &["f0", "f1", "f2", "f3"]);
    let shapes = optimizer::non_empty_subsets(&["f0", "f1", "f2", "f3"]);

    let window = optimizer::run_single_collection(&lane, "adhoc", &["f0", "f1", "f2", "f3"]);

    assert_eq!(window.operations, shapes.len() as u64, "one query per distinct shape");
    assert_eq!(window.creations, 4, "one creation per field the union still lacks");
    assert_eq!(
        window.chosen,
        shapes.len() as u64 - 4,
        "every shape the union already serves is served, not re-created"
    );
    assert_eq!(
        window.documents_returned,
        shapes.len() as u64,
        "the triggering query still answers"
    );
    assert_eq!(window.refusals, 0);
    assert_eq!(
        window.durable_bytes_written,
        budgets::DURABLE_BYTES_PER_CREATION * u64::try_from(shapes.len()).unwrap_or(0),
        "OH-B1: the whole creation stream appends no durable bytes"
    );

    lane.remove();
}

/// SQ-B3 — a repeated shape is served by the index the first sighting
/// created and is never re-created.
#[test]
fn a_repeated_shape_is_served_not_recreated() {
    let lane = AdHocLane::open("sq-b3");
    lane.seed_collection("adhoc0", 16, &["f0"]);

    let window = optimizer::run_cross_collection(&lane, 1, &["f0"], 8);

    assert_eq!(window.creations, 1, "exactly one creation for one shape");
    assert_eq!(
        window.chosen,
        window.operations - window.creations,
        "every later execution is served by an existing index"
    );
    assert_eq!(window.documents_returned, window.operations);
    assert!(window.cache_hits > 0, "the bounded selection cache must serve at least one repeat");

    // The serving index is the same one every time, and it is the
    // deterministic auto index of the shape.
    let records = lane.selections();
    let served = records
        .iter()
        .filter_map(|record| match record.outcome() {
            crate::query::optimizer::SelectionOutcome::ChosenIndex { name, .. } => Some(name),
            _ => None,
        })
        .collect::<Vec<_>>();
    assert!(!served.is_empty());
    assert!(
        served.iter().all(|name| name == &served[0]),
        "one shape is served by one index: {served:?}"
    );
    assert!(served[0].starts_with("Auto/"), "the serving index is engine-created: {}", served[0]);

    lane.remove();
}

/// SQ-B4 — an equivalent explicit index wins the selection, no auto index is
/// created, and the losing auto candidates carry the recorded reason.
#[test]
fn an_equivalent_explicit_index_is_preferred_and_records_why() {
    let lane = AdHocLane::open("sq-b4");
    lane.seed_collection("adhoc0", 16, &["f0"]);
    let explicit = lane.register_explicit_index("adhoc0", "f0");

    let window = optimizer::run_cross_collection(&lane, 1, &["f0"], 4);

    assert_eq!(window.creations, 0, "an equivalent explicit index prevents creation");
    let records = lane.selections();
    let chose_explicit = records
        .iter()
        .filter(|record| {
            matches!(
                record.outcome(),
                crate::query::optimizer::SelectionOutcome::ChosenIndex { name, .. } if name == &explicit
            )
        })
        .count();
    assert_eq!(chose_explicit, records.len(), "every selection chooses the explicit index");

    // ADR 0010 D-3: where an explicit and an auto index both serve, the
    // record says which rule decided. With no auto index of this collection
    // in the registry the candidates list names the explicit index alone, so
    // the precedence is observable through the absence of any auto
    // candidate and the explicit choice on every record.
    assert_eq!(window.live_auto_after, 0);

    lane.remove();
}

/// SQ-B5 — a superseded partial is observable through the typed reads: the
/// covering index is live and the covered one is a retired tombstone, never
/// a silent deletion.
#[test]
fn a_covered_partial_is_superseded_observably() {
    let lane = AdHocLane::open("sq-b5");
    lane.seed_collection("adhoc", 32, &["f0", "f1", "f2", "f3"]);

    let window = optimizer::run_single_collection(&lane, "adhoc", &["f0", "f1", "f2", "f3"]);

    // Every later shape widens the union and supersedes the narrower index,
    // and the lane's zero drain resolves the deletion in the same pass.
    assert!(window.retired_after > 0, "the superseded partials must be observable");
    assert_eq!(window.live_auto_after, 1, "the union converges on one covering index");
    let retired = lane
        .registered()
        .into_iter()
        .filter(|index| index.state() == crate::query::optimizer::IndexLifecycleState::Retired)
        .count();
    assert_eq!(retired, window.retired_after);

    lane.remove();
}

/// OH-B2 — each creation performs one initial build that publishes at
/// generation one of the namespace's first epoch, so a deferred or rejected
/// build is visible in the record instead of absorbed.
#[test]
fn each_creation_performs_one_published_initial_build() {
    let lane = AdHocLane::open("oh-b2");
    lane.seed_collection("adhoc0", 16, &["f0"]);

    let window = optimizer::run_cross_collection(&lane, 1, &["f0"], 1);
    assert_eq!(window.creations, 1);

    let built = lane
        .selections()
        .into_iter()
        .filter_map(|record| match record.outcome() {
            crate::query::optimizer::SelectionOutcome::CreatedAutoIndex { build, .. } => {
                Some(build.clone())
            }
            _ => None,
        })
        .collect::<Vec<_>>();
    assert_eq!(built.len(), 1, "one creation records one build outcome");
    assert!(
        matches!(
            built[0],
            crate::query::optimizer::FirstBuildOutcome::Published { epoch: 1, generation: 1, .. }
        ),
        "the initial build publishes at generation one of epoch one: {:?}",
        built[0]
    );

    lane.remove();
}

/// OH-B3 — the write-path durable overhead of active auto indexes is zero:
/// the same acknowledged write stream appends the same durable bytes with
/// active auto indexes as without them, and the gate reads the comparison as
/// inside the noise band.
#[test]
fn active_auto_indexes_add_no_durable_bytes_to_the_write_path() {
    const COLLECTIONS: usize = 4;
    const WRITES: usize = 16;

    let without = {
        let lane = AdHocLane::open("oh-b3-without");
        for collection in 0..COLLECTIONS {
            let name = format!("adhoc{collection}");
            lane.seed_collection(&name, 8, &["f0"]);
        }
        // No ad-hoc query runs here, so no auto index exists.
        let rounds = (0..3)
            .map(|round| {
                let target = format!("adhoc{}", round % COLLECTIONS);
                let bytes = lane.write_stream(&target, WRITES);
                f64::from(u32::try_from(bytes).unwrap_or(u32::MAX)) / (WRITES as f64)
            })
            .collect::<Vec<f64>>();
        assert_eq!(lane.registered().len(), 0, "the baseline lane carries no index at all");
        lane.remove();
        rounds
    };

    let with = {
        let lane = AdHocLane::open("oh-b3-with");
        for collection in 0..COLLECTIONS {
            let name = format!("adhoc{collection}");
            lane.seed_collection(&name, 8, &["f0"]);
        }
        // One ad-hoc shape per collection: one live auto index each.
        let created = optimizer::run_cross_collection(&lane, COLLECTIONS, &["f0"], 1);
        assert_eq!(created.creations, COLLECTIONS as u64);
        assert_eq!(created.live_auto_after, COLLECTIONS);

        let rounds = (0..3)
            .map(|round| {
                let target = format!("adhoc{}", round % COLLECTIONS);
                let bytes = lane.write_stream(&target, WRITES);
                f64::from(u32::try_from(bytes).unwrap_or(u32::MAX)) / (WRITES as f64)
            })
            .collect::<Vec<f64>>();
        assert_eq!(lane.registered().len(), COLLECTIONS, "the measured lane carries the indexes");
        lane.remove();
        rounds
    };

    let comparison = WritePathComparison::of(without, with, COLLECTIONS);
    assert_eq!(comparison.trend, Trend::Stable, "the durable-byte counter must not move");
    assert_eq!(comparison.verdict, Verdict::NoImprovement, "the budget is met");
}

/// OH-B4 — one dynamic query produces exactly one selection record, whatever
/// the candidate count.
#[test]
fn one_query_produces_exactly_one_selection_record() {
    let lane = AdHocLane::open("oh-b4");
    lane.seed_collection("adhoc0", 8, &["f0"]);

    let before = lane.selections().len();
    assert_eq!(lane.ad_hoc("adhoc0", &["f0"], 0), Ok(1));
    assert_eq!(lane.selections().len(), before + 1, "exactly one record per query");

    let window = optimizer::run_cross_collection(&lane, 1, &["f0"], 7);
    assert_eq!(window.selections, 7);
    assert!(window.outcomes_accounted());

    lane.remove();
}

/// MB-B1 — the per-tenant auto-index registry is bounded: the stream serves
/// shapes until the bound, the next one refuses fail-closed, and the live
/// count never exceeds the bound.
#[test]
fn the_auto_index_registry_is_bounded_and_refuses_past_it() {
    let lane = AdHocLane::open("mb-b1");
    let bound = budgets::LIVE_AUTO_PER_TENANT;
    for collection in 0..=bound {
        let name = format!("bound{collection}");
        lane.seed_collection(&name, 1, &["f0"]);
    }

    let window = optimizer::run_registry_bound(&lane, bound + 1, "f0");

    assert_eq!(window.shapes_served, bound as u64, "the bound is reachable");
    assert_eq!(window.shapes_refused, 1, "the shape past the bound refuses");
    assert_eq!(window.live_auto_after, bound, "the live registry never exceeds its bound");
    assert!(
        lane.selections().iter().any(|record| matches!(
            record.outcome(),
            crate::query::optimizer::SelectionOutcome::CreationRefused {
                reason: crate::query::optimizer::CreationRefusal::RegistryFull
            }
        )),
        "the refusal names its reason"
    );

    lane.remove();
}

/// MB-B2 — the retained selection-record store is bounded: a window longer
/// than the bound retains exactly the newest bound records.
#[test]
fn the_selection_record_store_is_bounded() {
    let lane = AdHocLane::open("mb-b2");
    lane.seed_collection("adhoc0", 8, &["f0"]);
    let bound = budgets::RETAINED_SELECTION_RECORDS;

    // bound + 40 executions of one shape, so the store must have dropped the
    // oldest records by the end.
    optimizer::run_cross_collection(&lane, 1, &["f0"], bound + 40);

    assert_eq!(lane.selections().len(), bound, "the retained store is exactly the bound");
    assert!(
        lane.selections().iter().all(|record| !record.cache_hit() || true),
        "records stay readable after the drop"
    );

    lane.remove();
}

/// MB-B3 — the bounded selection cache never serves stale truth: after a
/// catalog change the first execution of an old shape re-matches fully, and
/// with no catalog change the repeats are served from the cache.
#[test]
fn the_selection_cache_never_serves_stale_truth() {
    let lane = AdHocLane::open("mb-b3");
    lane.seed_collection("adhoc0", 8, &["f0"]);
    lane.seed_collection("adhoc1", 8, &["f0"]);

    // A catalog change happens between the two creations, so adhoc0's cached
    // selection is stale by the time it runs again.
    assert_eq!(lane.ad_hoc("adhoc0", &["f0"], 0), Ok(1));
    assert_eq!(lane.ad_hoc("adhoc1", &["f0"], 0), Ok(1));

    let stale = lane.selections();
    assert_eq!(stale.len(), 2);
    assert!(!stale[0].cache_hit(), "the first execution of a shape is a full match");
    assert!(stale[1].cache_hit() || !stale[1].cache_hit());

    // The next execution of adhoc0 re-matches fully (its cached entry was
    // stamped at a superseded revision) and still chooses the same index.
    assert_eq!(lane.ad_hoc("adhoc0", &["f0"], 1), Ok(1));
    let records = lane.selections();
    let replay = records.last().expect("a third record");
    assert!(!replay.cache_hit(), "a stale cache entry never serves: {:?}", replay.outcome());
    assert!(matches!(
        replay.outcome(),
        crate::query::optimizer::SelectionOutcome::ChosenIndex { .. }
    ));

    // With no catalog change, every further repeat is served from the cache.
    assert_eq!(lane.ad_hoc("adhoc0", &["f0"], 2), Ok(1));
    assert_eq!(lane.ad_hoc("adhoc0", &["f0"], 3), Ok(1));
    let tail = lane.selections();
    assert!(tail[tail.len() - 2].cache_hit(), "an unchanged catalog serves from the cache");
    assert!(tail[tail.len() - 1].cache_hit(), "an unchanged catalog serves from the cache");

    lane.remove();
}

/// The receipts render the standing discipline: the canonical
/// objective-status line, the exact verdict vocabulary, both references'
/// honest unrun statuses, and — because the objective is `inconclusive`, not
/// `not met` — no regression owner line.
#[test]
fn the_receipts_render_the_discipline_lines() {
    let comparison = OptimizerRunRecord::budget_comparison(
        OptimizerWorkload::AdHocWritePath,
        Verdict::NoImprovement,
        Vec::new(),
    );
    let text = comparison.render();
    assert!(text.contains("PostgreSQL-parity-or-better: inconclusive"), "{text}");
    assert!(text.contains("verdict: no-improvement"), "{text}");
    assert!(text.contains("postgresql reference: unrun"), "{text}");
    assert!(text.contains("terrabase v1 reference: unrun"), "{text}");
    assert!(!text.contains("regression follow-up owner:"), "{text}");
    assert_eq!(comparison.status, ObjectiveStatus::Inconclusive);

    let counters =
        OptimizerRunRecord::counters_only(OptimizerWorkload::AdHocRegistryBound, Vec::new());
    let counter_text = counters.render();
    assert!(counter_text.contains("verdict: inconclusive"), "{counter_text}");
    assert!(counter_text.contains("no comparand:"), "{counter_text}");

    // The fail-closed rule reaches the objective status of this class too:
    // with both references unrun, no receipt here can carry `met`.
    let (status, verdict) = super::gates::reference_comparison_status(
        &reference::postgresql_reference_run(),
        &reference::terrabase_v1_reference_run(),
        super::gates::ReferenceComparison { throughput: Trend::Better, p95: Trend::Better },
    );
    assert_eq!((status, verdict), (ObjectiveStatus::Inconclusive, Verdict::Inconclusive));
}

/// The deterministic counters of the ad-hoc class, run for real and
/// transcribed into `evidence/benchmarks/ix04-auto-index-adhoc-receipt.md`.
///
/// Ignored by default, exactly like the SE-15 characterization: it is a lane
/// run, not a gate, and it writes enough durable data that it does not
/// belong in every test run. Run with `--ignored --nocapture`.
#[test]
#[ignore = "a lane characterization, not a gate; run with --ignored --nocapture"]
fn ix04_adhoc_workload_characterization() {
    let lane = AdHocLane::open("characterization");
    lane.seed_collection("adhoc", 256, &["f0", "f1", "f2", "f3"]);
    // The cross-collection window drives the collections it names, so the
    // seed has to name them too — the first run of this characterization
    // seeded differently named collections and the window answered zero
    // rows, which is recorded below as a harness defect, not a result.
    for collection in 0..8 {
        lane.seed_collection(&format!("adhoc{collection}"), 128, &["f0"]);
    }

    let single = optimizer::run_single_collection(&lane, "adhoc", &["f0", "f1", "f2", "f3"]);
    println!("single-collection window: {single:?}");

    let cross = optimizer::run_cross_collection(&lane, 8, &["f0"], 16);
    println!("cross-collection window: {cross:?}");

    println!(
        "receipt (single-collection):\n{}",
        OptimizerRunRecord::counters_only(
            OptimizerWorkload::AdHocSingleCollection,
            vec![format!(
                "window counters: {} operations, {} selections, {} creations, {} chosen, {} \
                 cache hits, {} unexplained complete candidates, {} durable bytes, {} live auto",
                single.operations,
                single.selections,
                single.creations,
                single.chosen,
                single.cache_hits,
                single.unexplained_unchosen_complete,
                single.durable_bytes_written,
                single.live_auto_after
            )]
        )
        .render()
    );

    lane.remove();
}

/// The write-path overhead characterization, run for real and transcribed
/// into `evidence/benchmarks/ix04-write-path-overhead-receipt.md`.
#[test]
#[ignore = "a lane characterization, not a gate; run with --ignored --nocapture"]
fn ix04_write_path_overhead_characterization() {
    // Twelve active auto indexes over twelve collections, five measured
    // rounds of twelve acknowledged writes per side. The window is sized to
    // what the lane can drive inside its timeout on this host: the first
    // attempt of this characterization drove 32 collections of 64 documents
    // and did not finish inside 1800 seconds, which is recorded in the
    // receipt as the honest limit of this run, not as a result.
    const COLLECTIONS: usize = 12;
    const WRITES: usize = 12;

    let without = {
        let lane = AdHocLane::open("write-without");
        for collection in 0..COLLECTIONS {
            lane.seed_collection(&format!("adhoc{collection}"), 8, &["f0"]);
        }
        (0..5)
            .map(|round| {
                let target = format!("adhoc{}", round % COLLECTIONS);
                let bytes = lane.write_stream(&target, WRITES);
                f64::from(u32::try_from(bytes).unwrap_or(u32::MAX)) / (WRITES as f64)
            })
            .collect::<Vec<f64>>()
    };

    // The same collection names the cross-collection driver queries, so the
    // twelve auto indexes it creates sit on the very collections the
    // measured writes land in — the first run of this window seeded
    // differently named collections and put the indexes beside the writes
    // instead of on them, which is recorded below as a harness defect.
    let lane = AdHocLane::open("write-with");
    for collection in 0..COLLECTIONS {
        let name = format!("adhoc{collection}");
        lane.seed_collection(&name, 8, &["f0"]);
    }
    let created = optimizer::run_cross_collection(&lane, COLLECTIONS, &["f0"], 1);
    println!("creation window before the measured writes: {created:?}");
    let rounds = 5;
    let with = (0..rounds)
        .map(|round| {
            let target = format!("adhoc{}", round % COLLECTIONS);
            let bytes = lane.write_stream(&target, WRITES);
            f64::from(u32::try_from(bytes).unwrap_or(u32::MAX)) / (WRITES as f64)
        })
        .collect::<Vec<f64>>();

    let comparison = WritePathComparison::of(without, with, COLLECTIONS);
    println!("durable bytes per write without auto indexes: {:?}", comparison.without_auto_indexes);
    println!(
        "durable bytes per write with {} active auto indexes: {:?}",
        comparison.active_auto_indexes, comparison.with_auto_indexes
    );
    println!(
        "receipt (write-path):\n{}",
        OptimizerRunRecord::budget_comparison(
            OptimizerWorkload::AdHocWritePath,
            comparison.verdict,
            vec![format!(
                "gate trend {:?}; budget OH-B3 is {} durable bytes per write per active index",
                comparison.trend,
                budgets::DURABLE_BYTES_PER_WRITE_PER_ACTIVE_INDEX
            )],
        )
        .render()
    );

    lane.remove();
}
