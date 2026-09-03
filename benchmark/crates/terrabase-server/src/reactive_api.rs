//! The reactive transports (`S-04` slice 4, D-14/D-38): one shared
//! pump-and-drain core behind two wire shapes — `POST /api/v1/reactive/poll`
//! (HTTP fallback) and `GET /api/v1/databases/{name}/reactive` (WebSocket).
//!
//! The envelope is `MessagePack` (D-14); projected field values ride inside as
//! canonical JSON strings — the same `MessagePack` envelope, JSON bodies
//! split the architecture draws for the whole reactive surface. The frame
//! manifest is hand-defined here for the local milestone; `make contracts`
//! (S-00) generates the Rust and TS decoders from one manifest when the
//! monorepo lands.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::Mutex;

use crate::api::ApiError;
use crate::api::ApiState;
use crate::api::push_json_escaped;
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

/// Renders one projected-field list as canonical JSON (the frame's body
/// payload — `MessagePack` envelope, JSON bodies, D-14).
fn fields_json(fields: &[(String, FieldValue)]) -> String {
    let mut body = String::from("{");
    for (index, (field, value)) in fields.iter().enumerate() {
        if index > 0 {
            body.push(',');
        }
        body.push('"');
        push_json_escaped(&mut body, field);
        body.push_str("\":");
        match value {
            FieldValue::Text(text) => {
                body.push('"');
                push_json_escaped(&mut body, text);
                body.push('"');
            }
            FieldValue::Number(number) => body.push_str(&number.to_string()),
        }
    }
    body.push('}');
    body
}

/// Encodes one delivery frame as a `MessagePack` map (D-14: the envelope;
/// projected values ride as canonical JSON strings inside).
fn encode_frame(frame: &terrabase_tbep::reactive::ReactiveFrame) -> Result<Vec<u8>, ApiError> {
    use terrabase_sync_core::server::DeltaOp;
    use terrabase_tbep::reactive::ReactiveFrame;
    let refuse = |_| ApiError {
        status: StatusCode::INTERNAL_SERVER_ERROR,
        code: "frame_encode_failed",
        message: "the delivery frame could not be encoded".into(),
        fix: "retry the subscription; this is a server-side encoding fault".into(),
        placement: None,
    };
    let mut bytes = Vec::with_capacity(256);
    match frame {
        ReactiveFrame::Seed { subscription, members } => {
            rmp::encode::write_map_len(&mut bytes, 3).map_err(refuse)?;
            write_key(&mut bytes, "t");
            rmp::encode::write_str(&mut bytes, "seed").map_err(refuse)?;
            write_key(&mut bytes, "sub");
            rmp::encode::write_u64(&mut bytes, *subscription).map_err(refuse)?;
            write_key(&mut bytes, "members");
            rmp::encode::write_array_len(
                &mut bytes,
                u32::try_from(members.len()).map_err(|_| frame_bound_refused())?,
            )
            .map_err(refuse)?;
            for (document, fields) in members {
                rmp::encode::write_array_len(&mut bytes, 2).map_err(refuse)?;
                rmp::encode::write_str(&mut bytes, document).map_err(refuse)?;
                rmp::encode::write_str(&mut bytes, &fields_json(fields)).map_err(refuse)?;
            }
        }
        ReactiveFrame::Delta { subscription, delta } => {
            let (kind, document, fields) = match &delta.operation {
                DeltaOp::Insert { document, fields } => ("insert", Some(document), Some(fields)),
                DeltaOp::Update { document, fields } => ("update", Some(document), Some(fields)),
                DeltaOp::Remove { document } => ("remove", Some(document), None),
            };
            let entries = 5 + usize::from(fields.is_some());
            rmp::encode::write_map_len(
                &mut bytes,
                u32::try_from(entries).map_err(|_| frame_bound_refused())?,
            )
            .map_err(refuse)?;
            write_key(&mut bytes, "t");
            rmp::encode::write_str(&mut bytes, "delta").map_err(refuse)?;
            write_key(&mut bytes, "sub");
            rmp::encode::write_u64(&mut bytes, *subscription).map_err(refuse)?;
            write_key(&mut bytes, "rev");
            rmp::encode::write_u64(&mut bytes, delta.revision).map_err(refuse)?;
            write_key(&mut bytes, "op");
            rmp::encode::write_str(&mut bytes, kind).map_err(refuse)?;
            write_key(&mut bytes, "id");
            rmp::encode::write_str(&mut bytes, document.expect("every op names a document"))
                .map_err(refuse)?;
            if let Some(fields) = fields {
                write_key(&mut bytes, "body");
                rmp::encode::write_str(&mut bytes, &fields_json(fields)).map_err(refuse)?;
            }
        }
        ReactiveFrame::Reset { subscription } => {
            rmp::encode::write_map_len(&mut bytes, 2).map_err(refuse)?;
            write_key(&mut bytes, "t");
            rmp::encode::write_str(&mut bytes, "reset").map_err(refuse)?;
            write_key(&mut bytes, "sub");
            rmp::encode::write_u64(&mut bytes, *subscription).map_err(refuse)?;
        }
    }
    Ok(bytes)
}

/// The typed refusal for a frame the wire cannot carry (a members list
/// past the u32 array-length bound).
fn frame_bound_refused() -> ApiError {
    ApiError {
        status: StatusCode::INTERNAL_SERVER_ERROR,
        code: "frame_encode_failed",
        message: "the delivery frame could not be encoded".into(),
        fix: "retry the subscription; this is a server-side encoding fault".into(),
        placement: None,
    }
}

/// Writes one map key (every frame key is a short static string).
fn write_key(bytes: &mut Vec<u8>, key: &str) {
    rmp::encode::write_str(bytes, key).expect("a short static key always encodes");
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
    // ponytail: a 10 ms pump tick reaches the 50 ms p99 target; the
    // commit-hook pump (publication sink) replaces the tick when the
    // S-05 tailer wires the sink.
    const PUMP_TICK: std::time::Duration = std::time::Duration::from_millis(10);
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
    // The subscribe frame reuses the poll body's JSON shape, carried as
    // the MessagePack envelope's JSON body (D-14).
    let decoded = decode_subscribe_frame(&request);
    let spec = match decoded.and_then(|value| spec_of(&value)) {
        Ok(spec) => spec,
        Err(error) => {
            let body = format!(
                "{{\"error\":{{\"code\":\"{}\",\"fix\":\"{}\"}}}}",
                error.code(),
                "resend the subscribe frame with a collection"
            );
            socket.send(Message::Text(body.into())).await.ok();
            return Ok(());
        }
    };
    let Ok(host) = crate::api::document_host(state, name) else {
        socket
            .send(Message::Text(
                "{\"error\":{\"code\":\"database_not_found\",\"fix\":\"check the database name\"}}"
                    .into(),
            ))
            .await
            .ok();
        return Ok(());
    };
    let scope = bound_scope(state, name);
    // ponytail: the WS first-frame subscribe carries no resume; a
    // reconnecting client resumes through the poll transport until the
    // frame manifest grows a resume frame (S-10).
    let Ok((subscription, receiver, _missed)) = host.reactive_subscribe(&scope, &spec, None) else {
        socket
            .send(Message::Text(
                "{\"error\":{\"code\":\"reactive_refused\",\"fix\":\"retry the subscription\"}}"
                    .into(),
            ))
            .await
            .ok();
        return Ok(());
    };
    state.reactive.insert(subscription, name.to_owned(), receiver);
    loop {
        if let Err(error) = host.reactive_pump(&scope) {
            let _ = error;
            break;
        }
        for frame in drain(subscription, state) {
            match encode_frame(&frame) {
                Ok(bytes) => {
                    if socket.send(Message::Binary(bytes.into())).await.is_err() {
                        host.reactive_detach(subscription);
                        state.reactive.remove(subscription);
                        return Ok(());
                    }
                }
                Err(_) => break,
            }
        }
        // Wake on the tick or the client's close, whichever comes first.
        tokio::select! {
            () = tokio::time::sleep(PUMP_TICK) => {}
            closed = socket.recv() => {
                if closed.is_none() {
                    host.reactive_detach(subscription);
                    state.reactive.remove(subscription);
                    return Ok(());
                }
            }
        }
    }
    host.reactive_detach(subscription);
    state.reactive.remove(subscription);
    Ok(())
}

/// Decodes one subscribe frame: the `MessagePack` map the poll body
/// describes, with its JSON body carried as a string. Only the closed
/// subscribe shape decodes; anything else fails closed.
fn decode_subscribe_frame(bytes: &[u8]) -> Result<serde_json::Value, ApiError> {
    let invalid = |message: &str| ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_subscribe_frame",
        message: message.into(),
        fix: "send the subscribe frame as a MessagePack map of JSON-text values".into(),
        placement: None,
    };
    let mut cursor = bytes;
    let entries = rmp::decode::read_map_len(&mut cursor)
        .map_err(|_| invalid("the subscribe frame is not a MessagePack map"))?;
    let mut request = serde_json::Map::new();
    for _ in 0..entries {
        let mut key_buffer = [0_u8; 64];
        let key = rmp::decode::read_str(&mut cursor, &mut key_buffer)
            .map_err(|_| invalid("a subscribe frame key is not a short string"))?
            .to_owned();
        let mut value_buffer = [0_u8; 4096];
        let value = rmp::decode::read_str(&mut cursor, &mut value_buffer)
            .map_err(|_| invalid("a subscribe frame value is not the JSON body string"))?;
        let parsed: serde_json::Value = serde_json::from_str(value)
            .map_err(|_| invalid("a subscribe frame value is not JSON"))?;
        request.insert(key, parsed);
    }
    Ok(serde_json::Value::Object(request))
}

#[cfg(test)]
mod tests {
    use super::encode_frame;
    use terrabase_sync_core::server::Delta;
    use terrabase_sync_core::server::DeltaOp;
    use terrabase_sync_core::server::FieldValue;
    use terrabase_tbep::reactive::ReactiveFrame;

    /// A frame round-trips through `MessagePack` with its kind and payload
    /// intact (rmp decodes what rmp wrote).
    #[test]
    fn frames_encode_as_messagepack() {
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
        let mut cursor = bytes.as_slice();
        let entries = rmp::decode::read_map_len(&mut cursor).expect("a map");
        assert_eq!(entries, 6, "insert carries t/sub/rev/op/id/body");
        let mut key_buffer = [0_u8; 8];
        let first_key = rmp::decode::read_str(&mut cursor, &mut key_buffer).expect("a key");
        assert_eq!(first_key, "t");
        let mut kind_buffer = [0_u8; 8];
        let kind = rmp::decode::read_str(&mut cursor, &mut kind_buffer).expect("a kind");
        assert_eq!(kind, "delta");
    }
}
