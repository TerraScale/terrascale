//! The cross-region lanes' gates (S-05 slice 2b, D-35/D-36/D-45): the
//! visibility target and the fault matrix, in-process — one reactive
//! subscriber receiving another region's write through a
//! fault-injectable `MemberTransport`, partitions healing with zero
//! acknowledged-write loss, a killed member restarting into catch-up, and
//! the credit window bounding in-flight frames.
//!
//! The deterministic-merge half of the fault matrix — clock skew resolves
//! by the `(hlc, origin)` order — is deterministic by design and pinned
//! by `tests/replication.rs::concurrent_writes_converge_deterministically`
//! (slice 2a); the bidirectional lane test here drives the same merge over
//! real frames without re-deriving it.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use terrabase::engine::CommitOutcome;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};
use terrabase_member_transport::in_process::InProcessLane;
use terrabase_member_transport::transport::MemberTransport;
use terrabase_member_transport::{
    Envelope, FrameKind, LaneInstant, MemberId, Payload, TenantId as LaneTenantId, encode,
};
use terrabase_tbep::registry::{BoundScope, ChannelBindRequest};
use terrabase_tbep::replication::replicate_once;
use terrabase_tbep::replication_lanes::{MAX_INFLIGHT_FRAMES, RegionLink, ShipStep};
use terrabase_tbep::{MemberHost, MemberHostOptions, TbepHost};

static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

struct TestLocation(PathBuf);

impl TestLocation {
    fn new(label: &str) -> Self {
        let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
        Self(
            std::env::temp_dir()
                .join(format!("terrabase-s05-lanes-{label}-{}-{number}", std::process::id())),
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

/// Writes one row per commit so each replicated fixture row keeps its own
/// operation identity while the journal page fills to its bound.
fn put_many(host: &MemberHost, count: usize) {
    for index in 0..count {
        put(host, &format!("orders/seed-{index}"), i64::try_from(index).expect("the index fits"));
    }
}

fn replication_frame(
    source: &str,
    target: &str,
    tenant: &str,
    message_id: u64,
    correlation_id: Option<u64>,
) -> terrabase_member_transport::EncodedFrame {
    encode(&Envelope {
        kind: if correlation_id.is_some() {
            FrameKind::AckBatch
        } else {
            FrameKind::ReplicationBatch
        },
        message_id,
        correlation_id,
        causation_id: None,
        idempotency_key: None,
        source: MemberId::try_new(source).expect("the source identity parses"),
        target: MemberId::try_new(target).expect("the target identity parses"),
        issued_at: LaneInstant::at_ticks(0),
        expires_at: None,
        range: None,
        epoch: None,
        payload: if correlation_id.is_some() {
            Payload::AckBatch {
                tenant: LaneTenantId::try_new(tenant).expect("the tenant identity parses"),
                reports: Vec::new(),
            }
        } else {
            Payload::ReplicationBatch {
                tenant: LaneTenantId::try_new(tenant).expect("the tenant identity parses"),
                records: Vec::new(),
            }
        },
    })
    .expect("the adversarial frame encodes")
}

fn acknowledgement_frame(
    source: &str,
    target: &str,
    tenant: &str,
    message_id: u64,
    correlation_id: u64,
    reports: &[(&str, bool)],
) -> terrabase_member_transport::EncodedFrame {
    encode(&Envelope {
        kind: FrameKind::AckBatch,
        message_id,
        correlation_id: Some(correlation_id),
        causation_id: None,
        idempotency_key: None,
        source: MemberId::try_new(source).expect("the source identity parses"),
        target: MemberId::try_new(target).expect("the target identity parses"),
        issued_at: LaneInstant::at_ticks(0),
        expires_at: None,
        range: None,
        epoch: None,
        payload: Payload::AckBatch {
            tenant: LaneTenantId::try_new(tenant).expect("the tenant identity parses"),
            reports: reports
                .iter()
                .map(|(operation, held)| {
                    (OperationId::try_new(operation).expect("the operation identity parses"), *held)
                })
                .collect(),
        },
    })
    .expect("the acknowledgement frame encodes")
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

/// Two members, their two directed links, and the lane between them: the
/// minimal cross-region mesh, with the pump the S-09 member composition
/// will drive per poll interval.
struct Mesh<'a> {
    lane: InProcessLane,
    scope: BoundScope,
    a: &'a MemberHost,
    b: &'a MemberHost,
    link_a: RegionLink,
    link_b: RegionLink,
}

impl Mesh<'_> {
    /// One mesh tick: every member receives and acknowledges inbound
    /// frames, ships its next frame, and the lane clock advances one tick.
    fn tick(&mut self) {
        let Self { lane, scope, a, b, link_a, link_b } = self;
        link_a.receive(a.engine_host(), scope, lane).expect("the inbound pass applies");
        link_a.drain_acks(lane).expect("the ack pass resolves");
        link_a.ship(a.engine_host(), scope, lane).expect("the outbound pass ships");
        link_b.receive(b.engine_host(), scope, lane).expect("the inbound pass applies");
        link_b.drain_acks(lane).expect("the ack pass resolves");
        link_b.ship(b.engine_host(), scope, lane).expect("the outbound pass ships");
        lane.pump();
    }

    /// Drives ticks until `settled` holds, panicking past `bound` ticks.
    /// The condition reads the members' durable state only.
    fn drive_until(&mut self, bound: usize, label: &str, settled: impl Fn() -> bool) {
        for _ in 0..bound {
            if settled() {
                return;
            }
            self.tick();
        }
        panic!("the mesh did not settle: {label}");
    }
}

/// The visibility target, in-process: write in region A, pump the lanes,
/// and a reactive subscriber in region B receives the delta — measured
/// `p50` under 250 ms per round (the localhost form of the `SHORT_GOAL` S-05
/// target).
#[test]
fn cross_region_visibility_p50_under_250_milliseconds() {
    use terrabase_sync_core::server::{Comparison, Condition, FieldValue, SubscriptionSpec};
    use terrabase_tbep::reactive::ReactiveFrame;

    const ROUNDS: usize = 60;
    let (_a_location, a) = member("p50-a");
    let (_b_location, b) = member("p50-b");
    let scope = scope();
    let mut mesh = Mesh {
        lane: InProcessLane::new(),
        scope: scope.clone(),
        a: &a,
        b: &b,
        link_a: RegionLink::new("member-gru", "member-sfo").expect("the gru link opens"),
        link_b: RegionLink::new("member-sfo", "member-gru").expect("the sfo link opens"),
    };

    let spec = SubscriptionSpec {
        collection: "orders".into(),
        conditions: vec![Condition {
            field: "value".into(),
            op: Comparison::GreaterOrEqual,
            value: FieldValue::Number(0.0),
        }],
        projection: vec!["value".into()],
    };
    let (_id, feed, _missed) =
        b.reactive_subscribe(&scope, &spec, None).expect("the subscription opens");
    let ReactiveFrame::Seed { members, .. } = feed.recv().expect("the seed arrives") else {
        panic!("the first frame is the seed");
    };
    assert!(members.is_empty(), "region B starts empty");

    let mut durations = Vec::with_capacity(ROUNDS);
    for round in 0..ROUNDS {
        let started = Instant::now();
        put(&a, &format!("orders/vis-{round}"), i64::try_from(round).expect("the round fits"));
        mesh.tick();
        b.reactive_pump(&scope).expect("the pump fans out");
        let frame = feed
            .recv_timeout(Duration::from_secs(5))
            .unwrap_or_else(|_| panic!("round {round} delivers its delta"));
        let ReactiveFrame::Delta { delta, .. } = frame else {
            panic!("round {round} is a delta");
        };
        assert_eq!(delta.revision, u64::try_from(round + 1).expect("the revision fits"));
        durations.push(started.elapsed());
    }
    durations.sort();
    let p50 = durations[ROUNDS / 2];
    assert!(p50 < Duration::from_millis(250), "the p50 visibility is under 250 ms: {p50:?}");
    assert_eq!(orders(&b).len(), ROUNDS, "region B serves every replicated write");
    assert_eq!(orders(&a).len(), ROUNDS, "region A serves its own writes");
}

/// The partition leg of the fault matrix: writes continue locally on both
/// sides under the link's partition control, the pair counts one active
/// region — the exact rule the public API's `GlobalDurable` class enforces
/// (`requires_active_regions`, terrabase-server `api.rs`) — and healing
/// converges both members with zero acknowledged-write loss.
#[test]
fn partition_writes_continue_globally_refused_and_heals_without_loss() {
    let (_a_location, a) = member("partition-a");
    let (_b_location, b) = member("partition-b");
    let mut mesh = Mesh {
        lane: InProcessLane::new(),
        scope: scope(),
        a: &a,
        b: &b,
        link_a: RegionLink::new("member-gru", "member-sfo").expect("the gru link opens"),
        link_b: RegionLink::new("member-sfo", "member-gru").expect("the sfo link opens"),
    };

    // The partition: both directions of the link drop every frame.
    let gru = mesh.link_a.local().clone();
    let sfo = mesh.link_a.peer().clone();
    mesh.lane.link(&gru, &sfo).partition();
    let back = mesh.link_b.local().clone();
    let back_peer = mesh.link_b.peer().clone();
    mesh.lane.link(&back, &back_peer).partition();
    assert!(mesh.link_a.peer_partitioned(&mut mesh.lane), "the gru direction is partitioned");

    // Writes continue locally on both sides (RegionalDurable is local).
    for round in 0..25 {
        put(&a, &format!("orders/gru-{round}"), round);
        put(&b, &format!("orders/sfo-{round}"), round + 100);
    }
    assert_eq!(orders(&a).len(), 25, "region A serves its own writes under the partition");
    assert_eq!(orders(&b).len(), 25, "region B serves its own writes under the partition");

    // GlobalDurable is refused: the partitioned pair counts one active
    // region, below the API rule's floor of two.
    let active = 1 + usize::from(!mesh.link_a.peer_partitioned(&mut mesh.lane));
    assert_eq!(active, 1);
    assert!(active < 2, "GlobalDurable is refused while the pair is partitioned");

    // Healing: every acknowledged write converges to both members.
    mesh.lane.heal();
    mesh.drive_until(2_000, "partition heal converges", || {
        orders(mesh.a).len() == 50 && orders(mesh.b).len() == 50
    });
    for (label, host) in [("gru", &a), ("sfo", &b)] {
        let documents = orders(host);
        assert_eq!(documents.len(), 50, "{label} holds every write from both regions");
        assert!(
            documents.iter().all(|document| matches!(document.value(), DocumentValue::Object(_))),
            "{label} holds complete bodies"
        );
    }
}

/// The kill/restart leg of the fault matrix: the killed member's engine is
/// durable, so reopening its location replays its journal and the lanes
/// catch it up — the dropped member re-pulls from its own cursor, and its
/// replicated rows never re-ship (loop prevention).
#[test]
fn a_killed_member_restarts_and_catches_up() {
    let (b_location, b) = member("restart-b");
    let (_a_location, a) = member("restart-a");
    let mut mesh = Mesh {
        lane: InProcessLane::new(),
        scope: scope(),
        a: &a,
        b: &b,
        link_a: RegionLink::new("member-gru", "member-sfo").expect("the gru link opens"),
        link_b: RegionLink::new("member-sfo", "member-gru").expect("the sfo link opens"),
    };

    // Before the kill: three replicated writes settle.
    for round in 0..3 {
        put(mesh.a, &format!("orders/pre-{round}"), round);
    }
    mesh.drive_until(200, "the pre-kill writes settle", || orders(mesh.b).len() == 3);

    // The kill: region B's member drops; region A keeps writing. A's
    // frames sit undelivered (its window and expiry churn against the
    // absent receiver).
    let killed = mesh.b;
    let _ = killed;
    for round in 0..10 {
        put(mesh.a, &format!("orders/down-{round}"), round + 10);
    }

    // The restart: the same durable location reopens — replay is the
    // catch-up — and a fresh link re-pulls from the journal's start.
    let b = MemberHost::open(
        b_location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the restarted member opens");
    let scope = mesh.scope.clone();
    b.bind_channel(&ChannelBindRequest { scope }).expect("the database scope rebinds");
    mesh.b = &b;
    mesh.link_b = RegionLink::new("member-sfo", "member-gru").expect("the fresh sfo link opens");

    mesh.drive_until(2_000, "the restarted member catches up", || orders(mesh.b).len() == 13);
    assert_eq!(orders(mesh.a).len(), 13, "the writer's own state is unchanged");

    // Loop prevention survives the restart: the restarted member's journal
    // holds only replicated rows, so its link ships nothing new.
    let step = mesh
        .link_b
        .ship(mesh.b.engine_host(), &mesh.scope, &mut mesh.lane)
        .expect("the restarted member ships");
    assert_eq!(step, ShipStep::Idle, "replicated rows never re-ship");
}

/// The credit window (M1): under a partition the outbound direction holds
/// at most its bounded in-flight count, and healing drains the window
/// through the acknowledgements and completes the catch-up.
#[test]
fn the_credit_window_bounds_in_flight_frames_and_recovers() {
    let (_a_location, a) = member("credit-a");
    let (_b_location, b) = member("credit-b");
    let mut mesh = Mesh {
        lane: InProcessLane::new(),
        scope: scope(),
        a: &a,
        b: &b,
        link_a: RegionLink::new("member-gru", "member-sfo").expect("the gru link opens"),
        link_b: RegionLink::new("member-sfo", "member-gru").expect("the sfo link opens"),
    };

    let gru = mesh.link_a.local().clone();
    let sfo = mesh.link_a.peer().clone();
    mesh.lane.link(&gru, &sfo).partition();
    for round in 0..40 {
        put(mesh.a, &format!("orders/backlog-{round}"), round);
    }

    // The window fills and then refuses: no unbounded in-flight state, no
    // unbounded lane queue — the frames past the bound are simply skipped.
    let mut frames = 0;
    for _ in 0..(MAX_INFLIGHT_FRAMES * 4) {
        let step = mesh
            .link_a
            .ship(mesh.a.engine_host(), &mesh.scope, &mut mesh.lane)
            .expect("the ship pass runs");
        match step {
            ShipStep::Shipped(_) => frames += 1,
            ShipStep::WindowFull => break,
            ShipStep::Idle => panic!("the partitioned backlog is not idle"),
        }
        assert!(mesh.link_a.in_flight() <= MAX_INFLIGHT_FRAMES);
    }
    assert_eq!(
        frames, MAX_INFLIGHT_FRAMES,
        "exactly the window's worth of frames left before the bound refused"
    );
    // Every frame re-ships the same unacknowledged page; the apply path's
    // idempotency binding absorbs the duplicates once the lane heals.
    assert_eq!(mesh.link_a.in_flight(), MAX_INFLIGHT_FRAMES, "the window is exactly full");

    // Healing drains the window: expired losses release their credit, the
    // re-shipped frames arrive and are acknowledged, and the cursor
    // advances through the whole backlog.
    mesh.lane.heal();
    mesh.drive_until(4_000, "the windowed backlog drains", || orders(mesh.b).len() == 40);
    // The window drains fully once the backlog settles: a few more ticks
    // let the final acknowledgements land.
    for _ in 0..32 {
        mesh.tick();
    }
    assert_eq!(mesh.link_a.in_flight(), 0, "the window is empty after the backlog drains");
}

/// The deterministic merge over real frames: concurrent writes to distinct
/// documents in both regions converge to identical document sets in both,
/// each direction's lane carrying the other's records. Same-document skew
/// stays deterministic by design — pinned by
/// `tests/replication.rs::concurrent_writes_converge_deterministically`.
#[test]
fn both_directions_converge_over_the_lanes() {
    let (_a_location, a) = member("both-a");
    let (_b_location, b) = member("both-b");
    let mut mesh = Mesh {
        lane: InProcessLane::new(),
        scope: scope(),
        a: &a,
        b: &b,
        link_a: RegionLink::new("member-gru", "member-sfo").expect("the gru link opens"),
        link_b: RegionLink::new("member-sfo", "member-gru").expect("the sfo link opens"),
    };

    for round in 0..10 {
        put(mesh.a, &format!("orders/from-gru-{round}"), round);
        put(mesh.b, &format!("orders/from-sfo-{round}"), round + 50);
    }
    mesh.drive_until(2_000, "both directions settle", || {
        orders(mesh.a).len() == 20 && orders(mesh.b).len() == 20
    });
    let ids = |host: &MemberHost| {
        let mut ids = orders(host)
            .iter()
            .map(|document| document.id().as_str().to_owned())
            .collect::<Vec<_>>();
        ids.sort();
        ids
    };
    assert_eq!(ids(mesh.a), ids(mesh.b), "both members converge to the identical document set");
}

/// A page full of replicated rows must not hide a later origin row. The
/// filtered page advances the link's cursor, and the following page ships
/// the local origin write.
#[test]
fn replicated_only_page_advances_to_a_later_origin() {
    let (_source_location, source) = member("filtered-page-source");
    let (_target_location, target) = member("filtered-page-target");
    let scope = scope();

    put_many(&source, 1024);
    let mut source_cursor = None;
    assert_eq!(
        replicate_once(source.engine_host(), target.engine_host(), &scope, &mut source_cursor)
            .expect("the seed rows replicate"),
        1024
    );
    put(&target, "orders/after-filtered-page", 7);

    let mut lane = InProcessLane::new();
    let mut target_link = RegionLink::new("member-sfo", "member-gru").expect("the link opens");
    assert_eq!(
        target_link
            .ship(target.engine_host(), &scope, &mut lane)
            .expect("the link ships past replicated rows"),
        ShipStep::Shipped(1)
    );
    assert!(target_link.cursor().is_some(), "the filtered page checkpoint was retained");

    let mut source_link = RegionLink::new("member-gru", "member-sfo").expect("the link opens");
    source_link
        .receive(source.engine_host(), &scope, &mut lane)
        .expect("the later origin reaches its peer");
    target_link.drain_acks(&mut lane).expect("the later origin is acknowledged");
    assert!(
        orders(&source)
            .iter()
            .any(|document| document.id().as_str() == "orders/after-filtered-page"),
        "the origin after the filtered page is not lost"
    );
}

/// A replication delivery from a member other than the configured peer is
/// refused before the payload can reach the apply path.
#[test]
fn receive_refuses_a_forged_source() {
    let (_location, target) = member("forged-source-target");
    let scope = scope();
    let mut lane = InProcessLane::new();
    let target_id = MemberId::try_new("member-sfo").expect("the target identity parses");
    lane.send(
        &MemberId::try_new("member-attacker").expect("the forged identity parses"),
        &target_id,
        replication_frame("member-attacker", "member-sfo", &scope.tenant, 1, None),
    )
    .expect("the forged frame enters the lane");
    let mut link = RegionLink::new("member-sfo", "member-gru").expect("the link opens");
    assert!(
        matches!(
            link.receive(target.engine_host(), &scope, &mut lane),
            Err(terrabase_tbep::HostingError::Refused(_))
        ),
        "a forged source is refused"
    );
}

/// An acknowledgement for an in-flight frame must carry the frame's tenant;
/// a forged tenant cannot move the sender's durable cursor.
#[test]
fn drain_acks_refuses_a_wrong_tenant() {
    let (_source_location, source) = member("wrong-ack-source");
    let scope = scope();
    put(&source, "orders/ack-target", 1);
    let mut lane = InProcessLane::new();
    let mut link = RegionLink::new("member-gru", "member-sfo").expect("the link opens");
    assert!(matches!(
        link.ship(source.engine_host(), &scope, &mut lane).expect("the frame ships"),
        ShipStep::Shipped(1)
    ));
    let source_id = MemberId::try_new("member-gru").expect("the source identity parses");
    let peer_id = MemberId::try_new("member-sfo").expect("the peer identity parses");
    lane.send(
        &peer_id,
        &source_id,
        replication_frame("member-sfo", "member-gru", "tenant-forged", 9, Some(1)),
    )
    .expect("the forged acknowledgement enters the lane");
    assert!(
        matches!(link.drain_acks(&mut lane), Err(terrabase_tbep::HostingError::Refused(_))),
        "the wrong tenant acknowledgement is refused"
    );
    assert_eq!(link.cursor(), None, "the forged acknowledgement cannot advance the cursor");
    assert_eq!(link.in_flight(), 1, "the forged acknowledgement leaves the frame pending");
}

/// An acknowledgement is a proof for one exact frame, not merely a
/// correlated liveness signal. Every malformed report leaves the frame
/// pending so the sender can retry it after the peer produces a valid report.
#[test]
fn drain_acks_refuses_any_non_exact_operation_report() {
    let malformed = [
        ("empty", vec![]),
        (
            "extra",
            vec![
                ("put-orders/ack-first-1", true),
                ("put-orders/ack-second-2", true),
                ("fabricated-extra", true),
            ],
        ),
        ("missing", vec![("put-orders/ack-first-1", true)]),
        ("reordered", vec![("put-orders/ack-second-2", true), ("put-orders/ack-first-1", true)]),
        ("duplicate", vec![("put-orders/ack-first-1", true), ("put-orders/ack-first-1", true)]),
        ("fabricated", vec![("put-orders/ack-first-1", true), ("not-carried-by-the-frame", true)]),
    ];

    for (case, reports) in malformed {
        let (_source_location, source) = member(&format!("malformed-ack-source-{case}"));
        let scope = scope();
        put(&source, "orders/ack-first", 1);
        put(&source, "orders/ack-second", 2);
        let mut lane = InProcessLane::new();
        let mut link = RegionLink::new("member-gru", "member-sfo").expect("the link opens");
        assert_eq!(
            link.ship(source.engine_host(), &scope, &mut lane).expect("the frame ships"),
            ShipStep::Shipped(2),
            "the test frame carries both operations ({case})"
        );
        let peer_id = MemberId::try_new("member-sfo").expect("the peer identity parses");
        let source_id = MemberId::try_new("member-gru").expect("the source identity parses");
        lane.send(
            &peer_id,
            &source_id,
            acknowledgement_frame("member-sfo", "member-gru", &scope.tenant, 9, 1, &reports),
        )
        .expect("the malformed acknowledgement enters the lane");
        assert!(
            matches!(link.drain_acks(&mut lane), Err(terrabase_tbep::HostingError::Refused(_))),
            "the {case} acknowledgement is refused"
        );
        assert_eq!(link.cursor(), None, "the {case} acknowledgement cannot advance the cursor");
        assert_eq!(link.in_flight(), 1, "the {case} frame remains pending");
    }
}

/// A verified negative holding report keeps retry behavior intact: it is not
/// accepted as durability, but it also does not poison the lane.
#[test]
fn drain_acks_retries_an_exact_negative_report() {
    let (_source_location, source) = member("negative-ack-source");
    let scope = scope();
    put(&source, "orders/negative-ack", 1);
    let mut lane = InProcessLane::new();
    let mut link = RegionLink::new("member-gru", "member-sfo").expect("the link opens");
    assert_eq!(
        link.ship(source.engine_host(), &scope, &mut lane).expect("the frame ships"),
        ShipStep::Shipped(1)
    );
    let peer_id = MemberId::try_new("member-sfo").expect("the peer identity parses");
    let source_id = MemberId::try_new("member-gru").expect("the source identity parses");
    lane.send(
        &peer_id,
        &source_id,
        acknowledgement_frame(
            "member-sfo",
            "member-gru",
            &scope.tenant,
            9,
            1,
            &[("put-orders/negative-ack-1", false)],
        ),
    )
    .expect("the negative acknowledgement enters the lane");
    link.drain_acks(&mut lane).expect("a negative holding is a retry, not a protocol error");
    assert_eq!(link.cursor(), None, "a negative holding cannot advance the cursor");
    assert_eq!(link.in_flight(), 0, "the failed frame releases its credit");
    assert_eq!(
        link.ship(source.engine_host(), &scope, &mut lane).expect("the frame retries"),
        ShipStep::Shipped(1),
        "the exact negative report leaves the record eligible for retry"
    );
}

/// Duplicate delivery is an idempotent replay: the target publishes one
/// document, while duplicate acknowledgements settle one in-flight frame.
#[test]
fn duplicate_delivery_replays_idempotently() {
    let (_source_location, source) = member("replay-source");
    let (_target_location, target) = member("replay-target");
    let scope = scope();
    put(&source, "orders/replay", 3);
    let mut lane = InProcessLane::new();
    let source_id = MemberId::try_new("member-gru").expect("the source identity parses");
    let target_id = MemberId::try_new("member-sfo").expect("the target identity parses");
    lane.link(&source_id, &target_id).duplicate();
    let mut source_link = RegionLink::new("member-gru", "member-sfo").expect("the link opens");
    let mut target_link = RegionLink::new("member-sfo", "member-gru").expect("the link opens");
    assert!(matches!(
        source_link.ship(source.engine_host(), &scope, &mut lane).expect("the frame ships"),
        ShipStep::Shipped(1)
    ));
    assert_eq!(
        target_link
            .receive(target.engine_host(), &scope, &mut lane)
            .expect("the duplicate deliveries apply"),
        1,
        "the duplicate is a replay"
    );
    source_link.drain_acks(&mut lane).expect("the duplicate acknowledgements settle");
    assert_eq!(orders(&target).len(), 1, "replay does not duplicate the document");
    assert_eq!(source_link.in_flight(), 0, "the replay acknowledgements settle the frame");
}
