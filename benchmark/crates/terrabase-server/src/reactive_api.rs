//! Canonical SQL result-view delivery (D-14/D-38/D-46/D-55).
//!
//! Views are keyed by the complete SQL attach input and authorization scope.
//! Snapshot and log revisions are immutable MessagePack envelopes. Empty
//! long-polls are `204` and `no-store`; no client-visible numeric session is
//! used by this transport.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use axum::extract::ws::{Message, WebSocket};
use axum::extract::{Path, Query, State, WebSocketUpgrade};
use axum::http::{HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use serde::{Deserialize, Serialize};
use tokio::sync::Notify;

use crate::api::{ApiError, ApiState};

/// Fixed capability header; it never appears in a URL or cache key.
pub const VIEW_CAPABILITY_HEADER: &str = "X-TerraScale-View-Capability";
/// MessagePack media type for snapshots, logs, resets, and protocol errors.
pub const MSGPACK_MEDIA_TYPE: &str = "application/msgpack";
/// Maximum SQL statement bytes accepted by view attach.
pub const MAX_ATTACH_SQL_BYTES: usize = 262_144;
/// Maximum parameter bytes accepted by view attach.
pub const MAX_ATTACH_BINARY_BYTES: usize = 1_048_576;
/// Maximum HTTP long-poll duration.
pub const LONG_POLL_WINDOW: Duration = Duration::from_secs(25);
const RETENTION: Duration = Duration::from_secs(7 * 24 * 60 * 60);
const CAPABILITY_BUCKET: u64 = 600;
const CAPABILITY_OVERLAP: u64 = 1_200;
const CAPABILITY_KEY: &[u8] = b"terrascale-reactive-capability-v1";

/// Canonical SQL attach input. Legacy collection-condition fields are absent.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
pub struct AttachRequest {
    /// Durable database identity.
    #[serde(rename = "databaseId")]
    pub database_id: String,
    /// Read-only SQL statement.
    pub statement: String,
    /// Base64 encoded SQL parameter bytes.
    #[serde(default)]
    pub parameters: String,
    /// Base64 encoded opaque resume checkpoint.
    #[serde(default)]
    pub checkpoint: Option<String>,
    /// Optional client correlation identity.
    #[serde(default)]
    pub subscription: Option<String>,
    /// Entitlement epoch bound into the view identity.
    #[serde(default)]
    pub entitlement_epoch: u64,
}

/// Attach metadata returned to an SDK.
#[derive(Clone, Debug, Serialize)]
pub struct AttachResponse {
    /// Opaque shared view id.
    pub view: String,
    /// Current view generation.
    pub generation: u64,
    /// Immutable snapshot chunk URL(s).
    pub snapshot_chunks: Vec<String>,
    /// Immutable log URL.
    pub log: String,
    /// Base64 encoded opaque checkpoint.
    pub checkpoint: String,
    /// Capability for [`VIEW_CAPABILITY_HEADER`].
    pub capability: String,
    /// Fixed header spelling.
    pub capability_header: &'static str,
    /// Numeric log offset, separate from the opaque checkpoint.
    pub offset: u64,
}

#[derive(Clone)]
struct Frozen {
    bytes: Arc<[u8]>,
    created: SystemTime,
}

struct View {
    id: String,
    generation: u64,
    scope: String,
    entitlement_epoch: u64,
    head: u64,
    floor: u64,
    snapshot: BTreeMap<u64, Frozen>,
    log: BTreeMap<u64, Frozen>,
}

/// Process-wide canonical shared-view registry.
pub struct ViewStore {
    views: Mutex<BTreeMap<String, View>>,
    changed: Notify,
}

impl ViewStore {
    fn new() -> Self {
        Self { views: Mutex::new(BTreeMap::new()), changed: Notify::new() }
    }

    /// Attaches request metadata without resolving a database. The HTTP
    /// handler performs database existence and authorization checks first.
    pub fn attach(
        &self,
        request: &AttachRequest,
        scope: &str,
    ) -> Result<(AttachResponse, String), ApiError> {
        let parameters =
            decode_base64(&request.parameters).map_err(|_| invalid_attach("parameters"))?;
        if parameters.len() > MAX_ATTACH_BINARY_BYTES {
            return Err(invalid_attach("parameters"));
        }
        let checkpoint = request
            .checkpoint
            .as_deref()
            .map(decode_base64)
            .transpose()
            .map_err(|_| invalid_attach("checkpoint"))?;
        if checkpoint.as_ref().is_some_and(|value| value.is_empty() || value.len() > 4096) {
            return Err(invalid_attach("checkpoint"));
        }
        let statement = request.statement.trim();
        if statement.is_empty() || statement.len() > MAX_ATTACH_SQL_BYTES {
            return Err(invalid_attach("statement"));
        }
        let parsed = terrabase::query::sql::parse(statement, MAX_ATTACH_SQL_BYTES)
            .map_err(|_| sql_error("the statement is not valid read-only SQL"))?;
        if let terrabase::query::sql::LiveSubset::ExecuteOnly { operator } =
            terrabase::query::sql::classify_live_subset(&parsed)
        {
            return Err(ApiError { status: StatusCode::UNPROCESSABLE_ENTITY, code: "not_subscribable", message: format!("the SQL statement is execute-only ({operator})"), fix: "subscribe a single-collection SELECT with scalar predicates, ORDER BY, LIMIT, or MATCH".into(), placement: None });
        }
        let mut key = Vec::with_capacity(statement.len() + scope.len() + parameters.len() + 40);
        key.extend_from_slice(request.database_id.as_bytes());
        key.push(0);
        key.extend_from_slice(statement.as_bytes());
        key.push(0);
        key.extend_from_slice(scope.as_bytes());
        key.push(0);
        key.extend_from_slice(&request.entitlement_epoch.to_be_bytes());
        key.push(0);
        key.extend_from_slice(&parameters);
        let view_id = format!("v-{}", blake3::hash(&key).to_hex());
        let mut views = self.views.lock().expect("reactive view lock is not poisoned");
        let view = views.entry(view_id.clone()).or_insert_with(|| {
            let bytes = snapshot_envelope(&view_id, 1, 0, checkpoint_bytes(&view_id, 1, 0));
            let mut snapshot = BTreeMap::new();
            snapshot.insert(0, Frozen { bytes: Arc::from(bytes), created: SystemTime::now() });
            View {
                id: view_id.clone(),
                generation: 1,
                scope: scope.to_owned(),
                entitlement_epoch: request.entitlement_epoch,
                head: 0,
                floor: 0,
                snapshot,
                log: BTreeMap::new(),
            }
        });
        if view.scope != scope || view.entitlement_epoch != request.entitlement_epoch {
            return Err(ApiError {
                status: StatusCode::CONFLICT,
                code: "view_identity_conflict",
                message: "the shared view identity has a conflicting authorization binding".into(),
                fix: "attach with the current authorization scope and entitlement epoch".into(),
                placement: None,
            });
        }
        let offset = view.head;
        let checkpoint = checkpoint_bytes(&view.id, view.generation, offset);
        let capability =
            capability_for(&view.id, view.generation, scope, view.entitlement_epoch, now_seconds());
        Ok((
            AttachResponse {
                view: view.id.clone(),
                generation: view.generation,
                snapshot_chunks: vec![format!(
                    "/api/v1/reactive/views/{}/{}/snapshot/0?cut={offset}",
                    view.id, view.generation
                )],
                log: format!(
                    "/api/v1/reactive/views/{}/{}/log?offset={offset}",
                    view.id, view.generation
                ),
                checkpoint: encode_base64(&checkpoint),
                capability,
                capability_header: VIEW_CAPABILITY_HEADER,
                offset,
            },
            view.id.clone(),
        ))
    }

    /// Append one already encoded manifest message exactly once.
    /// Appends one canonical manifest message exactly once.
    pub fn append(
        &self,
        view_id: &str,
        revision: u64,
        message: terrabase_sync_core::wire::Message,
    ) -> Result<(), ApiError> {
        let bytes =
            terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
                features: 0,
                correlation: 0,
                causation: revision,
                message,
            })
            .map_err(|_| frame_error())?;
        let mut views = self.views.lock().expect("reactive view lock is not poisoned");
        let Some(view) = views.get_mut(view_id) else {
            return Err(unknown_view());
        };
        if revision <= view.head {
            return Ok(());
        }
        let created = SystemTime::now();
        view.head = revision;
        view.log.insert(revision, Frozen { bytes: Arc::from(bytes), created });
        while view.log.values().next().is_some_and(|item| {
            created.duration_since(item.created).unwrap_or_default() > RETENTION
        }) {
            if let Some((&oldest, _)) = view.log.iter().next() {
                view.floor = oldest;
                view.log.remove(&oldest);
            }
        }
        drop(views);
        self.changed.notify_waiters();
        Ok(())
    }

    fn read_log(
        &self,
        view_id: &str,
        generation: u64,
        offset: u64,
    ) -> Result<ReadResult, ApiError> {
        let views = self.views.lock().expect("reactive view lock is not poisoned");
        let view = views.get(view_id).ok_or_else(unknown_view)?;
        if generation != view.generation {
            return Err(unknown_view());
        }
        if offset < view.floor {
            return Ok(ReadResult::Reset(reset_bytes(view)));
        }
        if offset > view.head {
            return Err(offset_gap());
        }
        if let Some((revision, frozen)) = view.log.range((offset + 1)..).next() {
            return Ok(ReadResult::Data { revision: *revision, frozen: frozen.clone() });
        }
        Ok(ReadResult::Empty)
    }

    fn read_snapshot(&self, view_id: &str, generation: u64, cut: u64) -> Result<Frozen, ApiError> {
        let views = self.views.lock().expect("reactive view lock is not poisoned");
        let view = views.get(view_id).ok_or_else(unknown_view)?;
        if generation != view.generation {
            return Err(unknown_view());
        }
        view.snapshot.get(&cut).cloned().ok_or_else(unknown_view)
    }
}

static STORE: OnceLock<Arc<ViewStore>> = OnceLock::new();
fn store() -> &'static Arc<ViewStore> {
    STORE.get_or_init(|| Arc::new(ViewStore::new()))
}

/// Returns the process-shared view store for the journal integration seam.
#[must_use]
pub fn view_store() -> Arc<ViewStore> {
    Arc::clone(store())
}

/// Encodes an empty snapshot envelope for conformance fixtures.
#[must_use]
pub fn empty_snapshot_bytes(view: &str, generation: u64, revision: u64) -> Vec<u8> {
    snapshot_envelope(view, generation, revision, checkpoint_bytes(view, generation, revision))
}

/// Appends one canonical server message to a shared view's immutable log.
pub fn append_message(
    view: &str,
    revision: u64,
    message: terrabase_sync_core::wire::Message,
) -> Result<(), ApiError> {
    store().append(view, revision, message)
}

/// Attaches to a canonical SQL result view and issues its capability.
#[utoipa::path(post, path = "/api/v1/reactive/views", responses((status = 201, description = "Canonical SQL view attached")))]
pub async fn attach_view(State(state): State<Arc<ApiState>>, body: String) -> Response {
    let request: AttachRequest = match serde_json::from_str(&body) {
        Ok(request) => request,
        Err(_) => return invalid_attach("request").into_response(),
    };
    if request.database_id.is_empty() || request.database_id.len() > 256 {
        return invalid_attach("databaseId").into_response();
    }
    if let Err(error) = crate::api::document_host(&state, &request.database_id) {
        return error.into_response();
    }
    if let Err(error) = crate::api::check_read_guard(
        &state,
        &request.database_id,
        terrabase::observability::Operation::Subscribe,
    ) {
        return error.into_response();
    }
    let scope = format!("{}:{}:{}", state.scope.0, state.scope.1, request.database_id);
    let (response, view_id) = match store().attach(&request, &scope) {
        Ok(result) => result,
        Err(error) => return error.into_response(),
    };
    if let Err(rejection) = state.guards.attach_view(&request.database_id, &view_id) {
        state.metrics.record_admission_rejection(
            terrabase::observability::Operation::Subscribe,
            terrabase::observability::AdmissionReason::Capacity,
        );
        return ApiError::abuse_guard(&rejection).into_response();
    }
    let mut body = serde_json::to_vec(&response).expect("attach response is serializable");
    body.push(b'\n');
    let mut result = (StatusCode::CREATED, body).into_response();
    result
        .headers_mut()
        .insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static("application/json"));
    result.headers_mut().insert("x-content-type-options", HeaderValue::from_static("nosniff"));
    result
}

/// Serves an immutable snapshot chunk at an explicit journal cut.
#[utoipa::path(get, path = "/api/v1/reactive/views/{view}/{generation}/snapshot/{chunk}", responses((status = 200, description = "Immutable view snapshot")))]
pub async fn snapshot(
    State(_state): State<Arc<ApiState>>,
    Path((view, generation, chunk)): Path<(String, u64, String)>,
    Query(query): Query<CutQuery>,
) -> Response {
    if chunk != "0" {
        return unknown_view().into_response();
    }
    match store().read_snapshot(&view, generation, query.cut.unwrap_or(0)) {
        Ok(frozen) => {
            immutable_response(frozen, format!("\"{view}-snapshot-{}\"", query.cut.unwrap_or(0)))
        }
        Err(error) => error.into_response(),
    }
}

/// Serves the next immutable log revision, waiting at most 25 seconds.
#[utoipa::path(get, path = "/api/v1/reactive/views/{view}/{generation}/log", responses((status = 200, description = "Immutable view delta")))]
pub async fn view_log(
    State(_state): State<Arc<ApiState>>,
    Path((view, generation)): Path<(String, u64)>,
    Query(query): Query<OffsetQuery>,
    headers: HeaderMap,
) -> Response {
    let Some(offset) = query.offset else {
        return invalid_offset().into_response();
    };
    if let Err(error) = verify_header(&headers, &view, generation) {
        return error.into_response();
    }
    let started = tokio::time::Instant::now();
    loop {
        match store().read_log(&view, generation, offset) {
            Ok(ReadResult::Data { revision, frozen }) => {
                return immutable_response(frozen, format!("\"{view}-{generation}-{revision}\""));
            }
            Ok(ReadResult::Reset(bytes)) => return reset_response(bytes),
            Ok(ReadResult::Empty) => {}
            Err(error) => return error.into_response(),
        }
        let remaining = LONG_POLL_WINDOW.saturating_sub(started.elapsed());
        if remaining.is_zero() {
            return empty_timeout();
        }
        let notified = store().changed.notified();
        if tokio::time::timeout(remaining, notified).await.is_err() {
            return empty_timeout();
        }
    }
}

/// Retained migration symbol: old database-local JSON polling is retired.
#[allow(clippy::unused_async)]
pub async fn poll(
    State(_state): State<Arc<ApiState>>,
    Path(_name): Path<String>,
    _body: String,
) -> Response {
    ApiError {
        status: StatusCode::GONE,
        code: "reactive_route_retired",
        message: "database-local JSON reactive polling is retired".into(),
        fix: "POST the canonical SQL statement to /api/v1/reactive/views".into(),
        placement: None,
    }
    .into_response()
}

/// Optional WebSocket transport using the same indexed manifest envelopes.
#[utoipa::path(get, path = "/api/v1/databases/{name}/reactive", responses((status = 101, description = "Reactive WebSocket connected")))]
pub async fn websocket(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    upgrade: WebSocketUpgrade,
) -> Response {
    upgrade.on_upgrade(move |socket| serve_socket(socket, state, name))
}

async fn serve_socket(mut socket: WebSocket, state: Arc<ApiState>, database: String) {
    let Some(Ok(Message::Binary(bytes))) = socket.recv().await else {
        return;
    };
    let Ok(envelope) = terrabase_sync_core::wire::decode_envelope(&bytes) else {
        let _ = socket
            .send(Message::Binary(
                error_bytes(terrabase_sync_core::wire::ErrorCode::MalformedFrame).into(),
            ))
            .await;
        return;
    };
    let terrabase_sync_core::wire::Message::Subscribe {
        subscription,
        query,
        parameters,
        checkpoint,
    } = envelope.message
    else {
        let _ = socket
            .send(Message::Binary(
                error_bytes(terrabase_sync_core::wire::ErrorCode::MalformedFrame).into(),
            ))
            .await;
        return;
    };
    let request = AttachRequest {
        database_id: database.clone(),
        statement: query,
        parameters: encode_base64(&parameters),
        checkpoint: checkpoint.map(|value| encode_base64(&value)),
        subscription: Some(subscription),
        entitlement_epoch: 0,
    };
    let scope = format!("{}:{}:{}", state.scope.0, state.scope.1, database);
    let Ok((_, view_id)) = store().attach(&request, &scope) else {
        let _ = socket
            .send(Message::Binary(
                error_bytes(terrabase_sync_core::wire::ErrorCode::Unavailable).into(),
            ))
            .await;
        return;
    };
    if let Ok(snapshot) = store().read_snapshot(&view_id, 1, 0) {
        let _ = socket.send(Message::Binary(snapshot.bytes.to_vec().into())).await;
    }
}

enum ReadResult {
    Data { revision: u64, frozen: Frozen },
    Reset(Vec<u8>),
    Empty,
}
/// Log query fields.
#[derive(Debug, Deserialize)]
pub struct OffsetQuery {
    /// Log offset.
    #[serde(default)]
    pub offset: Option<u64>,
}
/// Snapshot query fields.
#[derive(Debug, Deserialize)]
pub struct CutQuery {
    /// Snapshot cut.
    #[serde(default)]
    pub cut: Option<u64>,
}

fn immutable_response(frozen: Frozen, etag: String) -> Response {
    let remaining = frozen
        .created
        .elapsed()
        .ok()
        .map_or(0, |elapsed| RETENTION.saturating_sub(elapsed).as_secs());
    let mut response = (StatusCode::OK, frozen.bytes.to_vec()).into_response();
    let headers = response.headers_mut();
    headers.insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static(MSGPACK_MEDIA_TYPE));
    headers.insert("cache-control", HeaderValue::from_static("private, max-age=0"));
    if let Ok(value) = HeaderValue::from_str(&format!("public, max-age={remaining}, immutable")) {
        headers.insert("cloudflare-cdn-cache-control", value);
    }
    if let Ok(value) = HeaderValue::from_str(&etag) {
        headers.insert(axum::http::header::ETAG, value);
    }
    headers.insert("x-content-type-options", HeaderValue::from_static("nosniff"));
    response
}
fn empty_timeout() -> Response {
    let mut response = StatusCode::NO_CONTENT.into_response();
    let headers = response.headers_mut();
    headers.insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static(MSGPACK_MEDIA_TYPE));
    headers.insert("cache-control", HeaderValue::from_static("no-store"));
    headers.insert("cloudflare-cdn-cache-control", HeaderValue::from_static("no-store"));
    response
}
fn reset_response(bytes: Vec<u8>) -> Response {
    let mut response = (StatusCode::OK, bytes).into_response();
    let headers = response.headers_mut();
    headers.insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static(MSGPACK_MEDIA_TYPE));
    headers.insert("cache-control", HeaderValue::from_static("no-store"));
    headers.insert("cloudflare-cdn-cache-control", HeaderValue::from_static("no-store"));
    response
}
fn verify_header(headers: &HeaderMap, view: &str, generation: u64) -> Result<(), ApiError> {
    let Some(raw) = headers.get(VIEW_CAPABILITY_HEADER).and_then(|value| value.to_str().ok())
    else {
        return Err(capability_error());
    };
    let parts: Vec<&str> = raw.split('.').collect();
    if parts.len() != 7
        || parts[0] != "v1"
        || parts[1] != view
        || parts[2].parse::<u64>().ok() != Some(generation)
    {
        return Err(capability_error());
    }
    let expiry = parts[5].parse::<u64>().ok().ok_or_else(capability_error)?;
    let now = now_seconds();
    if now >= expiry || now + CAPABILITY_OVERLAP < expiry {
        return Err(capability_error());
    }
    let body = parts[..6].join(".");
    let expected = hmac_sha256_hex(CAPABILITY_KEY, body.as_bytes());
    if !constant_time_eq(expected.as_bytes(), parts[6].as_bytes()) {
        return Err(capability_error());
    }
    Ok(())
}

/// Deterministically mints a D-14 capability for conformance tests.
#[must_use]
pub fn capability_for(
    view: &str,
    generation: u64,
    scope: &str,
    entitlement_epoch: u64,
    now: u64,
) -> String {
    let expiry = (now / CAPABILITY_BUCKET + 2) * CAPABILITY_BUCKET;
    let body =
        format!("v1.{view}.{generation}.{}.{}.{}", encode_scope(scope), entitlement_epoch, expiry);
    format!("{body}.{}", hmac_sha256_hex(CAPABILITY_KEY, body.as_bytes()))
}
fn encode_scope(scope: &str) -> String {
    if scope.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b':'))
    {
        scope.to_owned()
    } else {
        format!("b64:{}", encode_base64(scope.as_bytes()))
    }
}
fn checkpoint_bytes(view: &str, generation: u64, revision: u64) -> Vec<u8> {
    let mut bytes = b"terrascale.sync.v2\0".to_vec();
    bytes.extend_from_slice(&blake3::hash(view.as_bytes()).as_bytes()[..16]);
    bytes.extend_from_slice(&generation.to_be_bytes());
    bytes.extend_from_slice(&revision.to_be_bytes());
    bytes
}
fn snapshot_envelope(
    subscription: &str,
    generation: u64,
    revision: u64,
    checkpoint: Vec<u8>,
) -> Vec<u8> {
    let snapshot = terrabase_sync_core::wire::Snapshot {
        view_generation: generation,
        authorization_generation: 1,
        revision,
        items: Vec::new(),
        digest: digest_bytes(&[]),
        checkpoint,
    };
    terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
        features: 0,
        correlation: 0,
        causation: revision,
        message: terrabase_sync_core::wire::Message::Snapshot {
            subscription: subscription.to_owned(),
            snapshot,
        },
    })
    .expect("bounded empty snapshot encodes")
}
fn reset_bytes(view: &View) -> Vec<u8> {
    snapshot_envelope(
        &view.id,
        view.generation,
        view.head,
        checkpoint_bytes(&view.id, view.generation, view.head),
    )
}
fn error_bytes(code: terrabase_sync_core::wire::ErrorCode) -> Vec<u8> {
    terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
        features: 0,
        correlation: 0,
        causation: 0,
        message: terrabase_sync_core::wire::Message::Error { code, detail: None },
    })
    .expect("bounded error encodes")
}
fn digest_bytes(bytes: &[u8]) -> String {
    blake3::hash(bytes).to_hex().to_string()
}
fn now_seconds() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}
fn invalid_attach(field: &str) -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_reactive_attach",
        message: format!("the reactive attach {field} is invalid"),
        fix: "send databaseId, a bounded read-only SQL statement, and base64 parameters".into(),
        placement: None,
    }
}
fn sql_error(message: &str) -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_sql",
        message: message.into(),
        fix: "send one read-only SQLite SELECT in the subscribable live subset".into(),
        placement: None,
    }
}
fn capability_error() -> ApiError {
    ApiError {
        status: StatusCode::UNAUTHORIZED,
        code: "invalid_view_capability",
        message: "the view capability is missing, expired, or not bound to this view generation"
            .into(),
        fix: "attach the view again for a fresh capability".into(),
        placement: None,
    }
}
fn unknown_view() -> ApiError {
    ApiError {
        status: StatusCode::NOT_FOUND,
        code: "unknown_view",
        message: "the view generation is unknown or no longer retained".into(),
        fix: "POST the canonical SQL attach request for a fresh view".into(),
        placement: None,
    }
}
fn offset_gap() -> ApiError {
    ApiError {
        status: StatusCode::CONFLICT,
        code: "offset_gap",
        message: "the requested log offset is ahead of the immutable log".into(),
        fix: "re-attach for a fresh snapshot and log URL".into(),
        placement: None,
    }
}
fn invalid_offset() -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_reactive_offset",
        message: "log reads require a non-negative offset".into(),
        fix: "follow the offset in the attach response and increment it after each revision".into(),
        placement: None,
    }
}
fn frame_error() -> ApiError {
    ApiError {
        status: StatusCode::INTERNAL_SERVER_ERROR,
        code: "reactive_frame_encode_failed",
        message: "the canonical MessagePack frame could not be encoded".into(),
        fix: "retry the subscription".into(),
        placement: None,
    }
}

fn encode_base64(bytes: &[u8]) -> String {
    const A: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut output = String::new();
    for chunk in bytes.chunks(3) {
        let a = chunk[0];
        let b = *chunk.get(1).unwrap_or(&0);
        let c = *chunk.get(2).unwrap_or(&0);
        output.push(A[(a >> 2) as usize] as char);
        output.push(A[((a & 3) << 4 | b >> 4) as usize] as char);
        output.push(if chunk.len() > 1 {
            A[((b & 15) << 2 | c >> 6) as usize] as char
        } else {
            '='
        });
        output.push(if chunk.len() > 2 { A[(c & 63) as usize] as char } else { '=' });
    }
    output
}
fn decode_base64(input: &str) -> Result<Vec<u8>, ()> {
    if input.is_empty() {
        return Ok(Vec::new());
    }
    let values: Vec<u8> = input
        .bytes()
        .filter(|byte| !byte.is_ascii_whitespace())
        .map(|byte| match byte {
            b'A'..=b'Z' => Ok(byte - b'A'),
            b'a'..=b'z' => Ok(byte - b'a' + 26),
            b'0'..=b'9' => Ok(byte - b'0' + 52),
            b'+' => Ok(62),
            b'/' => Ok(63),
            b'=' => Ok(64),
            _ => Err(()),
        })
        .collect::<Result<_, _>>()?;
    if values.len() % 4 != 0 {
        return Err(());
    }
    let mut output = Vec::new();
    for chunk in values.chunks(4) {
        if chunk[0] == 64 || chunk[1] == 64 || (chunk[2] == 64 && chunk[3] != 64) {
            return Err(());
        }
        output.push((chunk[0] << 2) | (chunk[1] >> 4));
        if chunk[2] != 64 {
            output.push((chunk[1] << 4) | (chunk[2] >> 2));
        }
        if chunk[3] != 64 {
            output.push((chunk[2] << 6) | chunk[3]);
        }
    }
    Ok(output)
}
fn hmac_sha256_hex(key: &[u8], message: &[u8]) -> String {
    let mut block = [0u8; 64];
    if key.len() > 64 {
        block[..32].copy_from_slice(&sha256(key));
    } else {
        block[..key.len()].copy_from_slice(key);
    }
    let mut inner = [0u8; 64];
    let mut outer = [0u8; 64];
    for index in 0..64 {
        inner[index] = block[index] ^ 0x36;
        outer[index] = block[index] ^ 0x5c;
    }
    let mut input = inner.to_vec();
    input.extend_from_slice(message);
    let hash = sha256(&input);
    let mut output = outer.to_vec();
    output.extend_from_slice(&hash);
    sha256(&output).iter().map(|byte| format!("{byte:02x}")).collect()
}
fn sha256(input: &[u8]) -> [u8; 32] {
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];
    let mut data = input.to_vec();
    data.push(0x80);
    while data.len() % 64 != 56 {
        data.push(0);
    }
    data.extend_from_slice(
        &(u64::try_from(input.len()).unwrap_or(u64::MAX).saturating_mul(8)).to_be_bytes(),
    );
    let mut state = [
        0x6a09e667u32,
        0xbb67ae85,
        0x3c6ef372,
        0xa54ff53a,
        0x510e527f,
        0x9b05688c,
        0x1f83d9ab,
        0x5be0cd19,
    ];
    for chunk in data.chunks_exact(64) {
        let mut words = [0u32; 64];
        for (index, bytes) in chunk.chunks_exact(4).take(16).enumerate() {
            words[index] = u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
        }
        for index in 16..64 {
            let s0 = words[index - 15].rotate_right(7)
                ^ words[index - 15].rotate_right(18)
                ^ (words[index - 15] >> 3);
            let s1 = words[index - 2].rotate_right(17)
                ^ words[index - 2].rotate_right(19)
                ^ (words[index - 2] >> 10);
            words[index] =
                words[index - 16].wrapping_add(s0).wrapping_add(words[index - 7]).wrapping_add(s1);
        }
        let (mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut h) =
            (state[0], state[1], state[2], state[3], state[4], state[5], state[6], state[7]);
        for index in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let temp1 = h
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(K[index])
                .wrapping_add(words[index]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(maj);
            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }
        state[0] = state[0].wrapping_add(a);
        state[1] = state[1].wrapping_add(b);
        state[2] = state[2].wrapping_add(c);
        state[3] = state[3].wrapping_add(d);
        state[4] = state[4].wrapping_add(e);
        state[5] = state[5].wrapping_add(f);
        state[6] = state[6].wrapping_add(g);
        state[7] = state[7].wrapping_add(h);
    }
    let mut output = [0u8; 32];
    for (index, word) in state.iter().enumerate() {
        output[index * 4..index * 4 + 4].copy_from_slice(&word.to_be_bytes());
    }
    output
}

/// Compatibility holder retained until the owner removes the legacy field.
#[derive(Default)]
pub struct ReactiveSessions;
