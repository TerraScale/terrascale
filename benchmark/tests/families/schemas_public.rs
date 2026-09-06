//! Public-seam tests for the Document Schema model and its JSON Schema
//! subset mapping (SM-02).
//!
//! These tests reach only what an external caller can: the typed schema
//! payloads, the version identity, and the projection and parser of the
//! documented JSON Schema subset. No engine handle is needed; staging and
//! enforcement behavior is covered by the in-crate family tests.

use std::collections::BTreeMap;

use terrabase::{
    DocumentValue,
    families::{SchemaDefinition, SchemaFieldType, SchemaRule, SchemaVersion},
};

fn version(value: u64) -> SchemaVersion {
    SchemaVersion::try_new(value).expect("valid version")
}

fn text(values: &[&str]) -> Vec<DocumentValue> {
    values.iter().map(|value| DocumentValue::String((*value).to_owned())).collect()
}

/// One schema using every rule kind of the closed set.
fn full_schema() -> SchemaDefinition {
    SchemaDefinition::try_new(
        version(7),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
            SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::EnumValues {
                field: "role".to_owned(),
                values: vec!["reader".to_owned(), "writer".to_owned()],
            },
            SchemaRule::NumberRange { field: "score".to_owned(), min: Some(0), max: Some(100) },
            SchemaRule::TextLength { field: "note".to_owned(), min: Some(1), max: Some(80) },
        ],
    )
    .expect("valid schema")
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(key, value)| ((*key).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

/// Returns the derived `Debug` name of the schema-format failure produced by
/// parsing `document`.
///
/// The format error type is defined in the crate-private `families::schemas`
/// module (the canonical path the `schema_catalog` module imports it from),
/// so this external-seam test cannot name its variants and pins the exact
/// typed case by derived name instead. No behavior changes; only the
/// assertion spelling moves.
fn format_error_name(version: SchemaVersion, document: &DocumentValue) -> String {
    format!("{:?}", SchemaDefinition::from_json_schema(version, document).unwrap_err())
}

#[test]
fn json_schema_subset_round_trips_exactly() {
    let schema = full_schema();
    let projected = schema.to_json_schema();

    let DocumentValue::Object(root) = &projected else {
        panic!("the projection is an object");
    };
    assert_eq!(
        root.get("$schema"),
        Some(&DocumentValue::String("https://json-schema.org/draft/2020-12/schema".to_owned()))
    );
    assert_eq!(root.get("type"), Some(&DocumentValue::String("object".to_owned())));
    assert_eq!(root.get("x-terrabase-schema-version"), Some(&DocumentValue::Integer(7)));
    let DocumentValue::Array(required) = root.get("required").expect("required present") else {
        panic!("required is an array");
    };
    assert_eq!(required, &text(&["email"]));

    let parsed =
        SchemaDefinition::from_json_schema(version(7), &projected).expect("parses its own subset");
    assert_eq!(parsed, schema, "the subset projection round-trips exactly");

    // A schema with only required fields projects a property map without
    // those fields and still round-trips.
    let required_only = SchemaDefinition::try_new(
        version(1),
        vec![SchemaRule::Required { field: "email".to_owned() }],
    )
    .expect("valid schema");
    let projected = required_only.to_json_schema();
    assert_eq!(SchemaDefinition::from_json_schema(version(1), &projected), Ok(required_only));

    // The deterministic projection of equal schemas is equal regardless of
    // declaration order.
    let reordered = SchemaDefinition::try_new(
        version(7),
        vec![
            SchemaRule::TextLength { field: "note".to_owned(), min: Some(1), max: Some(80) },
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::NumberRange { field: "score".to_owned(), min: Some(0), max: Some(100) },
            SchemaRule::EnumValues {
                field: "role".to_owned(),
                values: vec!["reader".to_owned(), "writer".to_owned()],
            },
            SchemaRule::FieldType { field: "name".to_owned(), kind: SchemaFieldType::Text },
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
        ],
    )
    .expect("valid schema");
    assert_eq!(reordered.to_json_schema(), full_schema().to_json_schema());
}

#[test]
fn json_schema_parser_accepts_hand_written_subset_documents() {
    let hand_written = object(&[
        ("type", DocumentValue::String("object".to_owned())),
        (
            "properties",
            object(&[
                (
                    "age",
                    object(&[
                        ("type", DocumentValue::String("integer".to_owned())),
                        ("minimum", DocumentValue::Integer(0)),
                        ("maximum", DocumentValue::Integer(130)),
                    ]),
                ),
                ("name", object(&[("type", DocumentValue::String("string".to_owned()))])),
                (
                    "state",
                    object(&[
                        ("enum", DocumentValue::Array(text(&["on", "off"]))),
                        ("minLength", DocumentValue::Integer(2)),
                    ]),
                ),
            ]),
        ),
        ("required", DocumentValue::Array(text(&["name", "age"]))),
    ]);
    let schema = SchemaDefinition::from_json_schema(version(3), &hand_written)
        .expect("parses the documented subset");
    assert_eq!(schema.version(), version(3));
    assert_eq!(
        schema.rules().len(),
        7,
        "two required, two field types, one enum, one number range, one text length"
    );

    // The text type is JSON Schema's `string`; the logical field type keeps
    // its own closed name in the typed model.
    assert!(
        schema
            .rules()
            .iter()
            .any(|rule| matches!(rule, SchemaRule::FieldType { kind: SchemaFieldType::Text, .. }))
    );
}

#[test]
fn json_schema_parser_rejects_keywords_outside_the_subset() {
    let cases: Vec<(&str, DocumentValue)> = vec![
        (
            "top-level keyword",
            object(&[
                ("type", DocumentValue::String("object".to_owned())),
                ("additionalProperties", DocumentValue::Boolean(false)),
            ]),
        ),
        (
            "references",
            object(&[
                ("type", DocumentValue::String("object".to_owned())),
                ("$ref", DocumentValue::String("#/$defs/thing".to_owned())),
            ]),
        ),
        (
            "combinators",
            object(&[(
                "allOf",
                DocumentValue::Array(vec![object(&[(
                    "type",
                    DocumentValue::String("object".to_owned()),
                )])]),
            )]),
        ),
        (
            "property keyword",
            object(&[(
                "properties",
                object(&[(
                    "name",
                    object(&[("pattern", DocumentValue::String("^[a-z]+$".to_owned()))]),
                )]),
            )]),
        ),
        (
            "nested properties",
            object(&[(
                "properties",
                object(&[(
                    "inner",
                    object(&[(
                        "properties",
                        object(&[(
                            "leaf",
                            object(&[("type", DocumentValue::String("string".to_owned()))]),
                        )]),
                    )]),
                )]),
            )]),
        ),
    ];
    for (name, document) in cases {
        assert_eq!(
            format_error_name(version(1), &document),
            "UnsupportedKeyword",
            "{name} is outside the documented subset"
        );
    }
}

#[test]
fn json_schema_parser_rejects_wrong_dialect_types_and_values() {
    let wrong_dialect = object(&[
        ("$schema", DocumentValue::String("http://json-schema.org/draft-07/schema#".to_owned())),
        ("type", DocumentValue::String("object".to_owned())),
    ]);
    assert_eq!(format_error_name(version(1), &wrong_dialect), "UnsupportedDialect");

    let wrong_type = object(&[("type", DocumentValue::String("array".to_owned()))]);
    assert_eq!(format_error_name(version(1), &wrong_type), "UnsupportedType");

    let unknown_property_type = object(&[(
        "properties",
        object(&[("name", object(&[("type", DocumentValue::String("null".to_owned()))]))]),
    )]);
    assert_eq!(format_error_name(version(1), &unknown_property_type), "UnsupportedType");

    assert_eq!(format_error_name(version(1), &DocumentValue::Array(vec![])), "NotAnObject");

    let malformed: Vec<(&str, DocumentValue)> = vec![
        (
            "required is not an array",
            object(&[("required", DocumentValue::String("name".to_owned()))]),
        ),
        (
            "required holds a non-string",
            object(&[("required", DocumentValue::Array(vec![DocumentValue::Integer(1)]))]),
        ),
        (
            "enum holds a non-string",
            object(&[(
                "properties",
                object(&[(
                    "state",
                    object(&[("enum", DocumentValue::Array(vec![DocumentValue::Integer(1)]))]),
                )]),
            )]),
        ),
        (
            "empty enum",
            object(&[(
                "properties",
                object(&[("state", object(&[("enum", DocumentValue::Array(vec![]))]))]),
            )]),
        ),
        (
            "negative length bound",
            object(&[(
                "properties",
                object(&[("name", object(&[("minLength", DocumentValue::Integer(-1))]))]),
            )]),
        ),
        (
            "bound is a float",
            object(&[(
                "properties",
                object(&[("age", object(&[("minimum", DocumentValue::Number(0.5))]))]),
            )]),
        ),
        ("invalid field name", object(&[("required", DocumentValue::Array(text(&[" a "])))])),
    ];
    for (name, document) in malformed {
        assert_eq!(
            format_error_name(version(1), &document),
            "MalformedValue",
            "{name} is malformed"
        );
    }

    // The embedded version identity must agree with the typed parameter.
    let mismatched = full_schema().to_json_schema();
    assert_eq!(format_error_name(version(8), &mismatched), "VersionMismatch");
}

#[test]
fn schema_declarations_are_bounded() {
    let many_rules: Vec<SchemaRule> =
        (0..1025).map(|index| SchemaRule::Required { field: format!("field-{index}") }).collect();
    assert_eq!(
        SchemaDefinition::try_new(version(1), many_rules),
        Err(terrabase::RequestValidationError::InvalidFamilyPayload)
    );

    let many_values: Vec<String> = (0..257).map(|index| format!("value-{index}")).collect();
    assert_eq!(
        SchemaDefinition::try_new(
            version(1),
            vec![SchemaRule::EnumValues { field: "state".to_owned(), values: many_values }],
        ),
        Err(terrabase::RequestValidationError::InvalidFamilyPayload)
    );

    let boundary: Vec<SchemaRule> =
        (0..1024).map(|index| SchemaRule::Required { field: format!("field-{index}") }).collect();
    assert!(SchemaDefinition::try_new(version(1), boundary).is_ok());
}

#[test]
fn versions_and_text_bounds_stay_inside_the_interchange_domain() {
    // The interchange domain of the wire projection and the durable record is
    // the i64 integer range. A schema identity or a text-length bound above
    // `i64::MAX` is rejected where it is declared instead of being recorded
    // or projected as a loosened bound.
    let above = u64::try_from(i64::MAX).expect("positive") + 1;
    assert_eq!(
        SchemaVersion::try_new(above),
        Err(terrabase::RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        SchemaVersion::try_new(above + 1),
        Err(terrabase::RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        SchemaVersion::try_new(u64::MAX),
        Err(terrabase::RequestValidationError::InvalidFamilyPayload)
    );
    let edge = u64::try_from(i64::MAX).expect("positive");
    assert!(SchemaVersion::try_new(edge).is_ok(), "the i64 domain itself stays declarable");

    for bound in [above, u64::MAX] {
        assert_eq!(
            SchemaDefinition::try_new(
                version(1),
                vec![SchemaRule::TextLength {
                    field: "note".to_owned(),
                    min: Some(bound),
                    max: None,
                }],
            ),
            Err(terrabase::RequestValidationError::InvalidFamilyPayload),
            "a minimum length above the interchange domain is rejected ({bound})"
        );
        assert_eq!(
            SchemaDefinition::try_new(
                version(1),
                vec![SchemaRule::TextLength {
                    field: "note".to_owned(),
                    min: None,
                    max: Some(bound),
                }],
            ),
            Err(terrabase::RequestValidationError::InvalidFamilyPayload),
            "a maximum length above the interchange domain is rejected ({bound})"
        );
    }

    // At the domain edge nothing clamps: the projection carries the declared
    // integers exactly and still round-trips.
    let edge_schema = SchemaDefinition::try_new(
        SchemaVersion::try_new(edge).expect("valid version"),
        vec![SchemaRule::TextLength { field: "note".to_owned(), min: Some(1), max: Some(edge) }],
    )
    .expect("valid schema");
    let projected = edge_schema.to_json_schema();
    let DocumentValue::Object(root) = &projected else {
        panic!("the projection is an object");
    };
    assert_eq!(root.get("x-terrabase-schema-version"), Some(&DocumentValue::Integer(i64::MAX)));
    let DocumentValue::Object(properties) = root.get("properties").expect("properties") else {
        panic!("properties is an object");
    };
    let DocumentValue::Object(note) = properties.get("note").expect("note projected") else {
        panic!("note is an object");
    };
    assert_eq!(note.get("minLength"), Some(&DocumentValue::Integer(1)));
    assert_eq!(note.get("maxLength"), Some(&DocumentValue::Integer(i64::MAX)));
    assert_eq!(
        SchemaDefinition::from_json_schema(
            SchemaVersion::try_new(edge).expect("valid version"),
            &projected,
        ),
        Ok(edge_schema),
        "the edge-of-domain projection round-trips exactly"
    );
}

#[test]
fn json_schema_parser_rejects_duplicated_enum_values() {
    let duplicated = object(&[(
        "properties",
        object(&[("state", object(&[("enum", DocumentValue::Array(text(&["on", "off", "on"])))]))]),
    )]);
    assert_eq!(
        format_error_name(version(1), &duplicated),
        "MalformedValue",
        "an accepted-value set names distinct values"
    );

    // The typed model holds the same contract: the same rules construct
    // structurally, so the import rejection cannot hide behind `try_new`.
    let typed = SchemaDefinition::try_new(
        version(1),
        vec![SchemaRule::EnumValues {
            field: "state".to_owned(),
            values: vec!["on".to_owned(), "off".to_owned(), "on".to_owned()],
        }],
    );
    assert!(typed.is_ok(), "duplicates are a whole-definition concern");
}

#[test]
fn json_schema_parser_rejects_inert_bound_keywords() {
    // A numeric bound never binds a non-numeric type, and a length bound
    // never binds a non-text type: the evaluator would never fire them, so
    // the import fails closed instead of staging a rule that cannot apply.
    let inert: Vec<(&str, DocumentValue)> = vec![
        (
            "minimum on a string",
            object(&[(
                "properties",
                object(&[(
                    "name",
                    object(&[
                        ("type", DocumentValue::String("string".to_owned())),
                        ("minimum", DocumentValue::Integer(5)),
                    ]),
                )]),
            )]),
        ),
        (
            "maximum on a string",
            object(&[(
                "properties",
                object(&[(
                    "name",
                    object(&[
                        ("type", DocumentValue::String("string".to_owned())),
                        ("maximum", DocumentValue::Integer(5)),
                    ]),
                )]),
            )]),
        ),
        (
            "minimum on a boolean",
            object(&[(
                "properties",
                object(&[(
                    "active",
                    object(&[
                        ("type", DocumentValue::String("boolean".to_owned())),
                        ("minimum", DocumentValue::Integer(0)),
                    ]),
                )]),
            )]),
        ),
        (
            "minLength on an integer",
            object(&[(
                "properties",
                object(&[(
                    "age",
                    object(&[
                        ("type", DocumentValue::String("integer".to_owned())),
                        ("minLength", DocumentValue::Integer(2)),
                    ]),
                )]),
            )]),
        ),
        (
            "maxLength on a number",
            object(&[(
                "properties",
                object(&[(
                    "score",
                    object(&[
                        ("type", DocumentValue::String("number".to_owned())),
                        ("maxLength", DocumentValue::Integer(8)),
                    ]),
                )]),
            )]),
        ),
        (
            "minLength on an object",
            object(&[(
                "properties",
                object(&[(
                    "inner",
                    object(&[
                        ("type", DocumentValue::String("object".to_owned())),
                        ("minLength", DocumentValue::Integer(1)),
                    ]),
                )]),
            )]),
        ),
        (
            "maxLength on an array",
            object(&[(
                "properties",
                object(&[(
                    "tags",
                    object(&[
                        ("type", DocumentValue::String("array".to_owned())),
                        ("maxLength", DocumentValue::Integer(3)),
                    ]),
                )]),
            )]),
        ),
    ];
    for (name, document) in inert {
        assert_eq!(
            format_error_name(version(1), &document),
            "MalformedValue",
            "{name} can never bind"
        );
    }

    // The matching pairs import, and a bound without a declared type stays
    // meaningful: it binds whichever numeric or text value the field carries.
    let matching = object(&[
        (
            "properties",
            object(&[
                (
                    "age",
                    object(&[
                        ("type", DocumentValue::String("integer".to_owned())),
                        ("minimum", DocumentValue::Integer(0)),
                        ("maximum", DocumentValue::Integer(130)),
                    ]),
                ),
                (
                    "name",
                    object(&[
                        ("type", DocumentValue::String("string".to_owned())),
                        ("minLength", DocumentValue::Integer(1)),
                        ("maxLength", DocumentValue::Integer(80)),
                    ]),
                ),
                ("score", object(&[("maximum", DocumentValue::Integer(100))])),
            ]),
        ),
        ("required", DocumentValue::Array(text(&["name"]))),
    ]);
    let schema = SchemaDefinition::from_json_schema(version(2), &matching)
        .expect("bounds on their own type import");
    assert_eq!(schema.rules().len(), 6);
    assert_eq!(
        schema.rules().iter().filter(|rule| matches!(rule, SchemaRule::NumberRange { .. })).count(),
        2
    );
    assert_eq!(
        schema.rules().iter().filter(|rule| matches!(rule, SchemaRule::TextLength { .. })).count(),
        1
    );
}
