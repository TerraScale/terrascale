//! Schema-driven code generation for the `TerraBase` Engine Protocol.
//!
//! The one source of truth for TBEP is `schema/tbep-registry.json` (schema
//! identity `terrabase.engine.protocol/registry@1`). The `tbep-gen`
//! binary regenerates the committed bindings on both sides from it:
//!
//! - Rust: `src/generated/registry.rs` (consumed by this crate through
//!   `src/generated.rs`);
//! - C#: `host/TerraBase.Protocol/Generated/TbepRegistry.g.cs` (the C#
//!   protocol client project).
//!
//! The drift contract: the committed generated files must equal exactly
//! what regeneration produces. `tests/registry_drift.rs` re-runs this
//! module against the committed files and fails on any difference, so a
//! schema edit without regeneration cannot land quietly. The same test
//! validates the operation registry's parity-mapping column against
//! `parity/inventory.json`: every `PB-*` reference must name an existing
//! V1 parity row, and every operation carries either such references or
//! an explicit `new-capability` marker (PD-2026-08-26-14 D1, the GAP-A1
//! fold).
//!
//! The generator is deterministic: identical schema input produces
//! byte-identical output on every run and platform.

use std::fmt::Write as _;

use serde_json::Value;

/// The generated Rust bindings file, relative to the crate root.
pub const RUST_OUTPUT: &str = "src/generated/registry.rs";
/// The generated C# bindings file, relative to the repository root.
pub const CSHARP_OUTPUT: &str = "host/TerraBase.Protocol/Generated/TbepRegistry.g.cs";

/// A schema validation failure.
#[derive(Debug)]
pub struct SchemaError(String);

impl std::fmt::Display for SchemaError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "tbep schema error: {}", self.0)
    }
}

impl std::error::Error for SchemaError {}

fn err(message: impl Into<String>) -> SchemaError {
    SchemaError(message.into())
}

/// A field descriptor from the schema.
#[derive(Clone, Debug)]
struct FieldDef {
    name: String,
    type_name: String,
    bound: Option<u64>,
    optional: bool,
    one_of: Option<String>,
}

impl FieldDef {
    fn from_json(value: &Value) -> Result<Self, SchemaError> {
        let object = value.as_object().ok_or_else(|| err("a field must be an object"))?;
        Ok(Self {
            name: string_field(object, "name")?,
            type_name: string_field(object, "type")?,
            bound: object.get("bound").and_then(Value::as_u64),
            optional: object.get("optional").and_then(Value::as_bool).unwrap_or(false),
            one_of: object.get("one_of").and_then(Value::as_str).map(str::to_string),
        })
    }

    /// The Rust type of the stored field.
    fn rust_type(&self) -> String {
        let base = rust_type_name(&self.type_name);
        if self.optional { format!("Option<{base}>") } else { base }
    }

    /// The C# type of the stored field.
    fn csharp_type(&self) -> String {
        let base = csharp_type_name(&self.type_name);
        if self.optional { format!("{base}?") } else { base }
    }
}

fn string_field(object: &serde_json::Map<String, Value>, key: &str) -> Result<String, SchemaError> {
    object
        .get(key)
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| err(format!("missing string field `{key}`")))
}

/// An enum variant descriptor.
#[derive(Clone, Debug)]
struct VariantDef {
    name: String,
    code: u64,
    payload: Option<FieldDef>,
}

/// An enum descriptor.
#[derive(Clone, Debug)]
struct EnumDef {
    name: String,
    tag: String,
    variants: Vec<VariantDef>,
}

/// The fully parsed schema.
#[derive(Clone, Debug)]
pub struct Schema {
    identity: String,
    registry_version: String,
    wire_version: u64,
    max_frame_bytes: u64,
    max_detail_bytes: u64,
    max_list_items: u64,
    fixed_envelope_bytes: u64,
    stream_prefix_bytes: u64,
    payload_codecs: Vec<String>,
    family_kinds: Vec<String>,
    budgets: Vec<(String, u64, String)>,
    enums: Vec<EnumDef>,
    types: Vec<(String, Vec<FieldDef>)>,
    value_variants: Vec<(String, u64, String)>,
    operations: Vec<OperationDef>,
    notifications: Vec<NotificationDef>,
    channel_frames: Vec<NotificationDef>,
}

/// An operation descriptor (one request frame + one response frame).
#[derive(Clone, Debug)]
struct OperationDef {
    name: String,
    code: u64,
    response_code: u64,
    family: String,
    class: String,
    mutation: bool,
    parity: Vec<String>,
    request: BodyDef,
    response: BodyDef,
}

/// A named frame body descriptor.
#[derive(Clone, Debug)]
struct BodyDef {
    name: String,
    fields: Vec<FieldDef>,
}

/// A standalone frame descriptor (notification or channel frame).
#[derive(Clone, Debug)]
struct NotificationDef {
    name: String,
    code: u64,
    family: String,
    class: String,
    parity: Vec<String>,
    fields: Vec<FieldDef>,
}

fn pascal(name: &str) -> String {
    let mut out = String::new();
    for part in name.split(['_', '.', '-']) {
        let mut chars = part.chars();
        if let Some(first) = chars.next() {
            out.extend(first.to_uppercase());
            out.push_str(chars.as_str());
        }
    }
    out
}

fn rust_type_name(name: &str) -> String {
    match name {
        "u8" => "u8".into(),
        "u16" => "u16".into(),
        "u32" => "u32".into(),
        "u64" => "u64".into(),
        "i64" => "i64".into(),
        "f64" => "f64".into(),
        "bool" => "bool".into(),
        "str" => "String".into(),
        "bytes" => "Vec<u8>".into(),
        "value" => "TbepValue".into(),
        "[str]" => "Vec<String>".into(),
        "[u64]" => "Vec<u64>".into(),
        "[value]" => "Vec<TbepValue>".into(),
        "[(str,value)]" => "Vec<(String, TbepValue)>".into(),
        other => {
            if let Some(inner) = other.strip_prefix('[').and_then(|o| o.strip_suffix(']')) {
                format!("Vec<{}>", rust_type_name(inner))
            } else {
                pascal(other)
            }
        }
    }
}

fn csharp_type_name(name: &str) -> String {
    match name {
        "u8" => "byte".into(),
        "u16" => "ushort".into(),
        "u32" => "uint".into(),
        "u64" => "ulong".into(),
        "i64" => "long".into(),
        "f64" => "double".into(),
        "bool" => "bool".into(),
        "str" => "string".into(),
        "bytes" => "byte[]".into(),
        "value" => "TbepValue".into(),
        "[str]" => "string[]".into(),
        "[u64]" => "ulong[]".into(),
        "[value]" => "TbepValue[]".into(),
        "[(str,value)]" => "ObjectField[]".into(),
        other => {
            if let Some(inner) = other.strip_prefix('[').and_then(|o| o.strip_suffix(']')) {
                format!("{}[]", csharp_type_name(inner))
            } else {
                pascal(other)
            }
        }
    }
}

/// Whether a type name is a schema primitive (not a named type/enum).
fn is_primitive(name: &str) -> bool {
    matches!(
        name,
        "u8" | "u16"
            | "u32"
            | "u64"
            | "i64"
            | "f64"
            | "bool"
            | "str"
            | "bytes"
            | "value"
            | "[str]"
            | "[u64]"
            | "[value]"
            | "[(str,value)]"
    )
}

fn fields_from_json(value: &Value) -> Result<Vec<FieldDef>, SchemaError> {
    match value {
        Value::Null => Ok(Vec::new()),
        Value::Array(array) => array.iter().map(FieldDef::from_json).collect(),
        _ => Err(err("`fields` must be an array")),
    }
}

fn body_from_json(value: &Value) -> Result<BodyDef, String> {
    let object = value.as_object().ok_or("a body must be an object")?;
    let name = object.get("name").and_then(Value::as_str).ok_or("body without name")?;
    let fields = object
        .get("fields")
        .map(|fields| fields_from_json(fields).map_err(|e| e.to_string()))
        .transpose()?
        .unwrap_or_default();
    Ok(BodyDef { name: name.to_string(), fields })
}

fn parity_from_json(value: &Value) -> Result<Vec<String>, SchemaError> {
    let array = value.as_array().ok_or_else(|| err("`parity` must be an array"))?;
    array
        .iter()
        .map(|item| {
            item.as_str().map(str::to_string).ok_or_else(|| err("a parity entry must be a string"))
        })
        .collect()
}

fn notification_from_json(value: &Value) -> Result<NotificationDef, SchemaError> {
    let entry = value.as_object().ok_or_else(|| err("a frame must be an object"))?;
    let name = string_field(entry, "name")?;
    Ok(NotificationDef {
        name: name.clone(),
        code: entry
            .get("code")
            .and_then(Value::as_u64)
            .ok_or_else(|| err(format!("frame {name}: missing code")))?,
        family: entry.get("family").and_then(Value::as_str).unwrap_or("channel").to_string(),
        class: string_field(entry, "class")?,
        parity: parity_from_json(
            entry.get("parity").ok_or_else(|| err(format!("frame {name}: missing parity")))?,
        )?,
        fields: fields_from_json(entry.get("fields").unwrap_or(&Value::Null))?,
    })
}

impl Schema {
    /// Parses and validates the schema document.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaError`] naming the first structural violation:
    /// duplicate frame codes or names, unknown field types, payload-carrying
    /// enum variants with unsupported payload types, or an operation without
    /// a parity mapping.
    #[allow(clippy::too_many_lines)]
    pub fn parse(document: &str) -> Result<Self, SchemaError> {
        let root: Value =
            serde_json::from_str(document).map_err(|e| err(format!("invalid JSON: {e}")))?;
        let object = root.as_object().ok_or_else(|| err("the schema root must be an object"))?;

        let enums: Vec<EnumDef> = object
            .get("enums")
            .and_then(Value::as_array)
            .ok_or_else(|| err("missing `enums`"))?
            .iter()
            .map(|value| {
                let entry = value.as_object().ok_or_else(|| err("an enum must be an object"))?;
                let name = string_field(entry, "name")?;
                let tag = string_field(entry, "tag")?;
                if tag != "u8" && tag != "u16" {
                    return Err(err(format!("enum {name}: only u8/u16 tags are supported")));
                }
                let variants: Vec<VariantDef> = entry
                    .get("variants")
                    .and_then(Value::as_array)
                    .ok_or_else(|| err(format!("enum {name}: missing variants")))?
                    .iter()
                    .enumerate()
                    .map(|(index, variant)| {
                        let v = variant.as_object().ok_or_else(|| err("a variant is an object"))?;
                        let payload = v
                            .get("field")
                            .map(|f| {
                                let mut payload = FieldDef::from_json(f)?;
                                if payload.optional || payload.one_of.is_some() {
                                    return Err(err(format!(
                                        "enum {name}: payload fields carry no optionals"
                                    )));
                                }
                                payload.bound = None;
                                Ok(payload)
                            })
                            .transpose()?;
                        Ok(VariantDef {
                            name: string_field(v, "name")?,
                            code: v.get("code").and_then(Value::as_u64).unwrap_or(index as u64 + 1),
                            payload,
                        })
                    })
                    .collect::<Result<Vec<_>, SchemaError>>()?;
                Ok(EnumDef { name, tag, variants })
            })
            .collect::<Result<Vec<_>, SchemaError>>()?;
        let enum_names: Vec<String> = enums.iter().map(|e| e.name.clone()).collect();

        let types: Vec<(String, Vec<FieldDef>)> = object
            .get("types")
            .and_then(Value::as_array)
            .map(|array| {
                array
                    .iter()
                    .map(|value| {
                        let entry =
                            value.as_object().ok_or_else(|| err("a type must be an object"))?;
                        let name = string_field(entry, "name")?;
                        let fields = entry
                            .get("fields")
                            .ok_or_else(|| err(format!("type {name}: missing fields")))?;
                        Ok((name, fields_from_json(fields)?))
                    })
                    .collect::<Result<Vec<_>, SchemaError>>()
            })
            .transpose()?
            .unwrap_or_default();
        let type_names: Vec<String> = types.iter().map(|(n, _)| n.clone()).collect();

        let value_variants: Vec<(String, u64, String)> = object
            .get("value_type")
            .and_then(Value::as_object)
            .and_then(|v| v.get("variants"))
            .and_then(Value::as_array)
            .ok_or_else(|| err("missing `value_type.variants`"))?
            .iter()
            .map(|v| {
                let entry = v.as_object().ok_or_else(|| err("a value variant is an object"))?;
                Ok((
                    string_field(entry, "name")?,
                    entry
                        .get("tag")
                        .and_then(Value::as_u64)
                        .ok_or_else(|| err("value variants carry explicit tags"))?,
                    entry.get("payload").and_then(Value::as_str).unwrap_or("").to_string(),
                ))
            })
            .collect::<Result<Vec<_>, SchemaError>>()?;

        let operations: Vec<OperationDef> = object
            .get("operations")
            .and_then(Value::as_array)
            .ok_or_else(|| err("missing `operations`"))?
            .iter()
            .map(|value| {
                let entry =
                    value.as_object().ok_or_else(|| err("an operation must be an object"))?;
                let name = string_field(entry, "name")?;
                let request = body_from_json(
                    entry
                        .get("request")
                        .ok_or_else(|| err(format!("operation {name}: missing request")))?,
                )
                .map_err(err)?;
                let response = body_from_json(
                    entry
                        .get("response")
                        .ok_or_else(|| err(format!("operation {name}: missing response")))?,
                )
                .map_err(err)?;
                let response_code = entry
                    .get("response")
                    .and_then(Value::as_object)
                    .and_then(|r| r.get("code"))
                    .and_then(Value::as_u64)
                    .ok_or_else(|| err(format!("operation {name}: response without code")))?;
                Ok(OperationDef {
                    name: name.clone(),
                    code: entry
                        .get("code")
                        .and_then(Value::as_u64)
                        .ok_or_else(|| err(format!("operation {name}: missing code")))?,
                    response_code,
                    family: string_field(entry, "family")?,
                    class: string_field(entry, "class")?,
                    mutation: entry.get("mutation").and_then(Value::as_bool).unwrap_or(false),
                    parity: parity_from_json(entry.get("parity").ok_or_else(|| {
                        err(format!("operation {name}: missing parity mapping"))
                    })?)?,
                    request,
                    response,
                })
            })
            .collect::<Result<Vec<_>, SchemaError>>()?;

        let notifications: Vec<NotificationDef> = object
            .get("notifications")
            .and_then(Value::as_array)
            .map(|array| {
                array.iter().map(notification_from_json).collect::<Result<Vec<_>, SchemaError>>()
            })
            .transpose()?
            .unwrap_or_default();

        let channel_frames: Vec<NotificationDef> = object
            .get("channel_frames")
            .and_then(Value::as_array)
            .map(|array| {
                array.iter().map(notification_from_json).collect::<Result<Vec<_>, SchemaError>>()
            })
            .transpose()?
            .unwrap_or_default();

        let schema = Self {
            identity: string_field(object, "identity")?,
            registry_version: string_field(object, "registry_version")?,
            wire_version: object
                .get("wire_version")
                .and_then(Value::as_u64)
                .ok_or_else(|| err("missing wire_version"))?,
            max_frame_bytes: object
                .get("max_frame_bytes")
                .and_then(Value::as_u64)
                .ok_or_else(|| err("missing max_frame_bytes"))?,
            max_detail_bytes: object
                .get("max_detail_bytes")
                .and_then(Value::as_u64)
                .ok_or_else(|| err("missing max_detail_bytes"))?,
            max_list_items: object
                .get("max_list_items")
                .and_then(Value::as_u64)
                .ok_or_else(|| err("missing max_list_items"))?,
            fixed_envelope_bytes: object
                .get("envelope")
                .and_then(Value::as_object)
                .and_then(|e| e.get("fixed_cost_bytes"))
                .and_then(Value::as_u64)
                .ok_or_else(|| err("missing envelope.fixed_cost_bytes"))?,
            stream_prefix_bytes: object
                .get("stream_length_prefix_bytes")
                .and_then(Value::as_u64)
                .ok_or_else(|| err("missing stream_length_prefix_bytes"))?,
            payload_codecs: object
                .get("payload_codecs")
                .and_then(Value::as_array)
                .map(|array| array.iter().filter_map(Value::as_str).map(str::to_string).collect())
                .ok_or_else(|| err("missing payload_codecs"))?,
            family_kinds: object
                .get("family_kinds")
                .and_then(Value::as_array)
                .map(|array| array.iter().filter_map(Value::as_str).map(str::to_string).collect())
                .ok_or_else(|| err("missing family_kinds"))?,
            budgets: object
                .get("budgets")
                .and_then(Value::as_array)
                .map(|array| {
                    array
                        .iter()
                        .filter_map(|b| {
                            let entry = b.as_object()?;
                            Some((
                                entry.get("class")?.as_str()?.to_string(),
                                entry.get("wire_bytes")?.as_u64()?,
                                entry.get("frames")?.as_str()?.to_string(),
                            ))
                        })
                        .collect()
                })
                .ok_or_else(|| err("missing budgets"))?,
            enums,
            types,
            value_variants,
            operations,
            notifications,
            channel_frames,
        };

        schema.validate(&enum_names, &type_names)?;
        Ok(schema)
    }

    #[allow(clippy::too_many_lines)]
    fn validate(&self, enum_names: &[String], type_names: &[String]) -> Result<(), SchemaError> {
        // The value type is the one codec-level closed set shared with
        // `crate::codec::TbepValue`; its tags are pinned contiguous
        // 0..=6 so a schema edit cannot silently renumber documents.
        let mut value_tags: Vec<u64> = self.value_variants.iter().map(|(_, tag, _)| *tag).collect();
        value_tags.sort_unstable();
        if value_tags != (0..=6).collect::<Vec<u64>>() {
            return Err(err(
                "value_type variants must carry exactly the tags 0..=6 in the codec order",
            ));
        }
        let mut codes: std::collections::BTreeMap<u64, String> = std::collections::BTreeMap::new();
        let mut claim = |code: u64, kind: &str| -> Result<(), SchemaError> {
            if code > u64::from(u16::MAX) {
                return Err(err(format!("{kind}: code {code} exceeds u16")));
            }
            if let Some(existing) = codes.insert(code, kind.to_string()) {
                return Err(err(format!(
                    "frame kind {kind} reuses code {code} already claimed by {existing}"
                )));
            }
            let _ = pascal(kind);
            Ok(())
        };
        let mut kind_names: std::collections::BTreeSet<String> = std::collections::BTreeSet::new();
        let mut claim_name = |kind: &str| -> Result<(), SchemaError> {
            if !kind_names.insert(pascal(kind)) {
                return Err(err(format!("duplicate frame kind name {kind}")));
            }
            Ok(())
        };

        for operation in &self.operations {
            claim(operation.code, &operation.request.name)?;
            claim(operation.response_code, &operation.response.name)?;
            claim_name(&operation.request.name)?;
            claim_name(&operation.response.name)?;
            if operation.parity.is_empty() {
                return Err(err(format!("operation {} carries no parity mapping", operation.name)));
            }
        }
        for notification in self.notifications.iter().chain(self.channel_frames.iter()) {
            claim(notification.code, &notification.name)?;
            claim_name(&notification.name)?;
            if notification.parity.is_empty() {
                return Err(err(format!("frame {} carries no parity mapping", notification.name)));
            }
        }

        let mut all_fields: Vec<&FieldDef> = Vec::new();
        for (_, fields) in &self.types {
            all_fields.extend(fields);
        }
        for operation in &self.operations {
            all_fields.extend(&operation.request.fields);
            all_fields.extend(&operation.response.fields);
        }
        for notification in self.notifications.iter().chain(self.channel_frames.iter()) {
            all_fields.extend(&notification.fields);
        }
        for enum_def in &self.enums {
            for variant in &enum_def.variants {
                if let Some(payload) = &variant.payload {
                    let inner = payload
                        .type_name
                        .strip_prefix('[')
                        .and_then(|o| o.strip_suffix(']'))
                        .unwrap_or(&payload.type_name);
                    if !is_primitive(&payload.type_name)
                        && !enum_names.iter().any(|n| n == &payload.type_name)
                        && !type_names.iter().any(|n| n == &payload.type_name)
                        && !type_names.iter().any(|n| n == inner)
                    {
                        return Err(err(format!(
                            "enum {}: variant {} carries unknown payload type {}",
                            enum_def.name, variant.name, payload.type_name
                        )));
                    }
                }
            }
        }
        for field in &all_fields {
            let inner = field
                .type_name
                .strip_prefix('[')
                .and_then(|o| o.strip_suffix(']'))
                .unwrap_or(&field.type_name);
            let known = is_primitive(&field.type_name)
                || enum_names.iter().any(|n| n == &field.type_name)
                || type_names.iter().any(|n| n == &field.type_name)
                || (field.type_name.starts_with('[')
                    && (is_primitive(inner) || type_names.iter().any(|n| n == inner)));
            if !known {
                return Err(err(format!(
                    "field {} names unknown type {}",
                    field.name, field.type_name
                )));
            }
            if field.type_name.starts_with('[') && field.bound.is_some() {
                return Err(err(format!(
                    "field {}: list types take item bounds, not byte bounds",
                    field.name
                )));
            }
            if field.type_name == "str" && field.bound.is_none() && !field.optional {
                return Err(err(format!(
                    "field {}: string fields carry explicit bounds",
                    field.name
                )));
            }
        }
        Ok(())
    }

    /// Every frame body the registry defines, as
    /// `(code, body-name, family, class, direction, mutation-family)`,
    /// sorted by code.
    #[must_use]
    pub fn frame_rows(&self) -> Vec<FrameRow> {
        let mut rows: Vec<FrameRow> = Vec::new();
        for operation in &self.operations {
            rows.push(FrameRow {
                code: operation.code,
                kind: pascal(&operation.request.name),
                family: operation.family.clone(),
                class: operation.class.clone(),
                direction: "host-to-engine",
                mutation_family: operation.mutation,
            });
            rows.push(FrameRow {
                code: operation.response_code,
                kind: pascal(&operation.response.name),
                family: operation.family.clone(),
                class: operation.class.clone(),
                direction: "engine-to-host",
                mutation_family: false,
            });
        }
        for notification in self.notifications.iter().chain(self.channel_frames.iter()) {
            rows.push(FrameRow {
                code: notification.code,
                kind: pascal(&notification.name),
                family: notification.family.clone(),
                class: notification.class.clone(),
                direction: "engine-to-host",
                mutation_family: false,
            });
        }
        rows.sort_by(|a, b| a.code.cmp(&b.code).then(a.kind.cmp(&b.kind)));
        rows
    }
}

/// One registry row of the frame taxonomy.
#[derive(Clone, Debug)]
pub struct FrameRow {
    /// The wire code.
    pub code: u64,
    /// The generated kind name.
    pub kind: String,
    /// The frame family.
    pub family: String,
    /// The budget class.
    pub class: String,
    /// The frame direction.
    pub direction: &'static str,
    /// Whether request frames of this operation carry idempotency keys.
    pub mutation_family: bool,
}

/// Generates both bindings files from one schema document.
///
/// # Errors
///
/// Returns [`SchemaError`] when the document does not parse or violates
/// the validation rules above.
pub fn generate(document: &str) -> Result<GeneratedFiles, SchemaError> {
    let schema = Schema::parse(document)?;
    Ok(GeneratedFiles { rust: generate_rust(&schema), csharp: generate_csharp(&schema) })
}

/// The pair of generated bindings files.
#[derive(Clone, Debug)]
pub struct GeneratedFiles {
    /// The Rust bindings source (`src/generated/registry.rs`).
    pub rust: String,
    /// The C# bindings source
    /// (`host/TerraBase.Protocol/Generated/TbepRegistry.g.cs`).
    pub csharp: String,
}

/// Validates every `PB-*` parity reference against the parity inventory.
///
/// `new-capability` markers (entries starting with `new-capability`)
/// carry no inventory reference and pass by construction.
///
/// # Errors
///
/// Returns [`SchemaError`] naming the first reference that does not
/// resolve to a row of the inventory document, or fails to parse either
/// document.
pub fn check_parity_against_inventory(
    schema_document: &str,
    inventory_document: &str,
) -> Result<(), SchemaError> {
    let schema = Schema::parse(schema_document)?;
    let inventory: Value = serde_json::from_str(inventory_document)
        .map_err(|e| err(format!("invalid inventory JSON: {e}")))?;
    let known: Vec<String> = inventory
        .get("items")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| item.get("id").and_then(Value::as_str))
                .map(str::to_string)
                .collect()
        })
        .unwrap_or_default();
    let mut references: Vec<String> = Vec::new();
    for operation in &schema.operations {
        references.extend(operation.parity.iter().cloned());
    }
    for notification in schema.notifications.iter().chain(schema.channel_frames.iter()) {
        references.extend(notification.parity.iter().cloned());
    }
    if references.is_empty() {
        return Err(err("the registry carries no parity mappings at all"));
    }
    for reference in &references {
        if reference.starts_with("new-capability") {
            continue;
        }
        let id = reference.split_whitespace().next().unwrap_or(reference);
        if !known.iter().any(|k| k == id) {
            return Err(err(format!(
                "parity reference {id} does not name a row of parity/inventory.json"
            )));
        }
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Rust emission
// ---------------------------------------------------------------------------

#[allow(clippy::too_many_lines)]
fn generate_rust(schema: &Schema) -> String {
    let mut out = String::new();
    let _ = writeln!(
        out,
        "// GENERATED by tbep-gen from schema/tbep-registry.json — do not edit by hand."
    );
    let _ = writeln!(
        out,
        "// Regenerate from crates/terrabase-tbep/tools/tbep-gen (`cargo run` there)."
    );
    let _ =
        writeln!(out, "// Any hand edit is a drift break: tests/registry_drift.rs compares this");
    let _ = writeln!(out, "// file byte-for-byte against regeneration.");
    let _ = writeln!(out, "// The generated TBEP registry and frame bindings.");
    let _ = writeln!(out, "//");
    let _ = writeln!(
        out,
        "// Schema identity `{}` version `{}`.",
        schema.identity, schema.registry_version
    );
    let _ = writeln!(out, "use crate::codec::TbepValue;");
    let _ = writeln!(out, "use serde::ser::SerializeTuple as _;");
    let _ = writeln!(out);

    let _ = writeln!(out, "/// The protocol identity string.");
    let _ = writeln!(out, "pub const IDENTITY: &str = {:?};", schema.identity);
    let _ = writeln!(out, "/// The registry version (additive changes bump the minor).");
    let _ = writeln!(out, "pub const REGISTRY_VERSION: &str = {:?};", schema.registry_version);
    let _ =
        writeln!(out, "/// The wire version every frame carries; unknown versions fail closed.");
    let _ = writeln!(out, "pub const WIRE_VERSION: u8 = {};", schema.wire_version);
    let _ =
        writeln!(out, "/// The decode bound: larger frames are rejected before any field parses.");
    let _ = writeln!(out, "pub const MAX_FRAME_BYTES: usize = {};", schema.max_frame_bytes);
    let _ = writeln!(out, "/// The detail bound of `error` frames.");
    let _ = writeln!(out, "pub const MAX_DETAIL_BYTES: usize = {};", schema.max_detail_bytes);
    let _ = writeln!(out, "/// The item bound of every list field.");
    let _ = writeln!(out, "pub const MAX_LIST_ITEMS: usize = {};", schema.max_list_items);
    let _ = writeln!(
        out,
        "/// The fixed envelope cost (bytes) over the body, bin8 body header included."
    );
    let _ =
        writeln!(out, "pub const FIXED_ENVELOPE_BYTES: usize = {};", schema.fixed_envelope_bytes);
    let _ = writeln!(out, "/// The stream length prefix width (u32 little-endian) of every frame.");
    let _ = writeln!(out, "pub const STREAM_PREFIX_BYTES: usize = {};", schema.stream_prefix_bytes);
    let _ = writeln!(out);
    let _ = writeln!(out, "/// The closed payload-codec vocabulary (named versioned codecs).");
    let _ = writeln!(out, "pub const PAYLOAD_CODECS: &[&str] = &[");
    for codec in &schema.payload_codecs {
        let _ = writeln!(out, "    {codec:?},");
    }
    let _ = writeln!(out, "];");
    let _ = writeln!(out);
    let _ = writeln!(out, "/// The closed family-kind name vocabulary of `family.*` operations.");
    let _ = writeln!(out, "pub const FAMILY_KINDS: &[&str] = &[");
    for kind in &schema.family_kinds {
        let _ = writeln!(out, "    {kind:?},");
    }
    let _ = writeln!(out, "];");
    let _ = writeln!(out);
    let _ = writeln!(
        out,
        "/// The written bytes-on-wire budget per frame class (PD-2026-08-26-13 D1)."
    );
    let _ = writeln!(out, "pub const BUDGETS: &[(&str, usize)] = &[");
    for (class, bytes, _) in &schema.budgets {
        let _ = writeln!(out, "    (\"{class}\", {bytes}),");
    }
    let _ = writeln!(out, "];");
    let _ = writeln!(out);

    for enum_def in &schema.enums {
        emit_rust_enum(&mut out, enum_def);
    }

    let mut bodies: Vec<(&str, &[FieldDef], bool)> = Vec::new();
    for (name, fields) in &schema.types {
        // Named types are embedded record shapes, not wire frames; they
        // carry no WireBody impl of their own.
        bodies.push((name, fields, false));
    }
    for operation in &schema.operations {
        bodies.push((&operation.request.name, &operation.request.fields, true));
        bodies.push((&operation.response.name, &operation.response.fields, true));
    }
    for notification in schema.notifications.iter().chain(schema.channel_frames.iter()) {
        bodies.push((&notification.name, &notification.fields, true));
    }
    for (name, fields, is_frame) in &bodies {
        emit_rust_struct(&mut out, name, fields, *is_frame);
    }

    emit_rust_frame_kind(&mut out, schema);
    emit_rust_body_union(&mut out, schema);
    emit_rust_operation_table(&mut out, schema);
    out
}

#[allow(clippy::too_many_lines)]
fn emit_rust_enum(out: &mut String, enum_def: &EnumDef) {
    let name = pascal(&enum_def.name);
    let tag_type = enum_def.tag.as_str();
    let payloadless: Vec<&VariantDef> =
        enum_def.variants.iter().filter(|v| v.payload.is_none()).collect();
    // Copy rides only when every payload is a primitive scalar; payloads
    // carrying heap types (str, values, named types) keep the enum move-only.
    let copyable = enum_def.variants.iter().all(|variant| {
        variant.payload.as_ref().is_none_or(|payload| {
            matches!(
                payload.type_name.as_str(),
                "u8" | "u16" | "u32" | "u64" | "i64" | "f64" | "bool"
            )
        })
    });
    let _ = writeln!(out, "/// The generated `{}` closed vocabulary.", enum_def.name);
    let derive = if copyable {
        "#[derive(Clone, Copy, Debug, PartialEq)]"
    } else {
        "#[derive(Clone, Debug, PartialEq)]"
    };
    let _ = writeln!(out, "{derive}");
    let _ = writeln!(out, "pub enum {name} {{");
    for variant in &enum_def.variants {
        let vname = pascal(&variant.name);
        let _ = writeln!(out, "    /// The `{}` row (code {}).", variant.name, variant.code);
        if let Some(payload) = &variant.payload {
            let _ = writeln!(out, "    {vname}({}),", rust_type_name(&payload.type_name));
        } else {
            let _ = writeln!(out, "    {vname},");
        }
    }
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "impl {name} {{");
    let _ = writeln!(out, "    /// The variant's wire code; unknown codes fail closed on decode.");
    let _ = writeln!(out, "    #[must_use]");
    if copyable {
        let _ = writeln!(out, "    pub const fn code(self) -> {tag_type} {{");
    } else {
        let _ = writeln!(out, "    pub fn code(&self) -> {tag_type} {{");
    }
    let _ = writeln!(out, "        match self {{");
    for variant in &enum_def.variants {
        let vname = pascal(&variant.name);
        if variant.payload.is_some() {
            let _ = writeln!(out, "            Self::{vname}(_) => {},", variant.code);
        } else {
            let _ = writeln!(out, "            Self::{vname} => {},", variant.code);
        }
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    if !payloadless.is_empty() {
        let _ = writeln!(out);
        let _ = writeln!(out, "    /// Decodes a payload-less variant from its wire code.");
        let _ = writeln!(out, "    ///");
        let _ = writeln!(out, "    /// Payload-carrying variants decode through the serde codec");
        let _ = writeln!(out, "    /// (`from_wire`); they answer `None` here by construction.");
        let _ = writeln!(out, "    #[must_use]");
        let _ = writeln!(out, "    pub const fn from_code(code: {tag_type}) -> Option<Self> {{");
        let _ = writeln!(out, "        match code {{");
        for variant in &payloadless {
            let vname = pascal(&variant.name);
            let _ = writeln!(out, "            {} => Some(Self::{vname}),", variant.code);
        }
        let _ = writeln!(out, "            _ => None,");
        let _ = writeln!(out, "        }}");
        let _ = writeln!(out, "    }}");
    }
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);

    // serde: wire form is a one- or two-element array [tag] / [tag, payload].
    let _ = writeln!(out, "impl serde::Serialize for {name} {{");
    let _ = writeln!(
        out,
        "    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {{"
    );
    let _ = writeln!(out, "        match self {{");
    for variant in &enum_def.variants {
        let vname = pascal(&variant.name);
        if variant.payload.is_some() {
            let _ = writeln!(out, "            Self::{vname}(payload) => {{");
            let _ =
                writeln!(out, "                let mut tuple = serializer.serialize_tuple(2)?;");
            let _ = writeln!(
                out,
                "                tuple.serialize_element(&{}{})?;",
                variant.code, enum_def.tag
            );
            let _ = writeln!(out, "                tuple.serialize_element(payload)?;");
            let _ = writeln!(out, "                tuple.end()");
            let _ = writeln!(out, "            }}");
        } else {
            let _ = writeln!(out, "            Self::{vname} => {{");
            let _ =
                writeln!(out, "                let mut tuple = serializer.serialize_tuple(1)?;");
            let _ = writeln!(
                out,
                "                tuple.serialize_element(&{}{})?;",
                variant.code, enum_def.tag
            );
            let _ = writeln!(out, "                tuple.end()");
            let _ = writeln!(out, "            }}");
        }
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "impl<'de> serde::Deserialize<'de> for {name} {{");
    let _ = writeln!(
        out,
        "    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {{"
    );
    let _ = writeln!(out, "        struct Visitor;");
    let _ = writeln!(out, "        impl<'de> serde::de::Visitor<'de> for Visitor {{");
    let _ = writeln!(out, "            type Value = {name};");
    let _ = writeln!(
        out,
        "            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {{"
    );
    let _ = writeln!(
        out,
        "                formatter.write_str(\"a {} array of one tagged variant\")",
        enum_def.name
    );
    let _ = writeln!(out, "            }}");
    let _ = writeln!(
        out,
        "            fn visit_seq<A: serde::de::SeqAccess<'de>>(self, mut seq: A) -> Result<Self::Value, A::Error> {{"
    );
    let _ = writeln!(out, "                let tag = seq");
    let _ = writeln!(out, "                    .next_element::<{tag_type}>()?");
    let _ = writeln!(
        out,
        "                    .ok_or_else(|| serde::de::Error::invalid_length(0, &self))?;"
    );
    let _ = writeln!(out, "                match tag {{");
    for variant in &enum_def.variants {
        let vname = pascal(&variant.name);
        if let Some(payload) = &variant.payload {
            let payload_type = rust_type_name(&payload.type_name);
            let _ = writeln!(out, "                    {} => {{", variant.code);
            let _ = writeln!(
                out,
                "                        let payload = seq.next_element::<{payload_type}>()?"
            );
            let _ = writeln!(
                out,
                "                            .ok_or_else(|| serde::de::Error::invalid_length(1, &self))?;"
            );
            let _ = writeln!(
                out,
                "                        if seq.next_element::<serde::de::IgnoredAny>()?.is_some() {{"
            );
            let _ = writeln!(
                out,
                "                            return Err(serde::de::Error::invalid_length(2, &self));"
            );
            let _ = writeln!(out, "                        }}");
            let _ = writeln!(out, "                        Ok({name}::{vname}(payload))");
            let _ = writeln!(out, "                    }}");
        } else {
            let _ = writeln!(out, "                    {} => {{", variant.code);
            let _ = writeln!(
                out,
                "                        if seq.next_element::<serde::de::IgnoredAny>()?.is_some() {{"
            );
            let _ = writeln!(
                out,
                "                            return Err(serde::de::Error::invalid_length(1, &self));"
            );
            let _ = writeln!(out, "                        }}");
            let _ = writeln!(out, "                        Ok({name}::{vname})");
            let _ = writeln!(out, "                    }}");
        }
    }
    let _ = writeln!(
        out,
        "                    _ => Err(serde::de::Error::custom(\"unknown {} code\")),",
        enum_def.name
    );
    let _ = writeln!(out, "                }}");
    let _ = writeln!(out, "            }}");
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "        deserializer.deserialize_any(Visitor)");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
}

#[allow(clippy::too_many_lines)]
fn emit_rust_struct(out: &mut String, name: &str, fields: &[FieldDef], is_frame: bool) {
    let struct_name = pascal(name);
    let _ = writeln!(out, "/// The `{name}` frame body.");
    let _ = writeln!(out, "#[derive(Clone, Debug, PartialEq)]");
    let _ = writeln!(out, "pub struct {struct_name} {{");
    for field in fields {
        let _ = writeln!(out, "    /// The `{}` field.", field.name);
        let _ = writeln!(out, "    pub {}: {},", field.name, field.rust_type());
    }
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    // serde: the wire form is a positional array of the fields.
    let _ = writeln!(out, "impl serde::Serialize for {struct_name} {{");
    let _ = writeln!(
        out,
        "    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {{"
    );
    if fields.is_empty() {
        let _ = writeln!(out, "        serializer.serialize_tuple(0)?.end()");
    } else {
        let _ =
            writeln!(out, "        let mut tuple = serializer.serialize_tuple({})?;", fields.len());
        for field in fields {
            let expr = field_ser_expr(field);
            if field.type_name == "bytes" {
                if field.optional {
                    let _ = writeln!(
                        out,
                        "        tuple.serialize_element(&crate::codec::bytes_ref_opt(&self.{}))?;",
                        field.name
                    );
                } else {
                    let _ = writeln!(
                        out,
                        "        tuple.serialize_element(&crate::codec::bytes_ref(self.{}.as_slice()))?;",
                        field.name
                    );
                }
            } else {
                let _ = writeln!(out, "        tuple.serialize_element(&{expr})?;");
            }
        }
        let _ = writeln!(out, "        tuple.end()");
    }
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "impl<'de> serde::Deserialize<'de> for {struct_name} {{");
    let _ = writeln!(
        out,
        "    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {{"
    );
    let _ = writeln!(out, "        struct Visitor;");
    let _ = writeln!(out, "        impl<'de> serde::de::Visitor<'de> for Visitor {{");
    let _ = writeln!(out, "            type Value = {struct_name};");
    let _ = writeln!(
        out,
        "            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {{"
    );
    let _ = writeln!(
        out,
        "                formatter.write_str(\"a {name} array of {} fields\")",
        fields.len()
    );
    let _ = writeln!(out, "            }}");
    let _ = writeln!(
        out,
        "            fn visit_seq<A: serde::de::SeqAccess<'de>>(self, mut seq: A) -> Result<Self::Value, A::Error> {{"
    );
    for (index, field) in fields.iter().enumerate() {
        let field_type = if field.optional {
            format!("Option<{}>", rust_type_name(&field.type_name))
        } else {
            rust_type_name(&field.type_name)
        };
        if field.type_name == "bytes" {
            if field.optional {
                let _ = writeln!(out, "                let {name} = seq", name = field.name);
                let _ = writeln!(
                    out,
                    "                    .next_element::<Option<crate::envelope::WireBytes>>()?"
                );
                let _ = writeln!(
                    out,
                    "                    .ok_or_else(|| serde::de::Error::invalid_length({index}, &self))?"
                );
                let _ = writeln!(out, "                    .map(|wire| wire.0);");
            } else {
                let _ = writeln!(out, "                let {name} = seq", name = field.name);
                let _ = writeln!(
                    out,
                    "                    .next_element::<crate::envelope::WireBytes>()?"
                );
                let _ = writeln!(
                    out,
                    "                    .ok_or_else(|| serde::de::Error::invalid_length({index}, &self))?"
                );
                let _ = writeln!(out, "                    .0;");
            }
        } else {
            let _ = writeln!(out, "                let {} = seq", field.name);
            let _ = writeln!(out, "                    .next_element::<{field_type}>()?");
            let _ = writeln!(
                out,
                "                    .ok_or_else(|| serde::de::Error::invalid_length({index}, &self))?;",
            );
        }
    }
    if fields.is_empty() {
        let _ = writeln!(out, "                let _ = &mut seq;");
    } else {
        let _ = writeln!(
            out,
            "                if seq.next_element::<serde::de::IgnoredAny>()?.is_some() {{"
        );
        let _ = writeln!(
            out,
            "                    return Err(serde::de::Error::invalid_length({}, &self));",
            fields.len()
        );
        let _ = writeln!(out, "                }}");
    }
    let _ = writeln!(out, "                Ok(Self::Value {{");
    for field in fields {
        let _ = writeln!(out, "                    {},", field.name);
    }
    let _ = writeln!(out, "                }})");
    let _ = writeln!(out, "            }}");
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "        deserializer.deserialize_any(Visitor)");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    // The WireBody impl: kind + bounds validation (wire frames only).
    let mut checks: Vec<String> = Vec::new();
    for field in fields {
        if let Some(bound) = field.bound
            && field.type_name == "str"
        {
            if field.optional {
                checks.push(format!(
                        "if let Some(value) = &self.{} {{ crate::codec::check_str_bound(value, {bound}, \"{}\")?; }}",
                        field.name, field.name
                    ));
            } else {
                checks.push(format!(
                    "crate::codec::check_str_bound(&self.{}, {bound}, \"{}\")?;",
                    field.name, field.name
                ));
            }
        }
        if let Some(one_of) = &field.one_of
            && one_of == "family_kinds"
        {
            checks.push(format!(
                "crate::codec::check_one_of(&self.{}, FAMILY_KINDS, \"{}\")?;",
                field.name, field.name
            ));
        }
        if field.type_name.starts_with('[') {
            if field.optional {
                checks.push(format!(
                    "if let Some(value) = &self.{} {{ crate::codec::check_list_bound(value, MAX_LIST_ITEMS, \"{}\")?; }}",
                    field.name, field.name
                ));
            } else {
                checks.push(format!(
                    "crate::codec::check_list_bound(&self.{}, MAX_LIST_ITEMS, \"{}\")?;",
                    field.name, field.name
                ));
            }
        }
    }
    if !is_frame {
        return;
    }
    let _ = writeln!(out, "impl crate::codec::WireBody for {struct_name} {{");
    let kind_expr = frame_kind_expr_for_body(name);
    let _ = writeln!(out, "    const KIND: FrameKind = {kind_expr};");
    let _ = writeln!(out, "    fn validate(&self) -> Result<(), crate::codec::BodyError> {{");
    if checks.is_empty() {
        let _ = writeln!(out, "        let _ = self;");
        let _ = writeln!(out, "        Ok(())");
    } else {
        for check in &checks {
            let _ = writeln!(out, "        {check}");
        }
        let _ = writeln!(out, "        Ok(())");
    }
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
}

fn field_ser_expr(field: &FieldDef) -> String {
    if field.optional {
        // Option<&T> serializes as nil or the value, which is exactly the
        // optional discipline (nil only where the schema allows it).
        match field.type_name.as_str() {
            "str" | "bytes" => format!("self.{}.as_deref()", field.name),
            other if other.starts_with('[') => {
                format!("self.{}.as_deref()", field.name)
            }
            _ => format!("self.{}.as_ref()", field.name),
        }
    } else {
        match field.type_name.as_str() {
            "str" => format!("self.{}.as_str()", field.name),
            "bytes" => format!("self.{}.as_slice()", field.name),
            other if other.starts_with('[') => format!("self.{}.as_slice()", field.name),
            _ => format!("self.{}", field.name),
        }
    }
}

fn frame_kind_expr_for_body(name: &str) -> String {
    format!("FrameKind::{}", pascal(name))
}

#[allow(clippy::too_many_lines)]
fn emit_rust_frame_kind(out: &mut String, schema: &Schema) {
    let rows = schema.frame_rows();
    let _ = writeln!(out, "/// The closed frame-kind registry: one variant per wire frame.");
    let _ = writeln!(out, "#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]");
    let _ = writeln!(out, "pub enum FrameKind {{");
    for row in &rows {
        let _ = writeln!(out, "    /// Code {} ({}; {}).", row.code, row.direction, row.family);
        let _ = writeln!(out, "    {},", row.kind);
    }
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "impl FrameKind {{");
    let _ = writeln!(out, "    /// The frame's registry code.");
    let _ = writeln!(out, "    #[must_use]");
    let _ = writeln!(out, "    pub const fn code(self) -> u16 {{");
    let _ = writeln!(out, "        match self {{");
    for row in &rows {
        let _ = writeln!(out, "            Self::{} => {},", row.kind, row.code);
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ =
        writeln!(out, "    /// Decodes a kind from its registry code; unknown codes fail closed.");
    let _ = writeln!(out, "    #[must_use]");
    let _ = writeln!(out, "    pub const fn from_code(code: u16) -> Option<Self> {{");
    let _ = writeln!(out, "        match code {{");
    for row in &rows {
        let _ = writeln!(out, "            {} => Some(Self::{}),", row.code, row.kind);
    }
    let _ = writeln!(out, "            _ => None,");
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// The frame's registry name.");
    let _ = writeln!(out, "    #[must_use]");
    let _ = writeln!(out, "    pub const fn name(self) -> &'static str {{");
    let _ = writeln!(out, "        match self {{");
    for row in &rows {
        let _ = writeln!(out, "            Self::{} => \"{}\",", row.kind, row.kind);
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// The frame's family.");
    let _ = writeln!(out, "    #[must_use]");
    let _ = writeln!(out, "    pub const fn family(self) -> &'static str {{");
    let _ = writeln!(out, "        match self {{");
    for row in &rows {
        let _ = writeln!(out, "            Self::{} => \"{}\",", row.kind, row.family);
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// The frame's budget class.");
    let _ = writeln!(out, "    #[must_use]");
    let _ = writeln!(out, "    pub const fn class(self) -> &'static str {{");
    let _ = writeln!(out, "        match self {{");
    for row in &rows {
        let _ = writeln!(out, "            Self::{} => \"{}\",", row.kind, row.class);
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ =
        writeln!(out, "    /// Whether request frames of this operation carry an idempotency key.");
    let _ = writeln!(out, "    #[must_use]");
    let _ = writeln!(out, "    pub const fn carries_idempotency(self) -> bool {{");
    let _ = writeln!(out, "        matches!(self,");
    let mut mutation_kinds: Vec<&FrameRow> =
        rows.iter().filter(|row| row.mutation_family).collect();
    mutation_kinds.sort_by_key(|row| row.kind.clone());
    for (index, row) in mutation_kinds.iter().enumerate() {
        if index == 0 {
            let _ = writeln!(out, "            Self::{}", row.kind);
        } else {
            let _ = writeln!(out, "            | Self::{}", row.kind);
        }
    }
    let _ = writeln!(out, "        )");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "/// One row of the generated frame taxonomy.");
    let _ = writeln!(out, "#[derive(Clone, Copy, Debug, PartialEq)]");
    let _ = writeln!(out, "pub struct FrameRow {{");
    let _ = writeln!(out, "    /// The wire code.");
    let _ = writeln!(out, "    pub code: u16,");
    let _ = writeln!(out, "    /// The kind name.");
    let _ = writeln!(out, "    pub kind: &'static str,");
    let _ = writeln!(out, "    /// The frame family.");
    let _ = writeln!(out, "    pub family: &'static str,");
    let _ = writeln!(out, "    /// The budget class.");
    let _ = writeln!(out, "    pub class: &'static str,");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "/// The frame taxonomy rows, sorted by code.");
    let _ = writeln!(out, "pub const FRAME_ROWS: &[FrameRow] = &[");
    for row in &rows {
        let _ = writeln!(
            out,
            "    FrameRow {{ code: {}, kind: \"{}\", family: \"{}\", class: \"{}\" }},",
            row.code, row.kind, row.family, row.class
        );
    }
    let _ = writeln!(out, "];");
    let _ = writeln!(out);
}

fn emit_rust_body_union(out: &mut String, schema: &Schema) {
    let rows = schema.frame_rows();
    let _ = writeln!(out, "/// The closed body union: one variant per frame kind.");
    let _ = writeln!(out, "#[derive(Clone, Debug, PartialEq)]");
    let _ = writeln!(out, "pub enum TbepBody {{");
    for row in &rows {
        let _ = writeln!(out, "    /// The `{}` body.", row.kind);
        let _ = writeln!(out, "    {}({}),", row.kind, row.kind);
    }
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    for row in &rows {
        let _ = writeln!(out, "impl From<{}> for TbepBody {{", row.kind);
        let _ = writeln!(
            out,
            "    fn from(value: {}) -> Self {{ Self::{}(value) }}",
            row.kind, row.kind
        );
        let _ = writeln!(out, "}}");
        let _ = writeln!(out);
    }
    let _ = writeln!(out, "impl TbepBody {{");
    let _ = writeln!(out, "    /// The body's frame kind.");
    let _ = writeln!(out, "    #[must_use]");
    let _ = writeln!(out, "    pub const fn kind(&self) -> FrameKind {{");
    let _ = writeln!(out, "        match self {{");
    for row in &rows {
        let _ = writeln!(out, "            Self::{}(_) => FrameKind::{},", row.kind, row.kind);
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// Encodes the body through its concrete type.");
    let _ = writeln!(out, "    ///");
    let _ = writeln!(out, "    /// # Errors");
    let _ = writeln!(out, "    ///");
    let _ = writeln!(out, "    /// Returns the first bounds violation.");
    let _ =
        writeln!(out, "    pub fn encode(&self) -> Result<Vec<u8>, crate::codec::BodyError> {{");
    let _ = writeln!(out, "        match self {{");
    for row in &rows {
        let _ = writeln!(
            out,
            "            Self::{}(body) => crate::codec::encode_body(body),",
            row.kind
        );
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// Decodes the typed body of one frame kind.");
    let _ = writeln!(out, "    ///");
    let _ = writeln!(out, "    /// # Errors");
    let _ = writeln!(out, "    ///");
    let _ = writeln!(out, "    /// Returns the first decode or bounds violation.");
    let _ = writeln!(
        out,
        "    pub fn decode(kind: FrameKind, bytes: &[u8]) -> Result<Self, crate::codec::BodyError> {{"
    );
    let _ = writeln!(out, "        match kind {{");
    for row in &rows {
        let _ = writeln!(
            out,
            "            FrameKind::{} => Ok(Self::{}(crate::codec::decode_body(bytes)?)),",
            row.kind, row.kind
        );
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
}

fn emit_rust_operation_table(out: &mut String, schema: &Schema) {
    let _ = writeln!(out, "/// One row of the generated operation registry.");
    let _ = writeln!(out, "#[derive(Clone, Copy, Debug, PartialEq)]");
    let _ = writeln!(out, "pub struct OperationRow {{");
    let _ = writeln!(out, "    /// The operation's registry name.");
    let _ = writeln!(out, "    pub name: &'static str,");
    let _ = writeln!(out, "    /// The request frame code.");
    let _ = writeln!(out, "    pub request_code: u16,");
    let _ = writeln!(out, "    /// The response frame code.");
    let _ = writeln!(out, "    pub response_code: u16,");
    let _ = writeln!(out, "    /// Whether the operation is a mutation (idempotency pairing).");
    let _ = writeln!(out, "    pub mutation: bool,");
    let _ = writeln!(out, "    /// The parity-mapping column: V1 parity row ids or the explicit");
    let _ = writeln!(out, "    /// `new-capability` marker (PD-2026-08-26-14 D1).");
    let _ = writeln!(out, "    pub parity: &'static [&'static str],");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "/// The closed operation registry with the parity-mapping column.");
    let _ = writeln!(out, "pub const OPERATIONS: &[OperationRow] = &[");
    for operation in &schema.operations {
        let _ = writeln!(out, "    OperationRow {{");
        let _ = writeln!(out, "        name: \"{}\",", operation.name);
        let _ = writeln!(out, "        request_code: {},", operation.code);
        let _ = writeln!(out, "        response_code: {},", operation.response_code);
        let _ = writeln!(out, "        mutation: {},", operation.mutation);
        let _ = writeln!(out, "        parity: &[");
        for entry in &operation.parity {
            let _ = writeln!(out, "            \"{entry}\",");
        }
        let _ = writeln!(out, "        ],");
        let _ = writeln!(out, "    }},");
    }
    let _ = writeln!(out, "];");
}

// ---------------------------------------------------------------------------
// C# emission
// ---------------------------------------------------------------------------

#[allow(clippy::too_many_lines)]
fn generate_csharp(schema: &Schema) -> String {
    let mut out = String::new();
    let _ = writeln!(
        out,
        "// GENERATED by tbep-gen from crates/terrabase-tbep/schema/tbep-registry.json — do not edit by hand."
    );
    let _ =
        writeln!(out, "// Regenerate with `cargo run --bin tbep-gen` from the crate directory.");
    let _ = writeln!(out, "// Drift is checked by the Rust test suite (tests/registry_drift.rs).");
    let _ = writeln!(out, "#nullable enable");
    let _ = writeln!(out, "using MessagePack;");
    let _ = writeln!(out, "using MessagePack.Formatters;");
    let _ = writeln!(out);
    let _ = writeln!(out, "namespace TerraBase.Protocol;");
    let _ = writeln!(out);
    let _ = writeln!(out, "/// <summary>The generated TBEP registry constants.</summary>");
    let _ = writeln!(out, "public static partial class TbepRegistry");
    let _ = writeln!(out, "{{");
    let _ = writeln!(out, "    /// <summary>The protocol identity.</summary>");
    let _ = writeln!(out, "    public const string Identity = \"{}\";", schema.identity);
    let _ = writeln!(out, "    /// <summary>The registry version.</summary>");
    let _ =
        writeln!(out, "    public const string RegistryVersion = \"{}\";", schema.registry_version);
    let _ =
        writeln!(out, "    /// <summary>The wire version; unknown versions fail closed.</summary>");
    let _ = writeln!(out, "    public const byte WireVersion = {};", schema.wire_version);
    let _ = writeln!(
        out,
        "    /// <summary>The decode bound in bytes before any field parses.</summary>"
    );
    let _ = writeln!(out, "    public const int MaxFrameBytes = {};", schema.max_frame_bytes);
    let _ = writeln!(out, "    /// <summary>The detail bound of error frames.</summary>");
    let _ = writeln!(out, "    public const int MaxDetailBytes = {};", schema.max_detail_bytes);
    let _ = writeln!(out, "    /// <summary>The item bound of every list field.</summary>");
    let _ = writeln!(out, "    public const int MaxListItems = {};", schema.max_list_items);
    let _ = writeln!(
        out,
        "    /// <summary>The fixed envelope cost over the body, bin8 body header included.</summary>"
    );
    let _ =
        writeln!(out, "    public const int FixedEnvelopeBytes = {};", schema.fixed_envelope_bytes);
    let _ = writeln!(out, "    /// <summary>The stream length prefix width.</summary>");
    let _ =
        writeln!(out, "    public const int StreamPrefixBytes = {};", schema.stream_prefix_bytes);
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <summary>The closed payload-codec vocabulary.</summary>");
    let _ = writeln!(out, "    public static readonly string[] PayloadCodecs =");
    let _ = writeln!(out, "    [");
    for codec in &schema.payload_codecs {
        let _ = writeln!(out, "        \"{codec}\",");
    }
    let _ = writeln!(out, "    ];");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <summary>The closed family-kind name vocabulary.</summary>");
    let _ = writeln!(out, "    public static readonly string[] FamilyKinds =");
    let _ = writeln!(out, "    [");
    for kind in &schema.family_kinds {
        let _ = writeln!(out, "        \"{kind}\",");
    }
    let _ = writeln!(out, "    ];");
    let _ = writeln!(out);
    let _ = writeln!(
        out,
        "    /// <summary>The written bytes-on-wire budget per frame class.</summary>"
    );
    let _ = writeln!(out, "    public static readonly (string Class, int WireBytes)[] Budgets =");
    let _ = writeln!(out, "    [");
    for (class, bytes, _) in &schema.budgets {
        let _ = writeln!(out, "        (\"{class}\", {bytes}),");
    }
    let _ = writeln!(out, "    ];");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);

    for enum_def in &schema.enums {
        emit_csharp_enum(&mut out, enum_def);
    }

    let mut bodies: Vec<(&str, &[FieldDef])> = Vec::new();
    for (name, fields) in &schema.types {
        bodies.push((name, fields));
    }
    for operation in &schema.operations {
        bodies.push((&operation.request.name, &operation.request.fields));
        bodies.push((&operation.response.name, &operation.response.fields));
    }
    for notification in schema.notifications.iter().chain(schema.channel_frames.iter()) {
        bodies.push((&notification.name, &notification.fields));
    }
    for (name, fields) in &bodies {
        emit_csharp_struct(&mut out, name, fields);
    }
    emit_csharp_frame_kind(&mut out, schema);
    emit_csharp_operation_table(&mut out, schema);
    out
}

#[allow(clippy::too_many_lines)]
fn emit_csharp_enum(out: &mut String, enum_def: &EnumDef) {
    let name = pascal(&enum_def.name);
    let (tag_type, read, write_cast) = if enum_def.tag == "u16" {
        ("ushort", "ReadUInt16", "(ushort)")
    } else {
        ("byte", "ReadByte", "(byte)")
    };
    let _ = writeln!(
        out,
        "/// <summary>The generated `{}` closed vocabulary.</summary>",
        enum_def.name
    );
    let _ = writeln!(out, "[MessagePackFormatter(typeof({name}Formatter))]");
    let _ = writeln!(out, "public sealed class {name} : IEquatable<{name}>");
    let _ = writeln!(out, "{{");
    let _ = writeln!(out, "    /// <summary>The variant name.</summary>");
    let _ = writeln!(out, "    public required string Variant {{ get; init; }}");
    let _ = writeln!(out, "    /// <summary>The wire tag.</summary>");
    let _ = writeln!(out, "    public required {tag_type} Tag {{ get; init; }}");
    let _ = writeln!(out, "    /// <summary>The payload, when the variant carries one.</summary>");
    let _ = writeln!(out, "    public object? Payload {{ get; init; }}");
    let _ = writeln!(out);
    for variant in &enum_def.variants {
        let vname = pascal(&variant.name);
        if let Some(payload) = &variant.payload {
            let payload_type = csharp_type_name(&payload.type_name);
            let _ = writeln!(out, "    /// <summary>The `{}` variant.</summary>", variant.name);
            let _ = writeln!(
                out,
                "    public static {name} {vname}({payload_type} payload) => new() {{ Variant = \"{vname}\", Tag = {write_cast}{code}, Payload = payload }};",
                code = variant.code
            );
        } else {
            let _ = writeln!(out, "    /// <summary>The `{}` variant.</summary>", variant.name);
            let _ = writeln!(
                out,
                "    public static {name} {vname}() => new() {{ Variant = \"{vname}\", Tag = {write_cast}{code} }};",
                code = variant.code
            );
        }
    }
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(out, "    public bool Equals({name}? other) =>");
    let _ = writeln!(
        out,
        "        other is not null && Variant == other.Variant && Tag == other.Tag && Equals(Payload, other.Payload);"
    );
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(out, "    public override bool Equals(object? obj) => Equals(obj as {name});");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(
        out,
        "    public override int GetHashCode() => System.HashCode.Combine(Variant, Tag, Payload);"
    );
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "/// <summary>The strict formatter of <see cref=\"{name}\"/>.</summary>");
    let _ = writeln!(out, "public sealed class {name}Formatter : IMessagePackFormatter<{name}?>");
    let _ = writeln!(out, "{{");
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(
        out,
        "    public {name} Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)"
    );
    let _ = writeln!(out, "    {{");
    let _ = writeln!(out, "        if (reader.TryReadNil())");
    let _ = writeln!(out, "        {{");
    let _ = writeln!(out, "            throw TbepWire.FormatException(\"nil is not a {name}\");");
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "        var count = reader.ReadArrayHeader();");
    let _ = writeln!(out, "        var tag = reader.{read}();");
    for variant in &enum_def.variants {
        let vname = pascal(&variant.name);
        let _ = writeln!(out, "        if (tag == {write_cast}{code})", code = variant.code);
        let _ = writeln!(out, "        {{");
        if let Some(payload) = &variant.payload {
            let payload_type = csharp_type_name(&payload.type_name);
            let _ = writeln!(out, "            if (count != 2)");
            let _ = writeln!(out, "            {{");
            let _ = writeln!(
                out,
                "                throw TbepWire.FormatException(\"{vname} carries exactly one payload\");"
            );
            let _ = writeln!(out, "            }}");
            if payload_type == "byte[]" || payload_type == "string" {
                let _ = writeln!(
                    out,
                    "            var payload = options.Resolver!.GetFormatter<{payload_type}>()!.Deserialize(ref reader, options);"
                );
            } else if is_primitive(&payload.type_name)
                && !payload.type_name.starts_with('[')
                && payload.type_name != "value"
            {
                let _ = writeln!(
                    out,
                    "            var payload = reader.Read{Primitive}();",
                    Primitive = csharp_primitive_read(&payload.type_name)
                );
            } else if payload.type_name == "value" {
                let _ = writeln!(
                    out,
                    "            var payload = TbepValue.Formatter.Deserialize(ref reader, options);"
                );
            } else if payload.type_name == "[str]" {
                let _ = writeln!(
                    out,
                    "            var payload = options.Resolver!.GetFormatter<string[]>()!.Deserialize(ref reader, options);"
                );
            } else if payload.type_name == "[sync_field]" || payload.type_name.contains('[') {
                let cs = csharp_type_name(&payload.type_name);
                let _ = writeln!(
                    out,
                    "            var payload = options.Resolver!.GetFormatter<{cs}>()!.Deserialize(ref reader, options);"
                );
            } else {
                let _ = writeln!(
                    out,
                    "            var payload = options.Resolver!.GetFormatter<{payload_type}>()!.Deserialize(ref reader, options);"
                );
            }
            let _ = writeln!(
                out,
                "            return new {name} {{ Variant = \"{vname}\", Tag = tag, Payload = payload }};"
            );
        } else {
            let _ = writeln!(out, "            if (count != 1)");
            let _ = writeln!(out, "            {{");
            let _ = writeln!(
                out,
                "                throw TbepWire.FormatException(\"{vname} carries no payload\");"
            );
            let _ = writeln!(out, "            }}");
            let _ = writeln!(
                out,
                "            return new {name} {{ Variant = \"{vname}\", Tag = tag }};"
            );
        }
        let _ = writeln!(out, "        }}");
    }
    let _ = writeln!(out, "        throw TbepWire.FormatException(\"unknown {name} tag\");");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(
        out,
        "    public void Serialize(ref MessagePackWriter writer, {name}? value, MessagePackSerializerOptions options)"
    );
    let _ = writeln!(out, "    {{");
    let _ = writeln!(out, "        if (value is null)");
    let _ = writeln!(out, "        {{");
    let _ = writeln!(out, "            throw TbepWire.FormatException(\"nil is not a {name}\");");
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "        switch (value.Variant)");
    let _ = writeln!(out, "        {{");
    for variant in &enum_def.variants {
        let vname = pascal(&variant.name);
        let _ = writeln!(out, "            case \"{vname}\":");
        if let Some(payload) = &variant.payload {
            let payload_type = csharp_type_name(&payload.type_name);
            let _ = writeln!(out, "                writer.WriteArrayHeader(2);");
            let _ = writeln!(
                out,
                "                writer.Write({write_cast}{code});",
                code = variant.code
            );
            if payload_type == "byte[]"
                || payload_type == "string"
                || payload_type.contains('[')
                || payload_type == "TbepValue"
            {
                let formatter_expr = if payload_type == "TbepValue" {
                    "TbepValue.Formatter".to_string()
                } else {
                    format!("options.Resolver!.GetFormatter<{payload_type}>()!")
                };
                let _ = writeln!(
                    out,
                    "                {formatter_expr}.Serialize(ref writer, ({payload_type})value.Payload!, options);"
                );
            } else if is_primitive(&payload.type_name) {
                let _ =
                    writeln!(out, "                writer.Write(({payload_type})value.Payload!);");
            } else {
                let _ = writeln!(
                    out,
                    "                options.Resolver!.GetFormatter<{payload_type}>()!.Serialize(ref writer, ({payload_type})value.Payload!, options);"
                );
            }
            let _ = writeln!(out, "                return;");
        } else {
            let _ = writeln!(out, "                writer.WriteArrayHeader(1);");
            let _ = writeln!(
                out,
                "                writer.Write({write_cast}{code});",
                code = variant.code
            );
            let _ = writeln!(out, "                return;");
        }
    }
    let _ = writeln!(out, "            default:");
    let _ = writeln!(
        out,
        "                throw TbepWire.FormatException(\"unknown {name} variant\");"
    );
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
}

fn csharp_primitive_read(type_name: &str) -> &'static str {
    // The schema's primitive vocabulary is closed; the fallback names the
    // byte reader the generator only reaches for u8-shaped payloads.
    match type_name {
        "u16" => "UInt16",
        "u32" => "UInt32",
        "u64" => "UInt64",
        "i64" => "Int64",
        "f64" => "Double",
        "bool" => "Boolean",
        _ => "Byte",
    }
}

fn emit_csharp_struct(out: &mut String, name: &str, fields: &[FieldDef]) {
    let struct_name = pascal(name);
    let _ = writeln!(out, "/// <summary>The `{name}` frame body.</summary>");
    let _ = writeln!(out, "[MessagePackObject]");
    let _ = writeln!(out, "public sealed class {struct_name} : IEquatable<{struct_name}>");
    let _ = writeln!(out, "{{");
    for (index, field) in fields.iter().enumerate() {
        let _ = writeln!(out, "    /// <summary>The `{}` field.</summary>", field.name);
        let _ = writeln!(out, "    [Key({index})]");
        // Bodies are constructed all-at-once through init accessors;
        // `required` pins the contract for non-optional fields while
        // MessagePack's runtime setters stay free to fill every property.
        // Optional fields answer null and carry no requirement.
        let modifier = if field.optional { "" } else { "required " };
        let _ = writeln!(
            out,
            "    public {}{} {} {{ get; init; }}",
            modifier,
            field.csharp_type(),
            pascal(&field.name)
        );
    }
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(out, "    public bool Equals({struct_name}? other) => other is not null");
    for (index, field) in fields.iter().enumerate() {
        let terminator = if index + 1 == fields.len() { ";" } else { "" };
        let _ = writeln!(
            out,
            "        && TbepRegistry.FieldEquals({}, other.{}){}",
            pascal(&field.name),
            pascal(&field.name),
            terminator
        );
    }
    if fields.is_empty() {
        let _ = writeln!(out, "        ;");
    }
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(
        out,
        "    public override bool Equals(object? obj) => Equals(obj as {struct_name});"
    );
    let _ = writeln!(out);
    let _ = writeln!(out, "    /// <inheritdoc/>");
    let _ = writeln!(out, "    public override int GetHashCode()");
    let _ = writeln!(out, "    {{");
    let hash_parts: Vec<String> = fields.iter().map(|f| pascal(&f.name).clone()).collect();
    if hash_parts.is_empty() {
        let _ = writeln!(out, "        return typeof({struct_name}).GetHashCode();");
    } else {
        let _ = writeln!(out, "        return System.HashCode.Combine({});", hash_parts.join(", "));
    }
    let _ = writeln!(out, "    }}");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
}

fn emit_csharp_frame_kind(out: &mut String, schema: &Schema) {
    let rows = schema.frame_rows();
    let _ = writeln!(out, "/// <summary>The closed frame-kind registry.</summary>");
    let _ = writeln!(out, "public enum TbepFrameKind : ushort");
    let _ = writeln!(out, "{{");
    for row in &rows {
        let _ = writeln!(
            out,
            "    /// <summary>Code {} ({}; {}).</summary>",
            row.code, row.direction, row.family
        );
        let _ = writeln!(out, "    {} = {},", row.kind, row.code);
    }
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
    let _ = writeln!(out, "/// <summary>Frame-kind registry helpers.</summary>");
    let _ = writeln!(out, "public static partial class TbepRegistry");
    let _ = writeln!(out, "{{");
    let _ = writeln!(
        out,
        "    /// <summary>The per-kind registry rows (code, kind, family, class, direction, carries idempotency).</summary>"
    );
    let _ = writeln!(
        out,
        "    public static readonly (ushort Code, string Kind, string Family, string Class, string Direction, bool CarriesIdempotency)[] Frames ="
    );
    let _ = writeln!(out, "    [");
    for row in &rows {
        let _ = writeln!(
            out,
            "        ({}, \"{}\", \"{}\", \"{}\", \"{}\", {}),",
            row.code, row.kind, row.family, row.class, row.direction, row.mutation_family
        );
    }
    let _ = writeln!(out, "    ];");
    let _ = writeln!(out, "}}");
    let _ = writeln!(out);
}

fn emit_csharp_operation_table(out: &mut String, schema: &Schema) {
    let _ = writeln!(out, "/// <summary>One row of the generated operation registry.</summary>");
    let _ = writeln!(
        out,
        "public sealed record OperationRow(string Name, ushort RequestCode, ushort ResponseCode, bool Mutation, string[] Parity);"
    );
    let _ = writeln!(out);
    let _ = writeln!(out, "/// <summary>Registry helpers.</summary>");
    let _ = writeln!(out, "public static partial class TbepRegistry");
    let _ = writeln!(out, "{{");
    let _ = writeln!(
        out,
        "    /// <summary>The closed operation registry with the parity-mapping column.</summary>"
    );
    let _ = writeln!(out, "    public static readonly OperationRow[] Operations =");
    let _ = writeln!(out, "    [");
    for operation in &schema.operations {
        let parity =
            operation.parity.iter().map(|p| format!("\"{p}\"")).collect::<Vec<_>>().join(", ");
        let _ = writeln!(
            out,
            "        new OperationRow(\"{}\", {}, {}, {}, [{}]),",
            operation.name, operation.code, operation.response_code, operation.mutation, parity
        );
    }
    let _ = writeln!(out, "    ];");
    let _ = writeln!(out, "}}");
}
