//! The `terrascale.sync.v2` frame envelope: the shared discipline every
//! frame carries — protocol version, frame kind, correlation and
//! causation identity, an idempotency key exactly where mutation is
//! possible, and a content digest over the canonical body — plus its
//! versioned, fail-closed wire encoding.

use super::body::FrameKind;
use super::body::SyncV2Body;
use crate::FrameDigest;
use crate::FrameId;
use crate::SyncCoreError;

/// The closed error vocabulary of the protocol codec.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProtocolError {
    /// The frame's leading version byte is not
    /// [`super::SYNC_PROTOCOL_VERSION`]. Unknown versions fail closed;
    /// this includes the v1 wire, which v2 deliberately does not speak.
    UnsupportedVersion(u8),
    /// The frame is structurally invalid: truncation, trailing bytes,
    /// length disagreement, unknown kind, violated limits, or a
    /// codec/flag inconsistency.
    MalformedFrame(&'static str),
    /// The idempotency-key pairing is wrong: a mutation frame without a
    /// key, or a non-mutation frame with one.
    IdempotencyPairing(&'static str),
    /// An idempotency key is not constructible (empty, whitespace-only,
    /// or over [`super::MAX_KEY_BYTES`] bytes).
    InvalidIdempotencyKey(&'static str),
}

impl std::fmt::Display for ProtocolError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ProtocolError::UnsupportedVersion(version) => {
                write!(f, "unsupported protocol version {version}")
            }
            ProtocolError::MalformedFrame(reason) => write!(f, "malformed frame: {reason}"),
            ProtocolError::IdempotencyPairing(reason) => {
                write!(f, "idempotency pairing violation: {reason}")
            }
            ProtocolError::InvalidIdempotencyKey(reason) => {
                write!(f, "invalid idempotency key: {reason}")
            }
        }
    }
}

impl std::error::Error for ProtocolError {}

/// An idempotency key: the stable retry identity of one mutation,
/// carried by exactly the mutation-family frames. Not `Clone`-able into
/// a different value domain — it is an opaque bounded string.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct IdempotencyKey(String);

impl IdempotencyKey {
    /// Builds a key, rejecting empty, whitespace-only, or over-long
    /// values.
    ///
    /// # Errors
    ///
    /// Returns [`ProtocolError::InvalidIdempotencyKey`] for values that
    /// violate the key limits.
    pub fn new(value: &str) -> Result<Self, ProtocolError> {
        if value.is_empty() || value.trim().is_empty() {
            return Err(ProtocolError::InvalidIdempotencyKey("the key is empty"));
        }
        if value.len() > super::MAX_KEY_BYTES {
            return Err(ProtocolError::InvalidIdempotencyKey("the key exceeds its bound"));
        }
        if value.bytes().any(|b| b.is_ascii_control()) {
            return Err(ProtocolError::InvalidIdempotencyKey("the key carries control characters"));
        }
        Ok(Self(value.to_string()))
    }

    /// The key's text (already validated at construction).
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }

    /// The canonical key encoding (length-prefixed utf-8).
    fn to_bytes(&self) -> Vec<u8> {
        let mut out = Vec::with_capacity(self.0.len() + 8);
        out.extend_from_slice(
            &u64::try_from(self.0.len()).expect("bounded at construction").to_le_bytes(),
        );
        out.extend_from_slice(self.0.as_bytes());
        out
    }
}

/// One typed, versioned `terrascale.sync.v2` frame.
#[derive(Debug)]
pub struct SyncV2Frame {
    correlation: FrameId,
    causation: FrameId,
    idempotency: Option<IdempotencyKey>,
    digest: FrameDigest,
    body: SyncV2Body,
}

impl SyncV2Frame {
    /// Seals a body into a frame: validates the body, enforces the
    /// idempotency pairing (a key exactly on the mutation-family
    /// frames), and computes the content digest over the canonical body
    /// encoding.
    ///
    /// # Errors
    ///
    /// Returns [`ProtocolError`] when the body violates its invariants
    /// or the idempotency pairing is wrong.
    pub fn seal(
        body: SyncV2Body,
        correlation: FrameId,
        causation: FrameId,
        idempotency: Option<IdempotencyKey>,
    ) -> Result<Self, ProtocolError> {
        let pairing = match (body.is_mutation(), idempotency.is_some()) {
            (true, false) => {
                return Err(ProtocolError::IdempotencyPairing(
                    "a mutation frame requires its idempotency key",
                ));
            }
            (false, true) => {
                return Err(ProtocolError::IdempotencyPairing(
                    "only mutation frames carry an idempotency key",
                ));
            }
            _ => Ok(()),
        };
        pairing?;
        let body_bytes = body.to_body_bytes().map_err(ProtocolError::MalformedFrame)?;
        let digest = FrameDigest::of(&body_bytes);
        Ok(Self { correlation, causation, idempotency, digest, body })
    }

    /// The frame's kind.
    #[must_use]
    pub fn kind(&self) -> FrameKind {
        self.body.kind()
    }

    /// The correlation identity of the frame.
    #[must_use]
    pub fn correlation(&self) -> FrameId {
        self.correlation
    }

    /// The causation identity of the frame.
    #[must_use]
    pub fn causation(&self) -> FrameId {
        self.causation
    }

    /// The idempotency key, present exactly on mutation-family frames.
    #[must_use]
    pub fn idempotency(&self) -> Option<IdempotencyKey> {
        self.idempotency.clone()
    }

    /// The content digest the frame was sealed with.
    #[must_use]
    pub fn digest(&self) -> FrameDigest {
        self.digest
    }

    /// The frame's body, by reference.
    #[must_use]
    pub fn body(&self) -> &SyncV2Body {
        &self.body
    }

    /// The length of the encoded payload this frame carries, `0` when
    /// the frame class carries no payload — the input to the per-frame
    /// envelope-overhead accounting (`wire bytes minus payload bytes`).
    #[must_use]
    pub fn payload_len(&self) -> usize {
        self.body.payload_len()
    }

    /// Whether the sealed digest still matches the body.
    #[must_use]
    pub fn verify(&self) -> bool {
        self.body.to_body_bytes().is_ok_and(|bytes| FrameDigest::of(&bytes) == self.digest)
    }

    /// The versioned wire encoding:
    ///
    /// ```text
    /// u8  version = 2
    /// u16 kind code
    /// u64 correlation
    /// u64 causation
    /// u8  idempotency presence (0 | 1)
    /// [u64 length + utf-8 key]         (present frames only)
    /// u64 content digest (FNV-1a over the body bytes)
    /// u64 body length
    /// .. canonical body bytes          (begin with the u16 kind code)
    /// ```
    ///
    /// All integers little-endian. Deterministic across runs and
    /// platforms.
    ///
    /// # Panics
    ///
    /// Panics only if the sealed body no longer validates, which the
    /// seal-time validation precludes.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let body = self.body.to_body_bytes().expect("sealed frames hold valid bodies");
        let mut bytes = Vec::with_capacity(body.len() + super::FIXED_ENVELOPE_BYTES);
        bytes.push(super::SYNC_PROTOCOL_VERSION);
        bytes.extend_from_slice(&self.kind().code().to_le_bytes());
        bytes.extend_from_slice(&self.correlation.value().to_le_bytes());
        bytes.extend_from_slice(&self.causation.value().to_le_bytes());
        match &self.idempotency {
            Some(key) => {
                bytes.push(1);
                bytes.extend_from_slice(&key.to_bytes());
            }
            None => bytes.push(0),
        }
        bytes.extend_from_slice(&self.digest.value().to_le_bytes());
        bytes
            .extend_from_slice(&u64::try_from(body.len()).expect("body length fits").to_le_bytes());
        bytes.extend_from_slice(&body);
        bytes
    }

    /// Decodes a frame, failing closed on unknown versions (including
    /// v1), unknown kinds, truncation, trailing bytes, length
    /// disagreement, limit violations, and idempotency-pairing
    /// violations. Digest disagreement is not a decode error — call
    /// [`SyncV2Frame::verify`] on the result.
    ///
    /// # Errors
    ///
    /// Returns [`ProtocolError`] naming the exact violation.
    ///
    /// # Panics
    ///
    /// Cannot panic; the internal slice conversions are length-checked
    /// before they run.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, ProtocolError> {
        let malformed = ProtocolError::MalformedFrame;
        if bytes.len() > super::MAX_FRAME_BYTES {
            return Err(malformed("the frame exceeds the decode bound"));
        }
        if bytes.first() != Some(&super::SYNC_PROTOCOL_VERSION) {
            return Err(ProtocolError::UnsupportedVersion(bytes.first().copied().unwrap_or(0)));
        }
        if bytes.len() < 20 {
            return Err(malformed("truncated frame encoding"));
        }
        let mut rest = &bytes[1..];
        let mut take = |n: usize| -> Result<&[u8], ProtocolError> {
            if rest.len() < n {
                Err(malformed("truncated frame encoding"))
            } else {
                let (head, tail) = rest.split_at(n);
                rest = tail;
                Ok(head)
            }
        };
        let kind_bytes = take(2)?;
        let kind = u16::from_le_bytes(kind_bytes.try_into().expect("two bytes"));
        let correlation = FrameId::new(u64::from_le_bytes(take(8)?.try_into().expect("eight")));
        let causation = FrameId::new(u64::from_le_bytes(take(8)?.try_into().expect("eight")));
        let idempotency = match take(1)?[0] {
            0 => None,
            1 => {
                let key_len_at = 8;
                let key_len =
                    u64::from_le_bytes(take(key_len_at)?.try_into().expect("eight bytes"));
                let key_len = usize::try_from(key_len)
                    .map_err(|_| malformed("key length does not fit the platform"))?;
                if key_len > super::MAX_KEY_BYTES {
                    return Err(malformed("key length disagrees with the bytes"));
                }
                let key_bytes = take(key_len)?;
                let text =
                    std::str::from_utf8(key_bytes).map_err(|_| malformed("keys are utf-8"))?;
                Some(IdempotencyKey::new(text)?)
            }
            _ => return Err(malformed("unknown idempotency presence")),
        };
        let digest = FrameDigest(u64::from_le_bytes(take(8)?.try_into().expect("eight bytes")));
        let body_len = u64::from_le_bytes(take(8)?.try_into().expect("eight bytes"));
        let body_len = usize::try_from(body_len)
            .map_err(|_| malformed("body length does not fit the platform"))?;
        if body_len > super::MAX_PAYLOAD_BYTES {
            return Err(malformed("body length exceeds the frame bound"));
        }
        let body_bytes = take(body_len)?;
        if !rest.is_empty() {
            return Err(malformed("trailing bytes after the frame body"));
        }
        let decoded_kind =
            FrameKind::from_code(kind).ok_or_else(|| malformed("unknown frame kind"))?;
        let body = SyncV2Body::from_body_bytes(body_bytes).map_err(|error| match error {
            SyncCoreError::InvalidFrame(reason) => malformed(reason),
            SyncCoreError::UnknownCheckpointVersion(_) => {
                malformed("unknown checkpoint encoding version")
            }
            _ => malformed("the body did not decode"),
        })?;
        if body.kind() != decoded_kind {
            return Err(malformed("the body kind disagrees with the envelope kind"));
        }
        match (body.is_mutation(), idempotency.is_some()) {
            (true, false) => {
                return Err(ProtocolError::IdempotencyPairing(
                    "a mutation frame requires its idempotency key",
                ));
            }
            (false, true) => {
                return Err(ProtocolError::IdempotencyPairing(
                    "only mutation frames carry an idempotency key",
                ));
            }
            _ => {}
        }
        Ok(Self { correlation, causation, idempotency, digest, body })
    }
}
