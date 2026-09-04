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
#[must_use]
pub fn violations(schema: &SchemaDefinition, value: &DocumentValue) -> Vec<SchemaViolation> {
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

// ---------------------------------------------------------------------------
// Canonical terrascale.schema.json contract (D-48/D-78)
// ---------------------------------------------------------------------------

/// The field type vocabulary emitted by every SDK's schema builder.
///
/// This is intentionally separate from [`SchemaFieldType`].  The latter is
/// the small validation-rule vocabulary used by the legacy family record;
/// this enum is the complete public schema contract, including extracted
/// document and CRDT field types.  Keeping the two types distinct prevents a
/// JSON schema import from silently dropping metadata that the engine cannot
/// enforce yet.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum CanonicalFieldType {
    /// UTF-8 scalar text.
    String,
    /// Signed integer.
    Int,
    /// IEEE-754 number.
    Float,
    /// Boolean.
    Bool,
    /// Opaque bytes.
    Bytes,
    /// Arbitrary JSON value.
    Json,
    /// Epoch-millisecond timestamp.
    Timestamp,
    /// Full-text searchable text.
    Text,
    /// PN-counter CRDT field.
    CounterPn,
    /// Add-wins set CRDT field.
    SetAddWins,
    /// Remove-wins set CRDT field.
    SetRemoveWins,
    /// Multi-value register CRDT field.
    RegisterMv,
}

impl CanonicalFieldType {
    /// Returns the canonical JSON spelling.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::String => "string",
            Self::Int => "int",
            Self::Float => "float",
            Self::Bool => "bool",
            Self::Bytes => "bytes",
            Self::Json => "json",
            Self::Timestamp => "timestamp",
            Self::Text => "text",
            Self::CounterPn => "counter-pn",
            Self::SetAddWins => "set-add-wins",
            Self::SetRemoveWins => "set-remove-wins",
            Self::RegisterMv => "register-mv",
        }
    }

    /// Parses one canonical field type spelling; aliases are not accepted.
    #[allow(
        clippy::should_implement_trait,
        reason = "the schema keeps an Option-based spelling without an Error type"
    )]
    #[must_use]
    pub fn from_str(value: &str) -> Option<Self> {
        Some(match value {
            "string" => Self::String,
            "int" => Self::Int,
            "float" => Self::Float,
            "bool" => Self::Bool,
            "bytes" => Self::Bytes,
            "json" => Self::Json,
            "timestamp" => Self::Timestamp,
            "text" => Self::Text,
            "counter-pn" => Self::CounterPn,
            "set-add-wins" => Self::SetAddWins,
            "set-remove-wins" => Self::SetRemoveWins,
            "register-mv" => Self::RegisterMv,
            _ => return None,
        })
    }

    fn is_crdt(self) -> bool {
        matches!(self, Self::CounterPn | Self::SetAddWins | Self::SetRemoveWins | Self::RegisterMv)
    }
}

/// The collection-level conflict policy in the canonical schema contract.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum SchemaMergePolicy {
    /// Removes win over concurrent updates.
    RemoveWins,
    /// HLC is used as the deterministic concurrent tiebreak.
    HlcTiebreak,
    /// Last write wins according to the declared write clock.
    LastWriteWins,
}

impl SchemaMergePolicy {
    /// Returns the canonical JSON spelling.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::RemoveWins => "remove-wins",
            Self::HlcTiebreak => "hlc-tiebreak",
            Self::LastWriteWins => "last-write-wins",
        }
    }

    /// Parses one canonical merge-policy spelling.
    #[allow(
        clippy::should_implement_trait,
        reason = "the schema keeps an Option-based spelling without an Error type"
    )]
    #[must_use]
    pub fn from_str(value: &str) -> Option<Self> {
        Some(match value {
            "remove-wins" => Self::RemoveWins,
            "hlc-tiebreak" => Self::HlcTiebreak,
            "last-write-wins" => Self::LastWriteWins,
            _ => return None,
        })
    }
}

/// The physical kind of one declared index.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum CanonicalIndexKind {
    /// A `SQLite` B-tree over extracted columns.
    BTree,
    /// An `SQLite` FTS5 index over one text field.
    Fts,
}

impl CanonicalIndexKind {
    /// The canonical B-tree index kind.
    pub const BTREE: Self = Self::BTree;
    /// The canonical full-text index kind.
    pub const FTS: Self = Self::Fts;

    /// Returns the canonical JSON spelling.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::BTree => "btree",
            Self::Fts => "fts",
        }
    }

    /// Parses one canonical index-kind spelling.
    #[allow(
        clippy::should_implement_trait,
        reason = "the schema keeps an Option-based spelling without an Error type"
    )]
    #[must_use]
    pub fn from_str(value: &str) -> Option<Self> {
        Some(match value {
            "btree" => Self::BTree,
            "fts" => Self::Fts,
            _ => return None,
        })
    }
}

/// An explicit field rename in a schema evolution.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaFieldRename {
    from: String,
    to: String,
}

impl SchemaFieldRename {
    /// Creates a validated rename.  A rename is never inferred from a diff.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when either name is not
    /// a canonical identifier or both names match.
    pub fn try_new(
        from: impl Into<String>,
        to: impl Into<String>,
    ) -> Result<Self, SchemaFormatError> {
        let value = Self { from: from.into(), to: to.into() };
        validate_canonical_identifier(&value.from)?;
        validate_canonical_identifier(&value.to)?;
        if value.from == value.to {
            return Err(SchemaFormatError::MalformedValue);
        }
        Ok(value)
    }

    /// Returns the source field name.
    #[must_use]
    pub fn from(&self) -> &str {
        &self.from
    }

    /// Returns the destination field name.
    #[must_use]
    pub fn to(&self) -> &str {
        &self.to
    }
}

/// One extracted field in a canonical collection schema.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CanonicalField {
    name: String,
    kind: CanonicalFieldType,
    required: bool,
    indexed: bool,
    unique: bool,
    merge_policy: Option<CanonicalFieldType>,
}

impl CanonicalField {
    /// Creates one validated field definition.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when the name is not a
    /// canonical identifier or the merge policy is not a CRDT kind.
    pub fn try_new(
        name: impl Into<String>,
        kind: CanonicalFieldType,
        required: bool,
        indexed: bool,
        unique: bool,
        merge_policy: Option<CanonicalFieldType>,
    ) -> Result<Self, SchemaFormatError> {
        let value = Self {
            name: name.into(),
            kind,
            required,
            indexed: indexed || unique,
            unique,
            merge_policy,
        };
        validate_canonical_identifier(&value.name)?;
        if merge_policy.is_some_and(|policy| !policy.is_crdt()) {
            return Err(SchemaFormatError::MalformedValue);
        }
        Ok(value)
    }

    /// Returns the field name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }
    /// Returns the field type.
    #[must_use]
    pub const fn kind(&self) -> CanonicalFieldType {
        self.kind
    }
    /// Returns whether the field is required.
    #[must_use]
    pub const fn required(&self) -> bool {
        self.required
    }
    /// Returns whether the field has a value index.
    #[must_use]
    pub const fn indexed(&self) -> bool {
        self.indexed
    }
    /// Returns whether the field's index is unique.
    #[must_use]
    pub const fn unique(&self) -> bool {
        self.unique
    }
    /// Returns the optional typed CRDT field policy.
    #[must_use]
    pub const fn merge_policy(&self) -> Option<CanonicalFieldType> {
        self.merge_policy
    }
}

/// One named index in a canonical collection schema.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CanonicalIndex {
    name: String,
    fields: Vec<String>,
    unique: bool,
    kind: CanonicalIndexKind,
}

impl CanonicalIndex {
    /// Creates a non-unique B-tree index.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when the name or fields
    /// are not canonical identifiers.
    pub fn btree(name: impl Into<String>, fields: Vec<String>) -> Result<Self, SchemaFormatError> {
        Self::try_new(name, fields, false, CanonicalIndexKind::BTree)
    }

    /// Creates a single-field FTS5 index.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when the name, field, or
    /// field count is invalid for an FTS5 index.
    pub fn fts(
        name: impl Into<String>,
        field: impl Into<String>,
    ) -> Result<Self, SchemaFormatError> {
        Self::try_new(name, vec![field.into()], false, CanonicalIndexKind::Fts)
    }

    /// Creates one validated index.  The kind is always explicit at this seam.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when the name or fields
    /// are not canonical identifiers, or when an FTS index does not declare
    /// exactly one field.
    pub fn try_new(
        name: impl Into<String>,
        fields: Vec<String>,
        unique: bool,
        kind: CanonicalIndexKind,
    ) -> Result<Self, SchemaFormatError> {
        let value = Self { name: name.into(), fields, unique, kind };
        validate_canonical_identifier(&value.name)?;
        if value.fields.is_empty() || (kind == CanonicalIndexKind::Fts && value.fields.len() != 1) {
            return Err(SchemaFormatError::MalformedValue);
        }
        for field in &value.fields {
            validate_canonical_identifier(field)?;
        }
        Ok(value)
    }

    /// Returns the index name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }
    /// Returns indexed field names.
    #[must_use]
    pub fn fields(&self) -> &[String] {
        &self.fields
    }
    /// Returns whether this is unique.
    #[must_use]
    pub const fn unique(&self) -> bool {
        self.unique
    }
    /// Returns the physical index kind.
    #[must_use]
    pub const fn kind(&self) -> CanonicalIndexKind {
        self.kind
    }
}

/// Optional collection TTL declaration.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaTtl {
    field: String,
}

impl SchemaTtl {
    /// Creates a TTL declaration for one timestamp field.
    pub fn try_new(field: impl Into<String>) -> Result<Self, SchemaFormatError> {
        let value = Self { field: field.into() };
        validate_canonical_identifier(&value.field)?;
        Ok(value)
    }
    /// Returns the expiration field.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }
}

/// One collection's canonical schema contract.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CanonicalCollectionSchema {
    name: String,
    fields: Vec<CanonicalField>,
    indexes: Vec<CanonicalIndex>,
    ttl: Option<SchemaTtl>,
    renames: Vec<SchemaFieldRename>,
    version: Option<SchemaVersion>,
    history_retention_days: u32,
    merge_policy: SchemaMergePolicy,
}

impl CanonicalCollectionSchema {
    /// Creates and validates one canonical collection schema.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when the collection,
    /// fields, indexes, TTL, renames, or retention policy fail validation.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        name: impl Into<String>,
        fields: Vec<CanonicalField>,
        indexes: Vec<CanonicalIndex>,
        ttl: Option<SchemaTtl>,
        renames: Vec<SchemaFieldRename>,
        version: Option<SchemaVersion>,
        history_retention_days: u32,
        merge_policy: SchemaMergePolicy,
    ) -> Result<Self, SchemaFormatError> {
        let value = Self {
            name: name.into(),
            fields,
            indexes,
            ttl,
            renames,
            version,
            history_retention_days,
            merge_policy,
        };
        value.validate()?;
        Ok(value.canonicalized())
    }

    fn canonicalized(mut self) -> Self {
        self.fields.sort_by(|left, right| left.name.cmp(&right.name));
        self.indexes.sort_by(|left, right| left.name.cmp(&right.name));
        self.renames.sort_by(|left, right| left.from.cmp(&right.from).then(left.to.cmp(&right.to)));
        self
    }

    /// Validates references and the bounded retention policy.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when a name is invalid,
    /// a reference dangles, or the retention policy leaves the 1-30 day
    /// window.
    pub fn validate(&self) -> Result<(), SchemaFormatError> {
        validate_canonical_identifier(&self.name)?;
        if !(1..=30).contains(&self.history_retention_days) {
            return Err(SchemaFormatError::MalformedValue);
        }
        let mut fields = BTreeSet::new();
        for field in &self.fields {
            if !fields.insert(field.name.as_str()) {
                return Err(SchemaFormatError::MalformedValue);
            }
        }
        let mut indexes = BTreeSet::new();
        for index in &self.indexes {
            if !indexes.insert(index.name.as_str()) {
                return Err(SchemaFormatError::MalformedValue);
            }
            for field in &index.fields {
                if !fields.contains(field.as_str()) {
                    return Err(SchemaFormatError::MalformedValue);
                }
                if index.kind == CanonicalIndexKind::Fts
                    && !matches!(
                        self.fields
                            .iter()
                            .find(|candidate| candidate.name == *field)
                            .map(CanonicalField::kind),
                        Some(CanonicalFieldType::Text | CanonicalFieldType::String)
                    )
                {
                    return Err(SchemaFormatError::MalformedValue);
                }
            }
        }
        if let Some(ttl) = &self.ttl
            && (!fields.contains(ttl.field.as_str())
                || self
                    .fields
                    .iter()
                    .find(|field| field.name == ttl.field)
                    .map(CanonicalField::kind)
                    != Some(CanonicalFieldType::Timestamp))
        {
            return Err(SchemaFormatError::MalformedValue);
        }
        let mut sources = BTreeSet::new();
        let mut targets = BTreeSet::new();
        for rename in &self.renames {
            if !sources.insert(rename.from.as_str())
                || !targets.insert(rename.to.as_str())
                || fields.contains(rename.from.as_str())
                || !fields.contains(rename.to.as_str())
            {
                return Err(SchemaFormatError::MalformedValue);
            }
        }
        Ok(())
    }

    /// Returns the collection name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }
    /// Returns canonical fields.
    #[must_use]
    pub fn fields(&self) -> &[CanonicalField] {
        &self.fields
    }
    /// Returns canonical indexes.
    #[must_use]
    pub fn indexes(&self) -> &[CanonicalIndex] {
        &self.indexes
    }
    /// Returns the optional TTL declaration.
    #[must_use]
    pub fn ttl(&self) -> Option<&SchemaTtl> {
        self.ttl.as_ref()
    }
    /// Returns explicit field renames.
    #[must_use]
    pub fn renames(&self) -> &[SchemaFieldRename] {
        &self.renames
    }
    /// Returns the optional collection schema version.
    #[must_use]
    pub const fn version(&self) -> Option<SchemaVersion> {
        self.version
    }
    /// Returns the history retention window in days.
    #[must_use]
    pub const fn history_retention_days(&self) -> u32 {
        self.history_retention_days
    }
    /// Returns the collection merge policy.
    #[must_use]
    pub const fn merge_policy(&self) -> SchemaMergePolicy {
        self.merge_policy
    }
}

/// The complete canonical `terrascale.schema.json` document.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CanonicalSchemaDocument {
    collections: Vec<CanonicalCollectionSchema>,
    version: Option<SchemaVersion>,
}

impl CanonicalSchemaDocument {
    /// Creates one canonical document and sorts collections by name.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] when a collection name
    /// repeats or a nested collection fails validation.
    pub fn try_new(
        collections: Vec<CanonicalCollectionSchema>,
        version: Option<SchemaVersion>,
    ) -> Result<Self, SchemaFormatError> {
        let mut names = BTreeSet::new();
        for collection in &collections {
            collection.validate()?;
            if !names.insert(collection.name.as_str()) {
                return Err(SchemaFormatError::MalformedValue);
            }
        }
        let mut value = Self { collections, version };
        value.collections.sort_by(|left, right| left.name.cmp(&right.name));
        Ok(value)
    }

    /// Returns collections in canonical name order.
    #[must_use]
    pub fn collections(&self) -> &[CanonicalCollectionSchema] {
        &self.collections
    }
    /// Returns the optional document format version.
    #[must_use]
    pub const fn version(&self) -> Option<SchemaVersion> {
        self.version
    }

    /// Converts this contract to a deterministic JSON-shaped value.
    #[must_use]
    pub fn to_value(&self) -> DocumentValue {
        let collections = self.collections.iter().map(collection_to_value).collect();
        let mut root = vec![("collections", DocumentValue::Array(collections))];
        if let Some(version) = self.version {
            root.push(("version", DocumentValue::Integer(interchange_integer(version.get()))));
        }
        object(root)
    }

    /// Alias with an explicit name for callers crossing the JSON contract
    /// boundary.  The returned value remains the engine's typed JSON value;
    /// no second byte representation is introduced here.
    #[must_use]
    pub fn to_json_value(&self) -> DocumentValue {
        self.to_value()
    }

    /// Serializes the canonical contract as stable UTF-8 JSON.
    ///
    /// This small serializer is kept at the family boundary so SDK and host
    /// adapters do not each invent a schema encoding. It rejects non-finite
    /// numbers if this method is ever reused with a future value extension.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] if the canonical value
    /// contains a value that cannot be represented by JSON.
    pub fn to_json(&self) -> Result<String, SchemaFormatError> {
        self.try_to_json()
    }

    /// Fallible serializer retained as an explicit spelling for generic
    /// adapter code.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::MalformedValue`] if a value cannot be
    /// represented by JSON.
    pub fn try_to_json(&self) -> Result<String, SchemaFormatError> {
        let mut output = String::new();
        append_canonical_json(&self.to_value(), &mut output)?;
        Ok(output)
    }

    /// Parses exactly the canonical JSON-shaped value. Unknown keys and all
    /// former aliases (`merge`, `history`, `field_type`, and so on) fail closed.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaFormatError::NotAnObject`],
    /// [`SchemaFormatError::UnsupportedKeyword`], or
    /// [`SchemaFormatError::MalformedValue`] when the value is outside the
    /// canonical contract.
    pub fn from_value(value: &DocumentValue) -> Result<Self, SchemaFormatError> {
        let DocumentValue::Object(root) = value else {
            return Err(SchemaFormatError::NotAnObject);
        };
        reject_unknown(root, &["collections", "version"])?;
        let collections = match root.get("collections") {
            Some(DocumentValue::Array(values)) => {
                values.iter().map(collection_from_value).collect::<Result<Vec<_>, _>>()?
            }
            _ => return Err(SchemaFormatError::MalformedValue),
        };
        let version = parse_optional_version(root.get("version"))?;
        Self::try_new(collections, version)
    }

    /// Parses one canonical JSON-shaped engine value.
    ///
    /// # Errors
    ///
    /// Returns the same typed [`SchemaFormatError`] cases as
    /// [`Self::from_value`].
    pub fn from_json_value(value: &DocumentValue) -> Result<Self, SchemaFormatError> {
        Self::from_value(value)
    }
}

fn validate_canonical_identifier(value: &str) -> Result<(), SchemaFormatError> {
    if value.is_empty()
        || value.len() > 255
        || !value.bytes().enumerate().all(|(position, byte)| {
            if position == 0 {
                byte == b'_' || byte.is_ascii_alphabetic()
            } else {
                byte == b'_' || byte.is_ascii_alphanumeric()
            }
        })
    {
        Err(SchemaFormatError::MalformedValue)
    } else {
        Ok(())
    }
}

fn append_canonical_json(
    value: &DocumentValue,
    output: &mut String,
) -> Result<(), SchemaFormatError> {
    match value {
        DocumentValue::Null => output.push_str("null"),
        DocumentValue::Boolean(value) => output.push_str(if *value { "true" } else { "false" }),
        DocumentValue::Integer(value) => output.push_str(&value.to_string()),
        DocumentValue::Number(value) if value.is_finite() => output.push_str(&value.to_string()),
        DocumentValue::Number(_) => return Err(SchemaFormatError::MalformedValue),
        DocumentValue::String(value) => append_json_string(value, output),
        DocumentValue::Array(values) => {
            output.push('[');
            for (position, value) in values.iter().enumerate() {
                if position != 0 {
                    output.push(',');
                }
                append_canonical_json(value, output)?;
            }
            output.push(']');
        }
        DocumentValue::Object(values) => {
            output.push('{');
            for (position, (key, value)) in values.iter().enumerate() {
                if position != 0 {
                    output.push(',');
                }
                append_json_string(key, output);
                output.push(':');
                append_canonical_json(value, output)?;
            }
            output.push('}');
        }
    }
    Ok(())
}

fn append_json_string(value: &str, output: &mut String) {
    output.push('"');
    for character in value.chars() {
        match character {
            '"' => output.push_str("\\\""),
            '\\' => output.push_str("\\\\"),
            '\n' => output.push_str("\\n"),
            '\r' => output.push_str("\\r"),
            '\t' => output.push_str("\\t"),
            character if character.is_control() => {
                use std::fmt::Write;
                let _ = write!(output, "\\u{:04x}", character as u32);
            }
            character => output.push(character),
        }
    }
    output.push('"');
}

fn reject_unknown(
    fields: &BTreeMap<String, DocumentValue>,
    allowed: &[&str],
) -> Result<(), SchemaFormatError> {
    if fields.keys().all(|key| allowed.contains(&key.as_str())) {
        Ok(())
    } else {
        Err(SchemaFormatError::UnsupportedKeyword)
    }
}

fn parse_optional_version(
    value: Option<&DocumentValue>,
) -> Result<Option<SchemaVersion>, SchemaFormatError> {
    match value {
        None => Ok(None),
        Some(DocumentValue::Integer(value)) if *value > 0 => SchemaVersion::try_new(
            u64::try_from(*value).map_err(|_| SchemaFormatError::MalformedValue)?,
        )
        .map(Some)
        .map_err(|_| SchemaFormatError::MalformedValue),
        _ => Err(SchemaFormatError::MalformedValue),
    }
}

fn field_to_value(field: &CanonicalField) -> DocumentValue {
    let mut result = vec![
        ("name", DocumentValue::String(field.name.clone())),
        ("type", DocumentValue::String(field.kind.as_str().to_owned())),
        ("required", DocumentValue::Boolean(field.required)),
        ("indexed", DocumentValue::Boolean(field.indexed)),
    ];
    if field.unique {
        result.push(("unique", DocumentValue::Boolean(true)));
    }
    if let Some(policy) = field.merge_policy {
        result.push(("mergePolicy", DocumentValue::String(policy.as_str().to_owned())));
    }
    object(result)
}

fn index_to_value(index: &CanonicalIndex) -> DocumentValue {
    let mut result = vec![
        ("name", DocumentValue::String(index.name.clone())),
        (
            "fields",
            DocumentValue::Array(index.fields.iter().cloned().map(DocumentValue::String).collect()),
        ),
        ("kind", DocumentValue::String(index.kind.as_str().to_owned())),
    ];
    if index.unique {
        result.push(("unique", DocumentValue::Boolean(true)));
    }
    object(result)
}

fn collection_to_value(collection: &CanonicalCollectionSchema) -> DocumentValue {
    let mut result = vec![
        ("name", DocumentValue::String(collection.name.clone())),
        ("fields", DocumentValue::Array(collection.fields.iter().map(field_to_value).collect())),
        (
            "historyRetentionDays",
            DocumentValue::Integer(i64::from(collection.history_retention_days)),
        ),
        ("mergePolicy", DocumentValue::String(collection.merge_policy.as_str().to_owned())),
    ];
    if !collection.indexes.is_empty() {
        result.insert(
            2,
            (
                "indexes",
                DocumentValue::Array(collection.indexes.iter().map(index_to_value).collect()),
            ),
        );
    }
    if let Some(ttl) = &collection.ttl {
        result.push(("ttl", object(vec![("field", DocumentValue::String(ttl.field.clone()))])));
    }
    if !collection.renames.is_empty() {
        result.push((
            "renames",
            DocumentValue::Array(
                collection
                    .renames
                    .iter()
                    .map(|rename| {
                        object(vec![
                            ("from", DocumentValue::String(rename.from.clone())),
                            ("to", DocumentValue::String(rename.to.clone())),
                        ])
                    })
                    .collect(),
            ),
        ));
    }
    if let Some(version) = collection.version {
        result.push(("version", DocumentValue::Integer(interchange_integer(version.get()))));
    }
    object(result)
}

fn string_field(
    fields: &BTreeMap<String, DocumentValue>,
    key: &str,
) -> Result<String, SchemaFormatError> {
    match fields.get(key) {
        Some(DocumentValue::String(value)) => Ok(value.clone()),
        _ => Err(SchemaFormatError::MalformedValue),
    }
}

fn bool_field(
    fields: &BTreeMap<String, DocumentValue>,
    key: &str,
    default: bool,
) -> Result<bool, SchemaFormatError> {
    match fields.get(key) {
        None => Ok(default),
        Some(DocumentValue::Boolean(value)) => Ok(*value),
        _ => Err(SchemaFormatError::MalformedValue),
    }
}

#[allow(
    clippy::too_many_lines,
    reason = "the collection decoder reads every contract field in order"
)]
fn collection_from_value(
    value: &DocumentValue,
) -> Result<CanonicalCollectionSchema, SchemaFormatError> {
    let DocumentValue::Object(fields) = value else {
        return Err(SchemaFormatError::NotAnObject);
    };
    reject_unknown(
        fields,
        &[
            "name",
            "fields",
            "indexes",
            "ttl",
            "renames",
            "version",
            "historyRetentionDays",
            "mergePolicy",
        ],
    )?;
    let name = string_field(fields, "name")?;
    let Some(DocumentValue::Array(field_values)) = fields.get("fields") else {
        return Err(SchemaFormatError::MalformedValue);
    };
    let mut definitions = Vec::new();
    for value in field_values {
        let DocumentValue::Object(fields) = value else {
            return Err(SchemaFormatError::NotAnObject);
        };
        reject_unknown(fields, &["name", "type", "required", "indexed", "unique", "mergePolicy"])?;
        let kind = CanonicalFieldType::from_str(&string_field(fields, "type")?)
            .ok_or(SchemaFormatError::MalformedValue)?;
        let merge_policy = match fields.get("mergePolicy") {
            None => None,
            Some(DocumentValue::String(value)) => {
                Some(CanonicalFieldType::from_str(value).ok_or(SchemaFormatError::MalformedValue)?)
            }
            _ => return Err(SchemaFormatError::MalformedValue),
        };
        definitions.push(CanonicalField::try_new(
            string_field(fields, "name")?,
            kind,
            bool_field(fields, "required", true)?,
            bool_field(fields, "indexed", false)?,
            bool_field(fields, "unique", false)?,
            merge_policy,
        )?);
    }
    let mut indexes = Vec::new();
    if let Some(DocumentValue::Array(values)) = fields.get("indexes") {
        for value in values {
            let DocumentValue::Object(fields) = value else {
                return Err(SchemaFormatError::NotAnObject);
            };
            reject_unknown(fields, &["name", "fields", "unique", "kind"])?;
            let names = match fields.get("fields") {
                Some(DocumentValue::Array(values)) => values
                    .iter()
                    .map(|value| match value {
                        DocumentValue::String(value) => Ok(value.clone()),
                        _ => Err(SchemaFormatError::MalformedValue),
                    })
                    .collect::<Result<Vec<_>, _>>()?,
                _ => return Err(SchemaFormatError::MalformedValue),
            };
            let kind = CanonicalIndexKind::from_str(&string_field(fields, "kind")?)
                .ok_or(SchemaFormatError::MalformedValue)?;
            indexes.push(CanonicalIndex::try_new(
                string_field(fields, "name")?,
                names,
                bool_field(fields, "unique", false)?,
                kind,
            )?);
        }
    } else if fields.contains_key("indexes") {
        return Err(SchemaFormatError::MalformedValue);
    }
    let ttl = match fields.get("ttl") {
        None => None,
        Some(DocumentValue::Object(value)) => {
            Some(SchemaTtl::try_new(string_field(value, "field")?)?)
        }
        _ => return Err(SchemaFormatError::MalformedValue),
    };
    let renames = match fields.get("renames") {
        None => Vec::new(),
        Some(DocumentValue::Array(values)) => values
            .iter()
            .map(|value| {
                let DocumentValue::Object(fields) = value else {
                    return Err(SchemaFormatError::NotAnObject);
                };
                reject_unknown(fields, &["from", "to"])?;
                SchemaFieldRename::try_new(
                    string_field(fields, "from")?,
                    string_field(fields, "to")?,
                )
            })
            .collect::<Result<Vec<_>, _>>()?,
        _ => return Err(SchemaFormatError::MalformedValue),
    };
    let retention = match fields.get("historyRetentionDays") {
        Some(DocumentValue::Integer(value)) if *value >= 1 => {
            u32::try_from(*value).map_err(|_| SchemaFormatError::MalformedValue)?
        }
        _ => return Err(SchemaFormatError::MalformedValue),
    };
    let merge_policy = SchemaMergePolicy::from_str(&string_field(fields, "mergePolicy")?)
        .ok_or(SchemaFormatError::MalformedValue)?;
    CanonicalCollectionSchema::try_new(
        name,
        definitions,
        indexes,
        ttl,
        renames,
        parse_optional_version(fields.get("version"))?,
        retention,
        merge_policy,
    )
}

#[allow(
    clippy::items_after_test_module,
    reason = "published SDK aliases stay at the file end by contract"
)]
#[cfg(test)]
mod canonical_tests {
    use super::*;

    fn field(name: &str, kind: CanonicalFieldType) -> CanonicalField {
        CanonicalField::try_new(name, kind, true, false, false, None).expect("valid field")
    }

    #[test]
    fn canonical_contract_roundtrips_and_sorts() {
        let collection = CanonicalCollectionSchema::try_new(
            "events",
            vec![
                field("title", CanonicalFieldType::Text),
                field("expiresAt", CanonicalFieldType::Timestamp),
            ],
            vec![
                CanonicalIndex::try_new(
                    "title_search",
                    vec!["title".into()],
                    false,
                    CanonicalIndexKind::Fts,
                )
                .expect("valid fts"),
            ],
            Some(SchemaTtl::try_new("expiresAt").expect("valid ttl")),
            vec![],
            Some(SchemaVersion::try_new(2).expect("valid version")),
            7,
            SchemaMergePolicy::RemoveWins,
        )
        .expect("valid collection");
        let document =
            CanonicalSchemaDocument::try_new(vec![collection], None).expect("valid document");
        let encoded = document.to_json_value();
        assert_eq!(
            CanonicalSchemaDocument::from_json_value(&encoded).expect("roundtrip"),
            document
        );
    }

    #[test]
    fn canonical_contract_rejects_aliases_and_invalid_references() {
        let value = object(vec![(
            "collections",
            DocumentValue::Array(vec![object(vec![
                ("name", DocumentValue::String("events".into())),
                ("fields", DocumentValue::Array(vec![])),
                ("history", DocumentValue::Integer(7)),
                ("historyRetentionDays", DocumentValue::Integer(7)),
                ("mergePolicy", DocumentValue::String("remove-wins".into())),
            ])]),
        )]);
        assert_eq!(
            CanonicalSchemaDocument::from_json_value(&value),
            Err(SchemaFormatError::UnsupportedKeyword)
        );
        assert_eq!(
            CanonicalIndex::try_new(
                "bad",
                vec!["missing".into()],
                false,
                CanonicalIndexKind::BTree
            ),
            Ok(CanonicalIndex {
                name: "bad".into(),
                fields: vec!["missing".into()],
                unique: false,
                kind: CanonicalIndexKind::BTree
            })
        );
    }

    #[test]
    fn canonical_contract_rejects_rename_collisions() {
        let collection = CanonicalCollectionSchema::try_new(
            "events",
            vec![field("new_name", CanonicalFieldType::String)],
            vec![],
            None,
            vec![
                SchemaFieldRename::try_new("old_name", "new_name").expect("valid rename"),
                SchemaFieldRename::try_new("other", "new_name").expect("valid rename"),
            ],
            None,
            7,
            SchemaMergePolicy::RemoveWins,
        );
        assert_eq!(collection, Err(SchemaFormatError::MalformedValue));
    }
}

// These are the names used in the published SDK contract.  They are aliases
// for the deliberately verbose Rust implementation names above, not extra
// wire spellings or a second schema model.
/// Published SDK alias for the canonical schema document.
pub type SchemaDocument = CanonicalSchemaDocument;
/// Published SDK alias for one collection's canonical schema.
pub type CollectionSchema = CanonicalCollectionSchema;
/// Published SDK alias for one extracted field.
pub type FieldDef = CanonicalField;
/// Published SDK alias for the canonical field type vocabulary.
pub type FieldType = CanonicalFieldType;
/// Published SDK alias for one named index.
pub type IndexDef = CanonicalIndex;
/// Published SDK alias for the physical index kind.
pub type IndexKind = CanonicalIndexKind;
/// Published SDK alias for an explicit field rename.
pub type FieldRename = SchemaFieldRename;
/// Published SDK alias for the collection-level conflict policy.
pub type MergePolicy = SchemaMergePolicy;
