//! Public-seam tests for the Schema Migration model (SM-03).
//!
//! These tests reach only what an external caller can: the typed contract,
//! the closed transform vocabulary and its coercion contracts, the rollback
//! limits computed from the transform list, and the manifest identity. The
//! engine-driving behavior (dry run, apply, resume, rollback, evidence) is
//! covered by the in-crate migration tests.

use std::collections::BTreeMap;

use terrabase_v2::families::{
    SchemaDefinition, SchemaFieldType, SchemaRule, SchemaVersion,
    migrations::{
        Coercion, FieldOutcome, FieldTransform, MigrationError, RollbackBlockedCause,
        RollbackLimit, SchemaMigration, TransformLiteral, TransformRejection,
    },
};

fn version(value: u64) -> SchemaVersion {
    SchemaVersion::try_new(value).expect("valid version")
}

fn object(fields: &[(&str, terrabase_v2::DocumentValue)]) -> terrabase_v2::DocumentValue {
    terrabase_v2::DocumentValue::Object(
        fields
            .iter()
            .map(|(key, value)| ((*key).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn text(value: &str) -> terrabase_v2::DocumentValue {
    terrabase_v2::DocumentValue::String(value.to_owned())
}

fn integer(value: i64) -> terrabase_v2::DocumentValue {
    terrabase_v2::DocumentValue::Integer(value)
}

fn users_target(to: u64) -> SchemaDefinition {
    SchemaDefinition::try_new(
        version(to),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "email".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "nickname".to_owned(), kind: SchemaFieldType::Text },
        ],
    )
    .expect("valid target")
}

fn users_collection() -> terrabase_v2::types::CollectionName {
    terrabase_v2::types::CollectionName::try_new("users").expect("valid collection")
}

#[test]
fn migration_contracts_are_forward_only_and_version_matched() {
    let contract = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![FieldTransform::RenameField { from: "nick".to_owned(), to: "nickname".to_owned() }],
    )
    .expect("valid contract");
    assert_eq!(contract.from_version(), version(1));
    assert_eq!(contract.to_version(), version(2));
    assert_eq!(contract.target().version(), version(2));
    assert_eq!(contract.transforms().len(), 1);
    assert_eq!(contract.rollback_limit(), RollbackLimit::Reversible);

    // Backward and restating steps are refused; so is a target whose
    // version identity disagrees with the step.
    for (from, to, target) in
        [(2, 1, users_target(1)), (1, 1, users_target(1)), (1, 3, users_target(2))]
    {
        let refused = SchemaMigration::try_new(
            users_collection(),
            version(from),
            version(to),
            target,
            vec![],
        );
        assert_eq!(refused, Err(MigrationError::InvalidContract), "{from}->{to}");
    }
}

#[test]
fn the_transform_vocabulary_states_its_coercion_contracts() {
    // Add is add-if-absent.
    let add = FieldTransform::AddField {
        field: "plan".to_owned(),
        default: TransformLiteral::Text("free".to_owned()),
    };
    match add.apply(&object(&[("email", text("a"))])) {
        FieldOutcome::Changed(value) => {
            assert_eq!(value, object(&[("email", text("a")), ("plan", text("free"))]))
        }
        other => panic!("absent field added: {other:?}"),
    }
    assert_eq!(
        add.apply(&object(&[("plan", text("paid"))])),
        FieldOutcome::Unchanged,
        "a present value is never overwritten"
    );

    // Rename refuses an occupied destination.
    let rename = FieldTransform::RenameField { from: "a".to_owned(), to: "b".to_owned() };
    assert_eq!(
        rename.apply(&object(&[("a", text("1")), ("b", text("2"))])),
        FieldOutcome::Rejected(TransformRejection::TargetFieldOccupied)
    );

    // The closed coercions refuse everything outside their domain, and the
    // exact pairs round-trip.
    let render =
        FieldTransform::RetypeField { field: "n".to_owned(), coercion: Coercion::IntegerToText };
    let parse =
        FieldTransform::RetypeField { field: "n".to_owned(), coercion: Coercion::TextToInteger };
    match render.apply(&object(&[("n", integer(12))])) {
        FieldOutcome::Changed(rendered) => {
            assert_eq!(rendered, object(&[("n", text("12"))]));
            match parse.apply(&rendered) {
                FieldOutcome::Changed(parsed) => assert_eq!(parsed, object(&[("n", integer(12))])),
                other => panic!("the exact pair inverts: {other:?}"),
            }
        }
        other => panic!("integer renders: {other:?}"),
    }
    for bad in [" 1", "1 ", "+1", "01", "1.0", "-", "0x1", "1e3"] {
        assert_eq!(
            parse.apply(&object(&[("n", text(bad))])),
            FieldOutcome::Rejected(TransformRejection::ValueNotCoercible),
            "non-canonical text refused: {bad}"
        );
    }
    assert_eq!(
        parse.apply(&object(&[("n", text("x"))])),
        FieldOutcome::Rejected(TransformRejection::ValueNotCoercible)
    );

    // A non-object value passes every field transform untouched.
    assert_eq!(add.apply(&text("scalar")), FieldOutcome::Unchanged);
    assert_eq!(rename.apply(&integer(3)), FieldOutcome::Unchanged);
}

#[test]
fn rollback_limits_follow_the_destructive_transforms() {
    let rename_only = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![
            FieldTransform::RenameField { from: "nick".to_owned(), to: "nickname".to_owned() },
            FieldTransform::RetypeField {
                field: "age".to_owned(),
                coercion: Coercion::NumberToInteger,
            },
        ],
    )
    .expect("valid contract");
    assert_eq!(rename_only.rollback_limit(), RollbackLimit::Reversible);
    assert_eq!(
        rename_only.inverse_transforms(),
        Some(vec![
            FieldTransform::RetypeField {
                field: "age".to_owned(),
                coercion: Coercion::IntegerToNumber
            },
            FieldTransform::RenameField { from: "nickname".to_owned(), to: "nick".to_owned() },
        ])
    );

    let destructive = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![
            FieldTransform::AddField {
                field: "plan".to_owned(),
                default: TransformLiteral::Integer(0),
            },
            FieldTransform::RemoveField { field: "legacy".to_owned() },
        ],
    )
    .expect("valid contract");
    assert_eq!(
        destructive.rollback_limit(),
        RollbackLimit::NotReversible {
            causes: vec![RollbackBlockedCause::FieldDefaulted, RollbackBlockedCause::FieldRemoved],
        }
    );
    assert_eq!(destructive.inverse_transforms(), None, "no inverse exists to compute");

    // Only the destructive kinds matter for the limit.
    let mixed = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![
            FieldTransform::RenameField { from: "nick".to_owned(), to: "nickname".to_owned() },
            FieldTransform::RemoveField { field: "legacy".to_owned() },
        ],
    )
    .expect("valid contract");
    assert_eq!(
        mixed.rollback_limit(),
        RollbackLimit::NotReversible { causes: vec![RollbackBlockedCause::FieldRemoved] }
    );
}

#[test]
fn contracts_are_bounded_and_names_are_validated() {
    let many: Vec<FieldTransform> = (0..257)
        .map(|index| FieldTransform::RemoveField { field: format!("field-{index}") })
        .collect();
    let refused =
        SchemaMigration::try_new(users_collection(), version(1), version(2), users_target(2), many);
    assert_eq!(refused, Err(MigrationError::InvalidContract));

    let boundary: Vec<FieldTransform> = (0..256)
        .map(|index| FieldTransform::RemoveField { field: format!("field-{index}") })
        .collect();
    assert!(
        SchemaMigration::try_new(
            users_collection(),
            version(1),
            version(2),
            users_target(2),
            boundary
        )
        .is_ok(),
        "the bound itself constructs"
    );

    for name in ["", " spaced ", "control\u{0}char", &"x".repeat(256)] {
        let invalid = SchemaMigration::try_new(
            users_collection(),
            version(1),
            version(2),
            users_target(2),
            vec![FieldTransform::RemoveField { field: name.to_owned() }],
        );
        assert_eq!(invalid, Err(MigrationError::InvalidContract), "name refused: {name:?}");
    }
}

#[test]
fn equal_contracts_share_one_identity_and_changes_break_it() {
    // The manifest digest is crate-internal, but identity is observable
    // through the contract's own equality and through the rollback limit's
    // sensitivity: two equal declarations are one contract, and any changed
    // transform, default, or target rule is a different one.
    let first = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![FieldTransform::AddField {
            field: "plan".to_owned(),
            default: TransformLiteral::Text("free".to_owned()),
        }],
    )
    .expect("valid contract");

    let same = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![FieldTransform::AddField {
            field: "plan".to_owned(),
            default: TransformLiteral::Text("free".to_owned()),
        }],
    )
    .expect("valid contract");
    assert_eq!(first, same, "equal declarations are the same contract");

    let changed_default = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![FieldTransform::AddField {
            field: "plan".to_owned(),
            default: TransformLiteral::Text("paid".to_owned()),
        }],
    )
    .expect("valid contract");
    assert_ne!(first, changed_default, "a changed default is a different contract");

    let changed_field = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        users_target(2),
        vec![FieldTransform::AddField {
            field: "tier".to_owned(),
            default: TransformLiteral::Text("free".to_owned()),
        }],
    )
    .expect("valid contract");
    assert_ne!(first, changed_field);

    let changed_target = SchemaMigration::try_new(
        users_collection(),
        version(1),
        version(2),
        SchemaDefinition::try_new(
            version(2),
            vec![SchemaRule::Required { field: "email".to_owned() }],
        )
        .expect("valid target"),
        vec![FieldTransform::AddField {
            field: "plan".to_owned(),
            default: TransformLiteral::Text("free".to_owned()),
        }],
    )
    .expect("valid contract");
    assert_ne!(first, changed_target, "a changed target rule is a different contract");
}
