//! Wire scalars shared by the data plane and the control plane: exact JSON
//! numbers, `u64` decimal strings and durability levels.

use std::fmt;
use std::str::FromStr;

use serde::de::Error as _;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::value::RawValue;

use super::errors::{Error, Result};

/// An exact JSON number kept as its decimal text.
///
/// Document numbers are exact decimals of up to 128 significant digits, so
/// they never pass through `f64`. `Number` serializes as a bare JSON number
/// (not a string) and deserializes any JSON number without rounding. It works
/// with `serde_json` only, and must not be used inside `#[serde(flatten)]` or
/// untagged enums (those buffer values and lose the raw text). Do not put it in
/// a `serde_json::Value` (`json!`, `to_value`) either: without
/// `arbitrary_precision`, `Value` rounds it through `f64`. Serialize it from a
/// struct or map instead.
#[derive(Clone, PartialEq, Eq, Hash)]
pub struct Number(String);

impl Number {
    /// Validates `text` against the JSON number grammar.
    pub fn new(text: impl Into<String>) -> Result<Self> {
        let text = text.into();
        if is_json_number(&text) {
            Ok(Self(text))
        } else {
            Err(Error::InvalidInput(format!(
                "{text:?} is not a JSON number"
            )))
        }
    }

    /// The exact decimal text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }

    /// The value as `i64` when the text is a plain integer in range.
    #[must_use]
    pub fn as_i64(&self) -> Option<i64> {
        self.0.parse().ok()
    }

    /// The value as `u64` when the text is a plain non-negative integer in range.
    #[must_use]
    pub fn as_u64(&self) -> Option<u64> {
        self.0.parse().ok()
    }

    /// The nearest `f64` (lossy).
    #[must_use]
    pub fn to_f64_lossy(&self) -> f64 {
        self.0.parse().unwrap_or(f64::NAN)
    }
}

fn is_json_number(s: &str) -> bool {
    let b = s.as_bytes();
    let mut i = 0;
    if b.get(i) == Some(&b'-') {
        i += 1;
    }
    match b.get(i) {
        Some(b'0') => i += 1,
        Some(b'1'..=b'9') => {
            while matches!(b.get(i), Some(b'0'..=b'9')) {
                i += 1;
            }
        }
        _ => return false,
    }
    if b.get(i) == Some(&b'.') {
        i += 1;
        let start = i;
        while matches!(b.get(i), Some(b'0'..=b'9')) {
            i += 1;
        }
        if i == start {
            return false;
        }
    }
    if matches!(b.get(i), Some(b'e' | b'E')) {
        i += 1;
        if matches!(b.get(i), Some(b'+' | b'-')) {
            i += 1;
        }
        let start = i;
        while matches!(b.get(i), Some(b'0'..=b'9')) {
            i += 1;
        }
        if i == start {
            return false;
        }
    }
    i == b.len()
}

macro_rules! number_from_int {
    ($($t:ty),*) => {$(
        impl From<$t> for Number {
            fn from(v: $t) -> Self {
                Self(v.to_string())
            }
        }
    )*};
}
number_from_int!(i8, i16, i32, i64, i128, isize, u8, u16, u32, u64, u128, usize);

impl FromStr for Number {
    type Err = Error;
    fn from_str(s: &str) -> Result<Self> {
        Self::new(s)
    }
}

impl fmt::Debug for Number {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl fmt::Display for Number {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl Serialize for Number {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        let raw = RawValue::from_string(self.0.clone()).map_err(serde::ser::Error::custom)?;
        raw.serialize(s)
    }
}

impl<'de> Deserialize<'de> for Number {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        let raw = Box::<RawValue>::deserialize(d)?;
        let text = raw.get().trim();
        if is_json_number(text) {
            Ok(Self(text.to_owned()))
        } else {
            Err(D::Error::custom(format!(
                "expected a JSON number, got {text}"
            )))
        }
    }
}

/// Serde adapter for `u64` quantities carried as canonical decimal strings
/// (offsets, sequences, timestamps, byte and document counts).
pub mod u64_string {
    use super::*;

    pub fn serialize<S: Serializer>(v: &u64, s: S) -> std::result::Result<S::Ok, S::Error> {
        s.serialize_str(&v.to_string())
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<u64, D::Error> {
        let s = String::deserialize(d)?;
        parse_canonical_u64(&s)
            .ok_or_else(|| D::Error::custom(format!("u64 {s:?} is not a canonical decimal string")))
    }

    pub(crate) fn parse_canonical_u64(s: &str) -> Option<u64> {
        if s.is_empty()
            || (s.len() > 1 && s.starts_with('0'))
            || !s.bytes().all(|b| b.is_ascii_digit())
        {
            return None;
        }
        s.parse().ok()
    }
}

/// Serde adapter for `Option<u64>` decimal strings (`null` or absent is `None`).
pub mod opt_u64_string {
    use super::*;

    pub fn serialize<S: Serializer>(v: &Option<u64>, s: S) -> std::result::Result<S::Ok, S::Error> {
        match v {
            Some(v) => s.serialize_str(&v.to_string()),
            None => s.serialize_none(),
        }
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(
        d: D,
    ) -> std::result::Result<Option<u64>, D::Error> {
        match Option::<String>::deserialize(d)? {
            None => Ok(None),
            Some(s) => u64_string::parse_canonical_u64(&s)
                .map(Some)
                .ok_or_else(|| {
                    D::Error::custom(format!("u64 {s:?} is not a canonical decimal string"))
                }),
        }
    }
}

/// The acknowledgment level of a committed write.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Durability {
    LocalDurable,
    ObjectDurable,
    /// A level this client does not know yet.
    #[serde(untagged)]
    Other(String),
}
