//! Buckets, canonical shard identities, and shard keys (CONTEXT: Bucket,
//! Shard, Shard Key).
//!
//! A bucket is the permanent routing identity of a document: the fixed
//! [`hash`](crate::sharding::hash) of its shard key reduced into the fixed
//! 2^20 space. A shard's identity is its canonical range text in the
//! `TopologyView`, independent of the bucket boundaries it currently owns.

use std::fmt;

use super::{ShardingError, hash};
use crate::types::{CollectionName, DocumentId, DocumentValue};

/// The prefix of every canonical shard identity.
const SHARD_PREFIX: &str = "shard/";

/// The maximum byte length of a shard-key field name (the crate's logical
/// identifier bound).
const MAX_FIELD_NAME_BYTES: usize = 255;

/// The separator of the composite shard-key canonical form: the decimal byte
/// length of a component, U+001F, then the component's bytes, repeated.
const COMPOSITE_SEPARATOR: u8 = 0x1F;

/// A bucket: the permanent routing identity of one document (CONTEXT:
/// Bucket).
///
/// The value is a pure function of the document's shard key inside the fixed
/// [0, [`BUCKET_SPACE`](crate::sharding::BUCKET_SPACE)) space and never
/// changes. A bucket belongs to exactly one shard at any epoch; it is the
/// unit of migration and of bucket-scoped storage accounting (PB-5037).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Bucket {
    value: u32,
}

impl Bucket {
    /// Creates one bucket from its value inside the fixed space.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::BucketAboveSpace`] when `value` is not
    /// below [`BUCKET_SPACE`](crate::sharding::BUCKET_SPACE): no bucket
    /// exists outside the fixed 2^20 space, so no virtual-bucket scheme
    /// (V1 prefixed routing) can be expressed.
    pub const fn try_new(value: u32) -> Result<Self, ShardingError> {
        if value < crate::sharding::BUCKET_SPACE {
            Ok(Self { value })
        } else {
            Err(ShardingError::BucketAboveSpace { value })
        }
    }

    /// Returns the bucket's value in [0, 2^20).
    #[must_use]
    pub const fn get(self) -> u32 {
        self.value
    }
}

/// The canonical identity of one shard: `shard/<number>` (CONTEXT: Shard).
///
/// The identity is the range text the Platform and the engine agree on
/// (ADR 0011 D1): exactly one text per identity, decimal `u32` without
/// leading zeros, independent of the shard's bucket boundaries, so epochs
/// and fencing history stay attached to a durable identity across
/// resharding. A shard is an owned data range in the `TopologyView` with one
/// owning Member and one ownership epoch — never a database or process.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ShardId {
    number: u32,
}

impl ShardId {
    /// Creates one shard identity from its number.
    #[must_use]
    pub const fn from_number(number: u32) -> Self {
        Self { number }
    }

    /// Returns the shard's number.
    #[must_use]
    pub const fn number(self) -> u32 {
        self.number
    }

    /// Returns the canonical range text of this identity (`shard/<number>`),
    /// the text the `TopologyView` carries as the owned range.
    #[must_use]
    pub fn as_range(self) -> String {
        format!("{SHARD_PREFIX}{}", self.number)
    }

    /// Parses one canonical shard identity from range text.
    ///
    /// Returns `None` for anything but `shard/<number>` with a decimal
    /// `u32` without leading zeros: one identity, one text.
    #[must_use]
    pub fn from_range_text(text: &str) -> Option<Self> {
        let digits = text.strip_prefix(SHARD_PREFIX)?;
        if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        if digits.len() > 1 && digits.starts_with('0') {
            return None;
        }
        digits.parse::<u32>().ok().map(Self::from_number)
    }
}

impl fmt::Display for ShardId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.as_range())
    }
}

/// One resolved shard key: the bytes the bucket is derived from.
///
/// The key is built by [`ShardKey::identity`] (the lowercased document
/// identity, V1's default) or [`ShardKey::composite`] (rendered field
/// values in declaration order, length-delimited so no two component
/// sequences share bytes). Its bucket is
/// `xxh64(bytes) % 2^20` — a pure function of the key, never of topology
/// state.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct ShardKey {
    bytes: Vec<u8>,
}

impl ShardKey {
    /// Resolves the identity-keyed shard key: the document identity,
    /// lowercased with the Unicode-aware lowercase mapping, as UTF-8 bytes.
    #[must_use]
    pub fn identity(document: &DocumentId) -> Self {
        Self { bytes: document.as_str().to_lowercase().into_bytes() }
    }

    /// Builds the composite shard key from rendered components.
    ///
    /// The canonical form writes each component as its decimal byte length,
    /// a U+001F separator, then its bytes, so `["ab", "c"]` and `["a", "bc"]`
    /// never collide and component order is part of the key.
    #[must_use]
    pub fn composite(components: &[String]) -> Self {
        let mut bytes = Vec::new();
        for component in components {
            bytes.extend_from_slice(component.len().to_string().as_bytes());
            bytes.push(COMPOSITE_SEPARATOR);
            bytes.extend_from_slice(component.as_bytes());
        }
        Self { bytes }
    }

    /// Returns the canonical bytes of this key.
    #[must_use]
    pub fn as_bytes(&self) -> &[u8] {
        &self.bytes
    }

    /// Returns the bucket this key routes to: the fixed hash of the key
    /// bytes reduced into the fixed 2^20 space.
    ///
    /// # Panics
    ///
    /// Cannot panic: the digest is reduced modulo the space size, so the
    /// reduced value always fits the space's `u32` bounds.
    #[must_use]
    pub fn bucket(&self) -> Bucket {
        let digest = hash::xxh64(&self.bytes);
        let reduced = digest % u64::from(crate::sharding::BUCKET_SPACE);
        Bucket::try_new(u32::try_from(reduced).expect("a reduced digest stays below 2^20"))
            .expect("a reduced digest stays inside the space")
    }
}

/// One named attribute of a field shard key.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ShardKeyField {
    name: String,
}

impl ShardKeyField {
    /// Creates one validated shard-key field name.
    ///
    /// The rules match the crate's logical identifiers: non-empty, at most
    /// 255 bytes, no control characters, no leading or trailing whitespace.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::InvalidShardKeyName`] for text that breaks
    /// any of those rules.
    pub fn try_new(name: &str) -> Result<Self, ShardingError> {
        if name.is_empty()
            || name.len() > MAX_FIELD_NAME_BYTES
            || name.chars().any(char::is_control)
            || name != name.trim()
        {
            return Err(ShardingError::InvalidShardKeyName { name: name.to_owned() });
        }
        Ok(Self { name: name.to_owned() })
    }

    /// Returns the field's name.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.name
    }
}

/// What a collection derives its shard key from (CONTEXT: Shard Key).
///
/// The default is [`ShardKeyKind::Identity`]: the document identity,
/// lowercased. A collection may instead name document fields through a typed
/// declaration; changing a collection's shard key is a migration-scale
/// decision, never a silent reroute.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardKeyKind {
    /// The document identity, lowercased: the default shard key.
    Identity,
    /// The named document fields, in declaration order. Each field's value
    /// must render as text, a signed integer, or a boolean; anything else
    /// fails closed at resolution.
    Fields(Vec<ShardKeyField>),
}

/// The identity-keyed default, shared by every undeclared collection.
static IDENTITY_DEFAULT: ShardKeyKind = ShardKeyKind::Identity;

impl ShardKeyKind {
    /// Returns the identity-keyed default.
    #[must_use]
    pub const fn identity() -> Self {
        Self::Identity
    }

    /// Builds a field shard key from field names, in order.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::InvalidShardKeyName`] for an invalid field
    /// name, and refuses duplicate names: a key names each attribute once.
    pub fn try_fields(names: &[&str]) -> Result<Self, ShardingError> {
        let mut fields = Vec::with_capacity(names.len());
        for name in names {
            let field = ShardKeyField::try_new(name)?;
            if fields.contains(&field) {
                return Err(ShardingError::InvalidShardKeyName { name: (*name).to_owned() });
            }
            fields.push(field);
        }
        Ok(Self::Fields(fields))
    }

    /// Reports whether this kind is the identity-keyed default.
    #[must_use]
    pub const fn is_identity(&self) -> bool {
        matches!(self, Self::Identity)
    }

    /// Returns the named fields of a field shard key (empty for the
    /// identity default).
    #[must_use]
    pub fn fields(&self) -> &[ShardKeyField] {
        match self {
            Self::Identity => &[],
            Self::Fields(fields) => fields,
        }
    }
}

/// One collection's declared shard key.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CollectionShardKey {
    collection: CollectionName,
    declaration: ShardKeyKind,
}

impl CollectionShardKey {
    /// Declares one collection's shard key.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::InvalidShardKeyName`] when the collection
    /// name is not a valid logical identifier, or when a named field is
    /// invalid or duplicated.
    pub fn try_new(collection: &str, declaration: ShardKeyKind) -> Result<Self, ShardingError> {
        let collection = CollectionName::try_new(collection)
            .map_err(|_| ShardingError::InvalidShardKeyName { name: collection.to_owned() })?;
        Ok(Self { collection, declaration })
    }

    /// Returns the declared collection.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the declared key kind.
    #[must_use]
    pub fn declaration(&self) -> &ShardKeyKind {
        &self.declaration
    }
}

/// The engine-side set of per-collection shard-key declarations.
///
/// Collections without a declaration route identity-keyed. The set is a
/// value: re-declaring a collection's key replaces the declaration through
/// a new set, and the caller that changes one owns the migration-scale
/// consequences (no silent reroute).
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ShardKeySet {
    declarations: Vec<CollectionShardKey>,
}

impl ShardKeySet {
    /// Builds a validated set of declarations.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::DuplicateCollectionShardKey`] when one
    /// collection is declared twice.
    pub fn try_new(declarations: &[CollectionShardKey]) -> Result<Self, ShardingError> {
        let mut seen: Vec<&CollectionName> = Vec::with_capacity(declarations.len());
        for declaration in declarations {
            if seen.contains(&declaration.collection()) {
                return Err(ShardingError::DuplicateCollectionShardKey {
                    collection: declaration.collection().clone(),
                });
            }
            seen.push(declaration.collection());
        }
        Ok(Self { declarations: declarations.to_vec() })
    }

    /// Returns the empty set: every collection routes identity-keyed.
    #[must_use]
    pub fn identity() -> Self {
        Self::default()
    }

    /// Returns the shard-key kind a collection routes by (the identity
    /// default when undeclared).
    #[must_use]
    pub fn declaration_for(&self, collection: &CollectionName) -> &ShardKeyKind {
        self.declarations
            .iter()
            .find(|entry| entry.collection() == collection)
            .map_or(&IDENTITY_DEFAULT, |entry| entry.declaration())
    }

    /// Resolves one document's shard key under this set.
    ///
    /// Identity-keyed collections never need the value. Field-keyed
    /// collections require it, fail closed on a missing field, and refuse
    /// field values that cannot render.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::ShardKeyValueRequired`] when a field-keyed
    /// collection is resolved without a value,
    /// [`ShardingError::ShardKeyFieldMissing`] for an absent field, and
    /// [`ShardingError::ShardKeyFieldUnsupported`] for a value that is not
    /// text, a signed integer, or a boolean.
    pub fn resolve(
        &self,
        collection: &CollectionName,
        document: &DocumentId,
        value: Option<&DocumentValue>,
    ) -> Result<ShardKey, ShardingError> {
        match self.declaration_for(collection) {
            ShardKeyKind::Identity => Ok(ShardKey::identity(document)),
            ShardKeyKind::Fields(fields) => {
                let object = value.ok_or(ShardingError::ShardKeyValueRequired {
                    collection: collection.clone(),
                })?;
                let fields_of = match object {
                    DocumentValue::Object(fields) => Some(fields),
                    _ => None,
                };
                let mut rendered = Vec::with_capacity(fields.len());
                for field in fields {
                    let found = fields_of
                        .and_then(|fields| fields.get(field.as_str()))
                        .ok_or_else(|| ShardingError::ShardKeyFieldMissing {
                            collection: collection.clone(),
                            field: field.as_str().to_owned(),
                        })?;
                    rendered.push(render_field(field, found, collection)?);
                }
                Ok(ShardKey::composite(&rendered))
            }
        }
    }
}

/// Renders one shard-key field value with its type tag, so text `"5"` and
/// integer `5` never collide.
fn render_field(
    field: &ShardKeyField,
    value: &DocumentValue,
    collection: &CollectionName,
) -> Result<String, ShardingError> {
    match value {
        DocumentValue::String(text) => Ok(format!("s:{text}")),
        DocumentValue::Integer(number) => Ok(format!("i:{number}")),
        DocumentValue::Boolean(flag) => Ok(format!("b:{flag}")),
        _ => Err(ShardingError::ShardKeyFieldUnsupported {
            collection: collection.clone(),
            field: field.as_str().to_owned(),
        }),
    }
}
