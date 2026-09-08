//! `terrascale`: Rust SDK for TerraScale.
//!
//! Document CRUD, the
//! typed schema builder (plus `#[derive(Schema)]`), the typed SQL
//! builder and raw parameterized SQL, `subscribe`/`execute`, checkpoints and
//! typed resets, region pinning, tenants, and management.
//!
//! Start at [`TerraScaleClient`] with a [`ClientConfig`]; every operation
//! returns [`Result`] with a typed [`Error`].

pub mod client;
pub mod config;
pub mod documents;
pub mod error;
pub mod generated_contract;
pub mod management;
pub mod reactive;
pub mod regions;
pub mod schema;
pub mod serde_base64;
pub mod sql;
pub mod tenants;
pub mod transport;
pub(crate) mod websocket;

pub use client::{
    TerraScaleClient, API_ROOT, MAX_IDEMPOTENCY_KEY_LENGTH, PLACEMENT_HEADER, SESSION_HEADER,
};
pub use config::{ClientConfig, ClientConfigBuilder};
pub use documents::{
    BatchOperation, BatchResponse, Consistency, DatabaseScope, Document, DocumentOperationOptions,
    DocumentPage, DocumentReadOptions, DocumentRow, DocumentVersion, DocumentWriteResponse,
    DocumentsClient, Durability, ListOptions, MAX_BATCH_OPERATIONS, MAX_DOCUMENT_BYTES,
    MAX_DOCUMENT_PAGE_SIZE,
};
pub use error::{Error, ErrorKind, Result};
pub use management::{
    ApiKey, ApiKeyPage, ApiKeys, AuthConditionalUpdate, AuthConnector, AuthConsume, AuthMode,
    AuthOperationOptions, AuthOperator, AuthPageQuery, AuthPersistence, AuthPredicate, AuthRecord,
    AuthResult, AuthTransactionIdentity, AuthUniqueIndex, AuthUniqueInsert, Backup, BackupPlan,
    BackupPolicy, BackupStatus, BackupStatusResponse, Backups, BillableMeter, BranchListResponse,
    Branches, CancellationToken as ManagementCancellationToken, Collections, CollectionsResponse,
    CreateApiKey, CreateBranch, CreateDatabaseRequest, CreateDatabaseResult, CreateExport,
    CreateIndexRequest, CreateWebhook, CreatedApiKey, DatabaseBranch, DatabaseListResponse,
    DatabaseRecord, DatabaseSchema, Databases, DeleteDatabaseAccepted, DeleteDatabaseResult,
    DeletedDatabase, DeletionClaimReceipt, DeletionEdgeReceipt, DeletionOperation,
    DeletionPreparation, DeletionReceipt, DeletionRequest, DeletionStop, DeletionTarget,
    DeliveryLog, DeliveryRecord, Development, DevelopmentFaultKind, DevelopmentFaultRequest,
    DevelopmentFaultResponse, DevelopmentLogEntry, DevelopmentLogResponse, ExecuteBackupRequest,
    ExportAccepted, ExportArtifact, ExportCancellation, ExportDownload, ExportJobStatus,
    ExportList, ExportPhase, ExportStatus, Exports, HealthResponse, IndexCreatedResponse,
    IndexDescriptor, IndexListResponse, IndexRecommendation, ManagementClient, MeterTotal,
    OperationListOptions, OperationOptions, OperationPage, OperationSummary, Operations,
    PageOptions, PlacementLock, PlanBackupRequest, ProvisioningOperation, ReadAttribution,
    RecommendationList, RegionCatalogResponse, RegionDetails, Regions, RestoreAt, RestoreJob,
    RestorePoint, RestoreRequest, ResumeExport, RevokedApiKey, RotateWebhookSecret, RotatedApiKey,
    Schemas, StartRestore, TransferFormat, Usage, UsageClient, UsagePoint, UsageRange,
    UsageSummary, UsageWorkload, WebhookEndpoint, WebhookList, WebhookSecretResponse, Webhooks,
};
pub use reactive::{
    ApplyOutcome, AttachedView, CancellationToken, Checkpoint, LiveSubscription, PollOptions,
    PollOutcome, PollResponse, ReactiveClient, ResetFrame, ResetReason, ResultItem,
    SubscribeOptions, Subscription, Transport, ViewDelta, ViewOperation, ViewSnapshot,
    CAPABILITY_HEADER,
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
    MAX_SQL_PAGE_LIMIT, MAX_SQL_TEXT_BYTES, MAX_SQL_TIMEOUT_MS, METADATA_FIELDS,
};
pub use tenants::{
    CreateTenant, CreateTenantBranch, CreateTenantKey, Tenant, TenantApiKey, TenantBranch,
    TenantListOptions, TenantOperationOptions, TenantPage, TenantPlacementLock, TenantTemplate,
    TenantsClient,
};
pub use transport::{HttpTransport, Method, Request, ReqwestTransport, Response};

/// Pinned contract version published by terrabase. Regenerate with
/// `make contracts`; CI fails on drift.
pub const CONTRACT_VERSION: &str = generated_contract::CONTRACT_VERSION;
