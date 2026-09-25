//! `TerraBase` server composition, local-only admin boundary, and process entry contracts.

#![forbid(unsafe_code)]

mod client_auth;
mod client_http;
mod client_json;
mod config;
mod distributed_runtime;
mod document_service;
mod http;
mod lifecycle;
mod local_v1_runtime;
mod member_control;
mod memory_metrics;
mod memory_pressure;
mod replication_config;
mod replication_sender;
mod replication_sender_tls;
mod replication_server;
mod state;

pub(crate) use client_http::{ClientAdmissions, ClientGeneration, serve as serve_client};
pub(crate) use config::{ClientConfig, load_client_config, load_local_v1_config};
pub use config::{DEFAULT_LISTEN_PORT, MIN_USABLE_MEMORY_BYTES, ServerConfig, ServerConfigError};
pub(crate) use document_service::{
    DocumentServiceError, LocalDurableCommit, LocalV1DocumentService, LocalV1Scope,
};
pub use http::{HttpServerError, install_prometheus_recorder, serve};
pub use lifecycle::{Lifecycle, LifecycleError};
#[doc(hidden)]
pub use local_v1_runtime::run_distributed_process;
#[doc(hidden)]
pub use local_v1_runtime::run_local_v1_process;
pub(crate) use local_v1_runtime::{
    AuthorizedDocumentRequest, DocumentWorkerHandle, PreparedClientResponse, ResponseLease,
    WorkerOperation,
};
pub use member_control::{MemberControlIdentity, bind_member_control, serve_member_control};
pub use memory_metrics::MemoryMetricsSnapshot;
pub use memory_pressure::{CgroupMemoryMonitor, MemoryPressureError};
pub use replication_config::{
    MIN_REPLICATION_HEADROOM_BYTES, MIN_REPLICATION_PROCESS_BYTES,
    REPLICATION_PREDECODE_RESERVATION_BYTES, REPLICATION_SENDER_RESERVATION_BYTES,
    REPLICATION_STREAM_RESERVATION_BYTES, ReplicationConfig, ReplicationConfigError,
    ReplicationMemoryProfile, ReplicationOutboundPeer, parse_fixed_hex,
    parse_local_authority_scope,
};
pub use replication_sender_tls::{ReplicationOutboundTls, ReplicationOutboundTlsError};
pub use replication_server::{ReplicationServer, ReplicationServerError};
pub use state::{LocalServerState, ServerStartError};

/// Small result from the feature-gated client decoder allocation probe.
///
/// This type is exposed only to the dedicated allocation integration test; it
/// contains no parsed request values or references into the decoded request.
#[cfg(feature = "client-decode-allocation-probe")]
#[doc(hidden)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ClientDecodeProbeStats {
    /// Number of writes visited while the decoded request remained alive.
    pub writes: usize,
    /// Number of live writes visited.
    pub live_writes: usize,
    /// Number of borrowed document fields visited.
    pub fields: usize,
    /// Total number of bytes in those fields' canonical payloads.
    pub payload_bytes: usize,
    /// Wrapping FNV-1a checksum over every payload byte visited.
    pub checksum: u64,
}

/// Result categories from the feature-gated client decoder allocation probe.
#[cfg(feature = "client-decode-allocation-probe")]
#[doc(hidden)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClientDecodeProbeError {
    /// The body did not satisfy the client JSON request grammar.
    Validation,
    /// The body or temporary request descriptors exceeded available capacity.
    Capacity,
    /// The decoder returned an error outside its expected validation/capacity set.
    Unexpected,
}

/// Decode one request and visit its borrowed write descriptors while the
/// decoded commit remains alive. This narrow feature-gated seam exists only
/// for measuring the real decoder's allocation behavior in an integration
/// test; it does not expose parsed values or a general-purpose parser.
///
/// The first tuple member is the decoder's provisional memory admission
/// estimate for this input length. It is `None` when the body exceeds the
/// request cap.
///
/// # Errors
/// Returns a compact validation, capacity, or unexpected category for the
/// actual decoder and descriptor-construction path.
#[cfg(feature = "client-decode-allocation-probe")]
#[doc(hidden)]
pub fn client_decode_allocation_probe(
    body: &[u8],
) -> (
    Option<usize>,
    Result<ClientDecodeProbeStats, ClientDecodeProbeError>,
) {
    let bound = client_json::decode_memory_bound(body.len());
    let result = client_json::decode_commit(body)
        .map_err(|error| match error {
            client_json::ClientWireError::Capacity => ClientDecodeProbeError::Capacity,
            client_json::ClientWireError::Validation(_) => ClientDecodeProbeError::Validation,
            _ => ClientDecodeProbeError::Unexpected,
        })
        .and_then(|commit| {
            commit
                .with_writes(|writes| {
                    let writes = std::hint::black_box(writes);
                    let mut stats = ClientDecodeProbeStats {
                        writes: writes.len(),
                        live_writes: 0,
                        fields: 0,
                        payload_bytes: 0,
                        checksum: 0xcbf2_9ce4_8422_2325,
                    };
                    for write in writes {
                        match write.value {
                            tb_document::DocumentWriteValue::Live(fields) => {
                                stats.live_writes += 1;
                                for field in fields {
                                    stats.fields += 1;
                                    stats.payload_bytes += field.value.len();
                                    for byte in field.value {
                                        stats.checksum = (stats.checksum ^ u64::from(*byte))
                                            .wrapping_mul(0x0000_0100_0000_01b3);
                                    }
                                }
                            }
                            tb_document::DocumentWriteValue::Tombstone => {}
                        }
                    }
                    stats
                })
                .map_err(|error| match error {
                    client_json::ClientWireError::Capacity => ClientDecodeProbeError::Capacity,
                    client_json::ClientWireError::Validation(_) => {
                        ClientDecodeProbeError::Validation
                    }
                    _ => ClientDecodeProbeError::Unexpected,
                })
        });
    (bound, result)
}
