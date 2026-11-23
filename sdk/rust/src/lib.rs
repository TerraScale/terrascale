//! Rust SDK for TerraBase's project-scoped REST API and ts-auth.
//! PostgreSQL wire support (feature `postgres`) remains unqualified.

pub mod auth_idp;
pub mod auth_projects;
pub mod client;
pub mod config;
pub mod consistency;
pub mod error;
mod origin;
#[cfg(feature = "postgres")]
pub mod postgres;
pub mod transport;
pub mod v1;
pub mod webhooks;

pub use auth_idp::AuthIdpClient;
pub use auth_projects::ProjectsClient;
pub use client::TerraScaleClient;
pub use config::{ClientConfig, ClientConfigBuilder};
pub use consistency::{BookmarkTracker, ConsistencyMode};
pub use error::{Error, ErrorKind, Result};
pub use transport::{HttpTransport, Method, Request, ReqwestTransport, Response};
pub use webhooks::{verify_webhook, verify_webhook_at, WebhookHeaders};
