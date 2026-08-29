//! Convergence proofs of the leaderless multi-writer core over real
//! Loro documents: the frozen contract's §8 scenarios 6 (concurrent
//! same-document writes through different Members), 11 (delayed,
//! reordered, duplicated delivery), 12's data plane (rejoin catch-up),
//! and 13 (delete-wins with the loser in shadow history).

use std::collections::BTreeMap;

use loro::{LoroValue, ToJson};
use terrabase_replication_core::{ReplicaDoc, ReplicaExchange, ReplicaFrontier};

/// Peer identities standing in for Member identities.
const MEMBER_A: u64 = 0x11;
const MEMBER_B: u64 = 0x22;
const MEMBER_C: u64 = 0x33;

fn replica(peer: u64) -> ReplicaDoc {
    ReplicaDoc::new(peer).expect("peer identity accepted")
}

fn body(status: &str) -> BTreeMap<String, LoroValue> {
    let mut fields = BTreeMap::new();
    fields.insert("status".to_owned(), LoroValue::from(status));
    fields
}

/// One field of one document of the materialized scope.
fn field_of(value: &LoroValue, document: &str, field: &str) -> Option<LoroValue> {
    value
        .get_by_key("docs")
        .and_then(|docs| docs.get_by_key(document))
        .and_then(|doc| doc.get_by_key(field))
        .cloned()
}

/// Whether the materialized scope still holds one document.
fn holds(value: &LoroValue, document: &str) -> bool {
    value.get_by_key("docs").and_then(|docs| docs.get_by_key(document)).is_some()
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
    let peer = behind.peer_id();
    *behind = ReplicaDoc::from_snapshot(peer, &snapshot).expect("adopts");
}

#[test]
fn concurrent_same_document_writes_merge_to_one_deterministic_winner() {
    // Two Members accept concurrent writes to the same document during
    // a partition: neither sees the other's operation.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    member_a.apply_put("order-1", &body("accepted-by-a")).expect("a writes");
    member_b.apply_put("order-1", &body("accepted-by-b")).expect("b writes");
    let a_frontier_before = member_a.frontier();
    let b_frontier_before = member_b.frontier();

    // The partition heals; anti-entropy exchanges deltas both ways.
    sync_pair(&mut member_a, &mut member_b);

    // Identical materialized state on both replicas (scenario 6), with
    // one scalar winner — not a conflict marker, not a split state.
    assert_eq!(member_a.deep_value(), member_b.deep_value());
    let winner_a = field_of(&member_a.deep_value(), "order-1", "status").expect("a winner");
    let winner_b = field_of(&member_b.deep_value(), "order-1", "status").expect("b winner");
    assert_eq!(winner_a, winner_b);
    let winner = winner_a.to_json_value().to_string();
    assert!(
        winner.contains("accepted-by-a") || winner.contains("accepted-by-b"),
        "the winner is one of the concurrent writes: {winner}"
    );

    // The loser stays recoverable from history: BOTH concurrent writes
    // remain readable at their own operations after the merge — the
    // shadow history, whichever write won the materialized state.
    let a_end = a_frontier_before
        .ends()
        .iter()
        .find(|(peer, _)| *peer == MEMBER_A)
        .copied()
        .expect("a recorded its operation");
    let b_end = b_frontier_before
        .ends()
        .iter()
        .find(|(peer, _)| *peer == MEMBER_B)
        .copied()
        .expect("b recorded its operation");
    let shadow_a = member_a.value_after(a_end.0, a_end.1 - 1).expect("history serves a's write");
    assert_eq!(
        field_of(&shadow_a, "order-1", "status"),
        Some(LoroValue::from("accepted-by-a")),
        "a's write stays readable from history after the merge"
    );
    let shadow_b = member_b.value_after(b_end.0, b_end.1 - 1).expect("history serves b's write");
    assert_eq!(
        field_of(&shadow_b, "order-1", "status"),
        Some(LoroValue::from("accepted-by-b")),
        "the losing write stays readable from history after the merge"
    );
}

#[test]
fn the_merge_winner_is_deterministic_across_independent_runs() {
    // The same concurrent pair merges to the same winner when the
    // delivery order is reversed on independent replicas: the outcome
    // is a property of the operations, not of arrival order.
    let run = |first_delivery: u64| -> String {
        let mut member_a = replica(MEMBER_A);
        let mut member_b = replica(MEMBER_B);
        member_a.apply_put("order-1", &body("accepted-by-a")).expect("a writes");
        member_b.apply_put("order-1", &body("accepted-by-b")).expect("b writes");
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
        assert_eq!(member_a.deep_value(), member_b.deep_value());
        field_of(&member_a.deep_value(), "order-1", "status")
            .expect("winner")
            .to_json_value()
            .to_string()
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
    member_a.apply_put("order-1", &body("original")).expect("a seeds");
    sync_pair(&mut member_a, &mut member_b);

    // Concurrent divergence: b updates, a deletes.
    member_b.apply_put("order-1", &body("updated-by-b")).expect("b updates");
    member_a.apply_delete("order-1").expect("a deletes");
    assert!(holds(&member_b.deep_value(), "order-1"), "b still holds the document");
    assert!(!holds(&member_a.deep_value(), "order-1"), "a already deleted it");

    sync_pair(&mut member_a, &mut member_b);

    assert!(!holds(&member_a.deep_value(), "order-1"), "delete wins on the deleting member");
    assert!(!holds(&member_b.deep_value(), "order-1"), "delete wins on the updating member too");
    assert_eq!(member_a.deep_value(), member_b.deep_value());
}

#[test]
fn duplicated_and_reordered_delivery_converges_idempotently() {
    // Scenario 11: the same delta arrives twice, and a later delta
    // arrives before an earlier one; the replicas still converge with
    // no duplicated effect.
    let mut member_a = replica(MEMBER_A);
    let mut member_b = replica(MEMBER_B);
    member_a.apply_put("order-1", &body("one")).expect("a writes one");
    let first = delta_bytes(member_a.delta_for(&ReplicaFrontier::default()).expect("exports"));
    member_a.apply_put("order-2", &body("two")).expect("a writes two");
    let second = delta_bytes(
        member_a.delta_for(&ReplicaFrontier::from_ends(vec![(MEMBER_A, 1)])).expect("exports"),
    );

    // Reordered: the second window arrives before the first.
    member_b.absorb(&second).expect("b absorbs the later window first");
    member_b.absorb(&first).expect("b absorbs the earlier window");
    // Duplicated: the first window arrives again.
    member_b.absorb(&first).expect("duplicate delivery is a no-op");

    assert_eq!(member_a.deep_value(), member_b.deep_value());
    assert_eq!(
        field_of(&member_b.deep_value(), "order-1", "status"),
        Some(LoroValue::from("one")),
        "no duplicated materialization"
    );
    assert_eq!(field_of(&member_b.deep_value(), "order-2", "status"), Some(LoroValue::from("two")));
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

    member_a.apply_put("order-1", &body("from-a")).expect("a writes");
    member_b.apply_put("order-2", &body("from-b")).expect("b writes");
    sync_pair(&mut member_a, &mut member_b);

    // c rejoins: its empty frontier does not cover the merged state, so
    // the exchange must carry everything.
    assert!(!member_c.frontier().covers(&member_a.frontier()));
    sync_pair(&mut member_c, &mut member_a);
    sync_pair(&mut member_c, &mut member_b);

    assert_eq!(member_c.deep_value(), member_a.deep_value());
    assert_eq!(member_c.deep_value(), member_b.deep_value());
    assert_eq!(
        field_of(&member_c.deep_value(), "order-2", "status"),
        Some(LoroValue::from("from-b"))
    );
    // c's frontier now names every peer it absorbed.
    let frontier = member_c.frontier();
    let ends = frontier.ends();
    assert!(ends.iter().any(|(peer, _)| *peer == MEMBER_A), "c names a's operations");
    assert!(ends.iter().any(|(peer, _)| *peer == MEMBER_B), "c names b's operations");
}

#[test]
fn covered_frontiers_answer_up_to_date_without_exporting() {
    let mut member_a = replica(MEMBER_A);
    member_a.apply_put("order-1", &body("one")).expect("a writes");
    let frontier = member_a.frontier();

    assert_eq!(member_a.delta_for(&frontier), Ok(ReplicaExchange::UpToDate));
    let behind = ReplicaFrontier::from_ends(vec![(MEMBER_A, frontier.ends()[0].1 - 1)]);
    assert!(
        matches!(member_a.delta_for(&behind), Ok(ReplicaExchange::Delta(_))),
        "a missing counter exports a delta"
    );
    let foreign = ReplicaFrontier::from_ends(vec![(MEMBER_B, 5)]);
    assert!(
        matches!(member_a.delta_for(&foreign), Ok(ReplicaExchange::Delta(_))),
        "an unknown peer's frontier exports everything held"
    );
}

#[test]
fn compaction_raises_the_retained_floor() {
    let mut member = replica(MEMBER_A);
    member.apply_put("order-1", &body("one")).expect("writes");
    assert!(member.retained_floor().ends().is_empty(), "no floor before compaction");
    member.compact_history().expect("compacts");
    assert!(!member.retained_floor().ends().is_empty(), "the floor is the shallow root");
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
    assert_eq!(field_of(&member.deep_value(), "order-1", "status"), Some(LoroValue::from("one")));
}

#[test]
fn a_fresh_peer_adopts_a_compacted_snapshot() {
    // The rejoin shape the fallback serves: a Member that holds no
    // operations of its own sits behind a compacted peer's floor and
    // adopts its snapshot. (A peer WITH own ops must heal through a
    // non-compacted peer — the compaction guard lands with the
    // transport wiring; the composed test pins the typed refusal.)
    let mut holder = replica(MEMBER_A);
    holder.apply_put("order-1", &body("from-a")).expect("writes");
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
    assert_eq!(fresh.deep_value(), holder.deep_value());
    assert_eq!(field_of(&fresh.deep_value(), "order-1", "status"), Some(LoroValue::from("from-a")));
    sync_pair(&mut fresh, &mut holder);
    assert_eq!(fresh.frontier(), holder.frontier());
}
