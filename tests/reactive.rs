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
        authorization_generation: 1,
        revision: "r1".to_owned(),
        items: vec![],
        digest: "digest-r1".to_owned(),
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
            value: std::collections::BTreeMap::from([("name".to_owned(), br#""Ada""#.to_vec())]),
        }],
        digest: "digest-r2".to_owned(),
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

fn hex_bytes(value: &str) -> Vec<u8> {
    (0..value.len())
        .step_by(2)
        .map(|index| u8::from_str_radix(&value[index..index + 2], 16).expect("fixture hex"))
        .collect()
}

fn reactive_fixture(name: &str) -> Vec<u8> {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../contracts/v1/conformance/reactive.json"))
            .expect("reactive fixture");
    hex_bytes(
        fixture[name]
            .get("hex")
            .and_then(serde_json::Value::as_str)
            .expect("fixture frame"),
    )
}

fn message_fixture(name: &str) -> Vec<u8> {
    let fixture: serde_json::Value = serde_json::from_str(include_str!(
        "../contracts/v1/conformance/msgpack/message-vectors.json"
    ))
    .expect("message fixture");
    let vector = fixture["vectors"]
        .as_array()
        .expect("message vectors")
        .iter()
        .find(|vector| vector["name"] == name)
        .expect("named message vector");
    hex_bytes(vector["hex"].as_str().expect("message vector hex"))
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
async fn poll_rejects_non_https_or_userinfo_log_endpoints() {
    for log_url in [
        "http://sync.api.terrascale.tech/api/v1/reactive/views/view-1/4/log",
        "https://user:secret@sync.api.terrascale.tech/api/v1/reactive/views/view-1/4/log",
    ] {
        let mock = Mock::new(std::iter::empty::<Result<Response>>());
        let mut view = attached();
        view.log_url = log_url.to_owned();
        let error = client(Arc::clone(&mock))
            .reactive()
            .poll_from(&view, "r1")
            .await
            .expect_err("unsafe endpoint");
        assert_eq!(error.code, "invalid_reactive_endpoint");
        assert!(mock.requests.lock().expect("request mutex").is_empty());
    }
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
    assert!(body.get("transport").is_none());
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
    // [1,0,0,delta,0,0,[subscription,generation,from,to,[],digest,checkpoint]]
    let frame = vec![
        0x97, 1, 0, 0, 4, 0, 0, 0x97, 0xa1, b's', 1, 1, 2, 0x90, 0xa0, 0xc4, 1, b'x',
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
async fn poll_preserves_opaque_projected_bytes_and_digest() {
    // [1,0,0,delta,0,0,[subscription,generation,from,to,[update],digest,checkpoint]]
    // The projected value is deliberately not JSON; it must remain opaque.
    let frame = vec![
        0x97, 1, 0, 0, 4, 0, 0, 0x97, 0xa1, b's', 1, 1, 2, 0x91, 0x93, 2, 0xa1, b'd', 0x91, 0x92,
        0xa1, b'x', 0xc4, 1, 0xff, 0xa0, 0xc4, 1, b'k',
    ];
    let mock = Mock::new([Ok(msgpack_response(frame))]);
    let response = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect("opaque MessagePack delta");
    assert_eq!(response.deltas[0].digest, "");
    let ViewOperation::Update { value, .. } = &response.deltas[0].operations[0] else {
        panic!("expected update operation");
    };
    assert_eq!(value["x"], vec![0xff]);
}

#[tokio::test]
async fn producer_reactive_fixture_preserves_snapshot_log_and_reset_metadata() {
    let mock = Mock::new([
        Ok(msgpack_response(reactive_fixture("snapshot"))),
        Ok(msgpack_response(reactive_fixture("log"))),
        Ok(msgpack_response(reactive_fixture("reset"))),
    ]);
    let sdk = client(Arc::clone(&mock));
    let view = attached();

    let snapshot = sdk
        .reactive()
        .poll_from(&view, "0")
        .await
        .expect("snapshot fixture");
    let reset = snapshot.reset.expect("snapshot is a replacement frame");
    let snapshot = reset.snapshot.expect("snapshot payload");
    assert_eq!(snapshot.authorization_generation, 1);
    assert_eq!(
        snapshot.digest,
        "0000000000000000000000000000000000000000000000000000000000000000"
    );
    let delta = sdk
        .reactive()
        .poll_from(&view, "1")
        .await
        .expect("log fixture");
    assert_eq!(
        delta.deltas[0].digest,
        "d12faa372354de79608c8212f5fd2f3207bdf89a37b0242b1ec38d57848e19bc"
    );
    assert_eq!(delta.deltas[0].operations.len(), 1);
    let ViewOperation::Insert {
        identity,
        position,
        value,
    } = &delta.deltas[0].operations[0]
    else {
        panic!("expected insert operation");
    };
    assert_eq!(identity, "order-1");
    assert_eq!(*position, 0);
    assert_eq!(value["status"], br#""paid""#.to_vec());

    let reset = sdk
        .reactive()
        .poll_from(&view, "2")
        .await
        .expect("reset fixture");
    let reset = reset.reset.expect("replacement reset");
    assert_eq!(reset.reason, ResetReason::CheckpointExpired);
    assert_eq!(
        reset.snapshot.expect("replacement snapshot").digest,
        "0000000000000000000000000000000000000000000000000000000000000000"
    );
}

#[tokio::test]
async fn authorization_change_preserves_generation_and_removed_identities() {
    let mock = Mock::new([Ok(msgpack_response(message_fixture(
        "authorization_changed",
    )))]);
    let response = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "1")
        .await
        .expect("authorization change fixture");
    let reset = response.reset.expect("authorization replacement reset");
    assert_eq!(reset.reason, ResetReason::PermissionChanged);
    assert_eq!(reset.authorization_generation, Some(3));
    assert_eq!(reset.removed, vec!["d"]);
    assert_eq!(
        reset
            .snapshot
            .expect("replacement snapshot")
            .authorization_generation,
        3
    );
}

#[tokio::test]
async fn typed_message_errors_keep_the_published_error_code() {
    let mock = Mock::new([Ok(msgpack_response(message_fixture("error")))]);
    let error = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "1")
        .await
        .expect_err("typed server error");
    assert_eq!(error.kind, terrascale::ErrorKind::Unavailable);
    assert_eq!(error.code, "overloaded");
    assert!(error.message.contains("retry"));
}

#[tokio::test]
async fn poll_rejects_unsupported_versions_and_wrong_direction_tags() {
    let unsupported = vec![0x97, 1, 1, 0, 6, 0, 0, 0x90];
    let mock = Mock::new([Ok(msgpack_response(unsupported))]);
    let error = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect_err("unsupported version");
    assert_eq!(error.code, "unsupported_version");

    let wrong_direction = vec![
        0x97, 1, 0, 0, 1, 0, 0, 0x94, 0xa1, b's', 0xa0, 0xc4, 0, 0xc0,
    ];
    let mock = Mock::new([Ok(msgpack_response(wrong_direction))]);
    let error = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect_err("client-to-server tag");
    assert!(error.message.contains("client-to-server reactive message"));
}

#[tokio::test]
async fn poll_rejects_noncanonical_envelope_batches() {
    let envelope = vec![
        0x97, 1, 0, 0, 4, 0, 0, 0x97, 0xa1, b's', 1, 1, 2, 0x90, 0xa0, 0xc4, 1, b'x',
    ];
    let mut frames = vec![0x92];
    frames.extend_from_slice(&envelope);
    frames.extend_from_slice(&envelope);
    let mock = Mock::new([Ok(msgpack_response(frames))]);
    let error = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect_err("MessagePack envelope batch");
    assert_eq!(error.code, "malformed_frame");
}

#[tokio::test]
async fn poll_rejects_a_batch_of_seven_envelopes() {
    let envelope = vec![
        0x97, 1, 0, 0, 4, 0, 0, 0x97, 0xa1, b's', 1, 1, 2, 0x90, 0xa0, 0xc4, 1, b'x',
    ];
    let mut frames = vec![0x97];
    for _ in 0..7 {
        frames.extend_from_slice(&envelope);
    }
    let mock = Mock::new([Ok(msgpack_response(frames))]);
    let error = client(Arc::clone(&mock))
        .reactive()
        .poll_from(&attached(), "r1")
        .await
        .expect_err("seven-envelope MessagePack batch");
    assert_eq!(error.code, "malformed_frame");
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
    assert_eq!(error.code, "malformed_frame");
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
        authorization_generation: 1,
        revision: "1".to_owned(),
        items: vec![terrascale::ResultItem {
            identity: "known".to_owned(),
            value: std::collections::BTreeMap::from([("ok".to_owned(), b"true".to_vec())]),
        }],
        digest: "digest-1".to_owned(),
        checkpoint: terrascale::Checkpoint::new(vec![1]),
    });
    let outcome = subscription.apply_delta(ViewDelta {
        view_generation: 1,
        from_revision: "1".to_owned(),
        to_revision: "2".to_owned(),
        operations: vec![
            ViewOperation::Update {
                identity: "missing".to_owned(),
                value: std::collections::BTreeMap::new(),
            },
            ViewOperation::Remove {
                identity: "known".to_owned(),
            },
        ],
        digest: "digest-2".to_owned(),
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
                    authorization_generation: None,
                    removed: Vec::new(),
                    digest: String::new(),
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
                    authorization_generation: None,
                    removed: Vec::new(),
                    digest: String::new(),
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
