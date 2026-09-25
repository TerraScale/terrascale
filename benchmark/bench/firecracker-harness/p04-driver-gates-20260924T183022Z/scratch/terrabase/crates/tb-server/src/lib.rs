//! `TerraBase` server composition, local-only admin boundary, and process entry contracts.

#![forbid(unsafe_code)]

mod config;
mod http;
mod lifecycle;
mod memory_metrics;
mod memory_pressure;
mod replication_config;
mod replication_server;
mod state;

pub use config::{DEFAULT_LISTEN_PORT, MIN_USABLE_MEMORY_BYTES, ServerConfig, ServerConfigError};
pub use http::{HttpServerError, install_prometheus_recorder, serve};
pub use lifecycle::{Lifecycle, LifecycleError};
pub use memory_metrics::MemoryMetricsSnapshot;
pub use memory_pressure::{CgroupMemoryMonitor, MemoryPressureError};
pub use replication_config::{
    MIN_REPLICATION_HEADROOM_BYTES, MIN_REPLICATION_PROCESS_BYTES,
    REPLICATION_PREDECODE_RESERVATION_BYTES, REPLICATION_STREAM_RESERVATION_BYTES,
    ReplicationConfig, ReplicationConfigError, ReplicationMemoryProfile, parse_fixed_hex,
    parse_local_authority_scope,
};
pub use replication_server::{ReplicationServer, ReplicationServerError};
pub use state::{LocalServerState, ServerStartError};
