//! In-process tests for the typed schema surface (SM-04).
//!
//! These rows prove the schema lifecycle as one closed operation set over
//! the Platform adapter — declare, status, validate document, migration
//! status, dry run, apply — driven end to end against a real durable
//! [`Engine`](crate::engine::Engine) exactly the way the sibling W5-E rows
//! drive the document, query, and family registries. They live in-crate for
//! the same reason those do: the adapter wiring is crate-private
//! ([`TrustedAuthorizer`](super::TrustedAuthorizer), the crate-private
//! [`EnginePort`](crate::engine::EnginePort), and the crate-only engine
//! identity accessor).
//!
//! Claim discipline: these tests prove dispatch, tenant isolation from
//! backend-supplied context, content-free audit, and the scripted
//! declare → refuse → migrate → valid scenario at this seam only. The wire
//! registration is proven by the `terrabase-tbep` crate's own rows; the
//! host, CLI, and studio surfaces have their own test families.

use std::{collections::BTreeMap, path::PathBuf, sync::atomic::AtomicU64, time::Duration};

use crate::{
    adapters::protocol::{
        CallerContext, CallerEvidence, CredentialInput, CredentialKind, PrincipalId,
    },
    engine::{CommitOutcome, Engine, EngineConfig},
    families::{
        SchemaDefinition, SchemaFieldType, SchemaRule, SchemaVersion,
        migrations::{FieldTransform, SchemaMigration, TransformLiteral},
    },
    operations::{
        DocumentWriteRequest, MigrationApplyRequest, MigrationDryRunRequest,
        MigrationStatusRequest, PublicOperation, PublicResponse, SchemaDeclareRequest,
        SchemaOperation, SchemaStatusRequest, SchemaValidateRequest,
    },
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    },
};

use super::{AuditOperation, AuditStage, PlatformAdapter, PlatformError, TrustedAuthorizer};

/// A credential secret that never leaves the caller evidence.
const TEST_SECRET: &str = "test-secret-do-not-use";

/// A document field value no audit form may ever contain.
const AUDIT_CANARY: &str = "terrabase-schema-audit-canary";

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn document_id(id: &str) -> DocumentId {
    DocumentId::try_new(id).expect("valid identifier")
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn text(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn version(value: u64) -> SchemaVersion {
    SchemaVersion::try_new(value).expect("valid version")
}

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-sm04-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> Engine {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

fn adapter_for(engine: &Engine) -> PlatformAdapter<'_> {
    PlatformAdapter::new(TrustedAuthorizer::new(engine.identity()), engine)
}

fn caller_for(tenant_name: &str) -> CallerContext {
    let evidence = CallerEvidence::try_new(
        PrincipalId::try_new("principal-schema").expect("valid principal"),
        tenant(tenant_name),
        CredentialInput::try_new(CredentialKind::Bearer, TEST_SECRET).expect("valid credential"),
    )
    .expect("valid evidence");
    CallerContext::try_new(evidence).expect("valid context")
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

/// The v1 users schema: a required text `email` and a text `nick`.
fn users_schema(at: u64) -> SchemaDefinition {
    SchemaDefinition::try_new(
        version(at),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "nick".to_owned(), kind: SchemaFieldType::Text },
        ],
    )
    .expect("valid schema")
}

/// The v2 users schema: `nick` is replaced by the integer `login_count`.
fn users_target(at: u64) -> SchemaDefinition {
    SchemaDefinition::try_new(
        version(at),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType {
                field: "login_count".to_owned(),
                kind: SchemaFieldType::Integer,
            },
        ],
    )
    .expect("valid target")
}

fn users_contract() -> SchemaMigration {
    SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        users_target(2),
        vec![
            FieldTransform::RemoveField { field: "nick".to_owned() },
            FieldTransform::AddField {
                field: "login_count".to_owned(),
                default: TransformLiteral::Integer(0),
            },
        ],
    )
    .expect("valid contract")
}

fn declare(tenant_name: &str, schema: Option<SchemaDefinition>, identity: &str) -> PublicOperation {
    PublicOperation::schema(SchemaOperation::declare(
        SchemaDeclareRequest::try_new(
            tenant(tenant_name),
            collection("users"),
            schema,
            OperationId::try_new(identity).expect("valid identity"),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid declare"),
    ))
}

fn schema_status(tenant_name: &str) -> PublicOperation {
    PublicOperation::schema(SchemaOperation::status(
        SchemaStatusRequest::try_new(
            tenant(tenant_name),
            collection("users"),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid status"),
    ))
}

fn validate(tenant_name: &str, value: DocumentValue) -> PublicOperation {
    PublicOperation::schema(SchemaOperation::validate(
        SchemaValidateRequest::try_new(
            tenant(tenant_name),
            collection("users"),
            value,
            deadline(),
            Cancellation::new(),
        )
        .expect("valid validate"),
    ))
}

fn migration_status(tenant_name: &str) -> PublicOperation {
    PublicOperation::schema(SchemaOperation::migration_status(
        MigrationStatusRequest::try_new(
            tenant(tenant_name),
            collection("users"),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid migration status"),
    ))
}

fn dry_run(tenant_name: &str, contract: &SchemaMigration) -> PublicOperation {
    PublicOperation::schema(SchemaOperation::migration_dry_run(
        MigrationDryRunRequest::try_new(
            tenant(tenant_name),
            contract.clone(),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid dry run"),
    ))
}

fn apply(tenant_name: &str, contract: &SchemaMigration, identity: &str) -> PublicOperation {
    PublicOperation::schema(SchemaOperation::migration_apply(
        MigrationApplyRequest::try_new(
            tenant(tenant_name),
            contract.clone(),
            // The scripted contract removes `nick` and adds `login_count`,
            // so its rollback limit is not reversible: the caller states
            // the acknowledgment the migration model requires.
            crate::families::migrations::MigrationOptions { acknowledge_not_rollbackable: true },
            OperationId::try_new(identity).expect("valid identity"),
            deadline(),
            Cancellation::new(),
        )
        .expect("valid apply"),
    ))
}

/// The scripted scenario the ticket names: declare schema → an invalid write
/// is refused → migrate → the same shape is valid under the new version.
/// Phase one (declare, status, validate, the refused write) is
/// `declare_and_refuse_the_invalid_write`; phase two (migrate and revalidate)
/// stays inline so the scenario reads in order.
#[test]
fn schema_lifecycle_scenario_declare_refuse_migrate_valid() {
    let engine = open_engine("scenario");
    let adapter = adapter_for(&engine);
    let caller = caller_for("tenant-a");
    seed(
        &engine,
        "tenant-a",
        "seed-users",
        vec![Document::new(
            document_id("ada"),
            collection("users"),
            object(&[("email", text("ada@example.test")), ("nick", text("ada"))]),
        )],
    );

    declare_and_refuse_the_invalid_write(&adapter, &caller);

    // Dry run states a ready plan with its evidence.
    let contract = users_contract();
    match adapter.dispatch(&caller, dry_run("tenant-a", &contract)).expect("the dry run dispatches")
    {
        PublicResponse::Schema(crate::operations::SchemaResponse::Plan(plan)) => {
            assert!(plan.is_ready(), "the v1 users move to v2 cleanly");
            assert_eq!(plan.from_version(), version(1));
            assert_eq!(plan.to_version(), version(2));
        }
        other => panic!("the dry run answers a migration plan: {other:?}"),
    }

    // Apply seats version 2 with the recorded receipt.
    match adapter
        .dispatch(&caller, apply("tenant-a", &contract, "apply-1"))
        .expect("the apply dispatches")
    {
        PublicResponse::Schema(crate::operations::SchemaResponse::Receipt(receipt)) => {
            assert_eq!(receipt.to_version(), version(2), "the version advances to 2");
            assert_eq!(receipt.documents(), 1);
            assert_eq!(receipt.transformed(), 1);
            assert!(!receipt.already_applied());
            assert!(
                receipt.acknowledged_not_rollbackable(),
                "the receipt records the stated rollback acknowledgment"
            );
            assert!(matches!(
                receipt.rollback_limit(),
                crate::families::migrations::RollbackLimit::NotReversible { .. }
            ));
        }
        other => panic!("the apply answers a migration receipt: {other:?}"),
    }

    // The collection now records version 2 and no live drive.
    match adapter.dispatch(&caller, schema_status("tenant-a")).expect("the status dispatches") {
        PublicResponse::Schema(crate::operations::SchemaResponse::Status(answer)) => {
            assert_eq!(answer.enforced_version(), Some(version(2)));
        }
        other => panic!("the status answers a schema status response: {other:?}"),
    }
    match adapter
        .dispatch(&caller, migration_status("tenant-a"))
        .expect("the migration status dispatches")
    {
        PublicResponse::Schema(crate::operations::SchemaResponse::MigrationStatus(status)) => {
            assert!(
                matches!(status, crate::families::migrations::MigrationStatus::Complete { .. }),
                "the completed drive is recorded, not a live one"
            );
        }
        other => panic!("the migration status answers a migration status: {other:?}"),
    }

    // The migrated shape — the one the old schema could not express — is
    // valid under the new version.
    match adapter
        .dispatch(
            &caller,
            validate(
                "tenant-a",
                object(&[
                    ("email", text("ada@example.test")),
                    ("login_count", DocumentValue::Integer(0)),
                ]),
            ),
        )
        .expect("the validation dispatches")
    {
        PublicResponse::Schema(crate::operations::SchemaResponse::Validation(answer)) => {
            assert_eq!(answer.enforced_version(), Some(version(2)));
            assert!(answer.valid(), "the migrated shape is valid under version 2");
        }
        other => panic!("the validation answers a schema validation response: {other:?}"),
    }
}

/// Phase one of the lifecycle scenario: the declaration is recorded, the
/// status reports it, the typed validation names the violated field, and the
/// engine write path refuses the violating document without staging anything.
fn declare_and_refuse_the_invalid_write(adapter: &PlatformAdapter<'_>, caller: &CallerContext) {
    // Declare: the collection records schema version 1.
    match adapter
        .dispatch(caller, declare("tenant-a", Some(users_schema(1)), "declare-1"))
        .expect("the declare dispatches")
    {
        PublicResponse::Schema(crate::operations::SchemaResponse::Declared(answer)) => {
            assert_eq!(answer.enforced_version(), Some(version(1)));
            assert_eq!(answer.rule_count(), 3, "three rules are recorded");
        }
        other => panic!("the declare answers a declared schema response: {other:?}"),
    }

    // Status reports the recorded version.
    match adapter.dispatch(caller, schema_status("tenant-a")).expect("the status dispatches") {
        PublicResponse::Schema(crate::operations::SchemaResponse::Status(answer)) => {
            assert_eq!(answer.enforced_version(), Some(version(1)));
            assert_eq!(answer.rule_count(), 3);
        }
        other => panic!("the status answers a schema status response: {other:?}"),
    }

    // A document whose email is an integer is named by the typed validation.
    match adapter
        .dispatch(
            caller,
            validate(
                "tenant-a",
                object(&[("email", DocumentValue::Integer(7)), ("nick", text("ada"))]),
            ),
        )
        .expect("the validation dispatches")
    {
        PublicResponse::Schema(crate::operations::SchemaResponse::Validation(answer)) => {
            assert_eq!(answer.enforced_version(), Some(version(1)));
            assert!(!answer.valid(), "an integer email does not satisfy the text rule");
            assert!(
                answer.violations().iter().any(|violation| violation.field() == "email"),
                "the violation names the field, never the value"
            );
        }
        other => panic!("the validation answers a schema validation response: {other:?}"),
    }

    // The engine write path refuses the same document: no partial write.
    let refused = adapter.dispatch(
        caller,
        PublicOperation::document_write(
            DocumentWriteRequest::try_new(
                tenant("tenant-a"),
                Document::new(
                    document_id("bad"),
                    collection("users"),
                    object(&[("email", DocumentValue::Integer(7))]),
                ),
                ExpectedVersion::Any,
                OperationId::try_new("write-bad").expect("valid identity"),
                deadline(),
                Cancellation::new(),
            )
            .expect("valid write"),
        ),
    );
    assert!(
        refused.is_err(),
        "the write path refuses a document that violates the declared schema"
    );
}

/// Every schema operation runs under the derived scope and refuses a tenant
/// claim that names another tenant.
#[test]
fn schema_surface_refuses_a_cross_tenant_claim() {
    let engine = open_engine("tenant");
    let adapter = adapter_for(&engine);
    let caller = caller_for("tenant-a");
    assert_eq!(
        adapter.dispatch(&caller, declare("tenant-b", Some(users_schema(1)), "declare-b")),
        Err(PlatformError::TenantDenied),
        "the schema surface refuses a tenant the scope does not name"
    );
}

/// The schema lifecycle is audited with its own operation rows and never
/// carries a document body.
#[test]
fn schema_lifecycle_is_audited_without_document_bodies() {
    let engine = open_engine("audit");
    let adapter = adapter_for(&engine);
    let caller = caller_for("tenant-a");

    let _ = adapter.dispatch(
        &caller,
        validate(
            "tenant-a",
            object(&[("email", text(AUDIT_CANARY)), ("nick", text(AUDIT_CANARY))]),
        ),
    );
    let _ = adapter.dispatch(&caller, declare("tenant-a", Some(users_schema(1)), "declare-audit"));

    let events = adapter.audit_events();
    assert!(
        events.iter().any(|event| matches!(
            event.operation(),
            AuditOperation::SchemaDeclare | AuditOperation::SchemaValidate
        )),
        "the schema lifecycle is audited with its own operation rows"
    );
    assert!(
        events.iter().any(|event| event.stage() == AuditStage::Completed),
        "the lifecycle completes and is recorded as completed"
    );
    for event in &events {
        for form in [format!("{event:?}"), format!("{event}")] {
            assert!(!form.contains(AUDIT_CANARY), "no document body in audit: {form}");
            assert!(!form.contains(TEST_SECRET), "no secret in audit: {form}");
        }
    }
}

/// The closed operation set has exactly the six lifecycle variants; the
/// exhaustive match below is the compiler's own proof that a seventh cannot
/// land unchecked, and no request of the set carries a byte form.
#[test]
fn the_schema_operation_set_is_closed_and_typed() {
    let engine = open_engine("closed");
    let adapter = adapter_for(&engine);
    let caller = caller_for("tenant-a");
    let contract = users_contract();
    let operations = [
        declare("tenant-a", Some(users_schema(1)), "closed-1"),
        schema_status("tenant-a"),
        validate("tenant-a", object(&[])),
        migration_status("tenant-a"),
        dry_run("tenant-a", &contract),
        apply("tenant-a", &contract, "closed-2"),
    ];
    let mut names = Vec::new();
    for operation in &operations {
        // One arm per variant, no wildcard for the schema set: adding a
        // seventh variant without extending this match breaks compilation.
        names.push(match operation {
            PublicOperation::Schema(SchemaOperation::Declare(_)) => "declare",
            PublicOperation::Schema(SchemaOperation::Status(_)) => "status",
            PublicOperation::Schema(SchemaOperation::Validate(_)) => "validate",
            PublicOperation::Schema(SchemaOperation::MigrationStatus(_)) => "migration-status",
            PublicOperation::Schema(SchemaOperation::MigrationDryRun(_)) => "dry-run",
            PublicOperation::Schema(SchemaOperation::MigrationApply(_)) => "apply",
            PublicOperation::DocumentRead(_)
            | PublicOperation::DocumentWrite(_)
            | PublicOperation::Query(_)
            | PublicOperation::Family(_) => "not-a-schema-operation",
        });
        let _ = adapter.dispatch(&caller, operation.clone());
    }
    assert_eq!(names.len(), 6, "exactly six lifecycle operations");
}
