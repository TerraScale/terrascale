//! In-crate proofs for the engine-side bulk-insert ingestion stream
//! (GAP-D10, ticket #61), per `docs/engine/gapd10-bulk-ingest.md`.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. Storage faults are injected through the
//! private test-only control, and no test inspects raw pages, WAL bytes, or
//! private lock state.

use std::{
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
    time::Duration,
};

use crate::engine::{
    BulkIngestRequest, CommitError, DurableResolution, Engine, EngineConfig, EngineError,
    FaultPoint, IngestConflictMode, IngestRefusalReason, IngestTermination, UnknownReason,
    MAX_INGEST_BATCH_DOCUMENTS, MAX_INGEST_REFUSAL_DETAILS,
};
use crate::operations::RequestValidationError;
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, TenantId, TenantScope,
};

/// One byte over the engine's structural document limit (1 MiB,
/// `storage::codec::MAX_DOCUMENT_BYTES`), which `document_within_limits`
/// refuses. The constant is module-private to storage, so the test states
/// the observable size, not the constant.
const OVERSIZED_BYTES: usize = 2 * 1024 * 1024;

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-gapd10-ingest-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> Engine {
    let dir = location(tag);
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

fn scope(engine: &Engine) -> TenantScope {
    TenantScope::from_parts(TenantId::try_new("tenant-a").expect("valid tenant"), engine.identity())
}

fn document(id: &str, collection: &str, text: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::String(text.to_owned()),
    )
}

/// A document over the engine's structural byte limit, exercising the same
/// refusal `Transaction::put` produces (`EngineError::InvalidDocument`).
fn oversized_document(id: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new("users").expect("valid identifier"),
        DocumentValue::String("x".repeat(OVERSIZED_BYTES)),
    )
}

/// Builds the default overwrite-mode request with a caller-sized batch bound.
fn request(batch: usize) -> BulkIngestRequest {
    BulkIngestRequest::try_new(
        IngestConflictMode::Overwrite,
        LocalDurability::Written,
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid request")
    .try_with_batch_documents(batch)
    .expect("valid batch bound")
}

fn read_value(engine: &Engine, scope: &TenantScope, id: &str) -> Option<String> {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let transaction = engine
        .begin(scope, snapshot, OperationId::try_new("read-probe").expect("valid"))
        .expect("begins");
    transaction
        .read(&DocumentId::try_new(id).expect("valid identifier"))
        .expect("reads")
        .and_then(|document| match document.value() {
            DocumentValue::String(text) => Some(text.clone()),
            _ => None,
        })
}

#[test]
fn bulk_ingest_commits_across_multiple_batches_and_reconciles() {
    let engine = open_engine("batches");
    let scope = scope(&engine);
    let documents: Vec<Document> =
        (0..20).map(|index| document(&format!("users/{index}"), "users", "v")).collect();

    let receipt = engine.ingest_stream(&scope, request(8)).expect("stream opens").run(documents);

    assert_eq!(receipt.attempted(), 20);
    assert_eq!(receipt.committed(), 20);
    assert_eq!(receipt.refused(), 0);
    assert_eq!(receipt.aborted(), 0);
    assert!(receipt.reconciles(), "attempted must equal committed + refused + aborted");
    assert_eq!(receipt.committed_batches(), 3, "20 documents at batch 8 publish as 8 + 8 + 4");
    let last = receipt.last_committed_batch().expect("the final partial batch committed");
    assert_eq!(last.documents(), 4);
    assert!(matches!(receipt.termination(), IngestTermination::Completed));
    assert!(receipt.is_complete());
    for index in 0..20 {
        assert_eq!(
            read_value(&engine, &scope, &format!("users/{index}")).as_deref(),
            Some("v"),
            "every ingested document is published"
        );
    }
}

/// The behavioral bounded-memory proof: while the source is being consumed,
/// every batch behind the cursor must already be published. A whole-batch
/// materialization keeps the published sequence at its baseline until the
/// very end and fails the per-pull invariant.
#[test]
fn bulk_ingest_never_materializes_the_whole_batch() {
    let engine = open_engine("bounded");
    let scope = scope(&engine);
    let batch: u64 = 8;
    let total = 3 * batch + 5;
    let baseline = engine.read_snapshot(&scope).expect("snapshot").sequence().get();
    let pulls = AtomicU64::new(0);
    let violations: Arc<Mutex<Vec<(u64, u64)>>> = Arc::new(Mutex::new(Vec::new()));
    let probe_scope = scope.clone();
    let observed = Arc::clone(&violations);
    let probe = &engine;
    let source = (0..total).map(move |index| {
        let document = document(&format!("users/{index}"), "users", "v");
        // This runs between the stream's pulls: the stream holds no
        // transaction here, so a fresh snapshot observes every publication
        // the stream already made.
        let pull = pulls.fetch_add(1, Ordering::SeqCst) + 1;
        let published =
            probe.read_snapshot(&probe_scope).expect("probe snapshot").sequence().get();
        let commits = published.saturating_sub(baseline);
        // Every document before the cursor is either committed in a closed
        // batch or inside the single forming batch (plus the document being
        // handed over right now). Anything larger materialized the batch.
        if batch * (commits + 1) + 1 < pull {
            observed.lock().expect("violation log").push((pull, commits));
        }
        document
    });

    let receipt = engine.ingest_stream(&scope, request(8)).expect("stream opens").run(source);

    let violations = violations.lock().expect("violation log");
    assert!(
        violations.is_empty(),
        "the stream materialized more than one forming batch (pull, commits): {violations:?}"
    );
    assert_eq!(receipt.committed(), total);
    assert!(receipt.is_complete());
}

#[test]
fn bulk_ingest_refuses_invalid_documents_without_aborting() {
    let engine = open_engine("refusals");
    let scope = scope(&engine);
    let mut documents = Vec::new();
    for index in 0..4 {
        documents.push(document(&format!("users/{index}"), "users", "ok"));
    }
    documents.push(oversized_document("users/big"));
    for index in 4..8 {
        documents.push(document(&format!("users/{index}"), "users", "after"));
    }

    let receipt = engine.ingest_stream(&scope, request(8)).expect("stream opens").run(documents);

    assert_eq!(receipt.attempted(), 9);
    assert_eq!(receipt.committed(), 8, "the valid documents on both sides of the refusal commit");
    assert_eq!(receipt.refused(), 1);
    assert_eq!(receipt.aborted(), 0);
    assert!(receipt.reconciles());
    assert!(receipt.is_complete(), "a per-document refusal never aborts the stream");
    let details = receipt.refusal_details();
    assert_eq!(details.len(), 1);
    assert_eq!(details[0].document().as_str(), "users/big");
    assert_eq!(details[0].collection().as_str(), "users");
    assert_eq!(details[0].reason(), IngestRefusalReason::InvalidDocument);
    assert_eq!(receipt.dropped_refusal_details(), 0);
    assert_eq!(
        read_value(&engine, &scope, "users/7").as_deref(),
        Some("after"),
        "documents after the refused one still committed"
    );
    assert_eq!(read_value(&engine, &scope, "users/big"), None);
}

#[test]
fn bulk_ingest_duplicate_keys_last_write_wins_within_a_batch() {
    let engine = open_engine("duplicates");
    let scope = scope(&engine);
    let documents = vec![
        document("users/1", "users", "first"),
        document("users/2", "users", "only"),
        document("users/1", "users", "second"),
    ];

    let receipt = engine.ingest_stream(&scope, request(8)).expect("stream opens").run(documents);

    assert_eq!(receipt.attempted(), 3);
    assert_eq!(receipt.committed(), 3, "both pulls of the duplicate key are accounted committed");
    assert_eq!(receipt.committed_batches(), 1);
    assert!(receipt.reconciles());
    assert_eq!(
        read_value(&engine, &scope, "users/1").as_deref(),
        Some("second"),
        "V1 parity: a batch applies its puts sequentially, so the last write wins"
    );
}

#[test]
fn bulk_ingest_create_only_rejects_on_existing_document_without_false_success() {
    let engine = open_engine("create-only");
    let scope = scope(&engine);
    // The pre-existing document makes the second batch's create expectation
    // conflict at the commit seam.
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut seed =
        engine.begin(&scope, snapshot, OperationId::try_new("seed").expect("valid")).expect("begins");
    seed.put(document("users/existing", "users", "old"), ExpectedVersion::Any).expect("stages");
    assert!(matches!(
        seed.commit(LocalDurability::Written),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let mut documents: Vec<Document> =
        (0..8).map(|index| document(&format!("users/new-{index}"), "users", "v")).collect();
    documents.push(document("users/existing", "users", "challenger"));
    for index in 8..11 {
        documents.push(document(&format!("users/new-{index}"), "users", "v"));
    }

    let create_only = BulkIngestRequest::try_new(
        IngestConflictMode::CreateOnly,
        LocalDurability::Written,
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid request")
    .try_with_batch_documents(8)
    .expect("valid batch bound");

    let receipt = engine.ingest_stream(&scope, create_only).expect("stream opens").run(documents);

    assert_eq!(receipt.attempted(), 12);
    assert_eq!(receipt.committed(), 8, "only the first batch committed");
    assert_eq!(receipt.refused(), 0);
    assert_eq!(receipt.aborted(), 4, "the conflicting batch's documents are aborted, not claimed");
    assert!(receipt.reconciles());
    assert!(!receipt.is_complete(), "a rejected batch aborts the stream with no success claim");
    match receipt.termination() {
        IngestTermination::Rejected { operation, error } => {
            assert_eq!(*error, CommitError::Conflict);
            // The failed batch resolves durably as rejected: no false
            // success anywhere.
            assert_eq!(
                engine.resolve_commit(&scope, operation.clone()).expect("resolves"),
                DurableResolution::Rejected(CommitError::Conflict)
            );
        }
        other => panic!("expected a typed rejection termination, got {other:?}"),
    }
    for index in 0..8 {
        assert_eq!(
            read_value(&engine, &scope, &format!("users/new-{index}")).as_deref(),
            Some("v"),
            "the committed batch's documents stay committed"
        );
    }
    for index in 8..11 {
        assert_eq!(
            read_value(&engine, &scope, &format!("users/new-{index}")),
            None,
            "the rejected batch's documents were never published"
        );
    }
    assert_eq!(
        read_value(&engine, &scope, "users/existing").as_deref(),
        Some("old"),
        "the pre-existing document is untouched by the aborted batch"
    );
}

#[test]
fn bulk_ingest_unknown_commit_aborts_without_claiming_success() {
    let engine = open_engine("unknown");
    let scope = scope(&engine);
    engine.arm_storage_fault(FaultPoint::PointerSync);
    let documents: Vec<Document> =
        (0..3).map(|index| document(&format!("users/{index}"), "users", "v")).collect();

    let receipt = engine.ingest_stream(&scope, request(8)).expect("stream opens").run(documents);

    assert_eq!(receipt.attempted(), 3);
    assert_eq!(receipt.committed(), 0, "an unknown outcome is never counted committed");
    assert_eq!(receipt.refused(), 0);
    assert_eq!(receipt.aborted(), 3);
    assert!(receipt.reconciles());
    assert!(!receipt.is_complete());
    match receipt.termination() {
        IngestTermination::Unknown { operation, reason } => {
            assert_eq!(*reason, UnknownReason::PostPublicationPointerSync);
            // The caller's resolution handle: whatever the durable answer
            // is, the receipt itself claimed nothing. A post-publication
            // pointer-sync failure has published, so the durable answer here
            // is a committed receipt the receipt never took credit for.
            assert!(matches!(
                engine.resolve_commit(&scope, operation.clone()).expect("resolves"),
                DurableResolution::Committed(_)
            ));
        }
        other => panic!("expected an unknown termination, got {other:?}"),
    }
}

#[test]
fn bulk_ingest_cancellation_accounts_exactly() {
    let engine = open_engine("cancel");
    let scope = scope(&engine);
    let cancellation = Cancellation::new();
    let stream_request = BulkIngestRequest::try_new(
        IngestConflictMode::Overwrite,
        LocalDurability::Written,
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        cancellation.clone(),
    )
    .expect("valid request")
    .try_with_batch_documents(4)
    .expect("valid batch bound");
    // Cancel once ten documents have been pulled: batches of four mean two
    // committed batches (8 documents) and a forming batch of two.
    let signal = cancellation.clone();
    let pulled = AtomicU64::new(0);
    let source = (0..20).map(move |index| {
        let document = document(&format!("users/{index}"), "users", "v");
        if pulled.fetch_add(1, Ordering::SeqCst) + 1 == 10 {
            signal.cancel();
        }
        document
    });

    let receipt = engine.ingest_stream(&scope, stream_request).expect("stream opens").run(source);

    assert_eq!(receipt.attempted(), 10);
    assert_eq!(receipt.committed(), 8, "exactly the two closed batches are claimed");
    assert_eq!(receipt.refused(), 0);
    assert_eq!(receipt.aborted(), 2, "the forming batch is aborted, never claimed");
    assert!(receipt.reconciles());
    assert!(!receipt.is_complete());
    assert!(matches!(receipt.termination(), IngestTermination::Cancelled));
    for index in 0..8 {
        assert_eq!(read_value(&engine, &scope, &format!("users/{index}")).as_deref(), Some("v"));
    }
    for index in 8..10 {
        assert_eq!(
            read_value(&engine, &scope, &format!("users/{index}")),
            None,
            "the aborted forming batch published nothing"
        );
    }
}

#[test]
fn bulk_ingest_expired_deadline_stops_the_stream_typed() {
    let engine = open_engine("deadline");
    let scope = scope(&engine);
    let deadline = Deadline::after(Duration::from_nanos(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(2));

    let receipt = engine
        .ingest_stream(
            &scope,
            BulkIngestRequest::try_new(
                IngestConflictMode::Overwrite,
                LocalDurability::Written,
                deadline,
                Cancellation::new(),
            )
            .expect("valid request")
            .try_with_batch_documents(4)
            .expect("valid batch bound"),
        )
        .expect("stream opens")
        .run((0..6).map(|index| document(&format!("users/{index}"), "users", "v")));

    assert_eq!(receipt.attempted(), 0);
    assert_eq!(receipt.committed(), 0);
    assert!(receipt.reconciles());
    assert!(matches!(receipt.termination(), IngestTermination::Deadline));
}

#[test]
fn bulk_ingest_empty_source_completes_with_zero_counts() {
    let engine = open_engine("empty");
    let scope = scope(&engine);
    let receipt =
        engine.ingest_stream(&scope, request(8)).expect("stream opens").run(std::iter::empty());

    assert_eq!(receipt.attempted(), 0);
    assert_eq!(receipt.committed(), 0);
    assert_eq!(receipt.committed_batches(), 0);
    assert!(receipt.last_committed_batch().is_none());
    assert!(receipt.reconciles());
    assert!(receipt.is_complete(), "V1 parity: disposing without a single store is clean");
}

#[test]
fn bulk_ingest_abort_before_run_makes_no_claim() {
    let engine = open_engine("abort");
    let scope = scope(&engine);
    let receipt = engine.ingest_stream(&scope, request(8)).expect("stream opens").abort();

    assert_eq!(receipt.attempted(), 0);
    assert_eq!(receipt.committed(), 0);
    assert!(receipt.reconciles());
    assert!(!receipt.is_complete(), "an abort never reports success");
    assert!(matches!(receipt.termination(), IngestTermination::Cancelled));
}

#[test]
fn bulk_ingest_refusal_detail_window_is_bounded_and_drops_are_counted() {
    let engine = open_engine("window");
    let scope = scope(&engine);
    let invalid = 3 * MAX_INGEST_REFUSAL_DETAILS + 5;
    let documents: Vec<Document> =
        (0..invalid).map(|index| oversized_document(&format!("users/big-{index}"))).collect();

    let receipt = engine
        .ingest_stream(&scope, request(MAX_INGEST_BATCH_DOCUMENTS))
        .expect("stream opens")
        .run(documents);

    assert_eq!(receipt.refused(), invalid as u64, "every refusal is counted");
    assert_eq!(
        receipt.refusal_details().len(),
        MAX_INGEST_REFUSAL_DETAILS,
        "the detail window stays bounded"
    );
    assert_eq!(
        receipt.dropped_refusal_details(),
        (invalid - MAX_INGEST_REFUSAL_DETAILS) as u64,
        "the overflow beyond the window is disclosed"
    );
    assert!(receipt.is_complete());
    assert!(receipt.reconciles());
}

#[test]
fn bulk_ingest_stream_validates_the_scope() {
    let engine = open_engine("scope");
    let foreign = TenantScope::from_parts(
        TenantId::try_new("tenant-a").expect("valid tenant"),
        engine.identity().wrapping_add(1),
    );
    let error = engine
        .ingest_stream(&foreign, request(8))
        .expect_err("a scope from another engine is refused");
    assert_eq!(error, EngineError::ScopeMismatch);
}

#[test]
fn bulk_ingest_requests_reject_invalid_shapes() {
    let base = || {
        BulkIngestRequest::try_new(
            IngestConflictMode::Overwrite,
            LocalDurability::Written,
            Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
            Cancellation::new(),
        )
        .expect("valid request")
    };
    assert_eq!(
        base().try_with_batch_documents(0).err(),
        Some(RequestValidationError::InvalidRange),
        "a zero batch bound would never flush"
    );
    assert_eq!(
        base().try_with_batch_documents(MAX_INGEST_BATCH_DOCUMENTS + 1).err(),
        Some(RequestValidationError::InvalidRange),
        "the batch bound is capped at the engine's maximum"
    );
    assert!(base().try_with_batch_documents(MAX_INGEST_BATCH_DOCUMENTS).is_ok());

    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        BulkIngestRequest::try_new(
            IngestConflictMode::Overwrite,
            LocalDurability::Written,
            Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
            cancelled,
        )
        .err(),
        Some(RequestValidationError::InvalidCancellation)
    );
}
