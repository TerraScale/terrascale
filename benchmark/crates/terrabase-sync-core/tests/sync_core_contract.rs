//! The `terrabase-sync-core` seam contract, stated as tests.
//!
//! These tests cross the crate's public surface only. The consumer side
//! is exercised through Loro directly (`LoroDoc::import`) as the
//! receiving stand-in for the native SDK sync clients of
//! `terrascale.sync.v2` (PD-2026-08-26-12): the seam itself is
//! server-side only — clients never multi-master the document, and the
//! crate exposes no client write path at all.

use std::collections::BTreeMap;

use loro::{LoroDoc, LoroValue, ToJson};
use terrabase_sync_core::FrameBody;
use terrabase_sync_core::FrameId;
use terrabase_sync_core::JournalChange;
use terrabase_sync_core::JournalCommit;
use terrabase_sync_core::SyncBatch;
use terrabase_sync_core::SyncCheckpoint;
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
    doc: LoroDoc,
    checkpoint: SyncCheckpoint,
}

impl Consumer {
    fn new() -> Self {
        let doc = LoroDoc::new();
        doc.set_peer_id(0x5EED).expect("consumer peer id");
        Self { doc, checkpoint: SyncCheckpoint::initial() }
    }

    fn apply(&mut self, batch: &SyncBatch) -> Result<(), String> {
        let SyncBatch::Delta { bytes, next, .. } = batch else {
            return Err("consumer asked for a data batch".to_string());
        };
        let status = self.doc.import(bytes).map_err(|e| e.to_string())?;
        if status.pending.is_some() {
            return Err("pending dependencies left".to_string());
        }
        self.checkpoint = next.clone();
        Ok(())
    }

    fn resync(&mut self, snapshot_bytes: &[u8], checkpoint: &SyncCheckpoint) -> Result<(), String> {
        self.doc.import(snapshot_bytes).map_err(|e| e.to_string())?;
        self.checkpoint = checkpoint.clone();
        Ok(())
    }

    fn field(&self, document: &str, field: &str) -> Option<String> {
        self.doc
            .get_deep_value()
            .get_by_key("docs")
            .and_then(|docs| docs.get_by_key(document))
            .and_then(|doc| doc.get_by_key(field))
            .map(|value| value.to_json_value().to_string())
    }

    fn holds_document(&self, document: &str) -> bool {
        self.doc
            .get_deep_value()
            .get_by_key("docs")
            .and_then(|docs| docs.get_by_key(document))
            .is_some()
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
        Some("\"v0007-07\"".to_string()),
        "the consumer observed the materialized state"
    );
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
    consumer.resync(&snapshot.bytes, &snapshot.checkpoint).expect("resync applies");
    for index in 1..=8 {
        assert!(
            consumer.holds_document(&format!("d-{index:02}")),
            "the resynced consumer holds every document after the window overflow"
        );
    }
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

    // The floor rose to the compacted version: the shallow document
    // retains history only since it, so it is the oldest servable delta.
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
    // session exactly at the floor is served what follows the shallow
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
    resynced.resync(&snapshot.bytes, &snapshot.checkpoint).expect("resync applies");
    assert_eq!(
        resynced.doc.get_deep_value(),
        core.deep_value(),
        "the resynced session observes exactly the server's post-compaction state"
    );
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
        field_of(&core.deep_value(), "d-01", "f002"),
        "deltas serve again from the resynced position after compaction"
    );
}

/// Reads one document field out of a deep value (the server-side
/// observation used to compare against the consumer's).
fn field_of(value: &LoroValue, document: &str, field: &str) -> Option<String> {
    value
        .get_by_key("docs")
        .and_then(|docs| docs.get_by_key(document))
        .and_then(|doc| doc.get_by_key(field))
        .map(|value| value.to_json_value().to_string())
}

#[test]
fn checkpoint_encoding_round_trips_and_fails_closed_on_unknown_versions() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    core.apply(&put(1, "d-1", 2)).expect("applies");
    let checkpoint = core.checkpoint();
    let bytes = checkpoint.to_bytes();
    let decoded = SyncCheckpoint::from_bytes(&bytes).expect("round trips");
    assert_eq!(decoded, checkpoint);

    let mut tampered = bytes;
    tampered[0] = 0xFF;
    assert!(matches!(
        SyncCheckpoint::from_bytes(&tampered),
        Err(SyncCoreError::UnknownCheckpointVersion(0xFF))
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

    // Unknown contract version fails closed.
    let mut future = frame.to_bytes();
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
    consumer.apply(&batch).expect("duplicate apply stays idempotent");
    assert_eq!(consumer.field("d-1", "f001"), Some("\"v0001-01\"".to_string()));
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
