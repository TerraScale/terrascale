//! Public-path tests for the bulk-insert ingestion vocabulary (GAP-D10,
//! ticket #61), per `docs/engine/gapd10-bulk-ingest.md`.
//!
//! These tests use only public construction: the request shape, the closed
//! bounds, and the typed vocabulary. `TenantScope` has no public
//! constructor by design (Platform creates it after authorization), so the
//! commit-path behavior — bounded memory, refusal-without-abort, exact
//! abort accounting — is proved by the in-crate engine tests instead
//! (`src/engine/ingest_tests.rs`).

use std::time::Duration;

use terrabase_v2::engine::{
    BulkIngestRequest, IngestConflictMode, IngestRefusal, IngestRefusalReason,
    MAX_INGEST_BATCH_DOCUMENTS, MAX_INGEST_REFUSAL_DETAILS,
};
use terrabase_v2::operations::RequestValidationError;
use terrabase_v2::types::{Cancellation, CollectionName, Deadline, DocumentId, LocalDurability};

fn base_request() -> BulkIngestRequest {
    BulkIngestRequest::try_new(
        IngestConflictMode::Overwrite,
        LocalDurability::Written,
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid request")
}

#[test]
fn bulk_ingest_requests_default_to_the_engine_batch_maximum() {
    let request = base_request();
    assert_eq!(request.batch_documents(), MAX_INGEST_BATCH_DOCUMENTS);
    assert_eq!(request.conflict_mode(), IngestConflictMode::Overwrite);
    assert_eq!(request.durability(), LocalDurability::Written);
    assert!(!request.deadline().is_expired());
    assert!(!request.cancellation().is_cancelled());
}

#[test]
fn bulk_ingest_requests_reject_out_of_range_batch_bounds() {
    assert_eq!(
        base_request().try_with_batch_documents(0).err(),
        Some(RequestValidationError::InvalidRange),
        "a zero bound would never publish a batch"
    );
    assert_eq!(
        base_request().try_with_batch_documents(MAX_INGEST_BATCH_DOCUMENTS + 1).err(),
        Some(RequestValidationError::InvalidRange),
        "the engine's maximum caps the bound"
    );
    let bounded = base_request()
        .try_with_batch_documents(MAX_INGEST_BATCH_DOCUMENTS)
        .expect("the maximum itself is accepted");
    assert_eq!(bounded.batch_documents(), MAX_INGEST_BATCH_DOCUMENTS);
}

#[test]
fn bulk_ingest_requests_reject_cancelled_signals() {
    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        BulkIngestRequest::try_new(
            IngestConflictMode::CreateOnly,
            LocalDurability::Synced,
            Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
            cancelled,
        )
        .err(),
        Some(RequestValidationError::InvalidCancellation),
        "a pre-cancelled signal would make the stream a no-op with no accounting"
    );
}

#[test]
fn bulk_ingest_create_only_is_a_distinct_typed_selection() {
    let request = BulkIngestRequest::try_new(
        IngestConflictMode::CreateOnly,
        LocalDurability::Buffered,
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid request");
    assert_eq!(request.conflict_mode(), IngestConflictMode::CreateOnly);
    assert_ne!(request.conflict_mode(), IngestConflictMode::Overwrite);
}

#[test]
fn bulk_ingest_refusal_vocabulary_carries_document_facts() {
    let refusal = IngestRefusal::new(
        CollectionName::try_new("users").expect("valid identifier"),
        DocumentId::try_new("users/1").expect("valid identifier"),
        IngestRefusalReason::InvalidDocument,
    );
    assert_eq!(refusal.collection().as_str(), "users");
    assert_eq!(refusal.document().as_str(), "users/1");
    assert_eq!(refusal.reason(), IngestRefusalReason::InvalidDocument);
    assert_eq!(MAX_INGEST_REFUSAL_DETAILS, 64, "the disclosure window is fixed and documented");
}
