//! JSON document body encoding used by the SQLite storage backend.
//!
//! Serde owns JSON syntax, while this module keeps TerraBase's value model and
//! the non-finite floating point extension at the storage boundary.

use std::collections::BTreeMap;
use std::io::{self, Write};

use serde::de::{self, DeserializeSeed, MapAccess, SeqAccess, Visitor};
use serde::ser::{Serialize, SerializeMap, Serializer};
use serde_json::Deserializer;
use serde_json::ser::{CharEscape, CompactFormatter, Formatter};

use crate::types::{CollectionName, Document, DocumentId, DocumentValue};

use super::codec;

const NONFINITE_FIELD: &str = "__terrascale_nonfinite__";

/// Retains the persisted spelling of floats and control-character escapes.
struct StorageFormatter;

impl Formatter for StorageFormatter {
    fn write_f64<W: ?Sized + Write>(&mut self, writer: &mut W, value: f64) -> io::Result<()> {
        let text = value.to_string();
        writer.write_all(text.as_bytes())?;
        if !text.contains(['.', 'e', 'E']) {
            writer.write_all(b".0")?;
        }
        Ok(())
    }

    fn write_char_escape<W: ?Sized + Write>(
        &mut self,
        writer: &mut W,
        escape: CharEscape,
    ) -> io::Result<()> {
        let escape = match escape {
            CharEscape::Backspace => CharEscape::AsciiControl(8),
            CharEscape::FormFeed => CharEscape::AsciiControl(12),
            CharEscape::LineFeed => CharEscape::AsciiControl(10),
            CharEscape::CarriageReturn => CharEscape::AsciiControl(13),
            CharEscape::Tab => CharEscape::AsciiControl(9),
            other => other,
        };
        CompactFormatter.write_char_escape(writer, escape)
    }
}

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
            DocumentValue::Array(values) => serializer.collect_seq(values.iter().map(Value)),
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
        if let Ok(value) = i64::try_from(value) {
            return Ok(DocumentValue::Integer(value));
        }
        // The legacy parser treated an unsigned integer outside i64 as a
        // floating point number after its integer parse failed.
        #[allow(clippy::cast_precision_loss)]
        let value = value as f64;
        if value.is_finite() {
            Ok(DocumentValue::Number(value))
        } else {
            Err(E::custom("number exceeds f64"))
        }
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
        let mut values = Vec::new();
        while let Some(value) = access.next_element_seed(ValueSeed { depth: self.depth + 1 })? {
            values.push(value);
        }
        Ok(DocumentValue::Array(values))
    }

    fn visit_map<A: MapAccess<'de>>(self, mut access: A) -> Result<Self::Value, A::Error> {
        let mut fields = BTreeMap::new();
        while let Some(name) = access.next_key::<String>()? {
            let value = access.next_value_seed(ValueSeed { depth: self.depth + 1 })?;
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

    fn deserialize<D: de::Deserializer<'de>>(
        self,
        deserializer: D,
    ) -> Result<Self::Value, D::Error> {
        if self.depth > codec::MAX_VALUE_DEPTH {
            return Err(de::Error::custom("JSON value nesting exceeds limit"));
        }
        deserializer.deserialize_any(ValueVisitor { depth: self.depth })
    }
}

/// Encodes one document as the deterministic JSON storage envelope.
pub(crate) fn encode_document_body(document: &Document) -> Vec<u8> {
    let mut bytes = Vec::new();
    let mut serializer = serde_json::Serializer::with_formatter(&mut bytes, StorageFormatter);
    DocumentBody(document)
        .serialize(&mut serializer)
        .expect("document JSON serialization cannot fail");
    bytes
}

/// Decodes one JSON body, rejecting malformed input, trailing bytes, and an
/// invalid envelope.
pub(crate) fn decode_document_body(bytes: &[u8]) -> Option<Document> {
    let mut deserializer = Deserializer::from_slice(bytes);
    let DocumentValue::Object(mut fields) =
        ValueSeed { depth: 0 }.deserialize(&mut deserializer).ok()?
    else {
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

#[cfg(test)]
mod tests;
