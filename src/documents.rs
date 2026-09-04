//! Document CRUD over the public REST API.
//!
//! Document bodies are raw canonical JSON values on the wire (D-30). The
//! collection list and mutation receipts are envelopes defined by the public
//! contract, while a point read is the value itself.

use serde::{Deserialize, Serialize};

use crate::client::SharedClient;
use crate::error::{Error, ErrorKind, Result};
use crate::regions::RegionCode;
use crate::transport::{Method, Request};

/// Maximum encoded size of one document value (D-25).
pub const MAX_DOCUMENT_BYTES: usize = 1 << 20;

/// Maximum number of operations in one atomic document batch.
pub const MAX_BATCH_OPERATIONS: usize = 1_000;

/// Largest collection page accepted by the public API.
pub const MAX_DOCUMENT_PAGE_SIZE: u32 = 1_000;

const MAX_DOCUMENT_AT: u64 = i64::MAX as u64;

/// Acknowledgement class requested for a document mutation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Durability {
    Regional,
    Global,
}

impl Durability {
    fn as_str(self) -> &'static str {
        match self {
            Self::Regional => "regional",
            Self::Global => "global",
        }
    }
}

/// Read consistency requested for one REST operation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Consistency {
    Eventual,
    WritesFollowReads,
}

/// Per-request controls for document mutations and batch writes.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DocumentOperationOptions {
    pub idempotency_key: Option<String>,
    pub durability: Option<Durability>,
    pub if_match: Option<String>,
    pub session: Option<String>,
    pub consistency: Option<Consistency>,
    pub region_hint: Option<RegionCode>,
}

impl DocumentOperationOptions {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }
    #[must_use]
    pub fn with_idempotency_key(mut self, key: impl Into<String>) -> Self {
        self.idempotency_key = Some(key.into());
        self
    }
    #[must_use]
    pub fn with_durability(mut self, durability: Durability) -> Self {
        self.durability = Some(durability);
        self
    }
    #[must_use]
    pub fn with_if_match(mut self, etag: impl Into<String>) -> Self {
        self.if_match = Some(etag.into());
        self
    }
    #[must_use]
    pub fn with_session(mut self, session: impl Into<String>) -> Self {
        self.session = Some(session.into());
        self
    }
    #[must_use]
    pub fn with_consistency(mut self, consistency: Consistency) -> Self {
        self.consistency = Some(consistency);
        self
    }
    #[must_use]
    pub fn with_region_hint(mut self, region: RegionCode) -> Self {
        self.region_hint = Some(region);
        self
    }
}

/// Per-request controls for a point read.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DocumentReadOptions {
    pub at: Option<u64>,
    pub if_none_match: Option<String>,
    pub session: Option<String>,
    pub consistency: Option<Consistency>,
    pub region_hint: Option<RegionCode>,
}

impl DocumentReadOptions {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }
    #[must_use]
    pub fn with_at(mut self, at: u64) -> Self {
        self.at = Some(at);
        self
    }
    #[must_use]
    pub fn with_if_none_match(mut self, etag: impl Into<String>) -> Self {
        self.if_none_match = Some(etag.into());
        self
    }
    #[must_use]
    pub fn with_session(mut self, session: impl Into<String>) -> Self {
        self.session = Some(session.into());
        self
    }
    #[must_use]
    pub fn with_consistency(mut self, consistency: Consistency) -> Self {
        self.consistency = Some(consistency);
        self
    }
    #[must_use]
    pub fn with_region_hint(mut self, region: RegionCode) -> Self {
        self.region_hint = Some(region);
        self
    }
}

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
    /// Strong ETag returned by the HTTP response, when available.
    #[serde(default, skip_serializing, skip_deserializing)]
    pub etag: Option<String>,
    /// Causal session frontier observed after this request.
    #[serde(default, skip_serializing, skip_deserializing)]
    pub session: Option<String>,
}

/// The response to a PUT or PATCH document write.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DocumentWriteResponse {
    /// New document revision. The strong ETag is carried by the HTTP header.
    pub revision: String,
    /// Strong ETag associated with the revision, when supplied by the
    /// transport. The contract carries this value in an HTTP header.
    #[serde(default, skip_serializing, skip_deserializing)]
    pub etag: Option<String>,
    /// Causal session frontier observed after this write.
    #[serde(default, skip_serializing, skip_deserializing)]
    pub session: Option<String>,
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
    /// Optional causal session frontier for this read.
    pub session: Option<String>,
    /// Optional read consistency override.
    pub consistency: Option<Consistency>,
    /// Optional serving-region hint.
    pub region_hint: Option<RegionCode>,
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

    #[must_use]
    pub fn with_session(mut self, session: impl Into<String>) -> Self {
        self.session = Some(session.into());
        self
    }

    #[must_use]
    pub fn with_consistency(mut self, consistency: Consistency) -> Self {
        self.consistency = Some(consistency);
        self
    }

    #[must_use]
    pub fn with_region_hint(mut self, region: RegionCode) -> Self {
        self.region_hint = Some(region);
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
    /// Causal session frontier observed after this batch.
    #[serde(default, skip_serializing, skip_deserializing)]
    pub session: Option<String>,
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

    fn collection_root_in(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        region_hint: Option<RegionCode>,
    ) -> Result<String> {
        let name = self.shared.require_collection(collection)?;
        Ok(format!(
            "{}/collections/{}/documents",
            self.shared.database_root_in(scope, region_hint)?,
            encode_segment(&name)?
        ))
    }

    fn document_url_in(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        region_hint: Option<RegionCode>,
    ) -> Result<String> {
        Ok(format!(
            "{}/{}",
            self.collection_root_in(scope, collection, region_hint)?,
            encode_segment(id)?
        ))
    }

    /// The public contract has no server-assigned-id document operation.
    ///
    /// This compatibility method therefore fails closed. Call
    /// [`Self::put`] (or [`Self::create_with_id`]) when the application owns
    /// the document id. In particular, do not silently turn this method into
    /// a POST to an un-published route.
    pub async fn create(
        &self,
        _scope: &DatabaseScope,
        _collection: &str,
        _body: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        Err(contract_gap(
            "documents.create",
            "the public contract only publishes caller-id PUT; use put with a document id",
        ))
    }

    /// Create a document at an application-owned id.
    ///
    /// TerraBase models create-or-replace as `PUT`; this named helper makes
    /// that fact explicit while retaining [`Self::create`] for callers of the
    /// original draft API.
    pub async fn create_with_id(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        self.put(scope, collection, id, body).await
    }

    /// Create at an application-owned id with per-request mutation controls.
    pub async fn create_with_id_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
        options: DocumentOperationOptions,
    ) -> Result<DocumentWriteResponse> {
        self.put_with_options(scope, collection, id, body, options)
            .await
    }

    /// Read one document. The response body is the bare canonical JSON value.
    pub async fn get(&self, scope: &DatabaseScope, collection: &str, id: &str) -> Result<Document> {
        self.get_with_options(scope, collection, id, DocumentReadOptions::default())
            .await
    }

    /// Read one document with conditional, causal, consistency, and routing controls.
    pub async fn get_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        options: DocumentReadOptions,
    ) -> Result<Document> {
        validate_read_options(&options)?;
        let mut url = self.document_url_in(scope, collection, id, options.region_hint)?;
        if let Some(at) = options.at {
            url.push_str(&format!("?at={at}"));
        }
        let request = Request {
            route_name: "documents.get".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        let request = apply_read_headers(request, &options);
        let body: serde_json::Value = self.shared.send_typed(request).await?;
        Ok(Document {
            id: id.to_owned(),
            collection: collection.to_owned(),
            revision: String::new(),
            body,
            created_at: None,
            updated_at: None,
            etag: None,
            session: self.shared.session_token(),
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
        self.put_with_options(
            scope,
            collection,
            id,
            body,
            DocumentOperationOptions::default(),
        )
        .await
    }

    /// Replace a document with per-request mutation controls.
    pub async fn put_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
        options: DocumentOperationOptions,
    ) -> Result<DocumentWriteResponse> {
        self.write(
            "documents.put",
            Method::Put,
            scope,
            collection,
            id,
            body,
            &options,
        )
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

    /// Explicit upsert alias with per-request mutation controls.
    pub async fn upsert_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
        options: DocumentOperationOptions,
    ) -> Result<DocumentWriteResponse> {
        self.put_with_options(scope, collection, id, body, options)
            .await
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

    /// Compatibility update alias with per-request mutation controls.
    pub async fn update_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        body: serde_json::Value,
        options: DocumentOperationOptions,
    ) -> Result<DocumentWriteResponse> {
        self.put_with_options(scope, collection, id, body, options)
            .await
    }

    /// Merge `patch` into a document using RFC 7386 semantics.
    pub async fn patch(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        patch: serde_json::Value,
    ) -> Result<DocumentWriteResponse> {
        self.patch_with_options(
            scope,
            collection,
            id,
            patch,
            DocumentOperationOptions::default(),
        )
        .await
    }

    /// Merge a patch with per-request mutation controls.
    pub async fn patch_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        patch: serde_json::Value,
        options: DocumentOperationOptions,
    ) -> Result<DocumentWriteResponse> {
        self.write(
            "documents.patch",
            Method::Patch,
            scope,
            collection,
            id,
            patch,
            &options,
        )
        .await
    }

    #[allow(clippy::too_many_arguments)]
    async fn write(
        &self,
        route_name: &str,
        method: Method,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        value: serde_json::Value,
        options: &DocumentOperationOptions,
    ) -> Result<DocumentWriteResponse> {
        validate_operation_options(options)?;
        let url = self.document_url_in(scope, collection, id, options.region_hint)?;
        let encoded = encode_document(&value)?;
        let request = Request {
            route_name: route_name.to_owned(),
            method,
            url,
            headers: vec![("content-type".to_owned(), "application/json".to_owned())],
            body: Some(encoded),
        };
        let request = apply_operation_headers(request, options);
        let mut response: DocumentWriteResponse = self.shared.send_typed(request).await?;
        response.etag = Some(format!("\"{}\"", response.revision));
        response.session = self.shared.session_token();
        Ok(response)
    }

    /// Delete one document (the server writes a tombstone).
    pub async fn delete(&self, scope: &DatabaseScope, collection: &str, id: &str) -> Result<()> {
        self.delete_with_options(scope, collection, id, DocumentOperationOptions::default())
            .await
    }

    /// Delete a document with per-request mutation controls.
    pub async fn delete_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        options: DocumentOperationOptions,
    ) -> Result<()> {
        validate_operation_options(&options)?;
        let request = Request {
            route_name: "documents.delete".to_owned(),
            method: Method::Delete,
            url: self.document_url_in(scope, collection, id, options.region_hint)?,
            headers: Vec::new(),
            body: None,
        };
        self.shared
            .send_empty(apply_operation_headers(request, &options))
            .await
    }

    /// List documents in canonical identifier order.
    pub async fn list(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        options: ListOptions,
    ) -> Result<DocumentPage> {
        validate_list_options(&options)?;
        let mut url = self.collection_root_in(scope, collection, options.region_hint)?;
        append_list_query(&mut url, &options)?;
        let request = Request {
            route_name: "documents.list".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        self.shared
            .send_typed(apply_list_headers(request, &options))
            .await
    }

    /// Execute one atomic batch of put, patch, and delete operations.
    pub async fn batch(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        operations: Vec<BatchOperation>,
    ) -> Result<BatchResponse> {
        self.batch_with_options(
            scope,
            collection,
            operations,
            DocumentOperationOptions::default(),
        )
        .await
    }

    /// Execute an atomic batch with per-request mutation controls.
    pub async fn batch_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        operations: Vec<BatchOperation>,
        options: DocumentOperationOptions,
    ) -> Result<BatchResponse> {
        validate_operation_options(&options)?;
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
            format!(
                "{}/batch",
                self.collection_root_in(scope, collection, options.region_hint)?
            ),
            &BatchRequest { operations },
        )?;
        let mut response: BatchResponse = self
            .shared
            .send_typed(apply_operation_headers(request, &options))
            .await?;
        response.session = self.shared.session_token();
        Ok(response)
    }

    /// Per-document history, newest first. The response is wrapped as
    /// `{ "history": [...] }` by the public API.
    pub async fn history(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
    ) -> Result<Vec<DocumentVersion>> {
        self.history_with_options(scope, collection, id, ListOptions::default())
            .await
    }

    /// Return retained versions with bounded pagination and read controls.
    pub async fn history_with_options(
        &self,
        scope: &DatabaseScope,
        collection: &str,
        id: &str,
        options: ListOptions,
    ) -> Result<Vec<DocumentVersion>> {
        validate_list_options(&options)?;
        #[derive(Deserialize)]
        struct HistoryResponse {
            history: Vec<DocumentVersion>,
        }
        let mut url = format!(
            "{}/history",
            self.document_url_in(scope, collection, id, options.region_hint)?
        );
        append_list_query(&mut url, &options)?;
        let request = Request {
            route_name: "documents.history".to_owned(),
            method: Method::Get,
            url,
            headers: Vec::new(),
            body: None,
        };
        let request = apply_list_headers(request, &options);
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

fn validate_header_value(value: &str, field: &str, max: usize) -> Result<()> {
    if value.trim().is_empty() || value.len() > max || value.chars().any(char::is_control) {
        return Err(Error::new(
            ErrorKind::Validation,
            format!("invalid_{field}"),
            format!("{field} must be nonblank, at most {max} characters, and contain no control characters."),
        ));
    }
    Ok(())
}

fn validate_session(value: &str) -> Result<()> {
    if value != value.trim() || normalize_session_token(value).is_none() {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_session",
            "session must be a canonical unpadded base64url token of at most 16384 characters.",
        ));
    }
    Ok(())
}

fn normalize_session_token(value: &str) -> Option<&str> {
    if value.is_empty()
        || value.len() > 16_384
        || value.len() % 4 == 1
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
    {
        return None;
    }
    Some(value)
}

fn validate_operation_options(options: &DocumentOperationOptions) -> Result<()> {
    if let Some(key) = options.idempotency_key.as_deref() {
        validate_header_value(
            key,
            "idempotency_key",
            crate::client::MAX_IDEMPOTENCY_KEY_LENGTH,
        )?;
    }
    if let Some(etag) = options.if_match.as_deref() {
        validate_header_value(etag, "if_match", 1_024)?;
    }
    if let Some(session) = options.session.as_deref() {
        validate_session(session)?;
    }
    Ok(())
}

fn validate_read_options(options: &DocumentReadOptions) -> Result<()> {
    if options.at.is_some_and(|at| at > MAX_DOCUMENT_AT) {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_at",
            "Point-in-time read positions must fit a signed 64-bit integer.",
        ));
    }
    if let Some(etag) = options.if_none_match.as_deref() {
        validate_header_value(etag, "if_none_match", 1_024)?;
    }
    if let Some(session) = options.session.as_deref() {
        validate_session(session)?;
    }
    Ok(())
}

fn validate_list_options(options: &ListOptions) -> Result<()> {
    if options
        .limit
        .is_some_and(|limit| !(1..=MAX_DOCUMENT_PAGE_SIZE).contains(&limit))
    {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_limit",
            format!("Document list limit must be between 1 and {MAX_DOCUMENT_PAGE_SIZE}."),
        ));
    }
    if let Some(session) = options.session.as_deref() {
        validate_session(session)?;
    }
    Ok(())
}

fn append_list_query(url: &mut String, options: &ListOptions) -> Result<()> {
    if let Some(limit) = options.limit {
        url.push_str(&format!("?limit={limit}"));
    }
    if let Some(after) = options
        .after
        .as_deref()
        .filter(|value| !value.trim().is_empty())
    {
        let separator = if url.contains('?') { '&' } else { '?' };
        url.push(separator);
        url.push_str("after=");
        url.push_str(&encode_segment(after.trim())?);
    }
    Ok(())
}

fn apply_operation_headers(mut request: Request, options: &DocumentOperationOptions) -> Request {
    if let Some(region) = options.region_hint {
        request.headers.push((
            "x-terrascale-preferred-region".to_owned(),
            region.as_header_str().to_owned(),
        ));
    }
    if let Some(key) = options.idempotency_key.as_deref() {
        request
            .headers
            .push(("idempotency-key".to_owned(), key.trim().to_owned()));
    }
    if let Some(etag) = options.if_match.as_deref() {
        request
            .headers
            .push(("if-match".to_owned(), etag.trim().to_owned()));
    }
    if let Some(durability) = options.durability {
        request.headers.push((
            "x-terrascale-durability".to_owned(),
            durability.as_str().to_owned(),
        ));
    }
    if let Some(session) = options.session.as_deref() {
        request
            .headers
            .push(("x-terrascale-session".to_owned(), session.trim().to_owned()));
    }
    if options.consistency == Some(Consistency::Eventual) && options.session.is_none() {
        request
            .headers
            .push((crate::client::SKIP_SESSION_MARKER.to_owned(), String::new()));
    }
    request
}

fn apply_read_headers(mut request: Request, options: &DocumentReadOptions) -> Request {
    if let Some(region) = options.region_hint {
        request.headers.push((
            "x-terrascale-preferred-region".to_owned(),
            region.as_header_str().to_owned(),
        ));
    }
    if let Some(etag) = options.if_none_match.as_deref() {
        request
            .headers
            .push(("if-none-match".to_owned(), etag.trim().to_owned()));
    }
    if let Some(session) = options.session.as_deref() {
        request
            .headers
            .push(("x-terrascale-session".to_owned(), session.trim().to_owned()));
    }
    if options.consistency == Some(Consistency::Eventual) && options.session.is_none() {
        request
            .headers
            .push((crate::client::SKIP_SESSION_MARKER.to_owned(), String::new()));
    }
    request
}

fn apply_list_headers(mut request: Request, options: &ListOptions) -> Request {
    if let Some(region) = options.region_hint {
        request.headers.push((
            "x-terrascale-preferred-region".to_owned(),
            region.as_header_str().to_owned(),
        ));
    }
    if let Some(session) = options.session.as_deref() {
        request
            .headers
            .push(("x-terrascale-session".to_owned(), session.trim().to_owned()));
    }
    if options.consistency == Some(Consistency::Eventual) && options.session.is_none() {
        request
            .headers
            .push((crate::client::SKIP_SESSION_MARKER.to_owned(), String::new()));
    }
    request
}

fn contract_gap(operation: &str, fix: &str) -> Error {
    Error::new(
        ErrorKind::Domain,
        "contract_gap",
        format!(
            "The TerraBase contract does not publish the {operation} operation; no HTTP request was sent. Fix: {fix}."
        ),
    )
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
