//! In-crate contract tests for the query-to-write bridge (GAP-Q10, ticket
//! #65), per `docs/query/gapq10-bulk-by-query.md`.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. The proofs cover the decision record's
//! contract rows: the confirmation refusal, bounded page-wise execution with
//! per-page receipts, the selection limit, the strict-stale refusal, the
//! allow-stale execution, the typed patch application with `NotModified`
//! accounting, the typed patch refusal that stops a run, the bounded detail
//! window, the rejected-page accounting, and the engine's audit journal.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::bulk::{
    BulkByQueryOptions, BulkDetailOutcome, BulkOperationKind, BulkRefusal, BulkTermination,
    DeleteByQueryRequest, DeletionConfirmation, MAX_BULK_DETAILS, MAX_BULK_PAGE_DOCUMENTS,
    PatchByQueryRequest,
};
use super::{
    IndexNamespace, QueryCondition, QueryError, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
};
use crate::engine::{
    CommitError, DocumentPatch, Engine, EngineConfig, PatchOperation, PatchPath, PatchPathSegment,
};
use crate::operations::RequestValidationError;
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-gapq10-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn document(id: &str, fields: &[(&str, DocumentValue)]) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new("orders").expect("valid identifier"),
        object(fields),
    )
}

fn commit_documents(engine: &Engine, scope: &TenantScope, tag: &str, documents: Vec<Document>) {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::New).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        crate::engine::CommitOutcome::Committed(_) => {}
        other => panic!("seed commit did not succeed: {other:?}"),
    }
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn selection_spec(
    tenant_claim: &TenantId,
    paging: QueryPaging,
    staleness: StalenessPolicy,
) -> QuerySpec {
    QuerySpec::try_new(
        tenant_claim.clone(),
        QuerySource::try_new(CollectionName::try_new("orders").expect("valid identifier"))
            .expect("user collection"),
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "tenant-visible".to_owned(),
            value: DocumentValue::Boolean(true),
        }])
        .expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        paging,
        staleness,
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn strict_fresh() -> StalenessPolicy {
    StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("index-less strict policy")
}

fn all_pages(paging: QueryPaging) -> QueryPaging {
    paging
}

fn remaining_documents(engine: &Engine, scope: &TenantScope) -> Vec<Document> {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    engine
        .snapshot_documents(scope, &snapshot)
        .expect("snapshot view")
        .into_iter()
        .filter(|document| document.collection().as_str() == "orders")
        .collect()
}

fn confirmed_delete(
    spec: QuerySpec,
    options: BulkByQueryOptions,
) -> Result<DeleteByQueryRequest, RequestValidationError> {
    DeleteByQueryRequest::try_new(spec, options, LocalDurability::Synced)
        .map(|request| request.with_confirmation(DeletionConfirmation::confirm_deletions()))
}

fn number(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

#[test]
fn delete_by_query_refuses_without_confirmation() {
    let (dir, engine) = open_engine("delete-no-confirm");
    let scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &scope,
        "seed",
        vec![document("doc-1", &[("tenant-visible", DocumentValue::Boolean(true))])],
    );

    let request = DeleteByQueryRequest::try_new(
        selection_spec(
            &tenant("tenant-a"),
            all_pages(QueryPaging::try_new(0, None).expect("valid paging")),
            strict_fresh(),
        ),
        BulkByQueryOptions::new(),
        LocalDurability::Synced,
    )
    .expect("valid request");
    assert!(request.confirmation().is_none(), "the refusal default carries no token");

    let receipt = engine.queries().delete_by_query(&scope, request, &Cancellation::new());
    assert_eq!(receipt.kind(), BulkOperationKind::DeleteByQuery);
    assert!(
        matches!(
            receipt.termination(),
            BulkTermination::Refused(BulkRefusal::ConfirmationRequired)
        ),
        "a deletion without the explicit token refuses typed: {:?}",
        receipt.termination()
    );
    assert_eq!(receipt.selected(), 0);
    assert_eq!(receipt.processed(), 0);
    assert_eq!(receipt.unattempted(), 0);
    assert!(receipt.reconciles());
    assert!(!receipt.is_complete());
    assert_eq!(remaining_documents(&engine, &scope).len(), 1, "nothing was touched");
    assert!(
        !engine.bulk_operations_audit().iter().any(|entry| entry.stage().is_dispatched()),
        "a refused run never dispatches"
    );
    assert!(
        engine
            .bulk_operations_audit()
            .iter()
            .any(|entry| entry.stage().is_refused(&BulkRefusal::ConfirmationRequired)),
        "the refusal is audited"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_by_query_deletes_matches_in_bounded_pages() {
    let (dir, engine) = open_engine("delete-pages");
    let scope = scope(&engine, "tenant-a");
    let documents: Vec<Document> = (1..=10)
        .map(|index| {
            document(&format!("doc-{index}"), &[("tenant-visible", DocumentValue::Boolean(true))])
        })
        .collect();
    commit_documents(&engine, &scope, "seed", documents);

    let options = BulkByQueryOptions::new().try_with_page_documents(4).expect("valid page bound");
    let request = confirmed_delete(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            strict_fresh(),
        ),
        options,
    )
    .expect("valid request")
    .with_operation_id(OperationId::try_new("run-delete").expect("valid identifier"));

    let receipt = engine.queries().delete_by_query(&scope, request, &Cancellation::new());
    assert!(receipt.is_complete(), "termination: {:?}", receipt.termination());
    assert_eq!(receipt.selected(), 10);
    assert_eq!(receipt.processed(), 10);
    assert_eq!(receipt.committed_pages(), 3, "10 documents at bound 4 page as 4+4+2");
    let pages = receipt.pages();
    assert_eq!(pages.len(), 3);
    assert_eq!(pages[0].documents(), 4);
    assert_eq!(pages[1].documents(), 4);
    assert_eq!(pages[2].documents(), 2);
    assert_eq!(pages[0].ordinal(), 0);
    assert_eq!(pages[2].ordinal(), 2);
    assert!(
        pages[0].operation_id() != pages[1].operation_id()
            && pages[1].operation_id() != pages[2].operation_id(),
        "each page is one transaction with its own identity"
    );
    assert!(pages.iter().all(|page| page.durability() == LocalDurability::Synced));
    assert!(receipt.reconciles());
    assert_eq!(remaining_documents(&engine, &scope).len(), 0, "every match is gone");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_by_query_respects_the_selection_limit() {
    let (dir, engine) = open_engine("delete-limit");
    let scope = scope(&engine, "tenant-a");
    let documents: Vec<Document> = (1..=5)
        .map(|index| {
            document(&format!("doc-{index}"), &[("tenant-visible", DocumentValue::Boolean(true))])
        })
        .collect();
    commit_documents(&engine, &scope, "seed", documents);

    // V1's PageSize: the operation query keeps the caller's page size, so a
    // bounded selection caps the total documents the operation may touch.
    let request = confirmed_delete(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, Some(3)).expect("valid paging"),
            strict_fresh(),
        ),
        BulkByQueryOptions::new(),
    )
    .expect("valid request");
    let receipt = engine.queries().delete_by_query(&scope, request, &Cancellation::new());
    assert!(receipt.is_complete());
    assert_eq!(receipt.selected(), 3);
    assert_eq!(receipt.processed(), 3);
    let remaining = remaining_documents(&engine, &scope);
    assert_eq!(remaining.len(), 2, "the untouched matches stay");
    assert!(remaining.iter().all(|document| document.id().as_str() != "doc-1"));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_by_query_strict_stale_refuses_before_any_write() {
    let (dir, engine) = open_engine("delete-strict-stale");
    let scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &scope,
        "seed",
        vec![document("doc-1", &[("tenant-visible", DocumentValue::Boolean(true))])],
    );

    // V1: `AllowStale == false` and a stale index throw before any command
    // runs (AbstractDatabaseQueryRunner.cs:167-168). The strict policy over
    // a namespace the catalog cannot supply is the same refusal.
    let namespace =
        IndexNamespace::try_new(tenant("tenant-a"), "ix-orders-status").expect("valid namespace");
    let policy = StalenessPolicy::try_new(StalenessMode::Strict, vec![namespace])
        .expect("valid strict policy");
    let request = confirmed_delete(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            policy,
        ),
        BulkByQueryOptions::new(),
    )
    .expect("valid request");
    let receipt = engine.queries().delete_by_query(&scope, request, &Cancellation::new());
    assert_eq!(
        receipt.termination(),
        &BulkTermination::Selection(QueryError::IndexNotReady),
        "the strict-stale refusal is typed and selection-scoped"
    );
    assert_eq!(receipt.processed(), 0);
    assert_eq!(receipt.selected(), 0, "nothing was selected or written");
    assert!(receipt.reconciles());
    assert_eq!(remaining_documents(&engine, &scope).len(), 1, "the document survives");
    assert!(
        engine.bulk_operations_audit().iter().any(|entry| entry.stage().is_failed()),
        "the failed selection is audited"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_by_query_allow_stale_proceeds_and_reports_stale() {
    let (dir, engine) = open_engine("delete-allow-stale");
    let scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &scope,
        "seed",
        vec![
            document("doc-1", &[("tenant-visible", DocumentValue::Boolean(true))]),
            document("doc-2", &[("tenant-visible", DocumentValue::Boolean(true))]),
        ],
    );

    let namespace =
        IndexNamespace::try_new(tenant("tenant-a"), "ix-orders-status").expect("valid namespace");
    let policy = StalenessPolicy::try_new(StalenessMode::AllowStale, vec![namespace])
        .expect("valid allow-stale policy");
    let request = confirmed_delete(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            policy,
        ),
        BulkByQueryOptions::new(),
    )
    .expect("valid request");
    let receipt = engine.queries().delete_by_query(&scope, request, &Cancellation::new());
    assert!(receipt.is_complete(), "termination: {:?}", receipt.termination());
    assert!(receipt.staleness().is_stale(), "the stale basis is disclosed, not hidden");
    assert_eq!(receipt.processed(), 2);
    assert_eq!(remaining_documents(&engine, &scope).len(), 0);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn patch_by_query_applies_the_typed_patch_to_matches() {
    let (dir, engine) = open_engine("patch-applies");
    let scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &scope,
        "seed",
        vec![
            document(
                "doc-1",
                &[("tenant-visible", DocumentValue::Boolean(true)), ("count", number(1))],
            ),
            document(
                "doc-2",
                &[("tenant-visible", DocumentValue::Boolean(true)), ("count", number(1))],
            ),
            document(
                "doc-3",
                &[("tenant-visible", DocumentValue::Boolean(true)), ("count", number(1))],
            ),
        ],
    );

    let patch = DocumentPatch::try_new(vec![PatchOperation::Increment {
        path: PatchPath::try_from_segments(vec![
            PatchPathSegment::field("count").expect("valid field"),
        ])
        .expect("valid path"),
        delta: 5,
    }])
    .expect("valid patch");
    let request = PatchByQueryRequest::try_new(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            strict_fresh(),
        ),
        patch,
        BulkByQueryOptions::new(),
        LocalDurability::Synced,
    )
    .expect("valid request");
    let receipt = engine.queries().patch_by_query(&scope, request, &Cancellation::new());
    assert!(receipt.is_complete(), "termination: {:?}", receipt.termination());
    assert_eq!(receipt.kind(), BulkOperationKind::PatchByQuery);
    assert_eq!(receipt.selected(), 3);
    assert_eq!(receipt.processed(), 3);
    assert_eq!(receipt.not_modified(), 0);
    for document in remaining_documents(&engine, &scope) {
        let DocumentValue::Object(fields) = document.value() else {
            panic!("object document");
        };
        assert_eq!(fields.get("count"), Some(&DocumentValue::Integer(6)));
    }

    // A patch whose result equals the original value stages nothing: V1's
    // NotModified status, accounted separately from applied patches.
    let same_value = DocumentPatch::try_new(vec![PatchOperation::Replace {
        path: PatchPath::try_from_segments(vec![
            PatchPathSegment::field("count").expect("valid field"),
        ])
        .expect("valid path"),
        value: DocumentValue::Integer(6),
    }])
    .expect("valid patch");
    let request = PatchByQueryRequest::try_new(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, Some(1)).expect("valid paging"),
            strict_fresh(),
        ),
        same_value,
        BulkByQueryOptions::new(),
        LocalDurability::Synced,
    )
    .expect("valid request");
    let receipt = engine.queries().patch_by_query(&scope, request, &Cancellation::new());
    assert!(receipt.is_complete());
    assert_eq!(receipt.selected(), 1);
    assert_eq!(receipt.processed(), 0, "an unchanged document stages nothing");
    assert_eq!(receipt.not_modified(), 1);
    assert!(receipt.reconciles());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn patch_by_query_typed_refusal_stops_the_run_and_reconciles() {
    let (dir, engine) = open_engine("patch-refusal");
    let scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &scope,
        "seed",
        vec![
            document(
                "doc-1",
                &[("tenant-visible", DocumentValue::Boolean(true)), ("keep", number(1))],
            ),
            // doc-2 lacks `keep`: the Replace below refuses typed on it.
            document("doc-2", &[("tenant-visible", DocumentValue::Boolean(true))]),
        ],
    );

    let patch = DocumentPatch::try_new(vec![PatchOperation::Replace {
        path: PatchPath::try_from_segments(vec![
            PatchPathSegment::field("keep").expect("valid field"),
        ])
        .expect("valid path"),
        value: number(2),
    }])
    .expect("valid patch");
    let options = BulkByQueryOptions::new().try_with_page_documents(1).expect("valid bound");
    let request = PatchByQueryRequest::try_new(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            strict_fresh(),
        ),
        patch,
        options,
        LocalDurability::Synced,
    )
    .expect("valid request");
    let receipt = engine.queries().patch_by_query(&scope, request, &Cancellation::new());
    assert!(
        matches!(receipt.termination(), BulkTermination::PatchRefused { .. }),
        "the typed patch refusal stops the run: {:?}",
        receipt.termination()
    );
    assert_eq!(receipt.processed(), 1, "the first page committed before the refusal");
    assert_eq!(receipt.committed_pages(), 1);
    assert_eq!(receipt.unattempted(), 1, "the refused page's document is never claimed");
    assert!(receipt.reconciles());
    let remaining = remaining_documents(&engine, &scope);
    assert_eq!(remaining.len(), 2);
    for document in &remaining {
        if document.id().as_str() == "doc-1" {
            let DocumentValue::Object(fields) = document.value() else {
                panic!("object document");
            };
            assert_eq!(fields.get("keep"), Some(&DocumentValue::Integer(2)));
        }
    }
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn bulk_details_window_is_bounded_and_dropped_count_discloses() {
    let (dir, engine) = open_engine("details-bound");
    let scope = scope(&engine, "tenant-a");
    let documents: Vec<Document> = (1..=70)
        .map(|index| {
            document(
                &format!("doc-{index}"),
                &[("tenant-visible", DocumentValue::Boolean(true)), ("count", number(1))],
            )
        })
        .collect();
    commit_documents(&engine, &scope, "seed", documents);

    let patch = DocumentPatch::try_new(vec![PatchOperation::Increment {
        path: PatchPath::try_from_segments(vec![
            PatchPathSegment::field("count").expect("valid field"),
        ])
        .expect("valid path"),
        delta: 1,
    }])
    .expect("valid patch");
    let options = BulkByQueryOptions::new().with_details(true);
    let request = PatchByQueryRequest::try_new(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            strict_fresh(),
        ),
        patch,
        options,
        LocalDurability::Synced,
    )
    .expect("valid request");
    let receipt = engine.queries().patch_by_query(&scope, request, &Cancellation::new());
    assert!(receipt.is_complete());
    assert_eq!(receipt.selected(), 70);
    assert_eq!(receipt.processed(), 70);
    assert_eq!(receipt.details().len(), MAX_BULK_DETAILS, "the per-document window is bounded");
    assert_eq!(
        receipt.dropped_details(),
        u64::try_from(70 - MAX_BULK_DETAILS).expect("small count"),
        "the overflow is counted and disclosed"
    );
    assert!(receipt.details().iter().all(|detail| detail.outcome() == BulkDetailOutcome::Patched));
    assert!(receipt.details().iter().all(|detail| detail.collection().as_str() == "orders"));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_by_query_rejected_page_commit_claims_nothing() {
    let (dir, engine) = open_engine("delete-rejected-page");
    let scope = scope(&engine, "tenant-a");
    let documents: Vec<Document> = (1..=5)
        .map(|index| {
            document(&format!("doc-{index}"), &[("tenant-visible", DocumentValue::Boolean(true))])
        })
        .collect();
    commit_documents(&engine, &scope, "seed", documents);

    // The injected pointer-publication fault rejects the first page's commit:
    // the run stops, earlier committed batches stay honest (here: none), and
    // the failed page's documents are accounted unattempted, never claimed.
    engine.arm_storage_fault(crate::storage::FaultPoint::PointerPublish);
    let options = BulkByQueryOptions::new().try_with_page_documents(2).expect("valid bound");
    let request = confirmed_delete(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            strict_fresh(),
        ),
        options,
    )
    .expect("valid request");
    let receipt = engine.queries().delete_by_query(&scope, request, &Cancellation::new());
    match receipt.termination() {
        BulkTermination::Rejected { error, .. } => {
            assert_eq!(*error, CommitError::StorageFailure);
        }
        other => panic!("a rejected page commit stops the run: {other:?}"),
    }
    assert_eq!(receipt.processed(), 0);
    assert_eq!(receipt.unattempted(), 5);
    assert!(receipt.reconciles());
    assert_eq!(remaining_documents(&engine, &scope).len(), 5, "nothing was published");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_by_query_pre_cancelled_signal_stops_before_selection() {
    let (dir, engine) = open_engine("delete-pre-cancelled");
    let scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &scope,
        "seed",
        vec![document("doc-1", &[("tenant-visible", DocumentValue::Boolean(true))])],
    );

    let signal = Cancellation::new();
    signal.cancel();
    let request = confirmed_delete(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            strict_fresh(),
        ),
        BulkByQueryOptions::new(),
    )
    .expect("valid request");
    let receipt = engine.queries().delete_by_query(&scope, request, &signal);
    assert_eq!(receipt.termination(), &BulkTermination::Cancelled);
    assert_eq!(receipt.selected(), 0);
    assert_eq!(receipt.processed(), 0);
    assert!(receipt.reconciles());
    assert_eq!(remaining_documents(&engine, &scope).len(), 1);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn bulk_runs_record_audit_entries_on_the_engine() {
    let (dir, engine) = open_engine("audit-entries");
    let scope = scope(&engine, "tenant-a");
    commit_documents(
        &engine,
        &scope,
        "seed",
        vec![document("doc-1", &[("tenant-visible", DocumentValue::Boolean(true))])],
    );

    let run_identity = OperationId::try_new("run-audited").expect("valid identifier");
    let request = confirmed_delete(
        selection_spec(
            &tenant("tenant-a"),
            QueryPaging::try_new(0, None).expect("valid paging"),
            strict_fresh(),
        ),
        BulkByQueryOptions::new(),
    )
    .expect("valid request")
    .with_operation_id(run_identity.clone());
    let receipt = engine.queries().delete_by_query(&scope, request, &Cancellation::new());
    assert!(receipt.is_complete());

    let entries = engine.bulk_operations_audit();
    let dispatched = entries
        .iter()
        .filter(|entry| {
            entry.stage().is_dispatched() && entry.kind() == BulkOperationKind::DeleteByQuery
        })
        .collect::<Vec<_>>();
    assert_eq!(dispatched.len(), 1, "one dispatch entry per run, V1's scheduling position");
    assert_eq!(dispatched[0].tenant(), &tenant("tenant-a"));
    assert_eq!(dispatched[0].operation(), Some(&run_identity));
    let completed = entries
        .iter()
        .filter(|entry| entry.stage().is_completed() && entry.operation() == Some(&run_identity))
        .collect::<Vec<_>>();
    assert_eq!(completed.len(), 1);
    assert_eq!(completed[0].selected(), 1);
    assert_eq!(completed[0].processed(), 1);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn bulk_options_bounds_are_the_documented_constants() {
    assert_eq!(
        MAX_BULK_PAGE_DOCUMENTS, 4096,
        "V1's server batch bound (AbstractDatabaseQueryRunner.cs:153)"
    );
    let options = BulkByQueryOptions::new();
    assert_eq!(options.page_documents(), MAX_BULK_PAGE_DOCUMENTS);
    assert!(!options.details());
    assert_eq!(
        BulkByQueryOptions::new().try_with_page_documents(0).err(),
        Some(RequestValidationError::InvalidRange)
    );
    assert_eq!(
        BulkByQueryOptions::new().try_with_page_documents(MAX_BULK_PAGE_DOCUMENTS + 1).err(),
        Some(RequestValidationError::InvalidRange)
    );
    assert_eq!(
        BulkByQueryOptions::new()
            .try_with_page_documents(MAX_BULK_PAGE_DOCUMENTS)
            .expect("the maximum itself is accepted")
            .page_documents(),
        MAX_BULK_PAGE_DOCUMENTS
    );
    assert!(BulkByQueryOptions::new().with_details(true).details());
}
