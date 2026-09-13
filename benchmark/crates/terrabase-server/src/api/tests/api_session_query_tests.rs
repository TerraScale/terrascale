use super::super::*;
use super::state_with_keys_and_control;

use axum::body::Body;
use axum::http::{HeaderValue, Request, StatusCode};
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use terrabase::SessionToken;
use tower::util::ServiceExt;

const AUTH: &str = "authorization";
const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";

async fn seeded() -> (axum::Router, axum::http::HeaderValue, SessionToken) {
    let router = router(state_with_keys_and_control());
    let response = router
        .clone()
        .oneshot(
            Request::post("/api/v1/databases")
                .header(AUTH, KEY)
                .header("x-terrascale-tenant-id", "terrabase-local")
                .header("x-terrascale-project-id", "local")
                .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                .expect("database request"),
        )
        .await
        .expect("database response");
    assert_eq!(response.status(), StatusCode::CREATED);
    let response = router
        .clone()
        .oneshot(
            Request::put("/api/v1/databases/prod/collections/users/documents/one")
                .header(AUTH, KEY)
                .body(Body::from(r#"{"n":1}"#))
                .expect("document request"),
        )
        .await
        .expect("document response");
    assert_eq!(response.status(), StatusCode::OK);
    let token = decode_session_header(response.headers())
        .expect("session header decodes")
        .expect("write returns session");
    let encoded = HeaderValue::from_str(&URL_SAFE_NO_PAD.encode(token.encode())).expect("header");
    (router, encoded, token)
}

#[tokio::test]
async fn malformed_session_tokens_are_rejected_by_sql_and_all_list_routes() {
    let (router, _, _) = seeded().await;
    for request in [
        Request::get("/api/v1/databases/prod/collections/users/documents")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), "not-a-token")
            .body(Body::empty())
            .expect("documents request"),
        Request::get("/api/v1/databases/prod/collections")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), "not-a-token")
            .body(Body::empty())
            .expect("collections request"),
        Request::get("/api/v1/databases/prod/collections/users/indexes")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), "not-a-token")
            .body(Body::empty())
            .expect("indexes request"),
        Request::post("/api/v1/databases/prod/sql")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), "not-a-token")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"sql":"SELECT * FROM users"}"#))
            .expect("sql request"),
    ] {
        let response = router.clone().oneshot(request).await.expect("route response");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("error body");
        assert!(String::from_utf8_lossy(&body).contains("invalid_session_token"));
    }
}

#[tokio::test]
async fn future_session_token_is_rejected_by_sql_and_document_listing() {
    let (router, _, mut token) = seeded().await;
    let (origin, sequence) = token
        .frontier()
        .entries()
        .next()
        .map(|(origin, sequence)| (origin.clone(), sequence))
        .expect("write frontier");
    token.advance(origin, sequence + 1).expect("future frontier");
    let encoded = URL_SAFE_NO_PAD.encode(token.encode());
    for request in [
        Request::get("/api/v1/databases/prod/collections/users/documents")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), encoded.clone())
            .body(Body::empty())
            .expect("documents request"),
        Request::post("/api/v1/databases/prod/sql")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), encoded.clone())
            .header("content-type", "application/json")
            .body(Body::from(r#"{"sql":"SELECT * FROM users"}"#))
            .expect("sql request"),
    ] {
        let started = std::time::Instant::now();
        let response = router.clone().oneshot(request).await.expect("route response");
        assert_eq!(response.status(), StatusCode::CONFLICT);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("error body");
        assert!(String::from_utf8_lossy(&body).contains("stale_replica"));
        assert!(started.elapsed() >= std::time::Duration::from_secs(2));
    }
}

#[tokio::test]
async fn successful_session_query_and_lists_return_the_current_token() {
    let (router, encoded, expected) = seeded().await;
    for request in [
        Request::get("/api/v1/databases/prod/collections/users/documents")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), encoded.clone())
            .body(Body::empty())
            .expect("documents request"),
        Request::get("/api/v1/databases/prod/collections")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), encoded.clone())
            .body(Body::empty())
            .expect("collections request"),
        Request::get("/api/v1/databases/prod/collections/users/indexes")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), encoded.clone())
            .body(Body::empty())
            .expect("indexes request"),
        Request::post("/api/v1/databases/prod/sql")
            .header(AUTH, KEY)
            .header(SESSION_HEADER.clone(), encoded.clone())
            .header("content-type", "application/json")
            .body(Body::from(r#"{"sql":"SELECT * FROM users"}"#))
            .expect("sql request"),
    ] {
        let response = router.clone().oneshot(request).await.expect("route response");
        assert_eq!(response.status(), StatusCode::OK);
        let returned = decode_session_header(response.headers())
            .expect("response session decodes")
            .expect("response includes session");
        assert_eq!(returned, expected);
    }
}
