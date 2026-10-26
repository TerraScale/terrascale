#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{FunctionDeployment, FunctionTrigger};

#[tokio::test]
async fn function_lifecycle_matches_customer_contract() {
    let function = json!({"name":"audit","source":"function handler(event) { return event.body; }",
        "trigger":{"collection":"orders","events":["upsert"]},"cron":null,
        "createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T00:00:00Z"});
    let log = json!({"id":"01ARZ3NDEKTSV4RRFFQ69G5FAC","at":"2026-10-26T00:00:00Z",
        "trigger":"http","status":"ok","durationMs":"9007199254740993","lines":["hello"],"error":null});
    let fixture = Fixture::new(vec![
        reply(200, &function.to_string()),
        reply(200, &json!([function.clone()]).to_string()),
        reply(200, &function.to_string()),
        reply(200, "{\"hello\":\"Ada\"}"),
        reply(200, "[]"),
        reply(200, &json!([log]).to_string()),
        reply(204, ""),
    ])
    .await;
    let db = fixture.client().database("orders");
    let deployed = db
        .deploy_function(
            "audit",
            &FunctionDeployment {
                source: function["source"].as_str().unwrap().into(),
                trigger: Some(FunctionTrigger {
                    collection: "orders".into(),
                    events: Some(vec!["upsert".into()]),
                }),
                cron: None,
            },
        )
        .await
        .unwrap();
    assert_eq!(deployed.trigger.unwrap().collection, "orders");
    assert_eq!(db.list_functions().await.unwrap()[0].name, "audit");
    assert!(db.get_function("audit").await.unwrap().source.is_some());
    let result: Value = db
        .invoke_function("audit", &json!({"name":"Ada"}))
        .await
        .unwrap();
    assert_eq!(result["hello"], "Ada");
    assert!(db
        .list_function_deliveries("audit")
        .await
        .unwrap()
        .is_empty());
    assert_eq!(
        db.list_function_logs("audit").await.unwrap()[0].duration_ms,
        9007199254740993
    );
    db.delete_function("audit").await.unwrap();
    let requests = fixture.requests.lock().unwrap();
    let prefix = format!("/v1/projects/{PROJECT}/databases/orders/functions");
    for (i, method, suffix) in [
        (0, "PUT", "/audit"),
        (1, "GET", ""),
        (2, "GET", "/audit"),
        (3, "POST", "/audit/invoke"),
        (4, "GET", "/audit/deliveries"),
        (5, "GET", "/audit/logs"),
        (6, "DELETE", "/audit"),
    ] {
        assert_eq!(requests[i].method, method);
        assert_eq!(requests[i].uri, format!("{prefix}{suffix}"));
    }
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap()["trigger"],
        function["trigger"]
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[3].body).unwrap(),
        json!({"name":"Ada"})
    );
}

#[tokio::test]
async fn invalid_function_names_do_not_dispatch() {
    let fixture = Fixture::new(vec![]).await;
    let db = fixture.client().database("orders");
    for name in ["../other", "Bad Name", ""] {
        assert!(db.get_function(name).await.is_err());
        assert!(db.invoke_function::<Value>(name, &json!({})).await.is_err());
        assert!(db.delete_function(name).await.is_err());
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
}

#[test]
fn function_duration_accepts_both_contract_encodings() {
    for duration in [json!(1), json!("9007199254740993")] {
        let log: terrascale::v1::FunctionLog = serde_json::from_value(json!({"id":"fixture",
            "at":"2026-10-26T00:00:00Z","trigger":"http","status":"ok",
            "durationMs":duration,"lines":[],"error":null}))
        .unwrap();
        assert!(log.duration_ms > 0);
    }
}

#[tokio::test]
async fn custom_function_responses_preserve_status_headers_and_bytes() {
    let fixture = Fixture::with_type(
        vec![reply(201, "hello Ada"), reply(201, "{\"ok\":true}")],
        "text/plain",
    )
    .await;
    let db = fixture.client().database("orders");
    let response = db
        .invoke_function_response("hello", &json!(null))
        .await
        .unwrap();
    assert_eq!(response.status, 201);
    assert_eq!(response.headers["content-type"], "text/plain");
    assert_eq!(response.body, b"hello Ada");
    let result: Value = db.invoke_function("hello", &json!({})).await.unwrap();
    assert_eq!(result["ok"], true);
}
