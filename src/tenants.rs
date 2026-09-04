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

/// Cursor and page-size options for [`TenantsClient::list_page`].
///
/// Tenant listing is deliberately cursor based: a project may contain
/// millions of tenant databases and the server must not materialize the
/// complete catalog for one request.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TenantListOptions {
    /// Continuation cursor returned by the preceding page.
    pub cursor: Option<String>,
    /// Maximum number of tenants requested for this page.
    pub limit: Option<u32>,
}

impl TenantListOptions {
    /// Start with an empty cursor and the server's default page size.
    #[must_use]
    pub const fn new() -> Self {
        Self {
            cursor: None,
            limit: None,
        }
    }

    /// Continue listing after `cursor`.
    #[must_use]
    pub fn with_cursor(mut self, cursor: impl Into<String>) -> Self {
        self.cursor = Some(cursor.into());
        self
    }

    /// Request at most `limit` records.
    #[must_use]
    pub const fn with_limit(mut self, limit: u32) -> Self {
        self.limit = Some(limit);
        self
    }
}

/// One cursor page of tenants.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantPage {
    /// Tenants in this page.
    #[serde(alias = "items")]
    pub tenants: Vec<Tenant>,
    /// Cursor for the next page, if one remains.
    #[serde(default, alias = "nextCursor", skip_serializing_if = "Option::is_none")]
    pub next_cursor: Option<String>,
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
    #[serde(alias = "databaseId")]
    pub database_id: String,
    /// Regions hosting the tenant database.
    #[serde(default)]
    pub regions: Vec<RegionCode>,
    /// Name of the template from which placement and key policy were derived.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub template: Option<String>,
    /// Creation timestamp.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
}

/// A tenant-scoped API key minted from the template.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantApiKey {
    /// Key id.
    #[serde(alias = "keyId")]
    pub key_id: String,
    /// Secret token (shown once at creation).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token: Option<String>,
    /// Tenant this key is scoped to.
    #[serde(alias = "tenantKey")]
    pub tenant_key: String,
}

/// Tenant branch for support/debugging (per-tenant branching).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantBranch {
    /// Branch id.
    #[serde(alias = "branchId")]
    pub branch_id: String,
    /// Tenant this branch belongs to.
    #[serde(alias = "tenantKey")]
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
        validate_key(&request.key)?;
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
        Ok(self
            .list_page(TenantListOptions::default(), project_id)
            .await?
            .tenants)
    }

    /// List one cursor page of tenants in the project.
    pub async fn list_page(
        &self,
        options: TenantListOptions,
        project_id: Option<&str>,
    ) -> Result<TenantPage> {
        let mut url = self.root(project_id)?;
        let mut query = Vec::new();
        if let Some(cursor) = options.cursor {
            query.push(format!("cursor={}", encode_segment(&cursor)?));
        }
        if let Some(limit) = options.limit {
            query.push(format!("limit={limit}"));
        }
        if !query.is_empty() {
            url.push('?');
            url.push_str(&query.join("&"));
        }
        let request = Request {
            route_name: "tenants.list".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        let response: TenantListResponse = self.shared.send_typed(request).await?;
        Ok(response.into_page())
    }

    /// Delete a tenant database after explicit confirmation.
    pub async fn delete(&self, key: &str, confirm: bool, project_id: Option<&str>) -> Result<()> {
        if !confirm {
            return Err(crate::error::Error::new(
                crate::error::ErrorKind::Validation,
                "confirm_required",
                "Tenant deletion requires confirm to be true.",
            ));
        }
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
        // Shared's automatic idempotency decoration covers POST/PUT. DELETE
        // is also a mutation, so carry a stable key here for safe retries.
        request.headers.push((
            "idempotency-key".to_owned(),
            format!("ts_tenant_delete_{}", encode_key(key)?),
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
    validate_key(key)?;
    encode_segment(key)
}

fn validate_key(key: &str) -> Result<()> {
    if key.trim().is_empty() {
        return Err(crate::error::Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_tenant_key",
            "Tenant keys cannot be empty.",
        ));
    }
    Ok(())
}

/// Accept the two response shapes used by the SDK contract fixtures while the
/// tenant operations are being added to the published OpenAPI document.
#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum TenantListResponse {
    Page(TenantPage),
    Items(Vec<Tenant>),
}

impl TenantListResponse {
    fn into_page(self) -> TenantPage {
        match self {
            Self::Page(page) => page,
            Self::Items(tenants) => TenantPage {
                tenants,
                next_cursor: None,
            },
        }
    }
}

fn encode_segment(segment: &str) -> Result<String> {
    if segment.is_empty() {
        return Err(crate::error::Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_tenant_key",
            "Tenant keys cannot be empty.",
        ));
    }
    let mut encoded = String::with_capacity(segment.len());
    for byte in segment.as_bytes() {
        if byte.is_ascii_alphanumeric() || b"-_.~".contains(byte) {
            encoded.push(*byte as char);
        } else {
            encoded.push_str(&format!("%{byte:02X}"));
        }
    }
    Ok(encoded)
}
