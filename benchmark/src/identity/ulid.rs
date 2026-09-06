//! Strict parsing helpers for the upstream `ulid::Ulid` value.

pub use ::ulid::Ulid;

/// Length of canonical uppercase text.
pub const ULID_TEXT_LENGTH: usize = 26;
/// Length of the binary value in bytes.
pub const ULID_BINARY_BYTES: usize = 16;
/// Width of the Unix-millisecond timestamp.
pub const TIMESTAMP_BITS: u32 = 48;
/// Width of the random component.
pub const ENTROPY_BITS: u32 = 80;
/// Largest representable timestamp.
pub const MAX_TIMESTAMP_MILLIS: u64 = (1_u64 << TIMESTAMP_BITS) - 1;
/// Largest representable random component.
pub const MAX_ENTROPY: u128 = (1_u128 << ENTROPY_BITS) - 1;

const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/// Invalid canonical ULID input at an API or storage boundary.
#[derive(Clone, Copy, Debug, Eq, PartialEq, thiserror::Error)]
pub enum UlidError {
    /// Text does not contain exactly 26 bytes.
    #[error("ulid text must be 26 bytes, found {0}")]
    WrongTextLength(usize),
    /// A character is excluded from the Crockford alphabet as ambiguous.
    #[error("ulid text byte {index} is ambiguous: {:?}", *byte as char)]
    AmbiguousCharacter {
        /// Byte offset in the input.
        index: usize,
        /// Rejected byte.
        byte: u8,
    },
    /// A character is outside the canonical uppercase alphabet.
    #[error("ulid text byte {index} ({:?}) is not canonical uppercase Crockford Base32", *byte as char)]
    NonCanonicalCharacter {
        /// Byte offset in the input.
        index: usize,
        /// Rejected byte.
        byte: u8,
    },
    /// The encoded value exceeds 128 bits.
    #[error("ulid text overflows 128 bits: {:?}", *first as char)]
    TextOverflow {
        /// Leading byte that exceeds the permitted range.
        first: u8,
    },
    /// Binary input does not contain exactly 16 bytes.
    #[error("ulid binary must be 16 bytes, found {0}")]
    WrongBinaryLength(usize),
}

const fn ambiguous(byte: u8) -> bool {
    matches!(byte, b'I' | b'L' | b'O' | b'U' | b'i' | b'l' | b'o' | b'u')
}

/// Parses exactly the canonical uppercase Crockford text form.
///
/// # Errors
///
/// Returns an error when the input has the wrong length, contains a
/// non-canonical or ambiguous character, or exceeds the ULID value range.
pub fn from_text(text: &str) -> Result<Ulid, UlidError> {
    let bytes = text.as_bytes();
    if bytes.len() != ULID_TEXT_LENGTH {
        return Err(UlidError::WrongTextLength(bytes.len()));
    }
    for (index, &byte) in bytes.iter().enumerate() {
        if !ALPHABET.contains(&byte) {
            return Err(if ambiguous(byte) {
                UlidError::AmbiguousCharacter { index, byte }
            } else {
                UlidError::NonCanonicalCharacter { index, byte }
            });
        }
        if index == 0 && byte > b'7' {
            return Err(UlidError::TextOverflow { first: byte });
        }
    }
    // Validation above makes the upstream parser's permissive cases
    // impossible, so it can remain the sole decoder.
    Ulid::from_string(text)
        .map_err(|_| UlidError::NonCanonicalCharacter { index: 0, byte: bytes[0] })
}

/// Parses exactly one 16-byte ULID value.
///
/// # Errors
///
/// Returns [`UlidError::WrongBinaryLength`] when the input is not exactly
/// 16 bytes long.
pub fn from_binary(bytes: &[u8]) -> Result<Ulid, UlidError> {
    bytes.try_into().map(Ulid::from_bytes).map_err(|_| UlidError::WrongBinaryLength(bytes.len()))
}
