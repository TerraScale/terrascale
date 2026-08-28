//! Public-path tests for the bulk-by-query request vocabulary (GAP-Q10,
//! ticket #65), per `docs/query/gapq10-bulk-by-query.md`.
//!
//! These tests use only public construction: the option bounds, the
//! confirmation token, and the request shapes. `TenantScope` has no public
//! constructor by design (Platform creates it after authorization), so the
//! execution proofs — page-wise accounting, refusals, audit — run in the
//! in-crate query tests instead (`src/query/bulk_tests.rs`).

use std::time::Duration;

use terrabase_v2::engine::{DocumentPatch, PatchOperation, PatchPath, PatchPathSegment};
use terrabase_v2::operations::RequestValidationError;
use terrabase_v2::query::{
    BulkByQueryOptions, DeleteByQueryRequest, DeletionConfirmation, MAX_BULK_PAGE_DOCUMENTS,
    OrderDirection, OrderingKey, PatchByQueryRequest, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
};
use terrabase_v2::types::{
    Cancellation, CollectionName, Deadline, DocumentValue, LocalDurability, OperationId, TenantId,
};

fn selection_spec() -> QuerySpec {
    QuerySpec::try_new(
        TenantId::try_new("tenant-a").expect("valid tenant"),
        QuerySource::try_new(CollectionName::try_new("orders").expect("valid identifier"))
            .expect("user collection"),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("id", OrderDirection::Ascending).expect("valid key"),
        ])
        .expect("valid ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn one_field_patch() -> DocumentPatch {
    DocumentPatch::try_new(vec![PatchOperation::Add {
        path: PatchPath::try_from_segments(vec![
            PatchPathSegment::field("status").expect("valid field"),
        ])
        .expect("valid path"),
        value: DocumentValue::String("archived".to_owned()),
    }])
    .expect("valid patch")
}

#[test]
fn bulk_options_default_to_the_v1_batch_maximum_without_details() {
    let options = BulkByQueryOptions::new();
    assert_eq!(options.page_documents(), MAX_BULK_PAGE_DOCUMENTS);
    assert!(!options.details(), "V1's RetrieveDetails defaults to false");
}

#[test]
fn bulk_options_reject_out_of_range_page_bounds() {
    assert_eq!(
        BulkByQueryOptions::new().try_with_page_documents(0).err(),
        Some(RequestValidationError::InvalidRange),
        "a zero bound would never publish a page"
    );
    assert_eq!(
        BulkByQueryOptions::new().try_with_page_documents(MAX_BULK_PAGE_DOCUMENTS + 1).err(),
        Some(RequestValidationError::InvalidRange),
        "the engine's maximum caps the bound"
    );
}

#[test]
fn delete_requests_refuse_by_default_and_carry_the_confirmation_token() {
    let request = DeleteByQueryRequest::try_new(
        selection_spec(),
        BulkByQueryOptions::new(),
        LocalDurability::Written,
    )
    .expect("valid request");
    assert!(request.confirmation().is_none(), "no token means the engine seam refuses");
    assert_eq!(request.durability(), LocalDurability::Written);
    assert_eq!(request.options().page_documents(), MAX_BULK_PAGE_DOCUMENTS);
    assert_eq!(
        request.query().source().collection().map(terrabase_v2::types::CollectionName::as_str),
        Some("orders")
    );
    assert!(request.operation_id().is_none());

    let token = DeletionConfirmation::confirm_deletions();
    let confirmed = request
        .clone()
        .with_confirmation(token)
        .with_operation_id(OperationId::try_new("run-1").expect("valid identifier"));
    assert!(confirmed.confirmation().is_some());
    assert_eq!(confirmed.operation_id().map(|id| id.as_str()), Some("run-1"));
}

#[test]
fn patch_requests_carry_the_typed_patch_vocabulary() {
    let request = PatchByQueryRequest::try_new(
        selection_spec(),
        one_field_patch(),
        BulkByQueryOptions::new().with_details(true),
        LocalDurability::Synced,
    )
    .expect("valid request");
    assert_eq!(request.patch().operations().len(), 1);
    assert!(request.options().details(), "V1's RetrieveDetails is an explicit selection");
    assert_eq!(request.durability(), LocalDurability::Synced);
    assert!(request.operation_id().is_none());
    let named = request.with_operation_id(OperationId::try_new("run-2").expect("valid identifier"));
    assert_eq!(named.operation_id().map(|id| id.as_str()), Some("run-2"));
}

#[test]
fn delete_requests_clone_preserves_the_confirmation_and_identity() {
    let request = DeleteByQueryRequest::try_new(
        selection_spec(),
        BulkByQueryOptions::new(),
        LocalDurability::Buffered,
    )
    .expect("valid request")
    .with_confirmation(DeletionConfirmation::confirm_deletions())
    .with_operation_id(OperationId::try_new("run-3").expect("valid identifier"));
    let clone = request.clone();
    assert!(clone.confirmation().is_some());
    assert_eq!(clone.operation_id().map(|id| id.as_str()), Some("run-3"));
    assert_eq!(clone.durability(), LocalDurability::Buffered);
}
