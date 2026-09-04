//! Typed schema builder. Schema JSON is the source of truth (GOAL D-48):
//! authors declare collections through this builder, `terrascale push`
//! applies them to a branch, and codegen generates types from them.
//!
//! `#[derive(Schema)]` (re-exported from `terrascale-derive`, currently a
//! stub) implements [`Document`] with the struct name as collection.

pub use terrascale_derive::Schema;

use serde::{Deserialize, Serialize};

/// Extracted / stored field types for indexing and typed queries.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum FieldType {
    String,
    Integer,
    Float,
    Boolean,
    Json,
    Bytes,
    Timestamp,
}

/// One declared field of a collection.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FieldDef {
    /// Field name (JSON path segment at the document root).
    pub name: String,
    /// Stored type used for extraction and indexing.
    pub field_type: FieldType,
    /// Whether the field may be absent or null.
    pub optional: bool,
    /// Whether the write path maintains an index over this field.
    pub indexed: bool,
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
        }
    }

    /// Declare an optional field.
    #[must_use]
    pub fn optional(name: impl Into<String>, field_type: FieldType) -> Self {
        Self {
            name: name.into(),
            field_type,
            optional: true,
            indexed: false,
        }
    }

    /// Mark the field as indexed (maintained in the write transaction).
    #[must_use]
    pub fn indexed(mut self) -> Self {
        self.indexed = true;
        self
    }
}

/// The schema of one collection.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CollectionSchema {
    /// Collection name.
    pub name: String,
    /// Declared fields.
    pub fields: Vec<FieldDef>,
}

impl CollectionSchema {
    /// Start a collection schema with no fields.
    #[must_use]
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            fields: Vec::new(),
        }
    }

    /// Append a field declaration.
    #[must_use]
    pub fn field(mut self, field: FieldDef) -> Self {
        self.fields.push(field);
        self
    }
}

/// A document type bound to one collection.
///
/// Hand-implement for full control, or `#[derive(Schema)]` for the stub
/// convention (struct name, lowercased, as collection).
pub trait Document {
    /// Collection this document type lives in.
    fn collection() -> &'static str;
    /// Typed schema used by `push` and codegen.
    fn schema() -> CollectionSchema;
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

    /// Add a document type via its [`Document`] impl.
    #[must_use]
    pub fn document<T: Document>(mut self) -> Self {
        self.collections.push(T::schema());
        self
    }

    /// Finish and return the collection schemas.
    #[must_use]
    pub fn build(self) -> Vec<CollectionSchema> {
        self.collections
    }
}
