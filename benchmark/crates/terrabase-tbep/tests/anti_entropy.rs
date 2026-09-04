//! Journal-independent catch-up (S-05 slice 2e, D-45): Merkle repair over
//! the kept anti-entropy kernels and snapshot bootstrap from the
//! region-local snapshot file set.
//!
//! The tailer tests (`replication.rs`, `replication_lanes.rs`) pin the
//! journal path; these tests pin what happens when the journal cannot
//! serve: members that never exchanged a frame converge through
//! [`repair_once`](terrabase_tbep::anti_entropy::repair_once), a skewed
//! same-key write resolves to the higher stamp, a restarted member catches
//! up without its old link, a fresh member bootstraps from the peer's
//! snapshot file set and the tail resumes past it, and divergence past the
//! range budget reports [`NeedsSnapshot`](terrabase_tbep::anti_entropy::RepairDecision::NeedsSnapshot)
//! instead of repairing.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase::engine::CommitOutcome;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};
use terrabase_member_transport::in_process::InProcessLane;
use terrabase_member_transport::transport::MemberTransport;
use terrabase_tbep::anti_entropy::{
    RepairDecision, journal_end, repair_once, repair_once_bounded, snapshot_bootstrap,
};
use terrabase_tbep::registry::{BoundScope, ChannelBindRequest};
use terrabase_tbep::replication::replicate_once;
use terrabase_tbep::replication_lanes::RegionLink;
use terrabase_tbep::{MemberHost, MemberHostOptions, TbepHost};

static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

struct TestLocation(PathBuf);

impl TestLocation {
    fn new(label: &str) -> Self {
        let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
        Self(
            std::env::temp_dir()
                .join(format!("terrabase-s05-entropy-{label}-{}-{number}", std::process::id())),
        )
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestLocation {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn scope() -> BoundScope {
    BoundScope {
        principal: "spiffe://local/s05-test".into(),
        tenant: "tenant-a".into(),
        project: "project-a".into(),
        database: "database-a".into(),
    }
}

/// One region's member: its own durable location, the same tenant.
fn member(label: &str) -> (TestLocation, MemberHost) {
    let location = TestLocation::new(label);
    let host = MemberHost::open(
        location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the member host opens");
    host.bind_channel(&ChannelBindRequest { scope: scope() }).expect("the database scope binds");
    (location, host)
}

/// Writes one origin document through the member's engine.
fn put(host: &MemberHost, id: &str, value: i64) {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    let operation =
        OperationId::try_new(&format!("put-{id}-{value}")).expect("the operation identity parses");
    let mut transaction =
        engine.begin(&engine_scope, snapshot, operation).expect("the write begins");
    transaction
        .put(
            Document::new(
                DocumentId::try_new(id).expect("the document id parses"),
                CollectionName::try_new("orders").expect("the collection parses"),
                DocumentValue::Object(BTreeMap::from([(
                    "value".to_owned(),
                    DocumentValue::Integer(value),
                )])),
            ),
            EngineVersion::Any,
        )
        .expect("the put stages");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        outcome => panic!("the write committed: {outcome:?}"),
    }
}

/// The member's live documents of the collection, at a fresh snapshot.
fn orders(host: &MemberHost) -> Vec<Document> {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    engine
        .snapshot_documents_in_collection(
            &engine_scope,
            &snapshot,
            &CollectionName::try_new("orders").expect("the collection parses"),
        )
        .expect("the collection scan serves")
}

fn bodies(host: &MemberHost) -> BTreeMap<String, DocumentValue> {
    orders(host)
        .into_iter()
        .map(|document| (document.id().as_str().to_owned(), document.value().clone()))
        .collect()
}

/// Two members that never exchanged a frame converge through one Merkle
/// pass: every key ships exactly once through the merge, and the second
/// pass is a verified no-op.
#[test]
fn partitioned_members_converge_through_merkle_repair() {
    let (_a_location, a) = member("partition-a");
    let (_b_location, b) = member("partition-b");
    let scope = scope();

    for round in 0..10 {
        put(&a, &format!("orders/a-{round}"), round);
        put(&b, &format!("orders/b-{round}"), round + 100);
    }

    let decision = repair_once(a.engine_host(), b.engine_host(), &scope).expect("the repair runs");
    assert_eq!(decision, RepairDecision::Converged { repaired: 20 }, "every key ships once");
    assert_eq!(orders(&a).len(), 20, "region A serves both sides");
    assert_eq!(orders(&b).len(), 20, "region B serves both sides");
    assert_eq!(bodies(&a), bodies(&b), "both members serve identical bodies");

    let decision =
        repair_once(a.engine_host(), b.engine_host(), &scope).expect("the second pass runs");
    assert_eq!(decision, RepairDecision::Converged { repaired: 0 }, "convergence is stable");
}

/// Clock skew resolves through repair exactly as through the tailer: B
/// witnessed A's stamp before writing, so its higher stamp wins and A
/// adopts B's body.
#[test]
fn skewed_same_key_write_resolves_to_the_higher_stamp() {
    let (_a_location, a) = member("skew-a");
    let (_b_location, b) = member("skew-b");
    let scope = scope();

    put(&a, "orders/race", 1);
    let mut cursor = None;
    let shipped = replicate_once(a.engine_host(), b.engine_host(), &scope, &mut cursor)
        .expect("the first write replicates");
    assert_eq!(shipped, 1);
    // B's clock witnessed A's stamp, so this origin tick is strictly higher.
    put(&b, "orders/race", 2);

    let decision = repair_once(a.engine_host(), b.engine_host(), &scope).expect("the repair runs");
    assert_eq!(decision, RepairDecision::Converged { repaired: 1 }, "one record publishes");
    let winner =
        DocumentValue::Object(BTreeMap::from([("value".to_owned(), DocumentValue::Integer(2))]));
    assert_eq!(bodies(&a).get("orders/race"), Some(&winner), "A adopts the higher stamp");
    assert_eq!(bodies(&b).get("orders/race"), Some(&winner), "B keeps the higher stamp");
}

/// The restart leg without the old link: the killed member's engine is
/// durable, so reopening its location and running one Merkle pass catches
/// it up — no cursor, no re-shipped journal, no loss.
#[test]
fn a_restarted_member_catches_up_through_merkle_repair() {
    let (b_location, b) = member("restart-b");
    let (_a_location, a) = member("restart-a");
    let scope = scope();

    for round in 0..3 {
        put(&a, &format!("orders/pre-{round}"), round);
    }
    let mut cursor = None;
    let shipped = replicate_once(a.engine_host(), b.engine_host(), &scope, &mut cursor)
        .expect("the pre-kill writes replicate");
    assert_eq!(shipped, 3);
    drop(b);

    for round in 0..10 {
        put(&a, &format!("orders/down-{round}"), round + 10);
    }

    let b = MemberHost::open(
        b_location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the restarted member opens");
    b.bind_channel(&ChannelBindRequest { scope: scope.clone() })
        .expect("the database scope rebinds");

    let decision = repair_once(a.engine_host(), b.engine_host(), &scope).expect("the repair runs");
    assert_eq!(decision, RepairDecision::Converged { repaired: 10 }, "the ten missed writes ship");
    assert_eq!(orders(&b).len(), 13, "the restarted member serves everything");
    assert_eq!(bodies(&a), bodies(&b), "both members serve identical bodies");
}

/// Snapshot bootstrap: the fresh member installs the peer's snapshot file
/// set (the in-process form of the region-local Litestream snapshot),
/// serves it byte-identically, and the tail resumes past the snapshot —
/// only the post-snapshot deltas ship.
#[test]
fn snapshot_bootstrap_installs_peer_state_then_tail_catches_up() {
    let (peer_location, peer) = member("snapshot-peer");
    let scope = scope();

    for round in 0..8 {
        put(&peer, &format!("orders/snap-{round}"), round);
    }
    // Quiesce the peer: the bootstrap copies a durable file set with no
    // commit wave in flight, exactly the snapshot contract.
    drop(peer);

    let fresh_location = TestLocation::new("snapshot-fresh");
    let bootstrap = snapshot_bootstrap(peer_location.path(), fresh_location.path())
        .expect("the snapshot installs");
    assert!(bootstrap.files_copied.contains(&"terrabase.sqlite3".to_owned()));
    assert!(bootstrap.files_copied.contains(&"format".to_owned()));

    let peer = MemberHost::open(
        peer_location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the peer reopens");
    peer.bind_channel(&ChannelBindRequest { scope: scope.clone() })
        .expect("the peer scope rebinds");
    let fresh = MemberHost::open(
        fresh_location.path(),
        8,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the bootstrapped member opens");
    fresh
        .bind_channel(&ChannelBindRequest { scope: scope.clone() })
        .expect("the fresh scope rebinds");

    assert_eq!(bodies(&fresh), bodies(&peer), "the snapshot serves the peer state identically");

    // The tail resumes past the snapshot: only the three post-snapshot
    // deltas ship, and the link's cursor proves it.
    let snapshot_end = journal_end(peer.engine_host(), &scope).expect("the snapshot end reads");
    for round in 0..3 {
        put(&peer, &format!("orders/delta-{round}"), round + 50);
    }
    let mut lane = InProcessLane::new();
    let mut link_gru = RegionLink::new("member-gru", "member-fra").expect("the gru link opens");
    let mut link_fra = RegionLink::new("member-fra", "member-gru").expect("the fra link opens");
    link_gru.set_cursor(snapshot_end);
    assert_eq!(link_gru.cursor(), snapshot_end, "the link resumes past the snapshot");

    for _ in 0..200 {
        if orders(&fresh).len() == 11 {
            break;
        }
        link_gru.receive(peer.engine_host(), &scope, &mut lane).expect("the gru inbound applies");
        link_gru.drain_acks(&mut lane).expect("the gru acks drain");
        link_gru.ship(peer.engine_host(), &scope, &mut lane).expect("the gru side ships");
        link_fra.receive(fresh.engine_host(), &scope, &mut lane).expect("the fra inbound applies");
        link_fra.drain_acks(&mut lane).expect("the fra acks drain");
        link_fra.ship(fresh.engine_host(), &scope, &mut lane).expect("the fra side ships");
        lane.pump();
    }
    assert_eq!(orders(&fresh).len(), 11, "only the three deltas ship past the snapshot");
    assert_eq!(bodies(&fresh), bodies(&peer), "both members serve identical bodies");
}

/// Divergence past the range budget is not repaired: the pass reports the
/// snapshot the caller must install instead, publishing nothing.
#[test]
fn divergence_past_budget_reports_needs_snapshot() {
    let (_a_location, a) = member("budget-a");
    let (_b_location, b) = member("budget-b");
    let scope = scope();

    for round in 0..10 {
        put(&a, &format!("orders/a-{round}"), round);
        put(&b, &format!("orders/b-{round}"), round + 100);
    }

    let decision =
        repair_once_bounded(a.engine_host(), b.engine_host(), &scope, 2).expect("the pass runs");
    assert_eq!(
        decision,
        RepairDecision::NeedsSnapshot { ranges: 20, max_ranges: 2 },
        "the budget refuses the repair"
    );
    assert_eq!(orders(&a).len(), 10, "no repair record published on A");
    assert_eq!(orders(&b).len(), 10, "no repair record published on B");
}
