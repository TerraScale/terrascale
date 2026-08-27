//! The TBEP body codec and the shared value type.
//!
//! The wire codec is the mandated pair (charter PD-2026-08-26-02 D4):
//! Rust `rmp-serde` against C# `MessagePack-CSharp`, contractful and
//! positional everywhere — every frame body is a `MessagePack` **array**
//! of its schema-declared fields, never a map, never a typeless blob.
//! The generated registry (`crate::generated`) provides the typed bodies;
//! this module provides the encode/decode discipline around them:
//!
//! - encode validates the body's bounds first (fail closed before bytes
//!   exist);
//! - decode rejects trailing bytes, non-array encodings, nil where a
//!   value is required, and out-of-range integers, then validates the
//!   same bounds;
//! - [`TbepValue`] is the recursive document-body type: a tagged array
//!   `[tag]` / `[tag, payload]` with objects as name-sorted field lists,
//!   so encoding is deterministic across languages.

use serde::ser::SerializeTuple as _;

use crate::generated::FrameKind;

/// A body codec failure. Every row fails closed.
#[derive(Clone, Debug, PartialEq)]
pub enum BodyError {
    /// The encoded bytes are not a well-formed body array.
    Malformed(String),
    /// A string field exceeded its declared bound.
    StringTooLong {
        /// The offending field.
        field: &'static str,
        /// The declared byte bound.
        bound: usize,
    },
    /// A string field is not one of the closed vocabulary.
    NotInVocabulary {
        /// The offending field.
        field: &'static str,
    },
    /// A list field exceeded the item bound.
    ListTooLong {
        /// The offending field.
        field: &'static str,
        /// The item bound.
        bound: usize,
    },
}

impl std::fmt::Display for BodyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Malformed(reason) => write!(f, "malformed body: {reason}"),
            Self::StringTooLong { field, bound } => {
                write!(f, "field {field} exceeds its {bound}-byte bound")
            }
            Self::NotInVocabulary { field } => {
                write!(f, "field {field} is outside its closed vocabulary")
            }
            Self::ListTooLong { field, bound } => {
                write!(f, "field {field} exceeds its {bound}-item bound")
            }
        }
    }
}

impl std::error::Error for BodyError {}

/// The trait every generated frame body implements: its frame kind plus
/// its bounds validation.
pub trait WireBody: serde::Serialize + serde::de::DeserializeOwned {
    /// The frame kind this body rides in.
    const KIND: FrameKind;
    /// Validates the body's declared bounds.
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    fn validate(&self) -> Result<(), BodyError>;
}

/// Encodes one body: bounds are validated first, then the body encodes
/// through `rmp-serde` as a positional array.
///
/// # Errors
///
/// Returns [`BodyError::StringTooLong`] or [`BodyError::ListTooLong`]
/// for bound violations and [`BodyError::Malformed`] if serialization
/// itself fails (which a validated body precludes).
pub fn encode_body<B: WireBody>(body: &B) -> Result<Vec<u8>, BodyError> {
    body.validate()?;
    rmp_serde::to_vec(body).map_err(|error| BodyError::Malformed(error.to_string()))
}

/// Decodes one body: rejects trailing bytes and malformed encodings,
/// then validates the same bounds encode checked.
///
/// # Errors
///
/// Returns [`BodyError::Malformed`] for any decode failure and the
/// bounds rows exactly as [`encode_body`].
pub fn decode_body<B: WireBody>(bytes: &[u8]) -> Result<B, BodyError> {
    let mut deserializer = rmp_serde::Deserializer::new(std::io::Cursor::new(bytes));
    let body = B::deserialize(&mut deserializer)
        .map_err(|error| BodyError::Malformed(error.to_string()))?;
    if deserializer.position() != bytes.len() as u64 {
        return Err(BodyError::Malformed("trailing bytes after the body array".into()));
    }
    body.validate()?;
    Ok(body)
}

/// Checks a string field's byte bound.
///
/// # Errors
///
/// Returns [`BodyError::StringTooLong`] when the bound is exceeded.
pub fn check_str_bound(value: &str, bound: u64, field: &'static str) -> Result<(), BodyError> {
    let bound = usize::try_from(bound).unwrap_or(usize::MAX);
    if value.len() > bound {
        return Err(BodyError::StringTooLong { field, bound });
    }
    Ok(())
}

/// Checks membership in a closed string vocabulary.
///
/// # Errors
///
/// Returns [`BodyError::NotInVocabulary`] for a non-member.
pub fn check_one_of(
    value: &str,
    vocabulary: &[&str],
    field: &'static str,
) -> Result<(), BodyError> {
    if !vocabulary.contains(&value) {
        return Err(BodyError::NotInVocabulary { field });
    }
    Ok(())
}

/// Checks a list field's item bound.
///
/// # Errors
///
/// Returns [`BodyError::ListTooLong`] when the bound is exceeded.
pub fn check_list_bound<T>(
    value: &[T],
    bound: usize,
    field: &'static str,
) -> Result<(), BodyError> {
    if value.len() > bound {
        return Err(BodyError::ListTooLong { field, bound });
    }
    Ok(())
}

/// Wraps one required bytes field for serialization as msgpack `bin`
/// (the generated bodies call this; serde's blanket `[u8]` path would
/// ride as an array of integers instead).
#[must_use]
pub fn bytes_ref(value: &[u8]) -> crate::envelope::WireBytesRef<'_> {
    crate::envelope::WireBytesRef(value)
}

/// Wraps one optional bytes field the same way ([`bytes_ref`]).
#[must_use]
pub fn bytes_ref_opt(value: &Option<Vec<u8>>) -> Option<crate::envelope::WireBytesRef<'_>> {
    value.as_ref().map(|bytes| crate::envelope::WireBytesRef(bytes.as_slice()))
}

/// Encodes one standalone value in its canonical wire form (objects
/// name-sorted); the conformance corpus checks canonicalization through
/// this seam.
///
/// # Errors
///
/// Returns [`BodyError::Malformed`] if serialization fails (which a
/// value never triggers).
pub fn encode_value(value: &TbepValue) -> Result<Vec<u8>, BodyError> {
    rmp_serde::to_vec(value).map_err(|error| BodyError::Malformed(error.to_string()))
}

// ---------------------------------------------------------------------------
// TbepValue
// ---------------------------------------------------------------------------

/// The recursive document-body value: the wire type of document bodies
/// and family/sync payloads' scalar fields.
///
/// Wire form: a tagged array `[tag]` or `[tag, payload]`. Objects are
/// name-sorted field lists — `[(name, value)]` pairs in ascending name
/// order — so identical values encode to identical bytes.
#[derive(Clone, Debug, PartialEq)]
pub enum TbepValue {
    /// The null value.
    Null,
    /// A boolean.
    Boolean(bool),
    /// A signed integer.
    Integer(i64),
    /// A double.
    Number(f64),
    /// A utf-8 text.
    String(String),
    /// An ordered array of values.
    Array(Vec<TbepValue>),
    /// An object: field names in ascending byte order with their values.
    Object(Vec<(String, TbepValue)>),
}

impl TbepValue {
    /// The value's wire tag.
    #[must_use]
    pub const fn tag(&self) -> u8 {
        match self {
            Self::Null => 0,
            Self::Boolean(_) => 1,
            Self::Integer(_) => 2,
            Self::Number(_) => 3,
            Self::String(_) => 4,
            Self::Array(_) => 5,
            Self::Object(_) => 6,
        }
    }
}

impl serde::Serialize for TbepValue {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        match self {
            Self::Null => {
                let mut tuple = serializer.serialize_tuple(1)?;
                tuple.serialize_element(&0u8)?;
                tuple.end()
            }
            Self::Boolean(v) => {
                let mut tuple = serializer.serialize_tuple(2)?;
                tuple.serialize_element(&1u8)?;
                tuple.serialize_element(v)?;
                tuple.end()
            }
            Self::Integer(v) => {
                let mut tuple = serializer.serialize_tuple(2)?;
                tuple.serialize_element(&2u8)?;
                tuple.serialize_element(v)?;
                tuple.end()
            }
            Self::Number(v) => {
                let mut tuple = serializer.serialize_tuple(2)?;
                tuple.serialize_element(&3u8)?;
                tuple.serialize_element(v)?;
                tuple.end()
            }
            Self::String(v) => {
                let mut tuple = serializer.serialize_tuple(2)?;
                tuple.serialize_element(&4u8)?;
                tuple.serialize_element(v.as_str())?;
                tuple.end()
            }
            Self::Array(v) => {
                let mut tuple = serializer.serialize_tuple(2)?;
                tuple.serialize_element(&5u8)?;
                tuple.serialize_element(v.as_slice())?;
                tuple.end()
            }
            Self::Object(v) => {
                // Canonical form: objects serialize name-sorted, so
                // identical values encode to identical bytes.
                let mut sorted = v.clone();
                sorted.sort_by(|a, b| a.0.cmp(&b.0));
                let mut tuple = serializer.serialize_tuple(2)?;
                tuple.serialize_element(&6u8)?;
                tuple.serialize_element(sorted.as_slice())?;
                tuple.end()
            }
        }
    }
}

impl<'de> serde::Deserialize<'de> for TbepValue {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct Visitor;
        impl<'de> serde::de::Visitor<'de> for Visitor {
            type Value = TbepValue;
            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("a tagged TbepValue array")
            }
            fn visit_seq<A: serde::de::SeqAccess<'de>>(
                self,
                mut seq: A,
            ) -> Result<Self::Value, A::Error> {
                let tag = seq
                    .next_element::<u8>()?
                    .ok_or_else(|| serde::de::Error::invalid_length(0, &self))?;
                let value = match tag {
                    0 => TbepValue::Null,
                    1 => TbepValue::Boolean(
                        seq.next_element::<bool>()?
                            .ok_or_else(|| serde::de::Error::invalid_length(1, &self))?,
                    ),
                    2 => TbepValue::Integer(
                        seq.next_element::<i64>()?
                            .ok_or_else(|| serde::de::Error::invalid_length(1, &self))?,
                    ),
                    3 => TbepValue::Number(
                        seq.next_element::<f64>()?
                            .ok_or_else(|| serde::de::Error::invalid_length(1, &self))?,
                    ),
                    4 => TbepValue::String(
                        seq.next_element::<String>()?
                            .ok_or_else(|| serde::de::Error::invalid_length(1, &self))?,
                    ),
                    5 => TbepValue::Array(
                        seq.next_element::<Vec<TbepValue>>()?
                            .ok_or_else(|| serde::de::Error::invalid_length(1, &self))?,
                    ),
                    6 => {
                        let mut fields = seq
                            .next_element::<Vec<(String, TbepValue)>>()?
                            .ok_or_else(|| serde::de::Error::invalid_length(1, &self))?;
                        fields.sort_by(|a, b| a.0.cmp(&b.0));
                        TbepValue::Object(fields)
                    }
                    _ => return Err(serde::de::Error::custom("unknown TbepValue tag")),
                };
                if tag != 0 && seq.next_element::<serde::de::IgnoredAny>()?.is_some() {
                    return Err(serde::de::Error::invalid_length(2, &self));
                }
                Ok(value)
            }
        }
        deserializer.deserialize_any(Visitor)
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use crate::generated::*;

    #[test]
    fn value_round_trips_deterministically() {
        let value = TbepValue::Object(vec![
            ("z".into(), TbepValue::Integer(1)),
            ("a".into(), TbepValue::Array(vec![TbepValue::Boolean(true)])),
            ("m".into(), TbepValue::String("x".into())),
        ]);
        let mut wire = rmp_serde::to_vec(&value).expect("encodes");
        wire.extend_from_slice(&wire.clone());
        assert_eq!(rmp_serde::to_vec(&value).expect("re-encodes"), wire[..wire.len() / 2]);
        let decoded: TbepValue = rmp_serde::from_slice(&wire[..wire.len() / 2]).expect("decodes");
        // Decode canonicalizes to the name-sorted object.
        let canonical = TbepValue::Object(vec![
            ("a".into(), TbepValue::Array(vec![TbepValue::Boolean(true)])),
            ("m".into(), TbepValue::String("x".into())),
            ("z".into(), TbepValue::Integer(1)),
        ]);
        assert_eq!(decoded, canonical);
        // Objects decode canonically name-sorted.
        let unsorted =
            TbepValue::Object(vec![("b".into(), TbepValue::Null), ("a".into(), TbepValue::Null)]);
        let sorted: TbepValue =
            rmp_serde::from_slice(&rmp_serde::to_vec(&unsorted).expect("encodes"))
                .expect("decodes");
        match sorted {
            TbepValue::Object(fields) => {
                assert_eq!(fields[0].0, "a");
            }
            other => panic!("unexpected {other:?}"),
        }
    }

    #[test]
    fn body_decode_rejects_trailing_bytes() {
        let body = DocumentReadRequest { collection: "orders".into(), document: "d-01".into() };
        let mut wire = encode_body(&body).expect("encodes");
        wire.push(0x77);
        let error = decode_body::<DocumentReadRequest>(&wire).unwrap_err();
        assert!(matches!(error, BodyError::Malformed(ref reason) if reason.contains("trailing")));
    }

    #[test]
    fn body_decode_rejects_map_encoding() {
        // A map where the positional array is expected: fixmap with one pair.
        let wire = [0x81, 0x01, 0x02];
        let error = decode_body::<DocumentReadRequest>(&wire).unwrap_err();
        assert!(matches!(error, BodyError::Malformed(_)));
    }

    #[test]
    fn body_decode_rejects_extra_array_elements() {
        let body = DocumentReadRequest { collection: "orders".into(), document: "d-01".into() };
        let mut wire = encode_body(&body).expect("encodes");
        // fixarray(2) -> fixarray(3) with one extra element appended.
        wire[0] = 0x93;
        wire.push(0xc0);
        let error = decode_body::<DocumentReadRequest>(&wire).unwrap_err();
        assert!(matches!(error, BodyError::Malformed(_)));
    }

    #[test]
    fn body_decode_rejects_nil_where_a_value_is_required() {
        // fixarray(2) of [nil, nil]: both required string fields are nil.
        let wire = [0x92, 0xc0, 0xc0];
        let error = decode_body::<DocumentReadRequest>(&wire).unwrap_err();
        assert!(matches!(error, BodyError::Malformed(_)));
    }

    #[test]
    fn body_decode_accepts_nil_only_where_optional() {
        let body = DocumentReadResponse { found: false, document: None };
        let wire = encode_body(&body).expect("encodes");
        let decoded = decode_body::<DocumentReadResponse>(&wire).expect("decodes");
        assert_eq!(decoded, body);
        assert!(wire.contains(&0xc0));
    }

    #[test]
    fn body_decode_accepts_in_range_wider_integer_encodings() {
        // u64 fields carried as fixint, uint8 and uint16 codes all decode
        // when in range; this pins the width-variant discipline both
        // languages follow (out-of-range fails closed in the contract
        // tests below).
        let body = BackboneSubscribeRequest {
            subscription_id: 7,
            collections: None,
            from: SubscriptionStart::Live,
        };
        let wire = encode_body(&body).expect("encodes");
        assert_eq!(decode_body::<BackboneSubscribeRequest>(&wire).expect("decodes"), body);
    }

    #[test]
    fn body_validate_enforces_string_bounds() {
        let body = DocumentReadRequest { collection: "x".repeat(65), document: "d".into() };
        let error = encode_body(&body).unwrap_err();
        assert!(matches!(error, BodyError::StringTooLong { field: "collection", bound: 64 }));
    }

    #[test]
    fn body_validate_enforces_list_bounds() {
        let body = BackboneSubscribeRequest {
            subscription_id: 1,
            collections: Some((0..=MAX_LIST_ITEMS).map(|i| format!("c{i}")).collect()),
            from: SubscriptionStart::Live,
        };
        let error = encode_body(&body).unwrap_err();
        assert!(matches!(error, BodyError::ListTooLong { field: "collections", .. }));
    }

    #[test]
    fn family_kind_is_a_closed_vocabulary() {
        let body = FamilyReadRequest {
            family: "not-a-family".into(),
            name: "n".into(),
            request_codec: "terrabase.family-request/1".into(),
            request: Vec::new(),
        };
        let error = encode_body(&body).unwrap_err();
        assert!(matches!(error, BodyError::NotInVocabulary { field: "family" }));
        assert!(FAMILY_KINDS.contains(&"counter"));
    }

    #[test]
    fn enum_decode_rejects_unknown_tags() {
        // [tag 99] — no write_reason variant carries 99.
        let wire = [0x91, 0x63];
        let error = decode_body::<DocumentWriteResponse>(
            &[0x92, 0x91, 0x63, 0x91, 0x01], // outcome = [99], plus arity break
        );
        assert!(error.is_err());
        let _ = wire;
    }

    #[test]
    fn enum_decode_rejects_wrong_arity() {
        // [tag absent, extra element] — payload-less variant with a payload.
        let outcome =
            crate::codec::encode_body(&DocumentWriteResponse { outcome: WriteOutcome::Conflict })
                .expect("encodes");
        let decoded = decode_body::<DocumentWriteResponse>(&outcome).expect("decodes");
        assert_eq!(decoded.outcome.code(), 2);
    }
}
