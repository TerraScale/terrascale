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

/// Request for `POST /api/v1/databases`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateDatabaseRequest {
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
        };
        request.validate()?;
        Ok(request)
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
    ) -> Result<DatabaseRecord> {
        request.validate()?;
        let request = request_with_options(
            "databases.create",
            Method::Post,
            format!("{}/databases", self.shared.pinned_base_url(None)?),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
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
        if !confirm {
            return Err(Error::new(
                ErrorKind::Validation,
                "confirm_required",
                "Database deletion requires confirm to be true.",
            ));
        }
        let mut request = Request {
            route_name: "databases.delete".to_owned(),
            method: Method::Delete,
            url: database_url(&self.shared, database_id)?,
            headers: Vec::new(),
            body: None,
        };
        request.headers.push((
            "x-terrascale-delete-confirmation".to_owned(),
            database_id.trim().to_owned(),
        ));
        // DELETE is a mutation too. Carry a deterministic key so a retry
        // cannot turn one user action into two deletes.
        request.headers.push((
            "idempotency-key".to_owned(),
            format!(
                "ts_database_delete_{}",
                encode_segment(database_id, "database_name")?
            ),
        ));
        self.shared.send_typed(request).await
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
    ) -> Result<IndexListResponse> {
        let request = Request {
            route_name: "collections.indexes.recommendations".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/indexes/recommendations",
                collection_url(&self.shared, database_id, collection)?
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
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct CreateExport {}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExportJob {
    pub export_id: String,
    pub database_id: String,
    pub status: String,
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
        _request: CreateExport,
        _project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<ExportJob> {
        let request = request_options(
            Request {
                route_name: "exports.create".to_owned(),
                method: Method::Post,
                url: format!("{}/exports", database_url(&self.shared, database_id)?),
                headers: Vec::new(),
                body: None,
            },
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Usage {
    pub database_id: String,
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

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct HealthResponse {
    pub region: String,
    pub healthy: bool,
}
impl<'de> Deserialize<'de> for HealthResponse {
    fn deserialize<D>(deserializer: D) -> std::result::Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let value = serde_json::Value::deserialize(deserializer)?;
        match value {
            serde_json::Value::Object(object) => {
                #[derive(Deserialize)]
                struct Wire {
                    region: String,
                    healthy: bool,
                }
                let wire = serde_json::from_value::<Wire>(serde_json::Value::Object(object))
                    .map_err(serde::de::Error::custom)?;
                Ok(Self {
                    region: wire.region,
                    healthy: wire.healthy,
                })
            }
            serde_json::Value::Array(_) => Ok(Self {
                region: String::new(),
                healthy: false,
            }),
            _ => Err(serde::de::Error::custom(
                "health response must be an object",
            )),
        }
    }
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
