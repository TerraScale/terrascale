//! Public-path tests for the typed document patch vocabulary (GAP-D07,
//! ticket #60), per `docs/engine/gapd07-patch-language.md`.
//!
//! These tests use only public construction: the patch path segments, the
//! operation and patch types, the closed bounds, and the typed outcome and
//! refusal vocabulary. `TenantScope` has no public constructor by design
//! (Platform creates it after authorization), so the commit-path behavior —
//! deterministic application, typed refusals before staging, the
//! no-partial-write proof — is proved by the in-crate engine tests instead
//! (`src/engine/patch_tests.rs`).

use terrabase::engine::{
    AddOrPatch, DocumentPatch, MAX_PATCH_FIELD_BYTES, MAX_PATCH_OPERATIONS,
    MAX_PATCH_PATH_SEGMENTS, PatchError, PatchOperation, PatchOutcome, PatchPath, PatchPathSegment,
};
use terrabase::operations::RequestValidationError;
use terrabase::types::CollectionName;
use terrabase::{Document, DocumentId, DocumentValue, EngineError, ExpectedVersion};

fn collection() -> CollectionName {
    CollectionName::try_new("users").expect("valid identifier")
}

fn document_id() -> DocumentId {
    DocumentId::try_new("users/1").expect("valid identifier")
}

fn path(segments: Vec<PatchPathSegment>) -> PatchPath {
    PatchPath::try_from_segments(segments).expect("valid path")
}

fn field_path(parts: &[&str]) -> PatchPath {
    path(parts.iter().map(|part| PatchPathSegment::Field((*part).to_owned())).collect())
}

#[test]
fn patch_paths_are_typed_segments_with_convenience_constructors() {
    let via_constructor = PatchPathSegment::field("tags").expect("valid field");
    let via_variant = PatchPathSegment::Field("tags".to_owned());
    assert_eq!(via_constructor, via_variant);
    assert_eq!(PatchPathSegment::index(2), PatchPathSegment::Index(2));
    assert_eq!(PatchPathSegment::append(), PatchPathSegment::Append);

    let path = field_path(&["profile", "name"]);
    assert_eq!(
        path.segments(),
        [PatchPathSegment::Field("profile".to_owned()), PatchPathSegment::Field("name".to_owned()),]
    );
}

#[test]
fn patch_paths_refuse_the_root_and_unbounded_shapes() {
    assert_eq!(
        PatchPath::try_from_segments(Vec::new()).err(),
        Some(RequestValidationError::EmptyField),
        "the document root belongs to whole-document operations"
    );
    assert_eq!(
        PatchPath::try_from_segments(vec![PatchPathSegment::Field(String::new())]).err(),
        Some(RequestValidationError::EmptyField)
    );
    assert_eq!(
        PatchPathSegment::field("a\u{1}b").err(),
        Some(RequestValidationError::InvalidPatch)
    );
    assert_eq!(
        PatchPathSegment::field(&"a".repeat(MAX_PATCH_FIELD_BYTES + 1)).err(),
        Some(RequestValidationError::InvalidPatch),
        "field segments share the 255-byte identifier bound"
    );
    assert!(
        PatchPathSegment::field(&"a".repeat(MAX_PATCH_FIELD_BYTES)).is_ok(),
        "the bound itself is accepted"
    );

    let deep: Vec<PatchPathSegment> =
        std::iter::repeat_n(PatchPathSegment::append(), MAX_PATCH_PATH_SEGMENTS + 1).collect();
    let mut valid_depth: Vec<PatchPathSegment> =
        std::iter::repeat_n(PatchPathSegment::Field("a".to_owned()), MAX_PATCH_PATH_SEGMENTS - 1)
            .collect();
    valid_depth.push(PatchPathSegment::Append);
    assert!(
        PatchPath::try_from_segments(valid_depth).is_ok(),
        "the depth bound itself is accepted"
    );
    assert_eq!(
        PatchPath::try_from_segments(deep).err(),
        Some(RequestValidationError::InvalidPatch)
    );
}

#[test]
fn document_patches_are_bounded_and_append_is_add_only() {
    let operations: Vec<PatchOperation> = (0..MAX_PATCH_OPERATIONS as i64)
        .map(|i| PatchOperation::Increment { path: field_path(&["count"]), delta: i })
        .collect();
    let bounded = DocumentPatch::try_new(operations).expect("the bound itself is accepted");
    assert_eq!(bounded.operations().len(), MAX_PATCH_OPERATIONS);

    let overlong: Vec<PatchOperation> = (0..=MAX_PATCH_OPERATIONS as i64)
        .map(|i| PatchOperation::Increment { path: field_path(&["count"]), delta: i })
        .collect();
    assert_eq!(DocumentPatch::try_new(overlong).err(), Some(RequestValidationError::InvalidPatch));
    assert_eq!(DocumentPatch::try_new(Vec::new()).err(), Some(RequestValidationError::EmptyField));

    let append_path =
        path(vec![PatchPathSegment::Field("tags".to_owned()), PatchPathSegment::Append]);
    assert!(
        DocumentPatch::try_new(vec![PatchOperation::Add {
            path: append_path.clone(),
            value: DocumentValue::String("d".to_owned()),
        }])
        .is_ok()
    );
    assert_eq!(
        DocumentPatch::try_new(vec![PatchOperation::Remove { path: append_path }]).err(),
        Some(RequestValidationError::InvalidPatch),
        "the array end is a position only Add addresses"
    );
}

#[test]
fn patch_outcome_and_refusal_vocabularies_are_typed() {
    assert_eq!(PatchOutcome::Applied, PatchOutcome::Applied);
    assert_ne!(PatchOutcome::Created, PatchOutcome::NotModified);

    assert_eq!(PatchError::MissingTarget, PatchError::MissingTarget);
    assert_eq!(PatchError::StaleTarget { observed: 3 }, PatchError::StaleTarget { observed: 3 });
    assert_eq!(PatchError::PathNotFound, PatchError::PathNotFound);
    assert_eq!(PatchError::TypeMismatch, PatchError::TypeMismatch);
    assert_eq!(PatchError::ArithmeticOverflow, PatchError::ArithmeticOverflow);
    assert_eq!(
        PatchError::Engine(EngineError::StorageFailure),
        PatchError::Engine(EngineError::StorageFailure)
    );
}

#[test]
fn add_or_patch_carries_both_legs() {
    let if_missing = Document::new(document_id(), collection(), DocumentValue::Null);
    let patch = DocumentPatch::try_new(vec![PatchOperation::Increment {
        path: field_path(&["login_count"]),
        delta: 3,
    }])
    .expect("valid patch");
    let add_or_patch = AddOrPatch::new(if_missing, patch);
    assert_eq!(add_or_patch.if_missing().id(), &document_id());
    assert_eq!(add_or_patch.patch().operations().len(), 1);
}

#[test]
fn expected_version_discipline_uses_the_engine_vocabulary() {
    // The patch seam carries the same expectation the put seam does; the
    // three forms are the closed set the commit seam checks.
    assert_eq!(ExpectedVersion::Any, ExpectedVersion::Any);
    assert_eq!(ExpectedVersion::New, ExpectedVersion::New);
    assert_eq!(ExpectedVersion::Exact(4), ExpectedVersion::Exact(4));
    assert_ne!(ExpectedVersion::Exact(4), ExpectedVersion::Exact(5));
}
