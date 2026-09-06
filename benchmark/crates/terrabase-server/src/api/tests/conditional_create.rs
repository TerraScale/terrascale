use super::super::create_only_precondition;
use super::*;
use axum::Router;
use axum::body::Body;
use axum::http::Request;

const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";
const PATH: &str = "/api/v1/databases/create-only/collections/users/documents/unique";

async fn setup() -> Router {
    let app = router(state_with_keys_and_control());
    let response = app
        .clone()
        .oneshot(
            Request::post("/api/v1/databases")
                .header("authorization", KEY)
                .body(Body::from(r#"{"name":"create-only","placement":["GRU"],"residency":null}"#))
                .expect("request"),
        )
        .await
        .expect("create database");
    assert_eq!(response.status(), StatusCode::CREATED);
    app
}

fn create(key: &str, body: &str) -> Request<Body> {
    Request::put(PATH)
        .header("authorization", KEY)
        .header("if-none-match", "*")
        .header("idempotency-key", key)
        .body(Body::from(body.to_owned()))
        .expect("create request")
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn concurrent_create_only_puts_have_one_local_winner_and_preserve_it() {
    let app = setup().await;
    let mut tasks = tokio::task::JoinSet::new();
    let start = Arc::new(tokio::sync::Barrier::new(8));
    for index in 0..8 {
        let app = app.clone();
        let start = Arc::clone(&start);
        tasks.spawn(async move {
            start.wait().await;
            let response = app
                .oneshot(create(&format!("create-{index}"), &format!("{{\"winner\":{index}}}")))
                .await
                .expect("response");
            (index, response.status())
        });
    }
    let mut winners = Vec::new();
    while let Some(result) = tasks.join_next().await {
        let (index, status) = result.expect("task");
        if status == StatusCode::OK {
            winners.push(index);
        } else {
            assert_eq!(status, StatusCode::PRECONDITION_FAILED);
        }
    }
    assert_eq!(winners.len(), 1);
    let response = app
        .oneshot(Request::get(PATH).header("authorization", KEY).body(Body::empty()).expect("read"))
        .await
        .expect("response");
    assert_eq!(response.status(), StatusCode::OK);
    let bytes = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
    let document: serde_json::Value = serde_json::from_slice(&bytes).expect("json");
    assert_eq!(document["winner"], winners[0]);
}

#[tokio::test]
async fn create_only_idempotency_replays_without_a_second_commit() {
    let app = setup().await;
    let first = app.clone().oneshot(create("original", r#"{"value":1}"#)).await.expect("first");
    assert_eq!(first.status(), StatusCode::OK);
    let revision = first.headers()["etag"].clone();
    let replay = app.clone().oneshot(create("original", r#"{"value":1}"#)).await.expect("replay");
    assert_eq!(replay.status(), StatusCode::OK);
    assert_eq!(replay.headers()["etag"], revision, "replay does not commit again");
    let conflicting =
        app.clone().oneshot(create("different", r#"{"value":2}"#)).await.expect("conflict");
    assert_eq!(conflicting.status(), StatusCode::PRECONDITION_FAILED);
    let reused = app.clone().oneshot(create("original", r#"{"value":2}"#)).await.expect("reused");
    assert_eq!(reused.status(), StatusCode::CONFLICT, "idempotency key remains body-bound");
    let read = app
        .oneshot(
            Request::get(PATH).header("authorization", KEY).body(Body::empty()).expect("request"),
        )
        .await
        .expect("read");
    assert_eq!(read.headers()["etag"], revision);
}

#[test]
fn create_only_rejects_ambiguous_and_unsupported_header_forms() {
    let mut headers = axum::http::HeaderMap::new();
    assert!(!create_only_precondition(&headers).expect("absent"));
    headers.insert("if-none-match", HeaderValue::from_static("*"));
    assert!(create_only_precondition(&headers).expect("create only"));
    headers.append("if-none-match", HeaderValue::from_static("*"));
    assert!(create_only_precondition(&headers).is_err());
    headers.remove("if-none-match");
    headers.insert("if-none-match", HeaderValue::from_static("\"revision\""));
    assert!(create_only_precondition(&headers).is_err());
    headers.insert("if-none-match", HeaderValue::from_static("*"));
    headers.insert("if-match", HeaderValue::from_static("*"));
    assert!(create_only_precondition(&headers).is_err());
}
