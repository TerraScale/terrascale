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
const PATH: &str = "/api/v1/databases/replay/collections/items/documents/doc";

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

#[tokio::test]
async fn conditional_http_replay_preserves_original_result_after_intervening_changes() {
    let state = setup().await;
    let server = serve(state.clone()).await;
    let seed = request(&server, "PUT", PATH, "seed", None, r#"{"a":1,"b":1}"#).await;
    assert_eq!(seed.0, StatusCode::OK);
    let original_etag = seed.1["etag"].to_str().unwrap();
    let first = request(&server, "PATCH", PATH, "patch", Some(original_etag), r#"{"a":2}"#).await;
    assert_eq!(first.0, StatusCode::OK);
    // Discard the successful response as a client with a lost response would.
    let original_revision = first.1["etag"].clone();
    drop(first);
    let changed = request(&server, "PATCH", PATH, "other", None, r#"{"b":3}"#).await;
    assert_eq!(changed.0, StatusCode::OK);
    let host = document_host(&state, "replay").unwrap();
    let sequence = host.engine().published_sequence().unwrap();
    let retry = request(&server, "PATCH", PATH, "patch", Some(original_etag), r#"{"a":2}"#).await;
    assert_eq!(retry.0, StatusCode::OK);
    assert_eq!(retry.1["etag"], original_revision);
    assert_eq!(host.engine().published_sequence().unwrap(), sequence);
    let current = request(&server, "GET", PATH, "", None, "").await;
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&current.2).unwrap(),
        serde_json::json!({"a":2,"b":3})
    );
    for (method, path, etag, body) in [
        ("PATCH", PATH, Some(original_etag), r#"{"a":7}"#),
        ("PATCH", PATH, None, r#"{"a":2}"#),
        (
            "PATCH",
            "/api/v1/databases/replay/collections/other/documents/doc",
            Some(original_etag),
            r#"{"a":2}"#,
        ),
        ("PUT", PATH, Some(original_etag), r#"{"a":2}"#),
    ] {
        assert_eq!(
            request(&server, method, path, "patch", etag, body).await.0,
            StatusCode::CONFLICT
        );
    }
    let deleted =
        request(&server, "DELETE", PATH, "delete", Some(changed.1["etag"].to_str().unwrap()), "")
            .await;
    assert_eq!(deleted.0, StatusCode::NO_CONTENT);
    assert_eq!(
        request(&server, "DELETE", PATH, "delete", Some(changed.1["etag"].to_str().unwrap()), "")
            .await
            .0,
        StatusCode::NO_CONTENT
    );
    assert_eq!(
        request(&server, "PATCH", PATH, "patch", Some(original_etag), r#"{"a":2}"#).await.1["etag"],
        original_revision
    );
    assert_eq!(
        request(&server, "PUT", PATH, "seed", None, r#"{"a":1,"b":1}"#).await.1["etag"],
        seed.1["etag"]
    );
    assert_eq!(request(&server, "GET", PATH, "", None, "").await.0, StatusCode::NOT_FOUND);
}

#[tokio::test]
async fn conditional_http_postcommit_rf2_timeout_replays_only_after_durable_confirmation() {
    let mut state = setup().await;
    let seed = router(state.clone())
        .oneshot(
            Request::put(PATH).header("authorization", KEY).body(Body::from(r#"{"a":1}"#)).unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(seed.status(), StatusCode::OK);
    let etag = seed.headers()["etag"].to_str().unwrap().to_owned();
    let host = document_host(&state, "replay").unwrap();
    let owner = host.engine().database_owner();
    let route = terrabase_cluster::production::DatabaseRouteScope::new(
        owner.tenant(),
        owner.project(),
        owner.database(),
    );
    let progress = Arc::new(crate::replication_runtime::ReplicationProgress::default());
    progress.admit(route, std::collections::BTreeSet::from(["gru".into()])).unwrap();
    Arc::get_mut(&mut state).unwrap().replication_progress = Some(progress.clone());
    let server = serve(state.clone()).await;
    let before = host.engine().published_sequence().unwrap();
    let first = request(&server, "PUT", PATH, "timeout", Some(&etag), r#"{"a":2}"#).await;
    assert_eq!(first.0, StatusCode::SERVICE_UNAVAILABLE);
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&first.2).unwrap()["error"]["code"],
        "commit_unknown"
    );
    let committed = host.engine().published_sequence().unwrap();
    assert!(committed > before);
    assert_eq!(
        request(&server, "PUT", PATH, "timeout", Some(&etag), r#"{"a":2}"#).await.0,
        StatusCode::SERVICE_UNAVAILABLE
    );
    progress.advance_regional(route, committed.get());
    let retry = request(&server, "PUT", PATH, "timeout", Some(&etag), r#"{"a":2}"#).await;
    assert_eq!(retry.0, StatusCode::OK);
    assert_eq!(host.engine().published_sequence().unwrap(), committed);
    assert_eq!(
        request(&server, "PUT", PATH, "fresh", Some(&etag), r#"{"a":2}"#).await.0,
        StatusCode::PRECONDITION_FAILED
    );
    progress.fence_database(route);
    assert_eq!(
        request(&server, "PUT", PATH, "timeout", Some(&etag), r#"{"a":2}"#).await.0,
        StatusCode::SERVICE_UNAVAILABLE,
        "committed replay still enforces the current database serving fence"
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn conditional_http_concurrent_exact_retries_publish_once() {
    let state = setup().await;
    let server = Arc::new(serve(state.clone()).await);
    let seed = request(&server, "PUT", PATH, "seed", None, r#"{"a":1,"b":1}"#).await;
    assert_eq!(seed.0, StatusCode::OK);
    let etag = seed.1["etag"].to_str().unwrap().to_owned();
    let host = document_host(&state, "replay").unwrap();
    let before = host.engine().published_sequence().unwrap();
    let barrier = Arc::new(tokio::sync::Barrier::new(8));
    let mut tasks = tokio::task::JoinSet::new();
    for _ in 0..8 {
        let server = server.clone();
        let barrier = barrier.clone();
        let etag = etag.clone();
        tasks.spawn(async move {
            barrier.wait().await;
            request(&server, "PATCH", PATH, "concurrent", Some(&etag), r#"{"a":2}"#).await
        });
    }
    while let Some(result) = tasks.join_next().await {
        let response = result.unwrap();
        assert!(
            matches!(response.0, StatusCode::OK | StatusCode::SERVICE_UNAVAILABLE),
            "{response:?}"
        );
    }
    let retry = request(&server, "PATCH", PATH, "concurrent", Some(&etag), r#"{"a":2}"#).await;
    assert_eq!(retry.0, StatusCode::OK);
    assert_eq!(host.engine().published_sequence().unwrap().get(), before.get() + 1);
    assert_eq!(
        request(&server, "PATCH", PATH, "concurrent", Some(&etag), r#"{"a":3}"#).await.0,
        StatusCode::CONFLICT
    );
}
