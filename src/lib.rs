//! `terrascale`: Rust SDK for TerraScale.
//!
//! Same core surface as `@terrascale/sdk` (GOAL D-13): document CRUD, the
//! typed schema builder (plus a `#[derive(Schema)]` stub), the typed SQL
//! builder and raw parameterized SQL, `subscribe`/`execute`, checkpoints and
//! typed resets, region pinning, tenants, and management.
//!
//! Start at [`TerraScaleClient`] with a [`ClientConfig`]; every operation
//! returns [`Result`] with a typed [`Error`].

pub mod client;
pub mod config;
pub mod documents;
pub mod error;
pub mod management;
pub mod reactive;
pub mod regions;
pub mod schema;
pub mod sql;
pub mod tenants;
pub mod transport;

pub use client::TerraScaleClient;
pub use config::{ClientConfig, ClientConfigBuilder};
pub use documents::{DatabaseScope, Document, DocumentPage, DocumentVersion, DocumentsClient, ListOptions};
pub use error::{Error, ErrorKind, Result};
pub use management::ManagementClient;
pub use reactive::{
    ApplyOutcome, AttachedView, Checkpoint, ReactiveClient, ResetReason, ResultItem, SubscribeOptions,
    Subscription, Transport, ViewDelta, ViewOperation, ViewSnapshot,
};
pub use regions::{RegionAffinity, RegionCode};
pub use schema::{CollectionSchema, Document as DocumentSchema, FieldDef, FieldType, SchemaBuilder};
pub use sql::{Direction, Query, SelectBuilder, SqlClient, SqlRows};
pub use tenants::{CreateTenant, Tenant, TenantApiKey, TenantBranch, TenantsClient};
pub use transport::{HttpTransport, Method, ReqwestTransport, Request, Response};

/// Pinned contract version published by terrabase. Regenerate with
/// `make contracts`; CI fails on drift.
pub const CONTRACT_VERSION: &str = include_str!("../CONTRACT_VERSION");
