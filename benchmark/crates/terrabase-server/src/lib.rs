//! Production composition boundary for the `TerraBase` engine daemon.
//!
//! This crate opens one durable engine location, serves the existing TBEP
//! implementation over mandatory mutual TLS, optionally exposes a local UDS,
//! and publishes private Prometheus and lifecycle probes. It contains no
//! database or protocol implementation of its own.

pub mod admission;
pub mod admitted_databases;
pub mod api;
pub mod api_keys;
pub mod auth_runtime;
pub mod backup;
pub mod byo_jwt;
pub mod config;
pub mod control;
pub mod credential_rotation;
pub mod delegated_identity;
pub mod export_api;
pub mod export_local;
pub mod export_store;
pub mod guards;
pub mod http;
pub mod identity_secret_delivery;
pub mod index_api;
pub mod lifecycle_publication;
pub mod production;
pub mod production_admission;
pub mod production_data_runtime;
pub mod provisioning;
pub mod reactive_api;
mod read_operation;
pub mod replication_cut;
pub mod replication_proof;
pub mod replication_runtime;
pub mod runtime;
pub mod schema_api;
pub mod tenant_api;
pub mod usage_admission;
pub mod usage_api;
pub mod usage_closure_http;
pub mod view_capability;
pub mod webhook_api;

pub use config::ServerConfig;
pub use runtime::run;
pub use view_capability::{
    CAPABILITY_HEADER, CAPABILITY_OVERLAP_SECONDS, CAPABILITY_VERSION, CapabilityError,
    CapabilityIssuer, CapabilityKeyError, CapabilityVerifier, EDGE_CAPABILITY_HEADER,
    EXPIRY_BUCKET_SECONDS, VIEW_CAPABILITY_HEADER, ViewCapability, ViewCapabilityClaims,
    ViewCapabilityIssuer, ViewCapabilityVerifier,
};

/// Authenticated production replication peer workers.
pub mod replication_peers;

/// Encrypted native backup bridge for complete physical bootstrap snapshots.
pub mod native_snapshot_backup;

/// Signed verified native recovery facts for scoped control consumers.
pub mod native_recovery_receipt;

/// Joined live delivery catchup observations.
pub mod catchup_observation;

#[cfg(all(test, target_os = "linux"))]
mod test_keys;
