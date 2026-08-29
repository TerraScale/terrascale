//! Bounded canonical encoding for native register state.

use std::fmt;

use terrabase_causal::CanonicalEncode;

/// A value that can be carried by a native register.
///
/// Implementations must emit one stable type tag and canonical bytes. The
/// decoder receives the remaining allocation budget before it can allocate.
pub trait CanonicalValue: Clone + Eq {
    /// The stable value type tag.
    const TYPE_TAG: u8;

    /// Appends the value bytes, excluding the type tag.
    fn encode_value(&self, output: &mut Vec<u8>);

    /// Decodes one value after its type tag has been consumed.
    fn decode_value(reader: &mut Decoder<'_>, limits: &DecodeLimits) -> Result<Self, DecodeError>;
}

impl CanonicalValue for bool {
    const TYPE_TAG: u8 = 1;

    fn encode_value(&self, output: &mut Vec<u8>) {
        output.push(u8::from(*self));
    }

    fn decode_value(reader: &mut Decoder<'_>, _: &DecodeLimits) -> Result<Self, DecodeError> {
        match reader.byte()? {
            0 => Ok(false),
            1 => Ok(true),
            _ => Err(DecodeError::InvalidValue),
        }
    }
}

impl CanonicalValue for u64 {
    const TYPE_TAG: u8 = 2;

    fn encode_value(&self, output: &mut Vec<u8>) {
        encode_u64(*self, output);
    }

    fn decode_value(reader: &mut Decoder<'_>, _: &DecodeLimits) -> Result<Self, DecodeError> {
        reader.leb_u64()
    }
}

impl CanonicalValue for i64 {
    const TYPE_TAG: u8 = 3;

    fn encode_value(&self, output: &mut Vec<u8>) {
        encode_u64((self.wrapping_shl(1) ^ self.wrapping_shr(63)) as u64, output);
    }

    fn decode_value(reader: &mut Decoder<'_>, _: &DecodeLimits) -> Result<Self, DecodeError> {
        let value = reader.leb_u64()?;
        Ok(((value >> 1) as i64) ^ -((value & 1) as i64))
    }
}

impl CanonicalValue for String {
    const TYPE_TAG: u8 = 4;

    fn encode_value(&self, output: &mut Vec<u8>) {
        encode_u64(self.len() as u64, output);
        output.extend_from_slice(self.as_bytes());
    }

    fn decode_value(reader: &mut Decoder<'_>, limits: &DecodeLimits) -> Result<Self, DecodeError> {
        let bytes = reader.bytes(limits.max_value_bytes)?;
        String::from_utf8(bytes).map_err(|_| DecodeError::InvalidUtf8)
    }
}

/// Limits applied before any register decoding allocation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DecodeLimits {
    /// Maximum complete encoded register length.
    pub max_bytes: usize,
    /// Maximum candidate count in one register.
    pub max_candidates: usize,
    /// Maximum actor entries in one causal context.
    pub max_context_actors: usize,
    /// Maximum intervals for one actor in one causal context.
    pub max_intervals_per_actor: usize,
    /// Maximum encoded value bytes.
    pub max_value_bytes: usize,
}

impl Default for DecodeLimits {
    fn default() -> Self {
        Self {
            max_bytes: 1 << 20,
            max_candidates: 4096,
            max_context_actors: 4096,
            max_intervals_per_actor: 4096,
            max_value_bytes: 1 << 20,
        }
    }
}

/// A canonical register decoding failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DecodeError {
    /// The input ended before one field was complete.
    UnexpectedEof,
    /// An unsigned LEB128 value overflowed or used a non-minimal form.
    InvalidLeb128,
    /// The input has an unknown codec or datatype major version.
    UnsupportedVersion,
    /// The input's datatype tag does not match the requested register.
    WrongDatatype,
    /// A field has an invalid value or reserved tag.
    InvalidValue,
    /// A byte sequence is not UTF-8.
    InvalidUtf8,
    /// The input exceeds one of the configured bounds.
    LimitExceeded,
    /// The causal context is not in canonical normalized form.
    NonCanonicalContext,
    /// The input has bytes after the register.
    TrailingBytes,
}

impl fmt::Display for DecodeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::UnexpectedEof => "register bytes ended early",
            Self::InvalidLeb128 => "invalid or non-minimal unsigned LEB128",
            Self::UnsupportedVersion => "unsupported native register codec version",
            Self::WrongDatatype => "register datatype tag does not match",
            Self::InvalidValue => "invalid register value",
            Self::InvalidUtf8 => "register string is not UTF-8",
            Self::LimitExceeded => "register decode limit exceeded",
            Self::NonCanonicalContext => "register context is not canonical",
            Self::TrailingBytes => "register bytes have a trailing suffix",
        })
    }
}

impl std::error::Error for DecodeError {}

/// Entry point for the native register codecs.
pub struct RegisterCodec;

impl RegisterCodec {
    /// Encodes one LWW state using its canonical bytes.
    #[must_use]
    pub fn encode_lww<T: CanonicalValue>(state: &crate::LwwRegister<T>) -> Vec<u8> {
        state.canonical_bytes()
    }

    /// Decodes one bounded LWW state.
    pub fn decode_lww<T: CanonicalValue>(
        bytes: &[u8],
        limits: DecodeLimits,
    ) -> Result<crate::LwwRegister<T>, DecodeError> {
        crate::LwwRegister::decode(bytes, limits)
    }

    /// Encodes one MV state using its canonical bytes.
    #[must_use]
    pub fn encode_mv<T: CanonicalValue>(state: &crate::MvRegister<T>) -> Vec<u8> {
        state.canonical_bytes()
    }

    /// Decodes one bounded MV state.
    pub fn decode_mv<T: CanonicalValue>(
        bytes: &[u8],
        limits: DecodeLimits,
    ) -> Result<crate::MvRegister<T>, DecodeError> {
        crate::MvRegister::decode(bytes, limits)
    }
}

/// A bounded cursor passed to custom canonical value decoders.
pub struct Decoder<'a> {
    input: &'a [u8],
    offset: usize,
}

impl<'a> Decoder<'a> {
    pub(crate) fn new(input: &'a [u8], limits: DecodeLimits) -> Result<Self, DecodeError> {
        if input.len() > limits.max_bytes {
            return Err(DecodeError::LimitExceeded);
        }
        Ok(Self { input, offset: 0 })
    }

    /// Reads one byte.
    pub fn byte(&mut self) -> Result<u8, DecodeError> {
        let byte = *self.input.get(self.offset).ok_or(DecodeError::UnexpectedEof)?;
        self.offset += 1;
        Ok(byte)
    }

    /// Reads exactly `N` bytes.
    pub fn fixed<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        let end = self.offset.checked_add(N).ok_or(DecodeError::LimitExceeded)?;
        let bytes = self.input.get(self.offset..end).ok_or(DecodeError::UnexpectedEof)?;
        self.offset = end;
        bytes.try_into().map_err(|_| DecodeError::UnexpectedEof)
    }

    /// Reads one minimal unsigned LEB128 value.
    pub fn leb_u64(&mut self) -> Result<u64, DecodeError> {
        let mut value = 0_u64;
        for index in 0..10 {
            let byte = self.byte()?;
            let payload = u64::from(byte & 0x7f);
            if index == 9 && (byte & 0x7e) != 0 {
                return Err(DecodeError::InvalidLeb128);
            }
            value |= payload.checked_shl(index * 7).ok_or(DecodeError::InvalidLeb128)?;
            if byte & 0x80 == 0 {
                if index > 0 && payload == 0 {
                    return Err(DecodeError::InvalidLeb128);
                }
                return Ok(value);
            }
        }
        Err(DecodeError::InvalidLeb128)
    }

    /// Reads a length-prefixed byte string under `maximum`.
    pub fn bytes(&mut self, maximum: usize) -> Result<Vec<u8>, DecodeError> {
        let length = usize::try_from(self.leb_u64()?).map_err(|_| DecodeError::LimitExceeded)?;
        if length > maximum {
            return Err(DecodeError::LimitExceeded);
        }
        let end = self.offset.checked_add(length).ok_or(DecodeError::LimitExceeded)?;
        let bytes = self.input.get(self.offset..end).ok_or(DecodeError::UnexpectedEof)?;
        self.offset = end;
        Ok(bytes.to_vec())
    }

    /// Reports whether all input bytes were consumed.
    pub fn done(&self) -> bool {
        self.offset == self.input.len()
    }
}

pub(crate) fn encode_u64(mut value: u64, output: &mut Vec<u8>) {
    loop {
        let mut byte = (value & 0x7f) as u8;
        value >>= 7;
        if value != 0 {
            byte |= 0x80;
        }
        output.push(byte);
        if value == 0 {
            return;
        }
    }
}
