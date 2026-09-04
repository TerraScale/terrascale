//! Production composition boundary for the `TerraBase` engine daemon.
//!
//! This crate opens one durable engine location, serves the existing TBEP
//! implementation over mandatory mutual TLS, optionally exposes a local UDS,
//! and publishes private Prometheus and lifecycle probes. It contains no
//! database or protocol implementation of its own.

pub mod api;
pub mod api_keys;
pub mod backup;
pub mod config;
pub mod control;
pub mod guards;
pub mod http;
#[cfg(feature = "production-fips")]
pub mod production;
pub mod reactive_api;
pub mod runtime;

pub use config::ServerConfig;
pub use runtime::run;
