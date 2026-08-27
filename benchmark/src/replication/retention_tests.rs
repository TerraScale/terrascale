//! In-crate GAP-R02 tests: the tombstone retention watermark contract and
//! its two serving paths (offline-member catch-up and the declared-bound
//! interactions).
//!
//! These tests follow the W4-REPLICATION suite's conventions
//! (`src/replication/tests.rs`): `TenantScope::from_parts` mirrors
//! Platform's scope construction, every fixture uses a temporary directory
//! and synthetic documents, time is driven explicitly, and no private lock
//! state or WAL bytes are inspected.
//!
//! The declared history bound in these tests models what the storage
//! compaction path (GAP-S1) will declare in production: no engine component
//! gives up journal history today, so the declaration is the seam that
//! packet will drive, exercised here with `durably_held = true` because the
//! journal still covers the window it extracts from.

use crate::replication::MemberId;
use crate::replication::member::{MemberError, ReplicationMember};
use crate::replication::retention::{
    ConsumerObservation, DeletionEvidence, RetentionConsumer, RetentionRefusal, TombstoneRetention,
};
use crate::types::StorageSequence;

fn sequence(value: u64) -> StorageSequence {
    StorageSequence::new(value)
}

fn member(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid member identifier")
}

fn tenant(name: &str) -> crate::types::TenantId {
    crate::types::TenantId::try_new(name).expect("valid tenant identifier")
}

/// One consumer of the member class, for the contract tests.
fn member_consumer(name: &str, tenant_name: &str) -> RetentionConsumer {
    RetentionConsumer::Member { member: member(name), tenant: tenant(tenant_name) }
}

#[test]
fn the_floor_starts_at_zero_and_only_rises() {
    let mut retention = TombstoneRetention::new(None);
    assert_eq!(retention.retention_floor().get(), 0, "everything is retained at first");
    assert_eq!(retention.history_bound(), None, "no history has been given up");

    retention
        .observe_consumer(member_consumer("m-a", "tenant-a"), Some(sequence(4)))
        .expect_recorded();
    let advance = retention.advance_floor(sequence(2)).expect("two is below the consumer");
    assert_eq!(advance.floor().get(), 2);
    assert_eq!(retention.retention_floor().get(), 2);

    let refused = retention.advance_floor(sequence(1)).expect_err("the floor is monotone");
    assert_eq!(
        refused,
        RetentionRefusal::FloorRegression { current: sequence(2), candidate: sequence(1) },
        "a refused candidate does not move the floor"
    );
    assert_eq!(retention.retention_floor().get(), 2);
    assert_eq!(retention.refusals().len(), 1, "the refusal is observable");
}

#[test]
fn the_floor_never_advances_past_the_slowest_consumer() {
    let mut retention = TombstoneRetention::new(None);
    retention
        .observe_consumer(member_consumer("m-fast", "tenant-a"), Some(sequence(9)))
        .expect_recorded();
    retention
        .observe_consumer(member_consumer("m-offline", "tenant-a"), Some(sequence(3)))
        .expect_recorded();

    let refused = retention.advance_floor(sequence(4)).expect_err("the offline member holds it");
    match refused {
        RetentionRefusal::ConsumerBelowFloor { consumer, floor, candidate } => {
            assert_eq!(consumer, member_consumer("m-offline", "tenant-a"));
            assert_eq!(floor, Some(sequence(3)));
            assert_eq!(candidate.get(), 4);
        }
        other => panic!("the refusal names the binding consumer, got {other:?}"),
    }
    assert_eq!(retention.retention_floor().get(), 0, "nothing was discarded");

    let advance = retention.advance_floor(sequence(3)).expect("the slowest floor is allowed");
    assert_eq!(advance.floor().get(), 3);
    assert_eq!(retention.slowest_consumer().expect("consumers are recorded").1, Some(sequence(3)));
}

#[test]
fn a_consumer_that_has_consumed_nothing_needs_all_of_the_evidence() {
    let mut retention = TombstoneRetention::new(None);
    retention.observe_consumer(member_consumer("m-new", "tenant-a"), None).expect_recorded();
    let refused = retention.advance_floor(sequence(1)).expect_err("no progress blocks advance");
    match refused {
        RetentionRefusal::ConsumerBelowFloor { floor, .. } => {
            assert_eq!(floor, None, "the consumer has consumed nothing");
        }
        other => panic!("the refusal is the consumer bound, got {other:?}"),
    }
}

#[test]
fn the_floor_cap_retains_evidence_unconditionally_above_it() {
    let mut retention = TombstoneRetention::new(Some(sequence(5)));
    retention
        .observe_consumer(member_consumer("m-a", "tenant-a"), Some(sequence(50)))
        .expect_recorded();
    let refused = retention.advance_floor(sequence(6)).expect_err("the cap is a lower bound");
    assert_eq!(
        refused,
        RetentionRefusal::FloorExceededCap { cap: sequence(5), candidate: sequence(6) }
    );
    retention.advance_floor(sequence(5)).expect("the cap itself is allowed");
    assert_eq!(retention.retention_floor().get(), 5);
}

#[test]
fn forgetting_a_consumer_releases_its_floor_but_never_lowers_a_need() {
    let mut retention = TombstoneRetention::new(None);
    retention
        .observe_consumer(member_consumer("m-gone", "tenant-a"), Some(sequence(1)))
        .expect_recorded();
    retention
        .observe_consumer(member_consumer("m-here", "tenant-a"), Some(sequence(7)))
        .expect_recorded();
    retention.advance_floor(sequence(2)).expect_err("the gone member still holds it");
    assert!(retention.forget_consumer(&member_consumer("m-gone", "tenant-a")));
    retention.advance_floor(sequence(7)).expect("the remaining consumer bounds the floor now");
    assert!(!retention.forget_consumer(&member_consumer("m-gone", "tenant-a")));
}

#[test]
fn a_consumer_floor_never_regresses() {
    let mut retention = TombstoneRetention::new(None);
    retention
        .observe_consumer(member_consumer("m-a", "tenant-a"), Some(sequence(6)))
        .expect_recorded();
    assert_eq!(
        retention.observe_consumer(member_consumer("m-a", "tenant-a"), Some(sequence(2))),
        ConsumerObservation::Regression
    );
    assert_eq!(
        retention.consumers(),
        vec![(member_consumer("m-a", "tenant-a"), Some(sequence(6)))],
        "the recorded floor stands"
    );
}

#[test]
fn advancing_the_floor_discards_only_the_evidence_below_it() {
    let mut retention = TombstoneRetention::new(None);
    retention
        .observe_consumer(member_consumer("m-a", "tenant-a"), Some(sequence(9)))
        .expect_recorded();
    let facts = vec![
        evidence("tenant-a", "doc-1", 1),
        evidence("tenant-a", "doc-2", 4),
        evidence("tenant-b", "doc-1", 2),
    ];
    let held = retention.hold(&facts);
    assert_eq!(held.held(), 3, "every fact at or above the floor is held");
    assert_eq!(held.below_floor_skipped(), 0, "the floor started at zero");
    assert_eq!(retention.held_evidence_count(), 3);
    let advance = retention.advance_floor(sequence(3)).expect("the consumer allows three");
    assert_eq!(advance.discarded(), 2, "only the facts below the floor went away");
    assert_eq!(retention.held_evidence_count(), 1);
    let served = retention.deletion_evidence_above(&tenant("tenant-a"), sequence(0));
    assert_eq!(served.len(), 1, "the below-floor facts are not served");
    assert_eq!(served[0].id().as_str(), "doc-2");
    // Evidence serving is per tenant: tenant-b's fact went with the floor.
    assert_eq!(retention.deletion_evidence_above(&tenant("tenant-b"), sequence(0)).len(), 0);
}

#[test]
fn one_key_keeps_only_its_newest_evidence() {
    let mut retention = TombstoneRetention::new(None);
    let held =
        retention.hold(&[evidence("tenant-a", "doc-1", 2), evidence("tenant-a", "doc-1", 6)]);
    assert_eq!(held.below_floor_skipped(), 0, "the floor never rose above zero");
    assert_eq!(retention.held_evidence_count(), 1, "a subsumed delete carries no new fact");
    let served = retention.deletion_evidence_above(&tenant("tenant-a"), sequence(0));
    assert_eq!(served[0].sequence().get(), 6);
}

#[test]
fn a_history_declaration_is_monotone_and_requires_durable_evidence() {
    let mut retention = TombstoneRetention::new(None);
    let refused = retention
        .declare_history_bound(sequence(4), sequence(4), false)
        .expect_err("the retained set is not durable");
    assert_eq!(refused, RetentionRefusal::EvidenceNotDurable { bound: sequence(4) });
    assert_eq!(retention.history_bound(), None, "the bound did not move");

    let declared = retention
        .declare_history_bound(sequence(4), sequence(4), true)
        .expect("an empty window declares cleanly");
    assert_eq!(declared.bound().get(), 4);
    assert_eq!(declared.held(), 0);
    assert_eq!(declared.below_floor_discarded(), 0);

    let regression = retention
        .declare_history_bound(sequence(2), sequence(4), true)
        .expect_err("the bound only rises");
    assert_eq!(
        regression,
        RetentionRefusal::BoundRegression { current: sequence(4), candidate: sequence(2) }
    );
    assert_eq!(retention.history_bound(), Some(sequence(4)));
}

#[test]
fn a_history_declaration_names_the_consumers_below_the_bound() {
    let mut retention = TombstoneRetention::new(None);
    retention
        .observe_consumer(member_consumer("m-offline", "tenant-a"), Some(sequence(1)))
        .expect_recorded();
    retention
        .observe_consumer(member_consumer("m-caught-up", "tenant-a"), Some(sequence(9)))
        .expect_recorded();
    let declared = retention
        .declare_history_bound(sequence(5), sequence(5), true)
        .expect("the declaration is accepted");
    assert_eq!(
        declared.consumers_below_bound(),
        &[member_consumer("m-offline", "tenant-a")],
        "the disclosure names who will need evidence or a state transfer"
    );
}

#[test]
fn a_declared_bound_may_not_exceed_the_journals_durable_head() {
    // Decision D2.4 at the watermark contract level: the declaration carries
    // the journal's durable head, and a bound past it is refused — the range
    // (head, bound) would be given up as if it were empty, and every
    // publication landing there afterwards would be neither servable from
    // the journal nor held as evidence.
    let mut retention = TombstoneRetention::new(None);
    let refused = retention
        .declare_history_bound(sequence(5), sequence(4), true)
        .expect_err("the window below five does not exist below a head of four");
    assert_eq!(
        refused,
        RetentionRefusal::BoundBeyondWindow { head: sequence(4), candidate: sequence(5) }
    );
    assert_eq!(retention.history_bound(), None, "the bound did not move");
    assert_eq!(retention.refusals().len(), 1, "the refusal is observable");
    // The journal's own head is the limit: a window ending at it fully
    // exists, and a bound one past a later head is still refused.
    retention
        .declare_history_bound(sequence(4), sequence(4), true)
        .expect("a window ending at the head fully exists");
    let refused = retention
        .declare_history_bound(sequence(6), sequence(5), true)
        .expect_err("six is past a head of five");
    assert_eq!(
        refused,
        RetentionRefusal::BoundBeyondWindow { head: sequence(5), candidate: sequence(6) }
    );
}

/// The store-backed declaration form (GAP-S1 D5) carries the same three
/// fail-closed gates as the windowed one, and merges the bound record's
/// non-deletion facts by max — never lowering a recorded fact. These rows
/// pin the form's own contract; the engine-level path that drives it in
/// production is covered by the data-bearing engine test in
/// `src/engine/tests.rs`.
#[test]
fn a_retained_declaration_is_gated_and_merges_non_deletion_facts_by_max() {
    // D2.4: the restored bound may not exceed the journal's durable head.
    let mut retention = TombstoneRetention::new(None);
    let refused = retention
        .declare_history_bound_retained(sequence(5), sequence(4), &[], true)
        .expect_err("the store-backed bound may not pass the journal's durable head");
    assert_eq!(
        refused,
        RetentionRefusal::BoundBeyondWindow { head: sequence(4), candidate: sequence(5) }
    );
    assert_eq!(retention.history_bound(), None, "the bound did not move");

    // D2.3: the restored evidence must be asserted durable.
    let refused = retention
        .declare_history_bound_retained(
            sequence(4),
            sequence(4),
            &[(tenant("tenant-a"), Some(sequence(2)))],
            false,
        )
        .expect_err("the store-backed declaration must assert durability");
    assert_eq!(refused, RetentionRefusal::EvidenceNotDurable { bound: sequence(4) });
    assert_eq!(retention.history_bound(), None, "the bound did not move");
    assert!(
        retention.non_deletion_through(&tenant("tenant-a")).is_none(),
        "a refused declaration records no non-deletion fact"
    );
    assert_eq!(retention.refusals().len(), 2, "each refusal is observable");

    // The accepted restoration records the bound and the store's facts.
    retention
        .declare_history_bound_retained(
            sequence(4),
            sequence(6),
            &[(tenant("tenant-a"), Some(sequence(2)))],
            true,
        )
        .expect("the store-backed declaration is accepted");
    assert_eq!(retention.history_bound(), Some(sequence(4)));
    assert_eq!(retention.non_deletion_through(&tenant("tenant-a")), Some(sequence(2)));

    // Monotone: a regressing restoration is refused and the bound stands.
    let regression = retention
        .declare_history_bound_retained(sequence(3), sequence(6), &[], true)
        .expect_err("the restored bound is monotone like the windowed one");
    assert_eq!(
        regression,
        RetentionRefusal::BoundRegression { current: sequence(4), candidate: sequence(3) }
    );
    assert_eq!(retention.history_bound(), Some(sequence(4)));

    // The non-deletion max-merge across later declarations: a seen value
    // never lowers a recorded one, a `None` row records the tenant without
    // claiming a put for it, and a higher seen value raises the fact.
    retention
        .declare_history_bound_retained(
            sequence(5),
            sequence(6),
            &[(tenant("tenant-b"), Some(sequence(3)))],
            true,
        )
        .expect("a rising restoration is accepted");
    retention
        .declare_history_bound_retained(
            sequence(6),
            sequence(6),
            &[
                (tenant("tenant-a"), Some(sequence(1))),
                (tenant("tenant-b"), None),
                (tenant("tenant-c"), Some(sequence(9))),
            ],
            true,
        )
        .expect("the later restoration is accepted");
    assert_eq!(
        retention.non_deletion_through(&tenant("tenant-a")),
        Some(sequence(2)),
        "a lower seen sequence never lowers the recorded fact"
    );
    assert_eq!(
        retention.non_deletion_through(&tenant("tenant-b")),
        Some(sequence(3)),
        "a None row never erases a recorded put"
    );
    assert_eq!(
        retention.non_deletion_through(&tenant("tenant-c")),
        Some(sequence(9)),
        "a higher seen sequence raises the recorded fact"
    );
    assert_eq!(retention.history_bound(), Some(sequence(6)));
}

#[test]
fn holding_discloses_the_facts_dropped_below_the_floor() {
    // The same disclosure discipline as advance_floor and declare: a fact
    // the floor discards is counted, never silently dropped.
    let mut retention = TombstoneRetention::new(None);
    retention
        .observe_consumer(member_consumer("m-a", "tenant-a"), Some(sequence(9)))
        .expect_recorded();
    retention.advance_floor(sequence(3)).expect("the floor rises first");
    let held = retention.hold(&[
        evidence("tenant-a", "doc-kept", 4),
        evidence("tenant-a", "doc-old", 1),
        evidence("tenant-b", "doc-old", 2),
    ]);
    assert_eq!(held.held(), 1, "only the above-floor fact is held");
    assert_eq!(
        held.below_floor_skipped(),
        2,
        "the below-floor drops are a disclosed count, never a silent drop"
    );
    assert_eq!(retention.held_evidence_count(), 1);
}

/// One retained fact for the contract fixtures: a delete of `id` at
/// `sequence`.
fn evidence(tenant_name: &str, id: &str, at: u64) -> DeletionEvidence {
    DeletionEvidence::new(
        tenant(tenant_name),
        None,
        crate::types::DocumentId::try_new(id).expect("valid identifier"),
        1,
        sequence(at),
        crate::types::OperationId::try_new(&format!("op-{tenant_name}-{id}-{at}"))
            .expect("valid identifier"),
        crate::replication::ReplicationClockStamp::new(at, 0),
        crate::types::LocalDurability::Synced,
    )
}

/// The two-member fixture of the catch-up tests: one owner, one follower.
pub(super) fn fixture(
    tag: &str,
) -> (Vec<std::path::PathBuf>, ReplicationMember, ReplicationMember) {
    let topology = crate::replication::tests::two_member_topology("m-src", "m-dst", "main");
    let (source_dir, source) = crate::replication::tests::open_member(tag, "m-src", &topology);
    let (follower_dir, follower) = crate::replication::tests::open_member(tag, "m-dst", &topology);
    (vec![source_dir, follower_dir], source, follower)
}

#[test]
fn an_offline_member_converges_across_a_declared_history_boundary() {
    let (dirs, mut source, mut follower) = fixture("offline-converges");
    let mut network = crate::replication::transport::ReplicationNetwork::new();

    // The follower is caught up through the first two publications.
    crate::replication::tests::member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    let second = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-2",
        "doc-2",
        "v2",
    );
    let report = crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    assert!(report.caught_up(), "the follower starts caught up: {report:?}");
    assert_eq!(follower.advisory_progress(&tenant("tenant-a")), Some(second));

    // The follower goes offline; the source deletes a document and writes
    // a new one, then gives up the history below the delete.
    let delete_at =
        crate::replication::tests::member_delete(&mut source, "tenant-a", "main", "op-3", "doc-1");
    let put_at = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-4",
        "doc-3",
        "v3",
    );
    assert!(delete_at > second, "the delete is above the follower's cursor");
    // The bound sits above the delete: everything below it is the window
    // the declaration extracts from.
    source
        .declare_history_bound(put_at, true)
        .expect("the window's evidence is extracted from the journal");
    assert_eq!(source.tombstone_retention().history_bound(), Some(put_at), "the bound is declared");
    assert_eq!(
        source.tombstone_retention().held_evidence_count(),
        1,
        "the delete of doc-1 is retained"
    );
    assert_eq!(
        source.tombstone_retention().non_deletion_through(&tenant("tenant-a")),
        Some(second),
        "the newest put below the bound is the one the follower already has"
    );

    // The follower's cursor is below the effective bound, so the journal
    // alone cannot serve it — the retained evidence does.
    assert_eq!(source.effective_servable_start().expect("engine open"), Some(put_at));
    let _report = crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    assert_eq!(
        source.evidence_servings().len(),
        1,
        "the source served the retained evidence: {:?}",
        source.evidence_servings()
    );
    assert_eq!(source.evidence_servings()[0].through(), put_at);
    assert_eq!(source.evidence_servings()[0].requester(), follower.id());
    assert!(follower.fallbacks().is_empty(), "no fallback to a full scan was needed");

    // The deleted document did not resurrect and the follower converged.
    let documents = crate::replication::tests::documents_of(&follower, "tenant-a");
    let ids = documents.iter().map(|document| document.id().as_str()).collect::<Vec<_>>();
    assert_eq!(ids, vec!["doc-2", "doc-3"], "doc-1 stays deleted, doc-3 arrives");
    assert_eq!(
        follower.advisory_progress(&tenant("tenant-a")),
        Some(put_at),
        "the follower resumed incremental service at the bound"
    );

    let report = crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    assert!(report.caught_up(), "a further exchange is a no-op: {report:?}");

    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

#[test]
fn a_put_below_the_declared_bound_still_fails_closed() {
    let (dirs, mut source, mut follower) = fixture("put-missed");
    let mut network = crate::replication::transport::ReplicationNetwork::new();

    let first = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-1",
        "doc-1",
        "v1",
    );
    let report = crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    assert!(report.caught_up());
    assert_eq!(follower.advisory_progress(&tenant("tenant-a")), Some(first));

    // While the follower is offline, the source writes a put the follower
    // will miss and then deletes it — the put below the bound is the fact
    // evidence alone cannot reconstruct.
    let missed_put = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-2",
        "doc-2",
        "v2",
    );
    let delete_at =
        crate::replication::tests::member_delete(&mut source, "tenant-a", "main", "op-3", "doc-2");
    source.declare_history_bound(delete_at, true).expect("the window is declared");
    assert_eq!(
        source.tombstone_retention().non_deletion_through(&tenant("tenant-a")),
        Some(missed_put),
        "the missed put is named"
    );

    follower
        .request_catch_up(&mut network, source.id(), &scope_of(&follower, "tenant-a"))
        .expect("request sent");
    network.pump();
    source
        .serve_catch_up(&mut network, &scope_of(&source, "tenant-a"))
        .expect("the source answers");
    network.pump();
    // The first answer names the follower's own cursor, so the follower
    // drops its advisory progress and re-requests a full scan — and the
    // full scan is what the declared bound makes unservable.
    follower
        .apply_deliveries(&mut network, &scope_of(&follower, "tenant-a"))
        .expect("the flagged batch falls back instead of failing");
    assert_eq!(follower.fallbacks().len(), 1, "the fallback is observable");
    assert_eq!(follower.advisory_progress(&tenant("tenant-a")), None);
    follower
        .request_catch_up(&mut network, source.id(), &scope_of(&follower, "tenant-a"))
        .expect("the fallback re-request is sent");
    network.pump();
    source
        .serve_catch_up(&mut network, &scope_of(&source, "tenant-a"))
        .expect("the source answers the full scan");
    network.pump();
    let error = follower
        .apply_deliveries(&mut network, &scope_of(&follower, "tenant-a"))
        .expect_err("a missed put is a typed gap, never a silent skip");
    match error {
        MemberError::GapUnservable { cursor, servable_from, .. } => {
            assert_eq!(cursor.get(), 0, "a full scan has no cursor to serve below");
            assert_eq!(servable_from, delete_at, "the bound is the declared one");
        }
        other => panic!("the failure is the typed gap, got {other:?}"),
    }
    assert!(
        crate::replication::tests::documents_of(&follower, "tenant-a")
            .iter()
            .all(|document| document.id().as_str() != "doc-2"),
        "the missed put's delete did not resurrect anything"
    );
    // No evidence was served: the source did not pretend the window was
    // pure deletes.
    assert!(source.evidence_servings().is_empty());
    assert_eq!(
        follower.advisory_progress(&tenant("tenant-a")),
        None,
        "the follower's cursor was dropped by the fallback and never advanced"
    );

    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

#[test]
fn the_watermark_holds_history_for_a_seated_but_silent_member() {
    let (dirs, mut source, mut follower) = fixture("holds-for-offline");
    let mut network = crate::replication::transport::ReplicationNetwork::new();

    crate::replication::tests::member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    let second = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-2",
        "doc-2",
        "v2",
    );

    // The follower has never requested anything: seeding registers it with
    // no floor, so it holds the watermark.
    source.seed_retention_consumers().expect("the engine is open");
    let refusal = source
        .advance_tombstone_retention(second)
        .expect_err("a seated member that never synced needs everything");
    match refusal {
        RetentionRefusal::ConsumerBelowFloor { consumer, floor, .. } => {
            assert_eq!(consumer, member_consumer("m-dst", "tenant-a"));
            assert_eq!(floor, None);
        }
        other => panic!("the refusal names the offline member, got {other:?}"),
    }

    // Once the follower catches up and asks again from its new cursor, its
    // floor rises and the watermark may advance.
    let report = crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    assert!(report.caught_up());
    crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    source.seed_retention_consumers().expect("the engine is open");
    source
        .advance_tombstone_retention(second)
        .expect("the caught-up member no longer holds the watermark");
    assert_eq!(source.tombstone_retention().retention_floor().get(), second.get());

    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

#[test]
fn a_backup_chain_anchor_holds_the_watermark_until_the_chain_moves() {
    let (_dir, mut source, _follower) = fixture("chain-anchor");
    let anchor = sequence(4);
    source
        .observe_retention_consumer(
            RetentionConsumer::BackupChain { identity: "rp-chain-base".to_owned() },
            Some(anchor),
        )
        .expect_recorded();
    source
        .advance_tombstone_retention(sequence(5))
        .expect_err("the chain's base still needs evidence above four");
    source.advance_tombstone_retention(anchor).expect("the chain's own boundary is allowed");
    assert_eq!(source.tombstone_retention().retention_floor().get(), anchor.get());
}

#[test]
fn delivered_evidence_applies_once_per_operation_identity() {
    use crate::replication::transport::{FrameSelector, ReplicationFrame};

    let (dirs, mut source, mut follower) = fixture("evidence-idempotent");
    let mut network = crate::replication::transport::ReplicationNetwork::new();

    let first = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-1",
        "doc-1",
        "v1",
    );
    crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    let delete_at =
        crate::replication::tests::member_delete(&mut source, "tenant-a", "main", "op-2", "doc-1");
    let put_at = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-3",
        "doc-2",
        "v2",
    );
    source.declare_history_bound(put_at, true).expect("the window is declared");
    let evidence = source.tombstone_retention().deletion_evidence_above(&tenant("tenant-a"), first);
    assert_eq!(evidence.len(), 1, "the delete below the bound is retained");

    // The same evidence frame delivered twice: the second delivery must
    // resolve through the durable operation ledger, not delete twice or
    // fail — at-least-once delivery is the transport's contract.
    for round in 0..2 {
        network
            .send(
                source.id(),
                follower.id(),
                ReplicationFrame::DeletionEvidenceBatch {
                    source: source.id().clone(),
                    tenant: tenant("tenant-a"),
                    evidence: evidence.clone(),
                    servable_from: put_at,
                    retention_floor: source.tombstone_retention().retention_floor(),
                    non_deletion_through: source
                        .tombstone_retention()
                        .non_deletion_through(&tenant("tenant-a")),
                },
            )
            .expect("the bounded link accepts the evidence frame");
        let report = follower
            .apply_deliveries(&mut network, &scope_of(&follower, "tenant-a"))
            .expect("round {round} applies");
        if round == 0 {
            assert_eq!(report.applied(), 1, "the first delivery applies the delete");
        } else {
            assert_eq!(
                report.duplicates_resolved(),
                1,
                "the duplicate resolves by operation identity"
            );
            assert_eq!(report.applied(), 0, "nothing applies twice");
        }
    }

    let documents = crate::replication::tests::documents_of(&follower, "tenant-a");
    assert!(
        documents.is_empty(),
        "the deleted document is gone and nothing resurrected: {documents:?}"
    );
    assert_eq!(
        follower.advisory_progress(&tenant("tenant-a")),
        Some(delete_at.max(first)),
        "the cursor rests at the bound"
    );
    let _ = network.receive(follower.id(), FrameSelector::Deliveries);

    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

#[test]
fn a_history_declaration_whose_window_cannot_be_extracted_fails_closed() {
    let (dirs, mut source, _follower) = fixture("extraction-fails");
    crate::replication::tests::member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    let delete_at =
        crate::replication::tests::member_delete(&mut source, "tenant-a", "main", "op-2", "doc-1");
    // The bound sits above the delete, so the declaration's window is the
    // one the delete's evidence is extracted from.
    let bound = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-3",
        "doc-2",
        "v2",
    );
    assert!(bound > delete_at);
    source.declare_history_bound(bound, true).expect("the open engine declares cleanly");
    assert_eq!(source.tombstone_retention().held_evidence_count(), 1);

    // The engine goes away before the next declaration: the window cannot
    // be extracted, and the declaration is refused rather than proceeding
    // with an empty extraction — an unreadable window is not an empty one,
    // or history would be given up whose evidence was never held.
    source.close();
    let higher = crate::types::StorageSequence::new(bound.get() + 1);
    let error = source
        .declare_history_bound(higher, true)
        .expect_err("the declaration is refused without an engine");
    assert_eq!(
        error,
        crate::replication::member::DeclareBoundError::Engine(MemberError::EngineClosed),
        "the failure is the engine's, not a watermark refusal"
    );
    assert_eq!(
        source.tombstone_retention().history_bound(),
        Some(bound),
        "the bound did not move on a failed extraction"
    );
    assert_eq!(
        source.tombstone_retention().held_evidence_count(),
        1,
        "the evidence extracted before the failure is still held"
    );

    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

#[test]
fn a_member_refuses_a_declared_bound_past_its_journals_durable_head() {
    // Decision D2.4: a declared bound may not exceed the journal's durable
    // head — the window must fully exist. The extraction used to stop
    // silently at the journal's end and the declaration proceeded with a
    // window that never covered (head, bound), giving that range up as if
    // it were empty.
    let (dirs, mut source, _follower) = fixture("over-range-bound");
    crate::replication::tests::member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    let head = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-2",
        "doc-2",
        "v2",
    );
    for over in [1, 5] {
        let candidate = StorageSequence::new(head.get() + over);
        let error = source
            .declare_history_bound(candidate, true)
            .expect_err("the window below the candidate does not fully exist");
        assert_eq!(
            error,
            crate::replication::member::DeclareBoundError::Refusal(
                RetentionRefusal::BoundBeyondWindow { head, candidate }
            ),
            "head+{over} is past the journal's durable head"
        );
    }
    assert_eq!(source.tombstone_retention().history_bound(), None, "no bound moved");
    assert_eq!(
        source.tombstone_retention().refusals().len(),
        2,
        "each refusal is recorded in the bounded observation log"
    );

    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

#[test]
fn an_over_range_bound_refuses_instead_of_black_holing_the_window() {
    // The warden's black-hole repro: a bound declared at head+5 used to be
    // accepted with the extraction window silently truncated at the
    // journal's end. The follower's cursor then jumped to bound-1 on the
    // evidence path, a publication inside (head, bound) was served by
    // neither the journal nor the retained evidence, and every later
    // exchange reported caught up while the follower had silently
    // diverged. The refusal is the fix.
    let (dirs, mut source, mut follower) = fixture("black-hole-window");
    let mut network = crate::replication::transport::ReplicationNetwork::new();

    crate::replication::tests::member_put(&mut source, "tenant-a", "main", "op-1", "doc-1", "v1");
    let head = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-2",
        "doc-2",
        "v2",
    );
    let report = crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    assert!(report.caught_up(), "the follower starts caught up: {report:?}");
    assert_eq!(follower.advisory_progress(&tenant("tenant-a")), Some(head));

    // The over-range declaration is refused (D2.4), so no bound exists and
    // the journal remains the whole truth.
    let phantom = StorageSequence::new(head.get() + 5);
    let error = source
        .declare_history_bound(phantom, true)
        .expect_err("the window below the phantom bound does not fully exist");
    assert_eq!(
        error,
        crate::replication::member::DeclareBoundError::Refusal(
            RetentionRefusal::BoundBeyondWindow { head, candidate: phantom }
        ),
        "the phantom bound is refused"
    );

    // A publication lands inside the range the phantom bound would have
    // given up. Because the declaration was refused, the journal still
    // serves it and the follower converges instead of silently missing it.
    let put_at = crate::replication::tests::member_put(
        &mut source,
        "tenant-a",
        "main",
        "op-3",
        "doc-3",
        "v3",
    );
    assert!(put_at.get() < phantom.get(), "the put is inside the refused window");
    let report = crate::replication::tests::exchange_once(
        &mut network,
        &mut source,
        "tenant-a",
        &mut follower,
        "tenant-a",
    );
    assert!(report.caught_up(), "the follower catches up: {report:?}");
    let documents = crate::replication::tests::documents_of(&follower, "tenant-a");
    let ids = documents.iter().map(|document| document.id().as_str()).collect::<Vec<_>>();
    assert_eq!(ids, vec!["doc-1", "doc-2", "doc-3"], "nothing was black-holed");
    assert_eq!(follower.advisory_progress(&tenant("tenant-a")), Some(put_at));

    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

/// The caller-supplied scope of one member and tenant.
fn scope_of(member: &ReplicationMember, tenant_name: &str) -> crate::types::TenantScope {
    crate::types::TenantScope::from_parts(
        tenant(tenant_name),
        member.engine().expect("open").identity(),
    )
}

/// The observation assertions used above, spelled as extension methods so
/// the fixtures read as facts rather than matches.
trait ConsumerObservationExt {
    fn expect_recorded(self);
}

impl ConsumerObservationExt for ConsumerObservation {
    fn expect_recorded(self) {
        assert_eq!(self, ConsumerObservation::Recorded, "the observation is recorded");
    }
}
