use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::reactive::{
    ApplyOutcome, CancellationToken, PollOptions, PollOutcome, PollResponse, ResetFrame,
    ResetReason, ViewDelta, ViewOperation, ViewSnapshot, VIEW_CAPABILITY_HEADER,
};
use terrascale::{
    AttachedView, ClientConfig, DatabaseScope, Error, HttpTransport, Query, RegionCode, Request,
    Response, Result, SubscribeOptions, TerraScaleClient, Transport,
};

#[derive(Debug)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Result<Response>>>,
}

impl Mock {
    fn new(responses: impl IntoIterator<Item = Result<Response>>) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(responses.into_iter().collect()),
        })
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().expect("request mutex").push(request);
        self.responses
            .lock()
            .expect("response mutex")
            .pop_front()
            .unwrap_or_else(|| Err(Error::transport("mock ran out of responses")))
    }
}

fn client(mock: Arc<Mock>) -> TerraScaleClient {
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("test-key")
            .project_id("project")
            .build()
            .expect("config"),
        mock,
    )
}

fn snapshot() -> ViewSnapshot {
    ViewSnapshot {
        view_generation: 4,
        revision: "r1".to_owned(),
        items: vec![],
        checkpoint: terrascale::Checkpoint::new(b"cp1".to_vec()),
    }
}

fn attached() -> AttachedView {
    AttachedView {
        view_id: "view-1".to_owned(),
        generation: 4,
        snapshot: snapshot(),
        checkpoint: terrascale::Checkpoint::new(b"cp1".to_vec()),
        snapshot_urls: vec!["https://sync.api.terrascale.tech/snapshot/1".to_owned()],
        log_url: "https://sync.api.terrascale.tech/api/v1/reactive/views/view-1/4/log?cut=1"
            .to_owned(),
        capability: Some("capability".to_owned()),
    }
}

fn delta() -> ViewDelta {
    ViewDelta {
        view_generation: 4,
        from_revision: "r1".to_owned(),
        to_revision: "r2".to_owned(),
        operations: vec![ViewOperation::Insert {
            identity: "doc-1".to_owned(),
            position: 0,
            value: json!({"name": "Ada"}),
        }],
        checkpoint: terrascale::Checkpoint::new(b"cp2".to_vec()),
    }
}

fn msgpack_response(bytes: Vec<u8>) -> Response {
    Response {
        status: 200,
        headers: vec![("content-type".to_owned(), "application/msgpack".to_owned())],
        body: bytes,
    }
}

#[tokio::test]
async fn poll_sends_capability_header_and_keeps_canonical_log_url_immutable() {
    let mock = Mock::new([Ok(Response::json(
        200,
        &PollResponse {
            deltas: vec![],
            checkpoint: Some(terrascale::Checkpoint::new(b"cp1".to_vec())),
            reset: None,
        },
    ))]);
    let sdk = client(Arc::clone(&mock));
    let view = attached();

    sdk.reactive().poll_from(&view, "r 2").await.expect("poll");

    let requests = mock.requests.lock().expect("request mutex");
    assert_eq!(requests.len(), 1);
    assert_eq!(requests[0].method, terrascale::Method::Get);
    assert_eq!(requests[0].route_name, "reactive.poll");
    assert_eq!(
        requests[0].url,
        "https://sync.api.terrascale.tech/api/v1/reactive/views/view-1/4/log?cut=1&offset=r%202"
    );
    assert_eq!(
        requests[0]
            .headers
            .iter()
            .find(|(name, _)| name == VIEW_CAPABILITY_HEADER)
            .map(|(_, value)| value.as_str()),
        Some("capability")
    );
    assert_eq!(
        view.log_url,
        "https://sync.api.terrascale.tech/api/v1/reactive/views/view-1/4/log?cut=1"
    );
}

#[tokio::test]
async fn attach_uses_canonical_route_and_base64_wire_fields() {
    let mock = Mock::new([Ok(Response::json(
        200,
        &json!({
            "view": "view-unsafe",
            "generation": 8,
            "checkpoint": "Y3Ax",
            "snapshot_chunks": ["https://sync.api.terrascale.tech/api/v1/reactive/views/view-unsafe/8/snapshot/0?cut=1"],
            "log": "/api/v1/reactive/views/view-unsafe/8/log?cut=1",
            "capability": "cap",
            "offset": 1
        }),
    ))]);
    client(Arc::clone(&mock))
        .reactive()
        .subscribe(
            &DatabaseScope::new("database"),
            &Query::raw(
                "SELECT * FROM users WHERE id = ?".to_owned(),
                vec![json!(7)],
            ),
            SubscribeOptions::new()
                .with_subscription("sub-1")
                .with_region_hint(RegionCode::Fra),
        )
        .await
        .expect("attach");
    let request = &mock.requests.lock().expect("request mutex")[0];
    assert_eq!(
        request.url,
        "https://fra.api.terrascale.tech/api/v1/reactive/views"
    );
    let body: serde_json::Value =
        serde_json::from_slice(request.body.as_ref().expect("body")).expect("JSON attach body");
    assert_eq!(body["databaseId"], "database");
    assert_eq!(body["subscription"], "sub-1");
    assert_eq!(body["parameters"], "eyIxIjo3fQ==");
    assert_eq!(body["checkpoint"], serde_json::Value::Null);
}

#[tokio::test]
async fn poll_retries_only_within_the_explicit_bounded_budget() {
    let mock = Mock::new([
        Ok(Response::json(503, &json!({"code": "busy"}))),
        Ok(Response::json(503, &json!({"code": "busy"}))),
        Ok(Response::json(200, &PollResponse::default())),
    ]);
    let sdk = client(Arc::clone(&mock));
    sdk.reactive()
        .poll_with_options(&attached(), "r1", PollOptions::new(3))
        .await
        .expect("third attempt succeeds");
    assert_eq!(mock.requests.lock().expect("request mutex").len(), 3);

    let mock = Mock::new([
        Ok(Response::json(503, &json!({"code": "busy"}))),
        Ok(Response::json(503, &json!({"code": "busy"}))),
    ]);
    let error = client(Arc::clone(&mock))
        .reactive()
        .poll_with_options(&attached(), "r1", PollOptions::new(1))
        .await
        .expect_err("one attempt must fail closed");
    assert_eq!(error.status, Some(503));
    assert_eq!(mock.requests.lock().expect("request mutex").len(), 1);
}

#[tokio::test]
async fn poll_decodes_strict_indexed_msgpack_and_empty_statuses() {
    // [2,1,0,delta,0,0,[subscription,generation,from,to,[],digest,checkpoint]]
    let frame = vec![
        0x97, 2, 1, 0, 4, 0, 0, 0x97, 0xa1, b's', 1, 1, 2, 0x90, 0xa0, 0xc4, 1, b'x',
    ];
    let mock = Mock::new([Ok(msgpack_response(frame))]);
    let response = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect("MessagePack delta");
    assert_eq!(response.deltas.len(), 1);
    assert_eq!(response.deltas[0].to_revision, "2");
    assert_eq!(response.deltas[0].checkpoint.to_bytes(), b"x");

    for status in [204, 304] {
        let mock = Mock::new([Ok(Response {
            status,
            headers: vec![("cache-control".to_owned(), "no-store".to_owned())],
            body: Vec::new(),
        })]);
        let empty = client(Arc::clone(&mock))
            .reactive()
            .poll_from(&attached(), "r1")
            .await
            .expect("empty timeout");
        assert!(empty.deltas.is_empty());
    }
}

#[tokio::test]
async fn poll_decodes_a_deterministic_array_of_envelopes() {
    let envelope = vec![
        0x97, 2, 1, 0, 4, 0, 0, 0x97, 0xa1, b's', 1, 1, 2, 0x90, 0xa0, 0xc4, 1, b'x',
    ];
    let mut frames = vec![0x92];
    frames.extend_from_slice(&envelope);
    frames.extend_from_slice(&envelope);
    let mock = Mock::new([Ok(msgpack_response(frames))]);
    let response = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect("MessagePack envelope array");
    assert_eq!(response.deltas.len(), 2);
    assert_eq!(response.deltas[0].checkpoint.to_bytes(), b"x");
    assert_eq!(response.deltas[1].to_revision, "2");
}

#[tokio::test]
async fn poll_disambiguates_a_batch_of_seven_envelopes_from_one_envelope() {
    let envelope = vec![
        0x97, 2, 1, 0, 4, 0, 0, 0x97, 0xa1, b's', 1, 1, 2, 0x90, 0xa0, 0xc4, 1, b'x',
    ];
    let mut frames = vec![0x97];
    for _ in 0..7 {
        frames.extend_from_slice(&envelope);
    }
    let mock = Mock::new([Ok(msgpack_response(frames))]);
    let response = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect("seven-envelope MessagePack batch");
    assert_eq!(response.deltas.len(), 7);
}

#[tokio::test]
async fn expired_http_log_is_a_typed_reset() {
    let mock = Mock::new([
        Ok(Response::json(
            200,
            &json!({
                "view": "view-1",
                "generation": 4,
                "snapshot_chunks": [],
                "log": "/api/v1/reactive/views/view-1/4/log?cut=1",
                "checkpoint": "Y3Ax",
                "capability": "capability",
                "offset": 1
            }),
        )),
        Ok(Response::json(
            410,
            &json!({"error": {"code": "retention_advanced", "message": "expired"}}),
        )),
    ]);
    let mut live = client(Arc::clone(&mock))
        .reactive()
        .subscribe_live(
            &DatabaseScope::new("db"),
            &Query::raw("SELECT * FROM users".to_owned(), vec![]),
            SubscribeOptions::new(),
        )
        .await
        .expect("attach");
    assert!(matches!(
        live.next().await.expect("typed reset"),
        PollOutcome::Reset(ResetFrame {
            reason: ResetReason::HistoryExpired,
            ..
        })
    ));
}

#[tokio::test]
async fn malformed_msgpack_fails_closed_before_json_fallback() {
    let mock = Mock::new([Ok(msgpack_response(vec![0x81, 0xa1, b'x', 1]))]);
    let error = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect_err("maps are not indexed frames");
    assert_eq!(error.kind, terrascale::ErrorKind::InvalidResponse);
}

#[test]
fn checkpoint_bound_is_one_to_4096_bytes() {
    assert!(terrascale::Checkpoint::try_new(Vec::new()).is_err());
    assert!(terrascale::Checkpoint::try_new(vec![1; 4096]).is_ok());
    assert!(terrascale::Checkpoint::try_new(vec![1; 4097]).is_err());
}

#[test]
fn invalid_operations_reset_atomically() {
    let mut subscription = terrascale::Subscription::new("sub");
    subscription.apply_snapshot(ViewSnapshot {
        view_generation: 1,
        revision: "1".to_owned(),
        items: vec![terrascale::ResultItem {
            identity: "known".to_owned(),
            value: json!({"ok": true}),
        }],
        checkpoint: terrascale::Checkpoint::new(vec![1]),
    });
    let outcome = subscription.apply_delta(ViewDelta {
        view_generation: 1,
        from_revision: "1".to_owned(),
        to_revision: "2".to_owned(),
        operations: vec![
            ViewOperation::Update {
                identity: "missing".to_owned(),
                value: json!({}),
            },
            ViewOperation::Remove {
                identity: "known".to_owned(),
            },
        ],
        checkpoint: terrascale::Checkpoint::new(vec![2]),
    });
    assert_eq!(
        outcome,
        ApplyOutcome::ResetRequired(ResetReason::InvalidOperations)
    );
    assert_eq!(subscription.revision(), Some("1"));
    assert_eq!(subscription.items().len(), 1);
}

#[tokio::test]
async fn live_subscription_applies_deltas_and_surfaces_region_resets() {
    let mock = Mock::new([
        Ok(Response::json(
            200,
            &json!({
                "view": "view-1",
                "generation": 4,
                "snapshot": snapshot(),
                "checkpoint": [99, 112, 49],
                "snapshot_chunks": ["/api/v1/reactive/views/view-1/4/snapshot/0?cut=1"],
                "log": "/api/v1/reactive/views/view-1/4/log",
                "capability": "capability"
            }),
        )),
        Ok(Response::json(
            200,
            &PollResponse {
                deltas: vec![],
                checkpoint: None,
                reset: Some(ResetFrame {
                    reason: ResetReason::Reconnect,
                    snapshot: Some(snapshot()),
                    checkpoint: None,
                    region: None,
                    detail: None,
                }),
            },
        )),
        Ok(Response::json(
            200,
            &PollResponse {
                deltas: vec![delta()],
                checkpoint: None,
                reset: None,
            },
        )),
        Ok(Response::json(
            200,
            &PollResponse {
                deltas: vec![],
                checkpoint: None,
                reset: Some(ResetFrame {
                    reason: ResetReason::RegionMoved,
                    snapshot: None,
                    checkpoint: None,
                    region: Some(RegionCode::Fra),
                    detail: None,
                }),
            },
        )),
    ]);
    let sdk = client(Arc::clone(&mock));
    let mut live = sdk
        .reactive()
        .subscribe_live(
            &DatabaseScope::new("db"),
            &Query::raw("SELECT * FROM users".to_owned(), vec![]),
            SubscribeOptions::new(),
        )
        .await
        .expect("attach");

    assert!(matches!(
        live.next().await.expect("delta"),
        PollOutcome::Data { .. }
    ));
    assert_eq!(live.subscription().items().len(), 1);
    assert!(matches!(
        live.next().await.expect("reset"),
        PollOutcome::Reset(ResetFrame {
            reason: ResetReason::RegionMoved,
            region: Some(RegionCode::Fra),
            ..
        })
    ));
}

#[tokio::test]
async fn execute_uses_canonical_sql_parameter_object() {
    let mock = Mock::new([Ok(Response::json(200, &json!({"matched": 0, "rows": []})))]);
    client(Arc::clone(&mock))
        .reactive()
        .execute(
            &DatabaseScope::new("db"),
            &Query::raw(
                "SELECT * FROM users WHERE id = ? AND state = ?".to_owned(),
                vec![json!(7), json!("open")],
            ),
        )
        .await
        .expect("execute");
    let request = &mock.requests.lock().expect("request mutex")[0];
    let body: serde_json::Value =
        serde_json::from_slice(request.body.as_ref().expect("body")).expect("JSON SQL body");
    assert_eq!(
        body["sql"],
        "SELECT * FROM users WHERE id = ? AND state = ?"
    );
    assert_eq!(body["parameters"]["0"], 7);
    assert_eq!(body["parameters"]["1"], "open");
    assert!(body.get("statement").is_none());
}

#[tokio::test]
async fn cancellation_prevents_a_poll_request() {
    let mock = Mock::new(std::iter::empty::<Result<Response>>());
    let sdk = client(Arc::clone(&mock));
    let token = CancellationToken::new();
    token.cancel();
    let error = sdk
        .reactive()
        .poll_with_cancellation(&attached(), "r1", PollOptions::new(3), &token)
        .await
        .expect_err("cancelled");
    assert_eq!(error.code, "cancelled");
    assert!(mock.requests.lock().expect("request mutex").is_empty());
}

#[tokio::test]
async fn websocket_is_an_explicit_unsupported_boundary() {
    let mock = Mock::new(std::iter::empty::<Result<Response>>());
    let error = client(Arc::clone(&mock))
        .reactive()
        .subscribe(
            &DatabaseScope::new("db"),
            &Query::raw("SELECT * FROM users".to_owned(), vec![]),
            SubscribeOptions::new().with_transport(Transport::WebSocket),
        )
        .await
        .expect_err("HTTP transport cannot open websocket");
    assert_eq!(error.code, "websocket_not_supported");
    assert!(mock.requests.lock().expect("request mutex").is_empty());
}
