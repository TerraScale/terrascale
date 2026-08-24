//! Validated replication topology identifiers.
//!
//! `RegionId` and `MemberId` follow the same logical-identifier rules as the
//! crate's other public identifiers (non-empty, at most 255 bytes, no control
//! characters, no leading or trailing whitespace). The `logical_identifier!`
//! macro that enforces those rules for the W3 identifiers is private to
//! `src/types.rs`, so this module holds the equivalent validation for the
//! replication seam: the rules are stated here, not inherited by accident.

use std::fmt;

use super::{MemberId, RegionId};
use crate::operations::RequestValidationError;

/// The maximum byte length of a replication topology identifier.
const MAX_IDENTIFIER_BYTES: usize = 255;

/// Validates one replication topology identifier text.
///
/// The rules match `src/types.rs` exactly: non-empty, at most
/// [`MAX_IDENTIFIER_BYTES`] bytes, no control characters, and no leading or
/// trailing whitespace.
pub(crate) fn validate_topology_identifier(value: &str) -> Result<(), RequestValidationError> {
    if value.is_empty() {
        return Err(RequestValidationError::EmptyField);
    }
    if value.len() > MAX_IDENTIFIER_BYTES {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    if value.chars().any(char::is_control) {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    if value != value.trim() {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    Ok(())
}

impl RegionId {
    /// Creates one validated logical Region identifier.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for empty text and
    /// [`RequestValidationError::InvalidIdentifier`] for text over 255 bytes,
    /// control characters, or leading/trailing whitespace.
    pub fn try_new(value: &str) -> Result<Self, RequestValidationError> {
        validate_topology_identifier(value)?;
        Ok(Self { identifier: value.to_owned() })
    }

    /// Returns the canonical identifier text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.identifier
    }
}

impl fmt::Display for RegionId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.identifier)
    }
}

impl MemberId {
    /// Creates one validated logical Member identifier.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for empty text and
    /// [`RequestValidationError::InvalidIdentifier`] for text over 255 bytes,
    /// control characters, or leading/trailing whitespace.
    pub fn try_new(value: &str) -> Result<Self, RequestValidationError> {
        validate_topology_identifier(value)?;
        Ok(Self { identifier: value.to_owned() })
    }

    /// Returns the canonical identifier text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.identifier
    }
}

impl fmt::Display for MemberId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.identifier)
    }
}

#[cfg(test)]
mod tests {
    use super::{validate_topology_identifier, MAX_IDENTIFIER_BYTES};
    use crate::operations::RequestValidationError;

    #[test]
    fn topology_identifiers_match_the_logical_identifier_rules() {
        assert_eq!(validate_topology_identifier(""), Err(RequestValidationError::EmptyField));
        assert_eq!(
            validate_topology_identifier(" leading"),
            Err(RequestValidationError::InvalidIdentifier)
        );
        assert_eq!(
            validate_topology_identifier("trailing "),
            Err(RequestValidationError::InvalidIdentifier)
        );
        assert_eq!(
            validate_topology_identifier("control\u{7}"),
            Err(RequestValidationError::InvalidIdentifier)
        );
        assert_eq!(
            validate_topology_identifier(&"r".repeat(MAX_IDENTIFIER_BYTES + 1)),
            Err(RequestValidationError::InvalidIdentifier)
        );
        assert!(validate_topology_identifier("r-alpha_1.2").is_ok());
        assert!(validate_topology_identifier(&"m".repeat(MAX_IDENTIFIER_BYTES)).is_ok());
    }
}
