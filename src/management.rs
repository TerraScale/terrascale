//! Typed management operations from the published TerraScale REST contract.
//!
//! This module deliberately contains request and response types for each
//! management family. Operations that the public contract does not publish
//! are not exposed here.

use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};

use serde::{de::DeserializeOwned, Deserialize, Serialize};

use crate::client::SharedClient;
use crate::error::{Error, ErrorKind, Result};
use crate::regions::RegionCode;
use crate::schema::SchemaDocument;
use crate::transport::{Method, Request};

const DEFAULT_PAGE_SIZE: u32 = 100;
const MAX_PAGE_SIZE: u32 = 1_000;

/// A bounded page request used by list operations.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PageOptions {
    /// Number of records to skip.
    pub skip: u32,
    /// Maximum number of records to return.
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
    /// Construct and validate a page request.
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

/// A cooperative cancellation signal for long-running management requests.
///
/// Cancellation is checked before a request is admitted to the transport.
/// Cancellation after admission is represented by the server operation's
/// status because the shared HTTP transport owns the in-flight request.
#[derive(Debug, Clone, Default)]
pub struct CancellationToken(Arc<AtomicBool>);

impl CancellationToken {
    /// Create a new, non-cancelled token.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }
    /// Request cancellation.
    pub fn cancel(&self) {
        self.0.store(true, Ordering::Release);
    }
    /// Report whether cancellation was requested.
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::Acquire)
    }
}

/// Options shared by idempotent management operations.
#[derive(Debug, Clone, Default)]
pub struct OperationOptions {
    /// Caller-supplied idempotency key. If absent, the SDK pipeline creates
    /// one for POST and PUT requests.
    pub idempotency_key: Option<String>,
    /// Cooperative cancellation signal checked before sending.
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

fn paged_url(mut url: String, page: PageOptions) -> Result<String> {
    page.validate()?;
    url.push_str(&format!("?skip={}&take={}", page.skip, page.take));
    Ok(url)
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

fn request_with_options<T: serde::Serialize>(
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
        () = wait_cancelled(token) => Err(Error::new(
            ErrorKind::Transport,
            "cancelled",
            "The management operation was cancelled.",
        )),
    }
}

async fn wait_cancelled(token: CancellationToken) {
    while !token.is_cancelled() {
        tokio::task::yield_now().await;
    }
}

// ---------------------------------------------------------------------------
// Databases
// ---------------------------------------------------------------------------

/// A project database.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectDatabase {
    #[serde(rename = "databaseId", alias = "database_id")]
    pub database_id: String,
    #[serde(rename = "projectId", alias = "project_id")]
    pub project_id: String,
    pub name: String,
    #[serde(default)]
    pub regions: Vec<RegionCode>,
    #[serde(default, rename = "backupRegion", alias = "backup_region")]
    pub backup_region: Option<RegionCode>,
    #[serde(default)]
    pub residency: Option<String>,
    #[serde(
        default,
        rename = "createdAt",
        alias = "created_at",
        skip_serializing_if = "Option::is_none"
    )]
    pub created_at: Option<String>,
    #[serde(default)]
    pub version: u64,
}

/// Minimal request retained for compatibility with the project database contract.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateDatabase {
    pub name: String,
}

/// Database creation with an explicit placement lock required by TerraBase.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateDatabaseWithPlacement {
    pub name: String,
    pub placement: Vec<RegionCode>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub residency: Option<String>,
}

/// Immutable residency constraints accepted by the database contract.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlacementLock {
    /// Keep every copy in the European launch region.
    EuOnly,
    /// Keep every copy in the Brazilian launch region.
    BrOnly,
    /// Keep every copy in this explicit region set.
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
                .map(|region| region.as_str())
                .collect::<Vec<_>>()
                .join(",")),
        }
    }
}

impl CreateDatabaseWithPlacement {
    /// Create a request with an explicit, immutable placement.
    pub fn new(name: impl Into<String>, placement: Vec<RegionCode>) -> Result<Self> {
        let request = Self {
            name: name.into(),
            placement,
            residency: None,
        };
        request.validate()?;
        Ok(request)
    }
    /// Set a residency lock such as `eu-only` or `br-only`.
    pub fn residency(mut self, residency: impl Into<String>) -> Result<Self> {
        self.residency = Some(residency.into());
        self.validate()?;
        Ok(self)
    }
    /// Set a typed residency lock without accepting arbitrary policy text.
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
            .map(|region| RegionCode::parse(region, "residency"))
            .collect::<Result<Vec<_>>>()?,
    };
    if allowed.is_empty() || placement.iter().any(|region| !allowed.contains(region)) {
        return Err(Error::new(
            ErrorKind::Validation,
            "residency_violation",
            "every placement region must satisfy the residency lock.",
        ));
    }
    Ok(())
}

/// Soft-delete response for a project database.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DeletedDatabase {
    pub database_id: String,
    pub project_id: String,
    #[serde(default)]
    pub name: String,
    pub deleted: bool,
    #[serde(default)]
    pub physical_databases_deleted: u64,
}

/// Databases client under [`ManagementClient::databases`].
#[derive(Debug, Clone)]
pub struct Databases {
    shared: SharedClient,
}

impl Databases {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// List databases in a project with bounded pagination.
    pub async fn list(&self, project_id: Option<&str>) -> Result<Vec<ProjectDatabase>> {
        self.list_page(project_id, PageOptions::default()).await
    }
    /// List one page of project databases.
    pub async fn list_page(
        &self,
        project_id: Option<&str>,
        page: PageOptions,
    ) -> Result<Vec<ProjectDatabase>> {
        let url = paged_url(
            format!("{}/databases", self.shared.project_root(project_id)?),
            page,
        )?;
        let request = Request {
            route_name: "databases.list".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Create a project database.
    pub async fn create(
        &self,
        request: CreateDatabase,
        project_id: Option<&str>,
    ) -> Result<ProjectDatabase> {
        validate_non_empty(&request.name, "database_name")?;
        let request = Request::json(
            "databases.create",
            Method::Post,
            format!("{}/databases", self.shared.project_root(project_id)?),
            &request,
        )?;
        self.shared.send_typed(request).await
    }
    /// Create a database while explicitly setting its placement lock.
    pub async fn create_with_placement(
        &self,
        request: CreateDatabaseWithPlacement,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<ProjectDatabase> {
        request.validate()?;
        let request = request_with_options(
            "databases.create",
            Method::Post,
            format!("{}/databases", self.shared.project_root(project_id)?),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// Fetch one database.
    pub async fn get(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<ProjectDatabase> {
        let id = encode_segment(database_id, "database_id")?;
        let request = Request {
            route_name: "databases.get".to_owned(),
            method: Method::Get,
            url: format!("{}/databases/{id}", self.shared.project_root(project_id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Delete a database. Requires an exact confirmation.
    pub async fn delete(
        &self,
        database_id: &str,
        confirm: bool,
        project_id: Option<&str>,
    ) -> Result<()> {
        let id = encode_segment(database_id, "database_id")?;
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
            url: format!("{}/databases/{id}", self.shared.project_root(project_id)?),
            headers: Vec::new(),
            body: None,
        };
        request.headers.push((
            "x-terrascale-delete-confirmation".to_owned(),
            database_id.trim().to_owned(),
        ));
        self.shared.send_empty(request).await
    }
    /// Restore a soft-deleted database inside its retention window.
    pub async fn restore(
        &self,
        database_id: &str,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<ProjectDatabase> {
        let id = encode_segment(database_id, "database_id")?;
        let request = request_with_options(
            "databases.restore",
            Method::Post,
            format!(
                "{}/databases/{id}/restore",
                self.shared.project_root(project_id)?
            ),
            &EmptyBody {},
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}

// ---------------------------------------------------------------------------
// Branches, collections, and schemas
// ---------------------------------------------------------------------------

/// A database branch.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DatabaseBranch {
    #[serde(rename = "branchId", alias = "branch_id")]
    pub branch_id: String,
    #[serde(rename = "sourceDatabaseId", alias = "source_database_id")]
    pub source_database_id: String,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub placement: Vec<RegionCode>,
    #[serde(default)]
    pub residency: Option<String>,
}

/// Request to create a branch from a point in time.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
pub struct CreateBranch {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<i64>,
}

/// Branches client under [`ManagementClient::branches`].
#[derive(Debug, Clone)]
pub struct Branches {
    shared: SharedClient,
}

impl Branches {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// Open a branch using the server's default point.
    pub async fn create(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<DatabaseBranch> {
        self.create_with_options(
            database_id,
            CreateBranch::default(),
            project_id,
            OperationOptions::default(),
        )
        .await
    }
    /// Open a branch with an explicit name/PITR point and idempotency policy.
    pub async fn create_with_options(
        &self,
        database_id: &str,
        branch: CreateBranch,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<DatabaseBranch> {
        let db = encode_segment(database_id, "database_id")?;
        if let Some(name) = &branch.name {
            validate_non_empty(name, "branch_name")?;
        }
        if branch.at.is_some_and(|at| at < 0) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_restore_point",
                "branch at must be a non-negative Unix timestamp.",
            ));
        }
        let request = request_with_options(
            "branches.create",
            Method::Post,
            format!(
                "{}/databases/{db}/branches",
                self.shared.project_root(project_id)?
            ),
            &branch,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// List branches of a database.
    pub async fn list(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<Vec<DatabaseBranch>> {
        self.list_page(database_id, project_id, PageOptions::default())
            .await
    }
    /// List one page of branches.
    pub async fn list_page(
        &self,
        database_id: &str,
        project_id: Option<&str>,
        page: PageOptions,
    ) -> Result<Vec<DatabaseBranch>> {
        let db = encode_segment(database_id, "database_id")?;
        let request = Request {
            route_name: "branches.list".to_owned(),
            method: Method::Get,
            url: paged_url(
                format!(
                    "{}/databases/{db}/branches",
                    self.shared.project_root(project_id)?
                ),
                page,
            )?,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

/// The collections returned by TerraBase's collection discovery route.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CollectionsResponse {
    pub collections: Vec<String>,
}

/// Collections client under [`ManagementClient::collections`].
#[derive(Debug, Clone)]
pub struct Collections {
    shared: SharedClient,
}

impl Collections {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// List collections occupied by a database.
    pub async fn list(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<CollectionsResponse> {
        let db = encode_segment(database_id, "database_id")?;
        let request = Request {
            route_name: "collections.list".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/databases/{db}/collections",
                self.shared.project_root(project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

/// A schema read/apply result.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DatabaseSchema {
    pub schema: SchemaDocument,
    #[serde(default)]
    pub version: u64,
}

/// Schemas client under [`ManagementClient::schemas`].
#[derive(Debug, Clone)]
pub struct Schemas {
    shared: SharedClient,
}

impl Schemas {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// Read a database collection schema.
    pub async fn get(
        &self,
        database_id: &str,
        collection: &str,
        project_id: Option<&str>,
    ) -> Result<DatabaseSchema> {
        let url = self.collection_schema_url(database_id, collection, project_id)?;
        let request = Request {
            route_name: "schemas.get".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Apply a typed schema to a database collection.
    pub async fn put(
        &self,
        database_id: &str,
        collection: &str,
        schema: SchemaDocument,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<DatabaseSchema> {
        let url = self.collection_schema_url(database_id, collection, project_id)?;
        let request = request_with_options("schemas.put", Method::Put, url, &schema, &options)?;
        send_with_options(&self.shared, request, &options).await
    }
    fn collection_schema_url(
        &self,
        database_id: &str,
        collection: &str,
        project_id: Option<&str>,
    ) -> Result<String> {
        Ok(format!(
            "{}/databases/{}/collections/{}/schema",
            self.shared.project_root(project_id)?,
            encode_segment(database_id, "database_id")?,
            encode_segment(collection, "collection")?
        ))
    }
}

// ---------------------------------------------------------------------------
// Backups, PITR, and restore jobs
// ---------------------------------------------------------------------------

/// A backup job.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Backup {
    #[serde(rename = "backupId", alias = "backup_id")]
    pub backup_id: String,
    #[serde(rename = "databaseId", alias = "database_id")]
    pub database_id: String,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default, rename = "createdAt", alias = "created_at")]
    pub created_at: Option<String>,
    #[serde(default, rename = "completedAt", alias = "completed_at")]
    pub completed_at: Option<String>,
    #[serde(default, rename = "errorMessage", alias = "error_message")]
    pub error_message: Option<String>,
}

/// Backup policy configuration.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BackupPolicy {
    #[serde(rename = "fullBackupCron", alias = "full_backup_cron")]
    pub full_backup_cron: String,
    #[serde(
        default,
        rename = "incrementalBackupCron",
        alias = "incremental_backup_cron"
    )]
    pub incremental_backup_cron: Option<String>,
    #[serde(default, rename = "retentionDays", alias = "retention_days")]
    pub retention_days: Option<u32>,
    #[serde(default)]
    pub disabled: bool,
}

/// Confirmation plan returned before an on-demand backup.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BackupPlan {
    pub action: String,
    #[serde(rename = "requestDigest", alias = "request_digest")]
    pub request_digest: String,
    #[serde(rename = "confirmationToken", alias = "confirmation_token")]
    pub confirmation_token: String,
    #[serde(rename = "expiresAt", alias = "expires_at")]
    pub expires_at: String,
}

/// Request to plan a confirmed backup.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PlanBackupRequest {
    #[serde(rename = "operationSessionId", alias = "operation_session_id")]
    pub operation_session_id: String,
}

/// Request to execute a confirmed backup.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExecuteBackupRequest {
    #[serde(rename = "operationSessionId", alias = "operation_session_id")]
    pub operation_session_id: String,
    #[serde(rename = "confirmationToken", alias = "confirmation_token")]
    pub confirmation_token: String,
}

/// A PITR restore point.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RestorePoint {
    pub name: String,
    #[serde(default, rename = "backupId", alias = "backup_id")]
    pub backup_id: Option<String>,
    #[serde(rename = "createdAt", alias = "created_at")]
    pub created_at: String,
    #[serde(default, rename = "isSnapshotRestore", alias = "is_snapshot_restore")]
    pub is_snapshot_restore: bool,
    #[serde(default, rename = "isEncrypted", alias = "is_encrypted")]
    pub is_encrypted: bool,
    #[serde(default, rename = "isIncremental", alias = "is_incremental")]
    pub is_incremental: bool,
    #[serde(default, rename = "filesToRestore", alias = "files_to_restore")]
    pub files_to_restore: u64,
}

/// A restore job that always targets a new database.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RestoreJob {
    #[serde(rename = "restoreId", alias = "restore_id")]
    pub restore_id: String,
    #[serde(rename = "databaseId", alias = "database_id")]
    pub database_id: String,
    #[serde(rename = "backupId", alias = "backup_id")]
    pub backup_id: String,
    #[serde(rename = "restoredDatabaseId", alias = "restored_database_id")]
    pub restored_database_id: String,
    #[serde(rename = "restoredDatabaseName", alias = "restored_database_name")]
    pub restored_database_name: String,
    pub status: String,
    #[serde(default, rename = "completedAt", alias = "completed_at")]
    pub completed_at: Option<String>,
    #[serde(default, rename = "errorMessage", alias = "error_message")]
    pub error_message: Option<String>,
}

/// Start a restore from a backup or PITR point.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct StartRestore {
    #[serde(rename = "backupId", alias = "backup_id")]
    pub backup_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<i64>,
    #[serde(
        default,
        rename = "idempotencyKey",
        alias = "idempotency_key",
        skip_serializing_if = "Option::is_none"
    )]
    pub idempotency_key: Option<String>,
}

/// Backups client under [`ManagementClient::backups`].
#[derive(Debug, Clone)]
pub struct Backups {
    shared: SharedClient,
}

impl Backups {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    fn database_root(&self, database_id: &str, project_id: Option<&str>) -> Result<String> {
        Ok(format!(
            "{}/databases/{}",
            self.shared.project_root(project_id)?,
            encode_segment(database_id, "database_id")?
        ))
    }
    /// Configure the backup policy.
    pub async fn configure_policy(
        &self,
        database_id: &str,
        policy: BackupPolicy,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<BackupPolicy> {
        validate_non_empty(&policy.full_backup_cron, "full_backup_cron")?;
        if policy
            .retention_days
            .is_some_and(|days| !(1..=35).contains(&days))
        {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_retention_days",
                "retention_days must be between 1 and 35.",
            ));
        }
        let request = request_with_options(
            "backups.policy.configure",
            Method::Put,
            format!(
                "{}/backups/policy",
                self.database_root(database_id, project_id)?
            ),
            &policy,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// Plan a confirmed on-demand backup.
    pub async fn plan(
        &self,
        database_id: &str,
        request: PlanBackupRequest,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<BackupPlan> {
        validate_bounded(&request.operation_session_id, "operation_session_id", 128)?;
        let request = request_with_options(
            "backups.plan",
            Method::Post,
            format!(
                "{}/backups:plan",
                self.database_root(database_id, project_id)?
            ),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// Execute a confirmed on-demand backup with an explicit idempotency key.
    pub async fn execute(
        &self,
        database_id: &str,
        request: ExecuteBackupRequest,
        idempotency_key: impl Into<String>,
        project_id: Option<&str>,
    ) -> Result<Backup> {
        let options = OperationOptions {
            idempotency_key: Some(idempotency_key.into()),
            cancellation: None,
        };
        validate_bounded(&request.operation_session_id, "operation_session_id", 128)?;
        validate_bounded(&request.confirmation_token, "confirmation_token", 8_192)?;
        let request = request_with_options(
            "backups.execute",
            Method::Post,
            format!(
                "{}/backups:execute",
                self.database_root(database_id, project_id)?
            ),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// List backup jobs.
    pub async fn list(&self, database_id: &str, project_id: Option<&str>) -> Result<Vec<Backup>> {
        self.list_page(database_id, project_id, PageOptions::default())
            .await
    }
    /// List one page of backup jobs.
    pub async fn list_page(
        &self,
        database_id: &str,
        project_id: Option<&str>,
        page: PageOptions,
    ) -> Result<Vec<Backup>> {
        let request = Request {
            route_name: "backups.list".to_owned(),
            method: Method::Get,
            url: paged_url(
                format!("{}/backups", self.database_root(database_id, project_id)?),
                page,
            )?,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Fetch one backup job.
    pub async fn get(
        &self,
        database_id: &str,
        backup_id: &str,
        project_id: Option<&str>,
    ) -> Result<Backup> {
        let request = Request {
            route_name: "backups.get".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/backups/{}",
                self.database_root(database_id, project_id)?,
                encode_segment(backup_id, "backup_id")?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// List available PITR restore points.
    pub async fn list_restore_points(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<Vec<RestorePoint>> {
        let request = Request {
            route_name: "backups.restore_points.list".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/restore-points",
                self.database_root(database_id, project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Start a restore into a new database.
    pub async fn start_restore(
        &self,
        database_id: &str,
        request: StartRestore,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<RestoreJob> {
        validate_non_empty(&request.backup_id, "backup_id")?;
        if let Some(key) = &request.idempotency_key {
            validate_bounded(key, "idempotency_key", 128)?;
        }
        let request = request_with_options(
            "backups.restores.start",
            Method::Post,
            format!("{}/restores", self.database_root(database_id, project_id)?),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// List restore jobs.
    pub async fn list_restores(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<Vec<RestoreJob>> {
        self.list_restores_page(database_id, project_id, PageOptions::default())
            .await
    }
    /// List one page of restore jobs.
    pub async fn list_restores_page(
        &self,
        database_id: &str,
        project_id: Option<&str>,
        page: PageOptions,
    ) -> Result<Vec<RestoreJob>> {
        let request = Request {
            route_name: "backups.restores.list".to_owned(),
            method: Method::Get,
            url: paged_url(
                format!("{}/restores", self.database_root(database_id, project_id)?),
                page,
            )?,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Fetch one restore job.
    pub async fn get_restore(
        &self,
        database_id: &str,
        restore_id: &str,
        project_id: Option<&str>,
    ) -> Result<RestoreJob> {
        let request = Request {
            route_name: "backups.restores.get".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/restores/{}",
                self.database_root(database_id, project_id)?,
                encode_segment(restore_id, "restore_id")?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Restore at a PITR timestamp using the TerraBase restore route.
    pub async fn restore_at(
        &self,
        database_id: &str,
        request: RestoreAt,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<ProjectDatabase> {
        validate_non_empty(&request.name, "database_name")?;
        if request.at.is_some_and(|at| at < 0) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_restore_point",
                "at must be a non-negative Unix timestamp.",
            ));
        }
        let request = request_with_options(
            "backups.restore",
            Method::Post,
            format!(
                "{}/backups/restore",
                self.database_root(database_id, project_id)?
            ),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}

/// Request to restore into a new database at an optional Unix-millisecond point.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RestoreAt {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<i64>,
}

// ---------------------------------------------------------------------------
// Import/export
// ---------------------------------------------------------------------------

/// Formats supported by the documented import/export workflows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum TransferFormat {
    Jsonl,
    Csv,
    MongoJson,
    CosmosJson,
    DynamoDb,
}

/// Request to create a resumable whole-database export (D-60).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateExport {
    pub format: TransferFormat,
    #[serde(default, rename = "includeHistory", alias = "include_history")]
    pub include_history: bool,
}

/// Export operation status.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ExportJob {
    pub export_id: String,
    pub database_id: String,
    pub format: TransferFormat,
    pub status: String,
    #[serde(default)]
    pub next_cursor: Option<String>,
    #[serde(default)]
    pub download_url: Option<String>,
}

/// Export client under [`ManagementClient::exports`].
#[derive(Debug, Clone)]
pub struct Exports {
    shared: SharedClient,
}

impl Exports {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// Start a consistent, resumable export.
    pub async fn create(
        &self,
        database_id: &str,
        request: CreateExport,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<ExportJob> {
        let db = encode_segment(database_id, "database_id")?;
        let request = request_with_options(
            "exports.create",
            Method::Post,
            format!(
                "{}/databases/{db}/exports",
                self.shared.project_root(project_id)?
            ),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}

// ---------------------------------------------------------------------------
// API keys and usage
// ---------------------------------------------------------------------------

/// API-key metadata; the secret is never present in list responses.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ApiKey {
    #[serde(rename = "keyId", alias = "key_id")]
    pub key_id: String,
    #[serde(rename = "projectId", alias = "project_id")]
    pub project_id: String,
    pub name: String,
    pub prefix: String,
    #[serde(default, rename = "lastFour", alias = "last_four")]
    pub last_four: Option<String>,
    #[serde(default)]
    pub scopes: Vec<String>,
    #[serde(default, rename = "databaseId", alias = "database_id")]
    pub database_id: Option<String>,
    #[serde(default, rename = "environmentId", alias = "environment_id")]
    pub environment_id: Option<String>,
    #[serde(default, rename = "createdByUserId", alias = "created_by_user_id")]
    pub created_by_user_id: Option<String>,
    #[serde(default, rename = "createdByEmail", alias = "created_by_email")]
    pub created_by_email: Option<String>,
    #[serde(default, rename = "expiresAt", alias = "expires_at")]
    pub expires_at: Option<String>,
    #[serde(default, rename = "revokedAt", alias = "revoked_at")]
    pub revoked_at: Option<String>,
    #[serde(default, rename = "isActive", alias = "is_active")]
    pub is_active: bool,
}

/// Cursor page of API-key metadata.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ApiKeyPage {
    #[serde(default, rename = "apiKeys", alias = "api_keys")]
    pub api_keys: Vec<ApiKey>,
    #[serde(default, rename = "nextCursor", alias = "next_cursor")]
    pub next_cursor: Option<String>,
}

/// Request to mint a project API key.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateApiKey {
    pub name: String,
    #[serde(default)]
    pub scopes: Vec<String>,
    #[serde(
        default,
        rename = "databaseId",
        alias = "database_id",
        skip_serializing_if = "Option::is_none"
    )]
    pub database_id: Option<String>,
    #[serde(
        default,
        rename = "environmentId",
        alias = "environment_id",
        skip_serializing_if = "Option::is_none"
    )]
    pub environment_id: Option<String>,
    #[serde(
        default,
        rename = "expiresAt",
        alias = "expires_at",
        skip_serializing_if = "Option::is_none"
    )]
    pub expires_at: Option<String>,
}

/// Newly minted key; `secret` is shown exactly once.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreatedApiKey {
    #[serde(rename = "apiKey", alias = "api_key")]
    pub api_key: ApiKey,
    pub secret: String,
}
/// Rotated key; `secret` is shown exactly once.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RotatedApiKey {
    #[serde(rename = "apiKey", alias = "api_key")]
    pub api_key: ApiKey,
    pub secret: String,
    pub rotated: bool,
}
/// Revoke result.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RevokedApiKey {
    #[serde(rename = "keyId", alias = "key_id")]
    pub key_id: String,
    pub revoked: bool,
}

/// API-key client under [`ManagementClient::api_keys`].
#[derive(Debug, Clone)]
pub struct ApiKeys {
    shared: SharedClient,
}

impl ApiKeys {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// List project API-key metadata using cursor pagination.
    pub async fn list(
        &self,
        project_id: Option<&str>,
        cursor: Option<&str>,
        limit: u32,
    ) -> Result<ApiKeyPage> {
        let project = self.shared.project_root(project_id)?;
        if limit == 0 || limit > MAX_PAGE_SIZE {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_page_size",
                format!("limit must be between 1 and {MAX_PAGE_SIZE}."),
            ));
        }
        let mut url = format!("{project}/auth/api-keys?limit={limit}");
        if let Some(cursor) = cursor {
            url.push_str("&cursor=");
            url.push_str(&encode_segment(cursor, "cursor")?);
        }
        let request = Request {
            route_name: "api_keys.list".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Create a project API key.
    pub async fn create(
        &self,
        request: CreateApiKey,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<CreatedApiKey> {
        validate_non_empty(&request.name, "api_key_name")?;
        if request.scopes.iter().any(|scope| scope.trim().is_empty()) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_api_key_scope",
                "API-key scopes cannot be empty.",
            ));
        }
        let request = request_with_options(
            "api_keys.create",
            Method::Post,
            format!("{}/auth/api-keys", self.shared.project_root(project_id)?),
            &request,
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// Rotate an API key.
    pub async fn rotate(
        &self,
        key_id: &str,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<RotatedApiKey> {
        let request = request_with_options(
            "api_keys.rotate",
            Method::Post,
            format!(
                "{}/auth/api-keys/{}/rotate",
                self.shared.project_root(project_id)?,
                encode_segment(key_id, "key_id")?
            ),
            &EmptyBody {},
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
    /// Revoke an API key.
    pub async fn revoke(
        &self,
        key_id: &str,
        project_id: Option<&str>,
        options: OperationOptions,
    ) -> Result<RevokedApiKey> {
        let request = request_options(
            Request {
                route_name: "api_keys.revoke".to_owned(),
                method: Method::Delete,
                url: format!(
                    "{}/auth/api-keys/{}",
                    self.shared.project_root(project_id)?,
                    encode_segment(key_id, "key_id")?
                ),
                headers: Vec::new(),
                body: None,
            },
            &options,
        )?;
        send_with_options(&self.shared, request, &options).await
    }
}

#[derive(Debug, Clone, Copy, Serialize)]
struct EmptyBody {}

/// Project usage summary. Writes and storage are the billed dimensions.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UsageSummary {
    #[serde(rename = "projectId", alias = "project_id")]
    pub project_id: String,
    #[serde(default)]
    pub writes: u64,
    #[serde(default, rename = "storageBytes", alias = "storage_bytes")]
    pub storage_bytes: u64,
    #[serde(default, rename = "periodStart", alias = "period_start")]
    pub period_start: Option<String>,
    #[serde(default, rename = "periodEnd", alias = "period_end")]
    pub period_end: Option<String>,
}

/// Usage workload range.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum UsageRange {
    #[serde(rename = "24h")]
    Hours24,
    #[serde(rename = "7d")]
    Days7,
    #[serde(rename = "30d")]
    Days30,
}

/// One usage workload point.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UsagePoint {
    #[serde(rename = "start", alias = "startAt")]
    pub start: String,
    #[serde(rename = "end", alias = "endAt")]
    pub end: String,
    #[serde(default)]
    pub value: u64,
}
/// Project usage workload response.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UsageWorkload {
    pub project_id: String,
    pub range: UsageRange,
    pub points: Vec<UsagePoint>,
}

/// Usage client under [`ManagementClient::usage`].
#[derive(Debug, Clone)]
pub struct Usage {
    shared: SharedClient,
}

impl Usage {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// Fetch project usage.
    pub async fn get_project_usage(&self, project_id: Option<&str>) -> Result<UsageSummary> {
        let request = Request {
            route_name: "usage.project.get".to_owned(),
            method: Method::Get,
            url: format!("{}/usage", self.shared.project_root(project_id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Fetch usage workload time series.
    pub async fn workload(
        &self,
        project_id: Option<&str>,
        range: UsageRange,
    ) -> Result<UsageWorkload> {
        let text = match range {
            UsageRange::Hours24 => "24h",
            UsageRange::Days7 => "7d",
            UsageRange::Days30 => "30d",
        };
        let request = Request {
            route_name: "usage.project.workload".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/usage/workload?range={text}",
                self.shared.project_root(project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

// ---------------------------------------------------------------------------
// Regions and root
// ---------------------------------------------------------------------------

/// A servable region.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RegionSummary {
    pub code: RegionCode,
    pub name: String,
    pub country: String,
    pub latitude: f64,
    pub longitude: f64,
    pub available: bool,
}

/// Regions client under [`ManagementClient::regions`].
#[derive(Debug, Clone)]
pub struct Regions {
    shared: SharedClient,
}

impl Regions {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// List servable regions.
    pub async fn list(&self) -> Result<Vec<RegionSummary>> {
        let request = Request {
            route_name: "regions.list".to_owned(),
            method: Method::Get,
            url: format!("{}/regions", self.shared.global_base_url()),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
    /// Fetch one region.
    pub async fn get(&self, region: RegionCode) -> Result<RegionSummary> {
        let request = Request {
            route_name: "regions.get".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/regions/{}",
                self.shared.global_base_url(),
                region.as_str()
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

/// Management surface. Obtain from [`crate::TerraScaleClient::management`].
#[derive(Debug, Clone)]
pub struct ManagementClient {
    shared: SharedClient,
}

impl ManagementClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    /// Databases: create, list, get, restore, and delete.
    #[must_use]
    pub fn databases(&self) -> Databases {
        Databases::new(self.shared.clone())
    }
    /// Database branches.
    #[must_use]
    pub fn branches(&self) -> Branches {
        Branches::new(self.shared.clone())
    }
    /// Database collections.
    #[must_use]
    pub fn collections(&self) -> Collections {
        Collections::new(self.shared.clone())
    }
    /// Database schemas.
    #[must_use]
    pub fn schemas(&self) -> Schemas {
        Schemas::new(self.shared.clone())
    }
    /// Backups, PITR, and restore jobs.
    #[must_use]
    pub fn backups(&self) -> Backups {
        Backups::new(self.shared.clone())
    }
    /// Whole-database exports.
    #[must_use]
    pub fn exports(&self) -> Exports {
        Exports::new(self.shared.clone())
    }
    /// Project API keys.
    #[must_use]
    pub fn api_keys(&self) -> ApiKeys {
        ApiKeys::new(self.shared.clone())
    }
    /// Project usage summaries and workload.
    #[must_use]
    pub fn usage(&self) -> Usage {
        Usage::new(self.shared.clone())
    }
    /// Regions: list and get.
    #[must_use]
    pub fn regions(&self) -> Regions {
        Regions::new(self.shared.clone())
    }
}
