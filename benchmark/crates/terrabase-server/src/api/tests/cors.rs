use super::*;

#[tokio::test]
async fn browser_preflight_bypasses_auth_but_the_read_does_not() {
    let app = router(state_with_keys_and_control());
    let preflight = app
        .clone()
        .oneshot(
            Request::builder()
                .method("OPTIONS")
                .uri("/api/v1/reactive/views")
                .header("origin", "https://customer.example")
                .header("access-control-request-method", "POST")
                .header("access-control-request-headers", "authorization,content-type")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert!(preflight.status().is_success());
    let headers = preflight.headers();
    assert_eq!(headers["access-control-allow-origin"], "*");
    assert!(headers["access-control-allow-methods"].to_str().unwrap().contains("POST"));
    assert!(headers["access-control-allow-headers"].to_str().unwrap().contains("authorization"));
    assert!(!headers.contains_key("access-control-allow-credentials"));
    assert!(!headers.contains_key("vary"));

    let read = app
        .oneshot(
            Request::get("/api/v1/databases")
                .header("origin", "https://customer.example")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(read.status(), StatusCode::UNAUTHORIZED);
    assert_eq!(read.headers()["access-control-allow-origin"], "*");
    assert!(
        read.headers()["access-control-expose-headers"]
            .to_str()
            .unwrap()
            .contains("x-terrascale-region")
    );
}

#[tokio::test]
async fn cors_does_not_authorize_internal_headers_or_unsupported_methods() {
    let response = router(state_with_keys_and_control())
        .oneshot(
            Request::builder()
                .method("OPTIONS")
                .uri("/api/v1/reactive/views")
                .header("origin", "https://customer.example")
                .header("access-control-request-method", "TRACE")
                .header("access-control-request-headers", "x-terrascale-origin-proof")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert!(
        !response.headers()["access-control-allow-methods"].to_str().unwrap().contains("TRACE")
    );
    assert!(
        !response.headers()["access-control-allow-headers"]
            .to_str()
            .unwrap()
            .contains("origin-proof")
    );
}
