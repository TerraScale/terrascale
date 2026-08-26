//! The SE-02 prototype's contract tests: one test per claimed proof, all
//! crossing the spike's public surface (server delivery layer + consumer
//! session), no private helpers.

use terrabase_sync_core_spike::journal::JournalGenerator;
use terrabase_sync_core_spike::mapping::Checkpoint;
use terrabase_sync_core_spike::mapping::DeltaOutcome;
use terrabase_sync_core_spike::mapping::ServerDelivery;
use terrabase_sync_core_spike::scenario;

#[test]
fn delta_delivery_beats_full_document_resends_for_field_patches() {
    let outcome = scenario::delta_delivery(0xD1CE).expect("scenario runs");
    assert_eq!(outcome.documents, 64);
    let patch = outcome
        .patterns
        .iter()
        .find(|p| p.name == "single-field patch")
        .expect("patch pattern recorded");
    assert!(patch.delta_bytes > 0, "a real change produces a real delta");
    assert!(
        patch.delta_bytes < patch.full_document_bytes,
        "field-granular delta {} B must beat the full-document re-send {} B",
        patch.delta_bytes,
        patch.full_document_bytes
    );
    let delete = outcome
        .patterns
        .iter()
        .find(|p| p.name == "delete (tombstone)")
        .expect("delete pattern recorded");
    assert!(
        delete.delta_bytes < delete.full_document_bytes,
        "tombstone delta {} B must beat re-sending the body {} B",
        delete.delta_bytes,
        delete.full_document_bytes
    );
}

#[test]
fn whole_document_replace_delta_is_comparable_to_the_full_body() {
    let outcome = scenario::delta_delivery(0xD1CE).expect("scenario runs");
    let replace = outcome
        .patterns
        .iter()
        .find(|p| p.name == "whole-document replace")
        .expect("replace pattern recorded");
    // A whole-body change carries at least the body: the delta cannot be
    // much smaller than the baseline (it may be somewhat larger with
    // envelope overhead). The bound asserted is loose on purpose: the
    // exact ratio is recorded evidence, not a contract.
    assert!(
        replace.delta_bytes >= replace.full_document_bytes / 2,
        "whole-document replace delta {} B vs body {} B",
        replace.delta_bytes,
        replace.full_document_bytes
    );
}

#[test]
fn checkpoint_resume_after_reconnect_converges_and_survives_duplicates() {
    let outcome = scenario::checkpoint_resume(0xFEED).expect("scenario runs");
    assert!(outcome.converged_after_resume, "resume must converge");
    assert!(outcome.converged_after_duplicate, "duplicate re-delivery must stay converged");
    assert!(outcome.reconnect_batches >= 1, "resume used delta batches");
}

#[test]
fn consumer_behind_the_retained_floor_resyncs_by_snapshot() {
    let outcome = scenario::checkpoint_resume(0xFEED).expect("scenario runs");
    assert!(
        outcome.floor_answered,
        "a consumer older than the window must be answered with the floor"
    );
    assert!(outcome.converged_after_resync, "resync must converge");
    assert!(outcome.deltas_serve_after_resync, "deltas must serve again after resync");
}

#[test]
fn burst_delivery_stays_within_the_byte_budget() {
    let outcome = scenario::bounded_memory_burst(0xB057).expect("scenario runs");
    assert_eq!(outcome.commits, 50_000);
    assert!(
        outcome.all_batches_bounded,
        "every batch must fit the budget or be one flagged oversized commit (max {} B)",
        outcome.max_batch_bytes
    );
    assert!(outcome.max_batch_bytes <= outcome.budget_bytes);
    assert!(outcome.batches >= 2, "the burst splits into batches");
}

#[test]
fn replay_is_deterministic_and_consumers_agree() {
    let outcome = scenario::deterministic_replay(0x0DD0).expect("scenario runs");
    assert!(
        outcome.snapshots_identical,
        "two servers fed the same journal must produce identical snapshots"
    );
    assert!(outcome.deep_values_identical);
    assert!(
        outcome.consumers_observe_identically,
        "delta, snapshot, and commit-by-commit consumers must agree"
    );
    assert!(outcome.incremental_pull_identical);
}

#[test]
fn server_rejects_out_of_order_journal_sequences() {
    let mut journal = JournalGenerator::new(11);
    let mut server = ServerDelivery::new(64).expect("server");
    let first = journal.next_put("d-1", 3);
    server.apply(&first).expect("first applies");
    let mut stale = journal.next_put("d-2", 3);
    stale.storage_sequence = 1;
    let err = server.apply(&stale).expect_err("stale sequence rejected");
    assert!(matches!(
        err,
        terrabase_sync_core_spike::mapping::MappingError::SequenceOutOfOrder { got: 1, .. }
    ));
}

#[test]
fn server_rejects_foreign_peer_and_future_checkpoints() {
    let mut journal = JournalGenerator::new(12);
    let mut server = ServerDelivery::new(64).expect("server");
    server.apply(&journal.next_put("d-1", 3)).expect("first applies");

    let foreign = Checkpoint { vv_ends: vec![(99, 1)], storage_sequence: 1 };
    assert!(server.delta_since(&foreign, 1024).is_err());

    let future = Checkpoint {
        vv_ends: vec![(terrabase_sync_core_spike::SERVER_PEER, 10_000)],
        storage_sequence: 1,
    };
    assert!(server.delta_since(&future, 1024).is_err());
}

#[test]
fn put_replaces_dropped_fields_and_delete_removes_documents() {
    use loro::LoroDoc;
    use std::collections::BTreeMap;
    use terrabase_sync_core_spike::journal::{JournalChange, JournalEvent};
    use terrabase_sync_core_spike::mapping::{apply_event, documents_of};

    let doc = LoroDoc::new();
    doc.set_peer_id(terrabase_sync_core_spike::SERVER_PEER).unwrap();
    let mut body = BTreeMap::new();
    body.insert("a".to_string(), terrabase_sync_core_spike::journal::Value::Str("1".into()));
    body.insert("b".to_string(), terrabase_sync_core_spike::journal::Value::Str("2".into()));
    apply_event(
        &doc,
        &JournalEvent {
            storage_sequence: 1,
            operation_id: "op-1".into(),
            collection: "c".into(),
            document_id: "d".into(),
            change: JournalChange::Put { body },
        },
    )
    .expect("put applies");

    let mut body = BTreeMap::new();
    body.insert("a".to_string(), terrabase_sync_core_spike::journal::Value::Str("9".into()));
    apply_event(
        &doc,
        &JournalEvent {
            storage_sequence: 2,
            operation_id: "op-2".into(),
            collection: "c".into(),
            document_id: "d".into(),
            change: JournalChange::Put { body },
        },
    )
    .expect("replace applies");
    let rendered = documents_of(&doc.get_deep_value());
    assert_eq!(
        rendered.get("d").map(String::as_str),
        Some("a=9"),
        "a put replaces the whole body: dropped fields disappear"
    );

    apply_event(
        &doc,
        &JournalEvent {
            storage_sequence: 3,
            operation_id: "op-3".into(),
            collection: "c".into(),
            document_id: "d".into(),
            change: JournalChange::Delete,
        },
    )
    .expect("delete applies");
    assert!(
        !documents_of(&doc.get_deep_value()).contains_key("d"),
        "the tombstone removes the document from the deep value"
    );
}

#[test]
fn delta_from_a_fresh_checkpoint_covers_the_whole_history() {
    let mut journal = JournalGenerator::new(13);
    let mut server = ServerDelivery::new(1024).expect("server");
    for index in 0..5 {
        server.apply(&journal.next_put(&format!("d-{index}"), 8)).expect("applies");
    }
    match server.delta_since(&Checkpoint::initial(), 1 << 20).expect("delta serves") {
        DeltaOutcome::Delta { bytes, commits, .. } => {
            assert_eq!(commits, 5, "every commit is covered");
            let mut consumer =
                terrabase_sync_core_spike::consumer::ConsumerSession::new().expect("consumer");
            consumer
                .apply_delta(&bytes, &server.checkpoint())
                .expect("delta applies to a fresh consumer");
            assert_eq!(
                terrabase_sync_core_spike::mapping::documents_of(&consumer.deep_value()),
                terrabase_sync_core_spike::mapping::documents_of(&server.deep_value()),
                "a fresh consumer converges from a single whole-history delta"
            );
        }
        other => panic!("expected a delta, got {other:?}"),
    }
}
