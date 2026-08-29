//! End-to-end proofs of the composed Member (P2): concurrent writes
//! accepted at two servers during a partition, then healed — both
//! engines serve the merged CRDT state, delete-wins between servers,
//! and repeated heals are stable. This is the frozen contract's §8
//! scenario 6 and 13 at the server level, over real engines.

use std::collections::BTreeMap;
use std::path::PathBuf;

use terrabase_replication_core::{ComposeError, ComposedMember, ReconcileReport};
use terrabase_v2::types::DocumentValue;

const MEMBER_A: u64 = 0x11;
const MEMBER_B: u64 = 0x22;

fn location(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "tbv2-composed-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn body(status: &str) -> BTreeMap<String, DocumentValue> {
    BTreeMap::from([("status".to_owned(), DocumentValue::String(status.to_owned()))])
}

fn cleanup(members: &[(&str, PathBuf)]) {
    for (_, dir) in members {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn concurrent_writes_at_two_servers_converge_after_the_heal() {
    let dir_a = location("conv-a");
    let dir_b = location("conv-b");
    let mut member_a = ComposedMember::open(MEMBER_A, &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open(MEMBER_B, &dir_b, "tenant-a").expect("opens");

    // The partition: each server accepts its own client's write with no
    // exchange. Both succeed locally — there is no other Member to ask.
    member_a.accept_put("op-a1", "order-1", &body("accepted-by-a")).expect("a accepts");
    member_b.accept_put("op-b1", "order-1", &body("accepted-by-b")).expect("b accepts");
    assert_eq!(member_a.visible("order-1"), Ok(Some(body("accepted-by-a"))));
    assert_eq!(member_b.visible("order-1"), Ok(Some(body("accepted-by-b"))));
    assert_ne!(
        member_a.visible("order-1").unwrap(),
        member_b.visible("order-1").unwrap(),
        "during the partition the servers diverge"
    );

    // The heal: anti-entropy merges the CRDTs, then both engines are
    // reconciled to the merged state.
    let (report_a, report_b) = member_a.anti_entropy_with(&mut member_b).expect("heals");
    assert_eq!(report_a.puts() + report_b.puts(), 1, "exactly one server held the loser");

    // Both engines now serve the same merged body — the deterministic
    // winner, one of the two accepted writes.
    let a_view = member_a.visible("order-1").expect("a serves");
    let b_view = member_b.visible("order-1").expect("b serves");
    assert_eq!(a_view, b_view, "both servers serve the merged state");
    let winner = a_view.unwrap();
    assert!(
        winner == body("accepted-by-a") || winner == body("accepted-by-b"),
        "the served state is one of the concurrent writes"
    );
    assert_eq!(member_a.merged_state(), member_b.merged_state());
    assert_eq!(
        member_a.frontier(),
        member_b.frontier(),
        "both members hold the same causal frontier after the heal"
    );

    member_a.shutdown().expect("closes");
    member_b.shutdown().expect("closes");
    cleanup(&[("a", dir_a), ("b", dir_b)]);
}

#[test]
fn delete_wins_between_servers_after_the_heal() {
    let dir_a = location("del-a");
    let dir_b = location("del-b");
    let mut member_a = ComposedMember::open(MEMBER_A, &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open(MEMBER_B, &dir_b, "tenant-a").expect("opens");

    member_a.accept_put("op-seed", "order-1", &body("original")).expect("seeds");
    member_a.anti_entropy_with(&mut member_b).expect("first heal spreads the seed");
    assert_eq!(member_b.visible("order-1"), Ok(Some(body("original"))));

    // Concurrent divergence: b updates, a deletes.
    member_b.accept_put("op-b2", "order-1", &body("updated-by-b")).expect("b updates");
    member_a.accept_delete("op-a2", "order-1").expect("a deletes");
    assert_eq!(member_b.visible("order-1"), Ok(Some(body("updated-by-b"))));
    assert_eq!(member_a.visible("order-1"), Ok(None));

    member_a.anti_entropy_with(&mut member_b).expect("heals");

    assert_eq!(member_a.visible("order-1"), Ok(None), "delete wins on the deleting server");
    assert_eq!(member_b.visible("order-1"), Ok(None), "delete wins on the updating server");
    assert_eq!(member_a.merged_state(), member_b.merged_state());

    member_a.shutdown().expect("closes");
    member_b.shutdown().expect("closes");
    cleanup(&[("a", dir_a), ("b", dir_b)]);
}

#[test]
fn repeated_heals_commit_nothing_new() {
    let dir_a = location("stable-a");
    let dir_b = location("stable-b");
    let mut member_a = ComposedMember::open(MEMBER_A, &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open(MEMBER_B, &dir_b, "tenant-a").expect("opens");

    member_a.accept_put("op-a1", "order-1", &body("by-a")).expect("a accepts");
    member_b.accept_put("op-b1", "order-2", &body("by-b")).expect("b accepts");
    member_a.anti_entropy_with(&mut member_b).expect("first heal");
    let settled = member_a.frontier();

    let (again_a, again_b) = member_a.anti_entropy_with(&mut member_b).expect("second heal");
    assert_eq!(
        (again_a, again_b),
        (ReconcileReport::default(), ReconcileReport::default()),
        "an unchanged frontier reconciles nothing"
    );
    assert_eq!(member_a.frontier(), settled, "no new CRDT operations");

    member_a.shutdown().expect("closes");
    member_b.shutdown().expect("closes");
    cleanup(&[("a", dir_a), ("b", dir_b)]);
}

#[test]
fn a_third_server_joins_by_exchanging_with_one_member() {
    let dir_a = location("mesh-a");
    let dir_b = location("mesh-b");
    let dir_c = location("mesh-c");
    let mut member_a = ComposedMember::open(MEMBER_A, &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open(MEMBER_B, &dir_b, "tenant-a").expect("opens");
    let mut member_c = ComposedMember::open(0x33, &dir_c, "tenant-a").expect("opens");

    member_a.accept_put("op-a1", "order-1", &body("by-a")).expect("a accepts");
    member_b.accept_put("op-b1", "order-2", &body("by-b")).expect("b accepts");
    member_a.anti_entropy_with(&mut member_b).expect("a and b heal");

    // c joins by exchanging with a only — the merged state reaches it,
    // and c's own engine is reconciled without ever talking to b.
    member_c.anti_entropy_with(&mut member_a).expect("c catches up through a");
    assert_eq!(member_c.visible("order-1"), Ok(Some(body("by-a"))));
    assert_eq!(member_c.visible("order-2"), Ok(Some(body("by-b"))));
    assert_eq!(member_c.merged_state(), member_b.merged_state());

    member_a.shutdown().expect("closes");
    member_b.shutdown().expect("closes");
    member_c.shutdown().expect("closes");
    cleanup(&[("a", dir_a), ("b", dir_b), ("c", dir_c)]);
}

#[test]
fn invalid_tenant_text_is_refused() {
    let dir = location("invalid");
    let error = match ComposedMember::open(MEMBER_A, &dir, " tenant-with-space") {
        Err(error) => error,
        Ok(member) => panic!("invalid tenant text must be refused, opened {}", member.peer_id()),
    };
    assert_eq!(error, ComposeError::InvalidIdentifier);
    let _ = std::fs::remove_dir_all(&dir);
}
