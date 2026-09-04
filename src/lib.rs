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

pub use client::{
    TerraScaleClient, API_ROOT, MAX_IDEMPOTENCY_KEY_LENGTH, PLACEMENT_HEADER, SESSION_HEADER,
};
pub use config::{ClientConfig, ClientConfigBuilder};
pub use documents::{
    BatchOperation, BatchResponse, DatabaseScope, Document, DocumentPage, DocumentRow,
    DocumentVersion, DocumentWriteResponse, DocumentsClient, ListOptions, MAX_BATCH_OPERATIONS,
    MAX_DOCUMENT_BYTES, MAX_DOCUMENT_PAGE_SIZE,
};
pub use error::{Error, ErrorKind, Result};
pub use management::{
    ApiKey, ApiKeyPage, ApiKeys, Backup, BackupPlan, BackupPolicy, BackupStatus,
    BackupStatusResponse, Backups, BranchListResponse, Branches,
    CancellationToken as ManagementCancellationToken, Collections, CollectionsResponse,
    CreateApiKey, CreateBranch, CreateDatabase, CreateDatabaseRequest, CreateDatabaseWithPlacement,
    CreateExport, CreateIndexRequest, CreatedApiKey, DatabaseBranch, DatabaseListResponse,
    DatabaseRecord, DatabaseSchema, Databases, DeletedDatabase, ExecuteBackupRequest, ExportJob,
    Exports, HealthResponse, IndexCreatedResponse, IndexDescriptor, IndexListResponse,
    ManagementClient, OperationOptions, PageOptions, PlacementLock, PlanBackupRequest, Regions,
    ProjectDatabase, RegionSummary, RestoreAt, RestoreJob, RestorePoint, RestoreRequest,
    RevokedApiKey, RotatedApiKey, Schemas, StartRestore, TransferFormat, Usage, UsageClient,
    UsagePoint, UsageRange, UsageSummary, UsageWorkload,
};
pub use reactive::{
    ApplyOutcome, AttachedView, CancellationToken, Checkpoint, LiveSubscription, PollOptions,
    PollOutcome, PollResponse, ReactiveClient, ResetFrame, ResetReason, ResultItem,
    SubscribeOptions, Subscription, Transport, ViewDelta, ViewOperation, ViewSnapshot,
    VIEW_CAPABILITY_HEADER,
};
pub use regions::{
    normalize_base_url, pin_base_url, regional_host, RegionAffinity, RegionCode,
    GLOBAL_API_BASE_URL,
};
pub use schema::{
    CollectionSchema, Document as DocumentSchema, FieldDef, FieldType, FtsDef, IndexDef, IndexKind,
    MergePolicy, Schema, SchemaBuilder, SchemaDocument, SchemaError, SchemaJsonError, TtlDef,
};
pub use sql::{
    metadata_field, metadata_fields, select, validate_parameters, validate_sql, Direction, Query,
    QueryParameters, SelectBuilder, SqlClient, SqlExecuteOptions, SqlParameters, SqlRow, SqlRows,
    METADATA_FIELDS, MAX_SQL_PAGE_LIMIT, MAX_SQL_TEXT_BYTES, MAX_SQL_TIMEOUT_MS,
};
pub use tenants::{
    CreateTenant, CreateTenantBranch, CreateTenantKey, Tenant, TenantApiKey, TenantBranch,
    TenantListOptions, TenantOperationOptions, TenantPage, TenantPlacementLock, TenantTemplate,
    TenantsClient,
};
pub use transport::{HttpTransport, Method, Request, ReqwestTransport, Response};

/// Pinned contract version published by terrabase. Regenerate with
/// `make contracts`; CI fails on drift.
pub const CONTRACT_VERSION: &str = include_str!("../CONTRACT_VERSION");
