//! Exact project HTTP SQL fixtures, independent of a server binary or credentials.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::{service::service_fn, HeaderMap, Request, Response};
use hyper_util::rt::TokioIo;
use serde_json::{json, value::RawValue, Value};
use terrascale::v1::{Client, Error, RetryPolicy, SqlCheck, SqlResult};
use tokio::sync::Notify;

const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const EMPTY: &str = r#"{"matched":0,"truncated":false,"rows":[]}"#;
const PLAN: &str = r#"{"ok":true,"error":null,"collection":"items","hasLimit":true,"plan":["SEARCH documents"],"indexes":["items_score"],"suggestions":[{"field":"name","reason":"filtered without an index"}]}"#;
const INVALID: &str = r#"{"ok":false,"error":"only SELECT statements are supported","collection":null,"hasLimit":false,"plan":[],"indexes":[],"suggestions":[]}"#;

enum Reply {
    Body(u16, String),
    Disconnect,
    Hold,
}

struct Observed {
    method: String,
    path: String,
    headers: HeaderMap,
    body: Value,
}

struct Fixture {
    url: String,
    requests: Arc<Mutex<Vec<Observed>>>,
    arrived: Arc<Notify>,
    disconnected: Arc<Notify>,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl Fixture {
    async fn new(replies: Vec<Reply>) -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let replies = Arc::new(Mutex::new(VecDeque::from(replies)));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let arrived = Arc::new(Notify::new());
        let disconnected = Arc::new(Notify::new());
        let (responses, received, arrival, disconnect) = (
            replies,
            requests.clone(),
            arrived.clone(),
            disconnected.clone(),
        );
        let task = tokio::spawn(async move {
            let mut connections = tokio::task::JoinSet::new();
            loop {
                tokio::select! {
                    accepted = listener.accept() => {
                        let (socket, _) = accepted.unwrap();
                        let (responses, received, arrival, disconnect) = (
                            responses.clone(), received.clone(), arrival.clone(), disconnect.clone(),
                        );
                        connections.spawn(async move {
                            let service = service_fn(move |request: Request<hyper::body::Incoming>| {
                                let (responses, received, arrival) = (
                                    responses.clone(), received.clone(), arrival.clone(),
                                );
                                async move {
                                    let (parts, body) = request.into_parts();
                                    let body = body.collect().await.unwrap().to_bytes();
                                    received.lock().unwrap().push(Observed {
                                        method: parts.method.to_string(),
                                        path: parts.uri.to_string(),
                                        headers: parts.headers,
                                        body: serde_json::from_slice(&body).unwrap(),
                                    });
                                    let reply = responses.lock().unwrap().pop_front().expect("unexpected request");
                                    arrival.notify_one();
                                    match reply {
                                        Reply::Body(status, body) => Ok::<_, std::io::Error>(
                                            Response::builder()
                                                .status(status)
                                                .header("content-type", "application/json")
                                                .header("x-request-id", "sql-fixture-request")
                                                .body(Full::new(Bytes::from(body)))
                                                .unwrap(),
                                        ),
                                        Reply::Disconnect => Err(std::io::Error::other("fixture disconnect")),
                                        Reply::Hold => std::future::pending().await,
                                    }
                                }
                            });
                            let _ = hyper::server::conn::http1::Builder::new()
                                .serve_connection(TokioIo::new(socket), service)
                                .await;
                            disconnect.notify_one();
                        });
                    }
                    _ = connections.join_next(), if !connections.is_empty() => {}
                }
            }
        });
        Self {
            url,
            requests,
            arrived,
            disconnected,
            task,
        }
    }

    fn client(&self) -> Client {
        Client::builder(&self.url, "fixture-project-access-token")
            .project(PROJECT)
            .retry_policy(RetryPolicy::none())
            .build()
            .unwrap()
    }
}

fn body(status: u16, value: &str) -> Reply {
    Reply::Body(status, value.into())
}

fn assert_decode(error: Error, status: u16) {
    assert!(
        matches!(&error, Error::Response { error, .. } if matches!(error.as_ref(), Error::Decode(_))),
        "{error:?}"
    );
    assert_eq!(error.status(), Some(status));
    assert_eq!(error.request_id(), Some("sql-fixture-request"));
}

#[tokio::test]
async fn sql_routes_send_only_the_actual_project_contract() {
    let fixture = Fixture::new(vec![body(200, EMPTY), body(200, INVALID), body(200, PLAN)]).await;
    let database = fixture.client().database("orders");
    database
        .run_sql::<Value>("SELECT * FROM items LIMIT 1")
        .await
        .unwrap();
    let invalid = database.check_sql("DELETE FROM items").await.unwrap();
    assert!(!invalid.ok);
    assert_eq!(
        invalid.error.as_deref(),
        Some("only SELECT statements are supported")
    );
    let plan = database
        .explain_sql("SELECT name FROM items LIMIT 1")
        .await
        .unwrap();
    assert_eq!(plan.indexes, ["items_score"]);
    assert_eq!(plan.suggestions[0].field, "name");
    let requests = fixture.requests.lock().unwrap();
    for (request, suffix, sql) in [
        (&requests[0], "", "SELECT * FROM items LIMIT 1"),
        (&requests[1], "/check", "DELETE FROM items"),
        (&requests[2], "/explain", "SELECT name FROM items LIMIT 1"),
    ] {
        assert_eq!(request.method, "POST");
        assert_eq!(
            request.path,
            format!("/v1/projects/{PROJECT}/databases/orders/sql{suffix}")
        );
        assert_eq!(request.body, json!({"sql": sql}));
        assert_eq!(
            request.headers["authorization"],
            "Bearer fixture-project-access-token"
        );
        assert_eq!(request.headers["content-type"], "application/json");
        assert!(!request.headers.contains_key("if-match"));
        assert!(!request.headers.contains_key("idempotency-key"));
    }
}

#[tokio::test]
async fn sql_rows_preserve_exact_json_and_truncation() {
    let raw = r#"{"matched":1,"truncated":true,"rows":[{"id":"document-key","value":{"decimal":123456789012345678901234567890.123456789,"missing":null}}]}"#;
    let fixture = Fixture::new(vec![body(200, raw)]).await;
    let result = fixture
        .client()
        .database("orders")
        .run_sql::<Box<RawValue>>("SELECT decimal, missing FROM items LIMIT 1")
        .await
        .unwrap();
    assert!(result.truncated);
    assert_eq!(result.matched, 1);
    assert_eq!(result.rows[0].id, "document-key");
    assert!(result.rows[0]
        .value
        .get()
        .contains("123456789012345678901234567890.123456789"));
    let encoded = serde_json::to_string(&result).unwrap();
    assert_eq!(encoded, raw);
    let roundtrip: SqlResult<Box<RawValue>> = serde_json::from_str(&encoded).unwrap();
    assert_eq!(roundtrip.rows[0].value.get(), result.rows[0].value.get());
    let plan: SqlCheck = serde_json::from_str(PLAN).unwrap();
    assert_eq!(
        serde_json::from_str::<SqlCheck>(&serde_json::to_string(&plan).unwrap()).unwrap(),
        plan
    );
}

#[tokio::test]
async fn sql_empty_result_and_typed_rows_decode() {
    #[derive(Debug, serde::Deserialize, PartialEq)]
    struct Projection {
        name: String,
    }
    let fixture = Fixture::new(vec![
        body(200, EMPTY),
        body(
            200,
            r#"{"matched":1,"truncated":false,"rows":[{"id":"item-a","value":{"name":"Ada"}}]}"#,
        ),
    ])
    .await;
    let database = fixture.client().database("orders");
    let empty = database
        .run_sql::<Projection>("SELECT name FROM items")
        .await
        .unwrap();
    assert_eq!(empty.matched, 0);
    assert!(!empty.truncated);
    assert!(empty.rows.is_empty());
    let result = database
        .run_sql::<Projection>("SELECT name FROM items")
        .await
        .unwrap();
    assert_eq!(result.rows[0].value, Projection { name: "Ada".into() });
}

#[tokio::test]
async fn sql_rejects_malformed_results_and_unexpected_success_statuses() {
    for malformed in [
        "",
        "null",
        "{}",
        "{",
        r#"{"matched":1,"truncated":false,"rows":[]}"#,
        r#"{"matched":0,"truncated":false,"rows":[{"id":"a","value":{}}]}"#,
        r#"{"matched":-1,"truncated":false,"rows":[]}"#,
        r#"{"matched":"0","truncated":false,"rows":[]}"#,
        r#"{"matched":1,"truncated":false,"rows":[{"id":"","value":{}}]}"#,
        r#"{"matched":1,"truncated":false,"rows":[{"id":"a","value":null}]}"#,
    ] {
        let fixture = Fixture::new(vec![body(200, malformed)]).await;
        let error = fixture
            .client()
            .database("orders")
            .run_sql::<Value>("SELECT * FROM items")
            .await
            .unwrap_err();
        assert_eq!(error.raw_body(), Some(malformed.as_bytes()));
        assert_decode(error, 200);
    }
    for status in [201, 202, 204, 206, 304] {
        let fixture = Fixture::new(vec![body(status, EMPTY)]).await;
        let error = fixture
            .client()
            .database("orders")
            .run_sql::<Value>("SELECT * FROM items")
            .await
            .unwrap_err();
        assert_decode(error, status);
    }
}

#[tokio::test]
async fn sql_check_and_explain_keep_distinct_invalid_sql_semantics() {
    let invalid_error =
        r#"{"error":{"code":"invalid_request","message":"only SELECT statements are supported"}}"#;
    let fixture = Fixture::new(vec![body(200, INVALID), body(400, invalid_error)]).await;
    let database = fixture.client().database("orders");
    assert!(!database.check_sql("DELETE FROM items").await.unwrap().ok);
    let error = database.explain_sql("DELETE FROM items").await.unwrap_err();
    assert_eq!(error.status(), Some(400));
    assert_eq!(error.code(), Some("invalid_request"));
    let fixture = Fixture::new(vec![body(200, INVALID)]).await;
    assert_decode(
        fixture
            .client()
            .database("orders")
            .explain_sql("DELETE FROM items")
            .await
            .unwrap_err(),
        200,
    );
}

#[tokio::test]
async fn sql_rejects_inconsistent_plan_bodies() {
    for changes in [
        json!({"ok":true,"collection":null}),
        json!({"ok":true,"collection":"Bad Name"}),
        json!({"ok":true,"error":"bad SQL"}),
        json!({"ok":false,"error":null}),
        json!({"ok":false,"error":"bad SQL","collection":null,"plan":["SCAN"]}),
    ] {
        let mut malformed: Value = serde_json::from_str(PLAN).unwrap();
        for (key, value) in changes.as_object().unwrap() {
            malformed[key] = value.clone();
        }
        let fixture = Fixture::new(vec![Reply::Body(200, malformed.to_string())]).await;
        assert_decode(
            fixture
                .client()
                .database("orders")
                .check_sql("SELECT * FROM items")
                .await
                .unwrap_err(),
            200,
        );
    }
}

#[tokio::test]
async fn sql_preserves_denied_credentials_and_does_not_retry_them() {
    for (status, code) in [
        (401, "invalid_key"),
        (403, "forbidden_scope"),
        (404, "unknown_database"),
    ] {
        let error_body =
            json!({"error":{"code":code,"message":"credential cannot reach this database"}})
                .to_string();
        let fixture = Fixture::new(vec![Reply::Body(status, error_body.clone())]).await;
        let client = Client::builder(&fixture.url, "fixture-denied-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client
            .database("orders")
            .run_sql::<Value>("SELECT * FROM items")
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert_eq!(error.raw_body(), Some(error_body.as_bytes()));
        assert_eq!(error.request_id(), Some("sql-fixture-request"));
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn sql_read_only_retry_resends_identical_sql_after_disconnect() {
    let fixture = Fixture::new(vec![Reply::Disconnect, body(200, EMPTY)]).await;
    let client = Client::builder(&fixture.url, "fixture-project-access-token")
        .project(PROJECT)
        .retry_policy(RetryPolicy {
            max_retries: 1,
            base_delay: Duration::ZERO,
            max_delay: Duration::ZERO,
            ..RetryPolicy::default()
        })
        .build()
        .unwrap();
    client
        .database("orders")
        .run_sql::<Value>("SELECT * FROM items WHERE name = 'Ada'")
        .await
        .unwrap();
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    assert_eq!(requests[0].body, requests[1].body);
    assert_eq!(requests[0].path, requests[1].path);
}

#[tokio::test]
async fn sql_retries_current_server_read_admission_failures() {
    for code in ["storage_busy", "not_primary"] {
        let fixture = Fixture::new(vec![
            Reply::Body(503, json!({"error":{"code":code}}).to_string()),
            body(200, EMPTY),
        ])
        .await;
        let client = Client::builder(&fixture.url, "fixture-project-access-token")
            .project(PROJECT)
            .retry_policy(RetryPolicy {
                max_retries: 1,
                base_delay: Duration::ZERO,
                max_delay: Duration::ZERO,
                ..RetryPolicy::default()
            })
            .build()
            .unwrap();
        client
            .database("orders")
            .run_sql::<Value>("SELECT * FROM items")
            .await
            .unwrap();
        let requests = fixture.requests.lock().unwrap();
        assert_eq!(requests.len(), 2, "{code}");
        assert_eq!(requests[0].body, requests[1].body);
    }
}

#[tokio::test]
async fn dropping_sql_future_cancels_the_pending_http_request() {
    let fixture = Fixture::new(vec![Reply::Hold, body(200, EMPTY)]).await;
    let database = fixture.client().database("orders");
    let operation = tokio::spawn({
        let database = database.clone();
        async move { database.run_sql::<Value>("SELECT * FROM items").await }
    });
    tokio::time::timeout(Duration::from_secs(2), fixture.arrived.notified())
        .await
        .unwrap();
    operation.abort();
    assert!(operation.await.unwrap_err().is_cancelled());
    tokio::time::timeout(Duration::from_secs(2), fixture.disconnected.notified())
        .await
        .unwrap();
    database
        .run_sql::<Value>("SELECT * FROM items")
        .await
        .unwrap();
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
}

#[tokio::test]
async fn sql_requires_a_project_client_before_dispatch() {
    let fixture = Fixture::new(Vec::new()).await;
    let token = format!("tbk1.{}.{}", "a".repeat(22), "b".repeat(43));
    let database = Client::for_platform(&fixture.url, token)
        .build()
        .unwrap()
        .database("orders");
    assert!(matches!(
        database.run_sql::<Value>("SELECT * FROM items").await,
        Err(Error::InvalidInput(_))
    ));
    assert!(matches!(
        fixture
            .client()
            .database("Bad Name")
            .check_sql("SELECT * FROM items")
            .await,
        Err(Error::InvalidInput(_))
    ));
    assert!(fixture.requests.lock().unwrap().is_empty());
}
