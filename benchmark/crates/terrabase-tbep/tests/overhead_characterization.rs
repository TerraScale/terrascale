//! SE-07 TBEP overhead characterization (the named benchmark adapter of
//! `docs/benchmarks/se07-efficiency-budget.md`, items E-13..E-14).
//!
//! The measured quantities are deterministic — byte counts per frame and
//! write-call counts — not wall-clock timings. The per-frame overhead is a
//! property of the envelope structure, so the saturated-identity case
//! exercises the worst case exactly; nothing here is a latency or
//! throughput claim.

use std::alloc::{GlobalAlloc, Layout, System};
use std::io::Write as _;

use terrabase_tbep::envelope::{envelope_overhead, stream_wire_bytes};
use terrabase_tbep::generated::{
    BackboneSubscribeRequest, DocumentReadRequest, Error as ErrorBody, ErrorCode,
    FIXED_ENVELOPE_BYTES, FrameKind, MAX_FRAME_BYTES, STREAM_PREFIX_BYTES, SubscriptionStart,
    SupervisionHealthRequest, SyncDeltaPullResponse, TriggerNotification,
};
use terrabase_tbep::{IdempotencyKey, TbepFrame};

/// A counting allocator: allocation counts are exact and
/// machine-independent, which is why the budgets are written over counts.
// SAFETY: forwards every call to the system allocator unchanged; the
/// counter is a plain atomic around the delegation.
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

/// Reads and clears the allocation counter.
fn allocations() -> u64 {
    ALLOCATIONS.swap(0, std::sync::atomic::Ordering::Relaxed)
}

/// Serializes every test that measures through the global allocation
/// counter: the counter is process-global and the default test harness
/// runs this binary's tests on parallel threads, so two measuring tests
/// overlapping would each count the other's allocations. The lock makes
/// every measured window single-tenant (poisoned is fine — a panicked
/// measurement leaves the counter's semantics intact for the next test).
static SERIAL: std::sync::Mutex<()> = std::sync::Mutex::new(());

/// Takes the measurement lock for one test.
fn measure_exclusively() -> std::sync::MutexGuard<'static, ()> {
    SERIAL.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// One `io::Write` sink that counts its write calls: the deterministic
/// stand-in for syscall counting.
#[derive(Default)]
struct CountingWriter {
    /// Write calls observed.
    writes: usize,
    /// Total bytes taken.
    bytes: usize,
}

impl std::io::Write for CountingWriter {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        self.writes += 1;
        self.bytes += buf.len();
        Ok(buf.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

/// E-13 and E-14 together (one test so the allocation-counting windows
/// stay quiet — see the E-14 case below): the registry is code-swept
/// closed, a representative maximal-identity frame of every written
/// budget class keeps its measured per-frame overhead within the
/// generated fixed envelope bound (plus the stream prefix on the wire),
/// and the stream write itself rides one pre-sized buffer and one write
/// call.
#[test]
#[allow(clippy::too_many_lines, reason = "one representative frame per written budget class")]
fn every_registry_kind_overhead_stays_within_the_fixed_bound() {
    let _measure = measure_exclusively();
    // Code sweep: every u16 either decodes to exactly one registered kind
    // or fails; the registry is closed under its codes.
    let mut kinds: Vec<FrameKind> = Vec::new();
    for code in 0..=u16::MAX {
        if let Some(kind) = FrameKind::from_code(code) {
            assert_eq!(kind.code(), code, "a kind round-trips its own code");
            if !kinds.contains(&kind) {
                kinds.push(kind);
            }
        }
    }
    assert!(
        kinds.len() >= 46,
        "the registry sweep finds every generated kind (found {})",
        kinds.len()
    );

    // One representative maximal-identity frame per written budget class.
    let maximal_identities = 0x0101_0101_0101_0101_u64.wrapping_mul(u64::from(u16::MAX));
    let representatives: [(&str, TbepFrame); 5] = [
        (
            "control",
            TbepFrame::seal(
                ErrorBody { code: ErrorCode::MalformedFrame, detail: String::new() },
                maximal_identities,
                u64::MAX,
                None,
            )
            .expect("seals"),
        ),
        (
            "operation",
            TbepFrame::seal(
                DocumentReadRequest {
                    collection: "orders".into(),
                    document: "d-000042".into(),
                    level: None,
                },
                maximal_identities,
                u64::MAX,
                None,
            )
            .expect("seals"),
        ),
        (
            "subscription",
            TbepFrame::seal(
                BackboneSubscribeRequest {
                    subscription_id: u64::MAX,
                    collections: Some(vec!["orders".into()]),
                    from: SubscriptionStart::Live,
                },
                maximal_identities,
                u64::MAX,
                None,
            )
            .expect("seals"),
        ),
        (
            "trigger",
            TbepFrame::seal(
                TriggerNotification {
                    subscription_id: u64::MAX,
                    collection: "orders".into(),
                    sequence: u64::MAX,
                    operation_id: "op-000042".into(),
                },
                maximal_identities,
                u64::MAX,
                None,
            )
            .expect("seals"),
        ),
        (
            "delivery",
            TbepFrame::seal(
                SyncDeltaPullResponse {
                    session_id: u64::MAX,
                    payload_codec: "terrabase-native/1".into(),
                    payload: Vec::new(),
                    next: Vec::new(),
                    commits: 1,
                    single_commit_oversize: false,
                },
                maximal_identities,
                u64::MAX,
                None,
            )
            .expect("seals"),
        ),
    ];

    let plain = TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals");
    let overhead_plain = envelope_overhead(&plain);
    for (class, frame) in &representatives {
        let overhead = envelope_overhead(frame);
        let wire = stream_wire_bytes(frame);
        assert!(wire <= MAX_FRAME_BYTES, "E-13: the {class} frame stays within the frame bound");
        assert!(
            overhead <= FIXED_ENVELOPE_BYTES + STREAM_PREFIX_BYTES,
            "E-13: the {class} overhead {overhead} stays within the fixed bound \
             {FIXED_ENVELOPE_BYTES} (+ prefix)"
        );
        println!("E-13: {class} overhead {overhead} bytes (plain-envelope {overhead_plain})");
    }
    // The worst case reaches the fixed bound exactly (width-saturated
    // identities on a u16-range kind code), which is what makes the bound
    // the true ceiling; smaller kind codes ride as fixints below it.
    let saturated = &representatives[1].1;
    let overhead_saturated = saturated.to_bytes().len() - saturated.body_bytes().len();
    assert_eq!(
        overhead_saturated, FIXED_ENVELOPE_BYTES,
        "E-13: maximal identities on a u16 kind code reach the fixed bound exactly"
    );
    assert_eq!(representatives[0].1.to_bytes().len() - representatives[0].1.body_bytes().len(), 33);

    // The mutation-family pairing stays enforced with the overhead intact.
    let key = IdempotencyKey::new("efficiency-probe").expect("valid key");
    let mutation = TbepFrame::seal(
        terrabase_tbep::generated::SyncMutationSubmitRequest {
            session_id: 1,
            document: "d-1".into(),
            change: terrabase_tbep::generated::SyncChange::Put(vec![
                terrabase_tbep::generated::SyncField {
                    name: "qty".into(),
                    value: terrabase_tbep::TbepValue::Integer(1),
                },
            ]),
        },
        7,
        3,
        Some(&key),
    )
    .expect("seals");
    assert!(envelope_overhead(&mutation) <= FIXED_ENVELOPE_BYTES + STREAM_PREFIX_BYTES);

    stream_write_uses_one_buffer_and_one_write_case();
}

/// E-14: writing one frame to a stream performs exactly one write call —
/// the length prefix and the envelope ride one pre-sized buffer — and
/// strictly fewer allocations than the two-write path it replaces, on the
/// same frame.
///
/// This runs as a plain case INSIDE the E-13 test rather than as its own
/// `#[test]`: the counting allocator is process-global, and the harness's
/// main thread allocates to print a sibling test's completion — a print
/// that can land inside this test's measured window and invert the count
/// (observed as "11 vs 9" under a loaded workspace run). Inside one test
/// there is no sibling completion to print, so the measured windows are
/// quiet by construction.
///
/// The caller holds `SERIAL` for the whole E-13 test scope — including
/// this case — so the case must NOT re-take it: `std::sync::Mutex` is not
/// reentrant, and a second `measure_exclusively()` here deadlocks the
/// binary (observed as a permanent futex wait under the workspace run).
fn stream_write_uses_one_buffer_and_one_write_case() {
    let frame = TbepFrame::seal(
        SyncDeltaPullResponse {
            session_id: 1,
            payload_codec: "terrabase-native/1".into(),
            payload: vec![7_u8; 512],
            next: vec![1_u8; 24],
            commits: 4,
            single_commit_oversize: false,
        },
        11,
        7,
        None,
    )
    .expect("seals");
    let expected_wire = stream_wire_bytes(&frame);

    // The replaced shape: the envelope encoded to its own buffer, then a
    // length-prefix write and an envelope write — two write calls.
    allocations();
    let bytes = frame.to_bytes();
    let mut old_writer = CountingWriter::default();
    old_writer
        .write_all(&(u32::try_from(bytes.len()).expect("fits")).to_le_bytes())
        .expect("prefix writes");
    old_writer.write_all(&bytes).expect("envelope writes");
    let old_buffers = allocations();

    let mut writer = CountingWriter::default();
    allocations();
    frame.write_stream(&mut writer).expect("writes");
    let new_buffers = allocations();
    assert_eq!(writer.bytes, expected_wire, "the sink takes exactly the wire bytes");
    assert_eq!(writer.bytes, old_writer.bytes, "both paths carry identical wire bytes");
    assert_eq!(old_writer.writes, 2, "the replaced path wrote twice");
    assert_eq!(
        writer.writes, 1,
        "E-14: the length prefix and the envelope ride one buffer and one write call"
    );
    println!(
        "E-14: one-write path {new_buffers} allocations vs two-write path {old_buffers}          (identical wire bytes)"
    );
    assert!(
        new_buffers < old_buffers,
        "E-14: one pre-sized buffer beats the two-buffer path ({new_buffers} vs {old_buffers})"
    );
}
