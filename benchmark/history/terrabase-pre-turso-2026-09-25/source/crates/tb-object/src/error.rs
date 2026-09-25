use std::io;

use object_store::Error as ProviderError;
use thiserror::Error;

use crate::{ContentDigest, ObjectKey};

/// A required provider operation whose semantics have not passed a live probe.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ObjectCapability {
    /// Atomic create-only PUT used to reserve a unique object key.
    ConditionalCreate,
    /// Range requests return the exact requested interval.
    RangeRead,
    /// Multipart uploads can be explicitly aborted.
    MultipartAbort,
    /// Existing manifest generations can be updated with a conditional token.
    ConditionalManifestUpdate,
}

impl std::fmt::Display for ObjectCapability {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let name = match self {
            Self::ConditionalCreate => "conditional create-only PUT",
            Self::RangeRead => "range reads",
            Self::MultipartAbort => "multipart abort",
            Self::ConditionalManifestUpdate => "conditional manifest update",
        };
        formatter.write_str(name)
    }
}

/// Guarantees that need more than an ordinary object PUT.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ObjectGuarantee {
    /// Conditional manifest publication under a verified fenced owner.
    FencedConditionalManifestPublication,
}

impl std::fmt::Display for ObjectGuarantee {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::FencedConditionalManifestPublication => {
                formatter.write_str("fenced conditional manifest publication")
            }
        }
    }
}

/// Failures at the immutable object boundary.
#[derive(Debug, Error)]
pub enum ObjectError {
    /// Process memory admission rejected an object operation or buffer.
    #[error(transparent)]
    Admission(#[from] tb_core::AdmissionError),
    /// The provider rejected a request or reported a storage failure.
    #[error(transparent)]
    Provider(#[from] ProviderError),
    /// The input reader failed while producing an upload.
    #[error("object input stream failed: {0}")]
    Input(#[from] io::Error),
    /// The configured object limits are inconsistent or zero.
    #[error("invalid object limits: {0}")]
    InvalidLimits(&'static str),
    /// An object exceeds the configured maximum length.
    #[error("object length {actual} exceeds configured maximum {maximum}")]
    ObjectTooLarge { actual: u64, maximum: u64 },
    /// A range was empty, overflowed, or fell outside its object.
    #[error("object range [{start}, {end}) is invalid for length {object_length}")]
    InvalidRange {
        start: u64,
        end: u64,
        object_length: u64,
    },
    /// A requested range exceeds the configured per-read bound.
    #[error("range length {actual} exceeds configured maximum {maximum}")]
    RangeTooLarge { actual: u64, maximum: usize },
    /// The provider did not return the requested range and object size.
    #[error("provider returned unexpected object metadata for {key}")]
    UnexpectedRange { key: ObjectKey },
    /// Input ended before its declared length or had additional trailing bytes.
    #[error("input length differs from declared length {expected}; read {actual} bytes")]
    InputLengthMismatch { expected: u64, actual: u64 },
    /// Uploaded bytes did not match the caller's expected digest.
    #[error("content SHA-256 mismatch: expected {expected}, got {actual}")]
    ContentDigestMismatch {
        expected: ContentDigest,
        actual: ContentDigest,
    },
    /// A complete immutable object does not match its expected length or digest.
    #[error("stored object {key} failed whole-object verification: {reason}")]
    ObjectCorrupt {
        key: ObjectKey,
        reason: &'static str,
    },
    /// A returned block failed its sidecar-authenticated SHA-256 check.
    #[error("stored block at [{offset}, {end}) failed SHA-256 verification")]
    BlockCorrupt { offset: u64, end: u64 },
    /// An upload session was used in an invalid state.
    #[error("invalid immutable upload state: {0}")]
    InvalidUploadState(&'static str),
    /// A bounded request slot was unavailable; no waiting queue was admitted.
    #[error("object request concurrency limit reached")]
    Busy,
    /// The provider capability has not been demonstrated by a live probe.
    #[error("provider capability is unproven: {0}")]
    CapabilityUnproven(ObjectCapability),
    /// The operation would weaken an unimplemented object durability guarantee.
    #[error("unsupported object guarantee: {0}")]
    UnsupportedGuarantee(ObjectGuarantee),
    /// An object-store request exceeded its configured timeout.
    #[error("object request timed out during {operation}")]
    RequestTimeout { operation: &'static str },
    /// A bounded Rust allocation failed before transfer or decoding.
    #[error("allocation failed for {requested_bytes} bytes")]
    AllocationFailed { requested_bytes: usize },
    /// The provider reported an uncertain key collision too many times while
    /// reserving a unique immutable object name.
    #[error("could not reserve a unique immutable object key after {attempts} attempts")]
    ObjectKeyCollisionLimit { attempts: usize },
    /// A content key parsed from a manifest was malformed or outside this crate's namespace.
    #[error("invalid object key in manifest: {0}")]
    InvalidObjectKey(String),
    /// A manifest's object key, length, and digest combination is inconsistent.
    #[error("invalid manifest object reference: {0}")]
    InvalidManifestReference(&'static str),
    /// A block ordinal is not present in the authenticated sidecar.
    #[error("authenticated remote sidecar has no block with ordinal {ordinal}")]
    InvalidSidecarBlock { ordinal: u32 },
    /// Provider capabilities could not be read because the local report lock was poisoned.
    #[error("provider capability report lock was poisoned")]
    CapabilityReportPoisoned,
    /// The process-wide memory budget has not been initialized.
    #[error("process memory budget must be initialized before creating the object client")]
    ProcessMemoryBudgetUninitialized,
    /// The provider did not reject the losing side of an atomic conditional race.
    #[error("provider conditional-write probe did not demonstrate single-winner semantics")]
    ConditionalProbeFailed,
    /// The provider returned bytes outside the requested range during qualification.
    #[error("provider range-read probe returned unexpected metadata or bytes")]
    RangeProbeFailed,
    /// Multipart upload is not implemented by the provider selected for this object tier.
    #[error("provider does not support multipart upload")]
    MultipartUnsupported,
    /// A transfer failed and the provider also rejected explicit multipart cleanup.
    #[error("object transfer failed ({cause}); multipart cleanup also failed ({cleanup})")]
    UploadCleanup {
        /// The original stream, checksum, or provider failure.
        #[source]
        cause: Box<Self>,
        /// The failure while aborting the provider upload.
        cleanup: ProviderError,
    },
    /// Multipart cleanup timed out or was otherwise inconclusive after a transfer error.
    #[error("object transfer failed ({cause}); cleanup was inconclusive: {cleanup}")]
    UploadCleanupUncertain {
        /// The original stream, checksum, or provider failure.
        cause: Box<Self>,
        /// Cleanup failure or timeout detail.
        cleanup: String,
    },
    /// A completion response was uncertain and the unique object could not be verified.
    #[error("multipart completion for {key} was uncertain: {reason}")]
    CompletionUncertain {
        /// Unique key that may contain an unreferenced object.
        key: ObjectKey,
        /// Completion and readback results.
        reason: String,
    },
}
