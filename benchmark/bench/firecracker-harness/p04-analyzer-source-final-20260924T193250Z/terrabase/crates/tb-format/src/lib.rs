//! Versioned, bounded on-disk and transfer encodings used by `TerraBase`.
//!
//! Parsers borrow from their input buffers and validate the complete object
//! before returning a view. Callers must reserve memory before reading or
//! encoding, and retain the corresponding memory reservation for as long as an
//! encoded or decoded buffer remains live.

#![forbid(unsafe_code)]

mod document;
mod document_v2;
mod envelope;
mod remote;
mod segment;
mod wal;

pub use document::{
    ACTIVE_KEY_LEN, ACTIVE_VALUE_LEN, ActiveSchema, CATALOG_KEY_LEN, CHANGE_KEY_LEN,
    CatalogDefinition, DocumentBinding, DocumentChangeView, DocumentDot, DocumentEventId,
    DocumentFieldInput, DocumentKey, DocumentKind, DocumentSibling, DocumentStateView,
    DocumentView, FieldType, FieldValueView, HIGH_WATER_KEY_LEN, HIGH_WATER_VALUE_LEN,
    MARKER_KEY_LEN, PRIMARY_KEY_LEN, RETRY_KEY_LEN, RETRY_VALUE_LEN, SchemaDefinition, SchemaField,
    decode_active_value, decode_catalog, decode_change, decode_document, decode_document_key,
    decode_high_water_value, decode_retry_value, decode_schema, decode_state,
    decode_state_with_limits, encode_active_key, encode_active_value, encode_catalog_into,
    encode_catalog_key, encode_change_into, encode_change_key, encode_document_into,
    encode_high_water_key, encode_high_water_value, encode_marker_key, encode_primary_key,
    encode_retry_key, encode_retry_value, encode_schema_into, encode_state_into,
    is_document_reserved_key, validate_document_schema,
};
pub use document_v2::{
    APPLIED_KEY_LEN, APPLIED_VALUE_LEN, AppliedIdentity, CAUSAL_MANIFEST_KEY_LEN,
    CAUSAL_MANIFEST_MAX_VALUE_LEN, CausalManifest, STATE_POINTER_VALUE_LEN, SiblingDescriptor,
    StatePointer, decode_applied_identity, decode_causal_manifest, decode_state_pointer,
    describe_change, descriptor_change_key, encode_applied_key, encode_applied_value,
    encode_causal_manifest_into, encode_causal_manifest_key, encode_state_pointer,
    verify_descriptor_change, verify_state_pointer_manifest,
};
pub use envelope::{
    Envelope, EnvelopeCodec, EnvelopeInfo, decode_envelope, encode_envelope_with_codec_into,
    encode_lz4_envelope_into, inspect_envelope,
};
pub use remote::{
    AuthenticatedRemoteBlock, RemoteBlockDigest, RemoteBlockIter, RemoteDescriptorInput,
    RemoteDescriptorSidecar, decode_remote_descriptor_sidecar,
    encode_remote_descriptor_sidecar_into, remote_descriptor_sidecar_encoded_len,
};
pub use segment::{
    BlockDescriptor, CursorRecord, OwnedSegmentBlockCursor, SegmentBlock, SegmentBlockPosition,
    SegmentDescriptorIter, SegmentDirectory, SegmentFooter, SegmentHeader, SegmentRecord,
    SegmentRecordIter, SegmentValue, SegmentValueRange, decode_segment_block,
    decode_segment_block_checked, decode_segment_descriptor_entry, decode_segment_directory,
    decode_segment_footer, decode_segment_header, encode_segment_block_into,
    encode_segment_directory_into, encode_segment_footer_into, encode_segment_header_into,
    encoded_segment_record_len, parse_segment_record, segment_descriptor_entry_len,
};
pub use tb_core::TransactionId;
pub use wal::{
    WalFrame, WalOperation, WalOperationIter, decode_wal_frame, encode_wal_frame_into,
    wal_frame_len,
};

/// Default limits for the first local format increment.
///
/// Callers may lower these values to fit an engine or tenant budget. Limits
/// are checked before format-owned allocations; they do not replace the
/// process memory controller's reservation requirement.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FormatLimits {
    /// Maximum complete WAL frame size, including its header and trailer.
    pub max_frame_bytes: usize,
    /// Maximum mutations in one atomic WAL batch.
    pub max_operations: usize,
    /// Maximum key length, in bytes.
    pub max_key_bytes: usize,
    /// Maximum value length, in bytes.
    pub max_value_bytes: usize,
    /// Maximum number of blocks in one sorted run.
    pub max_segment_blocks: usize,
    /// Maximum stored bytes in one segment block, including block header and CRC.
    pub max_block_stored_bytes: usize,
    /// Maximum uncompressed bytes in one segment block.
    pub max_block_plain_bytes: usize,
    /// Maximum records in one segment block.
    pub max_records_per_block: usize,
    /// Maximum encoded size of a segment directory.
    pub max_directory_bytes: usize,
    /// Maximum encoded size of a remote range descriptor sidecar.
    pub max_remote_sidecar_bytes: usize,
    /// Maximum encoded bytes in one LZ4 envelope, including its header.
    pub max_envelope_encoded_bytes: usize,
    /// Maximum decompressed bytes in one LZ4 envelope.
    pub max_envelope_plain_bytes: usize,
}

impl FormatLimits {
    /// Limits selected for the first local WAL and immutable-run implementation.
    #[must_use]
    pub const fn local_v1() -> Self {
        Self {
            max_frame_bytes: tb_core::MAX_WAL_GROUP_BYTES,
            max_operations: tb_core::MAX_WAL_OPERATIONS,
            max_key_bytes: tb_core::MAX_KEY_BYTES,
            max_value_bytes: tb_core::MAX_VALUE_BYTES,
            max_segment_blocks: 1_000_000,
            max_block_stored_bytes: 2 * 1024 * 1024 + 20,
            max_block_plain_bytes: 2 * 1024 * 1024,
            max_records_per_block: 65_536,
            max_directory_bytes: 16 * 1024 * 1024,
            max_remote_sidecar_bytes: 16 * 1024 * 1024,
            max_envelope_encoded_bytes: tb_core::MAX_REPLICATION_BATCH_BYTES,
            max_envelope_plain_bytes: tb_core::MAX_REPLICATION_BATCH_BYTES,
        }
    }

    /// Clamp caller-supplied limits to immutable format v1 hard ceilings.
    ///
    /// Public fields let engine callers lower limits for tenant or process
    /// budgets. Raising a field never allows a writer to produce data that a
    /// default v1 reader cannot accept.
    pub(crate) const fn effective(self) -> Self {
        Self {
            max_frame_bytes: cap(self.max_frame_bytes, tb_core::MAX_WAL_GROUP_BYTES),
            max_operations: cap(self.max_operations, tb_core::MAX_WAL_OPERATIONS),
            max_key_bytes: cap(self.max_key_bytes, tb_core::MAX_KEY_BYTES),
            max_value_bytes: cap(self.max_value_bytes, tb_core::MAX_VALUE_BYTES),
            max_segment_blocks: cap(self.max_segment_blocks, 1_000_000),
            max_block_stored_bytes: cap(self.max_block_stored_bytes, 2 * 1024 * 1024 + 20),
            max_block_plain_bytes: cap(self.max_block_plain_bytes, 2 * 1024 * 1024),
            max_records_per_block: cap(self.max_records_per_block, 65_536),
            max_directory_bytes: cap(self.max_directory_bytes, 16 * 1024 * 1024),
            max_remote_sidecar_bytes: cap(self.max_remote_sidecar_bytes, 16 * 1024 * 1024),
            max_envelope_encoded_bytes: cap(
                self.max_envelope_encoded_bytes,
                tb_core::MAX_REPLICATION_BATCH_BYTES,
            ),
            max_envelope_plain_bytes: cap(
                self.max_envelope_plain_bytes,
                tb_core::MAX_REPLICATION_BATCH_BYTES,
            ),
        }
    }
}

impl Default for FormatLimits {
    fn default() -> Self {
        Self::local_v1()
    }
}

const fn cap(value: usize, maximum: usize) -> usize {
    if value < maximum { value } else { maximum }
}

/// A cleanly rejected malformed, unsupported, oversized, or corrupt encoding.
#[derive(Debug, thiserror::Error, Clone, PartialEq, Eq)]
pub enum FormatError {
    /// Input ended before a complete structure was available.
    #[error("truncated {format}: expected at least {expected} bytes, got {actual}")]
    Truncated {
        /// Structure being decoded.
        format: &'static str,
        /// Minimum or declared total length required.
        expected: usize,
        /// Available input length.
        actual: usize,
    },
    /// A magic, tag, ordering, or structural invariant did not match v1.
    #[error("invalid {format}: {reason}")]
    Invalid {
        /// Structure being decoded.
        format: &'static str,
        /// Failed invariant.
        reason: &'static str,
    },
    /// A future or unsupported major/minor version was encountered.
    #[error("unsupported {format} version {major}.{minor}")]
    UnsupportedVersion {
        /// Structure being decoded.
        format: &'static str,
        /// Unsupported major version.
        major: u16,
        /// Unsupported minor version.
        minor: u16,
    },
    /// A configured hard limit would be exceeded.
    #[error("{what} length/count {value} exceeds configured limit {limit}")]
    LimitExceeded {
        /// Value being limited.
        what: &'static str,
        /// Declared or calculated value.
        value: usize,
        /// Configured maximum.
        limit: usize,
    },
    /// A checksum failed.
    #[error("checksum mismatch in {format}")]
    ChecksumMismatch {
        /// Structure whose checksum failed.
        format: &'static str,
    },
    /// The checksum-valid frame is missing its fixed commit marker.
    #[error("invalid commit marker in WAL frame")]
    InvalidCommitMarker,
    /// The selected compression implementation rejected its input.
    #[error("LZ4 compression or decompression failed")]
    Compression,
    /// A fallible buffer reservation failed.
    #[error("buffer reservation failed")]
    Allocation,
}

pub(crate) fn limit(what: &'static str, value: usize, maximum: usize) -> Result<(), FormatError> {
    if value > maximum {
        return Err(FormatError::LimitExceeded {
            what,
            value,
            limit: maximum,
        });
    }
    Ok(())
}

pub(crate) fn checked_u32(value: usize, what: &'static str) -> Result<u32, FormatError> {
    u32::try_from(value).map_err(|_| FormatError::LimitExceeded {
        what,
        value,
        limit: u32::MAX as usize,
    })
}
