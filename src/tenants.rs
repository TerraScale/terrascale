//! Database-per-tenant (GOAL D-51): thousands of small databases as a
//! first-class pattern. `tenants.create(key)` provisions a database with
//! placement and key template inherited from a tenant template; tenant-scoped
//! API keys come from the template.

use serde::{Deserialize, Serialize};

use crate::client::SharedClient;
use crate::error::Result;
use crate::regions::RegionCode;
use crate::transport::{Method, Request};

/// Template a tenant database inherits placement and key policy from.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantTemplate {
    /// Template name.
    pub name: String,
    /// Regions the tenant database lives in.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub regions: Vec<RegionCode>,
}

/// Request to provision one tenant (= one database).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateTenant {
    /// Tenant key; selects the database for tenant-scoped keys.
    pub key: String,
    /// Template to inherit placement and key policy from.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub template: Option<String>,
    /// Explicit region list (cannot widen a placement lock).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub regions: Option<Vec<RegionCode>>,
}

impl CreateTenant {
    /// Provision a tenant from a template.
    #[must_use]
    pub fn new(key: impl Into<String>, template: impl Into<String>) -> Self {
        Self {
            key: key.into(),
            template: Some(template.into()),
            regions: None,
        }
    }
}

/// A provisioned tenant.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Tenant {
    /// Tenant key.
    pub key: String,
    /// Underlying database id.
    pub database_id: String,
    /// Regions hosting the tenant database.
    #[serde(default)]
    pub regions: Vec<RegionCode>,
    /// Creation timestamp.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
}

/// A tenant-scoped API key minted from the template.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantApiKey {
    /// Key id.
    pub key_id: String,
    /// Secret token (shown once at creation).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token: Option<String>,
    /// Tenant this key is scoped to.
    pub tenant_key: String,
}

/// Tenant branch for support/debugging (per-tenant branching).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantBranch {
    /// Branch id.
    pub branch_id: String,
    /// Tenant this branch belongs to.
    pub tenant_key: String,
    /// Branch status.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
}

/// Tenant client. Obtain from [`crate::TerraScaleClient::tenants`].
#[derive(Debug, Clone)]
pub struct TenantsClient {
    shared: SharedClient,
}

impl TenantsClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }

    fn root(&self, project_id: Option<&str>) -> Result<String> {
        Ok(format!("{}/tenants", self.shared.project_root(project_id)?))
    }

    /// Provision a tenant database from a template.
    pub async fn create(&self, request: CreateTenant, project_id: Option<&str>) -> Result<Tenant> {
        let request = Request::json(
            "tenants.create",
            Method::Post,
            self.root(project_id)?,
            &request,
        )?;
        self.shared.send_typed(request).await
    }

    /// Fetch one tenant by key.
    pub async fn get(&self, key: &str, project_id: Option<&str>) -> Result<Tenant> {
        let request = Request {
            route_name: "tenants.get".to_owned(),
            method: Method::Get,
            url: format!("{}/{}", self.root(project_id)?, encode_key(key)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }

    /// List tenants in the project.
    pub async fn list(&self, project_id: Option<&str>) -> Result<Vec<Tenant>> {
        let request = Request {
            route_name: "tenants.list".to_owned(),
            method: Method::Get,
            url: self.root(project_id)?,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }

    /// Delete a tenant database. Confirmation is required, like databases.
    pub async fn delete(&self, key: &str, project_id: Option<&str>) -> Result<()> {
        let mut request = Request {
            route_name: "tenants.delete".to_owned(),
            method: Method::Delete,
            url: format!("{}/{}", self.root(project_id)?, encode_key(key)?),
            headers: Vec::new(),
            body: None,
        };
        request.headers.push((
            "x-terrascale-delete-confirmation".to_owned(),
            key.to_owned(),
        ));
        self.shared.send_empty(request).await
    }

    /// Mint a tenant-scoped API key from the template's key policy.
    pub async fn create_key(&self, key: &str, project_id: Option<&str>) -> Result<TenantApiKey> {
        let url = format!("{}/{}/keys", self.root(project_id)?, encode_key(key)?);
        let request = Request::json(
            "tenants.keys.create",
            Method::Post,
            url,
            &serde_json::json!({}),
        )?;
        self.shared.send_typed(request).await
    }

    /// Open a per-tenant support branch.
    pub async fn create_branch(&self, key: &str, project_id: Option<&str>) -> Result<TenantBranch> {
        let url = format!("{}/{}/branches", self.root(project_id)?, encode_key(key)?);
        let request = Request::json(
            "tenants.branches.create",
            Method::Post,
            url,
            &serde_json::json!({}),
        )?;
        self.shared.send_typed(request).await
    }
}

fn encode_key(key: &str) -> Result<String> {
    if key.trim().is_empty() {
        return Err(crate::error::Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_tenant_key",
            "Tenant keys cannot be empty.",
        ));
    }
    Ok(key.to_owned())
}
