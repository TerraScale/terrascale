//! Names, document IDs, versions and idempotency keys (api-v1.md "Names and
//! identifiers", "Versions, ETags and offsets" and "Idempotency").

use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use sha2::{Digest as _, Sha256};
use uuid::Uuid;

use super::errors::{Error, Result};

/// Document IDs are lowercase hyphenated UUIDs; [`Uuid`]'s `Display` is that
/// canonical form.
pub type DocumentId = Uuid;

/// The deterministic document ID for a natural `key` in `collection`:
/// `uuidv5(uuidv5(NAMESPACE_URL, "terrabase:collection:" + collection), key)`.
#[must_use]
pub fn uuidv5_for(collection: &str, key: &str) -> Uuid {
    let namespace = Uuid::new_v5(
        &Uuid::NAMESPACE_URL,
        format!("terrabase:collection:{collection}").as_bytes(),
    );
    Uuid::new_v5(&namespace, key.as_bytes())
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

/// An `Idempotency-Key`: 1–64 characters of `[A-Za-z0-9_.-]`.
///
/// An exact retry with the same key within 24 hours returns the original
/// response (including generated IDs and offsets). Keep the key and reuse it
/// when retrying the same mutation; the same key with a different body fails
/// with [`Error::IdempotencyConflict`].
#[derive(Clone, PartialEq, Eq, Hash)]
pub struct IdempotencyKey(String);

impl IdempotencyKey {
    /// Validates a caller-chosen key.
    pub fn new(value: impl Into<String>) -> Result<Self> {
        let value = value.into();
        let valid = !value.is_empty()
            && value.len() <= 64
            && value
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'.' | b'-'));
        if valid {
            Ok(Self(value))
        } else {
            Err(Error::InvalidInput(format!(
                "Idempotency-Key {value:?} must be 1-64 characters of [A-Za-z0-9_.-]"
            )))
        }
    }

    /// A fresh random key (32 hex characters).
    #[must_use]
    pub fn random() -> Self {
        Self(Uuid::new_v4().simple().to_string())
    }

    /// A deterministic key derived from `parts` (SHA-256, 64 hex characters).
    /// Equal parts always give the same key, so a crashed caller that
    /// recomputes the same mutation replays the original outcome.
    #[must_use]
    pub fn derive(parts: &[&str]) -> Self {
        let mut hasher = Sha256::new();
        for part in parts {
            hasher.update((part.len() as u64).to_be_bytes());
            hasher.update(part.as_bytes());
        }
        Self(hex(&hasher.finalize()))
    }

    /// The key text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        let _ = write!(out, "{b:02x}");
    }
    out
}

impl fmt::Debug for IdempotencyKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(&self.0, f)
    }
}

impl fmt::Display for IdempotencyKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl FromStr for IdempotencyKey {
    type Err = Error;
    fn from_str(s: &str) -> Result<Self> {
        Self::new(s)
    }
}
