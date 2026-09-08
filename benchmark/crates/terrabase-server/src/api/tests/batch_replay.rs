use super::super::*;
use super::state_with_keys_and_control;
use axum::{
    body::Body,
    http::{HeaderMap, Request},
};
use http_body_util::BodyExt as _;
use hyper_util::{client::legacy::Client, rt::TokioExecutor};
use tower::ServiceExt as _;

const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";
const DOCUMENT_PATH: &str = "/api/v1/databases/replay/collections/items/documents/doc";

struct Server {
    address: std::net::SocketAddr,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for Server {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn setup() -> Arc<ApiState> {
    let state = state_with_keys_and_control();
    let response = router(state.clone())
        .oneshot(
            Request::post("/api/v1/databases")
                .header("authorization", KEY)
                .header("x-terrascale-tenant-id", "terrabase-local")
                .header("x-terrascale-project-id", "local")
                .body(Body::from(r#"{"name":"replay","placement":["GRU"],"residency":null}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    state
}

async fn serve(state: Arc<ApiState>) -> Server {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let task = tokio::spawn(async move {
        axum::serve(listener, router(state)).await.unwrap();
    });
    Server { address, task }
}

async fn request(
    server: &Server,
    method: &str,
    path: &str,
    key: &str,
    etag: Option<&str>,
    body: &str,
) -> (StatusCode, HeaderMap, bytes::Bytes) {
    let mut request = Request::builder()
        .method(method)
        .uri(format!("http://{}{path}", server.address))
        .header("authorization", KEY)
        .header("x-terrascale-timeout-ms", "10");
    if !key.is_empty() {
        request = request.header("idempotency-key", key);
    }
    if let Some(etag) = etag {
        request = request.header("if-match", etag);
    }
    let client = Client::builder(TokioExecutor::new()).build_http::<Body>();
    let response =
        client.request(request.body(Body::from(body.to_owned())).unwrap()).await.unwrap();
    let (parts, body) = response.into_parts();
    (parts.status, parts.headers, body.collect().await.unwrap().to_bytes())
}

const PATH: &str = "/api/v1/databases/replay/collections/items/documents/batch";

#[tokio::test]
async fn batch_http_replay_preserves_original_receipt_after_patch_and_delete() {
    let state = setup().await;
    let server = serve(state.clone()).await;
    assert_eq!(
        request(&server, "PUT", DOCUMENT_PATH, "seed", None, r#"{"a":1,"b":1}"#).await.0,
        StatusCode::OK
    );
    assert_eq!(
        request(
            &server,
            "PUT",
            "/api/v1/databases/replay/collections/items/documents/gone",
            "seed-gone",
            None,
            r#"{"x":1}"#
        )
        .await
        .0,
        StatusCode::OK
    );
    let batch =
        r#"{"operations":[{"op":"patch","id":"doc","value":{"a":2}},{"op":"delete","id":"gone"}]}"#;
    let first = request(&server, "POST", PATH, "batch", None, batch).await;
    assert_eq!(first.0, StatusCode::OK, "{first:?}");
    let original = first.2;
    assert_eq!(
        request(&server, "PATCH", DOCUMENT_PATH, "other", None, r#"{"b":3}"#).await.0,
        StatusCode::OK
    );
    let host = document_host(&state, "replay").unwrap();
    let sequence = host.engine().published_sequence().unwrap();
    let retry = request(&server, "POST", PATH, "batch", None, batch).await;
    assert_eq!(retry.0, StatusCode::OK, "{retry:?}");
    assert_eq!(retry.2, original);
    assert_eq!(host.engine().published_sequence().unwrap(), sequence);
    let current = request(&server, "GET", DOCUMENT_PATH, "", None, "").await;
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&current.2).unwrap(),
        serde_json::json!({"a":2,"b":3})
    );
    assert_eq!(
        request(&server, "DELETE", DOCUMENT_PATH, "later-delete", None, "").await.0,
        StatusCode::NO_CONTENT
    );
    let sequence = host.engine().published_sequence().unwrap();
    assert_eq!(request(&server, "POST", PATH, "batch", None, batch).await.2, original);
    assert_eq!(host.engine().published_sequence().unwrap(), sequence);
    for (path, body) in [
        (PATH, r#"{"operations":[{"op":"put","id":"doc","value":{"a":7}}]}"#),
        ("/api/v1/databases/replay/collections/other/documents/batch", batch),
    ] {
        assert_eq!(
            request(&server, "POST", path, "batch", None, body).await.0,
            StatusCode::CONFLICT
        );
    }
    assert_eq!(host.engine().published_sequence().unwrap(), sequence);
}

#[tokio::test]
async fn batch_http_postcommit_rf2_timeout_replays_without_repeating_effects() {
    let mut state = setup().await;
    let seed = router(state.clone())
        .oneshot(
            Request::put(DOCUMENT_PATH)
                .header("authorization", KEY)
                .body(Body::from(r#"{"a":1}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(seed.status(), StatusCode::OK);
    let host = document_host(&state, "replay").unwrap();
    let owner = host.engine().database_owner();
    let route = terrabase_cluster::production::DatabaseRouteScope::new(
        owner.tenant(),
        owner.project(),
        owner.database(),
    );
    let progress = Arc::new(crate::replication_runtime::ReplicationProgress::default());
    progress
        .admit(route, std::collections::BTreeSet::from([owner.region().as_str().into()]))
        .unwrap();
    Arc::get_mut(&mut state).unwrap().replication_progress = Some(progress.clone());
    let server = serve(state.clone()).await;
    let before = host.engine().published_sequence().unwrap();
    let batch =
        r#"{"operations":[{"op":"delete","id":"doc"},{"op":"put","id":"new","value":{"a":2}}]}"#;
    let first = request(&server, "POST", PATH, "timeout", None, batch).await;
    assert_eq!(first.0, StatusCode::SERVICE_UNAVAILABLE, "{first:?}");
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&first.2).unwrap()["error"]["code"],
        "commit_unknown"
    );
    let committed = host.engine().published_sequence().unwrap();
    assert!(committed > before);
    assert_eq!(
        request(&server, "POST", PATH, "timeout", None, batch).await.0,
        StatusCode::SERVICE_UNAVAILABLE
    );
    progress.advance_regional(route, committed.get());
    let retry = request(&server, "POST", PATH, "timeout", None, batch).await;
    assert_eq!(retry.0, StatusCode::OK, "{retry:?}");
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&retry.2).unwrap(),
        serde_json::json!({"committed":committed.get(),"count":2})
    );
    assert_eq!(host.engine().published_sequence().unwrap(), committed);
    progress.fence_database(route);
    assert_eq!(
        request(&server, "POST", PATH, "timeout", None, batch).await.0,
        StatusCode::SERVICE_UNAVAILABLE,
        "historical replay still enforces current serving authority"
    );
    let client = Client::builder(TokioExecutor::new()).build_http::<Body>();
    let response = client
        .request(
            Request::post(format!("http://{}{PATH}", server.address))
                .header("idempotency-key", "timeout")
                .body(Body::from(batch))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(
        response.status(),
        StatusCode::UNAUTHORIZED,
        "historical replay still requires current authorization"
    );
}
