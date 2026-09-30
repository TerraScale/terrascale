//! Lossless values and identities for the native TerraBase JSON protocol.
//!
//! Missing means a field is absent from [`Fields`]. It is distinct from
//! [`Value::Null`]. Integers use decimal strings and bytes use unpadded
//! base64url on the wire; no floating point or JSON coercion is involved.

use std::{fmt, str::FromStr};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use serde::{de, ser::SerializeMap, Deserialize, Deserializer, Serialize, Serializer};

use crate::error::{Error, ErrorKind, Result};

pub const MAX_FIELDS: usize = 1024;
pub const MAX_VALUE_NODES: usize = 4096;
pub const MAX_VALUE_DEPTH: usize = 16;
pub const MAX_VALUE_BYTES: usize = 1024 * 1024;

pub(crate) fn invalid(message: impl Into<String>) -> Error {
    Error::new(ErrorKind::Validation, "invalid_request", message)
}

macro_rules! uuid_id {
    ($($name:ident),+ $(,)?) => {$(
        #[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
        pub struct $name(uuid::Uuid);

        impl $name {
            /// Generate an entity identity using the producer's UUIDv7 convention.
            #[must_use]
            pub fn new() -> Self { Self(uuid::Uuid::now_v7()) }
            #[must_use]
            pub const fn bytes(&self) -> &[u8; 16] { self.0.as_bytes() }
        }
        impl Default for $name { fn default() -> Self { Self::new() } }
        impl fmt::Display for $name {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { self.0.fmt(f) }
        }
        impl FromStr for $name {
            type Err = Error;
            fn from_str(raw: &str) -> Result<Self> {
                let value = uuid::Uuid::parse_str(raw).map_err(|_| invalid("Expected a canonical UUID."))?;
                if value.to_string() != raw {
                    return Err(invalid("UUIDs must use lowercase canonical hyphenated form."));
                }
                Ok(Self(value))
            }
        }
        impl Serialize for $name {
            fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
                s.serialize_str(&self.to_string())
            }
        }
        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
                String::deserialize(d)?.parse().map_err(de::Error::custom)
            }
        }
    )+};
}

uuid_id!(TenantId, DatabaseId, CollectionId, DocumentId, ReplicaId);

/// A full-range unsigned integer serialized as canonical decimal text.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct DecimalU64(pub u64);

impl fmt::Display for DecimalU64 {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        self.0.fmt(f)
    }
}

impl FromStr for DecimalU64 {
    type Err = Error;
    fn from_str(raw: &str) -> Result<Self> {
        let value: u64 = raw
            .parse()
            .map_err(|_| invalid("Expected canonical unsigned decimal text."))?;
        if value.to_string() != raw {
            return Err(invalid("Unsigned decimal text is not canonical."));
        }
        Ok(Self(value))
    }
}

impl Serialize for DecimalU64 {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        s.serialize_str(&self.0.to_string())
    }
}

impl<'de> Deserialize<'de> for DecimalU64 {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        String::deserialize(d)?.parse().map_err(de::Error::custom)
    }
}

/// Opaque fixed-length digest, nonce, incarnation or state token.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Hex<const N: usize>([u8; N]);

impl<const N: usize> Hex<N> {
    #[must_use]
    pub const fn from_bytes(bytes: [u8; N]) -> Self {
        Self(bytes)
    }
    #[must_use]
    pub const fn bytes(&self) -> &[u8; N] {
        &self.0
    }
}

impl<const N: usize> fmt::Display for Hex<N> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for byte in self.0 {
            write!(f, "{byte:02x}")?;
        }
        Ok(())
    }
}

impl<const N: usize> FromStr for Hex<N> {
    type Err = Error;
    fn from_str(raw: &str) -> Result<Self> {
        if raw.len() != N * 2
            || !raw
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        {
            return Err(invalid(format!(
                "Expected exactly {} lowercase hexadecimal characters.",
                N * 2
            )));
        }
        let mut bytes = [0; N];
        for (i, byte) in bytes.iter_mut().enumerate() {
            *byte = u8::from_str_radix(&raw[i * 2..i * 2 + 2], 16)
                .map_err(|_| invalid("Invalid hexadecimal bytes."))?;
        }
        Ok(Self(bytes))
    }
}

impl<const N: usize> Serialize for Hex<N> {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        s.serialize_str(&self.to_string())
    }
}

impl<'de, const N: usize> Deserialize<'de> for Hex<N> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        String::deserialize(d)?.parse().map_err(de::Error::custom)
    }
}

pub type Digest = Hex<32>;
pub type Incarnation = Hex<16>;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Field {
    pub id: u32,
    pub value: Value,
}

/// Canonical numeric field mapping. No duplicate, zero or unsorted IDs exist.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Fields(Vec<Field>);

impl Fields {
    pub fn new(fields: Vec<Field>) -> Result<Self> {
        let result = Self(fields);
        validate_fields(&result, 1, false, &mut 0)?;
        Ok(result)
    }
    pub fn iter(&self) -> impl Iterator<Item = &Field> {
        self.0.iter()
    }
    /// `None` is missing; `Some(Value::Null)` is an explicit null.
    #[must_use]
    pub fn get(&self, id: u32) -> Option<&Value> {
        self.0
            .binary_search_by_key(&id, |field| field.id)
            .ok()
            .map(|i| &self.0[i].value)
    }
    #[must_use]
    pub fn len(&self) -> usize {
        self.0.len()
    }
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
    pub fn validate(&self) -> Result<()> {
        // tb-format document.rs: 94-byte header, 13-byte field directory
        // entries and trailing CRC32. Fields start at depth one.
        let map_bytes = validate_fields(self, 1, false, &mut 0)?;
        let document_bytes = 98usize
            .checked_add(self.len() * 4)
            .and_then(|bytes| bytes.checked_add(map_bytes - 2))
            .ok_or_else(|| invalid("Native document size overflow."))?;
        if document_bytes > MAX_VALUE_BYTES {
            return Err(invalid("Entire encoded native document exceeds 1 MiB."));
        }
        Ok(())
    }
}

impl Serialize for Fields {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        self.0.serialize(s)
    }
}

impl<'de> Deserialize<'de> for Fields {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        Self::new(Vec::<Field>::deserialize(d)?).map_err(de::Error::custom)
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Value {
    Null,
    Bool(bool),
    I64(i64),
    Utf8(String),
    Bytes(Vec<u8>),
    Map(Fields),
    Array(Vec<Value>),
}

impl Value {
    pub fn validate(&self) -> Result<()> {
        validate_value(self, 0, false, &mut 0).map(|_| ())
    }
}

impl Serialize for Value {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        let mut map = s.serialize_map(Some(if matches!(self, Self::Null) { 1 } else { 2 }))?;
        match self {
            Self::Null => map.serialize_entry("type", "null")?,
            Self::Bool(value) => {
                map.serialize_entry("type", "bool")?;
                map.serialize_entry("value", value)?;
            }
            Self::I64(value) => {
                map.serialize_entry("type", "i64")?;
                map.serialize_entry("value", &value.to_string())?;
            }
            Self::Utf8(value) => {
                map.serialize_entry("type", "utf8")?;
                map.serialize_entry("value", value)?;
            }
            Self::Bytes(value) => {
                map.serialize_entry("type", "bytes")?;
                map.serialize_entry("value", &URL_SAFE_NO_PAD.encode(value))?;
            }
            Self::Map(fields) => {
                map.serialize_entry("type", "map")?;
                map.serialize_entry("fields", fields)?;
            }
            Self::Array(items) => {
                map.serialize_entry("type", "array")?;
                map.serialize_entry("items", items)?;
            }
        }
        map.end()
    }
}

#[derive(Deserialize)]
#[serde(tag = "type", deny_unknown_fields)]
enum WireValue {
    #[serde(rename = "null")]
    Null {},
    #[serde(rename = "bool")]
    Bool { value: bool },
    #[serde(rename = "i64")]
    I64 { value: String },
    #[serde(rename = "utf8")]
    Utf8 { value: String },
    #[serde(rename = "bytes")]
    Bytes { value: String },
    #[serde(rename = "map")]
    Map { fields: Fields },
    #[serde(rename = "array")]
    Array { items: Vec<Value> },
}

impl<'de> Deserialize<'de> for Value {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        let result = match WireValue::deserialize(d)? {
            WireValue::Null {} => Self::Null,
            WireValue::Bool { value } => Self::Bool(value),
            WireValue::I64 { value } => {
                let parsed: i64 = value.parse().map_err(de::Error::custom)?;
                if parsed.to_string() != value {
                    return Err(de::Error::custom("Signed integer text is not canonical."));
                }
                Self::I64(parsed)
            }
            WireValue::Utf8 { value } => Self::Utf8(value),
            WireValue::Bytes { value } => {
                let bytes = URL_SAFE_NO_PAD.decode(&value).map_err(de::Error::custom)?;
                if URL_SAFE_NO_PAD.encode(&bytes) != value {
                    return Err(de::Error::custom(
                        "Byte text is not canonical unpadded base64url.",
                    ));
                }
                Self::Bytes(bytes)
            }
            WireValue::Map { fields } => Self::Map(fields),
            WireValue::Array { items } => Self::Array(items),
        };
        validate_value(&result, 0, false, &mut 0).map_err(de::Error::custom)?;
        Ok(result)
    }
}

fn validate_fields(
    fields: &Fields,
    depth: usize,
    inside_array: bool,
    nodes: &mut usize,
) -> Result<usize> {
    if fields.len() > MAX_FIELDS {
        return Err(invalid("Too many fields."));
    }
    let mut previous = 0;
    let mut bytes = 2usize;
    for field in fields.iter() {
        if field.id <= previous {
            return Err(invalid(
                "Field IDs must be positive and strictly increasing.",
            ));
        }
        previous = field.id;
        let value_bytes = validate_value(&field.value, depth, inside_array, nodes)?;
        bytes = bytes
            .checked_add(9)
            .and_then(|n| n.checked_add(value_bytes))
            .ok_or_else(|| invalid("Native map size overflow."))?;
    }
    Ok(bytes)
}

fn validate_value(
    value: &Value,
    depth: usize,
    inside_array: bool,
    nodes: &mut usize,
) -> Result<usize> {
    *nodes += 1;
    if depth > MAX_VALUE_DEPTH || *nodes > MAX_VALUE_NODES {
        return Err(invalid("Document value depth or node limit exceeded."));
    }
    let bytes = match value {
        Value::Null => 0,
        Value::Bool(_) => 1,
        Value::I64(_) => 8,
        Value::Utf8(value) => value.len(),
        Value::Bytes(value) => value.len(),
        Value::Map(fields) => validate_fields(fields, depth + 1, inside_array, nodes)?,
        Value::Array(items) => {
            if inside_array {
                return Err(invalid(
                    "Arrays under an ancestor array are unsupported by the native format.",
                ));
            }
            if items.len() > MAX_FIELDS {
                return Err(invalid("Too many array items."));
            }
            let mut bytes = 2usize;
            for item in items {
                let value_bytes = validate_value(item, depth + 1, true, nodes)?;
                bytes = bytes
                    .checked_add(5)
                    .and_then(|n| n.checked_add(value_bytes))
                    .ok_or_else(|| invalid("Native array size overflow."))?;
            }
            bytes
        }
    };
    if bytes > MAX_VALUE_BYTES {
        return Err(invalid("Encoded native value exceeds 1 MiB."));
    }
    Ok(bytes)
}
