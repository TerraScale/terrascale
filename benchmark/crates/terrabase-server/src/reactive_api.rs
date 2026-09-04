//! The reactive transports (`S-04` slice 4, D-14/D-38): one shared
//! pump-and-drain core behind two wire shapes — `POST /api/v1/reactive/poll`
//! (HTTP fallback) and `GET /api/v1/databases/{name}/reactive` (WebSocket).
//!
//! Delivery bytes use the bounded `terrascale.sync.v2` indexed-array codec.
//! The member host still exposes the older `ReactiveFrame` seam while the
//! shared SQL-view attach lane is integrated. This module adapts that seam at
//! the transport boundary and never emits the retired map format.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::Mutex;

use crate::api::ApiError;
use crate::api::ApiState;
use axum::extract::Path;
use axum::extract::State;
use axum::extract::WebSocketUpgrade;
use axum::extract::ws::Message;
use axum::extract::ws::WebSocket;
use axum::http::HeaderValue;
use axum::http::StatusCode;
use axum::response::IntoResponse;
use axum::response::Response;
use terrabase_sync_core::server::FieldValue;
use terrabase_sync_core::server::SubscriptionSpec;

/// One live reactive subscription the API tracks for its transports.
struct ReactiveSession {
    database: String,
    receiver: std::sync::mpsc::Receiver<terrabase_tbep::reactive::ReactiveFrame>,
}

/// Holds one admitted WebSocket slot (and, once subscribed, its
/// distinct-view slot) for a socket task. Every exit path drops the lease,
/// so a closed, failed, or refused socket always frees both D-64 budgets.
struct WsLease {
    guards: Arc<crate::guards::AbuseGuards>,
    database: String,
    view: Option<String>,
}

impl WsLease {
    /// Attaches the socket's distinct-view slot. A refused attach leaves
    /// any previous view untouched; dropping the lease still frees the
    /// WebSocket slot.
    #[allow(dead_code, reason = "the distinct-view attach is wired in the S-09 socket step")]
    fn attach(&mut self, view: String) -> Result<(), crate::guards::GuardRejection> {
        self.guards.attach_view(&self.database, &view)?;
        self.view = Some(view);
        Ok(())
    }
}

impl Drop for WsLease {
    fn drop(&mut self) {
        if let Some(view) = self.view.take() {
            self.guards.detach_view(&self.database, &view);
        }
        self.guards.release_ws(&self.database);
    }
}

/// Sends one typed guard refusal down a socket that never got its
/// subscription: the JSON error shape with the D-64 code and fix.
async fn send_guard_refusal(socket: &mut WebSocket, rejection: &crate::guards::GuardRejection) {
    let mut body = String::from("{\"error\":{\"code\":\"");
    body.push_str(rejection.code());
    body.push_str("\",\"message\":\"");
    crate::api::push_json_escaped(&mut body, &rejection.message());
    body.push_str("\",\"fix\":\"");
    crate::api::push_json_escaped(&mut body, &rejection.fix());
    body.push_str("\"}}");
    socket.send(Message::Text(body.into())).await.ok();
}

/// The API's reactive sessions: subscription id → its database and feed.
#[derive(Default)]
pub struct ReactiveSessions {
    sessions: Mutex<BTreeMap<u64, ReactiveSession>>,
}

impl ReactiveSessions {
    /// Records one opened subscription.
    ///
    /// # Panics
    ///
    /// Panics when the session lock is poisoned by a panicked transport.
    pub fn insert(
        &self,
        subscription: u64,
        database: String,
        receiver: std::sync::mpsc::Receiver<terrabase_tbep::reactive::ReactiveFrame>,
    ) {
        self.sessions
            .lock()
            .expect("the reactive session lock is not poisoned")
            .insert(subscription, ReactiveSession { database, receiver });
    }

    /// The database one subscription was opened on: a poll or unsubscribe
    /// against another database's route does not touch it.
    /// The database one subscription was opened on
    ///
    /// # Panics
    ///
    /// Panics when the session lock is poisoned by a panicked transport.
    pub fn database_of(&self, subscription: u64) -> Option<String> {
        self.sessions
            .lock()
            .expect("the reactive session lock is not poisoned")
            .get(&subscription)
            .map(|session| session.database.clone())
    }

    /// Drops one subscription's session (the transport closed).
    ///
    /// # Panics
    ///
    /// Panics when the session lock is poisoned by a panicked transport.
    pub fn remove(&self, subscription: u64) {
        self.sessions
            .lock()
            .expect("the reactive session lock is not poisoned")
            .remove(&subscription);
    }
}

/// The bound scope a reactive pump needs for one database.
fn bound_scope(state: &ApiState, name: &str) -> terrabase_tbep::registry::BoundScope {
    terrabase_tbep::registry::BoundScope {
        principal: "spiffe://terrascale.tech/api-reactive".into(),
        tenant: state.scope.0.clone(),
        project: state.scope.1.clone(),
        database: name.to_owned(),
    }
}

/// Parses one subscription specification out of a request's JSON value.
fn spec_of(request: &serde_json::Value) -> Result<SubscriptionSpec, ApiError> {
    use terrabase_sync_core::server::Comparison;
    use terrabase_sync_core::server::Condition;

    let Some(collection) = request.get("collection").and_then(serde_json::Value::as_str) else {
        return Err(ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_collection",
            message: "the subscribe request names no collection".into(),
            fix: "add \"collection\": \"<name>\" to the request body".into(),
            placement: None,
        });
    };
    let mut conditions = Vec::new();
    if let Some(items) = request.get("conditions").and_then(serde_json::Value::as_array) {
        for item in items {
            let Some(field) = item.get("field").and_then(serde_json::Value::as_str) else {
                return Err(ApiError {
                    status: StatusCode::BAD_REQUEST,
                    code: "missing_condition_field",
                    message: "a condition names no field".into(),
                    fix: "each condition is {\"field\":…,\"op\":…,\"value\":…}".into(),
                    placement: None,
                });
            };
            let op = match item.get("op").and_then(serde_json::Value::as_str) {
                Some("equals") => Comparison::Equals,
                Some("less") => Comparison::Less,
                Some("less_or_equal") => Comparison::LessOrEqual,
                Some("greater") => Comparison::Greater,
                Some("greater_or_equal") => Comparison::GreaterOrEqual,
                _ => {
                    return Err(ApiError {
                        status: StatusCode::BAD_REQUEST,
                        code: "invalid_condition_op",
                        message: "a condition names no known comparison".into(),
                        fix: "op is one of equals, less, less_or_equal, greater, greater_or_equal"
                            .into(),
                        placement: None,
                    });
                }
            };
            let Some(value) = item.get("value") else {
                return Err(ApiError {
                    status: StatusCode::BAD_REQUEST,
                    code: "missing_condition_value",
                    message: "a condition names no value".into(),
                    fix: "add \"value\" to the condition".into(),
                    placement: None,
                });
            };
            let value = match value {
                serde_json::Value::String(text) => FieldValue::Text(text.clone()),
                serde_json::Value::Number(number) => {
                    FieldValue::Number(number.as_f64().unwrap_or_default())
                }
                _ => {
                    return Err(ApiError {
                        status: StatusCode::BAD_REQUEST,
                        code: "invalid_condition_value",
                        message: "a condition's value is neither text nor number".into(),
                        fix: "dependency values are strings or numbers (the stored column classes)"
                            .into(),
                        placement: None,
                    });
                }
            };
            conditions.push(Condition { field: field.to_owned(), op, value });
        }
    }
    let projection = request
        .get("projection")
        .and_then(serde_json::Value::as_array)
        .map(|fields| {
            fields
                .iter()
                .filter_map(serde_json::Value::as_str)
                .map(str::to_owned)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    Ok(SubscriptionSpec { collection: collection.to_owned(), conditions, projection })
}

/// Converts one extracted value into the canonical JSON bytes carried by the
/// sync-v2 wire. The wire treats these bytes as opaque and never transcodes
/// them into nested `MessagePack` values.
fn canonical_value(value: &FieldValue) -> Result<Vec<u8>, ApiError> {
    match value {
        FieldValue::Text(text) => serde_json::to_vec(text),
        FieldValue::Number(number) => {
            let number = serde_json::Number::from_f64(*number).ok_or_else(frame_encode_failed)?;
            serde_json::to_vec(&number)
        }
    }
    .map_err(|_| frame_encode_failed())
}

fn frame_encode_failed() -> ApiError {
    ApiError {
        status: StatusCode::INTERNAL_SERVER_ERROR,
        code: "frame_encode_failed",
        message: "the delivery frame could not be encoded".into(),
        fix: "retry the subscription; this is a server-side encoding fault".into(),
        placement: None,
    }
}

fn projected_fields(
    fields: &[(String, FieldValue)],
) -> Result<Vec<terrabase_sync_core::wire::ProjectedField>, ApiError> {
    fields
        .iter()
        .map(|(path, value)| {
            Ok(terrabase_sync_core::wire::ProjectedField {
                path: path.clone(),
                value: canonical_value(value)?,
            })
        })
        .collect()
}

/// The old member seam gives this adapter a process-local numeric identity.
/// Keep it opaque to the wire by carrying its textual form. The real view
/// capability is issued by the shared attach endpoint once that host API is
/// available.
fn wire_subscription(subscription: u64) -> String {
    subscription.to_string()
}

/// Produces a bounded opaque checkpoint for an adapted frame. A checkpoint is
/// never a database cursor or a client-visible numeric session token.
fn wire_checkpoint(subscription: u64, generation: u64, revision: u64) -> Vec<u8> {
    let mut checkpoint = Vec::with_capacity(36);
    checkpoint.extend_from_slice(b"reactive-v2\0");
    checkpoint.extend_from_slice(&subscription.to_be_bytes());
    checkpoint.extend_from_slice(&generation.to_be_bytes());
    checkpoint.extend_from_slice(&revision.to_be_bytes());
    checkpoint
}

fn digest_bytes(bytes: &[u8]) -> String {
    blake3::hash(bytes).to_hex().to_string()
}

fn snapshot_digest(items: &[terrabase_sync_core::wire::ResultItem]) -> String {
    let mut bytes = Vec::new();
    for item in items {
        bytes.extend_from_slice(&(item.identity.len() as u64).to_be_bytes());
        bytes.extend_from_slice(item.identity.as_bytes());
        for field in &item.fields {
            bytes.extend_from_slice(&(field.path.len() as u64).to_be_bytes());
            bytes.extend_from_slice(field.path.as_bytes());
            bytes.extend_from_slice(&(field.value.len() as u64).to_be_bytes());
            bytes.extend_from_slice(&field.value);
        }
    }
    digest_bytes(&bytes)
}

fn snapshot(
    subscription: u64,
    members: &[(String, Vec<(String, FieldValue)>)],
) -> Result<terrabase_sync_core::wire::Snapshot, ApiError> {
    let items = members
        .iter()
        .map(|(identity, fields)| {
            Ok(terrabase_sync_core::wire::ResultItem {
                identity: identity.clone(),
                fields: projected_fields(fields)?,
            })
        })
        .collect::<Result<Vec<_>, ApiError>>()?;
    Ok(terrabase_sync_core::wire::Snapshot {
        view_generation: 1,
        authorization_generation: 1,
        revision: 0,
        digest: snapshot_digest(&items),
        checkpoint: wire_checkpoint(subscription, 1, 0),
        items,
    })
}

fn operation(
    operation: &terrabase_sync_core::server::DeltaOp,
) -> Result<terrabase_sync_core::wire::ResultOperation, ApiError> {
    use terrabase_sync_core::server::DeltaOp;
    use terrabase_sync_core::wire::ResultOperation;
    match operation {
        DeltaOp::Insert { document, fields } => Ok(ResultOperation::Insert {
            identity: document.clone(),
            // The legacy matcher does not retain an order position. The SQL
            // view host must supply it when the shared-view seam lands.
            position: 0,
            fields: projected_fields(fields)?,
        }),
        DeltaOp::Update { document, fields } => Ok(ResultOperation::Update {
            identity: document.clone(),
            fields: projected_fields(fields)?,
        }),
        DeltaOp::Remove { document } => Ok(ResultOperation::Remove { identity: document.clone() }),
    }
}

/// Encodes one delivery frame as a complete canonical sync-v2 envelope.
fn encode_frame(frame: &terrabase_tbep::reactive::ReactiveFrame) -> Result<Vec<u8>, ApiError> {
    use terrabase_sync_core::wire::{Envelope, Message, ResetReason, encode_envelope};
    use terrabase_tbep::reactive::ReactiveFrame;
    let (subscription, message) = match frame {
        ReactiveFrame::Seed { subscription, members } => {
            let subscription_text = wire_subscription(*subscription);
            let snapshot = snapshot(*subscription, members)?;
            (*subscription, Message::Snapshot { subscription: subscription_text, snapshot })
        }
        ReactiveFrame::Delta { subscription, delta } => {
            let subscription_text = wire_subscription(*subscription);
            let operation = operation(&delta.operation)?;
            let mut digest_input = Vec::new();
            digest_input.extend_from_slice(&delta.revision.to_be_bytes());
            digest_input.extend_from_slice(format!("{operation:?}").as_bytes());
            (
                *subscription,
                Message::Delta {
                    subscription: subscription_text,
                    view_generation: 1,
                    from_revision: delta.revision.saturating_sub(1),
                    to_revision: delta.revision,
                    operations: vec![operation],
                    digest: digest_bytes(&digest_input),
                    checkpoint: wire_checkpoint(*subscription, 1, delta.revision),
                },
            )
        }
        ReactiveFrame::Reset { subscription } => {
            let subscription_text = wire_subscription(*subscription);
            let snapshot = snapshot(*subscription, &[])?;
            (
                *subscription,
                Message::Reset {
                    subscription: subscription_text,
                    reason: ResetReason::Backpressure,
                    snapshot,
                },
            )
        }
    };
    encode_envelope(&Envelope {
        features: 0,
        correlation: subscription,
        causation: match frame {
            ReactiveFrame::Delta { delta, .. } => delta.revision,
            _ => 0,
        },
        message,
    })
    .map_err(|_| frame_encode_failed())
}

/// The poll transport (`POST /api/v1/databases/{name}/reactive/poll`):
/// subscribe, drain, or unsubscribe. The response body is one
/// `MessagePack` array of frames.
#[allow(clippy::too_many_lines, reason = "one transport contract, one function")]
#[utoipa::path(post, path = "/api/v1/databases/{name}/reactive/poll", responses((status = 200, description = "Reactive query changes")))]
pub async fn poll(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    body: String,
) -> Response {
    let request: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the reactive poll body is not JSON".into(),
                fix: "send {\"op\":\"subscribe\"|\"poll\"|\"unsubscribe\",…}".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let Ok(host) = crate::api::document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    if let Err(error) =
        crate::api::check_read_guard(&state, &name, terrabase::observability::Operation::Subscribe)
    {
        return error.into_response();
    }
    match request.get("op").and_then(serde_json::Value::as_str) {
        Some("subscribe") => {
            let spec = match spec_of(&request) {
                Ok(spec) => spec,
                Err(error) => return error.into_response(),
            };
            let scope = bound_scope(&state, &name);
            let resume = request.get("resume").and_then(|value| {
                let subscription = value.get("subscription").and_then(serde_json::Value::as_u64)?;
                let revision = value.get("revision").and_then(serde_json::Value::as_u64)?;
                Some((subscription, revision))
            });
            let (subscription, receiver, missed) = match host
                .reactive_subscribe(&scope, &spec, resume)
            {
                Ok(opened) => opened,
                Err(error) => {
                    return ApiError {
                        status: StatusCode::SERVICE_UNAVAILABLE,
                        code: "reactive_refused",
                        message: format!("the member refused the subscription: {error:?}"),
                        fix: "check the member's health and the collection name, then retry".into(),
                        placement: None,
                    }
                    .into_response();
                }
            };
            state.reactive.insert(subscription, name.clone(), receiver);
            // Every live poll subscription holds one distinct-view slot
            // (D-64): a database past its view budget refuses new
            // subscribers with the typed 429 instead of silently shedding.
            let view_key = format!("poll:{subscription}");
            if let Err(rejection) = state.guards.attach_view(&name, &view_key) {
                state.metrics.record_admission_rejection(
                    terrabase::observability::Operation::Subscribe,
                    terrabase::observability::AdmissionReason::Capacity,
                );
                host.reactive_unsubscribe(subscription);
                state.reactive.remove(subscription);
                return crate::api::ApiError::abuse_guard(&rejection).into_response();
            }
            // A resume replays exactly the missed revisions as its first
            // frames (no Seed — the client kept its state); everything
            // else seeds.
            let mut frames = Vec::new();
            for delta in missed.unwrap_or_default() {
                frames.push(terrabase_tbep::reactive::ReactiveFrame::Delta { subscription, delta });
            }
            frames.extend(drain(subscription, &state));
            respond_frames(subscription, &frames)
        }
        Some("poll") => {
            let Some(subscription) =
                request.get("subscription").and_then(serde_json::Value::as_u64)
            else {
                return ApiError {
                    status: StatusCode::BAD_REQUEST,
                    code: "missing_subscription",
                    message: "the poll names no subscription".into(),
                    fix: "add \"subscription\": <id> (the subscribe response's header names it)"
                        .into(),
                    placement: None,
                }
                .into_response();
            };
            if state.reactive.database_of(subscription).as_deref() != Some(name.as_str()) {
                return ApiError {
                    status: StatusCode::NOT_FOUND,
                    code: "unknown_subscription",
                    message: "no such subscription on this database".into(),
                    fix: "subscribe on this database first (the id names its database)".into(),
                    placement: None,
                }
                .into_response();
            }
            let scope = bound_scope(&state, &name);
            if let Err(error) = host.reactive_pump(&scope) {
                return ApiError {
                    status: StatusCode::SERVICE_UNAVAILABLE,
                    code: "reactive_pump_failed",
                    message: format!("the journal pump failed: {error:?}"),
                    fix: "retry the poll; the subscription survives one failed pump".into(),
                    placement: None,
                }
                .into_response();
            }
            let frames = drain(subscription, &state);
            respond_frames(subscription, &frames)
        }
        Some("unsubscribe") => {
            let Some(subscription) =
                request.get("subscription").and_then(serde_json::Value::as_u64)
            else {
                return ApiError {
                    status: StatusCode::BAD_REQUEST,
                    code: "missing_subscription",
                    message: "the unsubscribe names no subscription".into(),
                    fix: "add \"subscription\": <id>".into(),
                    placement: None,
                }
                .into_response();
            };
            if state.reactive.database_of(subscription).as_deref() != Some(name.as_str()) {
                return ApiError {
                    status: StatusCode::NOT_FOUND,
                    code: "unknown_subscription",
                    message: "no such subscription on this database".into(),
                    fix: "subscribe on this database first (the id names its database)".into(),
                    placement: None,
                }
                .into_response();
            }
            host.reactive_unsubscribe(subscription);
            state.reactive.remove(subscription);
            state.guards.detach_view(&name, &format!("poll:{subscription}"));
            axum::http::StatusCode::NO_CONTENT.into_response()
        }
        _ => ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "invalid_reactive_op",
            message: "the reactive request names no known op".into(),
            fix: "op is one of subscribe, poll, unsubscribe".into(),
            placement: None,
        }
        .into_response(),
    }
}

/// Drains one subscription's pending frames (nonblocking).
fn drain(subscription: u64, state: &ApiState) -> Vec<terrabase_tbep::reactive::ReactiveFrame> {
    let sessions = state.reactive.sessions.lock().expect("the session lock is not poisoned");
    let Some(session) = sessions.get(&subscription) else {
        return Vec::new();
    };
    let mut frames = Vec::new();
    while let Ok(frame) = session.receiver.try_recv() {
        frames.push(frame);
        if frames.len() >= 1024 {
            break;
        }
    }
    frames
}

/// The frames response: one `MessagePack` array, with the subscription id
/// on a header so the first (subscribe) call can name it.
fn respond_frames(
    subscription: u64,
    frames: &[terrabase_tbep::reactive::ReactiveFrame],
) -> Response {
    let mut bytes = Vec::with_capacity(256);
    let _ = rmp::encode::write_array_len(
        &mut bytes,
        u32::try_from(frames.len()).expect("the drained bound keeps this small"),
    );
    for frame in frames {
        match encode_frame(frame) {
            Ok(encoded) => bytes.extend_from_slice(&encoded),
            Err(error) => return error.into_response(),
        }
    }
    let mut response = axum::response::IntoResponse::into_response((StatusCode::OK, bytes));
    response
        .headers_mut()
        .insert(axum::http::header::CONTENT_TYPE, HeaderValue::from_static("application/msgpack"));
    if let Ok(value) = HeaderValue::from_str(&subscription.to_string()) {
        response.headers_mut().insert("x-terrascale-subscription", value);
    }
    response
}

/// The WebSocket transport (`GET /api/v1/databases/{name}/reactive`):
/// the client's first binary message is the `MessagePack` subscribe frame
/// (the same map the poll body carries); every server message after is
/// one `MessagePack` delivery frame.
#[utoipa::path(get, path = "/api/v1/databases/{name}/reactive", responses((status = 101, description = "Reactive WebSocket connected")))]
pub async fn websocket(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    upgrade: WebSocketUpgrade,
) -> Response {
    let state_for_task = Arc::clone(&state);
    upgrade.on_upgrade(move |socket| async move {
        serve_socket(socket, &state_for_task, &name).await.ok();
    })
}

/// One WebSocket session: subscribe on the first binary frame, then
/// forward delivery frames, pumping the journal on a short tick.
async fn serve_socket(
    mut socket: WebSocket,
    state: &Arc<ApiState>,
    name: &str,
) -> Result<(), axum::Error> {
    // The socket admits against the D-64 subscribe rate and connection
    // budgets before reading its first frame; the lease frees both slots
    // on every exit path below.
    if let Err(rejection) = state.guards.check_read_query(name) {
        state.metrics.record_admission_rejection(
            terrabase::observability::Operation::Subscribe,
            terrabase::observability::AdmissionReason::Capacity,
        );
        send_guard_refusal(&mut socket, &rejection).await;
        return Ok(());
    }
    if let Err(rejection) = state.guards.acquire_ws(name) {
        state.metrics.record_admission_rejection(
            terrabase::observability::Operation::Subscribe,
            terrabase::observability::AdmissionReason::Capacity,
        );
        send_guard_refusal(&mut socket, &rejection).await;
        return Ok(());
    }
    let _lease =
        WsLease { guards: Arc::clone(&state.guards), database: name.to_owned(), view: None };
    let Some(first) = socket.recv().await else {
        return Ok(());
    };
    let Ok(Message::Binary(request)) = first else {
        socket
            .send(Message::Text(
                "{\"error\":{\"code\":\"expected_subscribe\",\"fix\":\"send the subscribe frame first\"}}"
                    .into(),
            ))
            .await
            .ok();
        return Ok(());
    };
    // The first frame is one complete canonical sync-v2 Subscribe envelope.
    // The current member host still accepts only its internal
    // `SubscriptionSpec`, so SQL planning remains an explicit integration
    // seam. Maps and the retired JSON-text frame are rejected here.
    let subscribe = match decode_subscribe_frame(&request) {
        Ok(subscribe) => subscribe,
        Err(error) => {
            let body = format!(
                "{{\"error\":{{\"code\":\"{}\",\"fix\":\"send one canonical sync-v2 Subscribe envelope\"}}}}",
                error.code(),
            );
            socket.send(Message::Text(body.into())).await.ok();
            return Ok(());
        }
    };
    let error = ApiError::pending_surface(
        "reactive SQL view attach",
        "the member's shared SQL-view attach lane (E-5)",
    );
    let _ = subscribe;
    socket
        .send(Message::Text(
            format!("{{\"error\":{{\"code\":\"{}\",\"fix\":\"{}\"}}}}", error.code(), error.fix)
                .into(),
        ))
        .await
        .ok();
    Ok(())
}

/// Decodes one canonical sync-v2 Subscribe envelope. Only a client-to-server
/// Subscribe message is accepted, and the bounded wire decoder rejects maps,
/// extensions, unknown versions, and trailing bytes before allocation.
fn decode_subscribe_frame(bytes: &[u8]) -> Result<terrabase_sync_core::wire::Message, ApiError> {
    let invalid = |message: &str| ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_subscribe_frame",
        message: message.into(),
        fix: "send one canonical terrascale.sync.v2 Subscribe envelope".into(),
        placement: None,
    };
    let envelope = terrabase_sync_core::wire::decode_envelope(bytes)
        .map_err(|error| invalid(&format!("the subscribe envelope is invalid: {error}")))?;
    if !matches!(envelope.message, terrabase_sync_core::wire::Message::Subscribe { .. }) {
        return Err(invalid("the first frame must be a sync-v2 Subscribe message"));
    }
    Ok(envelope.message)
}

#[cfg(test)]
mod tests {
    use super::decode_subscribe_frame;
    use super::encode_frame;
    use terrabase_sync_core::server::Delta;
    use terrabase_sync_core::server::DeltaOp;
    use terrabase_sync_core::server::FieldValue;
    use terrabase_sync_core::wire::Envelope;
    use terrabase_sync_core::wire::Message;
    use terrabase_sync_core::wire::decode_envelope;
    use terrabase_sync_core::wire::encode_envelope;
    use terrabase_tbep::reactive::ReactiveFrame;

    /// A member frame is emitted as one complete canonical sync-v2 envelope,
    /// and projected values remain canonical JSON bytes inside that envelope.
    #[test]
    fn frames_encode_as_canonical_sync_v2() {
        let frame = ReactiveFrame::Delta {
            subscription: 3,
            delta: Delta {
                revision: 7,
                operation: DeltaOp::Insert {
                    document: "o-1".into(),
                    fields: vec![("total".into(), FieldValue::Number(4.0))],
                },
            },
        };
        let bytes = encode_frame(&frame).expect("the frame encodes");
        let envelope = decode_envelope(&bytes).expect("the canonical envelope decodes");
        let Message::Delta { subscription, operations, checkpoint, .. } = envelope.message else {
            panic!("the member delta is not a canonical Delta message");
        };
        assert_eq!(subscription, "3");
        assert_eq!(checkpoint.len(), 36);
        assert_eq!(operations.len(), 1);
        let terrabase_sync_core::wire::ResultOperation::Insert { fields, .. } = &operations[0]
        else {
            panic!("the member insert is not a canonical Insert operation");
        };
        assert_eq!(fields[0].path, "total");
        assert_eq!(fields[0].value, b"4.0");
    }

    #[test]
    fn canonical_snapshot_and_typed_reset_decode() {
        let seed = ReactiveFrame::Seed {
            subscription: 9,
            members: vec![("o-1".into(), vec![("status".into(), FieldValue::Text("paid".into()))])],
        };
        let snapshot =
            decode_envelope(&encode_frame(&seed).expect("seed encodes")).expect("seed decodes");
        assert!(matches!(snapshot.message, Message::Snapshot { .. }));

        let reset = ReactiveFrame::Reset { subscription: 9 };
        let reset =
            decode_envelope(&encode_frame(&reset).expect("reset encodes")).expect("reset decodes");
        assert!(matches!(
            reset.message,
            Message::Reset { reason: terrabase_sync_core::wire::ResetReason::Backpressure, .. }
        ));
    }

    #[test]
    fn websocket_subscribe_decoder_rejects_legacy_maps() {
        assert!(decode_subscribe_frame(&[0x81, 0xa1, b't', 0xa1, b'q']).is_err());
        let bytes = encode_envelope(&Envelope {
            features: 0,
            correlation: 1,
            causation: 0,
            message: Message::Subscribe {
                subscription: "view-capability".into(),
                query: "SELECT * FROM orders".into(),
                parameters: Vec::new(),
                checkpoint: None,
            },
        })
        .expect("subscribe encodes");
        assert!(matches!(decode_subscribe_frame(&bytes), Ok(Message::Subscribe { .. })));
    }
}
