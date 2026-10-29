#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Client, HranaVersion};
#[tokio::test]
async fn hrana_pipeline_preserves_snakecase_values_and_inband_errors() {
    let answer = r#"{"baton":"fixture-private","base_url":null,"results":[{"type":"ok","response":{"type":"execute","result":{"affected_row_count":9007199254740993}}},{"type":"error","error":{"code":"SQLITE_ERROR","message":"no table"}}]}"#;
    let fixture = Fixture::new(vec![reply(200, answer), reply(200, answer)]).await;
    let client = fixture.client();
    let body = json!({"baton":null,"requests":[{"type":"execute","stmt":{"sql":"SELECT ?","want_rows":true,"named_args":[],"args":[{"type":"integer","value":"9007199254740993"}]}}]});
    for version in [HranaVersion::V2, HranaVersion::V3] {
        let result = client
            .database("orders")
            .libsql_pipeline(version, &body)
            .await
            .unwrap();
        assert_eq!(result.baton.as_deref(), Some("fixture-private"));
        assert!(!format!("{result:?}").contains("fixture-private"));
        assert!(result.results[0].get().contains("9007199254740993"));
        assert!(result.results[1].get().contains("SQLITE_ERROR"));
    }
    let seen = fixture.requests.lock().unwrap();
    for (i, v) in ["v2", "v3"].into_iter().enumerate() {
        assert_eq!(
            seen[i].uri,
            format!("/v1/projects/{PROJECT}/databases/orders/libsql/{v}/pipeline")
        );
        assert_eq!(
            serde_json::from_slice::<Value>(&seen[i].body).unwrap(),
            body
        );
        assert_eq!(seen[i].headers["authorization"], "Bearer fixture-token");
    }
}
#[tokio::test]
async fn cursor_keeps_header_and_all_complete_lines_without_following_base_url() {
    let text="{\"baton\":\"fixture-private\",\"base_url\":null}\n{\"type\":\"row\",\"row\":[{\"type\":\"integer\",\"value\":\"9007199254740993\"}]}\n{\"type\":\"step_end\",\"affected_row_count\":0}\n";
    let fixture = Fixture::with_type(vec![reply(200, text)], "application/x-ndjson").await;
    let cursor = fixture
        .client()
        .database("orders")
        .libsql_cursor(&json!({"baton":null,"batch":{"steps":[]}}))
        .await
        .unwrap();
    assert_eq!(cursor.entries.len(), 2);
    assert_eq!(cursor.baton.as_deref(), Some("fixture-private"));
    assert!(!format!("{cursor:?}").contains("fixture-private"));
    assert!(fixture.requests.lock().unwrap()[0]
        .uri
        .ends_with("/libsql/v3/cursor"));
}
#[tokio::test]
async fn hrana_refusals_and_malformed_acks_redact_batons_without_replay() {
    for (status, body, code) in [
        (
            400,
            r#"{"code":"STREAM_EXPIRED","error":"fixture-private","message":"fixture-private"}"#,
            "STREAM_EXPIRED",
        ),
        (
            200,
            r#"{"baton":"fixture-private","base_url":null}"#,
            "hrana_error",
        ),
        (
            503,
            r#"{"code":"DATABASE_STREAMS_EXHAUSTED","message":"fixture-private"}"#,
            "DATABASE_STREAMS_EXHAUSTED",
        ),
    ] {
        let fixture = Fixture::new(vec![reply(status, body)]).await;
        let client = Client::builder(&fixture.url, "fixture-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client
            .database("orders")
            .libsql_pipeline(HranaVersion::V3, &json!({"requests":[]}))
            .await
            .unwrap_err();
        assert_eq!(error.code(), Some(code));
        assert_eq!(error.is_uncertain(), status != 400);
        assert!(!format!("{error:?} {error}").contains("fixture-private"));
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}
#[tokio::test]
async fn incomplete_cursor_and_invalid_requests_never_produce_partial_success() {
    let fixture = Fixture::new(vec![reply(
        200,
        "{\"baton\":null,\"base_url\":null}\n{\"type\":\"row\"}",
    )])
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    assert!(db
        .libsql_pipeline(HranaVersion::V3, &"x".repeat((1 << 20) + 1))
        .await
        .is_err());
    assert!(client
        .database("../bad")
        .libsql_cursor(&json!({}))
        .await
        .is_err());
    assert!(db.libsql_cursor(&json!({})).await.is_err());
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}
