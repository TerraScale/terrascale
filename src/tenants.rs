//! Database-per-tenant (GOAL D-51): thousands of small databases as a
//! first-class pattern.
//!
//! The published authorization-lease route is implemented; the remaining
//! tenant routes are not part of the pinned TerraBase contract yet. The
//! typed surface remains available so callers can validate requests and
//! compile against the planned API, but those operations fail closed with a
//! `contract_gap` error instead of sending a request to an invented route.

use serde::de::Error as DeError;
use serde::ser::Error as SerError;
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use crate::client::{SharedClient, SKIP_AUTH_MARKER};
use crate::error::{Error, ErrorKind, Result};
pub use crate::generated_contract::models::Lease;
use crate::regions::RegionCode;
use crate::transport::{Method, Request};

const MAX_TENANT_KEY_BYTES: usize = 64;
const MAX_PLACEMENT_REGIONS: usize = 20;
const MAX_TENANT_PAGE_SIZE: u32 = 1_000;

/// An immutable residency constraint inherited by a tenant database.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TenantPlacementLock {
    /// Keep every copy in the European launch region.
    EuOnly,
    /// Keep every copy in the Brazilian launch region.
    BrOnly,
    /// Keep every copy in this explicit set of regions.
    Regions(Vec<RegionCode>),
}

impl TenantPlacementLock {
    /// Construct an explicit region placement lock, rejecting an empty or
    /// duplicate region list.
    pub fn regions(regions: Vec<RegionCode>) -> Result<Self> {
        validate_regions(&regions)?;
        Ok(Self::Regions(regions))
    }

    fn validate(&self) -> Result<()> {
        if let Self::Regions(regions) = self {
            validate_regions(regions)?;
        }
        Ok(())
    }
}

impl Serialize for TenantPlacementLock {
    fn serialize<S>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        match self {
            Self::EuOnly => serializer.serialize_str("eu-only"),
            Self::BrOnly => serializer.serialize_str("br-only"),
            Self::Regions(regions) => {
                validate_regions(regions).map_err(|error| S::Error::custom(error.to_string()))?;
                regions.serialize(serializer)
            }
        }
    }
}

impl<'de> Deserialize<'de> for TenantPlacementLock {
    fn deserialize<D>(deserializer: D) -> std::result::Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        #[derive(Deserialize)]
        #[serde(untagged)]
        enum Wire {
            Policy(String),
            Regions(Vec<RegionCode>),
        }

        match Wire::deserialize(deserializer)? {
            Wire::Policy(value) => match value.as_str() {
                "eu-only" => Ok(Self::EuOnly),
                "br-only" => Ok(Self::BrOnly),
                _ => Err(D::Error::custom(
                    "placement lock must be eu-only, br-only, or a region list",
                )),
            },
            Wire::Regions(regions) => {
                validate_regions(&regions).map_err(|error| D::Error::custom(error.to_string()))?;
                Ok(Self::Regions(regions))
            }
        }
    }
}

/// Template a tenant database inherits placement and key policy from.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantTemplate {
    /// Template name.
    #[serde(deserialize_with = "deserialize_non_blank")]
    pub name: String,
    /// Residency constraint inherited by tenants using this template.
    #[serde(rename = "placementLock", alias = "placement_lock")]
    pub placement_lock: TenantPlacementLock,
    /// Optional key naming template.
    #[serde(
        default,
        rename = "keyTemplate",
        alias = "key_template",
        skip_serializing_if = "Option::is_none"
    )]
    pub key_template: Option<String>,
}

/// Cursor and page-size options for [`TenantsClient::list_page`].
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

    fn validate(&self) -> Result<()> {
        if self
            .limit
            .is_some_and(|limit| limit == 0 || limit > MAX_TENANT_PAGE_SIZE)
        {
            return Err(validation(
                "invalid_page_size",
                "tenant page limit must be between 1 and 1000.",
            ));
        }
        if self
            .cursor
            .as_deref()
            .is_some_and(|cursor| cursor.trim().is_empty())
        {
            return Err(validation(
                "invalid_cursor",
                "tenant page cursor cannot be blank.",
            ));
        }
        Ok(())
    }
}

/// One cursor page of tenants.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct TenantPage {
    /// Tenants in this page.
    #[serde(alias = "items")]
    pub tenants: Vec<Tenant>,
    /// Cursor for the next page, if one remains.
    #[serde(default, alias = "nextCursor", skip_serializing_if = "Option::is_none")]
    pub next_cursor: Option<String>,
}

impl<'de> Deserialize<'de> for TenantPage {
    fn deserialize<D>(deserializer: D) -> std::result::Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        // Early fixtures used a bare array; the draft envelope is the
        // canonical shape. Keep both forms available at the model boundary.
        #[derive(Deserialize)]
        #[serde(untagged)]
        enum Wire {
            Page {
                #[serde(alias = "items")]
                tenants: Vec<Tenant>,
                #[serde(default, rename = "nextCursor", alias = "next_cursor")]
                next_cursor: Option<String>,
            },
            Items(Vec<Tenant>),
        }
        match Wire::deserialize(deserializer)? {
            Wire::Page {
                tenants,
                next_cursor,
            } => Ok(Self {
                tenants,
                next_cursor,
            }),
            Wire::Items(tenants) => Ok(Self {
                tenants,
                next_cursor: None,
            }),
        }
    }
}

/// Request to provision one tenant (= one database).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateTenant {
    /// Tenant key; selects the database for tenant-scoped keys.
    #[serde(deserialize_with = "deserialize_non_blank")]
    pub key: String,
    /// Template to inherit placement and key policy from.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub template: Option<String>,
    /// Immutable placement lock. It cannot be widened after creation.
    #[serde(
        default,
        rename = "placementLock",
        alias = "placement_lock",
        skip_serializing_if = "Option::is_none"
    )]
    pub placement_lock: Option<TenantPlacementLock>,
}

impl CreateTenant {
    /// Provision a tenant from a template.
    #[must_use]
    pub fn new(key: impl Into<String>, template: impl Into<String>) -> Self {
        Self {
            key: key.into(),
            template: Some(template.into()),
            placement_lock: None,
        }
    }

    /// Set an explicit immutable placement lock.
    #[must_use]
    pub fn placement_lock(mut self, lock: TenantPlacementLock) -> Self {
        self.placement_lock = Some(lock);
        self
    }

    fn validate(&self) -> Result<()> {
        validate_key(&self.key)?;
        if let Some(template) = &self.template {
            validate_non_blank(template, "template")?;
        }
        if let Some(lock) = &self.placement_lock {
            lock.validate()?;
        }
        Ok(())
    }
}

/// A provisioned tenant.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Tenant {
    /// Optional service-assigned tenant identity.
    #[serde(
        default,
        rename = "tenantId",
        alias = "tenant_id",
        alias = "id",
        skip_serializing_if = "Option::is_none"
    )]
    pub tenant_id: Option<String>,
    /// Stable tenant key.
    #[serde(deserialize_with = "deserialize_non_blank")]
    pub key: String,
    /// Underlying database id.
    #[serde(alias = "databaseId", deserialize_with = "deserialize_non_blank")]
    pub database_id: String,
    /// Regions currently hosting the tenant database.
    #[serde(default)]
    pub regions: Vec<RegionCode>,
    /// Name of the template from which placement and key policy were derived.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub template: Option<String>,
    /// Immutable placement constraint, when returned by the service.
    #[serde(
        default,
        rename = "placementLock",
        alias = "placement_lock",
        skip_serializing_if = "Option::is_none"
    )]
    pub placement_lock: Option<TenantPlacementLock>,
    /// Creation timestamp.
    #[serde(
        default,
        rename = "createdAt",
        alias = "created_at",
        skip_serializing_if = "Option::is_none"
    )]
    pub created_at: Option<String>,
}

/// A tenant-scoped API key minted from the template.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantApiKey {
    /// Key id.
    #[serde(alias = "keyId", deserialize_with = "deserialize_non_blank")]
    pub key_id: String,
    /// Secret token (shown once at creation).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token: Option<String>,
    /// Tenant this key is scoped to.
    #[serde(alias = "tenantKey", deserialize_with = "deserialize_non_blank")]
    pub tenant_key: String,
}

/// Optional metadata for a tenant-scoped key.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct CreateTenantKey {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scopes: Option<Vec<String>>,
}

/// Optional metadata for a per-tenant support branch.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct CreateTenantBranch {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// Explicit options used when deleting a tenant.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TenantOperationOptions {
    /// Stable key used to replay a mutation safely.
    pub idempotency_key: Option<String>,
}

impl TenantOperationOptions {
    fn validate(&self) -> Result<()> {
        if let Some(key) = &self.idempotency_key {
            validate_non_blank(key, "idempotency_key")?;
            if key.len() > 128 {
                return Err(validation(
                    "invalid_idempotency_key",
                    "idempotency_key cannot exceed 128 characters.",
                ));
            }
        }
        Ok(())
    }
}

/// Tenant branch for support/debugging (per-tenant branching).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TenantBranch {
    /// Branch id.
    #[serde(alias = "branchId", deserialize_with = "deserialize_non_blank")]
    pub branch_id: String,
    /// Tenant this branch belongs to.
    #[serde(alias = "tenantKey", deserialize_with = "deserialize_non_blank")]
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

    /// Fetch the tenant's current published authorization lease. The lease
    /// is the signed, capability-equivalent token binding the tenant's
    /// authorization epoch, feature epoch, and rotation window.
    pub async fn authorization_lease(&self, tenant: &str) -> Result<Lease> {
        validate_key(tenant)?;
        let mut request = Request {
            route_name: "authorization.lease".to_owned(),
            method: Method::Get,
            url: format!(
                "{}/authorization/leases/{}",
                self.shared.pinned_base_url(None)?,
                encode_path_segment(tenant.trim())
            ),
            headers: Vec::new(),
            body: None,
        };
        // The published operation declares no security scheme: the lease is
        // fetched without the API bearer credential.
        request
            .headers
            .push((SKIP_AUTH_MARKER.to_owned(), String::new()));
        self.shared.send_typed(request).await
    }

    /// Provision a tenant database from a template.
    pub async fn create(&self, request: CreateTenant, _project_id: Option<&str>) -> Result<Tenant> {
        request.validate()?;
        Err(contract_gap("tenants.create"))
    }

    /// Fetch one tenant by key.
    pub async fn get(&self, key: &str, _project_id: Option<&str>) -> Result<Tenant> {
        validate_key(key)?;
        Err(contract_gap("tenants.get"))
    }

    /// List tenants. This operation is unavailable until the tenant route is
    /// published by TerraBase.
    pub async fn list(&self, project_id: Option<&str>) -> Result<Vec<Tenant>> {
        Ok(self
            .list_page(TenantListOptions::default(), project_id)
            .await?
            .tenants)
    }

    /// List one cursor page of tenants.
    pub async fn list_page(
        &self,
        options: TenantListOptions,
        _project_id: Option<&str>,
    ) -> Result<TenantPage> {
        options.validate()?;
        Err(contract_gap("tenants.list"))
    }

    /// Delete a tenant database after explicit confirmation.
    pub async fn delete(&self, key: &str, confirm: bool, project_id: Option<&str>) -> Result<()> {
        let idempotency_key = format!("ts_tenant_delete_{}", key.trim());
        self.delete_with_options(
            key,
            confirm,
            TenantOperationOptions {
                idempotency_key: Some(idempotency_key),
            },
            project_id,
        )
        .await
    }

    /// Delete with an explicit idempotency key, once the route is published.
    pub async fn delete_with_options(
        &self,
        key: &str,
        confirm: bool,
        options: TenantOperationOptions,
        _project_id: Option<&str>,
    ) -> Result<()> {
        validate_key(key)?;
        if !confirm {
            return Err(validation(
                "confirm_required",
                "Tenant deletion requires confirm to be true.",
            ));
        }
        options.validate()?;
        Err(contract_gap("tenants.delete"))
    }

    /// Mint a tenant-scoped API key from the template's key policy.
    pub async fn create_key(&self, key: &str, project_id: Option<&str>) -> Result<TenantApiKey> {
        self.create_key_with_request(key, CreateTenantKey::default(), project_id)
            .await
    }

    /// Mint a tenant-scoped API key with optional metadata.
    pub async fn create_key_with_request(
        &self,
        key: &str,
        request: CreateTenantKey,
        _project_id: Option<&str>,
    ) -> Result<TenantApiKey> {
        validate_key(key)?;
        validate_optional_metadata(&request.name, "name")?;
        validate_scopes(request.scopes.as_deref())?;
        Err(contract_gap("tenants.keys.create"))
    }

    /// Open a per-tenant support branch.
    pub async fn create_branch(&self, key: &str, project_id: Option<&str>) -> Result<TenantBranch> {
        self.create_branch_with_request(key, CreateTenantBranch::default(), project_id)
            .await
    }

    /// Open a support branch with optional metadata.
    pub async fn create_branch_with_request(
        &self,
        key: &str,
        request: CreateTenantBranch,
        _project_id: Option<&str>,
    ) -> Result<TenantBranch> {
        validate_key(key)?;
        validate_optional_metadata(&request.name, "name")?;
        validate_optional_metadata(&request.reason, "reason")?;
        Err(contract_gap("tenants.branches.create"))
    }
}

fn contract_gap(operation: &str) -> Error {
    Error::new(
        ErrorKind::Domain,
        "contract_gap",
        format!(
            "The TerraBase contract does not publish the {operation} tenant operation yet; no HTTP request was sent."
        ),
    )
}

fn validation(code: &str, message: &str) -> Error {
    Error::new(ErrorKind::Validation, code, message)
}

fn deserialize_non_blank<'de, D>(deserializer: D) -> std::result::Result<String, D::Error>
where
    D: Deserializer<'de>,
{
    let value = String::deserialize(deserializer)?;
    if value.trim().is_empty() {
        return Err(D::Error::custom("value cannot be blank."));
    }
    Ok(value)
}

fn validate_key(key: &str) -> Result<()> {
    validate_non_blank(key, "tenant key")?;
    let trimmed = key.trim();
    let mut chars = trimmed.chars();
    let first = chars.next();
    let valid_first = first.is_some_and(|value| value.is_ascii_alphanumeric());
    let valid_rest =
        chars.all(|value| value.is_ascii_alphanumeric() || matches!(value, '.' | '_' | ':' | '-'));
    if !valid_first
        || !valid_rest
        || trimmed.chars().any(char::is_control)
        || trimmed.len() > MAX_TENANT_KEY_BYTES
    {
        return Err(validation(
            "invalid_tenant_key",
            "tenant key must start with a letter or digit, use only letters, digits, '.', '_', ':', or '-', and be at most 64 UTF-8 bytes.",
        ));
    }
    Ok(())
}

fn validate_non_blank(value: &str, field: &str) -> Result<()> {
    if value.trim().is_empty() {
        return Err(validation(
            if field == "tenant key" {
                "invalid_tenant_key"
            } else {
                "invalid_required_field"
            },
            &format!("{field} cannot be blank."),
        ));
    }
    Ok(())
}

fn validate_optional_metadata(value: &Option<String>, field: &str) -> Result<()> {
    if let Some(value) = value {
        validate_non_blank(value, field)?;
    }
    Ok(())
}

fn validate_scopes(scopes: Option<&[String]>) -> Result<()> {
    if let Some(scopes) = scopes {
        for scope in scopes {
            validate_non_blank(scope, "scope")?;
        }
    }
    Ok(())
}

fn validate_regions(regions: &[RegionCode]) -> Result<()> {
    if regions.is_empty() || regions.len() > MAX_PLACEMENT_REGIONS {
        return Err(validation(
            "invalid_placement_lock",
            "placement lock region list must contain between 1 and 20 regions.",
        ));
    }
    for (index, region) in regions.iter().enumerate() {
        if regions[..index].contains(region) {
            return Err(validation(
                "invalid_placement_lock",
                "placement lock region list cannot contain duplicates.",
            ));
        }
    }
    Ok(())
}

fn encode_path_segment(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut encoded = String::with_capacity(value.len());
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            encoded.push(char::from(byte));
        } else {
            encoded.push('%');
            encoded.push(char::from(HEX[(byte >> 4) as usize]));
            encoded.push(char::from(HEX[(byte & 0x0f) as usize]));
        }
    }
    encoded
}
