//! Entity IDs and selectors.

use serde::{Deserialize, Deserializer};
use ulid::Ulid;

use super::errors::{Error, Result};

pub(crate) fn valid_entity_id(id: &str) -> bool {
    id.len() == 26
        && id.as_bytes()[0] <= b'7'
        && id
            .bytes()
            .all(|b| b"0123456789ABCDEFGHJKMNPQRSTVWXYZ".contains(&b))
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

pub(crate) fn optional_entity_id<'de, D: Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<String>, D::Error> {
    let id = Option::<String>::deserialize(d)?;
    if id.as_deref().is_some_and(|value| !valid_entity_id(value)) {
        return Err(serde::de::Error::custom(
            "entity ID must be a canonical uppercase ULID",
        ));
    }
    Ok(id)
}

/// A new ULID primary key in canonical uppercase form.
#[must_use]
pub fn new_id() -> String {
    Ulid::generate().to_string()
}

/// Reports whether `name` is a valid database name (`^[a-z][a-z0-9_-]{0,62}$`).
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
