//! The TBEP domain value type and the shared bounds discipline.
//!
//! With the `MessagePack` wire retired (ADR 0028), what remains of the old
//! codec module is the part every serving path shares: the recursive
//! document value ([`TbepValue`]) the hosting seam exchanges with the
//! engine, and the fail-closed bound checks the domain registry's
//! `validate` methods enforce. The gRPC layer converts [`TbepValue`]
//! to and from the protobuf `Value` message; object fields stay
//! name-sorted so identical values remain comparable in memory.

/// A body validation failure. Every row fails closed.
#[derive(Clone, Debug, PartialEq)]
pub enum BodyError {
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
        /// The declared item bound.
        bound: usize,
    },
}

impl std::fmt::Display for BodyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
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

/// The recursive document-body value: the payload type of document
/// bodies, schema declarations, and sync field values.
///
/// Objects are name-sorted field lists — `[(name, value)]` pairs in
/// ascending name order — so identical values compare equal regardless
/// of construction order.
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
    /// The value's tag (the retired wire form's; retained as the stable
    /// discriminant of the closed variant set).
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

    /// Canonicalizes an object's fields into ascending name order.
    #[must_use]
    pub fn canonical(mut self) -> Self {
        if let Self::Object(fields) = &mut self {
            fields.sort_by(|a, b| a.0.cmp(&b.0));
        }
        self
    }
}
