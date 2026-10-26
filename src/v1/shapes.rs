//! Collection shape reads (`shape-reads.md` sections 2 to 9 and 13).
//!
//! A [`Shape`] names one collection and an optional filter. [`Shape::stream`]
//! yields one [`ShapeUpdate`] per applied response. Each update carries the
//! whole state after its changes, so readers never see a half-applied
//! response. Nothing runs in the background: dropping the stream cancels the
//! poll.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine as _};
use futures_util::{stream, Stream};
use reqwest::StatusCode;
use serde::Deserialize;
use serde_json::value::RawValue;
use url::Url;

use super::client::{Client, Resp};
use super::documents::Collection;
use super::errors::{Error, Result};
use super::ids::{parse_document_id, DocumentId, Version};
use super::query::Filter;
use super::shape_crypto;

/// The longest `where` the server accepts, raw or canonical (section 2).
const MAX_WHERE_BYTES: usize = 8 << 10;
/// The protocol reserves 64 bytes of its 8 KiB request-line bound.
const MAX_REQUEST_TARGET: usize = (8 << 10) - 64;
/// Redirects, must-refetch resumes and shape-busy retries share this bound.
const MAX_RESTARTS: u32 = 3;

pub(crate) type EpochKeys = BTreeMap<(String, u64, String), zeroize::Zeroizing<[u8; 32]>>;

fn current_epoch() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
        / 600
}

fn retain_epoch(keys: &mut EpochKeys, epoch: u64) {
    keys.retain(|(_, saved_epoch, _), _| *saved_epoch == epoch);
}

/// One collection, optionally filtered. Performs no I/O until [`Shape::stream`].
#[derive(Debug, Clone)]
pub struct Shape {
    collection: Collection,
    filter: Option<String>,
}

impl Shape {
    pub(crate) fn new(collection: Collection) -> Self {
        Self {
            collection,
            filter: None,
        }
    }

    /// Limits the shape to documents matching `filter`.
    pub fn filter(self, filter: Filter) -> Result<Self> {
        filter.validate()?;
        let json = serde_json::to_string(&filter)
            .map_err(|e| Error::InvalidInput(format!("filter JSON: {e}")))?;
        self.with_filter(json)
    }

    /// Limits the shape to the `where` JSON `json`, sent verbatim.
    pub fn filter_json(self, json: &str) -> Result<Self> {
        Filter::from_json(json)?.validate()?;
        self.with_filter(json.to_owned())
    }

    fn with_filter(mut self, json: String) -> Result<Self> {
        if json.len() > MAX_WHERE_BYTES {
            return Err(Error::InvalidInput("filter exceeds 8 KiB".into()));
        }
        self.filter = Some(json);
        Ok(self)
    }

    /// Syncs the shape: a sealed snapshot, then chunks, then the live tail.
    ///
    /// Each item is one applied response. An error ends the stream; the
    /// state it held is discarded with the stream.
    pub fn stream(self) -> impl Stream<Item = Result<ShapeUpdate>> + Send {
        let sync = ShapeSync::new(self);
        stream::unfold(Some(sync), |sync| async move {
            let mut sync = sync?;
            let update = sync.step().await;
            let next = update.is_ok().then_some(sync);
            Some((update, next))
        })
    }

    /// Decrypts one sealed TBS1 object (section 12) for `handle` at
    /// `request_offset`. Epoch keys are cached only in this client's process
    /// memory for the current epoch, keyed by handle, epoch and key id.
    /// A rejected object yields no plaintext.
    pub async fn decrypt_sealed(
        &self,
        object: &[u8],
        handle: &str,
        request_offset: &str,
        epoch: u64,
        key_id: &str,
    ) -> Result<Vec<u8>> {
        let key_id_valid = super::ids::valid_entity_id(key_id);
        if !valid_handle(handle) || position(request_offset).is_none() || !key_id_valid {
            return Err(Error::InvalidInput(
                "shape object identity is malformed".into(),
            ));
        }
        self.collection.check()?;
        let key = self.epoch_key(handle, epoch, key_id).await?;
        shape_crypto::decrypt(object, handle, request_offset, epoch, key_id, &key)
    }

    async fn epoch_key(
        &self,
        handle: &str,
        epoch: u64,
        key_id: &str,
    ) -> Result<zeroize::Zeroizing<[u8; 32]>> {
        let database = self.collection.database();
        let client = database.client();
        // Serialize lookup and fetch so conflicts cannot race cache insertion.
        let mut keys = client.shape_keys().lock().await;
        retain_epoch(&mut keys, current_epoch());
        let identity = (handle.to_owned(), epoch, key_id.to_owned());
        if let Some(key) = keys.get(&identity) {
            return Ok(key.clone());
        }
        let (base, region) = client.shape_endpoint().await?;
        let query = [
            ("handle", handle.to_owned()),
            ("epoch", epoch.to_string()),
            ("key_id", key_id.to_owned()),
        ];
        let url = client.shape_url(
            &base,
            database.name(),
            self.collection.name(),
            Some("keys"),
            &query,
        )?;
        let mut resp = client
            .shape_get(
                &url,
                region.as_deref(),
                &[("Cache-Control", "no-store".into())],
            )
            .await?;
        if resp.status == StatusCode::CONFLICT {
            keys.clear();
        }
        resp.check_status(&[StatusCode::OK])?;
        // Clear key-bearing buffers on every exit and exclude them from diagnostics.
        let raw = zeroize::Zeroizing::new(std::mem::take(&mut resp.body));
        let body: KeyBody = serde_json::from_slice(&raw)
            .map_err(|e| resp.invalid(format!("shape key response: {e}")))?;
        if body.handle != handle || body.epoch != epoch.to_string() || body.key_id != key_id {
            return Err(resp.invalid("shape key does not match its request".into()));
        }
        let bytes = zeroize::Zeroizing::new(
            URL_SAFE_NO_PAD
                .decode(body.key.as_bytes())
                .map_err(|e| resp.invalid(format!("shape key encoding: {e}")))?,
        );
        let mut key = zeroize::Zeroizing::new([0u8; 32]);
        if bytes.len() != key.len() {
            return Err(resp.invalid("shape key is not 32 bytes".into()));
        }
        key.copy_from_slice(&bytes);
        let now = current_epoch();
        retain_epoch(&mut keys, now);
        if epoch == now {
            keys.insert(identity, key.clone());
        }
        Ok(key)
    }
}

/// One applied response: the state after it, and what it changed.
#[derive(Debug, Clone)]
pub struct ShapeUpdate {
    /// The offset the state now covers.
    pub offset: String,
    /// True only on a fresh response at the sealed head.
    pub up_to_date: bool,
    /// The changes this response applied, in order.
    pub changes: Vec<ShapeChange>,
    /// The whole state after `changes`.
    pub state: Arc<ShapeState>,
}

/// The documents of a shape, keyed by document id.
#[derive(Debug, Clone, Default)]
pub struct ShapeState {
    documents: BTreeMap<DocumentId, ShapeDocument>,
}

impl ShapeState {
    /// The document with this id, if it is in the shape.
    #[must_use]
    pub fn get(&self, id: DocumentId) -> Option<&ShapeDocument> {
        self.documents.get(&id)
    }

    /// Every document, in id order.
    pub fn iter(&self) -> impl Iterator<Item = (&DocumentId, &ShapeDocument)> {
        self.documents.iter()
    }

    /// The number of documents in the shape.
    #[must_use]
    pub fn len(&self) -> usize {
        self.documents.len()
    }

    /// True when the shape holds no documents.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.documents.is_empty()
    }
}

/// A document as the shape holds it. `document` is the stored bytes, unparsed.
#[derive(Debug, Clone)]
pub struct ShapeDocument {
    pub version: Version,
    pub document: Box<RawValue>,
}

/// The operations one commit produced, at its offset.
#[derive(Debug, Clone)]
pub struct ShapeChange {
    pub offset: String,
    pub ops: Vec<ShapeOp>,
}

/// One document operation. Deletes of unseen ids are no-ops.
#[derive(Debug, Clone)]
pub enum ShapeOp {
    Upsert {
        id: DocumentId,
        version: Version,
        document: Box<RawValue>,
    },
    Delete {
        id: DocumentId,
    },
}

/// Where a request points in a shape's feed.
#[derive(Debug, Clone, Copy)]
enum Position {
    /// Snapshot `seq`, any page.
    Snapshot { seq: u64 },
    /// The commit boundary `seq`.
    Commit(u64),
}

/// One request the sync loop can send.
#[derive(Debug, Clone)]
enum Target {
    /// `offset=-1` with no handle: starts a snapshot.
    Bootstrap,
    /// `offset=-1` with the opaque cursor, in the query or the header.
    Resume { cursor: String },
    /// A continuation of a known lineage, optionally a live long-poll.
    Continue {
        handle: String,
        offset: String,
        live: bool,
        bucket: Option<String>,
    },
}

impl Target {
    fn query(&self, filter: Option<&str>) -> Vec<(&'static str, String)> {
        let mut query = match self {
            Target::Bootstrap => vec![("offset", "-1".to_owned())],
            Target::Resume { cursor } => {
                vec![("offset", "-1".to_owned()), ("cursor", cursor.clone())]
            }
            Target::Continue {
                handle,
                offset,
                live,
                bucket,
                ..
            } => {
                let mut query = vec![("handle", handle.clone()), ("offset", offset.clone())];
                if *live {
                    query.push(("live", "true".to_owned()));
                    if let Some(bucket) = bucket {
                        query.push(("cursor", bucket.clone()));
                    }
                }
                query
            }
        };
        if let Some(filter) = filter {
            query.push(("where", filter.to_owned()));
        }
        query
    }
}

/// The result of one HTTP exchange, or a signal that the endpoint moved.
enum Sent {
    Response(Resp),
    Moved,
}

/// The sync state machine behind [`Shape::stream`].
struct ShapeSync {
    client: Client,
    database: String,
    collection: String,
    filter: Option<String>,
    state: Arc<ShapeState>,
    /// The lineage that produced `offset`. None while bootstrapping or resuming.
    handle: Option<String>,
    shape_id: Option<String>,
    offset: String,
    /// The resume cursor, set only once the snapshot is complete (section 7, rule 5).
    cursor: Option<String>,
    /// The live bucket from the last response, sent back on live requests.
    live: Option<String>,
    up_to_date: bool,
    /// A resume is queued for the next request.
    resume_pending: bool,
    /// A resume attempt is in flight. A second one never starts (section 7, rule 6).
    resumed: bool,
    endpoint: Option<(Url, Option<String>)>,
}

impl ShapeSync {
    fn new(shape: Shape) -> Self {
        let database = shape.collection.database();
        Self {
            client: database.client().clone(),
            database: database.name().to_owned(),
            collection: shape.collection.name().to_owned(),
            filter: shape.filter,
            state: Arc::default(),
            handle: None,
            shape_id: None,
            offset: "-1".to_owned(),
            cursor: None,
            live: None,
            up_to_date: false,
            resume_pending: false,
            resumed: false,
            endpoint: None,
        }
    }

    /// Sends requests until one response applies, then returns its update.
    async fn step(&mut self) -> Result<ShapeUpdate> {
        super::ids::check_database_selector(&self.database)?;
        super::ids::check_name("collection", &self.collection)?;
        retain_epoch(&mut *self.client.shape_keys().lock().await, current_epoch());
        let mut restarts = 0;
        'restart: loop {
            let mut target = self.target();
            let mut followed = false;
            let mut attempt = 0;
            loop {
                let resp = match self.send(&target).await? {
                    Sent::Response(resp) => resp,
                    Sent::Moved => {
                        self.lose_lineage();
                        restart(&mut restarts)?;
                        continue 'restart;
                    }
                };
                match resp.status {
                    StatusCode::TEMPORARY_REDIRECT
                        if !followed
                            && matches!(target, Target::Bootstrap | Target::Resume { .. }) =>
                    {
                        target = self.follow(&resp, &target)?;
                        followed = true;
                    }
                    StatusCode::OK => {
                        let Target::Continue {
                            handle,
                            offset,
                            live,
                            bucket,
                            ..
                        } = &target
                        else {
                            return Err(resp.invalid(
                                "shape bootstrap answered 200 without a redirect".into(),
                            ));
                        };
                        return self.apply(handle, offset, *live, bucket.as_deref(), &resp);
                    }
                    StatusCode::CONFLICT => {
                        self.client.shape_keys().lock().await.clear();
                        let error = resp.check_status(&[StatusCode::OK]).unwrap_err();
                        if error.code() != Some("must_refetch") {
                            return Err(error);
                        }
                        let now = resp.header("TB-Shape-Handle");
                        let changed = match (&self.handle, now) {
                            (Some(saved), Some(now)) => saved.as_str() != now,
                            _ => false,
                        };
                        if changed {
                            self.lose_lineage();
                        } else {
                            self.reset();
                        }
                        restart(&mut restarts)?;
                        continue 'restart;
                    }
                    StatusCode::SERVICE_UNAVAILABLE | StatusCode::TOO_MANY_REQUESTS => {
                        let err = resp.check_status(&[StatusCode::OK]).unwrap_err();
                        let retriable = matches!(
                            (err.status(), err.code()),
                            (Some(429), Some("shape_busy"))
                                | (Some(503), Some("shape_too_large" | "unavailable"))
                        );
                        if !retriable
                            || !self
                                .client
                                .shape_retry(attempt, resp.header("Retry-After"))
                                .await
                        {
                            return Err(err);
                        }
                        attempt += 1;
                    }
                    _ => return Err(resp.check_status(&[StatusCode::OK]).unwrap_err()),
                }
            }
        }
    }

    fn target(&self) -> Target {
        if let Some(handle) = &self.handle {
            let live =
                self.up_to_date && matches!(position(&self.offset), Some(Position::Commit(_)));
            return Target::Continue {
                handle: handle.clone(),
                offset: self.offset.clone(),
                live,
                bucket: self.live.clone(),
            };
        }
        match (&self.cursor, self.resume_pending) {
            (Some(cursor), true) => Target::Resume {
                cursor: cursor.clone(),
            },
            _ => Target::Bootstrap,
        }
    }

    /// Sends `target`. Retries transport failures, and re-resolves the endpoint
    /// once per flight. A moved endpoint returns [`Sent::Moved`] instead of
    /// replaying machine-local offsets.
    async fn send(&mut self, target: &Target) -> Result<Sent> {
        let mut refreshed = false;
        let mut attempt = 0;
        loop {
            let endpoint = self.client.shape_endpoint().await?;
            let moved = self
                .endpoint
                .as_ref()
                .is_some_and(|previous| previous != &endpoint);
            self.endpoint = Some(endpoint.clone());
            if moved {
                return Ok(Sent::Moved);
            }
            let (base, region) = endpoint;
            let mut query = target.query(self.filter.as_deref());
            let mut headers = Vec::new();
            let mut url =
                self.client
                    .shape_url(&base, &self.database, &self.collection, None, &query)?;
            if let Target::Resume { cursor, .. } = target {
                let target_len = url.path().len() + url.query().map_or(0, |q| q.len() + 1);
                if target_len > MAX_REQUEST_TARGET {
                    query.retain(|(name, _)| *name != "cursor");
                    headers.push(("TB-Shape-Resume", cursor.clone()));
                    url = self.client.shape_url(
                        &base,
                        &self.database,
                        &self.collection,
                        None,
                        &query,
                    )?;
                }
            }
            if url.path().len() + url.query().map_or(0, |q| q.len() + 1) > MAX_REQUEST_TARGET {
                return Err(Error::InvalidInput(
                    "encoded shape request target exceeds the protocol limit".into(),
                ));
            }
            let response = self
                .client
                .shape_get(&url, region.as_deref(), &headers)
                .await
                .and_then(|resp| {
                    if resp.status == StatusCode::MISDIRECTED_REQUEST {
                        Err(resp.check_status(&[StatusCode::OK]).unwrap_err())
                    } else {
                        Ok(resp)
                    }
                });
            match response {
                Ok(resp) => return Ok(Sent::Response(resp)),
                Err(error) if is_route_error(&error) => {
                    if !refreshed {
                        refreshed = true;
                        let fresh = self.client.shape_endpoint().await?;
                        let moved = fresh.0 != base || fresh.1 != region;
                        self.endpoint = Some(fresh);
                        if moved {
                            return Ok(Sent::Moved);
                        }
                    }
                    if !self.client.shape_retry(attempt, None).await {
                        return Err(error);
                    }
                    attempt += 1;
                }
                Err(error) => return Err(error),
            }
        }
    }

    /// Validates a `307` and returns the continuation it names. The target stays
    /// on this shape's path and origin, and no credentials leave it.
    fn follow(&mut self, resp: &Resp, previous: &Target) -> Result<Target> {
        let (base, _) = self
            .endpoint
            .as_ref()
            .ok_or_else(|| resp.invalid("shape endpoint missing".into()))?;
        let location = resp
            .header("Location")
            .ok_or_else(|| resp.invalid("shape redirect without Location".into()))?;
        let url = base
            .join(location)
            .map_err(|e| resp.invalid(format!("shape redirect target: {e}")))?;
        let expected = self
            .client
            .shape_url(base, &self.database, &self.collection, None, &[])?;
        let same_origin = url.scheme() == base.scheme()
            && url.host_str() == base.host_str()
            && url.port_or_known_default() == base.port_or_known_default();
        if !same_origin
            || url.path() != expected.path()
            || url.fragment().is_some()
            || !url.username().is_empty()
            || url.password().is_some()
        {
            return Err(resp.invalid("shape redirect leaves the shape route".into()));
        }
        let (mut handle, mut offset, mut filter) = (None, None, None);
        for (key, value) in url.query_pairs() {
            match &*key {
                "handle" if handle.is_none() => handle = Some(value.into_owned()),
                "offset" if offset.is_none() => offset = Some(value.into_owned()),
                "where" if filter.is_none() => filter = Some(value.into_owned()),
                _ => return Err(resp.invalid("shape redirect has unexpected parameters".into())),
            }
        }
        let handle = handle.ok_or_else(|| resp.invalid("shape redirect without handle".into()))?;
        let offset = offset.ok_or_else(|| resp.invalid("shape redirect without offset".into()))?;
        if filter.is_some() != self.filter.is_some()
            || !valid_handle(&handle)
            || resp.header("TB-Shape-Handle") != Some(handle.as_str())
            || position(&offset).is_none()
        {
            return Err(resp.invalid("shape redirect does not match its headers".into()));
        }
        if offset.starts_with("snap.") != matches!(previous, Target::Bootstrap) {
            return Err(resp.invalid("shape redirect names the wrong offset kind".into()));
        }
        if let Some(filter) = &filter {
            if filter.is_empty() || filter.len() > MAX_WHERE_BYTES {
                return Err(resp.invalid("shape redirect filter is invalid".into()));
            }
            Filter::from_json(filter)
                .map_err(|_| resp.invalid("shape redirect filter is invalid".into()))?;
        }
        self.filter = filter;
        Ok(Target::Continue {
            handle,
            offset,
            live: false,
            bucket: None,
        })
    }

    /// Validates a `200` body against its request and headers, then applies it.
    /// Nothing changes until every check passes.
    fn apply(
        &mut self,
        handle: &str,
        requested: &str,
        live: bool,
        bucket: Option<&str>,
        resp: &Resp,
    ) -> Result<ShapeUpdate> {
        let (Some(handle_header), Some(next_header), Some(up_to_date_header)) = (
            resp.header("TB-Shape-Handle"),
            resp.header("TB-Shape-Next-Offset"),
            resp.header("TB-Shape-Up-To-Date"),
        ) else {
            return Err(resp.invalid("shape response is missing a required header".into()));
        };
        let live_cursor = resp.header("TB-Shape-Cursor");
        if live && live_cursor.is_none() {
            return Err(resp.invalid("live response has no cursor bucket".into()));
        }
        if let Some(cursor) = live_cursor {
            let next =
                decimal(cursor).ok_or_else(|| resp.invalid("invalid live cursor bucket".into()))?;
            if bucket
                .and_then(decimal)
                .is_some_and(|previous| next <= previous)
            {
                return Err(resp.invalid("live cursor bucket did not advance".into()));
            }
        }
        let body: Body = resp.decode()?;
        if handle_header != handle
            || body.handle != handle
            || !valid_handle(handle)
            || !lower_hex(&body.shape_id, 32)
            || self
                .handle
                .as_deref()
                .is_some_and(|previous| previous == handle)
                && self
                    .shape_id
                    .as_deref()
                    .is_some_and(|previous| previous != body.shape_id)
            || body.offset != requested
            || body.next_offset != next_header
            || body.up_to_date.to_string() != up_to_date_header
            || !base64url(&body.cursor_resume)
        {
            return Err(resp.invalid("shape response does not match its request".into()));
        }
        let from = position(requested)
            .ok_or_else(|| resp.invalid("shape request offset is invalid".into()))?;
        let next = position(&body.next_offset)
            .ok_or_else(|| resp.invalid("shape next_offset is invalid".into()))?;
        let (Some(covered), Some(values_at)) =
            (decimal(&body.covered_offset), decimal(&body.values_at))
        else {
            return Err(resp.invalid("shape covered_offset or values_at is invalid".into()));
        };
        let advances = match (from, next) {
            (Position::Snapshot { seq }, Position::Snapshot { seq: end }) => {
                seq == end && covered == seq
            }
            (Position::Snapshot { seq }, Position::Commit(end)) => seq == end && covered == end,
            (Position::Commit(start), Position::Commit(end)) => end >= start && covered == end,
            (Position::Commit(_), Position::Snapshot { .. }) => false,
        };
        if !advances || values_at < covered {
            return Err(resp.invalid("shape response does not advance its offset".into()));
        }
        let snapshot_page = matches!(next, Position::Snapshot { .. });
        if snapshot_page {
            let start = requested.rsplit('.').next().unwrap();
            let end = body.next_offset.rsplit('.').next().unwrap();
            let advances = end != "0"
                && (start == "0"
                    || String::from_utf8(URL_SAFE_NO_PAD.decode(end).unwrap())
                        .unwrap()
                        .encode_utf16()
                        .cmp(
                            String::from_utf8(URL_SAFE_NO_PAD.decode(start).unwrap())
                                .unwrap()
                                .encode_utf16(),
                        )
                        .is_gt());
            if !advances {
                return Err(resp.invalid("snapshot page did not advance".into()));
            }
        }
        if body.up_to_date
            && (matches!(from, Position::Snapshot { .. })
                || !live && (!body.changes.is_empty() || body.next_offset != requested))
        {
            return Err(resp.invalid("shape response cannot be up to date".into()));
        }

        let upper = match next {
            Position::Snapshot { seq } | Position::Commit(seq) => seq,
        };
        let from_snapshot = matches!(from, Position::Snapshot { .. });
        let mut last = match from {
            Position::Commit(start) => Some(start),
            Position::Snapshot { .. } => None,
        };
        if from_snapshot && (values_at != upper || body.changes.len() != 1) {
            return Err(resp.invalid(
                "snapshot must carry one change materialized at its snapshot offset".into(),
            ));
        }
        if let (Position::Commit(start), Position::Commit(end)) = (from, next) {
            if start == end && (!body.changes.is_empty() || !body.up_to_date) {
                return Err(
                    resp.invalid("only a fresh empty response may retain its offset".into())
                );
            }
        }
        let mut changes = Vec::with_capacity(body.changes.len());
        for change in body.changes {
            let at = decimal(&change.offset)
                .ok_or_else(|| resp.invalid("shape change offset is invalid".into()))?;
            let ordered = last.is_none_or(|prev| at > prev);
            if !(ordered && at <= upper && (!from_snapshot || at == upper)) {
                return Err(resp.invalid("shape changes are out of order".into()));
            }
            last = Some(at);
            let mut ops = Vec::with_capacity(change.ops.len());
            let mut previous_id: Option<DocumentId> = None;
            for op in change.ops {
                let op = op.into_op(resp)?;
                let id = match &op {
                    ShapeOp::Upsert { id, .. } | ShapeOp::Delete { id } => id,
                };
                // Canonical ULID text sorts like the ULID value.
                if previous_id.is_some_and(|previous| previous >= *id) {
                    return Err(resp
                        .invalid("shape operations must have unique ids in ordinal order".into()));
                }
                previous_id = Some(*id);
                if from_snapshot && !matches!(op, ShapeOp::Upsert { .. }) {
                    return Err(resp.invalid("snapshot pages carry only upserts".into()));
                }
                ops.push(op);
            }
            changes.push(ShapeChange {
                offset: change.offset,
                ops,
            });
        }

        let state = Arc::make_mut(&mut self.state);
        for change in &changes {
            for op in &change.ops {
                match op {
                    ShapeOp::Upsert {
                        id,
                        version,
                        document,
                    } => {
                        state.documents.insert(
                            *id,
                            ShapeDocument {
                                version: version.clone(),
                                document: document.clone(),
                            },
                        );
                    }
                    ShapeOp::Delete { id } => {
                        state.documents.remove(id);
                    }
                }
            }
        }
        self.shape_id = Some(body.shape_id);
        self.handle = Some(handle.to_owned());
        self.offset = body.next_offset;
        self.up_to_date = body.up_to_date;
        self.cursor = (!snapshot_page).then_some(body.cursor_resume);
        self.live = resp.header("TB-Shape-Cursor").map(str::to_owned);
        self.resume_pending = false;
        self.resumed = false;
        Ok(ShapeUpdate {
            offset: self.offset.clone(),
            up_to_date: self.up_to_date,
            changes,
            state: Arc::clone(&self.state),
        })
    }

    /// Discards everything and starts again at `-1`.
    fn reset(&mut self) {
        self.state = Arc::default();
        self.handle = None;
        self.shape_id = None;
        self.offset = "-1".to_owned();
        self.cursor = None;
        self.live = None;
        self.up_to_date = false;
        self.resume_pending = false;
        self.resumed = false;
    }

    /// A lineage change: resume once if a usable cursor exists, else discard.
    fn lose_lineage(&mut self) {
        if self.cursor.is_some() && !self.resumed {
            self.handle = None;
            self.shape_id = None;
            self.offset = "-1".to_owned();
            self.live = None;
            self.up_to_date = false;
            self.resume_pending = true;
            self.resumed = true;
        } else {
            self.reset();
        }
    }
}

/// Counts one restart and fails once the bound is reached.
fn restart(restarts: &mut u32) -> Result<()> {
    *restarts += 1;
    if *restarts > MAX_RESTARTS {
        return Err(Error::Decode("shape restarted too often".into()));
    }
    Ok(())
}

fn is_route_error(error: &Error) -> bool {
    match error {
        Error::Transport(_) => true,
        Error::Response { error, .. } => is_route_error(error),
        _ => error.status() == Some(421),
    }
}

/// Parses a snapshot or commit offset. `-1` is not a position.
fn position(value: &str) -> Option<Position> {
    if let Some(rest) = value.strip_prefix("snap.") {
        let (seq, page) = rest.split_once('.')?;
        let seq = decimal(seq)?;
        let page_valid = page == "0"
            || URL_SAFE_NO_PAD
                .decode(page)
                .ok()
                .filter(|bytes| URL_SAFE_NO_PAD.encode(bytes) == page)
                .and_then(|bytes| String::from_utf8(bytes).ok())
                .is_some_and(|id| parse_document_id(&id).is_some());
        return page_valid.then_some(Position::Snapshot { seq });
    }
    decimal(value).map(Position::Commit)
}

/// A canonical decimal no greater than the signed 64-bit maximum.
fn decimal(value: &str) -> Option<u64> {
    let canonical = value == "0"
        || (!value.is_empty()
            && !value.starts_with('0')
            && value.bytes().all(|b| b.is_ascii_digit()));
    value
        .parse::<u64>()
        .ok()
        .filter(|n| canonical && *n <= i64::MAX as u64)
}

fn valid_handle(value: &str) -> bool {
    value
        .strip_prefix("h1-")
        .is_some_and(|hex| lower_hex(hex, 16))
}

fn lower_hex(value: &str, len: usize) -> bool {
    value.len() == len
        && value
            .bytes()
            .all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
}

fn base64url(value: &str) -> bool {
    !value.is_empty()
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
}

#[derive(Deserialize)]
struct Body {
    shape_id: String,
    handle: String,
    offset: String,
    next_offset: String,
    covered_offset: String,
    values_at: String,
    up_to_date: bool,
    cursor_resume: String,
    changes: Vec<WireChange>,
}

#[derive(Deserialize)]
struct WireChange {
    offset: String,
    ops: Vec<WireOp>,
}

#[derive(Deserialize)]
struct WireOp {
    op: String,
    id: String,
    version: Option<String>,
    document: Option<Box<RawValue>>,
}

impl WireOp {
    fn into_op(self, resp: &Resp) -> Result<ShapeOp> {
        let id = parse_document_id(&self.id)
            .ok_or_else(|| resp.invalid("shape op id is invalid".into()))?;
        match (self.op.as_str(), self.version, self.document) {
            ("upsert", Some(version), Some(document)) => Ok(ShapeOp::Upsert {
                id,
                version: Version::new(version)
                    .map_err(|_| resp.invalid("shape op version is invalid".into()))?,
                document,
            }),
            ("delete", None, None) => Ok(ShapeOp::Delete { id }),
            _ => Err(resp.invalid("shape op is malformed".into())),
        }
    }
}

#[derive(Deserialize)]
struct KeyBody {
    handle: String,
    epoch: String,
    key_id: String,
    #[serde(deserialize_with = "secret_key")]
    key: zeroize::Zeroizing<String>,
}

fn secret_key<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> std::result::Result<zeroize::Zeroizing<String>, D::Error> {
    String::deserialize(deserializer).map(zeroize::Zeroizing::new)
}

#[cfg(test)]
mod tests {
    use super::*;
    use reqwest::header::HeaderMap;
    use serde_json::{json, Value};

    fn vectors() -> Value {
        serde_json::from_str(include_str!("../../tests/fixtures/shape-vectors.json")).unwrap()
    }

    #[test]
    fn epoch_key_cache_evicts_past_epochs_before_lookup() {
        let mut keys = EpochKeys::new();
        let past = ("h1-f3171a2230109328".to_owned(), 100, "old".to_owned());
        let current = ("h1-f3171a2230109328".to_owned(), 101, "new".to_owned());
        keys.insert(past.clone(), zeroize::Zeroizing::new([1; 32]));
        keys.insert(current.clone(), zeroize::Zeroizing::new([2; 32]));
        retain_epoch(&mut keys, 101);
        assert!(!keys.contains_key(&past));
        assert_eq!(keys.len(), 1);
        assert_eq!(keys[&current].as_ref(), &[2; 32]);
        retain_epoch(&mut keys, 102);
        assert!(keys.is_empty());
    }

    #[test]
    fn all_offset_vectors_match_the_client_parser() {
        for vector in vectors()["offset_vectors"].as_array().unwrap() {
            let offset = vector["value"].as_str().unwrap();
            assert_eq!(
                offset == "-1" || position(offset).is_some(),
                vector["valid"].as_bool().unwrap(),
                "{offset}"
            );
        }
    }

    #[test]
    fn all_apply_vectors_replace_state_and_advance_atomically() {
        for vector in vectors()["apply_vectors"].as_array().unwrap() {
            let client = Client::builder("http://127.0.0.1:1", "fixture-token")
                .project("01ARZ3NDEKTSV4RRFFQ69G5FAW")
                .build()
                .unwrap();
            let mut sync = ShapeSync::new(client.database("demo").collection("notes").shape());
            let state = Arc::make_mut(&mut sync.state);
            for (id, doc) in vector["initial_state"].as_object().unwrap() {
                state.documents.insert(
                    id.parse().unwrap(),
                    ShapeDocument {
                        version: Version::new(doc["version"].as_str().unwrap()).unwrap(),
                        document: serde_json::from_value(doc["document"].clone()).unwrap(),
                    },
                );
            }
            let body = &vector["body"];
            let mut headers = HeaderMap::new();
            headers.insert(
                "TB-Shape-Handle",
                body["handle"].as_str().unwrap().parse().unwrap(),
            );
            headers.insert(
                "TB-Shape-Next-Offset",
                body["next_offset"].as_str().unwrap().parse().unwrap(),
            );
            headers.insert(
                "TB-Shape-Up-To-Date",
                body["up_to_date"].to_string().parse().unwrap(),
            );
            let resp = Resp {
                status: StatusCode::OK,
                headers,
                body: body.to_string().into_bytes(),
                mutation: false,
            };
            let update = sync
                .apply(
                    body["handle"].as_str().unwrap(),
                    body["offset"].as_str().unwrap(),
                    false,
                    None,
                    &resp,
                )
                .unwrap();
            let actual:serde_json::Map<String,Value>=update.state.iter().map(|(id,doc)| (id.to_string(),json!({"version":doc.version.as_str(),"document":serde_json::from_str::<Value>(doc.document.get()).unwrap()}))).collect();
            assert_eq!(
                Value::Object(actual),
                vector["expected_state"],
                "{}",
                vector["id"]
            );
            assert_eq!(update.offset, vector["expected_offset"].as_str().unwrap());
        }
    }
}
