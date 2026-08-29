//! The `terrabase-sync-core` seam contract, stated as tests.
//!
//! These tests cross the crate's public surface only. The consumer side
//! is exercised through the crate's native [`SyncConsumer`] — the
//! receiving stand-in for the native SDK sync clients of
//! `terrascale.sync.v2`: the seam itself is server-side only, clients
//! never multi-master the document, and the crate exposes no client
//! write path at all.

use std::collections::BTreeMap;

use terrabase_sync_core::FrameBody;
use terrabase_sync_core::FrameId;
use terrabase_sync_core::JournalChange;
use terrabase_sync_core::JournalCommit;
use terrabase_sync_core::SyncBatch;
use terrabase_sync_core::SyncCheckpoint;
use terrabase_sync_core::SyncConsumer;
use terrabase_sync_core::SyncCore;
use terrabase_sync_core::SyncCoreConfig;
use terrabase_sync_core::SyncCoreError;
use terrabase_sync_core::SyncFrame;
use terrabase_sync_core::SyncScope;
use terrabase_sync_core::SyncValue;

fn scope() -> SyncScope {
    SyncScope::new("tenant-a", "orders")
}

fn config() -> SyncCoreConfig {
    SyncCoreConfig { batch_byte_budget: 8 * 1024, checkpoint_window: 128 }
}

fn put(sequence: u64, document: &str, fields: usize) -> JournalCommit {
    let mut body = BTreeMap::new();
    for field in 0..fields {
        body.insert(format!("f{field:03}"), SyncValue::Str(format!("v{sequence:04}-{field:02}")));
    }
    JournalCommit {
        storage_sequence: sequence,
        operation_id: format!("op-{sequence:06}"),
        document_id: document.to_string(),
        change: JournalChange::Put { body },
    }
}

fn patch(sequence: u64, document: &str, field: usize) -> JournalCommit {
    let mut body = BTreeMap::new();
    body.insert(format!("f{field:03}"), SyncValue::Str(format!("v{sequence:04}")));
    JournalCommit {
        storage_sequence: sequence,
        operation_id: format!("op-{sequence:06}"),
        document_id: document.to_string(),
        change: JournalChange::Put { body },
    }
}

fn delete(sequence: u64, document: &str) -> JournalCommit {
    JournalCommit {
        storage_sequence: sequence,
        operation_id: format!("op-{sequence:06}"),
        document_id: document.to_string(),
        change: JournalChange::Delete,
    }
}

/// A consumer stand-in that applies server batches and tracks its
/// checkpoint exactly like a native sync client would.
struct Consumer {
    doc: SyncConsumer,
    checkpoint: SyncCheckpoint,
}

impl Consumer {
    fn new() -> Self {
        Self { doc: SyncConsumer::new(), checkpoint: SyncCheckpoint::initial() }
    }

    fn apply(&mut self, batch: &SyncBatch) -> Result<(), String> {
        let SyncBatch::Delta { bytes, next, .. } = batch else {
            return Err("consumer asked for a data batch".to_string());
        };
        self.doc.apply_delta(bytes).map_err(|e| e.to_string())?;
        self.checkpoint = next.clone();
        Ok(())
    }

    fn resync(&mut self, snapshot_bytes: &[u8]) -> Result<(), String> {
        self.checkpoint = self.doc.adopt_snapshot(snapshot_bytes).map_err(|e| e.to_string())?;
        Ok(())
    }

    fn field(&self, document: &str, field: &str) -> Option<SyncValue> {
        self.doc.field(document, field)
    }

    fn holds_document(&self, document: &str) -> bool {
        self.doc.holds_document(document)
    }
}

/// Drains every batch until the core reports `UpToDate`.
fn drain(core: &SyncCore, consumer: &mut Consumer) -> usize {
    let mut batches = 0;
    loop {
        let batch = core.delta_since(&consumer.checkpoint).expect("delta serves");
        match batch {
            SyncBatch::UpToDate => return batches,
            SyncBatch::BehindRetainedFloor { .. } => panic!("floor hit inside the window"),
            SyncBatch::Delta { .. } => {
                batches += 1;
                consumer.apply(&batch).expect("consumer applies");
            }
        }
    }
}

#[test]
fn journal_commits_materialize_and_serve_deltas_from_checkpoints() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    for sequence in 1..=16 {
        core.apply(&put(sequence, &format!("d-{sequence:02}"), 12)).expect("applies");
    }
    let mut consumer = Consumer::new();
    let batches = drain(&core, &mut consumer);
    assert!(batches >= 1);
    assert_eq!(
        consumer.field("d-07", "f007"),
        Some(SyncValue::Str("v0007-07".to_string())),
        "the consumer observed the materialized state"
    );
    assert_eq!(consumer.doc.materialized(), core.materialized(), "consumer and server agree");
}

#[test]
fn put_replaces_the_body_and_delete_tombstones() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(1, "d-1", 4)).expect("applies");
    // A put that drops fields replaces the whole body.
    core.apply(&patch(2, "d-1", 1)).expect("applies");
    let mut consumer = Consumer::new();
    drain(&core, &mut consumer);
    assert!(consumer.field("d-1", "f000").is_none(), "dropped fields disappear");
    assert!(consumer.field("d-1", "f001").is_some(), "kept fields stay");

    core.apply(&delete(3, "d-1")).expect("applies");
    drain(&core, &mut consumer);
    assert!(!consumer.holds_document("d-1"), "the tombstone removed the document");
}

#[test]
fn a_put_observing_the_delete_resurrects_the_document() {
    // Delete-wins over a concurrent update, resurrection when the put
    // observed the tombstone — the native tombstone-dot layer's
    // contract, mirrored from `terrabase-replication-core`.
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(1, "d-1", 2)).expect("applies");
    core.apply(&delete(2, "d-1")).expect("applies");
    let mut consumer = Consumer::new();
    drain(&core, &mut consumer);
    assert!(!consumer.holds_document("d-1"), "delete-wins removes the document");

    core.apply(&put(3, "d-1", 2)).expect("applies");
    drain(&core, &mut consumer);
    assert!(consumer.holds_document("d-1"), "a later put that observed the tombstone resurrects");
}

#[test]
fn deltas_respect_the_configured_byte_budget() {
    let config = SyncCoreConfig { batch_byte_budget: 2048, checkpoint_window: 1024 };
    let mut core = SyncCore::new(scope(), config).expect("core builds");
    let mut consumer = Consumer::new();
    // Feed and drain: every batch must fit the budget or be exactly one
    // flagged oversized commit (none of these commits can be oversized:
    // each carries one small field).
    for sequence in 1..=400 {
        let document = format!("d-{:02}", sequence % 20);
        let field = usize::try_from(sequence % 12).expect("field index");
        core.apply(&patch(sequence, &document, field)).expect("applies");
        loop {
            let batch = core.delta_since(&consumer.checkpoint).expect("delta serves");
            match batch {
                SyncBatch::UpToDate => break,
                SyncBatch::BehindRetainedFloor { .. } => panic!("floor hit"),
                SyncBatch::Delta {
                    ref bytes,
                    within_budget,
                    single_commit_oversize,
                    commits,
                    ..
                } => {
                    assert!(
                        within_budget || (single_commit_oversize && commits == 1),
                        "batch of {commits} commits, {} bytes exceeded the 2048-byte budget unflagged",
                        bytes.len()
                    );
                    assert!(bytes.len() <= 2048, "no unflagged oversize delivery");
                    consumer.apply(&batch).expect("applies");
                }
            }
        }
    }
    assert_eq!(consumer.doc.materialized(), core.materialized());
}

#[test]
fn consumers_behind_the_retained_floor_resync_from_a_snapshot() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    for sequence in 1..=8 {
        core.apply(&put(sequence, &format!("d-{sequence:02}"), 6)).expect("applies");
    }
    let mut consumer = Consumer::new();
    drain(&core, &mut consumer);
    let stalled = consumer.checkpoint.clone();
    // Overflow the 128-commit window.
    for sequence in 9..=(8 + 200) {
        let document = format!("d-{:02}", sequence % 8);
        let field = usize::try_from(sequence % 6).expect("field index");
        core.apply(&patch(sequence, &document, field)).expect("applies");
    }
    match core.delta_since(&stalled).expect("delta serves") {
        SyncBatch::BehindRetainedFloor { floor } => {
            assert!(floor.storage_sequence >= stalled.storage_sequence);
        }
        other => panic!("expected the retained floor, got {other:?}"),
    }
    let snapshot = core.resync_snapshot().expect("snapshot serves");
    consumer.resync(&snapshot.bytes).expect("resync applies");
    for index in 1..=8 {
        assert!(
            consumer.holds_document(&format!("d-{index:02}")),
            "the resynced consumer holds every document after the window overflow"
        );
    }
    assert_eq!(snapshot.checkpoint, core.checkpoint(), "the snapshot names the server checkpoint");
    assert!(matches!(
        core.delta_since(&consumer.checkpoint).expect("delta serves"),
        SyncBatch::UpToDate
    ));
}

#[test]
fn compacted_history_answers_consumers_below_the_new_floor_and_resync_recovers() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    for sequence in 1..=10 {
        core.apply(&put(sequence, &format!("d-{sequence:02}"), 6)).expect("applies");
    }
    let mut consumer = Consumer::new();
    drain(&core, &mut consumer);
    let stalled = consumer.checkpoint.clone();
    let stalled_end = stalled.end_for(terrabase_sync_core::SERVER_PEER_ID).unwrap_or(0);

    // Commits the stalled consumer never absorbs, then history compaction.
    for sequence in 11..=20 {
        let document = format!("d-{:02}", sequence % 10);
        let field = usize::try_from(sequence % 6).expect("field index");
        core.apply(&patch(sequence, &document, field)).expect("applies");
    }
    core.compact_history().expect("compacts");

    // The floor rose to the compacted version: the compacted core
    // retains operations only above it, so it is the oldest servable
    // delta.
    let floor_end = core.retained_floor().end_for(terrabase_sync_core::SERVER_PEER_ID).unwrap_or(0);
    assert!(
        floor_end > stalled_end,
        "compaction must raise the retained floor past the stalled consumer ({floor_end} <= {stalled_end})"
    );

    // A consumer below the new floor is answered, never silently served a
    // (possibly empty) batch that would leave it believing it is current.
    match core.delta_since(&stalled).expect("delta serves") {
        SyncBatch::BehindRetainedFloor { floor } => {
            let answered_end = floor.end_for(terrabase_sync_core::SERVER_PEER_ID).unwrap_or(0);
            assert!(answered_end >= floor_end, "the floor answer carries the new floor");
            assert!(answered_end > stalled_end, "the floor answer is past the stalled consumer");
        }
        other => panic!("expected the retained floor after compaction, got {other:?}"),
    }
    assert!(
        consumer.checkpoint == stalled,
        "the stalled consumer did not advance behind the server's back"
    );

    // The floor is the oldest position still served, not one past it: a
    // session exactly at the floor is served what follows the compaction
    // root instead of being forced to resync too.
    let at_floor = core.retained_floor();
    assert!(
        !matches!(
            core.delta_since(&at_floor).expect("delta serves"),
            SyncBatch::BehindRetainedFloor { .. }
        ),
        "a session at the floor is served, not forced to resync"
    );

    // Resync recovers the session exactly. A resync is a full-state
    // answer: the session adopts the snapshot as a fresh state rather
    // than merging it into retained pre-compaction history.
    let snapshot = core.resync_snapshot().expect("snapshot serves");
    let mut resynced = Consumer::new();
    resynced.resync(&snapshot.bytes).expect("resync applies");
    assert_eq!(
        resynced.doc.materialized(),
        core.materialized(),
        "the resynced session observes exactly the server's post-compaction state"
    );
    assert_eq!(resynced.checkpoint, core.checkpoint());
    assert!(matches!(
        core.delta_since(&resynced.checkpoint).expect("delta serves"),
        SyncBatch::UpToDate
    ));

    core.apply(&patch(21, "d-01", 2)).expect("applies after compaction");
    assert!(
        matches!(core.delta_since(&at_floor).expect("delta serves"), SyncBatch::Delta { .. }),
        "deltas still serve a session at the floor after further commits"
    );
    drain(&core, &mut resynced);
    assert_eq!(
        resynced.field("d-01", "f002"),
        core.materialized().get("d-01").and_then(|body| body.get("f002")).cloned(),
        "deltas serve again from the resynced position after compaction"
    );
}

#[test]
fn checkpoint_encoding_round_trips_and_fails_closed_on_unknown_versions() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(1, "d-1", 2)).expect("applies");
    let checkpoint = core.checkpoint();
    let bytes = checkpoint.to_bytes();
    let decoded = SyncCheckpoint::from_bytes(&bytes).expect("round trips");
    assert_eq!(decoded, checkpoint);

    let mut tampered = bytes.clone();
    tampered[0] = 0xFF;
    assert!(matches!(
        SyncCheckpoint::from_bytes(&tampered),
        Err(SyncCoreError::UnknownCheckpointVersion(0xFF))
    ));

    // The superseded version-1 checkpoint encoding (the external-engine
    // era) fails closed: no checkpoint decodes across the cutover.
    let mut legacy = bytes;
    legacy[0] = 1;
    assert!(matches!(
        SyncCheckpoint::from_bytes(&legacy),
        Err(SyncCoreError::UnknownCheckpointVersion(1))
    ));
}

#[test]
fn checkpoint_decoder_rejects_unbounded_and_noncanonical_inputs() {
    let mut oversized = vec![2, 0, 0, 0, 0, 0, 0, 0, 0, 65, 0, 0, 0];
    oversized.extend(std::iter::repeat_n(0, 65 * 16));
    assert!(matches!(
        SyncCheckpoint::from_bytes(&oversized),
        Err(SyncCoreError::InvalidFrame("checkpoint entry count exceeds bound"))
    ));

    let mut duplicate = vec![2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0];
    duplicate.extend_from_slice(&1_u64.to_le_bytes());
    duplicate.extend_from_slice(&1_u64.to_le_bytes());
    duplicate.extend_from_slice(&1_u64.to_le_bytes());
    duplicate.extend_from_slice(&2_u64.to_le_bytes());
    assert!(matches!(
        SyncCheckpoint::from_bytes(&duplicate),
        Err(SyncCoreError::InvalidFrame("checkpoint entries are not canonical"))
    ));

    let mut trailing = SyncCheckpoint::initial().to_bytes();
    trailing.push(0);
    assert!(matches!(
        SyncCheckpoint::from_bytes(&trailing),
        Err(SyncCoreError::InvalidFrame("trailing checkpoint bytes"))
    ));
}

#[test]
fn out_of_order_commits_and_invalid_checkpoints_are_typed_errors() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(5, "d-1", 2)).expect("applies");
    assert!(matches!(
        core.apply(&put(5, "d-2", 2)),
        Err(SyncCoreError::SequenceOutOfOrder { got: 5, .. })
    ));

    let foreign = SyncCheckpoint::from_ends(vec![(0xBEEF, 1)], 0);
    assert!(matches!(core.delta_since(&foreign), Err(SyncCoreError::ForeignPeer(0xBEEF))));

    let future = SyncCheckpoint::from_ends(vec![(terrabase_sync_core::SERVER_PEER_ID, 999)], 0);
    assert!(matches!(
        core.delta_since(&future),
        Err(SyncCoreError::CheckpointAheadOfServer { .. })
    ));
}

#[test]
fn replay_is_deterministic_at_the_seam() {
    let events: Vec<JournalCommit> = (1..=24)
        .map(|sequence| {
            if sequence == 24 {
                delete(sequence, "d-04")
            } else {
                put(sequence, &format!("d-{sequence:02}"), 5)
            }
        })
        .collect();
    let mut a = SyncCore::new(scope(), config()).expect("core builds");
    let mut b = SyncCore::new(scope(), config()).expect("core builds");
    for event in &events {
        a.apply(event).expect("applies");
        b.apply(event).expect("applies");
    }
    assert_eq!(
        a.resync_snapshot().expect("snapshot").bytes,
        b.resync_snapshot().expect("snapshot").bytes,
        "the same journal materializes to identical bytes"
    );
    assert_eq!(a.checkpoint(), b.checkpoint());
}

#[test]
fn frames_seal_verify_round_trip_and_detect_tampering() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(1, "d-1", 3)).expect("applies");
    let batch = core.delta_since(&SyncCheckpoint::initial()).expect("delta serves");
    let SyncBatch::Delta { bytes, next, .. } = batch else { panic!("expected a delta batch") };
    let body = FrameBody::Delta { bytes, next };
    let frame = SyncFrame::seal(body, FrameId::new(7), FrameId::new(3));
    assert_eq!(frame.contract(), terrabase_sync_core::SYNC_CORE_CONTRACT);
    assert_eq!(frame.correlation(), FrameId::new(7));
    assert_eq!(frame.causation(), FrameId::new(3));
    assert!(frame.verify(), "a sealed frame verifies");

    // Wire round trip: the encoded frame decodes to a verifying frame.
    let wire = frame.to_bytes();
    let decoded = SyncFrame::from_bytes(&wire).expect("frame decodes");
    assert!(decoded.verify(), "a decoded frame verifies");
    assert_eq!(decoded.correlation(), FrameId::new(7));

    // Tampering: flip one payload byte; the frame may decode but must not
    // verify (content digest disagreement).
    let mut tampered = wire;
    let last = tampered.len() - 1;
    tampered[last] ^= 0xFF;
    if let Ok(bad) = SyncFrame::from_bytes(&tampered) {
        assert!(!bad.verify(), "a tampered frame fails verification");
    }

    // Unknown contract versions fail closed — including 1, the
    // superseded external-engine frame encoding.
    let mut future = frame.to_bytes();
    future[0] = 1;
    assert!(SyncFrame::from_bytes(&future).is_err());
    future[0] = b'9';
    assert!(SyncFrame::from_bytes(&future).is_err());

    let body = FrameBody::Checkpoint(SyncCheckpoint::initial());
    let frame = SyncFrame::seal(body, FrameId::new(8), FrameId::new(7));
    assert!(frame.verify());
    assert!(SyncFrame::from_bytes(&frame.to_bytes()).is_ok());
}

#[test]
fn duplicate_delta_delivery_is_idempotent() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(1, "d-1", 3)).expect("applies");
    let batch = core.delta_since(&SyncCheckpoint::initial()).expect("delta serves");
    assert!(matches!(batch, SyncBatch::Delta { .. }));
    let mut consumer = Consumer::new();
    consumer.apply(&batch).expect("first apply");
    let once = consumer.doc.materialized();
    consumer.apply(&batch).expect("duplicate apply stays idempotent");
    assert_eq!(consumer.doc.materialized(), once, "re-application changes nothing");
    assert_eq!(consumer.field("d-1", "f001"), Some(SyncValue::Str("v0001-01".to_string())));
}

#[test]
fn reordered_delta_delivery_converges() {
    // Delivery order never matters: the materialized state is a function
    // of the operation set, not of arrival.
    let mut core =
        SyncCore::new(scope(), SyncCoreConfig { batch_byte_budget: 512, checkpoint_window: 128 })
            .expect("core builds");
    for sequence in 1..=12 {
        core.apply(&put(sequence, &format!("d-{:02}", sequence % 4), 3)).expect("applies");
    }
    let mut batches = Vec::new();
    let mut from = SyncCheckpoint::initial();
    loop {
        let batch = core.delta_since(&from).expect("delta serves");
        match batch {
            SyncBatch::UpToDate => break,
            SyncBatch::BehindRetainedFloor { .. } => panic!("floor hit"),
            SyncBatch::Delta { bytes, next, .. } => {
                batches.push(bytes);
                from = next;
            }
        }
    }
    assert!(batches.len() >= 2, "the fixture must split into several batches");
    let mut reordered = SyncConsumer::new();
    for bytes in batches.iter().rev() {
        reordered.apply_delta(bytes).expect("applies");
    }
    assert_eq!(reordered.materialized(), core.materialized(), "reverse order converges");
}

#[test]
fn frame_bodies_digest_deterministically() {
    let checkpoint = SyncCheckpoint::from_ends(vec![(1, 4)], 9);
    let a = FrameBody::Checkpoint(checkpoint.clone());
    let b = FrameBody::Checkpoint(checkpoint);
    assert_eq!(a.digest(), b.digest(), "equal bodies digest equally");
    let c = FrameBody::Checkpoint(SyncCheckpoint::from_ends(vec![(1, 5)], 9));
    assert_ne!(a.digest(), c.digest(), "different bodies digest differently");
}

#[test]
fn native_payload_bytes_never_decode_as_the_superseded_codecs_or_vice_versa() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(1, "d-1", 2)).expect("applies");
    let batch = core.delta_since(&SyncCheckpoint::initial()).expect("delta serves");
    let SyncBatch::Delta { bytes, .. } = batch else { panic!("expected a delta batch") };
    // Native payloads open with the native magic and version.
    assert_eq!(&bytes[..4], &[b'T', b'S', b'N', 1]);
    let snapshot = core.resync_snapshot().expect("snapshot serves");
    assert_eq!(&snapshot.bytes[..4], &[b'T', b'S', b'S', 1]);

    // A consumer applies the native forms...
    let mut consumer = SyncConsumer::new();
    consumer.apply_delta(&bytes).expect("the native delta applies");
    // ...and rejects foreign payload shapes fail-closed.
    assert!(matches!(consumer.apply_delta(&snapshot.bytes), Err(SyncCoreError::NativeCodec(_))));
}
