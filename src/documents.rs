//! Document CRUD over the public REST API.
//!
//! Document bodies are raw canonical JSON values on the wire (D-30). The
//! collection list and mutation receipts are envelopes defined by the public
//! contract, while a point read is the value itself.

use serde::{Deserialize, Serialize};

use crate::client::SharedClient;
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{Method, Request};

/// Maximum encoded size of one document value (D-25).
pub const MAX_DOCUMENT_BYTES: usize = 1 << 20;

/// Maximum number of operations in one atomic document batch.
pub const MAX_BATCH_OPERATIONS: usize = 1_000;

/// Largest collection page accepted by the public API.
pub const MAX_DOCUMENT_PAGE_SIZE: u32 = 1_000;

/// Database scope for a document operation.
#[derive(Debug, Clone)]
pub struct DatabaseScope {
    /// Database id (branded string, e.g. `db_...`).
    pub database_id: String,
    /// Retained for source compatibility with the project-scoped draft.
    /// Current public database routes are direct and do not include a project.
    pub project_id: Option<String>,
}

impl DatabaseScope {
    /// Scope to one database.
    #[must_use]
    pub fn new(database_id: impl Into<String>) -> Self {
        Self {
            database_id: database_id.into(),
            project_id: None,
        }
    }

    /// Scope to one database, retaining an optional legacy project value.
    #[must_use]
    pub fn in_project(database_id: impl Into<String>, project_id: impl Into<String>) -> Self {
        Self {
            database_id: database_id.into(),
            project_id: Some(project_id.into()),
        }
    }
}

/// A point-read document. The successful point-read body is the raw JSON
/// value; the SDK supplies the requested identity because it is not repeated
/// in that response body.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Document {
    /// Branded document id.
    pub id: String,
    /// Collection the document lives in.
    pub collection: String,
    /// Opaque revision, when one is supplied by a write response.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub revision: String,
    /// Canonical JSON body.
    pub body: serde_json::Value,
    /// Creation timestamp, if a future contract response supplies one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    /// Last-write timestamp, if a future contract response supplies one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<String>,
}

/// The response to a PUT or PATCH document write.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DocumentWriteResponse {
    /// New document revision. The strong ETag is carried by the HTTP header.
    pub revision: String,
}

/// One row returned by the collection list endpoint.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DocumentRow {
    /// Document identifier.
    pub id: String,
    /// Server document version.
    pub version: i64,
    /// Opaque canonical JSON value.
    pub value: serde_json::Value,
}

/// One retained historical document version.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DocumentVersion {
    /// Revision of this historical version.
    pub revision: String,
    /// Whether this version is a deletion tombstone.
    #[serde(default)]
    pub deleted: bool,
}

/// Pagination for [`DocumentsClient::list`].
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ListOptions {
    /// Last document identifier from the preceding page.
    pub after: Option<String>,
    /// Requested page size. Zero omits the query and uses the server default.
    pub limit: Option<u32>,
}

impl ListOptions {
    /// Creates empty options, allowing the server default page size.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets the continuation identifier.
    #[must_use]
    pub fn with_after(mut self, after: impl Into<String>) -> Self {
        self.after = Some(after.into());
        self
    }

    /// Sets the page-size hint.
    #[must_use]
    pub fn with_limit(mut self, limit: u32) -> Self {
        self.limit = Some(limit);
        self
    }
}

/// One page of documents. The wire field is exactly `documents`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DocumentPage {
    /// Rows in canonical document-key order.
    pub documents: Vec<DocumentRow>,
}

/// One operation in an atomic document batch.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BatchOperation {
    /// Operation name: `put`, `patch`, or `delete`.
    pub op: String,
    /// Document identifier.
    pub id: String,
    /// Raw JSON value for `put` and `patch`; omitted for `delete`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub value: Option<serde_json::Value>,
}

impl BatchOperation {
    /// Creates a replacement operation.
    #[must_use]
    pub fn put(id: impl Into<String>, value: serde_json::Value) -> Self {
        Self {
            op: "put".to_owned(),
            id: id.into(),
            value: Some(value),
        }
    }

    /// Creates an RFC 7386 merge-patch operation.
    #[must_use]
    pub fn patch(id: impl Into<String>, value: serde_json::Value) -> Self {
        Self {
            op: "patch".to_owned(),
            id: id.into(),
            value: Some(value),
        }
    }

    /// Creates a tombstone operation.
    #[must_use]
    pub fn delete(id: impl Into<String>) -> Self {
        Self {
            op: "delete".to_owned(),
            id: id.into(),
            value: None,
        }
    }
}

/// Receipt for an atomically committed document batch.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BatchResponse {
    /// Commit sequence or operation number assigned by the server.
    pub committed: u64,
    /// Number of operations committed.
    pub count: u32,
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

    fn collection_root(&self, scope: &DatabaseScope, collection: &str) -> Result<String> {
        let name = self.shared.require_collection(collection)?;
        Ok(format!(
            "{}/collections/{}/documents",
            self.shared.database_root(scope)?,
            encode_segment(&name)?
        ))
    }

    fn document_url(&self, scope: &DatabaseScope, collection: &str, id: &str) -> Result<String> {
        Ok(format!(
            "{}/{}",
            self.collection_root(scope, collection)?,
            encode_segment(id)?
        ))
    }

    /// Create is not a public operation: documents are created by PUT with a
    /// caller-owned identifier. The method remains as a source-compatible
    /// fail-closed seam and never sends an unsupported POST request.
    pub async fn create(
        &self,
        _scope: &DatabaseScope,
        _collection: &str,
        _body: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        Err(Error::new(
            ErrorKind::Domain,
            "unsupported_operation",
            "The document API has no server-assigned-id create route; use put with a document id.",
        ))
    }

    /// Read one document. The response body is the bare canonical JSON value.
    pub async fn get(&self, scope: &DatabaseScope, collection: &str, id: &str) -> Result<Document> {
        let request = Request {
            route_name: "documents.get".to_owned(),
            method: Method::Get,
            url: self.document_url(scope, collection, id)?,
            headers: Vec::new(),
            body: None,
        };
        let body: serde_json::Value = self.shared.send_typed(request).await?;
        Ok(Document {
            id: id.to_owned(),
            collection: collection.to_owned(),
            revision: String::new(),
            body,
            created_at: None,
            updated_at: None,
        })
    }

    /// Replace a document body wholesale. The response is `{ "revision" }`.
    pub async fn put(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        self.write("documents.put", Method::Put, scope, collection, id, body)
            .await
    }

    /// Explicit alias for [`Self::put`], whose operation is an upsert.
    pub async fn upsert(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        self.put(scope, collection, id, body).await
    }

    /// Compatibility alias for [`Self::put`].
    pub async fn update(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        self.put(scope, collection, id, body).await
    }

    /// Merge `patch` into a document using RFC 7386 semantics.
    pub async fn patch(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        patch: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        self.write(
            "documents.patch",
            Method::Patch,
            scope,
            collection,
            id,
            patch,
        )
        .await
    }

    async fn write(
        &self,
        route_name: &str,
        method: Method,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        value: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        let url = self.document_url(scope, collection, id)?;
        let encoded = encode_document(&value)?;
        let request = Request {
            route_name: route_name.to_owned(),
            method,
            url,
            headers: vec![("content-type".to_owned(), "application/json".to_owned())],
            body: Some(encoded),
        };
        self.shared.send_typed(request).await
    }

    /// Delete one document (the server writes a tombstone).
    pub async fn delete(&self, scope: &DatabaseScope, collection: &str, id: &str) -> Result<()> {
        let request = Request {
            route_name: "documents.delete".to_owned(),
            method: Method::Delete,
            url: self.document_url(scope, collection, id)?,
            headers: Vec::new(),
            body: None,
        };
        self.shared.send_empty(request).await
    }

    /// List documents in canonical identifier order.
    pub async fn list(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        options: ListOptions,
    ) -> Result<DocumentPage> {
        let mut url = self.collection_root(scope, collection)?;
        if let Some(limit) = options.limit {
            if limit > MAX_DOCUMENT_PAGE_SIZE {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_limit",
                    format!("Document list limit must not exceed {MAX_DOCUMENT_PAGE_SIZE}."),
                ));
            }
            if limit > 0 {
                url.push_str(&format!("?limit={limit}"));
            }
        }
        if let Some(after) = options.after {
            if !after.trim().is_empty() {
                let separator = if url.contains('?') { '&' } else { '?' };
                url.push(separator);
                url.push_str("after=");
                url.push_str(&encode_segment(after.trim())?);
            }
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

    /// Execute one atomic batch of put, patch, and delete operations.
    pub async fn batch(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        operations: Vec<BatchOperation>,
    ) -> Result<BatchResponse> {
        if operations.is_empty() || operations.len() > MAX_BATCH_OPERATIONS {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_batch",
                format!(
                    "A document batch must contain between 1 and {MAX_BATCH_OPERATIONS} operations."
                ),
            ));
        }
        for operation in &operations {
            if operation.id.trim().is_empty() {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_batch",
                    "Document batch operation ids cannot be blank.",
                ));
            }
            match operation.op.as_str() {
                "put" | "patch" => {
                    let Some(value) = operation.value.as_ref() else {
                        return Err(Error::new(
                            ErrorKind::Validation,
                            "invalid_batch",
                            "Put and patch operations require a value.",
                        ));
                    };
                    encode_document(value)?;
                }
                "delete" if operation.value.is_none() => {}
                "delete" => {
                    return Err(Error::new(
                        ErrorKind::Validation,
                        "invalid_batch",
                        "Delete operations must omit value.",
                    ));
                }
                _ => {
                    return Err(Error::new(
                        ErrorKind::Validation,
                        "invalid_batch",
                        "Document batch operations must be put, patch, or delete.",
                    ));
                }
            }
        }
        #[derive(Serialize)]
        struct BatchRequest {
            operations: Vec<BatchOperation>,
        }
        let request = Request::json(
            "documents.batch",
            Method::Post,
            format!("{}/batch", self.collection_root(scope, collection)?),
            &BatchRequest { operations },
        )?;
        self.shared.send_typed(request).await
    }

    /// Per-document history, newest first. The response is wrapped as
    /// `{ "history": [...] }` by the public API.
    pub async fn history(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
    ) -> Result<Vec<DocumentVersion>> {
        #[derive(Deserialize)]
        struct HistoryResponse {
            history: Vec<DocumentVersion>,
        }
        let request = Request {
            route_name: "documents.history".to_owned(),
            method: Method::Get,
            url: format!("{}/history", self.document_url(scope, collection, id)?),
            headers: Vec::new(),
            body: None,
        };
        Ok(self
            .shared
            .send_typed::<HistoryResponse>(request)
            .await?
            .history)
    }
}

fn encode_document(value: &serde_json::Value) -> Result<Vec<u8>> {
    let encoded = serde_json::to_vec(value).map_err(|error| {
        Error::new(
            ErrorKind::Validation,
            "invalid_document",
            format!("Document value is not valid JSON: {error}"),
        )
    })?;
    if encoded.len() > MAX_DOCUMENT_BYTES {
        return Err(Error::new(
            ErrorKind::Validation,
            "document_too_large",
            "Document values must not exceed 1 MiB of canonical JSON.",
        ));
    }
    Ok(encoded)
}

/// Percent-encode a URL path/query component over UTF-8 bytes. Encoding
/// Unicode scalar values as `%{code point}` produces invalid URLs for any
/// non-ASCII identifier; the contract requires conventional UTF-8 escaping.
fn encode_segment(segment: &str) -> Result<String> {
    if segment.trim().is_empty() {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_id",
            "Resource ids cannot be empty.",
        ));
    }
    let mut encoded = String::with_capacity(segment.len());
    for byte in segment.as_bytes() {
        if byte.is_ascii_alphanumeric() || b"-_.~".contains(byte) {
            encoded.push(char::from(*byte));
        } else {
            encoded.push_str(&format!("%{byte:02X}"));
        }
    }
    Ok(encoded)
}

#[cfg(test)]
mod tests {
    use super::encode_segment;

    #[test]
    fn path_encoding_uses_utf8_bytes_for_unicode() {
        assert_eq!(
            encode_segment("✓/名").expect("valid segment"),
            "%E2%9C%93%2F%E5%90%8D"
        );
    }
}
