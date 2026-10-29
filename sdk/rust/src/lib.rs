//! Rust SDK for TerraBase's project-scoped REST API and ts-auth.
//! PostgreSQL SQL support and security single-writer admission remain unqualified.

pub mod auth_idp;
pub mod client;
pub mod config;
pub mod error;
#[cfg(feature = "postgres")]
pub mod postgres;
pub mod regions;
pub mod serde_base64;
pub mod transport;
pub mod v1;

pub use auth_idp::{
    verify_auth_idp_webhook, verify_auth_idp_webhook_with_tolerance, AuthIdpClient,
};
pub use client::TerraScaleClient;
pub use config::{ClientConfig, ClientConfigBuilder};
pub use error::{Error, ErrorKind, Result};
pub use regions::{normalize_base_url, RegionCode};
pub use transport::{HttpTransport, Method, Request, ReqwestTransport, Response};
