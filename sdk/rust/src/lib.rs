//! Rust SDK for TerraBase's project-scoped REST API and ts-auth.
//! PostgreSQL SQL support and security single-writer admission remain unqualified.

pub mod auth_idp;
pub mod auth_projects;
pub mod client;
pub mod config;
pub mod error;
#[cfg(feature = "postgres")]
pub mod postgres;
pub mod regions;
pub mod serde_base64;
pub mod transport;
pub mod v1;
pub mod webhooks;

pub use auth_idp::AuthIdpClient;
pub use auth_projects::ProjectsClient;
pub use client::TerraScaleClient;
pub use config::{ClientConfig, ClientConfigBuilder};
pub use error::{Error, ErrorKind, Result};
pub use regions::{normalize_base_url, RegionCode};
pub use transport::{HttpTransport, Method, Request, ReqwestTransport, Response};
pub use webhooks::{verify_webhook, verify_webhook_at, WebhookHeaders};
