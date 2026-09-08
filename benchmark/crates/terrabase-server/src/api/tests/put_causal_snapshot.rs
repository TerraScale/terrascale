use super::super::*;
use super::state_with_keys_and_control;
use axum::body::Body;
use axum::http::Request;
use tower::ServiceExt as _;

const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";
const PATH: &str = "/api/v1/databases/put-causal/collections/items/documents/one";

async fn setup() -> (Arc<ApiState>, axum::Router) {
    let state = state_with_keys_and_control();
    let app = router(state.clone());
    let response = app
        .clone()
        .oneshot(
            Request::post("/api/v1/databases")
                .header("authorization", KEY)
                .header("x-terrascale-tenant-id", "terrabase-local")
                .header("x-terrascale-project-id", "local")
                .body(Body::from(r#"{"name":"put-causal","placement":["GRU"],"residency":null}"#))
                .expect("request"),
        )
        .await
        .expect("response");
    assert_eq!(response.status(), StatusCode::CREATED);
    (state, app)
}

fn put() -> axum::http::request::Builder {
    Request::put(PATH).header("authorization", KEY)
}

#[tokio::test]
async fn put_causal_snapshot_rejects_future_token_before_publication() {
    let (state, app) = setup().await;
    let first = app.clone().oneshot(put().body(Body::from(r#"{"n":1}"#)).unwrap()).await.unwrap();
    assert_eq!(first.status(), StatusCode::OK);
    let mut token = decode_session_header(first.headers()).unwrap().unwrap();
    let (origin, sequence) = token
        .frontier()
        .entries()
        .next()
        .map(|(origin, sequence)| (origin.clone(), sequence))
        .expect("committed origin");
    token.advance(origin, sequence + 1).unwrap();
    let host = document_host(&state, "put-causal").unwrap();
    let before = host.engine().published_sequence().unwrap();
    let response = app
        .clone()
        .oneshot(
            put()
                .header(SESSION_HEADER.clone(), URL_SAFE_NO_PAD.encode(token.encode()))
                .body(Body::from(r#"{"n":2}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CONFLICT);
    let body = axum::body::to_bytes(response.into_body(), 4096).await.unwrap();
    assert!(String::from_utf8_lossy(&body).contains("stale_replica"));
    assert_eq!(host.engine().published_sequence().unwrap(), before);
    let read = app
        .oneshot(Request::get(PATH).header("authorization", KEY).body(Body::empty()).unwrap())
        .await
        .unwrap();
    assert_eq!(read.headers()["etag"], first.headers()["etag"]);
}

#[tokio::test]
async fn put_causal_snapshot_advances_token_and_replays_original_revision_once() {
    let (state, app) = setup().await;
    let first = app
        .clone()
        .oneshot(
            put().header("idempotency-key", "original").body(Body::from(r#"{"n":1}"#)).unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(first.status(), StatusCode::OK);
    let first_token = decode_session_header(first.headers()).unwrap().unwrap();
    let changed = app
        .clone()
        .oneshot(
            put()
                .header("if-match", first.headers()["etag"].clone())
                .header(SESSION_HEADER.clone(), first.headers()[SESSION_HEADER.clone()].clone())
                .body(Body::from(r#"{"n":2}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(changed.status(), StatusCode::OK);
    let changed_token = decode_session_header(changed.headers()).unwrap().unwrap();
    let first_entries: Vec<_> = first_token.frontier().entries().collect();
    let changed_entries: Vec<_> = changed_token.frontier().entries().collect();
    assert_eq!(first_entries.len(), 1);
    assert_eq!(changed_entries.len(), 1);
    assert_eq!(first_entries[0].0, changed_entries[0].0);
    assert!(changed_entries[0].1 > first_entries[0].1, "response covers the overwrite");
    let host = document_host(&state, "put-causal").unwrap();
    let scope = document_scope(host.engine()).unwrap();
    let before = host.engine().published_sequence().unwrap();
    let usage_before = host
        .engine()
        .usage_page(&scope, terrabase::usage::UsagePageRequest::try_new(None, 100, None).unwrap())
        .unwrap();
    let replay = app
        .clone()
        .oneshot(
            put().header("idempotency-key", "original").body(Body::from(r#"{"n":1}"#)).unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(replay.status(), StatusCode::OK);
    assert_eq!(replay.headers()["etag"], first.headers()["etag"]);
    assert_eq!(host.engine().published_sequence().unwrap(), before);
    let usage_after = host
        .engine()
        .usage_page(&scope, terrabase::usage::UsagePageRequest::try_new(None, 100, None).unwrap())
        .unwrap();
    assert_eq!(usage_before.facts(), usage_after.facts(), "replay adds no billing facts");
    let stale = app
        .clone()
        .oneshot(
            put()
                .header("if-match", first.headers()["etag"].clone())
                .body(Body::from(r#"{"n":3}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(stale.status(), StatusCode::PRECONDITION_FAILED);
    let read = app
        .oneshot(Request::get(PATH).header("authorization", KEY).body(Body::empty()).unwrap())
        .await
        .unwrap();
    assert_eq!(read.headers()["etag"], changed.headers()["etag"]);
}
