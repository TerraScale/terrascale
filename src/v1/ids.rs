//! Entity IDs, raw document keys, selectors and version tokens.

use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use sha2::{Digest, Sha256};
use ulid::Ulid;

use super::errors::{Error, Result};

/// A document ID: a ULID, sent and received in canonical uppercase form.
pub type DocumentId = Ulid;

/// Parses a canonical uppercase ULID; `Ulid::from_string` alone also accepts lowercase.
pub(crate) fn parse_document_id(id: &str) -> Option<DocumentId> {
    valid_entity_id(id)
        .then(|| Ulid::from_string(id).ok())
        .flatten()
}

pub(crate) fn valid_entity_id(id: &str) -> bool {
    id.len() == 26
        && id.as_bytes()[0] <= b'7'
        && id
            .bytes()
            .all(|b| b"0123456789ABCDEFGHJKMNPQRSTVWXYZ".contains(&b))
}

pub(crate) fn document_id<'de, D: Deserializer<'de>>(
    d: D,
) -> std::result::Result<DocumentId, D::Error> {
    let id = String::deserialize(d)?;
    parse_document_id(&id)
        .ok_or_else(|| serde::de::Error::custom("document ID must be a canonical uppercase ULID"))
}

pub(crate) fn entity_id<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<String, D::Error> {
    let id = String::deserialize(d)?;
    if valid_entity_id(&id) {
        Ok(id)
    } else {
        Err(serde::de::Error::custom(
            "entity ID must be a canonical uppercase ULID",
        ))
    }
}

pub(crate) fn cursor<'de, D: Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<String>, D::Error> {
    let cursor = Option::<String>::deserialize(d)?;
    if cursor.as_deref() == Some("") {
        return Err(serde::de::Error::custom(
            "pagination cursor must be nonempty or null",
        ));
    }
    Ok(cursor)
}

/// The deterministic document ID for a natural `key` in `collection`: the
/// first 16 bytes of `SHA-256(collection + "\0" + key)` as a ULID.
#[must_use]
pub fn ulid_for(collection: &str, key: &str) -> DocumentId {
    let digest = Sha256::new()
        .chain_update(collection)
        .chain_update([0])
        .chain_update(key)
        .finalize();
    let mut bytes = [0; 16];
    bytes.copy_from_slice(&digest[..16]);
    Ulid::from_bytes(bytes)
}

/// Reports whether `name` is a valid database or collection name
/// (`^[a-z][a-z0-9_-]{0,62}$`).
#[must_use]
pub fn is_valid_name(name: &str) -> bool {
    let bytes = name.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 63
        && bytes[0].is_ascii_lowercase()
        && bytes[1..]
            .iter()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || *b == b'_' || *b == b'-')
}

pub(crate) fn check_name(kind: &str, name: &str) -> Result<()> {
    if is_valid_name(name) {
        Ok(())
    } else {
        Err(Error::InvalidInput(format!(
            "{kind} name {name:?} must match ^[a-z][a-z0-9_-]{{0,62}}$"
        )))
    }
}

pub(crate) fn check_database_selector(selector: &str) -> Result<()> {
    if valid_entity_id(selector) {
        Ok(())
    } else {
        check_name("database", selector)
    }
}

/// A document version: `tbv1-` followed by 32 lowercase hex characters. It is
/// the strong ETag of point reads and the value of `If-Match`.
#[derive(Clone, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct Version(String);

impl Version {
    /// Parses and validates a version.
    pub fn new(value: impl Into<String>) -> Result<Self> {
        let value = value.into();
        let valid = value.len() == 37
            && value.starts_with("tbv1-")
            && value.as_bytes()[5..]
                .iter()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(b));
        if valid {
            Ok(Self(value))
        } else {
            Err(Error::InvalidInput(format!(
                "version {value:?} must be tbv1- followed by 32 lowercase hex characters"
            )))
        }
    }

    /// The version text without quotes.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }

    /// Parses an `ETag` header value (`"tbv1-…"`, optionally weak-prefixed).
    pub(crate) fn from_etag(header: &str) -> Option<Self> {
        let v = header.trim();
        let v = v.strip_prefix("W/").unwrap_or(v);
        let v = v
            .strip_prefix('"')
            .and_then(|v| v.strip_suffix('"'))
            .unwrap_or(v);
        Self::new(v).ok()
    }

    pub(crate) fn quoted(&self) -> String {
        format!("\"{}\"", self.0)
    }
}

impl fmt::Debug for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(&self.0, f)
    }
}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl FromStr for Version {
    type Err = Error;
    fn from_str(s: &str) -> Result<Self> {
        Self::new(s)
    }
}

impl Serialize for Version {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        s.serialize_str(&self.0)
    }
}

impl<'de> Deserialize<'de> for Version {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        let s = String::deserialize(d)?;
        Self::new(s).map_err(serde::de::Error::custom)
    }
}
