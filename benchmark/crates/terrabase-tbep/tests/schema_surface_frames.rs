//! The SM-04 schema surface frames: the six typed schema-lifecycle
//! operations registered in the TBEP operation registry's new `schemas`
//! block, their typed (never byte) bodies, the closed generic family path
//! that cannot name the schema family, and the typed fail-closed behavior
//! of a shipped host that does not serve them yet.
//!
//! The engine-side seam is the closed operation registry in
//! `src/operations.rs` (`PublicOperation::Schema`); the engine execution is
//! `src/adapters/platform/engine_port.rs`. The registry carries the wire
//! slots and the generated bindings follow it on both sides; serving the
//! frames from a live engine process remains the engine-backed host
//! integrator action GAP-R03's D4 recorded. Until that lands, a schema
//! operation on a bound channel is answered with the typed
//! `operation_refused` — never a misleading `malformed_frame`.

use std::sync::Arc;

use serde_json::Value;
use terrabase_tbep::SyncDeliveryHost;
use terrabase_tbep::TbepFrame;
use terrabase_tbep::TbepServer;
use terrabase_tbep::generated::FRAME_ROWS;
use terrabase_tbep::generated::FrameKind;
use terrabase_tbep::generated::OPERATIONS;
use terrabase_tbep::generated::TbepBody;
use terrabase_tbep::generated::{
    BUDGETS, ErrorCode, FAMILY_KINDS, FieldTransform, FieldTransformStep, MigrationApplyRequest,
    MigrationDryRunRequest, MigrationPlan, MigrationReceipt, MigrationStatusRequest,
    RollbackLimitKind, SchemaDeclareRequest, SchemaMigrationContract, SchemaStatusRequest,
    SchemaValidateRequest, SchemaViolation, TransformCounts, TransformLiteral, ViolationKind,
};

/// The six schema-lifecycle operations with their request/response codes.
const SCHEMA_CODES: &[(FrameKind, FrameKind, u16, u16)] = &[
    (FrameKind::SchemaDeclareRequest, FrameKind::SchemaDeclareResponse, 336, 592),
    (FrameKind::SchemaStatusRequest, FrameKind::SchemaStatusResponse, 337, 593),
    (FrameKind::SchemaValidateRequest, FrameKind::SchemaValidateResponse, 338, 594),
    (FrameKind::MigrationStatusRequest, FrameKind::MigrationStatusResponse, 339, 595),
    (FrameKind::MigrationDryRunRequest, FrameKind::MigrationDryRunResponse, 340, 596),
    (FrameKind::MigrationApplyRequest, FrameKind::MigrationApplyResponse, 341, 597),
];

fn crate_root() -> std::path::PathBuf {
    std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn schema_document() -> Value {
    let path = crate_root().join("schema/tbep-registry.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()));
    serde_json::from_str(&text).expect("the registry schema parses")
}

/// One schema document of the documented JSON Schema subset: the shape the
/// `value` field of `schema.declare` carries, projected from the engine's
/// typed schema model.
fn users_schema_value() -> terrabase_tbep::TbepValue {
    use terrabase_tbep::TbepValue;
    TbepValue::Object(vec![
        (
            "$schema".into(),
            TbepValue::String("https://json-schema.org/draft/2020-12/schema".into()),
        ),
        ("type".into(), TbepValue::String("object".into())),
        (
            "properties".into(),
            TbepValue::Object(vec![
                (
                    "email".into(),
                    TbepValue::Object(vec![("type".into(), TbepValue::String("string".into()))]),
                ),
                (
                    "nick".into(),
                    TbepValue::Object(vec![("type".into(), TbepValue::String("string".into()))]),
                ),
            ]),
        ),
        ("required".into(), TbepValue::Array(vec![TbepValue::String("email".into())])),
        ("x-terrabase-schema-version".into(), TbepValue::Integer(1)),
    ])
}

#[test]
fn the_schema_operations_register_in_a_new_family_block() {
    for (request, response, request_code, response_code) in SCHEMA_CODES {
        assert_eq!(request.code(), *request_code, "the request code");
        assert_eq!(response.code(), *response_code, "the response answers at request + 0x100");
        let row = FRAME_ROWS
            .iter()
            .find(|row| row.code == *request_code)
            .unwrap_or_else(|| panic!("the schema request has a taxonomy row"));
        assert_eq!(row.family, "schemas", "the schema family");
        assert_eq!(row.class, "schema", "the schema budget class");
    }
    // The schema budget class exists and is written.
    let budget =
        BUDGETS.iter().find(|(class, _)| *class == "schema").expect("the schema budget class");
    assert_eq!(budget.1, 65_536, "a schema document and a bounded problem list fit the class");

    // Mutations pair with idempotency kinds; reads do not carry one.
    let mutation_kinds = [FrameKind::SchemaDeclareRequest, FrameKind::MigrationApplyRequest];
    for operation in OPERATIONS {
        let is_schema =
            operation.name.starts_with("schema.") || operation.name.starts_with("migration.");
        assert_eq!(
            is_schema,
            SCHEMA_CODES.iter().any(|(request, _, code, _)| *code == operation.request_code
                && request.code() != FrameKind::Error.code()),
            "every schema-family operation is one of the six registered kinds"
        );
        if is_schema {
            assert_eq!(
                mutation_kinds.iter().any(|kind| kind.code() == operation.request_code),
                operation.mutation,
                "operation {} mutation flag agrees with the idempotency pairing",
                operation.name
            );
            assert!(
                !operation.parity.is_empty(),
                "operation {} carries a parity mapping",
                operation.name
            );
        }
    }
}

/// No field of a schema-family frame — and no payload of a variant — may
/// declare a byte form.
fn assert_no_bytes(field: &Value, path: &str) {
    let kind = field["type"].as_str().unwrap_or_default();
    assert_ne!(kind, "bytes", "the field {path} must not carry a byte form");
    if let Some(variants) = field["variants"].as_array() {
        for variant in variants {
            if let Some(payload) = variant.get("field") {
                assert_no_bytes(payload, path);
            }
        }
    }
}

/// No request or response of the schema family carries a byte form: the
/// schema document rides as the protocol's typed `value`, and every other
/// field is an identity, a bound, a count, or a typed verdict.
#[test]
fn no_schema_frame_carries_a_byte_form() {
    let document = schema_document();
    let operations = document["operations"].as_array().expect("the operations list");
    let schema_operations: Vec<&Value> = operations
        .iter()
        .filter(|operation| {
            operation["family"].as_str() == Some("schemas")
                || operation["name"].as_str().is_some_and(|name| name.starts_with("migration."))
        })
        .collect();
    assert_eq!(schema_operations.len(), 6, "exactly six schema-family operations");

    for operation in &schema_operations {
        let name = operation["name"].as_str().expect("the operation name");
        for side in ["request", "response"] {
            let fields = operation[side]["fields"]
                .as_array()
                .unwrap_or_else(|| panic!("the {side} of {name} declares its fields"));
            assert!(!fields.is_empty(), "the {side} of {name} is a typed body, not an empty one");
            for field in fields {
                let field_name = field["name"].as_str().expect("the field name");
                assert_no_bytes(field, &format!("{name}.{side}.{field_name}"));
            }
        }
    }

    // The schema itself rides as the protocol's typed value.
    let declare = schema_operations
        .iter()
        .find(|operation| operation["name"].as_str() == Some("schema.declare"))
        .expect("the declare operation");
    let schema_field = declare["request"]["fields"]
        .as_array()
        .expect("the declare request fields")
        .iter()
        .find(|field| field["name"].as_str() == Some("schema"))
        .expect("the declare request names its schema field");
    assert_eq!(schema_field["type"].as_str(), Some("value"), "the schema is a typed value");
    assert_eq!(
        schema_field["optional"].as_bool(),
        Some(true),
        "a nil schema clears the collection's policy"
    );
}

/// The generic family byte path cannot name the schema family: the closed
/// family-kind list refuses it at encode time, fail-closed, so the schema
/// surface has no byte/frame escape hatch through `family.read`/`write`.
#[test]
fn the_generic_family_byte_path_cannot_name_the_schema_family() {
    assert!(
        !FAMILY_KINDS.contains(&"schema_validation"),
        "the generic family byte codec must not accept the schema family"
    );
    assert!(
        !FAMILY_KINDS.contains(&"conflict_tombstones"),
        "the generic family byte codec must not accept the tombstone family either"
    );
    let request = terrabase_tbep::generated::FamilyReadRequest {
        family: "schema_validation".into(),
        name: "users".into(),
        request_codec: "terrabase.family-request/1".into(),
        request: Vec::new(),
    };
    let sealed = TbepFrame::seal(request, 1, 0, None);
    assert!(
        sealed.is_err(),
        "sealing a generic family frame that names the schema family fails closed: {sealed:?}"
    );
    let admitted = terrabase_tbep::generated::FamilyReadRequest {
        family: "counter".into(),
        name: "users".into(),
        request_codec: "terrabase.family-request/1".into(),
        request: Vec::new(),
    };
    assert!(TbepFrame::seal(admitted, 1, 0, None).is_ok(), "a real family kind still seals");
}

#[test]
fn schema_bodies_round_trip_typed() {
    // A declare request: the typed schema value, nil never smuggled in.
    let declare = SchemaDeclareRequest {
        collection: "users".into(),
        schema: Some(users_schema_value()),
        operation_id: "op-declare-1".into(),
    };
    let declare_key = terrabase_tbep::IdempotencyKey::new("op-declare-1").expect("a valid key");
    let frame = TbepFrame::seal(declare, 3, 0, Some(&declare_key)).expect("the declare seals");
    let wire = frame.to_bytes();
    match TbepFrame::from_bytes(&wire).expect("the declare decodes").body() {
        TbepBody::SchemaDeclareRequest(body) => {
            assert_eq!(body.collection, "users");
            assert_eq!(body.operation_id, "op-declare-1");
            assert!(body.schema.is_some(), "the typed schema value survives the round trip");
        }
        other => panic!("the decoded body is the declare request: {other:?}"),
    }
    assert_eq!(TbepFrame::from_bytes(&wire).expect("decodes").to_bytes(), wire);

    // A validation answer: field and kind, never a field value.
    let validation = terrabase_tbep::generated::SchemaValidateResponse {
        enforced_version: Some(1),
        valid: false,
        violations: vec![SchemaViolation {
            field: "email".into(),
            kind: ViolationKind::TypeMismatch,
        }],
    };
    let frame = TbepFrame::seal(validation, 4, 3, None).expect("the validation seals");
    match TbepFrame::from_bytes(&frame.to_bytes()).expect("decodes").body() {
        TbepBody::SchemaValidateResponse(body) => {
            assert!(!body.valid);
            assert_eq!(body.violations[0].field, "email");
            assert_eq!(body.violations[0].kind, ViolationKind::TypeMismatch);
        }
        other => panic!("the decoded body is the validation response: {other:?}"),
    }
}

#[test]
fn migration_bodies_round_trip_typed() {
    // A dry-run plan: the evidence a later apply must reproduce, and the
    // bounded problem list with its disclosed truncation flag.
    let plan = MigrationPlan {
        manifest: "0123abcd".into(),
        collection: "users".into(),
        from_version: 1,
        to_version: 2,
        ready: false,
        documents: 2,
        source_digest: "a".repeat(64),
        result_digest: "b".repeat(64),
        transform_counts: TransformCounts { added: 0, removed: 0, renamed: 0, retyped: 0 },
        problems: vec![terrabase_tbep::generated::PlanProblem {
            document: "ada".into(),
            field: "nick".into(),
            kind: terrabase_tbep::generated::PlanProblemKind::TargetViolations,
            rejection: None,
            violations: vec![],
        }],
        problems_truncated: true,
    };
    let frame =
        TbepFrame::seal(terrabase_tbep::generated::MigrationDryRunResponse { plan }, 5, 3, None)
            .expect("the plan seals");
    match TbepFrame::from_bytes(&frame.to_bytes()).expect("decodes").body() {
        TbepBody::MigrationDryRunResponse(body) => {
            assert!(!body.plan.ready);
            assert!(body.plan.problems_truncated, "the truncation flag is stated, not hidden");
            assert_eq!(body.plan.problems.len(), 1);
        }
        other => panic!("the decoded body is the dry-run response: {other:?}"),
    }

    // An apply request: the closed transform vocabulary and the explicit
    // not-rollbackable acknowledgment.
    let contract = SchemaMigrationContract {
        collection: "users".into(),
        from_version: 1,
        to_version: 2,
        target: users_schema_value(),
        transforms: vec![
            FieldTransformStep {
                kind: FieldTransform::AddField,
                field: "login_count".into(),
                to: None,
                default: Some(TransformLiteral::Integer(0)),
                coercion: None,
            },
            FieldTransformStep {
                kind: FieldTransform::RenameField,
                field: "nick".into(),
                to: Some("nickname".into()),
                default: None,
                coercion: None,
            },
        ],
    };
    let apply = MigrationApplyRequest {
        contract,
        acknowledge_not_rollbackable: true,
        operation_id: "op-apply-1".into(),
    };
    let apply_key = terrabase_tbep::IdempotencyKey::new("op-apply-1").expect("a valid key");
    let frame = TbepFrame::seal(apply, 6, 0, Some(&apply_key)).expect("the apply seals");
    match TbepFrame::from_bytes(&frame.to_bytes()).expect("decodes").body() {
        TbepBody::MigrationApplyRequest(body) => {
            assert!(body.acknowledge_not_rollbackable);
            assert_eq!(body.contract.transforms.len(), 2);
        }
        other => panic!("the decoded body is the apply request: {other:?}"),
    }

    // An apply receipt: the recorded evidence and the rollback limit.
    let receipt = MigrationReceipt {
        collection: "users".into(),
        manifest: "0123abcd".into(),
        from_version: 1,
        to_version: 2,
        source_digest: "a".repeat(64),
        result_digest: "b".repeat(64),
        documents: 2,
        transformed: 2,
        transform_counts: TransformCounts { added: 0, removed: 0, renamed: 0, retyped: 0 },
        pages: 1,
        rollback_limit: RollbackLimitKind::NotReversible,
        acknowledged_not_rollbackable: true,
        already_applied: false,
    };
    let frame =
        TbepFrame::seal(terrabase_tbep::generated::MigrationApplyResponse { receipt }, 7, 6, None)
            .expect("the receipt seals");
    match TbepFrame::from_bytes(&frame.to_bytes()).expect("decodes").body() {
        TbepBody::MigrationApplyResponse(body) => {
            assert_eq!(body.receipt.to_version, 2);
            assert_eq!(body.receipt.rollback_limit, RollbackLimitKind::NotReversible);
        }
        other => panic!("the decoded body is the apply response: {other:?}"),
    }
}

#[test]
fn schema_requests_are_refused_typed_until_a_host_serves_them() {
    // The shipped hosts do not serve the schema frames; the wire must say
    // so typed (operation_refused), not misreport the request as a
    // direction violation or a malformed frame.
    let socket = std::env::temp_dir().join(format!("tbep-uds-schema-{}.sock", std::process::id()));
    let _server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(3)))
        .expect("the UDS listener binds");
    let mut stream = std::os::unix::net::UnixStream::connect(&socket).expect("connects");
    let bind = TbepFrame::seal(
        terrabase_tbep::generated::ChannelBindRequest {
            principal: "backend".into(),
            tenant: "tenant-a".into(),
        },
        1,
        0,
        None,
    )
    .expect("binds");
    bind.write_stream(&mut stream).expect("the bind writes");
    let bound = TbepFrame::read_stream(&mut stream).expect("the bind answers").expect("a frame");
    assert!(matches!(bound.body(), TbepBody::ChannelBindResponse(_)));

    let bodies = [
        TbepBody::from(SchemaStatusRequest { collection: "users".into() }),
        TbepBody::from(SchemaValidateRequest {
            collection: "users".into(),
            document: users_schema_value(),
        }),
        TbepBody::from(MigrationStatusRequest { collection: "users".into() }),
        TbepBody::from(MigrationDryRunRequest {
            contract: SchemaMigrationContract {
                collection: "users".into(),
                from_version: 1,
                to_version: 2,
                target: users_schema_value(),
                transforms: vec![],
            },
        }),
    ];
    for (index, body) in bodies.into_iter().enumerate() {
        let correlation = u64::try_from(index + 2).expect("a correlation");
        let frame = TbepFrame::seal(body, correlation, 0, None).expect("the schema request seals");
        frame.write_stream(&mut stream).expect("the request writes");
        let answer =
            TbepFrame::read_stream(&mut stream).expect("the server answers").expect("a frame");
        assert_eq!(answer.causation(), correlation, "the answer names its request");
        match answer.body() {
            TbepBody::Error(error) => assert_eq!(
                error.code,
                ErrorCode::OperationRefused,
                "the refusal is typed, not a decode failure"
            ),
            other => panic!("the unserved schema request is refused typed: {other:?}"),
        }
    }

    // The mutation carries the idempotency key the registry pairs with it.
    let declare = SchemaDeclareRequest {
        collection: "users".into(),
        schema: Some(users_schema_value()),
        operation_id: "op-declare-1".into(),
    };
    let key = terrabase_tbep::IdempotencyKey::new("op-declare-1").expect("a valid key");
    let frame = TbepFrame::seal(declare, 9, 0, Some(&key)).expect("the declare request seals");
    frame.write_stream(&mut stream).expect("the request writes");
    let answer = TbepFrame::read_stream(&mut stream).expect("the server answers").expect("a frame");
    match answer.body() {
        TbepBody::Error(error) => {
            assert_eq!(error.code, ErrorCode::OperationRefused, "the mutation is refused typed");
        }
        other => panic!("the unserved declare is refused typed: {other:?}"),
    }
}
