//! In-process tracer tests for the typed Protocol adapter seam.
//!
//! These are the W3-3 red tests from the W3 core contract's Protocol
//! section. They run inside the crate because the forwarding proof needs the
//! crate-private [`PlatformPort`](crate::adapters::platform::PlatformPort)
//! double — `ProtocolAdapter::new_with_platform_port` exists only under
//! `cfg(test)` — and the family-write fixture needs the crate-private
//! `TenantScope::from_parts`. "Platform before engine" is proven by the
//! double being the only observable collaborator: this module holds no
//! Engine path, and the recorded order shows exactly one platform dispatch
//! per approved call.
//!
//! Claim discipline: these tests prove typed forwarding, structural caller
//! validation, redaction, and bounded deadline/cancellation refusal only.
//! Authorization, confirmation, audit, grant opacity, secret policy, and
//! PB-3042 belong to the W5 Platform Adapter packet; no parity row changes.

use std::{
    sync::{Arc, Mutex},
    thread,
    time::Duration,
};

use crate::{
    adapters::platform::{PlatformAdapter, PlatformError, PlatformPort},
    families::{
        AttachmentMutation, AttachmentRead, AttachmentWrite, FamilyReadRequest, FamilyWriteRequest,
    },
    operations::{
        DocumentReadRequest, DocumentWriteRequest, FamilyOperation, PublicOperation,
        PublicResponse, QueryInput, QueryInputError, QueryOperation, RequestValidationError,
        TqlQueryText,
    },
    query::QuerySpec,
    types::{
        Cancellation, Deadline, DocumentId, ExpectedVersion, OperationId, TenantId, TenantScope,
    },
};

use super::{
    CallerContext, CallerContextError, CallerEvidence, CredentialInput, CredentialKind,
    PrincipalId, ProtocolAdapter, ProtocolError,
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const TEST_SECRET: &str = "test-secret-do-not-use";

fn principal() -> PrincipalId {
    PrincipalId::try_new("principal-alice").expect("valid principal")
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-beta").expect("valid tenant")
}

fn credential() -> CredentialInput {
    CredentialInput::try_new(CredentialKind::Bearer, TEST_SECRET).expect("valid credential")
}

fn caller() -> CallerContext {
    let evidence =
        CallerEvidence::try_new(principal(), tenant(), credential()).expect("valid evidence");
    CallerContext::try_new(evidence).expect("valid context")
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn document() -> DocumentId {
    DocumentId::try_new("doc-1").expect("valid identifier")
}

fn attachment_read(deadline: Deadline, cancellation: Cancellation) -> PublicOperation {
    let read =
        AttachmentRead::try_new(document(), deadline, cancellation).expect("valid attachment read");
    PublicOperation::family(FamilyOperation::read(FamilyReadRequest::attachment_metadata(read)))
}

fn family_read_operation() -> PublicOperation {
    attachment_read(deadline(), Cancellation::new())
}

fn family_write_with(
    identity: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
) -> PublicOperation {
    let write = AttachmentWrite::try_new(
        TenantScope::from_parts(tenant(), 7),
        document(),
        AttachmentMutation::Delete { name: "logo.png".to_owned() },
        ExpectedVersion::Any,
        identity,
        deadline,
        cancellation,
    )
    .expect("valid attachment write");
    PublicOperation::family(FamilyOperation::write(FamilyWriteRequest::attachment_mutation(write)))
}

fn family_write_operation(identity: &str) -> PublicOperation {
    family_write_with(
        OperationId::try_new(identity).expect("valid identity"),
        deadline(),
        Cancellation::new(),
    )
}

fn scripted_response() -> PublicResponse {
    PublicResponse::Document(None)
}

// ---------------------------------------------------------------------------
// The in-process Platform transport double
// ---------------------------------------------------------------------------

/// One platform dispatch observed by the double.
struct SeenDispatch {
    principal: String,
    tenant: String,
    credential_kind: CredentialKind,
    same_caller_reference: bool,
    operation: PublicOperation,
}

/// Shared observation record filled by the in-process platform double.
#[derive(Default)]
struct Record {
    order: Mutex<Vec<&'static str>>,
    seen: Mutex<Vec<SeenDispatch>>,
}

/// The in-process Platform transport double.
///
/// It records that the platform port is the only collaborator reached —
/// "before engine" — and answers every dispatch from a fixed script. It
/// performs no authorization and touches no Engine path.
struct ScriptedPlatform {
    record: Arc<Record>,
    expected_caller: *const CallerContext,
    script: Result<PublicResponse, PlatformError>,
}

impl PlatformPort for ScriptedPlatform {
    fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError> {
        self.record.order.lock().expect("order lock").push("platform.dispatch");
        self.record.seen.lock().expect("seen lock").push(SeenDispatch {
            principal: caller.principal().to_string(),
            tenant: caller.tenant_claim().to_string(),
            credential_kind: caller.credential_kind(),
            same_caller_reference: std::ptr::eq(caller, self.expected_caller),
            operation,
        });
        self.script.clone()
    }
}

/// Builds the double plus its shared record for one caller.
fn scripted_platform(
    caller: &CallerContext,
    script: Result<PublicResponse, PlatformError>,
) -> (ScriptedPlatform, Arc<Record>) {
    let record = Arc::new(Record::default());
    let platform = ScriptedPlatform {
        record: Arc::clone(&record),
        expected_caller: std::ptr::from_ref(caller),
        script,
    };
    (platform, record)
}

/// Returns the recorded collaborator order.
fn order(record: &Record) -> Vec<&'static str> {
    record.order.lock().expect("order lock").clone()
}

/// Returns every operation the platform double received.
fn seen_operations(record: &Record) -> Vec<PublicOperation> {
    record.seen.lock().expect("seen lock").iter().map(|entry| entry.operation.clone()).collect()
}

/// Matches the closed public operation registry with no wildcard arm, so a
/// newly added variant — a generic route/body form, a storage handle, or a
/// private control-plane operation — fails this file's compilation.
fn registry_kind(operation: &PublicOperation) -> &'static str {
    match operation {
        PublicOperation::DocumentRead(_) => "document-read",
        PublicOperation::DocumentWrite(_) => "document-write",
        PublicOperation::Query(_) => "query",
        PublicOperation::Family(_) => "family",
        PublicOperation::Schema(_) => "schema",
    }
}

// ---------------------------------------------------------------------------
// Named tracer tests
// ---------------------------------------------------------------------------

#[test]
fn caller_context_try_new_structurally_validates_and_redacts_evidence() {
    // Structural validation of the untrusted evidence.
    assert_eq!(PrincipalId::try_new(""), Err(CallerContextError::EmptyPrincipal));
    assert_eq!(PrincipalId::try_new("   "), Err(CallerContextError::EmptyPrincipal));
    assert!(PrincipalId::try_new("principal-alice").is_ok());
    assert!(matches!(
        CredentialInput::try_new(CredentialKind::Bearer, ""),
        Err(CallerContextError::EmptyCredential)
    ));
    assert!(matches!(
        CredentialInput::try_new(CredentialKind::ApiKey, " \t "),
        Err(CallerContextError::EmptyCredential)
    ));
    assert!(CredentialInput::try_new(CredentialKind::Bearer, TEST_SECRET).is_ok());

    // Structurally valid evidence becomes one opaque context. The context
    // authenticates and authorizes nothing; Platform (W5) owns that.
    let evidence =
        CallerEvidence::try_new(principal(), tenant(), credential()).expect("valid evidence");
    let context = CallerContext::try_new(evidence).expect("valid context");
    assert_eq!(context.principal().to_string(), "principal-alice");
    assert_eq!(context.tenant_claim().to_string(), "tenant-beta");
    assert_eq!(context.credential_kind(), CredentialKind::Bearer);

    // Redaction: every observable form of the credential, the evidence, and
    // the context shows the principal, the tenant claim, the credential
    // kind, and a redacted fingerprint — never the secret.
    for kind in [CredentialKind::Bearer, CredentialKind::ApiKey] {
        let credential = CredentialInput::try_new(kind, TEST_SECRET).expect("valid credential");
        let credential_forms = [format!("{credential:?}"), format!("{credential}")];
        for (label, form) in [
            ("credential debug", &credential_forms[0]),
            ("credential display", &credential_forms[1]),
        ] {
            assert!(!form.contains(TEST_SECRET), "{label} must not reveal the secret: {form}");
            assert!(
                form.contains("redacted:"),
                "{label} must carry the redacted fingerprint: {form}"
            );
        }

        let evidence =
            CallerEvidence::try_new(principal(), tenant(), credential).expect("valid evidence");
        let evidence_debug = format!("{evidence:?}");
        let evidence_display = format!("{evidence}");
        let context = CallerContext::try_new(evidence).expect("valid context");
        for (label, form) in [
            ("evidence debug", evidence_debug),
            ("evidence display", evidence_display),
            ("context debug", format!("{context:?}")),
            ("context display", format!("{context}")),
        ] {
            assert!(!form.contains(TEST_SECRET), "{label} must not reveal the secret: {form}");
            assert!(
                form.contains("redacted:"),
                "{label} must carry the redacted fingerprint: {form}"
            );
            assert!(form.contains("principal-alice"), "{label} carries the principal: {form}");
            assert!(form.contains("tenant-beta"), "{label} carries the tenant claim: {form}");
        }
    }
}

#[test]
fn public_operation_constructors_reject_generic_payloads_and_permissions() {
    // V1-compatible text is the only accepted text form; generic SQL shapes
    // and oversized raw payloads are typed rejections, never forwarded
    // strings.
    assert_eq!(TqlQueryText::try_new(""), Err(QueryInputError::EmptyText));
    assert_eq!(TqlQueryText::try_new("   "), Err(QueryInputError::EmptyText));
    assert_eq!(
        TqlQueryText::try_new("SELECT * FROM users"),
        Err(QueryInputError::UnsupportedGrammar)
    );
    let oversized = format!("from {}", "x".repeat(17 * 1024));
    assert_eq!(TqlQueryText::try_new(&oversized), Err(QueryInputError::UnsupportedGrammar));
    assert!(TqlQueryText::try_new("from users where name = 'ada'").is_ok());

    // The query input forms accept only the typed specification or the
    // validated text (compile-shape).
    let typed_shape: fn(QuerySpec) -> QueryInput = QueryInput::typed;
    let compatible_shape: fn(TqlQueryText) -> QueryInput = QueryInput::tql;
    let _ = (typed_shape, compatible_shape);

    // Family payloads reject structurally invalid fields and already-set
    // bounded states at construction.
    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        AttachmentRead::try_new(document(), deadline(), cancelled),
        Err(RequestValidationError::InvalidCancellation)
    );

    // The operation registry is closed: the exhaustive match has no wildcard
    // arm, and the constructor signatures take only typed payloads — no
    // route, body, byte buffer, storage handle, or permission claim exists
    // to pass.
    assert_eq!(registry_kind(&family_read_operation()), "family");
    assert_eq!(registry_kind(&family_write_operation("op-registry")), "family");
    let document_read_shape: fn(DocumentReadRequest) -> PublicOperation =
        PublicOperation::document_read;
    let document_write_shape: fn(DocumentWriteRequest) -> PublicOperation =
        PublicOperation::document_write;
    let query_shape: fn(QueryOperation) -> PublicOperation = PublicOperation::query;
    let family_shape: fn(FamilyOperation) -> PublicOperation = PublicOperation::family;
    let _ = (document_read_shape, document_write_shape, query_shape, family_shape);

    // Caller evidence can state no permission: its only inputs are the
    // principal, the tenant claim, and one closed credential kind
    // (compile-shape), and its observable forms carry no permission claim.
    let evidence_shape: fn(
        PrincipalId,
        TenantId,
        CredentialInput,
    ) -> Result<CallerEvidence, CallerContextError> = CallerEvidence::try_new;
    let _ = evidence_shape;
    let context = caller();
    for form in [format!("{context:?}"), format!("{context}")] {
        assert!(
            !form.to_lowercase().contains("permission"),
            "caller forms carry no permission claim: {form}"
        );
    }
}

#[test]
fn protocol_call_accepts_only_opaque_caller_context() {
    // Compile-shape: the only public call seam takes the borrowed opaque
    // CallerContext and one closed PublicOperation. No call form accepts a
    // principal string, a token, or a decoded credential. The helper's
    // signature fails to compile if the seam accepts anything else.
    fn call_takes_only_the_opaque_caller(
        adapter: &ProtocolAdapter<'_>,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, ProtocolError> {
        adapter.call(caller, operation)
    }
    let call_shape: fn(
        &ProtocolAdapter<'_>,
        &CallerContext,
        PublicOperation,
    ) -> Result<PublicResponse, ProtocolError> = call_takes_only_the_opaque_caller;
    let _ = call_shape;

    // Runtime: the exact opaque reference reaches the platform unchanged.
    let caller = caller();
    let (platform, record) = scripted_platform(&caller, Ok(scripted_response()));
    let adapter = ProtocolAdapter::new_with_platform_port(&platform);
    let response =
        adapter.call(&caller, family_read_operation()).expect("typed forwarding succeeds");
    assert_eq!(response, scripted_response());
    let seen = record.seen.lock().expect("seen lock");
    let entry = &seen[0];
    assert!(entry.same_caller_reference, "the opaque caller reference is forwarded unchanged");
    assert_eq!(entry.principal, "principal-alice");
    assert_eq!(entry.tenant, "tenant-beta");
    assert_eq!(entry.credential_kind, CredentialKind::Bearer);
}

#[test]
fn protocol_forwarding_calls_platform_port_before_engine() {
    // The double is the only collaborator: this module holds no Engine path,
    // and the recorded order shows exactly one platform dispatch per call —
    // nothing reaches storage or the Engine ahead of the platform.
    let caller = caller();
    let (platform, record) = scripted_platform(&caller, Ok(scripted_response()));
    let adapter = ProtocolAdapter::new_with_platform_port(&platform);
    let response =
        adapter.call(&caller, family_read_operation()).expect("typed forwarding succeeds");
    assert_eq!(response, scripted_response());
    assert_eq!(
        order(&record),
        ["platform.dispatch"],
        "the platform port is dispatched exactly once, before anything else"
    );
    assert_eq!(seen_operations(&record).len(), 1);
}

#[test]
fn protocol_rejects_generic_route_body_and_storage_handle() {
    // No generic form exists to reject at runtime: the closed type system
    // carries the refusal. The proof is the exhaustive registry match with
    // no wildcard arm plus constructor signatures that accept only typed
    // payloads.
    assert_eq!(registry_kind(&family_read_operation()), "family");
    assert_eq!(registry_kind(&family_write_operation("op-generic")), "family");

    // Everything the platform double ever receives stays one of the four
    // typed variants: no route, body, bytes, or storage handle crossed the
    // seam.
    let caller = caller();
    let (platform, record) = scripted_platform(&caller, Ok(scripted_response()));
    let adapter = ProtocolAdapter::new_with_platform_port(&platform);
    for operation in [family_read_operation(), family_write_operation("op-generic")] {
        adapter.call(&caller, operation).expect("typed forwarding succeeds");
    }
    for operation in seen_operations(&record) {
        assert!(
            matches!(
                registry_kind(&operation),
                "document-read" | "document-write" | "query" | "family"
            ),
            "only typed registry operations reach the platform: {operation:?}"
        );
    }
    assert_eq!(
        order(&record),
        ["platform.dispatch", "platform.dispatch"],
        "each approved call dispatched exactly once"
    );
}

// ---------------------------------------------------------------------------
// Unnamed required behaviors
// ---------------------------------------------------------------------------

#[test]
fn approved_typed_operation_with_stable_identity_is_forwarded_and_answered() {
    let caller = caller();
    let identity = OperationId::try_new("op-stable-1").expect("valid identity");
    let operation = family_write_with(identity.clone(), deadline(), Cancellation::new());
    let (platform, record) = scripted_platform(&caller, Ok(scripted_response()));
    let adapter = ProtocolAdapter::new_with_platform_port(&platform);
    let response = adapter
        .call(&caller, operation.clone())
        .expect("the approved typed operation is forwarded");
    assert_eq!(response, scripted_response());

    let seen = seen_operations(&record);
    assert_eq!(seen.len(), 1);
    // The forwarded operation equals the sent one structurally. Whole-value
    // equality is impossible by design: a cloned request carries a distinct
    // cancellation-signal identity, so the observable comparison is the
    // redacted Debug form plus the stable identity below.
    assert_eq!(
        format!("{:?}", seen[0]),
        format!("{operation:?}"),
        "the platform receives the operation unchanged"
    );
    match &seen[0] {
        PublicOperation::Family(FamilyOperation::Write(request)) => {
            assert_eq!(
                request.operation_id(),
                &identity,
                "the stable operation identity survives forwarding"
            );
        }
        other => panic!("expected the forwarded family write, got {other:?}"),
    }
}

#[test]
fn malformed_or_unknown_operations_are_rejected_without_generic_forwarding() {
    // An elapsed deadline is the bounded malformed-at-call-time state that
    // is constructible at this base: it is refused before dispatch, with no
    // platform access and no generic fallback. Reads carry no mutating
    // identity, so the refusal reports none.
    let caller = caller();
    let expired_read = attachment_read(
        Deadline::after(Duration::from_millis(5)).expect("valid deadline"),
        Cancellation::new(),
    );
    thread::sleep(Duration::from_millis(20));

    let (platform, record) = scripted_platform(&caller, Ok(scripted_response()));
    let adapter = ProtocolAdapter::new_with_platform_port(&platform);
    assert_eq!(
        adapter.call(&caller, expired_read),
        Err(ProtocolError::Deadline { operation_id: None })
    );
    assert!(
        order(&record).is_empty(),
        "an expired deadline is refused before any platform dispatch"
    );

    // Unknown or malformed shapes have no typed value to build: the closed
    // registry means there is nothing to forward and nothing to fall back
    // to (registry_kind's exhaustive match compiles only over the four
    // approved variants).
    assert_eq!(registry_kind(&family_read_operation()), "family");
}

#[test]
fn protocol_rejects_private_control_plane_operations() {
    // The registry contains no private control-plane variant: every variant
    // is one of the four approved public operations, and the exhaustive
    // match in registry_kind fails to compile if a private shape is added.
    // Nothing can be forwarded that the closed type cannot express, and the
    // typed refusal vocabulary stays available for the seam.
    for operation in [family_read_operation(), family_write_operation("op-private")] {
        assert!(
            matches!(
                registry_kind(&operation),
                "document-read" | "document-write" | "query" | "family"
            ),
            "only approved public operations exist in the registry"
        );
    }
    assert_ne!(ProtocolError::PrivateOperation, ProtocolError::GenericAccessRejected);
}

#[test]
fn deadline_and_cancellation_errors_preserve_operation_identity() {
    // End-to-end: an elapsed deadline on a mutating operation is refused
    // before dispatch with its identity preserved.
    let caller = caller();
    let identity = OperationId::try_new("op-preserve-deadline").expect("valid identity");
    let expired_write = family_write_with(
        identity.clone(),
        Deadline::after(Duration::from_millis(5)).expect("valid deadline"),
        Cancellation::new(),
    );
    thread::sleep(Duration::from_millis(20));

    let (platform, record) = scripted_platform(&caller, Ok(scripted_response()));
    let adapter = ProtocolAdapter::new_with_platform_port(&platform);
    assert_eq!(
        adapter.call(&caller, expired_write),
        Err(ProtocolError::Deadline { operation_id: Some(identity.clone()) })
    );
    assert!(order(&record).is_empty(), "the refusal happens before any platform dispatch");

    // Decision logic: a set cancellation signal is refused under the same
    // identity rule. No public operation constructor admits an
    // already-cancelled signal and no cancel handle survives construction
    // into the request, so this half is proven at the refusal decision
    // itself; the mapping half is covered by the scripted platform row.
    let cancelled_identity = OperationId::try_new("op-preserve-cancelled").expect("valid identity");
    assert_eq!(
        super::bound_refusal(false, true, Some(cancelled_identity.clone())),
        Some(ProtocolError::Cancelled { operation_id: Some(cancelled_identity) })
    );
    let deadline_identity = OperationId::try_new("op-preserve-both").expect("valid identity");
    assert_eq!(
        super::bound_refusal(true, false, Some(deadline_identity.clone())),
        Some(ProtocolError::Deadline { operation_id: Some(deadline_identity.clone()) })
    );
    // The deadline is checked first when both bounds are violated, still
    // preserving the identity, and a live operation passes.
    assert_eq!(
        super::bound_refusal(true, true, Some(deadline_identity)),
        Some(ProtocolError::Deadline {
            operation_id: Some(OperationId::try_new("op-preserve-both").expect("valid identity"))
        })
    );
    assert_eq!(super::bound_refusal(false, false, None), None);
}

#[test]
fn protocol_adapter_supports_in_process_and_production_transports() {
    // Production-shaped acquisition: the public path borrows the W5
    // PlatformAdapter. Its constructor is W5 wiring and is not implemented
    // at this base, so W3-3 proves the acquisition shape compiles and hides
    // the private port; execution through it lands with W5.
    fn production_acquisition<'platform>(
        platform: &'platform PlatformAdapter<'_>,
    ) -> ProtocolAdapter<'platform> {
        ProtocolAdapter::from_platform(platform)
    }
    let acquire: for<'platform> fn(&'platform PlatformAdapter<'_>) -> ProtocolAdapter<'platform> =
        production_acquisition;
    let _ = acquire;

    // In-process test transport: the cfg(test) port double is the only
    // double W3-3 may construct, and it injects no production authority.
    let caller = caller();
    let (platform, _record) = scripted_platform(&caller, Ok(scripted_response()));
    let adapter = ProtocolAdapter::new_with_platform_port(&platform);
    assert!(adapter.call(&caller, family_read_operation()).is_ok());
}

#[test]
fn platform_error_forwarding_follows_the_contract_rows() {
    // Provisional W3-3 forward mapping; W5 owns the final pure
    // `map_platform_error` and its exhaustive red tests. Every row below
    // follows the contract's Platform-to-Protocol table exactly: denials
    // collapse to PlatformDenied, malformed requests become
    // InvalidOperation, a conflict surfaces as EngineFailure with the safe
    // identity, and deadline, cancellation, and engine failures preserve
    // their optional identity.
    let caller = caller();
    let identity = OperationId::try_new("op-map-1").expect("valid identity");
    let rows = [
        (PlatformError::CallerDenied, ProtocolError::PlatformDenied),
        (PlatformError::TenantDenied, ProtocolError::PlatformDenied),
        (PlatformError::PermissionDenied, ProtocolError::PlatformDenied),
        (PlatformError::Expired, ProtocolError::PlatformDenied),
        (PlatformError::ConfirmationRequired, ProtocolError::PlatformDenied),
        (PlatformError::AuditFailure, ProtocolError::PlatformDenied),
        (PlatformError::SecretPolicyViolation, ProtocolError::PlatformDenied),
        (PlatformError::InvalidRequest, ProtocolError::InvalidOperation),
        (
            PlatformError::Conflict { operation_id: Some(identity.clone()) },
            ProtocolError::EngineFailure { operation_id: Some(identity.clone()) },
        ),
        (
            PlatformError::Deadline { operation_id: Some(identity.clone()) },
            ProtocolError::Deadline { operation_id: Some(identity.clone()) },
        ),
        (
            PlatformError::Cancelled { operation_id: Some(identity.clone()) },
            ProtocolError::Cancelled { operation_id: Some(identity.clone()) },
        ),
        (
            PlatformError::EngineFailure { operation_id: Some(identity.clone()) },
            ProtocolError::EngineFailure { operation_id: Some(identity) },
        ),
    ];
    for (platform_error, expected) in rows {
        let (platform, record) = scripted_platform(&caller, Err(platform_error));
        let adapter = ProtocolAdapter::new_with_platform_port(&platform);
        assert_eq!(
            adapter.call(&caller, family_read_operation()),
            Err(expected),
            "the platform refusal maps to its Protocol row"
        );
        assert_eq!(order(&record).len(), 1, "the platform saw the dispatch before refusing");
    }
}

#[test]
fn credential_fingerprint_is_stable_prefixed_and_secret_free() {
    let first = super::redacted_fingerprint(TEST_SECRET);
    let second = super::redacted_fingerprint(TEST_SECRET);
    assert_eq!(first, second, "the fingerprint is stable for one secret");
    assert!(first.starts_with("redacted:"), "the fingerprint carries the fixed redaction prefix");
    assert_eq!(
        first.len(),
        "redacted:".len() + 16,
        "the fingerprint is one fixed-width 64-bit FNV-1a digest in hex"
    );
    assert_ne!(
        first,
        super::redacted_fingerprint("test-secret-do-not-use-either"),
        "different secrets fingerprint differently"
    );
    assert!(!first.contains("test-secret"), "the fingerprint never embeds the secret");
    // FNV-1a of the empty input is the offset basis, pinning the algorithm.
    assert_eq!(super::redacted_fingerprint(""), "redacted:cbf29ce484222325");
}
