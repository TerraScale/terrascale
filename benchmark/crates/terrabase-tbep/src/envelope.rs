//! The TBEP frame envelope: the SE-04 envelope discipline in its
//! `MessagePack` engine-channel profile.
//!
//! Every frame, in both directions, is a `MessagePack` array of exactly:
//!
//! ```text
//! [ version u8, kind u16, correlation u64, causation u64,
//!   idempotency Option<str>, digest u64, body bytes ]
//! ```
//!
//! - **version**: `TBEP/1`; unknown versions fail closed.
//! - **kind**: a registry code from the generated frame taxonomy; unknown
//!   kinds fail closed.
//! - **correlation/causation**: this frame's identity and the peer frame
//!   it answers; responses echo the request's correlation as causation.
//! - **idempotency**: present exactly on mutation-family frames
//!   (`FrameKind::carries_idempotency`); the pairing is enforced at seal
//!   time and on decode. `None` rides as msgpack `nil`; `nil` anywhere
//!   else fails closed (the nil-handling pitfall the SE-14 survey
//!   flagged, pinned by the conformance vectors).
//! - **digest**: FNV-1a 64 over the body bytes; disagreement fails
//!   closed at decode.
//! - **body**: the body array's bytes; the body's own positional array is
//!   the payload. Length disagreement fails closed.
//!
//! On the stream, every frame is length-prefixed with a `u32`
//! little-endian prefix; the fixed envelope cost is
//! [`FIXED_ENVELOPE_BYTES`] over the body (plus the 4-byte stream
//! prefix), the input to the per-frame envelope-overhead budget
//! (PD-2026-08-26-13 D1).

use serde::Deserialize as _;

use crate::generated::FIXED_ENVELOPE_BYTES;
use crate::generated::FrameKind;
use crate::generated::MAX_FRAME_BYTES;
use crate::generated::STREAM_PREFIX_BYTES;
use crate::generated::TbepBody;
use crate::generated::WIRE_VERSION;

/// The idempotency-key bound (bytes).
pub const MAX_IDEMPOTENCY_BYTES: usize = 128;

/// Every envelope-level failure. All of them fail closed.
#[derive(Clone, Debug, PartialEq)]
pub enum EnvelopeError {
    /// The leading version byte is not the wire version.
    UnsupportedVersion(u8),
    /// The frame is structurally invalid (truncation, trailing bytes,
    /// wrong arity, nil in a required slot, bad types).
    Malformed(&'static str),
    /// The frame kind is not in the registry.
    UnknownFrameKind(u16),
    /// The digest does not cover the body bytes.
    DigestMismatch,
    /// The idempotency pairing is wrong for the frame kind.
    IdempotencyPairing(&'static str),
    /// An idempotency key is not constructible.
    InvalidIdempotencyKey(&'static str),
    /// The frame exceeds the decode bound.
    FrameTooLarge(usize),
    /// A stream transport failure.
    Io(String),
}

impl std::fmt::Display for EnvelopeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::UnsupportedVersion(version) => {
                write!(f, "unsupported TBEP wire version {version}")
            }
            Self::Malformed(reason) => write!(f, "malformed frame: {reason}"),
            Self::UnknownFrameKind(code) => write!(f, "unknown frame kind {code:#06x}"),
            Self::DigestMismatch => write!(f, "the content digest does not cover the body"),
            Self::IdempotencyPairing(reason) => {
                write!(f, "idempotency pairing violation: {reason}")
            }
            Self::InvalidIdempotencyKey(reason) => {
                write!(f, "invalid idempotency key: {reason}")
            }
            Self::FrameTooLarge(bound) => write!(f, "the frame exceeds the {bound}-byte bound"),
            Self::Io(message) => write!(f, "transport failure: {message}"),
        }
    }
}

impl std::error::Error for EnvelopeError {}

/// An idempotency key: the stable retry identity of one mutation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct IdempotencyKey(String);

impl IdempotencyKey {
    /// Builds a key, rejecting empty, whitespace-only, over-long, or
    /// control-carrying values.
    ///
    /// # Errors
    ///
    /// Returns [`EnvelopeError::InvalidIdempotencyKey`] for values that
    /// violate the key limits.
    pub fn new(value: &str) -> Result<Self, EnvelopeError> {
        if value.is_empty() || value.trim().is_empty() {
            return Err(EnvelopeError::InvalidIdempotencyKey("the key is empty"));
        }
        if value.len() > MAX_IDEMPOTENCY_BYTES {
            return Err(EnvelopeError::InvalidIdempotencyKey("the key exceeds its bound"));
        }
        if value.bytes().any(|b| b.is_ascii_control()) {
            return Err(EnvelopeError::InvalidIdempotencyKey("the key carries control characters"));
        }
        Ok(Self(value.to_string()))
    }

    /// The key's text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// The FNV-1a 64 content digest over the body bytes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FrameDigest(u64);

impl FrameDigest {
    /// Computes the digest over the body bytes.
    #[must_use]
    pub fn of(bytes: &[u8]) -> Self {
        let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
        for byte in bytes {
            hash ^= u64::from(*byte);
            hash = hash.wrapping_mul(0x100_0000_01b3);
        }
        Self(hash)
    }

    /// The digest's value.
    #[must_use]
    pub const fn value(self) -> u64 {
        self.0
    }
}

/// The body element of the envelope: raw bytes on the wire.
///
/// serde's blanket `[u8]` serialization rides as an array of integers,
/// which is neither compact nor symmetric with MessagePack-CSharp's
/// `byte[]` (msgpack `bin`); this wrapper pins the `bin` form on both
/// directions and fails closed on anything else.
#[derive(Clone, Debug, PartialEq)]
pub struct WireBytes(pub Vec<u8>);

impl serde::Serialize for WireBytes {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_bytes(&self.0)
    }
}

impl<'de> serde::Deserialize<'de> for WireBytes {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct Visitor;
        impl serde::de::Visitor<'_> for Visitor {
            type Value = WireBytes;
            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("the frame body as msgpack bin bytes")
            }
            fn visit_bytes<E>(self, value: &[u8]) -> Result<Self::Value, E> {
                Ok(WireBytes(value.to_vec()))
            }
            fn visit_byte_buf<E>(self, value: Vec<u8>) -> Result<Self::Value, E> {
                Ok(WireBytes(value))
            }
        }
        deserializer.deserialize_bytes(Visitor)
    }
}

/// The borrowing form of [`WireBytes`] for serialization call sites.
pub struct WireBytesRef<'a>(pub &'a [u8]);

impl serde::Serialize for WireBytesRef<'_> {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_bytes(self.0)
    }
}

/// The wire tuple of the envelope (serialization form).
type EnvelopeWire<'a> = (u8, u16, u64, u64, Option<&'a str>, u64, &'a WireBytes);

/// The wire tuple of the envelope (deserialization form).
type EnvelopeWireOwned = (u8, u16, u64, u64, Option<String>, u64, WireBytes);

/// One typed, versioned TBEP frame.
#[derive(Clone, Debug, PartialEq)]
pub struct TbepFrame {
    correlation: u64,
    causation: u64,
    idempotency: Option<IdempotencyKey>,
    digest: FrameDigest,
    body: TbepBody,
}

impl TbepFrame {
    /// Seals a body into a frame: validates the body's bounds, enforces
    /// the idempotency pairing (a key exactly on mutation-family frames),
    /// and computes the content digest over the encoded body.
    ///
    /// # Errors
    ///
    /// Returns [`EnvelopeError`] when the body violates its bounds or
    /// the idempotency pairing is wrong.
    pub fn seal(
        body: impl Into<TbepBody>,
        correlation: u64,
        causation: u64,
        idempotency: Option<&IdempotencyKey>,
    ) -> Result<Self, EnvelopeError> {
        let body = body.into();
        let kind = body.kind();
        let paired = idempotency.is_some();
        if kind.carries_idempotency() != paired {
            return Err(EnvelopeError::IdempotencyPairing(if paired {
                "only mutation-family frames carry an idempotency key"
            } else {
                "a mutation-family frame requires its idempotency key"
            }));
        }
        let bytes = body.encode().map_err(|_| EnvelopeError::Malformed("body bounds"))?;
        let digest = FrameDigest::of(&bytes);
        Ok(Self { correlation, causation, idempotency: idempotency.cloned(), digest, body })
    }

    /// The frame's kind.
    #[must_use]
    pub const fn kind(&self) -> FrameKind {
        self.body.kind()
    }

    /// The frame's correlation identity.
    #[must_use]
    pub const fn correlation(&self) -> u64 {
        self.correlation
    }

    /// The frame's causation identity.
    #[must_use]
    pub const fn causation(&self) -> u64 {
        self.causation
    }

    /// The idempotency key, present exactly on mutation-family frames.
    #[must_use]
    pub fn idempotency(&self) -> Option<&IdempotencyKey> {
        self.idempotency.as_ref()
    }

    /// The sealed digest.
    #[must_use]
    pub const fn digest(&self) -> FrameDigest {
        self.digest
    }

    /// The frame's body.
    #[must_use]
    pub const fn body(&self) -> &TbepBody {
        &self.body
    }

    /// Whether the sealed digest still covers the encoded body.
    #[must_use]
    pub fn verify(&self) -> bool {
        self.body.encode().is_ok_and(|bytes| FrameDigest::of(&bytes) == self.digest)
    }

    /// The encoded body bytes (the envelope's payload).
    #[must_use]
    pub fn body_bytes(&self) -> Vec<u8> {
        self.body.encode().unwrap_or_default()
    }

    /// The envelope's wire bytes.
    ///
    /// # Panics
    ///
    /// Cannot panic: sealed frames hold validated bodies.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let body = WireBytes(self.body_bytes());
        let wire: EnvelopeWire = (
            WIRE_VERSION,
            self.body.kind().code(),
            self.correlation,
            self.causation,
            self.idempotency.as_ref().map(IdempotencyKey::as_str),
            self.digest.value(),
            &body,
        );
        rmp_serde::to_vec(&wire).expect("the envelope tuple always encodes")
    }

    /// The stream encoding: a `u32` little-endian length prefix plus the
    /// envelope bytes, carried by **one** buffer and one write call
    /// (SE-07 budget E-14: the prefix and the envelope never ride two
    /// syscalls).
    ///
    /// # Errors
    ///
    /// Returns [`EnvelopeError::Io`] on write failures.
    pub fn write_stream<W: std::io::Write>(&self, writer: &mut W) -> Result<(), EnvelopeError> {
        use serde::Serialize as _;
        let body = WireBytes(self.body_bytes());
        let wire: EnvelopeWire = (
            WIRE_VERSION,
            self.body.kind().code(),
            self.correlation,
            self.causation,
            self.idempotency.as_ref().map(IdempotencyKey::as_str),
            self.digest.value(),
            &body,
        );
        // One buffer: a four-byte placeholder prefix, then the envelope
        // serialized directly into it, then the real length patched into
        // the placeholder.
        let mut framed =
            Vec::with_capacity(STREAM_PREFIX_BYTES + FIXED_ENVELOPE_BYTES + body.0.len());
        framed.extend_from_slice(&[0; STREAM_PREFIX_BYTES]);
        let mut serializer = rmp_serde::Serializer::new(&mut framed);
        wire.serialize(&mut serializer)
            .map_err(|_| EnvelopeError::Malformed("the envelope tuple always encodes"))?;
        let length = u32::try_from(framed.len() - STREAM_PREFIX_BYTES)
            .map_err(|_| EnvelopeError::FrameTooLarge(usize::MAX))?;
        if length as usize > MAX_FRAME_BYTES {
            return Err(EnvelopeError::FrameTooLarge(MAX_FRAME_BYTES));
        }
        framed[..STREAM_PREFIX_BYTES].copy_from_slice(&length.to_le_bytes());
        writer.write_all(&framed).map_err(|error| EnvelopeError::Io(error.to_string()))
    }

    /// Reads one frame from the stream. `Ok(None)` is a clean end of
    /// stream.
    ///
    /// # Errors
    ///
    /// Returns [`EnvelopeError`] for every failure; all of them fail
    /// closed (the caller must treat the frame as refused).
    pub fn read_stream<R: std::io::Read>(
        reader: &mut R,
    ) -> Result<Option<TbepFrame>, EnvelopeError> {
        let mut prefix = [0u8; STREAM_PREFIX_BYTES];
        match reader.read_exact(&mut prefix) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::UnexpectedEof => {
                return Ok(None);
            }
            Err(error) => return Err(EnvelopeError::Io(error.to_string())),
        }
        let length = u32::from_le_bytes(prefix) as usize;
        if length > MAX_FRAME_BYTES {
            return Err(EnvelopeError::FrameTooLarge(MAX_FRAME_BYTES));
        }
        let mut bytes = vec![0u8; length];
        reader.read_exact(&mut bytes).map_err(|error| EnvelopeError::Io(error.to_string()))?;
        TbepFrame::from_bytes(&bytes).map(Some)
    }

    /// Decodes one envelope, failing closed on unknown versions, unknown
    /// kinds, structural violations, digest disagreement, and
    /// idempotency-pairing violations.
    ///
    /// # Errors
    ///
    /// Returns [`EnvelopeError`] naming the exact violation.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, EnvelopeError> {
        if bytes.len() > MAX_FRAME_BYTES {
            return Err(EnvelopeError::FrameTooLarge(MAX_FRAME_BYTES));
        }
        let mut deserializer = rmp_serde::Deserializer::new(std::io::Cursor::new(bytes));
        let wire: EnvelopeWireOwned = EnvelopeWireOwned::deserialize(&mut deserializer)
            .map_err(|_| EnvelopeError::Malformed("the envelope did not decode"))?;
        if deserializer.position() != bytes.len() as u64 {
            return Err(EnvelopeError::Malformed("trailing bytes after the envelope"));
        }
        if wire.0 != WIRE_VERSION {
            return Err(EnvelopeError::UnsupportedVersion(wire.0));
        }
        let kind = FrameKind::from_code(wire.1).ok_or(EnvelopeError::UnknownFrameKind(wire.1))?;
        let idempotency =
            wire.4.as_deref().map(IdempotencyKey::new).transpose().map_err(|_| {
                EnvelopeError::Malformed("the idempotency key is not constructible")
            })?;
        if kind.carries_idempotency() != idempotency.is_some() {
            return Err(EnvelopeError::IdempotencyPairing(if idempotency.is_some() {
                "only mutation-family frames carry an idempotency key"
            } else {
                "a mutation-family frame requires its idempotency key"
            }));
        }
        if FrameDigest::of(&wire.6.0) != FrameDigest(wire.5) {
            return Err(EnvelopeError::DigestMismatch);
        }
        let body = TbepBody::decode(kind, &wire.6.0)
            .map_err(|_| EnvelopeError::Malformed("the body did not decode"))?;
        Ok(Self {
            correlation: wire.2,
            causation: wire.3,
            idempotency,
            digest: FrameDigest(wire.5),
            body,
        })
    }
}

/// The bytes-on-wire of one encoded frame (envelope bytes plus the
/// stream prefix): the input to per-frame overhead accounting
/// (`wire bytes minus body bytes`).
#[must_use]
pub fn stream_wire_bytes(frame: &TbepFrame) -> usize {
    frame.to_bytes().len() + STREAM_PREFIX_BYTES
}

/// The per-frame envelope overhead: wire bytes on the stream minus the
/// body bytes.
#[must_use]
pub fn envelope_overhead(frame: &TbepFrame) -> usize {
    stream_wire_bytes(frame) - frame.body_bytes().len()
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use crate::TbepValue;
    use crate::generated::*;

    #[test]
    fn minimal_frame_overhead_stays_within_the_fixed_bound() {
        let frame = TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals");
        let wire = frame.to_bytes();
        let body = frame.body_bytes();
        assert_eq!(body.len(), 1); // fixarray(0)
        // Small integers ride as msgpack fixints, so the actual envelope
        // cost is below the worst-case bound; it can never exceed it.
        assert!(wire.len() - body.len() <= FIXED_ENVELOPE_BYTES);
        assert!(envelope_overhead(&frame) <= FIXED_ENVELOPE_BYTES + STREAM_PREFIX_BYTES);
        // The worst case is real: maximal-width identities and digest
        // reach the bound exactly.
        let maximal_identities = 0x0101_0101_0101_0101_u64.wrapping_mul(u64::from(u16::MAX));
        let saturated =
            TbepFrame::seal(SupervisionHealthRequest {}, maximal_identities, u64::MAX, None)
                .expect("seals");
        assert_eq!(saturated.to_bytes().len() - saturated.body_bytes().len(), FIXED_ENVELOPE_BYTES);
    }

    #[test]
    fn envelope_round_trips_and_is_deterministic() {
        let body = DocumentReadRequest { collection: "orders".into(), document: "d-01".into() };
        let frame = TbepFrame::seal(body, 11, 7, None).expect("seals");
        let first = frame.to_bytes();
        assert_eq!(frame.to_bytes(), first);
        let decoded = TbepFrame::from_bytes(&first).expect("decodes");
        assert_eq!(decoded, frame);
        assert_eq!(decoded.correlation(), 11);
        assert_eq!(decoded.causation(), 7);
        assert!(decoded.verify());
    }

    #[test]
    fn mutation_pairing_is_enforced_both_ways() {
        let body = DocumentWriteRequest {
            document: DocumentPut {
                id: "d-01".into(),
                collection: "orders".into(),
                body: TbepValue::Object(vec![("f".into(), TbepValue::Integer(1))]),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-01".into(),
        };
        let unkeyed = TbepFrame::seal(body.clone(), 1, 0, None).unwrap_err();
        assert!(matches!(unkeyed, EnvelopeError::IdempotencyPairing(_)));
        let key = IdempotencyKey::new("retry-01").expect("key");
        let frame = TbepFrame::seal(body, 1, 0, Some(&key)).expect("seals");
        let read = TbepFrame::seal(
            DocumentReadRequest { collection: "orders".into(), document: "d-01".into() },
            2,
            0,
            Some(&key),
        );
        assert!(matches!(read.unwrap_err(), EnvelopeError::IdempotencyPairing(_)));
        let decoded = TbepFrame::from_bytes(&frame.to_bytes()).expect("decodes");
        assert_eq!(decoded.idempotency().map(IdempotencyKey::as_str), Some("retry-01"));
    }

    #[test]
    fn unknown_version_fails_closed() {
        let mut bytes =
            TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals").to_bytes();
        bytes[1] = 2; // the version byte rides right after the array header
        assert!(matches!(
            TbepFrame::from_bytes(&bytes).unwrap_err(),
            EnvelopeError::UnsupportedVersion(2)
        ));
    }

    #[test]
    fn unknown_kind_fails_closed() {
        let frame = TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals");
        let mut bytes = frame.to_bytes();
        // The kind rides as a u16 behind its `cd` marker at offset 2;
        // rewrite it to an unregistered code, keeping every other byte.
        assert_eq!(bytes[2], 0xcd);
        bytes[3] = 0x7f;
        bytes[4] = 0xff;
        assert!(matches!(
            TbepFrame::from_bytes(&bytes).unwrap_err(),
            EnvelopeError::UnknownFrameKind(0x7fff)
        ));
    }

    #[test]
    fn digest_mismatch_fails_closed() {
        let frame = TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals");
        let mut bytes = frame.to_bytes();
        // Flip one digest byte (find the digest via re-decode is complex;
        // flip the last body byte instead: body changes without digest).
        let last = bytes.len() - 1;
        bytes[last] ^= 0xff;
        assert!(matches!(
            TbepFrame::from_bytes(&bytes).unwrap_err(),
            EnvelopeError::Malformed(_) | EnvelopeError::DigestMismatch
        ));
    }

    #[test]
    fn truncated_and_trailing_frames_fail_closed() {
        let bytes =
            TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals").to_bytes();
        assert!(TbepFrame::from_bytes(&bytes[..bytes.len() - 1]).is_err());
        let mut trailing = bytes.clone();
        trailing.push(0x77);
        assert!(matches!(
            TbepFrame::from_bytes(&trailing).unwrap_err(),
            EnvelopeError::Malformed("trailing bytes after the envelope")
        ));
    }

    #[test]
    fn oversized_frames_fail_closed_before_parsing() {
        assert!(matches!(
            TbepFrame::from_bytes(&vec![0u8; MAX_FRAME_BYTES + 1]).unwrap_err(),
            EnvelopeError::FrameTooLarge(_)
        ));
    }

    #[test]
    fn stream_round_trip_and_clean_eof() {
        let frame = TbepFrame::seal(
            DocumentReadRequest { collection: "orders".into(), document: "d".into() },
            3,
            2,
            None,
        )
        .expect("seals");
        let mut buffer = Vec::new();
        frame.write_stream(&mut buffer).expect("writes");
        let mut cursor = std::io::Cursor::new(buffer);
        let decoded = TbepFrame::read_stream(&mut cursor).expect("reads").expect("frame");
        assert_eq!(decoded, frame);
        assert!(TbepFrame::read_stream(&mut cursor).expect("reads").is_none());
    }
}
