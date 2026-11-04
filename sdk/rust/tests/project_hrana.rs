#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::HranaVersion;
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
