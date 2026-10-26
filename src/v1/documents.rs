//! Documents: point reads and single-document writes (api-v1.md "Documents").

use reqwest::{Method, StatusCode};
use serde::de::{DeserializeOwned, Error as _};
use serde::{Deserialize, Deserializer, Serialize};
use serde_json::value::RawValue;

use super::client::{Client, Req, Resp};
use super::errors::{Error, Result};
use super::ids::{check_database_selector, check_name, DocumentId, Version};
use super::shapes::Shape;
use super::tx::{Tx, TxResult};
use super::types::{u64_string, Durability, Number};

/// A handle to one database by current name or immutable ULID. Performs no I/O.
#[derive(Debug, Clone)]
pub struct Database {
    client: Client,
    name: String,
}

impl Database {
    pub(crate) fn new(client: Client, name: String) -> Self {
        Self { client, name }
    }

    /// The selector used to construct this handle.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// The client this handle uses.
    #[must_use]
    pub fn client(&self) -> &Client {
        &self.client
    }

    /// Creates this database through the project management API.
    /// Requires `databases:write`; a conflict is returned for an existing name.
    pub async fn create(&self) -> Result<ProjectDatabaseInfo> {
        check_name("database", &self.name)?;
        self.client
            .send_project(
                Req::new(Method::POST, vec!["v1", "databases"])
                    .json(&serde_json::json!({"name": self.name}))?,
            )
            .await?
            .decode_status(&[StatusCode::CREATED])
    }

    /// A handle to the named collection. Performs no I/O.
    #[must_use]
    pub fn collection(&self, name: impl Into<String>) -> Collection {
        Collection {
            db: self.clone(),
            name: name.into(),
        }
    }

    /// Creates a schemaless collection on C# TerraBase.
    pub async fn create_collection(&self, name: &str) -> Result<ProjectCollectionInfo> {
        check_database_selector(&self.name)?;
        check_name("collection", name)?;
        let req = Req::new(
            Method::POST,
            vec!["v1", "databases", &self.name, "collections"],
        )
        .json(&serde_json::json!({"name": name}))?;
        self.client
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::CREATED])
    }

    /// Lists C# TerraBase collections with the producer's opaque cursor.
    pub async fn list_collections(
        &self,
        limit: Option<u32>,
        cursor: Option<&str>,
    ) -> Result<ProjectCollectionPage> {
        check_database_selector(&self.name)?;
        if limit.is_some_and(|n| !(1..=200).contains(&n)) {
            return Err(Error::InvalidInput(
                "collection limit must be 1 to 200".into(),
            ));
        }
        let mut req = Req::new(
            Method::GET,
            vec!["v1", "databases", &self.name, "collections"],
        );
        if let Some(limit) = limit {
            req.query.push(("limit", limit.to_string()));
        }
        if let Some(cursor) = cursor {
            req.query.push(("cursor", cursor.to_owned()));
        }
        self.client
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }

    /// Atomically commits `tx` (`POST /v1/databases/{db}/transactions`).
    ///
    /// Failures name the failing op through [`Error::op`]. An uncertain
    /// mutation must be reconciled by reading state before an explicit retry.
    pub async fn commit(&self, tx: &Tx) -> Result<TxResult> {
        check_database_selector(&self.name)?;
        let body = tx.encode()?;
        let mut req = Req::new(
            Method::POST,
            vec!["v1", "databases", &self.name, "transactions"],
        );
        req.body = Some(body);
        let resp = self.client.send_project(req).await?;
        let out: TxResult = resp.decode_status(&[StatusCode::OK])?;
        tx.check_results(&out.results)
            .map_err(|message| resp.invalid(message))?;
        Ok(out)
    }
}

/// Database metadata from the project management API.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectDatabaseInfo {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub project_id: String,
    pub name: String,
    pub branched_from: Option<String>,
    pub created_at: String,
    pub region: String,
    pub home_region: String,
    pub replica_regions: Vec<String>,
    /// Absent on responses from servers that predate `allRegions`.
    #[serde(default)]
    pub all_regions: bool,
}

/// A C# TerraBase collection; timestamps are exact decimal milliseconds.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProjectCollectionInfo {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    pub name: String,
    #[serde(with = "u64_string")]
    pub created_at: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProjectCollectionPage {
    pub items: Vec<ProjectCollectionInfo>,
    #[serde(deserialize_with = "super::ids::cursor")]
    pub next_cursor: Option<String>,
}

/// A handle to one collection by name. Performs no I/O.
#[derive(Debug, Clone)]
pub struct Collection {
    db: Database,
    name: String,
}

/// A write precondition.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Precondition {
    /// The document's current version must equal this one (`If-Match`). For a
    /// deleted document use the tombstone version from [`Error::NotFound`].
    IfMatch(Version),
    /// The document must be absent, deleted or expired (`If-None-Match: *`).
    IfNoneMatchAny,
}

/// Options for one document write.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct WriteOptions {
    /// Optional precondition.
    pub precondition: Option<Precondition>,
}

impl WriteOptions {
    /// No precondition.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Requires the current version to be `version`.
    #[must_use]
    pub fn if_match(mut self, version: Version) -> Self {
        self.precondition = Some(Precondition::IfMatch(version));
        self
    }

    /// Requires the document to be absent.
    #[must_use]
    pub fn if_none_match_any(mut self) -> Self {
        self.precondition = Some(Precondition::IfNoneMatchAny);
        self
    }
}

impl From<Precondition> for WriteOptions {
    fn from(p: Precondition) -> Self {
        Self {
            precondition: Some(p),
        }
    }
}

/// The response to a document write.
#[derive(Debug, Clone, Deserialize)]
pub struct WriteResult {
    #[serde(deserialize_with = "super::ids::document_id")]
    pub id: DocumentId,
    /// The new version; for a delete, the tombstone's version.
    pub version: Version,
    /// The database commit sequence of the write.
    #[serde(with = "u64_string")]
    pub offset: u64,
    pub durability: Durability,
    /// The stored document returned after a PATCH, retaining exact JSON numbers.
    pub document: Option<Box<RawValue>>,
    /// True when a PUT or POST answered `201` (the document did not exist).
    #[serde(skip)]
    pub created: bool,
    /// The `Location` header of a POST create.
    #[serde(skip)]
    pub location: Option<String>,
}

impl PartialEq for WriteResult {
    fn eq(&self, other: &Self) -> bool {
        self.id == other.id
            && self.version == other.version
            && self.offset == other.offset
            && self.durability == other.durability
            && self.document.as_deref().map(RawValue::get)
                == other.document.as_deref().map(RawValue::get)
            && self.created == other.created
            && self.location == other.location
    }
}

impl Eq for WriteResult {}

/// The event that produced a sibling.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Event {
    /// Lowercase hyphenated UUID.
    pub replica: String,
    /// 32 lowercase hex characters.
    pub incarnation: String,
    #[serde(with = "u64_string")]
    pub sequence: u64,
}

/// One concurrent version of a conflicted document.
#[derive(Debug, Clone, PartialEq)]
pub struct Sibling<T> {
    pub event: Event,
    /// True for a deleted sibling (it has no document).
    pub deleted: bool,
    pub document: Option<T>,
}

/// A document's point state.
#[derive(Debug, Clone, PartialEq)]
pub enum DocumentState<T> {
    /// One unconflicted value.
    Value(T),
    /// Concurrent siblings (live and deleted). Resolve with a PUT whose
    /// `If-Match` is the document's version. Security-sensitive callers
    /// should fail closed on this state.
    Conflict(Vec<Sibling<T>>),
}

/// A document as returned by GET and queries.
///
/// `T` is the body type: any `DeserializeOwned` type, or the default
/// `Box<RawValue>` to keep the exact JSON text (numbers included).
#[derive(Debug, Clone, PartialEq)]
pub struct Document<T = Box<RawValue>> {
    pub id: DocumentId,
    pub version: Version,
    pub state: DocumentState<T>,
    /// Vector distance on results of a [`super::Near`] query, meters on
    /// results of a [`super::GeoNear`] query; otherwise `None`.
    pub distance: Option<f64>,
    /// Relevance score on results sorted by `$score`; otherwise `None`.
    pub score: Option<f64>,
}

impl<T> Document<T> {
    /// The value of an unconflicted document.
    #[must_use]
    pub fn value(&self) -> Option<&T> {
        match &self.state {
            DocumentState::Value(v) => Some(v),
            DocumentState::Conflict(_) => None,
        }
    }

    /// Consumes the document and returns its value, or
    /// [`Error::ConflictUnresolved`] for a conflicted document.
    pub fn into_value(self) -> Result<T> {
        match self.state {
            DocumentState::Value(v) => Ok(v),
            DocumentState::Conflict(_) => Err(Error::ConflictUnresolved { op: None }),
        }
    }

    /// True when the document has concurrent siblings.
    #[must_use]
    pub fn is_conflict(&self) -> bool {
        matches!(self.state, DocumentState::Conflict(_))
    }
}

#[derive(Deserialize)]
struct WireSibling<T> {
    event: Event,
    deleted: bool,
    #[serde(default = "none")]
    document: Option<T>,
}

fn none<T>() -> Option<T> {
    None
}

#[derive(Deserialize)]
struct WireDocument<T> {
    #[serde(deserialize_with = "super::ids::document_id")]
    id: DocumentId,
    version: Version,
    state: String,
    #[serde(default = "none")]
    document: Option<T>,
    #[serde(default = "none")]
    siblings: Option<Vec<WireSibling<T>>>,
    #[serde(default)]
    distance: Option<f64>,
    #[serde(default)]
    score: Option<f64>,
}

impl<'de, T: Deserialize<'de>> Deserialize<'de> for Document<T> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        let w = WireDocument::<T>::deserialize(d)?;
        let state = match w.state.as_str() {
            "value" => DocumentState::Value(
                w.document
                    .ok_or_else(|| D::Error::custom("state value without document"))?,
            ),
            "conflict" => DocumentState::Conflict(
                w.siblings
                    .filter(|s| !s.is_empty())
                    .ok_or_else(|| D::Error::custom("state conflict without siblings"))?
                    .into_iter()
                    .map(|s| match (s.deleted, s.document) {
                        (false, Some(document)) => Ok(Sibling {
                            event: s.event,
                            deleted: false,
                            document: Some(document),
                        }),
                        (true, None) => Ok(Sibling {
                            event: s.event,
                            deleted: true,
                            document: None,
                        }),
                        (false, None) => Err(D::Error::custom("live sibling without a document")),
                        (true, Some(_)) => Err(D::Error::custom("deleted sibling with a document")),
                    })
                    .collect::<Result<_, _>>()?,
            ),
            other => {
                return Err(D::Error::custom(format!(
                    "unknown document state {other:?}"
                )))
            }
        };
        Ok(Self {
            id: w.id,
            version: w.version,
            state,
            distance: w.distance,
            score: w.score,
        })
    }
}

/// A document patch.
#[derive(Debug, Clone)]
pub enum Patch {
    /// An RFC 7396 merge patch (`application/merge-patch+json`); `null`
    /// members remove fields.
    Merge(Box<RawValue>),
    /// Ordered patch operations (`application/vnd.terrabase.ops+json`), 1–256.
    Ops(Vec<PatchOp>),
}

impl Patch {
    /// A merge patch from any serializable object.
    pub fn merge<T: Serialize + ?Sized>(patch: &T) -> Result<Self> {
        Ok(Self::Merge(encode_object(patch)?))
    }

    /// An ops patch.
    #[must_use]
    pub fn ops(ops: Vec<PatchOp>) -> Self {
        Self::Ops(ops)
    }

    pub(crate) fn validate(&self) -> Result<()> {
        if let Self::Ops(ops) = self {
            if ops.is_empty() || ops.len() > 256 {
                return Err(Error::InvalidInput("a patch needs 1-256 ops".into()));
            }
        }
        Ok(())
    }

    fn content_type(&self) -> &'static str {
        match self {
            Self::Merge(_) => "application/merge-patch+json",
            Self::Ops(_) => "application/vnd.terrabase.ops+json",
        }
    }

    fn body(&self) -> Result<Vec<u8>> {
        match self {
            Self::Merge(raw) => Ok(raw.get().as_bytes().to_vec()),
            Self::Ops(ops) => serde_json::to_vec(&OpsBody { ops })
                .map_err(|e| Error::InvalidInput(format!("encode patch: {e}"))),
        }
    }
}

#[derive(Serialize)]
struct OpsBody<'a> {
    ops: &'a [PatchOp],
}

/// One patch operation. Paths are RFC 6901 JSON Pointers without array indices.
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum PatchOp {
    /// Sets the member, creating missing parent objects.
    Set { path: String, value: Box<RawValue> },
    /// Removes the member.
    Unset { path: String },
    /// Adds `by` exactly; a missing member counts as 0.
    Inc { path: String, by: Number },
    /// Appends to the array, creating it when missing.
    Append { path: String, value: Box<RawValue> },
    /// Adds a scalar to a top-level set; the producer accepts at most 1 KiB.
    Add { path: String, value: Box<RawValue> },
    /// Removes a scalar from a top-level set; the producer accepts at most 1 KiB.
    Remove { path: String, value: Box<RawValue> },
}

impl PatchOp {
    /// `set` with any serializable value.
    pub fn set<T: Serialize + ?Sized>(path: impl Into<String>, value: &T) -> Result<Self> {
        Ok(Self::Set {
            path: path.into(),
            value: to_raw(value)?,
        })
    }

    /// `unset`.
    #[must_use]
    pub fn unset(path: impl Into<String>) -> Self {
        Self::Unset { path: path.into() }
    }

    /// `inc` by an exact number.
    #[must_use]
    pub fn inc(path: impl Into<String>, by: impl Into<Number>) -> Self {
        Self::Inc {
            path: path.into(),
            by: by.into(),
        }
    }

    /// `append` with any serializable value.
    pub fn append<T: Serialize + ?Sized>(path: impl Into<String>, value: &T) -> Result<Self> {
        Ok(Self::Append {
            path: path.into(),
            value: to_raw(value)?,
        })
    }

    /// `add` a scalar to a top-level set.
    pub fn add<T: Serialize + ?Sized>(path: impl Into<String>, value: &T) -> Result<Self> {
        Ok(Self::Add {
            path: path.into(),
            value: to_raw(value)?,
        })
    }

    /// `remove` a scalar from a top-level set.
    pub fn remove<T: Serialize + ?Sized>(path: impl Into<String>, value: &T) -> Result<Self> {
        Ok(Self::Remove {
            path: path.into(),
            value: to_raw(value)?,
        })
    }
}

pub(crate) fn to_raw<T: Serialize + ?Sized>(value: &T) -> Result<Box<RawValue>> {
    serde_json::value::to_raw_value(value)
        .map_err(|e| Error::InvalidInput(format!("encode value: {e}")))
}

/// Serializes `doc` and requires a JSON object.
pub(crate) fn encode_object<T: Serialize + ?Sized>(doc: &T) -> Result<Box<RawValue>> {
    let raw = to_raw(doc)?;
    if !raw.get().trim_start().starts_with('{') {
        return Err(Error::InvalidInput(
            "a document must be a JSON object".into(),
        ));
    }
    Ok(raw)
}

fn precondition_headers(p: Option<&Precondition>) -> Vec<(&'static str, String)> {
    match p {
        Some(Precondition::IfMatch(v)) => vec![("If-Match", v.quoted())],
        Some(Precondition::IfNoneMatchAny) => vec![("If-None-Match", "*".into())],
        None => Vec::new(),
    }
}

impl Collection {
    /// Starts a shape read of this collection. Performs no I/O.
    #[must_use]
    pub fn shape(&self) -> Shape {
        Shape::new(self.clone())
    }

    /// Reads C# TerraBase collection metadata.
    pub async fn get_collection(&self) -> Result<ProjectCollectionInfo> {
        self.check()?;
        self.client()
            .send_project(Req::new(Method::GET, self.segments(&[])))
            .await?
            .decode_status(&[StatusCode::OK])
    }

    /// Synchronously drops a C# TerraBase collection and its documents.
    pub async fn drop_collection(&self) -> Result<ProjectCollectionInfo> {
        self.check()?;
        self.client()
            .send_project(Req::new(Method::DELETE, self.segments(&[])))
            .await?
            .decode_status(&[StatusCode::OK])
    }

    /// Lists C# TerraBase documents in id order using its opaque cursor.
    pub async fn list<T: DeserializeOwned>(
        &self,
        limit: Option<u32>,
        cursor: Option<&str>,
    ) -> Result<super::QueryPage<T>> {
        self.check()?;
        if limit.is_some_and(|n| !(1..=1000).contains(&n)) {
            return Err(Error::InvalidInput(
                "document limit must be 1 to 1000".into(),
            ));
        }
        let mut req = Req::new(Method::GET, self.segments(&["documents"]));
        if let Some(limit) = limit {
            req.query.push(("limit", limit.to_string()));
        }
        if let Some(cursor) = cursor {
            req.query.push(("cursor", cursor.to_owned()));
        }
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }

    /// Expires documents whose member at `path` (an RFC 6901 JSON Pointer,
    /// 1-16 members) holds a past RFC 3339 UTC time. An hourly sweep deletes
    /// them, so removal can lag by up to an hour.
    pub async fn set_ttl(&self, path: &str) -> Result<()> {
        self.check()?;
        if !path.starts_with('/') || path.matches('/').count() > 16 {
            return Err(Error::InvalidInput(
                "ttl path must be a JSON Pointer with 1-16 members".into(),
            ));
        }
        let req = Req::new(Method::PUT, self.segments(&["ttl"]))
            .json(&serde_json::json!({ "path": path }))?;
        self.client()
            .send_project(req)
            .await?
            .check_status(&[StatusCode::OK])
    }

    /// The TTL path, or `None` when documents do not expire.
    pub async fn get_ttl(&self) -> Result<Option<String>> {
        #[derive(Deserialize)]
        struct Ttl {
            path: Option<String>,
        }
        self.check()?;
        let ttl: Ttl = self
            .client()
            .send_project(Req::new(Method::GET, self.segments(&["ttl"])))
            .await?
            .decode_status(&[StatusCode::OK])?;
        Ok(ttl.path)
    }

    /// Stops expiring documents.
    pub async fn clear_ttl(&self) -> Result<()> {
        self.check()?;
        self.client()
            .send_project(Req::new(Method::DELETE, self.segments(&["ttl"])))
            .await?
            .check_status(&[StatusCode::OK, StatusCode::NO_CONTENT])
    }

    /// The collection name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// The database handle.
    #[must_use]
    pub fn database(&self) -> &Database {
        &self.db
    }

    pub(crate) fn client(&self) -> &Client {
        &self.db.client
    }

    pub(crate) fn check(&self) -> Result<()> {
        check_database_selector(&self.db.name)?;
        check_name("collection", &self.name)
    }

    pub(crate) fn segments<'a>(&'a self, rest: &[&'a str]) -> Vec<&'a str> {
        let mut s = vec!["v1", "databases", &self.db.name, "collections", &self.name];
        s.extend_from_slice(rest);
        s
    }

    /// Reads a document. Absent, deleted and expired documents fail with
    /// [`Error::NotFound`] (with the tombstone version for deleted ones).
    pub async fn get<T: DeserializeOwned>(&self, id: DocumentId) -> Result<Document<T>> {
        self.get_inner(id, None)
            .await?
            .ok_or_else(|| Error::Decode("unexpected 304".into()))
    }

    /// Reads a document unless its version is still `version`; `Ok(None)`
    /// means `304 Not Modified`.
    pub async fn get_if_changed<T: DeserializeOwned>(
        &self,
        id: DocumentId,
        version: &Version,
    ) -> Result<Option<Document<T>>> {
        self.get_inner(id, Some(version)).await
    }

    async fn get_inner<T: DeserializeOwned>(
        &self,
        id: DocumentId,
        if_none_match: Option<&Version>,
    ) -> Result<Option<Document<T>>> {
        self.check()?;
        let mut req = self.point_request(Method::GET, id);
        if let Some(v) = if_none_match {
            req.headers.push(("If-None-Match", v.quoted()));
        }
        let resp = self.client().send(req).await?;
        resp.check_status(if if_none_match.is_some() {
            &[StatusCode::OK, StatusCode::NOT_MODIFIED]
        } else {
            &[StatusCode::OK]
        })?;
        if resp.status == StatusCode::NOT_MODIFIED {
            return Ok(None);
        }
        let doc: Document<T> = resp.decode()?;
        if doc.id != id {
            return Err(resp.invalid(format!("response is for document {}, not {id}", doc.id)));
        }
        Ok(Some(doc))
    }

    /// Writes the complete document (`PUT`). `created` reports a `201`.
    pub async fn put<T: Serialize + ?Sized>(
        &self,
        id: DocumentId,
        doc: &T,
        opts: WriteOptions,
    ) -> Result<WriteResult> {
        self.check()?;
        let body = encode_object(doc)?;
        let mut req = self.point_request(Method::PUT, id);
        req.body = Some(body.get().as_bytes().to_vec());
        req.headers = precondition_headers(opts.precondition.as_ref());
        write_result(
            self.client().send(req).await?,
            Some(id),
            &[StatusCode::OK, StatusCode::CREATED],
        )
    }

    /// Creates a document under a server-generated ID (`POST`, ULID on project clients).
    /// Preconditions are rejected.
    pub async fn create<T: Serialize + ?Sized>(
        &self,
        doc: &T,
        opts: WriteOptions,
    ) -> Result<WriteResult> {
        self.check()?;
        if opts.precondition.is_some() {
            return Err(Error::InvalidInput("create takes no precondition".into()));
        }
        let body = encode_object(doc)?;
        let mut req = Req::new(Method::POST, self.segments(&["documents"]));
        req.body = Some(body.get().as_bytes().to_vec());
        write_result(self.client().send(req).await?, None, &[StatusCode::CREATED])
    }

    /// Patches an existing document (`PATCH`). Only `IfMatch` is accepted.
    pub async fn patch(
        &self,
        id: DocumentId,
        patch: &Patch,
        opts: WriteOptions,
    ) -> Result<WriteResult> {
        self.check()?;
        patch.validate()?;
        if opts.precondition == Some(Precondition::IfNoneMatchAny) {
            return Err(Error::InvalidInput(
                "patch does not take IfNoneMatchAny".into(),
            ));
        }
        let mut req = self.point_request(Method::PATCH, id);
        req.body = Some(patch.body()?);
        req.content_type = Some(patch.content_type());
        req.headers = precondition_headers(opts.precondition.as_ref());
        write_result(self.client().send(req).await?, Some(id), &[StatusCode::OK])
    }

    /// Deletes (tombstones) a document. Only `IfMatch` is accepted.
    pub async fn delete(&self, id: DocumentId, opts: WriteOptions) -> Result<WriteResult> {
        self.check()?;
        if opts.precondition == Some(Precondition::IfNoneMatchAny) {
            return Err(Error::InvalidInput(
                "delete does not take IfNoneMatchAny".into(),
            ));
        }
        let mut req = self.point_request(Method::DELETE, id);
        req.headers = precondition_headers(opts.precondition.as_ref());
        write_result(self.client().send(req).await?, Some(id), &[StatusCode::OK])
    }

    pub(crate) fn query_segments(&self) -> Vec<&str> {
        self.segments(&["query"])
    }

    fn point_request(&self, method: Method, id: DocumentId) -> Req<'_> {
        let mut req = Req::new(method, self.segments(&["documents", "by-key"]));
        req.query.push(("id", id.to_string()));
        req
    }
}

fn write_result(
    resp: Resp,
    expected_id: Option<DocumentId>,
    allowed: &[StatusCode],
) -> Result<WriteResult> {
    let mut out: WriteResult = resp.decode_status(allowed)?;
    if expected_id.is_none() && resp.status != StatusCode::CREATED {
        return Err(resp.invalid("generated creation requires HTTP 201".into()));
    }
    if expected_id.is_some_and(|id| out.id != id) {
        return Err(resp.invalid(format!(
            "acknowledgement is for document {}, not the one written",
            out.id
        )));
    }
    out.created = resp.status == StatusCode::CREATED;
    out.location = resp.header("Location").map(str::to_owned);
    Ok(out)
}
