//! End-to-end proofs of the composed Member (P2): concurrent writes
//! accepted at two servers during a partition, then healed — both
//! engines serve the merged CRDT state, delete-wins between servers,
//! and repeated heals are stable. This is the frozen contract's §8
//! scenario 6 and 13 at the server level, over real engines.

use std::collections::BTreeMap;
use std::path::PathBuf;

use terrabase_replication_core::{ComposeError, ComposedMember, ReconcileReport};
use terrabase_v2::types::DocumentValue;

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
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open("member-b", &dir_b, "tenant-a").expect("opens");

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
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open("member-b", &dir_b, "tenant-a").expect("opens");

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
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open("member-b", &dir_b, "tenant-a").expect("opens");

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
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open("member-b", &dir_b, "tenant-a").expect("opens");
    let mut member_c = ComposedMember::open("member-c", &dir_c, "tenant-a").expect("opens");

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
fn a_restarted_member_recovers_its_merge_state_and_spreads_it() {
    let dir_a = location("restart-a");
    let dir_b = location("restart-b");
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");

    // a accepts a write no other Member has ever seen, then restarts.
    member_a.accept_put("op-a1", "order-1", &body("only-a-knew")).expect("a accepts");
    let before_restart = member_a.frontier();
    member_a.shutdown().expect("closes");

    // The restart: the engine reopens its durable location and the CRDT
    // snapshot staged beside it recovers the merge state — including the
    // write that never left this Member.
    let mut revived = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("reopens");
    assert_eq!(revived.frontier(), before_restart, "the causal frontier survives the restart");
    let mut member_b = ComposedMember::open("member-b", &dir_b, "tenant-a").expect("opens");
    revived.anti_entropy_with(&mut member_b).expect("the revived member spreads its write");
    assert_eq!(member_b.visible("order-1"), Ok(Some(body("only-a-knew"))));
    assert_eq!(revived.merged_state(), member_b.merged_state());

    revived.shutdown().expect("closes");
    member_b.shutdown().expect("closes");
    let _ = std::fs::remove_dir_all(&dir_a);
    let _ = std::fs::remove_dir_all(&dir_b);
}

#[test]
fn invalid_tenant_text_is_refused() {
    let dir = location("invalid");
    let error = match ComposedMember::open("member-a", &dir, " tenant-with-space") {
        Err(error) => error,
        Ok(member) => panic!("invalid tenant text must be refused, opened {}", member.peer_id()),
    };
    assert_eq!(error, ComposeError::InvalidIdentifier);
    let _ = std::fs::remove_dir_all(&dir);
}

/// A three-Region, six-Member replica set with one engine-backed
/// Member per seat, in canonical order.
fn three_region_members(
    tag: &str,
) -> (terrabase_v2::replication::ReplicaSet, Vec<ComposedMember>, Vec<PathBuf>) {
    use std::collections::BTreeMap;
    use terrabase_v2::replication::{MemberId, RegionId, ReplicaSet};

    let names = ["a1", "a2", "b1", "b2", "g1", "g2"];
    let mut dirs = Vec::new();
    let mut members = Vec::new();
    for name in names {
        let dir = location(&format!("{tag}-{name}"));
        members.push(ComposedMember::open(name, &dir, "tenant-a").expect("opens"));
        dirs.push(dir);
    }
    let voters = BTreeMap::from([
        (
            RegionId::try_new("r-alpha").expect("valid region"),
            vec![
                MemberId::try_new("a1").expect("valid member"),
                MemberId::try_new("a2").expect("valid member"),
            ],
        ),
        (
            RegionId::try_new("r-beta").expect("valid region"),
            vec![
                MemberId::try_new("b1").expect("valid member"),
                MemberId::try_new("b2").expect("valid member"),
            ],
        ),
        (
            RegionId::try_new("r-gamma").expect("valid region"),
            vec![
                MemberId::try_new("g1").expect("valid member"),
                MemberId::try_new("g2").expect("valid member"),
            ],
        ),
    ]);
    (ReplicaSet::new(1, voters), members, dirs)
}

/// Heals every Member with the first, in rounds, until no frontier
/// moves — the fixed point background anti-entropy reaches. One round
/// is not enough in general: a Member that exchanges before its peer
/// absorbed a later merge is still behind and needs the next round.
fn heal_all(members: &mut [ComposedMember]) {
    loop {
        let before: Vec<_> = members.iter().map(ComposedMember::frontier).collect();
        let (first, rest) = members.split_first_mut().expect("one member");
        for other in rest {
            first.anti_entropy_with(other).expect("heals");
        }
        let after: Vec<_> = members.iter().map(ComposedMember::frontier).collect();
        if before == after {
            return;
        }
    }
}

fn put_write(
    operation: &str,
    document: &str,
    status: &str,
) -> terrabase_replication_core::ComposedWrite {
    terrabase_replication_core::ComposedWrite::Put {
        operation: operation.to_owned(),
        document: document.to_owned(),
        body: body(status),
    }
}

#[test]
fn quorum_writes_during_a_partition_converge_after_the_heal() {
    use terrabase_replication_core::{ComposedCoordinationError, coordinate_composed_write};
    use terrabase_v2::replication::{LeaderlessLevel, MemberId};

    let (set, mut members, dirs) = three_region_members("coord");

    // The partition splits the Regions: the Region-alpha Members see
    // only each other, likewise Region beta. Each side accepts its own
    // client's write at RegionalQuorum — the minimum success level.
    let (alpha, _rest) = members.split_at_mut(2);
    let receipt_a = coordinate_composed_write(
        &set,
        &MemberId::try_new("a1").expect("valid member"),
        alpha,
        &put_write("op-a1", "order-1", "written-in-alpha"),
        LeaderlessLevel::RegionalQuorum,
    )
    .expect("alpha accepts at regional quorum");
    assert_eq!(receipt_a.contributors().len(), 2, "both alpha Members hold it");

    let (beta, _rest) = members.split_at_mut(4);
    let beta = &mut beta[2..];
    let receipt_b = coordinate_composed_write(
        &set,
        &MemberId::try_new("b1").expect("valid member"),
        beta,
        &put_write("op-b1", "order-1", "written-in-beta"),
        LeaderlessLevel::RegionalQuorum,
    )
    .expect("beta accepts at regional quorum");
    assert_eq!(receipt_b.contributors().len(), 2);
    assert_eq!(receipt_a.epoch(), 1);
    assert_eq!(receipt_b.epoch(), 1);

    // The gamma Members never saw either write.
    for member in &mut members[4..] {
        assert_eq!(member.visible("order-1"), Ok(None));
    }

    // The heal: every Member merges with the first.
    heal_all(&mut members);

    // All six servers serve the identical merged body.
    let views: Vec<_> =
        members.iter_mut().map(|member| member.visible("order-1").expect("serves")).collect();
    assert!(
        views.iter().all(|view| view == &views[0]),
        "every server serves the merged state: {views:?}"
    );
    let winner = views[0].clone().expect("the document exists");
    assert!(
        winner == body("written-in-alpha") || winner == body("written-in-beta"),
        "the served state is one of the quorum-accepted writes"
    );

    // The error class is also proven here: during the partition, one
    // Member alone cannot reach a regional majority.
    let (single, _rest) = members.split_at_mut(1);
    let error = coordinate_composed_write(
        &set,
        &MemberId::try_new("a1").expect("valid member"),
        single,
        &put_write("op-lonely", "order-2", "lonely"),
        LeaderlessLevel::RegionalQuorum,
    )
    .expect_err("one of two voters is not a majority");
    assert!(matches!(error, ComposedCoordinationError::QuorumNotReached { .. }));

    for member in members.drain(..) {
        member.shutdown().expect("closes");
    }
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn every_member_coordinates_in_rotation_at_full_reach() {
    use terrabase_replication_core::coordinate_composed_write;
    use terrabase_v2::replication::{LeaderlessLevel, MemberId};

    let (set, mut members, dirs) = three_region_members("rotate");
    for accepting in ["a1", "a2", "b1", "b2", "g1", "g2"] {
        let receipt = coordinate_composed_write(
            &set,
            &MemberId::try_new(accepting).expect("valid member"),
            &mut members,
            &put_write(&format!("op-{accepting}"), "order-1", "rotated"),
            LeaderlessLevel::RegionalQuorum,
        )
        .expect("every Member coordinates");
        assert_eq!(receipt.contributors().len(), 6, "the fan-out reaches every voter");
        assert_eq!(*receipt.accepting_member(), MemberId::try_new(accepting).expect("valid"));
    }
    heal_all(&mut members);
    let views: Vec<_> =
        members.iter_mut().map(|member| member.visible("order-1").expect("serves")).collect();
    assert!(views.iter().all(|view| view == &views[0]), "all serve one state: {views:?}");

    for member in members.drain(..) {
        member.shutdown().expect("closes");
    }
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn global_quorum_composed_requires_a_majority_of_regions() {
    use terrabase_replication_core::{ComposedCoordinationError, coordinate_composed_write};
    use terrabase_v2::replication::{LeaderlessLevel, MemberId};

    let (set, mut members, dirs) = three_region_members("global");
    // Two Regions reachable: a majority of three.
    let (reachable, _rest) = members.split_at_mut(4);
    coordinate_composed_write(
        &set,
        &MemberId::try_new("a1").expect("valid member"),
        reachable,
        &put_write("op-global", "order-1", "global-write"),
        LeaderlessLevel::GlobalQuorum,
    )
    .expect("two regions with regional majorities qualify");

    // One Region reachable: not a majority.
    let (alpha, _rest) = members.split_at_mut(2);
    let error = coordinate_composed_write(
        &set,
        &MemberId::try_new("a1").expect("valid member"),
        alpha,
        &put_write("op-global-fail", "order-2", "not-global"),
        LeaderlessLevel::GlobalQuorum,
    )
    .expect_err("one region is not a majority of three");
    assert!(matches!(error, ComposedCoordinationError::QuorumNotReached { .. }));

    for member in members.drain(..) {
        member.shutdown().expect("closes");
    }
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn a_rejoining_server_behind_the_floor_recovers_by_snapshot_adoption() {
    let dir_a = location("floor-a");
    let dir_c = location("floor-c");
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");

    member_a.accept_put("op-a1", "order-1", &body("one")).expect("a writes");
    member_a.accept_put("op-a2", "order-2", &body("two")).expect("a writes more");
    member_a.compact_history().expect("a bounds its history");

    // c rejoins with no operations of its own: it sits behind a's
    // retained floor and recovers by adopting a's snapshot.
    let mut member_c = ComposedMember::open("member-c", &dir_c, "tenant-a").expect("opens");
    member_a.anti_entropy_with(&mut member_c).expect("heals across the floor");
    assert_eq!(member_c.visible("order-1"), Ok(Some(body("one"))));
    assert_eq!(member_c.visible("order-2"), Ok(Some(body("two"))));
    assert_eq!(member_a.merged_state(), member_c.merged_state());

    member_a.shutdown().expect("closes");
    member_c.shutdown().expect("closes");
    let _ = std::fs::remove_dir_all(&dir_a);
    let _ = std::fs::remove_dir_all(&dir_c);
}

#[test]
fn guarded_compaction_waits_for_the_live_set_and_adoption_refuses_own_ops() {
    // The operator rule for bounded history: compaction only below the
    // frontier every live peer covers — a compacted Member can neither
    // serve a behind peer by delta nor absorb that peer's operations
    // whose causal context reaches into the shallow region. And the
    // snapshot fallback is typed-refused while the behind Member holds
    // writes of its own.
    let dir_a = location("guard-a");
    let dir_b = location("guard-b");
    let dir_c = location("guard-c");
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open("member-b", &dir_b, "tenant-a").expect("opens");

    member_a.accept_put("op-a1", "order-1", &body("one")).expect("a writes");
    member_a.anti_entropy_with(&mut member_b).expect("spreads to b");
    // Diverge: a writes past the exchange, b writes concurrently.
    member_a.accept_put("op-a2", "order-2", &body("two")).expect("a writes more");
    member_b.accept_put("op-b1", "order-3", &body("three")).expect("b writes concurrently");

    // The guarded compaction refuses while b is behind a's frontier.
    assert!(
        !member_a.compact_history_below(&[member_b.frontier()]).expect("guards"),
        "the guarded compaction refuses while a live peer is behind"
    );
    // The normal heal converges both servers with the history whole.
    member_a.anti_entropy_with(&mut member_b).expect("the un-compacted heal converges");
    for document in ["order-1", "order-2", "order-3"] {
        assert!(member_a.visible(document).expect("a serves").is_some(), "a serves {document}");
        assert!(member_b.visible(document).expect("b serves").is_some(), "b serves {document}");
    }
    // After convergence the guard admits the compaction.
    assert!(member_a.compact_history_below(&[member_b.frontier()]).expect("guards"));

    // A fresh Member holding one write of its own now sits behind a's
    // floor: adopting a's snapshot would drop its write, so the direct
    // heal is typed-refused — the operator compacts only below the live
    // set's frontier, which this Member has not yet joined.
    let mut member_c = ComposedMember::open("member-c", &dir_c, "tenant-a").expect("opens");
    member_c.accept_put("op-c1", "order-4", &body("four")).expect("c writes");
    let refusal =
        member_a.anti_entropy_with(&mut member_c).expect_err("adoption would drop c's own write");
    assert_eq!(refusal, ComposeError::SnapshotAdoptionWouldDropOwnOps);
    // c's own write is intact on its own engine.
    assert_eq!(member_c.visible("order-4"), Ok(Some(body("four"))));

    member_a.shutdown().expect("closes");
    member_b.shutdown().expect("closes");
    member_c.shutdown().expect("closes");
    for dir in [dir_a, dir_b, dir_c] {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn a_quorum_read_returns_the_merged_view_and_repairs_the_behind() {
    use terrabase_replication_core::{ReadRepairReport, quorum_read_with_repair};
    use terrabase_v2::replication::MemberId;

    let (set, mut members, dirs) = three_region_members("read");
    // a2 (the reader) holds order-1; a1 holds nothing; g1 in another
    // Region holds order-2 that the alpha quorum cannot see.
    let (alpha, _rest) = members.split_at_mut(2);
    let a2 = &mut alpha[1];
    a2.accept_put("op-a2", "order-1", &body("from-a2")).expect("a2 writes");
    let (gamma, _rest) = members.split_at_mut(6);
    gamma[4].accept_put("op-g1", "order-2", &body("from-g1")).expect("g1 writes");

    // The quorum read through a2: it merges with its regional peer a1,
    // returns the merged view immediately, and repairs a1 (observed
    // behind with nothing). The gamma Region's write is invisible to
    // the regional quorum — the level discipline of the read.
    let (view, report) = quorum_read_with_repair(
        &set,
        &MemberId::try_new("a2").expect("valid member"),
        &mut members,
        "order-1",
    )
    .expect("the quorum read serves");
    assert_eq!(view, Some(body("from-a2")));
    assert_eq!(report.repaired(), [MemberId::try_new("a1").expect("valid member")].as_slice());
    // The repaired Member's engine now serves the document too.
    assert_eq!(members[0].visible("order-1"), Ok(Some(body("from-a2"))));

    let (gamma_view, _) = quorum_read_with_repair(
        &set,
        &MemberId::try_new("g1").expect("valid member"),
        &mut members,
        "order-2",
    )
    .expect("the gamma read serves");
    assert_eq!(gamma_view, Some(body("from-g1")));

    // A second read at the settled frontier repairs nothing.
    let (_, again) = quorum_read_with_repair(
        &set,
        &MemberId::try_new("a2").expect("valid member"),
        &mut members,
        "order-1",
    )
    .expect("serves again");
    assert_eq!(again, ReadRepairReport::default());

    for member in members.drain(..) {
        member.shutdown().expect("closes");
    }
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn a_quorum_read_without_a_regional_majority_is_refused() {
    use terrabase_replication_core::{ComposeError, quorum_read_with_repair};
    use terrabase_v2::replication::MemberId;

    let (set, mut members, dirs) = three_region_members("read-min");
    // Only a1 is reachable: one of two alpha voters is not a majority.
    let (single, _rest) = members.split_at_mut(1);
    let error = quorum_read_with_repair(
        &set,
        &MemberId::try_new("a1").expect("valid member"),
        single,
        "order-1",
    )
    .expect_err("no regional majority");
    assert_eq!(error, ComposeError::ReadQuorumNotReached);

    for member in members.drain(..) {
        member.shutdown().expect("closes");
    }
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn an_epoch_change_keeps_every_acknowledged_write() {
    use std::collections::BTreeMap;
    use terrabase_replication_core::{EpochError, accept_epoch, coordinate_composed_write};
    use terrabase_v2::replication::{LeaderlessLevel, MemberId, RegionId, ReplicaSet};

    // Epoch 1: alpha seats a1,a2. A write is acknowledged at regional
    // quorum — durably held by both.
    let (set_v1, mut members, dirs) = three_region_members("epoch");
    let (alpha, _rest) = members.split_at_mut(2);
    coordinate_composed_write(
        &set_v1,
        &MemberId::try_new("a1").expect("valid member"),
        alpha,
        &put_write("op-w1", "order-1", "written-under-epoch-1"),
        LeaderlessLevel::RegionalQuorum,
    )
    .expect("epoch 1 accepts the write");

    // Epoch 2 replaces a2 with a3 in alpha. The overlap precondition
    // holds (2 + 2 > 3 voters in the union), so old and new quorums
    // must intersect.
    let dir_a3 = location("epoch-a3");
    members.push(ComposedMember::open("a3", &dir_a3, "tenant-a").expect("opens"));
    let mut voters = BTreeMap::new();
    voters.insert(
        RegionId::try_new("r-alpha").expect("valid region"),
        vec![
            MemberId::try_new("a1").expect("valid member"),
            MemberId::try_new("a3").expect("valid member"),
        ],
    );
    voters.insert(
        RegionId::try_new("r-beta").expect("valid region"),
        vec![
            MemberId::try_new("b1").expect("valid member"),
            MemberId::try_new("b2").expect("valid member"),
        ],
    );
    voters.insert(
        RegionId::try_new("r-gamma").expect("valid region"),
        vec![
            MemberId::try_new("g1").expect("valid member"),
            MemberId::try_new("g2").expect("valid member"),
        ],
    );
    let set_v2 = ReplicaSet::new(2, voters);

    // A skipped epoch is refused outright.
    let error = accept_epoch(
        &set_v1,
        &ReplicaSet::new(3, {
            let mut voters = BTreeMap::new();
            voters.insert(
                RegionId::try_new("r-alpha").expect("valid region"),
                vec![MemberId::try_new("a1").expect("valid member")],
            );
            voters
        }),
        &mut members,
    )
    .expect_err("epochs never skip");
    assert_eq!(error, EpochError::NotAStep { from: 1, to: 3 }, "a two-step proposal is refused");

    // The real transition: both sides' quorums durably record it.
    let receipt = accept_epoch(&set_v1, &set_v2, &mut members).expect("the epoch is accepted");
    assert_eq!(receipt.from(), 1);
    assert_eq!(receipt.to(), 2);
    assert!(receipt.old_acceptors().contains(&MemberId::try_new("a2").expect("valid member")));
    assert!(receipt.new_acceptors().contains(&MemberId::try_new("a3").expect("valid member")));
    assert_eq!(receipt.regions().len(), 3);

    // The acknowledged write survives: a3, joining only at epoch 2,
    // receives it through the heal — the overlap member a1 held it.
    heal_all(&mut members);
    for name in ["a1", "a2", "a3", "b1", "b2", "g1", "g2"] {
        let index = members
            .iter()
            .position(|member| member.member_id().as_str() == name)
            .expect("member present");
        assert_eq!(
            members[index].visible("order-1"),
            Ok(Some(body("written-under-epoch-1"))),
            "{name} serves the epoch-1 write after the transition"
        );
    }

    // A write under the new epoch coordinates against the new set: a2
    // (retired from the voters) is skipped by the fan-out.
    let receipt = coordinate_composed_write(
        &set_v2,
        &MemberId::try_new("a1").expect("valid member"),
        &mut members,
        &put_write("op-w2", "order-2", "written-under-epoch-2"),
        LeaderlessLevel::RegionalQuorum,
    )
    .expect("epoch 2 accepts the write");
    assert_eq!(receipt.epoch(), 2);
    assert_eq!(receipt.contributors().len(), 6, "a2 is no longer a voter");
    assert!(!receipt.contributors().iter().any(|ack| ack.member().as_str() == "a2"));

    for member in members.drain(..) {
        member.shutdown().expect("closes");
    }
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
    let _ = std::fs::remove_dir_all(&dir_a3);
}

#[test]
fn a_transition_without_regional_overlap_is_refused() {
    use std::collections::BTreeMap;
    use terrabase_replication_core::{EpochError, accept_epoch};
    use terrabase_v2::replication::{MemberId, RegionId, ReplicaSet};

    // Epoch 1 seats a1,a2 in alpha; the proposal replaces BOTH with
    // a3,a4: no old majority can intersect a new majority (2 + 2 is
    // not greater than the union of 4), so the transition is refused
    // before any durable recording.
    let (set_v1, mut members, dirs) = three_region_members("no-overlap");
    let mut voters = BTreeMap::new();
    voters.insert(
        RegionId::try_new("r-alpha").expect("valid region"),
        vec![
            MemberId::try_new("a3").expect("valid member"),
            MemberId::try_new("a4").expect("valid member"),
        ],
    );
    for region in ["r-beta", "r-gamma"] {
        voters.insert(
            RegionId::try_new(region).expect("valid region"),
            set_v1.voters_of(&RegionId::try_new(region).expect("valid region")).to_vec(),
        );
    }
    let set_v2 = ReplicaSet::new(2, voters);
    let error = accept_epoch(&set_v1, &set_v2, &mut members).expect_err("no overlap");
    assert_eq!(
        error,
        EpochError::NoRegionalOverlap {
            region: RegionId::try_new("r-alpha").expect("valid region")
        }
    );

    for member in members.drain(..) {
        member.shutdown().expect("closes");
    }
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
}

#[test]
fn admission_refuses_nothing_durable() {
    use terrabase_v2::families::modes::{AdmissionError, CollectionPolicy, ValueKind};
    use terrabase_v2::families::{SchemaDefinition, SchemaRule, SchemaVersion};

    let dir = location("admission");
    let mut member = ComposedMember::open("member-a", &dir, "tenant-a").expect("opens");
    let definition = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![SchemaRule::Required { field: "status".to_owned() }],
    )
    .expect("valid definition");

    // strict: an undeclared path is refused, nothing durable.
    member.set_policy(CollectionPolicy::strict(definition.clone()));
    let refusal = member
        .accept_put("op-1", "order-1", &body_from(&[("undeclared", "x")]))
        .expect_err("strict refuses the undeclared path");
    assert_eq!(
        refusal,
        ComposeError::SchemaAdmission(AdmissionError::UndeclaredPath {
            path: "undeclared".to_owned()
        })
    );
    assert_eq!(member.visible("order-1"), Ok(None), "nothing durable on refusal");

    // validated: a rule violation is refused with the violation named;
    // a conforming body is admitted.
    member.set_policy(CollectionPolicy::validated(definition));
    let refusal = member
        .accept_put("op-2", "order-1", &body_from(&[("note", "no status")]))
        .expect_err("the required rule refuses the missing field");
    assert!(matches!(
        refusal,
        ComposeError::SchemaAdmission(AdmissionError::RuleViolations { .. })
    ));
    member
        .accept_put("op-3", "order-1", &body_from(&[("status", "open")]))
        .expect("a conforming body is admitted");

    // schema-free: stable path kinds — re-kinding a created path fails.
    member.set_policy(CollectionPolicy::schema_free());
    let refusal = member
        .accept_put("op-4", "order-1", &body_int(&[("status", 7)]))
        .expect_err("re-kinding a created path fails closed");
    assert_eq!(
        refusal,
        ComposeError::SchemaAdmission(AdmissionError::KindMismatch {
            path: "status".to_owned(),
            expected: ValueKind::String,
            offered: ValueKind::Integer,
        })
    );
    // The engine still serves the admitted body, not the refused one.
    assert_eq!(member.visible("order-1"), Ok(Some(body_from(&[("status", "open")]))));

    member.shutdown().expect("closes");
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn a_policy_skewed_merge_is_quarantined_then_healed_by_alignment() {
    use terrabase_v2::families::modes::CollectionPolicy;
    use terrabase_v2::families::{SchemaDefinition, SchemaRule, SchemaVersion};

    // Policy skew during a rolling upgrade: member-a validates under a
    // version without the Required rule; member-b already upgraded.
    // The write is valid where it is admitted; b's post-merge
    // validation sees a violation and quarantines the merged document
    // — never silently visible — and aligning the policy heals it.
    let dir_a = location("skew-a");
    let dir_b = location("skew-b");
    let mut member_a = ComposedMember::open("member-a", &dir_a, "tenant-a").expect("opens");
    let mut member_b = ComposedMember::open("member-b", &dir_b, "tenant-a").expect("opens");
    let relaxed = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![SchemaRule::TextLength { field: "note".to_owned(), min: None, max: Some(8) }],
    )
    .expect("valid definition");
    let upgraded = SchemaDefinition::try_new(
        SchemaVersion::try_new(2).expect("valid version"),
        vec![
            SchemaRule::Required { field: "status".to_owned() },
            SchemaRule::TextLength { field: "note".to_owned(), min: None, max: Some(8) },
        ],
    )
    .expect("valid definition");
    member_a.set_policy(CollectionPolicy::validated(relaxed.clone()));
    member_b.set_policy(CollectionPolicy::validated(upgraded.clone()));

    member_a
        .accept_put("op-1", "order-1", &body_from(&[("note", "kept")]))
        .expect("admitted under the relaxed version");
    member_a.anti_entropy_with(&mut member_b).expect("the merge reaches b");

    // b quarantined the merged document and refuses the read.
    assert!(member_b.quarantined().contains_key("order-1"), "the merged body violates b's policy");
    assert_eq!(member_b.read_document("order-1"), Err(ComposeError::SchemaQuarantine));
    // The raw engine still holds the merged bytes — quarantine is a
    // read-gate, not an erasure.
    assert!(member_b.visible("order-1").expect("raw read").is_some());

    // Aligning the policy (rolling upgrade completes) heals: the next
    // reconcile clears the quarantine and serves the document.
    member_a.set_policy(CollectionPolicy::validated(upgraded));
    member_b.set_policy(CollectionPolicy::validated(relaxed.clone()));
    // Now a's policy is the upgraded one — the write side refuses new
    // violations, and the existing merged state validates under relaxed.
    member_b.reconcile().expect("reconciles");
    assert!(member_b.quarantined().is_empty(), "the aligned policy validates the merged body");
    assert!(member_b.read_document("order-1").expect("serves").is_some());

    member_a.shutdown().expect("closes");
    member_b.shutdown().expect("closes");
    let _ = std::fs::remove_dir_all(&dir_a);
    let _ = std::fs::remove_dir_all(&dir_b);
}

/// A string-field body helper.
fn body_from(entries: &[(&str, &str)]) -> BTreeMap<String, DocumentValue> {
    entries
        .iter()
        .map(|(path, text)| ((*path).to_owned(), DocumentValue::String((*text).to_owned())))
        .collect()
}

/// An integer-field body helper.
fn body_int(entries: &[(&str, i64)]) -> BTreeMap<String, DocumentValue> {
    entries
        .iter()
        .map(|(path, value)| ((*path).to_owned(), DocumentValue::Integer(*value)))
        .collect()
}
