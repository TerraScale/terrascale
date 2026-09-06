//! JSON document body encoding used by the SQLite storage backend.
//!
//! Serde owns JSON syntax, while this module keeps TerraBase's value model and
//! the non-finite floating point extension at the storage boundary.

use std::collections::BTreeMap;

use serde::de::{self, DeserializeSeed, MapAccess, SeqAccess, Visitor};
use serde::ser::{Serialize, SerializeMap, Serializer};
use serde_json::Deserializer;

use crate::types::{CollectionName, Document, DocumentId, DocumentValue};

use super::codec;

const NONFINITE_FIELD: &str = "__terrascale_nonfinite__";

struct Value<'a>(&'a DocumentValue);

impl Serialize for Value<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        match self.0 {
            DocumentValue::Null => serializer.serialize_unit(),
            DocumentValue::Boolean(value) => serializer.serialize_bool(*value),
            DocumentValue::Integer(value) => serializer.serialize_i64(*value),
            DocumentValue::Number(value) if value.is_finite() => serializer.serialize_f64(*value),
            DocumentValue::Number(value) => {
                let mut map = serializer.serialize_map(Some(1))?;
                map.serialize_entry(NONFINITE_FIELD, &format!("{:016x}", value.to_bits()))?;
                map.end()
            }
            DocumentValue::String(value) => serializer.serialize_str(value),
            DocumentValue::Array(values) => {
                serializer.collect_seq(values.iter().map(Value))
            }
            DocumentValue::Object(values) => {
                let mut map = serializer.serialize_map(Some(values.len()))?;
                for (key, value) in values {
                    map.serialize_entry(key, &Value(value))?;
                }
                map.end()
            }
        }
    }
}

struct DocumentBody<'a>(&'a Document);

impl Serialize for DocumentBody<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut map = serializer.serialize_map(Some(4))?;
        map.serialize_entry("$collection", self.0.collection().as_str())?;
        map.serialize_entry("$id", self.0.id().as_str())?;
        map.serialize_entry("$value", &Value(self.0.value()))?;
        map.serialize_entry("$version", &self.0.metadata().version())?;
        map.end()
    }
}

struct ValueVisitor {
    depth: usize,
}

impl<'de> Visitor<'de> for ValueVisitor {
    type Value = DocumentValue;

    fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("a TerraBase JSON value")
    }

    fn visit_unit<E>(self) -> Result<Self::Value, E> {
        Ok(DocumentValue::Null)
    }

    fn visit_bool<E>(self, value: bool) -> Result<Self::Value, E> {
        Ok(DocumentValue::Boolean(value))
    }

    fn visit_i64<E>(self, value: i64) -> Result<Self::Value, E> {
        Ok(DocumentValue::Integer(value))
    }

    fn visit_u64<E: de::Error>(self, value: u64) -> Result<Self::Value, E> {
        i64::try_from(value)
            .map(DocumentValue::Integer)
            .map_err(|_| E::custom("integer exceeds i64"))
    }

    fn visit_f64<E: de::Error>(self, value: f64) -> Result<Self::Value, E> {
        if value.is_finite() {
            Ok(DocumentValue::Number(value))
        } else {
            Err(E::custom("non-finite JSON number"))
        }
    }

    fn visit_str<E>(self, value: &str) -> Result<Self::Value, E> {
        Ok(DocumentValue::String(value.to_owned()))
    }

    fn visit_string<E>(self, value: String) -> Result<Self::Value, E> {
        Ok(DocumentValue::String(value))
    }

    fn visit_seq<A: SeqAccess<'de>>(self, mut access: A) -> Result<Self::Value, A::Error> {
        if self.depth > codec::MAX_VALUE_DEPTH {
            return Err(de::Error::custom("JSON value nesting exceeds limit"));
        }
        let mut values = Vec::new();
        while let Some(value) = access.next_element_seed(ValueSeed { depth: self.depth + 1 })? {
            values.push(value);
        }
        Ok(DocumentValue::Array(values))
    }

    fn visit_map<A: MapAccess<'de>>(self, mut access: A) -> Result<Self::Value, A::Error> {
        if self.depth > codec::MAX_VALUE_DEPTH {
            return Err(de::Error::custom("JSON value nesting exceeds limit"));
        }
        let mut fields = BTreeMap::new();
        while let Some((name, value)) = access.next_entry_seed(KeySeed, ValueSeed { depth: self.depth + 1 })? {
            fields.insert(name, value);
        }
        if fields.len() == 1 {
            if let Some(DocumentValue::String(bits)) = fields.get(NONFINITE_FIELD)
                && bits.len() == 16
                && bits.bytes().all(|byte| byte.is_ascii_hexdigit())
                && let Ok(bits) = u64::from_str_radix(bits, 16)
            {
                let value = f64::from_bits(bits);
                if !value.is_finite() {
                    return Ok(DocumentValue::Number(value));
                }
            }
        }
        Ok(DocumentValue::Object(fields))
    }
}

struct ValueSeed {
    depth: usize,
}

impl<'de> de::DeserializeSeed<'de> for ValueSeed {
    type Value = DocumentValue;

    fn deserialize<D: de::Deserializer<'de>>(self, deserializer: D) -> Result<Self::Value, D::Error> {
        if self.depth > codec::MAX_VALUE_DEPTH {
            return Err(de::Error::custom("JSON value nesting exceeds limit"));
        }
        deserializer.deserialize_any(ValueVisitor { depth: self.depth })
    }
}

struct KeySeed;

impl<'de> de::DeserializeSeed<'de> for KeySeed {
    type Value = String;

    fn deserialize<D: de::Deserializer<'de>>(self, deserializer: D) -> Result<Self::Value, D::Error> {
        deserializer.deserialize_string(StringVisitor)
    }
}

struct StringVisitor;

impl<'de> Visitor<'de> for StringVisitor {
    type Value = String;

    fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("a JSON string")
    }

    fn visit_str<E>(self, value: &str) -> Result<Self::Value, E> {
        Ok(value.to_owned())
    }

    fn visit_string<E>(self, value: String) -> Result<Self::Value, E> {
        Ok(value)
    }
}

/// Encodes one document as the deterministic JSON storage envelope.
pub(crate) fn encode_document_body(document: &Document) -> Vec<u8> {
    serde_json::to_vec(&DocumentBody(document)).expect("document JSON serialization cannot fail")
}

/// Decodes one JSON body, rejecting malformed input, trailing bytes, and an
/// invalid envelope.
pub(crate) fn decode_document_body(bytes: &[u8]) -> Option<Document> {
    let mut deserializer = Deserializer::from_slice(bytes);
    let DocumentValue::Object(mut fields) = ValueSeed { depth: 0 }.deserialize(&mut deserializer).ok()? else {
        return None;
    };
    deserializer.end().ok()?;
    let collection = match fields.remove("$collection")? {
        DocumentValue::String(value) => CollectionName::try_new(&value).ok()?,
        _ => return None,
    };
    let id = match fields.remove("$id")? {
        DocumentValue::String(value) => DocumentId::try_new(&value).ok()?,
        _ => return None,
    };
    let version = match fields.remove("$version")? {
        DocumentValue::Integer(value) if value >= 0 => u64::try_from(value).ok()?,
        _ => return None,
    };
    let value = fields.remove("$value")?;
    if !fields.is_empty() {
        return None;
    }
    let mut document = Document::new(id, collection, value);
    document.set_version(version);
    Some(document)
}
