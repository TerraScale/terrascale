//! In-process tests for the typed Platform adapter seam.
//!
//! These are the W5-E red tests: typed request construction, end-to-end
//! dispatch per [`PublicOperation`](crate::operations::PublicOperation)
//! variant, tenant scoping from backend-supplied context, and the typed
//! lifecycle audit skeleton. They run inside the crate because the adapter
//! wiring is crate-private — [`PlatformAdapter::new`](super::PlatformAdapter::new)
//! takes the crate-private
//! [`TrustedAuthorizer`](super::TrustedAuthorizer) and the crate-private
//! [`EnginePort`](crate::engine::EnginePort), and the fixtures read the
//! engine identity through its crate-only accessor. The sibling
//! `tests/adapters/platform/` directory records why no integration target
//! can hold them.
//!
//! Claim discipline: these tests prove construction validation, dispatch,
//! tenant isolation, engine-side scope validation, and content-free audit
//! at this seam only. There is deliberately no authentication to prove
//! (PD-2026-08-26-11): no credential is read, accepted, or stored, and the
//! `CallerDenied`, `PermissionDenied`, and `Expired` refusal vocabulary
//! stays unused for the future Platform side.

use std::{collections::BTreeMap, path::PathBuf, sync::atomic::AtomicU64, time::Duration};

use crate::{
    adapters::protocol::{
        CallerContext, CallerEvidence, CredentialInput, CredentialKind, PrincipalId,
        ProtocolAdapter, ProtocolError,
    },
    engine::{CommitOutcome, Engine, EngineConfig},
    families::{
        AttachmentMutation, AttachmentRead, AttachmentWrite, FamilyReadRequest, FamilyReadResult,
        FamilyWriteRequest,
    },
    operations::{
        DocumentReadRequest, DocumentWriteRequest, FamilyOperation, PublicOperation,
        PublicResponse, QueryInput, QueryOperation, RequestValidationError, TqlQueryText,
    },
    query::{
        OrderDirection, OrderingKey, QueryCondition, QueryOrdering, QueryPaging, QueryPredicate,
        QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
    },
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    },
};

use super::{
    AuditOperation, AuditRefusal, AuditStage, MAX_AUDIT_EVENTS, PlatformAdapter, PlatformError,
    TrustedAuthorizer,
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/// A credential secret that never leaves the caller evidence. Every audit
/// and error form is asserted to not contain it.
const TEST_SECRET: &str = "test-secret-do-not-use";

/// A document field value no audit form may ever contain.
const AUDIT_CANARY: &str = "terrabase-audit-canary-value";

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn document_id(id: &str) -> DocumentId {
    DocumentId::try_new(id).expect("valid identifier")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn object_document(id: &str, collection_name: &str, name: &str) -> Document {
    Document::new(
        document_id(id),
        collection(collection_name),
        object(&[("name", DocumentValue::String(name.to_owned()))]),
    )
}

/// A unique durable location per test, following the in-crate fixture idiom.
fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-w5e-platform-{tag}-{}-{index}", std::process::id()))
}

/// Opens one durable Engine for a dispatch test.
fn open_engine(tag: &str) -> Engine {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

fn principal_id(name: &str) -> PrincipalId {
    PrincipalId::try_new(name).expect("valid principal")
}

/// Builds the backend-supplied caller context for one tenant.
fn caller_for(tenant_name: &str) -> CallerContext {
    caller_with(CredentialKind::Bearer, "principal-alice", tenant_name)
}

/// Builds a backend-supplied caller context with an explicit credential kind.
fn caller_with(kind: CredentialKind, principal_name: &str, tenant_name: &str) -> CallerContext {
    let evidence = CallerEvidence::try_new(
        principal_id(principal_name),
        tenant(tenant_name),
        CredentialInput::try_new(kind, TEST_SECRET).expect("valid credential"),
    )
    .expect("valid evidence");
    CallerContext::try_new(evidence).expect("valid context")
}

/// Builds the adapter the way Platform wiring does, over one real Engine.
fn adapter_for(engine: &Engine) -> PlatformAdapter<'_> {
    PlatformAdapter::new(TrustedAuthorizer::new(engine.identity()), engine)
}

/// Seeds documents through the Engine seam itself, as in-crate fixtures do.
fn seed(engine: &Engine, tenant_name: &str, tag: &str, documents: Vec<Document>) {
    let scope = TenantScope::from_parts(tenant(tenant_name), engine.identity());
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identity");
    let mut transaction = engine.begin(&scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("seed commit failed: {other:?}"),
    }
}

fn query_spec(tenant_name: &str) -> QuerySpec {
    QuerySpec::try_new(
        tenant(tenant_name),
        QuerySource::try_new(collection("users")).expect("valid source"),
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("ada".to_owned()),
        }])
        .expect("valid predicate"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("name", OrderDirection::Ascending).expect("valid key"),
        ])
        .expect("valid ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, Some(10)).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid staleness"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn read_operation(tenant_name: &str, collection_name: &str, id: &str) -> PublicOperation {
    PublicOperation::document_read(
        DocumentReadRequest::try_new(
            tenant(tenant_name),
            collection(collection_name),
            document_id(id),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid read"),
    )
}

fn write_operation_with(
    tenant_name: &str,
    document: Document,
    expected: ExpectedVersion,
    identity: &str,
    deadline: Deadline,
) -> PublicOperation {
    PublicOperation::document_write(
        DocumentWriteRequest::try_new(
            tenant(tenant_name),
            document,
            expected,
            OperationId::try_new(identity).expect("valid identity"),
            deadline,
            Cancellation::new(),
        )
        .expect("valid write"),
    )
}

fn write_operation(
    tenant_name: &str,
    document: Document,
    expected: ExpectedVersion,
    identity: &str,
) -> PublicOperation {
    write_operation_with(tenant_name, document, expected, identity, deadline())
}

fn typed_query_operation(tenant_name: &str) -> PublicOperation {
    PublicOperation::query(
        QueryOperation::try_new(
            QueryInput::typed(query_spec(tenant_name)),
            None,
            deadline(),
            Cancellation::new(),
        )
        .expect("valid query"),
    )
}

fn v1_query_operation() -> PublicOperation {
    let text = TqlQueryText::try_new("from users").expect("valid query text");
    PublicOperation::query(
        QueryOperation::try_new(QueryInput::tql(text), None, deadline(), Cancellation::new())
            .expect("valid query"),
    )
}

fn family_read_operation(id: &str) -> PublicOperation {
    let read = AttachmentRead::try_new(document_id(id), deadline(), Cancellation::new())
        .expect("valid read");
    PublicOperation::family(FamilyOperation::read(FamilyReadRequest::attachment_metadata(read)))
}

fn family_write_operation(
    engine: &Engine,
    tenant_name: &str,
    id: &str,
    identity: &str,
) -> PublicOperation {
    let scope = TenantScope::from_parts(tenant(tenant_name), engine.identity());
    let write = AttachmentWrite::try_new(
        scope,
        document_id(id),
        AttachmentMutation::Store {
            name: "logo.png".to_owned(),
            content_type: "image/png".to_owned(),
            size: 4,
            hash: "0123456789abcdef".to_owned(),
            replace: false,
        },
        ExpectedVersion::Any,
        OperationId::try_new(identity).expect("valid identity"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid attachment write");
    PublicOperation::family(FamilyOperation::write(FamilyWriteRequest::attachment_mutation(write)))
}

fn identity(value: &str) -> OperationId {
    OperationId::try_new(value).expect("valid identity")
}

// ---------------------------------------------------------------------------
// Typed request construction
// ---------------------------------------------------------------------------

#[test]
fn document_read_request_try_new_validates_and_reports_parts() {
    let request = DocumentReadRequest::try_new(
        tenant("tenant-a"),
        collection("users"),
        document_id("doc-1"),
        deadline(),
        Cancellation::new(),
    )
    .expect("a live request constructs");
    assert_eq!(request.tenant_claim(), &tenant("tenant-a"));
    assert_eq!(request.collection().as_str(), "users");
    assert_eq!(request.document_id().as_str(), "doc-1");
    assert!(!request.deadline().is_expired());
    assert!(!request.cancellation().is_cancelled());

    // An elapsed deadline is refused at construction.
    let expired = Deadline::after(Duration::from_millis(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(10));
    assert_eq!(
        DocumentReadRequest::try_new(
            tenant("tenant-a"),
            collection("users"),
            document_id("doc-1"),
            expired,
            Cancellation::new()
        ),
        Err(RequestValidationError::InvalidDeadline)
    );

    // An already cancelled signal is refused at construction.
    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        DocumentReadRequest::try_new(
            tenant("tenant-a"),
            collection("users"),
            document_id("doc-1"),
            deadline(),
            cancelled
        ),
        Err(RequestValidationError::InvalidCancellation)
    );
}

#[test]
fn document_write_request_try_new_validates_and_reports_parts() {
    let identity = OperationId::try_new("op-write-1").expect("valid identity");
    let request = DocumentWriteRequest::try_new(
        tenant("tenant-a"),
        object_document("doc-1", "users", AUDIT_CANARY),
        ExpectedVersion::Any,
        identity.clone(),
        deadline(),
        Cancellation::new(),
    )
    .expect("a live request constructs");
    assert_eq!(request.tenant_claim(), &tenant("tenant-a"));
    assert_eq!(request.document().id().as_str(), "doc-1");
    assert_eq!(request.expected_version(), ExpectedVersion::Any);
    assert_eq!(request.operation_id(), &identity);
    assert!(!request.deadline().is_expired());
    assert!(!request.cancellation().is_cancelled());

    let expired = Deadline::after(Duration::from_millis(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(10));
    assert_eq!(
        DocumentWriteRequest::try_new(
            tenant("tenant-a"),
            object_document("doc-1", "users", "ada"),
            ExpectedVersion::Any,
            identity.clone(),
            expired,
            Cancellation::new()
        ),
        Err(RequestValidationError::InvalidDeadline)
    );

    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        DocumentWriteRequest::try_new(
            tenant("tenant-a"),
            object_document("doc-1", "users", "ada"),
            ExpectedVersion::Any,
            identity,
            deadline(),
            cancelled
        ),
        Err(RequestValidationError::InvalidCancellation)
    );
}

#[test]
fn query_operation_try_new_validates_and_reports_parts() {
    use crate::query::{
        OrderDirection, OrderingKey, QueryCondition, QueryOrdering, QueryPaging, QueryPredicate,
        QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
    };

    let spec = QuerySpec::try_new(
        tenant("tenant-a"),
        QuerySource::try_new(collection("users")).expect("valid source"),
        QueryPredicate::all(vec![QueryCondition::Equals {
            field: "name".to_owned(),
            value: DocumentValue::String("ada".to_owned()),
        }])
        .expect("valid predicate"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("name", OrderDirection::Ascending).expect("valid key"),
        ])
        .expect("valid ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, Some(10)).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid staleness"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification");

    let typed = QueryOperation::try_new(
        QueryInput::typed(spec),
        Some(OperationId::try_new("op-query-1").expect("valid identity")),
        deadline(),
        Cancellation::new(),
    )
    .expect("a live typed query constructs");
    assert!(matches!(typed.input(), QueryInput::Typed(_)));
    assert_eq!(typed.operation_id().map(OperationId::as_str), Some("op-query-1"));
    assert!(!typed.deadline().is_expired());
    assert!(!typed.cancellation().is_cancelled());

    let text = TqlQueryText::try_new("from users where name = 'ada'").expect("valid query text");
    let compatible =
        QueryOperation::try_new(QueryInput::tql(text), None, deadline(), Cancellation::new())
            .expect("a live V1-compatible query constructs");
    assert!(matches!(compatible.input(), QueryInput::Tql(_)));
    assert!(compatible.operation_id().is_none());

    // The bounds are checked for both input forms.
    let expired = Deadline::after(Duration::from_millis(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(10));
    let text = TqlQueryText::try_new("from users").expect("valid query text");
    assert_eq!(
        QueryOperation::try_new(QueryInput::tql(text), None, expired, Cancellation::new()),
        Err(RequestValidationError::InvalidDeadline)
    );

    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        QueryOperation::try_new(
            QueryInput::tql(TqlQueryText::try_new("from users").expect("valid text")),
            None,
            deadline(),
            cancelled
        ),
        Err(RequestValidationError::InvalidCancellation)
    );
}

// ---------------------------------------------------------------------------
// End-to-end dispatch per PublicOperation variant
// ---------------------------------------------------------------------------

#[test]
fn document_read_dispatches_end_to_end() {
    let engine = open_engine("read-e2e");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-1", "users", "ada")]);
    let adapter = adapter_for(&engine);

    let response = adapter
        .dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-1"))
        .expect("the approved read dispatches");
    match response {
        PublicResponse::Document(Some(document)) => {
            assert_eq!(document.id().as_str(), "doc-1");
            assert_eq!(document.collection().as_str(), "users");
        }
        other => panic!("expected the seeded document, got {other:?}"),
    }

    // The typed read names one collection: the same identifier under another
    // collection is not the requested document, and an absent identifier
    // reads as absent.
    assert_eq!(
        adapter.dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "orders", "doc-1")),
        Ok(PublicResponse::Document(None))
    );
    assert_eq!(
        adapter
            .dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-absent")),
        Ok(PublicResponse::Document(None))
    );
}

#[test]
fn document_read_resolves_a_reused_identifier_by_collection() {
    let engine = open_engine("read-reuse");
    // One identifier under two collections of one tenant. Canonical key order
    // is tenant, collection, identifier, so "orders" sorts before "users":
    // an identifier-only lookup answers the orders copy first.
    seed(
        &engine,
        "tenant-a",
        "seed-1",
        vec![
            object_document("doc-1", "orders", "first-order"),
            object_document("doc-1", "users", "ada"),
        ],
    );
    let adapter = adapter_for(&engine);

    // Both directions answer the requested collection's own copy, not the
    // first copy the identifier resolves to.
    for (collection_name, expected_name) in [("orders", "first-order"), ("users", "ada")] {
        let response = adapter
            .dispatch(&caller_for("tenant-a"), read_operation("tenant-a", collection_name, "doc-1"))
            .expect("the collection-scoped read dispatches");
        let PublicResponse::Document(Some(document)) = response else {
            panic!("expected the {collection_name} copy of the reused identifier");
        };
        assert_eq!(document.collection().as_str(), collection_name);
        assert_eq!(
            document.value(),
            &object(&[("name", DocumentValue::String(expected_name.to_owned()))])
        );
    }

    // An identifier absent from the named collection still reads as absent,
    // whether the identifier exists elsewhere or not at all.
    assert_eq!(
        adapter.dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "invoices", "doc-1")),
        Ok(PublicResponse::Document(None))
    );
    assert_eq!(
        adapter
            .dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-absent")),
        Ok(PublicResponse::Document(None))
    );
}

#[test]
fn document_write_dispatches_end_to_end() {
    let engine = open_engine("write-e2e");
    let adapter = adapter_for(&engine);

    let response = adapter
        .dispatch(
            &caller_for("tenant-a"),
            write_operation(
                "tenant-a",
                object_document("doc-1", "users", "ada"),
                ExpectedVersion::New,
                "op-write-e2e",
            ),
        )
        .expect("the approved write dispatches");
    let PublicResponse::Commit(CommitOutcome::Committed(receipt)) = response else {
        panic!("expected a committed receipt");
    };
    assert_eq!(receipt.operation_id().as_str(), "op-write-e2e");

    // The written document is readable back through the same seam.
    let read = adapter
        .dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-1"))
        .expect("the read-back dispatches");
    let PublicResponse::Document(Some(document)) = read else {
        panic!("expected the written document");
    };
    assert_eq!(document.metadata().version(), 1);
}

#[test]
fn query_dispatches_end_to_end() {
    let engine = open_engine("query-e2e");
    seed(
        &engine,
        "tenant-a",
        "seed-1",
        vec![object_document("doc-1", "users", "ada"), object_document("doc-2", "users", "grace")],
    );
    let adapter = adapter_for(&engine);

    // The typed form carries a tenant claim that matches the backend scope.
    let response = adapter
        .dispatch(&caller_for("tenant-a"), typed_query_operation("tenant-a"))
        .expect("the typed query dispatches");
    let PublicResponse::Query(result) = response else {
        panic!("expected a query result");
    };
    assert_eq!(result.rows().len(), 1);

    // The V1-compatible form has no claim: the backend scope's snapshot
    // tenant is its binding.
    let response = adapter
        .dispatch(&caller_for("tenant-a"), v1_query_operation())
        .expect("the V1-compatible query dispatches");
    let PublicResponse::Query(result) = response else {
        panic!("expected a query result");
    };
    assert_eq!(result.rows().len(), 2);
}

#[test]
fn family_write_dispatches_end_to_end() {
    let engine = open_engine("family-write");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-fam-1", "users", "ada")]);
    let adapter = adapter_for(&engine);

    let response = adapter
        .dispatch(
            &caller_for("tenant-a"),
            family_write_operation(&engine, "tenant-a", "doc-fam-1", "op-family-write-1"),
        )
        .expect("the family write dispatches");
    assert!(matches!(response, PublicResponse::Commit(CommitOutcome::Committed(_))));
}

#[test]
fn family_read_dispatches_end_to_end() {
    let engine = open_engine("family-read");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-fam-1", "users", "ada")]);
    let adapter = adapter_for(&engine);
    adapter
        .dispatch(
            &caller_for("tenant-a"),
            family_write_operation(&engine, "tenant-a", "doc-fam-1", "op-family-read-seed"),
        )
        .expect("the attachment store dispatches");

    let response = adapter
        .dispatch(&caller_for("tenant-a"), family_read_operation("doc-fam-1"))
        .expect("the family read dispatches");
    let PublicResponse::Family(FamilyReadResult::AttachmentMetadata(result)) = response else {
        panic!("expected an attachment metadata result");
    };
    assert_eq!(result.entries().len(), 1);
    assert_eq!(result.entries()[0].name(), "logo.png");
}

// ---------------------------------------------------------------------------
// Tenant scoping from backend-supplied context
// ---------------------------------------------------------------------------

#[test]
fn cross_tenant_probe_is_denied_and_audited() {
    let engine = open_engine("cross-tenant");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-1", "users", "ada")]);
    let adapter = adapter_for(&engine);

    // One probe per tenant-bearing request form: the caller's backend
    // context says tenant-a while the request claims tenant-b.
    let probes = [
        read_operation("tenant-b", "users", "doc-1"),
        write_operation(
            "tenant-b",
            object_document("doc-1", "users", "mallory"),
            ExpectedVersion::Any,
            "op-probe-write",
        ),
        typed_query_operation("tenant-b"),
        family_write_operation(&engine, "tenant-b", "doc-1", "op-probe-family"),
    ];
    let probes_count = probes.len();
    for probe in probes {
        assert_eq!(
            adapter.dispatch(&caller_for("tenant-a"), probe),
            Err(PlatformError::TenantDenied),
            "a request claiming another tenant is denied"
        );
    }

    // Snapshot the audit journal now, before any honest dispatch runs: the
    // probes alone must have produced exactly one refused event each.
    let refused_events = adapter.audit_events();

    // The refusal is the claim mismatch, not the other tenant: a consistent
    // backend context for tenant-b dispatches and simply sees no documents.
    assert_eq!(
        adapter.dispatch(&caller_for("tenant-b"), read_operation("tenant-b", "users", "doc-1")),
        Ok(PublicResponse::Document(None))
    );

    // Nothing leaked: tenant-a still reads its document under its own scope.
    let read = adapter
        .dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-1"))
        .expect("the honest read dispatches");
    assert!(matches!(read, PublicResponse::Document(Some(_))));

    // Every probe was audited as refused before the engine, naming the
    // backend tenant and the probe's operation kind, and nothing was ever
    // dispatched for them.
    let events = refused_events;
    assert_eq!(events.len(), probes_count, "one refused event per probe");
    for event in &events {
        assert_eq!(event.stage(), AuditStage::Refused(AuditRefusal::TenantDenied));
        assert_eq!(event.tenant().as_str(), "tenant-a");
        assert!(!matches!(event.stage(), AuditStage::Dispatched));
    }
    assert_eq!(events[0].operation(), AuditOperation::DocumentRead);
    assert_eq!(events[1].operation(), AuditOperation::DocumentWrite);
    assert_eq!(events[2].operation(), AuditOperation::Query);
    assert_eq!(events[3].operation(), AuditOperation::FamilyWrite);
    assert_eq!(events[1].operation_id(), Some(&identity("op-probe-write")));
}

#[test]
fn dispatch_through_a_foreign_engine_binding_fails_closed() {
    let engine = open_engine("foreign-binding");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-1", "users", "ada")]);

    // Adapter wiring that never observed this Engine's identity cannot
    // dispatch: the Engine's own scope validation refuses the derived scope
    // before any data access. Tenant scoping stays engine-enforced.
    let misbound = PlatformAdapter::new(TrustedAuthorizer::new(0), &engine);
    assert_eq!(
        misbound.dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-1")),
        Err(PlatformError::EngineFailure { operation_id: None })
    );
}

// ---------------------------------------------------------------------------
// Bounded refusals and typed error mapping
// ---------------------------------------------------------------------------

#[test]
fn expired_deadline_is_refused_with_identity() {
    let engine = open_engine("deadline");
    let adapter = adapter_for(&engine);

    // The request constructs while its bound is live and the bound elapses
    // before dispatch: construction-time validation passes, and the
    // dispatch-time check refuses with the identity preserved.
    let nearly_expired = Deadline::after(Duration::from_millis(50)).expect("valid deadline");
    let operation = write_operation_with(
        "tenant-a",
        object_document("doc-1", "users", "ada"),
        ExpectedVersion::New,
        "op-deadline-1",
        nearly_expired,
    );
    std::thread::sleep(Duration::from_millis(100));
    let response = adapter.dispatch(&caller_for("tenant-a"), operation);
    assert_eq!(
        response,
        Err(PlatformError::Deadline { operation_id: Some(identity("op-deadline-1")) })
    );

    // The refusal left no engine effect.
    assert_eq!(
        adapter.dispatch(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-1")),
        Ok(PublicResponse::Document(None))
    );
}

#[test]
fn version_conflict_maps_to_typed_conflict_with_identity() {
    let engine = open_engine("conflict");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-1", "users", "ada")]);
    let adapter = adapter_for(&engine);

    let response = adapter.dispatch(
        &caller_for("tenant-a"),
        write_operation(
            "tenant-a",
            object_document("doc-1", "users", "mallory"),
            ExpectedVersion::Exact(999),
            "op-conflict-1",
        ),
    );
    assert_eq!(
        response,
        Err(PlatformError::Conflict { operation_id: Some(identity("op-conflict-1")) })
    );
}

#[test]
fn structurally_invalid_document_maps_to_invalid_request() {
    let engine = open_engine("invalid-document");
    let adapter = adapter_for(&engine);

    // Nesting past the codec's value-depth limit fails the Engine's logical
    // document validation, never the adapter's own policy.
    let mut value = DocumentValue::Integer(1);
    for _ in 0..70 {
        value = DocumentValue::Array(vec![value]);
    }
    let document = Document::new(document_id("doc-deep"), collection("users"), value);
    let response = adapter.dispatch(
        &caller_for("tenant-a"),
        write_operation("tenant-a", document, ExpectedVersion::New, "op-invalid-1"),
    );
    assert_eq!(response, Err(PlatformError::InvalidRequest));
}

// ---------------------------------------------------------------------------
// No credential surface
// ---------------------------------------------------------------------------

#[test]
fn dispatch_reads_no_credentials() {
    let engine = open_engine("no-credentials");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-1", "users", "ada")]);
    let adapter = adapter_for(&engine);

    let bearer = adapter
        .dispatch(
            &caller_with(CredentialKind::Bearer, "principal-x", "tenant-a"),
            read_operation("tenant-a", "users", "doc-1"),
        )
        .expect("the bearer caller dispatches");
    let api_key = adapter
        .dispatch(
            &caller_with(CredentialKind::ApiKey, "principal-x", "tenant-a"),
            read_operation("tenant-a", "users", "doc-1"),
        )
        .expect("the api-key caller dispatches");
    assert_eq!(format!("{bearer:?}"), format!("{api_key:?}"));

    // No credential material or fingerprint crosses the seam in any
    // response form.
    let form = format!("{bearer:?}");
    assert!(!form.contains(TEST_SECRET), "no secret crosses the seam: {form}");
    assert!(!form.contains("redacted:"), "no fingerprint crosses the seam: {form}");
}

// ---------------------------------------------------------------------------
// The public Protocol path over the Platform adapter
// ---------------------------------------------------------------------------

#[test]
fn protocol_production_path_dispatches_through_platform() {
    let engine = open_engine("protocol-path");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-1", "users", "ada")]);
    let adapter = adapter_for(&engine);

    // The W3-3 production acquisition path now executes: Protocol borrows
    // the Platform adapter, which alone scopes and dispatches.
    let protocol = ProtocolAdapter::from_platform(&adapter);
    let response = protocol
        .call(&caller_for("tenant-a"), read_operation("tenant-a", "users", "doc-1"))
        .expect("the public path dispatches");
    assert!(matches!(response, PublicResponse::Document(Some(_))));

    // A cross-tenant probe through the public seam collapses to the typed
    // Platform denial at the Protocol row.
    assert_eq!(
        protocol.call(&caller_for("tenant-a"), read_operation("tenant-b", "users", "doc-1")),
        Err(ProtocolError::PlatformDenied)
    );
}

// ---------------------------------------------------------------------------
// The typed lifecycle audit skeleton
// ---------------------------------------------------------------------------

#[test]
fn audit_records_typed_lifecycle_without_bodies_or_secrets() {
    let engine = open_engine("audit-lifecycle");
    let adapter = adapter_for(&engine);

    // The written document carries a canary field value: no audit form may
    // ever contain it.
    let canary_document = Document::new(
        document_id("doc-audit"),
        collection("users"),
        object(&[("secret-material", DocumentValue::String(AUDIT_CANARY.to_owned()))]),
    );
    adapter
        .dispatch(
            &caller_for("tenant-a"),
            write_operation("tenant-a", canary_document, ExpectedVersion::New, "op-audit-1"),
        )
        .expect("the write dispatches");

    let events = adapter.audit_events();
    assert_eq!(events.len(), 2, "one dispatched event and one completed event");
    assert_eq!(events[0].stage(), AuditStage::Dispatched);
    assert_eq!(events[1].stage(), AuditStage::Completed);
    for event in &events {
        assert_eq!(event.operation(), AuditOperation::DocumentWrite);
        assert_eq!(event.principal().as_str(), "principal-alice");
        assert_eq!(event.tenant().as_str(), "tenant-a");
        assert_eq!(event.operation_id(), Some(&identity("op-audit-1")));
        for form in [format!("{event:?}"), format!("{event}")] {
            assert!(!form.contains(AUDIT_CANARY), "no document body in audit: {form}");
            assert!(!form.contains(TEST_SECRET), "no secret in audit: {form}");
            assert!(!form.contains("redacted:"), "no fingerprint in audit: {form}");
            assert!(
                !form.to_lowercase().contains("credential"),
                "no credential reference in audit: {form}"
            );
        }
    }
    assert_eq!(
        events[1].sequence(),
        events[0].sequence() + 1,
        "lifecycle events of one dispatch are consecutive and monotonic"
    );
}

#[test]
fn audit_refusals_record_the_typed_category() {
    let engine = open_engine("audit-refusals");
    seed(&engine, "tenant-a", "seed-1", vec![object_document("doc-1", "users", "ada")]);
    let adapter = adapter_for(&engine);

    // A refusal before the engine records exactly one refused event.
    adapter
        .dispatch(&caller_for("tenant-a"), read_operation("tenant-b", "users", "doc-1"))
        .expect_err("the probe is denied");
    let events = adapter.audit_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].stage(), AuditStage::Refused(AuditRefusal::TenantDenied));
    assert_eq!(events[0].operation(), AuditOperation::DocumentRead);

    // An elapsed deadline records the deadline category with the identity.
    let nearly_expired = Deadline::after(Duration::from_millis(50)).expect("valid deadline");
    let operation = write_operation_with(
        "tenant-a",
        object_document("doc-2", "users", "ada"),
        ExpectedVersion::New,
        "op-audit-deadline",
        nearly_expired,
    );
    std::thread::sleep(Duration::from_millis(100));
    adapter.dispatch(&caller_for("tenant-a"), operation).expect_err("the bound elapsed");
    let events = adapter.audit_events();
    assert_eq!(events.len(), 2);
    assert_eq!(events[1].stage(), AuditStage::Refused(AuditRefusal::Deadline));
    assert_eq!(events[1].operation_id(), Some(&identity("op-audit-deadline")));

    // An engine-side refusal records the dispatch and the refusal: the
    // conflict crossed the engine seam, so its lifecycle is visible whole.
    adapter
        .dispatch(
            &caller_for("tenant-a"),
            write_operation(
                "tenant-a",
                object_document("doc-1", "users", "mallory"),
                ExpectedVersion::Exact(999),
                "op-audit-conflict",
            ),
        )
        .expect_err("the version conflicts");
    let events = adapter.audit_events();
    assert_eq!(events.len(), 4);
    assert_eq!(events[2].stage(), AuditStage::Dispatched);
    assert_eq!(events[3].stage(), AuditStage::Refused(AuditRefusal::Conflict));
}

#[test]
fn audit_display_labels_the_operation_identity_distinctly() {
    let engine = open_engine("audit-display");
    let adapter = adapter_for(&engine);

    adapter
        .dispatch(
            &caller_for("tenant-a"),
            write_operation(
                "tenant-a",
                object_document("doc-audit", "users", "ada"),
                ExpectedVersion::New,
                "op-audit-display",
            ),
        )
        .expect("the write dispatches");

    let events = adapter.audit_events();
    assert_eq!(events.len(), 2, "one dispatched event and one completed event");
    for event in &events {
        let rendered = format!("{event}");
        // The closed operation kind and the stable operation identity are
        // different facts, so they render under different labels.
        assert!(rendered.contains("operation=document-write"), "{rendered}");
        assert!(rendered.contains("operation_id=op-audit-display"), "{rendered}");
        assert_eq!(
            rendered.matches("operation=").count(),
            1,
            "the operation kind is the only fact labelled operation=: {rendered}"
        );
    }
}

#[test]
fn audit_journal_is_bounded_and_ordered() {
    let engine = open_engine("audit-bound");
    let adapter = adapter_for(&engine);

    // Cross-tenant probes are cheap refusals: they never touch the engine,
    // so the bound is exercised without durable churn.
    let probes = 2 * MAX_AUDIT_EVENTS + 5;
    for _ in 0..probes {
        adapter
            .dispatch(&caller_for("tenant-a"), read_operation("tenant-b", "users", "doc-1"))
            .expect_err("the probe is denied");
    }

    let events = adapter.audit_events();
    assert_eq!(events.len(), MAX_AUDIT_EVENTS, "the journal keeps only the bound");
    for pair in events.windows(2) {
        assert!(pair[0].sequence() < pair[1].sequence(), "recorded order is monotonic");
    }
    assert!(
        events.first().is_some_and(|event| event.sequence() > 1),
        "the oldest events were dropped, and the truncation is observable"
    );
    assert_eq!(events.last().expect("events exist").sequence(), probes as u64);
}
