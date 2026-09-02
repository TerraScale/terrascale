//! SE-07 efficiency-budget measurements for the member lane (the named
//! benchmark adapter of `docs/benchmarks/se07-efficiency-budget.md`).
//!
//! Every budget item E-1..E-7 of the member lane maps to one test here.
//! The measured quantities are deterministic counters — frames, rounds,
//! heap allocations, queue depths — not wall-clock timings, so a value
//! reported twice from the same source identity is the same number.
//! Nothing here is a performance claim; the receipts under
//! `bench/RESULTS.md/` record the runs and use the charter's exact
//! verdict vocabulary.

use std::alloc::{GlobalAlloc, Layout, System};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase_member_transport::nats_usage::{ConfigName, ConfigRecord, LaneSettings, UsageError};
use terrabase_member_transport::{
    AcknowledgementLevel, CollectionName, DEFAULT_CATCH_UP_BATCH, DispatchReport, DocumentId,
    Envelope, EnvelopeError, FrameKind, InProcessLane, LaneConfig, LaneDocument, LaneMember,
    LaneWriteRequest, MAX_PAYLOAD_BYTES, MemberError, MemberId, MemberTransport, OperationId,
    Payload, ReplicationOutcome, TenantId, TopologyView, decode, encode, lane_topology,
};

/// The shared tenant of the efficiency fixtures.
const TENANT: &str = "tenant-eff";

/// A counting allocator: the deterministic stand-in for allocation
/// profiling. Counting allocations is exact and machine-independent, which
/// is why the budgets are written over counts, not bytes-per-second.
// SAFETY: forwards every call to the system allocator unchanged; the
// counter is a plain atomic around the delegation.
#[allow(unsafe_code)]
struct CountingAlloc;

static ALLOCATIONS: AtomicU64 = AtomicU64::new(0);

/// The bytes allocated in a measured window: the size side of the counter,
/// used where the defect is capacity amplification rather than count.
static ALLOCATED_BYTES: AtomicU64 = AtomicU64::new(0);

#[allow(unsafe_code)]
unsafe impl GlobalAlloc for CountingAlloc {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
        ALLOCATED_BYTES
            .fetch_add(u64::try_from(layout.size()).unwrap_or(u64::MAX), Ordering::Relaxed);
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) }
    }
}

#[global_allocator]
static ALLOCATOR: CountingAlloc = CountingAlloc;

/// Serializes the tests of this binary: allocation counting is exact only
/// when no sibling test allocates concurrently on another thread.
static SERIAL: std::sync::Mutex<()> = std::sync::Mutex::new(());

/// Reads and clears the allocation counter.
fn allocations() -> u64 {
    ALLOCATIONS.swap(0, Ordering::Relaxed)
}

/// Reads and clears the allocated-bytes counter.
fn allocated_bytes() -> u64 {
    ALLOCATED_BYTES.swap(0, Ordering::Relaxed)
}

/// One unique temporary directory per open.
fn temp_dir(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    let dir = std::env::temp_dir()
        .join(format!("terrabase-se07-eff-{tag}-{}-{index}", std::process::id()));
    std::fs::remove_dir_all(&dir).ok();
    dir
}

/// One lane-configured member over a fresh durable journal location.
fn open_member(name: &str, catch_up_batch: u32) -> LaneMember {
    let config = LaneConfig { topology: lane_topology(), catch_up_batch };
    LaneMember::try_new(config, MemberId::try_new(name).expect("valid id"), &temp_dir(name))
        .expect("member opens")
}

/// The lane tenant as a typed identity.
fn tenant() -> TenantId {
    TenantId::try_new(TENANT).expect("valid tenant")
}

/// Commits `count` operations on `writer`, each putting one document.
fn commit_ops(writer: &mut LaneMember, count: usize, prefix: &str) {
    for index in 0..count {
        let request = LaneWriteRequest::new(
            OperationId::try_new(&format!("{prefix}-{index}")).expect("valid operation"),
            "main",
        )
        .expect("valid request")
        .with_put(LaneDocument {
            id: DocumentId::try_new(&format!("doc-{index}")).expect("valid document"),
            collection: CollectionName::try_new("users").expect("valid collection"),
            value: format!("{prefix}-value-{index}"),
        });
        writer.commit_write(&tenant(), request).expect("write commits");
    }
}

/// Drives bounded catch-up rounds from `source` to `follower` until the
/// follower reports caught up, returning the rounds used.
fn drive_catch_up(
    lane: &mut InProcessLane,
    source: &mut LaneMember,
    follower: &mut LaneMember,
    max_rounds: usize,
) -> usize {
    let source_id = source.id().clone();
    let mut rounds = 0;
    loop {
        rounds += 1;
        assert!(rounds <= max_rounds, "catch-up exceeds the round bound");
        follower.request_catch_up(lane, &source_id, &tenant()).expect("request sends");
        lane.pump();
        source.dispatch_under(lane, &tenant()).expect("source serves");
        lane.pump();
        let report = follower.dispatch_under(lane, &tenant()).expect("follower applies");
        if report.caught_up {
            return rounds;
        }
    }
}

/// The follower's durable commit count for the fixture tenant.
fn journal_len(member: &LaneMember) -> usize {
    member.journal().tenant_commits(&tenant(), 0, u32::MAX / 2).expect("journal read").len()
}

// --- E-1: catch-up throughput ----------------------------------------------

/// E-1: a follower `B` commits behind catches up within
/// `ceil(B / catch_up_batch) + 1` bounded rounds.
#[test]
fn catch_up_rounds_stay_within_the_batch_bound() {
    const BACKLOG: usize = 512;
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    for (batch, bound) in [(DEFAULT_CATCH_UP_BATCH, 17usize), (128, 5)] {
        let mut lane = InProcessLane::new();
        let mut source = open_member("m-a1", batch);
        let mut follower = open_member("m-b1", batch);
        commit_ops(&mut source, BACKLOG, &format!("op-b{batch}"));
        let rounds = drive_catch_up(&mut lane, &mut source, &mut follower, 64);
        println!("E-1: backlog {BACKLOG} at batch {batch}: {rounds} rounds (bound {bound})");
        assert_eq!(journal_len(&follower), BACKLOG, "the follower applies the whole backlog");
        assert!(
            rounds <= bound,
            "E-1: {BACKLOG} commits behind catch up in {rounds} rounds, bound {bound} \
             (batch {batch})"
        );
    }
}

// --- E-2: flow-control bounds of the shared lane settings ------------------

/// E-2: the NATS-shared lane settings validate their flow-control knob to
/// the closed written range, failing closed outside it. (The pump window
/// knob this budget originally bounded left the settings with the SE-06
/// causal-drain repair; the surviving catch-up batch keeps the bound.)
#[test]
fn flow_control_bounds_validate_the_shared_lane_settings() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let settings = |batch: u32| LaneSettings { catch_up_batch: batch };
    // The closed range itself accepts.
    assert!(ConfigRecord::new(ConfigName::LaneSettings, 1, settings(8)).is_ok());
    assert!(ConfigRecord::new(ConfigName::LaneSettings, 2, settings(256)).is_ok());
    // Outside the closed range fails closed.
    for invalid in [settings(7), settings(257), settings(1), settings(512)] {
        assert!(
            matches!(
                ConfigRecord::new(ConfigName::LaneSettings, 1, invalid),
                Err(UsageError::InvalidRecord)
            ),
            "E-2: settings {invalid:?} must fail the flow-control bounds"
        );
    }
}

// --- E-5: slow-consumer isolation -------------------------------------------

/// Builds one filler frame so a test can occupy a link's bounded capacity.
fn filler_frame(from: &MemberId, to: &MemberId) -> Envelope {
    Envelope {
        kind: FrameKind::CatchUpRequest,
        message_id: 1,
        correlation_id: None,
        causation_id: None,
        idempotency_key: None,
        source: from.clone(),
        target: to.clone(),
        issued_at: terrabase_member_transport::LaneInstant::at_ticks(0),
        expires_at: None,
        range: None,
        epoch: None,
        payload: Payload::CatchUpRequest { tenant: tenant(), after: None, limit: 1 },
    }
}

/// E-5: a member whose inbound direction sits at its bounded capacity is
/// deferred observably and never aborts the dispatch pass: the healthy
/// peer's request in the same pass is served.
#[test]
fn slow_consumer_does_not_stall_healthy_catch_up() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut lane = InProcessLane::new();
    let mut writer = open_member("m-a1", DEFAULT_CATCH_UP_BATCH);
    let mut slow = open_member("m-b1", DEFAULT_CATCH_UP_BATCH);
    let mut healthy = open_member("m-g1", DEFAULT_CATCH_UP_BATCH);
    commit_ops(&mut writer, 8, "op-slow");
    let (writer_id, slow_id, healthy_id) =
        (writer.id().clone(), slow.id().clone(), healthy.id().clone());

    // Occupy the writer->slow direction to its bounded capacity: frames
    // held back by a long delay fill the bounded hold-back queue.
    lane.link(&writer_id, &slow_id).delay_pumps(100_000);
    for _ in 0..terrabase_member_transport::MAX_LINK_FRAMES {
        let filler = encode(&filler_frame(&writer_id, &slow_id)).expect("encodes");
        lane.send(&writer_id, &slow_id, filler).expect("fills the bounded direction");
    }
    let slow_before = lane.link_stats(&writer_id, &slow_id);

    // The slow peer requests first (canonical link order), then the
    // healthy peer; the writer's dispatch pass must still serve the
    // healthy peer.
    slow.request_catch_up(&mut lane, &writer_id, &tenant()).expect("slow requests");
    healthy.request_catch_up(&mut lane, &writer_id, &tenant()).expect("healthy requests");
    lane.pump();
    let report: Result<DispatchReport, MemberError> = writer.dispatch_under(&mut lane, &tenant());
    let report = report.expect("E-5: a slow consumer defers; the dispatch pass completes");
    assert_eq!(report.served, 1, "the healthy peer's request is served in the same pass");
    assert!(lane.pending(&healthy_id) > 0, "E-5: the healthy peer receives its catch-up batch");
    let slow_after = lane.link_stats(&writer_id, &slow_id);
    assert_eq!(
        slow_after.queued + slow_after.held,
        slow_before.queued + slow_before.held,
        "the slow direction stays at its bounded capacity, never beyond"
    );
    assert!(
        slow_after.overflowed >= slow_before.overflowed,
        "the rejected reply is counted, never silently buffered"
    );
}

/// E-5: link queues stay bounded while a consumer stops draining, and the
/// healthy members still converge around it.
#[test]
fn link_queues_stay_bounded_under_a_silent_consumer() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut lane = InProcessLane::new();
    let mut writer = open_member("m-a1", DEFAULT_CATCH_UP_BATCH);
    let silent = open_member("m-b1", DEFAULT_CATCH_UP_BATCH);
    let mut follower = open_member("m-g1", DEFAULT_CATCH_UP_BATCH);
    commit_ops(&mut writer, 16, "op-silent");
    let (writer_id, silent_id) = (writer.id().clone(), silent.id().clone());

    // The silent consumer never drains its inbound direction: every frame
    // beyond the bound is counted and dropped (or rejected with the typed
    // error), and the queue never grows past the bound.
    for index in 0..(terrabase_member_transport::MAX_LINK_FRAMES + 10) {
        let filler = encode(&filler_frame(&writer_id, &silent_id)).expect("encodes");
        let _ = lane.send(&writer_id, &silent_id, filler);
        let stats = lane.link_stats(&writer_id, &silent_id);
        assert!(
            stats.queued + stats.held <= terrabase_member_transport::MAX_LINK_FRAMES as u64,
            "E-5: the silent consumer's queue stays bounded (offered {})",
            index + 1
        );
    }
    let stats = lane.link_stats(&writer_id, &silent_id);
    assert_eq!(
        stats.overflowed + stats.dropped,
        10,
        "the bounded link counts every frame it could not buffer, never silently"
    );

    // The healthy pair converges while the silent consumer sits full.
    let rounds = drive_catch_up(&mut lane, &mut writer, &mut follower, 16);
    assert_eq!(journal_len(&follower), 16, "the healthy follower catches up fully");
    assert!(rounds <= 2, "a 16-commit backlog takes at most two default rounds");
}

// --- E-6: allocation-per-applied-commit --------------------------------------

/// Applies `updates` delivered commits onto a follower that already holds
/// `documents` documents, returning the heap allocations per applied
/// commit of the catch-up drive.
fn allocations_per_applied_commit(documents: usize, updates: usize) -> u64 {
    let mut lane = InProcessLane::new();
    let mut source = open_member("m-a1", DEFAULT_CATCH_UP_BATCH);
    let mut follower = open_member("m-b1", DEFAULT_CATCH_UP_BATCH);
    commit_ops(&mut source, documents, &format!("op-d{documents}"));
    // Bring the follower level with the initial documents first: the
    // measured window covers only the update commits.
    drive_catch_up(&mut lane, &mut source, &mut follower, 512);
    assert_eq!(journal_len(&follower), documents);

    for index in 0..updates {
        let request = LaneWriteRequest::new(
            OperationId::try_new(&format!("op-u{documents}-{index}")).expect("valid operation"),
            "main",
        )
        .expect("valid request")
        .with_put(LaneDocument {
            id: DocumentId::try_new(&format!("doc-{index}")).expect("valid document"),
            collection: CollectionName::try_new("users").expect("valid collection"),
            value: format!("updated-{index}"),
        });
        source.commit_write(&tenant(), request).expect("write commits");
    }
    let source_id = source.id().clone();
    allocations();
    let mut applied = 0usize;
    let mut rounds = 0;
    loop {
        rounds += 1;
        assert!(rounds < 64, "catch-up terminates");
        follower.request_catch_up(&mut lane, &source_id, &tenant()).expect("request sends");
        lane.pump();
        source.dispatch_under(&mut lane, &tenant()).expect("source serves");
        lane.pump();
        let report = follower.dispatch_under(&mut lane, &tenant()).expect("follower applies");
        applied += report.applied;
        if report.caught_up {
            break;
        }
    }
    let total = allocations();
    assert_eq!(applied, updates, "every update applies exactly once");
    u64::try_from(applied).map(|applied| total / applied).expect("positive divisor")
}

/// E-6: heap allocations while applying delivered commits are
/// independent of the tenant's document count: the large state's count
/// stays within amortized container-growth slack (at most 1.5x the small
/// state's count), where the pre-change apply path cloned the whole
/// derived document state per commit and grew linearly (5x at D=512).
#[test]
fn apply_allocations_are_independent_of_tenant_document_count() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let per_commit_small = allocations_per_applied_commit(32, 64);
    let per_commit_large = allocations_per_applied_commit(512, 64);
    println!(
        "E-6: allocations per applied commit: {per_commit_small} at D=32, {per_commit_large} at \
         D=512"
    );
    assert!(
        per_commit_large <= per_commit_small + per_commit_small / 2,
        "E-6: allocations per applied commit must not grow with the document count beyond \
         amortized container-growth slack ({per_commit_large} at 512 documents vs \
         {per_commit_small} at 32)"
    );
}

// --- E-7: bounded push serving -----------------------------------------------

/// Measures the allocations of pushing the journal's most recent operation
/// to a peer, at the given journal length.
fn push_allocations_at_length(length: usize) -> u64 {
    let mut lane = InProcessLane::new();
    let mut writer = open_member("m-a1", DEFAULT_CATCH_UP_BATCH);
    let peer = open_member("m-g1", DEFAULT_CATCH_UP_BATCH);
    commit_ops(&mut writer, length, &format!("op-p{length}"));
    let operation =
        OperationId::try_new(&format!("op-p{length}-{}", length - 1)).expect("valid operation");
    allocations();
    writer.send_push(&mut lane, peer.id(), &tenant(), &operation).expect("push sends");
    allocations()
}

/// E-7: serving one pushed operation reads bounded journal pages
/// regardless of journal length.
#[test]
fn push_serves_from_bounded_journal_pages() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let short = push_allocations_at_length(32);
    let long = push_allocations_at_length(300);
    println!("E-7: push allocations: {short} at journal 32, {long} at journal 300");
    assert!(
        long <= short + 4,
        "E-7: serving one push must not scan the journal ({long} allocations at length 300 \
         vs {short} at length 32)"
    );
}

// --- Baseline: the per-operation acknowledgement path ------------------------

/// The measured baseline of the per-operation acknowledgement path: K
/// operations acknowledged at `AllRegions` through one `wait_ack` per
/// operation. The batched path of E-3/E-4 replaces exactly this outcome;
/// its frame counts are the comparison's before side.
#[test]
fn acknowledgement_frame_economy_per_operation_path() {
    use terrabase_member_transport::LaneHarness;
    const OPERATIONS: usize = 8;
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut harness =
        LaneHarness::new("eff-ack-baseline", terrabase_member_transport::InProcessLane::new());
    for index in 0..OPERATIONS {
        harness.write("m-a1", TENANT, &format!("op-ack-{index}"), "doc-ack", "ack-value");
    }
    let mut acknowledged = 0;
    for index in 0..OPERATIONS {
        if let ReplicationOutcome::Acknowledged(report) = harness.wait_ack(
            "m-a1",
            TENANT,
            &format!("op-ack-{index}"),
            AcknowledgementLevel::AllRegions,
            64,
        ) {
            assert_eq!(report.contributed.len(), 3, "every Region contributed");
            acknowledged += 1;
        }
    }
    assert_eq!(acknowledged, OPERATIONS, "the baseline path acknowledges every operation");
    let mut push_frames = 0;
    let mut ack_frames = 0;
    for (kind, bytes) in harness.transport.class_bytes() {
        match kind {
            FrameKind::Push => push_frames = bytes.frames,
            FrameKind::AckReport => ack_frames = bytes.frames,
            _ => {}
        }
    }
    // Baseline fact (recorded in the receipt): the per-operation path
    // multiplies frames with the operation count.
    println!(
        "ack-baseline: {OPERATIONS} operations at AllRegions used {push_frames} push frames \
         and {ack_frames} ack-report frames"
    );
    assert!(push_frames >= OPERATIONS as u64, "the baseline path pushes per operation");
    assert!(ack_frames >= OPERATIONS as u64, "the baseline path answers per operation");
    harness.clean_up();
}

// --- E-3/E-4: the batched acknowledgement path --------------------------------

/// E-3/E-4: acknowledging a batch of K committed operations sends at most
/// one push-class frame per peer per round, and the peers answer with at
/// most one acknowledgement-class frame per peer per round — for any K.
#[test]
fn batched_acknowledgement_uses_bounded_frames_per_round() {
    use terrabase_member_transport::LaneHarness;
    const OPERATIONS: usize = 8;
    const PEERS: u64 = 5;
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut harness =
        LaneHarness::new("eff-ack-batched", terrabase_member_transport::InProcessLane::new());
    let operations: Vec<String> =
        (0..OPERATIONS).map(|index| format!("op-batch-{index}")).collect();
    for (index, operation) in operations.iter().enumerate() {
        harness.write("m-a1", TENANT, operation, &format!("doc-batch-{index}"), "batch-value");
    }
    let names: Vec<&str> = operations.iter().map(String::as_str).collect();
    let outcomes =
        harness.wait_ack_batch("m-a1", TENANT, &names, AcknowledgementLevel::AllRegions, 64);
    for (name, outcome) in &outcomes {
        match outcome {
            ReplicationOutcome::Acknowledged(report) => {
                assert_eq!(report.contributed.len(), 3, "{name}: every Region contributed");
            }
            other => panic!("{name}: the batched path acknowledges at AllRegions: {other:?}"),
        }
    }
    assert_eq!(outcomes.len(), OPERATIONS, "every operation resolves");
    let mut push_frames = 0;
    let mut ack_frames = 0;
    for (kind, bytes) in harness.transport.class_bytes() {
        match kind {
            FrameKind::Push => push_frames = bytes.frames,
            FrameKind::AckReport | FrameKind::AckBatch => ack_frames += bytes.frames,
            _ => {}
        }
    }
    println!(
        "ack-batched: {OPERATIONS} operations at AllRegions used {push_frames} push frames \
         and {ack_frames} acknowledgement frames"
    );
    assert!(
        push_frames <= PEERS,
        "E-3: one push frame per peer per round, not per operation ({push_frames} for \
         {OPERATIONS} operations)"
    );
    assert!(
        ack_frames <= PEERS,
        "E-4: one acknowledgement frame per peer per round, not per operation ({ack_frames} for \
         {OPERATIONS} operations)"
    );
    harness.clean_up();
}

// --- Repair (warden loop 1): the batched push's fencing invariant --------------

/// One three-Region topology whose writer owns two ranges at different
/// epochs — the fixture a mixed fencing batch must refuse under.
fn two_range_topology() -> TopologyView {
    let mut view = TopologyView::new();
    for region in ["r-alpha", "r-beta", "r-gamma"] {
        view = view.with_region(region).expect("valid region");
    }
    for (member, region) in [("m-a1", "r-alpha"), ("m-b1", "r-beta"), ("m-g1", "r-gamma")] {
        view = view.with_member(member, region, &format!("fd-{region}")).expect("valid member");
    }
    view.with_owner_at_epoch("main", "m-a1", 1)
        .expect("valid owner")
        .with_owner_at_epoch("aux", "m-a1", 4)
        .expect("valid owner")
        .validated()
        .expect("valid two-range topology")
}

/// One lane-configured member over a custom topology and fresh location.
fn open_member_on(topology: &TopologyView, name: &str) -> LaneMember {
    let config = LaneConfig { topology: topology.clone(), catch_up_batch: DEFAULT_CATCH_UP_BATCH };
    LaneMember::try_new(config, MemberId::try_new(name).expect("valid id"), &temp_dir(name))
        .expect("member opens")
}

/// Commits one operation on `writer` in the named range.
fn commit_one(writer: &mut LaneMember, operation: &str, range: &str, document: &str) {
    let request =
        LaneWriteRequest::new(OperationId::try_new(operation).expect("valid operation"), range)
            .expect("valid request")
            .with_put(LaneDocument {
                id: DocumentId::try_new(document).expect("valid document"),
                collection: CollectionName::try_new("users").expect("valid collection"),
                value: "fencing-value".to_owned(),
            });
    writer.commit_write(&tenant(), request).expect("write commits");
}

/// A batched push whose named operations do not resolve to a single
/// (range, epoch) fencing identity refuses with the typed error and sends
/// nothing: one frame fences every commit it carries under one identity,
/// so a mixed batch must never ride fenced under whichever identity the
/// sender happened to resolve last.
#[test]
fn a_mixed_fencing_batch_refuses_instead_of_fencing_under_one_identity() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let topology = two_range_topology();
    let mut writer = open_member_on(&topology, "m-a1");
    let peer = open_member_on(&topology, "m-b1");
    commit_one(&mut writer, "op-fence-main", "main", "doc-fence-main");
    commit_one(&mut writer, "op-fence-main-2", "main", "doc-fence-main-2");
    commit_one(&mut writer, "op-fence-aux", "aux", "doc-fence-aux");

    let mut lane = InProcessLane::new();
    let mixed = writer.send_push_batch(
        &mut lane,
        peer.id(),
        &tenant(),
        &[
            OperationId::try_new("op-fence-main").expect("valid operation"),
            OperationId::try_new("op-fence-aux").expect("valid operation"),
        ],
    );
    assert!(
        matches!(mixed, Err(MemberError::MixedFencingIdentity { first: (_, 1), other: (_, 4) })),
        "a mixed (range, epoch) batch must refuse with the typed error, got {mixed:?}"
    );
    assert_eq!(lane.pending(peer.id()), 0, "the refused batch sends nothing");

    // The uniform batch on the same writer still sends and carries every
    // named commit (the invariant must not cost the batching itself).
    let uniform = writer
        .send_push_batch(
            &mut lane,
            peer.id(),
            &tenant(),
            &[
                OperationId::try_new("op-fence-main").expect("valid operation"),
                OperationId::try_new("op-fence-main-2").expect("valid operation"),
            ],
        )
        .expect("a uniform (range, epoch) batch sends");
    assert_eq!(uniform, 2, "the uniform batch carries both commits");
    assert!(lane.pending(peer.id()) > 0, "the uniform batch reaches the wire");
}

// --- Repair (warden loop 1): decode capacity is payload-bounded ------------------

/// A hostile element count inside a digest-valid frame must not amplify
/// decode memory: the decoder's element capacity is bounded by the bytes
/// the declared payload actually holds, never by the declared count alone.
#[test]
fn hostile_element_counts_do_not_amplify_decode_memory() {
    use terrabase_member_transport::envelope::DIGEST_BYTES;

    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let probe = Envelope {
        kind: FrameKind::AckBatch,
        message_id: 7,
        correlation_id: None,
        causation_id: None,
        idempotency_key: None,
        source: MemberId::try_new("m-a1").expect("valid member"),
        target: MemberId::try_new("m-b1").expect("valid member"),
        issued_at: terrabase_member_transport::LaneInstant::at_ticks(0),
        expires_at: None,
        range: Some("main".to_owned()),
        epoch: Some(1),
        payload: Payload::AckBatch {
            tenant: tenant(),
            reports: vec![
                (OperationId::try_new("op-cap-a").expect("valid operation"), true),
                (OperationId::try_new("op-cap-b").expect("valid operation"), false),
            ],
        },
    };
    let frame = encode(&probe).expect("the probe frame encodes");
    let mut hostile = frame.bytes.clone();
    // The payload layout is fixed by the codec: the tenant string (u32
    // length plus bytes), then the report count. Rewrite the count to the
    // payload bound — the largest value the fail-closed check admits.
    let payload_start = hostile.len() - DIGEST_BYTES - frame.payload_len;
    let count_at = payload_start + 4 + TENANT.len();
    hostile[count_at..count_at + 4].copy_from_slice(
        &u32::try_from(MAX_PAYLOAD_BYTES).expect("the payload bound fits a u32").to_le_bytes(),
    );
    // Re-sign so the digest barrier passes and the probe reaches the
    // payload decode — the discipline under test is the decoder's own
    // memory bound, not the digest.
    let signed_len = hostile.len() - DIGEST_BYTES;
    let digest = ring::digest::digest(&ring::digest::SHA256, &hostile[..signed_len]);
    hostile[signed_len..].copy_from_slice(digest.as_ref());

    allocated_bytes();
    let outcome = decode(&hostile);
    let spent = allocated_bytes();
    assert!(
        matches!(outcome, Err(EnvelopeError::Truncated)),
        "the lying count still fails closed: {outcome:?}"
    );
    assert!(
        spent <= 64 * 1024,
        "decode of a declared count of {MAX_PAYLOAD_BYTES} must pre-allocate at most \
         payload-proportional memory, spent {spent} bytes"
    );
}

/// Keeps the topology export referenced so the fixture is self-describing.
#[allow(dead_code, reason = "the fixture names the lane topology it builds on")]
fn fixture_topology() -> TopologyView {
    lane_topology()
}
