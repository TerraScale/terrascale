use super::*;

use axum::body::Body;
use axum::http::header::CACHE_CONTROL;
use http_body_util::BodyExt;
use terrabase_tbep::proto::terrabase::v1::AuthGroupBootstrapGrant;

#[test]
fn auth_result_keeps_transaction_identity_and_camel_case_shape() {
    let mut result = AuthResult::record(None, true);
    result.transaction_id = Some("01JTRANSACTION#1".into());
    result.expires_at_ms = Some(1_735_000_000_000);

    let value = serde_json::to_value(result).expect("auth result serializes");
    assert_eq!(value["record"], serde_json::Value::Null);
    assert_eq!(value["matched"], true);
    assert_eq!(value["transactionId"], "01JTRANSACTION#1");
    assert_eq!(value["expiresAtMs"], 1_735_000_000_000u64);
    assert!(value.get("transaction_id").is_none());
    assert!(value.get("records").is_none());
}

#[tokio::test]
async fn error_responses_are_uncacheable_and_have_stable_envelope() {
    let response = error(StatusCode::BAD_REQUEST, "idempotency_key_required");
    assert_eq!(response.status(), StatusCode::BAD_REQUEST);
    assert_eq!(response.headers()[CACHE_CONTROL], "no-store");

    let body = response.into_body().collect().await.expect("body collects").to_bytes();
    let value: serde_json::Value = serde_json::from_slice(&body).expect("JSON error envelope");
    assert_eq!(value["error"]["code"], "idempotency_key_required");
    assert!(value["error"]["message"].as_str().is_some());
    assert!(value["error"]["fix"].as_str().is_some());
}

#[test]
fn placement_response_marks_private_result_uncacheable() {
    let grant =
        AuthGroupBootstrapGrant { regions: vec!["GRU".into(), "SFO".into()], ..Default::default() };
    let response = placement_response((StatusCode::OK, Body::empty()).into_response(), &grant);
    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(response.headers()[CACHE_CONTROL], "no-store");
    assert_eq!(response.headers()["x-terrascale-placement"], "GRU,SFO");
}

#[test]
fn openapi_registers_transactional_record_get() {
    let (_, document) = routes().split_for_parts();
    let paths = document.paths.paths;
    let path = paths
        .get("/api/v1/databases/{database}/auth/records/{model}/{id}")
        .expect("transactional record GET is published");
    assert!(path.get.is_some());
}
