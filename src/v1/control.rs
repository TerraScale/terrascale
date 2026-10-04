//! The `/v1/dashboard` control plane (control-plane-v1.md): tenants,
//! databases, operations, collections, keys and regions.
//!
//! Every mutation takes an explicit [`IdempotencyKey`]: the server requires
//! one, and reusing the same key when retrying the same mutation returns the
//! original response. Mutations are therefore replay-safe and retried on
//! transient failures.

use std::time::Duration;

use reqwest::Method;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::value::RawValue;

use super::client::{Client, Req};
use super::errors::{Error, Result};
use super::ids::IdempotencyKey;
use super::query::Filter;
use super::types::{opt_u64_string, u64_string, Durability};

/// Control-plane client; its key must have the `control` role.
#[derive(Debug, Clone)]
pub struct ControlClient {
    client: Client,
}

/// One page of a list.
#[derive(Debug, Clone, Deserialize)]
#[serde(bound(deserialize = "T: DeserializeOwned"))]
pub struct Page<T> {
    pub items: Vec<T>,
    #[serde(default)]
    pub next_cursor: Option<String>,
}

/// List pagination. `limit` is 1–200 (`None`: server default 50).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ListOptions {
    pub limit: Option<u32>,
    pub cursor: Option<String>,
}

impl ListOptions {
    fn query(&self) -> Vec<(&'static str, String)> {
        let mut q = Vec::new();
        if let Some(l) = self.limit {
            q.push(("limit", l.to_string()));
        }
        if let Some(c) = &self.cursor {
            q.push(("cursor", c.clone()));
        }
        q
    }
}

/// A tenant's quotas.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TenantLimits {
    pub max_databases: u32,
    #[serde(with = "u64_string")]
    pub max_storage_bytes: u64,
    pub max_rate_per_second: u32,
}

/// A tenant (one dashboard project).
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct Tenant {
    pub id: String,
    pub name: String,
    pub external_ref: String,
    /// `active`, `suspended` or `deleting`.
    pub status: String,
    pub limits: TenantLimits,
    #[serde(default)]
    pub advisor_external: bool,
    /// Unix milliseconds.
    #[serde(with = "u64_string")]
    pub created_at: u64,
}

/// Body of [`ControlClient::create_tenant`].
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct CreateTenant {
    pub name: String,
    /// Unique across tenants (the dashboard project ID).
    pub external_ref: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub limits: Option<TenantLimits>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub advisor_external: Option<bool>,
}

/// Body of [`ControlClient::update_tenant`]; `None` members are unchanged.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct UpdateTenant {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// `active` or `suspended`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub limits: Option<TenantLimits>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub advisor_external: Option<bool>,
}

/// A database's shape-log retention.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Retention {
    #[serde(with = "u64_string")]
    pub shape_log_bytes: u64,
    #[serde(with = "u64_string")]
    pub shape_log_age_seconds: u64,
}

/// A database record.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct DatabaseInfo {
    pub id: String,
    pub tenant_id: String,
    pub name: String,
    pub home_region: String,
    /// `ready`, `deleting` or `failed`.
    pub status: String,
    pub durability: Durability,
    pub retention: Retention,
    #[serde(with = "u64_string")]
    pub created_at: u64,
}

/// Body of [`ControlClient::create_database`].
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct CreateDatabase {
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub home_region: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub durability: Option<Durability>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub retention: Option<Retention>,
}

/// A failed operation's error.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct OperationFailure {
    pub code: String,
    #[serde(default)]
    pub message: String,
}

/// An asynchronous control-plane operation.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct Operation {
    pub id: String,
    pub kind: String,
    /// `running`, `succeeded` or `failed`.
    pub status: String,
    pub resource: String,
    #[serde(default)]
    pub error: Option<OperationFailure>,
    #[serde(with = "u64_string")]
    pub created_at: u64,
    #[serde(default, with = "opt_u64_string")]
    pub finished_at: Option<u64>,
}

/// A unique constraint.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct UniqueConstraint {
    pub name: String,
    pub paths: Vec<String>,
    /// `Some("lower")` folds case before comparing.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub transform: Option<String>,
}

/// An enforced schema; `None` means schemaless.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Schema {
    pub fields: Vec<Field>,
}

/// A schema field. `type` is `null`, `bool`, `i64`, `utf8`, `bytes`, `map`
/// or `array`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Field {
    pub name: String,
    #[serde(rename = "type")]
    pub kind: String,
    #[serde(default)]
    pub required: bool,
    #[serde(default)]
    pub nullable: bool,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub fields: Vec<Field>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub element: Option<Element>,
}

/// An array field's element type.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Element {
    #[serde(rename = "type")]
    pub kind: String,
    #[serde(default)]
    pub nullable: bool,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub fields: Vec<Field>,
}

/// One index key.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct IndexKey {
    pub path: String,
    /// `asc` or `desc`.
    pub order: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub transform: Option<String>,
}

/// An index declaration.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct IndexSpec {
    pub name: String,
    pub keys: Vec<IndexKey>,
}

/// A collection record.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct CollectionInfo {
    pub id: String,
    pub name: String,
    #[serde(with = "u64_string")]
    pub generation: u64,
    #[serde(default)]
    pub schema: Option<Schema>,
    #[serde(default)]
    pub ttl_path: Option<String>,
    #[serde(default)]
    pub unique: Vec<UniqueConstraint>,
    /// `auto` or `manual`.
    pub advisor: String,
    #[serde(with = "u64_string")]
    pub created_at: u64,
}

/// Body of [`ControlClient::create_collection`].
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct CreateCollection {
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub schema: Option<Schema>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_path: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub unique: Vec<UniqueConstraint>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub indexes: Vec<IndexSpec>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub advisor: Option<String>,
}

/// Body of [`ControlClient::update_collection`]. `None` members are
/// unchanged; `ttl_path: Some(None)` clears the TTL.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct UpdateCollection {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub schema: Option<Schema>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_path: Option<Option<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unique: Option<Vec<UniqueConstraint>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub advisor: Option<String>,
}

/// A data key grant. `database` and `collections` accept `"*"`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Grant {
    pub database: String,
    pub collections: Vec<String>,
    /// `read`, `write`, `shape`.
    pub permissions: Vec<String>,
}

/// A key's rate limit.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct KeyRate {
    pub per_second: u32,
    pub burst: u32,
    pub max_in_flight: u32,
}

/// An API key record. The secret is never returned after creation.
#[derive(Debug, Clone, Deserialize)]
pub struct Key {
    /// 22-character base64url key ID.
    pub id: String,
    #[serde(default)]
    pub tenant_id: Option<String>,
    pub name: String,
    /// `data`, `control` or `edge`.
    pub role: String,
    #[serde(default)]
    pub grants: Vec<Grant>,
    /// The mandatory row filter (query-v1 grammar), raw.
    #[serde(default)]
    pub row_filter: Option<Box<RawValue>>,
    #[serde(default)]
    pub rate: Option<KeyRate>,
    #[serde(default, with = "opt_u64_string")]
    pub expires_at: Option<u64>,
    #[serde(default, with = "opt_u64_string")]
    pub revoked_at: Option<u64>,
    #[serde(with = "u64_string")]
    pub created_at: u64,
    pub last_four: String,
}

/// A created or rotated key with its one-time token.
#[derive(Clone, Deserialize)]
pub struct KeyWithToken {
    pub key: Key,
    /// The `tbk1.…` token; returned only once.
    pub token: String,
}

impl std::fmt::Debug for KeyWithToken {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("KeyWithToken")
            .field("key", &self.key)
            .field("token", &"<redacted>")
            .finish()
    }
}

/// Body of [`ControlClient::create_key`] (role `data`).
#[derive(Debug, Clone, Default, Serialize)]
pub struct CreateKey {
    pub name: String,
    pub grants: Vec<Grant>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub row_filter: Option<Filter>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rate: Option<KeyRate>,
    /// Lifetime in seconds; `None` never expires.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_seconds: Option<u64>,
}

/// A region.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct Region {
    pub code: String,
    pub name: String,
    pub status: String,
}

/// A created key whose token is not a `tbk1` key is an unusable but applied
/// mutation.
fn checked_token(res: Result<KeyWithToken>) -> Result<KeyWithToken> {
    let out = res?;
    if !super::client::valid_token(&out.token) {
        return Err(Error::UndecodableAck {
            status: 201,
            message: "the returned token is not a tbk1 key".into(),
        });
    }
    Ok(out)
}

const D: [&str; 2] = ["v1", "dashboard"];

fn path<'a>(rest: &[&'a str]) -> Vec<&'a str> {
    let mut p = D.to_vec();
    p.extend_from_slice(rest);
    p
}

impl ControlClient {
    /// Wraps a client whose key is a control key.
    #[must_use]
    pub fn new(client: Client) -> Self {
        Self { client }
    }

    async fn get<T: DeserializeOwned>(
        &self,
        segments: Vec<&str>,
        query: Vec<(&str, String)>,
    ) -> Result<T> {
        let mut req = Req::new(Method::GET, segments);
        req.query = query;
        self.client.send(req).await?.decode()
    }

    async fn mutate<T: DeserializeOwned, B: Serialize + ?Sized>(
        &self,
        method: Method,
        key: &IdempotencyKey,
        segments: Vec<&str>,
        body: Option<&B>,
    ) -> Result<T> {
        let mut req = Req::new(method, segments);
        if let Some(b) = body {
            req = req.json(b)?;
        }
        req.idempotency_key = Some(key);
        self.client.send(req).await?.decode()
    }

    // Tenants.

    /// `POST /tenants`. `external_ref` is unique (`409 name_taken`).
    pub async fn create_tenant(&self, key: &IdempotencyKey, req: &CreateTenant) -> Result<Tenant> {
        self.mutate(Method::POST, key, path(&["tenants"]), Some(req))
            .await
    }

    /// `GET /tenants`, optionally filtered by `external_ref`.
    pub async fn list_tenants(
        &self,
        external_ref: Option<&str>,
        opts: &ListOptions,
    ) -> Result<Page<Tenant>> {
        let mut q = opts.query();
        if let Some(r) = external_ref {
            q.push(("external_ref", r.to_owned()));
        }
        self.get(path(&["tenants"]), q).await
    }

    /// The tenant with `external_ref`, if any.
    pub async fn find_tenant(&self, external_ref: &str) -> Result<Option<Tenant>> {
        Ok(self
            .list_tenants(Some(external_ref), &ListOptions::default())
            .await?
            .items
            .into_iter()
            .find(|t| t.external_ref == external_ref))
    }

    /// `GET /tenants/{tenant}`.
    pub async fn get_tenant(&self, tenant: &str) -> Result<Tenant> {
        self.get(path(&["tenants", tenant]), Vec::new()).await
    }

    /// `PATCH /tenants/{tenant}`.
    pub async fn update_tenant(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        req: &UpdateTenant,
    ) -> Result<Tenant> {
        self.mutate(Method::PATCH, key, path(&["tenants", tenant]), Some(req))
            .await
    }

    /// `DELETE /tenants/{tenant}`; returns the deletion operation.
    pub async fn delete_tenant(&self, key: &IdempotencyKey, tenant: &str) -> Result<Operation> {
        self.mutate::<_, ()>(Method::DELETE, key, path(&["tenants", tenant]), None)
            .await
    }

    // Databases.

    /// `POST /tenants/{tenant}/databases` (synchronous).
    pub async fn create_database(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        req: &CreateDatabase,
    ) -> Result<DatabaseInfo> {
        self.mutate(
            Method::POST,
            key,
            path(&["tenants", tenant, "databases"]),
            Some(req),
        )
        .await
    }

    /// `GET /tenants/{tenant}/databases`.
    pub async fn list_databases(
        &self,
        tenant: &str,
        opts: &ListOptions,
    ) -> Result<Page<DatabaseInfo>> {
        self.get(path(&["tenants", tenant, "databases"]), opts.query())
            .await
    }

    /// `GET /tenants/{tenant}/databases/{db}`.
    pub async fn get_database(&self, tenant: &str, database: &str) -> Result<DatabaseInfo> {
        self.get(
            path(&["tenants", tenant, "databases", database]),
            Vec::new(),
        )
        .await
    }

    /// `DELETE /tenants/{tenant}/databases/{db}`; returns the operation.
    pub async fn delete_database(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        database: &str,
    ) -> Result<Operation> {
        self.mutate::<_, ()>(
            Method::DELETE,
            key,
            path(&["tenants", tenant, "databases", database]),
            None,
        )
        .await
    }

    // Operations.

    /// `GET /operations/{id}`.
    pub async fn get_operation(&self, id: &str) -> Result<Operation> {
        self.get(path(&["operations", id]), Vec::new()).await
    }

    /// Polls an operation every `interval` until it finishes. A failed
    /// operation becomes [`Error::OperationFailed`].
    pub async fn wait_operation(&self, id: &str, interval: Duration) -> Result<Operation> {
        loop {
            let op = self.get_operation(id).await?;
            match op.status.as_str() {
                "succeeded" => return Ok(op),
                "failed" => {
                    let (code, message) = op
                        .error
                        .map(|e| (e.code, Some(e.message).filter(|m| !m.is_empty())))
                        .unwrap_or_else(|| ("operation_failed".into(), None));
                    return Err(Error::OperationFailed {
                        id: op.id,
                        code,
                        message,
                    });
                }
                "running" => tokio::time::sleep(interval).await,
                other => {
                    return Err(Error::Decode(format!(
                        "operation {} has unknown status {other:?}",
                        op.id
                    )))
                }
            }
        }
    }

    // Collections.

    /// `POST …/databases/{db}/collections`.
    pub async fn create_collection(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        database: &str,
        req: &CreateCollection,
    ) -> Result<CollectionInfo> {
        self.mutate(
            Method::POST,
            key,
            path(&["tenants", tenant, "databases", database, "collections"]),
            Some(req),
        )
        .await
    }

    /// `GET …/databases/{db}/collections`.
    pub async fn list_collections(
        &self,
        tenant: &str,
        database: &str,
        opts: &ListOptions,
    ) -> Result<Page<CollectionInfo>> {
        self.get(
            path(&["tenants", tenant, "databases", database, "collections"]),
            opts.query(),
        )
        .await
    }

    /// `GET …/collections/{collection}`.
    pub async fn get_collection(
        &self,
        tenant: &str,
        database: &str,
        collection: &str,
    ) -> Result<CollectionInfo> {
        self.get(
            path(&[
                "tenants",
                tenant,
                "databases",
                database,
                "collections",
                collection,
            ]),
            Vec::new(),
        )
        .await
    }

    /// `PATCH …/collections/{collection}`.
    pub async fn update_collection(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        database: &str,
        collection: &str,
        req: &UpdateCollection,
    ) -> Result<CollectionInfo> {
        self.mutate(
            Method::PATCH,
            key,
            path(&[
                "tenants",
                tenant,
                "databases",
                database,
                "collections",
                collection,
            ]),
            Some(req),
        )
        .await
    }

    /// `DELETE …/collections/{collection}`; returns the operation.
    pub async fn delete_collection(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        database: &str,
        collection: &str,
    ) -> Result<Operation> {
        self.mutate::<_, ()>(
            Method::DELETE,
            key,
            path(&[
                "tenants",
                tenant,
                "databases",
                database,
                "collections",
                collection,
            ]),
            None,
        )
        .await
    }

    // Keys.

    /// `POST /tenants/{tenant}/keys`: a data key; the token is returned once.
    pub async fn create_key(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        req: &CreateKey,
    ) -> Result<KeyWithToken> {
        if let Some(f) = &req.row_filter {
            f.validate()?;
        }
        checked_token(
            self.mutate(
                Method::POST,
                key,
                path(&["tenants", tenant, "keys"]),
                Some(req),
            )
            .await,
        )
    }

    /// `GET /tenants/{tenant}/keys`.
    pub async fn list_keys(&self, tenant: &str, opts: &ListOptions) -> Result<Page<Key>> {
        self.get(path(&["tenants", tenant, "keys"]), opts.query())
            .await
    }

    /// `GET /tenants/{tenant}/keys/{key}`.
    pub async fn get_key(&self, tenant: &str, key_id: &str) -> Result<Key> {
        self.get(path(&["tenants", tenant, "keys", key_id]), Vec::new())
            .await
    }

    /// `DELETE /tenants/{tenant}/keys/{key}`: revokes immediately.
    pub async fn revoke_key(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        key_id: &str,
    ) -> Result<Key> {
        self.mutate::<_, ()>(
            Method::DELETE,
            key,
            path(&["tenants", tenant, "keys", key_id]),
            None,
        )
        .await
    }

    /// `POST /tenants/{tenant}/keys/{key}/rotate`: a new key and token; the old
    /// key expires after `grace_seconds` (0–86400; `None`: server default).
    pub async fn rotate_key(
        &self,
        key: &IdempotencyKey,
        tenant: &str,
        key_id: &str,
        grace_seconds: Option<u32>,
    ) -> Result<KeyWithToken> {
        #[derive(Serialize)]
        struct Body {
            #[serde(skip_serializing_if = "Option::is_none")]
            grace_seconds: Option<u32>,
        }
        checked_token(
            self.mutate(
                Method::POST,
                key,
                path(&["tenants", tenant, "keys", key_id, "rotate"]),
                Some(&Body { grace_seconds }),
            )
            .await,
        )
    }

    // Regions.

    /// `GET /regions`.
    pub async fn list_regions(&self, opts: &ListOptions) -> Result<Page<Region>> {
        self.get(path(&["regions"]), opts.query()).await
    }
}
