//! `tbep-fbs` — the verified ADR 0025 codec boundary over the pinned
//! generated TBEP FlatBuffers bindings.
//!
//! The committed generated binding
//! `crates/terrabase-tbep/src/generated/fbs/tbep_generated.rs` (one
//! deterministic artifact from the single-file root schema) is compiled
//! here via a `#[path]` include. This module implements the receiver
//! discipline ADR 0025 requires for network input:
//!
//! 1. reject a frame above the global byte ceiling before any parse;
//! 2. reject a size prefix that disagrees with the delivered length
//!    (truncated or trailing bytes) before any parse;
//! 3. require the schema's `TBEP` file identifier with a bounded read;
//! 4. run the structural verifier (`size_prefixed_root_with_opts`)
//!    with explicit depth and table-count limits before any generated
//!    accessor reads a field;
//! 5. gate on protocol major, then on unknown required feature bits,
//!    before touching the body union;
//! 6. only then apply the typed envelope semantics: correlation and
//!    causation ULIDs present, known operation code, body present, and
//!    the body union member matching the operation discriminant.
//!
//! No unsafe code is written here; the `unsafe` that appears in the
//! compiled module tree is inside the pinned generated bindings.

// `flatc` 25.12.19 emits these three compiler-owned patterns. Keep the
// exception on the digest-checked generated module so handwritten code
// still passes `clippy -D warnings` without allowances.
#[rustfmt::skip]
#[allow(
    clippy::derivable_impls,
    clippy::extra_unused_lifetimes,
    clippy::missing_safety_doc
)]
#[path = "../../../src/generated/fbs/tbep_generated.rs"]
pub mod tbep_generated;

pub mod hand_encoding;
pub mod operation_map;
pub mod production;
pub mod schema_identity;

pub use operation_map::body_kind_for;
pub use tbep_generated::terrabase::tbep::{TbepEnvelope, TbepFrame, TbepOperation};

/// The pinned `flatc` version (`nix shell nixpkgs#flatbuffers`), the
/// single compiler allowed to produce the committed bindings.
pub const PINNED_FLATC_VERSION: &str = "25.12.19";

/// The FlatBuffers Rust runtime version pinned to match the compiler.
pub const PINNED_RUNTIME_VERSION: &str = "25.12.19";

/// The TBEP protocol major this receiver accepts. An envelope naming a
/// different major is rejected before any body access.
pub const TBEP_PROTOCOL_MAJOR: u16 = 1;

/// The required-feature bits this receiver understands: only
/// `ZSTD_LARGE_PAYLOAD` (bit 0) is defined by the schema today.
pub const KNOWN_FEATURES: u64 = 1;

/// The global frame ceiling (`tbep-registry.json` `max_frame_bytes`).
/// A larger frame is rejected before any parse.
pub const MAX_FRAME_BYTES: usize = 16_777_216;

/// The size-prefix length: standard size-prefixed FlatBuffers carry a
/// little-endian `u32` length of exactly one complete buffer (ADR 0025:
/// no second transport length prefix is added).
pub const SIZE_PREFIX_BYTES: usize = 4;

/// Structural-verifier ceilings for network input: a TBEP frame deeper
/// than 64 tables or holding more than 10,000 tables is hostile and is
/// rejected before field access.
pub const MAX_VERIFY_DEPTH: usize = 64;
/// The table-count ceiling the structural verifier enforces.
pub const MAX_VERIFY_TABLES: usize = 10_000;

/// Why one TBEP size-prefixed FlatBuffers frame was refused. The order
/// of the variants mirrors the receiver discipline above: earlier
/// variants are decided with less of the buffer parsed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DecodeError {
    /// Fewer bytes than the size prefix itself.
    Empty,
    /// The frame exceeded the global byte ceiling before any parse.
    Oversized {
        /// The delivered length in bytes.
        len: usize,
    },
    /// The size prefix declared more bytes than were delivered.
    Truncated {
        /// The length the prefix declared.
        declared: usize,
        /// The bytes actually delivered.
        actual: usize,
    },
    /// The size prefix declared fewer bytes than were delivered:
    /// trailing bytes after one complete buffer are refused.
    Trailing {
        /// The length the prefix declared.
        declared: usize,
        /// The bytes actually delivered.
        actual: usize,
    },
    /// The standard FlatBuffers file-identifier slot was absent or did
    /// not contain the schema's `TBEP` identifier.
    InvalidIdentifier,
    /// The structural verifier rejected the buffer (truncation inside
    /// the buffer, invalid or cyclic offsets, misalignment,
    /// unterminated string, or depth/table overrun).
    Structural(String),
    /// The protocol major is not one this receiver serves.
    VersionSkew {
        /// The envelope's protocol major.
        major: u16,
        /// The envelope's protocol minor.
        minor: u16,
    },
    /// The envelope required a feature bit this receiver does not know.
    UnsupportedFeature {
        /// The unknown required bits, masked to the undefined set.
        unknown: u64,
    },
    /// The correlation ULID is absent.
    MissingCorrelation,
    /// The causation ULID is absent.
    MissingCausation,
    /// The operation discriminant is not a registered frame code.
    UnknownOperation(u16),
    /// The body union member is `NONE`.
    MissingBody,
    /// The body union tag is not declared by the schema.
    UnknownBody(u8),
    /// The body union member does not match the operation discriminant.
    OperationBodyMismatch {
        /// The envelope's operation code.
        operation: u16,
        /// The union member name found in the body.
        body: &'static str,
    },
}

impl core::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::Empty => write!(f, "empty frame: fewer bytes than the size prefix"),
            Self::Oversized { len } => {
                write!(f, "frame of {len} bytes exceeds the {MAX_FRAME_BYTES}-byte ceiling")
            }
            Self::Truncated { declared, actual } => {
                write!(f, "size prefix declared {declared} bytes but {actual} were delivered")
            }
            Self::Trailing { declared, actual } => write!(
                f,
                "size prefix declared {declared} bytes but {actual} were delivered (trailing bytes)"
            ),
            Self::InvalidIdentifier => {
                write!(f, "missing or invalid TBEP file identifier")
            }
            Self::Structural(reason) => {
                write!(f, "structural verification failed: {reason}")
            }
            Self::VersionSkew { major, minor } => {
                write!(f, "protocol {major}.{minor} is not TBEP {TBEP_PROTOCOL_MAJOR}.x")
            }
            Self::UnsupportedFeature { unknown } => {
                write!(f, "unknown required feature bits: {unknown:#x}")
            }
            Self::MissingCorrelation => write!(f, "correlation ULID absent"),
            Self::MissingCausation => write!(f, "causation ULID absent"),
            Self::UnknownOperation(code) => {
                write!(f, "operation code {code} is not registered")
            }
            Self::MissingBody => write!(f, "body union member is NONE"),
            Self::UnknownBody(tag) => {
                write!(f, "body union tag {tag} is not declared")
            }
            Self::OperationBodyMismatch { operation, body } => {
                write!(f, "operation code {operation} does not carry body member {body}")
            }
        }
    }
}

impl core::error::Error for DecodeError {}

/// A structurally verified, version-gated, semantically valid TBEP
/// envelope. Constructed only through [`decode_envelope`].
#[derive(Debug, Clone, Copy)]
pub struct DecodedFrame<'a> {
    /// The verified envelope; generated accessors are safe to call on
    /// it because every gate above has passed.
    pub envelope: TbepEnvelope<'a>,
}

/// Decode one complete size-prefixed TBEP FlatBuffers frame from
/// network bytes, applying the ADR 0025 receiver discipline.
///
/// Every rejection happens before the corresponding field access:
/// byte-ceiling and length checks run before any parse, the structural
/// file-identifier check and verifier run before any generated
/// accessor, and the version and feature gates run before the body
/// union is touched.
pub fn decode_envelope(bytes: &[u8]) -> Result<DecodedFrame<'_>, DecodeError> {
    if bytes.len() < SIZE_PREFIX_BYTES {
        return Err(DecodeError::Empty);
    }
    if bytes.len() - SIZE_PREFIX_BYTES > MAX_FRAME_BYTES {
        return Err(DecodeError::Oversized { len: bytes.len() - SIZE_PREFIX_BYTES });
    }
    let declared = u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]) as usize;
    let actual = bytes.len() - SIZE_PREFIX_BYTES;
    if declared > actual {
        return Err(DecodeError::Truncated { declared, actual });
    }
    if declared < actual {
        return Err(DecodeError::Trailing { declared, actual });
    }

    let identifier_start = SIZE_PREFIX_BYTES + core::mem::size_of::<u32>();
    let identifier = tbep_generated::terrabase::tbep::TBEP_ENVELOPE_IDENTIFIER;
    let identifier_end = identifier_start + identifier.len();
    if bytes.len() < identifier_end
        || &bytes[identifier_start..identifier_end] != identifier.as_bytes()
    {
        return Err(DecodeError::InvalidIdentifier);
    }

    let options = flatbuffers::VerifierOptions {
        max_depth: MAX_VERIFY_DEPTH,
        max_tables: MAX_VERIFY_TABLES,
        ..flatbuffers::VerifierOptions::default()
    };
    let envelope = tbep_generated::terrabase::tbep::size_prefixed_root_as_tbep_envelope_with_opts(
        &options, bytes,
    )
    .map_err(|error| DecodeError::Structural(error.to_string()))?;

    if envelope.protocol_major() != TBEP_PROTOCOL_MAJOR {
        return Err(DecodeError::VersionSkew {
            major: envelope.protocol_major(),
            minor: envelope.protocol_minor(),
        });
    }
    let required = envelope.required_features().bits();
    let unknown = required & !KNOWN_FEATURES;
    if unknown != 0 {
        return Err(DecodeError::UnsupportedFeature { unknown });
    }

    if envelope.correlation().is_none() {
        return Err(DecodeError::MissingCorrelation);
    }
    if envelope.causation().is_none() {
        return Err(DecodeError::MissingCausation);
    }

    let operation = envelope.operation();
    if operation == TbepOperation::NONE {
        return Err(DecodeError::UnknownOperation(0));
    }
    if body_kind_for(operation).is_none() {
        return Err(DecodeError::UnknownOperation(operation.0));
    }

    let body = envelope.body_type();
    if body == TbepFrame::NONE {
        return Err(DecodeError::MissingBody);
    }
    if body.variant_name().is_none() {
        return Err(DecodeError::UnknownBody(body.0));
    }
    let expected = body_kind_for(operation).expect("body_kind_for validated the operation above");
    if body != expected {
        return Err(DecodeError::OperationBodyMismatch {
            operation: operation.0,
            body: body.variant_name().unwrap_or("UNKNOWN"),
        });
    }

    Ok(DecodedFrame { envelope })
}
