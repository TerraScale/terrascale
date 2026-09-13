//! Typed schema management for the public API.
//!
//! This module owns the contract represented by `terrascale.schema.json` and
//! the small control-plane state needed to apply it.  It deliberately does
//! not open a database, issue SQL, or mutate documents.  A member supplies a
//! [`SchemaDurablePort`] backed by its durable control store; if that port is
//! absent, every operation fails closed with an actionable unavailable error.
//!
//! The HTTP router is intentionally independent of `crate::api::ApiState`.
//! [`SchemaHttpState`] is the parent-wiring seam: the parent supplies the
//! durable service and a database identity resolver, then merges
//! [`router`] into its authenticated route table.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::sync::{Arc, Mutex};

use axum::Router;
use axum::body::Body;
use axum::extract::{Path, State};
use axum::http::header::{CONTENT_TYPE, HeaderValue};
use axum::http::header::{ETAG, IF_MATCH};
use axum::http::{HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use terrabase::families::migrations::{
    MigrationCoordinator, MigrationError, MigrationOptions, MigrationPlan, MigrationReceipt,
    MigrationStatus as FamilyMigrationStatus, SchemaMigration,
};
use terrabase::families::{
    CanonicalCollectionSchema, CanonicalFieldType, CanonicalIndexKind, CanonicalSchemaDocument,
    MergePolicy as FamilyMergePolicy, SchemaBranchPin, SchemaCatalogAdapter,
    SchemaCatalogCasOutcome, SchemaCatalogCasRequest, SchemaCatalogError, SchemaCatalogExpectation,
    SchemaCatalogRecord, SchemaMigrationStatus, SchemaVersion,
};
use terrabase::identity::SchemaCatalogEntryUlid;
use terrabase::types::{CollectionName, DocumentValue, TenantScope};

/// The largest accepted schema document in bytes.
pub const MAX_SCHEMA_BYTES: usize = 512 * 1024;
/// Exact route hook merged into the authenticated parent router.
pub const SCHEMA_ROUTE: &str = "/api/v1/databases/{database}/collections/{collection}/schema";
/// The largest accepted collection name, field name, or index name.
pub const MAX_IDENTIFIER_BYTES: usize = 255;
/// The largest number of fields in one collection.
pub const MAX_FIELDS: usize = 512;
/// The largest number of collections in one schema document.
pub const MAX_COLLECTIONS: usize = 512;
/// The largest number of explicit indexes in one collection.
pub const MAX_INDEXES: usize = 256;
/// The largest number of rename operations in one apply.
pub const MAX_RENAMES: usize = 256;
/// The largest number of remembered idempotency keys per target.
pub const MAX_IDEMPOTENCY_KEYS: usize = 128;
/// The largest number of migration steps retained in one status record.
pub const MAX_MIGRATION_STEPS: usize = 512;
/// The largest idempotency key accepted by the service.
pub const MAX_IDEMPOTENCY_KEY_BYTES: usize = 256;
/// JavaScript's `Number.MAX_SAFE_INTEGER`, used by the schema contract.
pub const MAX_SCHEMA_VERSION: u64 = 9_007_199_254_740_991;

/// A durable target.  `branch` is part of the identity, not a mutable field
/// in a schema record, so a schema can never accidentally cross a branch.
#[derive(
    Clone, Debug, Eq, Ord, PartialEq, PartialOrd, Serialize, Deserialize, utoipa::ToSchema,
)]
pub struct SchemaTarget {
    /// The immutable database identifier.
    pub database: String,
    /// The immutable branch pin (`main`, or a cloud development branch).
    pub branch: String,
    /// The collection whose schema is managed.
    pub collection: String,
}

/// Database identity supplied by the control plane.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub struct DatabaseIdentity {
    /// Immutable database identifier/incarnation.
    pub database_id: String,
    /// Immutable placement lock and selected regions, in control-plane order.
    pub placement: Vec<String>,
}

/// The supported scalar and CRDT field types in `terrascale.schema.json`.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(rename_all = "kebab-case")]
pub enum FieldType {
    /// UTF-8 string.
    String,
    /// Signed integer.  The wire alias `integer` is canonicalized to `int`.
    #[serde(rename = "int", alias = "integer")]
    Int,
    /// IEEE floating-point number.
    Float,
    /// Boolean.  The wire alias `boolean` is canonicalized to `bool`.
    #[serde(rename = "bool", alias = "boolean")]
    Bool,
    /// Opaque bytes.
    Bytes,
    /// Arbitrary JSON value.
    Json,
    /// RFC3339 timestamp.
    Timestamp,
    /// Full-text string.
    Text,
    /// PN counter CRDT.
    #[serde(rename = "counter-pn", alias = "pn-counter")]
    CounterPn,
    /// Add-wins set CRDT.
    #[serde(rename = "set-add-wins", alias = "add-wins-set")]
    SetAddWins,
    /// Remove-wins set CRDT.
    #[serde(rename = "set-remove-wins", alias = "remove-wins-set")]
    SetRemoveWins,
    /// Multi-value register CRDT.
    #[serde(rename = "register-mv", alias = "mv-register")]
    RegisterMv,
}

impl FieldType {
    fn is_crdt(self) -> bool {
        matches!(self, Self::CounterPn | Self::SetAddWins | Self::SetRemoveWins | Self::RegisterMv)
    }

    fn is_fts(self) -> bool {
        matches!(self, Self::String | Self::Text)
    }
}

/// Collection-level merge policy.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(rename_all = "kebab-case")]
pub enum MergePolicy {
    /// Remove wins when concurrent changes touch one field.
    #[default]
    RemoveWins,
    /// HLC is used as the deterministic tiebreaker.
    HlcTiebreak,
    /// Last write wins.
    LastWriteWins,
}

/// A typed field descriptor.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "published schema flags describe independent field constraints"
)]
pub struct FieldDescriptor {
    /// Field identifier.
    pub name: String,
    /// Field value type.
    #[serde(alias = "field_type")]
    pub r#type: FieldType,
    /// Whether writes must contain this field.
    #[serde(default = "default_true")]
    pub required: bool,
    /// Whether the extracted value is indexed.
    #[serde(default)]
    pub indexed: bool,
    /// Whether the extracted value has a uniqueness constraint.
    #[serde(default, skip_serializing_if = "is_false")]
    pub unique: bool,
    /// Optional field-level CRDT merge kernel.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    #[serde(rename = "mergePolicy", alias = "merge_policy", alias = "merge")]
    pub merge_policy: Option<FieldType>,
    /// Marks this field as an FTS field; normalization turns it into an FTS
    /// index descriptor and omits this builder-only flag from canonical JSON.
    #[serde(default, skip_serializing_if = "is_false")]
    pub fts: bool,
}

/// An ordinary or FTS index descriptor.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
#[schema(as = SchemaIndexDescriptor)]
pub struct IndexDescriptor {
    /// Stable index identifier.
    pub name: String,
    /// Ordered extracted fields in the index key.
    pub fields: Vec<String>,
    /// Whether duplicate values are refused within a region.
    #[serde(default, skip_serializing_if = "is_false")]
    pub unique: bool,
    /// Index implementation kind; omitted means the canonical B-tree kind.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<IndexKind>,
}

/// Index implementation kind.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, utoipa::ToSchema)]
#[serde(rename_all = "kebab-case")]
pub enum IndexKind {
    /// Extracted-value B-tree index.
    Btree,
    /// Full-text search index.
    Fts,
}

impl<'de> Deserialize<'de> for IndexKind {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let value = String::deserialize(deserializer)?;
        match value.as_str() {
            "btree" | "value" => Ok(Self::Btree),
            "fts" | "text" | "full-text" => Ok(Self::Fts),
            _ => Err(serde::de::Error::custom(format!("unknown index kind {value:?}"))),
        }
    }
}

/// Explicit full-text descriptor.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub struct FtsDescriptor {
    /// Fields indexed for full-text search.
    pub fields: Vec<String>,
}

impl<'de> Deserialize<'de> for FtsDescriptor {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let value = Value::deserialize(deserializer)?;
        match value {
            Value::String(field) => Ok(Self { fields: vec![field] }),
            Value::Array(fields) => fields
                .into_iter()
                .map(|field| {
                    field
                        .as_str()
                        .map(ToOwned::to_owned)
                        .ok_or_else(|| serde::de::Error::custom("FTS fields must be strings"))
                })
                .collect::<Result<Vec<_>, _>>()
                .map(|fields| Self { fields }),
            Value::Object(mut object) => {
                let fields = object
                    .remove("fields")
                    .ok_or_else(|| serde::de::Error::custom("FTS object must contain fields"))?;
                if !object.is_empty() {
                    return Err(serde::de::Error::custom("unknown FTS property"));
                }
                let fields = fields
                    .as_array()
                    .ok_or_else(|| serde::de::Error::custom("FTS fields must be an array"))?;
                fields
                    .iter()
                    .map(|field| {
                        field
                            .as_str()
                            .map(ToOwned::to_owned)
                            .ok_or_else(|| serde::de::Error::custom("FTS fields must be strings"))
                    })
                    .collect::<Result<Vec<_>, _>>()
                    .map(|fields| Self { fields })
            }
            _ => Err(serde::de::Error::custom("FTS must be a field, array, or object")),
        }
    }
}

/// Explicit TTL descriptor.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub struct TtlDescriptor {
    /// Timestamp field used as the expiry deadline.
    pub field: String,
}

/// A first-class field rename migration.
#[derive(
    Clone, Debug, Eq, Ord, PartialEq, PartialOrd, Serialize, Deserialize, utoipa::ToSchema,
)]
#[serde(deny_unknown_fields)]
pub struct FieldRename {
    /// Existing field name in stored documents.
    pub from: String,
    /// New field name in the target schema.
    pub to: String,
}

/// Canonical collection schema, matching the SDK's `CollectionSchema`.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub struct CollectionSchema {
    /// Collection identifier.
    pub name: String,
    /// Typed extracted field descriptors.
    #[serde(default)]
    pub fields: Vec<FieldDescriptor>,
    /// Explicit B-tree and FTS index descriptors.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub indexes: Vec<IndexDescriptor>,
    /// Optional full-text descriptor.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fts: Option<FtsDescriptor>,
    /// Optional TTL descriptor.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ttl: Option<TtlDescriptor>,
    /// Rename operations from the immediately previous schema.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub renames: Vec<FieldRename>,
    /// Schema version pinned by the branch.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<u64>,
    /// Retained history window, in days.
    #[serde(default = "default_history_days")]
    #[serde(rename = "historyRetentionDays", alias = "historyWindowDays", alias = "history")]
    pub history_retention_days: u32,
    /// Collection-level CRDT merge policy.
    #[serde(default)]
    #[serde(rename = "mergePolicy", alias = "merge")]
    pub merge_policy: MergePolicy,
}

/// The complete `terrascale.schema.json` document used by codegen and push.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SchemaJson {
    /// Collection declarations in canonical name order.
    pub collections: Vec<CollectionSchema>,
    /// Optional document format version.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<u64>,
}

/// Normalize and validate a complete schema document using the SDK contract.
///
/// # Errors
///
/// Returns a validation error for invalid versions, excess collections, duplicate
/// names, or invalid collection definitions.
pub fn normalize_schema_json(mut schema: SchemaJson) -> Result<SchemaJson, SchemaError> {
    if let Some(version) = schema.version {
        validate_version(version)?;
    }
    if schema.collections.len() > MAX_COLLECTIONS {
        return Err(SchemaError::invalid("schema declares too many collections"));
    }
    let mut names = BTreeSet::new();
    for collection in &mut schema.collections {
        *collection = normalize_schema(collection.clone())?;
        if !names.insert(collection.name.clone()) {
            return Err(SchemaError::invalid(format!(
                "duplicate collection '{}'",
                collection.name
            )));
        }
    }
    schema.collections.sort_by(|left, right| left.name.cmp(&right.name));
    Ok(schema)
}

/// Converts the server DTO to the engine's one canonical schema contract.
///
/// The conversion builds the family's canonical [`DocumentValue`] shape and
/// parses it with the family codec, which is the sole owner of canonical
/// field/index/TTL/merge spellings.  Consequently every durable adapter
/// receives the same value that the engine and SDKs verify.
///
/// # Errors
///
/// Returns schema normalization or canonical codec validation errors.
pub fn canonical_schema_document(
    schema: &SchemaJson,
) -> Result<CanonicalSchemaDocument, SchemaError> {
    let schema = normalize_schema_json(schema.clone())?;
    let collections = schema.collections.iter().map(canonical_collection_value).collect::<Vec<_>>();
    let mut document =
        BTreeMap::from([("collections".to_owned(), DocumentValue::Array(collections))]);
    if let Some(version) = schema.version {
        document.insert(
            "version".to_owned(),
            DocumentValue::Integer(i64::try_from(version).unwrap_or(i64::MAX)),
        );
    }
    CanonicalSchemaDocument::from_value(&DocumentValue::Object(document))
        .map_err(|_| SchemaError::invalid("schema is not a canonical engine schema document"))
}

/// Converts a canonical family document into the wire DTO used by the HTTP
/// handlers.  This is useful to adapters that must expose the exact
/// `terrascale.schema.json` bytes after a restart.
pub fn schema_json_from_canonical(schema: &CanonicalSchemaDocument) -> SchemaJson {
    let collections = schema.collections().iter().map(collection_from_canonical).collect();
    SchemaJson { collections, version: schema.version().map(SchemaVersion::get) }
}

/// Builds one collection's canonical codec value.
///
/// Spellings are produced with the family's own `as_str` vocabulary so the
/// subsequent [`CanonicalSchemaDocument::from_value`] parse applies the exact
/// engine validation (identifiers, TTL ownership, rename shape, retention
/// window, and merge policy) without naming crate-private family types.
fn canonical_collection_value(schema: &CollectionSchema) -> DocumentValue {
    let mut fields = Vec::with_capacity(schema.fields.len());
    for field in &schema.fields {
        let mut value = BTreeMap::from([
            ("name".to_owned(), DocumentValue::String(field.name.clone())),
            (
                "type".to_owned(),
                DocumentValue::String(canonical_field_type(field.r#type).as_str().to_owned()),
            ),
            ("required".to_owned(), DocumentValue::Boolean(field.required)),
            ("indexed".to_owned(), DocumentValue::Boolean(field.indexed)),
        ]);
        if field.unique {
            value.insert("unique".to_owned(), DocumentValue::Boolean(true));
        }
        if let Some(policy) = field.merge_policy {
            value.insert(
                "mergePolicy".to_owned(),
                DocumentValue::String(canonical_field_type(policy).as_str().to_owned()),
            );
        }
        fields.push(DocumentValue::Object(value));
    }
    let mut indexes = Vec::with_capacity(schema.indexes.len());
    for index in &schema.indexes {
        let kind = match index.kind.unwrap_or(IndexKind::Btree) {
            IndexKind::Btree => CanonicalIndexKind::BTree,
            IndexKind::Fts => CanonicalIndexKind::Fts,
        };
        let mut value = BTreeMap::from([
            ("name".to_owned(), DocumentValue::String(index.name.clone())),
            (
                "fields".to_owned(),
                DocumentValue::Array(
                    index.fields.iter().cloned().map(DocumentValue::String).collect(),
                ),
            ),
            ("kind".to_owned(), DocumentValue::String(kind.as_str().to_owned())),
        ]);
        if index.unique {
            value.insert("unique".to_owned(), DocumentValue::Boolean(true));
        }
        indexes.push(DocumentValue::Object(value));
    }
    let mut collection = BTreeMap::from([
        ("name".to_owned(), DocumentValue::String(schema.name.clone())),
        ("fields".to_owned(), DocumentValue::Array(fields)),
        (
            "historyRetentionDays".to_owned(),
            DocumentValue::Integer(i64::from(schema.history_retention_days)),
        ),
        (
            "mergePolicy".to_owned(),
            DocumentValue::String(canonical_merge_policy(schema.merge_policy).as_str().to_owned()),
        ),
    ]);
    if !indexes.is_empty() {
        collection.insert("indexes".to_owned(), DocumentValue::Array(indexes));
    }
    if let Some(ttl) = &schema.ttl {
        collection.insert(
            "ttl".to_owned(),
            DocumentValue::Object(BTreeMap::from([(
                "field".to_owned(),
                DocumentValue::String(ttl.field.clone()),
            )])),
        );
    }
    if !schema.renames.is_empty() {
        collection.insert(
            "renames".to_owned(),
            DocumentValue::Array(
                schema
                    .renames
                    .iter()
                    .map(|rename| {
                        DocumentValue::Object(BTreeMap::from([
                            ("from".to_owned(), DocumentValue::String(rename.from.clone())),
                            ("to".to_owned(), DocumentValue::String(rename.to.clone())),
                        ]))
                    })
                    .collect(),
            ),
        );
    }
    if let Some(version) = schema.version {
        collection.insert(
            "version".to_owned(),
            DocumentValue::Integer(i64::try_from(version).unwrap_or(i64::MAX)),
        );
    }
    DocumentValue::Object(collection)
}

fn collection_from_canonical(schema: &CanonicalCollectionSchema) -> CollectionSchema {
    let fields = schema
        .fields()
        .iter()
        .map(|field| FieldDescriptor {
            name: field.name().to_owned(),
            r#type: server_field_type(field.kind()),
            required: field.required(),
            indexed: field.indexed(),
            unique: field.unique(),
            merge_policy: field.merge_policy().map(server_field_type),
            fts: false,
        })
        .collect();
    let indexes = schema
        .indexes()
        .iter()
        .map(|index| IndexDescriptor {
            name: index.name().to_owned(),
            fields: index.fields().to_vec(),
            unique: index.unique(),
            kind: Some(match index.kind() {
                CanonicalIndexKind::BTree => IndexKind::Btree,
                CanonicalIndexKind::Fts => IndexKind::Fts,
            }),
        })
        .collect();
    let ttl = schema.ttl().map(|ttl| TtlDescriptor { field: ttl.field().to_owned() });
    let renames = schema
        .renames()
        .iter()
        .map(|rename| FieldRename { from: rename.from().to_owned(), to: rename.to().to_owned() })
        .collect();
    CollectionSchema {
        name: schema.name().to_owned(),
        fields,
        indexes,
        fts: None,
        ttl,
        renames,
        version: schema.version().map(SchemaVersion::get),
        history_retention_days: schema.history_retention_days(),
        merge_policy: server_merge_policy(schema.merge_policy()),
    }
}

const fn canonical_field_type(value: FieldType) -> CanonicalFieldType {
    match value {
        FieldType::String => CanonicalFieldType::String,
        FieldType::Int => CanonicalFieldType::Int,
        FieldType::Float => CanonicalFieldType::Float,
        FieldType::Bool => CanonicalFieldType::Bool,
        FieldType::Bytes => CanonicalFieldType::Bytes,
        FieldType::Json => CanonicalFieldType::Json,
        FieldType::Timestamp => CanonicalFieldType::Timestamp,
        FieldType::Text => CanonicalFieldType::Text,
        FieldType::CounterPn => CanonicalFieldType::CounterPn,
        FieldType::SetAddWins => CanonicalFieldType::SetAddWins,
        FieldType::SetRemoveWins => CanonicalFieldType::SetRemoveWins,
        FieldType::RegisterMv => CanonicalFieldType::RegisterMv,
    }
}

const fn server_field_type(value: CanonicalFieldType) -> FieldType {
    match value {
        CanonicalFieldType::String => FieldType::String,
        CanonicalFieldType::Int => FieldType::Int,
        CanonicalFieldType::Float => FieldType::Float,
        CanonicalFieldType::Bool => FieldType::Bool,
        CanonicalFieldType::Bytes => FieldType::Bytes,
        CanonicalFieldType::Json => FieldType::Json,
        CanonicalFieldType::Timestamp => FieldType::Timestamp,
        CanonicalFieldType::Text => FieldType::Text,
        CanonicalFieldType::CounterPn => FieldType::CounterPn,
        CanonicalFieldType::SetAddWins => FieldType::SetAddWins,
        CanonicalFieldType::SetRemoveWins => FieldType::SetRemoveWins,
        CanonicalFieldType::RegisterMv => FieldType::RegisterMv,
    }
}

const fn canonical_merge_policy(value: MergePolicy) -> FamilyMergePolicy {
    match value {
        MergePolicy::RemoveWins => FamilyMergePolicy::RemoveWins,
        MergePolicy::HlcTiebreak => FamilyMergePolicy::HlcTiebreak,
        MergePolicy::LastWriteWins => FamilyMergePolicy::LastWriteWins,
    }
}

const fn server_merge_policy(value: FamilyMergePolicy) -> MergePolicy {
    match value {
        FamilyMergePolicy::RemoveWins => MergePolicy::RemoveWins,
        FamilyMergePolicy::HlcTiebreak => MergePolicy::HlcTiebreak,
        FamilyMergePolicy::LastWriteWins => MergePolicy::LastWriteWins,
    }
}

/// The durable response to an apply operation.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub struct SchemaDocument {
    /// Target identity.
    pub target: SchemaTarget,
    /// Immutable database identity observed when this record was created.
    pub identity: DatabaseIdentity,
    /// Canonical schema JSON.
    pub schema: CollectionSchema,
    /// Monotonic HTTP/API generation used by `If-Match`.
    #[schema(format = "uint64", minimum = 0)]
    pub generation: u64,
    /// Durable branch-pinned schema version.
    #[schema(format = "uint64", minimum = 1, maximum = 9_007_199_254_740_991_u64)]
    pub schema_version: u64,
    /// Current resumable migration, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub migration: Option<MigrationStatus>,
}

/// Resumable migration state.  The worker that processes documents reports
/// progress through [`SchemaService::record_backfill_progress`].
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub struct MigrationStatus {
    /// Stable migration operation identifier.
    pub operation_id: String,
    /// Migration lifecycle state.
    pub state: MigrationState,
    /// Ordered rename/backfill steps.
    pub steps: Vec<MigrationStep>,
    /// Number of documents known to the worker at operation creation.
    #[schema(format = "uint64", minimum = 0)]
    pub total: u64,
    /// Number of documents durably acknowledged by the worker.
    #[schema(format = "uint64", minimum = 0)]
    pub completed: u64,
    /// Opaque resume cursor.  It is never interpreted as SQL.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next_cursor: Option<String>,
    /// Actionable status text for dashboard/CLI consumers.
    pub message: String,
}

/// Migration lifecycle state.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum MigrationState {
    /// Waiting for a backfill worker.
    Pending,
    /// Worker has reported progress.
    Running,
    /// A reversible migration is restoring its previous schema.
    RollingBack,
    /// All steps completed.
    Complete,
    /// Worker stopped and supplied an error.
    Failed,
}

/// One bounded migration step.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub struct MigrationStep {
    /// Step kind (`field_rename`, `index_backfill`, `ttl_backfill`, or `schema_reconcile`).
    pub kind: String,
    /// Source field for a rename, when applicable.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub from: Option<String>,
    /// Destination field for a rename, when applicable.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub to: Option<String>,
    /// Index name for an index backfill, when applicable.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub index: Option<String>,
}

/// A successful schema apply result.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct ApplySchemaResponse {
    /// The resulting canonical schema document.
    pub document: SchemaDocument,
    /// `true` when this request replayed an existing idempotency result.
    pub idempotent_replay: bool,
}

/// Durable storage error from the parent member.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurablePortError {
    /// Stable internal error detail, suitable for logs but not secrets.
    pub detail: String,
}

impl fmt::Display for DurablePortError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        self.detail.fmt(formatter)
    }
}

impl std::error::Error for DurablePortError {}

/// Result of a compare-and-set commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DurableCommit {
    /// The record was committed.
    Committed,
    /// Another writer changed the expected record first.
    GenerationConflict,
}

/// Explicit durable storage seam.  Implementations must use the member's
/// already-approved durable store; SQL DDL and DML are intentionally outside
/// this interface and are never generated by this module.
pub trait SchemaDurablePort: Send + Sync {
    /// Load one complete record, including idempotency and migration state.
    ///
    /// # Errors
    ///
    /// Returns a durable provider read or decoding failure.
    fn load(&self, target: &SchemaTarget) -> Result<Option<DurableSchemaRecord>, DurablePortError>;
    /// Atomically replace one record only if it still equals `expected`.
    /// This includes migration progress and idempotency records, since backfill
    /// updates do not change schema generation. `None` requires an absent record.
    ///
    /// # Errors
    ///
    /// Returns a durable provider write failure. A record mismatch is returned
    /// as `DurableCommit::GenerationConflict`.
    fn commit(
        &self,
        target: &SchemaTarget,
        expected: Option<&DurableSchemaRecord>,
        record: DurableSchemaRecord,
    ) -> Result<DurableCommit, DurablePortError>;
}

/// Object-safe production bridge to the engine schema-catalog family.
///
/// A member's `SQLite` implementation normally implements
/// [`SchemaCatalogAdapter`].  The HTTP service only needs this object-safe
/// form, which keeps the adapter's associated error type out of handler state
/// while preserving the family CAS/checksum rules.  There is intentionally no
/// default or process-local implementation.
pub trait SchemaCatalogPort: Send + Sync {
    /// Load a complete canonical catalog row for one branch pin.
    ///
    /// # Errors
    ///
    /// Returns a durable provider read, decoding, or branch-validation failure.
    fn load_catalog(
        &self,
        branch: &SchemaBranchPin,
    ) -> Result<Option<SchemaCatalogRecord>, DurablePortError>;

    /// Atomically apply one family CAS request.
    ///
    /// # Errors
    ///
    /// Returns a durable provider or request-validation failure. Concurrent changes
    /// are represented by `SchemaCatalogCasOutcome::Conflict`.
    fn compare_and_swap_catalog(
        &self,
        request: &SchemaCatalogCasRequest,
    ) -> Result<SchemaCatalogCasOutcome, DurablePortError>;
}

/// Adapter wrapper for a concrete [`SchemaCatalogAdapter`].
///
/// `SchemaCatalogAdapter` is mutable because it owns a `SQLite` connection;
/// HTTP handlers are concurrent, so this wrapper serializes each family
/// operation behind a mutex.  The mutex protects only the connection handle;
/// durability and transaction atomicity remain the adapter's responsibility.
pub struct DurableSchemaCatalogAdapter<A> {
    adapter: Mutex<A>,
}

impl<A> DurableSchemaCatalogAdapter<A> {
    /// Wrap a concrete family adapter for concurrent handler use.
    #[must_use]
    pub fn new(adapter: A) -> Self {
        Self { adapter: Mutex::new(adapter) }
    }
}

impl<A> SchemaCatalogPort for DurableSchemaCatalogAdapter<A>
where
    A: SchemaCatalogAdapter + Send,
    A::Error: fmt::Debug,
{
    fn load_catalog(
        &self,
        branch: &SchemaBranchPin,
    ) -> Result<Option<SchemaCatalogRecord>, DurablePortError> {
        self.adapter
            .lock()
            .map_err(|_| DurablePortError {
                detail: "schema catalog adapter lock poisoned".into(),
            })?
            .load(branch)
            .map_err(|error| DurablePortError { detail: format!("{error:?}") })
    }

    fn compare_and_swap_catalog(
        &self,
        request: &SchemaCatalogCasRequest,
    ) -> Result<SchemaCatalogCasOutcome, DurablePortError> {
        self.adapter
            .lock()
            .map_err(|_| DurablePortError {
                detail: "schema catalog adapter lock poisoned".into(),
            })?
            .compare_and_swap(request)
            .map_err(|error| DurablePortError { detail: format!("{error:?}") })
    }
}

/// Request for one canonical, branch-pinned catalog successor.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CanonicalSchemaApplyRequest {
    /// Immutable database/branch incarnation pin.
    pub branch: SchemaBranchPin,
    /// Stable family entry identity. Reusing it is idempotent.
    pub entry_id: SchemaCatalogEntryUlid,
    /// Complete canonical `terrascale.schema.json` document.
    pub schema: CanonicalSchemaDocument,
    /// Monotonic schema version.
    pub schema_version: SchemaVersion,
    /// Monotonic migration generation.
    pub migration_generation: u64,
    /// Durable migration lifecycle state.
    pub migration_status: SchemaMigrationStatus,
    /// Exact CAS expectation, or [`SchemaCatalogExpectation::absent`].
    pub expected: SchemaCatalogExpectation,
}

/// Result of applying one canonical catalog successor.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CanonicalSchemaApplyResponse {
    /// The complete durable catalog record.
    pub record: SchemaCatalogRecord,
    /// Whether the family adapter found the same entry/checksum already
    /// committed.
    pub idempotent_replay: bool,
}

/// Production service over the durable schema-catalog family API.
pub struct CanonicalSchemaService {
    store: Arc<dyn SchemaCatalogPort>,
}

impl CanonicalSchemaService {
    /// Construct a service from a real durable family adapter.
    #[must_use]
    pub fn new(store: Arc<dyn SchemaCatalogPort>) -> Self {
        Self { store }
    }

    /// Read the complete canonical document and its durable metadata.
    ///
    /// # Errors
    ///
    /// Returns the durable catalog read failure as a service error.
    pub fn get(
        &self,
        branch: &SchemaBranchPin,
    ) -> Result<Option<SchemaCatalogRecord>, SchemaError> {
        self.store.load_catalog(branch).map_err(|error| durable_error(&error))
    }

    /// Read the exact canonical `terrascale.schema.json` bytes from durable
    /// catalog state.  The family record's serializer is used directly, so a
    /// restart cannot alter key order, aliases, or omitted defaults.
    ///
    /// # Errors
    ///
    /// Returns the durable catalog read failure from [`Self::get`].
    pub fn get_json(&self, branch: &SchemaBranchPin) -> Result<Option<String>, SchemaError> {
        self.get(branch).map(|record| record.map(|record| record.schema_json()))
    }

    /// Apply one canonical document through the family compare-and-swap seam.
    ///
    /// The family API validates the branch pin, checksum, monotonic version,
    /// migration generation, and idempotent entry identity before the adapter
    /// is allowed to commit. No schema bytes are held in process state.
    ///
    /// # Errors
    ///
    /// Returns invalid record or CAS precondition errors, durable provider failures,
    /// or `schema_catalog_conflict` when a concurrent writer wins.
    pub fn apply(
        &self,
        request: CanonicalSchemaApplyRequest,
    ) -> Result<CanonicalSchemaApplyResponse, SchemaError> {
        let record = SchemaCatalogRecord::try_new(
            request.entry_id,
            request.branch,
            request.schema,
            request.schema_version,
            request.migration_generation,
            request.migration_status,
        )
        .map_err(catalog_error)?;
        let cas = SchemaCatalogCasRequest::try_new(request.expected, record.clone())
            .map_err(catalog_error)?;
        let outcome =
            self.store.compare_and_swap_catalog(&cas).map_err(|error| durable_error(&error))?;
        match outcome {
            SchemaCatalogCasOutcome::Applied => {
                Ok(CanonicalSchemaApplyResponse { record, idempotent_replay: false })
            }
            SchemaCatalogCasOutcome::AlreadyApplied => {
                Ok(CanonicalSchemaApplyResponse { record, idempotent_replay: true })
            }
            SchemaCatalogCasOutcome::Conflict => Err(SchemaError::new(
                409,
                "schema_catalog_conflict",
                "another writer changed the branch schema catalog",
                "GET the schema catalog and retry with its exact version and checksum",
            )),
        }
    }
}

fn catalog_error(error: SchemaCatalogError) -> SchemaError {
    let (status, code, fix) = match error {
        SchemaCatalogError::CompareAndSwapConflict => (
            409,
            "schema_catalog_conflict",
            "GET the schema catalog and retry with its exact version and checksum",
        ),
        SchemaCatalogError::IdempotencyConflict => (
            409,
            "idempotency_conflict",
            "retry the original request or use a new schema entry identity",
        ),
        SchemaCatalogError::BranchMismatch => (
            409,
            "branch_identity_changed",
            "route the request to the branch's current database incarnation",
        ),
        SchemaCatalogError::NonMonotonicVersion | SchemaCatalogError::VersionContentMismatch => (
            409,
            "schema_version_regression",
            "advance the schema version and preserve its canonical contents",
        ),
        SchemaCatalogError::NonMonotonicMigrationGeneration => (
            409,
            "migration_generation_regression",
            "resume migration from its latest durable generation",
        ),
        _ => (422, "invalid_schema_catalog", "repair the canonical schema and retry the apply"),
    };
    SchemaError::new(status, code, format!("schema catalog rejected the request: {error:?}"), fix)
}

/// Reads restart-safe migration state from the engine migration family.
///
/// The coordinator is supplied by the member's durable engine; this function
/// never maintains a second status cache in the server process.
///
/// # Errors
///
/// Returns the engine coordinator's scope, migration-state, or storage failure.
pub fn migration_status<C: MigrationCoordinator>(
    coordinator: &C,
    scope: &TenantScope,
    collection: &CollectionName,
) -> Result<FamilyMigrationStatus, SchemaError> {
    coordinator.migration_status(scope, collection).map_err(migration_error)
}

/// Computes a durable-engine migration plan without mutating documents.
///
/// # Errors
///
/// Returns invalid-contract, version, scope, or storage failures from the coordinator.
pub fn migration_dry_run<C: MigrationCoordinator>(
    coordinator: &C,
    scope: &TenantScope,
    contract: &SchemaMigration,
) -> Result<MigrationPlan, SchemaError> {
    coordinator.migration_dry_run(scope, contract).map_err(migration_error)
}

/// Drives one migration through the durable engine coordinator.
///
/// The engine records each bounded page and cursor, so an interrupted request
/// resumes after restart.  `options` is passed unchanged, including the
/// explicit acknowledgment required for irreversible transforms.
///
/// # Errors
///
/// Returns contract, version, acknowledgment, generation, or storage failures
/// from the durable migration coordinator.
pub fn migration_apply<C: MigrationCoordinator>(
    coordinator: &C,
    scope: &TenantScope,
    contract: &SchemaMigration,
    plan: &MigrationPlan,
    options: MigrationOptions,
) -> Result<MigrationReceipt, SchemaError> {
    coordinator.migration_apply(scope, contract, plan, options).map_err(migration_error)
}

fn migration_error(error: MigrationError) -> SchemaError {
    let (status, code, fix) = match error {
        MigrationError::InvalidContract | MigrationError::TargetSchemaMalformed => {
            (422, "invalid_migration", "repair the migration contract and run a fresh dry run")
        }
        MigrationError::FromVersionMismatch
        | MigrationError::PlanMismatch
        | MigrationError::ManifestMismatch => (
            409,
            "migration_version_conflict",
            "refresh migration status and dry-run against the current schema",
        ),
        MigrationError::DriveInProgress => (
            409,
            "migration_in_progress",
            "resume the durable migration owner instead of starting another drive",
        ),
        MigrationError::AcknowledgmentRequired => (
            409,
            "migration_acknowledgment_required",
            "acknowledge the documented rollback limit before applying",
        ),
        MigrationError::StorageFailure
        | MigrationError::ReservedStateInvalid
        | MigrationError::ChecksumMismatch
        | MigrationError::CountMismatch => (
            503,
            "migration_durable_store_error",
            "recover the durable engine and retry with the same migration identity",
        ),
        _ => (
            409,
            "migration_rejected",
            "inspect the typed migration status and follow its recovery action",
        ),
    };
    SchemaError::new(
        status,
        code,
        format!("migration coordinator rejected the request: {error:?}"),
        fix,
    )
}

/// Durable envelope persisted by [`SchemaDurablePort`].
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct DurableSchemaRecord {
    /// Public schema document.
    pub document: SchemaDocument,
    /// Previous collection contract retained until migration completion.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub previous_schema: Option<CollectionSchema>,
    /// Original ready-plan evidence retained before the first migration page.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub migration_plan: Option<Vec<u8>>,
    /// Durable rollback intent, retained after completion for request replay.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub rollback: Option<SchemaRollback>,
    /// Idempotency key → canonical request digest and response document.
    #[serde(default)]
    pub idempotency: BTreeMap<String, IdempotencyRecord>,
}

/// Identity of an admitted rollback request. The source generation stays fixed
/// while the public schema generation advances when restoration is published.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SchemaRollback {
    /// Caller-supplied idempotency identity.
    pub idempotency_key: String,
    /// Strong `ETag` required when the rollback was admitted.
    pub generation: u64,
}

/// Immutable collection contract identity; progress and replay records do not
/// change which schema generation won a concurrent publication.
pub(crate) fn schema_bundle_identity(
    record: &DurableSchemaRecord,
) -> Result<[u8; 32], serde_json::Error> {
    serde_json::to_vec(&(record.document.schema_version, &record.document.schema))
        .map(|bytes| *blake3::hash(&bytes).as_bytes())
}

/// A remembered idempotent response.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct IdempotencyRecord {
    /// BLAKE3 digest of target, identity, expected generation, and schema.
    pub digest: String,
    /// Applied document returned to the original caller.
    pub response: SchemaDocument,
}

/// Service-level error with a stable code and actionable fix.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct SchemaError {
    /// HTTP-compatible status.
    pub status: u16,
    /// Machine-readable error code.
    pub code: String,
    /// Bounded human-readable detail.
    pub message: String,
    /// One-line remediation.
    pub fix: String,
}

impl SchemaError {
    pub(crate) fn new(
        status: u16,
        code: &str,
        message: impl Into<String>,
        fix: impl Into<String>,
    ) -> Self {
        Self {
            status,
            code: code.to_owned(),
            message: bounded(message.into(), 512),
            fix: bounded(fix.into(), 512),
        }
    }

    fn invalid(message: impl Into<String>) -> Self {
        Self::new(
            422,
            "invalid_schema",
            message,
            "run terrascale diff and fix the schema before terrascale push",
        )
    }
}

/// Database identity resolver supplied by the parent HTTP layer.
pub trait SchemaIdentityPort: Send + Sync {
    /// Resolve the immutable database identity and placement for a request.
    ///
    /// # Errors
    ///
    /// Returns a lookup, placement, or identity-validation failure.
    fn resolve(&self, database: &str, branch: &str) -> Result<DatabaseIdentity, SchemaError>;
}

/// Schema service state suitable for `axum::State`.
#[derive(Clone)]
pub struct SchemaHttpState {
    /// Durable schema service.
    pub service: Arc<SchemaService>,
    /// Parent-owned database identity resolver.
    pub identity: Option<Arc<dyn SchemaIdentityPort>>,
}

impl SchemaHttpState {
    /// Construct state.  `None` identity is deliberate fail-closed mode.
    #[must_use]
    pub fn new(service: Arc<SchemaService>, identity: Option<Arc<dyn SchemaIdentityPort>>) -> Self {
        Self { service, identity }
    }

    /// Construct handler state for production wiring.
    ///
    /// Unlike [`Self::new`], this constructor cannot represent a missing
    /// identity resolver.  The resolver and the service are both durable
    /// parent-owned ports; tests that need fail-closed behavior may continue
    /// using `new(..., None)`.
    #[must_use]
    pub fn production(service: Arc<SchemaService>, identity: Arc<dyn SchemaIdentityPort>) -> Self {
        Self { service, identity: Some(identity) }
    }
}

/// Durable schema service.
pub struct SchemaService {
    store: Option<Arc<dyn SchemaDurablePort>>,
}

#[allow(clippy::large_enum_variant, reason = "short-lived two-variant preparation enum")]
enum ApplyPreparation {
    Ready {
        request: ApplySchemaRequest,
        current: Option<DurableSchemaRecord>,
        schema: CollectionSchema,
        digest: String,
    },
    Replay(ApplySchemaResponse),
}

#[allow(clippy::large_enum_variant, reason = "short-lived two-variant preparation enum")]
enum BackfillPreparation {
    Ready { current: DurableSchemaRecord, migration: MigrationStatus, digest: String },
    Replay(SchemaDocument),
}

fn trim_idempotency(idempotency: &mut BTreeMap<String, IdempotencyRecord>) {
    while idempotency.len() > MAX_IDEMPOTENCY_KEYS {
        let Some(key) = idempotency.keys().next().cloned() else { break };
        idempotency.remove(&key);
    }
}

impl SchemaService {
    /// Construct a service.  A missing port is retained as unavailable rather
    /// than replaced with process-local state.
    #[must_use]
    pub fn new(store: Option<Arc<dyn SchemaDurablePort>>) -> Self {
        Self { store }
    }

    /// Read a schema after checking the immutable database identity.
    ///
    /// # Errors
    ///
    /// Returns invalid target or identity errors, missing-schema or identity-conflict
    /// errors, or durable store failures.
    pub fn get(
        &self,
        target: &SchemaTarget,
        identity: &DatabaseIdentity,
    ) -> Result<SchemaDocument, SchemaError> {
        validate_target(target)?;
        validate_identity(identity)?;
        let record = self.load(target)?;
        let Some(record) = record else {
            return Err(SchemaError::new(
                404,
                "schema_not_found",
                "no schema is pinned for this collection",
                "apply terrascale.schema.json with terrascale push",
            ));
        };
        ensure_identity(&record.document.identity, identity)?;
        Ok(record.document)
    }

    /// Apply a canonicalizable collection schema with idempotency and an
    /// optimistic generation precondition.  All validation, normalization,
    /// and migration planning happen before the durable compare-and-set.
    ///
    /// # Errors
    ///
    /// Returns validation, identity, idempotency, migration-plan, generation-conflict,
    /// or durable store errors.
    pub fn apply(&self, request: ApplySchemaRequest) -> Result<ApplySchemaResponse, SchemaError> {
        let prepared = self.prepare_apply(request)?;
        let (request, current, schema, digest) = match prepared {
            ApplyPreparation::Ready { request, current, schema, digest } => {
                (request, current, schema, digest)
            }
            ApplyPreparation::Replay(response) => return Ok(response),
        };
        let current_generation = current.as_ref().map_or(0, |record| record.document.generation);
        let next_version = next_schema_version(current.as_ref(), schema.version)?;
        let migration = plan_migration(
            current.as_ref().map(|record| &record.document.schema),
            &schema,
            &request.target,
        )?;
        let document = SchemaDocument {
            target: request.target.clone(),
            identity: request.identity.clone(),
            schema,
            generation: current_generation.saturating_add(1),
            schema_version: next_version,
            migration,
        };
        let mut idempotency =
            current.as_ref().map_or_else(BTreeMap::new, |record| record.idempotency.clone());
        idempotency.insert(
            request.idempotency_key.clone(),
            IdempotencyRecord { digest, response: document.clone() },
        );
        trim_idempotency(&mut idempotency);
        self.commit_apply(&request, current.as_ref(), document, idempotency)
    }

    fn prepare_apply(&self, request: ApplySchemaRequest) -> Result<ApplyPreparation, SchemaError> {
        validate_target(&request.target)?;
        validate_identity(&request.identity)?;
        validate_idempotency_key(&request.idempotency_key)?;
        let schema = normalize_schema(request.schema.clone())?;
        if schema.name != request.target.collection {
            return Err(SchemaError::invalid(format!(
                "schema collection '{}' does not match target '{}'",
                schema.name, request.target.collection
            )));
        }
        let digest = request_digest(
            &request.target,
            &request.identity,
            request.expected_generation,
            &schema,
        )?;
        let current = self.load(&request.target)?;
        let current_generation = current.as_ref().map_or(0, |record| record.document.generation);
        if let Some(record) = current.as_ref() {
            ensure_identity(&record.document.identity, &request.identity)?;
            if let Some(previous) = record.idempotency.get(&request.idempotency_key) {
                if previous.digest == digest {
                    return Ok(ApplyPreparation::Replay(ApplySchemaResponse {
                        document: previous.response.clone(),
                        idempotent_replay: true,
                    }));
                }
                return Err(SchemaError::new(
                    409,
                    "idempotency_conflict",
                    "idempotency key was already used for a different schema request",
                    "retry with a new Idempotency-Key or replay the original request",
                ));
            }
        }
        if request.expected_generation.is_some_and(|expected| expected != current_generation) {
            return Err(SchemaError::new(
                412,
                "generation_mismatch",
                format!(
                    "schema generation is {current_generation}, not the requested precondition"
                ),
                "GET the schema, then retry with its ETag in If-Match",
            ));
        }
        if current.as_ref().is_some_and(|record| {
            record.document.migration.as_ref().is_some_and(|migration| {
                matches!(
                    migration.state,
                    MigrationState::Pending | MigrationState::Running | MigrationState::RollingBack
                ) || (migration.state == MigrationState::Failed && record.migration_plan.is_some())
            })
        }) {
            return Err(SchemaError::new(
                409,
                "migration_in_progress",
                "the current schema migration is still running",
                "wait for migration completion before applying another schema",
            ));
        }
        Ok(ApplyPreparation::Ready { request, current, schema, digest })
    }

    fn commit_apply(
        &self,
        request: &ApplySchemaRequest,
        current: Option<&DurableSchemaRecord>,
        document: SchemaDocument,
        idempotency: BTreeMap<String, IdempotencyRecord>,
    ) -> Result<ApplySchemaResponse, SchemaError> {
        let outcome = self
            .store()?
            .commit(
                &request.target,
                current,
                DurableSchemaRecord {
                    previous_schema: current.filter(|_| document.migration.is_some()).map(
                        |record| {
                            let mut schema = record.document.schema.clone();
                            schema.version = Some(record.document.schema_version);
                            schema
                        },
                    ),
                    migration_plan: None,
                    rollback: None,
                    document: document.clone(),
                    idempotency,
                },
            )
            .map_err(|error| durable_error(&error))?;
        if outcome == DurableCommit::GenerationConflict {
            return Err(SchemaError::new(
                409,
                "generation_conflict",
                "another schema writer committed first",
                "GET the schema and retry with a fresh If-Match and Idempotency-Key",
            ));
        }
        Ok(ApplySchemaResponse { document, idempotent_replay: false })
    }

    /// Record bounded worker progress for a rename/index/TTL backfill.  The
    /// worker supplies only counters and an opaque cursor; this service never
    /// executes a document update or SQL statement.
    ///
    /// # Errors
    ///
    /// Returns invalid counters or cursor, identity, idempotency, migration-state,
    /// generation-conflict, or durable store errors.
    pub fn record_backfill_progress(
        &self,
        progress: BackfillProgress,
    ) -> Result<SchemaDocument, SchemaError> {
        let prepared = self.prepare_backfill(&progress)?;
        let (current, mut migration, digest) = match prepared {
            BackfillPreparation::Ready { current, migration, digest } => {
                (current, migration, digest)
            }
            BackfillPreparation::Replay(document) => return Ok(document),
        };
        migration.total = progress.total;
        migration.completed = progress.completed;
        migration.next_cursor.clone_from(&progress.next_cursor);
        migration.state = if progress.failed {
            MigrationState::Failed
        } else if progress.completed == progress.total {
            MigrationState::Complete
        } else {
            MigrationState::Running
        };
        migration.message = if progress.failed {
            "backfill stopped; inspect the worker error and retry".into()
        } else if migration.state == MigrationState::Complete {
            "backfill complete; schema generation is ready".into()
        } else {
            "backfill is resumable from next_cursor".into()
        };
        let mut document = current.document.clone();
        document.migration = Some(migration);
        let mut idempotency = current.idempotency.clone();
        idempotency.insert(
            progress.idempotency_key,
            IdempotencyRecord { digest, response: document.clone() },
        );
        trim_idempotency(&mut idempotency);
        let outcome = self
            .store()?
            .commit(
                &progress.target,
                Some(&current),
                DurableSchemaRecord {
                    previous_schema: if document
                        .migration
                        .as_ref()
                        .is_some_and(|migration| migration.state == MigrationState::Complete)
                    {
                        None
                    } else {
                        current.previous_schema.clone()
                    },
                    migration_plan: if document
                        .migration
                        .as_ref()
                        .is_some_and(|migration| migration.state == MigrationState::Complete)
                    {
                        None
                    } else {
                        current.migration_plan.clone()
                    },
                    rollback: current.rollback.clone(),
                    document: document.clone(),
                    idempotency,
                },
            )
            .map_err(|error| durable_error(&error))?;
        if outcome == DurableCommit::GenerationConflict {
            return Err(SchemaError::new(
                409,
                "generation_conflict",
                "schema or migration progress changed while recording backfill progress",
                "reload migration status and report progress against the current generation",
            ));
        }
        Ok(document)
    }

    fn prepare_backfill(
        &self,
        progress: &BackfillProgress,
    ) -> Result<BackfillPreparation, SchemaError> {
        validate_backfill_progress(progress)?;
        let digest = progress_digest(progress)?;
        let current = self.load(&progress.target)?.ok_or_else(|| {
            SchemaError::new(
                404,
                "schema_not_found",
                "cannot update backfill status without a schema",
                "apply a schema before starting its backfill",
            )
        })?;
        if current.document.generation != progress.generation {
            return Err(SchemaError::new(
                412,
                "generation_mismatch",
                "backfill reports an obsolete schema generation",
                "restart the worker from the latest schema migration status",
            ));
        }
        let Some(migration) = current.document.migration.clone() else {
            return Err(SchemaError::new(
                409,
                "no_backfill",
                "the schema has no resumable backfill",
                "refresh migration status and apply a schema change that requires backfill",
            ));
        };
        if migration.state == MigrationState::RollingBack {
            return Err(SchemaError::new(
                409,
                "rollback_in_progress",
                "forward progress cannot replace rollback ownership",
                "wait for rollback to finish",
            ));
        }
        if migration.operation_id != progress.operation_id {
            return Err(SchemaError::new(
                409,
                "backfill_mismatch",
                "backfill operation does not match the pinned migration",
                "use the operation_id returned by GET schema",
            ));
        }
        if let Some(previous) = current.idempotency.get(&progress.idempotency_key) {
            if previous.digest == digest {
                return Ok(BackfillPreparation::Replay(previous.response.clone()));
            }
            return Err(SchemaError::new(
                409,
                "idempotency_conflict",
                "backfill idempotency key was already used for a different progress update",
                "retry the original progress update or use a new worker key",
            ));
        }
        if progress.completed < migration.completed
            || (migration.total != 0 && progress.total < migration.total)
        {
            return Err(SchemaError::new(
                409,
                "backfill_regression",
                "backfill progress cannot move backwards",
                "resume from the latest durable cursor and report monotonic counters",
            ));
        }
        Ok(BackfillPreparation::Ready { current, migration, digest })
    }

    fn store(&self) -> Result<&Arc<dyn SchemaDurablePort>, SchemaError> {
        self.store.as_ref().ok_or_else(|| {
            SchemaError::new(
                503,
                "durable_store_unavailable",
                "schema storage is not configured on this member",
                "start terrabase with its durable control store before applying schemas",
            )
        })
    }

    fn load(&self, target: &SchemaTarget) -> Result<Option<DurableSchemaRecord>, SchemaError> {
        let record = self.store()?.load(target).map_err(|error| durable_error(&error))?;
        if let Some(record) = &record {
            validate_durable_record(target, record)
                .map_err(|error| durable_error(&DurablePortError { detail: error.message }))?;
        }
        Ok(record)
    }
}

/// Schema apply input for library callers.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ApplySchemaRequest {
    /// Target database, branch, and collection.
    pub target: SchemaTarget,
    /// Immutable database identity observed by the parent.
    pub identity: DatabaseIdentity,
    /// Incoming SDK schema JSON.
    pub schema: CollectionSchema,
    /// Optional optimistic generation (`If-Match`).
    pub expected_generation: Option<u64>,
    /// Required replay key.
    pub idempotency_key: String,
}

/// Worker progress update for a resumable backfill.
#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize)]
pub struct BackfillProgress {
    /// Schema target.
    pub target: SchemaTarget,
    /// Schema generation being processed.
    pub generation: u64,
    /// Migration operation identifier.
    pub operation_id: String,
    /// Stable worker idempotency key (bounded for audit storage).
    pub idempotency_key: String,
    /// Total rows to process.
    pub total: u64,
    /// Rows completed.
    pub completed: u64,
    /// Opaque cursor for restart.
    pub next_cursor: Option<String>,
    /// Whether the worker failed this attempt.
    pub failed: bool,
}

/// Build the parent-wiring HTTP router for schema GET/PUT routes.
pub fn router(state: Arc<SchemaHttpState>) -> Router {
    Router::new().route(SCHEMA_ROUTE, get(get_schema).put(put_schema)).with_state(state)
}

/// GET handler exposed for parent route tables that use their own router.
pub async fn get_schema(
    state: State<Arc<SchemaHttpState>>,
    path: Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    schema_blocking_response(move || get_schema_blocking(state, path, &headers)).await
}

fn get_schema_blocking(
    State(state): State<Arc<SchemaHttpState>>,
    Path((database, collection)): Path<(String, String)>,
    headers: &HeaderMap,
) -> Response {
    let branch = match header_text(headers, "x-terrascale-branch") {
        Ok(branch) => branch.unwrap_or_else(|| "main".into()),
        Err(error) => return error.into_response(),
    };
    let target = SchemaTarget { database: database.clone(), branch: branch.clone(), collection };
    if let Err(error) = validate_target(&target) {
        return error.into_response();
    }
    let identity = match resolve_identity(&state, &database, &branch) {
        Ok(identity) => identity,
        Err(error) => return error.into_response(),
    };
    match state.service.get(&target, &identity) {
        Ok(document) => schema_response(StatusCode::OK, &document, true),
        Err(error) => error.into_response(),
    }
}

/// PUT handler exposed for parent route tables that use their own router.
pub async fn put_schema(
    state: State<Arc<SchemaHttpState>>,
    path: Path<(String, String)>,
    headers: HeaderMap,
    body: String,
) -> Response {
    schema_blocking_response(move || put_schema_blocking(state, path, &headers, &body)).await
}

fn put_schema_blocking(
    State(state): State<Arc<SchemaHttpState>>,
    Path((database, collection)): Path<(String, String)>,
    headers: &HeaderMap,
    body: &str,
) -> Response {
    if body.len() > MAX_SCHEMA_BYTES {
        return SchemaError::new(
            413,
            "schema_too_large",
            "schema JSON exceeds the member limit",
            "split the schema into bounded collection applies",
        )
        .into_response();
    }
    let schema: CollectionSchema = match serde_json::from_str(body) {
        Ok(schema) => schema,
        Err(error) => {
            return SchemaError::invalid(format!("schema JSON is invalid: {error}"))
                .into_response();
        }
    };
    let branch = match header_text(headers, "x-terrascale-branch") {
        Ok(branch) => branch.unwrap_or_else(|| "main".into()),
        Err(error) => return error.into_response(),
    };
    let idempotency_key = match header_text(headers, "idempotency-key") {
        Ok(Some(key)) if !key.is_empty() => key,
        Err(error) => return error.into_response(),
        Ok(_) => {
            return SchemaError::new(
                400,
                "missing_idempotency_key",
                "PUT schema requires Idempotency-Key",
                "send a fresh Idempotency-Key on every new schema apply",
            )
            .into_response();
        }
    };
    let expected_generation = match parse_if_match(headers) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    let target = SchemaTarget { database, branch, collection };
    if let Err(error) = validate_target(&target) {
        return error.into_response();
    }
    let identity = match resolve_identity(&state, &target.database, &target.branch) {
        Ok(identity) => identity,
        Err(error) => return error.into_response(),
    };
    let request =
        ApplySchemaRequest { target, identity, schema, expected_generation, idempotency_key };
    match state.service.apply(request) {
        Ok(result) => schema_response(StatusCode::OK, &result.document, true),
        Err(error) => error.into_response(),
    }
}

async fn schema_blocking_response(work: impl FnOnce() -> Response + Send + 'static) -> Response {
    tokio::task::spawn_blocking(work).await.unwrap_or_else(|_| {
        SchemaError::new(
            503,
            "schema_worker_unavailable",
            "schema operation worker could not complete",
            "retry the schema operation",
        )
        .into_response()
    })
}

fn resolve_identity(
    state: &SchemaHttpState,
    database: &str,
    branch: &str,
) -> Result<DatabaseIdentity, SchemaError> {
    state
        .identity
        .as_ref()
        .ok_or_else(|| {
            SchemaError::new(
                503,
                "durable_identity_unavailable",
                "database placement identity is not configured on this member",
                "start the member with its durable control-plane registry",
            )
        })?
        .resolve(database, branch)
}

pub(crate) fn schema_response(
    status: StatusCode,
    document: &SchemaDocument,
    include_etag: bool,
) -> Response {
    let mut response = json_response(status, document);
    if include_etag && let Ok(value) = format!("\"{}\"", document.generation).parse() {
        response.headers_mut().insert(ETAG, value);
    }
    response
}

impl IntoResponse for SchemaError {
    fn into_response(self) -> Response {
        let status = StatusCode::from_u16(self.status).unwrap_or(StatusCode::INTERNAL_SERVER_ERROR);
        json_response(status, &serde_json::json!({ "error": self }))
    }
}

fn json_response<T: Serialize>(status: StatusCode, value: &T) -> Response {
    let body = serde_json::to_vec(value).unwrap_or_else(|_| b"{\"error\":{}}".to_vec());
    Response::builder()
        .status(status)
        .header(CONTENT_TYPE, HeaderValue::from_static("application/json"))
        .body(Body::from(body))
        .expect("schema response is valid")
}

pub(crate) fn parse_if_match(headers: &HeaderMap) -> Result<Option<u64>, SchemaError> {
    let invalid = || {
        SchemaError::new(
            400,
            "invalid_if_match",
            "If-Match must contain one strong quoted schema generation",
            "send the exact ETag returned by GET schema",
        )
    };
    let mut values = headers.get_all(IF_MATCH).iter();
    let Some(value) = values.next() else { return Ok(None) };
    if values.next().is_some() {
        return Err(invalid());
    }
    let text = value.to_str().map_err(|_| invalid())?.trim_matches([' ', '\t']);
    let generation =
        text.strip_prefix('"').and_then(|text| text.strip_suffix('"')).ok_or_else(invalid)?;
    if generation.is_empty()
        || !generation.bytes().all(|byte| byte.is_ascii_digit())
        || (generation.len() > 1 && generation.starts_with('0'))
    {
        return Err(invalid());
    }
    generation.parse::<u64>().map(Some).map_err(|_| invalid())
}

pub(crate) fn header_text(headers: &HeaderMap, name: &str) -> Result<Option<String>, SchemaError> {
    let invalid = || {
        SchemaError::new(
            400,
            "invalid_header",
            format!("{name} must contain one valid text value"),
            format!("send exactly one {name} header"),
        )
    };
    let mut values = headers.get_all(name).iter();
    let Some(value) = values.next() else { return Ok(None) };
    if values.next().is_some() {
        return Err(invalid());
    }
    value.to_str().map(|text| Some(text.to_owned())).map_err(|_| invalid())
}

fn validate_backfill_progress(progress: &BackfillProgress) -> Result<(), SchemaError> {
    validate_target(&progress.target)?;
    validate_idempotency_key(&progress.idempotency_key)?;
    if progress.total > 10_000_000_000_000 || progress.completed > progress.total {
        return Err(SchemaError::invalid("backfill counters exceed their safe bounds"));
    }
    if progress.next_cursor.as_ref().is_some_and(|cursor| cursor.len() > MAX_SCHEMA_BYTES) {
        return Err(SchemaError::invalid("backfill cursor is too large"));
    }
    Ok(())
}

fn normalize_schema(mut schema: CollectionSchema) -> Result<CollectionSchema, SchemaError> {
    schema.name = schema.name.trim().into();
    validate_identifier(&schema.name, "collection")?;
    if schema.fields.len() > MAX_FIELDS {
        return Err(SchemaError::invalid("schema declares too many fields"));
    }
    if schema.indexes.len() > MAX_INDEXES {
        return Err(SchemaError::invalid("schema declares too many indexes"));
    }
    if schema.renames.len() > MAX_RENAMES {
        return Err(SchemaError::invalid("schema declares too many renames"));
    }
    if !(1..=30).contains(&schema.history_retention_days) {
        return Err(SchemaError::invalid(format!(
            "history retention for '{}' must be between 1 and 30 days",
            schema.name
        )));
    }
    if let Some(version) = schema.version {
        validate_version(version)?;
    }
    let mut fields = BTreeSet::new();
    for field in &mut schema.fields {
        field.name = field.name.trim().into();
        validate_identifier(&field.name, "field")?;
        if !fields.insert(field.name.clone()) {
            return Err(SchemaError::invalid(format!("duplicate field '{}'", field.name)));
        }
        if let Some(policy) = field.merge_policy
            && !policy.is_crdt()
        {
            return Err(SchemaError::invalid(format!(
                "field '{}' has a non-CRDT merge policy",
                field.name
            )));
        }
        field.indexed |= field.unique;
    }
    normalize_indexes(&mut schema)?;
    if let Some(ttl) = schema.ttl.as_mut() {
        ttl.field = ttl.field.trim().into();
        validate_identifier(&ttl.field, "TTL field")?;
        let descriptor =
            schema.fields.iter().find(|field| field.name == ttl.field).ok_or_else(|| {
                SchemaError::invalid(format!("TTL references unknown field '{}'", ttl.field))
            })?;
        if descriptor.r#type != FieldType::Timestamp {
            return Err(SchemaError::invalid(format!(
                "TTL field '{}' must have type timestamp",
                ttl.field
            )));
        }
    }
    let mut rename_sources = BTreeSet::new();
    let mut rename_targets = BTreeSet::new();
    for rename in &mut schema.renames {
        rename.from = rename.from.trim().into();
        rename.to = rename.to.trim().into();
        validate_identifier(&rename.from, "rename source")?;
        validate_identifier(&rename.to, "rename target")?;
        if rename.from == rename.to {
            return Err(SchemaError::invalid(format!(
                "rename '{}' -> '{}' must change the field name",
                rename.from, rename.to
            )));
        }
        if !rename_sources.insert(rename.from.clone()) {
            return Err(SchemaError::invalid(format!("duplicate rename source '{}'", rename.from)));
        }
        if !rename_targets.insert(rename.to.clone()) {
            return Err(SchemaError::invalid(format!("duplicate rename target '{}'", rename.to)));
        }
        if fields.contains(&rename.from) {
            return Err(SchemaError::invalid(format!(
                "rename source '{}' is still declared as a field",
                rename.from
            )));
        }
        if !fields.contains(&rename.to) {
            return Err(SchemaError::invalid(format!(
                "rename target '{}' is not declared as a field",
                rename.to
            )));
        }
    }
    schema.renames.sort();
    Ok(schema)
}

fn normalize_indexes(schema: &mut CollectionSchema) -> Result<(), SchemaError> {
    let mut index_names = BTreeSet::new();
    for index in &mut schema.indexes {
        index.name = index.name.trim().into();
        validate_identifier(&index.name, "index")?;
        if !index_names.insert(index.name.clone()) {
            return Err(SchemaError::invalid(format!("duplicate index '{}'", index.name)));
        }
        if index.fields.is_empty() {
            return Err(SchemaError::invalid(format!(
                "index '{}' must contain a field",
                index.name
            )));
        }
        let kind = index.kind.unwrap_or(IndexKind::Btree);
        index.kind = Some(kind);
        if kind == IndexKind::Fts && index.fields.len() != 1 {
            return Err(SchemaError::invalid(format!(
                "FTS index '{}' must contain exactly one field",
                index.name
            )));
        }
        for field in &mut index.fields {
            *field = field.trim().into();
            validate_identifier(field, "index field")?;
            let descriptor =
                schema.fields.iter().find(|candidate| candidate.name == *field).ok_or_else(
                    || {
                        SchemaError::invalid(format!(
                            "index '{}' references unknown field '{}'",
                            index.name, field
                        ))
                    },
                )?;
            if kind == IndexKind::Fts && !descriptor.r#type.is_fts() {
                return Err(SchemaError::invalid(format!(
                    "FTS field '{field}' must have type text or string"
                )));
            }
        }
    }
    let marked_fts: Vec<String> =
        schema.fields.iter().filter(|field| field.fts).map(|field| field.name.clone()).collect();
    for field in &mut schema.fields {
        field.fts = false;
    }
    let had_fts_descriptor = schema.fts.is_some();
    let mut fts_fields =
        schema.fts.as_mut().map(|fts| std::mem::take(&mut fts.fields)).unwrap_or_default();
    fts_fields.extend(marked_fts);
    fts_fields.sort();
    fts_fields.dedup();
    if fts_fields.len() > MAX_INDEXES.saturating_sub(schema.indexes.len()) {
        return Err(SchemaError::invalid("normalized schema declares too many indexes"));
    }
    if had_fts_descriptor && fts_fields.is_empty() {
        return Err(SchemaError::invalid("FTS must contain a field"));
    }
    if !fts_fields.is_empty() {
        for field in &fts_fields {
            validate_identifier(field, "FTS field")?;
            let descriptor =
                schema.fields.iter().find(|candidate| candidate.name == *field).ok_or_else(
                    || SchemaError::invalid(format!("FTS references unknown field '{field}'")),
                )?;
            if !descriptor.r#type.is_fts() {
                return Err(SchemaError::invalid(format!(
                    "FTS field '{field}' must have type text or string"
                )));
            }
            let name = format!("{field}_fts");
            validate_identifier(&name, "index")?;
            if index_names.contains(&name) {
                return Err(SchemaError::invalid(format!("duplicate index '{name}'")));
            }
            index_names.insert(name.clone());
            schema.indexes.push(IndexDescriptor {
                name,
                fields: vec![field.clone()],
                unique: false,
                kind: Some(IndexKind::Fts),
            });
        }
    }
    // The SDK canonicalizer represents FTS shorthand exclusively as named
    // index descriptors; it never emits the builder-only `fts` property.
    schema.fts = None;
    Ok(())
}

fn plan_migration(
    previous: Option<&CollectionSchema>,
    next: &CollectionSchema,
    target: &SchemaTarget,
) -> Result<Option<MigrationStatus>, SchemaError> {
    let mut steps = Vec::new();
    for rename in &next.renames {
        steps.push(MigrationStep {
            kind: "field_rename".into(),
            from: Some(rename.from.clone()),
            to: Some(rename.to.clone()),
            index: None,
        });
    }
    let old_indexes: BTreeSet<&str> = previous
        .into_iter()
        .flat_map(|schema| schema.indexes.iter())
        .map(|index| index.name.as_str())
        .collect();
    for index in &next.indexes {
        if !old_indexes.contains(index.name.as_str()) {
            steps.push(MigrationStep {
                kind: "index_backfill".into(),
                from: None,
                to: None,
                index: Some(index.name.clone()),
            });
        }
    }
    if previous.and_then(|schema| schema.ttl.as_ref()) != next.ttl.as_ref() && next.ttl.is_some() {
        steps.push(MigrationStep {
            kind: "ttl_backfill".into(),
            from: None,
            to: None,
            index: None,
        });
    }
    let descriptors_changed = previous.map_or_else(
        || next.fields.iter().any(|field| field.indexed || field.unique),
        |old| {
            old.indexes != next.indexes
                || old.ttl != next.ttl
                || old
                    .fields
                    .iter()
                    .map(|field| (&field.name, field.indexed, field.unique))
                    .collect::<Vec<_>>()
                    != next
                        .fields
                        .iter()
                        .map(|field| (&field.name, field.indexed, field.unique))
                        .collect::<Vec<_>>()
        },
    );
    if descriptors_changed && steps.is_empty() {
        steps.push(MigrationStep {
            kind: "schema_reconcile".into(),
            from: None,
            to: None,
            index: None,
        });
    }
    if steps.is_empty() {
        return Ok(None);
    }
    if steps.len() > MAX_MIGRATION_STEPS {
        return Err(SchemaError::invalid("schema migration contains too many steps"));
    }
    let operation_id = migration_id(target, next).map_err(|_| {
        SchemaError::new(
            500,
            "digest_failure",
            "could not derive migration operation id",
            "retry the schema apply",
        )
    })?;
    Ok(Some(MigrationStatus {
        operation_id,
        state: MigrationState::Pending,
        steps,
        total: 0,
        completed: 0,
        next_cursor: None,
        message: "backfill pending; start a worker and resume with next_cursor".into(),
    }))
}

fn migration_id(
    target: &SchemaTarget,
    schema: &CollectionSchema,
) -> Result<String, serde_json::Error> {
    let mut bytes = serde_json::to_vec(&(target, schema))?;
    let digest = blake3::hash(&bytes);
    bytes.fill(0);
    Ok(digest.to_hex().to_string())
}

fn request_digest(
    target: &SchemaTarget,
    identity: &DatabaseIdentity,
    expected: Option<u64>,
    schema: &CollectionSchema,
) -> Result<String, SchemaError> {
    serde_json::to_vec(&(target, identity, expected, schema))
        .map(|bytes| blake3::hash(&bytes).to_hex().to_string())
        .map_err(|_| {
            SchemaError::new(
                500,
                "digest_failure",
                "could not derive request idempotency digest",
                "retry the schema apply",
            )
        })
}

fn progress_digest(progress: &BackfillProgress) -> Result<String, SchemaError> {
    serde_json::to_vec(progress).map(|bytes| blake3::hash(&bytes).to_hex().to_string()).map_err(
        |_| {
            SchemaError::new(
                500,
                "digest_failure",
                "could not derive backfill idempotency digest",
                "retry the backfill progress update",
            )
        },
    )
}

pub(crate) fn validate_durable_record(
    target: &SchemaTarget,
    record: &DurableSchemaRecord,
) -> Result<(), SchemaError> {
    validate_stored_document(target, &record.document)?;
    if let Some(rollback) = &record.rollback {
        validate_idempotency_key(&rollback.idempotency_key)?;
        let active = record.previous_schema.is_some();
        if rollback.generation == 0
            || if active {
                record.document.generation != rollback.generation
                    || !record
                        .document
                        .migration
                        .as_ref()
                        .is_some_and(|migration| migration.state == MigrationState::RollingBack)
            } else {
                rollback.generation.checked_add(1) != Some(record.document.generation)
                    || record.document.migration.is_some()
            }
        {
            return Err(SchemaError::invalid("stored rollback identity is invalid"));
        }
    }
    if record.idempotency.len() > MAX_IDEMPOTENCY_KEYS {
        return Err(SchemaError::invalid("stored schema has too many replay entries"));
    }
    for (key, replay) in &record.idempotency {
        validate_idempotency_key(key)?;
        if replay.digest.len() != 64
            || !replay
                .digest
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return Err(SchemaError::invalid("stored replay digest is invalid"));
        }
        validate_stored_document(target, &replay.response)?;
        if replay.response.identity != record.document.identity
            || replay.response.generation > record.document.generation
            || (record.rollback.is_none()
                && replay.response.schema_version > record.document.schema_version)
        {
            return Err(SchemaError::invalid(
                "stored replay does not belong to this schema history",
            ));
        }
    }
    Ok(())
}

fn validate_stored_document(
    target: &SchemaTarget,
    document: &SchemaDocument,
) -> Result<(), SchemaError> {
    validate_identity(&document.identity)?;
    validate_version(document.schema_version)?;
    if document.target != *target
        || document.schema.name != target.collection
        || document.generation == 0
        || document.schema.version.is_some_and(|version| version != document.schema_version)
        || normalize_schema(document.schema.clone())? != document.schema
    {
        return Err(SchemaError::invalid(
            "stored schema target, generation, or canonical schema is invalid",
        ));
    }
    if let Some(migration) = &document.migration {
        let operation_id = migration_id(target, &document.schema)
            .map_err(|_| SchemaError::invalid("stored migration digest cannot be computed"))?;
        if migration.operation_id != operation_id
            || migration.steps.is_empty()
            || migration.steps.len() > MAX_MIGRATION_STEPS
            || migration.total > 10_000_000_000_000
            || migration.completed > migration.total
            || migration.next_cursor.as_ref().is_some_and(|cursor| cursor.len() > MAX_SCHEMA_BYTES)
            || migration.message.len() > 512
            || (migration.state == MigrationState::Complete
                && migration.completed != migration.total)
            || (migration.state == MigrationState::Pending
                && (migration.total != 0
                    || migration.completed != 0
                    || migration.next_cursor.is_some()))
        {
            return Err(SchemaError::invalid("stored migration status is invalid"));
        }
        for step in &migration.steps {
            let valid = match step.kind.as_str() {
                "schema_reconcile" => {
                    step.from.is_none() && step.to.is_none() && step.index.is_none()
                }
                "field_rename" => {
                    step.index.is_none()
                        && document.schema.renames.iter().any(|rename| {
                            step.from.as_ref() == Some(&rename.from)
                                && step.to.as_ref() == Some(&rename.to)
                        })
                }
                "index_backfill" => {
                    step.from.is_none()
                        && step.to.is_none()
                        && document
                            .schema
                            .indexes
                            .iter()
                            .any(|index| step.index.as_ref() == Some(&index.name))
                }
                "ttl_backfill" => {
                    step.from.is_none()
                        && step.to.is_none()
                        && step.index.is_none()
                        && document.schema.ttl.is_some()
                }
                _ => false,
            };
            if !valid {
                return Err(SchemaError::invalid("stored migration step is invalid"));
            }
        }
    }
    Ok(())
}

fn next_schema_version(
    current: Option<&DurableSchemaRecord>,
    requested: Option<u64>,
) -> Result<u64, SchemaError> {
    let previous = current.map_or(0, |record| record.document.schema_version);
    if let Some(requested) = requested {
        validate_version(requested)?;
        if requested <= previous {
            return Err(SchemaError::new(
                409,
                "schema_version_regression",
                format!("schema version {requested} is not newer than durable version {previous}"),
                "increment the schema version and retry",
            ));
        }
        Ok(requested)
    } else {
        let next = previous.saturating_add(1);
        validate_version(next)?;
        Ok(next)
    }
}

pub(crate) fn validate_target(target: &SchemaTarget) -> Result<(), SchemaError> {
    validate_identifier(&target.database, "database")?;
    validate_identifier(&target.branch, "branch")?;
    validate_identifier(&target.collection, "collection")
}

fn validate_identity(identity: &DatabaseIdentity) -> Result<(), SchemaError> {
    if identity.database_id.is_empty() || identity.database_id.len() > MAX_IDENTIFIER_BYTES {
        return Err(SchemaError::invalid("database identity is missing or too long"));
    }
    if identity.placement.is_empty() || identity.placement.len() > 32 {
        return Err(SchemaError::invalid(
            "database placement must contain between 1 and 32 regions",
        ));
    }
    if identity.placement.iter().any(|region| region.is_empty() || region.len() > 64) {
        return Err(SchemaError::invalid("database placement contains an invalid region"));
    }
    Ok(())
}

fn ensure_identity(
    stored: &DatabaseIdentity,
    current: &DatabaseIdentity,
) -> Result<(), SchemaError> {
    if stored != current {
        return Err(SchemaError::new(
            409,
            "database_identity_changed",
            "database id or placement changed while the schema was pinned",
            "route the request to the database's current hosting member and preserve its placement lock",
        ));
    }
    Ok(())
}

pub(crate) fn validate_idempotency_key(key: &str) -> Result<(), SchemaError> {
    if key.is_empty() || key.len() > MAX_IDEMPOTENCY_KEY_BYTES || !key.is_ascii() {
        return Err(SchemaError::new(
            400,
            "invalid_idempotency_key",
            "Idempotency-Key must be 1-256 ASCII bytes",
            "send a unique opaque Idempotency-Key header",
        ));
    }
    Ok(())
}

fn validate_version(version: u64) -> Result<(), SchemaError> {
    if version == 0 || version > MAX_SCHEMA_VERSION {
        return Err(SchemaError::invalid("schema version must be a positive safe integer"));
    }
    Ok(())
}

fn validate_identifier(value: &str, kind: &str) -> Result<(), SchemaError> {
    if value.is_empty() || value.len() > MAX_IDENTIFIER_BYTES {
        return Err(SchemaError::invalid(format!(
            "{kind} name must be 1-255 bytes and match [A-Za-z_][A-Za-z0-9_]*"
        )));
    }
    let mut chars = value.chars();
    let Some(first) = chars.next() else {
        return Err(SchemaError::invalid(format!("{kind} name is empty")));
    };
    if !(first.is_ascii_alphabetic() || first == '_')
        || !chars.all(|character| character.is_ascii_alphanumeric() || character == '_')
    {
        return Err(SchemaError::invalid(format!(
            "{kind} name '{value}' must match [A-Za-z_][A-Za-z0-9_]*"
        )));
    }
    Ok(())
}

fn durable_error(error: &DurablePortError) -> SchemaError {
    SchemaError::new(
        503,
        "durable_store_error",
        format!(
            "durable schema storage refused the operation: {}",
            bounded(error.detail.clone(), 256)
        ),
        "retry after the member's durable control store is healthy",
    )
}

fn bounded(mut value: String, max: usize) -> String {
    if value.len() <= max {
        return value;
    }
    let suffix = &"..."[..max.min(3)];
    let mut end = max - suffix.len();
    while !value.is_char_boundary(end) {
        end -= 1;
    }
    value.truncate(end);
    value.push_str(suffix);
    value
}

const fn default_true() -> bool {
    true
}
const fn default_history_days() -> u32 {
    7
}
#[allow(
    clippy::trivially_copy_pass_by_ref,
    reason = "serde skip_serializing_if passes a borrowed field"
)]
const fn is_false(value: &bool) -> bool {
    !*value
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;
    use terrabase::identity::DatabaseUlid;

    #[derive(Default)]
    struct FakeStore(Mutex<BTreeMap<SchemaTarget, DurableSchemaRecord>>);

    #[derive(Default)]
    struct FakeCatalog(Mutex<Option<SchemaCatalogRecord>>);

    impl SchemaCatalogAdapter for FakeCatalog {
        type Error = String;

        fn load(
            &mut self,
            branch: &SchemaBranchPin,
        ) -> Result<Option<SchemaCatalogRecord>, Self::Error> {
            Ok(self
                .0
                .lock()
                .map_err(|_| "lock poisoned".to_owned())?
                .as_ref()
                .filter(|record| record.branch() == branch)
                .cloned())
        }

        fn compare_and_swap(
            &mut self,
            request: &SchemaCatalogCasRequest,
        ) -> Result<SchemaCatalogCasOutcome, Self::Error> {
            let mut current = self.0.lock().map_err(|_| "lock poisoned".to_owned())?;
            let outcome =
                request.evaluate(current.as_ref()).map_err(|error| format!("{error:?}"))?;
            if outcome == SchemaCatalogCasOutcome::Applied {
                *current = Some(request.desired().clone());
            }
            Ok(outcome)
        }
    }

    impl SchemaDurablePort for FakeStore {
        fn load(
            &self,
            target: &SchemaTarget,
        ) -> Result<Option<DurableSchemaRecord>, DurablePortError> {
            Ok(self.0.lock().expect("lock").get(target).cloned())
        }
        fn commit(
            &self,
            target: &SchemaTarget,
            expected: Option<&DurableSchemaRecord>,
            record: DurableSchemaRecord,
        ) -> Result<DurableCommit, DurablePortError> {
            let mut records = self.0.lock().expect("lock");
            let current = records.get(target);
            if current != expected {
                return Ok(DurableCommit::GenerationConflict);
            }
            records.insert(target.clone(), record);
            Ok(DurableCommit::Committed)
        }
    }

    fn identity() -> DatabaseIdentity {
        DatabaseIdentity { database_id: "db-1".into(), placement: vec!["GRU".into(), "FRA".into()] }
    }
    fn target() -> SchemaTarget {
        SchemaTarget { database: "demo".into(), branch: "main".into(), collection: "orders".into() }
    }
    fn schema(version: Option<u64>) -> CollectionSchema {
        CollectionSchema {
            name: "orders".into(),
            fields: vec![
                FieldDescriptor {
                    name: "expiresAt".into(),
                    r#type: FieldType::Timestamp,
                    required: true,
                    indexed: false,
                    unique: false,
                    merge_policy: None,
                    fts: false,
                },
                FieldDescriptor {
                    name: "title".into(),
                    r#type: FieldType::String,
                    required: true,
                    indexed: false,
                    unique: false,
                    merge_policy: None,
                    fts: true,
                },
            ],
            indexes: vec![],
            fts: None,
            ttl: Some(TtlDescriptor { field: "expiresAt".into() }),
            renames: vec![],
            version,
            history_retention_days: 7,
            merge_policy: MergePolicy::RemoveWins,
        }
    }
    fn apply_request(
        service: &SchemaService,
        version: Option<u64>,
        generation: Option<u64>,
        key: &str,
    ) -> Result<ApplySchemaResponse, SchemaError> {
        service.apply(ApplySchemaRequest {
            target: target(),
            identity: identity(),
            schema: schema(version),
            expected_generation: generation,
            idempotency_key: key.into(),
        })
    }

    #[test]
    fn corrupt_durable_records_fail_reads_and_replays_without_writes() {
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store.clone()));
        apply_request(&service, Some(1), None, "first").expect("initial apply");
        let original = store.0.lock().unwrap()[&target()].clone();
        let corruptions: Vec<fn(&mut DurableSchemaRecord)> = vec![
            |r| r.document.target.branch = "foreign".into(),
            |r| r.document.generation = 0,
            |r| r.document.schema_version = 2,
            |r| r.document.schema.fields[0].name = " invalid".into(),
            |r| r.document.identity.database_id.clear(),
            |r| r.idempotency.get_mut("first").unwrap().digest = "invalid".into(),
            |r| r.idempotency.get_mut("first").unwrap().response.target.branch = "foreign".into(),
            |r| {
                r.idempotency.get_mut("first").unwrap().response.identity.database_id =
                    "foreign".into();
            },
            |r| r.idempotency.get_mut("first").unwrap().response.generation = 2,
        ];
        for corrupt in corruptions {
            let mut record = original.clone();
            corrupt(&mut record);
            store.0.lock().unwrap().insert(target(), record.clone());
            for error in [
                service.get(&target(), &identity()).unwrap_err(),
                apply_request(&service, Some(1), None, "first").unwrap_err(),
            ] {
                assert_eq!(error.status, 503);
                assert_eq!(error.code, "durable_store_error");
            }
            assert_eq!(store.0.lock().unwrap()[&target()], record);
        }
    }

    fn finish_initial_backfill(service: &SchemaService) {
        let document = service.get(&target(), &identity()).expect("initial schema");
        let migration = document.migration.expect("initial descriptors need backfill");
        service
            .record_backfill_progress(BackfillProgress {
                target: target(),
                generation: document.generation,
                operation_id: migration.operation_id,
                idempotency_key: "initial-worker".into(),
                total: 0,
                completed: 0,
                next_cursor: None,
                failed: false,
            })
            .expect("initial descriptor worker completion");
    }

    #[test]
    fn stored_migration_validation_accepts_history_and_rejects_corruption() {
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store.clone()));
        apply_request(&service, None, None, "first").unwrap();
        finish_initial_backfill(&service);
        let mut next = schema(None);
        next.indexes.push(IndexDescriptor {
            name: "title_index".into(),
            fields: vec!["title".into()],
            unique: false,
            kind: Some(IndexKind::Btree),
        });
        service
            .apply(ApplySchemaRequest {
                target: target(),
                identity: identity(),
                schema: next,
                expected_generation: Some(1),
                idempotency_key: "second".into(),
            })
            .unwrap();
        service.get(&target(), &identity()).expect("valid pending migration and historical replay");
        assert!(apply_request(&service, None, None, "first").unwrap().idempotent_replay);
        let original = store.0.lock().unwrap()[&target()].clone();
        let corruptions: Vec<fn(&mut MigrationStatus)> = vec![
            |m| m.operation_id = "0".repeat(64),
            |m| m.completed = 1,
            |m| m.total = 10_000_000_000_001,
            |m| m.steps.clear(),
            |m| m.steps[0].kind = "unknown".into(),
            |m| m.steps[0].index = Some("foreign".into()),
            |m| m.next_cursor = Some("x".repeat(MAX_SCHEMA_BYTES + 1)),
            |m| {
                m.state = MigrationState::Complete;
                m.total = 1;
            },
        ];
        for corrupt in corruptions {
            let mut record = original.clone();
            corrupt(record.document.migration.as_mut().unwrap());
            store.0.lock().unwrap().insert(target(), record);
            assert_eq!(service.get(&target(), &identity()).unwrap_err().status, 503);
        }
    }

    #[test]
    fn automatic_schema_version_cannot_exceed_safe_integer() {
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store.clone()));
        apply_request(&service, Some(MAX_SCHEMA_VERSION), None, "first").unwrap();
        finish_initial_backfill(&service);
        let original = store.0.lock().unwrap()[&target()].clone();
        let error = apply_request(&service, None, Some(1), "second").unwrap_err();
        assert_eq!(error.status, 422);
        assert_eq!(error.code, "invalid_schema");
        assert_eq!(store.0.lock().unwrap()[&target()], original);
    }

    #[test]
    fn generated_fts_indexes_obey_count_and_identifier_limits_before_commit() {
        for explicit_count in [MAX_INDEXES - 1, MAX_INDEXES] {
            let store = Arc::new(FakeStore::default());
            let service = SchemaService::new(Some(store.clone()));
            let mut input = schema(Some(1));
            input.indexes = (0..explicit_count)
                .map(|index| IndexDescriptor {
                    name: format!("index_{index}"),
                    fields: vec!["title".into()],
                    unique: false,
                    kind: Some(IndexKind::Btree),
                })
                .collect();
            let result = service.apply(ApplySchemaRequest {
                target: target(),
                identity: identity(),
                schema: input,
                expected_generation: None,
                idempotency_key: "first".into(),
            });
            if explicit_count == MAX_INDEXES - 1 {
                let applied = result.expect("exact normalized bound");
                assert_eq!(applied.document.schema.indexes.len(), MAX_INDEXES);
                assert_eq!(
                    normalize_schema(applied.document.schema.clone()).expect("normalize again"),
                    applied.document.schema
                );
            } else {
                assert_eq!(result.expect_err("generated index exceeds bound").status, 422);
                assert!(store.load(&target()).expect("load").is_none());
            }
        }
        for field_length in [MAX_IDENTIFIER_BYTES - 4, MAX_IDENTIFIER_BYTES - 3] {
            let store = Arc::new(FakeStore::default());
            let service = SchemaService::new(Some(store.clone()));
            let mut input = schema(Some(1));
            input.fields[1].name = "f".repeat(field_length);
            let result = service.apply(ApplySchemaRequest {
                target: target(),
                identity: identity(),
                schema: input,
                expected_generation: None,
                idempotency_key: "first".into(),
            });
            if field_length == MAX_IDENTIFIER_BYTES - 4 {
                assert_eq!(
                    result.expect("exact generated name bound").document.schema.indexes[0]
                        .name
                        .len(),
                    MAX_IDENTIFIER_BYTES
                );
            } else {
                assert_eq!(result.expect_err("generated name exceeds bound").status, 422);
                assert!(store.load(&target()).expect("load").is_none());
            }
        }
    }

    #[test]
    fn absent_durable_port_fails_closed() {
        let service = SchemaService::new(None);
        let error = apply_request(&service, Some(1), None, "first")
            .expect_err("missing store must fail closed");
        assert_eq!(error.code, "durable_store_unavailable");
        assert_eq!(error.status, 503);
    }

    #[test]
    fn canonicalizes_fts_and_defaults_and_rejects_bad_ttl_before_commit() {
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store));
        let result = apply_request(&service, Some(1), None, "first").expect("valid schema");
        assert_eq!(result.document.schema.indexes[0].name, "title_fts");
        assert!(!result.document.schema.fields[1].fts);
        let mut bad = schema(Some(2));
        bad.ttl = Some(TtlDescriptor { field: "title".into() });
        let error = service
            .apply(ApplySchemaRequest {
                target: target(),
                identity: identity(),
                schema: bad,
                expected_generation: Some(1),
                idempotency_key: "bad".into(),
            })
            .expect_err("bad TTL");
        assert_eq!(error.code, "invalid_schema");
        assert_eq!(service.get(&target(), &identity()).expect("original remains").generation, 1);
    }

    #[test]
    fn idempotency_and_generation_are_durable() {
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store));
        let first = apply_request(&service, Some(1), None, "same").expect("first");
        let replay = apply_request(&service, Some(1), None, "same").expect("replay");
        assert!(replay.idempotent_replay);
        assert_eq!(replay.document.generation, first.document.generation);
        let stale =
            apply_request(&service, Some(2), Some(0), "stale").expect_err("stale generation");
        assert_eq!(stale.code, "generation_mismatch");
        let conflict = apply_request(&service, Some(2), Some(1), "same")
            .expect_err("same key changed payload");
        assert_eq!(conflict.code, "idempotency_conflict");
    }

    #[test]
    fn rename_plans_resumable_backfill_and_progress_is_cas_protected() {
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store));
        apply_request(&service, Some(1), None, "first").expect("first");
        finish_initial_backfill(&service);
        let mut changed = schema(Some(2));
        changed.fields[1].name = "headline".into();
        changed.renames = vec![FieldRename { from: "title".into(), to: "headline".into() }];
        let applied = service
            .apply(ApplySchemaRequest {
                target: target(),
                identity: identity(),
                schema: changed,
                expected_generation: Some(1),
                idempotency_key: "rename".into(),
            })
            .expect("rename");
        let migration = applied.document.migration.expect("migration");
        assert_eq!(migration.state, MigrationState::Pending);
        let progress = service
            .record_backfill_progress(BackfillProgress {
                target: target(),
                generation: 2,
                operation_id: migration.operation_id,
                idempotency_key: "worker-1".into(),
                total: 10,
                completed: 10,
                next_cursor: None,
                failed: false,
            })
            .expect("progress");
        assert_eq!(progress.migration.expect("migration").state, MigrationState::Complete);
    }

    #[test]
    fn concurrent_backfill_progress_cannot_overwrite_a_more_advanced_cursor() {
        struct RacingStore {
            inner: Arc<FakeStore>,
            loaded: std::sync::Barrier,
            advanced: std::sync::Barrier,
        }
        impl SchemaDurablePort for RacingStore {
            fn load(
                &self,
                target: &SchemaTarget,
            ) -> Result<Option<DurableSchemaRecord>, DurablePortError> {
                let record = self.inner.load(target)?;
                self.loaded.wait();
                Ok(record)
            }
            fn commit(
                &self,
                target: &SchemaTarget,
                expected: Option<&DurableSchemaRecord>,
                record: DurableSchemaRecord,
            ) -> Result<DurableCommit, DurablePortError> {
                if record.document.migration.as_ref().expect("migration").completed == 50 {
                    let result = self.inner.commit(target, expected, record);
                    self.advanced.wait();
                    result
                } else {
                    self.advanced.wait();
                    self.inner.commit(target, expected, record)
                }
            }
        }
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store.clone()));
        apply_request(&service, Some(1), None, "first").expect("first");
        finish_initial_backfill(&service);
        let mut changed = schema(Some(2));
        changed.fields[1].name = "headline".into();
        changed.renames = vec![FieldRename { from: "title".into(), to: "headline".into() }];
        let applied = service
            .apply(ApplySchemaRequest {
                target: target(),
                identity: identity(),
                schema: changed,
                expected_generation: Some(1),
                idempotency_key: "rename".into(),
            })
            .expect("rename");
        let operation_id = applied.document.migration.expect("migration").operation_id;
        let service = SchemaService::new(Some(Arc::new(RacingStore {
            inner: store.clone(),
            loaded: std::sync::Barrier::new(2),
            advanced: std::sync::Barrier::new(2),
        })));
        let outcomes = std::thread::scope(|threads| {
            let handles = [10, 50].map(|completed| {
                let service = &service;
                let operation_id = &operation_id;
                threads.spawn(move || {
                    service.record_backfill_progress(BackfillProgress {
                        target: target(),
                        generation: 2,
                        operation_id: operation_id.clone(),
                        idempotency_key: format!("worker-{completed}"),
                        total: 100,
                        completed,
                        next_cursor: Some(format!("cursor-{completed}")),
                        failed: false,
                    })
                })
            });
            handles.map(|handle| handle.join().expect("worker"))
        });
        assert_eq!(outcomes[0].as_ref().expect_err("stale worker").code, "generation_conflict");
        assert!(outcomes[1].is_ok());
        let final_record = store.load(&target()).expect("load").expect("record");
        let migration = final_record.document.migration.expect("migration");
        assert_eq!(migration.completed, 50);
        assert_eq!(migration.next_cursor.as_deref(), Some("cursor-50"));
        assert!(final_record.idempotency.contains_key("worker-50"));
        assert!(!final_record.idempotency.contains_key("worker-10"));
    }

    #[test]
    fn identity_is_immutable() {
        let store = Arc::new(FakeStore::default());
        let service = SchemaService::new(Some(store));
        apply_request(&service, Some(1), None, "first").expect("first");
        finish_initial_backfill(&service);
        let mut changed = identity();
        changed.placement = vec!["SFO".into()];
        let error = service.get(&target(), &changed).expect_err("placement changed");
        assert_eq!(error.code, "database_identity_changed");
    }

    #[test]
    fn canonical_family_document_round_trip_preserves_all_metadata() {
        let input = SchemaJson { collections: vec![schema(Some(7))], version: Some(12) };
        let canonical = canonical_schema_document(&input).expect("canonical schema");
        assert_eq!(canonical.version().expect("document version").get(), 12);
        let output = schema_json_from_canonical(&canonical);
        assert_eq!(output.version, Some(12));
        let collection = &output.collections[0];
        assert_eq!(collection.version, Some(7));
        assert_eq!(collection.history_retention_days, 7);
        assert_eq!(collection.ttl, input.collections[0].ttl);
        assert_eq!(collection.merge_policy, MergePolicy::RemoveWins);
        assert_eq!(collection.indexes[0].kind, Some(IndexKind::Fts));
    }

    #[test]
    fn canonical_service_delegates_family_cas_and_replays_entry_identity() {
        let branch =
            SchemaBranchPin::try_new(DatabaseUlid::generate(), "main", 1).expect("branch pin");
        let store: Arc<dyn SchemaCatalogPort> =
            Arc::new(DurableSchemaCatalogAdapter::new(FakeCatalog::default()));
        let service = CanonicalSchemaService::new(store);
        let schema = canonical_schema_document(&SchemaJson {
            collections: vec![schema(Some(1))],
            version: Some(1),
        })
        .expect("canonical schema");
        let entry_id = SchemaCatalogEntryUlid::generate();
        let request = CanonicalSchemaApplyRequest {
            branch: branch.clone(),
            entry_id,
            schema,
            schema_version: SchemaVersion::try_new(1).expect("version"),
            migration_generation: 0,
            migration_status: SchemaMigrationStatus::None,
            expected: SchemaCatalogExpectation::absent(),
        };
        let first = service.apply(request.clone()).expect("first apply");
        assert!(!first.idempotent_replay);
        let replay = service.apply(request).expect("replay");
        assert!(replay.idempotent_replay);
        assert_eq!(replay.record.checksum(), first.record.checksum());
        assert_eq!(service.get(&branch).expect("load").expect("record").branch(), &branch);
    }
    struct BlockingIdentity {
        entered: Mutex<Option<tokio::sync::oneshot::Sender<()>>>,
        resume: Mutex<std::sync::mpsc::Receiver<()>>,
    }

    impl SchemaIdentityPort for BlockingIdentity {
        fn resolve(&self, _: &str, _: &str) -> Result<DatabaseIdentity, SchemaError> {
            self.entered.lock().expect("gate").take().expect("one call").send(()).expect("entered");
            self.resume
                .lock()
                .expect("gate")
                .recv_timeout(std::time::Duration::from_secs(5))
                .expect("async worker releases identity lookup");
            Ok(identity())
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn schema_handlers_keep_the_runtime_responsive_during_identity_lookup() {
        for write in [false, true] {
            let (entered, waiting) = tokio::sync::oneshot::channel();
            let (resume, receiver) = std::sync::mpsc::channel();
            let state = Arc::new(SchemaHttpState::production(
                Arc::new(SchemaService::new(Some(Arc::new(FakeStore::default())))),
                Arc::new(BlockingIdentity {
                    entered: Mutex::new(Some(entered)),
                    resume: Mutex::new(receiver),
                }),
            ));
            let request = tokio::spawn(async move {
                let path = Path(("demo".to_owned(), "orders".to_owned()));
                let mut headers = HeaderMap::new();
                if write {
                    headers.insert("idempotency-key", HeaderValue::from_static("blocking-schema"));
                    put_schema(
                        State(state),
                        path,
                        headers,
                        serde_json::to_string(&schema(Some(1))).expect("schema"),
                    )
                    .await
                } else {
                    get_schema(State(state), path, headers).await
                }
            });
            waiting.await.expect("identity lookup entered");
            resume.send(()).expect("sole async worker can release the lookup");
            let response = request.await.expect("handler");
            assert_eq!(
                response.status(),
                if write { StatusCode::OK } else { StatusCode::NOT_FOUND }
            );
            if write {
                let tag = response.headers().get(ETAG).expect("strong ETag");
                assert_eq!(tag, "\"1\"");
                let mut conditional = HeaderMap::new();
                conditional.insert(IF_MATCH, tag.clone());
                assert_eq!(parse_if_match(&conditional).expect("round trip"), Some(1));
            }
        }
    }
    #[test]
    fn error_text_limits_count_utf8_bytes() {
        for value in ["ascii".repeat(200), "é".repeat(600), "界".repeat(600), "🦀".repeat(600)]
        {
            for limit in [0, 1, 2, 3, 4, 5, 256, 512] {
                let result = bounded(value.clone(), limit);
                assert!(result.len() <= limit);
                assert!(std::str::from_utf8(result.as_bytes()).is_ok());
                if limit >= 3 {
                    assert!(result.ends_with("..."));
                    assert!(value.starts_with(&result[..result.len() - 3]));
                }
            }
        }
        assert_eq!(bounded("é".into(), 2), "é");
        let error = SchemaError::new(400, "test", "🦀".repeat(600), "界".repeat(600));
        assert!(error.message.len() <= 512);
        assert!(error.fix.len() <= 512);
        let durable = durable_error(&DurablePortError { detail: "é".repeat(600) });
        assert!(durable.message.len() <= 512);
    }
    #[test]
    fn conditional_schema_updates_require_a_single_strong_generation_tag() {
        let mut headers = HeaderMap::new();
        assert_eq!(parse_if_match(&headers).expect("absent"), None);
        for invalid in [
            "7",
            "W/\"7\"",
            "\"7",
            "7\"",
            "\"\"7\"\"",
            "\"+7\"",
            "\"07\"",
            "\"7\", \"8\"",
            "*",
            "\"18446744073709551616\"",
        ] {
            headers.insert(IF_MATCH, HeaderValue::from_str(invalid).expect("header"));
            assert!(parse_if_match(&headers).is_err(), "accepted invalid tag {invalid}");
        }
        headers.insert(IF_MATCH, HeaderValue::from_static("\"7\""));
        assert_eq!(parse_if_match(&headers).expect("strong"), Some(7));
        headers.append(IF_MATCH, HeaderValue::from_static("\"8\""));
        assert!(parse_if_match(&headers).is_err());
    }
    struct RejectIdentityLookup;

    impl SchemaIdentityPort for RejectIdentityLookup {
        fn resolve(&self, _: &str, _: &str) -> Result<DatabaseIdentity, SchemaError> {
            panic!("malformed headers must fail before identity lookup");
        }
    }

    #[tokio::test]
    async fn malformed_schema_headers_never_default_to_main_or_reach_identity_lookup() {
        for header in ["x-terrascale-branch", "idempotency-key"] {
            for duplicate in [false, true] {
                let state = Arc::new(SchemaHttpState::production(
                    Arc::new(SchemaService::new(None)),
                    Arc::new(RejectIdentityLookup),
                ));
                let mut headers = HeaderMap::new();
                headers.insert("idempotency-key", HeaderValue::from_static("request"));
                if duplicate {
                    headers.insert(header, HeaderValue::from_static("first"));
                    headers.append(header, HeaderValue::from_static("second"));
                } else {
                    headers
                        .insert(header, HeaderValue::from_bytes(&[0x80]).expect("opaque header"));
                }
                if header == "x-terrascale-branch" {
                    let response = get_schema(
                        State(state.clone()),
                        Path(("demo".into(), "orders".into())),
                        headers.clone(),
                    )
                    .await;
                    assert_eq!(response.status(), StatusCode::BAD_REQUEST);
                }
                let response = put_schema(
                    State(state),
                    Path(("demo".into(), "orders".into())),
                    headers,
                    serde_json::to_string(&schema(Some(1))).expect("schema"),
                )
                .await;
                assert_eq!(response.status(), StatusCode::BAD_REQUEST);
            }
        }
        assert_eq!(header_text(&HeaderMap::new(), "x-terrascale-branch").expect("absent"), None);
    }
}
