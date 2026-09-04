use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::reactive::{
    CancellationToken, PollOptions, PollOutcome, PollResponse, ResetFrame, ResetReason, ViewDelta,
    ViewOperation, ViewSnapshot, VIEW_CAPABILITY_HEADER,
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
        log_url: "https://sync.api.terrascale.tech/log?view=view-1".to_owned(),
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
        "https://sync.api.terrascale.tech/log?view=view-1&offset=r%202"
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
        "https://sync.api.terrascale.tech/log?view=view-1"
    );
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
async fn live_subscription_applies_deltas_and_surfaces_region_resets() {
    let mock = Mock::new([
        Ok(Response::json(
            200,
            &json!({
                "view_id": "view-1",
                "generation": 4,
                "snapshot": snapshot(),
                "checkpoint": [99],
                "snapshot_urls": ["https://sync.api.terrascale.tech/snapshot/1"],
                "log_url": "https://sync.api.terrascale.tech/log",
                "capability": "capability"
            }),
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
