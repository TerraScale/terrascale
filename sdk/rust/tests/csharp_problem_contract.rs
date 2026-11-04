//! Local HTTP negative contracts at server e30ffdc287530551b161b0130220be53d3c0475e.
//! That server emits nested envelopes; RFC 9457 cases exercise defensive
//! decoding and do not claim that the server implements problem details.

use std::{convert::Infallible, time::Duration};

use bytes::Bytes;
use http_body_util::Full;
use hyper::{service::service_fn, Request, Response};
use hyper_util::rt::TokioIo;
use terrascale::v1::{Client, Error, RetryPolicy};

const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

async fn response_error(
    status: u16,
    body: &'static [u8],
    content_type: &'static str,
    request_id: Option<&'static str>,
) -> Error {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let endpoint = format!("http://{}", listener.local_addr().unwrap());
    let server = tokio::spawn(async move {
        let (socket, _) = listener.accept().await.unwrap();
        let service = service_fn(move |request: Request<hyper::body::Incoming>| async move {
            assert_eq!(request.method(), "GET");
            assert_eq!(
                request.uri().path(),
                format!("/v1/projects/{PROJECT}/databases/app/collections/items")
            );
            assert_eq!(
                request.headers()["authorization"],
                "Bearer fixture-access-token"
            );
            let mut response = Response::builder()
                .status(status)
                .header("content-type", content_type)
                .header("x-terrabase-region", "fixture-region");
            if let Some(request_id) = request_id {
                response = response.header("x-request-id", request_id);
            }
            Ok::<_, Infallible>(response.body(Full::new(Bytes::from_static(body))).unwrap())
        });
        hyper::server::conn::http1::Builder::new()
            .keep_alive(false)
            .serve_connection(TokioIo::new(socket), service)
            .await
            .unwrap();
    });
    let client = Client::builder(endpoint, "fixture-access-token")
        .project(PROJECT)
        .region_pin("fixture-region")
        .retry_policy(RetryPolicy::none())
        .timeout(Some(Duration::from_secs(5)))
        .build()
        .unwrap();
    let error = client
        .database("app")
        .collection("items")
        .get_collection()
        .await
        .unwrap_err();
    tokio::time::timeout(Duration::from_secs(5), server)
        .await
        .unwrap()
        .unwrap();
    error
}

#[tokio::test]
async fn rfc9457_keeps_exact_extensions_and_http_status_wins() {
    let body = br#"{"type":"https://example.test/problems/permission","title":"Permission denied","status":200,"detail":"documents:read is required","code":"invalid_key","op":3,"requestId":"body-request","future":{"exact":123456789012345678901234567890.123456789,"unbounded":1e9999}}"#;
    let error = response_error(
        403,
        body,
        "application/problem+json",
        Some("header-request"),
    )
    .await;
    assert_eq!(error.status(), Some(403));
    assert_eq!(error.code(), Some("invalid_key"));
    assert_eq!(error.op(), Some(3));
    assert_eq!(error.request_id(), Some("header-request"));
    assert_eq!(error.raw_body(), Some(&body[..]));
    assert!(!error.is_uncertain());
    assert!(!error.is_not_found());
    assert!(error.to_string().contains("documents:read is required"));
    let raw: Box<serde_json::value::RawValue> =
        serde_json::from_slice(error.raw_body().unwrap()).unwrap();
    assert_eq!(serde_json::to_vec(&raw).unwrap(), body);
}

#[tokio::test]
async fn malformed_and_empty_errors_preserve_bytes_status_and_request_id() {
    for (status, body) in [
        (400, &b""[..]),
        (403, &b"{\"error\":"[..]),
        (503, &b"upstream error: \xff\xfe"[..]),
    ] {
        let error =
            response_error(status, body, "application/json", Some("malformed-request")).await;
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(format!("http_{status}").as_str()));
        assert_eq!(error.raw_body(), Some(body));
        assert_eq!(error.request_id(), Some("malformed-request"));
        assert_eq!(error.is_uncertain(), status >= 500);
    }
}
