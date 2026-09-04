//! Management surface: databases, branches, backups, usage, regions.
//!
//! Mirrors the `@terrascale/sdk` root client surface (projects, databases,
//! branches, backups, usage, regions) with the same route names.

use serde::{Deserialize, Serialize};

use crate::client::SharedClient;
use crate::error::Result;
use crate::regions::RegionCode;
use crate::transport::{Method, Request};

// ---------------------------------------------------------------------------
// Databases
// ---------------------------------------------------------------------------

/// A project database.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectDatabase {
    /// Branded database id.
    pub database_id: String,
    /// Owning project.
    pub project_id: String,
    /// Database name.
    pub name: String,
    /// Hosting regions.
    #[serde(default)]
    pub regions: Vec<RegionCode>,
    /// Creation timestamp.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    /// Schema version.
    #[serde(default)]
    pub version: u64,
}

/// Request to create a database.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateDatabase {
    /// Database name.
    pub name: String,
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

    /// List databases in the project.
    pub async fn list(&self, project_id: Option<&str>) -> Result<Vec<ProjectDatabase>> {
        let request = Request {
            route_name: "databases.list".to_owned(),
            method: Method::Get,
            url: format!("{}/databases", self.shared.project_root(project_id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }

    /// Create a database.
    pub async fn create(
        &self,
        request: CreateDatabase,
        project_id: Option<&str>,
    ) -> Result<ProjectDatabase> {
        let url = format!("{}/databases", self.shared.project_root(project_id)?);
        let request = Request::json("databases.create", Method::Post, url, &request)?;
        self.shared.send_typed(request).await
    }

    /// Fetch one database.
    pub async fn get(&self, database_id: &str, project_id: Option<&str>) -> Result<ProjectDatabase> {
        let request = Request {
            route_name: "databases.get".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/databases/{database_id}",
                self.shared.project_root(project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }

    /// Delete a database. Requires `confirm == true`, mirroring the TS SDK.
    pub async fn delete(
        &self,
        database_id: &str,
        confirm: bool,
        project_id: Option<&str>,
    ) -> Result<()> {
        if !confirm {
            return Err(crate::error::Error::new(
                crate::error::ErrorKind::Validation,
                "confirm_required",
                "Database deletion requires confirm to be true.",
            ));
        }
        let mut request = Request {
            route_name: "databases.delete".to_owned(),
            method: Method::Delete,
            url: format!(
                "{}/databases/{database_id}",
                self.shared.project_root(project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        request.headers.push((
            "x-terrascale-delete-confirmation".to_owned(),
            database_id.to_owned(),
        ));
        self.shared.send_empty(request).await
    }
}

// ---------------------------------------------------------------------------
// Branches
// ---------------------------------------------------------------------------

/// A database branch (dev branches, preview branches, restore targets).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DatabaseBranch {
    /// Branch id.
    pub branch_id: String,
    /// Source database id.
    pub source_database_id: String,
    /// Branch status.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
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

    /// Open a branch of a database. Creation lands in < 5 s (GOAL D-18).
    pub async fn create(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<DatabaseBranch> {
        let url = format!(
            "{}/databases/{database_id}/branches",
            self.shared.project_root(project_id)?
        );
        let request = Request::json(
            "branches.create",
            Method::Post,
            url,
            &serde_json::json!({}),
        )?;
        self.shared.send_typed(request).await
    }

    /// List branches of a database.
    pub async fn list(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<Vec<DatabaseBranch>> {
        let request = Request {
            route_name: "branches.list".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/databases/{database_id}/branches",
                self.shared.project_root(project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

// ---------------------------------------------------------------------------
// Backups
// ---------------------------------------------------------------------------

/// A backup job (Litestream stream per file, GOAL D-29).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Backup {
    /// Backup id.
    pub backup_id: String,
    /// Database id.
    pub database_id: String,
    /// Job status.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
    /// Creation timestamp.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
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

    /// Start a backup. Restores always land as a new database.
    pub async fn create(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<Backup> {
        let url = format!(
            "{}/databases/{database_id}/backups",
            self.shared.project_root(project_id)?
        );
        let request = Request::json("backups.create", Method::Post, url, &serde_json::json!({}))?;
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
                "{}/databases/{database_id}/backups/{backup_id}",
                self.shared.project_root(project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }

    /// List backup jobs for a database.
    pub async fn list(
        &self,
        database_id: &str,
        project_id: Option<&str>,
    ) -> Result<Vec<Backup>> {
        let request = Request {
            route_name: "backups.list".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/databases/{database_id}/backups",
                self.shared.project_root(project_id)?
            ),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

// ---------------------------------------------------------------------------
// Usage
// ---------------------------------------------------------------------------

/// Project usage summary (writes + storage are the billed dimensions).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UsageSummary {
    /// Project id.
    pub project_id: String,
    /// Billed writes in the period.
    #[serde(default)]
    pub writes: u64,
    /// Logical storage bytes.
    #[serde(default)]
    pub storage_bytes: u64,
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
}

// ---------------------------------------------------------------------------
// Regions
// ---------------------------------------------------------------------------

/// A servable region (no provider-internal fields cross this boundary).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RegionSummary {
    /// Region code.
    pub code: RegionCode,
    /// Display name.
    pub name: String,
    /// Country.
    pub country: String,
    /// Latitude.
    pub latitude: f64,
    /// Longitude.
    pub longitude: f64,
    /// Whether the region currently serves traffic.
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

    /// List servable regions (`GET /regions` is served by first contact).
    pub async fn list(&self) -> Result<Vec<RegionSummary>> {
        // Regions are global: bypass the pinned base URL.
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
            url: format!("{}/regions/{}", self.shared.global_base_url(), region.as_str()),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

// ---------------------------------------------------------------------------
// Management root
// ---------------------------------------------------------------------------

/// Management surface. Obtain from [`crate::TerraScaleClient::management`].
#[derive(Debug, Clone)]
pub struct ManagementClient {
    shared: SharedClient,
}

impl ManagementClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }

    /// Databases: create / get / list / delete.
    #[must_use]
    pub fn databases(&self) -> Databases {
        Databases::new(self.shared.clone())
    }

    /// Branches: create / list.
    #[must_use]
    pub fn branches(&self) -> Branches {
        Branches::new(self.shared.clone())
    }

    /// Backups: create / get / list.
    #[must_use]
    pub fn backups(&self) -> Backups {
        Backups::new(self.shared.clone())
    }

    /// Usage: project usage summaries.
    #[must_use]
    pub fn usage(&self) -> Usage {
        Usage::new(self.shared.clone())
    }

    /// Regions: list / get.
    #[must_use]
    pub fn regions(&self) -> Regions {
        Regions::new(self.shared.clone())
    }
}
