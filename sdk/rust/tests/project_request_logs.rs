#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::DatabaseRequestLogs;
fn logs(status: Value, operations: Value, latency: Value) -> Value {
    json!({"databaseId":PROJECT,"truncated":true,"recordingLimited":true,"items":[{
        "id":PROJECT,"time":"2026-10-26T12:00:00Z","region":"gru","method":"POST",
        "route":"/collections/{collection}/query","status":status,"latencyMs":latency,
        "operations":operations,"isQuery":true,"requestId":PROJECT}]})
}
#[tokio::test]
async fn request_logs_keep_capacity_indicators_filters_and_correlation() {
    let fixture = Fixture::new(vec![
        reply(200, &logs(json!(200), json!(5), json!(100.25)).to_string()),
        reply(
            200,
            r#"{"databaseId":"db","items":[],"truncated":false,"recordingLimited":false}"#,
        ),
    ])
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    let page = db.list_database_logs(Some("2xx")).await.unwrap();
    assert!(page.truncated && page.recording_limited);
    assert_eq!(page.items[0].request_id.as_deref(), Some(PROJECT));
    assert_eq!(page.items[0].latency_ms.as_str(), "100.25");
    assert!(db.list_database_logs(None).await.unwrap().items.is_empty());
    assert!(db.list_database_logs(Some("200")).await.is_err());
    assert!(client
        .database("../orders")
        .list_database_logs(None)
        .await
        .is_err());
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen.len(), 2);
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/logs?status=2xx")
    );
    assert_eq!(
        seen[1].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/logs")
    );
    assert_eq!(seen[0].headers["authorization"], "Bearer fixture-token");
}
#[test]
fn log_counters_and_latency_preserve_both_encodings() {
    for n in [0, 9_007_199_254_740_993, i64::MAX as u64] {
        for count in [json!(n), json!(n.to_string())] {
            let page: DatabaseRequestLogs =
                serde_json::from_value(logs(json!("503"), count, json!("100.00000000000001")))
                    .unwrap();
            assert_eq!(page.items[0].operations, n);
            assert_eq!(page.items[0].status, 503);
            assert_eq!(page.items[0].latency_ms.as_str(), "100.00000000000001");
        }
    }
    for bad in [json!(-1), json!(1.5), json!("01"), json!(u64::MAX)] {
        assert!(
            serde_json::from_value::<DatabaseRequestLogs>(logs(json!(200), bad, json!(1))).is_err()
        );
    }
    for bad in [json!(true), json!("NaN"), json!("01"), Value::Null] {
        assert!(
            serde_json::from_value::<DatabaseRequestLogs>(logs(json!(200), json!(1), bad)).is_err()
        );
    }
}
#[tokio::test]
async fn forbidden_logs_retain_typed_scope_refusal() {
    let fixture = Fixture::new(vec![reply(403, r#"{"error":{"code":"forbidden_scope"}}"#)]).await;
    let error = fixture
        .client()
        .database("orders")
        .list_database_logs(None)
        .await
        .unwrap_err();
    assert_eq!(error.code(), Some("forbidden_scope"));
    assert!(!error.is_uncertain());
}
