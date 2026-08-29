//! Convergence proofs of the leaderless multi-writer core over the
//! native `TerraBase` CRDTs — per-field LWW registers joined over dotted
//! version vectors and causal contexts: the frozen contract's §8
//! scenarios 6 (concurrent same-document writes through different
//! Members), 11 (delayed, reordered, duplicated delivery), 12's data
//! plane (rejoin catch-up), and 13 (delete-wins with the loser in
//! shadow history).
//!
//! Deterministic convergence is structural here: the materialized
//! state is a pure function of the SET of operations held, so
//! reordering, duplication, and retry cannot change it.

use std::collections::BTreeMap;

use terrabase_causal::{ActorId, Dot, OriginCounter};
use terrabase_replication_core::{
    ReplicaDoc, ReplicaExchange, ReplicaFrontier, ReplicaScope, ReplicaValue, actor_from_peer,
};

/// Peer identities standing in for Member identities.
const MEMBER_A: u64 = 0x11;
const MEMBER_B: u64 = 0x22;
const MEMBER_C: u64 = 0x33;

/// The injected physical times, in Unix milliseconds: fixed constants
/// so the proofs never depend on wall-clock luck.
const T0: u64 = 10_000;
const T1: u64 = 20_000;

fn replica(peer: u64) -> ReplicaDoc {
    ReplicaDoc::new(actor_from_peer(peer))
}

fn actor(peer: u64) -> ActorId {
    actor_from_peer(peer)
}

fn body(status: &str) -> BTreeMap<String, ReplicaValue> {
    BTreeMap::from([("status".to_owned(), ReplicaValue::Text(status.to_owned()))])
}

/// One field of one document of the materialized scope.
fn field_of(scope: &ReplicaScope, document: &str, field: &str) -> Option<ReplicaValue> {
    scope.get(document).and_then(|body| body.get(field)).cloned()
}

/// Whether the materialized scope still holds one document.
fn holds(scope: &ReplicaScope, document: &str) -> bool {
    scope.contains_key(document)
}

/// The dot of one actor's latest own operation, by its contiguous end.
fn own_dot(scope_frontier: &ReplicaFrontier, peer: u64) -> Dot {
    let (peer_actor, through) = scope_frontier
        .ends()
        .into_iter()
        .find(|(held, _)| *held == actor(peer))
        .expect("the peer recorded its operation");
    let counter = OriginCounter::new(through).expect("the recorded counter is nonzero");
    Dot::new(peer_actor, counter)
}

/// Unwraps one exchange into its delta bytes; the tests that use it
/// never cross a retained floor.
fn delta_bytes(exchange: ReplicaExchange) -> Vec<u8> {
    match exchange {
        ReplicaExchange::Delta(bytes) => bytes,
        other => panic!("expected a delta batch, got {other:?}"),
    }
}

/// Exchanges deltas both ways until both frontiers cover each other —
/// the anti-entropy loop the transport drives in production, including
/// the snapshot fallback for a peer behind a retained floor.
fn sync_pair(first: &mut ReplicaDoc, second: &mut ReplicaDoc) {
    loop {
        let first_frontier = first.frontier();
        let second_frontier = second.frontier();
        if first_frontier.covers(&second_frontier) && second_frontier.covers(&first_frontier) {
            return;
        }
        match first.delta_for(&second_frontier).expect("exports") {
            ReplicaExchange::UpToDate => {}
            ReplicaExchange::Delta(bytes) => second.absorb(&bytes).expect("second absorbs"),
            ReplicaExchange::BehindRetainedFloor { .. } => adopt(first, second),
        }
        match second.delta_for(&first_frontier).expect("exports") {
            ReplicaExchange::UpToDate => {}
            ReplicaExchange::Delta(bytes) => first.absorb(&bytes).expect("first absorbs"),
            ReplicaExchange::BehindRetainedFloor { .. } => adopt(second, first),
        }
    }
}

/// The behind-floor fallback: `behind` adopts `source`'s snapshot as
/// its state. Safe while the behind peer holds no operations of its
/// own — a peer with own ops heals through a non-compacted peer
/// instead, and the compaction guard lands with the transport wiring.
fn adopt(source: &ReplicaDoc, behind: &mut ReplicaDoc) {
    let own = behind.export_own_ops().expect("exports own ops");
    assert!(own.is_empty(), "adoption would drop the peer's own operations");
    let snapshot = source.snapshot_bytes().expect("snapshots");
    let peer = behind.actor();
    *behind = ReplicaDoc::from_snapshot(peer, &snapshot).expect("adopts");
}

#[test]
fn concurrent_same_document_writes_merge_to_one_deterministic_winner() {
    // Two Members accept concurrent writes to the same document during
    // a partition: neither sees the other's operation.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    member_a.apply_put(T0, "order-1", &body("accepted-by-a")).expect("a writes");
    member_b.apply_put(T0, "order-1", &body("accepted-by-b")).expect("b writes");
    let a_frontier_before = member_a.frontier();
    let b_frontier_before = member_b.frontier();

    // The partition heals; anti-entropy exchanges deltas both ways.
    sync_pair(&mut member_a, &mut member_b);

    // Identical materialized state on both replicas (scenario 6), with
    // one scalar winner — not a conflict marker, not a split state.
    assert_eq!(member_a.materialized(), member_b.materialized());
    let winner_a = field_of(&member_a.materialized(), "order-1", "status").expect("a winner");
    let winner_b = field_of(&member_b.materialized(), "order-1", "status").expect("b winner");
    assert_eq!(winner_a, winner_b);
    assert!(
        winner_a == ReplicaValue::Text("accepted-by-a".to_owned())
            || winner_a == ReplicaValue::Text("accepted-by-b".to_owned()),
        "the winner is one of the concurrent writes: {winner_a:?}"
    );

    // The loser stays recoverable from history: BOTH concurrent writes
    // remain readable at their own operations after the merge — the
    // shadow history, whichever write won the materialized state.
    let a_dot = own_dot(&a_frontier_before, MEMBER_A);
    let b_dot = own_dot(&b_frontier_before, MEMBER_B);
    let shadow_a = member_a.value_after(a_dot).expect("history serves a's write");
    assert_eq!(
        field_of(&shadow_a, "order-1", "status"),
        Some(ReplicaValue::Text("accepted-by-a".to_owned())),
        "a's write stays readable from history after the merge"
    );
    let shadow_b = member_b.value_after(b_dot).expect("history serves b's write");
    assert_eq!(
        field_of(&shadow_b, "order-1", "status"),
        Some(ReplicaValue::Text("accepted-by-b".to_owned())),
        "the losing write stays readable from history after the merge"
    );
}

#[test]
fn the_merge_winner_is_deterministic_across_independent_runs() {
    // The same concurrent pair merges to the same winner when the
    // delivery order is reversed on independent replicas: the outcome
    // is a property of the operations, not of arrival order.
    let run = |first_delivery: u64| -> ReplicaValue {
        let mut member_a = replica(MEMBER_A);
        let mut member_b = replica(MEMBER_B);
        member_a.apply_put(T0, "order-1", &body("accepted-by-a")).expect("a writes");
        member_b.apply_put(T0, "order-1", &body("accepted-by-b")).expect("b writes");
        if first_delivery == MEMBER_A {
            let bytes =
                delta_bytes(member_a.delta_for(&ReplicaFrontier::default()).expect("exports"));
            member_b.absorb(&bytes).expect("b absorbs");
            let bytes = delta_bytes(member_b.delta_for(&member_a.frontier()).expect("exports"));
            member_a.absorb(&bytes).expect("a absorbs");
        } else {
            let bytes =
                delta_bytes(member_b.delta_for(&ReplicaFrontier::default()).expect("exports"));
            member_a.absorb(&bytes).expect("a absorbs");
            let bytes = delta_bytes(member_a.delta_for(&member_b.frontier()).expect("exports"));
            member_b.absorb(&bytes).expect("b absorbs");
        }
        assert_eq!(member_a.materialized(), member_b.materialized());
        field_of(&member_a.materialized(), "order-1", "status").expect("winner")
    };
    assert_eq!(run(MEMBER_A), run(MEMBER_B), "delivery order does not choose the winner");
}

#[test]
fn delete_wins_over_a_concurrent_update() {
    // Scenario 13: one Member deletes while another updates; the merge
    // materializes the document deleted everywhere, the losing update
    // stays in shadow history.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    member_a.apply_put(T0, "order-1", &body("original")).expect("a seeds");
    sync_pair(&mut member_a, &mut member_b);

    // Concurrent divergence: b updates, a deletes — at the SAME
    // injected time, so the outcome cannot be an artifact of clock
    // skew: the tombstone layer decides, not the timestamp.
    member_b.apply_put(T1, "order-1", &body("updated-by-b")).expect("b updates");
    member_a.apply_delete(T1, "order-1").expect("a deletes");
    assert!(holds(&member_b.materialized(), "order-1"), "b still holds the document");
    assert!(!holds(&member_a.materialized(), "order-1"), "a already deleted it");

    sync_pair(&mut member_a, &mut member_b);

    assert!(!holds(&member_a.materialized(), "order-1"), "delete wins on the deleting member");
    assert!(!holds(&member_b.materialized(), "order-1"), "delete wins on the updating member too");
    assert_eq!(member_a.materialized(), member_b.materialized());
}

#[test]
fn an_update_that_observed_the_delete_resurrects_the_document() {
    // The mirror of scenario 13: a put whose causal context contains
    // the tombstone (the writer had seen the delete) re-creates the
    // document on every replica.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    member_a.apply_put(T0, "order-1", &body("original")).expect("a seeds");
    sync_pair(&mut member_a, &mut member_b);
    member_a.apply_delete(T1, "order-1").expect("a deletes");
    sync_pair(&mut member_a, &mut member_b);
    assert!(!holds(&member_b.materialized(), "order-1"), "the delete spread");

    // b has now observed the delete: its next put resurrects.
    member_b.apply_put(T1 + 1, "order-1", &body("reborn")).expect("b resurrects");
    sync_pair(&mut member_a, &mut member_b);
    assert_eq!(
        field_of(&member_a.materialized(), "order-1", "status"),
        Some(ReplicaValue::Text("reborn".to_owned())),
        "the resurrection reaches the deleting member"
    );
    assert_eq!(member_a.materialized(), member_b.materialized());
}

#[test]
fn duplicated_and_reordered_delivery_converges_idempotently() {
    // Scenario 11: the same delta arrives twice, and a later delta
    // arrives before an earlier one; the replicas still converge with
    // no duplicated effect.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    member_a.apply_put(T0, "order-1", &body("one")).expect("a writes one");
    let first = delta_bytes(member_a.delta_for(&ReplicaFrontier::default()).expect("exports"));
    member_a.apply_put(T0, "order-2", &body("two")).expect("a writes two");
    let second = delta_bytes(
        member_a
            .delta_for(&ReplicaFrontier::from_ends(vec![(actor(MEMBER_A), 1)]))
            .expect("exports"),
    );

    // Reordered: the second window arrives before the first.
    member_b.absorb(&second).expect("b absorbs the later window first");
    member_b.absorb(&first).expect("b absorbs the earlier window");
    // Duplicated: the first window arrives again.
    member_b.absorb(&first).expect("duplicate delivery is a no-op");

    assert_eq!(member_a.materialized(), member_b.materialized());
    assert_eq!(
        field_of(&member_b.materialized(), "order-1", "status"),
        Some(ReplicaValue::Text("one".to_owned())),
        "no duplicated materialization"
    );
    assert_eq!(
        field_of(&member_b.materialized(), "order-2", "status"),
        Some(ReplicaValue::Text("two".to_owned()))
    );
    assert!(member_b.frontier().covers(&member_a.frontier()));
}

#[test]
fn a_partitioned_member_catches_up_after_rejoin() {
    // Scenario 12's data plane: member c misses everything during the
    // partition; after rejoin one delta exchange brings it to the
    // merged state.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    let mut member_c = replica(MEMBER_C);

    member_a.apply_put(T0, "order-1", &body("from-a")).expect("a writes");
    member_b.apply_put(T0, "order-2", &body("from-b")).expect("b writes");
    sync_pair(&mut member_a, &mut member_b);

    // c rejoins: its empty frontier does not cover the merged state, so
    // the exchange must carry everything.
    assert!(!member_c.frontier().covers(&member_a.frontier()));
    sync_pair(&mut member_c, &mut member_a);
    sync_pair(&mut member_c, &mut member_b);

    assert_eq!(member_c.materialized(), member_a.materialized());
    assert_eq!(member_c.materialized(), member_b.materialized());
    assert_eq!(
        field_of(&member_c.materialized(), "order-2", "status"),
        Some(ReplicaValue::Text("from-b".to_owned()))
    );
    // c's frontier now names every peer it absorbed.
    let frontier = member_c.frontier();
    let ends = frontier.ends();
    assert!(ends.iter().any(|(held, _)| *held == actor(MEMBER_A)), "c names a's operations");
    assert!(ends.iter().any(|(held, _)| *held == actor(MEMBER_B)), "c names b's operations");
}

#[test]
fn covered_frontiers_answer_up_to_date_without_exporting() {
    let mut member_a = replica(MEMBER_A);
    member_a.apply_put(T0, "order-1", &body("one")).expect("a writes");
    let frontier = member_a.frontier();

    assert_eq!(member_a.delta_for(&frontier), Ok(ReplicaExchange::UpToDate));
    let behind = ReplicaFrontier::from_ends(vec![(actor(MEMBER_A), frontier.ends()[0].1 - 1)]);
    assert!(
        matches!(member_a.delta_for(&behind), Ok(ReplicaExchange::Delta(_))),
        "a missing counter exports a delta"
    );
    let foreign = ReplicaFrontier::from_ends(vec![(actor(MEMBER_B), 5)]);
    assert!(
        matches!(member_a.delta_for(&foreign), Ok(ReplicaExchange::Delta(_))),
        "an unknown peer's frontier exports everything held"
    );
}

#[test]
fn compaction_raises_the_retained_floor() {
    let mut member = replica(MEMBER_A);
    member.apply_put(T0, "order-1", &body("one")).expect("writes");
    assert!(member.retained_floor().ends().is_empty(), "no floor before compaction");
    member.compact_history().expect("compacts");
    assert!(!member.retained_floor().ends().is_empty(), "the floor is the compacted frontier");
    // An up-to-date peer still answers UpToDate.
    let now = member.frontier();
    assert_eq!(member.delta_for(&now), Ok(ReplicaExchange::UpToDate));
    // A peer behind the floor cannot be served by delta: the answer
    // names the floor for the snapshot fallback.
    assert!(matches!(
        member.delta_for(&ReplicaFrontier::default()),
        Ok(ReplicaExchange::BehindRetainedFloor { .. })
    ));
    // The merged state survives the compaction.
    assert_eq!(
        field_of(&member.materialized(), "order-1", "status"),
        Some(ReplicaValue::Text("one".to_owned()))
    );
    // The collected operation no longer answers a shadow read: history
    // below the floor is gone, exactly as the floor contract says.
    let collected = own_dot(&now, MEMBER_A);
    assert_eq!(
        member.value_after(collected),
        Err(terrabase_replication_core::ReplicaError::UnknownVersion)
    );
}

#[test]
fn a_fresh_peer_adopts_a_compacted_snapshot() {
    // The rejoin shape the fallback serves: a Member that holds no
    // operations of its own sits behind a compacted peer's floor and
    // adopts its snapshot. (A peer WITH own ops must heal through a
    // non-compacted peer — the compaction guard lands with the
    // transport wiring; the composed test pins the typed refusal.)
    let mut holder = replica(MEMBER_A);
    holder.apply_put(T0, "order-1", &body("from-a")).expect("writes");
    holder.compact_history().expect("compacts");
    let floor = holder.retained_floor();

    let mut fresh = replica(MEMBER_B);
    assert!(
        matches!(
            holder.delta_for(&fresh.frontier()),
            Ok(ReplicaExchange::BehindRetainedFloor { .. })
        ),
        "a fresh peer sits behind the compacted floor {:?}",
        floor.ends()
    );
    adopt(&holder, &mut fresh);

    // The adopted state is the merged state, and the pair is converged.
    assert_eq!(fresh.materialized(), holder.materialized());
    assert_eq!(
        field_of(&fresh.materialized(), "order-1", "status"),
        Some(ReplicaValue::Text("from-a".to_owned()))
    );
    sync_pair(&mut fresh, &mut holder);
    assert_eq!(fresh.frontier(), holder.frontier());
}

#[test]
fn structured_field_values_round_trip_through_the_registers() {
    // The canonical text form carries structured values exactly:
    // arrays, objects, integers, floats (by bit pattern), booleans,
    // and null all merge and materialize identically on both sides.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    let mut nested = BTreeMap::new();
    nested.insert("inner".to_owned(), ReplicaValue::Integer(-7));
    let body = BTreeMap::from([
        ("text".to_owned(), ReplicaValue::Text("s3:yes:not-a-length".to_owned())),
        ("flag".to_owned(), ReplicaValue::Boolean(true)),
        ("count".to_owned(), ReplicaValue::Integer(i64::MIN)),
        ("ratio".to_owned(), ReplicaValue::number(0.25)),
        ("nothing".to_owned(), ReplicaValue::Null),
        (
            "list".to_owned(),
            ReplicaValue::Array(vec![
                ReplicaValue::number(f64::NAN),
                ReplicaValue::Text("x".to_owned()),
            ]),
        ),
        ("map".to_owned(), ReplicaValue::Object(nested)),
    ]);
    member_a.apply_put(T0, "order-1", &body).expect("a writes");

    let bytes = delta_bytes(member_a.delta_for(&ReplicaFrontier::default()).expect("exports"));
    member_b.absorb(&bytes).expect("b absorbs");

    assert_eq!(member_a.materialized(), member_b.materialized());
    let merged = member_b.materialized();
    let body_back = merged.get("order-1").expect("the document merged");
    assert_eq!(body_back.get("ratio"), Some(&ReplicaValue::number(0.25)));
    let ReplicaValue::Array(items) = body_back.get("list").expect("the list merged") else {
        panic!("the list stays structured");
    };
    assert_eq!(items[0].as_f64().map(f64::is_nan), Some(true), "even NaN survives by bits");
}
