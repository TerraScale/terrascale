//! The strict ULID value type: one text form, one binary form (ADR 0027
//! 'Canonical representation').
//!
//! The normative value is the specification's 128-bit ULID — a 48-bit
//! Unix-millisecond component followed by 80 bits of entropy — carried here by
//! the pinned `ulid` crate's `Ulid` (the ADR's mandated Rust dependency). The
//! crate is built with no features (see the root `Cargo.toml` review note), so
//! it contributes only the value representation, the big-endian byte
//! conversion, and the canonical encoder; it contributes no generator and no
//! random source, because the `generate` submodule owns those under the
//! ADR's "cryptographically secure monotonic" rule.
//!
//! Parsing is deliberately stricter than the upstream crate. Upstream
//! `ulid::Ulid::from_string` accepts lowercase Crockford text and silently
//! truncates a 130-bit decode back to 128 bits; ADR 0027 instead requires that
//! "decoders reject overflow, incorrect length, ambiguous characters,
//! non-canonical text, and non-16-byte binary values", so
//! [`Ulid::from_text`] classifies every refusal itself and never normalizes
//! input: `"01arz..."` is a typed refusal, never a repaired canonical value.
//! One semantic value therefore has exactly one accepted text form
//! ([`Ulid::to_text`]) and one accepted binary form ([`Ulid::to_bytes`]).
//!
//! Comparison is unsigned lexicographic byte order over the 16-byte form,
//! which the specification defines as the ULID chronological order; it is the
//! storage-key order of ADR 0027 'Storage and sharding'. The timestamp
//! component is metadata only (ADR 0027: never trusted for causality,
//! authorization, conflict resolution, expiry, or durability).

// TerraBase and ULID are proper names, not items to link, so the dense
// documentation keeps them plain.
#![allow(clippy::doc_markdown)]

use std::fmt;
use std::str::FromStr;

/// The length of the one accepted text form: exactly 26 characters.
pub const ULID_TEXT_LENGTH: usize = 26;

/// The length of the one accepted binary form: exactly 16 bytes.
pub const ULID_BINARY_BYTES: usize = 16;

/// The bit width of the Unix-millisecond component.
pub const TIMESTAMP_BITS: u32 = 48;

/// The bit width of the entropy component.
pub const ENTROPY_BITS: u32 = 80;

/// The largest representable Unix-millisecond component: `2^48 - 1`
/// (281474976710655, far beyond any operational clock horizon).
pub const MAX_TIMESTAMP_MILLIS: u64 = (1_u64 << TIMESTAMP_BITS) - 1;

/// The largest representable entropy component: `2^80 - 1`. Reaching it inside
/// one millisecond is beyond any real generator, so monotonic generation
/// treats it as "wait for a later millisecond" rather than "wrap"
/// (ADR 0027 'Generation').
pub const MAX_ENTROPY: u128 = (1_u128 << ENTROPY_BITS) - 1;

/// The Crockford Base32 alphabet of the canonical text form: uppercase only,
/// with the ambiguous `I`, `L`, `O`, and `U` removed by the specification.
const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/// The marker for "this byte is not a canonical digit" in [`LOOKUP`].
const NO_DIGIT: u8 = 255;

/// Builds the strict decode table: only the 32 canonical uppercase alphabet
/// bytes map to a digit, and every other byte — including all lowercase text
/// and the ambiguous `I`, `L`, `O`, `U` in either case — maps to [`NO_DIGIT`].
#[allow(clippy::cast_possible_truncation)] // a digit index is below the 32-entry alphabet
const fn build_strict_lookup() -> [u8; 256] {
    let mut table = [NO_DIGIT; 256];
    let mut digit = 0_usize;
    while digit < ALPHABET.len() {
        table[ALPHABET[digit] as usize] = digit as u8;
        digit += 1;
    }
    table
}

/// The strict decode table. Unlike upstream `ulid`, this table has no
/// lowercase entries, so lowercase text cannot parse.
const LOOKUP: [u8; 256] = build_strict_lookup();

/// Returns whether the byte is one of the specification's excluded ambiguous
/// characters (`I`, `L`, `O`, `U`), which are refused with their own error
/// class so a caller can tell a transliterated `1`/`l` confusion from any
/// other stray byte.
const fn is_ambiguous(byte: u8) -> bool {
    matches!(byte, b'I' | b'L' | b'O' | b'U' | b'i' | b'l' | b'o' | b'u')
}

/// The typed refusals of ULID construction. Every variant names its exact
/// rejection class; none carries or implies a repaired value, because the
/// parsers never normalize input (the same rule
/// [`crate::regions::RegionCodeError`] applies to Region text).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UlidError {
    /// The text was not exactly [`ULID_TEXT_LENGTH`] bytes long. Carries the
    /// observed byte length.
    WrongTextLength(usize),
    /// The text contained one of the ambiguous Crockford characters
    /// (`I`, `L`, `O`, `U`) in either case at a canonical position. Carries
    /// the index and the offending byte.
    AmbiguousCharacter {
        /// The index of the offending byte.
        index: usize,
        /// The offending byte itself.
        byte: u8,
    },
    /// The text contained a byte outside the canonical uppercase alphabet —
    /// lowercase text, a hyphen, braces, whitespace, or a non-ASCII byte.
    /// Carries the index and the offending byte.
    NonCanonicalCharacter {
        /// The index of the offending byte.
        index: usize,
        /// The offending byte itself.
        byte: u8,
    },
    /// The text is 26 canonical characters but decodes to at least `2^128`,
    /// because its first character is above `7`. Carries that first byte.
    TextOverflow {
        /// The first character of the refused text.
        first: u8,
    },
    /// The binary value was not exactly [`ULID_BINARY_BYTES`] bytes long.
    /// Carries the observed byte length.
    WrongBinaryLength(usize),
    /// A parts construction carried a Unix-millisecond component above
    /// [`MAX_TIMESTAMP_MILLIS`]. Carries the refused value.
    TimestampOverflow {
        /// The refused Unix-millisecond component.
        milliseconds: u64,
    },
    /// A parts construction carried an entropy component above
    /// [`MAX_ENTROPY`]. Carries the refused value.
    EntropyOverflow {
        /// The refused entropy component.
        entropy: u128,
    },
}

impl fmt::Display for UlidError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match *self {
            Self::WrongTextLength(len) => {
                write!(formatter, "ulid text must be {ULID_TEXT_LENGTH} bytes, found {len}")
            }
            Self::AmbiguousCharacter { index, byte } => write!(
                formatter,
                "ulid text byte {index} is the ambiguous Crockford character {:?}",
                byte as char,
            ),
            Self::NonCanonicalCharacter { index, byte } => write!(
                formatter,
                "ulid text byte {index} ({:?}) is not canonical uppercase Crockford Base32",
                byte as char,
            ),
            Self::TextOverflow { first } => write!(
                formatter,
                "ulid text overflows 128 bits: first character {:?} is above '7'",
                first as char,
            ),
            Self::WrongBinaryLength(len) => {
                write!(formatter, "ulid binary must be {ULID_BINARY_BYTES} bytes, found {len}")
            }
            Self::TimestampOverflow { milliseconds } => write!(
                formatter,
                "ulid timestamp {milliseconds} exceeds {MAX_TIMESTAMP_MILLIS} milliseconds",
            ),
            Self::EntropyOverflow { entropy } => {
                write!(formatter, "ulid entropy {entropy} exceeds {MAX_ENTROPY}")
            }
        }
    }
}

impl std::error::Error for UlidError {}

/// A strict ULID value: 48 timestamp bits over 80 entropy bits, compared in
/// unsigned big-endian byte order (the specification's chronological order).
///
/// This is the value type only. Entity identities are the typed newtypes in
/// [`crate::identity`] (a [`crate::identity::ClusterUlid`] can never be
/// conflated with a [`crate::identity::DocumentUlid`]), and minting is the
/// monotonic generator in the `generate` submodule. There is
/// deliberately no `Default`: a nil ULID is a legitimate corner of the value
/// space and the specification corpus covers it, but no TerraBase entity is
/// ever "unset", so the type offers no way to default-construct one by
/// accident. A Region has no ULID at all (ADR 0027; see
/// [`crate::regions::RegionCode`]).
#[allow(clippy::module_name_repetitions)] // the name is the specification's term, not a module echo
#[derive(Clone, Copy, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Ulid(::ulid::Ulid);

impl Ulid {
    /// The all-zero value, whose canonical text is
    /// `00000000000000000000000000` (ULID specification vector).
    #[must_use]
    pub const fn min() -> Self {
        Self(::ulid::Ulid::from_bytes([0; ULID_BINARY_BYTES]))
    }

    /// The maximal value, whose canonical text is
    /// `7ZZZZZZZZZZZZZZZZZZZZZZZZZ` (ULID specification vector).
    #[must_use]
    pub const fn max() -> Self {
        Self(::ulid::Ulid::from_bytes([0xFF; ULID_BINARY_BYTES]))
    }

    /// Constructs a value from its two components.
    ///
    /// # Errors
    ///
    /// Returns [`UlidError::TimestampOverflow`] when `timestamp_millis` is
    /// above [`MAX_TIMESTAMP_MILLIS`] and [`UlidError::EntropyOverflow`] when
    /// `entropy` is above [`MAX_ENTROPY`]: no other component pairs exist.
    pub const fn from_parts(timestamp_millis: u64, entropy: u128) -> Result<Self, UlidError> {
        if timestamp_millis > MAX_TIMESTAMP_MILLIS {
            return Err(UlidError::TimestampOverflow { milliseconds: timestamp_millis });
        }
        if entropy > MAX_ENTROPY {
            return Err(UlidError::EntropyOverflow { entropy });
        }
        Ok(Self(::ulid::Ulid::from_parts(timestamp_millis, entropy)))
    }

    /// Parses the one accepted text form: exactly
    /// [`ULID_TEXT_LENGTH`] characters of canonical uppercase Crockford
    /// Base32 whose first character is at most `7`.
    ///
    /// # Errors
    ///
    /// Returns [`UlidError::WrongTextLength`] for any other byte length,
    /// [`UlidError::AmbiguousCharacter`] for the excluded lookalikes
    /// `I`, `L`, `O`, `U` in either case, [`UlidError::NonCanonicalCharacter`]
    /// for any other non-alphabet byte (which is how lowercase text is
    /// refused), and [`UlidError::TextOverflow`] when 26 canonical characters
    /// still decode to at least `2^128`.
    pub fn from_text(text: &str) -> Result<Self, UlidError> {
        let bytes = text.as_bytes();
        if bytes.len() != ULID_TEXT_LENGTH {
            return Err(UlidError::WrongTextLength(bytes.len()));
        }
        // The first character carries only the top two bits of the 130-bit
        // encoding, so anything above '7' overflows the 128-bit value; the
        // alphabet check happens first so 'I' is reported as ambiguous rather
        // than as overflow.
        let first = bytes[0];
        let first_digit = canonical_digit(0, first)?;
        if first_digit > 7 {
            return Err(UlidError::TextOverflow { first });
        }
        let mut value: u128 = 0;
        for (index, &byte) in bytes.iter().enumerate() {
            let digit = if index == 0 { first_digit } else { canonical_digit(index, byte)? };
            value = (value << 5) | u128::from(digit);
        }
        Ok(Self(::ulid::Ulid::from(value)))
    }

    /// Parses the one accepted binary form: exactly [`ULID_BINARY_BYTES`]
    /// bytes, interpreted as an unsigned big-endian integer. Every 16-byte
    /// pattern is a representable ULID, so this is the only binary refusal
    /// class.
    ///
    /// # Errors
    ///
    /// Returns [`UlidError::WrongBinaryLength`] for any byte length other
    /// than [`ULID_BINARY_BYTES`]. Trailing or truncated frames must be
    /// refused before this call by the owning protocol boundary.
    pub fn from_binary(bytes: &[u8]) -> Result<Self, UlidError> {
        match bytes.try_into() {
            Ok(wire) => Ok(Self::from_bytes(wire)),
            // The only refusal class of the fixed-width binary form: any
            // length other than sixteen bytes, carried exactly as observed.
            Err(_) => Err(UlidError::WrongBinaryLength(bytes.len())),
        }
    }

    /// Constructs a value from a decoded wire frame. Every 16-byte pattern is
    /// a representable ULID, so unlike the text and slice parsers this
    /// constructor is infallible; private TBEP protobuf fields decode through
    /// it after their own field-length validation
    /// (ADR 0027 'Canonical representation').
    #[must_use]
    pub const fn from_bytes(bytes: [u8; ULID_BINARY_BYTES]) -> Self {
        Self(::ulid::Ulid::from_bytes(bytes))
    }

    /// Returns the one accepted binary form: the value as an unsigned
    /// big-endian 16-byte array. Storage keys embed these bytes directly
    /// (ADR 0027 'Storage and sharding').
    #[must_use]
    pub const fn to_bytes(self) -> [u8; ULID_BINARY_BYTES] {
        self.0.to_bytes()
    }

    /// Returns the one accepted text form as an owned string: exactly
    /// [`ULID_TEXT_LENGTH`] uppercase Crockford characters. REST, logs,
    /// operator displays, and TQL literals carry this form.
    #[must_use]
    pub fn to_text(self) -> String {
        let mut buffer = [0; ULID_TEXT_LENGTH];
        self.0.array_to_str(&mut buffer).to_owned()
    }

    /// Writes the one accepted text form into a caller-owned buffer. This is
    /// the allocation-free path for logs and protocol encoders; it always
    /// writes exactly [`ULID_TEXT_LENGTH`] bytes.
    pub fn write_text(self, buffer: &mut [u8; ULID_TEXT_LENGTH]) -> &mut str {
        self.0.array_to_str(buffer)
    }

    /// Returns the 48-bit Unix-millisecond component.
    ///
    /// This is metadata only. ADR 0027 forbids trusting it for causality,
    /// authorization, conflict resolution, expiry, durability, record
    /// retention, or proof of creation time; the one sanctioned use is
    /// ADR 0022's client-request age and future-skew window, which does not
    /// prove freshness.
    #[must_use]
    pub const fn timestamp_millis(self) -> u64 {
        self.0.timestamp_ms()
    }

    /// Returns the 80-bit entropy component.
    #[must_use]
    pub const fn entropy(self) -> u128 {
        self.0.random()
    }

    /// Returns the next value inside the same millisecond: the entropy
    /// component incremented by one, the timestamp untouched.
    ///
    /// Returns `None` at [`MAX_ENTROPY`] — monotonic generation then waits
    /// for a later millisecond instead of wrapping into the timestamp, per
    /// ADR 0027 'Generation'.
    #[must_use]
    pub const fn increment_entropy(self) -> Option<Self> {
        match self.0.increment() {
            Some(value) => Some(Self(value)),
            None => None,
        }
    }
}

/// Decodes one canonical digit, classifying the refusal if it is not one.
const fn canonical_digit(index: usize, byte: u8) -> Result<u8, UlidError> {
    let digit = LOOKUP[byte as usize];
    if digit != NO_DIGIT {
        Ok(digit)
    } else if is_ambiguous(byte) {
        Err(UlidError::AmbiguousCharacter { index, byte })
    } else {
        Err(UlidError::NonCanonicalCharacter { index, byte })
    }
}

impl fmt::Debug for Ulid {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut buffer = [0; ULID_TEXT_LENGTH];
        formatter.write_str("Ulid(\"")?;
        formatter.write_str(self.0.array_to_str(&mut buffer))?;
        formatter.write_str("\")")
    }
}

impl fmt::Display for Ulid {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut buffer = [0; ULID_TEXT_LENGTH];
        formatter.write_str(self.0.array_to_str(&mut buffer))
    }
}

impl FromStr for Ulid {
    type Err = UlidError;

    fn from_str(text: &str) -> Result<Self, Self::Err> {
        Self::from_text(text)
    }
}

impl From<[u8; ULID_BINARY_BYTES]> for Ulid {
    fn from(bytes: [u8; ULID_BINARY_BYTES]) -> Self {
        Self::from_bytes(bytes)
    }
}

impl From<Ulid> for [u8; ULID_BINARY_BYTES] {
    fn from(value: Ulid) -> Self {
        value.to_bytes()
    }
}

impl TryFrom<&str> for Ulid {
    type Error = UlidError;

    fn try_from(text: &str) -> Result<Self, Self::Error> {
        Self::from_text(text)
    }
}

impl TryFrom<&[u8]> for Ulid {
    type Error = UlidError;

    fn try_from(bytes: &[u8]) -> Result<Self, Self::Error> {
        Self::from_binary(bytes)
    }
}
