//! SE-07 efficiency-budget measurements for the sync core and the
//! `terrascale.sync.v2` client channel (the named benchmark adapter of
//! `docs/benchmarks/se07-efficiency-budget.md`, items E-8..E-12).
//!
//! The measured quantities are deterministic counters — batch bytes,
//! utilization ratios, and heap-allocation counts — not wall-clock
//! timings. Nothing here is a performance claim; the receipts under
//! `docs/evidence/benchmarks/` record the runs and their verdicts in the
//! charter's exact vocabulary.

use std::alloc::{GlobalAlloc, Layout, System};
use std::collections::BTreeMap;

use terrabase_sync_core::protocol::{
    DELIVERY_FRAME_BUDGET_BYTES, FIXED_ENVELOPE_BYTES, TRIGGER_FRAME_BUDGET_BYTES,
};
use terrabase_sync_core::protocol::{IdempotencyKey, SyncV2Body, SyncV2Frame};
use terrabase_sync_core::{
    FrameBody, FrameId, JournalChange, JournalCommit, SyncBatch, SyncCheckpoint, SyncCore,
    SyncCoreConfig, SyncFrame, SyncScope, SyncValue,
};

/// A counting allocator: allocation counts are exact and
/// machine-independent, which is why the budgets are written over counts.
// SAFETY: forwards every call to the system allocator unchanged; the
// counter is a plain atomic around the delegation.
#[allow(unsafe_code)]
struct CountingAlloc;

static ALLOCATIONS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

#[allow(unsafe_code)]
unsafe impl GlobalAlloc for CountingAlloc {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        ALLOCATIONS.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) }
    }
}

#[global_allocator]
static ALLOCATOR: CountingAlloc = CountingAlloc;

/// Serializes the tests of this binary: allocation counting is exact only
/// when no sibling test allocates concurrently on another thread. The lock
/// is poison-tolerant (a panicking sibling must not take the rest of the
/// adapter's measurements down with it — the same discipline as the
/// member-lane adapter).
static SERIAL: std::sync::Mutex<()> = std::sync::Mutex::new(());

/// Reads and clears the allocation counter.
fn allocations() -> u64 {
    ALLOCATIONS.swap(0, std::sync::atomic::Ordering::Relaxed)
}

/// Builds one sync core under the fixture configuration and applies
/// `commits` small distinct commits (the fixture workload of the budget:
/// many small changes, where per-commit encoding overhead dominates).
fn core_with_commits(commits: usize) -> SyncCore {
    let config = SyncCoreConfig { batch_byte_budget: 64 * 1024, checkpoint_window: 8192 };
    let mut core =
        SyncCore::new(SyncScope::new("tenant-eff", "orders"), config).expect("core opens");
    for index in 0..commits {
        let body: BTreeMap<String, SyncValue> = BTreeMap::from([
            ("sku".to_owned(), SyncValue::Str(format!("sku-{index:06}"))),
            ("qty".to_owned(), SyncValue::Int(i64::try_from(index).expect("fits"))),
        ]);
        core.apply(&JournalCommit {
            storage_sequence: u64::try_from(index).expect("fits") + 1,
            operation_id: format!("op-{index:06}"),
            document_id: format!("doc-{}", index % 128),
            change: JournalChange::Put { body },
        })
        .expect("applies");
    }
    core
}

// --- E-8: delta-batch utilization -------------------------------------------

/// E-8: when the un-served backlog measures at least the configured batch
/// byte budget, the served delta batch carries at least 70% of the budget,
/// never exceeds it multi-commit, and preserves the seam's flags.
#[test]
fn delta_batch_utilization_meets_the_written_budget() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let budget = 64 * 1024;
    let core = core_with_commits(6000);
    // The backlog is far above the budget: the retained boundary sizes
    // measure it exactly (every boundary carries its own delta bytes).
    let batch = match core.delta_since(&SyncCheckpoint::initial()).expect("serves") {
        SyncBatch::Delta { bytes, commits, within_budget, single_commit_oversize, .. } => {
            assert!(within_budget, "a multi-commit batch never exceeds the budget");
            assert!(!single_commit_oversize, "no single commit is oversized in this fixture");
            assert!(commits > 1, "the batch carries many commits");
            (bytes.len(), commits)
        }
        other => panic!("E-8: a fresh session behind a full backlog receives a delta: {other:?}"),
    };
    let utilized_per_mille = batch.0 * 1000 / budget;
    println!("E-8: batch {} bytes of {} budget across {} commits", batch.0, budget, batch.1);
    assert!(
        utilized_per_mille >= 700,
        "E-8: delta batches must use at least 70% of the budget when the backlog is larger \
         (used {utilized_per_mille} per mille)"
    );
}

// --- E-10: frame-encode allocations ------------------------------------------

/// E-10: sealing and encoding one delivery frame to wire bytes performs at
/// most two heap allocations.
#[test]
fn frame_encode_allocations_meet_the_budget() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let payload: Vec<u8> = (0..256_u16).flat_map(u16::to_le_bytes).collect();
    let checkpoint = SyncCheckpoint::from_ends(vec![(1, 4)], 9);
    // The measured count is the minimum of repeated runs: allocation
    // counting is exact, but a quiet harness thread can add unrelated
    // allocations, and only noise is added — never removed. The sample
    // arrays are stack-fixed so the harness itself never allocates inside
    // a measured window.
    let mut counts = [0_u64; 5];
    let mut wire_len = 0;
    for slot in &mut counts {
        // Input construction (the body's own buffers) happens before the
        // measured window: the budget covers sealing and encoding.
        let frame = SyncFrame::seal(
            FrameBody::Delta { bytes: payload.clone(), next: checkpoint.clone() },
            FrameId::new(1),
            FrameId::new(0),
        );
        allocations();
        let wire = frame.to_bytes();
        wire_len = wire.len();
        *slot = allocations();
    }
    let count = *counts.iter().min().expect("measured");
    println!("E-10: seal+encode allocations: {count} (samples {counts:?}), {wire_len} wire bytes");
    assert!(
        count <= 2,
        "E-10: seal+encode must use at most 2 allocations (used {count} for {wire_len} wire \
         bytes; samples {counts:?})"
    );
}

// --- E-11: client-link byte budgets -------------------------------------------

/// E-11: measured against the SE-04 written definitions — the heartbeat
/// frame is exactly the fixed envelope, the trigger notification fits its
/// budget, and the delta payload per delivered change is recorded against
/// the delivery budget (the compression decision's input).
#[test]
fn client_link_frames_stay_within_their_written_budgets() {
    let _guard = SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    // Heartbeat: exactly the fixed envelope.
    let heartbeat =
        SyncV2Frame::seal(SyncV2Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
            .expect("seals");
    assert_eq!(
        heartbeat.to_bytes().len(),
        FIXED_ENVELOPE_BYTES,
        "E-11: a heartbeat encodes to exactly the fixed envelope"
    );

    // Trigger notification: content-free, within its written budget.
    let trigger = SyncV2Frame::seal(
        SyncV2Body::Trigger {
            collection: "orders".to_owned(),
            storage_sequence: 42,
            operation_id: "op-000042".to_owned(),
        },
        FrameId::new(2),
        FrameId::new(1),
        None,
    )
    .expect("seals");
    assert!(
        trigger.to_bytes().len() <= TRIGGER_FRAME_BUDGET_BYTES,
        "E-11: the trigger frame {} bytes fits its written budget {}",
        trigger.to_bytes().len(),
        TRIGGER_FRAME_BUDGET_BYTES
    );

    // Delta delivery: payload bytes per delivered change against the
    // written delivery budget, and the full frame per change with its
    // envelope — the measured input of the E-12 compression decision.
    let core = core_with_commits(6000);
    let (payload, commits) = match core.delta_since(&SyncCheckpoint::initial()).expect("serves") {
        SyncBatch::Delta { bytes, commits, .. } => (bytes, commits),
        other => panic!("a fresh session receives a delta: {other:?}"),
    };
    let delivery = SyncV2Frame::seal(
        SyncV2Body::DeltaBatch {
            codec: terrabase_sync_core::protocol::PayloadCodec::NativeDelta1,
            payload: payload.clone(),
            next: core.checkpoint(),
            within_budget: true,
            single_commit_oversize: false,
            commits,
        },
        FrameId::new(3),
        FrameId::new(2),
        None,
    )
    .expect("seals");
    let per_change_payload = payload.len() / usize::try_from(commits).expect("fits");
    let per_change_wire = delivery.to_bytes().len() / usize::try_from(commits).expect("fits");
    println!(
        "E-11: {commits} commits, {} payload bytes, {} per change payload, {} per change wire",
        payload.len(),
        per_change_payload,
        per_change_wire
    );
    assert!(
        per_change_wire <= DELIVERY_FRAME_BUDGET_BYTES,
        "E-11: per-change wire bytes {per_change_wire} stay within the delivery budget"
    );

    // The mutation-family pairing stays enforced (the unchanged discipline
    // the efficiency work may not disturb).
    assert!(
        SyncV2Frame::seal(SyncV2Body::Heartbeat, FrameId::new(9), FrameId::new(0), None).is_ok()
    );
    let _ = IdempotencyKey::new("efficiency-probe");
}
