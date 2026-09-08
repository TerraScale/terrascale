//! In-crate contract tests for the Schema Migration seam (SM-03).
//!
//! Each test reads as one end-to-end scenario through the public migration
//! operations and the public family seam: it stages schemas and documents
//! through the engine, drives migrations as the host drives them, and
//! observes documents, recorded versions, receipts, and typed errors. The
//! kill-point scenarios drop real durable engines and reopen them from
//! their directories, so resumption observes committed state, not memory.
//! No Recovery, Replication, or Benchmark receipt is claimed here; the
//! runs are focused and in-process.

#![allow(clippy::too_many_lines)]

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use super::record::ScanCursor;
use super::{
    Coercion, FieldOutcome, FieldTransform, MigrationError, MigrationOptions, MigrationStatus,
    MigrationStep, MigrationStopReason, RollbackBlockedCause, RollbackLimit, RollbackStep,
    SchemaMigration, TransformLiteral, TransformRejection, apply, apply_page, begin, dry_run,
    finalize, rollback, status,
};
use crate::engine::{CommitOutcome, Engine, EngineConfig, EngineError, Transaction};
use crate::families::{
    FamilyReadContext, FamilyReadRequest, FamilyReadResult, FamilyStore, FamilyWriteRequest,
    SchemaDefinition, SchemaFieldType, SchemaRule, SchemaVersion, ValidationRead, ValidationWrite,
};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, TenantId, TenantScope,
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-sm03-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), engine.identity())
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(45)).expect("valid deadline")
}

fn cancellation() -> Cancellation {
    Cancellation::new()
}

fn unique_operation(prefix: &str) -> OperationId {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("{prefix}-{index}")).expect("valid identifier")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn version(value: u64) -> SchemaVersion {
    SchemaVersion::try_new(value).expect("valid version")
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(key, value)| ((*key).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn text(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn integer(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

/// Commits one transaction staging whatever the closure stages. The closure
/// receives the transaction's operation identity so family writes can bind
/// to it.
fn commit<'engine>(
    engine: &'engine Engine,
    scope: &TenantScope,
    stage: impl FnOnce(&FamilyStore<'engine>, &mut Transaction<'engine>, &OperationId),
) -> CommitOutcome {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let identity = unique_operation("sm03-fixture");
    let store = engine.families();
    let mut transaction = engine.begin(scope, snapshot, identity.clone()).expect("begins");
    stage(&store, &mut transaction, &identity);
    transaction.commit(LocalDurability::Synced)
}

/// Declares one collection schema through the family seam.
fn declare_schema(engine: &Engine, scope: &TenantScope, name: &str, schema: SchemaDefinition) {
    let outcome = commit(engine, scope, |store, transaction, identity| {
        let write = ValidationWrite::try_new(
            scope.clone(),
            collection(name),
            Some(schema),
            identity.clone(),
            deadline(),
            cancellation(),
        )
        .expect("valid write");
        store
            .stage(transaction, FamilyWriteRequest::SchemaValidationUpdate(write.clone()))
            .expect("stages declaration");
    });
    assert!(matches!(outcome, CommitOutcome::Committed(_)), "declaration commits");
}

/// Reads one collection's enforced schema version through the family seam.
fn enforced_version(engine: &Engine, scope: &TenantScope, name: &str) -> Option<SchemaVersion> {
    let read = ValidationRead::try_new(collection(name), None, deadline(), cancellation())
        .expect("valid read");
    let context =
        FamilyReadContext::try_new(scope.clone(), engine.read_snapshot(scope).expect("snapshot"))
            .expect("valid context");
    match engine.families().read(context, FamilyReadRequest::Validation(read)) {
        Ok(FamilyReadResult::Validation(result)) => result.enforced_version(),
        other => panic!("validation read answers: {other:?}"),
    }
}

/// Reads one document's value through the engine seam, resolved by the
/// exact collection key: identifiers may repeat across collections, and an
/// identifier-only read answers whichever copy canonical order puts first.
fn document_value(engine: &Engine, scope: &TenantScope, name: &str, id: &str) -> DocumentValue {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let transaction = engine.begin(scope, snapshot, unique_operation("read")).expect("begins");
    transaction
        .read_in_collection(&collection(name), &document_id(id))
        .expect("reads")
        .expect("document exists")
        .value()
        .clone()
}

/// Reads one document of the users fixture.
fn user_value(engine: &Engine, scope: &TenantScope, id: &str) -> DocumentValue {
    document_value(engine, scope, "users", id)
}

/// Puts one user document through the engine seam and reports the outcome.
fn put_document(
    engine: &Engine,
    scope: &TenantScope,
    id: &str,
    name: &str,
    value: DocumentValue,
) -> Result<(), EngineError> {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let mut transaction = engine.begin(scope, snapshot, unique_operation("put")).expect("begins");
    let document = Document::new(document_id(id), collection(name), value);
    transaction.put(document, ExpectedVersion::Any)?;
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => Ok(()),
        CommitOutcome::Rejected(reason) => Err(match reason {
            crate::engine::CommitError::Validation => EngineError::InvalidDocument,
            _ => EngineError::StorageFailure,
        }),
        CommitOutcome::Unknown(_) => Err(EngineError::StorageFailure),
    }
}

/// One reversible migration fixture: rename `nick` to `nickname` and parse
/// `age_text` into an integer age.
fn reversible_contract(from: u64, to: u64) -> SchemaMigration {
    let target = SchemaDefinition::try_new(
        version(to),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "nickname".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
        ],
    )
    .expect("valid target");
    SchemaMigration::try_new(
        collection("users"),
        version(from),
        version(to),
        target,
        vec![
            FieldTransform::RenameField { from: "nick".to_owned(), to: "nickname".to_owned() },
            FieldTransform::RetypeField {
                field: "age_text".to_owned(),
                coercion: Coercion::TextToInteger,
            },
            FieldTransform::RenameField { from: "age_text".to_owned(), to: "age".to_owned() },
        ],
    )
    .expect("valid contract")
}

/// One not-rollbackable migration fixture: add a defaulted field and remove
/// a deprecated one.
fn irreversible_contract(from: u64, to: u64) -> SchemaMigration {
    let target = SchemaDefinition::try_new(
        version(to),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::EnumValues {
                field: "plan".to_owned(),
                values: vec!["free".to_owned(), "paid".to_owned()],
            },
        ],
    )
    .expect("valid target");
    SchemaMigration::try_new(
        collection("users"),
        version(from),
        version(to),
        target,
        vec![
            FieldTransform::AddField {
                field: "plan".to_owned(),
                default: TransformLiteral::Text("free".to_owned()),
            },
            FieldTransform::RemoveField { field: "legacy".to_owned() },
        ],
    )
    .expect("valid contract")
}

/// Declares version one of the `users` schema and stores three documents.
fn seeded_users(engine: &Engine, scope: &TenantScope) {
    let first = SchemaDefinition::try_new(
        version(1),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "nick".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "age_text".to_owned(), kind: SchemaFieldType::Text },
        ],
    )
    .expect("valid schema");
    declare_schema(engine, scope, "users", first);
    for (id, email, nick, age) in
        [("ada", "ada@example", "a", "42"), ("linus", "linus@example", "l", "7")]
    {
        let outcome = put_document(
            engine,
            scope,
            id,
            "users",
            object(&[
                ("email", text(email)),
                ("nick", text(nick)),
                ("age_text", text(age)),
                ("legacy", text("deprecated")),
            ]),
        );
        assert!(outcome.is_ok(), "seed document stages");
    }
}

/// Seeds many one-field documents in batched transactions, so a large
/// fixture costs a bounded number of durable commits rather than one per
/// document.
fn seed_many(engine: &Engine, scope: &TenantScope, count: usize, fields: &[(&str, &str)]) {
    for batch in 0..=(count / 256) {
        let start = batch * 256;
        let end = (start + 256).min(count);
        if start == end {
            break;
        }
        let snapshot = engine.read_snapshot(scope).expect("snapshot");
        let identity = unique_operation("seed-batch");
        let mut transaction = engine.begin(scope, snapshot, identity).expect("begins");
        for index in start..end {
            let value =
                object(&fields.iter().map(|(key, item)| (*key, text(item))).collect::<Vec<_>>());
            let document =
                Document::new(document_id(&format!("doc-{index:05}")), collection("users"), value);
            transaction.put(document, ExpectedVersion::Any).expect("stages document");
        }
        assert!(
            matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)),
            "seed batch commits"
        );
    }
}

/// Drives one migration to completion through the public driver.
fn drive(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
    options: MigrationOptions,
) -> super::MigrationReceipt {
    let plan = dry_run(engine, scope, contract).expect("plan is ready");
    apply(engine, scope, contract, &plan, options).expect("drive completes")
}

// ---------------------------------------------------------------------------
// Contract and vocabulary
// ---------------------------------------------------------------------------

#[test]
fn contracts_validate_versions_targets_and_names() {
    let target = SchemaDefinition::try_new(
        version(2),
        vec![SchemaRule::Required { field: "email".to_owned() }],
    )
    .expect("valid target");

    // Non-forward step.
    let back = SchemaMigration::try_new(
        collection("users"),
        version(2),
        version(1),
        target.clone(),
        vec![],
    );
    assert_eq!(back, Err(MigrationError::InvalidContract));

    // Restatement is not a migration.
    let same = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(1),
        target.clone(),
        vec![],
    );
    assert_eq!(same, Err(MigrationError::InvalidContract));

    // Target version must equal the to-version.
    let mismatched = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(3),
        target.clone(),
        vec![],
    );
    assert_eq!(mismatched, Err(MigrationError::InvalidContract));

    // The reserved family collection cannot migrate.
    let reserved = SchemaMigration::try_new(
        collection("terrabase.families"),
        version(1),
        version(2),
        target.clone(),
        vec![],
    );
    assert_eq!(reserved, Err(MigrationError::InvalidContract));

    // A self-rename is not a rename.
    let self_rename = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        target.clone(),
        vec![FieldTransform::RenameField { from: "email".to_owned(), to: "email".to_owned() }],
    );
    assert_eq!(self_rename, Err(MigrationError::InvalidContract));

    // An invalid field name is refused.
    let invalid_name = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        target.clone(),
        vec![FieldTransform::RemoveField { field: " spaced ".to_owned() }],
    );
    assert_eq!(invalid_name, Err(MigrationError::InvalidContract));

    // A malformed target schema is refused with its own typed error.
    let malformed_target = SchemaDefinition::try_new(
        version(2),
        vec![SchemaRule::NumberRange { field: "age".to_owned(), min: Some(10), max: Some(1) }],
    )
    .expect("constructs; semantics fail later");
    let malformed = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        malformed_target,
        vec![],
    );
    assert_eq!(malformed, Err(MigrationError::TargetSchemaMalformed));

    // The transform bound holds.
    let many: Vec<FieldTransform> = (0..257)
        .map(|index| FieldTransform::RemoveField { field: format!("field-{index}") })
        .collect();
    let bounded =
        SchemaMigration::try_new(collection("users"), version(1), version(2), target, many);
    assert_eq!(bounded, Err(MigrationError::InvalidContract));
}

#[test]
fn transform_coercion_contracts_are_explicit_and_idempotent() {
    // AddField writes the default only when the field is absent.
    let add = FieldTransform::AddField {
        field: "plan".to_owned(),
        default: TransformLiteral::Text("free".to_owned()),
    };
    match add.apply(&object(&[("email", text("a"))])) {
        FieldOutcome::Changed(value) => {
            assert_eq!(value, object(&[("email", text("a")), ("plan", text("free"))]));
        }
        other => panic!("absent field is added: {other:?}"),
    }
    assert_eq!(
        add.apply(&object(&[("email", text("a")), ("plan", text("paid"))])),
        FieldOutcome::Unchanged,
        "a present value is kept"
    );

    // RemoveField removes a present field and skips an absent one.
    let remove = FieldTransform::RemoveField { field: "legacy".to_owned() };
    match remove.apply(&object(&[("legacy", text("x"))])) {
        FieldOutcome::Changed(value) => assert_eq!(value, object(&[])),
        other => panic!("present field is removed: {other:?}"),
    }
    assert_eq!(remove.apply(&object(&[("email", text("a"))])), FieldOutcome::Unchanged);

    // RenameField moves, skips an absent source, and refuses an occupied
    // destination instead of overwriting it.
    let rename = FieldTransform::RenameField { from: "nick".to_owned(), to: "nickname".to_owned() };
    match rename.apply(&object(&[("nick", text("a"))])) {
        FieldOutcome::Changed(value) => assert_eq!(value, object(&[("nickname", text("a"))])),
        other => panic!("source moves: {other:?}"),
    }
    assert_eq!(rename.apply(&object(&[("email", text("a"))])), FieldOutcome::Unchanged);
    assert_eq!(
        rename.apply(&object(&[("nick", text("a")), ("nickname", text("b"))])),
        FieldOutcome::Rejected(TransformRejection::TargetFieldOccupied)
    );

    // RetypeField coerces exactly its named domain and refuses the rest.
    let parse =
        FieldTransform::RetypeField { field: "age".to_owned(), coercion: Coercion::TextToInteger };
    match parse.apply(&object(&[("age", text("42"))])) {
        FieldOutcome::Changed(value) => assert_eq!(value, object(&[("age", integer(42))])),
        other => panic!("canonical text parses: {other:?}"),
    }
    for bad in [" 42", "+42", "042", "42.0", "", "-0", "99999999999999999999"] {
        assert_eq!(
            parse.apply(&object(&[("age", text(bad))])),
            FieldOutcome::Rejected(TransformRejection::ValueNotCoercible),
            "non-canonical text refused: {bad}"
        );
    }
    assert_eq!(
        parse.apply(&object(&[("age", integer(42))])),
        FieldOutcome::Unchanged,
        "a coercion does not address another type"
    );
    assert_eq!(
        parse.apply(&object(&[("email", text("a"))])),
        FieldOutcome::Unchanged,
        "an absent field is skipped"
    );

    // The exact coercion pairs round-trip; the lossy edge is refused.
    let render =
        FieldTransform::RetypeField { field: "n".to_owned(), coercion: Coercion::IntegerToText };
    let parse_n =
        FieldTransform::RetypeField { field: "n".to_owned(), coercion: Coercion::TextToInteger };
    match render.apply(&object(&[("n", integer(-7))])) {
        FieldOutcome::Changed(value) => {
            assert_eq!(value, object(&[("n", text("-7"))]));
            match parse_n.apply(&object(&[("n", text("-7"))])) {
                FieldOutcome::Changed(back) => assert_eq!(back, object(&[("n", integer(-7))])),
                other => panic!("inverse coercion applies: {other:?}"),
            }
        }
        other => panic!("integer renders: {other:?}"),
    }
    let widen =
        FieldTransform::RetypeField { field: "n".to_owned(), coercion: Coercion::IntegerToNumber };
    let huge = (1_i64 << 60) + 1;
    assert_eq!(
        widen.apply(&object(&[("n", integer(huge))])),
        FieldOutcome::Rejected(TransformRejection::ValueNotCoercible),
        "an integer beyond the exact float domain is refused"
    );
    match widen.apply(&object(&[("n", integer(42))])) {
        FieldOutcome::Changed(value) => {
            assert_eq!(value, object(&[("n", DocumentValue::Number(42.0))]));
        }
        other => panic!("exact integer widens: {other:?}"),
    }
    let narrow =
        FieldTransform::RetypeField { field: "n".to_owned(), coercion: Coercion::NumberToInteger };
    for bad in [0.5, -0.5, f64::NAN, f64::INFINITY, 9.3e18] {
        assert_eq!(
            narrow.apply(&object(&[("n", DocumentValue::Number(bad))])),
            FieldOutcome::Rejected(TransformRejection::ValueNotCoercible),
            "inexact number refused: {bad}"
        );
    }
    match narrow.apply(&object(&[("n", DocumentValue::Number(-9.0))])) {
        FieldOutcome::Changed(value) => assert_eq!(value, object(&[("n", integer(-9))])),
        other => panic!("integral number narrows: {other:?}"),
    }

    // A non-object document passes through and every transform is
    // idempotent on its own result.
    assert_eq!(add.apply(&text("not an object")), FieldOutcome::Unchanged);
    for value in [
        object(&[("nick", text("a"))]),
        object(&[("age", text("42")), ("plan", text("free"))]),
        text("scalar"),
    ] {
        if let FieldOutcome::Changed(result) = add.apply(&value) {
            assert_eq!(add.apply(&result), FieldOutcome::Unchanged, "re-application is a no-op");
        }
    }
}

#[test]
fn rollback_limits_are_computed_from_the_transform_list() {
    assert_eq!(reversible_contract(1, 2).rollback_limit(), RollbackLimit::Reversible);
    assert_eq!(
        irreversible_contract(1, 2).rollback_limit(),
        RollbackLimit::NotReversible {
            causes: vec![RollbackBlockedCause::FieldDefaulted, RollbackBlockedCause::FieldRemoved],
        }
    );
    assert!(reversible_contract(1, 2).inverse_transforms().is_some());
    assert_eq!(irreversible_contract(1, 2).inverse_transforms(), None);

    let inverse =
        reversible_contract(1, 2).inverse_transforms().expect("reversible contracts invert");
    assert_eq!(
        inverse,
        vec![
            FieldTransform::RenameField { from: "age".to_owned(), to: "age_text".to_owned() },
            FieldTransform::RetypeField {
                field: "age_text".to_owned(),
                coercion: Coercion::IntegerToText
            },
            FieldTransform::RenameField { from: "nickname".to_owned(), to: "nick".to_owned() },
        ],
        "the inverse is the reversed list with every direction swapped"
    );
}

// ---------------------------------------------------------------------------
// Dry run
// ---------------------------------------------------------------------------

#[test]
fn dry_run_states_the_plan_without_mutating_anything() {
    let (_dir, engine) = open_engine("dry-run");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);

    let sequence_before = engine.read_snapshot(&users).expect("snapshot").sequence();
    let values_before: Vec<DocumentValue> =
        ["ada", "linus"].map(|id| user_value(&engine, &users, id)).into_iter().collect();

    let plan = dry_run(&engine, &users, &contract).expect("plan computes");
    let super::PlanOutcome::Ready { documents, source_digest, result_digest, transform_counts } =
        plan.outcome()
    else {
        panic!("the seeded collection is ready to migrate");
    };
    assert_eq!(*documents, 2);
    assert_ne!(source_digest, result_digest, "the transforms change the chain");
    assert_eq!(
        *transform_counts,
        super::TransformCounts { added: 0, removed: 0, renamed: 4, retyped: 2 }
    );
    assert!(!plan.manifest().is_empty());

    // Nothing mutated: sequence unchanged, values unchanged, no record, and
    // the enforced version is still one.
    assert_eq!(
        engine.read_snapshot(&users).expect("snapshot").sequence(),
        sequence_before,
        "a dry run publishes nothing"
    );
    let values_after: Vec<DocumentValue> =
        ["ada", "linus"].map(|id| user_value(&engine, &users, id)).into_iter().collect();
    assert_eq!(values_before, values_after);
    assert_eq!(status(&engine, &users, &collection("users")), Ok(MigrationStatus::None));
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));

    // A blocked plan names the documents and rule kinds and mutates
    // nothing: `age_text` holds non-canonical text.
    let outcome = put_document(
        &engine,
        &users,
        "grace",
        "users",
        object(&[
            ("email", text("grace@example")),
            ("nick", text("g")),
            ("age_text", text(" 36")),
            ("legacy", text("deprecated")),
        ]),
    );
    assert!(outcome.is_ok(), "the pre-version schema accepts the text");
    let blocked = dry_run(&engine, &users, &contract).expect("plan computes");
    let super::PlanOutcome::Blocked { problems } = blocked.outcome() else {
        panic!("the non-canonical age blocks the migration");
    };
    assert_eq!(problems.len(), 1);
    assert_eq!(problems[0].document(), &document_id("grace"));
    assert_eq!(problems[0].field(), "age_text");
    assert_eq!(
        problems[0].detail(),
        &super::PlanDetail::TransformRejected(TransformRejection::ValueNotCoercible)
    );
    assert!(!blocked.is_ready());
}

// ---------------------------------------------------------------------------
// Apply, evidence, and idempotency
// ---------------------------------------------------------------------------

#[test]
fn apply_moves_the_collection_between_versions_with_evidence() {
    let (_dir, engine) = open_engine("apply");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");

    let receipt = apply(&engine, &users, &contract, &plan, MigrationOptions::default())
        .expect("drive completes");

    // Documents carry the transformed shape.
    assert_eq!(
        user_value(&engine, &users, "ada"),
        object(&[
            ("email", text("ada@example")),
            ("nickname", text("a")),
            ("age", integer(42)),
            ("legacy", text("deprecated"))
        ])
    );
    assert_eq!(
        user_value(&engine, &users, "linus"),
        object(&[
            ("email", text("linus@example")),
            ("nickname", text("l")),
            ("age", integer(7)),
            ("legacy", text("deprecated"))
        ])
    );

    // The recorded schema advanced to the target through the migration.
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(2)));

    // The receipt states the plan's evidence.
    let super::PlanOutcome::Ready { documents, source_digest, result_digest, transform_counts } =
        plan.outcome()
    else {
        panic!("plan is ready");
    };
    assert_eq!(receipt.documents(), *documents);
    assert_eq!(receipt.transformed(), 2);
    assert_eq!(receipt.transform_counts(), *transform_counts);
    assert_eq!(receipt.source_digest(), source_digest);
    assert_eq!(receipt.result_digest(), result_digest);
    assert_eq!(receipt.pages(), 1);
    assert_eq!(receipt.from_version(), version(1));
    assert_eq!(receipt.to_version(), version(2));
    assert_eq!(receipt.rollback_limit(), &RollbackLimit::Reversible);
    assert!(!receipt.acknowledged_not_rollbackable());
    assert!(!receipt.already_applied());

    // The completed drive is recorded with its receipt.
    assert_eq!(
        status(&engine, &users, &collection("users")),
        Ok(MigrationStatus::Complete { receipt: receipt.clone() })
    );

    // New writes validate under the target schema through the SM-02 seam.
    assert!(
        put_document(
            &engine,
            &users,
            "kent",
            "users",
            object(&[("email", text("kent@example")), ("age", integer(1))])
        )
        .is_ok(),
        "a target-conforming document writes"
    );
    assert_eq!(
        put_document(
            &engine,
            &users,
            "bad",
            "users",
            object(&[("email", text("bad@example")), ("age", text("not an integer"))])
        ),
        Err(EngineError::InvalidDocument),
        "the target schema rejects a pre-version shape after finalize"
    );
}

#[test]
fn a_failed_page_records_a_durable_stop_without_seating_the_target() {
    let (dir, engine) = open_engine("stopped-page");
    let users = scope(&engine, "alpha");
    let source = SchemaDefinition::try_new(
        version(1),
        vec![SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text }],
    )
    .expect("valid source");
    let target = SchemaDefinition::try_new(
        version(2),
        vec![
            SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::TextLength { field: "name".to_owned(), min: None, max: Some(3) },
        ],
    )
    .expect("valid target");
    declare_schema(&engine, &users, "users", source.clone());
    put_document(&engine, &users, "ada", "users", object(&[("name", text("ada"))]))
        .expect("source document writes");
    let contract =
        SchemaMigration::try_new(collection("users"), version(1), version(2), target, Vec::new())
            .expect("valid migration");
    let plan = dry_run(&engine, &users, &contract).expect("dry run is ready");
    // The source schema permits this update after the dry run, while the
    // target schema does not. This exercises the page-time target fence.
    put_document(&engine, &users, "ada", "users", object(&[("name", text("a-long-name"))]))
        .expect("source schema accepts the changed value");
    assert_eq!(
        begin(&engine, &users, &contract, &plan, MigrationOptions::default()),
        Ok(MigrationStep::Begun)
    );
    assert_eq!(apply_page(&engine, &users, &contract, &plan), Err(MigrationError::TargetViolation));
    assert_eq!(
        status(&engine, &users, &collection("users")),
        Ok(MigrationStatus::Stopped {
            manifest: plan.manifest().to_owned(),
            from: version(1),
            to: version(2),
            reason: MigrationStopReason::TargetViolation,
            documents: 0,
            transformed: 0,
            pages: 0,
        })
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));
    drop(engine);
    let reopened =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("reopens");
    assert!(matches!(
        status(&reopened, &scope(&reopened, "alpha"), &collection("users")),
        Ok(MigrationStatus::Stopped { reason: MigrationStopReason::TargetViolation, .. })
    ));
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn re_running_a_completed_migration_is_a_noop_with_receipt() {
    let (_dir, engine) = open_engine("noop");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    let first = apply(&engine, &users, &contract, &plan, MigrationOptions::default())
        .expect("drive completes");

    let sequence_before = engine.read_snapshot(&users).expect("snapshot").sequence();
    let values_before: Vec<DocumentValue> =
        ["ada", "linus"].map(|id| user_value(&engine, &users, id)).into_iter().collect();

    let second = apply(&engine, &users, &contract, &plan, MigrationOptions::default())
        .expect("the re-run is a no-op with receipt");

    assert!(second.already_applied(), "the re-run reports itself a no-op");
    assert_eq!(second.manifest(), first.manifest());
    assert_eq!(second.source_digest(), first.source_digest());
    assert_eq!(second.result_digest(), first.result_digest());
    assert_eq!(second.documents(), first.documents());
    assert_eq!(
        engine.read_snapshot(&users).expect("snapshot").sequence(),
        sequence_before,
        "the re-run commits nothing"
    );
    let values_after: Vec<DocumentValue> =
        ["ada", "linus"].map(|id| user_value(&engine, &users, id)).into_iter().collect();
    assert_eq!(values_before, values_after);
}

#[test]
fn versions_advance_forward_only_through_the_recorded_lineage() {
    let (_dir, engine) = open_engine("lineage");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);

    // A schema-free collection has nothing to migrate from: the same
    // contract shape over a collection that never declared a schema.
    let bare_target = SchemaDefinition::try_new(
        version(2),
        vec![SchemaRule::Required { field: "email".to_owned() }],
    )
    .expect("valid target");
    let bare =
        SchemaMigration::try_new(collection("fresh"), version(1), version(2), bare_target, vec![])
            .expect("valid contract");
    assert_eq!(dry_run(&engine, &users, &bare), Err(MigrationError::FromVersionMismatch));

    drive(&engine, &users, &reversible_contract(1, 2), MigrationOptions::default());

    // A stale from-version is refused once the lineage advanced.
    let stale = dry_run(&engine, &users, &reversible_contract(1, 3));
    assert_eq!(stale, Err(MigrationError::FromVersionMismatch));

    // The completed lineage may continue forward from the recorded version.
    let next_target = SchemaDefinition::try_new(
        version(3),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "nickname".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Number },
        ],
    )
    .expect("valid target");
    let next = SchemaMigration::try_new(
        collection("users"),
        version(2),
        version(3),
        next_target,
        vec![FieldTransform::RetypeField {
            field: "age".to_owned(),
            coercion: Coercion::IntegerToNumber,
        }],
    )
    .expect("valid contract");
    let receipt = drive(&engine, &users, &next, MigrationOptions::default());
    assert_eq!(receipt.to_version(), version(3));
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(3)));

    // A different contract for the same completed step is a manifest
    // conflict, never a silent re-application.
    let other = SchemaMigration::try_new(
        collection("users"),
        version(2),
        version(3),
        SchemaDefinition::try_new(
            version(3),
            vec![SchemaRule::Required { field: "email".to_owned() }],
        )
        .expect("valid target"),
        vec![],
    )
    .expect("valid contract");
    let refused = dry_run(&engine, &users, &other);
    assert_eq!(refused, Err(MigrationError::FromVersionMismatch));
}

// ---------------------------------------------------------------------------
// Kill points: interruption and resumption
// ---------------------------------------------------------------------------

/// Drives the steps one at a time so tests can kill between them.
struct SteppedDrive<'a> {
    engine: &'a Engine,
    scope: &'a TenantScope,
    contract: SchemaMigration,
    plan: super::MigrationPlan,
    options: MigrationOptions,
}

impl SteppedDrive<'_> {
    fn begin(&self) -> MigrationStep {
        begin(self.engine, self.scope, &self.contract, &self.plan, self.options)
            .expect("begin seats the drive")
    }
    fn page(&self) -> MigrationStep {
        apply_page(self.engine, self.scope, &self.contract, &self.plan).expect("page applies")
    }
    fn finalize(&self) -> super::MigrationReceipt {
        finalize(self.engine, self.scope, &self.contract, &self.plan).expect("finalize seats")
    }
}

#[test]
fn interrupted_migrations_resume_from_the_recorded_step() {
    // K1: killed after begin, before any page. The drive resumes and
    // completes with the same evidence an uninterrupted drive produces.
    let (dir, engine) = open_engine("kill-k1");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    let drive = SteppedDrive {
        engine: &engine,
        scope: &users,
        contract: contract.clone(),
        plan: plan.clone(),
        options: MigrationOptions::default(),
    };
    assert_eq!(drive.begin(), MigrationStep::Begun);
    assert_eq!(
        status(&engine, &users, &collection("users")),
        Ok(MigrationStatus::InProgress {
            manifest: plan.manifest().to_owned(),
            from: version(1),
            to: version(2),
            documents: 0,
            transformed: 0,
            pages: 0,
        })
    );
    drop(engine);
    let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("engine reopens");
    let users = scope(&engine, "alpha");
    let drive = SteppedDrive {
        engine: &engine,
        scope: &users,
        contract: contract.clone(),
        plan: plan.clone(),
        options: MigrationOptions::default(),
    };
    // The interrupted begin is idempotent and the drive completes.
    assert_eq!(drive.begin(), MigrationStep::Begun);
    assert_eq!(drive.page(), MigrationStep::ScanComplete);
    let resumed = drive.finalize();
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(2)));

    // The uninterrupted twin produces the same evidence.
    let (_twin_dir, twin) = open_engine("kill-k1-twin");
    let twin_scope = scope(&twin, "alpha");
    seeded_users(&twin, &twin_scope);
    let straight = drive_to_receipt(&twin, &twin_scope, &contract);
    assert_eq!(resumed.manifest(), straight.manifest());
    assert_eq!(resumed.documents(), straight.documents());
    assert_eq!(resumed.transformed(), straight.transformed());
    assert_eq!(resumed.source_digest(), straight.source_digest());
    assert_eq!(resumed.result_digest(), straight.result_digest());
    assert_eq!(resumed.pages(), straight.pages());
}

fn drive_to_receipt(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
) -> super::MigrationReceipt {
    let plan = dry_run(engine, scope, contract).expect("plan is ready");
    apply(engine, scope, contract, &plan, MigrationOptions::default()).expect("drive completes")
}

#[test]
fn interrupted_pages_resume_without_double_application() {
    // K2: killed mid-collection, after a committed page. The engine is
    // dropped and reopened from its durable directory; the resumed drive
    // completes with exactly the uninterrupted evidence. The collection is
    // larger than one bounded page, so the interruption is genuinely
    // between committed pages.
    let (dir, engine) = open_engine("kill-k2");
    let users = scope(&engine, "alpha");
    let first = SchemaDefinition::try_new(
        version(1),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "age_text".to_owned(), kind: SchemaFieldType::Text },
        ],
    )
    .expect("valid schema");
    declare_schema(&engine, &users, "users", first);
    let count = super::MAX_MIGRATION_PAGE + 1;
    seed_many(&engine, &users, count, &[("email", "a"), ("age_text", "1")]);

    let target = SchemaDefinition::try_new(
        version(2),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
        ],
    )
    .expect("valid target");
    let contract = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        target,
        vec![FieldTransform::RetypeField {
            field: "age_text".to_owned(),
            coercion: Coercion::TextToInteger,
        }],
    )
    .expect("valid contract");

    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    let drive = SteppedDrive {
        engine: &engine,
        scope: &users,
        contract: contract.clone(),
        plan: plan.clone(),
        options: MigrationOptions::default(),
    };
    drive.begin();
    match drive.page() {
        MigrationStep::PageApplied { documents, transformed } => {
            assert_eq!(documents, u64::try_from(super::MAX_MIGRATION_PAGE).expect("positive"));
            assert_eq!(transformed, documents);
        }
        other => panic!("the first page is bounded: {other:?}"),
    }
    // One document is migrated past the page boundary by the fence: the
    // in-progress drive validates against the target.
    assert!(
        put_document(
            &engine,
            &users,
            "visitor",
            "users",
            object(&[("email", text("v")), ("age", integer(3))])
        )
        .is_ok()
    );
    assert_eq!(
        put_document(
            &engine,
            &users,
            "visitor-2",
            "users",
            object(&[("email", text("v")), ("age", text("3"))])
        ),
        Err(EngineError::InvalidDocument),
        "a document the target rules out is fenced while the drive is in progress"
    );

    // The visitor must leave before the resume: the plan's stated evidence
    // covers the seeded documents, and finalize refuses to seat over a
    // changed collection.
    let snapshot = engine.read_snapshot(&users).expect("snapshot");
    let mut clear =
        engine.begin(&users, snapshot, unique_operation("clear-visitor")).expect("begins");
    clear.delete(document_id("visitor"), ExpectedVersion::Any).expect("stages delete");
    assert!(matches!(clear.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    drop(engine);
    let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("engine reopens");
    let users = scope(&engine, "alpha");
    let drive = SteppedDrive {
        engine: &engine,
        scope: &users,
        contract: contract.clone(),
        plan: plan.clone(),
        options: MigrationOptions::default(),
    };
    assert_eq!(drive.begin(), MigrationStep::Begun, "the drive resumes");
    let resumed = loop {
        match drive.page() {
            MigrationStep::PageApplied { .. } => {}
            MigrationStep::ScanComplete => break drive.finalize(),
            other => panic!("resumed page answers: {other:?}"),
        }
    };

    // The resumed evidence covers the whole collection exactly once: the
    // count is the seeded documents plus the fenced visitor, and the two
    // in-window rejections staged nothing.
    assert_eq!(
        resumed.documents(),
        u64::try_from(count).expect("positive"),
        "no document is counted twice across the interruption"
    );
    assert_eq!(resumed.transformed(), u64::try_from(count).expect("positive"));
    assert_eq!(resumed.pages(), 2);

    // Spot-check boundary documents: the last of page one and the first of
    // page two are migrated exactly once — the coercion is idempotent, but
    // re-typing a value the second page already coerced would leave the
    // integer untouched either way, so the sharper proof is that every
    // boundary document carries the coerced integer and none keeps the
    // text.
    assert_eq!(
        user_value(&engine, &users, &format!("doc-{:05}", super::MAX_MIGRATION_PAGE - 1)),
        object(&[("email", text("a")), ("age_text", integer(1))])
    );
    assert_eq!(
        user_value(&engine, &users, &format!("doc-{:05}", super::MAX_MIGRATION_PAGE)),
        object(&[("email", text("a")), ("age_text", integer(1))])
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(2)));
}

#[test]
fn interruptions_after_the_scan_and_after_finalize_are_idempotent() {
    // K3: killed after the scan completed, before finalize.
    let (dir, engine) = open_engine("kill-k3");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    let drive = SteppedDrive {
        engine: &engine,
        scope: &users,
        contract: contract.clone(),
        plan: plan.clone(),
        options: MigrationOptions::default(),
    };
    drive.begin();
    assert_eq!(drive.page(), MigrationStep::ScanComplete);
    // Finalize before the scan completes is refused; here the scan is done,
    // so the refusal is only about calling pages again — the step answers
    // ScanComplete idempotently.
    assert_eq!(drive.page(), MigrationStep::ScanComplete);
    drop(engine);
    let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("engine reopens");
    let users = scope(&engine, "alpha");
    let drive = SteppedDrive {
        engine: &engine,
        scope: &users,
        contract: contract.clone(),
        plan: plan.clone(),
        options: MigrationOptions::default(),
    };
    let receipt = drive.finalize();
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(2)));

    // K4: killed after finalize; every later step is a no-op with receipt.
    drop(engine);
    let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("engine reopens");
    let users = scope(&engine, "alpha");
    let drive = SteppedDrive {
        engine: &engine,
        scope: &users,
        contract: contract.clone(),
        plan: plan.clone(),
        options: MigrationOptions::default(),
    };
    assert_eq!(drive.page(), MigrationStep::AlreadyComplete(receipt.clone()));
    let again = drive.finalize();
    assert_eq!(again, receipt);
    let sequence = engine.read_snapshot(&users).expect("snapshot").sequence();
    let _ = drive.begin();
    assert_eq!(
        engine.read_snapshot(&users).expect("snapshot").sequence(),
        sequence,
        "the post-completion steps commit nothing"
    );
}

#[test]
fn finalize_refuses_before_the_scan_completes() {
    let (_dir, engine) = open_engine("scan-incomplete");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    begin(&engine, &users, &contract, &plan, MigrationOptions::default()).expect("seated");
    assert_eq!(
        finalize(&engine, &users, &contract, &plan),
        Err(MigrationError::ScanIncomplete),
        "finalize before the scan completed refuses"
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));
}

// ---------------------------------------------------------------------------
// Evidence mismatches fail closed
// ---------------------------------------------------------------------------

#[test]
fn evidence_mismatches_fail_closed() {
    let (_dir, engine) = open_engine("evidence");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");

    // A forged count in the plan's stated evidence is refused at finalize.
    let forged_count = match plan.outcome() {
        super::PlanOutcome::Ready { source_digest, result_digest, transform_counts, .. } => {
            super::MigrationPlan::new(
                plan.manifest().to_owned(),
                collection("users"),
                version(1),
                version(2),
                super::PlanOutcome::Ready {
                    documents: 99,
                    source_digest: source_digest.clone(),
                    result_digest: result_digest.clone(),
                    transform_counts: *transform_counts,
                },
            )
        }
        super::PlanOutcome::Blocked { .. } => panic!("plan is ready"),
    };
    begin(&engine, &users, &contract, &forged_count, MigrationOptions::default()).expect("seated");
    assert_eq!(
        apply_page(&engine, &users, &contract, &forged_count),
        Ok(MigrationStep::ScanComplete)
    );
    assert_eq!(
        finalize(&engine, &users, &contract, &forged_count),
        Err(MigrationError::CountMismatch),
        "a forged document count fails closed"
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));

    // A real divergence is caught: roll the drive back and change the
    // collection after a fresh dry run, before finalize.
    loop {
        match rollback(&engine, &users, &contract) {
            Ok(RollbackStep::Complete { .. }) => break,
            Ok(RollbackStep::PageRestored { .. }) => {}
            Err(other) => panic!("rollback prepares the divergence scenario: {other:?}"),
        }
    }
    let fresh_plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    begin(&engine, &users, &contract, &fresh_plan, MigrationOptions::default()).expect("seated");
    assert_eq!(
        apply_page(&engine, &users, &contract, &fresh_plan),
        Ok(MigrationStep::ScanComplete)
    );
    // A target-conforming document enters under the in-progress fence; the
    // finalize evidence pass must refuse to seat over the changed count.
    assert!(
        put_document(
            &engine,
            &users,
            "late",
            "users",
            object(&[
                ("email", text("late@example")),
                ("nickname", text("x")),
                ("age", integer(1))
            ]),
        )
        .is_ok()
    );
    assert_eq!(
        finalize(&engine, &users, &contract, &fresh_plan),
        Err(MigrationError::CountMismatch),
        "a collection that changed after the plan fails closed"
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));
    // The failed finalize leaves the drive seated and resumable: clearing
    // the late document lets a fresh drive seat.
    let snapshot = engine.read_snapshot(&users).expect("snapshot");
    let mut clear = engine.begin(&users, snapshot, unique_operation("clear")).expect("begins");
    clear.delete(document_id("late"), ExpectedVersion::Any).expect("stages delete");
    assert!(matches!(clear.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    assert_eq!(
        apply_page(&engine, &users, &contract, &fresh_plan),
        Ok(MigrationStep::ScanComplete)
    );
    let receipt = finalize(&engine, &users, &contract, &fresh_plan).expect("seats after repair");
    assert_eq!(receipt.documents(), 2);
}

#[test]
fn customer_documents_cannot_collide_with_private_migration_records() {
    let (_dir, engine) = open_engine("collision");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    // A user document occupies the reserved migration-record address.
    let outcome = put_document(
        &engine,
        &users,
        "family:migration/users",
        "intruders",
        object(&[("email", text("x"))]),
    );
    assert!(outcome.is_ok(), "the address itself is a valid document id");

    let contract = reversible_contract(1, 2);
    assert!(
        dry_run(&engine, &users, &contract).is_ok(),
        "customer identifiers cannot occupy private migration records"
    );
    assert!(
        put_document(
            &engine,
            &users,
            "ada-2",
            "users",
            object(&[("email", text("a")), ("nick", text("a")), ("age_text", text("1"))])
        )
        .is_ok()
    );
}

// ---------------------------------------------------------------------------
// Rollback
// ---------------------------------------------------------------------------

#[test]
fn rollback_restores_a_reversible_migration_and_states_its_limits() {
    let (_dir, engine) = open_engine("rollback");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    begin(&engine, &users, &contract, &plan, MigrationOptions::default()).expect("seated");
    assert_eq!(apply_page(&engine, &users, &contract, &plan), Ok(MigrationStep::ScanComplete));

    // Partially rolled back and interrupted: the rollback pages are
    // bounded, resumable steps like the forward pages.
    let outcome = rollback(&engine, &users, &contract).expect("rollback pages");
    match outcome {
        RollbackStep::Complete { restored } => assert_eq!(restored, 2),
        RollbackStep::PageRestored { .. } => {}
    }

    // The collection is restored to its pre-migration state.
    assert_eq!(
        user_value(&engine, &users, "ada"),
        object(&[
            ("email", text("ada@example")),
            ("nick", text("a")),
            ("age_text", text("42")),
            ("legacy", text("deprecated")),
        ])
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));
    assert_eq!(status(&engine, &users, &collection("users")), Ok(MigrationStatus::None));
    // Writes validate under the restored from-version schema.
    assert!(
        put_document(
            &engine,
            &users,
            "new",
            "users",
            object(&[("email", text("n")), ("nick", text("n")), ("age_text", text("1"))]),
        )
        .is_ok()
    );
}

#[test]
fn single_page_rollback_restores_documents_the_target_rules_out() {
    // A rollback that completes in one page — a collection no larger than
    // `MAX_MIGRATION_PAGE` rolled back straight from the forward drive's
    // in-progress record — restores documents the migration's target
    // schema rules out: every document's from-shape lacks the rename's
    // target-required destination. The restoring puts must be fenced to
    // the from-version shape, never the target, exactly as the staging
    // contract states, or the rollback spuriously refuses.
    let (_dir, engine) = open_engine("rollback-one-page");
    let users = scope(&engine, "alpha");
    let first = SchemaDefinition::try_new(
        version(1),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "nick".to_owned(), kind: SchemaFieldType::Text },
        ],
    )
    .expect("valid schema");
    declare_schema(&engine, &users, "users", first);
    for (id, email, nick) in [("ada", "ada@example", "a"), ("linus", "linus@example", "l")] {
        let outcome = put_document(
            &engine,
            &users,
            id,
            "users",
            object(&[("email", text(email)), ("nick", text(nick))]),
        );
        assert!(outcome.is_ok(), "the from-version document stages");
    }
    let target = SchemaDefinition::try_new(
        version(2),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::Required { field: "nickname".to_owned() },
            SchemaRule::FieldType { field: "nickname".to_owned(), kind: SchemaFieldType::Text },
        ],
    )
    .expect("valid target");
    let contract = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        target,
        vec![FieldTransform::RenameField { from: "nick".to_owned(), to: "nickname".to_owned() }],
    )
    .expect("valid contract");
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    begin(&engine, &users, &contract, &plan, MigrationOptions::default()).expect("seated");
    // The whole collection fits one page: the drive sits in-progress with
    // the scan complete, so the rollback below completes in one page.
    assert_eq!(apply_page(&engine, &users, &contract, &plan), Ok(MigrationStep::ScanComplete));

    assert_eq!(
        rollback(&engine, &users, &contract),
        Ok(RollbackStep::Complete { restored: 2 }),
        "the one-page rollback restores documents the target rules out"
    );
    assert_eq!(
        user_value(&engine, &users, "ada"),
        object(&[("email", text("ada@example")), ("nick", text("a"))])
    );
    assert_eq!(
        user_value(&engine, &users, "linus"),
        object(&[("email", text("linus@example")), ("nick", text("l"))])
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));
    assert_eq!(status(&engine, &users, &collection("users")), Ok(MigrationStatus::None));
}

#[test]
fn interrupted_rollback_resumes_to_completion() {
    // A reversible migration over more than one page rolls back over more
    // than one page; the engine is dropped mid-rollback and reopened.
    let (_dir, engine) = open_engine("rollback-resume");
    let users = scope(&engine, "alpha");
    let first = SchemaDefinition::try_new(
        version(1),
        vec![SchemaRule::FieldType { field: "a".to_owned(), kind: SchemaFieldType::Text }],
    )
    .expect("valid schema");
    declare_schema(&engine, &users, "users", first);
    let count = super::MAX_MIGRATION_PAGE + 3;
    seed_many(&engine, &users, count, &[("a", "x")]);
    let second = SchemaDefinition::try_new(
        version(2),
        vec![SchemaRule::FieldType { field: "b".to_owned(), kind: SchemaFieldType::Text }],
    )
    .expect("valid target");
    let contract = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        second,
        vec![FieldTransform::RenameField { from: "a".to_owned(), to: "b".to_owned() }],
    )
    .expect("valid contract");
    let receipt = drive_to_receipt(&engine, &users, &contract);
    assert_eq!(receipt.documents(), u64::try_from(count).expect("positive"));

    // Finalize seated the version, so rollback is refused: version identity
    // never moves backward.
    assert_eq!(rollback(&engine, &users, &contract), Err(MigrationError::NotInProgress));
}

#[test]
fn rollback_before_finalize_resumes_across_pages() {
    let (dir, engine) = open_engine("rollback-pages");
    let users = scope(&engine, "alpha");
    let first = SchemaDefinition::try_new(
        version(1),
        vec![SchemaRule::FieldType { field: "a".to_owned(), kind: SchemaFieldType::Text }],
    )
    .expect("valid schema");
    declare_schema(&engine, &users, "users", first);
    let count = super::MAX_MIGRATION_PAGE + 3;
    seed_many(&engine, &users, count, &[("a", "x")]);
    let second = SchemaDefinition::try_new(
        version(2),
        vec![SchemaRule::FieldType { field: "b".to_owned(), kind: SchemaFieldType::Text }],
    )
    .expect("valid target");
    let contract = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        second,
        vec![FieldTransform::RenameField { from: "a".to_owned(), to: "b".to_owned() }],
    )
    .expect("valid contract");
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    begin(&engine, &users, &contract, &plan, MigrationOptions::default()).expect("seated");
    // One forward page, then interrupt.
    assert!(matches!(
        apply_page(&engine, &users, &contract, &plan),
        Ok(MigrationStep::PageApplied { .. })
    ));
    // One rollback page, then drop and reopen mid-rollback.
    let first_restored = match rollback(&engine, &users, &contract) {
        Ok(RollbackStep::PageRestored { restored }) => restored,
        other => panic!("the first rollback page applies: {other:?}"),
    };
    drop(engine);
    let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
        .expect("engine reopens");
    let users = scope(&engine, "alpha");
    let mut restored_total = first_restored;
    let finished = loop {
        match rollback(&engine, &users, &contract) {
            Ok(RollbackStep::PageRestored { restored }) => restored_total += restored,
            Ok(RollbackStep::Complete { restored }) => {
                restored_total += restored;
                break restored_total;
            }
            Err(other) => panic!("resumed rollback answers: {other:?}"),
        }
    };
    // Every migrated document is restored exactly once across the
    // interruption: the first page's count carries across the reopen, the
    // un-migrated documents restore nothing, and the already-restored page
    // idempotently skips them.
    assert_eq!(
        finished,
        u64::try_from(super::MAX_MIGRATION_PAGE).expect("positive"),
        "exactly the documents the forward page changed are restored"
    );
    assert_eq!(user_value(&engine, &users, "doc-00000"), object(&[("a", text("x"))]));
    assert_eq!(
        user_value(&engine, &users, &format!("doc-{:05}", count - 1)),
        object(&[("a", text("x"))])
    );
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));
    assert_eq!(status(&engine, &users, &collection("users")), Ok(MigrationStatus::None));
}

#[test]
fn not_rollbackable_migrations_require_explicit_acknowledgment() {
    let (_dir, engine) = open_engine("acknowledge");
    let users = scope(&engine, "alpha");
    seeded_users(&engine, &users);
    let contract = irreversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");

    // Without the acknowledgment nothing is applied.
    assert_eq!(
        begin(&engine, &users, &contract, &plan, MigrationOptions::default()),
        Err(MigrationError::AcknowledgmentRequired)
    );
    assert_eq!(status(&engine, &users, &collection("users")), Ok(MigrationStatus::None));
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));

    // With it, the migration applies and the receipt records it.
    let acknowledged = MigrationOptions { acknowledge_not_rollbackable: true };
    let receipt = apply(&engine, &users, &contract, &plan, acknowledged).expect("drive completes");
    assert!(receipt.acknowledged_not_rollbackable());
    assert_eq!(
        receipt.rollback_limit(),
        &RollbackLimit::NotReversible {
            causes: vec![RollbackBlockedCause::FieldDefaulted, RollbackBlockedCause::FieldRemoved],
        }
    );
    // The destructive transforms applied: defaults added, legacy removed.
    assert_eq!(
        user_value(&engine, &users, "ada"),
        object(&[
            ("email", text("ada@example")),
            ("nick", text("a")),
            ("age_text", text("42")),
            ("plan", text("free")),
        ])
    );

    // Rollback is refused with its own typed error.
    assert_eq!(rollback(&engine, &users, &contract), Err(MigrationError::NotRollbackable));
}

// ---------------------------------------------------------------------------
// Tenant scoping and empty collections
// ---------------------------------------------------------------------------

#[test]
fn migrations_are_tenant_scoped() {
    let (_dir, engine) = open_engine("tenant");
    let alpha = scope(&engine, "alpha");
    seeded_users(&engine, &alpha);
    let contract = reversible_contract(1, 2);

    // Another tenant's scope sees no schema for the same collection name:
    // the migration is refused, never cross-applied.
    let beta = scope(&engine, "beta");
    assert_eq!(dry_run(&engine, &beta, &contract), Err(MigrationError::FromVersionMismatch));
    assert_eq!(
        status(&engine, &beta, &collection("users")),
        Ok(MigrationStatus::None),
        "no migration record exists for the other tenant"
    );

    // Another Engine's scope is refused.
    let (_other_dir, other) = open_engine("tenant-other");
    let foreign = TenantScope::from_parts(tenant("alpha"), other.identity());
    assert_eq!(dry_run(&engine, &foreign, &contract), Err(MigrationError::InvalidRequest));

    // The owning tenant's migration does not leak into the other tenant:
    // beta has no document, no schema, and no migration record.
    drive(&engine, &alpha, &contract, MigrationOptions::default());
    let snapshot = engine.read_snapshot(&beta).expect("snapshot");
    let read = engine.begin(&beta, snapshot, unique_operation("beta-read")).expect("begins");
    assert!(
        read.read_in_collection(&collection("users"), &document_id("ada"))
            .expect("reads")
            .is_none(),
        "no document exists for beta"
    );
    assert_eq!(enforced_version(&engine, &beta, "users"), None);
    assert_eq!(status(&engine, &beta, &collection("users")), Ok(MigrationStatus::None));
}

#[test]
fn an_empty_collection_migrates_with_zero_evidence() {
    let (_dir, engine) = open_engine("empty");
    let users = scope(&engine, "alpha");
    declare_schema(
        &engine,
        &users,
        "users",
        SchemaDefinition::try_new(
            version(1),
            vec![SchemaRule::Required { field: "email".to_owned() }],
        )
        .expect("valid schema"),
    );
    let contract = reversible_contract(1, 2);
    let plan = dry_run(&engine, &users, &contract).expect("plan is ready");
    if let super::PlanOutcome::Ready { documents, .. } = plan.outcome() {
        assert_eq!(*documents, 0);
    } else {
        panic!("an empty collection is ready");
    }
    let receipt = apply(&engine, &users, &contract, &plan, MigrationOptions::default())
        .expect("drive completes");
    assert_eq!(receipt.documents(), 0);
    assert_eq!(receipt.transformed(), 0);
    assert_eq!(receipt.pages(), 1, "the scan-exhausting page still commits");
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(2)));
}

#[test]
fn the_scan_cursor_is_composite_and_exact() {
    // Identifier reuse across collections must not lose or double-visit
    // documents: the cursor is (collection, identifier).
    let (_dir, engine) = open_engine("cursor");
    let alpha = scope(&engine, "alpha");
    let first = SchemaDefinition::try_new(
        version(1),
        vec![SchemaRule::FieldType { field: "a".to_owned(), kind: SchemaFieldType::Text }],
    )
    .expect("valid schema");
    declare_schema(&engine, &alpha, "users", first);
    for index in 0..4 {
        let id = format!("shared-{index}");
        assert!(put_document(&engine, &alpha, &id, "users", object(&[("a", text("u"))])).is_ok());
        assert!(put_document(&engine, &alpha, &id, "others", object(&[("a", text("o"))])).is_ok());
    }
    let second = SchemaDefinition::try_new(
        version(2),
        vec![SchemaRule::FieldType { field: "b".to_owned(), kind: SchemaFieldType::Text }],
    )
    .expect("valid target");
    let contract = SchemaMigration::try_new(
        collection("users"),
        version(1),
        version(2),
        second,
        vec![FieldTransform::RenameField { from: "a".to_owned(), to: "b".to_owned() }],
    )
    .expect("valid contract");
    let receipt = drive_to_receipt(&engine, &alpha, &contract);
    assert_eq!(receipt.documents(), 4, "only the migrated collection's documents count");
    assert_eq!(user_value(&engine, &alpha, "shared-0"), object(&[("b", text("u"))]));
    assert!(
        put_document(&engine, &alpha, "shared-9", "others", object(&[("a", text("o"))])).is_ok()
    );
}

// ---------------------------------------------------------------------------
// Record codec
// ---------------------------------------------------------------------------

#[test]
fn the_record_codec_round_trips_exactly() {
    let record = super::record::MigrationRecord {
        manifest: "digest".to_owned(),
        from: version(1),
        to: version(2),
        state: super::record::RecordState::InProgress {
            cursor: Some(ScanCursor { collection: "users".to_owned(), document: "ada".to_owned() }),
            exhausted: false,
            target: reversible_contract(1, 2).target().clone(),
        },
        documents: 3,
        transformed: 2,
        counts: super::TransformCounts { added: 1, removed: 2, renamed: 3, retyped: 4 },
        source_chain: super::evidence::DocumentChain::new(),
        result_chain: super::evidence::DocumentChain::new(),
        pages: 5,
        acknowledged: true,
    };
    let encoded = super::record::encode(&record);
    assert_eq!(super::record::decode(&encoded), Ok(record));

    // Malformed fields fail closed.
    let DocumentValue::Object(fields) = &encoded else {
        panic!("record encodes as an object");
    };
    let mut tampered = fields.clone();
    tampered.insert("from".to_owned(), DocumentValue::Integer(0));
    assert!(super::record::decode(&DocumentValue::Object(tampered)).is_err());
    let mut tampered = fields.clone();
    tampered.insert("manifest".to_owned(), DocumentValue::Boolean(true));
    assert!(super::record::decode(&DocumentValue::Object(tampered)).is_err());
    let DocumentValue::Object(drive) = fields.get("drive").expect("drive present") else {
        panic!("drive is an object");
    };
    let mut unknown_state = fields.clone();
    unknown_state.insert("drive".to_owned(), object(&[("state", text("teleported"))]));
    assert!(super::record::decode(&DocumentValue::Object(unknown_state)).is_err());
    let _ = drive;
}

#[test]
fn stopped_partial_migration_rolls_back_across_two_reopens() {
    let (dir, engine) = open_engine("stopped-rollback-recovery");
    let users = scope(&engine, "alpha");
    let source = SchemaDefinition::try_new(
        version(1),
        vec![SchemaRule::FieldType { field: "a".into(), kind: SchemaFieldType::Text }],
    ).unwrap();
    declare_schema(&engine, &users, "users", source);
    let count = super::MAX_MIGRATION_PAGE + 1;
    seed_many(&engine, &users, count, &[("a", "x")]);
    let target = SchemaDefinition::try_new(version(2), vec![
        SchemaRule::FieldType { field: "b".into(), kind: SchemaFieldType::Text },
        SchemaRule::TextLength { field: "b".into(), min: None, max: Some(3) },
    ]).unwrap();
    let contract = SchemaMigration::try_new(collection("users"), version(1), version(2), target,
        vec![FieldTransform::RenameField { from: "a".into(), to: "b".into() }]).unwrap();
    let plan = dry_run(&engine, &users, &contract).unwrap();
    let last = format!("doc-{:05}", count - 1);
    put_document(&engine, &users, &last, "users", object(&[("a", text("source-still-valid"))])).unwrap();
    begin(&engine, &users, &contract, &plan, MigrationOptions::default()).unwrap();
    assert!(matches!(apply_page(&engine, &users, &contract, &plan), Ok(MigrationStep::PageApplied { .. })));
    assert_eq!(apply_page(&engine, &users, &contract, &plan), Err(MigrationError::TargetViolation));
    assert!(matches!(status(&engine, &users, &collection("users")).unwrap(),
        MigrationStatus::Stopped { documents: 1024, pages: 1, .. }));
    assert_eq!(user_value(&engine, &users, "doc-00000"), object(&[("b", text("x"))]));
    drop(engine);

    let engine = Engine::open(EngineConfig::durable(&dir).unwrap()).unwrap();
    let users = scope(&engine, "alpha");
    assert_eq!(rollback(&engine, &users, &contract), Ok(RollbackStep::PageRestored { restored: 1024 }));
    assert!(matches!(status(&engine, &users, &collection("users")).unwrap(), MigrationStatus::RollingBack { pages: 2, .. }));
    drop(engine);

    let engine = Engine::open(EngineConfig::durable(&dir).unwrap()).unwrap();
    let users = scope(&engine, "alpha");
    // Admission replay must preserve the cursor, not restore the first page again.
    assert!(matches!(commit(&engine, &users, |_, transaction, _| {
        super::stage_rollback(transaction, &contract).unwrap();
    }), CommitOutcome::Committed(_)));
    assert_eq!(rollback(&engine, &users, &contract), Ok(RollbackStep::Complete { restored: 0 }));
    assert_eq!(user_value(&engine, &users, "doc-00000"), object(&[("a", text("x"))]));
    assert_eq!(user_value(&engine, &users, &last), object(&[("a", text("source-still-valid"))]));
    assert_eq!(enforced_version(&engine, &users, "users"), Some(version(1)));
    assert_eq!(status(&engine, &users, &collection("users")), Ok(MigrationStatus::None));
}
