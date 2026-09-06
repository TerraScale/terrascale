//! Typed management operations from TerraBase's published REST contract.
//!
//! Management is deliberately database-scoped. The public contract has no
//! project prefix for database, branch, collection, backup, export, or usage
//! routes; those resources are addressed by their database name.

use crate::client::SharedClient;
use crate::error::{Error, ErrorKind, Result};
use crate::regions::RegionCode;
use crate::schema::CollectionSchema;
use crate::transport::{Method, Request};
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};

const DEFAULT_PAGE_SIZE: u32 = 100;
const MAX_PAGE_SIZE: u32 = 1_000;

/// Bounded offset pagination used by database and list methods.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PageOptions {
    pub skip: u32,
    pub take: u32,
}

impl Default for PageOptions {
    fn default() -> Self {
        Self {
            skip: 0,
            take: DEFAULT_PAGE_SIZE,
        }
    }
}
impl PageOptions {
    pub fn new(skip: u32, take: u32) -> Result<Self> {
        let page = Self { skip, take };
        page.validate()?;
        Ok(page)
    }
    fn validate(self) -> Result<()> {
        if self.take == 0 || self.take > MAX_PAGE_SIZE {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_page_size",
                format!("take must be between 1 and {MAX_PAGE_SIZE}."),
            ));
        }
        Ok(())
    }
}

/// Cooperative cancellation checked before and during a management request.
#[derive(Debug, Clone, Default)]
pub struct CancellationToken(Arc<AtomicBool>);
impl CancellationToken {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }
    pub fn cancel(&self) {
        self.0.store(true, Ordering::Release);
    }
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::Acquire)
    }
}

/// Options for retry-safe management mutations.
#[derive(Debug, Clone, Default)]
pub struct OperationOptions {
    pub idempotency_key: Option<String>,
    pub cancellation: Option<CancellationToken>,
}
impl OperationOptions {
    fn validate(&self) -> Result<()> {
        if let Some(key) = &self.idempotency_key {
            validate_bounded(key, "idempotency_key", 128)?;
        }
        if self
            .cancellation
            .as_ref()
            .is_some_and(CancellationToken::is_cancelled)
        {
            return Err(Error::new(
                ErrorKind::Validation,
                "cancelled",
                "The management operation was cancelled before it was sent.",
            ));
        }
        Ok(())
    }
}

fn validate_non_empty(value: &str, field: &str) -> Result<()> {
    if value.trim().is_empty() {
        return Err(Error::new(
            ErrorKind::Validation,
            format!("invalid_{field}"),
            format!("{field} cannot be empty."),
        ));
    }
    Ok(())
}
fn validate_bounded(value: &str, field: &str, max: usize) -> Result<()> {
    validate_non_empty(value, field)?;
    if value.trim().len() > max {
        return Err(Error::new(
            ErrorKind::Validation,
            format!("invalid_{field}"),
            format!("{field} must not exceed {max} characters."),
        ));
    }
    Ok(())
}
fn encode_segment(value: &str, field: &str) -> Result<String> {
    validate_non_empty(value, field)?;
    Ok(value
        .trim()
        .as_bytes()
        .iter()
        .flat_map(|byte| {
            if byte.is_ascii_alphanumeric() || b"-_.~".contains(byte) {
                vec![char::from(*byte).to_string()]
            } else {
                vec![format!("%{byte:02X}")]
            }
        })
        .collect())
}
fn request_options(mut request: Request, options: &OperationOptions) -> Result<Request> {
    options.validate()?;
    if let Some(key) = &options.idempotency_key {
        request
            .headers
            .push(("idempotency-key".to_owned(), key.trim().to_owned()));
    }
    Ok(request)
}
fn request_with_options<T: Serialize>(
    route: &str,
    method: Method,
    url: String,
    body: &T,
    options: &OperationOptions,
) -> Result<Request> {
    request_options(Request::json(route, method, url, body)?, options)
}
async fn send_with_options<T: DeserializeOwned>(
    shared: &SharedClient,
    request: Request,
    options: &OperationOptions,
) -> Result<T> {
    options.validate()?;
    let Some(token) = options.cancellation.clone() else {
        return shared.send_typed(request).await;
    };
    tokio::select! {
        result = shared.send_typed(request) => result,
        () = wait_cancelled(token) => Err(Error::new(ErrorKind::Transport, "cancelled", "The management operation was cancelled.")),
    }
}
async fn wait_cancelled(token: CancellationToken) {
    while !token.is_cancelled() {
        tokio::task::yield_now().await;
    }
}
fn database_url(shared: &SharedClient, name: &str) -> Result<String> {
    Ok(format!(
        "{}/databases/{}",
        shared.pinned_base_url(None)?,
        encode_segment(name, "database_name")?
    ))
}
fn collection_url(shared: &SharedClient, database: &str, collection: &str) -> Result<String> {
    Ok(format!(
        "{}/collections/{}",
        database_url(shared, database)?,
        encode_segment(collection, "collection")?
    ))
}

// ---------------------------------------------------------------------------
// Databases and placement
// ---------------------------------------------------------------------------

/// Direct TerraBase database metadata and placement record.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DatabaseRecord {
    pub name: String,
    #[serde(default)]
    pub placement: String,
    #[serde(default)]
    pub residency: String,
    #[serde(default)]
    pub deleted: bool,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub parent: Option<String>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DatabaseListResponse {
    pub databases: Vec<DatabaseRecord>,
}
/// Compatibility name from the pre-contract Rust draft.
pub type ProjectDatabase = DatabaseRecord;

/// Database creation distinguishes a created database from accepted provisioning.
#[derive(Debug, Clone, PartialEq)]
pub enum CreateDatabaseResult {
    Created(DatabaseRecord),
    Accepted(ProvisioningOperation),
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProvisioningOperation {
    /// Deployment environment owning the operation.
    pub environment: String,
    /// Authoritatively bound tenant identity.
    pub tenant_id: String,
    /// Authoritatively bound project identity.
    pub project_id: String,
    pub operation_id: String,
    pub database_id: String,
    /// Parent identity of a provisioned branch, when applicable.
    pub parent_database_id: Option<String>,
    pub key_version: String,
    pub name: String,
    pub regions: Vec<String>,
    pub residency: String,
    pub state: String,
}

/// Request for `POST /api/v1/databases`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateDatabaseRequest {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub operation_id: Option<String>,
    pub name: String,
    pub placement: Vec<RegionCode>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub residency: Option<String>,
}
/// Compatibility request retained only to produce a useful validation error;
/// placement is required by the current contract.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateDatabase {
    pub name: String,
}
pub type CreateDatabaseWithPlacement = CreateDatabaseRequest;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlacementLock {
    EuOnly,
    BrOnly,
    Regions(Vec<RegionCode>),
}
impl PlacementLock {
    fn wire_value(&self) -> Result<String> {
        match self {
            Self::EuOnly => Ok("eu-only".to_owned()),
            Self::BrOnly => Ok("br-only".to_owned()),
            Self::Regions(regions) if regions.is_empty() => Err(Error::new(
                ErrorKind::Validation,
                "invalid_residency",
                "a placement lock must contain at least one region.",
            )),
            Self::Regions(regions) => Ok(regions
                .iter()
                .map(|r| r.as_str())
                .collect::<Vec<_>>()
                .join(",")),
        }
    }
}
impl CreateDatabaseRequest {
    pub fn new(name: impl Into<String>, placement: Vec<RegionCode>) -> Result<Self> {
        let request = Self {
            name: name.into(),
            placement,
            residency: None,
            operation_id: None,
        };
        request.validate()?;
        Ok(request)
    }
    pub fn operation_id(mut self, operation_id: impl Into<String>) -> Result<Self> {
        self.operation_id = Some(operation_id.into());
        self.validate()?;
        Ok(self)
    }
    pub fn residency(mut self, residency: impl Into<String>) -> Result<Self> {
        self.residency = Some(residency.into());
        self.validate()?;
        Ok(self)
    }
    pub fn placement_lock(mut self, lock: PlacementLock) -> Result<Self> {
        self.residency = Some(lock.wire_value()?);
        self.validate()?;
        Ok(self)
    }
    fn validate(&self) -> Result<()> {
        if let Some(operation_id) = &self.operation_id {
            validate_non_empty(operation_id, "operation_id")?;
            if operation_id.len() > 128 {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_operation_id",
                    "operation_id must be at most 128 bytes.",
                ));
            }
        }
        validate_non_empty(&self.name, "database_name")?;
        if self.placement.is_empty() {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_placement",
                "placement must contain at least one region.",
            ));
        }
        let mut seen = Vec::new();
        for region in &self.placement {
            if seen.contains(region) {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_placement",
                    "placement cannot contain duplicate regions.",
                ));
            }
            seen.push(*region);
        }
        if let Some(residency) = &self.residency {
            validate_residency(residency, &self.placement)?;
        }
        Ok(())
    }
}
fn validate_residency(value: &str, placement: &[RegionCode]) -> Result<()> {
    let value = value.trim();
    validate_non_empty(value, "residency")?;
    let allowed = match value {
        "eu-only" => vec![RegionCode::Fra],
        "br-only" => vec![RegionCode::Gru],
        _ => value
            .split(',')
            .map(|r| RegionCode::parse(r, "residency"))
            .collect::<Result<Vec<_>>>()?,
    };
    if allowed.is_empty() || placement.iter().any(|r| !allowed.contains(r)) {
        return Err(Error::new(
            ErrorKind::Validation,
            "residency_violation",
            "every placement region must satisfy the residency lock.",
        ));
    }
    Ok(())
}

#[derive(Debug, Clone)]
pub struct Databases {
    shared: SharedClient,
}
impl Databases {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn list(&self, project_id: Option<&str>) -> Result<DatabaseListResponse> {
        self.list_page(project_id, PageOptions::default()).await
    }
    pub async fn list_page(
        &self,
        _project_id: Option<&str>,
        page: PageOptions,
    ) -> Result<DatabaseListResponse> {
        // The published operation has no query parameters. Keep this method
        // as a source-compatible validation shim for the draft API.
        page.validate()?;
        let request = Request {
            route_name: "databases.list".to_owned(),
            method: Method::Get,
            url: format!("{}/databases", self.shared.pinned_base_url(None)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    pub async fn create(
        &self,
        _request: CreateDatabase,
        _project_id: Option<&str>,
    ) -> Result<DatabaseRecord> {
        Err(Error::new(
            ErrorKind::Validation,
            "placement_required",
            "database creation requires an explicit placement; use CreateDatabaseRequest.",
        ))
    }
    pub async fn create_with_placement(
        &self,
        request: CreateDatabaseRequest,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<CreateDatabaseResult> {
        request.validate()?;
        let request = request_with_options(
            "databases.create",
            Method::Post,
            format!("{}/databases", self.shared.pinned_base_url(None)?),
            &request,
            &options,
        )?;
        let response = if let Some(token) = options.cancellation.clone() {
            tokio::select! {
                response = self.shared.send_response(request) => response?,
                () = wait_cancelled(token) => return Err(Error::new(ErrorKind::Transport, "cancelled", "The management operation was cancelled.")),
            }
        } else {
            self.shared.send_response(request).await?
        };
        match response.status {
            201 => response.json_body().map(CreateDatabaseResult::Created),
            202 => response.json_body().map(CreateDatabaseResult::Accepted),
            _ => Err(Error::new(
                ErrorKind::Validation,
                "invalid_create_response",
                "Database creation must return HTTP 201 or 202.",
            )),
        }
    }
    pub async fn get(
        &self,
        database_id: &str,
        _project_id: Option<&str>,
    ) -> Result<DatabaseRecord> {
        let request = Request {
            route_name: "databases.get".to_owned(),
            method: Method::Get,
            url: database_url(&self.shared, database_id)?,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    pub async fn delete(
        &self,
        database_id: &str,
        confirm: bool,
        _project_id: Option<&str>,
    ) -> Result<DatabaseRecord> {
        let options = OperationOptions {
            idempotency_key: Some(format!(
                "ts_database_delete_{}",
                encode_segment(database_id, "database_name")?
            )),
            cancellation: None,
        };
        self.delete_with_options(database_id, confirm, _project_id, options)
            .await
    }

    /// Soft-delete a database with explicit retry and cancellation controls.
    pub async fn delete_with_options(
        &self,
        database_id: &str,
        confirm: bool,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<DatabaseRecord> {
        if !confirm {
            return Err(Error::new(
                ErrorKind::Validation,
                "confirm_required",
                "Database deletion requires confirm to be true.",
            ));
        }
        let request = request_options(
            Request {
                route_name: "databases.delete".to_owned(),
                method: Method::Delete,
                url: database_url(&self.shared, database_id)?,
                headers: Vec::new(),
                body: None,
            },
            &options,
        )?;
        let mut request = request;
        request.headers.push((
            "x-terrascale-delete-confirmation".to_owned(),
            database_id.trim().to_owned(),
        ));
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn restore(
        &self,
        database_id: &str,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<DatabaseRecord> {
        let request = request_with_options(
            "databases.restore",
            Method::Post,
            format!("{}/restore", database_url(&self.shared, database_id)?),
            &EmptyBody {},
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}

// ---------------------------------------------------------------------------
// Branches, collections, indexes, and collection schemas
// ---------------------------------------------------------------------------

pub type DatabaseBranch = DatabaseRecord;
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RestoreRequest {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<i64>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BranchListResponse {
    pub branches: Vec<DatabaseRecord>,
}
#[derive(Debug, Clone)]
pub struct Branches {
    shared: SharedClient,
}
impl Branches {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn create(
        &self,
        database_id: &str,
        request: RestoreRequest,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<DatabaseRecord> {
        validate_restore(&request)?;
        let request = request_with_options(
            "branches.create",
            Method::Post,
            format!("{}/branches", database_url(&self.shared, database_id)?),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn list(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<BranchListResponse> {
        self.list_page(database_id, project_id, PageOptions::default())
            .await
    }
    pub async fn list_page(
        &self,
        database_id: &str,
        _project_id: Option<&str>,
        page: PageOptions,
    ) -> Result<BranchListResponse> {
        page.validate()?;
        let request = Request {
            route_name: "branches.list".to_owned(),
            method: Method::Get,
            url: format!("{}/branches", database_url(&self.shared, database_id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}
fn validate_restore(request: &RestoreRequest) -> Result<()> {
    validate_non_empty(&request.name, "database_name")?;
    if request.at.is_some_and(|at| at < 0) {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_restore_point",
            "at must be a non-negative Unix timestamp.",
        ));
    }
    Ok(())
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CollectionsResponse {
    pub collections: Vec<String>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IndexDescriptor {
    pub field: String,
    pub state: String,
    pub backfill_remaining: u64,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IndexListResponse {
    pub indexes: Vec<IndexDescriptor>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IndexCreatedResponse {
    pub field: String,
    pub state: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateIndexRequest {
    pub field: String,
}
#[derive(Debug, Clone)]
pub struct Collections {
    shared: SharedClient,
}
impl Collections {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn list(
        &self,
        database_id: &str,
        _project_id: Option<&str>,
    ) -> Result<CollectionsResponse> {
        let request = Request {
            route_name: "collections.list".to_owned(),
            method: Method::Get,
            url: format!("{}/collections", database_url(&self.shared, database_id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    pub async fn list_indexes(
        &self,
        database_id: &str,
        collection: &str,
        _project_id: Option<&str>,
    ) -> Result<IndexListResponse> {
        let request = Request {
            route_name: "collections.indexes.list".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/indexes",
                collection_url(&self.shared, database_id, collection)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    pub async fn create_index(
        &self,
        database_id: &str,
        collection: &str,
        request: CreateIndexRequest,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<IndexCreatedResponse> {
        validate_non_empty(&request.field, "index_field")?;
        let request = request_with_options(
            "collections.indexes.create",
            Method::Post,
            format!(
                "{}/indexes",
                collection_url(&self.shared, database_id, collection)?
            ),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn delete_index(
        &self,
        database_id: &str,
        collection: &str,
        field: &str,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<()> {
        let request = request_options(
            Request {
                route_name: "collections.indexes.delete".to_owned(),
                method: Method::Delete,
                url: format!(
                    "{}/indexes/{}",
                    collection_url(&self.shared, database_id, collection)?,
                    encode_segment(field, "index_field")?
                ),
                headers: Vec::new(),
                body: None,
            },
            &options,
        )?;
        self.shared.send_empty(request).await
    }
    pub async fn index_recommendations(
        &self,
        database_id: &str,
        collection: &str,
        _project_id: Option<&str>,
    ) -> Result<RecommendationList> {
        self.index_recommendations_with_limit(database_id, collection, None)
            .await
    }
    pub async fn index_recommendations_with_limit(
        &self,
        database_id: &str,
        collection: &str,
        limit: Option<u16>,
    ) -> Result<RecommendationList> {
        if limit.is_some_and(|value| !(1..=64).contains(&value)) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_limit",
                "limit must be between 1 and 64.",
            ));
        }
        let request = Request {
            route_name: "collections.indexes.recommendations".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/indexes/recommendations{}",
                collection_url(&self.shared, database_id, collection)?,
                limit
                    .map(|value| format!("?limit={value}"))
                    .unwrap_or_default()
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

/// Schema GET and PUT return one collection schema, never a whole-database wrapper.
#[derive(Debug, Clone)]
pub struct Schemas {
    shared: SharedClient,
}
impl Schemas {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn get(
        &self,
        database_id: &str,
        collection: &str,
        _project_id: Option<&str>,
    ) -> Result<CollectionSchema> {
        let request = Request {
            route_name: "schemas.get".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/schema",
                collection_url(&self.shared, database_id, collection)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    pub async fn put(
        &self,
        database_id: &str,
        collection: &str,
        schema: CollectionSchema,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<CollectionSchema> {
        validate_schema(&schema, collection)?;
        let request = request_with_options(
            "schemas.put",
            Method::Put,
            format!(
                "{}/schema",
                collection_url(&self.shared, database_id, collection)?
            ),
            &schema,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn update(
        &self,
        database_id: &str,
        collection: &str,
        schema: CollectionSchema,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<CollectionSchema> {
        self.put(database_id, collection, schema, project_id, options)
            .await
    }
}
fn validate_schema(schema: &CollectionSchema, collection: &str) -> Result<()> {
    validate_non_empty(collection, "collection")?;
    if schema.name != collection {
        return Err(Error::new(
            ErrorKind::Validation,
            "schema_collection_mismatch",
            "schema.name must match the collection path.",
        ));
    }
    schema
        .validate()
        .map_err(|e| Error::new(ErrorKind::Validation, "invalid_schema", e.to_string()))
}

// ---------------------------------------------------------------------------
// Backups and pending surfaces
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BackupStatusResponse {
    pub ready: bool,
    pub streaming: bool,
}
pub type BackupStatus = BackupStatusResponse;
#[derive(Debug, Clone)]
pub struct Backups {
    shared: SharedClient,
}
impl Backups {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn list(
        &self,
        database_id: &str,
        _project_id: Option<&str>,
    ) -> Result<BackupStatusResponse> {
        let request = Request {
            route_name: "backups.list".to_owned(),
            method: Method::Get,
            url: format!("{}/backups", database_url(&self.shared, database_id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    pub async fn restore(
        &self,
        database_id: &str,
        request: RestoreRequest,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<DatabaseRecord> {
        validate_restore(&request)?;
        let request = request_with_options(
            "backups.restore",
            Method::Post,
            format!(
                "{}/backups/restore",
                database_url(&self.shared, database_id)?
            ),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateExport {
    pub idempotency_key: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ResumeExport {
    pub resume_cursor: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExportAccepted {
    pub job_id: String,
    pub status_path: String,
    pub resume_cursor: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub enum ExportJobStatus {
    Accepted,
    Running,
    Cancelled,
    Complete,
    Failed,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExportArtifact {
    pub path: String,
    pub length: u64,
    pub checksum: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExportStatus {
    pub created_at_millis: u64,
    pub phase: ExportPhase,
    pub cancellation: ExportCancellation,
    pub pages: u64,
    pub job_id: String,
    pub status: ExportJobStatus,
    pub records: u64,
    pub estimated_records: Option<u64>,
    pub progress_basis_points: u16,
    pub resume_cursor: String,
    pub manifest: Option<ExportArtifact>,
    pub error: Option<String>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub enum ExportPhase {
    Documents,
    History,
    Done,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ExportCancellation {
    Available,
    Requested,
    Complete,
    Unavailable,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExportList {
    pub exports: Vec<ExportStatus>,
    pub next_cursor: Option<String>,
}
#[derive(Debug, Clone)]
pub struct Exports {
    shared: SharedClient,
}
impl Exports {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn create(
        &self,
        database_id: &str,
        input: CreateExport,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<ExportAccepted> {
        validate_non_empty(&input.idempotency_key, "idempotency_key")?;
        let request = request_with_options(
            "exports.create",
            Method::Post,
            format!("{}/exports", database_url(&self.shared, database_id)?),
            &input,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn list(
        &self,
        database_id: &str,
        after: Option<&str>,
        limit: Option<u16>,
    ) -> Result<ExportList> {
        if limit.is_some_and(|limit| !(1..=50).contains(&limit)) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_limit",
                "limit must be between 1 and 50.",
            ));
        }
        let mut url = format!("{}/exports", database_url(&self.shared, database_id)?);
        let mut query = Vec::new();
        if let Some(after) = after {
            query.push(format!("after={}", encode_segment(after, "after")?));
        }
        if let Some(limit) = limit {
            query.push(format!("limit={limit}"));
        }
        if !query.is_empty() {
            url.push('?');
            url.push_str(&query.join("&"));
        }
        self.shared
            .send_typed(Request {
                route_name: "exports.list".into(),
                method: Method::Get,
                url,
                headers: vec![],
                body: None,
            })
            .await
    }
    pub async fn cancel(
        &self,
        database_id: &str,
        job_id: &str,
        options: OperationOptions,
    ) -> Result<ExportStatus> {
        let request = request_options(
            Request {
                route_name: "exports.cancel".into(),
                method: Method::Post,
                url: format!(
                    "{}/exports/{}/cancel",
                    database_url(&self.shared, database_id)?,
                    encode_segment(job_id, "job_id")?
                ),
                headers: vec![],
                body: None,
            },
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn get(&self, database_id: &str, job_id: &str) -> Result<ExportStatus> {
        self.shared
            .send_typed(Request {
                route_name: "exports.get".into(),
                method: Method::Get,
                url: format!(
                    "{}/exports/{}",
                    database_url(&self.shared, database_id)?,
                    encode_segment(job_id, "job_id")?
                ),
                headers: vec![],
                body: None,
            })
            .await
    }
    /// Download one export artifact without decoding or transforming its bytes.
    pub async fn download(&self, database_id: &str, job_id: &str, member: &str) -> Result<Vec<u8>> {
        self.shared
            .send_bytes(Request {
                route_name: "exports.download".into(),
                method: Method::Get,
                url: format!(
                    "{}/exports/{}/artifacts/{}",
                    database_url(&self.shared, database_id)?,
                    encode_segment(job_id, "job_id")?,
                    encode_segment(member, "member")?
                ),
                headers: vec![("accept".into(), "application/octet-stream".into())],
                body: None,
            })
            .await
    }
    pub async fn resume(
        &self,
        database_id: &str,
        job_id: &str,
        input: ResumeExport,
        options: OperationOptions,
    ) -> Result<ExportStatus> {
        validate_non_empty(&input.resume_cursor, "resume_cursor")?;
        let request = request_with_options(
            "exports.resume",
            Method::Post,
            format!(
                "{}/exports/{}/resume",
                database_url(&self.shared, database_id)?,
                encode_segment(job_id, "job_id")?
            ),
            &input,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Usage {
    /// End of the latest finalized storage interval, when available.
    pub storage_complete_through_millis: Option<u64>,
    pub tenant_id: String,
    pub database_id: String,
    pub snapshot_sequence: u64,
    pub meters: [MeterTotal; 2],
    pub reads: ReadAttribution,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BillableMeter {
    DocumentWriteUnits,
    StorageByteSeconds,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct MeterTotal {
    pub meter: BillableMeter,
    pub units: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ReadAttribution {
    pub total_reads: u64,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IndexRecommendation {
    pub candidate_id: String,
    pub collection: String,
    pub fields: Vec<String>,
    pub score: f64,
    pub maintenance_cost: u64,
    pub generation: u64,
    pub needs_descriptor: bool,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RecommendationList {
    pub generation: u64,
    pub truncated: bool,
    pub recommendations: Vec<IndexRecommendation>,
}
#[derive(Debug, Clone)]
pub struct UsageClient {
    shared: SharedClient,
}
impl UsageClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn get_database(
        &self,
        database_id: &str,
        _project_id: Option<&str>,
    ) -> Result<Usage> {
        let request = Request {
            route_name: "usage.database.get".to_owned(),
            method: Method::Get,
            url: format!("{}/usage", database_url(&self.shared, database_id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

// ---------------------------------------------------------------------------
// Member health and root
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct HealthResponse {
    pub region: String,
    pub healthy: bool,
}
#[derive(Debug, Clone)]
pub struct Regions {
    shared: SharedClient,
}
impl Regions {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn list(&self) -> Result<HealthResponse> {
        let request = Request {
            route_name: "regions.list".to_owned(),
            method: Method::Get,
            url: format!("{}/regions", self.shared.global_base_url()),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

#[derive(Debug, Clone)]
pub struct ManagementClient {
    shared: SharedClient,
}
impl ManagementClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    #[must_use]
    pub fn databases(&self) -> Databases {
        Databases::new(self.shared.clone())
    }
    #[must_use]
    pub fn branches(&self) -> Branches {
        Branches::new(self.shared.clone())
    }
    #[must_use]
    pub fn collections(&self) -> Collections {
        Collections::new(self.shared.clone())
    }
    #[must_use]
    pub fn schemas(&self) -> Schemas {
        Schemas::new(self.shared.clone())
    }
    #[must_use]
    pub fn backups(&self) -> Backups {
        Backups::new(self.shared.clone())
    }
    #[must_use]
    pub fn exports(&self) -> Exports {
        Exports::new(self.shared.clone())
    }
    #[must_use]
    pub fn usage(&self) -> UsageClient {
        UsageClient::new(self.shared.clone())
    }
    #[must_use]
    pub fn auth_persistence(&self) -> AuthPersistence {
        AuthPersistence {
            shared: self.shared.clone(),
        }
    }
    #[must_use]
    pub fn operations(&self) -> Operations {
        Operations {
            shared: self.shared.clone(),
        }
    }
    #[must_use]
    pub fn development(&self) -> Development {
        Development {
            shared: self.shared.clone(),
        }
    }
    #[must_use]
    pub fn webhooks(&self) -> Webhooks {
        Webhooks::new(self.shared.clone())
    }
    #[must_use]
    pub fn regions(&self) -> Regions {
        Regions::new(self.shared.clone())
    }
}
#[derive(Debug, Clone, Copy, Serialize)]
struct EmptyBody {}

// Names exported by the original crate root remain as data-only aliases while
// their project-prefixed operations are gone. Keeping these aliases avoids a
// needless source break for code that only used the shared response models;
// no route below exposes the withdrawn project management behavior.
pub type DatabaseSchema = CollectionSchema;
pub type DeletedDatabase = DatabaseRecord;
pub type Backup = BackupStatusResponse;
pub type RegionSummary = HealthResponse;
pub type UsageSummary = Usage;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateBranch {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<i64>,
}
pub type RestoreAt = RestoreRequest;
pub type RestoreJob = DatabaseRecord;
pub type RestorePoint = DatabaseRecord;
pub type StartRestore = RestoreRequest;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BackupPolicy {
    pub full_backup_cron: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub incremental_backup_cron: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub retention_days: Option<u32>,
    #[serde(default)]
    pub disabled: bool,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BackupPlan {
    pub action: String,
    pub request_digest: String,
    pub confirmation_token: String,
    pub expires_at: String,
}
pub type PlanBackupRequest = RestoreRequest;
pub type ExecuteBackupRequest = RestoreRequest;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ApiKey {
    pub key_id: String,
    pub name: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ApiKeyPage {
    pub api_keys: Vec<ApiKey>,
    pub next_cursor: Option<String>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateApiKey {
    pub name: String,
}
pub type CreatedApiKey = ApiKey;
pub type RotatedApiKey = ApiKey;
pub type RevokedApiKey = ApiKey;
#[derive(Debug, Clone)]
pub struct ApiKeys;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum TransferFormat {
    Jsonl,
    Csv,
    MongoJson,
    CosmosJson,
    DynamoDb,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum UsageRange {
    Hours24,
    Days7,
    Days30,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UsagePoint {
    pub start: String,
    pub end: String,
    pub value: u64,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UsageWorkload {
    pub project_id: String,
    pub range: UsageRange,
    pub points: Vec<UsagePoint>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateWebhook {
    pub endpoint: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct WebhookEndpoint {
    pub id: String,
    pub endpoint: String,
    pub created_at: u64,
    pub deleted: bool,
}
/// One-time signing material. Decode the unpadded base64url secret before verification.
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct WebhookSecretResponse {
    #[serde(flatten)]
    pub webhook: WebhookEndpoint,
    pub secret: String,
}
impl std::fmt::Debug for WebhookSecretResponse {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("WebhookSecretResponse")
            .field("webhook", &self.webhook)
            .field("secret", &"[REDACTED]")
            .finish()
    }
}
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct RotateWebhookSecret {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub overlap_seconds: Option<u16>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct WebhookList {
    pub webhooks: Vec<WebhookEndpoint>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DeliveryRecord {
    pub id: String,
    pub event_id: String,
    pub revision: u64,
    pub retry_started_at: u64,
    pub attempts: u32,
    pub next_attempt_at: u64,
    pub status: Option<u16>,
    pub delivered: bool,
    pub terminal: bool,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DeliveryLog {
    pub deliveries: Vec<DeliveryRecord>,
}
#[derive(Debug, Clone)]
pub struct Webhooks {
    shared: SharedClient,
}
impl Webhooks {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    fn url(&self, database_id: &str, id: &str) -> Result<String> {
        Ok(format!(
            "{}/webhooks/{}",
            database_url(&self.shared, database_id)?,
            encode_segment(id, "webhook_id")?
        ))
    }
    pub async fn list(&self, database_id: &str) -> Result<WebhookList> {
        self.shared
            .send_typed(Request {
                route_name: "webhooks.list".into(),
                method: Method::Get,
                url: format!("{}/webhooks", database_url(&self.shared, database_id)?),
                headers: vec![],
                body: None,
            })
            .await
    }
    pub async fn create(
        &self,
        database_id: &str,
        input: CreateWebhook,
        options: OperationOptions,
    ) -> Result<WebhookSecretResponse> {
        validate_non_empty(&input.endpoint, "endpoint")?;
        let request = request_with_options(
            "webhooks.create",
            Method::Post,
            format!("{}/webhooks", database_url(&self.shared, database_id)?),
            &input,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn rotate_secret(
        &self,
        database_id: &str,
        id: &str,
        input: RotateWebhookSecret,
        options: OperationOptions,
    ) -> Result<WebhookSecretResponse> {
        if input.overlap_seconds.is_some_and(|value| value > 3600) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_overlap",
                "overlap_seconds must be at most 3600.",
            ));
        }
        let request = request_with_options(
            "webhooks.rotate_secret",
            Method::Post,
            format!("{}/rotate-secret", self.url(database_id, id)?),
            &input,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn delete(
        &self,
        database_id: &str,
        id: &str,
        options: OperationOptions,
    ) -> Result<WebhookEndpoint> {
        let request = request_options(
            Request {
                route_name: "webhooks.delete".into(),
                method: Method::Delete,
                url: self.url(database_id, id)?,
                headers: vec![],
                body: None,
            },
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn list_deliveries(&self, database_id: &str, id: &str) -> Result<DeliveryLog> {
        self.shared
            .send_typed(Request {
                route_name: "webhooks.deliveries.list".into(),
                method: Method::Get,
                url: format!("{}/deliveries", self.url(database_id, id)?),
                headers: vec![],
                body: None,
            })
            .await
    }
    pub async fn replay(
        &self,
        database_id: &str,
        id: &str,
        delivery_id: &str,
        options: OperationOptions,
    ) -> Result<DeliveryRecord> {
        let request = request_options(
            Request {
                route_name: "webhooks.replay".into(),
                method: Method::Post,
                url: format!(
                    "{}/deliveries/{}/replay",
                    self.url(database_id, id)?,
                    encode_segment(delivery_id, "delivery_id")?
                ),
                headers: vec![],
                body: None,
            },
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DevelopmentFaultKind {
    Unavailable,
    Latency,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DevelopmentFaultRequest {
    pub confirm_branch: String,
    pub fault: DevelopmentFaultKind,
    pub duration_ms: u64,
    pub max_operations: u16,
    pub delay_ms: u64,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DevelopmentFaultResponse {
    pub active: bool,
    pub expires_at_millis: u64,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DevelopmentLogEntry {
    pub sequence: u64,
    pub action: String,
    pub at_millis: u64,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DevelopmentLogResponse {
    pub entries: Vec<DevelopmentLogEntry>,
    pub truncated: bool,
}
#[derive(Debug, Clone)]
pub struct Development {
    shared: SharedClient,
}
impl Development {
    pub async fn set_fault(
        &self,
        database_id: &str,
        input: DevelopmentFaultRequest,
        options: OperationOptions,
    ) -> Result<DevelopmentFaultResponse> {
        if input.confirm_branch != database_id
            || !(1..=30_000).contains(&input.duration_ms)
            || !(1..=100).contains(&input.max_operations)
            || match input.fault {
                DevelopmentFaultKind::Latency => !(1..=1000).contains(&input.delay_ms),
                DevelopmentFaultKind::Unavailable => input.delay_ms != 0,
            }
        {
            return Err(Error::new(ErrorKind::Validation, "invalid_development_fault", "Confirm the branch name and use duration_ms 1..30000, max_operations 1..100, and delay_ms 1..1000 for latency or zero for unavailable."));
        }
        let request = request_with_options(
            "development.set_fault",
            Method::Post,
            format!("{}/dev/faults", database_url(&self.shared, database_id)?),
            &input,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn clear_fault(
        &self,
        database_id: &str,
        options: OperationOptions,
    ) -> Result<DevelopmentFaultResponse> {
        let request = request_options(
            Request {
                route_name: "development.clear_fault".into(),
                method: Method::Delete,
                url: format!("{}/dev/faults", database_url(&self.shared, database_id)?),
                headers: vec![],
                body: None,
            },
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    pub async fn logs(
        &self,
        database_id: &str,
        limit: Option<u16>,
    ) -> Result<DevelopmentLogResponse> {
        if limit.is_some_and(|limit| !(1..=100).contains(&limit)) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_limit",
                "limit must be between 1 and 100.",
            ));
        }
        self.shared
            .send_typed(Request {
                route_name: "development.logs".into(),
                method: Method::Get,
                url: format!(
                    "{}/dev/logs{}",
                    database_url(&self.shared, database_id)?,
                    limit
                        .map(|limit| format!("?limit={limit}"))
                        .unwrap_or_default()
                ),
                headers: vec![],
                body: None,
            })
            .await
    }
}

#[derive(Debug, Clone)]
pub struct Operations {
    shared: SharedClient,
}
impl Operations {
    /// Read the durable provisioning operation without resubmitting creation.
    pub async fn get(&self, operation_id: &str) -> Result<ProvisioningOperation> {
        self.shared
            .send_typed(Request {
                route_name: "operations.get".into(),
                method: Method::Get,
                url: format!(
                    "{}/operations/{}",
                    self.shared.pinned_base_url(None)?,
                    encode_segment(operation_id, "operation_id")?
                ),
                headers: vec![],
                body: None,
            })
            .await
    }
}

pub type AuthRecord = std::collections::BTreeMap<String, serde_json::Value>;
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthUniqueInsert {
    pub model: String,
    pub record: AuthRecord,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthConditionalUpdate {
    pub model: String,
    pub id: String,
    pub expected: AuthRecord,
    pub update: AuthRecord,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthConsume {
    pub model: String,
    pub id: String,
    pub expected: AuthRecord,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthUniqueIndex {
    pub model: String,
    pub fields: Vec<String>,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthTransactionIdentity {
    pub transaction_id: String,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthResult {
    pub record: Option<AuthRecord>,
    pub matched: bool,
    pub records: Option<Vec<AuthRecord>>,
    pub transaction_id: Option<String>,
    pub expires_at_ms: Option<u64>,
}
#[derive(Debug, Clone, Copy, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AuthOperator {
    #[default]
    Eq,
    Ne,
    Lt,
    Lte,
    Gt,
    Gte,
    In,
    NotIn,
    Contains,
    StartsWith,
    EndsWith,
}
#[derive(Debug, Clone, Copy, Default, PartialEq, Serialize, Deserialize)]
pub enum AuthConnector {
    #[default]
    AND,
    OR,
}
#[derive(Debug, Clone, Copy, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum AuthMode {
    #[default]
    Sensitive,
    Insensitive,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthPredicate {
    pub field: String,
    pub value: serde_json::Value,
    #[serde(default)]
    pub operator: AuthOperator,
    #[serde(default)]
    pub connector: AuthConnector,
    #[serde(default)]
    pub mode: AuthMode,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthPageQuery {
    pub model: String,
    pub predicates: Vec<AuthPredicate>,
    pub offset: u32,
    pub limit: u32,
}
/// Every auth mutation requires caller-owned durable identity.
#[derive(Debug, Clone)]
pub struct AuthOperationOptions {
    pub idempotency_key: String,
    pub transaction_id: Option<String>,
}
#[derive(Debug, Clone)]
pub struct AuthPersistence {
    shared: SharedClient,
}
impl AuthPersistence {
    async fn post(
        &self,
        database_id: &str,
        operation: &str,
        input: &impl Serialize,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        crate::client::validate_idempotency_key(&options.idempotency_key)?;
        let mut request = request_with_options(
            &format!("auth.{operation}"),
            Method::Post,
            format!(
                "{}/auth/{operation}",
                database_url(&self.shared, database_id)?
            ),
            input,
            &OperationOptions {
                idempotency_key: Some(options.idempotency_key),
                cancellation: None,
            },
        )?;
        if let Some(transaction) = options.transaction_id {
            if transaction.is_empty()
                || transaction.len() > 128
                || !transaction
                    .bytes()
                    .all(|byte| (0x21..=0x7e).contains(&byte))
            {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_transaction_identity",
                    "transaction_id must contain 1 through 128 ASCII header bytes.",
                ));
            }
            request
                .headers
                .push(("x-terrascale-auth-transaction".into(), transaction));
        }
        self.shared.send_typed(request).await
    }
    pub async fn insert_unique(
        &self,
        database_id: &str,
        input: AuthUniqueInsert,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "insertUnique", &input, options)
            .await
    }
    pub async fn compare_and_swap(
        &self,
        database_id: &str,
        input: AuthConditionalUpdate,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "compareAndSwap", &input, options)
            .await
    }
    pub async fn consume_one(
        &self,
        database_id: &str,
        input: AuthConsume,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "consumeOne", &input, options).await
    }
    pub async fn index(
        &self,
        database_id: &str,
        input: AuthUniqueIndex,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "index", &input, options).await
    }
    pub async fn find_page(
        &self,
        database_id: &str,
        input: AuthPageQuery,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "findPage", &input, options).await
    }
    pub async fn find_one(&self, database_id: &str, model: &str, id: &str) -> Result<AuthResult> {
        self.shared
            .send_typed(Request {
                route_name: "auth.findOne".into(),
                method: Method::Get,
                url: format!(
                    "{}/auth/records/{}/{}",
                    database_url(&self.shared, database_id)?,
                    encode_segment(model, "model")?,
                    encode_segment(id, "id")?
                ),
                headers: vec![],
                body: None,
            })
            .await
    }
    pub async fn begin_transaction(
        &self,
        database_id: &str,
        input: AuthTransactionIdentity,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "transactions/begin", &input, options)
            .await
    }
    pub async fn commit_transaction(
        &self,
        database_id: &str,
        input: AuthTransactionIdentity,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "transactions/commit", &input, options)
            .await
    }
    pub async fn rollback_transaction(
        &self,
        database_id: &str,
        input: AuthTransactionIdentity,
        options: AuthOperationOptions,
    ) -> Result<AuthResult> {
        self.post(database_id, "transactions/rollback", &input, options)
            .await
    }
}
