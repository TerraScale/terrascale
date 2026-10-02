//! Lossless Rust SDK for TerraBase's scoped native document protocol and ts-auth.
//!
//! Native reads retain conflicts, tombstones, numeric fields, signed integers,
//! bytes and observed context. Transactions preserve immutable retry identity.
//! PostgreSQL SQL support and security single-writer admission remain unqualified.

pub mod auth_idp;
mod auth_routing;
pub mod client;
pub mod config;
pub mod discovery;
pub mod documents;
pub mod error;
pub mod generated_contract;
#[cfg(feature = "postgres")]
pub mod postgres;
pub mod regions;
pub mod serde_base64;
pub mod subscriptions;
pub mod transactions;
pub mod transport;
pub mod v1;
pub mod values;

pub use auth_idp::{
    verify_auth_idp_webhook, verify_auth_idp_webhook_with_tolerance, AuthIdpClient,
};
pub use client::TerraScaleClient;
pub use config::{ClientConfig, ClientConfigBuilder};
pub use discovery::{
    AtlasDiscovery, DiscoveryAuthority, DiscoveryClock, DiscoveryMetadata, DiscoveryTime,
    NativeCapabilities, NativeEndpoint, NativeRequirement, RegionRtt, ResolvedNativeOrigin,
    ATLAS_BASE_URL,
};
pub use documents::{
    DatabaseScope, DocumentsClient, Event, Observed, Point, PointState, Predicate, ProtocolVersion,
    Query, QueryPage, QueryRow, Sibling, SiblingValue,
};
pub use error::{Error, ErrorKind, Result};
pub use regions::{normalize_base_url, regional_host, RegionCode};
pub use subscriptions::*;
pub use transactions::*;
pub use transport::{HttpTransport, Method, Request, ReqwestTransport, Response};
pub use values::{
    CollectionId, DatabaseId, DecimalU64, Digest, DocumentId, Field, Fields, Hex, Incarnation,
    ReplicaId, TenantId, Value,
};
pub const CONTRACT_VERSION: &str = generated_contract::CONTRACT_VERSION;
