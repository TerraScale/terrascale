//! SDK behavior checks not covered by the independent producer corpus.
//!
//! terrabase publishes HTTP + reactive fixtures with the pinned contract;
//! an SDK is not done until it passes. Until those fixtures land, this suite
//! pins the SDK's own request shapes, error mapping, region pinning,
//! checkpoint/resets, and builder rendering against a mock transport so
//! contract drift fails loudly here first.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::documents::{
    BatchOperation, Consistency, DocumentOperationOptions, DocumentReadOptions, Durability,
    ListOptions, MAX_BATCH_OPERATIONS, MAX_DOCUMENT_BYTES,
};
use terrascale::DocumentSchema;
use terrascale::{
    ApplyOutcome, AttachedView, Checkpoint, ClientConfig, CreateTenant, DatabaseScope, Direction,
    ErrorKind, FieldDef, FieldType, HttpTransport, Query, RegionAffinity, RegionCode, Request,
    ResetReason, Response, Result, SubscribeOptions, Subscription, TerraScaleClient, Transport,
    ViewDelta, ViewOperation, ViewSnapshot,
};

#[derive(Debug)]
struct Mock {
    recorded: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

impl Mock {
    fn new(responses: Vec<Response>) -> Arc<Self> {
        Arc::new(Self {
            recorded: Mutex::new(Vec::new()),
            responses: Mutex::new(responses.into()),
        })
    }

    fn last_request(self: &Arc<Self>) -> Request {
        self.recorded
            .lock()
            .expect("mock mutex")
            .last()
            .expect("one request")
            .clone()
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.recorded.lock().expect("mock mutex").push(request);
        self.responses
            .lock()
            .expect("mock mutex")
            .pop_front()
            .ok_or_else(|| terrascale::Error::transport("mock ran out of responses"))
    }
}

fn client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("ts_test_key")
            .project_id("proj_test")
            .build()
            .expect("config"),
        transport,
    )
}

fn doc_response() -> Response {
    Response::json(200, &json!({"name": " Ada "}))
}

fn scope() -> DatabaseScope {
    DatabaseScope::new("db_1")
}

#[tokio::test]
async fn document_crud_request_shapes() {
    let mock = Mock::new(vec![
        doc_response(),
        Response::json(200, &json!({"revision": "rev_put"})),
        Response::json(200, &json!({"revision": "rev_patch"})),
        Response {
            status: 204,
            headers: Vec::new(),
            body: Vec::new(),
        },
    ]);
    let sdk = client(Arc::clone(&mock));

    let create_error = sdk
        .documents()
        .create(&scope(), "users", json!({"name": "Ada"}))
        .await
        .expect_err("unsupported create");
    assert_eq!(create_error.code, "contract_gap");
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());

    let fetched = sdk
        .documents()
        .get(&scope(), "users", "doc_1")
        .await
        .expect("get");
    assert_eq!(fetched.id, "doc_1");
    assert_eq!(fetched.body, json!({"name": " Ada "}));
    assert_eq!(fetched.revision, "");
    assert!(mock
        .last_request()
        .url
        .ends_with("/api/v1/databases/db_1/collections/users/documents/doc_1"));

    let put = sdk
        .documents()
        .put(&scope(), "users", "doc_1", json!({"name": "B"}))
        .await
        .expect("put");
    assert_eq!(put.revision, "rev_put");
    assert_eq!(mock.last_request().route_name, "documents.put");
    assert_eq!(mock.last_request().body, Some(br#"{"name":"B"}"#.to_vec()));

    let patch = sdk
        .documents()
        .patch(&scope(), "users", "doc_1", json!({"name": "C"}))
        .await
        .expect("patch");
    assert_eq!(patch.revision, "rev_patch");
    assert_eq!(mock.last_request().route_name, "documents.patch");
    assert_eq!(mock.last_request().body, Some(br#"{"name":"C"}"#.to_vec()));

    sdk.documents()
        .delete(&scope(), "users", "doc_1")
        .await
        .expect("delete");
    let request = mock.last_request();
    assert_eq!(request.route_name, "documents.delete");
}

#[tokio::test]
async fn document_history_and_errors() {
    let mock = Mock::new(vec![
        Response::json(
            200,
            &json!({"history": [{"revision": "rev_0", "deleted": false}]}),
        ),
        Response::json(
            404,
            &json!({"code": "not_found", "message": "No such document. Check the id."}),
        ),
    ]);
    let sdk = client(Arc::clone(&mock));

    let history = sdk
        .documents()
        .history(&scope(), "users", "doc_1")
        .await
        .expect("history");
    assert_eq!(history.len(), 1);
    assert_eq!(history[0].revision, "rev_0");
    assert!(!history[0].deleted);
    assert!(mock
        .last_request()
        .url
        .ends_with("/documents/doc_1/history"));

    let error = sdk
        .documents()
        .get(&scope(), "users", "missing")
        .await
        .expect_err("404");
    assert_eq!(error.kind, ErrorKind::NotFound);
    assert_eq!(error.status, Some(404));
}

#[tokio::test]
async fn document_list_batch_and_unicode_shapes() {
    let mock = Mock::new(vec![
        Response::json(
            200,
            &json!({
                "documents": [{"id": "✓/one", "version": 7, "value": {"ok": true}}]
            }),
        ),
        Response::json(200, &json!({"committed": 9, "count": 2})),
    ]);
    let sdk = client(Arc::clone(&mock));
    let page = sdk
        .documents()
        .list(
            &DatabaseScope::new("db ✓"),
            "users/名",
            ListOptions::new().with_after("✓/last").with_limit(50),
        )
        .await
        .expect("list");
    assert_eq!(page.documents[0].id, "✓/one");
    assert_eq!(page.documents[0].version, 7);
    assert_eq!(page.documents[0].value, json!({"ok": true}));
    assert_eq!(
        mock.last_request().url,
        "https://api.terrascale.tech/api/v1/databases/db%20%E2%9C%93/collections/users%2F%E5%90%8D/documents?limit=50&after=%E2%9C%93%2Flast"
    );

    let receipt = sdk
        .documents()
        .batch(
            &DatabaseScope::new("db ✓"),
            "users/名",
            vec![
                BatchOperation::put("✓/one", json!({"ok": true})),
                BatchOperation::delete("✓/old"),
            ],
        )
        .await
        .expect("batch");
    assert_eq!(receipt.committed, 9);
    assert_eq!(receipt.count, 2);
    assert_eq!(mock.last_request().method, terrascale::Method::Post);
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&mock.last_request().body.expect("body"))
            .expect("json"),
        json!({
            "operations": [
                {"op": "put", "id": "✓/one", "value": {"ok": true}},
                {"op": "delete", "id": "✓/old"}
            ]
        })
    );
}

#[tokio::test]
async fn document_bounds_fail_before_transport() {
    let mock = Mock::new(vec![]);
    let sdk = client(Arc::clone(&mock));
    let oversized = serde_json::Value::String("x".repeat(MAX_DOCUMENT_BYTES));
    let error = sdk
        .documents()
        .put(&scope(), "users", "doc_1", oversized)
        .await
        .expect_err("oversized document");
    assert_eq!(error.code, "document_too_large");
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());

    let operations = (0..=MAX_BATCH_OPERATIONS)
        .map(|index| BatchOperation::delete(format!("doc-{index}")))
        .collect();
    let error = sdk
        .documents()
        .batch(&scope(), "users", operations)
        .await
        .expect_err("oversized batch");
    assert_eq!(error.code, "invalid_batch");
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());

    let error = sdk
        .documents()
        .list(&scope(), "users", ListOptions::new().with_limit(1001))
        .await
        .expect_err("oversized page");
    assert_eq!(error.code, "invalid_limit");
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());

    let error = sdk
        .documents()
        .list(&scope(), "users", ListOptions::new().with_limit(0))
        .await
        .expect_err("zero page");
    assert_eq!(error.code, "invalid_limit");

    let error = sdk
        .documents()
        .get_with_options(
            &scope(),
            "users",
            "doc_1",
            DocumentReadOptions::new().with_at(u64::MAX),
        )
        .await
        .expect_err("out-of-range point-in-time read");
    assert_eq!(error.code, "invalid_at");
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());

    let error = sdk
        .documents()
        .get_with_options(
            &scope(),
            "users",
            "doc_1",
            DocumentReadOptions::new().with_session("not a token"),
        )
        .await
        .expect_err("invalid session token");
    assert_eq!(error.code, "invalid_session");
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());
}

#[test]
fn schema_builder_and_derive_stub() {
    let schema = terrascale::CollectionSchema::new("users")
        .field(FieldDef::required("name", FieldType::String).indexed())
        .field(FieldDef::optional("age", FieldType::Integer));
    let built = terrascale::SchemaBuilder::new()
        .collection(schema)
        .build()
        .expect("valid schema");
    assert_eq!(built.len(), 1);
    assert_eq!(built[0].fields.len(), 2);
    assert!(built[0].fields[0].indexed);

    #[derive(terrascale::schema::Schema)]
    struct User {
        #[allow(dead_code)]
        name: String,
    }
    assert_eq!(User::collection(), "user");
    assert_eq!(User::schema().name, "user");
    let _ = terrascale::CONTRACT_VERSION;
}

#[test]
fn sql_builder_renders_parameterized_statements() {
    let query = Query::select(&["id", "body"])
        .from("users")
        .expect("collection")
        .where_("age > $1")
        .order_by("age", Direction::Desc)
        .expect("order")
        .limit(10)
        .offset(5)
        .param(json!(18))
        .build()
        .expect("query");
    assert_eq!(
        query.sql(),
        "SELECT id, body FROM users WHERE age > $1 ORDER BY age DESC LIMIT 10 OFFSET 5"
    );
    assert_eq!(query.params(), &[json!(18)]);

    let raw = Query::raw(
        "SELECT * FROM users WHERE id = $1".to_owned(),
        vec![json!("doc_1")],
    );
    assert!(raw.sql().contains("$1"));

    assert!(Query::select(&["id"]).build().is_err());
    assert!(Query::select(&[])
        .from("users")
        .expect("ok")
        .build()
        .is_err());
    assert!(
        Query::select(&["id; DROP"]).from("users").is_err()
            || Query::select(&["id"])
                .from("users")
                .expect("ok")
                .order_by("x; DROP", Direction::Asc)
                .is_err()
    );
}

#[tokio::test]
async fn sql_execute_posts_to_sql_endpoint() {
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({"columns": ["id"], "rows": [{"id": "doc_1"}]}),
    )]);
    let sdk = client(Arc::clone(&mock));
    let rows = sdk
        .sql()
        .execute(
            &scope(),
            &Query::raw("SELECT * FROM users".to_owned(), vec![]),
        )
        .await
        .expect("execute");
    assert_eq!(rows.columns, vec!["id".to_owned()]);
    assert_eq!(rows.rows.len(), 1);
    let request = mock.last_request();
    assert_eq!(request.route_name, "sql.execute");
    assert!(request.url.ends_with("/databases/db_1/sql"));
}

#[test]
fn checkpoint_base64_roundtrip() {
    let checkpoint = Checkpoint::new(vec![1, 2, 3, 250]);
    let encoded = checkpoint.to_base64();
    assert_eq!(
        Checkpoint::from_base64(&encoded)
            .expect("decode")
            .to_bytes(),
        vec![1, 2, 3, 250]
    );
    assert!(Checkpoint::from_base64("!!! not base64 !!!").is_err());
}

#[test]
fn typed_resets_cover_required_reasons() {
    for reason in [
        ResetReason::PermissionChanged,
        ResetReason::AuthExpired,
        ResetReason::RegionMoved,
        ResetReason::SchemaChanged,
        ResetReason::GenerationChanged,
        ResetReason::HistoryExpired,
        ResetReason::SlowConsumer,
        ResetReason::Reconnect,
    ] {
        assert!(!reason.to_string().is_empty());
    }
    // Unknown future reasons still parse through the catch-all.
    let parsed: ResetReason =
        serde_json::from_value(json!({"server_reset": {"detail": "new policy"}}))
            .expect("catch-all");
    assert!(matches!(parsed, ResetReason::ServerReset { .. }));
}

#[test]
fn subscription_applies_ordered_deltas_and_resets() {
    let mut subscription = Subscription::new("sub_1");
    subscription.apply_snapshot(ViewSnapshot {
        view_generation: 7,
        authorization_generation: 1,
        revision: "r1".to_owned(),
        items: vec![],
        digest: "digest-r1".to_owned(),
        checkpoint: Checkpoint::new(vec![7]),
    });
    let outcome = subscription.apply_delta(ViewDelta {
        view_generation: 7,
        from_revision: "r1".to_owned(),
        to_revision: "r2".to_owned(),
        operations: vec![ViewOperation::Insert {
            identity: "doc_1".to_owned(),
            position: 0,
            value: std::collections::BTreeMap::from([("name".to_owned(), br#""Ada""#.to_vec())]),
        }],
        digest: "digest-r2".to_owned(),
        checkpoint: Checkpoint::new(vec![8]),
    });
    assert_eq!(outcome, ApplyOutcome::Applied);
    assert_eq!(subscription.items().len(), 1);

    // Replaying the same cut is a duplicate, not an error.
    let outcome = subscription.apply_delta(ViewDelta {
        view_generation: 7,
        from_revision: "r0".to_owned(),
        to_revision: "r2".to_owned(),
        operations: vec![],
        digest: "digest-r2".to_owned(),
        checkpoint: Checkpoint::new(vec![8]),
    });
    assert_eq!(outcome, ApplyOutcome::Duplicate);

    // A skipped cut forces a typed reset, never a silent gap.
    let outcome = subscription.apply_delta(ViewDelta {
        view_generation: 7,
        from_revision: "r9".to_owned(),
        to_revision: "r10".to_owned(),
        operations: vec![],
        digest: "digest-r10".to_owned(),
        checkpoint: Checkpoint::new(vec![9]),
    });
    assert_eq!(outcome, ApplyOutcome::ResetRequired(ResetReason::Reconnect));
}

#[tokio::test]
async fn subscribe_attaches_with_checkpoint_and_hint() {
    let snapshot = json!({
        "view": "view_1",
        "generation": 3,
        "snapshot": {
            "view_generation": 3,
            "revision": "r1",
            "items": [],
            "checkpoint": [3],
        },
        "checkpoint": [3],
        "snapshot_chunks": [],
        "log": "/api/v1/reactive/views/view_1/3/log?cut=1",
        "capability": "capability",
        "offset": 1,
    });
    let mock = Mock::new(vec![Response::json(200, &snapshot)]);
    let sdk = client(Arc::clone(&mock));
    let attached: AttachedView = sdk
        .reactive()
        .subscribe(
            &scope(),
            &Query::raw("SELECT * FROM users".to_owned(), vec![]),
            SubscribeOptions::new()
                .with_transport(Transport::LongPolling)
                .with_region_hint(RegionCode::Sfo)
                .with_checkpoint(Checkpoint::new(vec![2])),
        )
        .await
        .expect("subscribe");
    assert_eq!(attached.view_id, "view_1");
    let request = mock.last_request();
    assert_eq!(request.route_name, "reactive.subscribe");
    assert!(request.url.starts_with("https://sfo.api.terrascale.tech"));
    assert!(request.url.contains("/reactive/views"));
    assert_eq!(
        request
            .headers
            .iter()
            .find(|(name, _)| name == "x-terrascale-preferred-region")
            .map(|(_, value)| value.as_str()),
        Some("SFO")
    );
}

#[tokio::test]
async fn reactive_methods_reject_invalid_queries_before_transport() {
    let mock = Mock::new(vec![]);
    let sdk = client(Arc::clone(&mock));
    let invalid = Query::raw("DROP TABLE users", vec![]);

    let error = sdk
        .reactive()
        .subscribe(&scope(), &invalid, SubscribeOptions::new())
        .await
        .expect_err("invalid subscription query");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());

    let error = sdk
        .reactive()
        .execute(&scope(), &invalid)
        .await
        .expect_err("invalid reactive execute query");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());
}

#[tokio::test]
async fn region_pinning_and_421_repair() {
    // Failover without a pin uses global first contact.
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({"region": "GRU", "healthy": true}),
    )]);
    let sdk = client(Arc::clone(&mock));
    sdk.management().regions().list().await.expect("regions");
    assert!(mock
        .last_request()
        .url
        .starts_with("https://api.terrascale.tech"));

    // Strict affinity pins REST to the regional hostname.
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({"region": "GRU", "healthy": true}),
    )]);
    let transport: Arc<dyn HttpTransport> = mock;
    let strict = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("ts_test_key")
            .project_id("proj_test")
            .preferred_region(RegionCode::Gru)
            .region_affinity(RegionAffinity::Strict)
            .build()
            .expect("config"),
        Arc::clone(&transport),
    );
    strict.management().regions().list().await.expect("regions");
    assert!(strict.pinned_region() == Some(RegionCode::Gru));

    // A 421 repairs the pin from X-TerraScale-Placement and surfaces retry.
    let mock = Mock::new(vec![Response {
        status: 421,
        headers: vec![("x-terrascale-placement".to_owned(), "fra".to_owned())],
        body: b"{}".to_vec(),
    }]);
    let sdk = client(Arc::clone(&mock));
    let error = sdk
        .documents()
        .get(&scope(), "users", "doc_1")
        .await
        .expect_err("421");
    assert_eq!(error.kind, ErrorKind::RegionRedirect);
    assert_eq!(error.placement.as_deref(), Some("fra"));
    assert_eq!(sdk.pinned_region(), Some(RegionCode::Fra));
}

#[tokio::test]
async fn tenants_and_management_shapes() {
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({
            "database_id": "db_1",
            "project_id": "proj_test",
            "name": "main",
            "regions": ["GRU"],
            "version": 1,
        }),
    )]);
    let sdk = client(Arc::clone(&mock));

    let tenant_error = sdk
        .tenants()
        .create(CreateTenant::new("acme", "default"), None)
        .await
        .expect_err("tenant route is not in the pinned contract");
    assert_eq!(tenant_error.code, "contract_gap");
    assert!(mock.recorded.lock().expect("mock mutex").is_empty());

    let database = sdk
        .management()
        .databases()
        .get("db_1", None)
        .await
        .expect("database");
    assert_eq!(database.name, "main");
    assert!(mock.last_request().url.contains("/databases/db_1"));

    // Destructive calls require explicit confirmation.
    assert!(sdk
        .management()
        .databases()
        .delete("db_1", false, None)
        .await
        .is_err());
}

#[test]
fn config_validation() {
    assert!(ClientConfig::builder()
        .region_affinity(RegionAffinity::Strict)
        .build()
        .is_err());
    assert!(ClientConfig::builder()
        .max_retry_attempts(9)
        .build()
        .is_err());
    assert!(ClientConfig::builder().api_base_url("   ").build().is_err());
    for unsafe_url in [
        "http://example.com",
        "https://user:pass@example.com",
        "https://example.com/api?token=secret",
        "https://example.com/api#fragment",
        "https://terrabase.internal",
        "https://api.example.test/tbep",
    ] {
        assert!(ClientConfig::builder()
            .api_base_url(unsafe_url)
            .build()
            .is_err());
    }
    assert!(ClientConfig::builder()
        .api_base_url("http://127.0.0.1:8787")
        .build()
        .is_ok());
    assert!(ClientConfig::builder()
        .api_base_url("http://sdk.test")
        .build()
        .is_ok());
}

#[tokio::test]
async fn document_request_options_match_published_headers_and_queries() {
    let mock = Mock::new(vec![
        Response::json(200, &json!({"revision": "rev_1"})),
        Response::json(200, &json!({"name": "Ada"})),
        Response::json(200, &json!({"documents": []})),
        Response::json(200, &json!({"history": []})),
    ]);
    let sdk = client(Arc::clone(&mock));
    sdk.documents()
        .put_with_options(
            &scope(),
            "users",
            "ada",
            json!({"name": "Ada"}),
            DocumentOperationOptions::new()
                .with_idempotency_key("write-1")
                .with_durability(Durability::Global)
                .with_if_match("\"old\"")
                .with_session("session-12")
                .with_consistency(Consistency::WritesFollowReads)
                .with_region_hint(RegionCode::Sfo),
        )
        .await
        .expect("put");
    let put = mock.last_request();
    assert!(put.url.starts_with("https://sfo.api.terrascale.tech"));
    assert!(put
        .headers
        .iter()
        .any(|(name, value)| name == "x-terrascale-preferred-region" && value == "SFO"));
    assert!(put
        .headers
        .iter()
        .any(|(name, value)| name == "idempotency-key" && value == "write-1"));
    assert!(put
        .headers
        .iter()
        .any(|(name, value)| name == "x-terrascale-durability" && value == "global"));
    assert!(put
        .headers
        .iter()
        .any(|(name, value)| name == "if-match" && value == "\"old\""));
    assert!(put
        .headers
        .iter()
        .any(|(name, value)| name == "x-terrascale-session" && value == "session-12"));
    // Consistency is represented by the causal session header; the public
    // document contract does not publish an x-terrascale-consistency header.
    assert!(!put
        .headers
        .iter()
        .any(|(name, _)| name == "x-terrascale-consistency"));

    sdk.documents()
        .get_with_options(
            &scope(),
            "users",
            "ada",
            DocumentReadOptions::new()
                .with_at(42)
                .with_if_none_match("\"rev_1\"")
                .with_session("session-22")
                .with_consistency(Consistency::Eventual)
                .with_region_hint(RegionCode::Fra),
        )
        .await
        .expect("get");
    let get = mock.last_request();
    assert!(get.url.starts_with("https://fra.api.terrascale.tech"));
    assert!(get.url.ends_with("?at=42"));
    assert!(get
        .headers
        .iter()
        .any(|(name, value)| name == "if-none-match" && value == "\"rev_1\""));
    assert!(get
        .headers
        .iter()
        .any(|(name, value)| name == "x-terrascale-session" && value == "session-22"));

    sdk.documents()
        .list(
            &scope(),
            "users",
            ListOptions::new()
                .with_limit(10)
                .with_after("rev_1")
                .with_session("session-33")
                .with_region_hint(RegionCode::Gru),
        )
        .await
        .expect("list");
    assert!(mock.last_request().url.ends_with("?limit=10&after=rev_1"));

    sdk.documents()
        .history_with_options(
            &scope(),
            "users",
            "ada",
            ListOptions::new().with_limit(5).with_after("rev_0"),
        )
        .await
        .expect("history");
    assert!(mock
        .last_request()
        .url
        .ends_with("/history?limit=5&after=rev_0"));
}
