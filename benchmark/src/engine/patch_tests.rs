//! In-crate contract tests for the typed document patch vocabulary
//! (GAP-D07, ticket #60), per `docs/engine/gapd07-patch-language.md`.
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. Every refusal is observed as a typed
//! `PatchError`, and no test inspects raw pages, WAL bytes, or private lock
//! state.

use std::path::PathBuf;

use crate::engine::patch::{
    AddOrPatch, DocumentPatch, MAX_PATCH_OPERATIONS, MAX_PATCH_PATH_SEGMENTS, PatchError,
    PatchOperation, PatchOutcome, PatchPath, PatchPathSegment,
};
use crate::engine::{Engine, EngineConfig, apply_add_or_patch, apply_document_patch};
use crate::operations::RequestValidationError;
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId, TenantScope,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-gapd07-patch-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> Engine {
    let dir = location(tag);
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn tenant_scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), engine.identity())
}

fn object(entries: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        entries.iter().map(|(name, value)| ((*name).to_owned(), value.clone())).collect(),
    )
}

fn array(items: &[DocumentValue]) -> DocumentValue {
    DocumentValue::Array(items.to_vec())
}

fn string(text: &str) -> DocumentValue {
    DocumentValue::String(text.to_owned())
}

fn integer(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

/// The seed document every application test patches: an object with one
/// nested object, one array, and one counter-shaped field.
fn seed_document() -> Document {
    let value = object(&[
        ("profile", object(&[("name", string("ada"))])),
        ("tags", array(&[string("a"), string("b"), string("c")])),
        ("login_count", integer(1)),
    ]);
    Document::new(document_id("users/1"), collection("users"), value)
}

fn field_path(parts: &[&str]) -> PatchPath {
    PatchPath::try_from_segments(
        parts.iter().map(|part| PatchPathSegment::Field((*part).to_owned())).collect(),
    )
    .expect("valid path")
}

/// Commits the seed document at version 1 and returns the engine.
fn seeded_engine(tag: &str) -> Engine {
    let engine = open_engine(tag);
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("seed")).expect("begins");
    transaction.put(seed_document(), ExpectedVersion::New).expect("stages seed");
    let outcome = transaction.commit(LocalDurability::Synced);
    assert!(matches!(outcome, crate::engine::CommitOutcome::Committed(_)), "seed commits");
    engine
}

fn read_value(engine: &Engine, id: &str) -> Option<DocumentValue> {
    let scope = tenant_scope(engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let transaction = engine.begin(&scope, snapshot, operation("read-probe")).expect("begins");
    transaction
        .read_in_collection(&collection("users"), &document_id(id))
        .expect("reads")
        .map(|document| document.value().clone())
}

fn patch(operations: Vec<PatchOperation>) -> DocumentPatch {
    DocumentPatch::try_new(operations).expect("valid patch")
}

// ---------------------------------------------------------------------------
// Construction refusals — before any data access, before any write
// ---------------------------------------------------------------------------

#[test]
fn patch_paths_refuse_unbounded_and_malformed_shapes() {
    assert_eq!(
        PatchPath::try_from_segments(Vec::new()).err(),
        Some(RequestValidationError::EmptyField),
        "the empty path addresses the document root, which no patch operation owns"
    );

    let too_deep =
        std::iter::repeat_n(PatchPathSegment::Field("a".to_owned()), MAX_PATCH_PATH_SEGMENTS + 1)
            .collect();
    assert_eq!(
        PatchPath::try_from_segments(too_deep).err(),
        Some(RequestValidationError::InvalidPatch),
        "depth is bounded like every other closed clause at this seam"
    );

    let append_mid_path = vec![
        PatchPathSegment::Field("tags".to_owned()),
        PatchPathSegment::Append,
        PatchPathSegment::Field("x".to_owned()),
    ];
    assert_eq!(
        PatchPath::try_from_segments(append_mid_path).err(),
        Some(RequestValidationError::InvalidPatch),
        "append is a last-segment array form only"
    );
}

#[test]
fn patch_field_segments_are_bounded_and_control_free() {
    assert_eq!(
        PatchPath::try_from_segments(vec![PatchPathSegment::Field(String::new())]).err(),
        Some(RequestValidationError::EmptyField)
    );
    assert_eq!(
        PatchPath::try_from_segments(vec![PatchPathSegment::Field("a\u{7}b".to_owned())]).err(),
        Some(RequestValidationError::InvalidPatch)
    );
    let oversize = PatchPathSegment::Field("a".repeat(256));
    assert_eq!(
        PatchPath::try_from_segments(vec![oversize]).err(),
        Some(RequestValidationError::InvalidPatch),
        "field segments share the 255-byte identifier bound"
    );
    let boundary = PatchPathSegment::Field("a".repeat(255));
    assert!(PatchPath::try_from_segments(vec![boundary]).is_ok());
}

#[test]
fn document_patch_refuses_empty_and_overlong_operation_lists() {
    assert_eq!(
        DocumentPatch::try_new(Vec::new()).err(),
        Some(RequestValidationError::EmptyField),
        "a patch with no operations is not an operation"
    );

    let overlong = (0..=MAX_PATCH_OPERATIONS)
        .map(|i| PatchOperation::Replace {
            path: field_path(&["login_count"]),
            value: integer(i64::try_from(i).expect("in range")),
        })
        .collect();
    assert_eq!(DocumentPatch::try_new(overlong).err(), Some(RequestValidationError::InvalidPatch));
}

#[test]
fn append_is_only_valid_under_add() {
    let append_path = PatchPath::try_from_segments(vec![
        PatchPathSegment::Field("tags".to_owned()),
        PatchPathSegment::Append,
    ])
    .expect("valid path");
    assert_eq!(
        patch(vec![PatchOperation::Add { path: append_path.clone(), value: string("d") }])
            .operations()
            .len(),
        1
    );
    assert_eq!(
        DocumentPatch::try_new(vec![PatchOperation::Remove { path: append_path }]).err(),
        Some(RequestValidationError::InvalidPatch),
        "removing 'the end of the array' is not a position"
    );
}

// ---------------------------------------------------------------------------
// Application semantics
// ---------------------------------------------------------------------------

#[test]
fn add_creates_overwrites_and_appends() {
    let engine = seeded_engine("add-forms");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    let outcome = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Exact(1),
        &patch(vec![
            // create a member
            PatchOperation::Add {
                path: field_path(&["active"]),
                value: DocumentValue::Boolean(true),
            },
            // overwrite an existing member
            PatchOperation::Replace {
                path: field_path(&["profile", "name"]),
                value: string("grace"),
            },
            // append to an array
            PatchOperation::Add {
                path: PatchPath::try_from_segments(vec![
                    PatchPathSegment::Field("tags".to_owned()),
                    PatchPathSegment::Append,
                ])
                .expect("valid path"),
                value: string("d"),
            },
            // insert in bounds at index 0, shifting later elements
            PatchOperation::Add {
                path: PatchPath::try_from_segments(vec![
                    PatchPathSegment::Field("tags".to_owned()),
                    PatchPathSegment::Index(0),
                ])
                .expect("valid path"),
                value: string("first"),
            },
        ]),
    )
    .expect("applies");
    assert_eq!(outcome, PatchOutcome::Applied);
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let value = read_value(&engine, "users/1").expect("document");
    let DocumentValue::Object(fields) = value else { panic!("object document") };
    assert_eq!(fields.get("active"), Some(&DocumentValue::Boolean(true)));
    assert_eq!(
        fields.get("profile"),
        Some(&object(&[("name", string("grace"))])),
        "replace sets the member in place"
    );
    assert_eq!(
        fields.get("tags"),
        Some(&array(&[string("first"), string("a"), string("b"), string("c"), string("d")])),
        "insert shifts and append lands at the end"
    );
}

#[test]
fn array_index_bounds_are_enforced() {
    let engine = seeded_engine("array-bounds");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    let beyond_end = PatchPath::try_from_segments(vec![
        PatchPathSegment::Field("tags".to_owned()),
        PatchPathSegment::Index(3),
    ])
    .expect("valid path");
    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Add { path: beyond_end, value: string("x") }]),
    )
    .expect_err("index == len is out of bounds for insert (V1 parity)");

    assert_eq!(error, PatchError::PathNotFound);
    let read_scope = tenant_scope(&engine, "t");
    let read_snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
    let probe = engine.begin(&read_scope, read_snapshot, operation("read-probe")).expect("begins");
    assert_eq!(
        probe
            .read_in_collection(&collection("users"), &document_id("users/1"))
            .expect("reads")
            .expect("document")
            .value()
            .clone(),
        seed_document().value().clone(),
        "a refused patch leaves the document exactly as it was"
    );
}

#[test]
fn replace_and_remove_require_an_existing_target() {
    let engine = seeded_engine("strict-targets");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Replace { path: field_path(&["absent"]), value: string("x") }]),
    )
    .expect_err("replace requires the target");
    assert_eq!(error, PatchError::PathNotFound);

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Remove { path: field_path(&["absent"]) }]),
    )
    .expect_err("remove requires the target");
    assert_eq!(error, PatchError::PathNotFound);

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Remove { path: field_path(&["profile", "absent"]) }]),
    )
    .expect_err("nested remove requires the target");
    assert_eq!(error, PatchError::PathNotFound);
}

#[test]
fn remove_deletes_members_and_shifts_array_elements() {
    let engine = seeded_engine("remove-forms");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![
            PatchOperation::Remove { path: field_path(&["profile"]) },
            PatchOperation::Remove {
                path: PatchPath::try_from_segments(vec![
                    PatchPathSegment::Field("tags".to_owned()),
                    PatchPathSegment::Index(1),
                ])
                .expect("valid path"),
            },
        ]),
    )
    .expect("applies");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let DocumentValue::Object(fields) = read_value(&engine, "users/1").expect("document") else {
        panic!("object document")
    };
    assert_eq!(fields.get("profile"), None);
    assert_eq!(fields.get("tags"), Some(&array(&[string("a"), string("c")])), "remove shifts down");
}

#[test]
fn nested_creation_materializes_objects_never_arrays() {
    let engine = seeded_engine("nested-creation");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Add {
            path: field_path(&["settings", "notifications", "email"]),
            value: DocumentValue::Boolean(false),
        }]),
    )
    .expect("applies");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let DocumentValue::Object(fields) = read_value(&engine, "users/1").expect("document") else {
        panic!("object document")
    };
    assert_eq!(
        fields.get("settings"),
        Some(&object(&[("notifications", object(&[("email", DocumentValue::Boolean(false))]))])),
        "add creates missing intermediate objects"
    );

    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-2")).expect("begins");
    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Add {
            path: PatchPath::try_from_segments(vec![
                PatchPathSegment::Field("absent".to_owned()),
                PatchPathSegment::Index(0),
            ])
            .expect("valid path"),
            value: string("x"),
        }]),
    )
    .expect_err("an array has no canonical implicit length, so it is never materialized");
    assert_eq!(error, PatchError::TypeMismatch);

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Add {
            path: PatchPath::try_from_segments(vec![
                PatchPathSegment::Field("login_count".to_owned()),
                PatchPathSegment::Field("x".to_owned()),
            ])
            .expect("valid path"),
            value: string("x"),
        }]),
    )
    .expect_err("a field under a scalar parent is a type mismatch");
    assert_eq!(error, PatchError::TypeMismatch);
}

#[test]
fn increment_adds_sets_and_refuses_non_numbers() {
    let engine = seeded_engine("increment");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![
            // integer target adds, V1's CanAddOrPatchIncrement arithmetic
            PatchOperation::Increment { path: field_path(&["login_count"]), delta: 3 },
            // absent target sets the delta (V1 session increment parity)
            PatchOperation::Increment { path: field_path(&["visits"]), delta: 7 },
            // negative deltas subtract
            PatchOperation::Increment { path: field_path(&["visits"]), delta: -2 },
        ]),
    )
    .expect("applies");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let DocumentValue::Object(fields) = read_value(&engine, "users/1").expect("document") else {
        panic!("object document")
    };
    assert_eq!(fields.get("login_count"), Some(&integer(4)), "1 + 3, the pinned V1 arithmetic");
    assert_eq!(fields.get("visits"), Some(&integer(5)), "7 set, then -2 applied in order");

    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-2")).expect("begins");
    for path in [["profile"], ["tags"]] {
        let error = apply_document_patch(
            &mut transaction,
            &collection("users"),
            &document_id("users/1"),
            ExpectedVersion::Any,
            &patch(vec![PatchOperation::Increment { path: field_path(&path), delta: 1 }]),
        )
        .expect_err("increment on a non-number refuses typed");
        assert_eq!(error, PatchError::TypeMismatch, "target {path:?}");
    }
}

#[test]
fn increment_refuses_float_targets_and_overflow() {
    let engine = open_engine("increment-typed");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("seed")).expect("begins");
    let value = object(&[("f64", DocumentValue::Number(1.5)), ("maxed", integer(i64::MAX))]);
    transaction
        .put(
            Document::new(document_id("users/1"), collection("users"), value),
            ExpectedVersion::New,
        )
        .expect("stages");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");
    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Increment { path: field_path(&["f64"]), delta: 1 }]),
    )
    .expect_err("float increment is a silent precision change, not a typed operation");
    assert_eq!(error, PatchError::TypeMismatch);

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Increment { path: field_path(&["maxed"]), delta: 1 }]),
    )
    .expect_err("wrapping is silent; the typed seam refuses");
    assert_eq!(error, PatchError::ArithmeticOverflow);
}

// ---------------------------------------------------------------------------
// Sequencing and determinism
// ---------------------------------------------------------------------------

#[test]
fn operations_apply_in_order_and_see_earlier_effects() {
    let engine = seeded_engine("ordering");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![
            PatchOperation::Add { path: field_path(&["counter"]), value: integer(5) },
            // the increment sees the member the add just created
            PatchOperation::Increment { path: field_path(&["counter"]), delta: 3 },
        ]),
    )
    .expect("applies");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let DocumentValue::Object(fields) = read_value(&engine, "users/1").expect("document") else {
        panic!("object document")
    };
    assert_eq!(fields.get("counter"), Some(&integer(8)));
}

#[test]
fn the_same_patch_on_the_same_document_is_deterministic() {
    let operations = || {
        patch(vec![
            PatchOperation::Replace { path: field_path(&["profile", "name"]), value: string("x") },
            PatchOperation::Increment { path: field_path(&["login_count"]), delta: 2 },
            PatchOperation::Add {
                path: PatchPath::try_from_segments(vec![
                    PatchPathSegment::Field("tags".to_owned()),
                    PatchPathSegment::Append,
                ])
                .expect("valid path"),
                value: string("z"),
            },
        ])
    };

    let mut results = Vec::new();
    for tag in ["determinism-a", "determinism-b"] {
        let engine = seeded_engine(tag);
        let scope = tenant_scope(&engine, "t");
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");
        let outcome = apply_document_patch(
            &mut transaction,
            &collection("users"),
            &document_id("users/1"),
            ExpectedVersion::Any,
            &operations(),
        )
        .expect("applies");
        assert_eq!(outcome, PatchOutcome::Applied);
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            crate::engine::CommitOutcome::Committed(_)
        ));
        results.push(read_value(&engine, "users/1").expect("document"));
    }
    assert_eq!(results[0], results[1], "one patch value and one document value have one result");
}

#[test]
fn idempotent_operations_reach_a_fixed_point() {
    let engine = seeded_engine("idempotent");
    let replace = || {
        patch(vec![PatchOperation::Replace {
            path: field_path(&["profile", "name"]),
            value: string("same"),
        }])
    };
    let apply_once = |engine: &Engine, operation_name: &str| {
        let scope = tenant_scope(engine, "t");
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction =
            engine.begin(&scope, snapshot, operation(operation_name)).expect("begins");
        let outcome = apply_document_patch(
            &mut transaction,
            &collection("users"),
            &document_id("users/1"),
            ExpectedVersion::Any,
            &replace(),
        )
        .expect("applies");
        (outcome, transaction.commit(LocalDurability::Synced))
    };

    let (first, first_commit) = apply_once(&engine, "patch-1");
    assert_eq!(first, PatchOutcome::Applied);
    assert!(matches!(first_commit, crate::engine::CommitOutcome::Committed(_)));

    let (second, second_commit) = apply_once(&engine, "patch-2");
    assert_eq!(
        second,
        PatchOutcome::NotModified,
        "the second application of an idempotent patch changes nothing"
    );
    assert_eq!(
        second_commit,
        crate::engine::CommitOutcome::Rejected(crate::engine::CommitError::Validation),
        "a not-modified patch stages nothing, so committing it alone is the empty-write-set refusal"
    );
}

#[test]
fn remove_twice_refuses_the_second_application_typed() {
    let engine = seeded_engine("remove-twice");
    let remove = || patch(vec![PatchOperation::Remove { path: field_path(&["tags"]) }]);

    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");
    assert_eq!(
        apply_document_patch(
            &mut transaction,
            &collection("users"),
            &document_id("users/1"),
            ExpectedVersion::Any,
            &remove(),
        )
        .expect("removes"),
        PatchOutcome::Applied
    );
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-2")).expect("begins");
    // `Remove` is idempotent in the value sense — the document stays at the
    // post-remove value — but the fail-closed form of that determinism is a
    // typed refusal on the second application, never a silent no-op.
    assert_eq!(
        apply_document_patch(
            &mut transaction,
            &collection("users"),
            &document_id("users/1"),
            ExpectedVersion::Any,
            &remove(),
        )
        .expect_err("the removed target no longer exists"),
        PatchError::PathNotFound
    );
    assert!(transaction.staged_writes().is_empty());
}

#[test]
fn increment_is_not_idempotent_by_design() {
    let engine = seeded_engine("increment-twice");
    let increment =
        || patch(vec![PatchOperation::Increment { path: field_path(&["login_count"]), delta: 3 }]);

    for (step, expected) in [("patch-1", 4i64), ("patch-2", 7)] {
        let scope = tenant_scope(&engine, "t");
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction = engine.begin(&scope, snapshot, operation(step)).expect("begins");
        let outcome = apply_document_patch(
            &mut transaction,
            &collection("users"),
            &document_id("users/1"),
            ExpectedVersion::Any,
            &increment(),
        )
        .expect("applies");
        assert_eq!(outcome, PatchOutcome::Applied, "every application of a delta mutates");
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            crate::engine::CommitOutcome::Committed(_)
        ));

        let DocumentValue::Object(fields) = read_value(&engine, "users/1").expect("document")
        else {
            panic!("object document")
        };
        assert_eq!(fields.get("login_count"), Some(&integer(expected)));
    }
}

// ---------------------------------------------------------------------------
// Fail-closed discipline
// ---------------------------------------------------------------------------

#[test]
fn a_patch_that_fails_midway_stages_nothing() {
    let engine = seeded_engine("no-partial");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Any,
        &patch(vec![
            // the first operation would succeed on its own
            PatchOperation::Add { path: field_path(&["would_land"]), value: string("x") },
            // the second refuses: string target under increment
            PatchOperation::Increment { path: field_path(&["profile", "name"]), delta: 1 },
        ]),
    )
    .expect_err("the patch refuses as a whole");
    assert_eq!(error, PatchError::TypeMismatch);

    assert!(
        transaction.staged_writes().is_empty(),
        "a refused patch leaves the transaction's write set untouched"
    );
    drop(transaction);
    assert_eq!(
        read_value(&engine, "users/1").expect("document"),
        seed_document().value().clone(),
        "no partial write reached the document"
    );
}

#[test]
fn stale_targets_refuse_typed_before_any_staging() {
    let engine = seeded_engine("stale");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Exact(7),
        &patch(vec![PatchOperation::Remove { path: field_path(&["tags"]) }]),
    )
    .expect_err("the observed version contradicts the expectation");
    assert_eq!(error, PatchError::StaleTarget { observed: 1 });
    assert!(transaction.staged_writes().is_empty());

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::New,
        &patch(vec![PatchOperation::Remove { path: field_path(&["tags"]) }]),
    )
    .expect_err("the document exists, so the create expectation is stale");
    assert_eq!(error, PatchError::StaleTarget { observed: 1 });
    assert!(transaction.staged_writes().is_empty());
}

#[test]
fn a_missing_target_refuses_typed() {
    let engine = seeded_engine("missing");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("patch-1")).expect("begins");

    let error = apply_document_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/9"),
        ExpectedVersion::Any,
        &patch(vec![PatchOperation::Add { path: field_path(&["x"]), value: string("y") }]),
    )
    .expect_err("V1 parity: cannot apply a patch to a document that does not exist");
    assert_eq!(error, PatchError::MissingTarget);
    assert!(transaction.staged_writes().is_empty());
}

#[test]
fn a_staged_patch_rejected_at_commit_leaves_the_document_untouched() {
    let engine = seeded_engine("commit-conflict");
    let scope = tenant_scope(&engine, "t");
    let patcher_snapshot = engine.read_snapshot(&scope).expect("snapshot");

    // Another writer publishes version 2 after the patcher's snapshot.
    {
        let mut other = engine
            .begin(&scope, engine.read_snapshot(&scope).expect("snapshot"), operation("other"))
            .expect("begins");
        let mut updated = seed_document();
        updated.set_value(object(&[("login_count", integer(99))]));
        other.put(updated, ExpectedVersion::Any).expect("stages");
        assert!(matches!(
            other.commit(LocalDurability::Synced),
            crate::engine::CommitOutcome::Committed(_)
        ));
    }

    let mut patcher = engine.begin(&scope, patcher_snapshot, operation("patch-1")).expect("begins");
    apply_document_patch(
        &mut patcher,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Exact(1),
        &patch(vec![PatchOperation::Increment { path: field_path(&["login_count"]), delta: 1 }]),
    )
    .expect("stages against the stale snapshot");
    let outcome = patcher.commit(LocalDurability::Synced);
    assert_eq!(
        outcome,
        crate::engine::CommitOutcome::Rejected(crate::engine::CommitError::Conflict),
        "the commit seam re-checks the expectation against the published view"
    );

    let DocumentValue::Object(fields) = read_value(&engine, "users/1").expect("document") else {
        panic!("object document")
    };
    assert_eq!(
        fields.get("login_count"),
        Some(&integer(99)),
        "the losing patch wrote nothing; the other writer's value stands"
    );
}

// ---------------------------------------------------------------------------
// AddOrPatch
// ---------------------------------------------------------------------------

#[test]
fn add_or_patch_creates_when_missing_and_patches_when_present() {
    let engine = seeded_engine("add-or-patch");

    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("aop-1")).expect("begins");
    let if_missing = Document::new(
        document_id("users/2"),
        collection("users"),
        object(&[("login_count", integer(1))]),
    );
    let outcome = apply_add_or_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/2"),
        ExpectedVersion::Any,
        &AddOrPatch::new(
            if_missing,
            patch(vec![PatchOperation::Increment { path: field_path(&["login_count"]), delta: 3 }]),
        ),
    )
    .expect("applies");
    assert_eq!(outcome, PatchOutcome::Created);
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
    assert_eq!(
        read_value(&engine, "users/2").expect("document"),
        object(&[("login_count", integer(1))]),
        "the create leg stores the if-missing document unchanged"
    );

    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("aop-2")).expect("begins");
    let outcome = apply_add_or_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/2"),
        ExpectedVersion::Any,
        &AddOrPatch::new(
            Document::new(document_id("users/2"), collection("users"), object(&[])),
            patch(vec![PatchOperation::Increment { path: field_path(&["login_count"]), delta: 3 }]),
        ),
    )
    .expect("applies");
    assert_eq!(outcome, PatchOutcome::Applied);
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
    assert_eq!(
        read_value(&engine, "users/2").expect("document"),
        object(&[("login_count", integer(4))]),
        "the patch leg applies to the existing document"
    );
}

#[test]
fn add_or_patch_patch_leg_honors_the_expected_version() {
    let engine = seeded_engine("aop-stale");
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("aop-1")).expect("begins");
    let error = apply_add_or_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/1"),
        ExpectedVersion::Exact(9),
        &AddOrPatch::new(
            Document::new(document_id("users/1"), collection("users"), object(&[])),
            patch(vec![PatchOperation::Remove { path: field_path(&["tags"]) }]),
        ),
    )
    .expect_err("the patch leg carries the caller's expectation");
    assert_eq!(error, PatchError::StaleTarget { observed: 1 });
    assert!(transaction.staged_writes().is_empty());
}

#[test]
fn add_or_patch_refuses_a_create_document_that_retargets_the_write() {
    let engine = seeded_engine("aop-retarget");

    // A compound addressed at users/2 whose create document carries a
    // different identity must refuse instead of writing to an unaddressed
    // target.
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("aop-1")).expect("begins");
    let error = apply_add_or_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/2"),
        ExpectedVersion::Any,
        &AddOrPatch::new(
            Document::new(document_id("users/9"), collection("users"), object(&[])),
            patch(vec![PatchOperation::Increment { path: field_path(&["login_count"]), delta: 1 }]),
        ),
    )
    .expect_err("the create leg may not write to an id other than the addressed one");
    assert_eq!(
        error,
        PatchError::Construction(RequestValidationError::InvalidPatch),
        "a retargeting create document is a malformed compound, refused at application time"
    );
    assert!(transaction.staged_writes().is_empty());

    // The collection leg of the same discipline.
    let scope = tenant_scope(&engine, "t");
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine.begin(&scope, snapshot, operation("aop-2")).expect("begins");
    let error = apply_add_or_patch(
        &mut transaction,
        &collection("users"),
        &document_id("users/2"),
        ExpectedVersion::Any,
        &AddOrPatch::new(
            Document::new(document_id("users/2"), collection("archive"), object(&[])),
            patch(vec![PatchOperation::Increment { path: field_path(&["login_count"]), delta: 1 }]),
        ),
    )
    .expect_err("the create leg may not write to a collection other than the addressed one");
    assert_eq!(error, PatchError::Construction(RequestValidationError::InvalidPatch));
    assert!(transaction.staged_writes().is_empty());

    // Neither refusal staged anything, and the seam's all-or-nothing
    // publication is the only write path, so the published state cannot
    // hold either target of the retargeting compounds.
    assert!(read_value(&engine, "users/2").is_none(), "the addressed target stays absent");
    assert!(read_value(&engine, "users/9").is_none(), "the unaddressed id was never written");
}
