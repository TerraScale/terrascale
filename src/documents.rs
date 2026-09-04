//! Document CRUD over the public REST API.
//!
//! Paths follow the pinned contract (`CONTRACT_VERSION`); every document
//! carries an opaque string `revision` used for conditional reads
//! (`If-None-Match`) and conflict reporting.

use serde::{Deserialize, Serialize};

use crate::client::SharedClient;
use crate::error::Result;
use crate::transport::{Method, Request};

/// Database scope for a document operation.
#[derive(Debug, Clone)]
pub struct DatabaseScope {
    /// Database id (branded string, e.g. `db_...`).
    pub database_id: String,
    /// Project scope; falls back to the client config when absent.
    pub project_id: Option<String>,
}

impl DatabaseScope {
    /// Scope to one database, inheriting the project from the client config.
    #[must_use]
    pub fn new(database_id: impl Into<String>) -> Self {
        Self {
            database_id: database_id.into(),
            project_id: None,
        }
    }

    /// Scope to one database inside an explicit project.
    #[must_use]
    pub fn in_project(
        database_id: impl Into<String>,
        project_id: impl Into<String>,
    ) -> Self {
        Self {
            database_id: database_id.into(),
            project_id: Some(project_id.into()),
        }
    }
}

/// A stored document: opaque canonical JSON body plus metadata.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Document {
    /// Branded document id.
    pub id: String,
    /// Collection the document lives in.
    pub collection: String,
    /// Opaque revision for ETags and conflict reporting.
    pub revision: String,
    /// Canonical JSON body.
    pub body: serde_json::Value,
    /// Creation timestamp, if the server returned one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    /// Last-write timestamp, if the server returned one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<String>,
}

/// A retained historical version of a document.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DocumentVersion {
    /// Revision of this historical version.
    pub revision: String,
    /// Body bytes of this version.
    pub body: serde_json::Value,
    /// Why this version lost (dot, HLC, policy for conflict merges).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub superseded_by: Option<String>,
    /// Commit timestamp.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub committed_at: Option<String>,
}

/// Pagination for [`DocumentsClient::list`].
#[derive(Debug, Clone, Default)]
pub struct ListOptions {
    /// Continuation cursor from a previous page.
    pub cursor: Option<String>,
    /// Page size hint.
    pub limit: Option<u32>,
}

/// One page of documents.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DocumentPage {
    /// Documents in this page.
    pub items: Vec<Document>,
    /// Cursor for the next page, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next_cursor: Option<String>,
}

/// Document CRUD client. Obtain from [`crate::TerraScaleClient::documents`].
#[derive(Debug, Clone)]
pub struct DocumentsClient {
    shared: SharedClient,
}

impl DocumentsClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }

    fn scope_root(&self, scope: &DatabaseScope) -> Result<String> {
        self.shared.database_root(scope)
    }

    fn collection_root(&self, scope: &DatabaseScope, collection: &str) -> Result<String> {
        self.shared
            .require_collection(collection)
            .map(|name| format!("{}/collections/{name}/documents", self.scope_root(scope)?))
    }

    /// Create a document with a server-assigned id.
    pub async fn create(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        body: serde_json::Value,
    ) -> Result<Document> {
        let root = self.collection_root(scope, collection)?;
        let request = Request::json("documents.create", Method::Post, root, &body)?;
        self.shared.send_typed(request).await
    }

    /// Read one document by id.
    pub async fn get(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
    ) -> Result<Document> {
        let root = self.collection_root(scope, collection)?;
        let request = Request {
            route_name: "documents.get".to_owned(),
            method: Method::Get,
            url: format!("{}/{}", root, encode_segment(id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }

    /// Replace a document body wholesale.
    pub async fn put(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
    ) -> Result<Document> {
        let root = self.collection_root(scope, collection)?;
        let request = Request::json(
            "documents.put",
            Method::Put,
            format!("{}/{}", root, encode_segment(id)?),
            &body,
        )?;
        self.shared.send_typed(request).await
    }

    /// Merge `patch` into a document (remove-wins per field on conflict).
    pub async fn patch(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        patch: serde_json::Value,
    ) -> Result<Document> {
        let root = self.collection_root(scope, collection)?;
        let request = Request::json(
            "documents.patch",
            Method::Patch,
            format!("{}/{}", root, encode_segment(id)?),
            &patch,
        )?;
        self.shared.send_typed(request).await
    }

    /// Delete one document.
    pub async fn delete(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
    ) -> Result<()> {
        let root = self.collection_root(scope, collection)?;
        let request = Request {
            route_name: "documents.delete".to_owned(),
            method: Method::Delete,
            url: format!("{}/{}", root, encode_segment(id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_empty(request).await
    }

    /// List documents in a collection.
    pub async fn list(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        options: ListOptions,
    ) -> Result<DocumentPage> {
        let root = self.collection_root(scope, collection)?;
        let mut url = root;
        let mut query = Vec::new();
        if let Some(cursor) = &options.cursor {
            query.push(format!("cursor={}", encode_segment(cursor)?));
        }
        if let Some(limit) = options.limit {
            query.push(format!("take={limit}"));
        }
        if !query.is_empty() {
            url.push('?');
            url.push_str(&query.join("&"));
        }
        let request = Request {
            route_name: "documents.list".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }

    /// Per-document history: why each version won or lost.
    pub async fn history(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
    ) -> Result<Vec<DocumentVersion>> {
        let root = self.collection_root(scope, collection)?;
        let request = Request {
            route_name: "documents.history".to_owned(),
            method: Method::Get,
            url: format!("{}/{}/history", root, encode_segment(id)?),
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_typed(request).await
    }
}

fn encode_segment(segment: &str) -> Result<String> {
    if segment.is_empty() {
        return Err(crate::error::Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_id",
            "Resource ids cannot be empty.",
        ));
    }
    Ok(segment
        .chars()
        .flat_map(|c| {
            if c.is_ascii_alphanumeric() || "-_.~".contains(c) {
                vec![c.to_string()]
            } else {
                vec![format!("%{0:02X}", c as u32)]
            }
        })
        .collect())
}
