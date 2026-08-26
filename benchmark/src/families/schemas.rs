//! Collection schemas: definition, versioning, and write validation (SM-02).
//!
//! This module owns the engine's Document Schema capability on top of the
//! accepted [`SchemaValidation`](super::FamilyKind::SchemaValidation) family:
//! the bounded deterministic evaluator for one document value, the semantic
//! checks a declared schema must pass before it is staged, the engine
//! write-path hook that enforces a collection's declared schema on every
//! staged document put, and the documented JSON Schema subset mapping used
//! for interoperability.
//!
//! The schema language is the accepted W3-F closed rule set, now versioned:
//! each [`SchemaDefinition`] carries a positive [`SchemaVersion`] identity,
//! rules live in canonical order, and one collection's schema is recorded
//! with its metadata in the reserved family collection, scoped to one tenant
//! by the Engine's document keying. Schema-free collections keep their exact
//! prior behavior: when no validation record exists the evaluator is never
//! consulted and no write-path byte changes.
//!
//! Validation is bounded and deterministic by construction: a schema holds at
//! most 256 rules, each rule performs one bounded field lookup, and
//! violations are reported in canonical rule order with typed, content-safe
//! kinds — a violation names a field and a rule, or the document itself when
//! the value is not an object and the schema declares at least one rule, and
//! never a field value.
//!
//! The JSON Schema mapping projects a schema onto a strict, documented
//! subset of JSON Schema draft 2020-12 and parses exactly that subset back,
//! failing closed on any keyword outside it. JSON Schema verbatim is
//! rejected: its unbounded constructs (references, pattern properties,
//! combinators) cannot guarantee bounded deterministic evaluation at the
//! engine seam. The decision record is `docs/families/schemas.md`.

use std::collections::{BTreeMap, BTreeSet};

use super::ViolationKind;
use super::payloads::{
    MAX_ENUM_VALUES, SchemaDefinition, SchemaRule, SchemaVersion, SchemaViolation,
    interchange_integer, validate_name as validate_field_name,
};
use super::state::{RESERVED_COLLECTION, ValidationRecord, record_id};

use crate::{
    engine::{EngineError, Transaction},
    types::{Document, DocumentValue},
};

// ---------------------------------------------------------------------------
// Bounded deterministic evaluation
// ---------------------------------------------------------------------------

/// Reports whether one logical type matches one document value.
fn type_matches(kind: super::SchemaFieldType, value: &DocumentValue) -> bool {
    use super::SchemaFieldType as FieldType;
    matches!(
        (kind, value),
        (FieldType::Boolean, DocumentValue::Boolean(_))
            | (FieldType::Integer | FieldType::Number, DocumentValue::Integer(_))
            | (FieldType::Number, DocumentValue::Number(_))
            | (FieldType::Text, DocumentValue::String(_))
            | (FieldType::Object, DocumentValue::Object(_))
            | (FieldType::Array, DocumentValue::Array(_))
    )
}

/// Converts one integer policy bound into the float domain.
///
/// A bound beyond 2^53 rounds, which can only blur a comparison at the very
/// edge of the integer range; rule bounds sit far below that in practice.
#[allow(clippy::cast_precision_loss)]
fn float_bound(bound: i64) -> f64 {
    bound as f64
}

/// Computes the policy violations of one document value.
///
/// Evaluation walks the schema's canonical rule order once, so the violation
/// list is deterministic: same schema and value, same order, same kinds. The
/// closed rule set rules object fields, so a document value that is not an
/// object can satisfy none of it: with at least one rule declared, the
/// document itself is the violation, reported once against the document
/// rather than a field ([`ViolationKind::DocumentNotObject`], empty field
/// name). A rule-free schema stays vacuous for every value, object or not.
pub(crate) fn violations(schema: &SchemaDefinition, value: &DocumentValue) -> Vec<SchemaViolation> {
    let DocumentValue::Object(fields) = value else {
        if schema.rules().is_empty() {
            return Vec::new();
        }
        return vec![SchemaViolation::new(String::new(), ViolationKind::DocumentNotObject)];
    };
    let mut found = Vec::new();
    for rule in schema.rules() {
        let field = rule.field();
        let present = fields.get(field);
        match rule {
            SchemaRule::Required { field } => {
                if present.is_none() {
                    found.push(SchemaViolation::new(
                        field.clone(),
                        ViolationKind::MissingRequiredField,
                    ));
                }
            }
            SchemaRule::FieldType { field, kind } => {
                if let Some(value) = present
                    && !type_matches(*kind, value)
                {
                    found.push(SchemaViolation::new(field.clone(), ViolationKind::TypeMismatch));
                }
            }
            SchemaRule::EnumValues { field, values } => {
                if let Some(value) = present {
                    let allowed = match value {
                        DocumentValue::String(text) => values.contains(text),
                        _ => false,
                    };
                    if !allowed {
                        found.push(SchemaViolation::new(
                            field.clone(),
                            ViolationKind::ValueNotAllowed,
                        ));
                    }
                }
            }
            SchemaRule::NumberRange { field, min, max } => {
                // The bound applies to every numeric encoding the type rule
                // admits: the integer and the float domain alike.
                if let Some(value) = present {
                    let out_of_range = match value {
                        DocumentValue::Integer(number) => {
                            min.is_some_and(|bound| *number < bound)
                                || max.is_some_and(|bound| *number > bound)
                        }
                        DocumentValue::Number(number) => {
                            min.is_some_and(|bound| *number < float_bound(bound))
                                || max.is_some_and(|bound| *number > float_bound(bound))
                        }
                        _ => false,
                    };
                    if out_of_range {
                        found.push(SchemaViolation::new(
                            field.clone(),
                            ViolationKind::NumberOutOfRange,
                        ));
                    }
                }
            }
            SchemaRule::TextLength { field, min, max } => {
                if let Some(DocumentValue::String(text)) = present {
                    let length = u64::try_from(text.chars().count()).unwrap_or(u64::MAX);
                    let below = min.is_some_and(|bound| length < bound);
                    let above = max.is_some_and(|bound| length > bound);
                    if below || above {
                        found.push(SchemaViolation::new(
                            field.clone(),
                            ViolationKind::LengthOutOfRange,
                        ));
                    }
                }
            }
        }
    }
    found
}

/// Reports whether one accepted-value list names a value twice.
fn has_duplicate(values: &[String]) -> bool {
    let mut seen: BTreeSet<&str> = BTreeSet::new();
    values.iter().any(|value| !seen.insert(value.as_str()))
}

/// Validates one schema semantically before it is staged.
///
/// Structural problems (names, bounds, rule count) are rejected at payload
/// construction; the rules here need the whole definition: inverted numeric
/// or length bounds, an empty or repeated accepted-value set, a bound rule
/// without any bound, a bound rule the field's declared type can never bind,
/// and a duplicate field-and-kind pair.
pub(crate) fn validate_schema(schema: &SchemaDefinition) -> Result<(), super::FamilyError> {
    use super::SchemaFieldType as FieldType;

    let malformed = super::FamilyError::ValidationPolicyMalformed;
    // The field's declared type decides which bound rules can ever bind: a
    // numeric range binds the integer and number types, a text length binds
    // the text type. Any other pairing could never fire, so it is malformed
    // rather than staged as an inert rule.
    let declared_types: BTreeMap<&str, super::SchemaFieldType> = schema
        .rules()
        .iter()
        .filter_map(|rule| match rule {
            SchemaRule::FieldType { field, kind } => Some((field.as_str(), *kind)),
            _ => None,
        })
        .collect();
    let numeric =
        |kind: super::SchemaFieldType| matches!(kind, FieldType::Integer | FieldType::Number);
    let mut keys: Vec<String> = Vec::new();
    for rule in schema.rules() {
        let kind = match rule {
            SchemaRule::Required { .. } => "required",
            SchemaRule::FieldType { .. } => "field-type",
            SchemaRule::EnumValues { .. } => "enum",
            SchemaRule::NumberRange { .. } => "number-range",
            SchemaRule::TextLength { .. } => "text-length",
        };
        keys.push(format!("{}/{}", rule.field(), kind));
        match rule {
            SchemaRule::NumberRange { min: Some(min), max: Some(max), .. } if min > max => {
                return Err(malformed);
            }
            SchemaRule::TextLength { min: Some(min), max: Some(max), .. } if min > max => {
                return Err(malformed);
            }
            SchemaRule::NumberRange { min: None, max: None, .. }
            | SchemaRule::TextLength { min: None, max: None, .. } => {
                return Err(malformed);
            }
            SchemaRule::EnumValues { values, .. } if values.is_empty() || has_duplicate(values) => {
                return Err(malformed);
            }
            SchemaRule::NumberRange { field, .. }
                if declared_types.get(field.as_str()).is_some_and(|kind| !numeric(*kind)) =>
            {
                return Err(malformed);
            }
            SchemaRule::TextLength { field, .. }
                if declared_types
                    .get(field.as_str())
                    .is_some_and(|kind| *kind != FieldType::Text) =>
            {
                return Err(malformed);
            }
            _ => {}
        }
    }
    keys.sort();
    if keys.windows(2).any(|pair| pair[0] == pair[1]) {
        return Err(malformed);
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Engine write-path hook
// ---------------------------------------------------------------------------

/// Enforces the collection's declared schema on one staged document put.
///
/// This is the engine-seam hook called from
/// [`Transaction::put`](crate::engine::Transaction::put). It reads the
/// collection's validation record through the transaction itself, so a
/// schema staged earlier in the same transaction already applies, and the
/// record is looked up under the transaction's own tenant: another tenant's
/// schema for the same collection name is invisible and never applied.
///
/// While a Schema Migration drive is in progress on the collection, the
/// migration's target schema retargets enforcement instead of the recorded
/// one (the SM-03 evolution hook): puts conform to where the collection is
/// going, and a rollback drive falls back to the still-recorded from-version
/// schema. An unreadable migration record fails closed exactly like an
/// unreadable validation record.
///
/// The reserved family collection is exempt: family records are engine
/// state, not user documents, and a schema cannot be declared on it. A user
/// document that occupies the reserved validation-record address makes the
/// collection's schema state unreadable; the hook fails closed rather than
/// treating the collision as an absent schema. Unreadable or malformed
/// schema state surfaces as [`EngineError::StorageFailure`] — the engine
/// vocabulary has no reserved-state case, a recorded mapping limit — and a
/// violating document surfaces as [`EngineError::InvalidDocument`] with the
/// typed field-and-kind report available through the family validation read.
pub(crate) fn enforce_put(
    transaction: &Transaction<'_>,
    document: &Document,
) -> Result<(), EngineError> {
    let collection = document.collection().as_str();
    if collection == RESERVED_COLLECTION {
        return Ok(());
    }
    if let Some(target) =
        super::migrations::record::in_progress_target(transaction, document.collection())?
    {
        return if violations(&target, document.value()).is_empty() {
            Ok(())
        } else {
            Err(EngineError::InvalidDocument)
        };
    }
    let id = record_id("validation", &[collection]).map_err(|_| EngineError::StorageFailure)?;
    let Some(record) = transaction.read(&id)? else {
        return Ok(());
    };
    if record.collection().as_str() != RESERVED_COLLECTION {
        return Err(EngineError::StorageFailure);
    }
    let schema =
        ValidationRecord::decode(record.value()).map_err(|_| EngineError::StorageFailure)?;
    if violations(&schema.schema, document.value()).is_empty() {
        Ok(())
    } else {
        Err(EngineError::InvalidDocument)
    }
}

// ---------------------------------------------------------------------------
// JSON Schema subset mapping
// ---------------------------------------------------------------------------

/// The JSON Schema dialect of the documented subset.
const JSON_SCHEMA_DIALECT: &str = "https://json-schema.org/draft/2020-12/schema";

/// The extension keyword carrying the schema's engine version identity.
const VERSION_KEYWORD: &str = "x-terrabase-schema-version";

/// Failures of the documented JSON Schema subset mapping.
///
/// Every case is typed and content-safe: no case echoes any part of the
/// rejected schema document.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SchemaFormatError {
    /// The schema document, or one property subschema, is not an object.
    NotAnObject,
    /// A keyword outside the documented subset appeared.
    UnsupportedKeyword,
    /// The `$schema` dialect is not the documented one.
    UnsupportedDialect,
    /// A `type` value outside the closed set appeared.
    UnsupportedType,
    /// The embedded version identity differs from the requested one.
    VersionMismatch,
    /// A keyword's value does not match the documented subset shape.
    MalformedValue,
}

/// Returns the JSON Schema `type` name of one logical field type.
fn json_type_name(kind: super::SchemaFieldType) -> &'static str {
    use super::SchemaFieldType as FieldType;
    match kind {
        FieldType::Boolean => "boolean",
        FieldType::Integer => "integer",
        FieldType::Number => "number",
        FieldType::Text => "string",
        FieldType::Object => "object",
        FieldType::Array => "array",
    }
}

/// Parses one JSON Schema `type` name into a logical field type.
fn parse_json_type(text: &str) -> Option<super::SchemaFieldType> {
    use super::SchemaFieldType as FieldType;
    match text {
        "boolean" => Some(FieldType::Boolean),
        "integer" => Some(FieldType::Integer),
        "number" => Some(FieldType::Number),
        "string" => Some(FieldType::Text),
        "object" => Some(FieldType::Object),
        "array" => Some(FieldType::Array),
        _ => None,
    }
}

/// Builds one object value in canonical key order.
fn object(fields: Vec<(&str, DocumentValue)>) -> DocumentValue {
    DocumentValue::Object(
        fields.into_iter().map(|(key, value)| (key.to_owned(), value)).collect::<BTreeMap<_, _>>(),
    )
}

impl SchemaDefinition {
    /// Projects the schema onto the documented JSON Schema subset.
    ///
    /// The projection is total and deterministic: rules are already in
    /// canonical order, the resulting object keys follow the canonical map
    /// order, and the schema's version identity travels in the
    /// `x-terrabase-schema-version` extension keyword. Field rules map onto
    /// the subset keywords `type`, `enum`, `minimum`, `maximum`, `minLength`,
    /// and `maxLength`; required fields map onto the `required` array. The
    /// structured result is the interchange value at every typed seam; its
    /// JSON byte serialization is the host codec's concern.
    #[must_use]
    pub fn to_json_schema(&self) -> DocumentValue {
        // Group the canonical rules per field, preserving field order.
        let mut grouped: Vec<(String, Vec<&SchemaRule>)> = Vec::new();
        for rule in self.rules() {
            match grouped.last_mut() {
                Some((field, rules)) if field == rule.field() => rules.push(rule),
                _ => grouped.push((rule.field().to_owned(), vec![rule])),
            }
        }
        let mut required: Vec<DocumentValue> = Vec::new();
        let mut properties: BTreeMap<String, DocumentValue> = BTreeMap::new();
        for (field_name, rules) in &grouped {
            let mut subschema: Vec<(&str, DocumentValue)> = Vec::new();
            for rule in rules {
                match rule {
                    SchemaRule::Required { .. } => {
                        required.push(DocumentValue::String(field_name.clone()));
                    }
                    SchemaRule::FieldType { kind, .. } => subschema
                        .push(("type", DocumentValue::String(json_type_name(*kind).to_owned()))),
                    SchemaRule::EnumValues { values, .. } => subschema.push((
                        "enum",
                        DocumentValue::Array(
                            values.iter().map(|v| DocumentValue::String(v.clone())).collect(),
                        ),
                    )),
                    SchemaRule::NumberRange { min, max, .. } => {
                        if let Some(min) = min {
                            subschema.push(("minimum", DocumentValue::Integer(*min)));
                        }
                        if let Some(max) = max {
                            subschema.push(("maximum", DocumentValue::Integer(*max)));
                        }
                    }
                    SchemaRule::TextLength { min, max, .. } => {
                        if let Some(min) = min {
                            subschema.push((
                                "minLength",
                                DocumentValue::Integer(interchange_integer(*min)),
                            ));
                        }
                        if let Some(max) = max {
                            subschema.push((
                                "maxLength",
                                DocumentValue::Integer(interchange_integer(*max)),
                            ));
                        }
                    }
                }
            }
            if !subschema.is_empty() {
                properties.insert(field_name.clone(), object(subschema));
            }
        }
        let mut document = vec![
            ("$schema", DocumentValue::String(JSON_SCHEMA_DIALECT.to_owned())),
            ("type", DocumentValue::String("object".to_owned())),
            ("properties", DocumentValue::Object(properties)),
        ];
        if !required.is_empty() {
            document.push(("required", DocumentValue::Array(required)));
        }
        document.push((
            VERSION_KEYWORD,
            DocumentValue::Integer(interchange_integer(self.version().get())),
        ));
        object(document)
    }

    /// Parses one schema from exactly the documented JSON Schema subset.
    ///
    /// Every keyword outside the subset, every wrong dialect, every type
    /// outside the closed set, and every malformed keyword value is rejected
    /// fail-closed — the parser never ignores a keyword it does not know, so
    /// an imported schema cannot be silently weakened. The typed `version`
    /// parameter names the version identity of the result; when the document
    /// carries the `x-terrabase-schema-version` extension it must agree.
    ///
    /// # Errors
    ///
    /// Returns the typed [`SchemaFormatError`] case; no case carries any part
    /// of the rejected document.
    pub fn from_json_schema(
        version: SchemaVersion,
        value: &DocumentValue,
    ) -> Result<Self, SchemaFormatError> {
        let DocumentValue::Object(root) = value else {
            return Err(SchemaFormatError::NotAnObject);
        };
        for key in root.keys() {
            match key.as_str() {
                "$schema" | "type" | "properties" | "required" | VERSION_KEYWORD => {}
                _ => return Err(SchemaFormatError::UnsupportedKeyword),
            }
        }
        match root.get("$schema") {
            None => {}
            Some(DocumentValue::String(dialect)) if dialect == JSON_SCHEMA_DIALECT => {}
            _ => return Err(SchemaFormatError::UnsupportedDialect),
        }
        match root.get("type") {
            None => {}
            Some(DocumentValue::String(kind)) if kind == "object" => {}
            _ => return Err(SchemaFormatError::UnsupportedType),
        }
        match root.get(VERSION_KEYWORD) {
            None => {}
            Some(DocumentValue::Integer(embedded)) => {
                if u64::try_from(*embedded) != Ok(version.get()) {
                    return Err(SchemaFormatError::VersionMismatch);
                }
            }
            _ => return Err(SchemaFormatError::MalformedValue),
        }
        let required_names = parse_required(root.get("required"))?;
        let per_field = parse_properties(root.get("properties"))?;
        let mut per_field = per_field;
        for name in required_names {
            per_field
                .entry(name.clone())
                .or_default()
                .insert(0, SchemaRule::Required { field: name });
        }
        let rules = per_field.into_values().flatten().collect();
        SchemaDefinition::try_new(version, rules).map_err(|_| SchemaFormatError::MalformedValue)
    }
}

/// Parses the subset's `required` keyword into validated, unique field names.
///
/// The flattened canonical rule order puts each field's required rule first,
/// so the caller inserts it at the head of the field's rule list.
fn parse_required(encoded: Option<&DocumentValue>) -> Result<Vec<String>, SchemaFormatError> {
    let Some(encoded) = encoded else {
        return Ok(Vec::new());
    };
    let DocumentValue::Array(names) = encoded else {
        return Err(SchemaFormatError::MalformedValue);
    };
    let mut required: Vec<String> = Vec::new();
    for name in names {
        let DocumentValue::String(name) = name else {
            return Err(SchemaFormatError::MalformedValue);
        };
        validate_field_name(name).map_err(|_| SchemaFormatError::MalformedValue)?;
        if required.contains(name) {
            return Err(SchemaFormatError::MalformedValue);
        }
        required.push(name.clone());
    }
    Ok(required)
}

/// Parses the subset's `properties` keyword into per-field rule lists.
///
/// Rules are collected per field in a canonical map — field order, each
/// field's rules in kind order — which is exactly the canonical rule order
/// of the typed model once flattened.
fn parse_properties(
    encoded: Option<&DocumentValue>,
) -> Result<BTreeMap<String, Vec<SchemaRule>>, SchemaFormatError> {
    let Some(encoded) = encoded else {
        return Ok(BTreeMap::new());
    };
    let DocumentValue::Object(properties) = encoded else {
        return Err(SchemaFormatError::NotAnObject);
    };
    let mut per_field: BTreeMap<String, Vec<SchemaRule>> = BTreeMap::new();
    for (field_name, subschema) in properties {
        validate_field_name(field_name).map_err(|_| SchemaFormatError::MalformedValue)?;
        let DocumentValue::Object(keywords) = subschema else {
            return Err(SchemaFormatError::NotAnObject);
        };
        per_field.insert(field_name.clone(), parse_property(field_name, keywords)?);
    }
    Ok(per_field)
}

/// Parses one property subschema into its field's rules, in kind order.
///
/// A bound keyword is only accepted where it can bind: a `minimum`/`maximum`
/// pair needs the `integer` or `number` type, and a `minLength`/`maxLength`
/// pair needs the `string` type. On any other declared type the evaluator
/// could never fire the rule, so the import fails closed instead of staging
/// a rule that silently does nothing. A bound without a declared type stays
/// accepted: it binds whichever numeric or text value the field carries.
fn parse_property(
    field_name: &str,
    keywords: &BTreeMap<String, DocumentValue>,
) -> Result<Vec<SchemaRule>, SchemaFormatError> {
    use super::SchemaFieldType as FieldType;

    for key in keywords.keys() {
        match key.as_str() {
            "type" | "enum" | "minimum" | "maximum" | "minLength" | "maxLength" => {}
            _ => return Err(SchemaFormatError::UnsupportedKeyword),
        }
    }
    let field = field_name.to_owned();
    let mut rules: Vec<SchemaRule> = Vec::new();
    let declared_type = match keywords.get("type") {
        None => None,
        Some(DocumentValue::String(kind)) => {
            let Some(kind) = parse_json_type(kind) else {
                return Err(SchemaFormatError::UnsupportedType);
            };
            rules.push(SchemaRule::FieldType { field: field.clone(), kind });
            Some(kind)
        }
        _ => return Err(SchemaFormatError::MalformedValue),
    };
    match keywords.get("enum") {
        None => {}
        Some(DocumentValue::Array(values)) => {
            if values.is_empty() || values.len() > MAX_ENUM_VALUES {
                return Err(SchemaFormatError::MalformedValue);
            }
            let values = values
                .iter()
                .map(|item| match item {
                    DocumentValue::String(value) => Some(value.clone()),
                    _ => None,
                })
                .collect::<Option<Vec<_>>>()
                .ok_or(SchemaFormatError::MalformedValue)?;
            if has_duplicate(&values) {
                return Err(SchemaFormatError::MalformedValue);
            }
            rules.push(SchemaRule::EnumValues { field: field.clone(), values });
        }
        _ => return Err(SchemaFormatError::MalformedValue),
    }
    let integer_keyword = |key: &str| -> Result<Option<i64>, SchemaFormatError> {
        match keywords.get(key) {
            None => Ok(None),
            Some(DocumentValue::Integer(value)) => Ok(Some(*value)),
            _ => Err(SchemaFormatError::MalformedValue),
        }
    };
    let minimum = integer_keyword("minimum")?;
    let maximum = integer_keyword("maximum")?;
    if minimum.is_some() || maximum.is_some() {
        if declared_type.is_some_and(|kind| !matches!(kind, FieldType::Integer | FieldType::Number))
        {
            return Err(SchemaFormatError::MalformedValue);
        }
        rules.push(SchemaRule::NumberRange { field: field.clone(), min: minimum, max: maximum });
    }
    let length = |key: &str| -> Result<Option<u64>, SchemaFormatError> {
        match integer_keyword(key)? {
            None => Ok(None),
            Some(value) if value >= 0 => Ok(Some(u64::try_from(value).unwrap_or(0))),
            Some(_) => Err(SchemaFormatError::MalformedValue),
        }
    };
    let min_length = length("minLength")?;
    let max_length = length("maxLength")?;
    if min_length.is_some() || max_length.is_some() {
        if declared_type.is_some_and(|kind| kind != FieldType::Text) {
            return Err(SchemaFormatError::MalformedValue);
        }
        rules.push(SchemaRule::TextLength { field, min: min_length, max: max_length });
    }
    Ok(rules)
}
