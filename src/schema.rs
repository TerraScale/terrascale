//! Typed schema builder.
//!
//! `terrascale.schema.json` is the source of truth for a database. This
//! module keeps the JSON model made up of ordinary serde data types so the
//! CLI, codegen, and every SDK can exchange it without Rust details.

pub use terrascale_derive::Schema;

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::fmt;

/// The supported extracted column types.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum FieldType {
    #[serde(rename = "string")]
    String,
    #[serde(rename = "int", alias = "integer")]
    Integer,
    #[serde(rename = "float")]
    Float,
    #[serde(rename = "bool", alias = "boolean")]
    Boolean,
    #[serde(rename = "json")]
    Json,
    #[serde(rename = "bytes")]
    Bytes,
    #[serde(rename = "timestamp")]
    Timestamp,
    #[serde(rename = "text")]
    Text,
    /// A PN counter merge kernel (D-36).
    #[serde(rename = "counter-pn", alias = "pn-counter")]
    PnCounter,
    /// An add-wins set merge kernel (D-36).
    #[serde(rename = "set-add-wins", alias = "add-wins-set")]
    AddWinsSet,
    /// A remove-wins set merge kernel (D-36).
    #[serde(rename = "set-remove-wins", alias = "remove-wins-set")]
    RemoveWinsSet,
    /// A multi-value register merge kernel (D-36).
    #[serde(rename = "register-mv", alias = "mv-register")]
    MvRegister,
}

/// The collection-level conflict policy. Typed CRDT fields select their
/// kernel through [`FieldDef::merge_policy`]; this policy governs fields that
/// do not select one explicitly.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum MergePolicy {
    /// Remove-wins map semantics with an HLC tiebreak for assignments (D-36).
    #[default]
    RemoveWins,
    /// Keep the assignment with deterministic HLC/origin ordering.
    HlcTiebreak,
    /// Alias for HLC tiebreak terminology used by schema authors.
    #[serde(rename = "last-write-wins")]
    LastWriteWins,
}

/// One declared field of a collection.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct FieldDef {
    /// Field name (a root JSON path segment).
    pub name: String,
    /// Stored type used for extraction and typed queries.
    #[serde(rename = "type", alias = "field_type")]
    pub field_type: FieldType,
    /// Whether the field may be absent or null.
    #[serde(rename = "required", serialize_with = "serialize_required")]
    pub optional: bool,
    /// Whether the write path maintains an index over this field.
    #[serde(default)]
    pub indexed: bool,
    /// Whether this field is unique within a Region.
    #[serde(default, skip_serializing_if = "is_false")]
    pub unique: bool,
    /// Optional typed CRDT merge kernel for this field.
    #[serde(
        default,
        rename = "mergePolicy",
        skip_serializing_if = "Option::is_none"
    )]
    pub merge_policy: Option<FieldType>,
}

const fn is_false(value: &bool) -> bool {
    !*value
}

fn serialize_required<S>(optional: &bool, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    serializer.serialize_bool(!optional)
}

#[derive(Deserialize)]
struct FieldDefWire {
    name: String,
    #[serde(rename = "type", alias = "field_type")]
    field_type: FieldType,
    #[serde(default)]
    required: Option<bool>,
    #[serde(default)]
    optional: Option<bool>,
    #[serde(default)]
    indexed: bool,
    #[serde(default)]
    unique: bool,
    #[serde(default, rename = "mergePolicy", alias = "merge_policy")]
    merge_policy: Option<FieldType>,
}

impl<'de> Deserialize<'de> for FieldDef {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let wire = FieldDefWire::deserialize(deserializer)?;
        let optional = match (wire.required, wire.optional) {
            (Some(_), Some(_)) => {
                return Err(serde::de::Error::custom(
                    "field schema cannot contain both required and optional",
                ));
            }
            (Some(required), None) => !required,
            (None, Some(optional)) => optional,
            (None, None) => false,
        };
        Ok(Self {
            name: wire.name,
            field_type: wire.field_type,
            optional,
            indexed: wire.indexed,
            unique: wire.unique,
            merge_policy: wire.merge_policy,
        })
    }
}

impl FieldDef {
    /// Declare a required field.
    #[must_use]
    pub fn required(name: impl Into<String>, field_type: FieldType) -> Self {
        Self {
            name: name.into(),
            field_type,
            optional: false,
            indexed: false,
            unique: false,
            merge_policy: None,
        }
    }

    /// Declare an optional field.
    #[must_use]
    pub fn optional(name: impl Into<String>, field_type: FieldType) -> Self {
        Self {
            optional: true,
            ..Self::required(name, field_type)
        }
    }

    /// Mark the field as indexed (maintained in the write transaction).
    #[must_use]
    pub fn indexed(mut self) -> Self {
        self.indexed = true;
        self
    }

    /// Mark the field as unique within its Region.
    #[must_use]
    pub fn unique(mut self) -> Self {
        self.unique = true;
        self.indexed = true;
        self
    }

    /// Select a typed CRDT merge kernel for this field.
    #[must_use]
    pub fn merge_policy(mut self, policy: FieldType) -> Self {
        self.merge_policy = Some(policy);
        self
    }
}

/// A declared ordinary index. Indexes are maintained transactionally with
/// writes, so a visible index can never produce a stale read.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct IndexDef {
    /// Stable name used in schema diffs and index plans.
    pub name: String,
    /// Ordered field names in the index key.
    pub fields: Vec<String>,
    /// Whether duplicate keys are rejected within a Region.
    #[serde(default, skip_serializing_if = "is_false")]
    pub unique: bool,
}

impl IndexDef {
    /// Declare an index over one or more fields.
    #[must_use]
    pub fn new<I, S>(name: impl Into<String>, fields: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        Self {
            name: name.into(),
            fields: fields.into_iter().map(Into::into).collect(),
            unique: false,
        }
    }

    /// Declare an index whose name is derived from its sole field.
    #[must_use]
    pub fn on(field: impl Into<String>) -> Self {
        let field = field.into();
        Self::new(field.clone(), [field])
    }

    /// Make this index unique within its Region.
    #[must_use]
    pub fn unique(mut self) -> Self {
        self.unique = true;
        self
    }
}

/// A collection TTL declaration. The field must contain a timestamp value.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TtlDef {
    /// Root field containing the expiry timestamp.
    pub field: String,
}

impl TtlDef {
    #[must_use]
    pub fn new(field: impl Into<String>) -> Self {
        Self {
            field: field.into(),
        }
    }
}

impl From<String> for TtlDef {
    fn from(field: String) -> Self {
        Self::new(field)
    }
}

impl From<&str> for TtlDef {
    fn from(field: &str) -> Self {
        Self::new(field)
    }
}

/// The schema of one collection.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CollectionSchema {
    /// Collection/table name.
    pub name: String,
    /// Declared extracted fields.
    pub fields: Vec<FieldDef>,
    /// Explicit indexes in addition to indexes requested on fields.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub indexes: Vec<IndexDef>,
    /// Optional expiry policy.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ttl: Option<TtlDef>,
    /// Retained document history window, in days (D-25).
    #[serde(
        default = "CollectionSchema::default_history_window_days",
        rename = "historyRetentionDays",
        alias = "historyWindowDays"
    )]
    pub history_window_days: u8,
    /// Default conflict policy for fields without a typed merge kernel.
    #[serde(rename = "mergePolicy", default)]
    pub merge_policy: MergePolicy,
}

impl CollectionSchema {
    /// The default history retention required by D-25.
    pub const DEFAULT_HISTORY_WINDOW_DAYS: u8 = 7;
    /// The minimum supported history window.
    pub const MIN_HISTORY_WINDOW_DAYS: u8 = 1;
    /// The maximum supported history window.
    pub const MAX_HISTORY_WINDOW_DAYS: u8 = 30;

    const fn default_history_window_days() -> u8 {
        Self::DEFAULT_HISTORY_WINDOW_DAYS
    }

    /// Start a collection schema with the default seven-day history window.
    #[must_use]
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            fields: Vec::new(),
            indexes: Vec::new(),
            ttl: None,
            history_window_days: Self::DEFAULT_HISTORY_WINDOW_DAYS,
            merge_policy: MergePolicy::default(),
        }
    }

    /// Append a field declaration.
    #[must_use]
    pub fn field(mut self, field: FieldDef) -> Self {
        self.fields.push(field);
        self
    }

    /// Append an explicit index declaration.
    #[must_use]
    pub fn index(mut self, index: IndexDef) -> Self {
        self.indexes.push(index);
        self
    }

    /// Configure the collection TTL field.
    #[must_use]
    pub fn ttl(mut self, field: impl Into<TtlDef>) -> Self {
        self.ttl = Some(field.into());
        self
    }

    /// Alias for [`Self::ttl`].
    #[must_use]
    pub fn ttl_field(self, field: impl Into<TtlDef>) -> Self {
        self.ttl(field)
    }

    /// Set the retained history window. Use [`Self::validate`] or
    /// [`SchemaBuilder::try_build`] to receive an error for an out-of-range
    /// value.
    #[must_use]
    pub fn history_window_days(mut self, days: u8) -> Self {
        self.history_window_days = days;
        self
    }

    /// Alias using the terminology in the other SDKs.
    #[must_use]
    pub fn history_retention_days(self, days: u8) -> Self {
        self.history_window_days(days)
    }

    /// Short alias for [`Self::history_window_days`].
    #[must_use]
    pub fn history_window(self, days: u8) -> Self {
        self.history_window_days(days)
    }

    /// Set the default policy for untyped fields.
    #[must_use]
    pub fn merge_policy(mut self, policy: MergePolicy) -> Self {
        self.merge_policy = policy;
        self
    }

    /// Alias for [`Self::merge_policy`].
    #[must_use]
    pub fn merge(self, policy: MergePolicy) -> Self {
        self.merge_policy(policy)
    }

    /// Validate names, references, and retention bounds before applying JSON.
    pub fn validate(&self) -> Result<(), SchemaError> {
        if self.name.trim().is_empty() {
            return Err(SchemaError::EmptyCollectionName);
        }
        if !is_valid_identifier(&self.name) {
            return Err(SchemaError::InvalidIdentifier {
                kind: "collection",
                name: self.name.clone(),
            });
        }
        if !(Self::MIN_HISTORY_WINDOW_DAYS..=Self::MAX_HISTORY_WINDOW_DAYS)
            .contains(&self.history_window_days)
        {
            return Err(SchemaError::HistoryWindowOutOfRange {
                days: self.history_window_days,
            });
        }

        for (position, field) in self.fields.iter().enumerate() {
            if field.name.trim().is_empty() {
                return Err(SchemaError::EmptyFieldName { position });
            }
            if !is_valid_identifier(&field.name) {
                return Err(SchemaError::InvalidIdentifier {
                    kind: "field",
                    name: field.name.clone(),
                });
            }
            if self.fields[..position]
                .iter()
                .any(|other| other.name == field.name)
            {
                return Err(SchemaError::DuplicateField(field.name.clone()));
            }
            if let Some(policy) = field.merge_policy {
                if !matches!(
                    policy,
                    FieldType::PnCounter
                        | FieldType::AddWinsSet
                        | FieldType::RemoveWinsSet
                        | FieldType::MvRegister
                ) {
                    return Err(SchemaError::InvalidFieldMergePolicy(field.name.clone()));
                }
            }
        }
        for (position, index) in self.indexes.iter().enumerate() {
            if index.name.trim().is_empty() || index.fields.is_empty() {
                return Err(SchemaError::InvalidIndex { position });
            }
            if !is_valid_identifier(&index.name) {
                return Err(SchemaError::InvalidIdentifier {
                    kind: "index",
                    name: index.name.clone(),
                });
            }
            if self.indexes[..position]
                .iter()
                .any(|other| other.name == index.name)
            {
                return Err(SchemaError::DuplicateIndex(index.name.clone()));
            }
            for field in &index.fields {
                if !is_valid_identifier(field) {
                    return Err(SchemaError::InvalidIdentifier {
                        kind: "index field",
                        name: field.clone(),
                    });
                }
                if !self.fields.iter().any(|declared| declared.name == *field) {
                    return Err(SchemaError::UnknownIndexField {
                        index: index.name.clone(),
                        field: field.clone(),
                    });
                }
            }
        }
        if let Some(ttl) = &self.ttl {
            if !is_valid_identifier(&ttl.field) {
                return Err(SchemaError::InvalidIdentifier {
                    kind: "TTL field",
                    name: ttl.field.clone(),
                });
            }
            let Some(field) = self.fields.iter().find(|field| field.name == ttl.field) else {
                return Err(SchemaError::UnknownTtlField(ttl.field.clone()));
            };
            if field.field_type != FieldType::Timestamp {
                return Err(SchemaError::TtlFieldNotTimestamp(ttl.field.clone()));
            }
        }
        Ok(())
    }

    /// Serialize this collection as schema JSON after validation.
    pub fn to_json(&self) -> Result<String, SchemaJsonError> {
        self.validate().map_err(SchemaJsonError::InvalidSchema)?;
        serde_json::to_string(self).map_err(SchemaJsonError::Serialize)
    }
}

/// A validation failure in a schema declaration.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SchemaError {
    EmptyCollectionName,
    EmptyFieldName { position: usize },
    InvalidIdentifier { kind: &'static str, name: String },
    DuplicateField(String),
    HistoryWindowOutOfRange { days: u8 },
    InvalidIndex { position: usize },
    DuplicateIndex(String),
    UnknownIndexField { index: String, field: String },
    UnknownTtlField(String),
    TtlFieldNotTimestamp(String),
    InvalidFieldMergePolicy(String),
}

impl fmt::Display for SchemaError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::EmptyCollectionName => f.write_str("collection name cannot be empty"),
            Self::EmptyFieldName { position } => {
                write!(f, "field at position {position} has no name")
            }
            Self::InvalidIdentifier { kind, name } => {
                write!(f, "{kind} name {name:?} must match [A-Za-z_][A-Za-z0-9_]*")
            }
            Self::DuplicateField(name) => write!(f, "duplicate field {name:?}"),
            Self::HistoryWindowOutOfRange { days } => {
                write!(
                    f,
                    "history window must be between 1 and 30 days (got {days})"
                )
            }
            Self::InvalidIndex { position } => {
                write!(f, "index at position {position} has no name or fields")
            }
            Self::DuplicateIndex(name) => write!(f, "duplicate index {name:?}"),
            Self::UnknownIndexField { index, field } => {
                write!(f, "index {index:?} references unknown field {field:?}")
            }
            Self::UnknownTtlField(field) => write!(f, "TTL references unknown field {field:?}"),
            Self::TtlFieldNotTimestamp(field) => {
                write!(f, "TTL field {field:?} must have type timestamp")
            }
            Self::InvalidFieldMergePolicy(field) => {
                write!(f, "field {field:?} has a non-CRDT merge policy")
            }
        }
    }
}

impl std::error::Error for SchemaError {}

/// Error returned when producing schema JSON.
#[derive(Debug)]
pub enum SchemaJsonError {
    InvalidSchema(SchemaError),
    Serialize(serde_json::Error),
}

impl fmt::Display for SchemaJsonError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidSchema(error) => error.fmt(f),
            Self::Serialize(error) => error.fmt(f),
        }
    }
}

impl std::error::Error for SchemaJsonError {}

impl From<serde_json::Error> for SchemaJsonError {
    fn from(error: serde_json::Error) -> Self {
        Self::Serialize(error)
    }
}

/// Fluent builder for a whole database schema (one or more collections).
#[derive(Debug, Default)]
pub struct SchemaBuilder {
    collections: Vec<CollectionSchema>,
}

impl SchemaBuilder {
    /// Start an empty schema.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Add a fully built collection schema.
    #[must_use]
    pub fn collection(mut self, collection: CollectionSchema) -> Self {
        self.collections.push(collection);
        self
    }

    /// Add a document type via its [`Document`] implementation.
    #[must_use]
    pub fn document<T: Document>(mut self) -> Self {
        self.collections.push(T::schema());
        self
    }

    /// Validate and return the collection schemas.
    pub fn build(self) -> Result<Vec<CollectionSchema>, SchemaError> {
        self.try_build()
    }

    /// Validate and return the collection schemas.
    pub fn try_build(self) -> Result<Vec<CollectionSchema>, SchemaError> {
        for collection in &self.collections {
            collection.validate()?;
        }
        Ok(self.collections)
    }

    /// Serialize the complete schema document used by `terrascale push`.
    pub fn to_json(self) -> Result<String, SchemaJsonError> {
        let collections = self.build().map_err(SchemaJsonError::InvalidSchema)?;
        serde_json::to_string(&SchemaDocument { collections }).map_err(SchemaJsonError::Serialize)
    }
}

/// The root `terrascale.schema.json` document.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SchemaDocument {
    pub collections: Vec<CollectionSchema>,
}

/// A document type bound to one collection.
pub trait Document {
    /// Collection this document type lives in.
    fn collection() -> &'static str;
    /// Typed schema used by `push` and codegen.
    fn schema() -> CollectionSchema;
}

/// Whether a schema name can be emitted as an unquoted SQL identifier.
pub(crate) fn is_valid_identifier(value: &str) -> bool {
    let mut chars = value.chars();
    let Some(first) = chars.next() else {
        return false;
    };
    value.len() <= 255
        && (first.is_ascii_alphabetic() || first == '_')
        && chars.all(|character| character.is_ascii_alphanumeric() || character == '_')
}
