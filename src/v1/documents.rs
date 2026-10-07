//! Documents: point reads and single-document writes (api-v1.md "Documents").

use reqwest::{Method, StatusCode};
use serde::de::{DeserializeOwned, Error as _};
use serde::{Deserialize, Deserializer, Serialize};
use serde_json::value::RawValue;

use super::client::{Client, Req, Resp};
use super::errors::{Error, Result};
use super::ids::{check_name, DocumentId, IdempotencyKey, Version};
use super::tx::{Tx, TxResult};
use super::types::{u64_string, Durability, Number};

/// A handle to one database by name. Performs no I/O.
#[derive(Debug, Clone)]
pub struct Database {
    client: Client,
    name: String,
}

impl Database {
    pub(crate) fn new(client: Client, name: String) -> Self {
        Self { client, name }
    }

    /// The database name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// The client this handle uses.
    #[must_use]
    pub fn client(&self) -> &Client {
        &self.client
    }

    /// Creates this database using the pinned C# producer's idempotent PUT.
    /// This is the pre-TS-589 route, not the successor management POST.
    pub async fn create(&self) -> Result<ProjectDatabaseInfo> {
        check_name("database", &self.name)?;
        self.client
            .send_project(Req::new(Method::PUT, vec!["v1", "databases", &self.name]))
            .await?
            .decode()
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
        check_name("database", &self.name)?;
        check_name("collection", name)?;
        let req = Req::new(
            Method::POST,
            vec!["v1", "databases", &self.name, "collections"],
        )
        .json(&serde_json::json!({"name": name}))?;
        self.client.send_project(req).await?.decode()
    }

    /// Lists C# TerraBase collections with the producer's opaque cursor.
    pub async fn list_collections(
        &self,
        limit: Option<u32>,
        cursor: Option<&str>,
    ) -> Result<ProjectCollectionPage> {
        check_name("database", &self.name)?;
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
        self.client.send_project(req).await?.decode()
    }

    /// Atomically commits `tx` (`POST /v1/databases/{db}/transactions`).
    ///
    /// Failures name the failing op through [`Error::op`]. With
    /// [`Tx::idempotency_key`] set, an uncertain commit is retried and an
    /// exact replay returns the original result.
    pub async fn commit(&self, tx: &Tx) -> Result<TxResult> {
        check_name("database", &self.name)?;
        let body = tx.encode()?;
        let mut req = Req::new(
            Method::POST,
            vec!["v1", "databases", &self.name, "transactions"],
        );
        req.body = Some(body);
        req.idempotency_key = tx.key();
        let resp = self.client.send(req).await?;
        let out: TxResult = resp.decode()?;
        tx.check_results(&out.results)
            .map_err(|message| Error::UndecodableAck {
                status: resp.status.as_u16(),
                message,
            })?;
        Ok(out)
    }
}

/// Database metadata from the pinned C# producer.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectDatabaseInfo {
    pub id: uuid::Uuid,
    pub project_id: uuid::Uuid,
    pub name: String,
}

/// A C# TerraBase collection; timestamps are exact decimal milliseconds.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct ProjectCollectionInfo {
    pub name: String,
    #[serde(with = "u64_string")]
    pub created_at: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct ProjectCollectionPage {
    pub items: Vec<ProjectCollectionInfo>,
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
    /// Optional `Idempotency-Key`; makes the write replay-safe, so transient
    /// failures and `commit_uncertain` are retried.
    pub idempotency_key: Option<IdempotencyKey>,
}

impl WriteOptions {
    /// No precondition and no idempotency key.
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

    /// Sets the `Idempotency-Key`.
    #[must_use]
    pub fn idempotency_key(mut self, key: IdempotencyKey) -> Self {
        self.idempotency_key = Some(key);
        self
    }
}

impl From<Precondition> for WriteOptions {
    fn from(p: Precondition) -> Self {
        Self {
            precondition: Some(p),
            idempotency_key: None,
        }
    }
}

/// The response to a document write.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct WriteResult {
    pub id: DocumentId,
    /// The new version; for a delete, the tombstone's version.
    pub version: Version,
    /// The database commit sequence of the write.
    #[serde(with = "u64_string")]
    pub offset: u64,
    pub durability: Durability,
    /// True when a PUT or POST answered `201` (the document did not exist).
    #[serde(skip)]
    pub created: bool,
    /// The `Location` header of a POST create.
    #[serde(skip)]
    pub location: Option<String>,
}

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
    id: DocumentId,
    version: Version,
    state: String,
    #[serde(default = "none")]
    document: Option<T>,
    #[serde(default = "none")]
    siblings: Option<Vec<WireSibling<T>>>,
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
    /// Reads C# TerraBase collection metadata.
    pub async fn get_collection(&self) -> Result<ProjectCollectionInfo> {
        self.check()?;
        self.client()
            .send_project(Req::new(Method::GET, self.segments(&[])))
            .await?
            .decode()
    }

    /// Synchronously drops a C# TerraBase collection and its documents.
    pub async fn drop_collection(&self) -> Result<ProjectCollectionInfo> {
        self.check()?;
        self.client()
            .send_project(Req::new(Method::DELETE, self.segments(&[])))
            .await?
            .decode()
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
        self.client().send_project(req).await?.decode()
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
        check_name("database", &self.db.name)?;
        check_name("collection", &self.name)
    }

    fn segments<'a>(&'a self, rest: &[&'a str]) -> Vec<&'a str> {
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
        let id = id.to_string();
        let mut req = Req::new(Method::GET, self.segments(&["documents", &id]));
        if let Some(v) = if_none_match {
            req.headers.push(("If-None-Match", v.quoted()));
        }
        let resp = self.client().send(req).await?;
        if resp.status == StatusCode::NOT_MODIFIED {
            return Ok(None);
        }
        let doc: Document<T> = resp.decode()?;
        if doc.id.to_string() != id {
            return Err(Error::Decode(format!(
                "response is for document {}, not {id}",
                doc.id
            )));
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
        let id = id.to_string();
        let mut req = Req::new(Method::PUT, self.segments(&["documents", &id]));
        req.body = Some(body.get().as_bytes().to_vec());
        req.headers = precondition_headers(opts.precondition.as_ref());
        req.idempotency_key = opts.idempotency_key.as_ref();
        write_result(self.client().send(req).await?, Some(&id))
    }

    /// Creates a document under a server-generated UUIDv7 (`POST`).
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
        req.idempotency_key = opts.idempotency_key.as_ref();
        write_result(self.client().send(req).await?, None)
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
        let id = id.to_string();
        let mut req = Req::new(Method::PATCH, self.segments(&["documents", &id]));
        req.body = Some(patch.body()?);
        req.content_type = Some(patch.content_type());
        req.headers = precondition_headers(opts.precondition.as_ref());
        req.idempotency_key = opts.idempotency_key.as_ref();
        write_result(self.client().send(req).await?, Some(&id))
    }

    /// Deletes (tombstones) a document. Only `IfMatch` is accepted.
    pub async fn delete(&self, id: DocumentId, opts: WriteOptions) -> Result<WriteResult> {
        self.check()?;
        if opts.precondition == Some(Precondition::IfNoneMatchAny) {
            return Err(Error::InvalidInput(
                "delete does not take IfNoneMatchAny".into(),
            ));
        }
        let id = id.to_string();
        let mut req = Req::new(Method::DELETE, self.segments(&["documents", &id]));
        req.headers = precondition_headers(opts.precondition.as_ref());
        req.idempotency_key = opts.idempotency_key.as_ref();
        write_result(self.client().send(req).await?, Some(&id))
    }

    pub(crate) fn query_segments(&self) -> Vec<&str> {
        self.segments(&["query"])
    }
}

fn write_result(resp: Resp, expected_id: Option<&str>) -> Result<WriteResult> {
    let mut out: WriteResult = resp.decode()?;
    if expected_id.is_some_and(|id| out.id.to_string() != id) {
        return Err(Error::UndecodableAck {
            status: resp.status.as_u16(),
            message: format!(
                "acknowledgement is for document {}, not the one written",
                out.id
            ),
        });
    }
    out.created = resp.status == StatusCode::CREATED;
    out.location = resp.header("Location").map(str::to_owned);
    Ok(out)
}
