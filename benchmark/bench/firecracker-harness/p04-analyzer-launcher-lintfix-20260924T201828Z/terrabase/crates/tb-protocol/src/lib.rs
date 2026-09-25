//! Versioned, bounded Protobuf and gRPC contracts for `TerraBase` replication.
//!
//! The crate owns wire encoding, negotiated transport limits, and transient
//! admission permits. It does not own a replica journal: a receipt or decoded
//! batch cannot advance a durable/applied frontier or create a durable ACK.

#![forbid(unsafe_code)]

mod admission;
mod batch;
mod handshake;
mod security;
mod sync_codec;
mod timeout;

/// Checked-in version 1 Protobuf messages and generated Tonic client/server.
#[allow(
    clippy::default_trait_access,
    clippy::doc_markdown,
    clippy::missing_errors_doc,
    clippy::must_use_candidate,
    reason = "The included Tonic bindings are generated and their diagnostics are upstream-owned."
)]
pub mod v1 {
    tonic::include_proto!("terrabase.v1");
}

pub use admission::{
    AdmissionController, AdmissionError, AdmissionLimits, BatchPermit, CreditError,
    PeerCreditPermit, PeerCreditWindow, StreamPermit,
};
pub use batch::{
    CommittedGroupMetadata, DecodedTransportGroup, EncodedCommittedGroup, PreAdmittedBatch,
    ProtocolError, decode_committed_group, decode_transport_group,
    decode_transport_group_pre_admitted, encode_committed_group, validate_committed_group_header,
    validate_committed_group_header_with_limits,
};
pub use handshake::{
    NegotiationPolicy, negotiate_hello, v1_transfer_limits, validate_frontiers,
    validate_peer_identity,
};
pub use security::{
    PeerIdentityValidator, PeerValidationError, client_mtls_config, server_mtls_config,
    validate_mtls_peer,
};
pub use sync_codec::{
    AdmittedSyncFrame, MAX_PREDECODE_RESERVATION_BYTES, ReceiveMemoryGate, SyncAdmissionContext,
    SyncCodec, SyncRequestDecoder, SyncResponseEncoder,
};
pub use timeout::{
    ProgressTimeout, TransportTimeouts, apply_request_deadline, with_progress_timeout,
};

/// Version 1 protocol major supported by this crate.
pub const V1_PROTOCOL_MAJOR: u32 = 1;
/// Version 1 protocol minor supported by this crate.
pub const V1_PROTOCOL_MINOR: u32 = 0;
/// Version 1 committed-event format supported by the first transport.
pub const V1_EVENT_FORMAT: u32 = 1;
/// Maximum complete local transaction group carried by v1.
pub const MAX_TRANSACTION_GROUP_BYTES: usize = tb_core::MAX_WAL_GROUP_BYTES;
/// Maximum distributed metadata attached to a v1 transaction group.
pub const MAX_EVENT_METADATA_BYTES: usize = tb_core::MAX_EVENT_METADATA_BYTES;
/// Maximum document changes in one v1 transaction group.
pub const MAX_CHANGES_PER_GROUP: usize = tb_core::MAX_WAL_OPERATIONS;
/// Maximum total WAL operations in one v1 transaction group.
pub const MAX_WAL_OPERATIONS_PER_GROUP: usize = tb_core::MAX_WAL_OPERATIONS;
/// Maximum encoded application payload size, including metadata and envelope.
pub const MAX_ENCODED_APPLICATION_BYTES: usize = tb_core::MAX_REPLICATION_BATCH_BYTES;
/// Maximum decoded application payload size, including metadata and WAL frame.
pub const MAX_DECODED_APPLICATION_BYTES: usize = tb_core::MAX_REPLICATION_BATCH_BYTES;
/// Maximum whole Protobuf gRPC message, including bounded event metadata.
pub const MAX_GRPC_MESSAGE_BYTES: usize = 4 * 1024 * 1024;
/// Maximum frontier entries accepted in one handshake.
pub const MAX_FRONTIERS_PER_HANDSHAKE: usize = 1_024;
/// Hard per-peer cap on open streams.
pub const MAX_OPEN_STREAMS_PER_PEER: usize = 64;
/// Hard per-peer cap on admitted in-flight batches.
pub const MAX_IN_FLIGHT_BATCHES_PER_PEER: usize = 1_024;
/// Hard per-peer cap on reserved application bytes.
pub const MAX_IN_FLIGHT_BYTES_PER_PEER: usize = 64 * 1024 * 1024;
